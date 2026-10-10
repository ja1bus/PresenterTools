namespace PresenterTools.Core.Geometry;

/// <summary>拡大窓をカーソルのどちら側に置くか。</summary>
public enum MagnifierQuadrant
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>
/// Magnifier の取得範囲と拡大窓の配置の計算（SPEC §16）。座標はすべて物理ピクセル。
/// </summary>
public static class MagnifierLayout
{
    public const int DefaultGap = 24;

    private static readonly MagnifierQuadrant[] Order =
        [MagnifierQuadrant.BottomRight, MagnifierQuadrant.BottomLeft, MagnifierQuadrant.TopRight, MagnifierQuadrant.TopLeft];

    /// <summary>
    /// 取得範囲。拡大窓の物理サイズ ÷ 拡大率を、カーソル中心・モニター内に収めて返す。
    /// </summary>
    public static RectInt ComputeSourceRect(PointInt cursor, SizeInt windowSize, double zoom, RectInt monitorBounds)
    {
        zoom = double.IsFinite(zoom) && zoom >= 1 ? zoom : 1;
        var w = Math.Max(1, (int)Math.Round(windowSize.Width / zoom));
        var h = Math.Max(1, (int)Math.Round(windowSize.Height / zoom));
        var rect = new RectInt(cursor.X - w / 2, cursor.Y - h / 2, w, h);
        return rect.ClampInto(monitorBounds);
    }

    /// <summary>
    /// 拡大窓の位置。取得範囲（＋余白）と重ならず作業領域に収まる候補を、
    /// 直前の候補 → 右下 → 左下 → 右上 → 左上 の順で探す。無ければ重なりが最小の候補。
    /// </summary>
    public static RectInt ComputeWindowRect(
        SizeInt windowSize, RectInt source, RectInt workArea, MagnifierQuadrant? previous,
        out MagnifierQuadrant chosen, int gap = DefaultGap)
    {
        var keepOut = source.Inflate(gap, gap);
        var candidates = previous is { } p ? Order.Where(q => q != p).Prepend(p) : Order;

        RectInt? best = null;
        var bestOverlap = long.MaxValue;
        chosen = MagnifierQuadrant.BottomRight;

        foreach (var q in candidates)
        {
            var rect = Place(q, windowSize, keepOut);
            var clamped = rect.ClampInto(workArea);
            var overlap = clamped.Intersect(keepOut).Area;
            if (overlap == 0 && clamped == rect)
            {
                chosen = q;
                return clamped;
            }
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                best = clamped;
                chosen = q;
            }
        }
        return best!.Value;
    }

    private static RectInt Place(MagnifierQuadrant q, SizeInt size, RectInt keepOut) => q switch
    {
        MagnifierQuadrant.BottomRight => new RectInt(keepOut.Right, keepOut.Bottom, size.Width, size.Height),
        MagnifierQuadrant.BottomLeft => new RectInt(keepOut.Left - size.Width, keepOut.Bottom, size.Width, size.Height),
        MagnifierQuadrant.TopRight => new RectInt(keepOut.Right, keepOut.Top - size.Height, size.Width, size.Height),
        _ => new RectInt(keepOut.Left - size.Width, keepOut.Top - size.Height, size.Width, size.Height),
    };
}
