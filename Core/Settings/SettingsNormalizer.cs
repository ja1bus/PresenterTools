using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

/// <summary>
/// 設定値の範囲・形式を検証し、不正な値を既定値（または範囲内）へ補正する（SPEC §17）。
/// 補正した項目名は <see cref="Normalize"/> の戻り値で返す（ログ・テスト用）。
/// </summary>
public static class SettingsNormalizer
{
    public const double MinOpacity = 0.1, MaxOpacity = 1.0;
    public const double MinFillOpacity = 0.05, MaxFillOpacity = 0.8;
    public const double MinRingDiameter = 20, MaxRingDiameter = 300;
    public const double MinRingThickness = 1, MaxRingThickness = 30;
    public const double MinClickSize = 20, MaxClickSize = 300;
    public const int MinClickDurationMs = 150, MaxClickDurationMs = 1500;
    public const double MinLaserWidth = 2, MaxLaserWidth = 40;
    public const double MinTrailSeconds = 0.2, MaxTrailSeconds = 3.0;
    public const double MinMagnifierWidth = 200, MaxMagnifierWidth = 1200;
    public const double MinMagnifierHeight = 120, MaxMagnifierHeight = 800;
    public const double MinFontSize = 16, MaxFontSize = 120;
    public const double MinChordSeconds = 0.5, MaxChordSeconds = 10;
    public const double MinTextSeconds = 1, MaxTextSeconds = 30;
    public const double MinEnterHoldSeconds = 0.3, MaxEnterHoldSeconds = 10;
    public const double MinPanelWidth = 220, MaxPanelWidth = 2000;
    public const double MinPanelHeight = 260, MaxPanelHeight = 2000;

    public static readonly IReadOnlyList<int> ZoomPercents = [200, 300, 400];

    public static IReadOnlyList<string> Normalize(AppSettings s)
    {
        var fixes = new List<string>();
        var d = new AppSettings();

        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
        s.General ??= Fix(fixes, "general", new GeneralSettings());
        s.Hotkeys ??= Fix(fixes, "hotkeys", new HotkeySettings());
        s.CursorHighlight ??= Fix(fixes, "cursorHighlight", new CursorHighlightSettings());
        s.ClickEffect ??= Fix(fixes, "clickEffect", new ClickEffectSettings());
        s.Laser ??= Fix(fixes, "laser", new LaserSettings());
        s.Magnifier ??= Fix(fixes, "magnifier", new MagnifierSettings());
        s.KeyDisplay ??= Fix(fixes, "keyDisplay", new KeyDisplaySettings());
        s.ControlPanel ??= Fix(fixes, "controlPanel", new ControlPanelSettings());

        // 表示言語（V1.3.1）。無い（V1.3.0 以前）なら Windows に合わせる。大文字・空白の違いは黙って直し、知らない値だけ記録する。
        var language = UiLanguage.Normalize(s.General.Language);
        if (language == UiLanguage.System && !string.IsNullOrWhiteSpace(s.General.Language)) fixes.Add("general.language");
        s.General.Language = language;

        var ch = s.CursorHighlight;
        ch.Style = EnumValue(fixes, "cursorHighlight.style", ch.Style, d.CursorHighlight.Style);
        // 廃止した十字線は不正値ではなく旧版の値なので、補正の記録（読み込み時の通知）は出さずにリングへ移す。
        if (ch.Style == CursorHighlightStyle.Crosshair) ch.Style = CursorHighlightStyle.Ring;
        ch.Color = Color(fixes, "cursorHighlight.color", ch.Color, d.CursorHighlight.Color);
        ch.Opacity = Range(fixes, "cursorHighlight.opacity", ch.Opacity, MinOpacity, MaxOpacity, d.CursorHighlight.Opacity);
        ch.FillOpacity = Range(fixes, "cursorHighlight.fillOpacity", ch.FillOpacity, MinFillOpacity, MaxFillOpacity, d.CursorHighlight.FillOpacity);
        ch.Diameter = Range(fixes, "cursorHighlight.diameter", ch.Diameter, MinRingDiameter, MaxRingDiameter, d.CursorHighlight.Diameter);
        ch.Thickness = Range(fixes, "cursorHighlight.thickness", ch.Thickness, MinRingThickness, MaxRingThickness, d.CursorHighlight.Thickness);

        var ce = s.ClickEffect;
        ce.LeftColor = Color(fixes, "clickEffect.leftColor", ce.LeftColor, d.ClickEffect.LeftColor);
        ce.RightColor = Color(fixes, "clickEffect.rightColor", ce.RightColor, d.ClickEffect.RightColor);
        ce.Size = Range(fixes, "clickEffect.size", ce.Size, MinClickSize, MaxClickSize, d.ClickEffect.Size);
        ce.DurationMs = (int)Range(fixes, "clickEffect.durationMs", ce.DurationMs, MinClickDurationMs, MaxClickDurationMs, d.ClickEffect.DurationMs);

        var la = s.Laser;
        la.Color = Color(fixes, "laser.color", la.Color, d.Laser.Color);
        la.Width = Range(fixes, "laser.width", la.Width, MinLaserWidth, MaxLaserWidth, d.Laser.Width);
        la.TrailSeconds = Range(fixes, "laser.trailSeconds", la.TrailSeconds, MinTrailSeconds, MaxTrailSeconds, d.Laser.TrailSeconds);
        la.TriggerMode = EnumValue(fixes, "laser.triggerMode", la.TriggerMode, d.Laser.TriggerMode);
        la.MouseButton = EnumValue(fixes, "laser.mouseButton", la.MouseButton, d.Laser.MouseButton);

        var mg = s.Magnifier;
        if (!ZoomPercents.Contains(mg.ZoomPercent))
        {
            fixes.Add("magnifier.zoomPercent");
            mg.ZoomPercent = d.Magnifier.ZoomPercent;
        }
        mg.Width = Range(fixes, "magnifier.width", mg.Width, MinMagnifierWidth, MaxMagnifierWidth, d.Magnifier.Width);
        mg.Height = Range(fixes, "magnifier.height", mg.Height, MinMagnifierHeight, MaxMagnifierHeight, d.Magnifier.Height);
        mg.MouseButton = EnumValue(fixes, "magnifier.mouseButton", mg.MouseButton, d.Magnifier.MouseButton);
        if (mg.MouseButton != MouseTriggerButton.None && mg.MouseButton == la.MouseButton)
        {
            fixes.Add("magnifier.mouseButton");
            mg.MouseButton = MouseTriggerButton.None;
        }

        var kd = s.KeyDisplay;
        kd.FontSize = Range(fixes, "keyDisplay.fontSize", kd.FontSize, MinFontSize, MaxFontSize, d.KeyDisplay.FontSize);
        kd.Position = EnumValue(fixes, "keyDisplay.position", kd.Position, d.KeyDisplay.Position);
        kd.TextColor = Color(fixes, "keyDisplay.textColor", kd.TextColor, d.KeyDisplay.TextColor);
        kd.BackgroundColor = Color(fixes, "keyDisplay.backgroundColor", kd.BackgroundColor, d.KeyDisplay.BackgroundColor);
        kd.BackgroundOpacity = Range(fixes, "keyDisplay.backgroundOpacity", kd.BackgroundOpacity, 0, 1, d.KeyDisplay.BackgroundOpacity);
        kd.ChordSeconds = Range(fixes, "keyDisplay.chordSeconds", kd.ChordSeconds, MinChordSeconds, MaxChordSeconds, d.KeyDisplay.ChordSeconds);
        kd.TextSeconds = Range(fixes, "keyDisplay.textSeconds", kd.TextSeconds, MinTextSeconds, MaxTextSeconds, d.KeyDisplay.TextSeconds);
        kd.EnterHoldSeconds = Range(fixes, "keyDisplay.enterHoldSeconds", kd.EnterHoldSeconds, MinEnterHoldSeconds, MaxEnterHoldSeconds, d.KeyDisplay.EnterHoldSeconds);

        var cp = s.ControlPanel;
        cp.Width = Range(fixes, "controlPanel.width", cp.Width, MinPanelWidth, MaxPanelWidth, d.ControlPanel.Width);
        cp.Height = Range(fixes, "controlPanel.height", cp.Height, MinPanelHeight, MaxPanelHeight, d.ControlPanel.Height);
        if (cp.Left.HasValue != cp.Top.HasValue)
        {
            fixes.Add("controlPanel.position");
            cp.Left = cp.Top = null;
        }

        NormalizeHotkeys(s.Hotkeys, fixes);
        return fixes;
    }

    /// <summary>
    /// ショートカットの解析・規則・重複を検証する。不正な項目は既定値へ、既定値も他と重複するなら未割当にする。
    /// 表記も正規形（"Ctrl+Shift+F9"）にそろえる。
    /// </summary>
    private static void NormalizeHotkeys(HotkeySettings hotkeys, List<string> fixes)
    {
        var defaults = new HotkeySettings();
        var used = new HashSet<Shortcut>();
        var pending = new List<HotkeyAction>();

        foreach (var action in HotkeyActionNames.All)
        {
            var raw = hotkeys.Get(action);
            if (string.IsNullOrWhiteSpace(raw))
            {
                hotkeys.Set(action, "");
                continue;
            }
            if (Shortcut.TryParse(raw, out var sc) && sc.Validate() == ShortcutValidation.Ok && used.Add(sc))
            {
                hotkeys.Set(action, sc.ToString());
                continue;
            }
            fixes.Add($"hotkeys.{action}");
            pending.Add(action);
        }

        foreach (var action in pending)
        {
            var fallback = Shortcut.TryParse(defaults.Get(action), out var sc) && used.Add(sc) ? sc : Shortcut.None;
            hotkeys.Set(action, fallback.ToString());
        }
    }

    private static T Fix<T>(List<string> fixes, string name, T value)
    {
        fixes.Add(name);
        return value;
    }

    private static double Range(List<string> fixes, string name, double value, double min, double max, double fallback)
    {
        if (!double.IsFinite(value))
        {
            fixes.Add(name);
            return fallback;
        }
        if (value < min || value > max)
        {
            fixes.Add(name);
            return Math.Clamp(value, min, max);
        }
        return value;
    }

    private static string Color(List<string> fixes, string name, string? value, string fallback)
    {
        if (ArgbColor.TryParse(value, out var c)) return c.ToString();
        fixes.Add(name);
        return fallback;
    }

    private static T EnumValue<T>(List<string> fixes, string name, T value, T fallback) where T : struct, Enum
    {
        if (Enum.IsDefined(value)) return value;
        fixes.Add(name);
        return fallback;
    }
}
