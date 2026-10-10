using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.UI;

/// <summary>
/// 詳細設定画面（SPEC §10.2）。設定の複製を編集し、［適用］／［OK］で検証してから反映する。
/// 見た目（Cursor Highlight・Click Effect・Laser）は編集した時点でプレビューし、確定せずに閉じたら確定済みの見た目に戻す。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly SettingsDraft _draft;
    private int _suspended;
    private bool _previewPending;
    private bool _closed;
    /// <summary>再起動の確認に「いいえ」と答えた表示言語（同じ選択で何度も聞かない）。</summary>
    private string? _declinedLanguage;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        _draft = new SettingsDraft(host.Settings, host.State.MagnifierEnabled, host.State.KeyDisplayMode);
        foreach (var entry in _draft.Hotkeys.All) entry.StatusSource = e => e.UpdateStatus(_host.Hotkeys.Get(e.Action));
        InitializeComponent();
        DataContext = _draft;
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? Strings.SetVersionUnknown;
        VersionText.Text = $"PresenterTools  v{version}";
        ManualLink.ToolTip = AppLinks.Manual(System.Globalization.CultureInfo.CurrentUICulture);
        RefreshStatuses();
        LaserPreview.ApplySettings(_draft.Settings.Laser);
        _host.Hotkeys.RegistrationsChanged += OnRegistrationsChanged;

        // 入力欄の変更（スライダー・コンボボックス・チェック・色欄）と初期化による差し替えをまとめてプレビューへ回す。
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => SchedulePreview()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => SchedulePreview()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => SchedulePreview()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => SchedulePreview()));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => SchedulePreview()));
        _draft.PropertyChanged += OnDraftChanged;

        Closed += (_, _) =>
        {
            _closed = true;
            _draft.PropertyChanged -= OnDraftChanged;
            // ［キャンセル］・×・Esc で閉じた場合もプレビューを確定済みの見た目に戻す（確定後なら同じ見た目のまま）。
            _host.RestoreAppearance();
            _host.Hotkeys.RegistrationsChanged -= OnRegistrationsChanged;
            // 入力中のまま閉じた場合も一時解除を戻す。
            while (_suspended > 0)
            {
                _suspended--;
                _host.Hotkeys.Resume();
            }
        };
    }

    private void OnRegistrationsChanged(object? sender, EventArgs e) => RefreshStatuses();

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e) => SchedulePreview();

    /// <summary>
    /// 見た目のプレビューを予約する。入力イベントの時点ではバインドの書き戻しが済んでいない場合があるため、
    /// 書き戻しの後（Background 優先度）にまとめて 1 回反映する。
    /// </summary>
    private void SchedulePreview()
    {
        if (_previewPending || _closed) return;
        _previewPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _previewPending = false;
            if (_closed) return;
            _host.PreviewAppearance(_draft.Build(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode).Settings);
            LaserPreview.ApplySettings(_draft.Settings.Laser);
        });
    }

    private void RefreshStatuses()
    {
        foreach (var entry in _draft.Hotkeys.All) entry.UpdateStatus(_host.Hotkeys.Get(entry.Action));
        // 入力欄にフォーカスがある間は全ショートカットを一時解除している（D-17）。どのタブからも見える下部に理由を出す。
        SuspendNotice.Visibility = _host.Hotkeys.IsSuspended ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCaptureStarted(object? sender, EventArgs e)
    {
        _suspended++;
        _host.Hotkeys.Suspend();
    }

    private void OnCaptureEnded(object? sender, EventArgs e)
    {
        if (_suspended == 0) return;
        _suspended--;
        _host.Hotkeys.Resume();
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HotkeyEntry entry }) entry.Shortcut = "";
    }

    private void OnResetSection(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SettingsSection>(tag, out var section)) return;
        _draft.ResetSection(section);
        ShowNotice(Strings.NoticeTabReset);
    }

    private void OnRevertSection(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SettingsSection>(tag, out var section)) return;
        _draft.RevertSection(section);
        ShowNotice(Strings.NoticeTabReverted);
    }

    private void OnResetHotkeys(object sender, RoutedEventArgs e)
    {
        _draft.ResetHotkeys();
        ShowNotice(Strings.NoticeHotkeysReset);
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        _draft.ResetAll();
        ShowNotice(Strings.NoticeAllReset);
    }

    /// <summary>戻した内容はまだ確定していないことを示す。</summary>
    private void ShowNotice(string what) =>
        NoticeText.Text = string.Format(Strings.NoticeNotAppliedFormat, what);

    /// <summary>取扱説明書（Web）を既定のブラウザーで開く。開けなければ URL を見せる（設定画面は閉じない）。</summary>
    private void OnOpenManual(object sender, RoutedEventArgs e)
    {
        var url = AppLinks.Manual(System.Globalization.CultureInfo.CurrentUICulture);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Services.ErrorLog.Write("Failed to open the user guide", ex);
            MessageBox.Show(this, string.Format(Strings.SetManualOpenFailedFormat, ex.Message, url),
                "PresenterTools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var outcome = Apply();
        if (OfferRestartIfLanguageChanged(outcome)) return; // 再起動中（この画面も閉じられる）
        if (outcome == ApplyOutcome.Applied) Close();
    }

    private void OnApply(object sender, RoutedEventArgs e) => OfferRestartIfLanguageChanged(Apply());

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private enum ApplyOutcome
    {
        /// <summary>入力に問題があり反映しなかった。</summary>
        Invalid,
        /// <summary>動作には反映したが保存できなかった。</summary>
        NotSaved,
        /// <summary>保存したが、登録できなかったショートカットがある。</summary>
        HotkeyFailed,
        Applied,
    }

    /// <summary>
    /// 保存した表示言語が今の表示と違えば再起動を確認する（SPEC §34。AreaShot と同じ）。保存できていない場合は、
    /// 新しいプロセスが古い言語を読んでしまうため確認しない。「いいえ」と答えた言語は、この画面では再び聞かない。
    /// 再起動を始めた場合は true。
    /// </summary>
    private bool OfferRestartIfLanguageChanged(ApplyOutcome outcome)
    {
        if (outcome is ApplyOutcome.Invalid or ApplyOutcome.NotSaved) return false;
        if (_host.PendingLanguage() is not { } wanted || wanted.Name == _declinedLanguage) return false;
        if (_host.OfferRestart(this, wanted)) return true;
        _declinedLanguage = wanted.Name;
        return false;
    }

    /// <summary>検証して反映する。問題があればメッセージを出す（Applied 以外では画面を閉じない）。</summary>
    private ApplyOutcome Apply()
    {
        var pending = _draft.Build(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode);
        var settings = pending.Settings;
        var problems = Validate(settings);
        if (problems.Count > 0)
        {
            MessageBox.Show(this, Strings.ValidationHeader + "\n\n" + string.Join("\n", problems),
                Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return ApplyOutcome.Invalid;
        }

        var result = _host.ApplySettings(settings, pending.MagnifierEnabled, pending.KeyDisplayMode);
        _draft.Accept(new SettingsEditResult(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode));
        var failures = result.HotkeyFailures;
        RefreshStatuses();
        NoticeText.Text = "";
        if (!result.SaveResult.Success)
        {
            NoticeText.Text = Strings.SaveFailedNotice;
            var hotkeyNotice = failures.Count == 0 ? "" : "\n\n" + Strings.SaveFailedHotkeyNotice;
            MessageBox.Show(this, string.Format(Strings.SaveFailedMessageFormat, result.SaveResult.Error) + hotkeyNotice,
                Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return ApplyOutcome.NotSaved;
        }
        if (failures.Count == 0) return ApplyOutcome.Applied;

        var lines = failures.Select(f => string.Format(Strings.HotkeyFailureLineFormat,
            HotkeyActionNames.GetDisplayName(f.Action), f.Shortcut, f.Describe()));
        MessageBox.Show(this, string.Format(Strings.HotkeyFailuresMessageFormat, string.Join("\n", lines)),
            Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        return ApplyOutcome.HotkeyFailed;
    }

    private static List<string> Validate(AppSettings s)
    {
        var problems = new List<string>();
        void Color(string name, string value)
        {
            if (!ArgbColor.IsValid(value)) problems.Add(string.Format(Strings.ValidationColorFormat, name, value));
        }
        Color(Strings.ColorNameCursor, s.CursorHighlight.Color);
        Color(Strings.ColorNameClickLeft, s.ClickEffect.LeftColor);
        Color(Strings.ColorNameClickRight, s.ClickEffect.RightColor);
        Color(Strings.ColorNameLaser, s.Laser.Color);
        Color(Strings.ColorNameKeyText, s.KeyDisplay.TextColor);
        Color(Strings.ColorNameKeyBackground, s.KeyDisplay.BackgroundColor);

        foreach (var issue in HotkeyBindings.Validate(s.Hotkeys))
            problems.Add(string.Format(Strings.ValidationHotkeyFormat, HotkeyActionNames.GetDisplayName(issue.Action), issue.Message));

        if (s.Laser.MouseButton != MouseTriggerButton.None && s.Laser.MouseButton == s.Magnifier.MouseButton)
            problems.Add(Strings.ValidationSameMouseButton);
        return problems;
    }
}
