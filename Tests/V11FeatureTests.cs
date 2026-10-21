using PresenterTools.Core.Input;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

/// <summary>V1.1 で追加した設定（初期化・パネルのショートカット表記・Cursor Highlight の形・Click Effect の追加表示）。</summary>
public sealed class V11FeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public V11FeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly HotkeyAction[] PanelActions =
    [
        HotkeyAction.ToggleMaster, HotkeyAction.ToggleCursorHighlight, HotkeyAction.ToggleClickEffect,
        HotkeyAction.ToggleLaser, HotkeyAction.LaserHold, HotkeyAction.ToggleMagnifier,
        HotkeyAction.ToggleKeyDisplay,
    ];

    // ---- 新しい既定値 ----

    [Fact]
    public void NewDefaults()
    {
        var s = new AppSettings();
        Assert.True(s.General.ShowShortcutsInPanel);
        Assert.Equal(CursorHighlightStyle.Ring, s.CursorHighlight.Style);
        Assert.Equal(0.35, s.CursorHighlight.FillOpacity);
        Assert.False(s.ClickEffect.ShowButtonLabel);
        Assert.False(s.ClickEffect.ShowWhileHeld);
        Assert.Equal("", s.Hotkeys.ToggleCursorHighlight);
        Assert.Equal("", s.Hotkeys.ToggleClickEffect);
        Assert.Empty(SettingsNormalizer.Normalize(s));
    }

    [Fact]
    public void V10SettingsFile_LoadsWithNewDefaultsAndNoCorrection()
    {
        // V1.0 の保存形式（新しい項目が無い）。
        File.WriteAllText(FilePath, """
        {
          "schemaVersion": 1,
          "general": { "masterEnabled": true, "alwaysOnTop": false, "hideOwnShortcutsInKeyDisplay": true },
          "hotkeys": { "toggleMaster": "Ctrl+Shift+F9", "laserHold": "Ctrl+Shift+F1" },
          "cursorHighlight": { "enabled": true, "color": "#FFD400", "opacity": 0.8, "diameter": 64, "thickness": 5 },
          "clickEffect": { "enabled": true, "leftColor": "#2F80FF", "rightColor": "#FF8C00", "size": 80, "durationMs": 400 }
        }
        """);
        var result = new SettingsStore(FilePath).Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Corrections);
        Assert.False(result.Settings.General.AlwaysOnTop);
        Assert.True(result.Settings.General.ShowShortcutsInPanel);
        Assert.Equal(CursorHighlightStyle.Ring, result.Settings.CursorHighlight.Style);
        Assert.Equal("", result.Settings.Hotkeys.ToggleCursorHighlight);
    }

    [Fact]
    public void NewSettings_RoundTrip()
    {
        var store = new SettingsStore(FilePath);
        var s = new AppSettings();
        s.General.ShowShortcutsInPanel = false;
        s.CursorHighlight.Style = CursorHighlightStyle.FilledCircle;
        s.CursorHighlight.FillOpacity = 0.5;
        s.ClickEffect.ShowButtonLabel = true;
        s.ClickEffect.ShowWhileHeld = true;
        s.Hotkeys.ToggleCursorHighlight = "Ctrl+Shift+F5";
        s.Hotkeys.ToggleClickEffect = "Ctrl+Shift+F6";
        store.Save(s);

        Assert.Contains("\"style\": \"FilledCircle\"", File.ReadAllText(FilePath));
        var r = store.Load().Settings;
        Assert.False(r.General.ShowShortcutsInPanel);
        Assert.Equal(CursorHighlightStyle.FilledCircle, r.CursorHighlight.Style);
        Assert.Equal(0.5, r.CursorHighlight.FillOpacity);
        Assert.True(r.ClickEffect.ShowButtonLabel);
        Assert.True(r.ClickEffect.ShowWhileHeld);
        Assert.Equal("Ctrl+Shift+F5", r.Hotkeys.ToggleCursorHighlight);
        Assert.Equal("Ctrl+Shift+F6", r.Hotkeys.ToggleClickEffect);
    }

    [Fact]
    public void Normalize_FixesInvalidStyleAndFillOpacity()
    {
        File.WriteAllText(FilePath, """{ "cursorHighlight": { "style": "Star", "fillOpacity": 5 } }""");
        var result = new SettingsStore(FilePath).Load();
        Assert.Contains("cursorHighlight.style", result.Corrections);
        Assert.Contains("cursorHighlight.fillOpacity", result.Corrections);
        Assert.Equal(CursorHighlightStyle.Ring, result.Settings.CursorHighlight.Style);
        Assert.Equal(SettingsNormalizer.MaxFillOpacity, result.Settings.CursorHighlight.FillOpacity);
    }

    [Fact]
    public void NewHotkeys_DuplicateWithExistingIsDetectedAndFixed()
    {
        var h = new HotkeySettings { ToggleMaster = "Ctrl+Shift+F9", ToggleCursorHighlight = "Ctrl+Shift+F9" }; // Master と重複
        Assert.Contains(HotkeyBindings.Validate(h), i => i.Action == HotkeyAction.ToggleCursorHighlight);

        var s = new AppSettings { Hotkeys = h };
        var fixes = SettingsNormalizer.Normalize(s);
        Assert.Contains("hotkeys.ToggleCursorHighlight", fixes);
        Assert.Equal("Ctrl+Shift+F9", s.Hotkeys.ToggleMaster);
        Assert.Equal("", s.Hotkeys.ToggleCursorHighlight); // 既定値（未割当）へ
    }

    // ---- 初期化 ----

    private static AppSettings Customized()
    {
        var s = new AppSettings();
        s.General.MasterEnabled = false;
        s.General.AlwaysOnTop = false;
        s.General.HideOwnShortcutsInKeyDisplay = false;
        s.General.ShowShortcutsInPanel = false;
        s.Hotkeys.ToggleMaster = "Ctrl+Alt+M";
        s.CursorHighlight.Enabled = false;
        s.CursorHighlight.Style = CursorHighlightStyle.FilledCircle;
        s.CursorHighlight.Diameter = 150;
        s.ClickEffect.Enabled = false;
        s.ClickEffect.Size = 200;
        s.ClickEffect.ShowWhileHeld = true;
        s.Laser.Enabled = false;
        s.Laser.Width = 30;
        s.Laser.MouseButton = MouseTriggerButton.XButton1;
        s.Magnifier.ZoomPercent = 400;
        s.Magnifier.MouseButton = MouseTriggerButton.XButton2;
        s.KeyDisplay.FontSize = 90;
        s.ControlPanel.Left = 100;
        s.ControlPanel.Top = 200;
        s.ControlPanel.Width = 400;
        return s;
    }

    [Fact]
    public void ResetSection_CursorHighlight_KeepsOnOffAndOtherTabs()
    {
        var s = Customized();
        SettingsReset.ResetSection(s, SettingsSection.CursorHighlight);
        Assert.False(s.CursorHighlight.Enabled);                                 // ON/OFF は残す
        Assert.Equal(CursorHighlightStyle.Ring, s.CursorHighlight.Style);        // 既定値へ
        Assert.Equal(64, s.CursorHighlight.Diameter);
        Assert.Equal(200, s.ClickEffect.Size);                                   // 他のタブはそのまま
        Assert.Equal("Ctrl+Alt+M", s.Hotkeys.ToggleMaster);                      // ショートカットもそのまま
    }

    [Theory]
    [InlineData(SettingsSection.ClickEffect)]
    [InlineData(SettingsSection.Laser)]
    [InlineData(SettingsSection.Magnifier)]
    [InlineData(SettingsSection.KeyDisplay)]
    [InlineData(SettingsSection.General)]
    public void ResetSection_EachTab(SettingsSection section)
    {
        var s = Customized();
        SettingsReset.ResetSection(s, section);
        var d = new AppSettings();
        switch (section)
        {
            case SettingsSection.ClickEffect:
                Assert.False(s.ClickEffect.Enabled);
                Assert.Equal(d.ClickEffect.Size, s.ClickEffect.Size);
                Assert.False(s.ClickEffect.ShowWhileHeld);
                Assert.Equal(150, s.CursorHighlight.Diameter);
                break;
            case SettingsSection.Laser:
                Assert.False(s.Laser.Enabled);
                Assert.Equal(d.Laser.Width, s.Laser.Width);
                Assert.Equal(MouseTriggerButton.None, s.Laser.MouseButton);
                Assert.Equal(MouseTriggerButton.XButton2, s.Magnifier.MouseButton);
                break;
            case SettingsSection.Magnifier:
                Assert.Equal(300, s.Magnifier.ZoomPercent);
                Assert.Equal(MouseTriggerButton.None, s.Magnifier.MouseButton);
                Assert.Equal(MouseTriggerButton.XButton1, s.Laser.MouseButton);
                break;
            case SettingsSection.KeyDisplay:
                Assert.Equal(d.KeyDisplay.FontSize, s.KeyDisplay.FontSize);
                break;
            case SettingsSection.General:
                Assert.False(s.General.MasterEnabled); // Master の ON/OFF は残す
                Assert.True(s.General.AlwaysOnTop);
                Assert.True(s.General.HideOwnShortcutsInKeyDisplay);
                Assert.True(s.General.ShowShortcutsInPanel);
                Assert.Equal("Ctrl+Alt+M", s.Hotkeys.ToggleMaster);
                break;
        }
        Assert.Equal(100, s.ControlPanel.Left); // パネル位置は常に残す
        Assert.Empty(SettingsNormalizer.Normalize(s));
    }

    [Fact]
    public void ResetHotkeys_OnlyHotkeys()
    {
        var s = Customized();
        SettingsReset.ResetHotkeys(s);
        Assert.Equal("", s.Hotkeys.ToggleMaster); // 既定（V1.5.0 から未割当）
        Assert.Equal("", s.Hotkeys.ToggleCursorHighlight);
        Assert.Equal(150, s.CursorHighlight.Diameter);
    }

    [Fact]
    public void ResetAll_ResetsEverythingExceptPanelPlacement()
    {
        var s = Customized();
        SettingsReset.ResetAll(s);
        Assert.True(s.General.MasterEnabled);
        Assert.True(s.CursorHighlight.Enabled);
        Assert.True(s.Laser.Enabled);
        Assert.Equal("", s.Hotkeys.ToggleMaster);
        Assert.Equal(64, s.CursorHighlight.Diameter);
        Assert.Equal(MouseTriggerButton.None, s.Laser.MouseButton);
        Assert.Equal(100, s.ControlPanel.Left);
        Assert.Equal(200, s.ControlPanel.Top);
        Assert.Equal(400, s.ControlPanel.Width);
        Assert.Empty(SettingsNormalizer.Normalize(s));
    }

    // ---- 操作パネルのショートカット表記 ----

    [Fact]
    public void Hints_AlwaysFullNotation()
    {
        // V1.2: 修飾キーが共通でも見出しにまとめず、各項目を正式な表記にする（D-30）。
        var h = new HotkeySettings { ToggleMaster = "Ctrl+Shift+F9", ShowControlPanel = "Ctrl+Shift+F10", ToggleLaser = "Ctrl+Shift+F4", LaserHold = "Ctrl+Shift+F1", ToggleMagnifier = "Ctrl+Alt+Z" };
        var hints = ShortcutHints.Build(HotkeyBindings.Parse(h), PanelActions);
        Assert.Equal("Ctrl+Shift+F9", hints[HotkeyAction.ToggleMaster]);
        Assert.Equal("Ctrl+Shift+F4", hints[HotkeyAction.ToggleLaser]);
        Assert.Equal("Ctrl+Shift+F1", hints[HotkeyAction.LaserHold]);
        Assert.Equal("Ctrl+Alt+Z", hints[HotkeyAction.ToggleMagnifier]);
        Assert.Equal("", hints[HotkeyAction.ToggleCursorHighlight]); // 未割当
        Assert.Equal("", hints[HotkeyAction.ShowControlPanel]);      // 表示対象外
    }

    [Fact]
    public void Hints_NoModifierKey()
    {
        var fkeys = new HotkeySettings();
        foreach (var a in HotkeyActionNames.All) fkeys.Set(a, "");
        fkeys.ToggleMaster = "F13";
        var hints = ShortcutHints.Build(HotkeyBindings.Parse(fkeys), PanelActions);
        Assert.Equal("F13", hints[HotkeyAction.ToggleMaster]);
        Assert.Equal("", hints[HotkeyAction.ToggleLaser]);
        Assert.Equal("", ShortcutHints.Empty[HotkeyAction.ToggleMaster]);
    }

    [Fact]
    public void Shortcut_KeyTextAndModifiers()
    {
        Assert.True(Shortcut.TryParse("Win+Alt+Ctrl+K", out var sc));
        Assert.Equal("K", sc.KeyText);
        Assert.Equal("Ctrl+Alt+Win", Shortcut.FormatModifiers(sc.Modifiers));
        Assert.Equal("Ctrl+Alt+Win+K", sc.ToString());
        Assert.Equal("", Shortcut.None.KeyText);
        Assert.Equal("", Shortcut.FormatModifiers(KeyModifiers.None));
    }
}
