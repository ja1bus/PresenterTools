using System.Windows;
using System.Windows.Media;
using PresenterTools.Core.Effects;
using PresenterTools.Core.Geometry;

namespace PresenterTools.Overlay;

/// <summary>
/// レーザーの軌跡の描画（SPEC §8.4）。軌跡は全モニター共通（物理ピクセルの画面座標）で、
/// このレイヤーは自分のモニターの座標系へ変換して描く。範囲外の部分は窓の外なので見えない。
/// ペンは不透明度・太さを段階に量子化してキャッシュする（SPEC §22）。
/// </summary>
public sealed class LaserLayer : FrameworkElement
{
    private const int Levels = 16;
    private readonly Pen[] _pens = new Pen[Levels];
    private Brush _glow = Brushes.Red;
    private Brush _core = Brushes.White;
    private double _width = 8;
    private LaserTrail? _trail;
    private RectInt _monitor;
    private double _scale = 1;
    private double _now;
    private bool _active;
    private bool _hadContent;

    public LaserLayer()
    {
        IsHitTestVisible = false;
        SetStyle(Colors.Red, 8);
    }

    public void SetStyle(Color color, double width)
    {
        _width = width;
        for (var i = 0; i < Levels; i++)
        {
            // i = 0 が最も新しい（太く・不透明）、Levels-1 が最も古い。
            var life = 1 - (double)i / Levels;
            var c = Color.FromArgb((byte)(color.A * life), color.R, color.G, color.B);
            var pen = new Pen(new SolidColorBrush(c), Math.Max(1, width * (0.25 + 0.75 * life)))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            _pens[i] = pen;
        }
        var glow = new SolidColorBrush(Color.FromArgb((byte)(color.A * 0.45), color.R, color.G, color.B));
        glow.Freeze();
        _glow = glow;
        var core = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        core.Freeze();
        _core = core;
    }

    /// <summary>フレームごとに呼ぶ。軌跡がこのモニターに掛かるとき（または前回描いたとき）だけ再描画する。</summary>
    public void Update(LaserTrail trail, RectInt monitorBounds, double scale, double now, bool active)
    {
        _trail = trail;
        _monitor = monitorBounds;
        _scale = scale <= 0 ? 1 : scale;
        _now = now;
        _active = active;

        var touches = Touches(trail, monitorBounds);
        if (touches || _hadContent)
        {
            _hadContent = touches;
            InvalidateVisual();
        }
    }

    private bool Touches(LaserTrail trail, RectInt bounds)
    {
        var margin = (int)(_width * _scale * 2) + 2;
        var area = bounds.Inflate(margin, margin);
        if (_active && trail.Head is { } head && area.Contains(new PointInt((int)head.X, (int)head.Y))) return true;
        foreach (var p in trail.Points)
        {
            if (area.Contains(new PointInt((int)p.X, (int)p.Y))) return true;
        }
        return false;
    }

    private Point ToLocal(in TrailPoint p) => new((p.X - _monitor.Left) / _scale, (p.Y - _monitor.Top) / _scale);

    protected override void OnRender(DrawingContext dc)
    {
        var trail = _trail;
        if (trail is null) return;
        var points = trail.Points;

        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].StartsStroke) continue;
            var age = trail.AgeRatio(points[i], _now);
            var level = Math.Clamp((int)(age * Levels), 0, Levels - 1);
            dc.DrawLine(_pens[level], ToLocal(points[i - 1]), ToLocal(points[i]));
        }

        if (_active && trail.Head is { } headPoint)
        {
            // 先端の光点
            var head = ToLocal(headPoint);
            dc.DrawEllipse(_glow, null, head, _width * 1.4, _width * 1.4);
            dc.DrawEllipse(_core, null, head, _width * 0.75, _width * 0.75);
        }
    }
}
