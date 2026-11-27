using System.Windows;
using System.Windows.Controls;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Models;
using PresenterTools.Services;

namespace PresenterTools.Overlay;

/// <summary>
/// モニター 1 台を覆うオーバーレイ（SPEC §14）。クリック波紋・レーザー・Key Display を描く。
/// </summary>
public sealed class OverlayWindow : ClickThroughWindow
{
    private const double KeyDisplayMargin = 48;

    public OverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        Title = "PresenterTools Overlay";
        _root.Children.Add(Clicks);
        _root.Children.Add(_penHost);
        _root.Children.Add(Laser);
        _root.Children.Add(KeyDisplay);
        Content = _root;
        SetBounds(monitor.Bounds);
        // 拡大率だけの変更は DisplaySettingsChanged が来ないことがあるため、画面ペンの再確認に使う（docs/archive/design-1.7.0.md D11）。
        DpiChanged += (_, _) => MonitorDpiChanged?.Invoke(this);
    }

    private readonly Grid _root = new() { IsHitTestVisible = false };
    // 画面ペンの描線（V1.7.0）。波紋の上・Laser と Key Display の下。
    private readonly Grid _penHost = new() { IsHitTestVisible = false };

    /// <summary>この窓が WM_DPICHANGED を受けた。</summary>
    public event Action<OverlayWindow>? MonitorDpiChanged;

    /// <summary>画面ペンの描線を載せる（null で外す）。</summary>
    public void SetPenLayer(UIElement? layer)
    {
        _penHost.Children.Clear();
        if (layer is not null) _penHost.Children.Add(layer);
    }

    public MonitorInfo Monitor { get; }
    public ClickEffectLayer Clicks { get; } = new();
    public LaserLayer Laser { get; } = new();
    public KeyDisplayView KeyDisplay { get; } = new();

    /// <summary>画面座標（物理ピクセル）をこの窓のローカル DIP に変換する。</summary>
    public Point ToLocal(PointInt p)
    {
        var s = Scale;
        return new Point((p.X - Monitor.Bounds.Left) / s, (p.Y - Monitor.Bounds.Top) / s);
    }

    /// <summary>Key Display の位置。下側はタスクバーを避けるため作業領域を基準にする。</summary>
    public void ApplyKeyDisplayPosition(KeyDisplayPosition position)
    {
        var s = Scale;
        var b = Monitor.Bounds;
        var w = Monitor.WorkArea;
        var left = (w.Left - b.Left) / s + KeyDisplayMargin;
        var top = (w.Top - b.Top) / s + KeyDisplayMargin;
        var right = (b.Right - w.Right) / s + KeyDisplayMargin;
        var bottom = (b.Bottom - w.Bottom) / s + KeyDisplayMargin;

        KeyDisplay.HorizontalAlignment = position switch
        {
            KeyDisplayPosition.BottomLeft or KeyDisplayPosition.TopLeft => HorizontalAlignment.Left,
            KeyDisplayPosition.BottomRight or KeyDisplayPosition.TopRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        KeyDisplay.VerticalAlignment = position is KeyDisplayPosition.TopCenter or KeyDisplayPosition.TopLeft or KeyDisplayPosition.TopRight
            ? VerticalAlignment.Top
            : VerticalAlignment.Bottom;
        KeyDisplay.Margin = new Thickness(left, top, right, bottom);
        KeyDisplay.MaxWidth = Math.Max(200, b.Width / s - left - right);
    }
}
