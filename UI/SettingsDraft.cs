using System.ComponentModel;
using System.Runtime.CompilerServices;
using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;
using PresenterTools.Input;

namespace PresenterTools.UI;

/// <summary>Settings 画面で編集中のショートカット 1 件（同じ項目を複数のタブに出すため変更通知を持つ）。</summary>
public sealed class HotkeyEntry : INotifyPropertyChanged
{
    private string _shortcut;
    private string _status = "";
    private bool _isError;
    private bool _hasProblem;

    public HotkeyEntry(HotkeyAction action, string shortcut)
    {
        Action = action;
        _shortcut = shortcut;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HotkeyAction Action { get; }
    public string DisplayName => HotkeyActionNames.GetDisplayName(Action);

    public string Shortcut
    {
        get => _shortcut;
        set
        {
            if (Set(ref _shortcut, value ?? "")) StatusSource?.Invoke(this);
        }
    }

    /// <summary>登録状態の表示文字列。</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsError
    {
        get => _isError;
        private set => Set(ref _isError, value);
    }

    /// <summary>入力欄を強調する（入力規則・重複の問題がある。V1.5.0）。</summary>
    public bool HasProblem
    {
        get => _hasProblem;
        private set => Set(ref _hasProblem, value);
    }

    /// <summary>状態の再計算を依頼する先（SettingsDraft が設定）。</summary>
    internal Action<HotkeyEntry>? StatusSource { get; set; }

    /// <summary>
    /// 現在登録されているものと比べて状態を表示する。<paramref name="problem"/> は編集中の値の入力規則・重複の問題
    /// （V1.5.0。適用する前に、どの欄を直せばよいかを示す）。
    /// </summary>
    public void UpdateStatus(HotkeyRegistration? registration, string? problem = null)
    {
        HasProblem = problem is not null;
        if (problem is not null)
        {
            Status = problem;
            IsError = true;
            return;
        }
        var applied = registration?.Shortcut.ToString() ?? "";
        var normalized = Core.Input.Shortcut.TryParse(Shortcut, out var sc) ? sc.ToString() : Shortcut;
        if (normalized != applied)
        {
            Status = Strings.HotkeyStatusPending;
            IsError = false;
            return;
        }
        Status = registration?.Describe() ?? Strings.ShortcutUnassigned;
        IsError = registration?.Status == HotkeyStatus.Failed;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>ショートカットの編集用。タブをまたいで同じ項目にバインドする。</summary>
public sealed class HotkeyDraft
{
    public HotkeyDraft(HotkeySettings hotkeys)
    {
        All = HotkeyActionNames.All.Select(a => new HotkeyEntry(a, hotkeys.Get(a))).ToList();
    }

    public IReadOnlyList<HotkeyEntry> All { get; }

    public HotkeyEntry this[HotkeyAction action] => All.First(e => e.Action == action);

    public HotkeyEntry ToggleMaster => this[HotkeyAction.ToggleMaster];
    public HotkeyEntry ShowControlPanel => this[HotkeyAction.ShowControlPanel];
    public HotkeyEntry ToggleCursorHighlight => this[HotkeyAction.ToggleCursorHighlight];
    public HotkeyEntry ToggleClickEffect => this[HotkeyAction.ToggleClickEffect];
    public HotkeyEntry ToggleLaser => this[HotkeyAction.ToggleLaser];
    public HotkeyEntry LaserHold => this[HotkeyAction.LaserHold];
    public HotkeyEntry ToggleMagnifier => this[HotkeyAction.ToggleMagnifier];
    public HotkeyEntry ToggleKeyDisplay => this[HotkeyAction.ToggleKeyDisplay];

    /// <summary>入力欄の値を指定の設定に置き換える（既定値に戻す操作用）。</summary>
    public void Load(HotkeySettings hotkeys)
    {
        foreach (var e in All) e.Shortcut = hotkeys.Get(e.Action);
    }

    public HotkeySettings ToSettings()
    {
        var h = new HotkeySettings();
        foreach (var e in All) h.Set(e.Action, e.Shortcut);
        return h;
    }
}

/// <summary>
/// Settings 画面の編集内容。保存する設定の複製と、保存しない実行時の状態（Magnifier の ON・Key Display のモード）を持つ。
/// 既定値・開いたときの値に戻すときは <see cref="Settings"/> を新しい複製に差し替えて変更を通知する（画面のバインドをすべて読み直させる）。
/// </summary>
public sealed class SettingsDraft : INotifyPropertyChanged
{
    private readonly SettingsEditSession _session;

    public SettingsDraft(AppSettings settings, bool magnifierEnabled, KeyDisplayMode keyDisplayMode)
    {
        _session = new(settings, magnifierEnabled, keyDisplayMode);
        Hotkeys = new HotkeyDraft(settings.Hotkeys);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AppSettings Settings => _session.Settings;
    public HotkeyDraft Hotkeys { get; }

    private void Edit(Action action)
    {
        _session.Settings.Hotkeys = Hotkeys.ToSettings();
        action();
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        Hotkeys.Load(Settings.Hotkeys);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Settings)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MagnifierEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyDisplayMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyDisplayEnabled)));
    }

    public void ResetSection(SettingsSection section) => Edit(() => _session.ResetSection(section));
    public void RevertSection(SettingsSection section) => Edit(() => _session.RevertSection(section));
    public void ResetHotkeys() => Edit(_session.ResetHotkeys);
    public void ResetAll() => Edit(_session.ResetAll);
    public void Import(AppSettings imported, bool includeHotkeys, bool includeMouseButtons) =>
        Edit(() => _session.Import(imported, includeHotkeys, includeMouseButtons));

    public bool MagnifierEnabled
    {
        get => _session.MagnifierEnabled;
        set
        {
            if (_session.MagnifierEnabled == value) return;
            _session.MagnifierEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MagnifierEnabled)));
        }
    }

    public KeyDisplayMode KeyDisplayMode
    {
        get => _session.KeyDisplayMode;
        set
        {
            if (_session.KeyDisplayMode == value) return;
            _session.KeyDisplayMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyDisplayMode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyDisplayEnabled)));
        }
    }

    /// <summary>Key Display タブのチェックボックス用（V1.5.0。ON は全文表示の FullText）。</summary>
    public bool KeyDisplayEnabled
    {
        get => KeyDisplayMode != KeyDisplayMode.Off;
        set => KeyDisplayMode = value ? KeyDisplayMode.FullText : KeyDisplayMode.Off;
    }

    public SettingsEditResult Build(AppSettings current, bool magnifier, KeyDisplayMode keyDisplay)
    {
        _session.Settings.Hotkeys = Hotkeys.ToSettings();
        return _session.Build(current, magnifier, keyDisplay);
    }

    /// <summary>［適用］すると今の動作・設定が変わるか（<see cref="SettingsEditSession.HasChanges"/>）。</summary>
    public bool HasChanges(AppSettings current, bool magnifier, KeyDisplayMode keyDisplay)
    {
        _session.Settings.Hotkeys = Hotkeys.ToSettings();
        return _session.HasChanges(current, magnifier, keyDisplay);
    }

    public void Accept(SettingsEditResult applied)
    {
        _session.Accept(applied);
        RefreshBindings();
    }
}
