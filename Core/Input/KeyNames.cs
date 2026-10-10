using System.Globalization;
using PresenterTools.Core.Localization;

namespace PresenterTools.Core.Input;

/// <summary>
/// 仮想キーの名前。ショートカット文字列（設定ファイル）用のトークン名と、
/// Key Display 用の表示ラベルの 2 種類を持つ。
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<int, string> TokenNames = BuildTokenNames();
    private static readonly Dictionary<string, int> TokenLookup = BuildTokenLookup();

    private static readonly Dictionary<int, string> DisplayOverrides = new()
    {
        [VirtualKeys.Back] = "Backspace",
        [VirtualKeys.Return] = "Enter",
        [VirtualKeys.Escape] = "Esc",
        [VirtualKeys.Prior] = "PageUp",
        [VirtualKeys.Next] = "PageDown",
        [VirtualKeys.Left] = "←",
        [VirtualKeys.Up] = "↑",
        [VirtualKeys.Right] = "→",
        [VirtualKeys.Down] = "↓",
        [VirtualKeys.Snapshot] = "PrtSc",
        [VirtualKeys.Apps] = "Menu",
        [VirtualKeys.Multiply] = "Num *",
        [VirtualKeys.Add] = "Num +",
        [VirtualKeys.Subtract] = "Num -",
        [VirtualKeys.Decimal] = "Num .",
        [VirtualKeys.Divide] = "Num /",
    };

    private static Dictionary<int, string> BuildTokenNames()
    {
        var map = new Dictionary<int, string>
        {
            [VirtualKeys.Back] = "Backspace",
            [VirtualKeys.Tab] = "Tab",
            [VirtualKeys.Return] = "Enter",
            [VirtualKeys.Pause] = "Pause",
            [VirtualKeys.Capital] = "CapsLock",
            [VirtualKeys.Escape] = "Esc",
            [VirtualKeys.Space] = "Space",
            [VirtualKeys.Prior] = "PageUp",
            [VirtualKeys.Next] = "PageDown",
            [VirtualKeys.End] = "End",
            [VirtualKeys.Home] = "Home",
            [VirtualKeys.Left] = "Left",
            [VirtualKeys.Up] = "Up",
            [VirtualKeys.Right] = "Right",
            [VirtualKeys.Down] = "Down",
            [VirtualKeys.Snapshot] = "PrintScreen",
            [VirtualKeys.Insert] = "Insert",
            [VirtualKeys.Delete] = "Delete",
            [VirtualKeys.Apps] = "Apps",
            [VirtualKeys.Multiply] = "NumMultiply",
            [VirtualKeys.Add] = "NumAdd",
            [VirtualKeys.Subtract] = "NumSubtract",
            [VirtualKeys.Decimal] = "NumDecimal",
            [VirtualKeys.Divide] = "NumDivide",
            [VirtualKeys.NumLock] = "NumLock",
            [VirtualKeys.Scroll] = "ScrollLock",
            [VirtualKeys.Kana] = "Kana",
            [VirtualKeys.Kanji] = "Kanji",
            [VirtualKeys.Convert] = "Convert",
            [VirtualKeys.NonConvert] = "NonConvert",
        };
        for (var vk = VirtualKeys.D0; vk <= VirtualKeys.D9; vk++) map[vk] = ((char)vk).ToString();
        for (var vk = VirtualKeys.A; vk <= VirtualKeys.Z; vk++) map[vk] = ((char)vk).ToString();
        for (var i = 0; i <= 9; i++) map[VirtualKeys.NumPad0 + i] = $"Num{i}";
        for (var i = 0; i < 24; i++) map[VirtualKeys.F1 + i] = $"F{i + 1}";
        return map;
    }

    private static Dictionary<string, int> BuildTokenLookup()
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in TokenNames) lookup[name] = vk;
        // 別名
        lookup["Return"] = VirtualKeys.Return;
        lookup["Escape"] = VirtualKeys.Escape;
        lookup["Del"] = VirtualKeys.Delete;
        lookup["Ins"] = VirtualKeys.Insert;
        lookup["PgUp"] = VirtualKeys.Prior;
        lookup["PgDn"] = VirtualKeys.Next;
        lookup["Back"] = VirtualKeys.Back;
        return lookup;
    }

    /// <summary>ショートカット文字列用の名前。表に無いキーは "VK_BA" 形式。</summary>
    public static string GetTokenName(int vk) =>
        TokenNames.TryGetValue(vk, out var name) ? name : $"VK_{vk:X2}";

    /// <summary>ショートカット文字列のキー名を仮想キーに変換する。</summary>
    public static bool TryParseToken(string token, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        if (TokenLookup.TryGetValue(token, out vk)) return true;
        if (token.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(token[3..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vk)
            && vk is > 0 and < 0xFF)
        {
            return true;
        }
        vk = 0;
        return false;
    }

    /// <summary>
    /// Key Display 用の表示ラベル。記号キー（OEM キー）は現在の配列での無修飾の文字（<paramref name="baseChar"/>）を使う。
    /// </summary>
    public static string GetDisplayLabel(int vk, char? baseChar = null)
    {
        if (ImeKeyLabel(vk) is { } ime) return ime;
        if (DisplayOverrides.TryGetValue(vk, out var label)) return label;
        if (TokenNames.TryGetValue(vk, out var name)) return name;
        if (baseChar is { } c && !char.IsControl(c) && !char.IsWhiteSpace(c))
            return char.ToUpperInvariant(c).ToString();
        return $"VK_{vk:X2}";
    }

    /// <summary>日本語キーボードの IME キーの表示ラベル（表示言語に従う。保存用のトークン名 Kana・Kanji 等とは別）。</summary>
    private static string? ImeKeyLabel(int vk) => vk switch
    {
        VirtualKeys.Kana => Strings.KeyKana,
        VirtualKeys.Kanji or VirtualKeys.OemAuto or VirtualKeys.OemEnlw => Strings.KeyHankakuZenkaku,
        VirtualKeys.Convert => Strings.KeyConvert,
        VirtualKeys.NonConvert => Strings.KeyNonConvert,
        VirtualKeys.OemAttn => Strings.KeyEisu,
        VirtualKeys.OemCopy => Strings.KeyKatakanaHiragana,
        _ => null,
    };
}
