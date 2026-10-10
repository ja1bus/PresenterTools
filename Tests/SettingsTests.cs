using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PresenterToolsTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_AreValidAndNeedNoCorrection()
    {
        var s = new AppSettings();
        Assert.Empty(SettingsNormalizer.Normalize(s));
        Assert.True(s.General.MasterEnabled);
        Assert.True(s.General.AlwaysOnTop);
        Assert.Equal(300, s.Magnifier.ZoomPercent);
        Assert.Equal(0.8, s.Laser.TrailSeconds);
        Assert.Equal("Ctrl+Shift+F9", s.Hotkeys.ToggleMaster);
        Assert.Equal("", s.Hotkeys.LaserHold); // V1.2 から既定は未割当
        Assert.Empty(HotkeyBindings.Validate(s.Hotkeys));
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var result = new SettingsStore(FilePath).Load();
        Assert.Equal(SettingsLoadStatus.NotFound, result.Status);
        Assert.Equal(new AppSettings().CursorHighlight.Diameter, result.Settings.CursorHighlight.Diameter);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var store = new SettingsStore(FilePath);
        var s = new AppSettings();
        s.General.MasterEnabled = false;
        s.CursorHighlight.Color = "#112233";
        s.CursorHighlight.Diameter = 120;
        s.ClickEffect.DurationMs = 700;
        s.Laser.MouseButton = MouseTriggerButton.XButton2;
        s.Magnifier.ZoomPercent = 400;
        s.KeyDisplay.Position = KeyDisplayPosition.TopRight;
        s.Hotkeys.ToggleMaster = "Ctrl+Alt+M";
        s.ControlPanel.Left = -1500;
        s.ControlPanel.Top = 200;
        store.Save(s);

        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Corrections);
        var r = result.Settings;
        Assert.False(r.General.MasterEnabled);
        Assert.Equal("#112233", r.CursorHighlight.Color);
        Assert.Equal(120, r.CursorHighlight.Diameter);
        Assert.Equal(700, r.ClickEffect.DurationMs);
        Assert.Equal(MouseTriggerButton.XButton2, r.Laser.MouseButton);
        Assert.Equal(400, r.Magnifier.ZoomPercent);
        Assert.Equal(KeyDisplayPosition.TopRight, r.KeyDisplay.Position);
        Assert.Equal("Ctrl+Alt+M", r.Hotkeys.ToggleMaster);
        Assert.Equal(-1500, r.ControlPanel.Left);
        Assert.Equal(200, r.ControlPanel.Top);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Save_WritesEnumsAsNames()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings());
        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"position\": \"BottomCenter\"", json);
        Assert.Contains("\"mouseButton\": \"None\"", json);
        Assert.Contains("\"toggleMaster\": \"Ctrl+Shift+F9\"", json);
    }

    [Fact]
    public void Load_CorruptedJson_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        var store = new SettingsStore(FilePath, () => new DateTime(2026, 9, 22, 10, 20, 30));
        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.Corrupted, result.Status);
        Assert.Equal(FilePath + ".broken-20260922102030", result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(new AppSettings().Laser.Width, result.Settings.Laser.Width);
    }

    [Fact]
    public void Load_RemovedKeyDisplaySettings_AreIgnoredWithoutCorrections()
    {
        // V1.2 の改訂 3 で廃止した ON/OFF で使うモード・即時 OFF のショートカット（D-36）。
        File.WriteAllText(FilePath, """
        {
          "keyDisplay": { "toggleMode": "SpecialKeysOnly", "fontSize": 50 },
          "hotkeys": { "keyDisplayOff": "Ctrl+Shift+F8", "toggleKeyDisplay": "Ctrl+Shift+F7" }
        }
        """);
        var result = new SettingsStore(FilePath).Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Corrections);
        Assert.Equal(50, result.Settings.KeyDisplay.FontSize);
        Assert.Equal("Ctrl+Shift+F7", result.Settings.Hotkeys.ToggleKeyDisplay);

        new SettingsStore(FilePath).Save(result.Settings);
        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain("toggleMode", json);
        Assert.DoesNotContain("keyDisplayOff", json);
    }

    [Fact]
    public void Load_InvalidValues_AreCorrected()
    {
        File.WriteAllText(FilePath, """
        {
          "cursorHighlight": { "color": "yellow", "opacity": 5, "diameter": 1000, "thickness": -3 },
          "clickEffect": { "durationMs": 10 },
          "laser": { "trailSeconds": 99, "mouseButton": "XButton9" },
          "magnifier": { "zoomPercent": 250, "mouseButton": "XButton1" },
          "keyDisplay": { "toggleMode": "Off", "position": "Middle", "fontSize": "48" },
          "hotkeys": { "toggleMaster": "Ctrl+Nope", "laserHold": "A", "toggleMagnifier": "Ctrl+Shift+F1" },
          "controlPanel": { "left": 10 },
          "unknownFutureSetting": { "x": 1 }
        }
        """);
        var result = new SettingsStore(FilePath).Load();
        var s = result.Settings;
        var d = new AppSettings();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(d.CursorHighlight.Color, s.CursorHighlight.Color);
        Assert.Equal(1.0, s.CursorHighlight.Opacity);
        Assert.Equal(SettingsNormalizer.MaxRingDiameter, s.CursorHighlight.Diameter);
        Assert.Equal(SettingsNormalizer.MinRingThickness, s.CursorHighlight.Thickness);
        Assert.Equal(SettingsNormalizer.MinClickDurationMs, s.ClickEffect.DurationMs);
        Assert.Equal(SettingsNormalizer.MaxTrailSeconds, s.Laser.TrailSeconds);
        Assert.Equal(MouseTriggerButton.None, s.Laser.MouseButton);
        Assert.Equal(300, s.Magnifier.ZoomPercent);
        Assert.Equal(MouseTriggerButton.XButton1, s.Magnifier.MouseButton);
        Assert.Equal(KeyDisplayPosition.BottomCenter, s.KeyDisplay.Position);
        Assert.Equal(48, s.KeyDisplay.FontSize);
        Assert.Null(s.ControlPanel.Left);
        Assert.Null(s.ControlPanel.Top);

        // 解析できない → 既定。規則違反（修飾なしの A）→ 既定。
        Assert.Equal(d.Hotkeys.ToggleMaster, s.Hotkeys.ToggleMaster);
        // Magnifier は有効な割当なのでそのまま。Laser 描画は規則違反で既定（V1.2 から未割当）に戻る。
        Assert.Equal("Ctrl+Shift+F1", s.Hotkeys.ToggleMagnifier);
        Assert.Equal("", s.Hotkeys.LaserHold);
        Assert.Empty(HotkeyBindings.Validate(s.Hotkeys));

        Assert.Contains("cursorHighlight.color", result.Corrections);
        Assert.Contains("hotkeys.LaserHold", result.Corrections);
    }

    [Fact]
    public void Load_NullSectionsAndNaN_UseDefaults()
    {
        File.WriteAllText(FilePath, """{ "general": null, "laser": { "width": "NaN" } }""");
        var s = new SettingsStore(FilePath).Load().Settings;
        Assert.NotNull(s.General);
        Assert.True(s.General.MasterEnabled);
        Assert.Equal(new AppSettings().Laser.Width, s.Laser.Width);
    }

    [Fact]
    public void Normalize_SameMouseButtonForLaserAndMagnifier_ClearsMagnifier()
    {
        var s = new AppSettings();
        s.Laser.MouseButton = MouseTriggerButton.XButton1;
        s.Magnifier.MouseButton = MouseTriggerButton.XButton1;
        SettingsNormalizer.Normalize(s);
        Assert.Equal(MouseTriggerButton.XButton1, s.Laser.MouseButton);
        Assert.Equal(MouseTriggerButton.None, s.Magnifier.MouseButton);
    }

    [Fact]
    public void Normalize_ColorsAreCanonicalized()
    {
        var s = new AppSettings();
        s.Laser.Color = "#ff00aa";
        s.KeyDisplay.TextColor = "#80FFFFFF";
        SettingsNormalizer.Normalize(s);
        Assert.Equal("#FF00AA", s.Laser.Color);
        Assert.Equal("#80FFFFFF", s.KeyDisplay.TextColor);
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        var s = new AppSettings();
        var c = s.Clone();
        c.CursorHighlight.Diameter = 200;
        c.Hotkeys.ToggleMaster = "";
        Assert.Equal(64, s.CursorHighlight.Diameter);
        Assert.Equal("Ctrl+Shift+F9", s.Hotkeys.ToggleMaster);
    }

    [Theory]
    [InlineData("#FFD400", 255, 0xFF, 0xD4, 0x00)]
    [InlineData("#80102030", 0x80, 0x10, 0x20, 0x30)]
    [InlineData(" #abcdef ", 255, 0xAB, 0xCD, 0xEF)]
    public void ArgbColor_Parse(string text, int a, int r, int g, int b)
    {
        Assert.True(ArgbColor.TryParse(text, out var c));
        Assert.Equal(new ArgbColor((byte)a, (byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FFD400")]
    [InlineData("#FFD4")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void ArgbColor_ParseInvalid(string? text) => Assert.False(ArgbColor.TryParse(text, out _));

    [Fact]
    public void ArgbColor_WithOpacity()
    {
        Assert.Equal(128, ArgbColor.FromRgb(1, 2, 3).WithOpacity(0.5).A);
        Assert.Equal(255, ArgbColor.FromRgb(1, 2, 3).WithOpacity(double.NaN).A);
    }
}
