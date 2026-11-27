using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.ScreenPen;
using PresenterTools.Services;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>AppHost へ戻す操作（<see cref="PenController"/> は状態を直接変えない）。</summary>
public interface IPenHost
{
    /// <summary>ペンを終える（PresenterState.SetPen(false) を通す。線は残す）。</summary>
    void EndPen(string reason);
    /// <summary>操作パネルを入力面の上へ置き直す（D4）。</summary>
    void RaisePanel();
    /// <summary>自アプリの窓か（前面を戻す候補にしない）。</summary>
    bool IsOwnWindow(IntPtr hwnd);
    /// <summary>描線を捨てたことを利用者に知らせる（D11）。</summary>
    void NotifyDiscarded();
    /// <summary>保留の再確認（1 秒後に 1 回。D11）。</summary>
    void RecheckPen(string trigger);
    /// <summary>最新の列挙（構成変更のたびに更新される。右クリックの「書き込み先」の候補。V1.8.0）。</summary>
    DisplaySnapshot CurrentDisplays { get; }
    /// <summary>書き込み先の切り替え（メニューが閉じた後。docs/archive/design-1.8.0.md P6）。列挙し直して <see cref="PenController.ExecuteSwitch"/> を呼ぶ。</summary>
    void SwitchPenTarget(DisplayDescriptor chosen);
}

/// <summary>切り替えの結果（ホストの案内に使う）。</summary>
public enum PenSwitchResult
{
    /// <summary>予約の間に終了・再開始していた。何もしない。</summary>
    Stale,
    Same,
    Ok,
    /// <summary>切り替えなかった。移動元は保たれている、または保留で隠した。</summary>
    Failed,
    /// <summary>切り替えず、移動元の線を捨ててペンを終えた。</summary>
    FailedDiscarded,
}

/// <summary>
/// 画面ペンの入力面・メニュー・描線の表示の統合（docs/archive/design-1.7.0.md D1・D4〜D7・D11、docs/archive/design-1.8.0.md P2〜P9）。AppHost から呼ぶ。
/// 描線は入力面ではなく書き込み先の <see cref="OverlayWindow"/> に描き、Master に従って表示・非表示になる。
/// </summary>
public sealed class PenController : IPenSurfaceSink
{
    private readonly IPenHost _host;
    private readonly PenDrawing _drawing = new();
    private readonly PenSession _session = new();
    private readonly PenToolState _tools = new();
    private readonly PenLayer _layer;
    private readonly DispatcherTimer _retry;
    private OverlayWindow? _attachedTo;
    private PenSurfaceWindow? _surface;
    private ContextMenu? _menu;
    // 親のメニューとサブメニューの Popup の窓（D4。前面が入力面かメニューかの判定に使う）。
    private readonly HashSet<IntPtr> _menuHwnds = [];
    // 切り替えの予約に写した、閉じる直前のメニューの窓（切り替えの Deactivate で使う）。
    private IReadOnlySet<IntPtr> _closedMenuHwnds = new HashSet<IntPtr>();
    private IntPtr _restoreCandidate;
    private long _retryGeneration;
    private PenSettings _settings = new();
    private bool _limitLogged;
    private PendingSwitch? _pendingSwitch;
    private bool _switching;
    private bool _dpiDuringSwitch;
    // 切り替えの確定後に行う DPI の再確認（ホストが案内を更新した後に RunDeferredRecheck で行う。コードレビュー R2）。
    private bool _recheckAfterSwitch;
    // 直近の Activate が作った所有者の世代（切り替えの成否の照合。コードレビュー R1）。
    private long _startedGeneration;
    private bool _faultShowArmed;
    private bool _restoring;
    // 消しゴムの候補の判定（移動の通知をまとめる）。
    private Point? _hoverPoint;
    private bool _hoverScheduled;
    private string? _hoverTrace;
    // 蛍光マーカーのカーソル（色と拡大率が同じなら使い回す）。
    private Cursor? _highlighterCursor;
    private (ArgbColor Color, double Scale)? _highlighterKey;
    private (ArgbColor Color, double Scale)? _highlighterFailedKey;
    private bool _cursorErrorLogged;
    private string? _cursorTrace;

    private sealed record PendingSwitch(PenSurfaceWindow Surface, ContextMenu Menu, DisplayDescriptor Chosen)
    {
        public IReadOnlySet<IntPtr> Hwnds { get; set; } = new HashSet<IntPtr>();
    }

    public PenController(IPenHost host)
    {
        _host = host;
        _layer = new PenLayer(_drawing);
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            // 待つ間に所有者が替わっていたら何もしない（D11）。
            if (_session.ShouldRetry(_retryGeneration)) _host.RecheckPen("retry");
        };
    }

    /// <summary>入力面と描線が属する実行中の画面（D11）。入力面か描線がある間だけ持つ。</summary>
    public PenOwner? Owner => _session.Owner;

    /// <summary>モニター情報を取得できず、描線を隠して判定を待っている（D11 の保留）。</summary>
    public bool IsPending => _session.IsPending;

    public bool IsActive => _surface is not null;
    public bool HasLines => !_drawing.IsEmpty;
    /// <summary>選んでいる道具（P2）。</summary>
    public PenToolState Tools => _tools;
    /// <summary>
    /// メニューを開いてから Closed が来るまで true。メニューの外の入力面をクリックすると、IsOpen が false になった後、
    /// Closed より前に同じクリックが入力面へ届く（初期試験で確認）。それで点を描かないよう、Closed までを表示中とみなす（D6）。
    /// </summary>
    public bool IsMenuOpen => _menu is not null;
    public bool CanBeginStroke => !_drawing.IsAtLimit;
    public bool IsEraser => _tools.IsEraser;

    /// <summary>描線に関係する設定（色・幅・開始時の太さ）。起動時は開始時の太さの通常のペン。</summary>
    public void Initialize(PenSettings settings)
    {
        _settings = settings;
        _tools.Reset(settings.StartWidth);
    }

    /// <summary>カーソルがペン中の書き込み先にあるか（Cursor Highlight・Click Effect を出さない。D8）。</summary>
    public bool Covers(Core.Geometry.PointInt p) => IsActive && Owner is { } o && o.Bounds.Contains(p);

    // ---- 開始・終了（D7） ----

    /// <summary>
    /// 入力面を表示する。所有者があり同じ画面なら描線を続け、無ければ作る。開始のたびに世代番号を新しくする（D11）。
    /// </summary>
    public void Activate(DisplayDescriptor target, OverlayManager overlays)
    {
        if (IsActive) return;
        var previousOwner = Owner;
        var previousPending = IsPending;
        var hadOwner = previousOwner is not null;
        var same = _session.Start(target, out var started);
        _startedGeneration = started.Generation;
        // 切り替えた画面に線を残して、設定の別の画面で開始した（P6「次の開始」）。線は開始に成功するまで退避し、
        // 失敗したら前の画面へ戻す（コードレビュー R4）。
        var saved = same ? null : _drawing.TakeAll();
        if (saved is not null)
        {
            _layer.InvalidatePending();
            _layer.RenderCommitted();
        }
        if (!hadOwner) TestTrace.Write("pen-owner", "new");
        if (saved is not null && !_switching && PenSwitchFault.TakeStartShow()) _faultShowArmed = true;
        try
        {
            StartSurface(target, overlays);
        }
        catch
        {
            RevertStart(previousOwner, previousPending, saved, overlays);
            throw;
        }
        if (saved is not null)
        {
            TestTrace.Write("pen-discard", "start-other");
            AfterLinesChanged();
        }
        _host.RaisePanel();
        TestTrace.Write("pen-start", target.DeviceName);
        // 動かさないうちにも消しゴムの候補を出す（設計レビュー D7）。
        ScheduleHoverRefresh();
    }

    /// <summary>
    /// 開始に失敗した（入力面の作成・表示の例外）: 作りかけの入力面と前面の監視を片付け、所有者と退避した線を開始前に戻す（コードレビュー R4）。
    /// </summary>
    private void RevertStart(PenOwner? previousOwner, bool previousPending, PenDrawingContents? saved, OverlayManager overlays)
    {
        StopForegroundWatch();
        if (_surface is { } s)
        {
            _surface = null;
            try { s.CloseByController(); }
            catch (InvalidOperationException) { } // 表示前に例外になった窓
        }
        _restoreCandidate = IntPtr.Zero;
        _session.RevertStart(previousOwner, previousPending);
        if (previousOwner is null)
        {
            OwnerReleased();
            return;
        }
        if (saved is not null) _drawing.Restore(saved);
        Detach();
        Attach(overlays); // 前の画面の窓へ付け直して描き直す
        _layer.RenderCommitted();
        TestTrace.Write("pen-start-failed", saved is null ? "kept" : "restored");
    }

    private void StartSurface(DisplayDescriptor target, OverlayManager overlays)
    {
        _retry.Stop();
        Attach(overlays);
        // 戻す候補（D4: 最後に入力面がアクティブになる直前の他のアプリの窓）。入力面が作成時に自分のスレッドで
        // アクティブ扱いになると、クリックで前面になっても WM_MOUSEACTIVATE が来ない（初期試験で確認）ため、
        // ペンの間は前面の変化を見て、他のアプリの最後の前面を覚え、入力面が前面になったときに候補にする。
        _restoreCandidate = IntPtr.Zero;
        _lastForeign = IntPtr.Zero;
        var foreground = GetForegroundWindow();
        NoteForeign(foreground);
        _lastNonPen = foreground;
        _restoreCandidate = _lastForeign;
        StartForegroundWatch();
        _limitLogged = false;
        var surface = new PenSurfaceWindow(this, target.Bounds);
        _surface = surface;
        _highlighterFailedKey = null; // 入力面の作成のたびにカーソルを作り直す（P4）
        UpdateCursor();
        surface.DpiChanged += (_, _) => OnSurfaceDpiChanged(surface); // 契機 (2)（D11）
        if (_faultShowArmed)
        {
            _faultShowArmed = false;
            throw new InvalidOperationException("Test failure: PRESENTERTOOLS_TEST_PEN_SWITCH_FAIL show");
        }
        surface.Show();
        surface.BringToTop();
        if (_switching && (_restoring ? PenSwitchFault.DpiOnRestore : PenSwitchFault.DpiOnTarget)) OnSurfaceDpiChanged(surface);
        // 拡大率がシステムと違う画面では、作成時の WPF の DPI 処理で入力面がアクティブになり、前面を取れる状況だと
        // 開始時に前面を奪う（初期試験で確認）。開始は前面を変えない（D4・D7: 最初のクリックでアクティブになる）ため、
        // 開始前の前面が他のアプリなら戻す。
        // 戻す呼び出しは記録の引数に置かない（TestTrace.Write は Release で呼び出しごと消える。R1）。
        if (_lastForeign != IntPtr.Zero && GetForegroundWindow() == surface.Handle)
        {
            var returned = SetForegroundWindow(_lastForeign);
            TestTrace.Write("pen-start-foreground", returned ? "returned" : "refused");
        }
    }

    /// <summary>
    /// 最初のクリックの前に入力面が前面になった（作成時の DPI 処理・前の入力面を閉じた後の OS の選択。前面の監視で
    /// 遅れて分かることもある）: 他のアプリの最後の前面、無ければ直前の前面（操作パネルなど）へ返す。クリック前の
    /// キー・Esc を入力面が受けない（docs/archive/design-1.8.0.md P6 手順 9。コードレビュー R3）。
    /// </summary>
    private void ReturnForegroundBeforeFirstClick(PenSurfaceWindow surface)
    {
        if (surface.ActivationEnabled || GetForegroundWindow() != surface.Handle) return;
        var target = _lastForeign != IntPtr.Zero ? _lastForeign : _lastNonPen;
        string result;
        if (target == IntPtr.Zero || target == surface.Handle || !IsWindow(target) || !IsWindowVisible(target)) result = "none";
        else result = SetForegroundWindow(target) ? "returned" : "refused";
        TestTrace.Write("pen-surface-activate", "blocked " + result);
    }

    private void OnSurfaceDpiChanged(PenSurfaceWindow surface)
    {
        if (_switching)
        {
            _dpiDuringSwitch = true; // 切り替えの確定の後に 1 回だけ再確認する（設計レビュー D2）
            return;
        }
        if (ReferenceEquals(surface, _surface)) _host.RecheckPen("dpi");
    }

    /// <summary>入力面を閉じる（線は残す）。描線が無ければ所有者も解放する。切り替え（reason = switch）では道具を保つ。</summary>
    public void Deactivate(string reason)
    {
        if (_surface is not { } surface) return;
        // (1) 前面が入力面かメニューかを判定し、(2) メニューを閉じてキャプチャを外し、(3) (1) が真なら候補へ前面を戻す（D4）。
        var foreground = GetForegroundWindow();
        // メニューは実際の Popup の HWND で判定する（Closed の前に前面が操作パネルへ移った場合を含めない。R6）。
        // サブメニュー・切り替えの予約に写した窓も含める（設計レビュー D4）。
        var ownForeground = foreground != IntPtr.Zero
            && (foreground == surface.Handle || _menuHwnds.Contains(foreground) || _closedMenuHwnds.Contains(foreground));
        StopForegroundWatch();
        CloseMenu();
        surface.FinishStroke();
        SetHoverCandidate(null);
        _hoverPoint = null;
        _surface = null;
        surface.CloseByController();
        TestTrace.Write("pen-end", reason);
        if (ownForeground)
        {
            var candidate = _restoreCandidate;
            string result;
            if (candidate == IntPtr.Zero) result = "none";
            else if (!IsWindow(candidate) || !IsWindowVisible(candidate)) result = "gone";
            else result = SetForegroundWindow(candidate) ? "ok" : "refused";
            TestTrace.Write("pen-restore-foreground", result);
        }
        _restoreCandidate = IntPtr.Zero;
        if (reason != "switch")
        {
            var before = _tools.Tool;
            _tools.OnPenEnded(); // 消しゴムのまま終えたら次の開始は前の道具（SPEC §36.9）
            if (before != _tools.Tool) TestTrace.Write("pen-tool", _tools.TraceName);
        }
        if (_session.SurfaceClosed(HasLines)) OwnerReleased();
    }

    /// <summary>描線を消して描き直す（所有者には触れない）。</summary>
    private void ClearLines(string? discardReason)
    {
        _drawing.Clear();
        _layer.InvalidatePending();
        _layer.RenderCommitted();
        if (discardReason is not null) TestTrace.Write("pen-discard", discardReason);
        AfterLinesChanged();
    }

    /// <summary>入力面も描線も無くなったら所有者を解放する。</summary>
    private void ReleaseOwnerIfIdle()
    {
        if (_session.ReleaseIfIdle(HasLines)) OwnerReleased();
    }

    private void OwnerReleased()
    {
        _retry.Stop();
        Detach();
        TestTrace.Write("pen-owner", "released");
    }

    /// <summary>
    /// 描線を捨て、ペン中なら終える（構成変更・書き込み先の変更。所有者は <see cref="PenSession"/> で既に解放した）。
    /// </summary>
    private void DiscardAll(string endReason, string discardReason)
    {
        if (IsActive) _host.EndPen(endReason); // 入力面を閉じる。所有者は既に無いので解放の記録は出ない
        ClearLines(discardReason);
        OwnerReleased();
    }

    // ---- 窓の付け替え（D5・D11） ----

    /// <summary>描線を旧窓から外し、保留中の描画予約を無効にする（再構築の前）。</summary>
    public void Detach()
    {
        SetHoverCandidate(null);
        _layer.InvalidatePending();
        _attachedTo?.SetPenLayer(null);
        _attachedTo = null;
    }

    private void Attach(OverlayManager overlays)
    {
        if (Owner is not { } o) return;
        var target = overlays.OverlayFor(o.DeviceName, o.Bounds);
        if (target is null || ReferenceEquals(target, _attachedTo)) return;
        Detach();
        target.SetPenLayer(_layer);
        _attachedTo = target;
        _layer.RenderCommitted();
    }

    /// <summary>
    /// 構成変更の後に比べる（D11）。保持なら新しい窓へ付け直し、破棄なら線を捨ててペンを終え、保留なら線を隠して待つ。
    /// </summary>
    public PenDisplayOutcome OnDisplaysChanged(string trigger, DisplaySnapshot snapshot, OverlayManager overlays)
    {
        var wasActive = IsActive;
        var hadLines = HasLines;
        var outcome = _session.OnDisplays(snapshot);
        TestTrace.Write("pen-display", trigger + " " + outcome.ToString().ToLowerInvariant());
        switch (outcome)
        {
            case PenDisplayOutcome.Keep:
                _retry.Stop();
                Attach(overlays);
                break;
            case PenDisplayOutcome.Discard:
                DiscardAll("display", "display");
                if (wasActive || hadLines) _host.NotifyDiscarded();
                break;
            case PenDisplayOutcome.Pending:
                if (IsActive) _host.EndPen("display-unknown"); // 描かずに始めていれば所有者もここで解放される
                Detach(); // 描線は捨てずに隠す
                if (Owner is { } still && !_retry.IsEnabled && trigger != "retry")
                {
                    _retryGeneration = still.Generation;
                    _retry.Start();
                }
                break;
        }
        return outcome;
    }

    // ---- 書き込み先の切り替え（docs/archive/design-1.8.0.md P6） ----

    /// <summary>
    /// 選んだ時点の列挙で判定して切り替える（P6 の手順 3〜8。ホストが列挙し直して呼ぶ）。
    /// </summary>
    public PenSwitchResult ExecuteSwitch(DisplayDescriptor chosen, DisplaySnapshot snapshot, OverlayManager overlays)
    {
        _recheckAfterSwitch = false;
        if (Owner is not { } owner || !IsActive)
        {
            TestTrace.Write("pen-switch", "stale");
            return PenSwitchResult.Stale;
        }
        var decision = PenTargetResolver.DecideSwitch(owner, chosen, snapshot);
        switch (decision.Kind)
        {
            case PenSwitchKind.SourcePending:
            case PenSwitchKind.SourceDiscard:
            {
                var hadSomething = IsActive || HasLines;
                var outcome = OnDisplaysChanged("switch", snapshot, overlays);
                TestTrace.Write("pen-switch", decision.Kind == PenSwitchKind.SourcePending ? "failed source-pending" : "failed source-discard");
                return outcome == PenDisplayOutcome.Discard && hadSomething ? PenSwitchResult.FailedDiscarded : PenSwitchResult.Failed;
            }
            case PenSwitchKind.Failed:
                OnDisplaysChanged("switch", snapshot, overlays); // 保持: 新しい窓に付け直すだけ
                TestTrace.Write("pen-switch", "failed " + decision.Status);
                return PenSwitchResult.Failed;
            case PenSwitchKind.Same:
                OnDisplaysChanged("switch", snapshot, overlays);
                TestTrace.Write("pen-switch", "same");
                return PenSwitchResult.Same;
        }

        var target = decision.Target!;
        if (PenSwitchFault.Overlay || overlays.OverlayFor(target.DeviceName, target.Bounds) is null)
        {
            OnDisplaysChanged("switch", snapshot, overlays);
            TestTrace.Write("pen-switch", "failed overlay");
            return PenSwitchResult.Failed;
        }
        return SwitchTo(target, decision.Source!, overlays);
    }

    /// <summary>
    /// 切り替える（P6 の手順 8）。移動先の入力面が出るまで移動元の線を退避し、出せなければ移動元で戻す。
    /// </summary>
    private PenSwitchResult SwitchTo(DisplayDescriptor target, DisplayDescriptor source, OverlayManager overlays)
    {
        // 8.1 退避して移動元の窓から線を消す。
        var saved = _drawing.TakeAll();
        _layer.InvalidatePending();
        _layer.RenderCommitted();
        // 8.2 移動元を閉じる（線が空なので所有者はここで 1 回だけ解放される）。
        Deactivate("switch");
        // 8.3 移動先で開始する。移動元へ戻す間も含めて _switching を保ち、入力面の DPI 通知は確定後の再確認 1 回に
        // まとめる（戻す途中の再確認が復元した線を保留・破棄しない。コードレビュー R1）。
        _switching = true;
        _dpiDuringSwitch = false;
        _recheckAfterSwitch = false;
        _faultShowArmed = PenSwitchFault.Show;
        try
        {
            if (TryStartOn(target, overlays, "Pen: could not show the input surface on the new display."))
            {
                TestTrace.Write("pen-discard", "switch");
                TestTrace.Write("pen-switch", "ok");
                _recheckAfterSwitch = _dpiDuringSwitch;
                return PenSwitchResult.Ok;
            }

            // 8.5 失敗: 作りかけの入力面を閉じ、移動元で線と道具を戻す。
            CloseFailedSurface();
            _drawing.Restore(saved);
            _restoring = true;
            _faultShowArmed = PenSwitchFault.ShowBoth;
            if (TryStartOn(source, overlays, "Pen: could not show the input surface again on the original display."))
            {
                _layer.RenderCommitted();
                AfterLinesChanged();
                TestTrace.Write("pen-switch", "failed surface");
                _recheckAfterSwitch = _dpiDuringSwitch;
                return PenSwitchResult.Failed;
            }
        }
        finally
        {
            _switching = false;
            _restoring = false;
            _faultShowArmed = false;
        }
        CloseFailedSurface();
        _drawing.Clear();
        _layer.RenderCommitted();
        TestTrace.Write("pen-discard", "switch-failed");
        TestTrace.Write("pen-switch", "failed surface");
        var hadOwner = Owner is not null; // 戻す開始が例外のときは RevertStart で解放済み（記録を 2 回出さない）
        _session.Discard();
        if (hadOwner) OwnerReleased();
        _tools.OnPenEnded();
        _host.EndPen("switch-failed");
        return PenSwitchResult.FailedDiscarded;
    }

    /// <summary>
    /// 切り替えの中で 1 つの画面に開始する。この試行が作った入力面・所有者の世代・その画面のオーバーレイがそろって
    /// 表示されているときだけ成功とする（古い面・別の試行の状態で成否を決めない。コードレビュー R1）。
    /// </summary>
    private bool TryStartOn(DisplayDescriptor display, OverlayManager overlays, string errorMessage)
    {
        try
        {
            Activate(display, overlays);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(errorMessage, ex);
            return false;
        }
        var expected = Owner is { } o ? overlays.OverlayFor(o.DeviceName, o.Bounds) : null;
        return _surface is { IsVisible: true }
            && Owner is { } owner && owner.Generation == _startedGeneration
            && expected is not null && ReferenceEquals(_attachedTo, expected);
    }

    /// <summary>
    /// 切り替えの確定後の DPI の再確認（切り替えの間に入力面の DPI 通知が来たときだけ 1 回）。ホストが切り替えの案内・
    /// リングを更新した後に呼ぶ（再確認で出した破棄の案内を後から消さない。コードレビュー R2）。
    /// </summary>
    public void RunDeferredRecheck()
    {
        if (!_recheckAfterSwitch) return;
        _recheckAfterSwitch = false;
        if (Owner is not null) _host.RecheckPen("dpi");
    }

    /// <summary>切り替えで作りかけた入力面を閉じ、所有者を解放する（入力面を 2 つ残さない）。</summary>
    private void CloseFailedSurface()
    {
        if (_surface is { } s)
        {
            _surface = null;
            StopForegroundWatch();
            try { s.CloseByController(); }
            catch (InvalidOperationException) { } // 表示前に例外になった窓
        }
        if (_session.SurfaceClosed(HasLines)) OwnerReleased();
    }

    // ---- 設定の適用（D9） ----

    /// <summary>
    /// 確定した設定を受ける。書き込み先が変わったら所有者と比べ、違えば線を捨ててペンを終える。開始時の太さが変われば選択も変える。
    /// 捨てたら true。
    /// </summary>
    public bool ApplySettings(PenSettings before, PenSettings after, DisplaySnapshot snapshot)
    {
        _settings = after;
        if (before.StartWidth != after.StartWidth)
        {
            _tools.Reset(after.StartWidth);
            TestTrace.Write("pen-tool", _tools.TraceName);
        }
        if (before.HighlighterColor != after.HighlighterColor) _highlighterFailedKey = null;
        UpdateCursor();
        if (!_session.OnApply(before, after, snapshot)) return false;
        DiscardAll("target-changed", "target-changed");
        return true;
    }

    // ---- 入力面からの通知（D4） ----

    public void BeginStroke(Point p)
    {
        CloseMenu();
        bool begun;
        if (_tools.Tool == PenTool.Highlighter)
        {
            var (color, opacity) = PenHighlighter.Resolve(_settings.HighlighterColor);
            begun = _drawing.BeginStroke(new PenPoint(p.X, p.Y), color, PenHighlighter.Width, PenTool.Highlighter, opacity);
        }
        else
        {
            var color = ArgbColor.TryParse(_settings.Color, out var c) ? c : ArgbColor.FromRgb(0xFF, 0x20, 0x20);
            begun = _drawing.BeginStroke(new PenPoint(p.X, p.Y), color, _settings.WidthOf(_tools.Width));
        }
        if (!begun)
        {
            UpdateCursor();
            return;
        }
        _layer.ScheduleCurrent();
    }

    public void AddPoint(Point p, bool inside)
    {
        if (_drawing.AddPoint(new PenPoint(p.X, p.Y), inside)) _layer.ScheduleCurrent();
        else if (_drawing.CurrentHitLimit && !_limitLogged)
        {
            _limitLogged = true;
            UpdateCursor();
        }
    }

    public void EndStroke()
    {
        var moves = _drawing.MovesReceived;
        var hit = _drawing.CurrentHitLimit;
        var stroke = _drawing.EndStroke();
        if (stroke is null) return;
        if (hit) TestTrace.Write("pen-limit", $"{moves} {_drawing.TotalPoints}");
        TestTrace.Write("pen-stroke", stroke.PointCount.ToString(CultureInfo.InvariantCulture) + (stroke.Kind == PenTool.Highlighter ? " highlighter" : " pen"));
        // 予約を無効にしてから同じ処理の中で確定した画と描いている画を描き直す（P3。二重の表示・抜けを作らない）。
        _layer.InvalidatePending();
        _layer.RenderCommitted();
        UpdateCursor();
        _limitLogged = false;
    }

    public void EscExit()
    {
        TestTrace.Write("pen-esc", "exit");
        _host.EndPen("esc");
    }

    public void CloseRequested(PenSurfaceWindow surface)
    {
        // 予約の間に終了・再開始していれば何もしない（R2）。
        if (ReferenceEquals(_surface, surface)) _host.EndPen("closed");
    }

    public void SurfaceActivated()
    {
        _host.RaisePanel();
        // 最初のクリックでアクティブになるとき（R3）は、Activated の後に入力面が Z 順の先頭へ上がり、パネルが下になる
        // （工程Fで確認）。アクティブ化の処理が終わった後にもう一度上げる。
        _surface?.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _host.RaisePanel());
    }

    public void NoteForeground(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || IsPenWindow(foreground)) return;
        // 直前が操作パネルなど自アプリの窓なら候補なし（D4。古い外部窓を残さない。R9）。
        if (!_host.IsOwnWindow(foreground)) _restoreCandidate = foreground;
        else if (!IsClickThrough(foreground)) _restoreCandidate = IntPtr.Zero;
    }

    private static bool IsClickThrough(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0;

    /// <summary>入力面かそのメニューの窓か（前面が移っても戻す候補を変えない）。</summary>
    private bool IsPenWindow(IntPtr hwnd) =>
        (_surface is { } s && hwnd == s.Handle) || _menuHwnds.Contains(hwnd) || _closedMenuHwnds.Contains(hwnd);

    // ---- 前面の変化の監視（ペンの間だけ。戻す候補を決める） ----

    private IntPtr _lastForeign;
    // 入力面・メニュー以外の最後の前面（自アプリの窓を含む。最初のクリック前に前面を返す先。R3）。
    private IntPtr _lastNonPen;
    private IntPtr _foregroundHook;
    private WinEventProc? _foregroundProc;

    private void NoteForeign(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || IsPenWindow(hwnd)) return;
        if (!_host.IsOwnWindow(hwnd))
        {
            _lastForeign = hwnd;
            return;
        }
        // 操作パネルなど自アプリの窓へ移ったら、それより前の外部窓は候補にしない（R9）。クリックを通すオーバーレイ
        // （Cursor など）は、線を画面の外で離したときなどに OS が一時的に前面にする（工程Fで確認）ため数えない。
        if (!IsClickThrough(hwnd)) _lastForeign = IntPtr.Zero;
    }

    private void StartForegroundWatch()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        // 呼び出し元のスレッド（UI）のメッセージループで届く。デリゲートは解除まで保持する。
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void StopForegroundWatch()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundProc = null;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        TestTrace.Write("pen-foreground", hwnd.ToString("X"));
        if (_surface is { } s && hwnd == s.Handle)
        {
            if (!s.ActivationEnabled)
            {
                ReturnForegroundBeforeFirstClick(s);
                return;
            }
            // 直前が自アプリの窓なら _lastForeign は空で、候補もなしになる（R9）。
            _restoreCandidate = _lastForeign;
            return;
        }
        if (hwnd != IntPtr.Zero && !IsPenWindow(hwnd)) _lastNonPen = hwnd;
        NoteForeign(hwnd);
    }

    public void CaptureFailed() => TestTrace.Write("pen-capture-failed");

    // ---- 消しゴム（P7） ----

    public void Hover(PenSurfaceWindow surface, Point p)
    {
        if (!ReferenceEquals(surface, _surface) || IsMenuOpen || !_tools.IsEraser) return;
        _hoverPoint = p;
        if (_hoverScheduled) return;
        _hoverScheduled = true;
        surface.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _hoverScheduled = false;
            if (_hoverPoint is { } q && _surface is not null && !IsMenuOpen && _tools.IsEraser) UpdateHoverAt(q);
        });
    }

    public void HoverEnded(PenSurfaceWindow surface)
    {
        if (!ReferenceEquals(surface, _surface)) return; // 古い入力面の遅れた通知
        _hoverPoint = null;
        SetHoverCandidate(null);
    }

    public void EraseAt(PenSurfaceWindow surface, Point p)
    {
        if (!ReferenceEquals(surface, _surface) || IsMenuOpen || !_tools.IsEraser) return;
        _hoverPoint = null;
        var index = _drawing.HitTest(new PenPoint(p.X, p.Y));
        if (index < 0)
        {
            SetHoverCandidate(null);
            TestTrace.Write("pen-erase", "none");
            return;
        }
        var hit = _drawing.Strokes[index];
        if (!ReferenceEquals(hit, _layer.Hover))
        {
            // 強調していない画は消さない。押した位置の画を強調して次のクリックを待つ（設計レビュー D7）。
            SetHoverCandidate(hit);
            TestTrace.Write("pen-erase", "mismatch");
            return;
        }
        _drawing.Remove(hit);
        _layer.RenderCommitted();
        TestTrace.Write("pen-erase", _drawing.Strokes.Count.ToString(CultureInfo.InvariantCulture));
        UpdateCursor();
        UpdateHoverAt(p);
        ReleaseOwnerIfIdle();
    }

    public void EraserReleased(PenSurfaceWindow surface, Point p)
    {
        if (!ReferenceEquals(surface, _surface) || IsMenuOpen || !_tools.IsEraser) return;
        UpdateHoverAt(p);
    }

    private void UpdateHoverAt(Point p)
    {
        var index = _drawing.HitTest(new PenPoint(p.X, p.Y));
        SetHoverCandidate(index >= 0 ? _drawing.Strokes[index] : null);
    }

    private void SetHoverCandidate(PenStroke? stroke)
    {
        _layer.SetHover(stroke);
        TraceHover();
    }

    private void TraceHover()
    {
        var h = _layer.Hover;
        var value = h is null ? "none" : IndexOf(h).ToString(CultureInfo.InvariantCulture);
        if (value == _hoverTrace) return;
        _hoverTrace = value;
        TestTrace.Write("pen-hover", value);
    }

    private int IndexOf(PenStroke s)
    {
        var strokes = _drawing.Strokes;
        for (var i = 0; i < strokes.Count; i++) if (ReferenceEquals(strokes[i], s)) return i;
        return -1;
    }

    /// <summary>移動の通知に頼らず、今のカーソルの位置で候補を判定し直す（メニューを閉じた後・入力面の作成の後。設計レビュー D7）。</summary>
    private void ScheduleHoverRefresh()
    {
        if (_surface is not { } surface) return;
        surface.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(surface, _surface) || IsMenuOpen) return;
            if (!_tools.IsEraser)
            {
                SetHoverCandidate(null);
                return;
            }
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed) return;
            if (!GetCursorPos(out var pt) || !surface.IsVisible) return;
            Point p;
            try { p = surface.PointFromScreen(new Point(pt.X, pt.Y)); }
            catch (InvalidOperationException) { return; }
            if (p.X < 0 || p.Y < 0 || p.X >= surface.ActualWidth || p.Y >= surface.ActualHeight)
            {
                SetHoverCandidate(null);
                return;
            }
            UpdateHoverAt(p);
        });
    }

    /// <summary>描線が変わった後（一画戻す・全消去・付け直し）。カーソルと候補を合わせる。</summary>
    private void AfterLinesChanged()
    {
        UpdateCursor();
        TraceHover();
        if (_tools.IsEraser) ScheduleHoverRefresh();
    }

    // ---- カーソル（P4） ----

    private void UpdateCursor()
    {
        if (_surface is not { } surface) return;
        Cursor cursor;
        string name;
        if (_tools.Tool != PenTool.Eraser && _drawing.IsAtLimit)
        {
            cursor = Cursors.No;
            name = "no";
        }
        else if (_tools.Tool == PenTool.Eraser)
        {
            cursor = Cursors.Arrow;
            name = "eraser";
        }
        else if (_tools.Tool == PenTool.Highlighter)
        {
            cursor = HighlighterCursor(out var ok);
            name = ok ? "highlighter" : "fallback";
        }
        else
        {
            cursor = Cursors.Pen;
            name = "pen";
        }
        surface.SetToolCursor(cursor);
        if (name != _cursorTrace)
        {
            _cursorTrace = name;
            TestTrace.Write("pen-cursor", name);
        }
    }

    private Cursor HighlighterCursor(out bool ok)
    {
        var (color, _) = PenHighlighter.Resolve(_settings.HighlighterColor);
        var scale = Owner?.Scale ?? 1.0;
        var key = (color, scale);
        if (_highlighterCursor is not null && _highlighterKey == key)
        {
            ok = true;
            return _highlighterCursor;
        }
        if (_highlighterFailedKey == key)
        {
            ok = false;
            return Cursors.Cross;
        }
        try
        {
            var created = PenCursors.CreateHighlighter(color, scale);
            var old = _highlighterCursor;
            _highlighterCursor = created;
            _highlighterKey = key;
            _highlighterFailedKey = null;
            _surface?.SetToolCursor(created);
            old?.Dispose(); // 差し替えた後に捨てる
            ok = true;
            return created;
        }
        catch (Exception ex)
        {
            _highlighterFailedKey = key;
            if (!_cursorErrorLogged)
            {
                _cursorErrorLogged = true; // 起動中で 1 回だけ
                ErrorLog.Write("Pen: could not create the highlighter cursor. Using the cross cursor.", ex);
            }
            ok = false;
            return Cursors.Cross;
        }
    }

    // ---- 右クリックのメニュー（D6・P9） ----

    public void OpenMenu(Point p)
    {
        if (_surface is not { } surface) return;
        CloseMenu();
        SetHoverCandidate(null);
        _hoverPoint = null;
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = surface,
        };
        Grid.SetIsSharedSizeScope(menu, true);
        if (_drawing.IsAtLimit)
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = Strings.PenMenuLimit, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }, IsEnabled = false });
        var penColor = ArgbColor.TryParse(_settings.Color, out var pc) ? pc : ArgbColor.FromRgb(0xFF, 0x20, 0x20);
        foreach (var (width, label) in new[] { (PenWidth.Thin, Strings.PenThin), (PenWidth.Medium, Strings.PenMedium), (PenWidth.Thick, Strings.PenThick) })
        {
            var item = new MenuItem
            {
                Header = ToolHeader(label, penColor, _settings.WidthOf(width), 1.0),
                IsCheckable = true,
                IsChecked = _tools.IsCheckedWidth(width),
            };
            var w = width;
            item.Click += (_, _) => SelectTool(() => _tools.SelectWidth(w));
            menu.Items.Add(item);
        }
        var (hlColor, hlOpacity) = PenHighlighter.Resolve(_settings.HighlighterColor);
        var highlighter = new MenuItem
        {
            Header = ToolHeader(Strings.PenHighlighter, hlColor, PenHighlighter.Width, hlOpacity),
            IsCheckable = true,
            IsChecked = _tools.IsCheckedHighlighter,
        };
        highlighter.Click += (_, _) => SelectTool(_tools.SelectHighlighter);
        menu.Items.Add(highlighter);
        var eraser = new MenuItem
        {
            Header = ToolHeader(Strings.PenEraser, null, 0, 1.0),
            IsCheckable = true,
            IsChecked = _tools.IsCheckedEraser,
        };
        eraser.Click += (_, _) => SelectTool(_tools.SelectEraser);
        menu.Items.Add(eraser);
        menu.Items.Add(new Separator());
        menu.Items.Add(TargetMenu(menu, surface));
        menu.Items.Add(new Separator());
        var hasStrokes = _drawing.Strokes.Count > 0;
        var undo = new MenuItem { Header = Strings.PenMenuUndo, IsEnabled = hasStrokes };
        undo.Click += (_, _) => Undo();
        var clear = new MenuItem { Header = Strings.PenMenuClearAll, IsEnabled = hasStrokes };
        clear.Click += (_, _) => ClearAll();
        var clearExit = new MenuItem { Header = Strings.PenMenuClearExit };
        clearExit.Click += (_, _) => ClearAndExit();
        menu.Items.Add(undo);
        menu.Items.Add(clear);
        menu.Items.Add(clearExit);
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) TestTrace.Write("pen-esc", "menu");
        };
        menu.Opened += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu) && PresentationSource.FromVisual(menu) is HwndSource source) AddMenuHwnd(source.Handle);
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
            {
                // 切り替えの予約に、閉じる直前のメニューの窓を写す（D4）。
                if (_pendingSwitch is { } pending && ReferenceEquals(pending.Menu, menu)) pending.Hwnds = new HashSet<IntPtr>(_menuHwnds);
                _menu = null;
                _menuHwnds.Clear();
            }
            _host.RaisePanel();
            if (_pendingSwitch is { } p && ReferenceEquals(p.Menu, menu)) ScheduleSwitch();
            ScheduleHoverRefresh(); // 閉じた後、今の位置で候補を出し直す（D7）
        };
        _menu = menu;
        menu.IsOpen = true;
    }

    private void AddMenuHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !_menuHwnds.Add(hwnd)) return;
        TestTrace.Write("pen-menu-hwnd", _menuHwnds.Count.ToString(CultureInfo.InvariantCulture));
    }

    private void SelectTool(Action select)
    {
        var before = _tools.TraceName;
        select();
        if (before != _tools.TraceName) TestTrace.Write("pen-tool", _tools.TraceName);
        if (_tools.Tool == PenTool.Highlighter) _highlighterFailedKey = null; // 選び直しのたびに作り直しを試す（P4）
        if (!_tools.IsEraser) SetHoverCandidate(null);
        UpdateCursor();
    }

    /// <summary>「書き込み先」のサブメニュー（P6）。候補は最新の列挙から。</summary>
    private MenuItem TargetMenu(ContextMenu menu, PenSurfaceWindow surface)
    {
        var root = new MenuItem { Header = Strings.PenMenuTarget };
        var candidates = PenTargetResolver.SwitchCandidates(_host.CurrentDisplays);
        foreach (var d in candidates)
        {
            var item = new MenuItem
            {
                Header = PenDisplayLabel.MenuLabel(d),
                IsCheckable = true,
                IsChecked = Owner is { } o && PenTargetResolver.SameIdentity(o, d),
                Tag = d,
            };
            var chosen = d;
            item.Click += (_, _) =>
            {
                // Opened で取れなかった場合に備え、閉じる前の親とサブメニューの窓をここでも記録する（D4）。
                if (ReferenceEquals(_menu, menu))
                {
                    if (PresentationSource.FromVisual(menu) is HwndSource parent) AddMenuHwnd(parent.Handle);
                    if (PresentationSource.FromVisual(item) is HwndSource sub) AddMenuHwnd(sub.Handle);
                }
                _pendingSwitch = new PendingSwitch(surface, menu, chosen);
                // 既に閉じていれば（Closed が先に来た場合）ここで予約する。
                if (!ReferenceEquals(_menu, menu)) ScheduleSwitch();
            };
            root.Items.Add(item);
        }
        root.IsEnabled = candidates.Count > 0;
        root.SubmenuOpened += (_, _) =>
        {
            // サブメニューの Popup の窓を、描かれた後に記録する（前面になっても戻す候補を消さない。D4）。
            root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!ReferenceEquals(_menu, menu) || root.Items.Count == 0) return;
                if (root.Items[0] is Visual v && PresentationSource.FromVisual(v) is HwndSource source) AddMenuHwnd(source.Handle);
            });
        };
        return root;
    }

    private void ScheduleSwitch()
    {
        if (_pendingSwitch is not { } pending) return;
        pending.Surface.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(_pendingSwitch, pending)) return;
            _pendingSwitch = null;
            // 予約の間に終了・再開始した、または別のメニューが開いていれば何もしない（P6 の手順 1）。
            if (!ReferenceEquals(_surface, pending.Surface) || _menu is not null)
            {
                TestTrace.Write("pen-switch", "stale");
                return;
            }
            _closedMenuHwnds = pending.Hwnds;
            try
            {
                _host.SwitchPenTarget(pending.Chosen);
            }
            finally
            {
                _closedMenuHwnds = new HashSet<IntPtr>();
            }
        });
    }

    /// <summary>メニューの行（名前と見本の線）。<paramref name="color"/> が null なら見本なし（消しゴム）。</summary>
    private static UIElement ToolHeader(string label, ArgbColor? color, double thickness, double opacity)
    {
        // 名前の列は道具の行で共有の幅（メニューに Grid.IsSharedSizeScope）。英語の「Highlighter」が線に接しないように。
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 48, SharedSizeGroup = "PenWidthLabel" });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        if (color is not { } c) return panel;
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        var line = new Line
        {
            X1 = thickness / 2, Y1 = 0, X2 = 72 - thickness / 2, Y2 = 0,
            Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 72, Height = Math.Max(thickness, 4),
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(0, Math.Max(thickness, 4) / 2),
        };
        Grid.SetColumn(line, 1);
        panel.Children.Add(line);
        return panel;
    }

    private void CloseMenu()
    {
        if (_menu is { IsOpen: true } m) m.IsOpen = false;
        _menu = null;
        _menuHwnds.Clear();
    }

    public void Undo()
    {
        if (!_drawing.Undo()) return;
        _layer.RenderCommitted();
        TestTrace.Write("pen-undo", _drawing.Strokes.Count.ToString(CultureInfo.InvariantCulture));
        AfterLinesChanged();
        ReleaseOwnerIfIdle();
    }

    public void ClearAll()
    {
        _drawing.Clear();
        _layer.RenderCommitted();
        TestTrace.Write("pen-clear");
        AfterLinesChanged();
        ReleaseOwnerIfIdle();
    }

    private void ClearAndExit()
    {
        _drawing.Clear();
        _layer.RenderCommitted();
        TestTrace.Write("pen-discard", "menu-clear-exit");
        _host.EndPen("menu-clear-exit");
    }

    /// <summary>アプリの終了。入力面を閉じる（描線はプロセスとともに消える）。</summary>
    public void Dispose()
    {
        _retry.Stop();
        StopForegroundWatch();
        CloseMenu();
        if (_surface is { } s)
        {
            _surface = null;
            s.CloseByController();
        }
        Detach();
        _highlighterCursor?.Dispose();
        _highlighterCursor = null;
    }
}
