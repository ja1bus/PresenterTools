using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Models;

namespace PresenterTools.Overlay;

/// <summary>
/// マウスボタンを押している間の印（SPEC §8.3）。半透明の円＋縁取りをカーソルに合わせて移動する。
/// 色は押しているボタン（左・右）の Click Effect の色。
/// </summary>
public sealed class HoldIndicatorWindow : ClickThroughWindow
{
    private readonly Ellipse _fill = new();
    private readonly Ellipse _edge = new() { StrokeThickness = 3 };
    private Brush _leftFill = Brushes.Transparent, _leftEdge = Brushes.Transparent;
    private Brush _rightFill = Brushes.Transparent, _rightEdge = Brushes.Transparent;
    private double _diameter = 40;

    public HoldIndicatorWindow()
    {
        Title = "PresenterTools Hold";
        var root = new Grid();
        root.Children.Add(_fill);
        root.Children.Add(_edge);
        Content = root;
    }

    public void ApplySettings(ClickEffectSettings s)
    {
        // 波紋の最大サイズの半分。小さすぎると投影で見えないため下限を設ける。
        _diameter = Math.Max(28, s.Size / 2);
        (_leftFill, _leftEdge) = MakeBrushes(s.LeftColor, Colors.DodgerBlue);
        (_rightFill, _rightEdge) = MakeBrushes(s.RightColor, Colors.DarkOrange);
    }

    public void SetButton(bool isRight)
    {
        _fill.Fill = isRight ? _rightFill : _leftFill;
        _edge.Stroke = isRight ? _rightEdge : _leftEdge;
    }

    public void MoveTo(PointInt cursor, double monitorScale)
    {
        var size = Math.Max(8, (int)Math.Ceiling(_diameter * monitorScale));
        SetBounds(new RectInt(cursor.X - size / 2, cursor.Y - size / 2, size, size));
    }

    private static (Brush Fill, Brush Edge) MakeBrushes(string text, Color fallback)
    {
        var color = ArgbColor.TryParse(text, out var c) ? Color.FromArgb(c.A, c.R, c.G, c.B) : fallback;
        var fill = new SolidColorBrush(color) { Opacity = 0.35 };
        var edge = new SolidColorBrush(color) { Opacity = 0.9 };
        fill.Freeze();
        edge.Freeze();
        return (fill, edge);
    }
}
