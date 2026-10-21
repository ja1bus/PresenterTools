using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

/// <summary>設定のインポートで読み込めなかった理由。</summary>
public enum SettingsImportError
{
    None,
    /// <summary>ファイルが大きすぎる（別用途のファイルの誤選択）。</summary>
    TooLarge,
    /// <summary>JSON として読めない。</summary>
    NotJson,
    /// <summary>PresenterTools のエクスポートファイルではない（別用途の JSON・settings.json そのものなど）。</summary>
    NotExportFile,
    /// <summary>このバージョンより新しいファイル形式・設定の版で、正しく読めない。</summary>
    NewerFormat,
    /// <summary>形式は合っているが、設定の内容を読めない。</summary>
    InvalidSettings,
}

public sealed record SettingsImportResult(
    AppSettings? Settings,
    SettingsImportError Error,
    IReadOnlyList<string> Corrections,
    string? SourceAppVersion = null,
    string? Detail = null,
    string? ExportedAt = null);

/// <summary>
/// 設定のエクスポート・インポートのファイル形式（V1.5.0。SPEC §17.2）。
/// settings.json をそのまま包み、アプリ名・出力元のバージョン・ファイル形式の版を添える。
/// 操作パネルの位置・サイズは PC ごとのモニター配置に依存するため書き出さない。
/// 何を取り込むか（ショートカット・マウスボタンの有無、言語・ON/OFF を除くこと）は <see cref="SettingsEditSession.Import"/> が決める。
/// </summary>
public static class SettingsTransfer
{
    public const string AppName = "PresenterTools";
    /// <summary>ファイル形式の版。読めない変更をしたときだけ上げる（項目の追加では上げない）。</summary>
    public const int CurrentFormatVersion = 1;
    /// <summary>これより大きいファイルは読まない（設定は数 KB）。</summary>
    public const long MaxFileBytes = 1024 * 1024;

    public static string DefaultFileName(DateTime now) => $"PresenterTools-{now:yyyyMMdd}.json";

    /// <summary>確定済みの設定をエクスポート用の JSON にする。</summary>
    public static string Serialize(AppSettings settings, string appVersion, DateTimeOffset exportedAt)
    {
        var body = JsonSerializer.SerializeToNode(settings, SettingsJson.Options)!.AsObject();
        body.Remove("controlPanel");
        var root = new JsonObject
        {
            ["app"] = AppName,
            ["formatVersion"] = CurrentFormatVersion,
            ["appVersion"] = appVersion,
            ["exportedAt"] = exportedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            ["settings"] = body,
        };
        return root.ToJsonString(SettingsJson.Options);
    }

    /// <summary>エクスポート用の JSON を一時ファイル経由で書く（書き込み途中で壊れたファイルを残さない）。</summary>
    public static void Write(string path, string json)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// エクスポートファイルの内容を検証して読む。値の範囲・形式は <see cref="SettingsNormalizer"/> で補正し、補正した項目を返す。
    /// </summary>
    public static SettingsImportResult Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return Fail(SettingsImportError.NotJson, ex.Message);
        }

        if (node is not JsonObject root || !IsString(root["app"], AppName))
            return Fail(SettingsImportError.NotExportFile);
        if (!TryGetInt(root["formatVersion"], out var format) || format < 1)
            return Fail(SettingsImportError.NotExportFile);
        var appVersion = root["appVersion"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        if (format > CurrentFormatVersion)
            return new(null, SettingsImportError.NewerFormat, [], appVersion);
        if (root["settings"] is not JsonObject body)
            return Fail(SettingsImportError.NotExportFile);
        if (body["schemaVersion"] is { } schema && (!TryGetInt(schema, out var sv) || sv > AppSettings.CurrentSchemaVersion))
            return new(null, SettingsImportError.NewerFormat, [], appVersion);

        AppSettings? settings;
        try
        {
            settings = body.Deserialize<AppSettings>(SettingsJson.Options);
        }
        catch (JsonException ex)
        {
            return new(null, SettingsImportError.InvalidSettings, [], appVersion, ex.Message);
        }
        if (settings is null) return new(null, SettingsImportError.InvalidSettings, [], appVersion);

        var corrections = SettingsNormalizer.Normalize(settings);
        var exportedAt = root["exportedAt"] is JsonValue e && e.TryGetValue<string>(out var at) ? at : null;
        return new(settings, SettingsImportError.None, corrections, appVersion, ExportedAt: exportedAt);
    }

    private static SettingsImportResult Fail(SettingsImportError error, string? detail = null) => new(null, error, [], null, detail);

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) && s == expected;

    private static bool TryGetInt(JsonNode? node, out int result)
    {
        result = 0;
        return node is JsonValue value && value.TryGetValue(out result);
    }
}
