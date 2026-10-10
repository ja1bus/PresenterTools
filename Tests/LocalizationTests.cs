using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;
using PresenterTools.Core.Settings;

namespace PresenterTools.Tests;

/// <summary>
/// テストの間だけ表示言語（現在のスレッドの CurrentUICulture）を切り替え、終わったら必ず戻す（他のテストへ漏らさない）。
/// </summary>
public sealed class UiCultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public UiCultureScope(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}

/// <summary>プロセス全体の既定（DefaultThreadCurrentUICulture）を変えるテストは、他のテストと並行させない。</summary>
[CollectionDefinition(nameof(ProcessCultureCollection), DisableParallelization = true)]
public sealed class ProcessCultureCollection;

/// <summary>表示言語（英語・日本語）の文字列と、言語の決め方（SPEC §34）。</summary>
public sealed partial class LocalizationTests
{
    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"{culture.Name} の文字列がありません（ja\\PresenterTools.Core.resources.dll）");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static readonly Dictionary<string, string> English = Load(CultureInfo.InvariantCulture);
    private static readonly Dictionary<string, string> Japanese = Load(CultureInfo.GetCultureInfo("ja"));

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")]
    private static partial Regex Placeholder();

    private static int[] Placeholders(string s) =>
        Placeholder().Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();

    [Fact]
    public void Japanese_has_exactly_the_same_keys_as_English()
    {
        Assert.NotEmpty(English);
        Assert.Empty(English.Keys.Except(Japanese.Keys)); // 訳し忘れ（実行時は英語で表示される）
        Assert.Empty(Japanese.Keys.Except(English.Keys)); // 英語側に無いキー
    }

    [Fact]
    public void No_string_is_empty()
    {
        Assert.Empty(English.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
        Assert.Empty(Japanese.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        foreach (var (key, en) in English)
            Assert.True(Placeholders(en).SequenceEqual(Placeholders(Japanese[key])), $"{key}: 差し込み位置 {{n}} が英語と日本語で違う");
    }

    [Fact]
    public void Format_strings_can_be_formatted_and_others_have_no_placeholder()
    {
        foreach (var (key, en) in English)
        {
            var count = Placeholders(en).Length;
            if (!key.EndsWith("Format", StringComparison.Ordinal))
            {
                Assert.True(count == 0, $"{key}: {{n}} を持つのに名前が Format で終わらない");
                continue;
            }
            Assert.True(count > 0, $"{key}: 名前が Format で終わるのに {{0}} が無い");
            var args = Enumerable.Range(0, count).Select(i => (object)$"<{i}>").ToArray();
            foreach (var text in new[] { en, Japanese[key] })
            {
                var formatted = string.Format(CultureInfo.InvariantCulture, text, args);
                for (var i = 0; i < count; i++) Assert.Contains($"<{i}>", formatted);
            }
        }
    }

    [Fact]
    public void Japanese_text_is_not_left_in_English_strings()
    {
        var japaneseChars = new Regex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}（）「」［］／・。、]");
        Assert.Empty(English.Where(p => japaneseChars.IsMatch(p.Value)).Select(p => p.Key));
    }

    [Theory]
    [InlineData("ja-JP", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")] // 英語・日本語以外は英語
    [InlineData("zh-CN", "en")] // 漢字を使う言語でも日本語にはしない
    [InlineData("ko-KR", "en")]
    [InlineData("", "en")]      // InvariantCulture
    public void Resolve_follows_the_windows_display_language_by_default(string windowsUi, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(UiLanguage.System, CultureInfo.GetCultureInfo(windowsUi)).Name);
    }

    [Theory]
    [InlineData("en", "ja-JP", "en")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("ja", "de-DE", "ja")]
    [InlineData("JA ", "en-US", "ja")]
    [InlineData("fr", "ja-JP", "ja")]  // 未知の値は「Windows に合わせる」
    [InlineData(null, "fr-FR", "en")]
    public void Resolve_uses_the_setting_before_windows(string? setting, string windowsUi, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(setting, CultureInfo.GetCultureInfo(windowsUi)).Name);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" en ", "en")]
    [InlineData("Ja", "ja")]
    [InlineData("ja-JP", "")]
    [InlineData("fr", "")]
    public void Normalize_keeps_only_known_values(string? value, string expected)
    {
        Assert.Equal(expected, UiLanguage.Normalize(value));
    }

    [Fact]
    public void Get_returns_text_in_the_requested_language()
    {
        using var _ = new UiCultureScope("ja");
        Assert.Equal(English[nameof(Strings.RestartPrompt)], UiLanguage.Get(nameof(Strings.RestartPrompt), CultureInfo.GetCultureInfo("en")));
        Assert.Equal(Japanese[nameof(Strings.RestartPrompt)], UiLanguage.Get(nameof(Strings.RestartPrompt), CultureInfo.GetCultureInfo("ja")));
    }

    [Theory]
    [InlineData("ja-JP", "操作パネルを表示")]
    [InlineData("ja", "操作パネルを表示")]
    [InlineData("en", "Show control panel")]
    [InlineData("fr-FR", "Show control panel")] // 訳の無い言語は英語へフォールバック
    public void Resources_fall_back_to_English(string culture, string expected)
    {
        Assert.Equal(expected, Strings.ResourceManager.GetString(nameof(Strings.ActionShowControlPanel), CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Core_messages_follow_the_ui_language()
    {
        var duplicate = new HotkeySettings { ToggleMaster = "Ctrl+Shift+F9", ShowControlPanel = "Ctrl+Shift+F9" };
        var invalid = new HotkeySettings { ToggleMaster = "Ctrl+Nope" };
        using (new UiCultureScope("en"))
        {
            Assert.Equal("Show control panel", HotkeyActionNames.GetDisplayName(HotkeyAction.ShowControlPanel));
            Assert.Equal("Laser drawing", HotkeyActionNames.GetDisplayName(HotkeyAction.LaserHold));
            Assert.Equal("Not assigned", Shortcut.DescribeValidation(ShortcutValidation.Empty));
            Assert.StartsWith("Combine the key with Ctrl", Shortcut.DescribeValidation(ShortcutValidation.RequiresCommandModifier));
            Assert.Equal("Same combination as \"Master ON/OFF\"", HotkeyBindings.Validate(duplicate).Single().Message);
            Assert.Equal("\"Ctrl+Nope\" is not a valid shortcut", HotkeyBindings.Validate(invalid).Single().Message);
        }
        using (new UiCultureScope("ja-JP"))
        {
            Assert.Equal("操作パネルを表示", HotkeyActionNames.GetDisplayName(HotkeyAction.ShowControlPanel));
            Assert.Equal("Laser 描画", HotkeyActionNames.GetDisplayName(HotkeyAction.LaserHold));
            Assert.Equal("未割当", Shortcut.DescribeValidation(ShortcutValidation.Empty));
            Assert.Equal("「Master ON/OFF」と同じ組み合わせです", HotkeyBindings.Validate(duplicate).Single().Message);
            Assert.Equal("「Ctrl+Nope」はショートカットとして解釈できません", HotkeyBindings.Validate(invalid).Single().Message);
        }
    }

    [Theory]
    [InlineData("en", "Could not save the settings.")]
    [InlineData("ja", "設定を保存できませんでした。")]
    public void Save_failure_keeps_full_path_and_exception_detail_in_both_languages(string language, string expectedStart)
    {
        using var _ = new UiCultureScope(language);
        var directory = Path.Combine(Path.GetTempPath(), "PresenterTools-l10n-" + Guid.NewGuid());
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(path); // 保存先と同名のディレクトリで書き込みを失敗させる
            var result = new SettingsStore(path).TrySave(new AppSettings());
            Assert.False(result.Success);
            Assert.StartsWith(expectedStart, result.Error);
            Assert.Contains(Path.GetFullPath(path), result.Error);
            Assert.True(result.Error!.Split('\n').Length >= 3, "例外の詳細の行がある");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Shortcut_text_saved_to_settings_does_not_depend_on_language()
    {
        foreach (var language in new[] { "en", "ja" })
        {
            using var _ = new UiCultureScope(language);
            Assert.True(Shortcut.TryParse("Ctrl+Shift+F10", out var sc));
            Assert.Equal("Ctrl+Shift+F10", sc.ToString());
            Assert.Equal("Ctrl+Shift+F10", new HotkeySettings().ShowControlPanel);
        }
    }
}

/// <summary>起動時の言語の適用（UiLanguage.Apply）。プロセス全体の既定を変えるため、必ず元に戻す。</summary>
[Collection(nameof(ProcessCultureCollection))]
public sealed class UiLanguageApplyTests
{
    [Fact]
    public void Apply_sets_ui_culture_for_current_and_new_threads_but_not_number_format()
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // 地域設定は別（小数点はカンマ）
            UiLanguage.Apply(UiLanguage.Resolve(UiLanguage.System, CultureInfo.GetCultureInfo("en-US")));
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("en", CultureInfo.DefaultThreadCurrentUICulture?.Name);
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
            Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));

            string? onNewThread = null;
            var thread = new Thread(() => onNewThread = CultureInfo.CurrentUICulture.Name);
            thread.Start();
            thread.Join();
            Assert.Equal("en", onNewThread);
            Assert.Equal("Show control panel", HotkeyActionNames.GetDisplayName(HotkeyAction.ShowControlPanel));

            UiLanguage.Apply(UiLanguage.Resolve(UiLanguage.System, CultureInfo.GetCultureInfo("ja-JP")));
            Assert.Equal("ja", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("操作パネルを表示", HotkeyActionNames.GetDisplayName(HotkeyAction.ShowControlPanel));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("ja", "https://www.iwanaga.net/presentertools/manual/")]
    [InlineData("ja-JP", "https://www.iwanaga.net/presentertools/manual/")]
    [InlineData("en", "https://www.iwanaga.net/presentertools/en/manual/")]
    [InlineData("en-US", "https://www.iwanaga.net/presentertools/en/manual/")]
    [InlineData("fr-FR", "https://www.iwanaga.net/presentertools/en/manual/")]
    public void Manual_link_follows_ui_language(string culture, string expected)
    {
        // 取扱説明書（Web）は表示言語の版を開く。日本語以外は英語（SPEC §10.2）。
        Assert.Equal(expected, AppLinks.Manual(CultureInfo.GetCultureInfo(culture)));
    }
}
