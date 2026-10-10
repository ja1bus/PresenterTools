using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.Effects;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.KeyDisplay;
using PresenterTools.Core.Models;
using PresenterTools.Services;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>
/// オーバーレイ一式（モニターごとのオーバーレイ、カーソルのリング、ボタン押下中の印）の生成・破棄と、
/// 各表示への配分（SPEC §8.2・§8.3・§13・§14）。
/// 波紋・レーザーのアニメーション中だけ CompositionTarget.Rendering を購読し、待機中は描画処理を行わない。
/// </summary>
public sealed class OverlayManager : IDisposable
{
    private readonly MonitorService _monitors;
    private readonly List<OverlayWindow> _overlays = [];
    private readonly CursorRingWindow _ring = new();
    private readonly HoldIndicatorWindow _hold = new();
    private readonly DispatcherTimer _holdDelay;
    private readonly LaserTrail _trail = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private AppSettings _settings = new();
    private bool _visible;
    private bool _ringVisible;
    private bool _laserActive;
    private bool _animating;
    private bool _leftHeld, _rightHeld, _lastHeldRight;
    private bool _holdShown;
    private OverlayWindow? _keyDisplayHost;

    public OverlayManager(MonitorService monitors)
    {
        _monitors = monitors;
        // 普通のクリックで印が一瞬だけ出てちらつかないよう、押し続けたときだけ表示する。
        _holdDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldDelayMs) };
        _holdDelay.Tick += OnHoldDelay;
        Rebuild();
    }

    /// <summary>ボタンを押してから押下中の印を出すまでの時間。</summary>
    public const int HoldDelayMs = 200;

    /// <summary>アニメーション中のフレームごとに発生（Laser 描画キーの解放・Esc の確認に使う）。</summary>
    public event Action? Frame;

    /// <summary>単調増加の経過秒。</summary>
    public double Now => _clock.Elapsed.TotalSeconds;

    public bool IsLaserActive => _laserActive;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _ring.ApplySettings(settings.CursorHighlight);
        _hold.ApplySettings(settings.ClickEffect);
        if (!settings.ClickEffect.ShowWhileHeld) HideHold();
        var laserColor = ArgbColor.TryParse(settings.Laser.Color, out var c) ? c : ArgbColor.FromRgb(255, 32, 32);
        _trail.LifetimeSeconds = settings.Laser.TrailSeconds;
        foreach (var o in _overlays)
        {
            o.Clicks.ApplySettings(settings.ClickEffect);
            o.Laser.SetStyle(Color.FromArgb(laserColor.A, laserColor.R, laserColor.G, laserColor.B), settings.Laser.Width);
            o.KeyDisplay.ApplySettings(settings.KeyDisplay);
            o.ApplyKeyDisplayPosition(settings.KeyDisplay.Position);
        }
        // 大きさが変わったら表示中の窓を今のカーソル位置で合わせ直す。Settings のプレビューで頻繁に呼ばれるため、
        // 隠して出し直すことはしない（ちらつかせない）。
        if (_ringVisible) MoveRing(CursorPosition());
    }

    /// <summary>モニター構成に合わせてオーバーレイを作り直す。</summary>
    public void Rebuild()
    {
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
        _keyDisplayHost = null;
        foreach (var m in _monitors.Monitors)
        {
            var o = new OverlayWindow(m);
            _overlays.Add(o);
            if (_visible) o.Show();
        }
        ApplySettings(_settings);
    }

    /// <summary>Master に合わせて全体を表示・非表示にする。</summary>
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        foreach (var o in _overlays)
        {
            if (visible)
            {
                o.Show();
                o.BringToTop();
            }
            else
            {
                o.Hide();
            }
        }
        if (!visible)
        {
            SetRingVisible(false);
            HideHold();
            ClearTransient();
        }
    }

    /// <summary>リングの表示・非表示。表示するときは現在のカーソル位置へ移す。</summary>
    public void SetRingVisible(bool visible, PointInt? cursor = null)
    {
        visible &= _visible;
        if (visible && cursor is { } p) MoveRing(p);
        if (_ringVisible == visible) return;
        _ringVisible = visible;
        if (visible)
        {
            _ring.Show();
            _ring.BringToTop();
        }
        else
        {
            _ring.Hide();
        }
    }

    /// <summary>Cursor Highlight の形（リング・塗りつぶし円）をカーソル位置へ移す。</summary>
    public void MoveRing(PointInt cursor) => _ring.MoveTo(cursor, _monitors.FromPoint(cursor).DpiScale);

    // ---- ボタン押下中の印（SPEC §8.3） ----

    /// <summary>左右ボタンの押下・解放を伝える。押下中の印の表示が OFF でも押下状態だけは追う。</summary>
    public void SetButtonHeld(bool isRight, bool down, PointInt p)
    {
        if (isRight) _rightHeld = down;
        else _leftHeld = down;
        if (down) _lastHeldRight = isRight;

        if (!_leftHeld && !_rightHeld)
        {
            HideHold();
            return;
        }
        var showRight = down ? isRight : _rightHeld;
        if (_holdShown)
        {
            _hold.SetButton(showRight);
            return;
        }
        if (down && _visible && _settings.ClickEffect.ShowWhileHeld)
        {
            _holdDelay.Stop();
            _holdDelay.Start();
        }
    }

    /// <summary>押下中の印をカーソルに追従させる。解放を取りこぼしていたら（管理者ウィンドウ上で離した等）消す。</summary>
    public void MoveHold(PointInt cursor)
    {
        if (!_holdShown) return;
        if (!AnyButtonDown())
        {
            HideHold();
            return;
        }
        _hold.MoveTo(cursor, _monitors.FromPoint(cursor).DpiScale);
    }

    /// <summary>押下中の印を消し、押下状態も忘れる。</summary>
    public void HideHold()
    {
        _holdDelay.Stop();
        _leftHeld = _rightHeld = false;
        if (!_holdShown) return;
        _holdShown = false;
        _hold.Hide();
    }

    private void OnHoldDelay(object? sender, EventArgs e)
    {
        _holdDelay.Stop();
        if (!_visible || !_settings.ClickEffect.ShowWhileHeld || !(_leftHeld || _rightHeld)) return;
        if (!AnyButtonDown())
        {
            HideHold();
            return;
        }
        _hold.SetButton(_leftHeld && _rightHeld ? _lastHeldRight : _rightHeld);
        _hold.MoveTo(CursorPosition(), _monitors.FromPoint(CursorPosition()).DpiScale);
        _holdShown = true;
        _hold.Show();
        _hold.BringToTop();
    }

    /// <summary>
    /// 物理的な左右ボタンのどちらかが押されているか。GetAsyncKeyState は左右入れ替え設定前の物理ボタンを返すため、
    /// どちらのボタンかは区別せず「どれかが押されているか」だけを見る。
    /// </summary>
    private static bool AnyButtonDown() => IsKeyDown(VK_LBUTTON) || IsKeyDown(VK_RBUTTON);

    private static PointInt CursorPosition() =>
        GetCursorPos(out var p) ? new PointInt(p.X, p.Y) : default;

    public void AddClick(PointInt p, bool isRight)
    {
        var o = OverlayAt(p);
        if (o is null || !_visible) return;
        o.Clicks.Add(o.ToLocal(p), isRight, Now);
        StartAnimation();
    }

    /// <summary>Laser の ACTIVE 開始・終了。終了後も軌跡は残像時間で消える。</summary>
    public void SetLaserActive(bool active, PointInt? cursor = null)
    {
        var next = active && _visible;
        if (next && !_laserActive)
        {
            var p = cursor ?? CursorPosition();
            _trail.BeginStroke(p.X, p.Y, Now);
        }
        else if (!next) _trail.EndStroke();
        _laserActive = next;
        StartAnimation();
    }

    public void AddLaserPoint(PointInt p)
    {
        if (!_laserActive) return;
        _trail.Add(p.X, p.Y, Now);
    }

    /// <summary>Key Display を指定モニターに表示する（他のモニターの表示は消す）。</summary>
    public void ShowKeyDisplay(MonitorInfo monitor, KeyDisplayModel model)
    {
        var host = _overlays.FirstOrDefault(o => o.Monitor.Handle == monitor.Handle) ?? _overlays.FirstOrDefault();
        if (host is null) return;
        if (_keyDisplayHost is not null && _keyDisplayHost != host) _keyDisplayHost.KeyDisplay.Hide();
        _keyDisplayHost = host;
        if (model.IsEmpty)
        {
            host.KeyDisplay.Hide();
            return;
        }
        host.KeyDisplay.Update(model.DisplayText, model.ChordKeys, model.ChordCount);
    }

    public void HideKeyDisplay()
    {
        foreach (var o in _overlays) o.KeyDisplay.Hide();
        _keyDisplayHost = null;
    }

    /// <summary>波紋・軌跡・Key Display を即座に消す。</summary>
    public void ClearTransient()
    {
        _laserActive = false;
        _trail.Clear();
        foreach (var o in _overlays)
        {
            o.Clicks.Clear();
            o.Laser.Update(_trail, o.Monitor.Bounds, o.Scale, Now, false);
        }
        HideKeyDisplay();
    }

    public void Dispose()
    {
        StopAnimation();
        _holdDelay.Stop();
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
        _ring.Close();
        _hold.Close();
    }

    private OverlayWindow? OverlayAt(PointInt p) => _overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(p));

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        Frame?.Invoke();
        var now = Now;
        _trail.Prune(now);
        var busy = _laserActive || !_trail.IsEmpty;
        foreach (var o in _overlays)
        {
            o.Clicks.Update(now);
            o.Laser.Update(_trail, o.Monitor.Bounds, o.Scale, now, _laserActive);
            busy |= o.Clicks.IsAnimating;
        }
        if (!busy) StopAnimation();
    }
}
