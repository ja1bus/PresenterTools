using PresenterTools.Core.Input;
using PresenterTools.Core.KeyDisplay;
using PresenterTools.Core.Models;

namespace PresenterTools.Tests;

public sealed class KeyDisplayModelTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;

    private static KeyDisplayModel NewModel(KeyDisplayMode mode, params Shortcut[] suppressed)
    {
        var model = new KeyDisplayModel(new KeyDisplayOptions
        {
            ChordDuration = TimeSpan.FromSeconds(2),
            TextDuration = TimeSpan.FromSeconds(5),
            EnterHoldDuration = TimeSpan.FromSeconds(1.5),
            MaxTextLength = 20,
            SuppressedShortcuts = suppressed.ToHashSet(),
        });
        model.SetMode(mode);
        return model;
    }

    private static KeyInput Down(int vk, KeyModifiers mods = KeyModifiers.None, string? text = null, char? baseChar = null) =>
        new(vk, true, mods, text, baseChar);

    private static KeyInput Up(int vk) => new(vk, false, KeyModifiers.None);

    private static void Type(KeyDisplayModel m, string text, TimeSpan now)
    {
        foreach (var c in text)
        {
            // 記号は OEM キー（0xBD）として送る。'-' や '.' の文字コードは VK_INSERT / VK_DELETE と重なるため。
            var vk = char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : c == ' ' ? VirtualKeys.Space : 0xBD;
            m.Process(Down(vk, char.IsUpper(c) ? Shift : KeyModifiers.None, c.ToString()), now);
            m.Process(Up(vk), now);
        }
    }

    [Fact]
    public void FullText_AppendsTypedCharacters()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "ip addr show", T0);
        Assert.Equal("ip addr show", m.DisplayText);
        Assert.Empty(m.ChordKeys);
    }

    [Fact]
    public void FullText_Backspace_RemovesLastCharacter()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "ls -lx", T0);
        Assert.True(m.Process(Down(VirtualKeys.Back), T0));
        Assert.Equal("ls -l", m.DisplayText);
        Assert.Empty(m.ChordKeys); // Backspace は Chord にしない

        for (var i = 0; i < 10; i++) m.Process(Down(VirtualKeys.Back), T0);
        Assert.Equal("", m.DisplayText);
        Assert.False(m.Process(Down(VirtualKeys.Back), T0)); // 空なら変化なし
    }

    [Fact]
    public void FullText_Backspace_RemovesSurrogatePair()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        m.Process(Down(0x41, text: "a"), T0);
        m.Process(Down(0x42, text: "😀"), T0);
        m.Process(Down(VirtualKeys.Back), T0);
        Assert.Equal("a", m.DisplayText);
    }

    [Fact]
    public void FullText_Enter_ShowsMarkAndClearsAfterHold()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "show run", T0);
        Assert.True(m.Process(Down(VirtualKeys.Return, text: "\r"), T0));
        Assert.Equal("show run ↵", m.DisplayText);
        Assert.Equal(T0 + TimeSpan.FromSeconds(1.5), m.NextDeadline);

        Assert.False(m.Tick(T0 + TimeSpan.FromSeconds(1.4)));
        Assert.Equal("show run ↵", m.DisplayText);
        Assert.True(m.Tick(T0 + TimeSpan.FromSeconds(1.5)));
        Assert.Equal("", m.DisplayText);
        Assert.True(m.IsEmpty);
    }

    [Fact]
    public void FullText_TypingAfterEnter_StartsNewLine()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "ls", T0);
        m.Process(Down(VirtualKeys.Return, text: "\r"), T0);
        Type(m, "pwd", T0 + TimeSpan.FromSeconds(0.5));
        Assert.Equal("pwd", m.DisplayText);
    }

    [Fact]
    public void FullText_EnterOnEmptyLine_ShowsChord()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        m.Process(Down(VirtualKeys.Return, text: "\r"), T0);
        Assert.Equal(["Enter"], m.ChordKeys);
        Assert.Equal("", m.DisplayText);
    }

    [Fact]
    public void FullText_TextExpiresAfterIdle()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "ping", T0);
        Assert.False(m.Tick(T0 + TimeSpan.FromSeconds(4.9)));
        Assert.True(m.Tick(T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal("", m.DisplayText);
    }

    [Fact]
    public void FullText_ShortcutShownAsChordAndKeepsText()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "ping 8.8", T0);
        m.Process(Down(0x43, Ctrl, baseChar: 'c'), T0 + TimeSpan.FromSeconds(4));
        Assert.Equal(["Ctrl", "C"], m.ChordKeys);
        Assert.Equal("ping 8.8", m.DisplayText);
        // Chord の入力で文字列の保持も延長される（Chord は T0+6 で消え、文字列は残る）
        Assert.True(m.Tick(T0 + TimeSpan.FromSeconds(6)));
        Assert.Empty(m.ChordKeys);
        Assert.Equal("ping 8.8", m.DisplayText);
    }

    [Fact]
    public void FullText_TabIsChordAndDoesNotChangeText()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "ip a", T0);
        m.Process(Down(VirtualKeys.Tab, text: "\t"), T0);
        Assert.Equal("ip a", m.DisplayText);
        Assert.Equal(["Tab"], m.ChordKeys);
    }

    [Fact]
    public void FullText_TruncatesWithEllipsis()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "abcdefghijklmnopqrstuvwxyz", T0);
        Assert.Equal("…ghijklmnopqrstuvwxyz", m.DisplayText);
    }

    [Fact]
    public void FullText_ImeKeysAreIgnored()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Assert.False(m.Process(Down(VirtualKeys.Kanji), T0));
        Assert.True(m.IsEmpty);
    }

    [Theory]
    [InlineData(0x43, KeyModifiers.Control, "Ctrl + C")]
    [InlineData(0x5A, KeyModifiers.Control, "Ctrl + Z")]
    [InlineData(0x54, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl + Shift + T")]
    [InlineData(0x09, KeyModifiers.None, "Tab")]
    [InlineData(0x09, KeyModifiers.Shift, "Shift + Tab")]
    [InlineData(0x0D, KeyModifiers.None, "Enter")]
    [InlineData(0x1B, KeyModifiers.None, "Esc")]
    [InlineData(0x2E, KeyModifiers.None, "Delete")]
    [InlineData(0x24, KeyModifiers.None, "Home")]
    [InlineData(0x23, KeyModifiers.None, "End")]
    [InlineData(0x21, KeyModifiers.None, "PageUp")]
    [InlineData(0x22, KeyModifiers.None, "PageDown")]
    [InlineData(0x26, KeyModifiers.None, "↑")]
    [InlineData(0x74, KeyModifiers.None, "F5")]
    [InlineData(0x09, KeyModifiers.Alt, "Alt + Tab")]
    [InlineData(0x45, KeyModifiers.Win, "Win + E")]
    public void ShowsChords(int vk, KeyModifiers mods, string expected)
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Assert.True(m.Process(Down(vk, mods), T0));
        Assert.Equal(expected, m.ChordLabel);
    }

    [Fact]
    public void Chord_OemKeyUsesBaseCharacter()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        m.Process(Down(0xBB, Ctrl, baseChar: ';'), T0);
        Assert.Equal("Ctrl + ;", m.ChordLabel);
    }

    [Fact]
    public void Chord_RepeatIncrementsCount_AndExpires()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        m.Process(Down(VirtualKeys.Down), T0);
        m.Process(Down(VirtualKeys.Down), T0 + TimeSpan.FromSeconds(0.5));
        m.Process(Down(VirtualKeys.Down), T0 + TimeSpan.FromSeconds(1));
        Assert.Equal(3, m.ChordCount);

        m.Process(Down(VirtualKeys.Up), T0 + TimeSpan.FromSeconds(1.2));
        Assert.Equal(1, m.ChordCount);

        Assert.True(m.Tick(T0 + TimeSpan.FromSeconds(3.2)));
        Assert.True(m.IsEmpty);
        Assert.Null(m.NextDeadline);
    }

    [Fact]
    public void Chord_AfterExpiryCountRestarts()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        m.Process(Down(VirtualKeys.Tab), T0);
        m.Process(Down(VirtualKeys.Tab), T0 + TimeSpan.FromSeconds(3));
        Assert.Equal(1, m.ChordCount);
    }

    [Fact]
    public void ModifierOnly_IsNotShown()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Assert.False(m.Process(Down(VirtualKeys.LControl), T0));
        Assert.False(m.Process(Down(VirtualKeys.LShift, Ctrl), T0));
        Assert.False(m.Process(Down(VirtualKeys.LWin), T0));
        Assert.True(m.IsEmpty);
    }

    [Fact]
    public void OwnShortcut_IsSuppressedIncludingRepeats()
    {
        var own = new Shortcut(Ctrl | Shift, 0x70);
        var m = NewModel(KeyDisplayMode.FullText, own);
        Assert.False(m.Process(Down(0x70, Ctrl | Shift), T0));
        Assert.False(m.Process(Down(0x70, Ctrl | Shift), T0)); // 自動リピート
        Assert.False(m.Process(Down(0x70, Ctrl), T0));          // Shift を先に離しても押しっぱなしの間は無視
        m.Process(Up(0x70), T0);
        Assert.True(m.IsEmpty);

        // 修飾キーが違えば通常どおり表示する。
        Assert.True(m.Process(Down(0x70), T0));
        Assert.Equal("F1", m.ChordLabel);
    }

    [Fact]
    public void SetMode_ClearsDisplay()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Type(m, "secret", T0);
        m.Process(Down(0x43, Ctrl), T0);
        Assert.False(m.IsEmpty);

        m.SetMode(KeyDisplayMode.Off);
        Assert.True(m.IsEmpty);
        Assert.Equal("", m.DisplayText);
        Assert.False(m.Process(Down(0x41, text: "a"), T0)); // Off では何もしない
    }

    [Fact]
    public void ControlCharacterText_IsNotAppended()
    {
        var m = NewModel(KeyDisplayMode.FullText);
        Assert.False(m.Process(Down(0x90, text: "\u001b"), T0));
        Assert.False(m.Process(Down(0x14), T0)); // CapsLock（文字なし）
        Assert.True(m.IsEmpty);
    }
}
