using PresenterTools.Core.Geometry;
using PresenterTools.Core.Models;

namespace PresenterTools.Core.ScreenPen;

/// <summary>書き込み先を決めた結果の種類（docs/archive/design-1.7.0.md D3）。</summary>
public enum PenTargetStatus
{
    Resolved,
    /// <summary>モニターの情報を取得できない。</summary>
    Unknown,
    /// <summary>読み込んだ設定の書き込み先をこの PC で特定できず、選び直しが必要。</summary>
    Reselect,
    /// <summary>同じ識別の画面が 2 台以上。</summary>
    Ambiguous,
    /// <summary>保存した書き込み先が接続されていない。</summary>
    NotConnected,
    /// <summary>複数台で書き込み先が未指定で、自動で選ぶ画面も決められない。</summary>
    NotSelected,
}

public sealed record PenTargetResult(PenTargetStatus Status, DisplayDescriptor? Display = null)
{
    public bool IsResolved => Status == PenTargetStatus.Resolved;
}

/// <summary>構成変更での描線の扱い（D11）。</summary>
public enum PenOwnerDecision
{
    Keep,
    Discard,
    Pending,
}

/// <summary>書き込み先の切り替えの判定の種類（docs/archive/design-1.8.0.md P6）。</summary>
public enum PenSwitchKind
{
    /// <summary>移動元の情報を取得できない（保留。切り替えない）。</summary>
    SourcePending,
    /// <summary>移動元が使えなくなった（破棄。切り替えない）。</summary>
    SourceDiscard,
    /// <summary>選んだ画面に切り替えられない（<see cref="PenSwitchDecision.Status"/> が理由）。</summary>
    Failed,
    /// <summary>今の画面を選んだ。</summary>
    Same,
    /// <summary>切り替える。</summary>
    Go,
}

/// <summary>切り替えの判定の結果。Go では移動先と移動元（どちらも今回の列挙の記述）を持つ。</summary>
public sealed record PenSwitchDecision(PenSwitchKind Kind, PenTargetStatus Status = PenTargetStatus.Unknown, DisplayDescriptor? Target = null, DisplayDescriptor? Source = null);

/// <summary>入力面と描線が属する実行中の画面（D11）。設定の書き込み先とは別に持つ。</summary>
public sealed record PenOwner(DisplayIdentity Identity, string DeviceKey, string DeviceName, RectInt Bounds, double Scale, long Generation)
{
    public static PenOwner From(DisplayDescriptor d, long generation) =>
        new(d.Identity, d.DeviceKey, d.DeviceName, d.Bounds, d.Scale, generation);
}

/// <summary>保存した書き込み先と接続中の画面から使う画面を決め、構成変更で描線を残すかを決める（D3・D11）。</summary>
public static class PenTargetResolver
{
    public static PenTargetResult Resolve(PenSettings saved, DisplaySnapshot snapshot)
    {
        if (!snapshot.ListComplete || snapshot.Placeholder) return new(PenTargetStatus.Unknown);
        var displays = snapshot.Displays;

        if (saved.HasTarget)
        {
            if (saved.TargetReselect) return new(PenTargetStatus.Reselect);
            if (saved.TargetDevicePath.Length > 0)
            {
                var matches = displays.Where(d => d.Identity == DisplayIdentity.Key && d.DeviceKey == saved.TargetDevicePath).ToList();
                if (matches.Count == 1) return new(PenTargetStatus.Resolved, matches[0]);
                if (matches.Count > 1) return new(PenTargetStatus.Ambiguous);
                // そこに対象がある可能性を消せないため、未接続と断定しない。
                if (snapshot.HasUnidentified) return new(PenTargetStatus.Unknown);
                return new(PenTargetStatus.NotConnected);
            }
            if (saved.TargetDeviceName.Length > 0)
            {
                var gdi = displays.Where(d => d.Identity == DisplayIdentity.Gdi
                    && string.Equals(d.DeviceName, saved.TargetDeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (gdi.Count == 1) return new(PenTargetStatus.Resolved, gdi[0]);
                if (gdi.Count > 1) return new(PenTargetStatus.Ambiguous);
            }
            return new(PenTargetStatus.NotConnected);
        }

        if (displays.Count == 1)
        {
            var only = displays[0];
            return only.Identity == DisplayIdentity.Failed ? new(PenTargetStatus.Unknown) : new(PenTargetStatus.Resolved, only);
        }
        return AutoSelect(displays);
    }

    /// <summary>
    /// 未指定で複数台のとき（2026-11-27 の利用者の要望）。内蔵ディスプレイが 1 台ならそれ、なければメイン ディスプレイを使う。
    /// 識別できない画面があると内蔵かどうかを判定できないため、内蔵が見つからない限り Unknown とする。
    /// </summary>
    private static PenTargetResult AutoSelect(IReadOnlyList<DisplayDescriptor> displays)
    {
        var usable = displays.Where(d => d.Identity != DisplayIdentity.Failed).ToList();
        var internals = usable.Where(d => d.IsInternal).ToList();
        if (internals.Count == 1) return new(PenTargetStatus.Resolved, internals[0]);
        if (usable.Count < displays.Count) return new(PenTargetStatus.Unknown);
        var primary = usable.Where(d => d.IsPrimary).ToList();
        return primary.Count == 1 ? new(PenTargetStatus.Resolved, primary[0]) : new(PenTargetStatus.NotSelected);
    }

    /// <summary>所有者と同じ識別か（配置は見ない）。</summary>
    public static bool SameIdentity(PenOwner owner, DisplayDescriptor d) => owner.Identity switch
    {
        DisplayIdentity.Key => d.Identity == DisplayIdentity.Key && d.DeviceKey == owner.DeviceKey,
        DisplayIdentity.Gdi => string.Equals(d.DeviceName, owner.DeviceName, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>全体領域（物理）と拡大率が同じか。</summary>
    public static bool SamePlacement(PenOwner owner, DisplayDescriptor d) =>
        d.Bounds == owner.Bounds && Math.Abs(d.Scale - owner.Scale) < 0.001;

    /// <summary>構成変更の後、描線を残すか（D11 の比較表を上から順に）。</summary>
    public static PenOwnerDecision Compare(PenOwner owner, DisplaySnapshot snapshot)
    {
        if (!snapshot.ListComplete || snapshot.Placeholder) return PenOwnerDecision.Pending;
        var same = snapshot.Displays.Where(d => SameIdentity(owner, d)).ToList();

        if (owner.Identity == DisplayIdentity.Key)
        {
            if (same.Count == 1) return SamePlacement(owner, same[0]) ? PenOwnerDecision.Keep : PenOwnerDecision.Discard;
            if (same.Count > 1) return PenOwnerDecision.Discard;
            // 照会できないことを不存在より優先する（複製の片方だけ取れなかった場合も）。
            return snapshot.HasUnidentified ? PenOwnerDecision.Pending : PenOwnerDecision.Discard;
        }

        // Gdi: GDI 名と配置で識別する（同じ番号・同じ配置の別機器は区別できない。D14 の15）。
        return same.Count == 1 && SamePlacement(owner, same[0]) ? PenOwnerDecision.Keep : PenOwnerDecision.Discard;
    }

    // ---- 書き込み先の切り替え（V1.8.0。docs/archive/design-1.8.0.md P6） ----

    /// <summary>右クリックのサブメニューに出す画面。列挙が不完全なら空。識別に失敗した画面は出さない。</summary>
    public static IReadOnlyList<DisplayDescriptor> SwitchCandidates(DisplaySnapshot snapshot) =>
        !snapshot.ListComplete || snapshot.Placeholder
            ? []
            : snapshot.Displays.Where(d => d.Identity != DisplayIdentity.Failed).ToList();

    /// <summary>メニューを開いた時点の記述 <paramref name="chosen"/> を、新しい列挙で探し直す。</summary>
    public static PenTargetResult FindCurrent(DisplayDescriptor chosen, DisplaySnapshot snapshot)
    {
        if (!snapshot.ListComplete || snapshot.Placeholder) return new(PenTargetStatus.Unknown);
        var matches = chosen.Identity switch
        {
            DisplayIdentity.Key => snapshot.Displays.Where(d => d.Identity == DisplayIdentity.Key && d.DeviceKey == chosen.DeviceKey).ToList(),
            DisplayIdentity.Gdi => snapshot.Displays.Where(d => d.Identity == DisplayIdentity.Gdi
                && string.Equals(d.DeviceName, chosen.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => [],
        };
        if (matches.Count == 1) return new(PenTargetStatus.Resolved, matches[0]);
        if (matches.Count > 1) return new(PenTargetStatus.Ambiguous);
        return snapshot.HasUnidentified ? new(PenTargetStatus.Unknown) : new(PenTargetStatus.NotConnected);
    }

    /// <summary>
    /// 切り替えの判定（P6 の手順 3）。上から順に: 移動元を D11 で比べ（保留・破棄を優先）、選んだ画面を探し、
    /// 今の画面なら Same、そうでなければ Go。
    /// </summary>
    public static PenSwitchDecision DecideSwitch(PenOwner owner, DisplayDescriptor chosen, DisplaySnapshot snapshot)
    {
        switch (Compare(owner, snapshot))
        {
            case PenOwnerDecision.Pending: return new(PenSwitchKind.SourcePending);
            case PenOwnerDecision.Discard: return new(PenSwitchKind.SourceDiscard);
        }
        var found = FindCurrent(chosen, snapshot);
        if (!found.IsResolved) return new(PenSwitchKind.Failed, found.Status);
        var target = found.Display!;
        if (SameIdentity(owner, target) && SamePlacement(owner, target)) return new(PenSwitchKind.Same, PenTargetStatus.Resolved, target);
        // 保持の判定なので、所有者と同じ識別の画面はちょうど 1 台ある。
        var source = snapshot.Displays.First(d => SameIdentity(owner, d));
        return new(PenSwitchKind.Go, PenTargetStatus.Resolved, target, source);
    }

    /// <summary>
    /// 設定の適用時（D9）。書き込み先の組が変わっていなければ保持。変わったら、新しい書き込み先が所有者と同じ画面・同じ配置のときだけ保持する。
    /// </summary>
    public static PenOwnerDecision OnApply(PenOwner owner, PenSettings before, PenSettings after, DisplaySnapshot snapshot)
    {
        if (before.SameTargetAs(after)) return PenOwnerDecision.Keep;
        var r = Resolve(after, snapshot);
        return r.IsResolved && SameIdentity(owner, r.Display!) && SamePlacement(owner, r.Display!)
            ? PenOwnerDecision.Keep
            : PenOwnerDecision.Discard;
    }
}
