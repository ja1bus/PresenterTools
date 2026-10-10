namespace PresenterTools.Services;

/// <summary>
/// 多重起動の防止（SPEC §32 D-15）。2 つ目の起動は既存プロセスへ「操作パネル表示」を通知して終了する。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PresenterTools.SingleInstance";
    private const string EventName = @"Local\PresenterTools.ShowControlPanel";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// 最初のインスタンスなら取得して返す。既に起動済みなら既存側へ通知して null。
    /// <paramref name="waitForPrevious"/> が正なら、既存側が終了して解放するまでその時間だけ待つ（表示言語の変更での再起動用。SPEC §34）。
    /// </summary>
    public static SingleInstance? TryAcquire(TimeSpan waitForPrevious = default)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (createdNew || (waitForPrevious > TimeSpan.Zero && WaitForRelease(mutex, waitForPrevious)))
            return new SingleInstance(mutex, showEvent);

        showEvent.Set();
        showEvent.Dispose();
        mutex.Dispose();
        return null;
    }

    private static bool WaitForRelease(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // 前のプロセスが解放せずに終わった。所有権はこちらに移っている。
        }
    }

    /// <summary>2 つ目の起動からの通知を受けたときの処理（スレッドプールから呼ばれる）。</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
