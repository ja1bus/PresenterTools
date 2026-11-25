using PresenterTools.Core.Input;

namespace PresenterTools.Core.KeyDisplay;

/// <summary>入力先の欄の種類（UI Automation の IsPassword）。</summary>
public enum FieldKind
{
    Normal,
    Password,
}

/// <summary>判定が済んだキー。<see cref="Hidden"/> なら表示処理へ渡さない。</summary>
public readonly record struct GatedKey(KeyInput Input, long Timestamp, IntPtr Foreground, bool Hidden);

/// <summary>
/// パスワード欄での自動非表示の判定（V1.6.0。詳細設計 §2）。キーを保留し、入力先の照会の結果とフォーカス変更イベント
/// （観測）を、入力先を移し得るキー（区切り）で分けた区間に結び付けて、表示するか隠すかを決める。
/// 時刻はすべて同じ時計の値（アプリでは <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>）。
/// Win32・UI Automation に依存せず、UI スレッドからだけ呼ぶ。
/// </summary>
public sealed class PasswordInputGate
{
    private const int MaxObservations = 512;

    private sealed record Pending(KeyInput Input, long Time, IntPtr Foreground, bool IsBoundary);

    /// <summary>
    /// 観測。照会（<see cref="IsEvent"/> = false）の時刻は照会の開始時刻、<see cref="End"/> は結果が戻った時刻（イベントは時刻と同じ）。
    /// 照会の結果は開始から戻るまでのどの時点の入力先でもあり得るので、その間に区切りがあれば区切りの前の判定には使わない。
    /// </summary>
    private readonly record struct Observation(long Time, FieldKind Kind, IntPtr Foreground, bool IsEvent, long End);

    private readonly long _settle;
    private readonly long _timeout;
    private readonly long _poll;
    private readonly List<Pending> _queue = [];
    private readonly List<long> _boundaries = [];
    private readonly List<Observation> _observations = [];   // 時刻順
    private readonly List<long> _failedQueries = [];          // 失敗した照会の開始時刻（時刻順）
    private long _startTime;
    private long _lastQueryStart = long.MinValue;             // 結果（成功・失敗）が届いた照会の開始時刻の最大
    // 専用スレッドが始めたと知らされ、結果がまだ届いていない照会の開始時刻（時刻順）。最大だけを持つと、その間に
    // 照会が実在したとみなしてしまうので、知らされた照会を個々に持つ（コードレビュー R7）。
    private readonly List<long> _unreportedStarts = [];
    private long? _initialRequest;
    private long? _lastBoundary;                              // 最後の区切りの時刻（区間の照会を依頼するため。保留を解いても残す）
    private bool _passwordObserved;

    /// <param name="settle">キーを受けてから照会するまでの待ち時間（<c>SettleDelay</c>）。</param>
    /// <param name="timeout">待ち時間の後、照会の結果を待つ時間（<c>QueryTimeout</c>）。</param>
    /// <param name="poll">入力先がパスワード欄の間に照会する間隔（<c>PasswordPoll</c>）。0 なら照会しない。</param>
    public PasswordInputGate(long settle, long timeout, long poll = 0)
    {
        if (settle <= 0 || timeout <= 0 || poll < 0) throw new ArgumentOutOfRangeException(nameof(settle));
        _settle = settle;
        _timeout = timeout;
        _poll = poll;
    }

    /// <summary>監視の回の番号。開始・停止のたびに増える。</summary>
    public int Generation { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>照会が時間切れになったキーの数（記録用）。</summary>
    public int TimedOutCount { get; private set; }

    public int PendingCount => _queue.Count;

    /// <summary>監視を始める。新しい回の番号を返す。開始時の入力先をすぐに 1 回照会する。</summary>
    public int Start(long now)
    {
        ClearState();
        Generation++;
        IsActive = true;
        _startTime = now;
        _initialRequest = now;
        return Generation;
    }

    /// <summary>
    /// 監視を止め、保留を捨てる。押下は捨て、離したイベントだけを順に返す（除外中のショートカットの記録を解くため。詳細設計 §2.3）。
    /// </summary>
    public IReadOnlyList<KeyInput> Stop()
    {
        var ups = _queue.Where(p => !p.Input.IsKeyDown).Select(p => p.Input).ToList();
        ClearState();
        Generation++;
        IsActive = false;
        return ups;
    }

    /// <summary>キーを保留の列へ入れる。時刻はフックで受けた時刻。</summary>
    public void Enqueue(in KeyInput input, long time, IntPtr foreground)
    {
        if (!IsActive) return;
        var boundary = IsBoundary(input);
        _queue.Add(new Pending(input, time, foreground, boundary));
        if (boundary)
        {
            _boundaries.Add(time);
            if (_lastBoundary is null || time > _lastBoundary) _lastBoundary = time;
        }
    }

    /// <summary>
    /// 照会の結果（<paramref name="isEvent"/> = false、時刻は開始時刻、<paramref name="end"/> は結果が戻った時刻）または
    /// フォーカス変更イベントを受ける。古い回のものは捨てる。<paramref name="end"/> を省くと時刻と同じ。
    /// </summary>
    public bool OnObservation(int generation, FieldKind kind, long time, IntPtr foreground, bool isEvent, long? end = null)
    {
        if (!IsActive || generation != Generation) return false;
        var o = new Observation(time, kind, foreground, isEvent, isEvent ? time : Math.Max(time, end ?? time));
        var index = _observations.FindLastIndex(x => x.Time <= time) + 1;
        _observations.Insert(index, o);
        // 最新かどうかは、件数の上限で古いものを捨てる前に決める（捨てた後では添字がずれる。コードレビュー R2）。
        var isLatest = index == _observations.Count - 1;
        if (_observations.Count > MaxObservations) _observations.RemoveAt(0);
        if (!isEvent) NoteQueryDone(time);
        // 現在の入力先がパスワード欄になったら、表示中の内容を消す合図（時刻の古い観測では消さない）。
        if (kind == FieldKind.Password && isLatest) _passwordObserved = true;
        return true;
    }

    /// <summary>照会が失敗した（例外）。時刻は開始時刻。古い回のものは捨てる。</summary>
    public bool OnQueryFailed(int generation, long startedAt)
    {
        if (!IsActive || generation != Generation) return false;
        var index = _failedQueries.FindLastIndex(x => x <= startedAt) + 1;
        _failedQueries.Insert(index, startedAt);
        if (_failedQueries.Count > MaxObservations) _failedQueries.RemoveAt(0);
        NoteQueryDone(startedAt);
        return true;
    }

    /// <summary>
    /// 専用スレッドが照会を始めた（結果はまだ届いていないことがある）。時刻は開始時刻。区間の照会が実行中かを知り、
    /// その結果を待つために使う（詳細設計 §2.2「保留を解く時刻」。コードレビュー R1・R4）。
    /// 照会は 1 本のスレッドで順に行い、結果は始めた順に届く前提。知らされた照会だけを待つ（知らされる前に始まって
    /// 結果も届いた照会は待つ必要がなく、知らされていない照会を時刻の間から推定しない。R7）。
    /// </summary>
    public void NoteQueryStarted(long start)
    {
        if (!IsActive || start <= _lastQueryStart) return; // 結果が届いた照会
        if (_unreportedStarts.Count > 0 && start <= _unreportedStarts[^1])
        {
            if (_unreportedStarts.Contains(start)) return;
            _unreportedStarts.Insert(_unreportedStarts.FindLastIndex(s => s < start) + 1, start);
        }
        else _unreportedStarts.Add(start);
        if (_unreportedStarts.Count > MaxObservations) _unreportedStarts.RemoveAt(0);
    }

    /// <summary>最新の観測がパスワード欄になったか（表示を消す合図）。読むと戻る。</summary>
    public bool ConsumePasswordObserved()
    {
        var value = _passwordObserved;
        _passwordObserved = false;
        return value;
    }

    /// <summary>
    /// 次に照会を始めてほしい時刻（これ以降に 1 回）。不要なら null。入力先がパスワード欄の間は、キーが無くても
    /// <c>poll</c> ごとに照会する（「パスワードを表示」で通常の文字欄に変わったことに気付くため。フォーカスは動かずイベントが来ない）。
    /// </summary>
    public long? NextQueryAt
    {
        get
        {
            long? next = _initialRequest;
            // 区切りの後の区間には、保留中のキーが無くても照会を 1 回依頼する。区切りのキーは区間の照会が済んでいれば
            // すぐ判定されて列から消えるので、依頼しないと次の区間に照会が無いまま、後のキーが区間の終わりで捨てられる
            // （工程 G、T-08 の Enter の後の Ctrl+C で検出）。
            if (_lastBoundary is { } lb && lb + _settle > _lastQueryStart && (next is null || lb + _settle < next)) next = lb + _settle;
            if (_poll > 0 && _observations.Count > 0 && _observations[^1].Kind == FieldKind.Password)
            {
                var poll = Math.Max(_observations[^1].Time, _lastQueryStart) + _poll;
                if (next is null || poll < next) next = poll;
            }
            foreach (var p in _queue)
            {
                if (!p.Input.IsKeyDown) continue;
                var r = p.Time + _settle;
                if (r <= _lastQueryStart) continue;
                if (next is null || r < next) next = r;
            }
            return next;
        }
    }

    /// <summary>照会が届かなくても判定する時刻（時間切れ）。保留が無ければ null。</summary>
    public long? NextTimeoutAt
    {
        get
        {
            long? next = null;
            foreach (var p in _queue)
            {
                if (!p.Input.IsKeyDown) continue;
                var r = p.Time + _settle;
                if (r <= _lastQueryStart) continue;
                var t = r + _timeout;
                if (next is null || t < next) next = t;
            }
            return next;
        }
    }

    /// <summary>判定の済んだキーを受けた順に返す。先頭が未判定なら止まる。</summary>
    public IReadOnlyList<GatedKey> TakeResolved(long now)
    {
        var result = new List<GatedKey>();
        while (_queue.Count > 0)
        {
            var p = _queue[0];
            if (!p.Input.IsKeyDown)
            {
                result.Add(new GatedKey(p.Input, p.Time, p.Foreground, false));
                _queue.RemoveAt(0);
                continue;
            }
            var r = p.Time + _settle;
            var (segStart, segEnd) = SegmentOf(p);
            // 場合 A: キーの後に始めた照会の結果が届いた。
            var answered = r <= _lastQueryStart;
            // 場合 B: 区間が閉じ、区切りより前に始めた照会の結果がすべて届いている。次の区間の照会を待たずに判定する（R4）。
            // 区切りより前に始めた照会が実行中なら、その結果か時間切れを待つ（§2.2「保留を解く時刻」）。
            var closed = !answered && segEnd is { } e && !HasUnreportedQuery(long.MinValue, e);
            if (!answered && !closed && now < r + _timeout) break;
            if (!answered && !closed) TimedOutCount++;
            // 表示すると判定したキーでも、そのキーの後の観測で同じ窓の入力先がパスワード欄になっていれば出さない
            // （入欄で表示を消した後に、通常欄で押したキーが遅れて出ないように。詳細設計 §2.2）。
            var hidden = Decide(p, segStart, segEnd) || (_observations.Count > 0 && _observations[^1] is { Kind: FieldKind.Password } now0
                && now0.Foreground == p.Foreground && now0.Time > p.Time);
            result.Add(new GatedKey(p.Input, p.Time, p.Foreground, hidden));
            _queue.RemoveAt(0);
        }
        Prune();
        return result;
    }

    /// <summary>
    /// 入力先を移し得るキー（区切り）か。Ctrl・Alt・Win を伴わない文字キー、Backspace、Shift、CapsLock、IME のキー以外の押下（詳細設計 §2.1）。
    /// </summary>
    public static bool IsBoundary(in KeyInput input)
    {
        if (!input.IsKeyDown) return false;
        var vk = input.VirtualKey;
        if (vk is VirtualKeys.Back or VirtualKeys.Capital or VirtualKeys.Shift or VirtualKeys.LShift or VirtualKeys.RShift) return false;
        if (VirtualKeys.IsImeKey(vk)) return false;
        // 修飾キー単独の押下は区切りにしない。押しただけでは入力先は動かず、区切りにすると、直後に押す Ctrl+C などの
        // 主キーが長さ数 ms の区間に入って、入力先を特定できないキーとして捨てられる（工程 G、T-08 で検出）。
        // 組み合わせの主キー（Ctrl+C・Alt+Tab・Shift+Tab など）は区切り。
        if (VirtualKeys.IsModifier(vk)) return false;
        var command = (input.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        if (command || VirtualKeys.IsNamedSpecialKey(vk)) return true;
        return !IsPrintable(input.Text);
    }

    /// <summary>キーが属する区間（始まりと、次の区切りの時刻。区切りのキー自身はその時刻で終わる）。</summary>
    private (long Start, long? End) SegmentOf(Pending k)
    {
        var t = k.Time;
        var segStart = _startTime;
        foreach (var b in _boundaries)
        {
            if (b < t) segStart = b;
            else break;
        }
        if (k.IsBoundary) return (segStart, t);
        foreach (var b in _boundaries)
        {
            if (b > t) return (segStart, b);
        }
        return (segStart, null);
    }

    /// <summary>
    /// [from, to) に始めたと知らされた照会のうち、結果がまだ届いていないものがあるか（R7。開始時刻の間に照会があるとはみなさない）。
    /// </summary>
    private bool HasUnreportedQuery(long from, long to) => _unreportedStarts.Exists(s => s >= from && s < to);

    private bool Decide(Pending k, long segStart, long? segEnd)
    {
        var t = k.Time;
        var q = FirstQueryAtOrAfter(t + _settle);
        // Q が無い（時間切れ）まま区間が閉じていれば、区間の観測で判定する。Q が区切りをまたいだ（区切りの後に戻った）
        // 場合も、Q は次の欄を答えたかもしれないので使わず、区間の観測で判定する（工程 G で追加。T-05 の WPF で検出）。
        var caseB = segEnd is { } e0 && (q is null || e0 <= q.Value.Start || q.Value.End >= e0);
        var limit = caseB ? segEnd!.Value : q?.Start ?? long.MaxValue;

        // (a) 押した時点で、同じ区間・同じ前面の窓の最新の観測がパスワード欄。押す前に始めて押した後に戻った照会でも、
        // その間に区切りが無ければ（押したのが区切りでない文字なら）入力先は動いていないので、パスワード欄の答えは保護に使う（R8）。
        if (LatestBefore(t) is { Kind: FieldKind.Password } a && a.Time >= segStart && a.Foreground == k.Foreground) return true;
        if (_observations.Exists(o => !o.IsEvent && o.Kind == FieldKind.Password && o.Time < t && o.End >= t
            && o.Time >= segStart && o.Foreground == k.Foreground && !_boundaries.Exists(b => b >= t && b <= o.End))) return true;
        // (b) 押した後、Q の開始（場合 B は区間の終わり）までにパスワード欄へのフォーカス変更イベントが来た。
        if (_observations.Any(o => o.IsEvent && o.Kind == FieldKind.Password && o.Time >= t && o.Time <= limit)) return true;

        if (!caseB)
        {
            // 場合 A: キーの後に始めた照会で判定する。
            if (q is { Success: true } ok) return ok.Kind == FieldKind.Password;
            return IsConfirmedPassword(k, segStart, null);
        }

        // 場合 B: Q を始める前に区間が終わった。区間の最新の観測で判定する。押す前の観測は (a) と同じく
        // その時点で最新のものだけが効き、押す前に来た古い Password のイベントだけでは隠さない（R3）。
        var end = segEnd!.Value;
        // 監視を始めた区間は区切りのキーで始まらない（待つべきフォーカスの移動が無い）ので、開始時の照会から区間の照会に数える。
        var low = segStart == _startTime ? segStart : segStart + _settle;
        Observation? latest = null;
        var hasQuery = false;
        foreach (var o in _observations)
        {
            var inSegment = o.IsEvent ? o.Time >= segStart && o.Time < end : o.Time >= low && o.Time < end;
            if (!inSegment) continue;
            // 区間の中で始めたが区切りの後に戻った照会は、区切りの後の欄を答えたかもしれないので使わない。
            if (!o.IsEvent && o.End >= end) continue;
            if (!o.IsEvent) hasQuery = true;
            if (latest is null || o.Time >= latest.Value.Time) latest = o;
        }
        if (hasQuery) return latest!.Value.Kind == FieldKind.Password;
        // 区間の照会を一度も始めないまま区間が終わった: 入力先を特定できないので捨てる（D1、§8 の 2。R1）。
        // 区間の長さによらない（待ち時間より長い区間でも、照会の開始が区切りの後になれば同じ）。
        // 区切りより前に始めた照会が戻らないまま時間切れになった場合は、照会が止まっていて区間を調べられなかったので、
        // 失敗と同じに扱う（利用者の決定「調べられないときは表示する」）。
        // 区切りをまたいだ照会だけがあった場合は、区間の入力先を特定できる照会が無かったので、照会を始めなかった場合と同じく捨てる
        // （照会が失敗したのではなく、欄の素早い移動で答えが区間のものか分からない。§8 の 2）。
        var tried = _failedQueries.Any(f => f >= low && f < end) || HasUnreportedQuery(long.MinValue, end);
        if (!tried) return true;
        // 区間の照会はすべて失敗・時間切れ: パスワード欄と確認済みのときだけ隠す（§8 の 4）。
        return IsConfirmedPassword(k, segStart, end);
    }

    /// <summary>照会に失敗したとき: 同じ区間・同じ前面の窓でパスワード欄と確認済みなら隠す（詳細設計 §2.2、D3）。</summary>
    private bool IsConfirmedPassword(Pending k, long segStart, long? before)
    {
        Observation? latest = null;
        foreach (var o in _observations)
        {
            if (before is { } b && o.Time >= b) break;
            if (before is { } b2 && o.End >= b2) continue; // 区切りの後に戻った照会は使わない
            latest = o;
        }
        return latest is { Kind: FieldKind.Password } l && l.Time >= segStart && l.Foreground == k.Foreground;
    }

    private (long Start, long End, bool Success, FieldKind Kind)? FirstQueryAtOrAfter(long time)
    {
        (long Start, long End, bool Success, FieldKind Kind)? best = null;
        foreach (var o in _observations)
        {
            if (o.IsEvent || o.Time < time) continue;
            best = (o.Time, o.End, true, o.Kind);
            break;
        }
        foreach (var f in _failedQueries)
        {
            if (f < time) continue;
            if (best is null || f < best.Value.Start) best = (f, f, false, FieldKind.Normal);
            break;
        }
        return best;
    }

    /// <summary>押した時点の最新の観測。押した後に戻った照会は、押した後の入力先を答えたかもしれないので使わない。</summary>
    private Observation? LatestBefore(long time)
    {
        Observation? latest = null;
        foreach (var o in _observations)
        {
            if (o.Time >= time) break;
            if (o.End >= time) continue;
            latest = o;
        }
        return latest;
    }

    private void NoteQueryDone(long start)
    {
        if (start > _lastQueryStart) _lastQueryStart = start;
        // 結果は始めた順に届くので、これ以前に始めた照会はすべて済んでいる。
        _unreportedStarts.RemoveAll(s => s <= start);
        if (_initialRequest is { } i && start >= i) _initialRequest = null;
    }

    /// <summary>判定に使わなくなった区切り・観測を捨てる（最新の観測は残す）。</summary>
    private void Prune()
    {
        long threshold;
        if (_queue.Count > 0)
        {
            var head = _queue[0].Time;
            threshold = _startTime;
            foreach (var b in _boundaries)
            {
                if (b < head) threshold = b;
                else break;
            }
        }
        else threshold = _boundaries.Count > 0 ? _boundaries[^1] : _startTime;

        _boundaries.RemoveAll(b => b < threshold);
        var keepFrom = _observations.FindIndex(o => o.Time >= threshold);
        if (keepFrom < 0) keepFrom = _observations.Count;
        if (keepFrom > 1) _observations.RemoveRange(0, keepFrom - 1); // 区間より前の最新の 1 件は残す
        _failedQueries.RemoveAll(f => f < threshold);
    }

    private void ClearState()
    {
        _queue.Clear();
        _boundaries.Clear();
        _observations.Clear();
        _failedQueries.Clear();
        _lastQueryStart = long.MinValue;
        _unreportedStarts.Clear();
        _initialRequest = null;
        _lastBoundary = null;
        _passwordObserved = false;
        TimedOutCount = 0;
    }

    private static bool IsPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (char.IsControl(c)) return false;
        }
        return true;
    }
}
