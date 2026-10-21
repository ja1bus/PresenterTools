using PresenterTools.Core.Input;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

public sealed class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F9", KeyModifiers.Control | KeyModifiers.Shift, 0x78)]
    [InlineData("ctrl + alt + m", KeyModifiers.Control | KeyModifiers.Alt, 0x4D)]
    [InlineData("Win+Shift+1", KeyModifiers.Win | KeyModifiers.Shift, 0x31)]
    [InlineData("F13", KeyModifiers.None, 0x7C)]
    [InlineData("Control+PgDn", KeyModifiers.Control, 0x22)]
    [InlineData("Ctrl+VK_BA", KeyModifiers.Control, 0xBA)]
    public void Parse_Valid(string text, KeyModifiers mods, int vk)
    {
        Assert.True(Shortcut.TryParse(text, out var sc));
        Assert.Equal(new Shortcut(mods, vk), sc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Hyper+A")]
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("Ctrl+VK_ZZ")]
    public void Parse_Invalid(string? text) => Assert.False(Shortcut.TryParse(text, out _));

    [Fact]
    public void ToString_UsesCanonicalOrder()
    {
        var sc = new Shortcut(KeyModifiers.Win | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control, 0x70);
        Assert.Equal("Ctrl+Alt+Shift+Win+F1", sc.ToString());
        Assert.Equal("", Shortcut.None.ToString());
        Assert.True(Shortcut.TryParse(sc.ToString(), out var back));
        Assert.Equal(sc, back);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F1", ShortcutValidation.Ok)]
    [InlineData("Alt+Q", ShortcutValidation.Ok)]
    [InlineData("F13", ShortcutValidation.Ok)]
    [InlineData("Shift+F24", ShortcutValidation.Ok)]
    [InlineData("F5", ShortcutValidation.RequiresCommandModifier)]
    [InlineData("Shift+A", ShortcutValidation.RequiresCommandModifier)]
    [InlineData("Space", ShortcutValidation.RequiresCommandModifier)]
    public void Validate(string text, ShortcutValidation expected)
    {
        Assert.True(Shortcut.TryParse(text, out var sc));
        Assert.Equal(expected, sc.Validate());
    }

    [Fact]
    public void Validate_EmptyAndModifierOnly()
    {
        Assert.Equal(ShortcutValidation.Empty, Shortcut.None.Validate());
        Assert.Equal(ShortcutValidation.ModifierOnly, new Shortcut(KeyModifiers.Control, VirtualKeys.LShift).Validate());
    }

    [Fact]
    public void HotkeyBindings_DetectsDuplicatesAndInvalid()
    {
        var h = new HotkeySettings
        {
            ToggleMaster = "Ctrl+Shift+F9",
            ShowControlPanel = "ctrl+shift+f9",
            ToggleLaser = "Q",
            LaserHold = "???",
            ToggleMagnifier = "",
        };
        var issues = HotkeyBindings.Validate(h);
        Assert.Contains(issues, i => i.Action == HotkeyAction.ShowControlPanel && i.Message.Contains("Master ON/OFF"));
        Assert.Contains(issues, i => i.Action == HotkeyAction.ToggleLaser);
        Assert.Contains(issues, i => i.Action == HotkeyAction.LaserHold);
        Assert.DoesNotContain(issues, i => i.Action == HotkeyAction.ToggleMagnifier);
        Assert.DoesNotContain(issues, i => i.Action == HotkeyAction.ToggleMaster);
    }

    [Fact]
    public void HotkeyBindings_ParseSkipsUnassigned()
    {
        var h = new HotkeySettings { ToggleMaster = "Ctrl+Shift+F9", ShowControlPanel = "Ctrl+Shift+F10", ToggleLaser = "Ctrl+Shift+F4", ToggleMagnifier = "" };
        var map = HotkeyBindings.Parse(h);
        Assert.False(map.ContainsKey(HotkeyAction.ToggleMagnifier));
        Assert.Equal(new Shortcut(KeyModifiers.Control | KeyModifiers.Shift, 0x78), map[HotkeyAction.ToggleMaster]);
        Assert.Equal(3, map.Count); // Master・操作パネル・Laser ON/OFF
    }

    [Fact]
    public void HotkeySettings_GetSetCoversAllActions()
    {
        var h = new HotkeySettings();
        foreach (var action in HotkeyActionNames.All)
        {
            h.Set(action, "Ctrl+Alt+" + (char)('A' + (int)action));
            Assert.Equal("Ctrl+Alt+" + (char)('A' + (int)action), h.Get(action));
            Assert.False(string.IsNullOrEmpty(HotkeyActionNames.GetDisplayName(action)));
        }
    }

    [Theory]
    [InlineData(0x09, null, "Tab")]
    [InlineData(0x0D, null, "Enter")]
    [InlineData(0x1B, null, "Esc")]
    [InlineData(0x08, null, "Backspace")]
    [InlineData(0x2E, null, "Delete")]
    [InlineData(0x25, null, "←")]
    [InlineData(0x28, null, "↓")]
    [InlineData(0x21, null, "PageUp")]
    [InlineData(0x7B, null, "F12")]
    [InlineData(0x43, null, "C")]
    [InlineData(0xBA, ':', ":")]
    [InlineData(0xC0, '@', "@")]
    [InlineData(0xBA, null, "VK_BA")]
    public void KeyNames_DisplayLabel(int vk, char? baseChar, string expected)
    {
        // 言語に依存しないラベルは、表示言語が英語でも日本語でも同じ。
        foreach (var language in new[] { "en", "ja" })
        {
            using var _ = new UiCultureScope(language);
            Assert.Equal(expected, KeyNames.GetDisplayLabel(vk, baseChar));
        }
    }

    /// <summary>IME キーのラベルは表示言語に従う。保存用のトークン名（Kanji 等）は言語にかかわらず同じ。</summary>
    [Theory]
    [InlineData(0x19, "en", "Hankaku/Zenkaku")]
    [InlineData(0x19, "ja", "半角/全角")]
    [InlineData(0xF3, "ja", "半角/全角")]
    [InlineData(0x1C, "en", "Henkan")]
    [InlineData(0x1C, "ja", "変換")]
    [InlineData(0x1D, "en", "Muhenkan")]
    [InlineData(0x1D, "ja", "無変換")]
    [InlineData(0x15, "ja", "かな")]
    [InlineData(0x19, "de-DE", "Hankaku/Zenkaku")] // 日本語以外は英語へフォールバック
    public void KeyNames_ImeLabel_follows_ui_language(int vk, string language, string expected)
    {
        using var _ = new UiCultureScope(language);
        Assert.Equal(expected, KeyNames.GetDisplayLabel(vk));
    }

    [Theory]
    [InlineData(0x19, "Kanji")]
    [InlineData(0x1C, "Convert")]
    [InlineData(0x1D, "NonConvert")]
    [InlineData(0x15, "Kana")]
    public void KeyNames_Token_does_not_depend_on_language(int vk, string token)
    {
        foreach (var language in new[] { "en", "ja" })
        {
            using var _ = new UiCultureScope(language);
            Assert.Equal(token, KeyNames.GetTokenName(vk));
            Assert.Equal("Ctrl+Shift+" + token, new Shortcut(KeyModifiers.Control | KeyModifiers.Shift, vk).ToString());
        }
    }
}
