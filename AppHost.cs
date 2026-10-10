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
public sealed class AppHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _store = new(SettingsStore.DefaultFilePath);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly KeyDisplayModel _keyModel = new();
    private readonly DispatcherTimer _keyTimer;
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

    public void ShowSettings()
    {
        if (_settingsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

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
        _settings = newSettings;
        ApplySettingsToComponents();

        _batch = true;
        try
        {
            // 描画方式を変えたら描画中のものは終える（押している間／押すたびの判断が食い違わないように）。
            if (triggerModeChanged) _state.EndAllLaser();
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
        if (_state.KeyDisplayMode != KeyDisplayMode.Off || _state.MagnifierEnabled || _state.IsLaserActive)
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
        _keyModel.Clear();
        _settingsWindow?.Close();
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

        _overlays.SetRingVisible(_state.IsCursorHighlightVisible && !laserActive, CursorPosition());
        if (!_state.IsClickEffectVisible) _overlays.HideHold();

        if (_state.IsMagnifierVisible) _magnifier.Start();
        else _magnifier.Stop();

        UpdateHooks();
        RefreshUi();
        if (save) SaveSettings();
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
        else if (_state.IsCursorHighlightVisible) _overlays.MoveRing(p);
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
                if (down && _state.IsClickEffectVisible) _overlays.AddClick(p, isRight);
                // 解放は常に伝える（表示中に OFF にされても押下状態を残さないため）。
                if (_state.IsClickEffectVisible || !down) _overlays.SetButtonHeld(isRight, down, p);
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

    private void OnKeyEvent(KeyInput input, IntPtr foreground)
    {
        if (_hook is null) return;
        if (foreground != IntPtr.Zero && !_hook.IsOwnWindow(foreground)) _lastExternalForeground = foreground;
        if (_keyModel.Process(input, Now)) RefreshKeyDisplay();
    }

    private void RefreshKeyDisplay()
    {
        if (_overlays is null || _monitors is null) return;
        // 表示先はアクティブウィンドウのモニター。自分のウィンドウなら直前の他アプリのモニター（SPEC §13）。
        var target = _lastExternalForeground != IntPtr.Zero ? _monitors.FromWindow(_lastExternalForeground) : _monitors.Primary;
        _overlays.ShowKeyDisplay(target, _keyModel);
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
        _overlays.Rebuild();
        _panel?.EnsureOnScreen(_monitors.Monitors);
        ApplyState(save: false);
    }
}
