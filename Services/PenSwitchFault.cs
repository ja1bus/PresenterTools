using PresenterTools.Core.ScreenPen;

namespace PresenterTools.Services;

/// <summary>
/// 書き込み先の切り替えの試験用の故障（docs/archive/design-1.8.0.md P6。Debug ビルドだけ）。
/// 環境変数 <c>PRESENTERTOOLS_TEST_PEN_SWITCH_FAIL</c> の値: gone・dup・incomplete・source・overlay・show・dpi、
/// コードレビューの回帰確認用に show-dpi・show-both・dpi-discard・start-show。
/// Release では常に何もしない。
/// </summary>
public static class PenSwitchFault
{
#if DEBUG
    private static readonly string Mode = (Environment.GetEnvironmentVariable("PRESENTERTOOLS_TEST_PEN_SWITCH_FAIL") ?? "").Trim().ToLowerInvariant();
#else
    private static readonly string Mode = ""; // const だと Release で到達できないコードの警告になる
#endif

    public static bool Overlay => Mode == "overlay";
    /// <summary>移動先の入力面の表示を失敗させる（show・show-dpi・show-both）。</summary>
    public static bool Show => Mode is "show" or "show-dpi" or "show-both";
    /// <summary>移動元へ戻す入力面の表示も失敗させる（show-both。コードレビュー R1）。</summary>
    public static bool ShowBoth => Mode == "show-both";
    /// <summary>移動先の表示中に DPI 通知を与える（dpi・dpi-discard）。</summary>
    public static bool DpiOnTarget => Mode is "dpi" or "dpi-discard";
    /// <summary>移動元へ戻す表示中に DPI 通知を与える（show-dpi。コードレビュー R1）。</summary>
    public static bool DpiOnRestore => Mode == "show-dpi";

    private static bool _startShowUsed;
    private static bool _recheckUsed;

    /// <summary>別の画面に線を残した状態からの開始で、入力面の表示を 1 回だけ失敗させる（start-show。コードレビュー R4）。</summary>
    public static bool TakeStartShow()
    {
        if (Mode != "start-show" || _startShowUsed) return false;
        _startShowUsed = true;
        return true;
    }

    /// <summary>
    /// DPI 通知による最初の再確認の列挙に故障を与える（show-dpi: 不完全、dpi-discard: 所有者の画面が無い）。1 回だけ。
    /// 1 秒後の再試行（retry）は実際の列挙のまま。
    /// </summary>
    public static DisplaySnapshot ApplyRecheck(DisplaySnapshot snapshot, string trigger, PenOwner? owner)
    {
        if (trigger != "dpi" || _recheckUsed || owner is null) return snapshot;
        switch (Mode)
        {
            case "show-dpi":
                _recheckUsed = true;
                return snapshot with { ListComplete = false };
            case "dpi-discard":
                _recheckUsed = true;
                return snapshot with { Displays = snapshot.Displays.Where(d => !PenTargetResolver.SameIdentity(owner, d)).ToList() };
            default:
                return snapshot;
        }
    }

    /// <summary>切り替えのときの列挙に故障を与える（gone・dup・incomplete・source）。</summary>
    public static DisplaySnapshot Apply(DisplaySnapshot snapshot, DisplayDescriptor chosen, PenOwner? owner)
    {
        bool IsChosen(DisplayDescriptor d) => chosen.Identity == DisplayIdentity.Key
            ? d.DeviceKey == chosen.DeviceKey && d.Identity == DisplayIdentity.Key
            : string.Equals(d.DeviceName, chosen.DeviceName, StringComparison.OrdinalIgnoreCase);
        switch (Mode)
        {
            case "gone":
                return snapshot with { Displays = snapshot.Displays.Where(d => !IsChosen(d)).ToList() };
            case "dup":
                var match = snapshot.Displays.FirstOrDefault(IsChosen);
                return match is null ? snapshot : snapshot with { Displays = [.. snapshot.Displays, match] };
            case "incomplete":
                return snapshot with { ListComplete = false };
            case "source":
                return owner is null
                    ? snapshot
                    : snapshot with { Displays = snapshot.Displays.Where(d => !PenTargetResolver.SameIdentity(owner, d)).ToList() };
            default:
                return snapshot;
        }
    }
}
