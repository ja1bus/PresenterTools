using PresenterTools.Core.Localization;

namespace PresenterTools.Core.Input;

public enum ShortcutValidation
{
    Ok,
    Empty,
    ModifierOnly,
    RequiresCommandModifier,
}

/// <summary>
/// グローバルショートカット（修飾キー＋主キー）。文字列表記は "Ctrl+Shift+F9"。
/// Key が 0 のものは未割当を表す。
/// </summary>
public readonly record struct Shortcut(KeyModifiers Modifiers, int Key)
{
    public static readonly Shortcut None = default;

    public bool IsEmpty => Key == 0;

    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = ParseModifier(parts[i]);
            if (m == KeyModifiers.None || modifiers.HasFlag(m)) return false;
            modifiers |= m;
        }

        var last = parts[^1];
        if (ParseModifier(last) != KeyModifiers.None) return false; // 修飾キーだけ
        if (!KeyNames.TryParseToken(last, out var vk) || VirtualKeys.IsModifier(vk)) return false;

        shortcut = new Shortcut(modifiers, vk);
        return true;
    }

    private static KeyModifiers ParseModifier(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => KeyModifiers.Control,
        "ALT" => KeyModifiers.Alt,
        "SHIFT" => KeyModifiers.Shift,
        "WIN" or "WINDOWS" => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// 規則（SPEC §11.2）: 主キーが必要。Ctrl・Alt・Win のいずれかを含む（F13～F24 は例外）。
    /// </summary>
    public ShortcutValidation Validate()
    {
        if (IsEmpty) return ShortcutValidation.Empty;
        if (VirtualKeys.IsModifier(Key)) return ShortcutValidation.ModifierOnly;
        var hasCommand = (Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        if (!hasCommand && Key is not (>= VirtualKeys.F13 and <= VirtualKeys.F24))
            return ShortcutValidation.RequiresCommandModifier;
        return ShortcutValidation.Ok;
    }

    public static string DescribeValidation(ShortcutValidation result) => result switch
    {
        ShortcutValidation.Ok => "",
        ShortcutValidation.Empty => Strings.ShortcutUnassigned,
        ShortcutValidation.ModifierOnly => Strings.ShortcutModifierOnly,
        ShortcutValidation.RequiresCommandModifier => Strings.ShortcutRequiresModifier,
        _ => result.ToString(),
    };

    /// <summary>"Ctrl+Alt+Shift+Win+Key" の順で表記する。未割当は空文字。</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var modifiers = FormatModifiers(Modifiers);
        var key = KeyNames.GetTokenName(Key);
        return modifiers.Length == 0 ? key : modifiers + "+" + key;
    }

    /// <summary>主キーだけの表記（"F9"）。</summary>
    public string KeyText => IsEmpty ? "" : KeyNames.GetTokenName(Key);

    /// <summary>修飾キーを "Ctrl+Alt+Shift+Win" の順で表記する。無ければ空文字。</summary>
    public static string FormatModifiers(KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }
}
