using PresenterTools.Core.Geometry;
using PresenterTools.Core.Input;
using PresenterTools.Core.KeyDisplay;
using PresenterTools.Core.Licensing;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Tests;

/// <summary>V1.4.0: 同梱のライセンス表記・拡大窓の取得の見送り・Key Display のサロゲートペア。</summary>
public sealed class V14FeatureTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);
    private const string Emoji = "😀"; // サロゲートペア（UTF-16 で 2 単位）

    [Fact]
    public void License_and_notices_are_copied_next_to_the_executable()
    {
        var license = LicenseFiles.Read(LicenseFiles.LicenseName, out var problem);
        Assert.True(license is not null, problem);
        Assert.StartsWith("MIT License", license);
        Assert.Contains("ja1bus", license);

        var notices = LicenseFiles.Read(LicenseFiles.NoticesName, out problem);
        Assert.True(notices is not null, problem);
        // MSIX は .NET のランタイムを内包するため、その著作権・許諾表示を含める。
        Assert.Contains("Copyright (c) .NET Foundation and Contributors", notices);

        // 拡張子の無い LICENSE（暗号化される拡張子を避ける）。
        Assert.Equal("", Path.GetExtension(LicenseFiles.PathOf(LicenseFiles.LicenseName)));
        Assert.Equal("https://github.com/ja1bus/PresenterTools", AppLinks.Repository);
    }

    [Fact]
    public void Unreadable_license_reports_the_full_path()
    {
        Assert.Null(LicenseFiles.Read("no-such-file", out var problem));
        Assert.Contains(LicenseFiles.PathOf("no-such-file"), problem);
    }

    [Fact]
    public void Magnifier_defers_capture_while_the_shown_window_covers_the_new_source()
    {
        // 既定 480×300・300%。カーソルを (500,500) から (800,700) へ動かすと、移動前の窓が新しい取得範囲に掛かる。
        var monitor = new RectInt(0, 0, 1920, 1080);
        var work = new RectInt(0, 0, 1920, 1040);
        var size = new SizeInt(480, 300);
        var src1 = MagnifierLayout.ComputeSourceRect(new PointInt(500, 500), size, 3, monitor);
        var win1 = MagnifierLayout.ComputeWindowRect(size, src1, work, null, out var q);
        var src2 = MagnifierLayout.ComputeSourceRect(new PointInt(800, 700), size, 3, monitor);
        var win2 = MagnifierLayout.ComputeWindowRect(size, src2, work, q, out _);

        Assert.True(win1.IntersectsWith(src2));
        Assert.False(win2.IntersectsWith(src2));
        Assert.True(MagnifierLayout.ShouldDeferCapture(win1, win2, src2));
        // 窓を動かした次のフレームでは取得する。
        Assert.False(MagnifierLayout.ShouldDeferCapture(win2, win2, src2));
    }

    [Fact]
    public void Magnifier_does_not_defer_when_the_window_stays_or_is_not_shown()
    {
        var source = new RectInt(100, 100, 160, 100);
        var overlapping = new RectInt(150, 150, 480, 300);
        Assert.False(MagnifierLayout.ShouldDeferCapture(null, overlapping, source));
        // どの候補でも重なって窓が動かない場合に見送ると、取得が止まったままになる。
        Assert.False(MagnifierLayout.ShouldDeferCapture(overlapping, overlapping, source));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Key_display_never_splits_a_surrogate_pair(int leadingLetters)
    {
        var model = new KeyDisplayModel(new KeyDisplayOptions { MaxTextLength = 20 });
        model.SetMode(KeyDisplayMode.FullText);
        for (var i = 0; i < leadingLetters; i++) Type(model, "a");
        for (var i = 0; i < 25; i++) Type(model, Emoji);

        var text = model.DisplayText;
        Assert.StartsWith("…", text);
        AssertNoLoneSurrogate(text);
        Assert.EndsWith(Emoji, text);
    }

    [Fact]
    public void Key_display_drops_the_whole_emoji_at_the_cut()
    {
        // 絵文字 1 文字＋英字 79 文字を 20 単位で省略すると、旧実装は省略記号の直後に下位サロゲートだけが残った。
        var model = new KeyDisplayModel(new KeyDisplayOptions { MaxTextLength = 20 });
        model.SetMode(KeyDisplayMode.FullText);
        Type(model, Emoji);
        for (var i = 0; i < 19; i++) Type(model, "b");

        Assert.Equal("…" + new string('b', 19), model.DisplayText);
    }

    private static void Type(KeyDisplayModel model, string text)
    {
        var vk = text == "a" ? 'A' : text == "b" ? 'B' : 0xBD;
        model.Process(new KeyInput(vk, true, KeyModifiers.None, text), T0);
        model.Process(new KeyInput(vk, false, KeyModifiers.None), T0);
    }

    private static void AssertNoLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                Assert.True(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]), $"上位サロゲートだけが {i} にある: {s}");
                i++;
            }
            else Assert.False(char.IsLowSurrogate(s[i]), $"下位サロゲートだけが {i} にある: {s}");
        }
    }
}
