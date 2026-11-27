using System.Text.Json.Nodes;
using PresenterTools.Core.Input;
using PresenterTools.Core.Models;
using PresenterTools.Core.ScreenPen;
using PresenterTools.Core.Settings;
using PresenterTools.Core.State;

namespace PresenterTools.Tests;

/// <summary>V1.7.0 画面ペン: 状態・設定（docs/verification.md T-01 (6)〜(13)）。</summary>
public sealed class ScreenPenSettingsTests
{
    // ---- (6) PresenterState ----

    [Fact]
    public void Pen_starts_off_and_is_not_saved()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        Assert.False(state.PenActive);
        state.SetPen(true);
        Assert.True(state.IsPenActive);
        var s = new AppSettings();
        state.WriteTo(s);
        Assert.False(PresenterState.FromSettings(s).PenActive);
    }

    [Fact]
    public void Master_off_ends_the_pen_and_master_on_does_not_restart_it()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        state.SetPen(true);
        state.SetMaster(false);
        Assert.False(state.PenActive);
        state.SetMaster(true);
        Assert.False(state.PenActive);
        state.SetMaster(false);
        state.SetPen(true); // Master OFF の間の開始は無視する
        Assert.False(state.PenActive);
    }

    [Fact]
    public void Pen_ends_laser_and_blocks_drawing_until_it_ends()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        Assert.True(state.BeginLaser(LaserTrigger.Keyboard));
        Assert.True(state.IsLaserActive);
        state.SetPen(true);
        Assert.False(state.IsLaserActive);
        Assert.True(state.LaserEnabled); // ON/OFF は変えない
        Assert.False(state.BeginLaser(LaserTrigger.Mouse));
        state.ToggleLaserDrawing(LaserTrigger.Keyboard);
        Assert.False(state.IsLaserActive);
        state.SetPen(false);
        Assert.True(state.BeginLaser(LaserTrigger.Keyboard));
    }

    [Fact]
    public void Pen_change_does_not_ask_to_save()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        bool? persist = null;
        state.Changed += (_, e) => persist = e.PersistSettings;
        state.SetPen(true);
        Assert.False(persist);
    }

    // ---- (7)(8)(9) 読み込み・補正・保存 ----

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>既定の設定を保存したファイルを書き換えて読み込む。</summary>
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

    private static JsonObject Pen(JsonObject root) => root["pen"]!.AsObject();

    [Fact]
    public void Version_1_6_0_file_gets_pen_defaults_without_corrections()
    {
        var result = LoadWith(root =>
        {
            root.Remove("pen");
            root["hotkeys"]!.AsObject().Remove("togglePen");
        });
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Corrections);
        var p = result.Settings.Pen;
        Assert.Equal("#FF2020", p.Color);
        Assert.Equal((2.0, 4.0, 8.0), (p.ThinWidth, p.MediumWidth, p.ThickWidth));
        Assert.Equal(PenWidth.Medium, p.StartWidth);
        Assert.False(p.HasTarget);
        Assert.Equal("", result.Settings.Hotkeys.TogglePen);
    }

    [Theory]
    [InlineData("2.5", 3)]
    [InlineData("\"2.5\"", 3)]
    [InlineData("0", 1)]
    [InlineData("1.4", 1)]
    public void Thin_width_is_rounded_and_clamped(string json, double expected)
    {
        var result = LoadWith(root => Pen(root)["thinWidth"] = JsonNode.Parse(json));
        Assert.Equal(expected, result.Settings.Pen.ThinWidth);
        Assert.Contains("pen.thinWidth", result.Corrections);
    }

    [Fact]
    public void Thick_width_over_40_is_clamped()
    {
        var result = LoadWith(root => Pen(root)["thickWidth"] = 41);
        Assert.Equal(40, result.Settings.Pen.ThickWidth);
    }

    [Theory]
    [InlineData(4, 4, 8)]
    [InlineData(8, 4, 2)]
    public void Width_order_violation_resets_all_three(double thin, double medium, double thick)
    {
        var result = LoadWith(root =>
        {
            Pen(root)["thinWidth"] = thin;
            Pen(root)["mediumWidth"] = medium;
            Pen(root)["thickWidth"] = thick;
        });
        var p = result.Settings.Pen;
        Assert.Equal((2.0, 4.0, 8.0), (p.ThinWidth, p.MediumWidth, p.ThickWidth));
        Assert.Contains("pen.widthOrder", result.Corrections);
    }

    [Fact]
    public void Invalid_start_width_and_color_fall_back()
    {
        var result = LoadWith(root =>
        {
            Pen(root)["startWidth"] = 9;
            Pen(root)["color"] = "red";
        });
        Assert.Equal(PenWidth.Medium, result.Settings.Pen.StartWidth);
        Assert.Equal("#FF2020", result.Settings.Pen.Color);
    }

    [Fact]
    public void Target_limits_reset_the_whole_group()
    {
        var name = LoadWith(root =>
        {
            Pen(root)["targetDevicePath"] = "9:ABCDEFGHI";
            Pen(root)["targetDeviceName"] = new string('n', 513);
            Pen(root)["targetLabel"] = "L";
            Pen(root)["targetReselect"] = true;
        }).Settings.Pen;
        Assert.Equal(("", "", "", false), (name.TargetDevicePath, name.TargetDeviceName, name.TargetLabel, name.TargetReselect));

        var path = LoadWith(root => Pen(root)["targetDevicePath"] = new string('p', 8193)).Settings.Pen;
        Assert.False(path.HasTarget);

        var exact = LoadWith(root =>
        {
            Pen(root)["targetDevicePath"] = new string('p', 8192);
            Pen(root)["targetDeviceName"] = new string('n', 512);
        });
        Assert.Equal(8192, exact.Settings.Pen.TargetDevicePath.Length);
        Assert.Equal(512, exact.Settings.Pen.TargetDeviceName.Length);
        Assert.Empty(exact.Corrections);
    }

    [Fact]
    public void Null_targets_become_empty_and_reselect_needs_a_target()
    {
        var result = LoadWith(root =>
        {
            Pen(root)["targetDevicePath"] = null;
            Pen(root)["targetDeviceName"] = null;
            Pen(root)["targetLabel"] = null;
            Pen(root)["targetReselect"] = true;
        });
        var p = result.Settings.Pen;
        Assert.Equal(("", "", ""), (p.TargetDevicePath, p.TargetDeviceName, p.TargetLabel));
        Assert.False(p.TargetReselect);
    }

    [Fact]
    public void Non_finite_widths_in_the_model_fall_back_per_width()
    {
        var s = new AppSettings();
        s.Pen.ThinWidth = 3;
        s.Pen.MediumWidth = double.NaN;
        s.Pen.ThickWidth = double.PositiveInfinity;
        var fixes = SettingsNormalizer.Normalize(s);
        Assert.Equal((3.0, 4.0, 8.0), (s.Pen.ThinWidth, s.Pen.MediumWidth, s.Pen.ThickWidth));
        Assert.Contains("pen.mediumWidth", fixes);
        Assert.Contains("pen.thickWidth", fixes);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Quoted_non_finite_width_keeps_the_other_settings(string value)
    {
        void Change(JsonObject root)
        {
            Pen(root)["mediumWidth"] = value;
            Pen(root)["thinWidth"] = 3;
            Pen(root)["color"] = "#00FF00";
            root["laser"]!["width"] = 12;
        }
        var loaded = LoadWith(Change);
        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal((3.0, 4.0, "#00FF00", 12.0), (loaded.Settings.Pen.ThinWidth, loaded.Settings.Pen.MediumWidth, loaded.Settings.Pen.Color, loaded.Settings.Laser.Width));
        Assert.Contains("pen.mediumWidth", loaded.Corrections);

        var imported = SettingsTransfer.Parse(Export(root => Change(root["settings"]!.AsObject())));
        Assert.Equal(SettingsImportError.None, imported.Error);
        Assert.Equal((3.0, 4.0, "#00FF00"), (imported.Settings!.Pen.ThinWidth, imported.Settings.Pen.MediumWidth, imported.Settings.Pen.Color));
        Assert.Contains("pen.mediumWidth", imported.Corrections);
    }

    [Fact]
    public void Raw_nan_or_wrong_type_is_a_corrupted_file_and_a_rejected_import()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new SettingsStore(path).Save(new AppSettings());
            var text = File.ReadAllText(path);
            var raw = text.Replace("\"mediumWidth\": 4", "\"mediumWidth\": NaN");
            Assert.NotEqual(text, raw);
            File.WriteAllText(path, raw);
            Assert.Equal(SettingsLoadStatus.Corrupted, new SettingsStore(path).Load().Status);

            var wrongType = LoadWith(root => Pen(root)["mediumWidth"] = new JsonObject());
            Assert.Equal(SettingsLoadStatus.Corrupted, wrongType.Status);
        }
        finally { Directory.Delete(dir, recursive: true); }

        var original = Export();
        var export = original.Replace("\"mediumWidth\":4", "\"mediumWidth\":NaN");
        Assert.NotEqual(original, export);
        Assert.Null(SettingsTransfer.Parse(export).Settings);
        Assert.Null(SettingsTransfer.Parse(Export(root => root["settings"]!["pen"]!["mediumWidth"] = new JsonObject())).Settings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Saved_target_resolves_to_the_same_display_after_reload(int kind)
    {
        var monitors = new[] { DisplaySnapshotBuilderTests.M(@"\\.\DISPLAY1"), DisplaySnapshotBuilderTests.M(@"\\.\DISPLAY2", x: 1920) };
        IReadOnlyList<RawPath> paths = kind switch
        {
            1 => [DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY1", "PATH-A"), DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY2", "PATH-B")],
            2 => [DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY1", "PATH-A"), DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY2", "PATH-C"), DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY2", "PATH-B")],
            _ => [DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY1", "PATH-A"), DisplaySnapshotBuilderTests.P(@"\\.\DISPLAY2", @"\\?\" + new string('X', DeviceKey.MaxLength - 9))],
        };
        var snapshot = DisplaySnapshotBuilder.Build(monitors, true, paths);
        var target = snapshot.Displays[1];
        Assert.Equal(DisplayIdentity.Key, target.Identity);
        if (kind == 3) Assert.Equal(DeviceKey.MaxLength, target.DeviceKey.Length);

        var s = new AppSettings();
        s.Pen.TargetDevicePath = target.DeviceKey;
        s.Pen.TargetDeviceName = target.DeviceName;
        s.Pen.TargetLabel = "Mon";
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new SettingsStore(path).Save(s);
            var loaded = new SettingsStore(path).Load();
            Assert.Empty(loaded.Corrections);
            // 列挙の順序が変わっても同じ画面になる
            var reordered = DisplaySnapshotBuilder.Build(monitors, true, paths.Reverse().ToList());
            Assert.Equal(target.DeviceName, PenTargetResolver.Resolve(loaded.Settings.Pen, reordered).Display?.DeviceName);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void All_pen_items_round_trip()
    {
        var s = new AppSettings();
        s.Pen.Color = "#1565C0";
        s.Pen.ThinWidth = 3;
        s.Pen.MediumWidth = 6;
        s.Pen.ThickWidth = 12;
        s.Pen.StartWidth = PenWidth.Thick;
        s.Pen.TargetDevicePath = "4:PATH";
        s.Pen.TargetDeviceName = @"\\.\DISPLAY2";
        s.Pen.TargetLabel = "DELL";
        s.Hotkeys.TogglePen = "Ctrl+Alt+F8";
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new SettingsStore(path).Save(s);
            var p = new SettingsStore(path).Load().Settings;
            Assert.Equal(("#1565C0", 3.0, 6.0, 12.0, PenWidth.Thick), (p.Pen.Color, p.Pen.ThinWidth, p.Pen.MediumWidth, p.Pen.ThickWidth, p.Pen.StartWidth));
            Assert.Equal(("4:PATH", @"\\.\DISPLAY2", "DELL", false), (p.Pen.TargetDevicePath, p.Pen.TargetDeviceName, p.Pen.TargetLabel, p.Pen.TargetReselect));
            Assert.Equal("Ctrl+Alt+F8", p.Hotkeys.TogglePen);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- (10) ショートカット ----

    [Fact]
    public void Toggle_pen_shortcut_is_validated_like_the_others()
    {
        var h = new HotkeySettings { ToggleMaster = "Ctrl+Alt+M", TogglePen = "Ctrl+Alt+M" };
        var problems = HotkeyBindings.Validate(h);
        Assert.Contains(problems, p => p.Action == HotkeyAction.TogglePen);

        // 末尾に追加したため、既存 8 件の値（登録 ID は (int)action + 1）が変わらない。
        Assert.Equal(8, (int)HotkeyAction.TogglePen);
        Assert.Equal(HotkeyAction.TogglePen, HotkeyActionNames.All[^1]);
        Assert.Equal(9, HotkeyActionNames.All.Count);
        Assert.Equal(SettingsSection.Pen, SettingsValidator.SectionOf(HotkeyAction.TogglePen));
        Assert.Contains(HotkeyAction.TogglePen, SettingsReset.HotkeysOf(SettingsSection.Pen));
    }

    [Fact]
    public void Duplicate_toggle_pen_in_a_file_is_corrected_like_the_others()
    {
        var result = LoadWith(root =>
        {
            root["hotkeys"]!["toggleMaster"] = "Ctrl+Alt+M";
            root["hotkeys"]!["togglePen"] = "Ctrl+Alt+M";
        });
        var expected = LoadWith(root =>
        {
            root["hotkeys"]!["toggleMaster"] = "Ctrl+Alt+M";
            root["hotkeys"]!["toggleKeyDisplay"] = "Ctrl+Alt+M";
        });
        Assert.Equal("Ctrl+Alt+M", result.Settings.Hotkeys.ToggleMaster);
        Assert.Equal(expected.Settings.Hotkeys.ToggleKeyDisplay, result.Settings.Hotkeys.TogglePen);
    }

    // ---- (11) SettingsEditSession の組 ----

    private static void SetTarget(AppSettings s, string path, string name, string label, bool reselect = false)
    {
        s.Pen.TargetDevicePath = path;
        s.Pen.TargetDeviceName = name;
        s.Pen.TargetLabel = label;
        s.Pen.TargetReselect = reselect;
    }

    private static (string, string, string, bool) TargetOf(AppSettings s) =>
        (s.Pen.TargetDevicePath, s.Pen.TargetDeviceName, s.Pen.TargetLabel, s.Pen.TargetReselect);

    [Fact]
    public void Target_group_is_merged_as_a_whole()
    {
        var baseline = new AppSettings();
        SetTarget(baseline, "1:A", "DA", "LA");
        var edit = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        var current = new AppSettings();
        SetTarget(current, "1:C", "DC", "LC", reselect: true);

        // draft で 1 項目だけ変えると draft の組
        edit.Settings.Pen.TargetLabel = "LB";
        Assert.Equal(("1:A", "DA", "LB", false), TargetOf(edit.Build(current, false, KeyDisplayMode.Off).Settings));

        // draft で変えなければ current の組
        var untouched = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        Assert.Equal(("1:C", "DC", "LC", true), TargetOf(untouched.Build(current, false, KeyDisplayMode.Off).Settings));

        // baseline と draft で 1 項目だけ変え、残りを current だけで変えた場合も draft の組
        var mixed = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        mixed.Settings.Pen.TargetDevicePath = "1:B";
        var current2 = new AppSettings();
        SetTarget(current2, "1:A", "DX", "LX");
        Assert.Equal(("1:B", "DA", "LA", false), TargetOf(mixed.Build(current2, false, KeyDisplayMode.Off).Settings));
    }

    [Fact]
    public void Pen_tab_reset_keeps_target_and_forces_appearance()
    {
        var baseline = new AppSettings();
        SetTarget(baseline, "1:A", "DA", "LA");
        baseline.Pen.Color = "#00FF00";
        baseline.Pen.StartWidth = PenWidth.Thick;
        var edit = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        var current = new AppSettings();
        SetTarget(current, "1:C", "DC", "LC");
        current.Pen.Color = "#0000FF";
        edit.ResetSection(SettingsSection.Pen);
        var built = edit.Build(current, false, KeyDisplayMode.Off).Settings;
        Assert.Equal(("1:C", "DC", "LC", false), TargetOf(built)); // 書き込み先は強制しない
        Assert.Equal("#FF2020", built.Pen.Color);
        Assert.Equal(PenWidth.Medium, built.Pen.StartWidth);
    }

    [Fact]
    public void Pen_tab_revert_and_reset_all_force_the_target()
    {
        var baseline = new AppSettings();
        SetTarget(baseline, "1:A", "DA", "LA");
        baseline.Hotkeys.TogglePen = "Ctrl+Alt+F8";
        var current = new AppSettings();
        SetTarget(current, "1:C", "DC", "LC");
        current.Hotkeys.TogglePen = "Ctrl+Alt+F9";

        var revert = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        revert.Settings.Pen.TargetDevicePath = "1:Z";
        revert.Settings.Hotkeys.TogglePen = "Ctrl+Alt+F7";
        revert.RevertSection(SettingsSection.Pen);
        var r = revert.Build(current, false, KeyDisplayMode.Off).Settings;
        Assert.Equal(("1:A", "DA", "LA", false), TargetOf(r));
        Assert.Equal("Ctrl+Alt+F8", r.Hotkeys.TogglePen);

        var all = new SettingsEditSession(baseline, false, KeyDisplayMode.Off);
        all.ResetAll();
        Assert.Equal(("", "", "", false), TargetOf(all.Build(current, false, KeyDisplayMode.Off).Settings));
    }

    [Fact]
    public void Pen_edits_are_pending_changes()
    {
        var current = new AppSettings();
        foreach (var change in new Action<PenSettings>[] { p => p.TargetDevicePath = "1:A", p => p.Color = "#000000", p => p.ThickWidth = 9 })
        {
            var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
            change(edit.Settings.Pen);
            Assert.True(edit.HasChanges(current, false, KeyDisplayMode.Off));
        }
    }

    // ---- (12) エクスポート・インポート ----

    private static string Export(Action<JsonObject>? change = null, AppSettings? settings = null)
    {
        var root = JsonNode.Parse(SettingsTransfer.Serialize(settings ?? new AppSettings(), "1.7.0", DateTimeOffset.Now))!.AsObject();
        change?.Invoke(root);
        return root.ToJsonString();
    }

    [Fact]
    public void Export_has_pen_and_import_forces_every_pen_item()
    {
        var source = new AppSettings();
        source.Pen.Color = "#123456";
        source.Pen.ThickWidth = 20;
        SetTarget(source, "4:PATH", @"\\.\DISPLAY2", "DELL");
        source.Hotkeys.TogglePen = "Ctrl+Alt+F8";
        var json = Export(settings: source);
        var body = JsonNode.Parse(json)!["settings"]!.AsObject();
        Assert.True(body.ContainsKey("pen"));
        Assert.False(body["pen"]!.AsObject().ContainsKey("hasTarget")); // 計算値は書かない
        Assert.False(body.ContainsKey("controlPanel"));

        var imported = SettingsTransfer.Parse(json).Settings!;
        var current = new AppSettings();
        current.Hotkeys.TogglePen = "Ctrl+Alt+F9";
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.Import(imported, includeHotkeys: false, includeMouseButtons: false);
        var built = edit.Build(current, false, KeyDisplayMode.Off).Settings;
        Assert.Equal(("#123456", 20.0), (built.Pen.Color, built.Pen.ThickWidth));
        Assert.Equal(("4:PATH", @"\\.\DISPLAY2", "DELL", false), TargetOf(built));
        Assert.Equal("Ctrl+Alt+F9", built.Hotkeys.TogglePen); // ショートカットを読まない指定

        var withKeys = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        withKeys.Import(imported, includeHotkeys: true, includeMouseButtons: false);
        Assert.Equal("Ctrl+Alt+F8", withKeys.Build(current, false, KeyDisplayMode.Off).Settings.Hotkeys.TogglePen);
    }

    [Fact]
    public void Import_with_only_a_gdi_name_requires_reselection()
    {
        var json = Export(root =>
        {
            var pen = root["settings"]!["pen"]!.AsObject();
            pen["targetDeviceName"] = @"\\.\DISPLAY1";
        });
        var pen = SettingsTransfer.Parse(json).Settings!.Pen;
        Assert.Equal(("", "", @"\\.\DISPLAY1", true), (pen.TargetDevicePath, pen.TargetDeviceName, pen.TargetLabel, pen.TargetReselect));
        var single = PenTargetResolverTests.Snap(PenTargetResolverTests.Gdi(@"\\.\DISPLAY1"));
        Assert.Equal(PenTargetStatus.Reselect, PenTargetResolver.Resolve(pen, single).Status);

        // 保存→読み直し後も同じ
        var s = new AppSettings { Pen = pen };
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new SettingsStore(path).Save(s);
            Assert.Equal(PenTargetStatus.Reselect, PenTargetResolver.Resolve(new SettingsStore(path).Load().Settings.Pen, single).Status);
        }
        finally { Directory.Delete(dir, recursive: true); }

        // ラベルがあればそれを残す
        var labeled = SettingsTransfer.Parse(Export(root =>
        {
            var p = root["settings"]!["pen"]!.AsObject();
            p["targetDeviceName"] = @"\\.\DISPLAY1";
            p["targetLabel"] = "DELL";
        })).Settings!.Pen;
        Assert.Equal(("DELL", true), (labeled.TargetLabel, labeled.TargetReselect));
    }

    [Fact]
    public void Import_with_a_path_is_kept_and_reported_when_missing_here()
    {
        var pen = SettingsTransfer.Parse(Export(root => root["settings"]!["pen"]!["targetDevicePath"] = "5:OTHER")).Settings!.Pen;
        Assert.Equal(("5:OTHER", false), (pen.TargetDevicePath, pen.TargetReselect));
        var here = PenTargetResolverTests.Snap(PenTargetResolverTests.Key("4:HERE", @"\\.\DISPLAY1"));
        Assert.Equal(PenTargetStatus.NotConnected, PenTargetResolver.Resolve(pen, here).Status);

        var reselect = SettingsTransfer.Parse(Export(root =>
        {
            root["settings"]!["pen"]!["targetDevicePath"] = "5:OTHER";
            root["settings"]!["pen"]!["targetReselect"] = true;
        })).Settings!.Pen;
        Assert.True(reselect.TargetReselect); // 元が true ならそのまま
    }

    [Fact]
    public void Import_without_a_target_stays_unselected()
    {
        var old = SettingsTransfer.Parse(Export(root => root["settings"]!.AsObject().Remove("pen"))).Settings!.Pen;
        Assert.False(old.HasTarget);
        Assert.False(old.TargetReselect);
        var single = PenTargetResolverTests.Snap(PenTargetResolverTests.Key("4:HERE", @"\\.\DISPLAY1"));
        Assert.True(PenTargetResolver.Resolve(old, single).IsResolved);

        var empty = SettingsTransfer.Parse(Export()).Settings!.Pen;
        Assert.False(empty.TargetReselect);
    }

    // ---- (13) 検証 ----

    [Fact]
    public void Validator_reports_pen_color_and_width_order_but_not_a_missing_target()
    {
        var s = new AppSettings();
        Assert.Empty(SettingsValidator.Validate(s)); // 書き込み先なしは問題にしない
        s.Pen.Color = "blue";
        s.Pen.ThinWidth = 10;
        var problems = SettingsValidator.Validate(s);
        Assert.Contains(problems, p => p is { Section: SettingsSection.Pen, Field: "pen.color" });
        Assert.Contains(problems, p => p is { Section: SettingsSection.Pen, Field: SettingsValidator.PenThinWidthField });
    }
}
