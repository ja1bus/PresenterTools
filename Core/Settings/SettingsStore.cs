using System.Text;
using System.Text.Json;
using PresenterTools.Core.Localization;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.Settings;

public enum SettingsLoadStatus
{
    /// <summary>読み込めた（補正した項目があっても含む）。</summary>
    Loaded,
    /// <summary>ファイルが無いため既定値。</summary>
    NotFound,
    /// <summary>JSON として読めないため退避して既定値。</summary>
    Corrupted,
    /// <summary>読み込み自体に失敗（アクセス権など）したため既定値。ファイルは触らない。</summary>
    ReadFailed,
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    IReadOnlyList<string> Corrections,
    string? BackupPath = null,
    string? Error = null);

public sealed record SettingsSaveResult(bool Success, string? Error = null);

/// <summary>
/// settings.json の読み書き（SPEC §17）。保存は一時ファイル経由で置き換え、書き込み途中で壊れないようにする。
/// </summary>
public sealed class SettingsStore
{
    private readonly Func<DateTime> _clock;

    public SettingsStore(string filePath, Func<DateTime>? clock = null)
    {
        FilePath = filePath;
        _clock = clock ?? (() => DateTime.Now);
    }

    public string FilePath { get; }

    /// <summary>既定の保存先: %LocalAppData%\PresenterTools\settings.json。</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PresenterTools");

    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "settings.json");

    public SettingsLoadResult Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath))
                return Defaults(SettingsLoadStatus.NotFound);
            json = File.ReadAllText(FilePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Defaults(SettingsLoadStatus.ReadFailed, error: ex.Message);
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options);
        }
        catch (JsonException ex)
        {
            return Defaults(SettingsLoadStatus.Corrupted, BackupBrokenFile(), ex.Message);
        }

        if (settings is null)
            return Defaults(SettingsLoadStatus.Corrupted, BackupBrokenFile(), "null");

        var fixes = SettingsNormalizer.Normalize(settings);
        return new SettingsLoadResult(settings, SettingsLoadStatus.Loaded, fixes);
    }

    public SettingsSaveResult TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return new(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 利用者が原因（暗号化・権限など）を確かめられるよう、フルパスと例外の詳細は言語にかかわらず残す。
            return new(false, string.Format(Strings.SettingsSaveFailedFormat, Path.GetFullPath(FilePath), ex.Message));
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, SettingsJson.Options);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, FilePath, overwrite: true);
    }

    private SettingsLoadResult Defaults(SettingsLoadStatus status, string? backup = null, string? error = null) =>
        new(new AppSettings(), status, [], backup, error);

    private string? BackupBrokenFile()
    {
        try
        {
            var backup = $"{FilePath}.broken-{_clock():yyyyMMddHHmmss}";
            File.Copy(FilePath, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
