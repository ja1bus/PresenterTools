using PresenterTools.Core.Effects;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;
using PresenterTools.Core.State;

namespace PresenterTools.Tests;

public sealed class ReviewFixTests
{
    [Fact]
    public void AppearanceEditPreservesExternalOffAndZoomChanges()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, true, KeyDisplayMode.FullText);
        edit.Settings.Laser.Color = "#123456";
        current.General.MasterEnabled = false;
        current.General.AlwaysOnTop = false;
        current.CursorHighlight.Enabled = false;
        current.Magnifier.ZoomPercent = 400;
        var result = edit.Build(current, false, KeyDisplayMode.Off);
        Assert.False(result.Settings.General.MasterEnabled);
        Assert.False(result.Settings.General.AlwaysOnTop);
        Assert.False(result.Settings.CursorHighlight.Enabled);
        Assert.Equal(400, result.Settings.Magnifier.ZoomPercent);
        Assert.False(result.MagnifierEnabled);
        Assert.Equal(KeyDisplayMode.Off, result.KeyDisplayMode);
        Assert.Equal("#123456", result.Settings.Laser.Color);
        Assert.NotEqual(result.Settings.Laser.Color, current.Laser.Color);
    }

    [Fact]
    public void ExplicitRuntimeEditWinsOverCurrentState()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off)
        { MagnifierEnabled = true, KeyDisplayMode = KeyDisplayMode.FullText };
        var result = edit.Build(current, false, KeyDisplayMode.Off);
        Assert.True(result.MagnifierEnabled);
        Assert.Equal(KeyDisplayMode.FullText, result.KeyDisplayMode);
    }

    [Fact]
    public void RepeatedApplyDoesNotReplayPreviouslyAppliedChanges()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        edit.Settings.Magnifier.ZoomPercent = 400;
        edit.KeyDisplayMode = KeyDisplayMode.FullText;
        var applied = edit.Build(current, false, KeyDisplayMode.Off);
        edit.Accept(applied);
        applied.Settings.Magnifier.ZoomPercent = 200;
        edit.Settings.Laser.Width = 12;
        var result = edit.Build(applied.Settings, false, KeyDisplayMode.Off);
        Assert.Equal(200, result.Settings.Magnifier.ZoomPercent);
        Assert.Equal(KeyDisplayMode.Off, result.KeyDisplayMode);
        Assert.Equal(12, result.Settings.Laser.Width);
    }

    [Fact]
    public void RevertAfterApplyStillUsesOpeningValuesAndForcesUnchangedValues()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, true, KeyDisplayMode.FullText);
        edit.Settings.Laser.Width = 12;
        edit.Accept(edit.Build(current, true, KeyDisplayMode.FullText));
        current.Laser.Width = 20;
        edit.RevertSection(SettingsSection.Laser);
        edit.RevertSection(SettingsSection.KeyDisplay);
        var result = edit.Build(current, false, KeyDisplayMode.Off);
        Assert.Equal(new AppSettings().Laser.Width, result.Settings.Laser.Width);
        Assert.Equal(KeyDisplayMode.FullText, result.KeyDisplayMode);
    }

    [Fact]
    public void ResetTabForcesDefaultsButPreservesExternalEnabledAndHotkey()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        current.CursorHighlight.Enabled = false;
        current.CursorHighlight.Diameter = 100;
        current.Hotkeys.ToggleCursorHighlight = "Ctrl+Alt+H";
        edit.ResetSection(SettingsSection.CursorHighlight);
        var result = edit.Build(current, false, KeyDisplayMode.Off);
        Assert.False(result.Settings.CursorHighlight.Enabled);
        Assert.Equal(new AppSettings().CursorHighlight.Diameter, result.Settings.CursorHighlight.Diameter);
        Assert.Equal("Ctrl+Alt+H", result.Settings.Hotkeys.ToggleCursorHighlight);
    }

    [Fact]
    public void ResetAllPreservesPlacementAndCurrentTransientState()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, true, KeyDisplayMode.FullText);
        current.General.MasterEnabled = false;
        current.ControlPanel.Left = 123;
        current.Hotkeys.LaserHold = "Ctrl+Alt+L";
        edit.ResetAll();
        var result = edit.Build(current, false, KeyDisplayMode.Off);
        Assert.True(result.Settings.General.MasterEnabled);
        Assert.Equal(123, result.Settings.ControlPanel.Left);
        Assert.Empty(result.Settings.Hotkeys.LaserHold);
        Assert.False(result.MagnifierEnabled);
        Assert.Equal(KeyDisplayMode.Off, result.KeyDisplayMode);
    }

    [Fact]
    public void RevertGeneralRestoresItsStatesAndShortcuts()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, true, KeyDisplayMode.FullText);
        current.General.MasterEnabled = false;
        current.CursorHighlight.Enabled = false;
        current.Hotkeys.ToggleMaster = "Ctrl+Alt+M";
        edit.RevertSection(SettingsSection.General);
        var result = edit.Build(current, false, KeyDisplayMode.Off);
        Assert.True(result.Settings.General.MasterEnabled);
        Assert.True(result.Settings.CursorHighlight.Enabled);
        Assert.Equal("", result.Settings.Hotkeys.ToggleMaster); // 開いたときの値（既定の未割当）
        Assert.True(result.MagnifierEnabled);
        Assert.Equal(KeyDisplayMode.Off, result.KeyDisplayMode);
    }

    [Fact]
    public void ResetHotkeysForcesEvenUneditedShortcuts()
    {
        var current = new AppSettings();
        var edit = new SettingsEditSession(current, false, KeyDisplayMode.Off);
        current.Hotkeys.LaserHold = "Ctrl+Alt+L";
        edit.ResetHotkeys();
        Assert.Empty(edit.Build(current, false, KeyDisplayMode.Off).Settings.Hotkeys.LaserHold);
    }

    [Fact]
    public void StationaryLaserHeadSurvivesTrailExpiryAndDisappearsOnEnd()
    {
        var trail = new LaserTrail { LifetimeSeconds = 0.8 };
        trail.BeginStroke(100, 100, 0);
        trail.Prune(5);
        Assert.Empty(trail.Points);
        Assert.True(trail.IsDrawing);
        Assert.Equal(100, trail.Head!.Value.X);
        trail.EndStroke();
        Assert.Null(trail.Head);
        Assert.False(trail.IsDrawing);
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(120, 100)]
    public void RestartCreatesBoundaryWhileKeepingOldTrail(double x, double y)
    {
        var trail = new LaserTrail();
        trail.BeginStroke(100, 100, 0);
        trail.Add(120, 100, 0.1);
        trail.EndStroke();
        trail.BeginStroke(x, y, 0.2);
        trail.Add(x + 20, y, 0.3);
        Assert.Equal(4, trail.Points.Count);
        Assert.True(trail.Points[2].StartsStroke);
        Assert.False(trail.Points[1].StartsStroke);
        Assert.False(trail.Points[3].StartsStroke);
    }

    [Fact]
    public void TinyMovementUpdatesHeadWithoutAddingTrailPoint()
    {
        var trail = new LaserTrail();
        trail.BeginStroke(100, 100, 0);
        trail.Add(101, 100, 0.1);
        Assert.Single(trail.Points);
        Assert.Equal(101, trail.Head!.Value.X);
        trail.Clear();
        Assert.Null(trail.Head);
        Assert.Empty(trail.Points);
    }

    [Fact]
    public void LaserDrawingAndRuntimeTogglesNeverRequestPersistence()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        state.SetLaser(true);
        var changes = new List<bool>();
        state.Changed += (_, e) => changes.Add(e.PersistSettings);
        state.BeginLaser(LaserTrigger.Keyboard);
        state.BeginLaser(LaserTrigger.Mouse);
        state.EndLaser(LaserTrigger.Keyboard);
        state.EndLaser(LaserTrigger.Mouse);
        state.ToggleLaserDrawing(LaserTrigger.Mouse);
        state.EndAllLaser();
        state.ToggleMagnifier();
        state.ToggleKeyDisplay();
        Assert.Equal(6, changes.Count);
        Assert.All(changes, persist => Assert.False(persist));
        state.SetMaster(false);
        Assert.True(changes[^1]);
    }

    [Fact]
    public void PersistentTogglesRequestPersistence()
    {
        var state = PresenterState.FromSettings(new AppSettings());
        var changes = new List<bool>();
        state.Changed += (_, e) => changes.Add(e.PersistSettings);
        state.ToggleMaster();
        state.SetCursorHighlight(!state.CursorHighlightEnabled);
        state.SetClickEffect(!state.ClickEffectEnabled);
        state.ToggleLaser();
        Assert.Equal(4, changes.Count);
        Assert.All(changes, persist => Assert.True(persist));
    }

    [Fact]
    public void SaveFailureReturnsFullPathAndCanBeRetried()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PresenterTools-review-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            // 保存先と同名のディレクトリで、既存の利用者データに触れず書き込み失敗を起こす。
            Directory.CreateDirectory(path);
            var failed = store.TrySave(new AppSettings());
            Assert.False(failed.Success);
            Assert.Contains(path, failed.Error);
            Directory.Delete(path);
            var settings = new AppSettings();
            settings.Laser.Width = 12;
            Assert.True(store.TrySave(settings).Success);
            Assert.Equal(12, store.Load().Settings.Laser.Width);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
