namespace PresenterTools.Core.Geometry;

public readonly record struct PointInt(int X, int Y);

public readonly record struct SizeInt(int Width, int Height);

/// <summary>整数の矩形（物理ピクセル）。Right / Bottom は含まない（Left + Width）。</summary>
public readonly record struct RectInt(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static RectInt FromLTRB(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public bool Contains(PointInt p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public bool Contains(RectInt r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

    public RectInt Intersect(RectInt other)
    {
        var l = Math.Max(Left, other.Left);
        var t = Math.Max(Top, other.Top);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : default;
    }

    public bool IntersectsWith(RectInt other) => !Intersect(other).IsEmpty;

    public RectInt Inflate(int dx, int dy) => new(Left - dx, Top - dy, Width + dx * 2, Height + dy * 2);

    public RectInt Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    /// <summary>
    /// <paramref name="bounds"/> の内側に収まるよう移動する（大きすぎる場合は左上をそろえる）。サイズは変えない。
    /// </summary>
    public RectInt ClampInto(RectInt bounds)
    {
        var left = Width >= bounds.Width ? bounds.Left : Math.Clamp(Left, bounds.Left, bounds.Right - Width);
        var top = Height >= bounds.Height ? bounds.Top : Math.Clamp(Top, bounds.Top, bounds.Bottom - Height);
        return this with { Left = left, Top = top };
    }
}

/// <summary>モニター 1 台分の領域（物理ピクセル）。</summary>
public readonly record struct MonitorArea(RectInt Bounds, RectInt WorkArea, bool IsPrimary);
