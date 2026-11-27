using System.Diagnostics;
using System.Text.Json.Nodes;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.ScreenPen;
using PresenterTools.Core.Settings;
using Xunit.Abstractions;

namespace PresenterTools.Tests;

/// <summary>V1.8.0 画面ペンの道具（docs/verification.md 20261127-pen-tools の T-01 (1)(2)）。</summary>
public sealed class PenToolStateTests
{
    [Fact]
    public void Starts_as_the_pen_with_the_start_width()
    {
        var t = new PenToolState(PenWidth.Thick);
        Assert.Equal((PenTool.Pen, PenWidth.Thick), (t.Tool, t.Width));
        Assert.True(t.IsCheckedWidth(PenWidth.Thick));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eraser_returns_to_the_previous_tool_when_the_pen_ends(bool highlighter)
    {
        var t = new PenToolState();
        t.SelectWidth(PenWidth.Thin);
        if (highlighter) t.SelectHighlighter();
        t.SelectEraser();
        t.SelectEraser(); // 2 回選んでも前の道具を失わない
        Assert.True(t.IsEraser);
        t.OnPenEnded();
        Assert.Equal(highlighter ? PenTool.Highlighter : PenTool.Pen, t.Tool);
        Assert.Equal(PenWidth.Thin, t.Width);
    }

    [Fact]
    public void Switching_without_ending_keeps_the_eraser_and_reset_returns_to_the_pen()
    {
        var t = new PenToolState();
        t.SelectHighlighter();
        t.SelectEraser();
        Assert.True(t.IsEraser); // 切り替えでは OnPenEnded を呼ばない
        t.Reset(PenWidth.Thin);
        Assert.Equal((PenTool.Pen, PenWidth.Thin), (t.Tool, t.Width));
        t.SelectEraser();
        t.OnPenEnded();
        Assert.Equal(PenTool.Pen, t.Tool);
    }

    [Fact]
    public void Exactly_one_menu_item_is_checked()
    {
        var t = new PenToolState();
        foreach (var select in new Action[] { () => t.SelectWidth(PenWidth.Thin), () => t.SelectWidth(PenWidth.Thick), t.SelectHighlighter, t.SelectEraser })
        {
            select();
            var checks = new[] { t.IsCheckedWidth(PenWidth.Thin), t.IsCheckedWidth(PenWidth.Medium), t.IsCheckedWidth(PenWidth.Thick), t.IsCheckedHighlighter, t.IsCheckedEraser };
            Assert.Equal(1, checks.Count(c => c));
        }
    }

    [Theory]
    [InlineData("#FFD400", 0xFF, 0xD4, 0x00, 0.4)]
    [InlineData("#80FFD400", 0xFF, 0xD4, 0x00, 0.4 * 128 / 255.0)]
    [InlineData("#12", 0xFF, 0xD4, 0x00, 0.4)]
    [InlineData(null, 0xFF, 0xD4, 0x00, 0.4)]
    public void Highlighter_color_is_opaque_and_alpha_goes_to_opacity(string? color, int r, int g, int b, double opacity)
    {
        var (c, o) = PenHighlighter.Resolve(color);
        Assert.Equal(new ArgbColor(255, (byte)r, (byte)g, (byte)b), c);
        Assert.Equal(opacity, o, 6);
        Assert.Equal(20, PenHighlighter.Width);
    }

    [Fact]
    public void Each_stroke_keeps_its_own_tool_color_width_and_opacity()
    {
        var d = new PenDrawing(maxTotalPoints: 3);
        var (hl, o) = PenHighlighter.Resolve("#FFD400");
        d.BeginStroke(new PenPoint(0, 0), ArgbColor.FromRgb(1, 2, 3), 4);
        d.EndStroke();
        d.BeginStroke(new PenPoint(0, 0), hl, PenHighlighter.Width, PenTool.Highlighter, o);
        d.AddPoint(new PenPoint(5, 0), true);
        d.EndStroke();
        Assert.Equal((PenTool.Pen, 4.0, 1.0), (d.Strokes[0].Kind, d.Strokes[0].Width, d.Strokes[0].Opacity));
        Assert.Equal((PenTool.Highlighter, 20.0, 0.4), (d.Strokes[1].Kind, d.Strokes[1].Width, d.Strokes[1].Opacity));
        Assert.True(d.IsAtLimit);
        Assert.False(d.BeginStroke(new PenPoint(0, 0), hl, PenHighlighter.Width, PenTool.Highlighter, o)); // 上限で始められない
    }
}

/// <summary>消しゴムの当たり判定と一画の削除・切り替えの退避（T-01 (3)(4)）。</summary>
public sealed class PenEraserTests
{
    private static readonly ArgbColor Red = ArgbColor.FromRgb(0xFF, 0, 0);

    private static PenStroke Line(PenDrawing d, double width, params (double X, double Y)[] points)
    {
        d.BeginStroke(new PenPoint(points[0].X, points[0].Y), Red, width);
        foreach (var p in points.Skip(1)) d.AddPoint(new PenPoint(p.X, p.Y), true);
        return d.EndStroke()!;
    }

    private static PenDrawing ThreeLines()
    {
        var d = new PenDrawing();
        Line(d, 2, (0, 0), (100, 0));
        Line(d, 4, (0, 50), (100, 50));
        Line(d, 8, (0, 100), (100, 100));
        return d;
    }

    [Fact]
    public void Picks_the_stroke_under_the_point_and_the_last_one_at_a_crossing()
    {
        var d = ThreeLines();
        Assert.Equal(1, d.HitTest(new PenPoint(50, 50)));
        Line(d, 2, (50, -20), (50, 120)); // 3 本と交差する
        Assert.Equal(3, d.HitTest(new PenPoint(50, 50)));
        Assert.Equal(-1, d.HitTest(new PenPoint(200, 200)));
        Assert.Equal(-1, new PenDrawing().HitTest(new PenPoint(0, 0)));
    }

    [Fact]
    public void Boundary_distance_is_half_width_plus_margin()
    {
        var d = new PenDrawing();
        Line(d, 2, (0, 0), (100, 0)); // 半幅 1 + 余裕 4 = 5
        Assert.Equal(0, d.HitTest(new PenPoint(50, 5)));
        Assert.Equal(-1, d.HitTest(new PenPoint(50, 5.01)));
        Assert.Equal(0, d.HitTest(new PenPoint(104, 3))); // 端の外側（線端の円の範囲）
        var hl = new PenDrawing();
        hl.BeginStroke(new PenPoint(0, 0), Red, PenHighlighter.Width, PenTool.Highlighter, 0.4);
        hl.AddPoint(new PenPoint(100, 0), true);
        hl.EndStroke();
        Assert.Equal(0, hl.HitTest(new PenPoint(50, 14)));
        Assert.Equal(-1, hl.HitTest(new PenPoint(50, 14.01)));
    }

    [Fact]
    public void Dots_and_single_point_parts_hit_but_the_gap_across_a_break_does_not()
    {
        var d = new PenDrawing();
        d.BeginStroke(new PenPoint(10, 10), Red, 2); // 点
        d.EndStroke();
        Assert.Equal(0, d.HitTest(new PenPoint(13, 12)));

        d.BeginStroke(new PenPoint(0, 100), Red, 2);
        d.AddPoint(new PenPoint(20, 100), true);
        d.AddPoint(new PenPoint(-5, 100), false); // 外へ出た
        d.AddPoint(new PenPoint(80, 100), true);  // 1 点だけの部分
        d.AddPoint(new PenPoint(-5, 100), false);
        var s = d.EndStroke()!;
        Assert.Equal(2, s.Parts.Count(p => p.Count > 0));
        Assert.Equal(1, d.HitTest(new PenPoint(80, 103)));
        Assert.Equal(-1, d.HitTest(new PenPoint(50, 100))); // 切れ目の両側を結ぶ所は描いていない
    }

    [Fact]
    public void Current_stroke_is_not_a_candidate()
    {
        var d = new PenDrawing();
        d.BeginStroke(new PenPoint(0, 0), Red, 4);
        d.AddPoint(new PenPoint(50, 0), true);
        Assert.Equal(-1, d.HitTest(new PenPoint(25, 0)));
    }

    [Fact]
    public void Remove_takes_only_that_stroke_and_undo_takes_the_last_remaining()
    {
        var d = ThreeLines();
        var total = d.TotalPoints;
        var second = d.Strokes[1];
        Assert.True(d.Remove(second));
        Assert.False(d.Remove(second));
        Assert.Equal(2, d.Strokes.Count);
        Assert.Equal(total - second.PointCount, d.TotalPoints);
        var third = d.Strokes[1];
        Assert.True(d.Undo());
        Assert.DoesNotContain(third, d.Strokes);
        Assert.True(d.Undo());
        Assert.False(d.Undo());
    }

    [Fact]
    public void Remove_frees_space_under_the_limit()
    {
        var d = new PenDrawing(maxTotalPoints: 4);
        Line(d, 2, (0, 0), (10, 0));
        Line(d, 2, (0, 10), (10, 10));
        Assert.True(d.IsAtLimit);
        d.Remove(d.Strokes[0]);
        Assert.False(d.IsAtLimit);
        Assert.True(d.BeginStroke(new PenPoint(0, 0), Red, 2));
    }

    [Fact]
    public void TakeAll_empties_and_Restore_brings_back_order_points_and_limit()
    {
        var d = new PenDrawing(maxTotalPoints: 6);
        Line(d, 2, (0, 0), (10, 0));
        Line(d, 4, (0, 10), (10, 10));
        Line(d, 8, (0, 20), (10, 20));
        var strokes = d.Strokes.ToList();
        var taken = d.TakeAll();
        Assert.True(d.IsEmpty);
        Assert.Equal(0, d.TotalPoints);
        Assert.Equal((3, 6), (taken.StrokeCount, taken.TotalPoints));
        d.Restore(taken);
        Assert.Equal(strokes, d.Strokes);
        Assert.Equal(6, d.TotalPoints);
        Assert.True(d.IsAtLimit);
    }
}

/// <summary>切り替えの判定・候補・表示名（T-01 (5)）。</summary>
public sealed class PenSwitchTests
{
    private const string D1 = @"\\.\DISPLAY1", D2 = @"\\.\DISPLAY2";
    private static readonly DisplayDescriptor A = PenTargetResolverTests.Key("ka", D1) with { IsPrimary = true };
    private static readonly DisplayDescriptor B = PenTargetResolverTests.Key("kb", D2, x: 1920, scale: 1.5);
    private static DisplaySnapshot Snap(params DisplayDescriptor[] d) => PenTargetResolverTests.Snap(d);
    private static PenOwner OwnerOf(DisplayDescriptor d) => PenOwner.From(d, 1);

    [Fact]
    public void FindCurrent_uses_the_new_description_and_reports_each_failure()
    {
        var moved = B with { Bounds = new RectInt(-1920, 0, 2560, 1440) };
        var r = PenTargetResolver.FindCurrent(B, Snap(A, moved));
        Assert.Equal(PenTargetStatus.Resolved, r.Status);
        Assert.Equal(moved.Bounds, r.Display!.Bounds);
        Assert.Equal(PenTargetStatus.Ambiguous, PenTargetResolver.FindCurrent(B, Snap(A, B, B)).Status);
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.FindCurrent(B, Snap(A)).Status);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.FindCurrent(B, Snap(A, PenTargetResolverTests.Failed(D2, 1920))).Status);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.FindCurrent(B, PenTargetResolverTests.SnapQueryFailed(A)).Status);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.FindCurrent(B, new DisplaySnapshot(false, false, [A, B])).Status);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.FindCurrent(B, DisplaySnapshot.Incomplete).Status);
    }

    [Fact]
    public void FindCurrent_for_gdi_counts_only_gdi_displays_with_the_name()
    {
        var g = PenTargetResolverTests.Gdi(D2, x: 1920);
        var keyed = PenTargetResolverTests.Key("kx", D2, x: 1920);
        Assert.Equal(PenTargetStatus.Resolved, PenTargetResolver.FindCurrent(g, Snap(A, g)).Status);
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.FindCurrent(g, Snap(A, keyed)).Status);
    }

    [Fact]
    public void Candidates_skip_failed_displays_and_are_empty_when_incomplete()
    {
        var f = PenTargetResolverTests.Failed(@"\\.\DISPLAY3", 4000);
        Assert.Equal([A, B], PenTargetResolver.SwitchCandidates(Snap(A, f, B)));
        Assert.Empty(PenTargetResolver.SwitchCandidates(new DisplaySnapshot(false, false, [A, B])));
        Assert.Empty(PenTargetResolver.SwitchCandidates(DisplaySnapshot.Incomplete));
    }

    [Fact]
    public void Decide_same_when_nothing_changed()
    {
        var d = PenTargetResolver.DecideSwitch(OwnerOf(A), A, Snap(A, B));
        Assert.Equal(PenSwitchKind.Same, d.Kind);
    }

    [Fact]
    public void Decide_source_problems_come_before_same_and_go()
    {
        var owner = OwnerOf(A);
        var movedA = A with { Bounds = new RectInt(0, 0, 2560, 1440) };
        Assert.Equal(PenSwitchKind.SourceDiscard, PenTargetResolver.DecideSwitch(owner, A, Snap(movedA, B)).Kind); // 同じキーで配置が変わった
        Assert.Equal(PenSwitchKind.SourceDiscard, PenTargetResolver.DecideSwitch(owner, A, Snap(A, A, B)).Kind);   // 今の画面のキーが重複
        var incomplete = new DisplaySnapshot(false, false, [A, B]);
        Assert.Equal(PenSwitchKind.SourcePending, PenTargetResolver.DecideSwitch(owner, B, incomplete).Kind);      // 選んだ画面は有効でも
        Assert.Equal(PenSwitchKind.SourceDiscard, PenTargetResolver.DecideSwitch(owner, B, Snap(B)).Kind);         // 移動元だけが消えた
    }

    [Fact]
    public void Decide_failed_when_the_chosen_display_is_gone_or_duplicated()
    {
        var owner = OwnerOf(A);
        var gone = PenTargetResolver.DecideSwitch(owner, B, Snap(A));
        Assert.Equal((PenSwitchKind.Failed, PenTargetStatus.NotConnected), (gone.Kind, gone.Status));
        var dup = PenTargetResolver.DecideSwitch(owner, B, Snap(A, B, B));
        Assert.Equal((PenSwitchKind.Failed, PenTargetStatus.Ambiguous), (dup.Kind, dup.Status));
    }

    [Fact]
    public void Decide_go_carries_the_new_descriptions_of_target_and_source()
    {
        var movedB = B with { Bounds = new RectInt(1920, 0, 3840, 2160), Scale = 2.0 };
        var d = PenTargetResolver.DecideSwitch(OwnerOf(A), B, Snap(A, movedB));
        Assert.Equal(PenSwitchKind.Go, d.Kind);
        Assert.Equal(movedB, d.Target);
        Assert.Equal(A, d.Source);
    }

    [Fact]
    public void Gdi_owner_choosing_the_keyed_display_with_its_name_is_same()
    {
        var gdiOwner = OwnerOf(PenTargetResolverTests.Gdi(D1));
        var keyedA = A with { Bounds = new RectInt(0, 0, 1920, 1080) };
        Assert.Equal(PenSwitchKind.Same, PenTargetResolver.DecideSwitch(gdiOwner, keyedA, Snap(keyedA, B)).Kind);
    }

    [Fact]
    public void Menu_label_has_number_name_and_marks_only()
    {
        Assert.Equal("1: Mon" + Strings.PenPrimaryMark, PenDisplayLabel.MenuLabel(A));
        var noName = PenTargetResolverTests.Key("kc", @"\\.\DISPLAY3", friendly: "");
        Assert.Equal("3: " + Strings.PenDisplayName, PenDisplayLabel.MenuLabel(noName));
        var dup = B with { IsDuplicate = true };
        Assert.Equal("2: Mon" + Strings.PenDuplicateMark, PenDisplayLabel.MenuLabel(dup));
        var longName = new string('L', 80);
        Assert.Equal("4: " + longName, PenDisplayLabel.MenuLabel(PenTargetResolverTests.Key("kd", @"\\.\DISPLAY4", friendly: longName)));
        // 同じ名前の 2 台は番号で区別される
        Assert.NotEqual(PenDisplayLabel.MenuLabel(A with { IsPrimary = false }), PenDisplayLabel.MenuLabel(B));
    }

    // ---- 次の開始の組み合わせ（設計レビュー D3。AppHost.TryStartPen の呼び出し順を PenSession で再現） ----

    private static PenSession OwnedBy(DisplayDescriptor d)
    {
        var s = new PenSession();
        s.Start(d, out _);
        s.SurfaceClosed(hasLines: true); // 線を残して終えた
        return s;
    }

    [Fact]
    public void Next_start_on_another_display_discards_and_on_the_same_display_keeps()
    {
        var s = OwnedBy(B);
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(A, B)));
        Assert.False(s.Start(A, out _)); // 設定の書き込み先 A: 線を捨てる側

        var t = OwnedBy(B);
        Assert.Equal(PenDisplayOutcome.Keep, t.OnDisplays(Snap(A, B)));
        var auto = PenTargetResolver.Resolve(new PenSettings(), Snap(A with { IsPrimary = false }, B with { IsPrimary = true }));
        Assert.Equal(B, auto.Display! with { IsPrimary = false });
        Assert.True(t.Start(auto.Display!, out _)); // 自動の書き込み先が B: 線を保つ
    }

    [Fact]
    public void Next_start_that_cannot_resolve_leaves_the_owner_and_pending_waits()
    {
        var s = OwnedBy(B);
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(B)));
        var saved = new PenSettings { TargetDevicePath = "ka", TargetDeviceName = D1 };
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.Resolve(saved, Snap(B)).Status);
        Assert.NotNull(s.Owner); // 開始しないので B の線は残る

        var p = OwnedBy(B);
        Assert.Equal(PenDisplayOutcome.Pending, p.OnDisplays(new DisplaySnapshot(false, false, [A, B])));
        Assert.True(p.ShouldRetry(p.Owner!.Generation));

        var d = OwnedBy(B);
        Assert.Equal(PenDisplayOutcome.Discard, d.OnDisplays(Snap(A)));
        Assert.Null(d.Owner);
    }

    // ---- 開始の失敗の巻き戻し（コードレビュー R4） ----

    [Fact]
    public void Failed_start_on_another_display_restores_the_previous_owner()
    {
        var s = OwnedBy(B);
        var before = s.Owner!;
        Assert.False(s.Start(A, out var started)); // A で開始しようとした（B の線を退避する側）
        Assert.NotEqual(before.Generation, started.Generation);

        s.RevertStart(before, previousPending: false); // A の入力面を出せなかった
        Assert.Same(before, s.Owner);
        Assert.False(s.SurfaceShown);
        Assert.False(s.IsPending);
        Assert.False(s.ShouldRetry(started.Generation)); // 失敗した開始の世代では再試行しない
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(A, B))); // B の所有者として判定が続く

        var n = new PenSession();
        n.Start(A, out _);
        n.RevertStart(null, previousPending: false); // 所有者の無い状態からの失敗（切り替えの移動先・戻し）
        Assert.Null(n.Owner);
        Assert.False(n.ReleaseIfIdle(hasLines: false)); // 解放済み（二重に解放しない）
    }
}

/// <summary>.cur のバイト列（T-01 (6)）。</summary>
public sealed class CursorFileTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void Layout_matches_the_cur_format(int size)
    {
        var px = new byte[size * size * 4];
        for (var i = 0; i < size * 4; i++) px[i] = 0xAB; // 上の行
        var hot = size / 2;
        var b = CursorFile.Build(size, size, hot, hot - 1, px);
        Assert.Equal((0, 2, 1), (BitConverter.ToUInt16(b, 0), BitConverter.ToUInt16(b, 2), BitConverter.ToUInt16(b, 4)));
        Assert.Equal(size == 256 ? 0 : size, b[6]);
        Assert.Equal(size == 256 ? 0 : size, b[7]);
        Assert.Equal((hot, hot - 1), (BitConverter.ToUInt16(b, 10), BitConverter.ToUInt16(b, 12)));
        var dataSize = BitConverter.ToInt32(b, 14);
        var offset = BitConverter.ToInt32(b, 18);
        Assert.Equal(22, offset);
        Assert.Equal(b.Length - offset, dataSize);
        Assert.Equal(40, BitConverter.ToInt32(b, offset));
        Assert.Equal(size, BitConverter.ToInt32(b, offset + 4));
        Assert.Equal(size * 2, BitConverter.ToInt32(b, offset + 8));
        Assert.Equal(32, BitConverter.ToUInt16(b, offset + 14));
        var mask = CursorFile.MaskStride(size);
        Assert.Equal(0, mask % 4);
        Assert.Equal(40 + size * size * 4 + mask * size, dataSize);
        // 上の行は下から上の並びで最後の行になる
        var lastRow = offset + 40 + (size - 1) * size * 4;
        Assert.Equal(0xAB, b[lastRow]);
        Assert.Equal(0, b[offset + 40]);
    }

    [Theory]
    [InlineData(0, 32, 0, 0)]
    [InlineData(257, 32, 0, 0)]
    [InlineData(32, 32, 32, 0)]
    [InlineData(32, 32, 0, -1)]
    public void Out_of_range_throws(int w, int h, int hx, int hy)
    {
        var px = new byte[Math.Max(0, w * h * 4)];
        Assert.ThrowsAny<ArgumentException>(() => CursorFile.Build(w, h, hx, hy, px));
    }
}

/// <summary>蛍光マーカーの色の設定（T-01 (7)）。</summary>
public sealed class PenHighlighterSettingsTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SettingsLoadResult LoadWith(Action<JsonObject> change)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new SettingsStore(path).Save(new AppSettings());
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            change(root);
            File.WriteAllText(path, root.ToJsonString());
            return new SettingsStore(path).Load();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Version_1_7_0_file_gets_yellow_without_corrections()
    {
        var r = LoadWith(root => root["pen"]!.AsObject().Remove("highlighterColor"));
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Empty(r.Corrections);
        Assert.Equal("#FFD400", r.Settings.Pen.HighlighterColor);
    }

    [Fact]
    public void Invalid_or_null_falls_back_with_a_correction_and_alpha_is_kept()
    {
        foreach (JsonNode? bad in new JsonNode?[] { JsonValue.Create("#12"), null })
        {
            var r = LoadWith(root => root["pen"]!["highlighterColor"] = bad);
            Assert.Equal("#FFD400", r.Settings.Pen.HighlighterColor);
            Assert.Contains("pen.highlighterColor", r.Corrections);
        }
        var a = LoadWith(root => root["pen"]!["highlighterColor"] = "#80FFD400");
        Assert.Equal("#80FFD400", a.Settings.Pen.HighlighterColor);
        Assert.Empty(a.Corrections);
    }

    [Fact]
    public void Pen_and_highlighter_colors_are_saved_separately()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var s = new AppSettings();
            s.Pen.Color = "#2F80FF";
            s.Pen.HighlighterColor = "#00C853";
            new SettingsStore(path).Save(s);
            var loaded = new SettingsStore(path).Load().Settings.Pen;
            Assert.Equal(("#2F80FF", "#00C853"), (loaded.Color, loaded.HighlighterColor));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Tab_reset_revert_and_reset_all_handle_the_highlighter_color()
    {
        var baseline = new AppSettings();
        baseline.Pen.HighlighterColor = "#E040FB";
        baseline.Pen.TargetDevicePath = "1:A";
        var current = new AppSettings();
        current.Pen.TargetDevicePath = "1:C";

        var reset = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        reset.ResetSection(SettingsSection.Pen);
        var r = reset.Build(current, false, KeyDisplayMode.Off).Settings.Pen;
        Assert.Equal(("#FFD400", "1:C"), (r.HighlighterColor, r.TargetDevicePath)); // 書き込み先は残る

        var revert = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        revert.Settings.Pen.HighlighterColor = "#000000";
        revert.RevertSection(SettingsSection.Pen);
        Assert.Equal("#E040FB", revert.Build(current, false, KeyDisplayMode.Off).Settings.Pen.HighlighterColor);

        var all = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        all.ResetAll();
        Assert.Equal("#FFD400", all.Build(current, false, KeyDisplayMode.Off).Settings.Pen.HighlighterColor);

        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.Settings.Pen.HighlighterColor = "#123456";
        Assert.True(edit.HasChanges(current, false, KeyDisplayMode.Off));
    }

    [Fact]
    public void Export_contains_and_import_reads_the_highlighter_color_without_hotkeys()
    {
        var source = new AppSettings();
        source.Pen.HighlighterColor = "#E040FB";
        source.Hotkeys.TogglePen = "Ctrl+Alt+F8";
        var json = SettingsTransfer.Serialize(source, "1.8.0", DateTimeOffset.Now);
        Assert.Equal("#E040FB", (string?)JsonNode.Parse(json)!["settings"]!["pen"]!["highlighterColor"]);
        var imported = SettingsTransfer.Parse(json).Settings!;
        var current = new AppSettings();
        current.Hotkeys.TogglePen = "Ctrl+Alt+F9";
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.Import(imported, includeHotkeys: false, includeMouseButtons: false);
        var built = edit.Build(current, false, KeyDisplayMode.Off).Settings;
        Assert.Equal(("#E040FB", "Ctrl+Alt+F9"), (built.Pen.HighlighterColor, built.Hotkeys.TogglePen));
    }

    [Fact]
    public void Validator_reports_the_highlighter_color_on_the_pen_tab()
    {
        var s = new AppSettings();
        s.Pen.HighlighterColor = "yellow";
        Assert.Contains(SettingsValidator.Validate(s), p => p is { Section: SettingsSection.Pen, Field: "pen.highlighterColor" });
    }
}

/// <summary>当たり判定の速さ（T-01 (6b)・T-10 (1)）。通常の実行では緩い上限だけを確かめ、T-10 で Release の実測値を記録する。</summary>
[Trait("Category", "Perf")]
public sealed class PenHitTestPerfTests(ITestOutputHelper output)
{
    [Fact]
    public void Worst_case_with_200000_points()
    {
        // 画面の縁に沿った枠 1,000 画 × 200 点（中央はどの画にも当たらず、どの外接矩形にも入る）。
        var d = new PenDrawing();
        var red = ArgbColor.FromRgb(255, 0, 0);
        const double W = 1706, H = 960;
        for (var s = 0; s < 1000; s++)
        {
            var inset = 10 + s * 0.05;
            var corners = new[] { (inset, inset), (W - inset, inset), (W - inset, H - inset), (inset, H - inset), (inset, inset) };
            var pts = new List<PenPoint>();
            for (var i = 0; i < 200; i++)
            {
                var t = i / 199.0 * 4;
                var seg = Math.Min(3, (int)t);
                var f = t - seg;
                var (x0, y0) = corners[seg];
                var (x1, y1) = corners[seg + 1];
                pts.Add(new PenPoint(x0 + (x1 - x0) * f, y0 + (y1 - y0) * f));
            }
            d.BeginStroke(pts[0], red, 2);
            foreach (var p in pts.Skip(1)) d.AddPoint(p, true);
            d.EndStroke();
        }
        Assert.Equal(200_000, d.TotalPoints);
        var center = new PenPoint(W / 2, H / 2);
        Assert.Equal(-1, d.HitTest(center));
        var times = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var sw = Stopwatch.StartNew();
            d.HitTest(center);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var median = times[50];
        output.WriteLine($"HitTest median {median:F3} ms (max {times[^1]:F3} ms)");
        Assert.True(median < 50, $"median {median} ms");
    }
}
