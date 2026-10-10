namespace PresenterTools.Core.Geometry;

/// <summary>
/// 操作パネルの位置検証（SPEC §13）。切断したモニター上など、見えない位置に取り残さない。
/// </summary>
public static class WindowPlacement
{
    public const int MinVisibleWidth = 120;
    public const int MinVisibleHeight = 40;
    public const int TitleBarHeight = 32;
    public const int DefaultMargin = 24;

    /// <summary>
    /// ウィンドウが十分見えているか。いずれかのモニターの作業領域と
    /// 幅 120 × 高さ 40 以上重なり、かつタイトルバー（上端 32 px）の一部が作業領域内にあること。
    /// </summary>
    public static bool IsSufficientlyVisible(RectInt window, IReadOnlyList<MonitorArea> monitors)
    {
        var titleBar = window with { Height = Math.Min(TitleBarHeight, window.Height) };
        foreach (var m in monitors)
        {
            var overlap = window.Intersect(m.WorkArea);
            if (overlap.Width >= MinVisibleWidth && overlap.Height >= MinVisibleHeight && titleBar.IntersectsWith(m.WorkArea))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 見える位置を返す。十分見えていれば、最も重なるモニターの作業領域内に収める（大きければ縮める）。
    /// 見えていなければプライマリ（無ければ先頭）の作業領域の右下へ移す。
    /// </summary>
    public static RectInt EnsureVisible(RectInt window, IReadOnlyList<MonitorArea> monitors)
    {
        if (monitors.Count == 0) return window;

        if (IsSufficientlyVisible(window, monitors))
        {
            var host = monitors.OrderByDescending(m => window.Intersect(m.WorkArea).Area).First();
            return FitSize(window, host.WorkArea);
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        if (primary == default) primary = monitors[0];
        return DefaultPosition(new SizeInt(window.Width, window.Height), primary.WorkArea);
    }

    /// <summary>作業領域の右下（余白付き）。</summary>
    public static RectInt DefaultPosition(SizeInt size, RectInt workArea, int margin = DefaultMargin)
    {
        var w = Math.Min(size.Width, workArea.Width);
        var h = Math.Min(size.Height, workArea.Height);
        var left = Math.Max(workArea.Left, workArea.Right - w - margin);
        var top = Math.Max(workArea.Top, workArea.Bottom - h - margin);
        return new RectInt(left, top, w, h);
    }

    /// <summary>作業領域に収まるよう縮め、はみ出した分は内側へずらす（タスクバーの下にボタンが隠れないようにする）。</summary>
    private static RectInt FitSize(RectInt window, RectInt workArea)
    {
        var w = Math.Min(window.Width, workArea.Width);
        var h = Math.Min(window.Height, workArea.Height);
        return (window with { Width = w, Height = h }).ClampInto(workArea);
    }
}
