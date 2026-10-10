using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

/// <summary>設定の表示言語 general.language（V1.3.1。SPEC §34・D-39）。</summary>
public sealed class LanguageSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SettingsLoadResult LoadJson(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path).Load();
    }

    [Fact]
    public void Default_is_follow_windows()
    {
        Assert.Equal(UiLanguage.System, new AppSettings().General.Language);
    }

    [Fact]
    public void Old_file_without_language_follows_windows_without_correction()
    {
        var result = LoadJson("""{ "schemaVersion": 1, "general": { "masterEnabled": false } }""");
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal("", result.Settings.General.Language);
        Assert.Empty(result.Corrections);
        Assert.False(result.Settings.General.MasterEnabled);
    }

    [Theory]
    [InlineData("en", "en", false)]
    [InlineData("ja", "ja", false)]
    [InlineData(" JA ", "ja", false)] // 大文字・空白は黙って直す
    [InlineData("fr", "", true)]       // 知らない値は Windows に合わせる（補正として記録）
    public void Language_is_normalized_on_load(string stored, string expected, bool corrected)
    {
        var result = LoadJson($$"""{ "schemaVersion": 1, "general": { "language": "{{stored}}" } }""");
        Assert.Equal(expected, result.Settings.General.Language);
        Assert.Equal(corrected, result.Corrections.Contains("general.language"));
    }

    [Fact]
    public void Language_is_saved_and_loaded()
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var s = new AppSettings();
        s.General.Language = UiLanguage.English;
        store.Save(s);
        Assert.Contains("\"language\": \"en\"", File.ReadAllText(store.FilePath));
        Assert.Equal("en", store.Load().Settings.General.Language);
    }

    [Fact]
    public void Reset_general_tab_keeps_language_but_reset_all_clears_it()
    {
        var s = new AppSettings();
        s.General.Language = UiLanguage.Japanese;
        s.General.AlwaysOnTop = false;
        SettingsReset.ResetSection(s, SettingsSection.General);
        Assert.Equal("ja", s.General.Language);
        Assert.True(s.General.AlwaysOnTop);

        SettingsReset.ResetAll(s);
        Assert.Equal("", s.General.Language);
    }

    [Fact]
    public void Revert_general_tab_restores_language_when_opened()
    {
        var original = new AppSettings();
        original.General.Language = UiLanguage.English;
        var s = original.Clone();
        s.General.Language = UiLanguage.Japanese;
        SettingsReset.RevertSection(s, original, SettingsSection.General);
        Assert.Equal("en", s.General.Language);
    }

    [Fact]
    public void Edited_language_is_applied_but_tab_reset_does_not_force_it()
    {
        var opened = new AppSettings();
        var session = new SettingsEditSession(opened, false, KeyDisplayMode.Off);
        session.Settings.General.Language = UiLanguage.English;
        Assert.Equal("en", session.Build(opened, false, KeyDisplayMode.Off).Settings.General.Language);

        // 言語を編集せずに General を既定値に戻した場合、現在の言語（"ja"）を開いたときの値で上書きしない。
        var session2 = new SettingsEditSession(opened, false, KeyDisplayMode.Off);
        session2.ResetSection(SettingsSection.General);
        var current = opened.Clone();
        current.General.Language = UiLanguage.Japanese;
        Assert.Equal("ja", session2.Build(current, false, KeyDisplayMode.Off).Settings.General.Language);
    }
}
