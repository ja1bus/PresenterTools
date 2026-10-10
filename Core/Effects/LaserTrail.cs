namespace PresenterTools.Core.Effects;

/// <summary>軌跡の 1 点（物理ピクセルの画面座標と、記録時刻［秒・単調増加］）。</summary>
public readonly record struct TrailPoint(double X, double Y, double Time, bool StartsStroke = false);

/// <summary>
/// レーザーの軌跡（SPEC §8.4）。点を時刻付きで記録し、残像時間を過ぎた点を捨てる。
/// 描画側は各点の経過時間から太さ・不透明度を決める。
/// </summary>
public sealed class LaserTrail
{
    private readonly List<TrailPoint> _points = [];
    private bool _startsStroke = true;
    public bool IsDrawing { get; private set; }
    /// <summary>現在の光点。残像の寿命とは独立して保持する。</summary>
    public TrailPoint? Head { get; private set; }

    public void BeginStroke(double x, double y, double time)
    {
        IsDrawing = true;
        _startsStroke = true;
        Add(x, y, time);
    }

    public void EndStroke()
    {
        IsDrawing = false;
        Head = null;
        _startsStroke = true;
    }

    public LaserTrail(int maxPoints = 1024, double minDistance = 1.5)
    {
        MaxPoints = Math.Max(2, maxPoints);
        MinDistance = Math.Max(0, minDistance);
    }

    public int MaxPoints { get; }
    public double MinDistance { get; }

    /// <summary>残像時間（秒）。</summary>
    public double LifetimeSeconds { get; set; } = 0.8;

    public IReadOnlyList<TrailPoint> Points => _points;
    public bool IsEmpty => _points.Count == 0;

    /// <summary>点を追加する。直前の点から <see cref="MinDistance"/> 未満の移動なら時刻だけ更新する。</summary>
    public void Add(double x, double y, double time)
    {
        if (IsDrawing) Head = new TrailPoint(x, y, time);
        if (!_startsStroke && _points.Count > 0)
        {
            var last = _points[^1];
            var dx = x - last.X;
            var dy = y - last.Y;
            if (dx * dx + dy * dy < MinDistance * MinDistance)
            {
                // 微小移動は点数を増やさず残像の時刻を更新する。
                _points[^1] = last with { Time = time };
                return;
            }
        }
        _points.Add(new TrailPoint(x, y, time, _startsStroke || _points.Count == 0));
        _startsStroke = false;
        if (_points.Count > MaxPoints) _points.RemoveRange(0, _points.Count - MaxPoints);
    }

    /// <summary>残像時間を過ぎた点を削除する。</summary>
    public void Prune(double now)
    {
        var cutoff = now - LifetimeSeconds;
        var remove = 0;
        while (remove < _points.Count && _points[remove].Time < cutoff) remove++;
        if (remove > 0) _points.RemoveRange(0, remove);
    }

    public void Clear()
    {
        _points.Clear();
        EndStroke();
    }

    /// <summary>点の経過割合（0 = 新しい、1 = 消える直前）。</summary>
    public double AgeRatio(in TrailPoint point, double now) =>
        LifetimeSeconds <= 0 ? 1 : Math.Clamp((now - point.Time) / LifetimeSeconds, 0, 1);
}
