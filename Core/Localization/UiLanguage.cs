using System.Globalization;

namespace PresenterTools.Core.Localization;

/// <summary>
/// 表示言語（SPEC §34）。設定値は "" = Windows の表示言語に合わせる（既定）/ "en" / "ja"（V1.3.1。AreaShot と同じ方式）。
/// Windows に合わせる場合は、起動時の CurrentUICulture（Windows の表示言語）が日本語なら日本語、それ以外は英語（既定の言語）。
/// 地域設定（数値・日付の書式 = CurrentCulture）・キーボード配列・IME では判断しない。
/// 言語は起動時に 1 回だけ決め、終了まで変えない（設定・Windows の表示言語の変更は再起動後に反映する）。
/// </summary>
public static class UiLanguage
{
    public const string System = "";
    public const string English = "en";
    public const string Japanese = "ja";

    /// <summary>設定画面の選択肢の順。</summary>
    public static readonly IReadOnlyList<string> All = [System, English, Japanese];

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo(English);
    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo(Japanese);

    /// <summary>未知の値は ""（Windows に合わせる）にする。大文字・前後の空白は直す（設定ファイルの補正用）。</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        English => English,
        Japanese => Japanese,
        _ => System,
    };

    /// <summary>設定値と Windows の表示言語から、画面に使う言語（en / ja）を決める。</summary>
    public static CultureInfo Resolve(string? setting, CultureInfo windowsUiCulture) => Normalize(setting) switch
    {
        English => EnglishCulture,
        Japanese => JapaneseCulture,
        _ => windowsUiCulture.TwoLetterISOLanguageName == Japanese ? JapaneseCulture : EnglishCulture,
    };

    /// <summary>
    /// 表示言語を適用する。現在のスレッドと、以降に作るスレッド（入力フック・スレッドプール）に効く。
    /// 数値書式の CurrentCulture は変えない。
    /// </summary>
    public static void Apply(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>指定した言語での文字列（再起動の確認を、選んだ言語で出すため）。</summary>
    public static string Get(string key, CultureInfo culture) =>
        Strings.ResourceManager.GetString(key, culture) ?? key;
}
