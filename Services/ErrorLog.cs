using System.IO;
using System.Text;
using PresenterTools.Core.Settings;

namespace PresenterTools.Services;

/// <summary>
/// %LocalAppData%\PresenterTools\error.log への記録（SPEC §20）。
/// キー入力の内容は書かない（SPEC §21）。
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.DefaultDirectory, "error.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DefaultDirectory);
                var text = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').AppendLine(message);
                if (exception is not null) text.AppendLine(exception.ToString());
                File.AppendAllText(FilePath, text.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくても本体の動作は続ける。
        }
    }
}
