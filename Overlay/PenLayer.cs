using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.ScreenPen;
using PresenterTools.Services;

namespace PresenterTools.Overlay;

/// <summary>
/// 画面ペンの描線の描画（docs/archive/design-1.7.0.md D5）。書き込み先の <see cref="OverlayWindow"/> に重ねる。
/// 確定した画は確定・一画戻す・全消去のときだけ描き直す。描いている画は満ちた区間を凍結して保持し、
/// 移動ごとに描き直すのは最後の区間（最大 <see cref="PenDrawing.SectionPoints"/> 点）だけにする。
/// 移動の通知は 1 回の描画にまとめ、静止中は描画処理をしない（CompositionTarget.Rendering は使わない）。
/// </summary>
public sealed class PenLayer : FrameworkElement
{
    private readonly VisualCollection _children;
    private readonly DrawingVisual _committed = new();
    private readonly DrawingVisual _currentFrozen = new();
    private readonly DrawingVisual _currentTail = new();
    private readonly PenDrawing _drawing;
    private bool _renderPending;
    private long _generation;
    // 描いている画の凍結済みの範囲（部分の数と、最後の部分の末尾区間の開始位置）。
    private PenStroke? _frozenStroke;
    private int _frozenParts = -1;
    private int _frozenTailStart = -1;

    public PenLayer(PenDrawing drawing)
    {
        _drawing = drawing;
        // 子の一覧を先に作る（IsHitTestVisible の変更が子をたどるため）。
        _children = new VisualCollection(this) { _committed, _currentFrozen, _currentTail };
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>保留中の描画予約を無効にする（窓の付け替え・破棄の前。D11）。</summary>
    public void InvalidatePending() => _generation++;

    /// <summary>確定した画を描き直す（確定・一画戻す・全消去・付け直し）。</summary>
    public void RenderCommitted()
    {
        using (var dc = _committed.RenderOpen())
        {
            foreach (var stroke in _drawing.Strokes) DrawStroke(dc, stroke, 0, int.MaxValue);
        }
        _frozenStroke = null;
        _frozenParts = _frozenTailStart = -1;
        RenderCurrentNow();
        TestTrace.Write("pen-render", "committed");
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
            _frozenStroke = null;
            _frozenParts = _frozenTailStart = -1;
            return;
        }

        var parts = stroke.Parts;
        var lastIndex = parts.Count - 1;
        while (lastIndex > 0 && parts[lastIndex].Count == 0) lastIndex--;
        var last = parts[lastIndex];
        var tailStart = TailStart(last.Count);

        if (!ReferenceEquals(stroke, _frozenStroke) || _frozenParts != lastIndex || _frozenTailStart != tailStart)
        {
            // 満ちた区間が増えたときだけ凍結部分を描き直す（区間 1 つ分ごとに 1 回）。
            using (var dc = _currentFrozen.RenderOpen())
                DrawStroke(dc, stroke, 0, lastIndex, lastPartEnd: tailStart);
            _frozenStroke = stroke;
            _frozenParts = lastIndex;
            _frozenTailStart = tailStart;
        }
        using (var dc = _currentTail.RenderOpen())
        {
            var pen = MakePen(stroke);
            DrawPoints(dc, pen, stroke, last, tailStart, last.Count, isSingleDot: stroke.IsDot);
        }
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
    private static void DrawStroke(DrawingContext dc, PenStroke stroke, int firstPart, int endPart, int lastPartEnd = 0)
    {
        var pen = MakePen(stroke);
        var parts = stroke.Parts;
        var end = Math.Min(endPart, parts.Count);
        for (var i = firstPart; i < end; i++)
            DrawPoints(dc, pen, stroke, parts[i], 0, parts[i].Count, stroke.IsDot);
        if (endPart < parts.Count && lastPartEnd > 0)
            DrawPoints(dc, pen, stroke, parts[endPart], 0, lastPartEnd + 1, false);
    }

    private static void DrawPoints(DrawingContext dc, Pen pen, PenStroke stroke, IReadOnlyList<PenPoint> part, int start, int end, bool isSingleDot)
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
                dc.DrawEllipse(pen.Brush, null, new Point(p.X, p.Y), stroke.Width / 2, stroke.Width / 2);
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

    private static Pen MakePen(PenStroke stroke)
    {
        var c = stroke.Color;
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
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
