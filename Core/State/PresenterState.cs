using PresenterTools.Core.Models;

namespace PresenterTools.Core.State;

/// <summary>Laser を ACTIVE にしている入力源。キーボードとマウスを同時に押していても正しく扱う。</summary>
[Flags]
public enum LaserTrigger
{
    None = 0,
    Keyboard = 1,
    Mouse = 2,
}

/// <summary>操作パネルに表示する Laser の状態。</summary>
public enum LaserStatus
{
    Off,
    Ready,
    Active,
}

public sealed class StateChangedEventArgs(bool persistSettings) : EventArgs
{
    public bool PersistSettings { get; } = persistSettings;
}

/// <summary>
/// 実行時の ON/OFF 状態と状態遷移（SPEC §8.1・§8.4・§15.1・§32 D-11）。
/// 個別の ON/OFF は Master OFF の間も保持し、実際に表示するかどうかは Is*Visible / Effective* で判断する。
/// UI スレッドからのみ操作する前提（スレッドセーフではない）。
/// </summary>
public sealed class PresenterState
{
    private bool _master = true;
    private bool _cursorHighlight;
    private bool _clickEffect;
    private bool _laser;
    private bool _magnifier;
    private KeyDisplayMode _keyDisplayMode = KeyDisplayMode.Off;
    private LaserTrigger _laserTriggers;

    /// <summary>いずれかの状態が変わったときに発生する。</summary>
    public event EventHandler<StateChangedEventArgs>? Changed;

    public bool MasterEnabled => _master;
    public bool CursorHighlightEnabled => _cursorHighlight;
    public bool ClickEffectEnabled => _clickEffect;
    public bool LaserEnabled => _laser;
    public bool MagnifierEnabled => _magnifier;
    public KeyDisplayMode KeyDisplayMode => _keyDisplayMode;
    public LaserTrigger LaserTriggers => _laserTriggers;

    public bool IsCursorHighlightVisible => _master && _cursorHighlight;
    public bool IsClickEffectVisible => _master && _clickEffect;
    public bool IsLaserReady => _master && _laser;
    public bool IsLaserActive => IsLaserReady && _laserTriggers != LaserTrigger.None;
    public bool IsMagnifierVisible => _master && _magnifier;
    public KeyDisplayMode EffectiveKeyDisplayMode => _master ? _keyDisplayMode : KeyDisplayMode.Off;

    public LaserStatus LaserStatus => !IsLaserReady ? LaserStatus.Off : IsLaserActive ? LaserStatus.Active : LaserStatus.Ready;

    /// <summary>保存済み設定から起動時の状態を作る。Key Display は常に Off、Magnifier は常に OFF。</summary>
    public static PresenterState FromSettings(AppSettings settings) => new()
    {
        _master = settings.General.MasterEnabled,
        _cursorHighlight = settings.CursorHighlight.Enabled,
        _clickEffect = settings.ClickEffect.Enabled,
        _laser = settings.Laser.Enabled,
        _magnifier = false,
        _keyDisplayMode = KeyDisplayMode.Off,
    };

    /// <summary>保存対象の ON/OFF を設定へ書き戻す（Key Display・Magnifier は保存しない）。</summary>
    public void WriteTo(AppSettings settings)
    {
        settings.General.MasterEnabled = _master;
        settings.CursorHighlight.Enabled = _cursorHighlight;
        settings.ClickEffect.Enabled = _clickEffect;
        settings.Laser.Enabled = _laser;
    }

    public void SetMaster(bool on)
    {
        if (_master == on) return;
        _master = on;
        if (!on) _laserTriggers = LaserTrigger.None;
        OnChanged();
    }

    public void ToggleMaster() => SetMaster(!_master);

    public void SetCursorHighlight(bool on) => Set(ref _cursorHighlight, on);
    public void SetClickEffect(bool on) => Set(ref _clickEffect, on);

    public void SetLaser(bool on)
    {
        if (_laser == on) return;
        _laser = on;
        if (!on) _laserTriggers = LaserTrigger.None;
        OnChanged();
    }

    public void ToggleLaser() => SetLaser(!_laser);

    public void SetMagnifier(bool on) => Set(ref _magnifier, on, persistSettings: false);
    public void ToggleMagnifier() => SetMagnifier(!_magnifier);

    public void SetKeyDisplayMode(KeyDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) mode = KeyDisplayMode.Off;
        if (_keyDisplayMode == mode) return;
        _keyDisplayMode = mode;
        OnChanged(persistSettings: false);
    }

    /// <summary>Key Display の ON/OFF を切り替える（D-36）。</summary>
    public void ToggleKeyDisplay() =>
        SetKeyDisplayMode(_keyDisplayMode == KeyDisplayMode.Off ? KeyDisplayMode.FullText : KeyDisplayMode.Off);

    /// <summary>Key Display を OFF にする（トレイメニュー）。表示中文字列の消去は Key Display 側がモード変化を受けて行う。</summary>
    public void KeyDisplayOff() => SetKeyDisplayMode(KeyDisplayMode.Off);

    /// <summary>トリガー押下。Laser が READY でなければ無視して false。</summary>
    public bool BeginLaser(LaserTrigger trigger)
    {
        if (!IsLaserReady || trigger == LaserTrigger.None) return false;
        if (_laserTriggers.HasFlag(trigger)) return true;
        var wasActive = IsLaserActive;
        _laserTriggers |= trigger;
        if (!wasActive) OnChanged(persistSettings: false);
        return true;
    }

    /// <summary>トリガー解放。すべての入力源が離されたら ACTIVE を終える。</summary>
    public void EndLaser(LaserTrigger trigger)
    {
        if ((_laserTriggers & trigger) == 0) return;
        var wasActive = IsLaserActive;
        _laserTriggers &= ~trigger;
        if (wasActive != IsLaserActive) OnChanged(persistSettings: false);
    }

    /// <summary>
    /// 描画方式 Toggle のトリガー押下（SPEC §8.4）。ACTIVE なら（どのトリガーで始めたかに関係なく）終了し、
    /// READY なら <paramref name="trigger"/> で開始する。Laser が READY でなければ何もしない。
    /// </summary>
    public void ToggleLaserDrawing(LaserTrigger trigger)
    {
        if (IsLaserActive) EndAllLaser();
        else BeginLaser(trigger);
    }

    /// <summary>すべてのトリガーを離したことにして ACTIVE を終える。</summary>
    public void EndAllLaser() => EndLaser(LaserTrigger.Keyboard | LaserTrigger.Mouse);

    private void Set(ref bool field, bool value, bool persistSettings = true)
    {
        if (field == value) return;
        field = value;
        OnChanged(persistSettings);
    }

    private void OnChanged(bool persistSettings = true) => Changed?.Invoke(this, new StateChangedEventArgs(persistSettings));
}
