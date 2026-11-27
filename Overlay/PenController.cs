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
}

/// <summary>
/// 画面ペンの入力面・メニュー・描線の表示の統合（docs/archive/design-1.7.0.md D1・D4〜D7・D11）。AppHost から呼ぶ。
/// 描線は入力面ではなく書き込み先の <see cref="OverlayWindow"/> に描き、Master に従って表示・非表示になる。
/// </summary>
public sealed class PenController : IPenSurfaceSink
{
    private readonly IPenHost _host;
    private readonly PenDrawing _drawing = new();
    private readonly PenSession _session = new();
    private readonly PenLayer _layer;
    private readonly DispatcherTimer _retry;
    private OverlayWindow? _attachedTo;
    private PenSurfaceWindow? _surface;
    private ContextMenu? _menu;
    private IntPtr _menuHwnd;
    private IntPtr _restoreCandidate;
    private long _retryGeneration;
    private PenSettings _settings = new();
    private bool _limitLogged;

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
    public PenWidth SelectedWidth { get; private set; } = PenWidth.Medium;
    /// <summary>
    /// メニューを開いてから Closed が来るまで true。メニューの外の入力面をクリックすると、IsOpen が false になった後、
    /// Closed より前に同じクリックが入力面へ届く（初期試験で確認）。それで点を描かないよう、Closed までを表示中とみなす（D6）。
    /// </summary>
    public bool IsMenuOpen => _menu is not null;
    public bool CanBeginStroke => !_drawing.IsAtLimit;

    /// <summary>描線に関係する設定（色・幅・開始時の太さ）。起動時は開始時の太さを選ぶ。</summary>
    public void Initialize(PenSettings settings)
    {
        _settings = settings;
        SelectedWidth = settings.StartWidth;
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
        var hadOwner = Owner is not null;
        if (!_session.Start(target, out _))
            ClearLines("target-changed"); // 防御（通常は D9・D11 で先に捨てている）
        if (!hadOwner) TestTrace.Write("pen-owner", "new");
        _retry.Stop();
        Attach(overlays);
        // 戻す候補（D4: 最後に入力面がアクティブになる直前の他のアプリの窓）。入力面が作成時に自分のスレッドで
        // アクティブ扱いになると、クリックで前面になっても WM_MOUSEACTIVATE が来ない（初期試験で確認）ため、
        // ペンの間は前面の変化を見て、他のアプリの最後の前面を覚え、入力面が前面になったときに候補にする。
        _restoreCandidate = IntPtr.Zero;
        _lastForeign = IntPtr.Zero;
        NoteForeign(GetForegroundWindow());
        _restoreCandidate = _lastForeign;
        StartForegroundWatch();
        _limitLogged = false;
        _surface = new PenSurfaceWindow(this, target.Bounds);
        _surface.SetLimitCursor(_drawing.IsAtLimit);
        _surface.DpiChanged += (_, _) => _host.RecheckPen("dpi"); // 契機 (2)（D11）
        _surface.Show();
        _surface.BringToTop();
        // 拡大率がシステムと違う画面では、作成時の WPF の DPI 処理で入力面がアクティブになり、前面を取れる状況だと
        // 開始時に前面を奪う（初期試験で確認）。開始は前面を変えない（D4・D7: 最初のクリックでアクティブになる）ため、
        // 開始前の前面が他のアプリなら戻す。
        // 戻す呼び出しは記録の引数に置かない（TestTrace.Write は Release で呼び出しごと消える。R1）。
        if (_lastForeign != IntPtr.Zero && GetForegroundWindow() == _surface.Handle)
        {
            var returned = SetForegroundWindow(_lastForeign);
            TestTrace.Write("pen-start-foreground", returned ? "returned" : "refused");
        }
        _host.RaisePanel();
        TestTrace.Write("pen-start", target.DeviceName);
    }

    /// <summary>入力面を閉じる（線は残す）。描線が無ければ所有者も解放する。</summary>
    public void Deactivate(string reason)
    {
        if (_surface is not { } surface) return;
        // (1) 前面が入力面かメニューかを判定し、(2) メニューを閉じてキャプチャを外し、(3) (1) が真なら候補へ前面を戻す（D4）。
        var foreground = GetForegroundWindow();
        // メニューは実際の Popup の HWND で判定する（Closed の前に前面が操作パネルへ移った場合を含めない。R6）。
        var ownForeground = foreground != IntPtr.Zero && (foreground == surface.Handle || foreground == _menuHwnd);
        StopForegroundWatch();
        CloseMenu();
        surface.FinishStroke();
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
        if (_session.SurfaceClosed(HasLines)) OwnerReleased();
    }

    /// <summary>描線を消して描き直す（所有者には触れない）。</summary>
    private void ClearLines(string? discardReason)
    {
        _drawing.Clear();
        _layer.InvalidatePending();
        _layer.RenderCommitted();
        if (discardReason is not null) TestTrace.Write("pen-discard", discardReason);
        _surface?.SetLimitCursor(false);
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

    // ---- 設定の適用（D9） ----

    /// <summary>
    /// 確定した設定を受ける。書き込み先が変わったら所有者と比べ、違えば線を捨ててペンを終える。開始時の太さが変われば選択も変える。
    /// 捨てたら true。
    /// </summary>
    public bool ApplySettings(PenSettings before, PenSettings after, DisplaySnapshot snapshot)
    {
        _settings = after;
        if (before.StartWidth != after.StartWidth) SelectedWidth = after.StartWidth;
        if (!_session.OnApply(before, after, snapshot)) return false;
        DiscardAll("target-changed", "target-changed");
        return true;
    }

    // ---- 入力面からの通知（D4） ----

    public void BeginStroke(Point p)
    {
        CloseMenu();
        var color = ArgbColor.TryParse(_settings.Color, out var c) ? c : ArgbColor.FromRgb(0xFF, 0x20, 0x20);
        if (!_drawing.BeginStroke(new PenPoint(p.X, p.Y), color, _settings.WidthOf(SelectedWidth)))
        {
            _surface?.SetLimitCursor(true);
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
            _surface?.SetLimitCursor(true);
        }
    }

    public void EndStroke()
    {
        var moves = _drawing.MovesReceived;
        var hit = _drawing.CurrentHitLimit;
        var stroke = _drawing.EndStroke();
        if (stroke is null) return;
        if (hit) TestTrace.Write("pen-limit", $"{moves} {_drawing.TotalPoints}");
        TestTrace.Write("pen-stroke", stroke.PointCount.ToString(CultureInfo.InvariantCulture));
        _layer.InvalidatePending();
        _layer.RenderCommitted();
        _surface?.SetLimitCursor(_drawing.IsAtLimit);
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

    public void SurfaceActivated() => _host.RaisePanel();

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
        (_surface is { } s && hwnd == s.Handle) || (_menuHwnd != IntPtr.Zero && hwnd == _menuHwnd);

    // ---- 前面の変化の監視（ペンの間だけ。戻す候補を決める） ----

    private IntPtr _lastForeign;
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
            // 直前が自アプリの窓なら _lastForeign は空で、候補もなしになる（R9）。
            _restoreCandidate = _lastForeign;
            return;
        }
        NoteForeign(hwnd);
    }

    public void CaptureFailed() => TestTrace.Write("pen-capture-failed");

    // ---- 右クリックのメニュー（D6） ----

    public void OpenMenu(Point p)
    {
        if (_surface is null) return;
        CloseMenu();
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = _surface,
        };
        Grid.SetIsSharedSizeScope(menu, true);
        if (_drawing.IsAtLimit)
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = Strings.PenMenuLimit, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }, IsEnabled = false });
        foreach (var (width, label) in new[] { (PenWidth.Thin, Strings.PenThin), (PenWidth.Medium, Strings.PenMedium), (PenWidth.Thick, Strings.PenThick) })
        {
            var item = new MenuItem
            {
                Header = WidthHeader(label, width),
                IsCheckable = true,
                IsChecked = SelectedWidth == width,
            };
            var w = width;
            item.Click += (_, _) => SelectedWidth = w;
            menu.Items.Add(item);
        }
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
            if (ReferenceEquals(_menu, menu) && PresentationSource.FromVisual(menu) is HwndSource source) _menuHwnd = source.Handle;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
            {
                _menu = null;
                _menuHwnd = IntPtr.Zero;
            }
            _host.RaisePanel();
        };
        _menu = menu;
        menu.IsOpen = true;
    }

    private UIElement WidthHeader(string label, PenWidth width)
    {
        var c = ArgbColor.TryParse(_settings.Color, out var color) ? color : ArgbColor.FromRgb(0xFF, 0x20, 0x20);
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        var thickness = _settings.WidthOf(width);
        // 名前の列は3行で共有の幅（メニューに Grid.IsSharedSizeScope）。英語の「Medium」が線に接しないように（初期試験で確認）。
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 48, SharedSizeGroup = "PenWidthLabel" });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var line = new Line
        {
            X1 = thickness / 2, Y1 = 0, X2 = 72 - thickness / 2, Y2 = 0,
            Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 72, Height = Math.Max(thickness, 4),
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
        _menuHwnd = IntPtr.Zero;
    }

    public void Undo()
    {
        if (!_drawing.Undo()) return;
        _layer.RenderCommitted();
        TestTrace.Write("pen-undo", _drawing.Strokes.Count.ToString(CultureInfo.InvariantCulture));
        _surface?.SetLimitCursor(_drawing.IsAtLimit);
        ReleaseOwnerIfIdle();
    }

    public void ClearAll()
    {
        _drawing.Clear();
        _layer.RenderCommitted();
        TestTrace.Write("pen-clear");
        _surface?.SetLimitCursor(false);
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
    }
}
