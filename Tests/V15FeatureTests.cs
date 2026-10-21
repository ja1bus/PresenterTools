using System.Text.Json.Nodes;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

/// <summary>
/// V1.5.0: 設定エラーの場所（SettingsValidator）・Laser を描く操作の有無・未適用の変更の判定・
/// 設定のエクスポート／インポート（SettingsTransfer・SettingsEditSession.Import）。
/// </summary>
public sealed class V15FeatureTests
{
    // ---- 設定エラーの場所 ----

    [Fact]
    public void Validator_accepts_defaults()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
    }

    [Fact]
    public void Validator_reports_the_tab_and_field_of_each_problem()
    {
        var s = new AppSettings();
        s.Laser.Color = "red";
        s.KeyDisplay.BackgroundColor = "#12";
        s.Laser.MouseButton = MouseTriggerButton.XButton1;
        s.Magnifier.MouseButton = MouseTriggerButton.XButton1;
        s.Hotkeys.ToggleMaster = "Ctrl+Alt+M";
        s.Hotkeys.ToggleMagnifier = "Ctrl+Alt+M"; // Master と重複（後の欄に出す）

        var problems = SettingsValidator.Validate(s);

        Assert.Contains(problems, p => p is { Section: SettingsSection.Laser, Field: "laser.color" });
        Assert.Contains(problems, p => p is { Section: SettingsSection.KeyDisplay, Field: "keyDisplay.backgroundColor" });
        Assert.Contains(problems, p => p is { Section: SettingsSection.Laser, Field: SettingsValidator.LaserMouseField });
        var hotkey = Assert.Single(problems, p => p.Action is not null);
        Assert.Equal(HotkeyAction.ToggleMagnifier, hotkey.Action);
        Assert.Equal(SettingsSection.Magnifier, hotkey.Section);
        Assert.Equal("hotkeys.toggleMagnifier", hotkey.Field);
        // ショートカットの問題を先に、続いてタブの順に並べる（最初の問題のタブを開くため）。
        Assert.Same(hotkey, problems[0]);
    }

    [Theory]
    [InlineData(HotkeyAction.ToggleMaster, SettingsSection.General)]
    [InlineData(HotkeyAction.ShowControlPanel, SettingsSection.General)]
    [InlineData(HotkeyAction.ToggleCursorHighlight, SettingsSection.CursorHighlight)]
    [InlineData(HotkeyAction.ToggleClickEffect, SettingsSection.ClickEffect)]
    [InlineData(HotkeyAction.ToggleLaser, SettingsSection.Laser)]
    [InlineData(HotkeyAction.LaserHold, SettingsSection.Laser)]
    [InlineData(HotkeyAction.ToggleMagnifier, SettingsSection.Magnifier)]
    [InlineData(HotkeyAction.ToggleKeyDisplay, SettingsSection.KeyDisplay)]
    public void Each_shortcut_belongs_to_the_tab_that_shows_it(HotkeyAction action, SettingsSection section)
    {
        Assert.Equal(section, SettingsValidator.SectionOf(action));
        Assert.Contains(action, SettingsReset.HotkeysOf(section));
    }

    // ---- Laser を描く操作 ----

    [Fact]
    public void Laser_needs_a_drawing_shortcut_or_mouse_button()
    {
        var s = new AppSettings();
        Assert.False(SettingsValidator.HasLaserTrigger(s)); // 既定はどちらも未割当

        s.Hotkeys.ToggleLaser = "Ctrl+Alt+L"; // ON/OFF だけでは描けない
        Assert.False(SettingsValidator.HasLaserTrigger(s));

        s.Hotkeys.LaserHold = "Ctrl+Alt+D";
        Assert.True(SettingsValidator.HasLaserTrigger(s));

        s.Hotkeys.LaserHold = "";
        s.Laser.MouseButton = MouseTriggerButton.XButton2;
        Assert.True(SettingsValidator.HasLaserTrigger(s));
    }

    // ---- 未適用の変更 ----

    [Fact]
    public void Fresh_session_has_no_changes()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        Assert.False(edit.HasChanges(current, false, KeyDisplayMode.Off));
    }

    [Fact]
    public void Editing_a_value_is_a_change_until_it_is_put_back()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.Settings.Laser.Width = 20;
        Assert.True(edit.HasChanges(current, false, KeyDisplayMode.Off));
        edit.Settings.Laser.Width = current.Laser.Width;
        Assert.False(edit.HasChanges(current, false, KeyDisplayMode.Off));
    }

    [Fact]
    public void Runtime_states_count_as_changes()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.KeyDisplayMode = KeyDisplayMode.FullText;
        Assert.True(edit.HasChanges(current, false, KeyDisplayMode.Off));
        edit.KeyDisplayMode = KeyDisplayMode.Off;
        edit.MagnifierEnabled = true;
        Assert.True(edit.HasChanges(current, false, KeyDisplayMode.Off));
    }

    [Fact]
    public void Changes_made_outside_the_screen_are_not_pending_changes()
    {
        // 設定画面を開いている間に操作パネルで ON/OFF を変えても、触っていない項目は未適用の変更にしない。
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        current.Laser.Enabled = false;
        current.General.MasterEnabled = false;
        Assert.False(edit.HasChanges(current, true, KeyDisplayMode.FullText));
    }

    [Fact]
    public void Resetting_values_that_are_already_defaults_is_not_a_change()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.ResetSection(SettingsSection.CursorHighlight);
        edit.ResetHotkeys();
        Assert.False(edit.HasChanges(current, false, KeyDisplayMode.Off));
    }

    [Fact]
    public void Panel_placement_is_never_a_pending_change()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        current.ControlPanel.Left = 500;
        Assert.False(edit.HasChanges(current, false, KeyDisplayMode.Off));
    }

    // ---- エクスポート ----

    [Fact]
    public void Export_has_app_name_versions_and_no_panel_placement()
    {
        var s = new AppSettings();
        s.ControlPanel.Left = 123;
        s.Laser.Width = 12;
        var json = SettingsTransfer.Serialize(s, "1.5.0", new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.FromHours(9)));
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("PresenterTools", (string?)root["app"]);
        Assert.Equal(SettingsTransfer.CurrentFormatVersion, (int?)root["formatVersion"]);
        Assert.Equal("1.5.0", (string?)root["appVersion"]);
        Assert.Equal("2026-10-21T09:30:00+09:00", (string?)root["exportedAt"]);
        var body = root["settings"]!.AsObject();
        Assert.False(body.ContainsKey("controlPanel"));
        Assert.Equal(12, (double?)body["laser"]!["width"]);
        Assert.Equal(AppSettings.CurrentSchemaVersion, (int?)body["schemaVersion"]);
        Assert.Contains("\"toggleMaster\": \"\"", json); // 手で読める形式（settings.json と同じ）
    }

    [Fact]
    public void Default_file_name_has_the_date()
    {
        Assert.Equal("PresenterTools-20261021.json", SettingsTransfer.DefaultFileName(new DateTime(2026, 10, 21, 23, 59, 0)));
    }

    [Fact]
    public void Export_file_is_written_and_read_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "PresenterTools-20261021.json");
            File.WriteAllText(path, "old");
            var s = new AppSettings();
            s.CursorHighlight.Color = "#00C853";
            SettingsTransfer.Write(path, SettingsTransfer.Serialize(s, "1.5.0", DateTimeOffset.Now));
            Assert.False(File.Exists(path + ".tmp"));

            var result = SettingsTransfer.Parse(File.ReadAllText(path));
            Assert.Equal(SettingsImportError.None, result.Error);
            Assert.Equal("#00C853", result.Settings!.CursorHighlight.Color);
            Assert.Equal("1.5.0", result.SourceAppVersion);
            Assert.NotNull(result.ExportedAt);
            Assert.Empty(result.Corrections);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- インポートの検証 ----

    private static string Export(Action<JsonObject>? change = null)
    {
        var root = JsonNode.Parse(SettingsTransfer.Serialize(new AppSettings(), "1.5.0", DateTimeOffset.Now))!.AsObject();
        change?.Invoke(root);
        return root.ToJsonString();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"app\": ")]
    public void Import_rejects_text_that_is_not_json(string text)
    {
        Assert.Equal(SettingsImportError.NotJson, SettingsTransfer.Parse(text).Error);
    }

    [Fact]
    public void Import_rejects_other_json_files()
    {
        // settings.json そのもの・別のアプリの JSON・配列・アプリ名違い・形式の版なし・設定なし
        var settingsJson = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), SettingsJson.Options);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse(settingsJson).Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse("{\"name\":\"other\",\"version\":1}").Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse("[1,2,3]").Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse(Export(r => r["app"] = "AreaShot")).Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse(Export(r => r.Remove("formatVersion"))).Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse(Export(r => r["formatVersion"] = "1")).Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse(Export(r => r.Remove("settings"))).Error);
        Assert.Equal(SettingsImportError.NotExportFile, SettingsTransfer.Parse(Export(r => r["settings"] = "x")).Error);
    }

    [Fact]
    public void Import_rejects_newer_formats()
    {
        var newer = SettingsTransfer.Parse(Export(r =>
        {
            r["formatVersion"] = SettingsTransfer.CurrentFormatVersion + 1;
            r["appVersion"] = "9.0.0";
        }));
        Assert.Equal(SettingsImportError.NewerFormat, newer.Error);
        Assert.Equal("9.0.0", newer.SourceAppVersion);
        Assert.Null(newer.Settings);

        var newerSchema = SettingsTransfer.Parse(Export(r => r["settings"]!["schemaVersion"] = AppSettings.CurrentSchemaVersion + 1));
        Assert.Equal(SettingsImportError.NewerFormat, newerSchema.Error);
    }

    [Fact]
    public void Import_rejects_unreadable_settings()
    {
        var result = SettingsTransfer.Parse(Export(r => r["settings"]!["laser"] = new JsonObject { ["width"] = new JsonArray() }));
        Assert.Equal(SettingsImportError.InvalidSettings, result.Error);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void Import_corrects_out_of_range_values_and_reports_them()
    {
        var result = SettingsTransfer.Parse(Export(r =>
        {
            r["settings"]!["laser"]!["width"] = 999;
            r["settings"]!["keyDisplay"]!["position"] = "Somewhere";
        }));
        Assert.Equal(SettingsImportError.None, result.Error);
        Assert.Equal(SettingsNormalizer.MaxLaserWidth, result.Settings!.Laser.Width);
        Assert.Equal(KeyDisplayPosition.BottomCenter, result.Settings.KeyDisplay.Position);
        Assert.Contains("laser.width", result.Corrections);
        Assert.Contains("keyDisplay.position", result.Corrections);
    }

    [Fact]
    public void Import_accepts_files_from_newer_apps_with_the_same_format()
    {
        // 項目が増えただけなら同じ形式の版のまま。知らない項目は無視して読む。
        var result = SettingsTransfer.Parse(Export(r =>
        {
            r["appVersion"] = "1.9.0";
            r["settings"]!["laser"]!["futureOption"] = true;
            r["futureSection"] = new JsonObject();
        }));
        Assert.Equal(SettingsImportError.None, result.Error);
        Assert.Equal("1.9.0", result.SourceAppVersion);
    }

    // ---- 編集中の値への読み込み ----

    private static AppSettings OtherPc()
    {
        var s = new AppSettings();
        s.General.Language = "en";
        s.General.MasterEnabled = false;
        s.General.AlwaysOnTop = false;
        s.CursorHighlight.Enabled = false;
        s.CursorHighlight.Diameter = 120;
        s.ClickEffect.Enabled = false;
        s.ClickEffect.LeftColor = "#00C853";
        s.Laser.Enabled = false;
        s.Laser.Width = 20;
        s.Laser.MouseButton = MouseTriggerButton.XButton1;
        s.Magnifier.ZoomPercent = 400;
        s.Magnifier.MouseButton = MouseTriggerButton.XButton2;
        s.KeyDisplay.FontSize = 80;
        s.Hotkeys.ToggleMaster = "Ctrl+Alt+M";
        s.ControlPanel.Left = 999;
        return s;
    }

    private static (AppSettings Current, SettingsEditSession Edit) ThisPc()
    {
        var current = new AppSettings();
        current.General.Language = "ja";
        current.Hotkeys.ShowControlPanel = "Ctrl+Shift+F10";
        current.ControlPanel.Left = 10;
        return (current, new SettingsEditSession(current, false, KeyDisplayMode.Off));
    }

    [Fact]
    public void Import_takes_settings_but_keeps_language_states_and_panel_of_this_pc()
    {
        var (current, edit) = ThisPc();
        edit.Import(OtherPc(), includeHotkeys: false, includeMouseButtons: false);
        Assert.True(edit.HasChanges(current, false, KeyDisplayMode.Off));
        var r = edit.Build(current, false, KeyDisplayMode.Off).Settings;

        Assert.False(r.General.AlwaysOnTop);
        Assert.Equal(120, r.CursorHighlight.Diameter);
        Assert.Equal("#00C853", r.ClickEffect.LeftColor);
        Assert.Equal(20, r.Laser.Width);
        Assert.Equal(400, r.Magnifier.ZoomPercent);
        Assert.Equal(80, r.KeyDisplay.FontSize);

        Assert.Equal("ja", r.General.Language);
        Assert.True(r.General.MasterEnabled);
        Assert.True(r.CursorHighlight.Enabled);
        Assert.True(r.ClickEffect.Enabled);
        Assert.True(r.Laser.Enabled);
        Assert.Equal(10, r.ControlPanel.Left);
        // 既定ではショートカット・マウスボタンも、この PC のまま。
        Assert.Equal("", r.Hotkeys.ToggleMaster);
        Assert.Equal("Ctrl+Shift+F10", r.Hotkeys.ShowControlPanel);
        Assert.Equal(MouseTriggerButton.None, r.Laser.MouseButton);
        Assert.Equal(MouseTriggerButton.None, r.Magnifier.MouseButton);
    }

    [Fact]
    public void Import_takes_shortcuts_only_when_asked()
    {
        var (current, edit) = ThisPc();
        edit.Import(OtherPc(), includeHotkeys: true, includeMouseButtons: false);
        var r = edit.Build(current, false, KeyDisplayMode.Off).Settings;
        Assert.Equal("Ctrl+Alt+M", r.Hotkeys.ToggleMaster);
        Assert.Equal("", r.Hotkeys.ShowControlPanel); // ファイル側の値（未割当）で置き換える
        Assert.Equal(MouseTriggerButton.None, r.Laser.MouseButton);
    }

    [Fact]
    public void Import_takes_mouse_buttons_only_when_asked()
    {
        var (current, edit) = ThisPc();
        edit.Import(OtherPc(), includeHotkeys: false, includeMouseButtons: true);
        var r = edit.Build(current, false, KeyDisplayMode.Off).Settings;
        Assert.Equal(MouseTriggerButton.XButton1, r.Laser.MouseButton);
        Assert.Equal(MouseTriggerButton.XButton2, r.Magnifier.MouseButton);
        Assert.Equal("Ctrl+Shift+F10", r.Hotkeys.ShowControlPanel);
    }

    [Fact]
    public void Imported_values_are_applied_even_if_this_pc_changed_them_meanwhile()
    {
        // 読み込んだ項目は、確定済みの値と同じでも編集として扱う（後から確定済みの値が変わっても読み込んだ値にする）。
        var (current, edit) = ThisPc();
        edit.Import(new AppSettings(), includeHotkeys: false, includeMouseButtons: false);
        current.Laser.Width = 30;
        Assert.Equal(new AppSettings().Laser.Width, edit.Build(current, false, KeyDisplayMode.Off).Settings.Laser.Width);
    }
}
