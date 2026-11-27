using System.Text;
using PresenterTools.Core.Geometry;

namespace PresenterTools.Core.ScreenPen;

/// <summary>画面の識別の種類（docs/archive/design-1.7.0.md D3）。</summary>
public enum DisplayIdentity
{
    /// <summary>照会が成功し、その GDI 名の全出力先のデバイスパスが取れた。<see cref="DisplayDescriptor.DeviceKey"/> で識別する。</summary>
    Key,
    /// <summary>デバイスパスで識別できず GDI 名で識別する（RDP・仮想ディスプレイ、照会全体の失敗）。</summary>
    Gdi,
    /// <summary>出力先の一部だけ情報が取れなかった、またはキーが上限を超えた。書き込み先にできない。</summary>
    Failed,
}

/// <summary>WPF に依存しない画面 1 台の記述。座標は物理ピクセル、Scale は 1.0 = 100%。</summary>
public sealed record DisplayDescriptor(
    DisplayIdentity Identity,
    string DeviceKey,
    string DeviceName,
    string FriendlyName,
    RectInt Bounds,
    bool IsPrimary,
    double Scale)
{
    /// <summary>出力先が複数の複製表示。</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>出力先にノート PC などの内蔵ディスプレイを含む（未指定のときの書き込み先の候補）。</summary>
    public bool IsInternal { get; init; }
}

/// <summary>1 回の列挙の結果（D3）。<see cref="ListComplete"/> が false なら判定に使わない（Unknown・保留）。</summary>
public sealed record DisplaySnapshot(bool ListComplete, bool QueryFailed, IReadOnlyList<DisplayDescriptor> Displays)
{
    public bool Placeholder { get; init; }

    public static DisplaySnapshot Incomplete { get; } = new(false, true, []) { Placeholder = true };

    /// <summary>識別できない画面があるか（照会全体の失敗、または一部の出力先の失敗）。</summary>
    public bool HasUnidentified => QueryFailed || Displays.Any(d => d.Identity == DisplayIdentity.Failed);
}

/// <summary>
/// 出力先の識別 DeviceKey（D3）。各デバイスパスを大文字に正規化し、序数順に並べ、各要素を「文字数:パス」として連結する。
/// 単一の出力先も同じ形式。区切り文字を含むパスでも衝突せず、元の組へ一意に戻せる。
/// </summary>
public static class DeviceKey
{
    /// <summary>選べる画面のキーの上限（設定の上限と同じ）。</summary>
    public const int MaxLength = 8192;

    public static string Create(IEnumerable<string> devicePaths)
    {
        var sb = new StringBuilder();
        foreach (var path in devicePaths.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal))
            sb.Append(path.Length).Append(':').Append(path);
        return sb.ToString();
    }

    /// <summary>キーを出力先のパスの組へ戻す。形式に合わなければ false。</summary>
    public static bool TryParse(string key, out IReadOnlyList<string> paths)
    {
        var list = new List<string>();
        paths = list;
        var i = 0;
        while (i < key.Length)
        {
            var colon = key.IndexOf(':', i);
            if (colon <= i || !int.TryParse(key.AsSpan(i, colon - i), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var len)
                || len <= 0 || len > key.Length - colon - 1) return false; // 加算のオーバーフローを避ける（R8）
            list.Add(key.Substring(colon + 1, len));
            i = colon + 1 + len;
        }
        return list.Count > 0;
    }
}

/// <summary>列挙の生データ 1 行分（EnumDisplayMonitors の 1 台）。</summary>
public sealed record RawMonitor(string DeviceName, RectInt Bounds, bool IsPrimary, double? Scale, bool InfoOk);

/// <summary>
/// QueryDisplayConfig の 1 つの active path（source 側の GDI 名と target 側のデバイスパス・名前）。
/// SourceDeviceName は取得に失敗したら null。TargetQueried は target 側の照会が成功したか（成功してもパスが空の出力先はある）。
/// Internal は出力の方式が内蔵（ノート PC の画面など）か。
/// </summary>
public sealed record RawPath(string? SourceDeviceName, string? TargetDevicePath, string? TargetFriendlyName, bool TargetQueried = true, bool Internal = false);

/// <summary>
/// 列挙の生データから <see cref="DisplaySnapshot"/> を作る（D3。Win32 の呼び出しは MonitorService 側で行い、ここは判定だけ）。
/// </summary>
public static class DisplaySnapshotBuilder
{
    /// <param name="monitors">EnumDisplayMonitors の結果。GetMonitorInfo に失敗した行は InfoOk=false。</param>
    /// <param name="enumOk">EnumDisplayMonitors 自体が成功したか。</param>
    /// <param name="paths">active path の一覧。照会全体が失敗したら null。</param>
    public static DisplaySnapshot Build(IReadOnlyList<RawMonitor> monitors, bool enumOk, IReadOnlyList<RawPath>? paths)
    {
        var queryFailed = paths is null;
        if (!enumOk || monitors.Count == 0)
            return new DisplaySnapshot(false, queryFailed, []) { Placeholder = monitors.Count == 0 };

        var complete = monitors.All(m => m.InfoOk && m.Scale is > 0);
        var list = new List<DisplayDescriptor>();
        foreach (var m in monitors.Where(m => m.InfoOk))
        {
            var scale = m.Scale is > 0 ? m.Scale.Value : 1.0;
            if (paths is null)
            {
                list.Add(new DisplayDescriptor(DisplayIdentity.Gdi, "", m.DeviceName, "", m.Bounds, m.IsPrimary, scale));
                continue;
            }
            var mine = paths.Where(p => string.Equals(p.SourceDeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            // source 名を取れなかった path があると、それがこの画面の出力先かもしれないため、確実に識別できない。
            var unknownSource = paths.Any(p => p.SourceDeviceName is null);
            if (mine.Count == 0)
            {
                var identity = unknownSource ? DisplayIdentity.Failed : DisplayIdentity.Gdi;
                list.Add(new DisplayDescriptor(identity, "", m.DeviceName, "", m.Bounds, m.IsPrimary, scale));
                continue;
            }
            // 複製の名前は並べ替えた先頭の出力先から取り、列挙の順序に依存させない。
            var friendly = mine.OrderBy(p => p.TargetDevicePath?.ToUpperInvariant() ?? "", StringComparer.Ordinal).First().TargetFriendlyName ?? "";
            var isInternal = mine.Any(p => p.Internal);
            if (unknownSource || mine.Any(p => !p.TargetQueried))
            {
                // 一部の出力先だけでキーを作らない（複製の片方だけ取れなかった場合を含む）。
                list.Add(new DisplayDescriptor(DisplayIdentity.Failed, "", m.DeviceName, friendly, m.Bounds, m.IsPrimary, scale) { IsDuplicate = mine.Count > 1, IsInternal = isInternal });
                continue;
            }
            var withPath = mine.Where(p => !string.IsNullOrEmpty(p.TargetDevicePath)).ToList();
            if (withPath.Count == 0)
            {
                // 照会は成功したがデバイスパスを持たない出力先（仮想ディスプレイなど）だけ。GDI 名で識別する。
                list.Add(new DisplayDescriptor(DisplayIdentity.Gdi, "", m.DeviceName, friendly, m.Bounds, m.IsPrimary, scale) { IsDuplicate = mine.Count > 1, IsInternal = isInternal });
                continue;
            }
            if (withPath.Count < mine.Count)
            {
                // 一部の出力先だけパスが空。残りだけでキーを作ると単独の画面と同じキーになるため識別しない（R5）。
                list.Add(new DisplayDescriptor(DisplayIdentity.Failed, "", m.DeviceName, friendly, m.Bounds, m.IsPrimary, scale) { IsDuplicate = true, IsInternal = isInternal });
                continue;
            }
            var key = DeviceKey.Create(withPath.Select(p => p.TargetDevicePath!));
            var id = key.Length > DeviceKey.MaxLength ? DisplayIdentity.Failed : DisplayIdentity.Key;
            list.Add(new DisplayDescriptor(id, id == DisplayIdentity.Key ? key : "", m.DeviceName, friendly, m.Bounds, m.IsPrimary, scale)
            {
                IsDuplicate = mine.Count > 1,
                IsInternal = isInternal,
            });
        }
        return new DisplaySnapshot(complete, queryFailed, list);
    }
}
