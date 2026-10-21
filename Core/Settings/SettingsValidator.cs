using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

/// <summary>
/// Settings 画面の検証で見つかった問題 1 件（V1.5.0。SPEC §10.2）。
/// <paramref name="Section"/> は直す欄があるタブ、<paramref name="Field"/> は設定ファイルの項目名（"laser.color" など）。
/// ショートカットの問題は <paramref name="Action"/> も持つ（同じ欄が General と各機能のタブの両方にあるため）。
/// </summary>
public sealed record SettingsProblem(SettingsSection Section, string Field, string Message, HotkeyAction? Action = null);

/// <summary>［適用］／［OK］の前に設定を検証する（色の形式・ショートカットの入力規則と重複・マウスボタンの重複）。</summary>
public static class SettingsValidator
{
    public const string LaserMouseField = "laser.mouseButton";
    public const string MagnifierMouseField = "magnifier.mouseButton";

    /// <summary>問題を画面の上から順に返す。問題が無ければ空。</summary>
    public static IReadOnlyList<SettingsProblem> Validate(AppSettings s)
    {
        var problems = new List<SettingsProblem>();
        void Color(SettingsSection section, string field, string name, string value)
        {
            if (!ArgbColor.IsValid(value))
                problems.Add(new(section, field, string.Format(Strings.ValidationColorFormat, name, value)));
        }

        foreach (var issue in HotkeyBindings.Validate(s.Hotkeys))
            problems.Add(new(SectionOf(issue.Action), "hotkeys." + System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(issue.Action.ToString()),
                string.Format(Strings.ValidationHotkeyFormat, HotkeyActionNames.GetDisplayName(issue.Action), issue.Message), issue.Action));

        Color(SettingsSection.CursorHighlight, "cursorHighlight.color", Strings.ColorNameCursor, s.CursorHighlight.Color);
        Color(SettingsSection.ClickEffect, "clickEffect.leftColor", Strings.ColorNameClickLeft, s.ClickEffect.LeftColor);
        Color(SettingsSection.ClickEffect, "clickEffect.rightColor", Strings.ColorNameClickRight, s.ClickEffect.RightColor);
        Color(SettingsSection.Laser, "laser.color", Strings.ColorNameLaser, s.Laser.Color);
        if (HasSameMouseButton(s))
            problems.Add(new(SettingsSection.Laser, LaserMouseField, Strings.ValidationSameMouseButton));
        Color(SettingsSection.KeyDisplay, "keyDisplay.textColor", Strings.ColorNameKeyText, s.KeyDisplay.TextColor);
        Color(SettingsSection.KeyDisplay, "keyDisplay.backgroundColor", Strings.ColorNameKeyBackground, s.KeyDisplay.BackgroundColor);
        return problems;
    }

    /// <summary>Laser と Magnifier に同じマウスボタンを割り当てている。</summary>
    public static bool HasSameMouseButton(AppSettings s) =>
        s.Laser.MouseButton != MouseTriggerButton.None && s.Laser.MouseButton == s.Magnifier.MouseButton;

    /// <summary>
    /// Laser を描く操作（描画ショートカット・マウスボタン）が 1 つでも割り当てられているか（V1.5.0。SPEC §8.4・§10.1）。
    /// ON/OFF ショートカットでは描けないので数えない。登録に失敗している場合は割り当てありとして扱う（別に失敗を表示する）。
    /// </summary>
    public static bool HasLaserTrigger(AppSettings s) =>
        Shortcut.TryParse(s.Hotkeys.LaserHold, out var sc) && !sc.IsEmpty || s.Laser.MouseButton != MouseTriggerButton.None;

    /// <summary>そのショートカットの欄がある機能のタブ（Master・操作パネルは General だけにある）。</summary>
    public static SettingsSection SectionOf(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleCursorHighlight => SettingsSection.CursorHighlight,
        HotkeyAction.ToggleClickEffect => SettingsSection.ClickEffect,
        HotkeyAction.ToggleLaser or HotkeyAction.LaserHold => SettingsSection.Laser,
        HotkeyAction.ToggleMagnifier => SettingsSection.Magnifier,
        HotkeyAction.ToggleKeyDisplay => SettingsSection.KeyDisplay,
        _ => SettingsSection.General,
    };
}
