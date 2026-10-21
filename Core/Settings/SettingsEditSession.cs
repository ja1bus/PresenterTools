using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

public sealed record SettingsEditResult(AppSettings Settings, bool MagnifierEnabled, KeyDisplayMode KeyDisplayMode);

/// <summary>編集前との差分だけを現在値へ反映する。明示的な初期化・復元は同じ値でも編集として扱う。</summary>
public sealed class SettingsEditSession
{
    private readonly SettingsEditResult _original;
    private SettingsEditResult _baseline;
    private readonly HashSet<string> _forced = [];
    private bool _forceMagnifier, _forceKeyDisplay;

    public SettingsEditSession(AppSettings settings, bool magnifier, KeyDisplayMode keyDisplay)
    {
        _original = new(settings.Clone(), magnifier, keyDisplay);
        _baseline = new(settings.Clone(), magnifier, keyDisplay);
        Settings = settings.Clone();
        MagnifierEnabled = magnifier;
        KeyDisplayMode = keyDisplay;
    }

    public AppSettings Settings { get; private set; }
    public bool MagnifierEnabled { get; set; }
    public KeyDisplayMode KeyDisplayMode { get; set; }

    public SettingsEditResult Build(AppSettings current, bool magnifier, KeyDisplayMode keyDisplay)
    {
        var result = ToNode(current);
        Merge(ToNode(_baseline.Settings), ToNode(Settings), result, "");
        var settings = result.Deserialize<AppSettings>(SettingsJson.Options)!;
        settings.ControlPanel = current.ControlPanel;
        return new(settings,
            _forceMagnifier || MagnifierEnabled != _baseline.MagnifierEnabled ? MagnifierEnabled : magnifier,
            _forceKeyDisplay || KeyDisplayMode != _baseline.KeyDisplayMode ? KeyDisplayMode : keyDisplay);
    }

    /// <summary>
    /// ［適用］すると今の動作・設定が変わるか（V1.5.0。未適用の変更の表示と［適用］の有効化に使う）。
    /// 編集中の値ではなく <see cref="Build"/> の結果を確定済みの値と比べる。設定画面を開いている間に操作パネルなどで
    /// ON/OFF を変えても、触っていない項目を未適用の変更と数えないため。
    /// </summary>
    public bool HasChanges(AppSettings current, bool magnifier, KeyDisplayMode keyDisplay)
    {
        var pending = Build(current, magnifier, keyDisplay);
        if (pending.MagnifierEnabled != magnifier || pending.KeyDisplayMode != keyDisplay) return true;
        var a = ToNode(pending.Settings);
        var b = ToNode(current);
        foreach (var key in new[] { "controlPanel", "schemaVersion" })
        {
            a.Remove(key);
            b.Remove(key);
        }
        return !JsonNode.DeepEquals(a, b);
    }

    /// <summary>
    /// インポートしたファイルの設定を編集中の値へ読み込む（V1.5.0。SPEC §10.2）。確定は［適用］／［OK］で行う。
    /// 表示言語・各機能の ON/OFF・操作パネルの位置とサイズは、この PC の状態を残す。
    /// ショートカットとマウスボタンの割り当ては、指定したときだけ読み込む（既定は残す。PC ごとに空きキーが違うため）。
    /// </summary>
    public void Import(AppSettings imported, bool includeHotkeys, bool includeMouseButtons)
    {
        var s = Settings.Clone();
        var i = imported.Clone();
        s.General = new GeneralSettings
        {
            MasterEnabled = s.General.MasterEnabled,
            Language = s.General.Language,
            AlwaysOnTop = i.General.AlwaysOnTop,
            HideOwnShortcutsInKeyDisplay = i.General.HideOwnShortcutsInKeyDisplay,
            ShowShortcutsInPanel = i.General.ShowShortcutsInPanel,
        };
        i.CursorHighlight.Enabled = s.CursorHighlight.Enabled;
        s.CursorHighlight = i.CursorHighlight;
        i.ClickEffect.Enabled = s.ClickEffect.Enabled;
        s.ClickEffect = i.ClickEffect;
        i.Laser.Enabled = s.Laser.Enabled;
        if (!includeMouseButtons) i.Laser.MouseButton = s.Laser.MouseButton;
        s.Laser = i.Laser;
        if (!includeMouseButtons) i.Magnifier.MouseButton = s.Magnifier.MouseButton;
        s.Magnifier = i.Magnifier;
        s.KeyDisplay = i.KeyDisplay;
        if (includeHotkeys) s.Hotkeys = i.Hotkeys;
        Settings = s;

        // 読み込んだ項目は、確定済みの値と同じでも編集として扱う（既定値に戻す操作と同じ）。
        var node = ToNode(Settings);
        var keep = new HashSet<string> { "enabled", "masterEnabled", "language" };
        if (!includeMouseButtons) keep.Add("mouseButton");
        foreach (var section in new[] { "general", "cursorHighlight", "clickEffect", "laser", "magnifier", "keyDisplay" })
            ForceLeaves(node[section]!.AsObject(), section, keep);
        if (includeHotkeys) ForceLeaves(node["hotkeys"]!.AsObject(), "hotkeys");
    }

    // 保存に失敗しても動作への適用は済んでいるため、適用済みの値を次の比較基準にする。
    public void Accept(SettingsEditResult applied)
    {
        _baseline = new(applied.Settings.Clone(), applied.MagnifierEnabled, applied.KeyDisplayMode);
        Settings = applied.Settings.Clone();
        MagnifierEnabled = applied.MagnifierEnabled;
        KeyDisplayMode = applied.KeyDisplayMode;
        _forced.Clear();
        _forceMagnifier = _forceKeyDisplay = false;
    }

    public void ResetSection(SettingsSection section)
    {
        Settings = Settings.Clone();
        SettingsReset.ResetSection(Settings, section);
        ForceSection(section, includeState: false);
    }

    public void RevertSection(SettingsSection section)
    {
        Settings = Settings.Clone();
        SettingsReset.RevertSection(Settings, _original.Settings, section);
        ForceSection(section, includeState: true);
        foreach (var action in SettingsReset.HotkeysOf(section))
            _forced.Add("hotkeys." + JsonNamingPolicy.CamelCase.ConvertName(action.ToString()));
        if (section is SettingsSection.General or SettingsSection.Magnifier)
        {
            MagnifierEnabled = _original.MagnifierEnabled;
            _forceMagnifier = true;
        }
        if (section == SettingsSection.KeyDisplay)
        {
            KeyDisplayMode = _original.KeyDisplayMode;
            _forceKeyDisplay = true;
        }
    }

    public void ResetHotkeys()
    {
        Settings = Settings.Clone();
        SettingsReset.ResetHotkeys(Settings);
        ForceLeaves(ToNode(Settings)["hotkeys"]!.AsObject(), "hotkeys");
    }

    public void ResetAll()
    {
        Settings = Settings.Clone();
        SettingsReset.ResetAll(Settings);
        foreach (var section in Enum.GetValues<SettingsSection>()) ForceSection(section, includeState: true);
        ResetHotkeys();
    }

    private void ForceSection(SettingsSection section, bool includeState)
    {
        var name = JsonNamingPolicy.CamelCase.ConvertName(section.ToString());
        // 既定値に戻す操作（includeState = false）は ON/OFF と表示言語を変えないので、編集として扱わない。
        ForceLeaves(ToNode(Settings)[name]!.AsObject(), name, includeState ? null : new HashSet<string> { "enabled", "masterEnabled", "language" });
        if (includeState && section == SettingsSection.General)
            foreach (var feature in new[] { "cursorHighlight", "clickEffect", "laser" }) _forced.Add(feature + ".enabled");
    }

    private void ForceLeaves(JsonObject node, string path, HashSet<string>? excluded = null)
    {
        foreach (var (key, _) in node)
            if (excluded?.Contains(key) != true) _forced.Add(path + "." + key);
    }

    private void Merge(JsonObject baseline, JsonObject draft, JsonObject result, string path)
    {
        foreach (var (key, value) in draft)
        {
            if (path.Length == 0 && key is "controlPanel" or "schemaVersion") continue;
            var field = path.Length == 0 ? key : path + "." + key;
            if (value is JsonObject child)
                Merge(baseline[key]!.AsObject(), child, result[key]!.AsObject(), field);
            else if (_forced.Contains(field) || !JsonNode.DeepEquals(baseline[key], value))
                result[key] = value?.DeepClone();
        }
    }

    private static JsonObject ToNode(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, SettingsJson.Options)!.AsObject();
}
