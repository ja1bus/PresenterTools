using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

/// <summary>ショートカット設定の 1 件分の検証結果。</summary>
public sealed record HotkeyBindingIssue(HotkeyAction Action, string Message);

/// <summary>
/// ショートカット設定（文字列）を解析済みの割当表に変換し、Settings 画面の入力を検証する。
/// </summary>
public static class HotkeyBindings
{
    /// <summary>割当済み（空でない・解析できる）ものだけを返す。</summary>
    public static IReadOnlyDictionary<HotkeyAction, Shortcut> Parse(HotkeySettings hotkeys)
    {
        var map = new Dictionary<HotkeyAction, Shortcut>();
        foreach (var action in HotkeyActionNames.All)
        {
            if (Shortcut.TryParse(hotkeys.Get(action), out var sc)) map[action] = sc;
        }
        return map;
    }

    /// <summary>入力規則・重複を検証する。問題が無ければ空。</summary>
    public static IReadOnlyList<HotkeyBindingIssue> Validate(HotkeySettings hotkeys)
    {
        var issues = new List<HotkeyBindingIssue>();
        var owners = new Dictionary<Shortcut, HotkeyAction>();
        foreach (var action in HotkeyActionNames.All)
        {
            var raw = hotkeys.Get(action);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Shortcut.TryParse(raw, out var sc))
            {
                issues.Add(new HotkeyBindingIssue(action, string.Format(Strings.HotkeyParseErrorFormat, raw)));
                continue;
            }
            var result = sc.Validate();
            if (result != ShortcutValidation.Ok)
            {
                issues.Add(new HotkeyBindingIssue(action, Shortcut.DescribeValidation(result)));
                continue;
            }
            if (owners.TryGetValue(sc, out var other))
            {
                issues.Add(new HotkeyBindingIssue(action, string.Format(Strings.HotkeyDuplicateFormat, HotkeyActionNames.GetDisplayName(other))));
                continue;
            }
            owners[sc] = action;
        }
        return issues;
    }
}
