using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.State;
using PresenterTools.Services;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.UI;

/// <summary>
/// 操作パネル（SPEC §10.1）。現在の状態の表示と主要機能の ON/OFF だけを行う。
/// × で閉じてもアプリは終了せず非表示（トレイ格納）にする。
/// </summary>
public partial class ControlPanelWindow : Window
{
    private static readonly Brush OnBrush = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush OffBrush = Frozen(0x9E, 0x9E, 0x9E);
    private static readonly Brush ReadyBrush = Frozen(0xF5, 0x7F, 0x17);
    private static readonly Brush ActiveBrush = Frozen(0xD3, 0x2F, 0x2F);
    private static readonly Brush ZoomSelectedBrush = Frozen(0x15, 0x65, 0xC0);
    private static readonly Brush NeutralBrush = Frozen(0x78, 0x90, 0x9C);

    private static readonly Brush HintBrush = Frozen(0x5F, 0x63, 0x68);
    private static readonly Brush HintFailedBrush = Frozen(0xC6, 0x28, 0x28);

    /// <summary>パネルにショートカットを添える操作（修飾キーをまとめるかどうかはこれらだけで判断する）。</summary>
    public static readonly IReadOnlyList<HotkeyAction> ShortcutActions =
    [
        HotkeyAction.ToggleMaster,
        HotkeyAction.ToggleCursorHighlight,
        HotkeyAction.ToggleClickEffect,
        HotkeyAction.ToggleLaser,
        HotkeyAction.LaserHold,
        HotkeyAction.ToggleMagnifier,
        HotkeyAction.ToggleKeyDisplay,
    ];

    private readonly AppHost _host;
    private readonly DispatcherTimer _saveTimer;
    private MonitorService? _monitors;
    private ControlPanelSettings? _pendingPlacement;
    private bool _placementReady;

    public ControlPanelWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _host.SaveSettings();
        };
        LocationChanged += (_, _) => SchedulePlacementSave();
        SizeChanged += (_, e) =>
        {
            if (_pendingPlacement is { } cp && ActualHeight > 0)
            {
                // 表示時の最初の大きさ決定（内容に合わせた高さが決まった時点）で位置を決める。
                _pendingPlacement = null;
                PlaceAt(cp);
                return;
            }
            // 登録失敗の表示などで縦に伸びたとき、作業領域の下へはみ出した分を内側へずらす。
            if (e.HeightChanged && e.NewSize.Height > e.PreviousSize.Height && _placementReady && IsVisible && _monitors is not null)
                EnsureOnScreen(_monitors.Monitors);
            SchedulePlacementSave();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    /// <summary>
    /// 高さは内容で決まる（SPEC §10.1）。上下の縁・四隅は高さを変えない縁として扱い、利用者には幅だけを変えさせる。
    /// 幅を変えると WPF は SizeToContent を Manual にするので、変え終わったら内容に合わせる設定へ戻す。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                var hit = (int)DefWindowProc(hwnd, msg, wParam, lParam);
                int? mapped = hit switch
                {
                    HTTOP or HTBOTTOM => HTBORDER,
                    HTTOPLEFT or HTBOTTOMLEFT => HTLEFT,
                    HTTOPRIGHT or HTBOTTOMRIGHT => HTRIGHT,
                    _ => null,
                };
                if (mapped is { } m)
                {
                    handled = true;
                    return new IntPtr(m);
                }
                break;
            case WM_EXITSIZEMOVE:
                if (SizeToContent != SizeToContent.Height) SizeToContent = SizeToContent.Height;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>状態に合わせて表示を更新する。</summary>
    public void Refresh(PresenterState s, AppSettings settings, string? status, ShortcutHints hints, Func<HotkeyAction, bool> isFailed)
    {
        RefreshShortcuts(settings, hints, isFailed);

        var master = s.MasterEnabled;
        SetButton(MasterButton, master ? "ON" : "OFF", master ? OnBrush : ActiveBrush, dim: false);
        SetButton(PointerButton, OnOff(s.CursorHighlightEnabled, master), s.CursorHighlightEnabled ? OnBrush : OffBrush, !master);
        SetButton(ClickButton, OnOff(s.ClickEffectEnabled, master), s.ClickEffectEnabled ? OnBrush : OffBrush, !master);

        var (laserText, laserBrush) = s.LaserStatus switch
        {
            LaserStatus.Active => ("ACTIVE", ActiveBrush),
            LaserStatus.Ready => ("READY", ReadyBrush),
            _ => s.LaserEnabled ? ("READY", ReadyBrush) : ("OFF", OffBrush),
        };
        SetButton(LaserButton, master ? laserText : laserText + "  (Master OFF)", laserBrush, !master);

        var zoom = settings.Magnifier.ZoomPercent;
        SetButton(MagnifierButton, s.MagnifierEnabled ? $"ON  {zoom}%" + (master ? "" : "  (Master OFF)") : OnOff(false, master),
            s.MagnifierEnabled ? OnBrush : OffBrush, !master);
        SetButton(Zoom200, "200%", zoom == 200 ? ZoomSelectedBrush : NeutralBrush, false);
        SetButton(Zoom300, "300%", zoom == 300 ? ZoomSelectedBrush : NeutralBrush, false);
        SetButton(Zoom400, "400%", zoom == 400 ? ZoomSelectedBrush : NeutralBrush, false);

        var keyOn = s.KeyDisplayMode != KeyDisplayMode.Off;
        SetButton(KeyDisplayButton, OnOff(keyOn, master), keyOn ? OnBrush : OffBrush, !master);

        PinButton.IsChecked = settings.General.AlwaysOnTop;
        Topmost = settings.General.AlwaysOnTop;

        StatusText.Text = status ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 各項目の下に割り当て中のショートカットを出す（SPEC §10.1）。ボタンのツールチップには常に正式な表記を出す。
    /// 登録に失敗したものは赤で示す。
    /// </summary>
    private void RefreshShortcuts(AppSettings settings, ShortcutHints hints, Func<HotkeyAction, bool> isFailed)
    {
        string Full(HotkeyAction a) => Shortcut.TryParse(settings.Hotkeys.Get(a), out var sc) ? sc.ToString() : "";
        string Tip(string what, params (string Label, HotkeyAction Action)[] items)
        {
            var lines = items.Where(i => Full(i.Action).Length > 0)
                .Select(i => $"{i.Label}: {Full(i.Action)}" + (isFailed(i.Action) ? Strings.PanelFailedSuffix : ""));
            return string.Join(Environment.NewLine, new[] { what }.Concat(lines));
        }

        SetHint(MasterHint, hints, isFailed, HotkeyAction.ToggleMaster);
        SetHint(PointerHint, hints, isFailed, HotkeyAction.ToggleCursorHighlight);
        SetHint(ClickHint, hints, isFailed, HotkeyAction.ToggleClickEffect);
        SetHint(LaserHint, hints, isFailed, HotkeyAction.ToggleLaser, (HotkeyAction.LaserHold, Strings.PanelDrawPrefix));
        SetHint(MagnifierHint, hints, isFailed, HotkeyAction.ToggleMagnifier);
        SetHint(KeyDisplayHint, hints, isFailed, HotkeyAction.ToggleKeyDisplay);

        var shortcut = Strings.PanelShortcut;
        MasterButton.ToolTip = Tip(Strings.PanelMasterToolTip, (shortcut, HotkeyAction.ToggleMaster));
        PointerButton.ToolTip = Tip(Strings.PanelPointerToolTip, (shortcut, HotkeyAction.ToggleCursorHighlight));
        ClickButton.ToolTip = Tip(Strings.PanelClickToolTip, (shortcut, HotkeyAction.ToggleClickEffect));
        var toggle = settings.Laser.TriggerMode == LaserTriggerMode.Toggle;
        LaserButton.ToolTip = Tip(toggle ? Strings.PanelLaserToolTipToggle : Strings.PanelLaserToolTipHold,
            ("ON/OFF", HotkeyAction.ToggleLaser),
            (toggle ? Strings.PanelLaserDrawToggle : Strings.PanelLaserDrawHold, HotkeyAction.LaserHold));
        MagnifierButton.ToolTip = Tip(Strings.PanelMagnifierToolTip, (shortcut, HotkeyAction.ToggleMagnifier));
        KeyDisplayButton.ToolTip = Tip(Strings.PanelKeyDisplayToolTip, (shortcut, HotkeyAction.ToggleKeyDisplay));
    }

    /// <summary>
    /// 表記を設定する。<paramref name="second"/> は Laser 描画のように ON/OFF と並べるもの（接頭辞付き）。
    /// </summary>
    private static void SetHint(TextBlock hint, ShortcutHints hints, Func<HotkeyAction, bool> isFailed, HotkeyAction action,
        (HotkeyAction Action, string Prefix)? second = null)
    {
        var parts = new List<string>(2);
        var failed = false;
        if (hints[action] is { Length: > 0 } a)
        {
            parts.Add(a);
            failed |= isFailed(action);
        }
        if (second is { } s && hints[s.Action] is { Length: > 0 } b)
        {
            parts.Add(s.Prefix + b);
            failed |= isFailed(s.Action);
        }
        // 1 行に収まらない長さなら 2 行にする。
        var text = string.Join(parts.Sum(p => p.Length) > 12 ? Environment.NewLine : " / ", parts);
        hint.Text = failed ? "⚠ " + text : text;
        hint.Foreground = failed ? HintFailedBrush : HintBrush;
        hint.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>表示して前面に出す（トレイ・最小化・他ウィンドウの後ろから）。</summary>
    public void ShowAndActivate(IReadOnlyList<MonitorInfo> monitors)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        EnsureOnScreen(monitors);
        Activate();
        if (!Topmost)
        {
            // 最前面設定でなくても一度は前面に出す。
            Topmost = true;
            Topmost = false;
        }
        Focus();
    }

    /// <summary>
    /// 保存済みの位置と幅を復元する（SPEC §13）。表示前に呼ぶ。高さは内容で決まるため保存値を使わず、
    /// 位置は表示時に高さが決まってから決める（初回の右下配置に実際の高さが要るため）。
    /// </summary>
    public void RestorePlacement(ControlPanelSettings cp, MonitorService monitors)
    {
        _monitors = monitors;
        Width = cp.Width;
        _pendingPlacement = cp;
    }

    private void PlaceAt(ControlPanelSettings cp)
    {
        var monitors = _monitors!;
        var handle = new WindowInteropHelper(this).Handle;
        var height = ActualHeight;

        RectInt rect;
        if (cp.Left is { } left && cp.Top is { } top)
        {
            var scale = monitors.FromPoint(new PointInt(left + 10, top + 10)).DpiScale;
            rect = new RectInt(left, top, (int)(cp.Width * scale), (int)(height * scale));
        }
        else
        {
            var primary = monitors.Primary;
            rect = WindowPlacement.DefaultPosition(
                new SizeInt((int)(cp.Width * primary.DpiScale), (int)(height * primary.DpiScale)), primary.WorkArea);
        }
        rect = WindowPlacement.EnsureVisible(rect, monitors.Monitors.Select(m => m.Area).ToList());
        SetWindowPos(handle, IntPtr.Zero, rect.Left, rect.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        _placementReady = true;
    }

    /// <summary>見えない位置（切断したモニター上など）にあれば見える位置へ移す。</summary>
    public void EnsureOnScreen(IReadOnlyList<MonitorInfo> monitors)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState != WindowState.Normal || !GetWindowRect(handle, out var r)) return;
        var current = RectInt.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var fixedRect = WindowPlacement.EnsureVisible(current, monitors.Select(m => m.Area).ToList());
        if (fixedRect == current) return;
        SetWindowPos(handle, IntPtr.Zero, fixedRect.Left, fixedRect.Top, fixedRect.Width, fixedRect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>現在の位置（物理ピクセル）とサイズ（DIP）を設定へ書く。通常表示のときだけ。高さは記録のみで復元には使わない。</summary>
    public void CapturePlacement(ControlPanelSettings cp)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!_placementReady || handle == IntPtr.Zero || WindowState != WindowState.Normal || !GetWindowRect(handle, out var r)) return;
        cp.Left = r.Left;
        cp.Top = r.Top;
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            cp.Width = Math.Round(ActualWidth);
            cp.Height = Math.Round(ActualHeight);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_host.IsExiting) return;
        // × はトレイ格納（SPEC §32 D-14）。
        e.Cancel = true;
        _host.SaveSettings();
        Hide();
    }

    private void SchedulePlacementSave()
    {
        if (!_placementReady || !IsVisible) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static string OnOff(bool on, bool master) => (on ? "ON" : "OFF") + (master ? "" : "  (Master OFF)");

    private static void SetButton(Button b, string text, Brush background, bool dim)
    {
        b.Content = text;
        b.Background = background;
        b.Opacity = dim ? 0.5 : 1.0;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnMasterClick(object sender, RoutedEventArgs e) => _host.State.ToggleMaster();
    private void OnPointerClick(object sender, RoutedEventArgs e) => _host.State.SetCursorHighlight(!_host.State.CursorHighlightEnabled);
    private void OnClickFxClick(object sender, RoutedEventArgs e) => _host.State.SetClickEffect(!_host.State.ClickEffectEnabled);
    private void OnLaserClick(object sender, RoutedEventArgs e) => _host.State.ToggleLaser();
    private void OnMagnifierClick(object sender, RoutedEventArgs e) => _host.State.ToggleMagnifier();
    private void OnKeyDisplayClick(object sender, RoutedEventArgs e) => _host.State.ToggleKeyDisplay();
    private void OnPinClick(object sender, RoutedEventArgs e) => _host.SetAlwaysOnTop(PinButton.IsChecked == true);
    private void OnSettingsClick(object sender, RoutedEventArgs e) => _host.ShowSettings();
    private void OnExitClick(object sender, RoutedEventArgs e) => _host.Exit();

    private void OnZoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var zoom)) _host.SetMagnifierZoom(zoom);
    }
}
