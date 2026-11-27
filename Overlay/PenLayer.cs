using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.ScreenPen;
using PresenterTools.Services;

namespace PresenterTools.Overlay;

/// <summary>
/// 画面ペンの描線の描画（docs/archive/design-1.7.0.md D5、docs/archive/design-1.8.0.md P3・P7）。書き込み先の <see cref="OverlayWindow"/> に重ねる。
/// 確定した画は画ごとに 1 回だけ凍結した <see cref="Drawing"/> にして保持し、確定・一画戻す・全消去・消しゴム・付け直しのときは
/// それを並べ直すだけにする。描いている画は満ちた区間を凍結して保持し、移動ごとに描き直すのは最後の区間（最大
/// <see cref="PenDrawing.SectionPoints"/> 点）だけにする。移動の通知は 1 回の描画にまとめ、静止中は描画処理をしない。
/// 蛍光マーカーは画ごとに不透明の色で描いてから 1 回だけ不透明度を掛ける（1 つの画の中の重なりで濃くならない）。
/// </summary>
public sealed class PenLayer : FrameworkElement
{
    private static readonly Color OutlineColor = Color.FromRgb(0x00, 0x78, 0xD4);
    private const double OutlineOpacity = 0.35;

    private readonly VisualCollection _children;
    private readonly DrawingVisual _committed = new();
    private readonly ContainerVisual _currentGroup = new();
    private readonly DrawingVisual _currentFrozen = new();
    private readonly DrawingVisual _currentTail = new();
    private readonly PenDrawing _drawing;
    private readonly Dictionary<PenStroke, Drawing> _cache = new(ReferenceEqualityComparer.Instance);
    private bool _renderPending;
    private long _generation;
    // 描いている画の凍結済みの範囲（部分の数と、最後の部分の末尾区間の開始位置）。
    private PenStroke? _frozenStroke;
    private int _frozenParts = -1;
    private int _frozenTailStart = -1;
    // 消しゴムの候補と、その縁取り（候補が変わるまで使う）。
    private PenStroke? _hover;
    private Drawing? _hoverOutline;

    public PenLayer(PenDrawing drawing)
    {
        _drawing = drawing;
        _currentGroup.Children.Add(_currentFrozen);
        _currentGroup.Children.Add(_currentTail);
        // 子の一覧を先に作る（IsHitTestVisible の変更が子をたどるため）。
        _children = new VisualCollection(this) { _committed, _currentGroup };
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>消しゴムの候補（null で強調なし）。</summary>
    public PenStroke? Hover => _hover;

    /// <summary>保留中の描画予約を無効にする（窓の付け替え・破棄の前。D11）。</summary>
    public void InvalidatePending() => _generation++;

    /// <summary>消しゴムの候補を変える。変わったら確定した画を並べ直す（幾何は作り直さない）。</summary>
    public void SetHover(PenStroke? stroke)
    {
        if (ReferenceEquals(stroke, _hover)) return;
        _hover = stroke;
        _hoverOutline = stroke is null ? null : BuildOutline(stroke);
        RenderCommittedOnly();
    }

    /// <summary>確定した画を描き直す（確定・一画戻す・全消去・付け直し）。描いている画も描き直す。</summary>
    public void RenderCommitted()
    {
        RenderCommittedOnly();
        _frozenStroke = null;
        _frozenParts = _frozenTailStart = -1;
        RenderCurrentNow();
        TestTrace.Write("pen-render", "committed");
    }

    private void RenderCommittedOnly()
    {
        var strokes = _drawing.Strokes;
        // 一覧に無くなった画の保持を外す。
        if (_cache.Count > strokes.Count)
        {
            var alive = new HashSet<PenStroke>(strokes, ReferenceEqualityComparer.Instance);
            foreach (var gone in _cache.Keys.Where(k => !alive.Contains(k)).ToList()) _cache.Remove(gone);
        }
        if (_hover is not null && !strokes.Contains(_hover))
        {
            _hover = null;
            _hoverOutline = null;
        }
        using var dc = _committed.RenderOpen();
        foreach (var stroke in strokes)
        {
            // 強調は候補の画の直前に差し込む（古い太い線に隠れない。設計レビュー D6）。
            if (_hoverOutline is not null && ReferenceEquals(stroke, _hover)) dc.DrawDrawing(_hoverOutline);
            dc.DrawDrawing(CachedDrawing(stroke));
        }
    }

    private Drawing CachedDrawing(PenStroke stroke)
    {
        if (_cache.TryGetValue(stroke, out var d)) return d;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var highlighter = stroke.Kind == PenTool.Highlighter;
            if (highlighter) dc.PushOpacity(stroke.Opacity);
            DrawStroke(dc, MakePen(stroke), stroke, 0, int.MaxValue);
            if (highlighter) dc.Pop();
        }
        group.Freeze();
        _cache[stroke] = group;
        return group;
    }

    private static Drawing BuildOutline(PenStroke stroke)
    {
        var brush = new SolidColorBrush(OutlineColor);
        brush.Freeze();
        var pen = new Pen(brush, stroke.Width + 2 * PenEraser.OutlineMargin)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // 縁取り全体に 1 回だけ不透明度を掛ける（縁取りの自分との重なりで濃くならない）。
            dc.PushOpacity(OutlineOpacity);
            DrawStroke(dc, pen, stroke, 0, int.MaxValue);
            dc.Pop();
        }
        group.Freeze();
        return group;
    }

    /// <summary>描いている画の描き直しを予約する（移動ごと。予約が残っていれば追加しない）。</summary>
    public void ScheduleCurrent()
    {
        if (_renderPending) return;
        _renderPending = true;
        var generation = _generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _renderPending = false;
            if (generation != _generation) return;
            RenderCurrentNow();
            TestTrace.Write("pen-render", "current");
        });
    }

    private void RenderCurrentNow()
    {
        var stroke = _drawing.Current;
        if (stroke is null)
        {
            // 凍結の記録は RenderCommitted が先に空にするため、記録の有無で消去を飛ばさない（R3）。
            using (_currentFrozen.RenderOpen()) { }
            using (_currentTail.RenderOpen()) { }
            _currentGroup.Opacity = 1; // 透明度を次の通常のペンへ残さない
            _frozenStroke = null;
            _frozenParts = _frozenTailStart = -1;
            return;
        }

        // 凍結部分と末尾の区間を 1 つのグループにまとめて不透明度を掛ける（継ぎ目の重なりで濃くならない）。
        _currentGroup.Opacity = stroke.Kind == PenTool.Highlighter ? stroke.Opacity : 1;
        var pen = MakePen(stroke);
        var parts = stroke.Parts;
        var lastIndex = parts.Count - 1;
        while (lastIndex > 0 && parts[lastIndex].Count == 0) lastIndex--;
        var last = parts[lastIndex];
        var tailStart = TailStart(last.Count);

        if (!ReferenceEquals(stroke, _frozenStroke) || _frozenParts != lastIndex || _frozenTailStart != tailStart)
        {
            // 満ちた区間が増えたときだけ凍結部分を描き直す（区間 1 つ分ごとに 1 回）。
            using (var dc = _currentFrozen.RenderOpen())
                DrawStroke(dc, pen, stroke, 0, lastIndex, lastPartEnd: tailStart);
            _frozenStroke = stroke;
            _frozenParts = lastIndex;
            _frozenTailStart = tailStart;
        }
        using (var dc = _currentTail.RenderOpen())
            DrawPoints(dc, pen, last, tailStart, last.Count, isSingleDot: stroke.IsDot);
    }

    /// <summary>最後の部分の末尾の区間の開始位置（区間は最後の点を次の区間と共有する）。</summary>
    private static int TailStart(int count)
    {
        var s = PenDrawing.SectionPoints;
        if (count <= s) return 0;
        return (count - 2) / (s - 1) * (s - 1);
    }

    /// <summary>
    /// 画の部分 [firstPart, endPart) と、endPart 番目の部分の先頭から lastPartEnd 点（共有する 1 点を含む）までを描く。
    /// </summary>
    private static void DrawStroke(DrawingContext dc, Pen pen, PenStroke stroke, int firstPart, int endPart, int lastPartEnd = 0)
    {
        var parts = stroke.Parts;
        var end = Math.Min(endPart, parts.Count);
        for (var i = firstPart; i < end; i++)
            DrawPoints(dc, pen, parts[i], 0, parts[i].Count, stroke.IsDot);
        if (endPart < parts.Count && lastPartEnd > 0)
            DrawPoints(dc, pen, parts[endPart], 0, lastPartEnd + 1, false);
    }

    private static void DrawPoints(DrawingContext dc, Pen pen, IReadOnlyList<PenPoint> part, int start, int end, bool isSingleDot)
    {
        end = Math.Min(end, part.Count);
        var count = end - start;
        if (count <= 0) return;
        if (count == 1)
        {
            // 押して離しただけの点、または 1 点だけの部分は円で描く。
            if (isSingleDot || start == 0)
            {
                var p = part[start];
                dc.DrawEllipse(pen.Brush, null, new Point(p.X, p.Y), pen.Thickness / 2, pen.Thickness / 2);
            }
            return;
        }
        foreach (var section in PenDrawing.Sections(Slice(part, start, end)))
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(section[0].X, section[0].Y), isFilled: false, isClosed: false);
                var points = new List<Point>(section.Count - 1);
                for (var i = 1; i < section.Count; i++) points.Add(new Point(section[i].X, section[i].Y));
                ctx.PolyLineTo(points, isStroked: true, isSmoothJoin: true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static IReadOnlyList<PenPoint> Slice(IReadOnlyList<PenPoint> part, int start, int end)
    {
        if (start == 0 && end == part.Count) return part;
        var list = new List<PenPoint>(end - start);
        for (var i = start; i < end; i++) list.Add(part[i]);
        return list;
    }

    /// <summary>
    /// 画のペン。通常のペンは色のアルファのまま（1.7.0 と同じ）。蛍光マーカーは不透明の RGB で描き、
    /// 不透明度は呼び出し側で画全体に 1 回だけ掛ける（アルファを二重に掛けない）。
    /// </summary>
    private static Pen MakePen(PenStroke stroke)
    {
        var c = stroke.Color;
        var a = stroke.Kind == PenTool.Highlighter ? (byte)255 : c.A;
        var brush = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        brush.Freeze();
        var pen = new Pen(brush, stroke.Width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
