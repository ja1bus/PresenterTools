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
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(Clicks);
        root.Children.Add(Laser);
        root.Children.Add(KeyDisplay);
        Content = root;
        SetBounds(monitor.Bounds);
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
