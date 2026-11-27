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

    public PenStroke(ArgbColor color, double width)
    {
        Color = color;
        Width = width;
    }

    public ArgbColor Color { get; }
    public double Width { get; }
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
    public bool BeginStroke(PenPoint p, ArgbColor color, double width)
    {
        if (Current is not null) EndStroke();
        if (IsAtLimit) return false;
        Current = new PenStroke(color, width);
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
        _strokes.Add(s);
        return s;
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
