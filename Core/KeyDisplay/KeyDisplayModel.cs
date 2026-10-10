using PresenterTools.Core.Input;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.KeyDisplay;

/// <summary>Key Display の時間設定と除外するショートカット。</summary>
public sealed class KeyDisplayOptions
{
    public TimeSpan ChordDuration { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan TextDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan EnterHoldDuration { get; init; } = TimeSpan.FromSeconds(1.5);
    public int MaxTextLength { get; init; } = 80;
    /// <summary>表示しないショートカット（PresenterTools 自身の登録キー）。</summary>
    public IReadOnlySet<Shortcut> SuppressedShortcuts { get; init; } = new HashSet<Shortcut>();

    public static KeyDisplayOptions FromSettings(KeyDisplaySettings s, IEnumerable<Shortcut> suppressed) => new()
    {
        ChordDuration = TimeSpan.FromSeconds(s.ChordSeconds),
        TextDuration = TimeSpan.FromSeconds(s.TextSeconds),
        EnterHoldDuration = TimeSpan.FromSeconds(s.EnterHoldSeconds),
        SuppressedShortcuts = suppressed.ToHashSet(),
    };
}

/// <summary>
/// Key Display の表示内容（SPEC §15）。キーイベントを受けて、Full Text の文字列と
/// 特殊キー・ショートカット（Chord）の表示を管理する。時刻は単調増加の経過時間で渡す。
/// Win32・WPF に依存しないため単体テストできる。
/// </summary>
public sealed class KeyDisplayModel
{
    private const string EnterMark = " ↵";
    private const string Ellipsis = "…";

    private readonly System.Text.StringBuilder _text = new();
    private readonly HashSet<int> _suppressedDownKeys = [];
    private bool _committed;
    private TimeSpan _textExpiresAt;
    private string[] _chordKeys = [];
    private int _chordCount;
    private TimeSpan _chordExpiresAt;

    public KeyDisplayModel(KeyDisplayOptions? options = null)
    {
        Options = options ?? new KeyDisplayOptions();
    }

    public KeyDisplayOptions Options { get; set; }

    public KeyDisplayMode Mode { get; private set; } = KeyDisplayMode.Off;

    /// <summary>表示する文字列（省略記号・Enter 記号を含む）。空なら表示しない。</summary>
    public string DisplayText
    {
        get
        {
            if (_text.Length == 0) return "";
            var max = Math.Max(1, Options.MaxTextLength);
            var body = _text.Length > max ? Ellipsis + _text.ToString(_text.Length - max, max) : _text.ToString();
            return _committed ? body + EnterMark : body;
        }
    }

    /// <summary>Chord のキー（例: ["Ctrl", "Shift", "T"]）。空なら表示しない。</summary>
    public IReadOnlyList<string> ChordKeys => _chordKeys;

    /// <summary>同じ Chord を続けて押した回数（1 なら回数表示なし）。</summary>
    public int ChordCount => _chordCount;

    /// <summary>"Ctrl + Shift + T" 形式の表記。</summary>
    public string ChordLabel => string.Join(" + ", _chordKeys);

    public bool IsEmpty => _text.Length == 0 && _chordKeys.Length == 0;

    /// <summary>次に表示が変わる（時間切れになる）時刻。表示が無ければ null。</summary>
    public TimeSpan? NextDeadline
    {
        get
        {
            TimeSpan? next = null;
            if (_text.Length > 0) next = _textExpiresAt;
            if (_chordKeys.Length > 0 && (next is null || _chordExpiresAt < next)) next = _chordExpiresAt;
            return next;
        }
    }

    /// <summary>モードを切り替える。切り替えたときは表示中の内容を消去する。</summary>
    public bool SetMode(KeyDisplayMode mode)
    {
        if (Mode == mode) return false;
        Mode = mode;
        Clear();
        return true;
    }

    public void Clear()
    {
        _text.Clear();
        _committed = false;
        _chordKeys = [];
        _chordCount = 0;
        _suppressedDownKeys.Clear();
    }

    /// <summary>キーイベントを処理する。表示が変わったら true。</summary>
    public bool Process(in KeyInput input, TimeSpan now)
    {
        if (Mode == KeyDisplayMode.Off) return false;
        var vk = input.VirtualKey;

        if (!input.IsKeyDown)
        {
            _suppressedDownKeys.Remove(vk);
            return false;
        }
        if (_suppressedDownKeys.Contains(vk)) return false; // 除外したショートカットの自動リピート
        if (VirtualKeys.IsModifier(vk)) return false;
        if (Options.SuppressedShortcuts.Contains(new Shortcut(input.Modifiers, vk)))
        {
            _suppressedDownKeys.Add(vk);
            return false;
        }

        var hasCommand = (input.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        var isNamed = VirtualKeys.IsNamedSpecialKey(vk);

        if (!hasCommand)
        {
            if (vk == VirtualKeys.Back) return Backspace(now);
            if (vk == VirtualKeys.Return && _text.Length > 0 && !_committed && input.Modifiers == KeyModifiers.None)
            {
                _committed = true;
                _textExpiresAt = now + Options.EnterHoldDuration;
                return true;
            }
            if (VirtualKeys.IsImeKey(vk)) return false;
            if (!isNamed)
            {
                return IsPrintable(input.Text) && Append(input.Text!, now);
            }
        }

        ShowChord(input, now);
        return true;
    }

    /// <summary>時間切れの表示を消す。表示が変わったら true。</summary>
    public bool Tick(TimeSpan now)
    {
        var changed = false;
        if (_chordKeys.Length > 0 && now >= _chordExpiresAt)
        {
            _chordKeys = [];
            _chordCount = 0;
            changed = true;
        }
        if (_text.Length > 0 && now >= _textExpiresAt)
        {
            _text.Clear();
            _committed = false;
            changed = true;
        }
        return changed;
    }

    private bool Backspace(TimeSpan now)
    {
        if (_committed)
        {
            // 確定済み（Enter 後）の行は新しい行の入力が始まったものとして消す。
            _text.Clear();
            _committed = false;
            return true;
        }
        if (_text.Length == 0) return false;
        // サロゲートペアは 2 単位まとめて消す。
        var remove = _text.Length >= 2 && char.IsLowSurrogate(_text[^1]) && char.IsHighSurrogate(_text[^2]) ? 2 : 1;
        _text.Length -= remove;
        _textExpiresAt = now + Options.TextDuration;
        return true;
    }

    private bool Append(string text, TimeSpan now)
    {
        if (_committed)
        {
            _text.Clear();
            _committed = false;
        }
        _text.Append(text);
        // 無制限に伸びないよう、表示に必要な分（＋余裕）だけ残す。
        var keep = Math.Max(1, Options.MaxTextLength) * 2;
        if (_text.Length > keep) _text.Remove(0, _text.Length - keep);
        _textExpiresAt = now + Options.TextDuration;
        return true;
    }

    private void ShowChord(in KeyInput input, TimeSpan now)
    {
        var keys = new List<string>(5);
        if (input.Modifiers.HasFlag(KeyModifiers.Control)) keys.Add("Ctrl");
        if (input.Modifiers.HasFlag(KeyModifiers.Alt)) keys.Add("Alt");
        if (input.Modifiers.HasFlag(KeyModifiers.Shift)) keys.Add("Shift");
        if (input.Modifiers.HasFlag(KeyModifiers.Win)) keys.Add("Win");
        keys.Add(KeyNames.GetDisplayLabel(input.VirtualKey, input.BaseChar));

        var same = _chordKeys.Length > 0 && now < _chordExpiresAt && _chordKeys.SequenceEqual(keys);
        _chordKeys = [.. keys];
        _chordCount = same ? _chordCount + 1 : 1;
        _chordExpiresAt = now + Options.ChordDuration;

        // Full Text で入力中の文字列は、特殊キーを押している間も消さない。
        if (_text.Length > 0 && !_committed && _textExpiresAt < now + Options.TextDuration)
            _textExpiresAt = now + Options.TextDuration;
    }

    private static bool IsPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (char.IsControl(c)) return false;
        }
        return true;
    }
}
