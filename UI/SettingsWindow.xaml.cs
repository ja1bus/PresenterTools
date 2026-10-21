using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PresenterTools.Core.Licensing;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;
using PresenterTools.Core.State;
using PresenterTools.UI.Controls;

namespace PresenterTools.UI;

/// <summary>
/// 詳細設定画面（SPEC §10.2）。設定の複製を編集し、［適用］／［OK］で検証してから反映する。
/// 見た目（Cursor Highlight・Click Effect・Laser）は編集した時点でプレビューし、確定せずに閉じたら確定済みの見た目に戻す。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush PendingBrush = Frozen(0x8A, 0x4B, 0x00);
    private static readonly Brush ErrorBrush = Frozen(0xC6, 0x28, 0x28);
    private static readonly Brush QuietBrush = Frozen(0x55, 0x55, 0x55);

    private readonly AppHost _host;
    private readonly SettingsDraft _draft;
    private int _suspended;
    private bool _previewPending;
    private bool _closed;
    /// <summary>この画面で一度でも［適用］／［OK］で反映したか（［キャンセル］で戻らない変更があることを示す。V1.5.0）。</summary>
    private bool _appliedOnce;
    /// <summary>再起動の確認に「いいえ」と答えた表示言語（同じ選択で何度も聞かない）。</summary>
    private string? _declinedLanguage;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        _draft = new SettingsDraft(host.Settings, host.State.MagnifierEnabled, host.State.KeyDisplayMode);
        // 重複は他の欄にも関係するので、1 つの欄が変わったらすべての欄の状態を見直す。
        foreach (var entry in _draft.Hotkeys.All) entry.StatusSource = _ => RefreshStatuses();
        InitializeComponent();
        DataContext = _draft;
        VersionText.Text = string.Format(Strings.AboutVersionFormat, AppVersion);
        ManualLink.ToolTip = AppLinks.Manual(System.Globalization.CultureInfo.CurrentUICulture);
        SourceLink.ToolTip = AppLinks.Repository;
        RefreshStatuses();
        LaserPreview.ApplySettings(_draft.Settings.Laser);
        KeyPreview.ApplySettings(_draft.Settings.KeyDisplay);
        UpdateDependentControls(_draft.Settings);
        UpdatePending();
        _host.Hotkeys.RegistrationsChanged += OnRegistrationsChanged;
        _host.State.Changed += OnStateChanged;

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
            _host.State.Changed -= OnStateChanged;
            // 入力中のまま閉じた場合も一時解除を戻す。
            while (_suspended > 0)
            {
                _suspended--;
                _host.Hotkeys.Resume();
            }
        };
    }

    /// <summary>設定画面に表示するアプリのバージョン（csproj の Version）。</summary>
    private static string AppVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? Strings.SetVersionUnknown;

    /// <summary>指定のタブを開く（V1.5.0。操作パネルの Laser の案内・設定エラーの場所の表示に使う）。</summary>
    public void SelectSection(SettingsSection section) => Tabs.SelectedItem = TabOf(section);

    private TabItem TabOf(SettingsSection section) => section switch
    {
        SettingsSection.CursorHighlight => CursorTab,
        SettingsSection.ClickEffect => ClickTab,
        SettingsSection.Laser => LaserTab,
        SettingsSection.Magnifier => MagnifierTab,
        SettingsSection.KeyDisplay => KeyDisplayTab,
        _ => GeneralTab,
    };

    private void OnRegistrationsChanged(object? sender, EventArgs e) => RefreshStatuses();

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e) => SchedulePreview();

    // 操作パネル・ショートカットで ON/OFF を変えたときも、未適用の変更の表示を見直す。
    private void OnStateChanged(object? sender, StateChangedEventArgs e) => SchedulePreview();

    /// <summary>
    /// 見た目のプレビューを予約する。入力イベントの時点ではバインドの書き戻しが済んでいない場合があるため、
    /// 書き戻しの後（Background 優先度）にまとめて 1 回反映する。未適用の変更の表示・連動する欄もここで見直す。
    /// </summary>
    private void SchedulePreview()
    {
        if (_previewPending || _closed) return;
        _previewPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _previewPending = false;
            if (_closed) return;
            var pending = _draft.Build(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode).Settings;
            _host.PreviewAppearance(pending);
            LaserPreview.ApplySettings(_draft.Settings.Laser);
            KeyPreview.ApplySettings(_draft.Settings.KeyDisplay);
            UpdateDependentControls(pending);
            UpdatePending();
        });
    }

    /// <summary>
    /// 編集中の値に連動する表示を更新する（V1.5.0）。Cursor Highlight の形で使わない項目を薄くし、
    /// Laser を描く操作が無いことと、Laser・Magnifier の同じマウスボタンを示す。
    /// </summary>
    private void UpdateDependentControls(AppSettings pending)
    {
        var filled = _draft.Settings.CursorHighlight.Style == CursorHighlightStyle.FilledCircle;
        SetRowEnabled(!filled, RingOpacityLabel, RingOpacity, RingOpacityValue);
        SetRowEnabled(filled, RingFillOpacityLabel, RingFillOpacity, RingFillOpacityValue);
        SetRowEnabled(!filled, RingThicknessLabel, RingThickness, RingThicknessValue);

        LaserNoTriggerNotice.Visibility = SettingsValidator.HasLaserTrigger(pending) ? Visibility.Collapsed : Visibility.Visible;

        var sameMouse = SettingsValidator.HasSameMouseButton(pending);
        foreach (var box in new[] { LaserMouseBox, MagMouseBox })
        {
            if (sameMouse) box.BorderBrush = ErrorBrush;
            else box.ClearValue(Control.BorderBrushProperty);
        }
    }

    private static void SetRowEnabled(bool enabled, params UIElement[] elements)
    {
        foreach (var e in elements)
        {
            e.IsEnabled = enabled;
            e.Opacity = enabled ? 1 : 0.4;
        }
    }

    /// <summary>
    /// 未適用の変更の有無を画面下部に出し、［適用］の有効・無効を決める（V1.5.0。SPEC §10.2）。
    /// 変更が無くても、保存の失敗・登録できないショートカットがある間は［適用］で再試行できるようにする。
    /// </summary>
    private void UpdatePending()
    {
        var hasChanges = _draft.HasChanges(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode);
        var retry = _host.HasSaveError || _host.Hotkeys.Failures.Any();
        ApplyButton.IsEnabled = hasChanges || retry;
        (PendingText.Text, PendingText.Foreground) = (hasChanges, retry, _appliedOnce) switch
        {
            (true, _, false) => (Strings.PendingChanges, PendingBrush),
            (true, _, true) => (Strings.PendingChangesAfterApply, PendingBrush),
            (false, true, _) => (Strings.PendingRetry, ErrorBrush),
            (false, false, true) => (Strings.PendingNoneAfterApply, QuietBrush),
            _ => ("", QuietBrush),
        };
    }

    private void RefreshStatuses()
    {
        // 適用する前に、入力規則・重複の問題がある欄を示す（V1.5.0）。重複は後の欄に出す（HotkeyBindings.Validate と同じ）。
        var problems = HotkeyBindings.Validate(_draft.Hotkeys.ToSettings()).ToDictionary(i => i.Action, i => i.Message);
        foreach (var entry in _draft.Hotkeys.All)
            entry.UpdateStatus(_host.Hotkeys.Get(entry.Action), problems.GetValueOrDefault(entry.Action));
        // 入力欄にフォーカスがある間は全ショートカットを一時解除している（D-17）。どのタブからも見える下部に理由を出す。
        SuspendNotice.Visibility = _host.Hotkeys.IsSuspended ? Visibility.Visible : Visibility.Collapsed;
        SchedulePreview();
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

    /// <summary>戻した・読み込んだ内容はまだ確定していないことを示す。</summary>
    private void ShowNotice(string what) =>
        NoticeText.Text = string.Format(Strings.NoticeNotAppliedFormat, what);

    // ---- 設定の管理（V1.5.0。SPEC §10.2・§17.2） ----

    /// <summary>確定済みの設定をファイルに書き出す。未適用の変更は含まれないことを先に伝える。</summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_draft.HasChanges(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode))
        {
            var answer = MessageBox.Show(this, Strings.ExportPendingPrompt, Strings.SettingsTitle,
                MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.ExportDialogTitle,
            FileName = SettingsTransfer.DefaultFileName(DateTime.Now),
            DefaultExt = ".json",
            AddExtension = true,
            Filter = Strings.TransferFileFilter,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = Path.GetFullPath(dialog.FileName);
        try
        {
            SettingsTransfer.Write(path, SettingsTransfer.Serialize(_host.Settings, AppVersion, DateTimeOffset.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Services.ErrorLog.Write($"Settings export failed: {path}", ex);
            MessageBox.Show(this, string.Format(Strings.ExportFailedFormat, path, ex.Message),
                Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        NoticeText.Text = string.Format(Strings.NoticeExportedFormat, path);
    }

    /// <summary>ファイルの設定を編集中の値へ読み込む（確定は［適用］／［OK］）。</summary>
    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.ImportDialogTitle,
            DefaultExt = ".json",
            Filter = Strings.TransferFileFilter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = Path.GetFullPath(dialog.FileName);

        SettingsImportResult result;
        try
        {
            // 別用途の大きなファイルを誤って選んだ場合に、すべてを読み込まない。
            result = new FileInfo(path).Length > SettingsTransfer.MaxFileBytes
                ? new SettingsImportResult(null, SettingsImportError.TooLarge, [])
                : SettingsTransfer.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 暗号化などで読めない場合に利用者が確かめられるよう、フルパスを示す。
            Services.ErrorLog.Write($"Settings import failed: {path}", ex);
            ShowImportError(path, ex.Message);
            return;
        }

        if (result.Settings is null)
        {
            Services.ErrorLog.Write($"Settings import rejected: {path} {result.Error} {result.Detail}");
            ShowImportError(path, result.Error switch
            {
                SettingsImportError.TooLarge => Strings.ImportErrorTooLarge,
                SettingsImportError.NotJson => Strings.ImportErrorNotJson,
                SettingsImportError.NewerFormat => string.Format(Strings.ImportErrorNewerFormat, result.SourceAppVersion ?? Strings.SetVersionUnknown),
                SettingsImportError.InvalidSettings => Strings.ImportErrorInvalid,
                _ => Strings.ImportErrorNotExportFile,
            });
            return;
        }

        // 検証を通ったファイルだけ、何を読み込むかを選ばせる（ショートカット・マウスボタンは既定で読み込まない）。
        var exportedAt = DateTimeOffset.TryParse(result.ExportedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var at) ? at.ToLocalTime().ToString("g") : null;
        var options = new ImportOptionsWindow(Path.GetFileName(path), result.SourceAppVersion, exportedAt) { Owner = this };
        if (options.ShowDialog() != true) return;

        if (result.Corrections.Count > 0)
            Services.ErrorLog.Write($"Settings import corrected: {path} " + string.Join(", ", result.Corrections));
        _draft.Import(result.Settings, options.IncludeHotkeys, options.IncludeMouseButtons);
        var notice = string.Format(Strings.NoticeImportedFormat, Path.GetFileName(path));
        if (result.Corrections.Count > 0) notice += Strings.NoticeImportCorrected;
        ShowNotice(notice);
    }

    private void ShowImportError(string path, string reason) =>
        MessageBox.Show(this, string.Format(Strings.ImportFailedFormat, reason, path),
            Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- About（V1.5.0 で下部から移した） ----

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

    /// <summary>同梱の LICENSE（MIT License の全文）をこの画面で表示する（V1.4.0）。読めなければフルパスを示す。</summary>
    private void OnShowLicense(object sender, RoutedEventArgs e)
    {
        var text = LicenseFiles.Read(LicenseFiles.LicenseName, out var problem);
        if (text is null)
        {
            Services.ErrorLog.Write($"Failed to read the license file: {problem}");
            MessageBox.Show(this, string.Format(Strings.SetLicenseUnreadableFormat, problem),
                "PresenterTools", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, text, Strings.SetLicenseLink, MessageBoxButton.OK, MessageBoxImage.None);
    }

    /// <summary>公開リポジトリ（ソースコード）を既定のブラウザーで開く（V1.4.0）。開けなければ URL を見せる。</summary>
    private void OnOpenSource(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLinks.Repository) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Services.ErrorLog.Write("Failed to open the source code page", ex);
            MessageBox.Show(this, string.Format(Strings.SetSourceOpenFailedFormat, ex.Message, AppLinks.Repository),
                "PresenterTools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 同梱の THIRD-PARTY-NOTICES.md をメモ帳で開く（V1.4.0。.md は関連付けが無いことがあるため）。
    /// 開けなければフルパスを示す（暗号化などで読めない場合に利用者が確かめられるように）。
    /// </summary>
    private void OnOpenNotices(object sender, RoutedEventArgs e)
    {
        var path = LicenseFiles.PathOf(LicenseFiles.NoticesName);
        string? problem = null;
        if (!File.Exists(path)) problem = path;
        else
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                problem = $"{path}{Environment.NewLine}{ex.Message}";
            }
        }
        if (problem is null) return;
        Services.ErrorLog.Write($"Failed to open the third-party notices: {problem}");
        MessageBox.Show(this, string.Format(Strings.SetNoticesOpenFailedFormat, problem),
            "PresenterTools", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var problems = SettingsValidator.Validate(settings);
        if (problems.Count > 0)
        {
            // 最初の問題がある欄のタブを開いて見せてから、すべての問題を一覧で示す（V1.5.0）。
            ShowProblem(problems[0].Section, problems[0].Field, problems[0].Action);
            MessageBox.Show(this, Strings.ValidationHeader + "\n\n" + string.Join("\n", problems.Select(p => p.Message)),
                Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return ApplyOutcome.Invalid;
        }

        var result = _host.ApplySettings(settings, pending.MagnifierEnabled, pending.KeyDisplayMode);
        _draft.Accept(new SettingsEditResult(_host.Settings, _host.State.MagnifierEnabled, _host.State.KeyDisplayMode));
        _appliedOnce = true;
        var failures = result.HotkeyFailures;
        RefreshStatuses();
        NoticeText.Text = "";
        UpdatePending();
        if (!result.SaveResult.Success)
        {
            NoticeText.Text = Strings.SaveFailedNotice;
            var hotkeyNotice = failures.Count == 0 ? "" : "\n\n" + Strings.SaveFailedHotkeyNotice;
            MessageBox.Show(this, string.Format(Strings.SaveFailedMessageFormat, result.SaveResult.Error) + hotkeyNotice,
                Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return ApplyOutcome.NotSaved;
        }
        if (failures.Count == 0) return ApplyOutcome.Applied;

        // 他のアプリ・Windows と競合して登録できなかった操作とキーを示し、その欄のタブを開く（V1.5.0）。
        ShowProblem(SettingsValidator.SectionOf(failures[0].Action), null, failures[0].Action);
        var lines = failures.Select(f => string.Format(Strings.HotkeyFailureLineFormat,
            HotkeyActionNames.GetDisplayName(f.Action), f.Shortcut, f.Describe()));
        MessageBox.Show(this, string.Format(Strings.HotkeyFailuresMessageFormat, string.Join("\n", lines)) + "\n\n" + Strings.HotkeyFailuresRetryHint,
            Strings.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        return ApplyOutcome.HotkeyFailed;
    }

    /// <summary>
    /// 問題がある欄のタブを開き、その欄を見える位置へ移す（V1.5.0）。ショートカットは General にもあるため、
    /// General を開いているときは General のまま見せる。欄の強調は各欄の表示（赤枠・赤字）で行う。
    /// ショートカット欄にはフォーカスを移さない（フォーカスがあると全ショートカットを一時解除するため。D-17）。
    /// </summary>
    private void ShowProblem(SettingsSection section, string? field, HotkeyAction? action)
    {
        if (!(action is not null && Tabs.SelectedItem == GeneralTab)) SelectSection(section);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closed) return;
            FrameworkElement? target = field switch
            {
                "cursorHighlight.color" => CursorColor,
                "clickEffect.leftColor" => ClickLeftColor,
                "clickEffect.rightColor" => ClickRightColor,
                "laser.color" => LaserColor,
                "keyDisplay.textColor" => KeyTextColor,
                "keyDisplay.backgroundColor" => KeyBackColor,
                SettingsValidator.LaserMouseField => LaserMouseBox,
                _ => null,
            };
            if (target is null && action is { } a && Tabs.SelectedItem is TabItem { Content: DependencyObject content })
                target = Descendants<ShortcutBox>(content).FirstOrDefault(b => b.DataContext is HotkeyEntry entry && entry.Action == a);
            if (target is null) return;
            target.BringIntoView();
            if (target is ColorField color) color.FocusInput();
            else if (target is ComboBox) target.Focus();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
