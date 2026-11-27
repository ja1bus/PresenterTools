using PresenterTools.Core.Models;

namespace PresenterTools.Core.ScreenPen;

/// <summary>構成変更の通知 1 回の判定結果（<see cref="PenSession.OnDisplays"/>）。</summary>
public enum PenDisplayOutcome
{
    /// <summary>所有者が無い（何もしない）。</summary>
    None,
    Keep,
    /// <summary>描線を捨て、ペン中なら終える。所有者は解放した。</summary>
    Discard,
    /// <summary>描線を隠して待つ。ペン中なら終える。</summary>
    Pending,
}

/// <summary>
/// 画面ペンの所有者（<see cref="PenOwner"/>）の寿命と、構成変更・設定の適用での判定（docs/archive/design-1.7.0.md D9・D11）。
/// WPF に依存しない部分で、<c>PenController</c> が入力面・描線の有無を伝えて使う。
/// </summary>
public sealed class PenSession
{
    private long _generation;

    /// <summary>入力面と描線が属する実行中の画面。入力面か描線がある間だけ持つ。</summary>
    public PenOwner? Owner { get; private set; }

    /// <summary>モニター情報を取得できず判定を待っている。</summary>
    public bool IsPending { get; private set; }

    /// <summary>入力面が出ている。</summary>
    public bool SurfaceShown { get; private set; }

    /// <summary>
    /// ペンの開始に成功した。開始のたびに世代番号を新しくする。既存の所有者と違う画面なら false を返す
    /// （呼び出し側が先に描線を捨てる。通常は D9・D11 で先に捨てている）。
    /// </summary>
    public bool Start(DisplayDescriptor target, out PenOwner owner)
    {
        var same = Owner is not { } existing || (PenTargetResolver.SameIdentity(existing, target) && PenTargetResolver.SamePlacement(existing, target));
        _generation++;
        owner = PenOwner.From(target, _generation);
        Owner = owner;
        IsPending = false;
        SurfaceShown = true;
        return same;
    }

    /// <summary>入力面を閉じた。描線が無ければ所有者を解放して true。</summary>
    public bool SurfaceClosed(bool hasLines)
    {
        SurfaceShown = false;
        return ReleaseIfIdle(hasLines);
    }

    /// <summary>入力面も描線も無ければ所有者を解放して true（一画戻す・全消去の後にも呼ぶ）。</summary>
    public bool ReleaseIfIdle(bool hasLines)
    {
        if (SurfaceShown || hasLines || Owner is null) return false;
        Owner = null;
        IsPending = false;
        return true;
    }

    /// <summary>
    /// 構成変更の通知 1 回を比べる（D11）。破棄なら所有者を解放する（以後、同じ画面に戻っても描線は戻らない）。
    /// 保留なら <see cref="IsPending"/> を立てる。ペン中の終了は呼び出し側が行い、その後 <see cref="SurfaceClosed"/> を呼ぶ。
    /// </summary>
    public PenDisplayOutcome OnDisplays(DisplaySnapshot snapshot)
    {
        if (Owner is not { } owner) return PenDisplayOutcome.None;
        switch (PenTargetResolver.Compare(owner, snapshot))
        {
            case PenOwnerDecision.Keep:
                IsPending = false;
                // 同じ機器・配置で GDI 名だけが変わった場合（D11）、表示先と DPI 通知の照合に使う名前を新しい名前にする。
                // 世代は変えない（R4）。
                var match = snapshot.Displays.First(d => PenTargetResolver.SameIdentity(owner, d));
                if (!string.Equals(match.DeviceName, owner.DeviceName, StringComparison.Ordinal))
                    Owner = owner with { DeviceName = match.DeviceName };
                return PenDisplayOutcome.Keep;
            case PenOwnerDecision.Discard:
                Discard();
                return PenDisplayOutcome.Discard;
            default:
                IsPending = true;
                return PenDisplayOutcome.Pending;
        }
    }

    /// <summary>保留の再試行（1 秒後）を、この世代の所有者がまだ保留中のときだけ行う。</summary>
    public bool ShouldRetry(long generation) => IsPending && Owner is { } o && o.Generation == generation;

    /// <summary>
    /// 設定の適用（D9）。書き込み先の組が変わり、新しい書き込み先が所有者と同じ画面・配置に解決されなければ、
    /// 所有者を解放して true（呼び出し側が描線を捨て、ペン中なら終える）。
    /// </summary>
    public bool OnApply(PenSettings before, PenSettings after, DisplaySnapshot snapshot)
    {
        if (Owner is not { } owner) return false;
        if (PenTargetResolver.OnApply(owner, before, after, snapshot) == PenOwnerDecision.Keep) return false;
        Discard();
        return true;
    }

    /// <summary>所有者を手放す（描線を捨てた）。</summary>
    public void Discard()
    {
        Owner = null;
        IsPending = false;
    }
}
