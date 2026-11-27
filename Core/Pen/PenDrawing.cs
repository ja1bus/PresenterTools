using PresenterTools.Core.Models;

namespace PresenterTools.Core.ScreenPen;

/// <summary>描線の 1 点（書き込み先モニターの左上を原点とする DIP）。</summary>
public readonly record struct PenPoint(double X, double Y);

/// <summary>
/// 1 画（docs/archive/design-1.7.0.md D5）。色・幅は画を始めた時点で確定する。
/// 画面の外へ出ると「切れ目」で部分（<see cref="Parts"/>）を分け、外側を結ばない。
/// </summary>
public sealed class PenStroke
{
    private readonly List<List<PenPoint>> _parts = [[]];

    public PenStroke(ArgbColor color, double width, PenTool kind = PenTool.Pen, double opacity = 1.0)
    {
        Color = color;
        Width = width;
        Kind = kind;
        Opacity = opacity;
    }

    public ArgbColor Color { get; }
    public double Width { get; }
    /// <summary>描いた道具（<see cref="PenTool.Pen"/> か <see cref="PenTool.Highlighter"/>。V1.8.0）。</summary>
    public PenTool Kind { get; }
    /// <summary>画全体に掛ける不透明度（蛍光マーカー。通常のペンは 1 で、色のアルファのまま描く）。</summary>
    public double Opacity { get; }

    /// <summary>確定時の外接矩形（点の座標。線幅は含まない）。確定前は空。</summary>
    public double MinX { get; private set; }
    public double MinY { get; private set; }
    public double MaxX { get; private set; }
    public double MaxY { get; private set; }
    public bool HasBounds { get; private set; }
    /// <summary>部分の一覧（描画で毎回複製しないよう内部の一覧をそのまま返す。最後の部分は空のことがある）。</summary>
    public IReadOnlyList<IReadOnlyList<PenPoint>> Parts => _parts;
    public int PointCount { get; private set; }

    /// <summary>押して離すまで 1 DIP 未満の移動なら点（幅の直径の円）として描く。</summary>
    public bool IsDot => PointCount == 1;

    internal List<PenPoint> LastPart => _parts[^1];

    internal void Add(PenPoint p)
    {
        _parts[^1].Add(p);
        PointCount++;
    }

    /// <summary>切れ目。次の点は新しい部分の始点になり、前の部分と結ばない。</summary>
    internal void Break()
    {
        if (_parts[^1].Count > 0) _parts.Add([]);
    }

    /// <summary>当たり判定で点を調べる単位（隣の塊と 1 点を共有し、その間の線分も含む）。</summary>
    private const int ChunkPoints = 32;
    private readonly record struct Chunk(int Part, int Start, int End, double MinX, double MinY, double MaxX, double MaxY);
    private List<Chunk>? _chunks;

    internal void ComputeBounds()
    {
        _chunks = [];
        for (var pi = 0; pi < _parts.Count; pi++)
        {
            var part = _parts[pi];
            for (var start = 0; start < part.Count; start += ChunkPoints)
            {
                // 線分 [i-1, i] を両方の塊で扱えるよう、前の塊の最後の点から始める。
                var from = Math.Max(0, start - 1);
                var to = Math.Min(part.Count, start + ChunkPoints);
                double cx0 = double.MaxValue, cy0 = double.MaxValue, cx1 = double.MinValue, cy1 = double.MinValue;
                for (var i = from; i < to; i++)
                {
                    var q = part[i];
                    if (q.X < cx0) cx0 = q.X;
                    if (q.Y < cy0) cy0 = q.Y;
                    if (q.X > cx1) cx1 = q.X;
                    if (q.Y > cy1) cy1 = q.Y;
                }
                _chunks.Add(new Chunk(pi, from, to, cx0, cy0, cx1, cy1));
            }
        }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var part in _parts)
            foreach (var p in part)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        HasBounds = PointCount > 0;
        (MinX, MinY, MaxX, MaxY) = HasBounds ? (minX, minY, maxX, maxY) : (0, 0, 0, 0);
    }

    /// <summary>点 p が画の線（幅の半分 + margin 以内）に当たるか（P7）。切れ目の間は線分にしない。</summary>
    public bool Hits(PenPoint p, double margin)
    {
        var r = Width / 2 + margin;
        if (_chunks is null) ComputeBounds();
        if (p.X < MinX - r || p.X > MaxX + r || p.Y < MinY - r || p.Y > MaxY + r) return false;
        var r2 = r * r;
        foreach (var c in _chunks!)
        {
            if (p.X < c.MinX - r || p.X > c.MaxX + r || p.Y < c.MinY - r || p.Y > c.MaxY + r) continue;
            var part = _parts[c.Part];
            if (part.Count == 1)
            {
                if (Dist2(p, part[0]) <= r2) return true;
                continue;
            }
            for (var i = Math.Max(1, c.Start + 1); i < c.End; i++)
                if (SegmentDist2(p, part[i - 1], part[i]) <= r2) return true;
        }
        return false;
    }

    private static double Dist2(PenPoint a, PenPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double SegmentDist2(PenPoint p, PenPoint a, PenPoint b)
    {
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var len2 = vx * vx + vy * vy;
        if (len2 <= 0) return Dist2(p, a);
        var t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
        t = Math.Clamp(t, 0, 1);
        var dx = p.X - (a.X + t * vx);
        var dy = p.Y - (a.Y + t * vy);
        return dx * dx + dy * dy;
    }
}

/// <summary>切り替えの間に退避した描線（<see cref="PenDrawing.TakeAll"/>。docs/archive/design-1.8.0.md P6 の手順 8）。</summary>
public sealed class PenDrawingContents
{
    internal PenDrawingContents(List<PenStroke> strokes, int totalPoints)
    {
        Strokes = strokes;
        TotalPoints = totalPoints;
    }

    internal List<PenStroke> Strokes { get; }
    public int TotalPoints { get; }
    public int StrokeCount => Strokes.Count;
}

/// <summary>
/// 描線のモデル（D5）。確定した画の一覧と描いている画を持つ。点の間引き・一画戻す・全消去・全体の点数の上限。
/// 経過時間・量で古い画を自動で消さない（AC-5）。
/// </summary>
public sealed class PenDrawing
{
    public const int DefaultMaxTotalPoints = 200_000;
    /// <summary>描画用の区間（StreamGeometry 1 つ）の上限。画の点数の上限ではない。</summary>
    public const int SectionPoints = 10_000;
    /// <summary>直前の点からこれ未満の移動なら点を足さない（DIP）。</summary>
    public const double MinDistance = 1.0;

    private readonly List<PenStroke> _strokes = [];
    private bool _breakPending;

    public PenDrawing(int maxTotalPoints = DefaultMaxTotalPoints) => MaxTotalPoints = Math.Max(1, maxTotalPoints);

    public int MaxTotalPoints { get; }
    public IReadOnlyList<PenStroke> Strokes => _strokes;
    public PenStroke? Current { get; private set; }
    public bool IsDrawing => Current is not null;
    public int TotalPoints { get; private set; }
    public bool IsAtLimit => TotalPoints >= MaxTotalPoints;
    public bool IsEmpty => _strokes.Count == 0 && Current is null;
    /// <summary>描いている画で受け取った移動の数（受理しなかったものを含む。試験用の記録 pen-limit）。</summary>
    public int MovesReceived { get; private set; }
    /// <summary>描いている画が上限に達したか（pen-limit を 1 回だけ記録するため）。</summary>
    public bool CurrentHitLimit { get; private set; }

    /// <summary>画を始める。上限に達していれば始めず false。</summary>
    public bool BeginStroke(PenPoint p, ArgbColor color, double width, PenTool kind = PenTool.Pen, double opacity = 1.0)
    {
        if (Current is not null) EndStroke();
        if (IsAtLimit) return false;
        Current = new PenStroke(color, width, kind, opacity);
        MovesReceived = 0;
        CurrentHitLimit = false;
        _breakPending = false;
        Current.Add(p);
        TotalPoints++;
        return true;
    }

    /// <summary>
    /// 移動を加える。<paramref name="inside"/> が false（書き込み先の外）なら点を捨てて切れ目にする。
    /// 受理したら true（描き直しが要る）。
    /// </summary>
    public bool AddPoint(PenPoint p, bool inside)
    {
        if (Current is null) return false;
        MovesReceived++;
        if (!inside)
        {
            _breakPending = true;
            return false;
        }
        if (_breakPending)
        {
            Current.Break();
            _breakPending = false;
        }
        else
        {
            var part = Current.LastPart;
            if (part.Count > 0)
            {
                var last = part[^1];
                var dx = p.X - last.X;
                var dy = p.Y - last.Y;
                if (dx * dx + dy * dy < MinDistance * MinDistance) return false;
            }
        }
        if (IsAtLimit)
        {
            CurrentHitLimit = true;
            return false;
        }
        Current.Add(p);
        TotalPoints++;
        return true;
    }

    /// <summary>描いている画をそこまでで確定する。確定した画を返す（無ければ null）。</summary>
    public PenStroke? EndStroke()
    {
        var s = Current;
        Current = null;
        _breakPending = false;
        if (s is null) return null;
        if (s.PointCount == 0) return null;
        s.ComputeBounds();
        _strokes.Add(s);
        return s;
    }

    /// <summary>
    /// 消しゴムの当たり判定（P7）。確定した画を最後から調べ、最初に当たった画の添字を返す（無ければ -1）。
    /// 描いている画は対象にしない。
    /// </summary>
    public int HitTest(PenPoint p, double margin = PenEraser.HitMargin)
    {
        for (var i = _strokes.Count - 1; i >= 0; i--)
            if (_strokes[i].Hits(p, margin)) return i;
        return -1;
    }

    /// <summary>一画を消す（P7）。一覧に無ければ false。点数を引き、上限に達していれば空きが戻る。</summary>
    public bool Remove(PenStroke stroke)
    {
        var i = _strokes.IndexOf(stroke);
        if (i < 0) return false;
        _strokes.RemoveAt(i);
        TotalPoints -= stroke.PointCount;
        return true;
    }

    /// <summary>確定した画を取り出して描線を空にする（切り替えの退避。描いている画は確定させる）。</summary>
    public PenDrawingContents TakeAll()
    {
        if (Current is not null) EndStroke();
        var taken = new PenDrawingContents([.. _strokes], TotalPoints);
        Clear();
        return taken;
    }

    /// <summary><see cref="TakeAll"/> で取り出した描線を戻す（今の描線は捨てる）。</summary>
    public void Restore(PenDrawingContents contents)
    {
        Clear();
        _strokes.AddRange(contents.Strokes);
        TotalPoints = contents.TotalPoints;
    }

    /// <summary>最後の画を消す。消した画があれば true。</summary>
    public bool Undo()
    {
        if (Current is not null) EndStroke();
        if (_strokes.Count == 0) return false;
        TotalPoints -= _strokes[^1].PointCount;
        _strokes.RemoveAt(_strokes.Count - 1);
        return true;
    }

    public void Clear()
    {
        _strokes.Clear();
        Current = null;
        _breakPending = false;
        TotalPoints = 0;
    }

    /// <summary>
    /// 1 つの部分を描画用の区間に分ける（D5 の「継ぎ目」）。区間が <paramref name="sectionPoints"/> に満ちたら、
    /// 最後の点を次の区間の始点として共有して続ける。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PenPoint>> Sections(IReadOnlyList<PenPoint> part, int sectionPoints = SectionPoints)
    {
        var result = new List<IReadOnlyList<PenPoint>>();
        if (part.Count == 0) return result;
        sectionPoints = Math.Max(2, sectionPoints);
        var start = 0;
        while (true)
        {
            var count = Math.Min(sectionPoints, part.Count - start);
            result.Add(part.Skip(start).Take(count).ToList());
            if (start + count >= part.Count) break;
            start += count - 1; // 最後の点を共有する
        }
        return result;
    }
}
