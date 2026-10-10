using System.Windows.Media;
using System.Windows.Shapes;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Models;

namespace PresenterTools.Overlay;

/// <summary>
/// Cursor Highlight のリング・塗りつぶし円（SPEC §8.2）。円の大きさだけの小さな窓をカーソルに合わせて移動する。
/// 全画面のオーバーレイを毎回描き直すより、窓の移動だけで済むため負荷が小さい。
/// </summary>
public sealed class CursorRingWindow : ClickThroughWindow
{
    private readonly Ellipse _ring = new();
    private double _diameter = 64;

    public CursorRingWindow()
    {
        Title = "PresenterTools Cursor";
        Content = _ring;
    }

    public void ApplySettings(CursorHighlightSettings s)
    {
        _diameter = s.Diameter;
        var color = ArgbColor.TryParse(s.Color, out var c) ? c : ArgbColor.FromRgb(255, 212, 0);
        var filled = s.Style == CursorHighlightStyle.FilledCircle;
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)) { Opacity = filled ? s.FillOpacity : s.Opacity };
        brush.Freeze();
        _ring.Fill = filled ? brush : null;
        _ring.Stroke = filled ? null : brush;
        _ring.StrokeThickness = filled ? 0 : s.Thickness;
    }

    /// <summary>カーソル位置（物理ピクセル）と、そのモニターの DPI 倍率に合わせて移動する。</summary>
    public void MoveTo(PointInt cursor, double monitorScale)
    {
        var size = Math.Max(4, (int)Math.Ceiling(_diameter * monitorScale));
        SetBounds(new RectInt(cursor.X - size / 2, cursor.Y - size / 2, size, size));
    }
}
