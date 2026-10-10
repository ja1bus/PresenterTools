using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

/// <summary>Settings 画面のタブ（初期化の単位）。</summary>
public enum SettingsSection
{
    General,
    CursorHighlight,
    ClickEffect,
    Laser,
    Magnifier,
    KeyDisplay,
}

/// <summary>
/// 設定を既定値へ戻す（SPEC §10.2）。Settings 画面の編集用の複製に対して使い、反映は［適用］／［OK］で行う。
/// </summary>
public static class SettingsReset
{
    /// <summary>
    /// タブ 1 つ分を既定値に戻す。各機能の ON/OFF（Master・Enabled）とショートカットは変えない
    /// （ON/OFF は設定ではなく状態で、ショートカットは他のアプリと競合し得るため専用の操作で戻す）。
    /// </summary>
    public static void ResetSection(AppSettings s, SettingsSection section)
    {
        switch (section)
        {
            case SettingsSection.General:
                // 表示言語は戻さない（見た目の初期化で再起動が要らないように。D-39）。［すべての設定を初期化］では戻す。
                s.General = new GeneralSettings { MasterEnabled = s.General.MasterEnabled, Language = s.General.Language };
                break;
            case SettingsSection.CursorHighlight:
                s.CursorHighlight = new CursorHighlightSettings { Enabled = s.CursorHighlight.Enabled };
                break;
            case SettingsSection.ClickEffect:
                s.ClickEffect = new ClickEffectSettings { Enabled = s.ClickEffect.Enabled };
                break;
            case SettingsSection.Laser:
                s.Laser = new LaserSettings { Enabled = s.Laser.Enabled };
                break;
            case SettingsSection.Magnifier:
                s.Magnifier = new MagnifierSettings();
                break;
            case SettingsSection.KeyDisplay:
                s.KeyDisplay = new KeyDisplaySettings();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section));
        }
    }

    /// <summary>
    /// タブ 1 つ分を <paramref name="original"/>（設定画面を開いたときの値）に戻す（SPEC §10.2 の［開いたときの値に戻す］）。
    /// 既定値に戻す操作と違い、そのタブに表示している ON/OFF とショートカットも戻す。
    /// General は機能の ON/OFF とすべてのショートカットを表示しているため、それらも含む。
    /// Magnifier の ON・Key Display の現在モードは設定に含まれないため、呼び出し側で戻す。
    /// </summary>
    public static void RevertSection(AppSettings s, AppSettings original, SettingsSection section)
    {
        var o = original.Clone();
        switch (section)
        {
            case SettingsSection.General:
                s.General = o.General;
                s.CursorHighlight.Enabled = o.CursorHighlight.Enabled;
                s.ClickEffect.Enabled = o.ClickEffect.Enabled;
                s.Laser.Enabled = o.Laser.Enabled;
                break;
            case SettingsSection.CursorHighlight:
                s.CursorHighlight = o.CursorHighlight;
                break;
            case SettingsSection.ClickEffect:
                s.ClickEffect = o.ClickEffect;
                break;
            case SettingsSection.Laser:
                s.Laser = o.Laser;
                break;
            case SettingsSection.Magnifier:
                s.Magnifier = o.Magnifier;
                break;
            case SettingsSection.KeyDisplay:
                s.KeyDisplay = o.KeyDisplay;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section));
        }
        foreach (var action in HotkeysOf(section)) s.Hotkeys.Set(action, o.Hotkeys.Get(action));
    }

    /// <summary>そのタブに表示しているショートカット。</summary>
    public static IReadOnlyList<HotkeyAction> HotkeysOf(SettingsSection section) => section switch
    {
        SettingsSection.General => HotkeyActionNames.All,
        SettingsSection.CursorHighlight => [HotkeyAction.ToggleCursorHighlight],
        SettingsSection.ClickEffect => [HotkeyAction.ToggleClickEffect],
        SettingsSection.Laser => [HotkeyAction.LaserHold, HotkeyAction.ToggleLaser],
        SettingsSection.Magnifier => [HotkeyAction.ToggleMagnifier],
        SettingsSection.KeyDisplay => [HotkeyAction.ToggleKeyDisplay],
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    /// <summary>ショートカットをすべて既定値に戻す。</summary>
    public static void ResetHotkeys(AppSettings s) => s.Hotkeys = new HotkeySettings();

    /// <summary>
    /// すべての設定（ON/OFF・ショートカットを含む）を既定値に戻す。操作パネルの位置・サイズは残す。
    /// </summary>
    public static void ResetAll(AppSettings s)
    {
        var d = new AppSettings();
        s.SchemaVersion = d.SchemaVersion;
        s.General = d.General;
        s.Hotkeys = d.Hotkeys;
        s.CursorHighlight = d.CursorHighlight;
        s.ClickEffect = d.ClickEffect;
        s.Laser = d.Laser;
        s.Magnifier = d.Magnifier;
        s.KeyDisplay = d.KeyDisplay;
    }
}
