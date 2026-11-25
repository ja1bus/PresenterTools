namespace PresenterTools.Core.KeyDisplay;

/// <summary>照会・フォーカス変更イベントの結果。前面の窓は観測した時点のもの。</summary>
public readonly record struct FieldProbeResult(FieldKind Kind, IntPtr Foreground);

/// <summary>入力先の欄を調べる手段（アプリでは UI Automation）。失敗は例外で知らせる。</summary>
public interface IFocusedFieldProbe
{
    /// <summary>現在の入力先を調べる。</summary>
    FieldProbeResult Query();

    /// <summary>
    /// フォーカス変更イベントを購読する。<paramref name="handler"/> は任意のスレッドから呼ばれ、第 2 引数は通知を受けた時刻。
    /// 受けた時刻と前面の窓は、通知の入口で欄の種類などのプロパティを取得する前に読む（取得の時間だけ時刻がずれないように。
    /// コードレビュー R5）。時刻は <paramref name="clock"/> で読む。
    /// </summary>
    void Subscribe(Func<long> clock, Action<FieldProbeResult, long> handler);

    /// <summary>購読を解除する。失敗したら例外を出し、購読は残っているものとして扱う。</summary>
    void Unsubscribe();
}

public enum FocusNoticeKind
{
    Event,
    QueryResult,
    QueryFailed,
}

/// <summary>
/// 監視の結果。<see cref="Generation"/> は依頼した監視の回、<see cref="Time"/> はイベントを受けた時刻か照会の開始時刻、
/// <see cref="End"/> は照会の結果が戻った時刻（イベントは <see cref="Time"/> と同じ）。
/// </summary>
public readonly record struct FocusNotice(int Generation, FocusNoticeKind Kind, FieldKind Field, long Time, IntPtr Foreground, long End);

/// <summary>
/// 入力先の照会とフォーカス変更イベントの購読を、アプリで 2 本の専用スレッド（照会用と購読用）で行う（V1.6.0。詳細設計 §2.5）。
/// 購読の解除が数秒戻らないことがあるので、照会は購読を待たない。照会の依頼は 1 件にまとめ、購読は「したい／したくない」の
/// 希望の状態だけを持つ。照会や解除が戻らずスレッドが止まっても、スレッド・依頼・購読は増えない。結果は <c>post</c> へ渡す
/// （呼び出し側で UI スレッドへ移す）。照会は順に行い、結果は始めた順に渡す。
/// </summary>
public sealed class FocusWatchWorker : IDisposable
{
    private readonly IFocusedFieldProbe _probe;
    private readonly Func<long> _clock;
    private readonly Func<long, TimeSpan> _toTimeSpan;
    private readonly Action<FocusNotice> _post;
    private readonly Action<string>? _logOnce;
    private readonly TimeSpan _retryDelay;
    private readonly object _lock = new();
    private readonly HashSet<string> _logged = [];
    private Thread? _thread;
    private Thread? _subscriptionThread;

    // _lock で守る
    private int _activeGeneration;          // 0 は停止中
    private int _subscribedGeneration;      // 0 は購読なし。解除に失敗している間は古い回のまま
    private long _retrySubscriptionAt;      // 解除に失敗した後、次に試す時刻（Environment.TickCount64）
    private long? _requestAt;
    private int _lastQueryGeneration;
    private long _lastQueryStart;
    private bool _disposed;
    private bool _queryBusy;
    private bool _subscriptionBusy;

    /// <param name="clock">時刻（<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> など）。</param>
    /// <param name="toTimeSpan">時刻の差を待ち時間へ変換する。</param>
    /// <param name="logOnce">失敗の種類名を記録する（種類ごとに 1 回だけ呼ぶ）。</param>
    /// <param name="retryDelay">購読の解除に失敗したとき、次に解除を試すまでの間隔（既定 1 秒）。</param>
    public FocusWatchWorker(IFocusedFieldProbe probe, Func<long> clock, Func<long, TimeSpan> toTimeSpan, Action<FocusNotice> post,
        Action<string>? logOnce = null, TimeSpan? retryDelay = null)
    {
        _probe = probe;
        _clock = clock;
        _toTimeSpan = toTimeSpan;
        _post = post;
        _logOnce = logOnce;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>試験用の記録（照会・購読の開始と終了）。アプリでは Debug ビルドの試験用の記録へ渡す。</summary>
    public Action<string>? Trace { get; set; }

    private void T(string what)
    {
        try { Trace?.Invoke(what); } catch { /* 記録の失敗で監視を止めない */ }
    }

    /// <summary>専用スレッドの数（試験用。0 か 2）。</summary>
    public int ThreadCount => (_thread is null ? 0 : 1) + (_subscriptionThread is null ? 0 : 1);

    /// <summary>照会・購読の登録や解除で専用スレッドが処理中か（試験用）。</summary>
    public bool IsBusy
    {
        get { lock (_lock) return _queryBusy || _subscriptionBusy; }
    }

    /// <summary>購読している回の番号（試験用。0 は購読なし）。</summary>
    public int SubscribedGeneration
    {
        get { lock (_lock) return _subscribedGeneration; }
    }

    /// <summary>
    /// この回で最後に始めた照会の開始時刻（結果が届く前を含む）。この回でまだ照会していなければ null。
    /// 区間の照会が実行中かを判定に知らせるために使う（<see cref="PasswordInputGate.NoteQueryStarted"/>）。
    /// </summary>
    public long? LastQueryStart(int generation)
    {
        lock (_lock) return generation != 0 && generation == _lastQueryGeneration ? _lastQueryStart : null;
    }

    /// <summary>
    /// 監視を始める（回の番号を付けて購読する）。<paramref name="queryAt"/> を渡すと、開始時の照会を同じロックの中で依頼する。
    /// </summary>
    public void Start(int generation, long? queryAt = null)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_lock)
        {
            if (_disposed) return;
            _activeGeneration = generation;
            _requestAt = queryAt;
            if (_thread is null) CreateThreads();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>監視を止める（未処理の依頼を捨て、購読を解除する）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _activeGeneration = 0;
            _requestAt = null;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>この時刻以降に 1 回照会する。未処理の依頼があれば早い方の時刻にまとめる。現在の回でなければ無視する。</summary>
    public void RequestQuery(int generation, long notBefore)
    {
        lock (_lock)
        {
            if (_disposed || generation != _activeGeneration || generation == 0) return;
            if (_requestAt is null || notBefore < _requestAt) _requestAt = notBefore;
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        Thread? query, subscription;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _activeGeneration = 0;
            _requestAt = null;
            query = _thread;
            subscription = _subscriptionThread;
            Monitor.PulseAll(_lock);
        }
        // 照会・購読の解除が戻らない場合も、合わせて 1 秒で打ち切る（背景スレッドなのでプロセスの終了を妨げない。
        // 解除は数秒戻らないことがあり、終了を 2 秒以内にするため。工程 G、T-07 で検出）。
        var deadline = Environment.TickCount64 + 1000;
        query?.Join(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)));
        subscription?.Join(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)));
    }

    private void CreateThreads()
    {
        _thread = Spawn(RunQueries, "PresenterTools.FocusQuery");
        _subscriptionThread = Spawn(RunSubscriptions, "PresenterTools.FocusSubscribe");
    }

    private static Thread Spawn(ThreadStart body, string name)
    {
        var thread = new Thread(body) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread;
    }

    /// <summary>照会のスレッド。購読の登録・解除を待たない（解除が数秒戻らないことがある。工程 G、T-07 で検出）。</summary>
    private void RunQueries()
    {
        while (true)
        {
            int query;
            long queryStart;
            lock (_lock)
            {
                _queryBusy = false;
                while (true)
                {
                    if (_disposed) return;
                    var want = _activeGeneration;
                    if (want != 0 && _requestAt is { } at)
                    {
                        var untilQuery = at - _clock();
                        if (untilQuery <= 0)
                        {
                            _requestAt = null;
                            query = want;
                            // 開始時刻は、結果より先に判定側から読めるようにここで記録する（R1・R4）。
                            queryStart = _clock();
                            _lastQueryGeneration = want;
                            _lastQueryStart = queryStart;
                            break;
                        }
                        Monitor.Wait(_lock, ClampWait(_toTimeSpan(untilQuery)));
                        continue;
                    }
                    Monitor.Wait(_lock);
                }
                _queryBusy = true;
            }
            RunQuery(query, queryStart);
        }
    }

    /// <summary>購読のスレッド。希望の状態に合わせて登録・解除する。1 本で順に行うので、購読は同時に 1 件まで。</summary>
    private void RunSubscriptions()
    {
        while (true)
        {
            int unsubscribe, subscribe;
            lock (_lock)
            {
                _subscriptionBusy = false;
                while (true)
                {
                    var want = _disposed ? 0 : _activeGeneration;
                    if (_subscribedGeneration != want)
                    {
                        // 解除に失敗した後は間を空けて試す（その間も照会は続ける。R6）。終了時はすぐ試す。
                        var retryIn = _retrySubscriptionAt - Environment.TickCount64;
                        if (_disposed || retryIn <= 0)
                        {
                            unsubscribe = _subscribedGeneration;
                            subscribe = want;
                            break;
                        }
                        Monitor.Wait(_lock, ClampWait(TimeSpan.FromMilliseconds(retryIn)));
                        continue;
                    }
                    if (_disposed) return;
                    Monitor.Wait(_lock);
                }
                _subscriptionBusy = true;
            }
            ChangeSubscription(unsubscribe, subscribe);
        }
    }

    private void ChangeSubscription(int from, int to)
    {
        if (from != 0)
        {
            T("unsubscribe-begin\t" + from);
            try { _probe.Unsubscribe(); T("unsubscribe-end"); }
            catch (Exception ex)
            {
                T("unsubscribe-failed\t" + ex.GetType().Name);
                // 解除できたか分からない間は、古い購読が残っているものとして新しく購読しない（購読が増えないように。R6）。
                // 古い回の通知は受け手が回の番号で捨てる。照会だけで判定を続け、間を空けて解除を試し直す。
                LogOnce("unsubscribe", ex);
                lock (_lock)
                {
                    if (_disposed) _subscribedGeneration = 0; // 終了時は諦める
                    else _retrySubscriptionAt = Environment.TickCount64 + (long)_retryDelay.TotalMilliseconds;
                }
                return;
            }
        }
        var subscribed = 0;
        if (to != 0)
        {
            try
            {
                var generation = to;
                T("subscribe-begin\t" + to);
                _probe.Subscribe(_clock, (r, time) => _post(new FocusNotice(generation, FocusNoticeKind.Event, r.Kind, time, r.Foreground, time)));
                subscribed = to;
                T("subscribe-end");
            }
            catch (Exception ex)
            {
                T("subscribe-failed\t" + ex.GetType().Name);
                // 購読に失敗したら照会だけで動く（詳細設計 §2.5）。同じ回では再試行しない。
                LogOnce("subscribe", ex);
                subscribed = to;
            }
        }
        lock (_lock) _subscribedGeneration = subscribed;
    }

    private void RunQuery(int generation, long start)
    {
        FocusNotice notice;
        try
        {
            T("query-begin\t" + generation);
            var r = _probe.Query();
            // 結果は開始から戻るまでのどの時点の入力先でもあり得るので、戻った時刻も渡す（工程 G）。時刻は記録より先に読む
            // （試験用の記録の書き込み時間を含めると、区切りの前に戻った照会まで区切りをまたいだとみなす。コードレビュー R9）。
            var end = _clock();
            T("query-end");
            notice = new FocusNotice(generation, FocusNoticeKind.QueryResult, r.Kind, start, r.Foreground, end);
        }
        catch (Exception ex)
        {
            var end = _clock();
            LogOnce("query", ex);
            notice = new FocusNotice(generation, FocusNoticeKind.QueryFailed, FieldKind.Normal, start, IntPtr.Zero, end);
        }
        _post(notice);
    }

    private void LogOnce(string operation, Exception ex)
    {
        var kind = operation + ": " + ex.GetType().Name;
        lock (_logged)
        {
            if (!_logged.Add(kind)) return;
        }
        try { _logOnce?.Invoke(kind); }
        catch { /* 記録の失敗で監視を止めない */ }
    }

    private static TimeSpan ClampWait(TimeSpan wait) =>
        wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1)
        : wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
}
