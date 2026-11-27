using PresenterTools.Core.Geometry;
using PresenterTools.Core.Models;
using PresenterTools.Core.ScreenPen;

namespace PresenterTools.Tests;

/// <summary>V1.7.0 画面ペン: 描線のモデル（docs/verification.md T-01 (1)〜(3)）。</summary>
public sealed class PenDrawingTests
{
    private static readonly ArgbColor Red = ArgbColor.FromRgb(0xFF, 0x20, 0x20);
    private static readonly ArgbColor Blue = ArgbColor.FromRgb(0x20, 0x20, 0xFF);

    [Fact]
    public void Moves_under_one_dip_are_not_added()
    {
        var d = new PenDrawing();
        d.BeginStroke(new PenPoint(0, 0), Red, 4);
        Assert.False(d.AddPoint(new PenPoint(0.5, 0.5), true));
        Assert.True(d.AddPoint(new PenPoint(1, 0), true));
        var s = d.EndStroke()!;
        Assert.Equal(2, s.PointCount);
        Assert.False(s.IsDot);
    }

    [Fact]
    public void Press_and_release_without_moving_is_a_dot()
    {
        var d = new PenDrawing();
        d.BeginStroke(new PenPoint(10, 10), Red, 8);
        d.AddPoint(new PenPoint(10.3, 10), true);
        var s = d.EndStroke()!;
        Assert.True(s.IsDot);
        Assert.Equal(1, d.TotalPoints);
    }

    [Fact]
    public void Leaving_the_display_breaks_the_stroke_without_joining()
    {
        var d = new PenDrawing();
        d.BeginStroke(new PenPoint(0, 0), Red, 4);
        d.AddPoint(new PenPoint(5, 0), true);
        Assert.False(d.AddPoint(new PenPoint(-3, 0), false)); // 外の点は捨てる
        Assert.False(d.AddPoint(new PenPoint(-9, 0), false));
        Assert.True(d.AddPoint(new PenPoint(5.2, 0), true));   // 戻った点は 1 DIP 未満でも新しい部分の始点
        d.AddPoint(new PenPoint(9, 0), true);
        var s = d.EndStroke()!;
        Assert.Equal(2, s.Parts.Count);
        Assert.Equal(new[] { new PenPoint(0, 0), new PenPoint(5, 0) }, s.Parts[0]);
        Assert.Equal(new PenPoint(5.2, 0), s.Parts[1][0]); // 前の部分の終点を共有しない
        Assert.Equal(4, s.PointCount);
    }

    [Fact]
    public void Render_sections_share_the_seam_point()
    {
        var part = Enumerable.Range(0, 25_000).Select(i => new PenPoint(i, 0)).ToList();
        var sections = PenDrawing.Sections(part);
        Assert.Equal(3, sections.Count);
        Assert.All(sections, s => Assert.True(s.Count <= PenDrawing.SectionPoints));
        Assert.Equal(sections[0][^1], sections[1][0]); // 継ぎ目は直前の最後の点を始点として共有する
        Assert.Equal(sections[1][^1], sections[2][0]);
        Assert.Equal(part[^1], sections[2][^1]);
        Assert.Equal(part.Count, sections.Sum(s => s.Count) - (sections.Count - 1));
    }

    [Fact]
    public void Total_limit_stops_accepting_points_and_undo_frees_space()
    {
        var d = new PenDrawing();
        d.BeginStroke(new PenPoint(0, 0), Red, 4);
        for (var i = 1; i < 199_990; i++) d.AddPoint(new PenPoint(i * 2, 0), true);
        d.EndStroke();
        Assert.Equal(199_990, d.TotalPoints);

        d.BeginStroke(new PenPoint(0, 10), Blue, 8);
        var accepted = 0;
        for (var i = 1; i <= 20; i++) if (d.AddPoint(new PenPoint(i * 2, 10), true)) accepted++;
        Assert.Equal(9, accepted);
        Assert.Equal(200_000, d.TotalPoints);
        Assert.Equal(20, d.MovesReceived);   // 受け取った移動数と受理した点数を別に数える
        Assert.True(d.CurrentHitLimit);
        var last = d.EndStroke()!;
        Assert.Equal(10, last.PointCount);
        Assert.True(d.IsAtLimit);
        Assert.False(d.BeginStroke(new PenPoint(0, 20), Red, 4)); // 上限中は新しい画を始めない

        Assert.True(d.Undo());
        Assert.Equal(199_990, d.TotalPoints);
        Assert.False(d.IsAtLimit);
        Assert.True(d.BeginStroke(new PenPoint(0, 20), Red, 4)); // 空きが戻った後の押下で描ける
    }

    [Fact]
    public void Undo_clear_and_per_stroke_style()
    {
        var d = new PenDrawing();
        Assert.False(d.Undo()); // 空のときの一画戻す
        d.BeginStroke(new PenPoint(0, 0), Red, 8);
        d.AddPoint(new PenPoint(10, 0), true);
        d.EndStroke();
        d.BeginStroke(new PenPoint(0, 5), Blue, 2);
        d.AddPoint(new PenPoint(10, 5), true);
        d.EndStroke();
        Assert.Equal((Red, 8.0), (d.Strokes[0].Color, d.Strokes[0].Width));
        Assert.Equal((Blue, 2.0), (d.Strokes[1].Color, d.Strokes[1].Width));
        Assert.True(d.Undo());
        Assert.Single(d.Strokes);
        Assert.Equal(Red, d.Strokes[0].Color);
        d.Clear();
        Assert.True(d.IsEmpty);
        Assert.Equal(0, d.TotalPoints);
    }
}

/// <summary>画面の識別の組み立て（T-01 (4a)）。</summary>
public sealed class DisplaySnapshotBuilderTests
{
    internal static RawMonitor M(string name, int x = 0, int y = 0, int w = 1920, int h = 1080, bool primary = false, double? scale = 1.0, bool ok = true) =>
        new(name, new RectInt(x, y, w, h), primary, scale, ok);

    internal static RawPath P(string source, string? path, string friendly = "Mon", bool queried = true, bool isInternal = false) =>
        new(source, path, friendly, queried, isInternal);

    [Fact]
    public void Built_in_output_marks_the_display_including_a_duplicate()
    {
        var s = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1"), M(@"\\.\DISPLAY2", x: 1920, primary: true)], true,
            [P(@"\\.\DISPLAY1", A, isInternal: true), P(@"\\.\DISPLAY2", B)]);
        Assert.True(s.Displays[0].IsInternal);
        Assert.False(s.Displays[1].IsInternal);

        // 複製表示（内蔵と外部に同じ画面）は内蔵を含む
        var dup = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true,
            [P(@"\\.\DISPLAY1", B), P(@"\\.\DISPLAY1", A, isInternal: true)]);
        Assert.True(dup.Displays[0].IsInternal);

        // 照会全体の失敗では内蔵を判定できない
        Assert.False(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, null).Displays[0].IsInternal);
    }

    private const string A = @"\\?\DISPLAY#DEL40F6#5&1a&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string B = @"\\?\DISPLAY#GSM5B09#5&2b&0&UID2#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void Key_does_not_depend_on_order_or_case()
    {
        var monitors = new[] { M(@"\\.\DISPLAY1", primary: true), M(@"\\.\DISPLAY2", x: 1920) };
        var s1 = DisplaySnapshotBuilder.Build(monitors, true, [P(@"\\.\DISPLAY1", A), P(@"\\.\DISPLAY2", B)]);
        var s2 = DisplaySnapshotBuilder.Build(monitors, true, [P(@"\\.\DISPLAY2", B), P(@"\\.\DISPLAY1", A.ToLowerInvariant())]);
        Assert.Equal(s1.Displays.Select(d => d.DeviceKey), s2.Displays.Select(d => d.DeviceKey));
        Assert.All(s1.Displays, d => Assert.Equal(DisplayIdentity.Key, d.Identity));
        Assert.True(s1.ListComplete);

        // 複製: 出力先 2 つの順序を入れ替えても同じキー。1 つの論理画面。
        var dup1 = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", A), P(@"\\.\DISPLAY1", B)]);
        var dup2 = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", B), P(@"\\.\DISPLAY1", A)]);
        var only = Assert.Single(dup1.Displays);
        Assert.True(only.IsDuplicate);
        Assert.Equal(only.DeviceKey, Assert.Single(dup2.Displays).DeviceKey);
        Assert.Equal(only.FriendlyName, Assert.Single(dup2.Displays).FriendlyName);
    }

    [Fact]
    public void Key_format_is_length_prefixed_and_reversible()
    {
        var key = DeviceKey.Create(["b|x", "A:1"]);
        Assert.Equal("3:A:13:B|X", key);
        Assert.True(DeviceKey.TryParse(key, out var paths));
        Assert.Equal(new[] { "A:1", "B|X" }, paths);
        // 区切り文字を含むパスでも別の組と衝突しない。
        Assert.NotEqual(DeviceKey.Create(["A", "B"]), DeviceKey.Create(["A1:B"]));
        Assert.NotEqual(DeviceKey.Create(["A|B"]), DeviceKey.Create(["A", "B"]));
        Assert.False(DeviceKey.TryParse("9:short", out _));
    }

    [Fact]
    public void TryParse_rejects_huge_or_overlong_lengths_without_throwing()
    {
        // R8: 長さの加算がオーバーフローしても例外にしない。
        Assert.False(DeviceKey.TryParse("2147483647:X", out _));
        Assert.False(DeviceKey.TryParse("99999999999:X", out _)); // int を超える
        Assert.False(DeviceKey.TryParse("2:X", out _));           // 長さ過大
        Assert.True(DeviceKey.TryParse("1:X", out var one));      // 末尾ちょうど
        Assert.Equal(new[] { "X" }, one);
        Assert.True(DeviceKey.TryParse("1:X2:YZ", out var two));  // 複数要素
        Assert.Equal(new[] { "X", "YZ" }, two);
        Assert.False(DeviceKey.TryParse("1:X2147483647:Y", out _));
    }

    [Fact]
    public void Duplicate_with_one_empty_path_is_failed_not_a_partial_key()
    {
        // R5: 全経路が有効 → Key、一部だけ空 → Failed、全経路が空 → Gdi。
        var full = Assert.Single(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", A), P(@"\\.\DISPLAY1", B)]).Displays);
        Assert.Equal(DisplayIdentity.Key, full.Identity);
        var partial = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", A), P(@"\\.\DISPLAY1", null)]);
        var d = Assert.Single(partial.Displays);
        Assert.Equal(DisplayIdentity.Failed, d.Identity);
        Assert.Equal("", d.DeviceKey);
        Assert.True(d.IsDuplicate);
        Assert.True(partial.HasUnidentified);
        var empty = Assert.Single(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", null), P(@"\\.\DISPLAY1", "")]).Displays);
        Assert.Equal(DisplayIdentity.Gdi, empty.Identity);
        // 単独の A と同じキーにならない。
        var single = Assert.Single(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", A)]).Displays);
        Assert.NotEqual(single.DeviceKey, d.DeviceKey);
    }

    [Fact]
    public void Partial_target_failure_makes_the_display_failed()
    {
        var s = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true,
            [P(@"\\.\DISPLAY1", A), P(@"\\.\DISPLAY1", null, queried: false)]);
        var d = Assert.Single(s.Displays);
        Assert.Equal(DisplayIdentity.Failed, d.Identity);
        Assert.Equal("", d.DeviceKey); // 片方だけのキーを作らない
        Assert.True(s.HasUnidentified);
    }

    [Fact]
    public void Query_failure_and_missing_paths_fall_back_to_gdi()
    {
        var failed = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1"), M(@"\\.\DISPLAY2", x: 1920)], true, null);
        Assert.All(failed.Displays, d => Assert.Equal(DisplayIdentity.Gdi, d.Identity));
        Assert.True(failed.QueryFailed);
        Assert.True(failed.ListComplete);

        var noPath = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1"), M(@"\\.\DISPLAY9", x: 1920)], true, [P(@"\\.\DISPLAY1", A)]);
        Assert.Equal(DisplayIdentity.Key, noPath.Displays[0].Identity);
        Assert.Equal(DisplayIdentity.Gdi, noPath.Displays[1].Identity); // 経路の無い GDI 名
    }

    [Fact]
    public void Unknown_source_name_makes_unmatched_displays_failed()
    {
        var s = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1"), M(@"\\.\DISPLAY2", x: 1920)], true,
            [P(@"\\.\DISPLAY1", A), new RawPath(null, B, "X")]);
        Assert.Equal(DisplayIdentity.Failed, s.Displays[1].Identity);
    }

    [Fact]
    public void Monitor_info_or_dpi_failure_and_empty_list_are_incomplete()
    {
        Assert.False(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1"), M("", ok: false)], true, [P(@"\\.\DISPLAY1", A)]).ListComplete);
        Assert.False(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1", scale: null)], true, [P(@"\\.\DISPLAY1", A)]).ListComplete);
        Assert.False(DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], false, [P(@"\\.\DISPLAY1", A)]).ListComplete);
        var empty = DisplaySnapshotBuilder.Build([], true, []);
        Assert.False(empty.ListComplete);
        Assert.True(DisplaySnapshot.Incomplete.Placeholder);
    }

    [Fact]
    public void Too_long_key_is_failed_and_8192_is_accepted()
    {
        // "<len>:" を含めてちょうど 8,192 文字になるパス。
        var exact = @"\\?\" + new string('X', DeviceKey.MaxLength - 5 - 4);
        Assert.Equal(DeviceKey.MaxLength, DeviceKey.Create([exact]).Length);
        var ok = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", exact)]);
        Assert.Equal(DisplayIdentity.Key, Assert.Single(ok.Displays).Identity);

        var tooLong = DisplaySnapshotBuilder.Build([M(@"\\.\DISPLAY1")], true, [P(@"\\.\DISPLAY1", exact + "Y")]);
        Assert.Equal(DisplayIdentity.Failed, Assert.Single(tooLong.Displays).Identity);
    }

    [Fact]
    public void Display_label_has_number_name_marks_and_size()
    {
        Assert.Equal("2", PenDisplayLabel.Number(@"\\.\DISPLAY2"));
        Assert.Equal("12", PenDisplayLabel.Number(@"\\.\DISPLAY12"));
        var d = new DisplayDescriptor(DisplayIdentity.Key, "k", @"\\.\DISPLAY1", "DELL U2720Q", new RectInt(0, 0, 2560, 1440), true, 1.5);
        var label = PenDisplayLabel.Format(d);
        Assert.StartsWith("1: DELL U2720Q", label);
        Assert.EndsWith("2560×1440 150%", label);
    }
}

/// <summary>書き込み先の解決と所有者との比較（T-01 (4)(5)）。</summary>
public sealed class PenTargetResolverTests
{
    internal static DisplayDescriptor Key(string key, string name, int x = 0, int y = 0, int w = 1920, int h = 1080, double scale = 1.0, string friendly = "Mon") =>
        new(DisplayIdentity.Key, key, name, friendly, new RectInt(x, y, w, h), false, scale);

    internal static DisplayDescriptor Gdi(string name, int x = 0, int y = 0, int w = 1920, int h = 1080, double scale = 1.0) =>
        new(DisplayIdentity.Gdi, "", name, "", new RectInt(x, y, w, h), false, scale);

    internal static DisplayDescriptor Failed(string name, int x = 0) =>
        new(DisplayIdentity.Failed, "", name, "", new RectInt(x, 0, 1920, 1080), false, 1.0);

    internal static DisplaySnapshot Snap(params DisplayDescriptor[] displays) =>
        new(true, false, displays);

    internal static DisplaySnapshot SnapQueryFailed(params DisplayDescriptor[] displays) =>
        new(true, true, displays);

    private static PenSettings Saved(string path = "", string name = "", string label = "", bool reselect = false) =>
        new() { TargetDevicePath = path, TargetDeviceName = name, TargetLabel = label, TargetReselect = reselect };

    private const string D1 = @"\\.\DISPLAY1", D2 = @"\\.\DISPLAY2";

    [Fact]
    public void Resolve_follows_every_row_of_the_d3_table()
    {
        var a = Key("ka", D1);
        var b = Key("kb", D2, x: 1920);
        // 不完全
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(Saved("ka"), new DisplaySnapshot(false, false, [a])).Status);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(Saved(), DisplaySnapshot.Incomplete).Status);
        // 選び直し
        Assert.Equal(PenTargetStatus.Reselect, PenTargetResolver.Resolve(Saved(label: "X", reselect: true), Snap(a)).Status);
        // キーが 1 台に一致
        Assert.Same(b, PenTargetResolver.Resolve(Saved("kb"), Snap(a, b)).Display);
        // 2 台に一致
        Assert.Equal(PenTargetStatus.Ambiguous, PenTargetResolver.Resolve(Saved("ka"), Snap(a, Key("ka", D2, x: 1920))).Status);
        // 一致なしで照会の失敗・Failed あり
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(Saved("kz"), SnapQueryFailed(Gdi(D1))).Status);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(Saved("kz"), Snap(a, Failed(D2, 1920))).Status);
        // パスが空で GDI 名が一致し、今も Gdi
        var g1 = Gdi(D1);
        Assert.Same(g1, PenTargetResolver.Resolve(Saved(name: D1), Snap(g1, Gdi(D2, x: 1920))).Display);
        // パスが空で GDI 名は一致するが今は Key
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.Resolve(Saved(name: D1), Snap(a, b)).Status);
        // どれにも当たらない
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.Resolve(Saved("kz", D1), Snap(a, b)).Status);
        // 保存なし・1 台（Key・Gdi）
        Assert.Same(a, PenTargetResolver.Resolve(Saved(), Snap(a)).Display);
        Assert.Same(g1, PenTargetResolver.Resolve(Saved(), Snap(g1)).Display);
        // 保存なし・1 台で Failed
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(Saved(), Snap(Failed(D1))).Status);
        // 保存なし・2 台で内蔵もメインもない（通常は起きない）
        Assert.Equal(PenTargetStatus.NotSelected, PenTargetResolver.Resolve(Saved(), Snap(a, b)).Status);
    }

    [Fact]
    public void Unselected_with_several_displays_uses_the_built_in_then_the_main_display()
    {
        var main = Key("ka", D1) with { IsPrimary = true };
        var builtIn = Key("kb", D2, x: 1920) with { IsInternal = true };
        var other = Key("kc", @"\\.\DISPLAY3", x: 3840);

        // 内蔵ディスプレイがメインでなくても内蔵を選ぶ
        Assert.Same(builtIn, PenTargetResolver.Resolve(Saved(), Snap(main, builtIn, other)).Display);
        // 内蔵がなければメイン
        Assert.Same(main, PenTargetResolver.Resolve(Saved(), Snap(other, main)).Display);
        // 内蔵が 2 台（判定できない）ならメイン
        var builtIn2 = other with { IsInternal = true };
        Assert.Same(main, PenTargetResolver.Resolve(Saved(), Snap(main, builtIn, builtIn2)).Display);
        // RDP など照会全体の失敗（Gdi）ではメイン
        var gMain = Gdi(D1) with { IsPrimary = true };
        Assert.Same(gMain, PenTargetResolver.Resolve(Saved(), SnapQueryFailed(gMain, Gdi(D2, x: 1920))).Display);
        // 識別できない画面があると、内蔵が見つかれば内蔵、見つからなければ Unknown（それが内蔵かもしれない）
        Assert.Same(builtIn, PenTargetResolver.Resolve(Saved(), Snap(main, builtIn, Failed(@"\\.\DISPLAY3", 3840))).Display);
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(Saved(), Snap(main, Failed(D2, 1920))).Status);
        // 保存した書き込み先があれば従来どおりそれを使い、自動では選ばない
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.Resolve(Saved("kz", D1), Snap(main, builtIn)).Status);
        Assert.Equal(PenTargetStatus.Reselect, PenTargetResolver.Resolve(Saved(name: D1, reselect: true), Snap(main, builtIn)).Status);
    }

    [Fact]
    public void Resolve_edge_cases()
    {
        // 空キーの Gdi 2 台のうち一方だけ GDI 名が一致（空キーを重複と数えない）
        var g2 = Gdi(D2, x: 1920);
        Assert.Same(g2, PenTargetResolver.Resolve(Saved(name: D2), Snap(Gdi(D1), g2)).Display);
        // 保存なし＋照会全体の失敗＋1 台
        var only = Gdi(D1);
        Assert.Same(only, PenTargetResolver.Resolve(Saved(), SnapQueryFailed(only)).Display);
        // 負の座標、同じ型番の 2 台（パスだけ違う）
        var left = Key("k-left", D2, x: -1920, y: -200, friendly: "Same");
        var right = Key("k-right", D1, friendly: "Same");
        Assert.Same(left, PenTargetResolver.Resolve(Saved("k-left"), Snap(right, left)).Display);
        // 補正で組が空になった設定で 1 台なら自動で決まる
        var corrected = new AppSettings();
        corrected.Pen.TargetDeviceName = new string('x', 513);
        Core.Settings.SettingsNormalizer.Normalize(corrected);
        Assert.False(corrected.Pen.HasTarget);
        Assert.Same(right, PenTargetResolver.Resolve(corrected.Pen, Snap(right)).Display);
    }

    private static PenOwner Owner(DisplayDescriptor d) => PenOwner.From(d, 1);

    [Fact]
    public void Compare_follows_every_row_of_the_d11_table()
    {
        var a = Key("ka", D1);
        var b = Key("kb", D2, x: 1920);
        var o = Owner(a);
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(o, Snap(a, b)));
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(o, Snap(a, Key("kb", D2, x: 1920, scale: 1.5)))); // 他の画面だけ変化
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(o, Snap(Key("ka", D2), b with { DeviceName = D1 }))); // GDI 名だけ変化
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(o, Snap(a with { FriendlyName = "別名" }))); // 表示名だけの違い
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(o, Snap(Key("ka", D1, x: 100))));     // 位置
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(o, Snap(Key("ka", D1, w: 1280, h: 720)))); // 解像度
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(o, Snap(Key("ka", D1, scale: 1.25)))); // 拡大率
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(o, Snap(b)));                          // 切断
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(o, Snap(Key("kz", D1))));              // 別機器へ置換
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(o, Snap(a, Key("ka", D2, x: 1920)))); // 重複
        Assert.Equal(PenOwnerDecision.Pending, PenTargetResolver.Compare(o, new DisplaySnapshot(false, false, [a]))); // 不完全（DPI の失敗を含む）
        Assert.Equal(PenOwnerDecision.Pending, PenTargetResolver.Compare(o, Snap(Failed(D1))));                // キーが無く別の画面が Failed
        Assert.Equal(PenOwnerDecision.Pending, PenTargetResolver.Compare(o, SnapQueryFailed(Gdi(D1))));

        var og = Owner(Gdi(D1));
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(og, Snap(Gdi(D1))));
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(og, Snap(Key("ka", D1)))); // 今回 Key でも
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(og, Snap(Gdi(D1, x: 10))));
        Assert.Equal(PenOwnerDecision.Discard, PenTargetResolver.Compare(og, Snap(Gdi(D2))));
    }

    [Fact]
    public void Duplicate_owner_with_one_output_failing_is_pending()
    {
        var monitors = new[] { DisplaySnapshotBuilderTests.M(D1) };
        var before = DisplaySnapshotBuilder.Build(monitors, true,
            [DisplaySnapshotBuilderTests.P(D1, "PATH-A"), DisplaySnapshotBuilderTests.P(D1, "PATH-B")]);
        var after = DisplaySnapshotBuilder.Build(monitors, true,
            [DisplaySnapshotBuilderTests.P(D1, "PATH-A"), DisplaySnapshotBuilderTests.P(D1, null, queried: false)]);
        var o = Owner(before.Displays[0]);
        Assert.Equal(PenOwnerDecision.Pending, PenTargetResolver.Compare(o, after));
        Assert.Equal(PenOwnerDecision.Keep, PenTargetResolver.Compare(o, before));
    }

    [Fact]
    public void Duplicate_owner_pends_while_one_path_is_empty_and_recovers()
    {
        // R5 の回帰: A+B → A+空 → A+B で保留から回復する。
        var monitors = new[] { DisplaySnapshotBuilderTests.M(D1) };
        var ab = DisplaySnapshotBuilder.Build(monitors, true, [DisplaySnapshotBuilderTests.P(D1, "PATH-A"), DisplaySnapshotBuilderTests.P(D1, "PATH-B")]);
        var aEmpty = DisplaySnapshotBuilder.Build(monitors, true, [DisplaySnapshotBuilderTests.P(D1, "PATH-A"), DisplaySnapshotBuilderTests.P(D1, null)]);
        var s = new PenSession();
        s.Start(ab.Displays[0], out _);
        s.SurfaceClosed(hasLines: true);
        Assert.Equal(PenDisplayOutcome.Pending, s.OnDisplays(aEmpty));
        Assert.Equal(PenTargetStatus.Unknown, PenTargetResolver.Resolve(new PenSettings { TargetDevicePath = ab.Displays[0].DeviceKey }, aEmpty).Status); // 開始を拒否
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(ab));
        Assert.NotNull(s.Owner);
    }
}

/// <summary>所有者の寿命・通知の順序・適用時（T-01 (5) の順序・寿命・適用時。PenController の判定部分）。</summary>
public sealed class PenSessionTests
{
    private const string D1 = @"\\.\DISPLAY1", D2 = @"\\.\DISPLAY2";
    private static readonly DisplayDescriptor A = PenTargetResolverTests.Key("ka", D1);
    private static readonly DisplayDescriptor B = PenTargetResolverTests.Key("kb", D2, x: 1920);
    private static DisplaySnapshot Snap(params DisplayDescriptor[] d) => PenTargetResolverTests.Snap(d);

    private static PenSession Started(DisplayDescriptor d)
    {
        var s = new PenSession();
        s.Start(d, out _);
        return s;
    }

    [Fact]
    public void Discard_is_latched_even_if_the_display_returns()
    {
        var s = Started(A);
        s.SurfaceClosed(hasLines: true);
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(A, B)));
        Assert.Equal(PenDisplayOutcome.Discard, s.OnDisplays(Snap(B)));
        Assert.Equal(PenDisplayOutcome.None, s.OnDisplays(Snap(A, B))); // 戻っても描線は戻らない
        Assert.Null(s.Owner);
    }

    [Fact]
    public void Keep_with_a_renamed_gdi_device_updates_the_owner_name_and_keeps_the_generation()
    {
        // R4: 同じキー・配置のまま DISPLAY1 → DISPLAY3 と再列挙された。
        var s = Started(A);
        s.SurfaceClosed(hasLines: true);
        var generation = s.Owner!.Generation;
        const string D3 = @"\\.\DISPLAY3";
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(A with { DeviceName = D3 }, B)));
        Assert.Equal(D3, s.Owner!.DeviceName);
        Assert.Equal(generation, s.Owner.Generation);
        // 次の構成変更でも新しい名前で比べ、配置が変われば破棄する。
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(A with { DeviceName = D3 })));
        Assert.Equal(PenDisplayOutcome.Discard, s.OnDisplays(Snap(A with { DeviceName = D3, Scale = 1.5 })));
    }

    [Fact]
    public void Pending_then_recovery_keeps_or_discards()
    {
        var s = Started(A);
        Assert.Equal(PenDisplayOutcome.Pending, s.OnDisplays(DisplaySnapshot.Incomplete));
        Assert.True(s.IsPending);
        Assert.Equal(PenDisplayOutcome.Keep, s.OnDisplays(Snap(A)));
        Assert.False(s.IsPending);

        var t = Started(A);
        t.OnDisplays(DisplaySnapshot.Incomplete);
        Assert.Equal(PenDisplayOutcome.Discard, t.OnDisplays(Snap(A with { Scale = 1.5 })));
    }

    [Fact]
    public void Old_retry_does_nothing_after_the_owner_changes()
    {
        var s = Started(A);
        s.OnDisplays(DisplaySnapshot.Incomplete);
        var generation = s.Owner!.Generation;
        Assert.True(s.ShouldRetry(generation));
        s.Start(A, out _); // 待つ間に別の開始
        Assert.False(s.ShouldRetry(generation));
    }

    [Fact]
    public void Owner_lifetime()
    {
        // 開始直後（未描画）に切断 → 所有者なし
        var s = Started(A);
        Assert.Equal(PenDisplayOutcome.Discard, s.OnDisplays(Snap(B)));
        Assert.False(s.SurfaceClosed(hasLines: false)); // 既に解放済み
        Assert.Null(s.Owner);

        // ペン中の全消去では入力面が残るので保持、その後の拡大率変更で破棄
        var t = Started(A);
        Assert.False(t.ReleaseIfIdle(hasLines: false));
        Assert.NotNull(t.Owner);
        Assert.Equal(PenDisplayOutcome.Discard, t.OnDisplays(Snap(A with { Scale = 2 })));

        // ペン終了後に最後の一画を戻す → 解放、以後の構成変更では何もしない
        var u = Started(A);
        Assert.False(u.SurfaceClosed(hasLines: true));
        Assert.True(u.ReleaseIfIdle(hasLines: false));
        Assert.Equal(PenDisplayOutcome.None, u.OnDisplays(Snap(B)));

        // 描かずにペン終了 → 解放
        var v = Started(A);
        Assert.True(v.SurfaceClosed(hasLines: false));
        Assert.Null(v.Owner);
    }

    [Fact]
    public void Start_reports_a_different_existing_owner()
    {
        var s = Started(A);
        s.SurfaceClosed(hasLines: true);
        Assert.True(s.Start(A, out var same));
        Assert.Equal(2, same.Generation); // 開始のたびに世代番号を新しくする
        s.SurfaceClosed(hasLines: true);
        Assert.False(s.Start(B, out _));
    }

    private static PenSettings Target(DisplayDescriptor? d, string label = "") => d is null
        ? new PenSettings()
        : new PenSettings { TargetDevicePath = d.DeviceKey, TargetDeviceName = d.DeviceName, TargetLabel = label };

    [Fact]
    public void Apply_keeps_only_when_the_new_target_is_the_owner()
    {
        // 未指定の単画面 A で描き、B を追加
        PenSession Drawn()
        {
            var s = new PenSession();
            s.Start(A, out _);
            s.SurfaceClosed(hasLines: true);
            s.OnDisplays(Snap(A, B));
            return s;
        }
        var none = new PenSettings();
        var colorOnly = new PenSettings { Color = "#00FF00" };
        Assert.False(Drawn().OnApply(none, colorOnly, Snap(A, B)));         // 書き込み先は変わっていない
        Assert.False(Drawn().OnApply(none, Target(A), Snap(A, B)));         // A を選ぶ
        Assert.True(Drawn().OnApply(none, Target(B), Snap(A, B)));          // B を選ぶ
        var onA = Drawn();
        Assert.False(onA.OnApply(none, Target(A), Snap(A, B)));
        Assert.True(onA.OnApply(Target(A), none, Snap(A, B)));              // 未指定に戻す
        Assert.True(Drawn().OnApply(none, Target(A), DisplaySnapshot.Incomplete)); // 照会保留中（Unknown）

        // Gdi 同士で GDI 名だけで選ぶ
        var ga = PenTargetResolverTests.Gdi(D1);
        var gb = PenTargetResolverTests.Gdi(D2, x: 1920);
        var g = new PenSession();
        g.Start(ga, out _);
        g.SurfaceClosed(hasLines: true);
        Assert.False(g.OnApply(Target(ga, "旧"), Target(ga, "新"), Snap(ga, gb))); // ラベルだけ違う
        Assert.True(g.OnApply(Target(ga), Target(gb), Snap(ga, gb)));

        // 同じ設定での再適用（保存の再試行）
        var r = Drawn();
        Assert.False(r.OnApply(Target(A), Target(A), Snap(A, B)));
        Assert.NotNull(r.Owner);
    }
}
