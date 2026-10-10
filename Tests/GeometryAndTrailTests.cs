using PresenterTools.Core.Effects;
using PresenterTools.Core.Geometry;

namespace PresenterTools.Tests;

public sealed class GeometryAndTrailTests
{
    private static readonly MonitorArea Primary = new(new RectInt(0, 0, 1920, 1080), new RectInt(0, 0, 1920, 1040), true);
    private static readonly MonitorArea Left = new(new RectInt(-2560, -200, 2560, 1440), new RectInt(-2560, -200, 2560, 1392), false);

    [Fact]
    public void Placement_VisibleWindowIsKept()
    {
        var w = new RectInt(100, 100, 300, 470);
        Assert.Equal(w, WindowPlacement.EnsureVisible(w, [Primary, Left]));

        var onSecond = new RectInt(-1000, 300, 300, 470);
        Assert.Equal(onSecond, WindowPlacement.EnsureVisible(onSecond, [Primary, Left]));
    }

    [Fact]
    public void Placement_WindowOnDisconnectedMonitorMovesToPrimary()
    {
        var onSecond = new RectInt(-1000, 300, 300, 470);
        var result = WindowPlacement.EnsureVisible(onSecond, [Primary]);
        Assert.Equal(new RectInt(1920 - 300 - 24, 1040 - 470 - 24, 300, 470), result);
        Assert.True(Primary.WorkArea.Contains(result));
    }

    [Fact]
    public void Placement_MostlyOffscreenMovesBack()
    {
        // 右端に 50px だけ見えている → 幅 120 未満なので戻す
        var w = new RectInt(1870, 100, 300, 470);
        Assert.True(Primary.WorkArea.Contains(WindowPlacement.EnsureVisible(w, [Primary])));

        // タイトルバーが作業領域の上に隠れている
        var above = new RectInt(100, -300, 300, 470);
        Assert.False(WindowPlacement.IsSufficientlyVisible(above, [Primary]));
    }

    [Fact]
    public void Placement_PartlyUnderTaskbarIsMovedInside()
    {
        // 下端が作業領域（タスクバーの上）を 90px はみ出している → 内側へずらす
        var w = new RectInt(1700, 610, 300, 520);
        var r = WindowPlacement.EnsureVisible(w, [Primary]);
        Assert.Equal(new RectInt(1620, 1040 - 520, 300, 520), r);
    }

    [Fact]
    public void Placement_TooLargeWindowIsShrunk()
    {
        var w = new RectInt(0, 0, 3000, 2000);
        var r = WindowPlacement.EnsureVisible(w, [Primary]);
        Assert.Equal(1920, r.Width);
        Assert.Equal(1040, r.Height);
    }

    [Fact]
    public void Placement_NoMonitorsReturnsInput()
    {
        var w = new RectInt(5, 5, 10, 10);
        Assert.Equal(w, WindowPlacement.EnsureVisible(w, []));
    }

    [Fact]
    public void Magnifier_SourceRectIsCenteredOnCursor()
    {
        var src = MagnifierLayout.ComputeSourceRect(new PointInt(960, 540), new SizeInt(480, 300), 3, Primary.Bounds);
        Assert.Equal(new RectInt(960 - 80, 540 - 50, 160, 100), src);
    }

    [Fact]
    public void Magnifier_SourceRectClampedAtMonitorEdge()
    {
        var src = MagnifierLayout.ComputeSourceRect(new PointInt(5, 1075), new SizeInt(480, 300), 2, Primary.Bounds);
        Assert.Equal(new RectInt(0, 1080 - 150, 240, 150), src);
        Assert.True(Primary.Bounds.Contains(src));
    }

    [Fact]
    public void Magnifier_WindowPrefersBottomRightAndAvoidsSource()
    {
        var size = new SizeInt(480, 300);
        var src = MagnifierLayout.ComputeSourceRect(new PointInt(400, 300), size, 3, Primary.Bounds);
        var win = MagnifierLayout.ComputeWindowRect(size, src, Primary.WorkArea, null, out var q);
        Assert.Equal(MagnifierQuadrant.BottomRight, q);
        Assert.False(win.IntersectsWith(src));
        Assert.True(Primary.WorkArea.Contains(win));
    }

    [Theory]
    [InlineData(1900, 1030, MagnifierQuadrant.TopLeft)]
    [InlineData(1900, 100, MagnifierQuadrant.BottomLeft)]
    [InlineData(100, 1030, MagnifierQuadrant.TopRight)]
    public void Magnifier_WindowFlipsNearEdges(int x, int y, MagnifierQuadrant expected)
    {
        var size = new SizeInt(480, 300);
        var src = MagnifierLayout.ComputeSourceRect(new PointInt(x, y), size, 3, Primary.Bounds);
        var win = MagnifierLayout.ComputeWindowRect(size, src, Primary.WorkArea, null, out var q);
        Assert.Equal(expected, q);
        Assert.False(win.IntersectsWith(src));
        Assert.True(Primary.WorkArea.Contains(win));
    }

    [Fact]
    public void Magnifier_KeepsPreviousQuadrantWhenValid()
    {
        var size = new SizeInt(480, 300);
        var src = MagnifierLayout.ComputeSourceRect(new PointInt(960, 540), size, 3, Primary.Bounds);
        MagnifierLayout.ComputeWindowRect(size, src, Primary.WorkArea, MagnifierQuadrant.TopLeft, out var q);
        Assert.Equal(MagnifierQuadrant.TopLeft, q);
    }

    [Fact]
    public void Magnifier_HugeWindowStaysInsideWorkArea()
    {
        var size = new SizeInt(1800, 1000);
        var src = MagnifierLayout.ComputeSourceRect(new PointInt(960, 540), size, 2, Primary.Bounds);
        var win = MagnifierLayout.ComputeWindowRect(size, src, Primary.WorkArea, null, out _);
        Assert.True(Primary.WorkArea.Contains(win));
    }

    [Fact]
    public void RectInt_Operations()
    {
        var a = new RectInt(0, 0, 100, 100);
        var b = new RectInt(50, 50, 100, 100);
        Assert.Equal(new RectInt(50, 50, 50, 50), a.Intersect(b));
        Assert.True(a.Intersect(new RectInt(100, 0, 10, 10)).IsEmpty);
        Assert.Equal(new RectInt(-5, -5, 110, 110), a.Inflate(5, 5));
        Assert.Equal(new RectInt(0, 100, 100, 100), new RectInt(-20, 500, 100, 100).ClampInto(new RectInt(0, 0, 1000, 200)));
    }

    [Fact]
    public void Trail_PrunesOldPoints()
    {
        var t = new LaserTrail { LifetimeSeconds = 0.8 };
        t.Add(0, 0, 0.0);
        t.Add(10, 0, 0.5);
        t.Add(20, 0, 1.0);
        t.Prune(1.0);
        Assert.Equal(2, t.Points.Count);
        Assert.Equal(10, t.Points[0].X);
        t.Prune(2.0);
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Trail_SkipsTinyMovesButRefreshesTime()
    {
        var t = new LaserTrail(minDistance: 1.5);
        t.Add(0, 0, 0.0);
        t.Add(1, 0, 0.7);
        Assert.Single(t.Points);
        Assert.Equal(0.7, t.Points[0].Time);
    }

    [Fact]
    public void Trail_CapsPointCount()
    {
        var t = new LaserTrail(maxPoints: 10);
        for (var i = 0; i < 25; i++) t.Add(i * 10, 0, i * 0.01);
        Assert.Equal(10, t.Points.Count);
        Assert.Equal(150, t.Points[0].X);
    }

    [Fact]
    public void Trail_AgeRatio()
    {
        var t = new LaserTrail { LifetimeSeconds = 1.0 };
        var p = new TrailPoint(0, 0, 10);
        Assert.Equal(0, t.AgeRatio(p, 10));
        Assert.Equal(0.5, t.AgeRatio(p, 10.5), 6);
        Assert.Equal(1, t.AgeRatio(p, 20));
    }
}
