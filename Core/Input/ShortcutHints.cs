using PresenterTools.Core.Models;

namespace PresenterTools.Core.Input;

/// <summary>
/// 操作パネルに添えるショートカットの表記（SPEC §10.1）。各項目を省略しない正式な表記（"Ctrl+Shift+F9"）にする
/// （V1.1 の修飾キーを見出しにまとめる表記は V1.2 で廃止。D-30）。
/// </summary>
public sealed class ShortcutHints
{
    private readonly Dictionary<HotkeyAction, string> _labels;

    private ShortcutHints(Dictionary<HotkeyAction, string> labels) => _labels = labels;

    public static readonly ShortcutHints Empty = new([]);

    /// <summary>項目の表記。未割当・表示対象外は空文字。</summary>
    public string this[HotkeyAction action] => _labels.GetValueOrDefault(action, "");

    /// <param name="assigned">割当済みのショートカット。</param>
    /// <param name="shown">パネルに表示する項目。</param>
    public static ShortcutHints Build(IReadOnlyDictionary<HotkeyAction, Shortcut> assigned, IEnumerable<HotkeyAction> shown) =>
        new(shown.Distinct()
            .Where(a => assigned.TryGetValue(a, out var sc) && !sc.IsEmpty)
            .ToDictionary(a => a, a => assigned[a].ToString()));
}
