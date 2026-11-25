using System.Text.Json.Nodes;
using PresenterTools.Core.Input;
using PresenterTools.Core.KeyDisplay;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

/// <summary>
/// V1.6.0: パスワード欄での自動非表示の設定（keyDisplay.hidePasswordInput）と、表示の消去・隠したキーの記録
/// （KeyDisplayModel.ClearDisplay・ObserveHidden。試験表 T-01 (16)(17) と設定のテスト）。
/// </summary>
public sealed class V16FeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public V16FeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- 設定 ----

    [Fact]
    public void Default_is_on()
    {
        Assert.True(new AppSettings().KeyDisplay.HidePasswordInput);
        Assert.Empty(SettingsNormalizer.Normalize(new AppSettings()));
    }

    [Fact]
    public void Settings_from_older_versions_turn_it_on_without_a_correction_notice()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings());
        var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.True(root["keyDisplay"]!.AsObject().Remove("hidePasswordInput"));
        File.WriteAllText(FilePath, root.ToJsonString());

        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Corrections);
        Assert.True(result.Settings.KeyDisplay.HidePasswordInput);
    }

    [Fact]
    public void Off_is_saved_and_read_back()
    {
        var store = new SettingsStore(FilePath);
        var s = new AppSettings();
        s.KeyDisplay.HidePasswordInput = false;
        store.Save(s);
        Assert.Contains("\"hidePasswordInput\": false", File.ReadAllText(FilePath));
        Assert.False(store.Load().Settings.KeyDisplay.HidePasswordInput);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_treated_as_a_broken_file()
    {
        var store = new SettingsStore(FilePath, () => new DateTime(2026, 11, 25, 10, 0, 0));
        store.Save(new AppSettings());
        var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        root["keyDisplay"]!["hidePasswordInput"] = "yes";
        File.WriteAllText(FilePath, root.ToJsonString());

        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Corrupted, result.Status);
        Assert.True(result.Settings.KeyDisplay.HidePasswordInput);
    }

    [Fact]
    public void Export_and_import_carry_the_value_and_old_exports_turn_it_on()
    {
        var s = new AppSettings();
        s.KeyDisplay.HidePasswordInput = false;
        var exported = SettingsTransfer.Parse(SettingsTransfer.Serialize(s, "1.6.0", DateTimeOffset.Now));
        Assert.False(exported.Settings!.KeyDisplay.HidePasswordInput);

        var edit = new SettingsEditSession(new AppSettings(), false, KeyDisplayMode.Off);
        edit.Import(exported.Settings, includeHotkeys: false, includeMouseButtons: false);
        Assert.False(edit.Build(new AppSettings(), false, KeyDisplayMode.Off).Settings.KeyDisplay.HidePasswordInput);

        var old = JsonNode.Parse(SettingsTransfer.Serialize(new AppSettings(), "1.5.2", DateTimeOffset.Now))!.AsObject();
        old["settings"]!["keyDisplay"]!.AsObject().Remove("hidePasswordInput");
        var fromOld = SettingsTransfer.Parse(old.ToJsonString());
        Assert.Empty(fromOld.Corrections);
        Assert.True(fromOld.Settings!.KeyDisplay.HidePasswordInput);
    }

    [Fact]
    public void Reset_turns_it_on_and_revert_restores_the_opened_value()
    {
        var current = new AppSettings();
        current.KeyDisplay.HidePasswordInput = false;
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);

        edit.ResetSection(SettingsSection.KeyDisplay);
        Assert.True(edit.Settings.KeyDisplay.HidePasswordInput);
        Assert.True(edit.Build(current, false, KeyDisplayMode.Off).Settings.KeyDisplay.HidePasswordInput);

        edit.RevertSection(SettingsSection.KeyDisplay);
        Assert.False(edit.Settings.KeyDisplay.HidePasswordInput);
    }

    [Fact]
    public void Changing_only_the_check_box_is_a_pending_change()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.FullText);
        Assert.False(edit.HasChanges(current, false, KeyDisplayMode.FullText));
        edit.Settings.KeyDisplay.HidePasswordInput = false;
        Assert.True(edit.HasChanges(current, false, KeyDisplayMode.FullText));
    }

    // ---- 表示の消去と隠したキー ----

    private static readonly Shortcut Own = new(KeyModifiers.Control | KeyModifiers.Shift, VirtualKeys.F1 + 9); // Ctrl+Shift+F10
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    private static KeyDisplayModel NewModel()
    {
        var m = new KeyDisplayModel(new KeyDisplayOptions { SuppressedShortcuts = new HashSet<Shortcut> { Own } });
        m.SetMode(KeyDisplayMode.FullText);
        return m;
    }

    private static KeyInput OwnDown => new(Own.Key, true, Own.Modifiers);
    private static KeyInput F10Down => new(Own.Key, true, KeyModifiers.None);
    private static KeyInput F10Up => new(Own.Key, false, KeyModifiers.None);

    [Fact]
    public void ClearDisplay_clears_text_and_keys_but_keeps_held_shortcuts_suppressed()
    {
        var m = NewModel();
        Assert.True(m.Process(new KeyInput('A', true, KeyModifiers.None, "a"), T0));
        Assert.False(m.Process(OwnDown, T0));     // 自身のショートカット（押したまま）
        Assert.True(m.Process(new KeyInput(VirtualKeys.Tab, true, KeyModifiers.None), T0));
        m.ClearDisplay();
        Assert.True(m.IsEmpty);
        Assert.Equal("", m.DisplayText);
        Assert.Equal(0, m.ChordCount);
        Assert.False(m.Process(F10Down, T0));     // 修飾キーを離した後の自動リピートも出ない
        Assert.True(m.IsEmpty);
    }

    [Fact]
    public void ObserveHidden_suppresses_the_repeat_of_a_hidden_shortcut_until_released()
    {
        var m = NewModel();
        m.ObserveHidden(OwnDown);                 // パスワード欄で押した（表示しない）
        Assert.True(m.IsEmpty);
        Assert.False(m.Process(F10Down, T0));     // 通常欄へ戻った後の自動リピート
        Assert.False(m.Process(F10Up, T0));
        Assert.True(m.Process(F10Down, T0));      // 離した後の押下は通常どおり
        Assert.Equal("F10", m.ChordLabel);
    }

    [Fact]
    public void ObserveHidden_ignores_other_keys_and_releases()
    {
        var m = NewModel();
        m.ObserveHidden(new KeyInput('Q', true, KeyModifiers.None, "Q"));
        m.ObserveHidden(F10Up);
        Assert.True(m.IsEmpty);
        Assert.True(m.Process(new KeyInput('Q', true, KeyModifiers.None, "Q"), T0));
        Assert.Equal("Q", m.DisplayText);
    }
}
