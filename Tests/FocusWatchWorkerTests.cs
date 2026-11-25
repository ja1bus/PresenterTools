using System.Diagnostics;
using PresenterTools.Core.KeyDisplay;

namespace PresenterTools.Tests;

/// <summary>V1.6.0: 照会の専用スレッド（詳細設計 §2.5、試験表 T-01 (13)〜(15)）。偽の照会で、止まる・例外を出す場合を確かめる。</summary>
public sealed class FocusWatchWorkerTests
{
    private sealed class FakeProbe : IFocusedFieldProbe
    {
        public readonly ManualResetEventSlim Release = new(true);
        public readonly ManualResetEventSlim UnsubscribeGate = new(true);   // Reset で解除が戻らない
        public int Queries, Subscribes, Unsubscribes, ActiveSubscriptions, MaxActiveSubscriptions, Running, MaxRunning;
        public volatile bool ThrowOnQuery, ThrowOnSubscribe, ThrowOnUnsubscribe;
        public FieldKind Kind = FieldKind.Normal;
        public readonly List<Action<FieldProbeResult, long>> Handlers = [];   // 有効な購読（解除に失敗したものは残る）
        public Func<long>? Clock;

        public FieldProbeResult Query()
        {
            lock (Calls) Calls.Add("query");
            var running = Interlocked.Increment(ref Running);
            InterlockedMax(ref MaxRunning, running);
            Interlocked.Increment(ref Queries);
            try
            {
                Release.Wait(TimeSpan.FromSeconds(10));
                if (ThrowOnQuery) throw new InvalidOperationException("query");
                return new FieldProbeResult(Kind, new IntPtr(1));
            }
            finally { Interlocked.Decrement(ref Running); }
        }

        public int SubscribeDelayMs;
        public readonly List<string> Calls = [];

        public void Subscribe(Func<long> clock, Action<FieldProbeResult, long> handler)
        {
            lock (Calls) Calls.Add("subscribe");
            if (SubscribeDelayMs > 0) Thread.Sleep(SubscribeDelayMs);
            Interlocked.Increment(ref Subscribes);
            if (ThrowOnSubscribe) throw new InvalidOperationException("subscribe");
            Clock = clock;
            lock (Handlers) Handlers.Add(handler);
            InterlockedMax(ref MaxActiveSubscriptions, Interlocked.Increment(ref ActiveSubscriptions));
        }

        public void Unsubscribe()
        {
            Interlocked.Increment(ref Unsubscribes);
            UnsubscribeGate.Wait(TimeSpan.FromSeconds(10));
            if (ThrowOnUnsubscribe) throw new InvalidOperationException("unsubscribe"); // 古い購読は残る
            lock (Handlers) Handlers.Clear();
            if (ActiveSubscriptions > 0) Interlocked.Decrement(ref ActiveSubscriptions);
        }

        /// <summary>フォーカス変更の通知を、受けた時刻を読んでから、プロパティの取得に <paramref name="delayMs"/> かけて渡す。</summary>
        public void Fire(FieldKind kind, int delayMs)
        {
            var received = Clock!();
            Thread.Sleep(delayMs);
            Action<FieldProbeResult, long>[] handlers;
            lock (Handlers) handlers = [.. Handlers];
            foreach (var h in handlers) h(new FieldProbeResult(kind, new IntPtr(1)), received);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    private static FocusWatchWorker NewWorker(FakeProbe probe, List<FocusNotice> notices, List<string>? logs = null, TimeSpan? retryDelay = null) =>
        new(probe, Stopwatch.GetTimestamp, ticks => TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency),
            n => { lock (notices) notices.Add(n); },
            k => { if (logs is not null) lock (logs) logs.Add(k); },
            retryDelay);

    private static void WaitUntil(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "timed out");

    [Fact]
    public void Requests_while_a_query_hangs_are_merged_into_one()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        probe.Release.Reset();
        worker.Start(1);
        worker.RequestQuery(1, Stopwatch.GetTimestamp());
        WaitUntil(() => probe.Queries == 1);
        for (var i = 0; i < 50; i++) worker.RequestQuery(1, Stopwatch.GetTimestamp());
        Thread.Sleep(50);
        Assert.Equal(1, probe.Queries);
        Assert.Equal(2, worker.ThreadCount); // 照会用と購読用の 2 本から増えない

        probe.Release.Set();
        WaitUntil(() => probe.Queries == 2);
        WaitUntil(() => { lock (notices) return notices.Count(n => n.Kind == FocusNoticeKind.QueryResult) == 2; });
        Thread.Sleep(50);
        Assert.Equal(2, probe.Queries);
        Assert.Equal(1, probe.MaxRunning);
    }

    [Fact]
    public void Restarting_while_hung_keeps_one_subscription_and_answers_for_the_latest_run()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        worker.Start(1);
        WaitUntil(() => probe.ActiveSubscriptions == 1);
        probe.Release.Reset();
        worker.RequestQuery(1, Stopwatch.GetTimestamp());
        WaitUntil(() => probe.Queries == 1);

        for (var gen = 2; gen <= 21; gen++)
        {
            if (gen % 2 == 0) worker.Stop();
            else worker.Start(gen);
        }
        worker.Start(22);
        worker.RequestQuery(22, Stopwatch.GetTimestamp());
        Assert.Equal(2, worker.ThreadCount); // 照会用と購読用の 2 本から増えない

        probe.Release.Set();
        WaitUntil(() => { lock (notices) return notices.Any(n => n.Generation == 22 && n.Kind == FocusNoticeKind.QueryResult); });
        WaitUntil(() => !worker.IsBusy);
        Assert.Equal(1, probe.ActiveSubscriptions);
        Assert.Equal(1, probe.MaxActiveSubscriptions);
        lock (notices)
        {
            // 止まっていた照会の結果は旧い回の番号で届く（受け手が捨てる）
            Assert.Contains(notices, n => n.Generation == 1 && n.Kind == FocusNoticeKind.QueryResult);
            Assert.DoesNotContain(notices, n => n.Generation is > 1 and < 22);
        }
    }

    [Fact]
    public void Stop_unsubscribes_and_ignores_requests_of_old_runs()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        worker.Start(1);
        WaitUntil(() => probe.ActiveSubscriptions == 1);
        worker.Stop();
        WaitUntil(() => probe.ActiveSubscriptions == 0);
        worker.RequestQuery(1, Stopwatch.GetTimestamp());
        Thread.Sleep(50);
        Assert.Equal(0, probe.Queries);
    }

    [Fact]
    public void Failures_are_reported_and_logged_once_per_kind()
    {
        var probe = new FakeProbe { ThrowOnQuery = true, ThrowOnSubscribe = true };
        var notices = new List<FocusNotice>();
        var logs = new List<string>();
        using var worker = NewWorker(probe, notices, logs);
        worker.Start(1);
        for (var i = 0; i < 3; i++)
        {
            worker.RequestQuery(1, Stopwatch.GetTimestamp());
            var expected = i + 1;
            WaitUntil(() => { lock (notices) return notices.Count(n => n.Kind == FocusNoticeKind.QueryFailed) == expected; });
        }
        worker.Stop();
        worker.Start(2); // 購読に再び失敗しても記録は増えない
        WaitUntil(() => probe.Subscribes == 2);
        WaitUntil(() => !worker.IsBusy);
        lock (logs)
        {
            Assert.Equal(1, logs.Count(l => l == "query: InvalidOperationException"));
            Assert.Equal(1, logs.Count(l => l == "subscribe: InvalidOperationException"));
        }
    }

    /// <summary>コードレビュー R6: 解除に失敗している間は追加購読せず照会で続け、回復後は最新の回だけが購読される。</summary>
    [Fact]
    public void A_failed_unsubscribe_does_not_add_subscriptions()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        var logs = new List<string>();
        using var worker = NewWorker(probe, notices, logs, TimeSpan.FromMilliseconds(100));
        worker.Start(1);
        WaitUntil(() => probe.ActiveSubscriptions == 1);
        probe.ThrowOnUnsubscribe = true;

        var gen = 1;
        for (var i = 0; i < 10; i++)
        {
            worker.Stop();
            worker.Start(++gen);
            worker.RequestQuery(gen, Stopwatch.GetTimestamp());
            var expected = gen;
            WaitUntil(() => { lock (notices) return notices.Any(n => n.Generation == expected && n.Kind == FocusNoticeKind.QueryResult); });
        }
        Assert.Equal(1, probe.MaxActiveSubscriptions);
        Assert.Equal(1, worker.SubscribedGeneration);  // 古い購読が残っているものとして扱う

        probe.ThrowOnUnsubscribe = false;
        WaitUntil(() => worker.SubscribedGeneration == gen);
        Assert.Equal(1, probe.ActiveSubscriptions);
        Assert.Equal(1, probe.MaxActiveSubscriptions);
        probe.Fire(FieldKind.Password, 0);
        lock (notices)
        {
            var events = notices.Where(n => n.Kind == FocusNoticeKind.Event).ToList();
            Assert.Equal(gen, Assert.Single(events).Generation);
        }
        lock (logs) Assert.Equal(1, logs.Count(l => l == "unsubscribe: InvalidOperationException"));
    }

    /// <summary>
    /// コードレビュー R5: イベントの時刻は通知を受けた時刻（プロパティの取得の前）。古い通常欄のイベントの取得が遅れ、
    /// その間に始めた照会がパスワード欄を返しても、時刻の新旧は逆転しない。
    /// </summary>
    [Fact]
    public void An_event_keeps_the_time_it_was_received_even_if_reading_properties_is_slow()
    {
        var probe = new FakeProbe { Kind = FieldKind.Password };
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        worker.Start(1);
        WaitUntil(() => probe.ActiveSubscriptions == 1);

        var before = Stopwatch.GetTimestamp();
        var slowEvent = Task.Run(() => probe.Fire(FieldKind.Normal, 200)); // 通常欄へのフォーカス変更（取得に 200 ms）
        WaitUntil(() => probe.Clock is not null && Stopwatch.GetTimestamp() - before > Stopwatch.Frequency / 50);
        worker.RequestQuery(1, Stopwatch.GetTimestamp());                  // その後に始めた照会はパスワード欄
        WaitUntil(() => { lock (notices) return notices.Any(n => n.Kind == FocusNoticeKind.QueryResult); });
        slowEvent.Wait(TimeSpan.FromSeconds(5));

        FocusNotice ev, query;
        lock (notices)
        {
            ev = notices.Single(n => n.Kind == FocusNoticeKind.Event);
            query = notices.Single(n => n.Kind == FocusNoticeKind.QueryResult);
            Assert.True(notices.IndexOf(ev) > notices.IndexOf(query)); // 届いた順はイベントが後
        }
        Assert.True(ev.Time < query.Time);                                 // 時刻はイベントが前

        // 届いた順に判定へ渡しても、最新はパスワード欄の照会のまま。
        var gate = new PasswordInputGate(1, Stopwatch.Frequency);
        var g = gate.Start(before - 1);
        gate.OnObservation(g, query.Field, query.Time, query.Foreground, isEvent: false);
        Assert.True(gate.ConsumePasswordObserved());
        gate.OnObservation(g, ev.Field, ev.Time, ev.Foreground, isEvent: true);
        var keyTime = Math.Max(ev.Time, query.Time) + 1;                  // 両方の観測より後に押したキー
        gate.Enqueue(new Core.Input.KeyInput('A', true, Core.Input.KeyModifiers.None, "a"), keyTime, new IntPtr(1));
        Assert.True(Assert.Single(gate.TakeResolved(keyTime + Stopwatch.Frequency * 2)).Hidden);
    }

    [Fact]
    public void Last_query_start_is_known_before_the_result_arrives()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        probe.Release.Reset();
        worker.Start(1);
        Assert.Null(worker.LastQueryStart(1));
        var at = Stopwatch.GetTimestamp();
        worker.RequestQuery(1, at);
        WaitUntil(() => probe.Queries == 1);
        var started = worker.LastQueryStart(1);
        Assert.NotNull(started);
        Assert.True(started >= at);
        Assert.Null(worker.LastQueryStart(2));
        lock (notices) Assert.Empty(notices);
        probe.Release.Set();
        WaitUntil(() => { lock (notices) return notices.Count == 1; });
        lock (notices) Assert.Equal(started, notices[0].Time);
    }

    /// <summary>コードレビュー R9: 照会の終了時刻は、試験用の記録を書く前に読む（記録の時間を終了時刻に含めない）。</summary>
    [Fact]
    public void The_query_end_time_does_not_include_writing_the_trace()
    {
        long now = 100;
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = new FocusWatchWorker(probe, () => Interlocked.Read(ref now), ticks => TimeSpan.FromMilliseconds(ticks),
            n => { lock (notices) notices.Add(n); })
        {
            Trace = what => { if (what == "query-end") Interlocked.Exchange(ref now, 110); }, // 記録の書き込み中に時計が進む
        };
        worker.Start(1, 100);
        WaitUntil(() => { lock (notices) return notices.Any(n => n.Kind == FocusNoticeKind.QueryResult); });
        lock (notices)
        {
            var n = notices.Single(x => x.Kind == FocusNoticeKind.QueryResult);
            Assert.Equal(100, n.Time);
            Assert.Equal(100, n.End);
        }
    }

    /// <summary>工程 G（T-05 の WPF で検出）: 開始時の照会は、時間のかかる購読の登録より先に行う。</summary>
    [Fact]
    public void The_initial_query_runs_before_a_slow_subscription()
    {
        var probe = new FakeProbe { SubscribeDelayMs = 500 };
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        var start = Stopwatch.GetTimestamp();
        worker.Start(1, start);
        WaitUntil(() => { lock (notices) return notices.Any(n => n.Kind == FocusNoticeKind.QueryResult); });
        Assert.True(Stopwatch.GetTimestamp() - start < Stopwatch.Frequency * 3 / 10); // 購読の 500 ms を待たない（照会と購読は別のスレッド）
        lock (notices) Assert.True(notices[0].End >= notices[0].Time);
    }

    /// <summary>工程 G（T-07 で検出）: 購読の解除が戻らない間も、次の回の照会は待たずに行う。</summary>
    [Fact]
    public void A_hanging_unsubscribe_does_not_block_queries()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        worker.Start(1);
        WaitUntil(() => probe.ActiveSubscriptions == 1);
        probe.UnsubscribeGate.Reset();                 // 解除が戻らない
        worker.Stop();
        WaitUntil(() => probe.Unsubscribes == 1);
        var start = Stopwatch.GetTimestamp();
        worker.Start(2, start);
        WaitUntil(() => { lock (notices) return notices.Any(n => n.Generation == 2 && n.Kind == FocusNoticeKind.QueryResult); });
        Assert.True(Stopwatch.GetTimestamp() - start < Stopwatch.Frequency / 2);
        Assert.Equal(1, probe.MaxActiveSubscriptions);  // 解除が済むまで新しく購読しない
        probe.UnsubscribeGate.Set();
        WaitUntil(() => worker.SubscribedGeneration == 2);
        Assert.Equal(1, probe.ActiveSubscriptions);
        Assert.Equal(1, probe.MaxActiveSubscriptions);
    }

    [Fact]
    public void A_query_waits_until_the_requested_time()
    {
        var probe = new FakeProbe();
        var notices = new List<FocusNotice>();
        using var worker = NewWorker(probe, notices);
        worker.Start(1);
        var at = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10; // 100 ms 後
        worker.RequestQuery(1, at);
        WaitUntil(() => { lock (notices) return notices.Count > 0; });
        lock (notices) Assert.True(notices[0].Time >= at);
    }
}
