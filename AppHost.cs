using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PresenterTools.Core.Geometry;
using PresenterTools.Core.Input;
using PresenterTools.Core.KeyDisplay;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.ScreenPen;
using PresenterTools.Core.Settings;
using PresenterTools.Core.State;
using PresenterTools.Input;
using PresenterTools.Overlay;
using PresenterTools.Services;
using PresenterTools.UI;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools;

public sealed record SettingsApplyResult(IReadOnlyList<HotkeyRegistration> HotkeyFailures, SettingsSaveResult SaveResult);

/// <summary>
/// 構成ルート（SPEC §18）。各サービスを生成し、入力・状態変化を表示部へ配る。UI スレッドで使う。
/// </summary>
public sealed class AppHost : IDisposable, IPenHost
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _store = new(SettingsStore.DefaultFilePath);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly KeyDisplayModel _keyModel = new();
    private readonly DispatcherTimer _keyTimer;
    // パスワード欄での自動非表示（V1.6.0。詳細設計 §2）。時刻は Stopwatch.GetTimestamp の値。
    private static readonly long SettleDelay = Stopwatch.Frequency * 120 / 1000;
    private static readonly long QueryTimeout = Stopwatch.Frequency * 300 / 1000;
    private static readonly long PasswordPoll = Stopwatch.Frequency * 150 / 1000;
    private readonly PasswordInputGate _gate = new(SettleDelay, QueryTimeout, PasswordPoll);
    private readonly DispatcherTimer _gateTimer;
    private FocusWatchWorker? _focusWorker;
    private bool _gateTimeoutLogged;
    private MonitorService? _monitors;
    private OverlayManager? _overlays;
    private MagnifierController? _magnifier;
    private LowLevelInputHook? _hook;
    private GlobalHotkeyService? _hotkeys;
    private TrayService? _tray;
    private ControlPanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private PresenterState _state = new();
    private string? _loadNotice;
    private string? _saveError;
    private string? _hookError;
    private IntPtr _lastExternalForeground;
    private int _laserHoldKey;
    private bool _laserWasActive;
    private bool _batch;
    // 画面ペン（V1.7.0。docs/archive/design-1.7.0.md）。
    private PenController? _pen;
    private string? _penEndReason;
    private string? _penGuide;
    private string? _penNotice;
    private bool _masterWasEnabled;
    /// <summary>起動時の Windows の表示言語（「Windows の表示言語に合わせる」の判定に使う）。</summary>
    private readonly CultureInfo _windowsUiCulture;
    /// <summary>この起動で使っている表示言語（終了まで変えない。SPEC §34）。</summary>
    private CultureInfo _uiCulture;

    public AppHost(Dispatcher dispatcher, CultureInfo windowsUiCulture)
    {
        _dispatcher = dispatcher;
        _windowsUiCulture = windowsUiCulture;
        _uiCulture = UiLanguage.Resolve(UiLanguage.System, windowsUiCulture);
        _keyTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _keyTimer.Tick += OnKeyTimer;
        _gateTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
        _gateTimer.Tick += (_, _) =>
        {
            _gateTimer.Stop();
            PumpGate();
        };
    }

    public bool IsExiting { get; private set; }
    public PresenterState State => _state;
    public AppSettings Settings => _settings;
    public GlobalHotkeyService Hotkeys => _hotkeys ?? throw new InvalidOperationException("not started");
    public ControlPanelWindow? Panel => _panel;

    private TimeSpan Now => _clock.Elapsed;

    public void Start()
    {
        LoadSettings();
        _state = PresenterState.FromSettings(_settings);

        _monitors = new MonitorService(_dispatcher);
        _monitors.DisplaysChanged += OnDisplaysChanged;
        _overlays = new OverlayManager(_monitors);
        _overlays.Frame += OnFrame;
        _magnifier = new MagnifierController(_monitors);
        _pen = new PenController(this);
        _pen.Initialize(_settings.Pen);
        _overlays.HoldSuppressedAt = p => _pen?.Covers(p) == true;
        _overlays.OverlayDpiChanged += OnOverlayDpiChanged;

        _hook = new LowLevelInputHook(_dispatcher);
        _hook.MouseMoved += OnMouseMoved;
        _hook.MouseButton += OnMouseButton;
        _hook.KeyEvent += OnKeyEvent;
        _hook.HookFailed += OnHookFailed;

        _hotkeys = new GlobalHotkeyService();
        _hotkeys.Pressed += OnHotkey;
        _hotkeys.RegistrationsChanged += (_, _) => RefreshUi();

        _tray = new TrayService();
        _tray.ShowPanelRequested += (_, _) => ShowPanel();
        _tray.ToggleMasterRequested += (_, _) => _state.ToggleMaster();
        _tray.KeyDisplayOffRequested += (_, _) => _state.KeyDisplayOff();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => Exit();

        _panel = new ControlPanelWindow(this);

        ApplySettingsToComponents();
        var failures = _hotkeys.Apply(HotkeyBindings.Parse(_settings.Hotkeys));

        _state.Changed += OnStateChanged;
        _masterWasEnabled = _state.MasterEnabled;
        ApplyState(save: false);
        // 高さは内容で決まるので、ショートカット表示などを反映してから位置を決める。
        _panel.RestorePlacement(_settings.ControlPanel, _monitors);
        _panel.Show();

        if (failures.Count > 0)
        {
            _tray.ShowBalloon(Strings.HotkeyFailedBalloonTitle,
                string.Join("\n", failures.Select(f => string.Format(Strings.HotkeyFailedItemFormat, HotkeyActionNames.GetDisplayName(f.Action), f.Shortcut))) +
                "\n" + Strings.HotkeyFailedBalloonHint, warning: true);
        }
    }

    // ---- 操作（操作パネル・トレイ・Settings から呼ぶ） ----

    public void ShowPanel()
    {
        if (_panel is null || _monitors is null) return;
        _panel.ShowAndActivate(_monitors.Monitors);
    }

    /// <summary>
    /// 設定画面を開く。<paramref name="section"/> を指定するとそのタブを開く（V1.5.0。操作パネルの Laser の案内から）。
    /// 開いている場合は前面に出す（編集中の内容はそのまま）。
    /// </summary>
    public void ShowSettings(SettingsSection? section = null)
    {
        // 入力面が設定画面を覆わないよう、ペンを終える（線は残す。D8）。
        if (_state.PenActive) EndPen("settings");
        if (_settingsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            if (section is { } s) existing.SelectSection(s);
            existing.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_penGuide == Strings.PenGuideSettingsOpen) _penGuide = null;
            RefreshUi(); // パネルの Pen のボタンを戻す
        };
        if (section is { } first) _settingsWindow.SelectSection(first);
        _settingsWindow.Show();
        _settingsWindow.Activate();
        RefreshUi();
    }

    /// <summary>設定画面が開いている（ペンを開始しない。D2）。</summary>
    public bool IsSettingsOpen => _settingsWindow is not null;

    /// <summary>ペンの入力面が出ている（操作パネルをその上に出す。D4）。</summary>
    public bool IsPenSurfaceShown => _pen?.IsActive == true;

    /// <summary>操作パネルの Pen の行の下に出す案内（D3）。無ければ null。</summary>
    public string? PenGuideText => _penGuide;

    /// <summary>接続中の画面（設定画面のペン タブの一覧・識別）。</summary>
    public DisplaySnapshot QueryDisplays() => _monitors?.QuerySnapshot() ?? DisplaySnapshot.Incomplete;

    // ---- 画面ペン（V1.7.0。docs/archive/design-1.7.0.md D2・D3・D7・D11） ----

    /// <summary>ショートカット・操作パネルの Pen のボタン。</summary>
    public void TogglePen(bool fromHotkey)
    {
        if (_state.PenActive) EndPen(fromHotkey ? "hotkey" : "panel");
        else TryStartPen(fromHotkey);
    }

    /// <summary>開始の判定を一か所で行う（D2）。満たさなければ状態を変えずに案内する。</summary>
    private void TryStartPen(bool fromHotkey)
    {
        if (_pen is null || _monitors is null || _overlays is null || IsExiting) return;
        if (!_state.MasterEnabled) return; // Master OFF の間は無視する
        if (_settingsWindow is not null)
        {
            ShowPenGuide("SettingsOpen", Strings.PenGuideSettingsOpen, fromHotkey, keep: false);
            return;
        }
        var snapshot = _monitors.QuerySnapshot();
        // 所有者があれば開始の時点でも比べる（契機 (4)）。保留なら開始しない。
        var decision = _pen.OnDisplaysChanged("start", snapshot, _overlays);
        if (decision == PenDisplayOutcome.Pending)
        {
            ShowPenGuide(nameof(PenTargetStatus.Unknown), Strings.PenGuideUnknown, fromHotkey);
            return;
        }
        var result = PenTargetResolver.Resolve(_settings.Pen, snapshot);
        if (result.Display is not { } target || !result.IsResolved || _overlays.OverlayFor(target.DeviceName, target.Bounds) is null)
        {
            var status = result.IsResolved ? PenTargetStatus.Unknown : result.Status; // 窓の作り直しが追いついていない
            var text = status switch
            {
                PenTargetStatus.NotSelected => Strings.PenGuideNotSelected,
                PenTargetStatus.NotConnected => string.Format(Strings.PenGuideNotConnectedFormat, PenDisplayLabel.Saved(_settings.Pen)),
                PenTargetStatus.Reselect => Strings.PenGuideReselect,
                _ => Strings.PenGuideUnknown,
            };
            ShowPenGuide(status.ToString(), text, fromHotkey);
            return;
        }
        _penGuide = null;
        _penNotice = null;
        _pen.Activate(target, _overlays);
        _state.SetPen(true);
    }

    /// <summary>案内を出す。パネルの案内は書き込み先が決まるまで残す。ショートカットからはバルーンでも出す（D3）。</summary>
    private void ShowPenGuide(string kind, string text, bool fromHotkey, bool keep = true)
    {
        TestTrace.Write("pen-guide", kind);
        _penGuide = keep ? text : _penGuide;
        if (fromHotkey) _tray?.ShowBalloon(Strings.PenBalloonTitle, text, warning: false);
        RefreshUi();
    }

    /// <summary>
    /// 構成変更の後、書き込み先の案内が出ていれば保存先を解決し直し、開始できる状態になっていれば消す（D3。R7）。
    /// 設定画面が開いている間の案内はそのまま。
    /// </summary>
    private void ClearPenGuideIfResolved(DisplaySnapshot snapshot)
    {
        if (_penGuide is null || _penGuide == Strings.PenGuideSettingsOpen || _monitors is null || _overlays is null) return;
        var result = PenTargetResolver.Resolve(_settings.Pen, snapshot);
        if (result.Display is not { } target || !result.IsResolved || _overlays.OverlayFor(target.DeviceName, target.Bounds) is null) return;
        if (_pen?.IsPending == true) return; // 描線の判定待ちの間は開始できない
        _penGuide = null;
        TestTrace.Write("pen-guide", "cleared");
    }

    /// <summary>ペンを終える（線は残す）。状態の変更を通し、入力面の後始末は <see cref="ApplyState"/> で行う。</summary>
    public void EndPen(string reason)
    {
        if (_pen is null) return;
        if (!_state.PenActive)
        {
            if (_pen.IsActive) _pen.Deactivate(reason);
            return;
        }
        _penEndReason = reason;
        _state.SetPen(false);
        // 一括反映中（設定の適用）は ApplyState が後で呼ばれるが、入力面はここで閉じる。
        if (_pen.IsActive) _pen.Deactivate(reason);
        _penEndReason = null;
    }

    void IPenHost.RaisePanel() => RaisePanel();

    private void RaisePanel()
    {
        if (_pen?.IsActive == true) _panel?.RaiseAbovePen();
    }

    bool IPenHost.IsOwnWindow(IntPtr hwnd) => _hook?.IsOwnWindow(hwnd) == true;

    void IPenHost.NotifyDiscarded()
    {
        _penNotice = Strings.StatusPenDiscarded;
        RefreshUi();
    }

    void IPenHost.RecheckPen(string trigger) => RecheckPen(trigger);

    /// <summary>列挙し直して所有者と比べる（DPI 変更・保留の再試行・Master ON。D11）。窓は作り直さない。</summary>
    private void RecheckPen(string trigger)
    {
        if (_pen?.Owner is null || _monitors is null || _overlays is null || IsExiting) return;
        var snapshot = _monitors.QuerySnapshot();
        _pen.OnDisplaysChanged(trigger, snapshot, _overlays);
        ClearPenGuideIfResolved(snapshot);
        RefreshUi();
    }

    private void OnOverlayDpiChanged(OverlayWindow window)
    {
        // 所有者の画面の窓だけを見る（他の画面の DPI 変更は D11 の (1) で扱う）。
        if (_pen?.Owner is not { } owner) return;
        if (!string.Equals(window.Monitor.DeviceName, owner.DeviceName, StringComparison.OrdinalIgnoreCase)) return;
        RecheckPen("dpi");
    }

    /// <summary>直前の保存に失敗している（設定画面で［適用］による再試行を許すため。V1.5.0）。</summary>
    public bool HasSaveError => _saveError is not null;

    public void SetAlwaysOnTop(bool on)
    {
        _settings.General.AlwaysOnTop = on;
        RefreshUi();
        SaveSettings();
    }

    public void SetMagnifierZoom(int zoomPercent)
    {
        if (!SettingsNormalizer.ZoomPercents.Contains(zoomPercent)) return;
        _settings.Magnifier.ZoomPercent = zoomPercent;
        _magnifier?.ApplySettings(_settings.Magnifier);
        RefreshUi();
        SaveSettings();
    }

    /// <summary>
    /// Settings 画面で編集中の見た目（Cursor Highlight・Click Effect・Laser）を、保存せずに表示へ反映する（SPEC §10.2・D-31）。
    /// ON/OFF・ショートカット等は確定済みのまま。取り消すときは <see cref="RestoreAppearance"/>。
    /// </summary>
    public void PreviewAppearance(AppSettings draft)
    {
        if (!IsExiting) _overlays?.ApplySettings(SettingsPreview.Compose(_settings, draft));
    }

    /// <summary>プレビューをやめ、確定済みの見た目に戻す（終了処理中は何もしない）。</summary>
    public void RestoreAppearance()
    {
        if (!IsExiting) _overlays?.ApplySettings(_settings);
    }

    /// <summary>Settings 画面の内容を反映する。登録に失敗したショートカットを返す。</summary>
    public SettingsApplyResult ApplySettings(AppSettings newSettings, bool magnifierOn, KeyDisplayMode keyMode)
    {
        SettingsNormalizer.Normalize(newSettings);
        newSettings.ControlPanel = _settings.ControlPanel; // パネルの位置は Settings 画面では編集しない
        var triggerModeChanged = newSettings.Laser.TriggerMode != _settings.Laser.TriggerMode;
        var laserMouseChanged = newSettings.Laser.MouseButton != _settings.Laser.MouseButton;
        var penBefore = _settings.Pen;
        _settings = newSettings;
        ApplySettingsToComponents();
        // 書き込み先の組が変わったときだけ所有者と比べる（D9）。捨てたらペンも終える。
        if (_pen is not null && _monitors is not null)
        {
            var targetChanged = !penBefore.SameTargetAs(newSettings.Pen);
            _pen.ApplySettings(penBefore, newSettings.Pen, targetChanged ? _monitors.QuerySnapshot() : _monitors.Snapshot);
            if (targetChanged) _penGuide = null; // 次の開始で改めて判定する
        }

        _batch = true;
        try
        {
            // 描画方式を変えたら描画中のものは終える（押している間／押すたびの判断が食い違わないように）。
            if (triggerModeChanged) _state.EndAllLaser();
            // マウスボタンの割当を変えたら、旧ボタンによる描画を終える（旧ボタンを離しても新しい割当と一致せず終われないため）。
            else if (laserMouseChanged) _state.EndLaser(LaserTrigger.Mouse);
            _state.SetMaster(_settings.General.MasterEnabled);
            _state.SetCursorHighlight(_settings.CursorHighlight.Enabled);
            _state.SetClickEffect(_settings.ClickEffect.Enabled);
            _state.SetLaser(_settings.Laser.Enabled);
            _state.SetMagnifier(magnifierOn);
            _state.SetKeyDisplayMode(keyMode);
        }
        finally
        {
            _batch = false;
        }

        var failures = Hotkeys.Apply(HotkeyBindings.Parse(_settings.Hotkeys));
        ApplyState(save: false);
        return new SettingsApplyResult(failures, SaveSettings());
    }

    public SettingsSaveResult SaveSettings()
    {
        _state.WriteTo(_settings);
        _panel?.CapturePlacement(_settings.ControlPanel);
        var result = _store.TrySave(_settings);
        var previousError = _saveError;
        _saveError = result.Error;
        if (!result.Success) ErrorLog.Write(result.Error!);
        if (_saveError != previousError) RefreshUi();
        return result;
    }

    /// <summary>
    /// 確定済みの設定の表示言語が今の表示と違えば、その言語。同じなら null
    /// （「Windows の表示言語に合わせる」と同じ言語の固定を切り替えた場合など。SPEC §34）。
    /// </summary>
    public CultureInfo? PendingLanguage()
    {
        var wanted = UiLanguage.Resolve(_settings.General.Language, _windowsUiCulture);
        return wanted.Name == _uiCulture.Name ? null : wanted;
    }

    /// <summary>
    /// 表示言語を変えるための再起動を、選んだ言語で確認する（AreaShot と同じ方式）。
    /// はいなら再起動を始めて true（すべての画面が閉じられる）。いいえ・失敗なら false（次回の起動で反映）。
    /// </summary>
    public bool OfferRestart(Window owner, CultureInfo wanted)
    {
        var message = UiLanguage.Get(nameof(Strings.RestartPrompt), wanted);
        // 保存しない実行時の状態は再起動で OFF に戻ることを書き添える。
        if (_state.KeyDisplayMode != KeyDisplayMode.Off || _state.MagnifierEnabled || _state.IsLaserActive
            || _state.PenActive || _pen?.HasLines == true)
            message += "\n\n" + UiLanguage.Get(nameof(Strings.RestartPromptStateReset), wanted);
        var answer = MessageBox.Show(owner, message, "PresenterTools", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer == MessageBoxResult.Yes && Restart(wanted);
    }

    /// <summary>
    /// 新しいプロセスを --restart 付きで起動してから終了する。新しいプロセスは、このプロセスが
    /// ショートカット・フック・多重起動防止を解放するまで待ってから起動する（App.OnExit で解放する）。
    /// </summary>
    private bool Restart(CultureInfo wanted)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("The executable path is not available.");
            Process.Start(new ProcessStartInfo(exe, App.RestartArgument) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            ErrorLog.Write("Restart failed", ex);
            MessageBox.Show(Strings.RestartFailed, "PresenterTools", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        ErrorLog.Write($"Restarting to change the display language: {_uiCulture.Name} -> {wanted.Name}");
        Exit();
        return true;
    }

    public void Exit()
    {
        if (IsExiting) return;
        SaveSettings();
        IsExiting = true;
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        IsExiting = true;
        _state.Changed -= OnStateChanged;
        _keyTimer.Stop();
        StopPasswordGate();
        _focusWorker?.Dispose();   // 以後の通知は回の番号で捨てる。購読の解除は 2 秒で打ち切る
        _focusWorker = null;
        _keyModel.Clear();
        _settingsWindow?.Close();
        if (_pen?.IsActive == true) _pen.Deactivate("exit");
        _pen?.Dispose();           // 描線はプロセスとともに消える
        _pen = null;
        _hook?.Dispose();          // フック解除（SPEC §22）
        _hotkeys?.Dispose();       // ホットキー解除
        _magnifier?.Dispose();
        _overlays?.Dispose();
        _tray?.Dispose();
        _monitors?.Dispose();
        _panel?.Close();
        _hook = null;
        _hotkeys = null;
        _magnifier = null;
        _overlays = null;
        _tray = null;
        _monitors = null;
        _panel = null;
    }

    // ---- 状態の反映 ----

    private void LoadSettings()
    {
        var result = _store.Load();
        _settings = result.Settings;
        // 表示言語は、通知文・トレイ・画面を作る前に決める（以降の画面・メッセージはこの言語で出る。SPEC §34）。
        _uiCulture = UiLanguage.Resolve(_settings.General.Language, _windowsUiCulture);
        UiLanguage.Apply(_uiCulture);
        _loadNotice = result.Status switch
        {
            SettingsLoadStatus.Corrupted => result.BackupPath is null
                ? Strings.LoadCorrupted
                : string.Format(Strings.LoadCorruptedBackupFormat, Path.GetFileName(result.BackupPath)),
            SettingsLoadStatus.ReadFailed => Strings.LoadReadFailed,
            SettingsLoadStatus.Loaded when result.Corrections.Count > 0 => Strings.LoadCorrected,
            _ => null,
        };
        if (result.Status is SettingsLoadStatus.Corrupted or SettingsLoadStatus.ReadFailed)
            ErrorLog.Write($"Settings load: {_store.FilePath} {result.Status} {result.Error}");
        else if (result.Corrections.Count > 0)
            ErrorLog.Write("Settings corrected: " + string.Join(", ", result.Corrections));
    }

    private void ApplySettingsToComponents()
    {
        _overlays?.ApplySettings(_settings);
        _magnifier?.ApplySettings(_settings.Magnifier);
        var suppressed = _settings.General.HideOwnShortcutsInKeyDisplay
            ? HotkeyBindings.Parse(_settings.Hotkeys).Values
            : Enumerable.Empty<Shortcut>();
        _keyModel.Options = KeyDisplayOptions.FromSettings(_settings.KeyDisplay, suppressed);
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        if (!_batch) ApplyState(save: e.PersistSettings);
    }

    private void ApplyState(bool save)
    {
        if (_overlays is null || _magnifier is null) return;
        _overlays.SetVisible(_state.MasterEnabled);

        // 画面ペン: Master OFF などで状態が OFF になったら入力面を閉じる（線は残す。D2・D7）。
        if (_pen is not null)
        {
            if (!_state.IsPenActive && _pen.IsActive) _pen.Deactivate(_penEndReason ?? "master");
            var masterTurnedOn = _state.MasterEnabled && !_masterWasEnabled;
            _masterWasEnabled = _state.MasterEnabled;
            if (masterTurnedOn && _pen.Owner is not null) RecheckPen("master"); // 契機 (3)
        }

        // Key Display: モードが変わったら表示中の文字列を即座に消す（SPEC §15.1）。
        if (_keyModel.SetMode(_state.EffectiveKeyDisplayMode))
        {
            _keyTimer.Stop();
            _overlays.HideKeyDisplay();
        }

        var laserActive = _state.IsLaserActive;
        if (laserActive != _laserWasActive)
        {
            _laserWasActive = laserActive;
            _overlays.SetLaserActive(laserActive, CursorPosition());
        }
        if (!_state.LaserTriggers.HasFlag(LaserTrigger.Keyboard)) _laserHoldKey = 0;

        var cursor = CursorPosition();
        _overlays.SetRingVisible(_state.IsCursorHighlightVisible && !laserActive && _pen?.Covers(cursor) != true, cursor);
        if (!_state.IsClickEffectVisible) _overlays.HideHold();

        if (_state.IsMagnifierVisible) _magnifier.Start();
        else _magnifier.Stop();

        UpdateHooks();
        UpdatePasswordGate();
        RefreshUi();
        if (save) SaveSettings();
    }

    // ---- パスワード欄での自動非表示（V1.6.0。詳細設計 §2） ----

    /// <summary>最終の状態から、監視を 1 回だけ開始・停止する（§2.4）。</summary>
    private void UpdatePasswordGate()
    {
        var want = !IsExiting && _hook is not null
            && _state.EffectiveKeyDisplayMode != KeyDisplayMode.Off
            && _settings.KeyDisplay.HidePasswordInput;
        if (want == _gate.IsActive) return;
        if (!want)
        {
            StopPasswordGate();
            return;
        }
        _focusWorker ??= new FocusWatchWorker(new UiaFocusedFieldProbe(), Stopwatch.GetTimestamp,
            ticks => TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency),
            notice => _dispatcher.BeginInvoke(DispatcherPriority.Input, () => OnFocusNotice(notice)),
            kind => _dispatcher.BeginInvoke(() => ErrorLog.Write("Password field check failed: " + kind)));
#if DEBUG
        _focusWorker.Trace ??= what => TestTrace.Write("worker", what);
#endif
        var generation = _gate.Start(Stopwatch.GetTimestamp());
        _focusWorker.Start(generation, _gate.NextQueryAt); // 開始時の照会を購読より先に行う
        TestTrace.Write("gate-start", generation.ToString(CultureInfo.InvariantCulture));
        PumpGate();
    }

    /// <summary>監視を止め、保留を捨てる。離したイベントだけは表示処理へ渡す（§2.3）。</summary>
    private void StopPasswordGate()
    {
        if (!_gate.IsActive) return;
        _gateTimer.Stop();
        _focusWorker?.Stop();
        foreach (var up in _gate.Stop()) _keyModel.Process(up, Now);
        TestTrace.Write("gate-stop");
    }

    private void OnFocusNotice(FocusNotice notice)
    {
        if (!_gate.IsActive || notice.Generation != _gate.Generation) return; // 停止・終了後や古い回の通知（D2）
        if (notice.Kind == FocusNoticeKind.QueryFailed)
        {
            _gate.OnQueryFailed(notice.Generation, notice.Time);
            TestTrace.Write("query-failed");
        }
        else
        {
            var isEvent = notice.Kind == FocusNoticeKind.Event;
            _gate.OnObservation(notice.Generation, notice.Field, notice.Time, notice.Foreground, isEvent, notice.End);
            TestTrace.Write(isEvent ? "event" : "query", $"{notice.Field}\t{notice.Time}\t{notice.End}");
        }
        PumpGate();
    }

    /// <summary>判定の済んだキーを順に表示処理へ渡し、次の照会と時間切れを予約する。</summary>
    private void PumpGate()
    {
        if (!_gate.IsActive) return;
        // 実行中の照会を判定に知らせてから判定する（区切りより前に始めた照会の結果を待つため。R1・R4）。
        if (_focusWorker?.LastQueryStart(_gate.Generation) is { } started) _gate.NoteQueryStarted(started);
        var resolved = _gate.TakeResolved(Stopwatch.GetTimestamp());
        foreach (var key in resolved)
        {
            // 表示先は、キーを押した時の前面の窓のモニター（§2.4、D6）。
            if (key.Foreground != IntPtr.Zero && _hook?.IsOwnWindow(key.Foreground) == false) _lastExternalForeground = key.Foreground;
            if (key.Hidden)
            {
                _keyModel.ObserveHidden(key.Input);
                TestTrace.Write("hide-key", key.Timestamp.ToString(CultureInfo.InvariantCulture));
                ClearKeyDisplayForPassword();
            }
            else
            {
                if (key.Input.IsKeyDown) TestTrace.Write("show-key", key.Timestamp.ToString(CultureInfo.InvariantCulture));
                if (_keyModel.Process(key.Input, Now)) RefreshKeyDisplay();
            }
        }
        if (_gate.ConsumePasswordObserved()) ClearKeyDisplayForPassword();
        if (_gate.TimedOutCount > 0 && !_gateTimeoutLogged)
        {
            _gateTimeoutLogged = true;
            ErrorLog.Write("Password field check timed out");
        }

        if (_gate.NextQueryAt is { } at) _focusWorker?.RequestQuery(_gate.Generation, at);
        _gateTimer.Stop();
        if (_gate.NextTimeoutAt is { } deadline)
        {
            var wait = TimeSpan.FromSeconds((double)(deadline - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
            _gateTimer.Interval = wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait;
            _gateTimer.Start();
        }
    }

    /// <summary>表示中の文字・キー名を消す。除外中のショートカットの押下状態は残す（§2.3、D4）。</summary>
    private void ClearKeyDisplayForPassword()
    {
        var wasEmpty = _keyModel.IsEmpty;
        _keyModel.ClearDisplay();
        _keyTimer.Stop();
        _overlays?.HideKeyDisplay();
        if (!wasEmpty) TestTrace.Write("clear");
    }

    /// <summary>必要なときだけフックを導入する（SPEC §11.4・§21）。</summary>
    private void UpdateHooks()
    {
        if (_hook is null) return;
        var master = _state.MasterEnabled;
        var laserButton = _state.IsLaserReady ? _settings.Laser.MouseButton : MouseTriggerButton.None;
        var magnifierButton = master ? _settings.Magnifier.MouseButton : MouseTriggerButton.None;
        var mouse = master && (_state.CursorHighlightEnabled || _state.ClickEffectEnabled || _state.LaserEnabled
            || laserButton != MouseTriggerButton.None || magnifierButton != MouseTriggerButton.None);
        var keyboard = _state.EffectiveKeyDisplayMode != KeyDisplayMode.Off;
        _hook.Configure(mouse, keyboard, laserButton, magnifierButton);
    }

    private void RefreshUi()
    {
        _panel?.Refresh(_state, _settings, BuildStatus(), BuildShortcutHints(), IsHotkeyFailed);
        _tray?.Update(_state);
    }

    /// <summary>操作パネルに添えるショートカットの表記（SPEC §10.1）。表示しない設定なら空。</summary>
    private ShortcutHints BuildShortcutHints() =>
        _settings.General.ShowShortcutsInPanel
            ? ShortcutHints.Build(HotkeyBindings.Parse(_settings.Hotkeys), ControlPanelWindow.ShortcutActions)
            : ShortcutHints.Empty;

    private bool IsHotkeyFailed(HotkeyAction action) => _hotkeys?.Get(action)?.Status == HotkeyStatus.Failed;

    private string? BuildStatus()
    {
        var lines = new List<string>();
        var failed = _hotkeys?.Failures.Count() ?? 0;
        if (failed > 0) lines.Add(string.Format(Strings.StatusHotkeyFailedFormat, failed));
        // 設定画面のショートカット欄にフォーカスがある間は全ショートカットを一時解除している（D-17）。押しても反応しない理由を示す。
        if (_hotkeys?.IsSuspended == true) lines.Add(Strings.StatusHotkeysSuspended);
        if (_hookError is not null) lines.Add("⚠ " + _hookError);
        if (_saveError is not null) lines.Add("⚠ " + _saveError);
        if (_penNotice is not null) lines.Add("ℹ " + _penNotice);
        if (_loadNotice is not null) lines.Add("ℹ " + _loadNotice);
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static PointInt CursorPosition() =>
        GetCursorPos(out var p) ? new PointInt(p.X, p.Y) : default;

    // ---- 入力 ----

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleMaster:
                _state.ToggleMaster();
                break;
            case HotkeyAction.ShowControlPanel:
                ShowPanel();
                break;
            case HotkeyAction.ToggleCursorHighlight:
                _state.SetCursorHighlight(!_state.CursorHighlightEnabled);
                break;
            case HotkeyAction.ToggleClickEffect:
                _state.SetClickEffect(!_state.ClickEffectEnabled);
                break;
            case HotkeyAction.ToggleLaser:
                _state.ToggleLaser();
                break;
            case HotkeyAction.LaserHold:
                if (IsLaserToggleMode)
                {
                    _state.ToggleLaserDrawing(LaserTrigger.Keyboard);
                    break;
                }
                var key = Hotkeys.Get(HotkeyAction.LaserHold)?.Shortcut.Key ?? 0;
                if (key != 0 && _state.BeginLaser(LaserTrigger.Keyboard)) _laserHoldKey = key;
                break;
            case HotkeyAction.ToggleMagnifier:
                _state.ToggleMagnifier();
                break;
            case HotkeyAction.ToggleKeyDisplay:
                _state.ToggleKeyDisplay();
                break;
            case HotkeyAction.TogglePen:
                TogglePen(fromHotkey: true);
                break;
        }
    }

    private bool IsLaserToggleMode => _settings.Laser.TriggerMode == LaserTriggerMode.Toggle;

    /// <summary>
    /// アニメーション中のフレームごと。Hold では Laser 描画キーが離されたら、Toggle では Esc が押されたら ACTIVE を終える。
    /// Esc は観測するだけで、前面のアプリにもそのまま届く（SPEC §8.4）。
    /// </summary>
    private void OnFrame()
    {
        if (_laserHoldKey != 0 && !IsKeyDown(_laserHoldKey))
        {
            _laserHoldKey = 0;
            _state.EndLaser(LaserTrigger.Keyboard);
        }
        if (IsLaserToggleMode && _state.IsLaserActive && IsKeyDown(VirtualKeys.Escape)) _state.EndAllLaser();
    }

    private void OnMouseMoved(PointInt p)
    {
        if (_overlays is null) return;
        if (_state.IsLaserActive) _overlays.AddLaserPoint(p);
        else if (_state.IsCursorHighlightVisible)
        {
            // ペン中の書き込み先ではリングを隠し、出たら現在位置に出し直す（D8）。
            if (_pen?.Covers(p) == true) _overlays.SetRingVisible(false);
            else _overlays.SetRingVisible(true, p);
        }
        _overlays.MoveHold(p);
    }

    private void OnMouseButton(MouseButtonKind kind, bool down, PointInt p)
    {
        switch (kind)
        {
            case MouseButtonKind.Left:
            case MouseButtonKind.Right:
                if (_overlays is null) break;
                var isRight = kind == MouseButtonKind.Right;
                // ペン中の書き込み先でのクリックには波紋・押下中の印を出さない（D8）。
                var effect = _state.IsClickEffectVisible && _pen?.Covers(p) != true;
                if (down && effect) _overlays.AddClick(p, isRight);
                // 解放は常に伝える（表示中に OFF にされても押下状態を残さないため）。
                if (effect || !down) _overlays.SetButtonHeld(isRight, down, p);
                break;
            case MouseButtonKind.XButton1:
            case MouseButtonKind.XButton2:
                var button = kind == MouseButtonKind.XButton1 ? MouseTriggerButton.XButton1 : MouseTriggerButton.XButton2;
                if (button == _settings.Laser.MouseButton)
                {
                    if (IsLaserToggleMode)
                    {
                        if (down) _state.ToggleLaserDrawing(LaserTrigger.Mouse);
                    }
                    else if (down) _state.BeginLaser(LaserTrigger.Mouse);
                    else _state.EndLaser(LaserTrigger.Mouse);
                }
                else if (button == _settings.Magnifier.MouseButton && down && _state.MasterEnabled)
                {
                    _state.ToggleMagnifier();
                }
                break;
        }
    }

    private void OnKeyEvent(KeyInput input, IntPtr foreground, long timestamp)
    {
        if (_hook is null) return;
        if (_gate.IsActive)
        {
            // パスワード欄での自動非表示が働いている間は、判定が済むまで保留する（V1.6.0。詳細設計 §2）。
            _gate.Enqueue(input, timestamp, foreground);
            PumpGate();
            return;
        }
        if (foreground != IntPtr.Zero && !_hook.IsOwnWindow(foreground)) _lastExternalForeground = foreground;
        if (_keyModel.Process(input, Now)) RefreshKeyDisplay();
    }

    private void RefreshKeyDisplay()
    {
        if (_overlays is null || _monitors is null) return;
        // 表示先はアクティブウィンドウのモニター。自分のウィンドウなら直前の他アプリのモニター（SPEC §13）。
        var target = _lastExternalForeground != IntPtr.Zero ? _monitors.FromWindow(_lastExternalForeground) : _monitors.Primary;
        _overlays.ShowKeyDisplay(target, _keyModel);
        TestTrace.Write("show", $"{TestTrace.Map(_keyModel.DisplayText)}\t{TestTrace.MapChord(_keyModel.ChordKeys)}\t{target.DeviceName}");
        ScheduleKeyTimer();
    }

    private void ScheduleKeyTimer()
    {
        _keyTimer.Stop();
        if (_keyModel.NextDeadline is not { } deadline) return;
        var wait = deadline - Now;
        _keyTimer.Interval = wait < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(15) : wait;
        _keyTimer.Start();
    }

    private void OnKeyTimer(object? sender, EventArgs e)
    {
        _keyTimer.Stop();
        if (_keyModel.Tick(Now)) RefreshKeyDisplay();
        else ScheduleKeyTimer();
    }

    private void OnHookFailed(bool mouse, int error)
    {
        var feature = mouse ? Strings.HookFeatureMouse : Strings.HookFeatureKeyboard;
        _hookError = string.Format(Strings.HookFailedFormat, feature, error);
        ErrorLog.Write(_hookError);
        RefreshUi();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (_overlays is null || _monitors is null) return;
        // 順序は docs/archive/design-1.7.0.md D11 の「再構築の順序」。
        _pen?.Detach();
        _overlays.Rebuild();
        _panel?.EnsureOnScreen(_monitors.Monitors);
        _pen?.OnDisplaysChanged("settings-changed", _monitors.Snapshot, _overlays);
        ClearPenGuideIfResolved(_monitors.Snapshot);
        ApplyState(save: false);
        RaisePanel();
        _settingsWindow?.OnDisplaysChanged();
    }
}
