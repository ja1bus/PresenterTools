using System.Diagnostics;
using System.IO;
using System.Text;

namespace PresenterTools.Services;

/// <summary>
/// 試験用の記録（V1.6.0。詳細設計 §8 の 9）。Debug ビルドで環境変数 <c>PRESENTERTOOLS_TEST_TRACE</c> に記録先を
/// 指定したときだけ書く。Release ビルドでは呼び出しごと消える（<see cref="ConditionalAttribute"/>）。
/// 文字そのものは書かず、<c>PRESENTERTOOLS_TEST_GROUPS</c>（例: <c>S=Qz7#;N=user1;R=ab</c>）の組の名前に置き換える。
/// 組に無い文字は <c>?</c>。
/// </summary>
public static class TestTrace
{
#if DEBUG
    private static readonly string? Path = Environment.GetEnvironmentVariable("PRESENTERTOOLS_TEST_TRACE");
    private static readonly Dictionary<char, char> Groups = ParseGroups(Environment.GetEnvironmentVariable("PRESENTERTOOLS_TEST_GROUPS"));
    private static readonly object Gate = new();
#endif

    [Conditional("DEBUG")]
    public static void Write(string kind, string detail = "")
    {
#if DEBUG
        if (string.IsNullOrEmpty(Path)) return;
        var line = $"{Stopwatch.GetTimestamp()}\t{kind}\t{detail}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Path, line, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
#endif
    }

    /// <summary>文字列を組の名前に置き換える（Debug ビルドだけで使う）。</summary>
    public static string Map(string? text)
    {
#if DEBUG
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(Groups.TryGetValue(c, out var g) ? g : '?');
        return sb.ToString();
#else
        return "";
#endif
    }

    /// <summary>キー名の表記。1 文字のラベルは組の名前に置き換え、それ以外（Tab・Ctrl など）はそのまま。</summary>
    public static string MapChord(IReadOnlyList<string> keys) =>
        string.Join("+", keys.Select(k => k.Length == 1 ? Map(k) : k));

#if DEBUG
    private static Dictionary<char, char> ParseGroups(string? spec)
    {
        var map = new Dictionary<char, char>();
        if (string.IsNullOrEmpty(spec)) return map;
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq != 1) continue;
            foreach (var c in part[(eq + 1)..]) map[c] = part[0];
        }
        return map;
    }
#endif
}
