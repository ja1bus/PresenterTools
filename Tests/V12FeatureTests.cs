using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;
using PresenterTools.Core.State;

namespace PresenterTools.Tests;

/// <summary>V1.2 の変更（ショートカットの既定・Laser の描画方式・十字線の廃止・見た目のプレビュー・開いたときの値に戻す）。</summary>
public sealed class V12FeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public V12FeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- ショートカットの既定（D-32） ----

    [Fact]
    public void DefaultHotkeys_OnlyMasterAndPanel()
    {
        var h = new HotkeySettings();
        Assert.Equal("Ctrl+Shift+F9", h.ToggleMaster);
        Assert.Equal("Ctrl+Shift+F10", h.ShowControlPanel);
        foreach (var action in HotkeyActionNames.All.Where(a => a is not (HotkeyAction.ToggleMaster or HotkeyAction.ShowControlPanel)))
            Assert.Equal("", h.Get(action));
        Assert.Equal(2, HotkeyBindings.Parse(h).Count);
    }

    [Fact]
    public void SavedOldDefaultHotkeys_AreKept()
    {
        // V1.1 で保存したファイル（旧既定の Ctrl+Shift+F キー）はそのまま使う。
        File.WriteAllText(FilePath, """
        { "hotkeys": { "toggleLaser": "Ctrl+Shift+F4", "laserHold": "Ctrl+Shift+F1", "toggleKeyDisplay": "Ctrl+Shift+F7" } }
        """);
        var result = new SettingsStore(FilePath).Load();
        Assert.Empty(result.Corrections);
        Assert.Equal("Ctrl+Shift+F4", result.Settings.Hotkeys.ToggleLaser);
        Assert.Equal("Ctrl+Shift+F1", result.Settings.Hotkeys.LaserHold);
        Assert.Equal("Ctrl+Shift+F7", result.Settings.Hotkeys.ToggleKeyDisplay);
        Assert.Equal("", result.Settings.Hotkeys.ToggleMagnifier); // 保存されていない項目は新しい既定
    }

    // ---- 十字線の廃止（D-33） ----

    [Fact]
    public void Crosshair_LoadsAsRingWithoutCorrectionNotice()
    {
        File.WriteAllText(FilePath, """
        { "cursorHighlight": { "style": "Crosshair", "diameter": 90 } }
        """);
        var result = new SettingsStore(FilePath).Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Corrections);
        Assert.Equal(CursorHighlightStyle.Ring, result.Settings.CursorHighlight.Style);
        Assert.Equal(90, result.Settings.CursorHighlight.Diameter);
    }

    // ---- Laser の描画方式（D-29） ----

    [Fact]
    public void TriggerMode_DefaultHoldAndRoundTrip()
    {
        Assert.Equal(LaserTriggerMode.Hold, new AppSettings().Laser.TriggerMode);
        var store = new SettingsStore(FilePath);
        var s = new AppSettings();
        s.Laser.TriggerMode = LaserTriggerMode.Toggle;
        store.Save(s);
        Assert.Contains("\"triggerMode\": \"Toggle\"", File.ReadAllText(FilePath));
        Assert.Equal(LaserTriggerMode.Toggle, store.Load().Settings.Laser.TriggerMode);
    }

    [Fact]
    public void TriggerMode_InvalidIsCorrected()
    {
        File.WriteAllText(FilePath, """{ "laser": { "triggerMode": "Sometimes" } }""");
        var result = new SettingsStore(FilePath).Load();
        Assert.Equal(LaserTriggerMode.Hold, result.Settings.Laser.TriggerMode);
        Assert.Contains("laser.triggerMode", result.Corrections);
    }

    [Fact]
    public void ToggleLaserDrawing_StartsAndStops()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        state.ToggleLaserDrawing(LaserTrigger.Keyboard);
        Assert.Equal(LaserStatus.Active, state.LaserStatus);
        state.ToggleLaserDrawing(LaserTrigger.Keyboard);
        Assert.Equal(LaserStatus.Ready, state.LaserStatus);
    }

    [Fact]
    public void ToggleLaserDrawing_EitherTriggerEnds()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        state.ToggleLaserDrawing(LaserTrigger.Mouse);
        Assert.True(state.IsLaserActive);
        state.ToggleLaserDrawing(LaserTrigger.Keyboard); // マウスで始めてキーで終える
        Assert.False(state.IsLaserActive);
        Assert.Equal(LaserTrigger.None, state.LaserTriggers);
    }

    [Fact]
    public void ToggleLaserDrawing_IgnoredWhenNotReady()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        state.SetMaster(false);
        state.ToggleLaserDrawing(LaserTrigger.Keyboard);
        Assert.False(state.IsLaserActive);
    }

    [Fact]
    public void EndAllLaser_EndsBothTriggers()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        state.BeginLaser(LaserTrigger.Keyboard);
        state.BeginLaser(LaserTrigger.Mouse);
        var changes = 0;
        state.Changed += (_, _) => changes++;
        state.EndAllLaser();
        Assert.False(state.IsLaserActive);
        Assert.Equal(1, changes);
        state.EndAllLaser(); // 描画中でなければ何も起きない
        Assert.Equal(1, changes);
    }

    // ---- 見た目のプレビュー（D-31） ----

    [Fact]
    public void Preview_TakesAppearanceOnly()
    {
        var applied = new AppSettings();
        var draft = applied.Clone();
        draft.CursorHighlight.Enabled = false;
        draft.CursorHighlight.Style = CursorHighlightStyle.FilledCircle;
        draft.CursorHighlight.Diameter = 120;
        draft.CursorHighlight.Color = "#00C853";
        draft.ClickEffect.Enabled = false;
        draft.ClickEffect.Size = 150;
        draft.ClickEffect.ShowButtonLabel = true;
        draft.Laser.Enabled = false;
        draft.Laser.Width = 20;
        draft.Laser.TrailSeconds = 2;
        draft.Laser.TriggerMode = LaserTriggerMode.Toggle;
        draft.Laser.MouseButton = MouseTriggerButton.XButton1;
        draft.Magnifier.Width = 900;
        draft.KeyDisplay.FontSize = 80;
        draft.Hotkeys.ToggleLaser = "Ctrl+Alt+L";

        var p = SettingsPreview.Compose(applied, draft);

        Assert.Equal(CursorHighlightStyle.FilledCircle, p.CursorHighlight.Style);
        Assert.Equal(120, p.CursorHighlight.Diameter);
        Assert.Equal("#00C853", p.CursorHighlight.Color);
        Assert.Equal(150, p.ClickEffect.Size);
        Assert.True(p.ClickEffect.ShowButtonLabel);
        Assert.Equal(20, p.Laser.Width);
        Assert.Equal(2, p.Laser.TrailSeconds);

        // 見た目以外は確定済みのまま。
        Assert.True(p.CursorHighlight.Enabled);
        Assert.True(p.ClickEffect.Enabled);
        Assert.True(p.Laser.Enabled);
        Assert.Equal(LaserTriggerMode.Hold, p.Laser.TriggerMode);
        Assert.Equal(MouseTriggerButton.None, p.Laser.MouseButton);
        Assert.Equal(applied.Magnifier.Width, p.Magnifier.Width);
        Assert.Equal(applied.KeyDisplay.FontSize, p.KeyDisplay.FontSize);
        Assert.Equal("", p.Hotkeys.ToggleLaser);

        // 元の設定は変えない。
        Assert.Equal(64, applied.CursorHighlight.Diameter);
        Assert.Equal(120, draft.CursorHighlight.Diameter);
    }

    [Fact]
    public void Preview_IncompleteColorKeepsAppliedColor()
    {
        var applied = new AppSettings();
        applied.Laser.Color = "#00C853";
        var draft = applied.Clone();
        draft.Laser.Color = "#12";          // 入力途中
        draft.CursorHighlight.Color = "";   // 消した直後
        draft.ClickEffect.LeftColor = "#E040FB";

        var p = SettingsPreview.Compose(applied, draft);
        Assert.Equal("#00C853", p.Laser.Color);
        Assert.Equal(applied.CursorHighlight.Color, p.CursorHighlight.Color);
        Assert.Equal("#E040FB", p.ClickEffect.LeftColor);
    }

    // ---- 開いたときの値に戻す（§10.2） ----

    private static AppSettings Edited(AppSettings original)
    {
        var s = original.Clone();
        s.General.MasterEnabled = !s.General.MasterEnabled;
        s.General.AlwaysOnTop = !s.General.AlwaysOnTop;
        s.CursorHighlight.Enabled = !s.CursorHighlight.Enabled;
        s.CursorHighlight.Diameter = 200;
        s.ClickEffect.Enabled = !s.ClickEffect.Enabled;
        s.ClickEffect.Size = 250;
        s.Laser.Enabled = !s.Laser.Enabled;
        s.Laser.Width = 33;
        s.Laser.TriggerMode = LaserTriggerMode.Toggle;
        s.Magnifier.Width = 1000;
        s.KeyDisplay.FontSize = 99;
        foreach (var a in HotkeyActionNames.All) s.Hotkeys.Set(a, "");
        return s;
    }

    private static AppSettings Original()
    {
        var o = new AppSettings();
        o.CursorHighlight.Diameter = 90;
        o.Laser.Width = 12;
        o.Hotkeys.ToggleCursorHighlight = "Ctrl+Alt+H";
        o.Hotkeys.ToggleLaser = "Ctrl+Alt+L";
        o.Hotkeys.LaserHold = "Ctrl+Alt+D";
        o.Hotkeys.ToggleKeyDisplay = "Ctrl+Alt+K";
        return o;
    }

    [Fact]
    public void RevertSection_Laser_RestoresTabIncludingOnOffAndItsHotkeysOnly()
    {
        var original = Original();
        var s = Edited(original);
        SettingsReset.RevertSection(s, original, SettingsSection.Laser);

        Assert.Equal(original.Laser.Enabled, s.Laser.Enabled);
        Assert.Equal(12, s.Laser.Width);
        Assert.Equal(LaserTriggerMode.Hold, s.Laser.TriggerMode);
        Assert.Equal("Ctrl+Alt+L", s.Hotkeys.ToggleLaser);
        Assert.Equal("Ctrl+Alt+D", s.Hotkeys.LaserHold);

        // 他のタブは編集したまま。
        Assert.Equal(200, s.CursorHighlight.Diameter);
        Assert.Equal("", s.Hotkeys.ToggleCursorHighlight);
        Assert.Equal("", s.Hotkeys.ToggleMaster);
        Assert.Equal(99, s.KeyDisplay.FontSize);
    }

    [Fact]
    public void RevertSection_General_RestoresOnOffFlagsAndAllHotkeys()
    {
        var original = Original();
        var s = Edited(original);
        SettingsReset.RevertSection(s, original, SettingsSection.General);

        Assert.Equal(original.General.MasterEnabled, s.General.MasterEnabled);
        Assert.Equal(original.General.AlwaysOnTop, s.General.AlwaysOnTop);
        Assert.Equal(original.CursorHighlight.Enabled, s.CursorHighlight.Enabled);
        Assert.Equal(original.ClickEffect.Enabled, s.ClickEffect.Enabled);
        Assert.Equal(original.Laser.Enabled, s.Laser.Enabled);
        foreach (var a in HotkeyActionNames.All) Assert.Equal(original.Hotkeys.Get(a), s.Hotkeys.Get(a));

        // 各タブの見た目は編集したまま。
        Assert.Equal(200, s.CursorHighlight.Diameter);
        Assert.Equal(33, s.Laser.Width);
    }

    [Theory]
    [InlineData(SettingsSection.CursorHighlight)]
    [InlineData(SettingsSection.ClickEffect)]
    [InlineData(SettingsSection.Magnifier)]
    [InlineData(SettingsSection.KeyDisplay)]
    public void RevertSection_EachTab(SettingsSection section)
    {
        var original = Original();
        var s = Edited(original);
        SettingsReset.RevertSection(s, original, section);
        switch (section)
        {
            case SettingsSection.CursorHighlight:
                Assert.Equal(90, s.CursorHighlight.Diameter);
                Assert.Equal(original.CursorHighlight.Enabled, s.CursorHighlight.Enabled);
                Assert.Equal("Ctrl+Alt+H", s.Hotkeys.ToggleCursorHighlight);
                Assert.Equal(250, s.ClickEffect.Size);
                break;
            case SettingsSection.ClickEffect:
                Assert.Equal(original.ClickEffect.Size, s.ClickEffect.Size);
                Assert.Equal(original.ClickEffect.Enabled, s.ClickEffect.Enabled);
                Assert.Equal(200, s.CursorHighlight.Diameter);
                break;
            case SettingsSection.Magnifier:
                Assert.Equal(original.Magnifier.Width, s.Magnifier.Width);
                Assert.Equal(99, s.KeyDisplay.FontSize);
                break;
            case SettingsSection.KeyDisplay:
                Assert.Equal(original.KeyDisplay.FontSize, s.KeyDisplay.FontSize);
                Assert.Equal("Ctrl+Alt+K", s.Hotkeys.ToggleKeyDisplay);
                Assert.Equal(1000, s.Magnifier.Width);
                break;
        }
        Assert.NotEqual(original.General.MasterEnabled, s.General.MasterEnabled); // General は編集したまま
    }

    [Fact]
    public void RevertSection_DoesNotShareObjectsWithOriginal()
    {
        var original = Original();
        var s = Edited(original);
        SettingsReset.RevertSection(s, original, SettingsSection.Laser);
        s.Laser.Width = 5;
        Assert.Equal(12, original.Laser.Width);
    }
}
