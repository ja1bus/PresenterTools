using PresenterTools.Core.Models;
using PresenterTools.Core.State;

namespace PresenterTools.Tests;

public sealed class PresenterStateTests
{
    private static PresenterState NewState(Action<AppSettings>? configure = null)
    {
        var s = new AppSettings();
        configure?.Invoke(s);
        return PresenterState.FromSettings(s);
    }

    [Fact]
    public void FromSettings_KeyDisplayAndMagnifierStartOff()
    {
        var state = NewState();
        Assert.True(state.MasterEnabled);
        Assert.True(state.CursorHighlightEnabled);
        Assert.Equal(KeyDisplayMode.Off, state.KeyDisplayMode);
        Assert.False(state.MagnifierEnabled);
        Assert.Equal(LaserStatus.Ready, state.LaserStatus);
    }

    [Fact]
    public void FromSettings_RestoresPersistedFlags()
    {
        var state = NewState(s =>
        {
            s.General.MasterEnabled = false;
            s.ClickEffect.Enabled = false;
            s.Laser.Enabled = false;
        });
        Assert.False(state.MasterEnabled);
        Assert.False(state.ClickEffectEnabled);
        Assert.Equal(LaserStatus.Off, state.LaserStatus);
    }

    [Fact]
    public void MasterOff_HidesEverythingButKeepsIndividualFlags()
    {
        var state = NewState();
        state.SetMagnifier(true);
        state.SetKeyDisplayMode(KeyDisplayMode.FullText);

        state.SetMaster(false);

        Assert.False(state.IsCursorHighlightVisible);
        Assert.False(state.IsClickEffectVisible);
        Assert.False(state.IsLaserReady);
        Assert.False(state.IsMagnifierVisible);
        Assert.Equal(KeyDisplayMode.Off, state.EffectiveKeyDisplayMode);
        Assert.True(state.CursorHighlightEnabled);
        Assert.True(state.MagnifierEnabled);
        Assert.Equal(KeyDisplayMode.FullText, state.KeyDisplayMode);

        state.SetMaster(true);
        Assert.True(state.IsCursorHighlightVisible);
        Assert.True(state.IsMagnifierVisible);
        Assert.Equal(KeyDisplayMode.FullText, state.EffectiveKeyDisplayMode);
    }

    [Fact]
    public void ToggleKeyDisplay_SwitchesBetweenOffAndOn()
    {
        var state = NewState();
        state.ToggleKeyDisplay();
        Assert.Equal(KeyDisplayMode.FullText, state.KeyDisplayMode);
        state.ToggleKeyDisplay();
        Assert.Equal(KeyDisplayMode.Off, state.KeyDisplayMode);
    }

    [Fact]
    public void SetKeyDisplayMode_RemovedSpecialValueBecomesOff()
    {
        var state = NewState();
        state.SetKeyDisplayMode(KeyDisplayMode.FullText);
        state.SetKeyDisplayMode((KeyDisplayMode)1); // V1.2 の改訂 3 で廃止した Special Keys Only
        Assert.Equal(KeyDisplayMode.Off, state.KeyDisplayMode);
    }

    [Fact]
    public void KeyDisplayOff_AlwaysOff()
    {
        var state = NewState();
        state.SetKeyDisplayMode(KeyDisplayMode.FullText);
        state.KeyDisplayOff();
        Assert.Equal(KeyDisplayMode.Off, state.KeyDisplayMode);
        state.KeyDisplayOff();
        Assert.Equal(KeyDisplayMode.Off, state.KeyDisplayMode);
    }

    [Fact]
    public void Laser_ActiveOnlyWhileTriggerHeld()
    {
        var state = NewState();
        Assert.Equal(LaserStatus.Ready, state.LaserStatus);

        Assert.True(state.BeginLaser(LaserTrigger.Keyboard));
        Assert.Equal(LaserStatus.Active, state.LaserStatus);

        state.BeginLaser(LaserTrigger.Mouse);
        state.EndLaser(LaserTrigger.Keyboard);
        Assert.Equal(LaserStatus.Active, state.LaserStatus); // マウス側がまだ押されている

        state.EndLaser(LaserTrigger.Mouse);
        Assert.Equal(LaserStatus.Ready, state.LaserStatus);
    }

    [Fact]
    public void Laser_IgnoredWhenDisabledOrMasterOff()
    {
        var state = NewState(s => s.Laser.Enabled = false);
        Assert.False(state.BeginLaser(LaserTrigger.Keyboard));
        Assert.Equal(LaserStatus.Off, state.LaserStatus);

        state.SetLaser(true);
        state.SetMaster(false);
        Assert.False(state.BeginLaser(LaserTrigger.Keyboard));
        Assert.False(state.IsLaserActive);
    }

    [Fact]
    public void Laser_DisablingOrMasterOffStopsActive()
    {
        var state = NewState();
        state.BeginLaser(LaserTrigger.Keyboard);
        state.SetLaser(false);
        Assert.Equal(LaserTrigger.None, state.LaserTriggers);

        state.SetLaser(true);
        state.BeginLaser(LaserTrigger.Keyboard);
        state.SetMaster(false);
        state.SetMaster(true);
        Assert.Equal(LaserStatus.Ready, state.LaserStatus); // 押しっぱなし扱いで復帰しない
    }

    [Fact]
    public void Changed_RaisedOnlyOnActualChange()
    {
        var state = NewState();
        var count = 0;
        state.Changed += (_, _) => count++;

        state.SetMaster(true);
        state.SetCursorHighlight(true);
        state.KeyDisplayOff();
        Assert.Equal(0, count);

        state.SetCursorHighlight(false);
        state.ToggleMaster();
        state.BeginLaser(LaserTrigger.Keyboard); // Master OFF なので無視
        Assert.Equal(2, count);
    }

    [Fact]
    public void WriteTo_DoesNotPersistKeyDisplayOrMagnifier()
    {
        var state = NewState();
        state.SetKeyDisplayMode(KeyDisplayMode.FullText);
        state.SetMagnifier(true);
        state.SetClickEffect(false);

        var s = new AppSettings();
        state.WriteTo(s);
        Assert.False(s.ClickEffect.Enabled);

        var restored = PresenterState.FromSettings(s);
        Assert.Equal(KeyDisplayMode.Off, restored.KeyDisplayMode);
        Assert.False(restored.MagnifierEnabled);
        Assert.False(restored.ClickEffectEnabled);
    }
}
