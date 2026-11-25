using PresenterTools.Core.Input;
using PresenterTools.Core.KeyDisplay;

namespace PresenterTools.Tests;

/// <summary>
/// V1.6.0: パスワード欄での自動非表示の判定（詳細設計 §2、試験表 T-01 (1)〜(12)）。時刻は ms とみなした整数。
/// 待ち時間 120、時間切れ 300。
/// </summary>
public sealed class PasswordInputGateTests
{
    private const long Settle = 120, Timeout = 300;
    private static readonly IntPtr W = new(100), W2 = new(200);

    private static PasswordInputGate NewGate(out int gen, FieldKind initial = FieldKind.Normal)
    {
        var gate = new PasswordInputGate(Settle, Timeout);
        gen = gate.Start(0);
        gate.OnObservation(gen, initial, 0, W, isEvent: false);
        return gate;
    }

    private static KeyInput Ch(char c) => new(char.ToUpperInvariant(c), true, KeyModifiers.None, c.ToString());
    private static KeyInput Up(int vk) => new(vk, false, KeyModifiers.None);
    private static readonly KeyInput Tab = new(VirtualKeys.Tab, true, KeyModifiers.None, "\t");
    private static readonly KeyInput ShiftTab = new(VirtualKeys.Tab, true, KeyModifiers.Shift, "\t");

    private static string Visible(IEnumerable<GatedKey> keys) =>
        string.Concat(keys.Where(k => k.Input.IsKeyDown && !k.Hidden).Select(k => k.Input.VirtualKey == VirtualKeys.Tab ? "[Tab]" : k.Input.Text));

    private static string Hidden(IEnumerable<GatedKey> keys) =>
        string.Concat(keys.Where(k => k.Hidden).Select(k => k.Input.VirtualKey == VirtualKeys.Tab ? "[Tab]" : k.Input.Text));

    private static void Query(PasswordInputGate g, int gen, long start, FieldKind kind, IntPtr? fg = null) =>
        g.OnObservation(gen, kind, start, fg ?? W, isEvent: false);

    private static void Event(PasswordInputGate g, int gen, long time, FieldKind kind, IntPtr? fg = null) =>
        g.OnObservation(gen, kind, time, fg ?? W, isEvent: true);

    // ---- (1) 順番と保留 ----

    [Fact]
    public void Keys_come_out_in_order_and_stop_at_the_first_undecided()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        g.Enqueue(Ch('b'), 110, W);
        g.Enqueue(Up('A'), 115, W);
        Assert.Equal(220, g.NextQueryAt);
        Assert.Empty(g.TakeResolved(200));

        Query(g, gen, 220, FieldKind.Normal);
        var first = g.TakeResolved(225);
        Assert.Equal("a", Visible(first));
        Assert.Single(first); // b は未判定なので、後ろの離したイベントも止まる

        Query(g, gen, 230, FieldKind.Normal);
        var rest = g.TakeResolved(235);
        Assert.Equal("b", Visible(rest));
        Assert.Contains(rest, k => !k.Input.IsKeyDown);
        Assert.Equal(0, g.PendingCount);
    }

    [Fact]
    public void Key_up_alone_passes_without_a_query()
    {
        var g = NewGate(out _);
        g.Enqueue(Up('A'), 100, W);
        var r = g.TakeResolved(100);
        Assert.Single(r);
        Assert.False(r[0].Hidden);
        Assert.Null(g.NextQueryAt);
    }

    // ---- (2) 場合 A ----

    [Fact]
    public void A_shows_when_the_query_after_the_key_is_normal()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        Query(g, gen, 220, FieldKind.Normal);
        Assert.Equal("a", Visible(g.TakeResolved(230)));
    }

    [Fact]
    public void A_hides_when_the_latest_observation_in_the_segment_is_password()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 50, FieldKind.Password);
        g.Enqueue(Ch('a'), 100, W);
        Query(g, gen, 220, FieldKind.Normal); // (a) が先に当たる
        Assert.Equal("a", Hidden(g.TakeResolved(230)));
    }

    [Fact]
    public void A_hides_when_a_password_event_arrives_while_pending()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        Event(g, gen, 150, FieldKind.Password);
        Event(g, gen, 180, FieldKind.Normal);
        Query(g, gen, 220, FieldKind.Normal);
        Assert.Equal("a", Hidden(g.TakeResolved(230)));
    }

    [Fact]
    public void A_hides_when_the_query_says_password()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        Query(g, gen, 220, FieldKind.Password);
        Assert.Equal("a", Hidden(g.TakeResolved(230)));
    }

    [Fact]
    public void A_query_started_before_the_deadline_is_not_used_for_the_key()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        Query(g, gen, 150, FieldKind.Normal); // 期限（220）の前に始めた照会
        Assert.Empty(g.TakeResolved(160));
        Assert.Equal(220, g.NextQueryAt);
        Query(g, gen, 221, FieldKind.Password);
        Assert.Equal("a", Hidden(g.TakeResolved(230)));
    }

    [Fact]
    public void One_query_decides_every_key_whose_deadline_has_passed()
    {
        var g = NewGate(out var gen);
        foreach (var (c, t) in new[] { ('a', 100L), ('b', 130L), ('c', 160L) }) g.Enqueue(Ch(c), t, W);
        Query(g, gen, 300, FieldKind.Normal);
        Assert.Equal("abc", Visible(g.TakeResolved(310)));
    }

    // ---- (3) 場合 B ----

    [Fact]
    public void B_shows_the_last_key_before_a_boundary_when_the_segment_was_checked()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);              // ユーザー名欄へ
        Query(g, gen, 1120, FieldKind.Normal);
        g.TakeResolved(1130);
        g.Enqueue(Ch('1'), 2000, W);
        g.Enqueue(Tab, 2050, W);              // 別の通常欄へ（区切り）
        Event(g, gen, 2080, FieldKind.Normal);
        Query(g, gen, 2170, FieldKind.Normal);
        var r = g.TakeResolved(2180);
        Assert.Equal("1[Tab]", Visible(r)); // 通常欄で押した Tab も通常欄の区間に属する
    }

    [Fact]
    public void Keys_decided_after_entering_a_password_field_are_not_shown()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        Query(g, gen, 1120, FieldKind.Normal);
        g.TakeResolved(1130);
        g.Enqueue(Ch('1'), 2000, W);
        g.Enqueue(Tab, 2050, W);              // パスワード欄へ
        Event(g, gen, 2080, FieldKind.Password);
        Assert.True(g.ConsumePasswordObserved());
        Query(g, gen, 2170, FieldKind.Password);
        var r = g.TakeResolved(2180);
        Assert.Equal("", Visible(r));         // 入欄で消した後に、通常欄で押したキーが遅れて出ない
        Assert.Equal("1[Tab]", Hidden(r));

        // 別の窓の観測では隠さない
        g.Enqueue(Ch('x'), 2300, W2);
        Query(g, gen, 2420, FieldKind.Normal, W2);
        Assert.Equal("x", Visible(g.TakeResolved(2430)));
    }

    [Fact]
    public void B_hides_keys_of_a_segment_that_ended_before_it_could_be_checked()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('u'), 500, W);
        g.Enqueue(Tab, 1000, W);
        g.Enqueue(Ch('Q'), 1020, W);
        g.Enqueue(ShiftTab, 1040, W);
        g.Enqueue(Ch('a'), 1600, W);
        Query(g, gen, 620, FieldKind.Normal);
        Query(g, gen, 1160, FieldKind.Normal); // 照会は戻った後の欄（ユーザー名欄）を返す
        Query(g, gen, 1720, FieldKind.Normal);
        var r = g.TakeResolved(1730);
        Assert.Equal("u[Tab]a", Visible(r));
        Assert.Equal("Q[Tab]", Hidden(r)); // パスワードの文字と、戻る Shift+Tab
    }

    // ---- (3) コードレビュー R1: 区間の照会を始めないまま区間が終わる（区間の長さによらない） ----

    /// <summary>
    /// 通常欄の照会 990 → 1000 に Tab で入欄 → 1010 に文字 → 1000+length に Tab で退欄。イベントは届かない。
    /// queryAt: 照会を始めた時刻（null は区間の後の 1145 まで始まらない）。failed: その照会が失敗した。
    /// </summary>
    [Theory]
    [InlineData(119, null, false, true)]   // 区間が待ち時間より短い
    [InlineData(120, null, false, true)]   // ちょうど待ち時間
    [InlineData(121, null, false, true)]   // 待ち時間より長いが、照会の開始は区間の後
    [InlineData(140, null, false, true)]   // レビューの再現（照会の開始 1145）
    [InlineData(121, 1120, false, false)]  // 区間の照会（待ち時間ちょうど）が通常欄
    [InlineData(140, 1135, false, false)]  // 退欄の直前に始めた照会が通常欄
    [InlineData(140, 1135, true, false)]   // 区間の照会が失敗: 確認済みでないので表示（§8 の 4）
    public void R1_segment_closed_without_any_query_hides_its_keys(int length, int? queryAt, bool failed, bool expectHidden)
    {
        var g = new PasswordInputGate(Settle, Timeout);
        var gen = g.Start(0);
        Query(g, gen, 990, FieldKind.Normal);
        g.Enqueue(Tab, 1000, W);
        g.Enqueue(Ch('Q'), 1010, W);
        var exit = 1000L + length;
        if (queryAt is { } qa)
        {
            if (failed) g.OnQueryFailed(gen, qa);
            else Query(g, gen, qa, FieldKind.Normal);
        }
        g.Enqueue(ShiftTab, exit, W);
        Query(g, gen, Math.Max(exit, 1145) + 5, FieldKind.Normal); // 戻った通常欄
        var r = g.TakeResolved(1300);
        Assert.Equal(expectHidden, Hidden(r).Contains('Q'));
        Assert.Equal(expectHidden, !Visible(r).Contains('Q'));
    }

    [Fact]
    public void R1_failure_in_the_segment_still_hides_a_confirmed_password_field()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        Event(g, gen, 1005, FieldKind.Password);
        g.Enqueue(Ch('Q'), 1010, W);
        g.OnQueryFailed(gen, 1135);
        g.Enqueue(ShiftTab, 1140, W);
        // 入欄の Tab も、判定がパスワード欄の観測の後なので出ない（§2.2）。
        Assert.Equal("[Tab]Q[Tab]", Hidden(g.TakeResolved(1140)));
    }

    [Fact]
    public void R1_R4_waits_for_a_query_started_before_the_boundary()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        g.Enqueue(Ch('u'), 1010, W);
        g.NoteQueryStarted(1135);                 // 区間の照会を始めた（結果はまだ）
        g.Enqueue(Tab, 1140, W);
        // 1000 の Tab の区間（開始〜1000）は開始時の照会で済んでいるので出る。u は区切りより前に始めた照会の結果を待つ
        // （R7 の修正前は、1135 の照会が 1000 より前にもあるとみなして Tab まで待っていた）。
        Assert.Equal("[Tab]", Visible(g.TakeResolved(1140)));
        Assert.Equal(2, g.PendingCount);          // u と 1140 の Tab
        Query(g, gen, 1135, FieldKind.Normal);
        var r = g.TakeResolved(1150);
        Assert.Equal("u[Tab]", Visible(r));
        Assert.Equal(0, g.TimedOutCount);
    }

    [Fact]
    public void R1_a_hung_query_counts_as_a_failure_after_the_timeout()
    {
        var g = NewGate(out _);
        g.NoteQueryStarted(900);                  // 照会が戻らない（対象アプリの応答なし）。以後の照会も始まらない
        g.Enqueue(Tab, 1000, W);
        g.Enqueue(Ch('u'), 1010, W);
        g.Enqueue(Tab, 1140, W);
        Assert.Empty(g.TakeResolved(1419));
        Assert.Equal("[Tab]u", Visible(g.TakeResolved(1430))); // 調べられないときは表示する（区間の照会なしで捨てない）
        Assert.Equal("[Tab]", Visible(g.TakeResolved(1560)));
        Assert.Equal(3, g.TimedOutCount);
    }

    [Fact]
    public void The_first_segment_counts_the_query_made_at_start()
    {
        var g = NewGate(out _);                   // 開始時（0）の照会が通常欄
        g.Enqueue(Tab, 50, W);                    // ON にしてすぐ Tab
        Assert.Equal("[Tab]", Visible(g.TakeResolved(50)));
    }

    // ---- コードレビュー R3: 通常欄に戻った後の、以前のパスワード欄のイベント ----

    [Fact]
    public void R3_an_older_password_event_does_not_hide_keys_after_returning_by_click()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 200, FieldKind.Password);
        Event(g, gen, 600, FieldKind.Normal);     // クリックで通常欄へ（区切りではない）
        Query(g, gen, 800, FieldKind.Normal);
        g.Enqueue(Ch('a'), 1000, W);
        g.Enqueue(Tab, 1050, W);
        Query(g, gen, 1170, FieldKind.Normal);
        var r = g.TakeResolved(1180);
        Assert.Equal("a[Tab]", Visible(r));
        Assert.Equal("", Hidden(r));
    }

    [Fact]
    public void R3_keys_typed_in_the_password_field_and_the_tab_out_stay_hidden()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 200, FieldKind.Password);
        g.Enqueue(Ch('Q'), 1000, W);
        g.Enqueue(Tab, 1050, W);
        Query(g, gen, 1170, FieldKind.Normal);
        var r = g.TakeResolved(1180);
        Assert.Equal("Q[Tab]", Hidden(r));
        Assert.Equal("", Visible(r));
    }

    // ---- コードレビュー R4: 閉じた区間は次の区間の照会を待たない ----

    [Fact]
    public void R4_a_closed_segment_is_decided_when_the_boundary_arrives()
    {
        var g = NewGate(out var gen);
        Query(g, gen, 800, FieldKind.Normal);
        g.Enqueue(Ch('a'), 1000, W);
        Assert.Empty(g.TakeResolved(1000));
        g.Enqueue(Tab, 1050, W);
        var r = g.TakeResolved(1050);             // 次の照会の結果を渡す前
        Assert.Equal("a[Tab]", Visible(r));
        Assert.Equal(0, g.TimedOutCount);
        Assert.Null(g.NextTimeoutAt);
    }

    // ---- コードレビュー R7: 次の照会が始まっても、区切りをまたいだ照会だけの区間の文字は捨てる ----

    /// <summary>
    /// 1000 に Tab で入欄（回収済み）→ 1010 に文字 → 1130 に照会を開始 → 1140 に Tab で退欄（イベントは欠落）。
    /// 1130 の照会は退欄後の 1260 に Normal を返し、1261 に次の照会が始まる。nextNotedFirst: 次の照会の開始を、
    /// 先の照会の結果より先に判定側へ知らせる。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R7_a_segment_with_only_a_straddling_query_hides_its_keys_even_when_the_next_query_has_started(bool nextNotedFirst)
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        Assert.Equal("[Tab]", Visible(g.TakeResolved(1000)));
        g.Enqueue(Ch('Q'), 1010, W);
        g.NoteQueryStarted(1130);
        g.Enqueue(Tab, 1140, W);
        Assert.Empty(g.TakeResolved(1140));                   // 区切りより前に始めた照会の結果を待つ
        if (nextNotedFirst)
        {
            g.NoteQueryStarted(1261);
            Assert.Empty(g.TakeResolved(1261));               // 1130 の結果はまだ
        }
        g.OnObservation(gen, FieldKind.Normal, 1130, W, isEvent: false, end: 1260);
        g.NoteQueryStarted(1261);
        var r = g.TakeResolved(1262);
        Assert.Equal("Q[Tab]", Hidden(r));
        Assert.Equal("", Visible(r));
        Assert.Equal(0, g.TimedOutCount);
    }

    /// <summary>R7 の別の順: 1110 に文字、1120 に照会、1130 に退欄、1260 に結果、1261 に始めた次の照会は戻らない。</summary>
    [Fact]
    public void R7_the_key_is_dropped_when_the_straddling_result_arrives_without_waiting_for_the_next_query()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        g.TakeResolved(1000);
        g.Enqueue(Ch('Q'), 1110, W);
        g.NoteQueryStarted(1120);
        g.Enqueue(Tab, 1130, W);
        Assert.Empty(g.TakeResolved(1130));
        g.OnObservation(gen, FieldKind.Normal, 1120, W, isEvent: false, end: 1260);
        g.NoteQueryStarted(1261);
        var r = g.TakeResolved(1261);
        Assert.Equal("Q[Tab]", Hidden(r));
        Assert.Equal(0, g.TimedOutCount);
        Assert.Null(g.NextTimeoutAt);
    }

    /// <summary>R7: 区間の中で始めた照会が実際に戻らない場合は、従来どおり失敗と同じに扱う（R1。確認済みのときだけ隠す）。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R7_a_hung_query_in_the_segment_still_counts_as_a_failure(bool confirmedPassword)
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        g.TakeResolved(1000);
        if (confirmedPassword) Event(g, gen, 1005, FieldKind.Password);
        g.Enqueue(Ch('Q'), 1110, W);
        g.NoteQueryStarted(1120);                                // 戻らない
        g.Enqueue(Tab, 1130, W);
        Assert.Empty(g.TakeResolved(1529));
        var r = g.TakeResolved(1530);
        Assert.Equal(confirmedPassword, Hidden(r).Contains('Q'));
        Assert.Equal(!confirmedPassword, Visible(r).Contains('Q'));
        Assert.True(g.TimedOutCount >= 1);
    }

    // ---- コードレビュー R8: 押下に重なったパスワード欄の照会 ----

    /// <summary>
    /// パスワード欄にいる状態で 1000 に監視と初回照会を開始 → 1010 に文字 → 1040 に初回照会が Password →
    /// 1130 に照会 → 1200 にクリックで同じ窓の通常欄へ（Normal イベント）→ 1210 に照会が Normal。
    /// </summary>
    [Fact]
    public void R8_a_key_typed_while_the_initial_password_query_runs_stays_hidden_after_returning()
    {
        var g = new PasswordInputGate(Settle, Timeout);
        var gen = g.Start(1000);
        g.Enqueue(Ch('Q'), 1010, W);
        g.OnObservation(gen, FieldKind.Password, 1000, W, isEvent: false, end: 1040);
        Assert.True(g.ConsumePasswordObserved());
        Assert.Empty(g.TakeResolved(1040));                      // 押した後の照会を待つ
        g.NoteQueryStarted(1130);
        Event(g, gen, 1200, FieldKind.Normal);
        g.OnObservation(gen, FieldKind.Normal, 1130, W, isEvent: false, end: 1210);
        var r = g.TakeResolved(1210);
        Assert.Equal("Q", Hidden(r));
        Assert.Equal("", Visible(r));

        // その後に通常欄で押した文字は表示する
        g.Enqueue(Ch('a'), 1220, W);
        Query(g, gen, 1340, FieldKind.Normal);
        Assert.Equal("a", Visible(g.TakeResolved(1350)));
    }

    [Fact]
    public void R8_a_normal_query_straddling_a_boundary_does_not_override_the_previous_password()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 500, FieldKind.Password);
        g.Enqueue(Ch('Q'), 1010, W);
        g.Enqueue(Tab, 1020, W);                                 // パスワード欄から出る
        g.OnObservation(gen, FieldKind.Normal, 1000, W, isEvent: false, end: 1030); // 出た先を答えた
        Query(g, gen, 1140, FieldKind.Normal);
        var r = g.TakeResolved(1150);
        Assert.Equal("Q[Tab]", Hidden(r));
        Assert.Equal("", Visible(r));
    }

    [Fact]
    public void R8_a_password_query_straddling_the_tab_into_the_field_does_not_hide_that_tab()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 500, W);
        Query(g, gen, 620, FieldKind.Normal);
        g.Enqueue(Ch('a'), 700, W);
        Query(g, gen, 820, FieldKind.Normal);
        Assert.Equal("[Tab]a", Visible(g.TakeResolved(830)));
        g.Enqueue(Tab, 1010, W);                                 // パスワード欄へ
        g.OnObservation(gen, FieldKind.Password, 1000, W, isEvent: false, end: 1030); // 入った先を答えた
        Assert.Equal("[Tab]", Visible(g.TakeResolved(1030)));
    }

    // ---- 工程 G（T-05 の WPF で検出）: 区切りをまたいだ照会 ----

    [Fact]
    public void G_a_poll_query_that_returned_after_the_tab_out_does_not_show_the_tab()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);                                  // パスワード欄へ
        Query(g, gen, 990, FieldKind.Normal);
        g.TakeResolved(1000);
        Event(g, gen, 1040, FieldKind.Password);
        g.OnObservation(gen, FieldKind.Password, 1160, W, isEvent: false, end: 1162);
        g.OnObservation(gen, FieldKind.Password, 1320, W, isEvent: false, end: 1330);
        // 1500 に始めた照会が 1500.2 の Shift+Tab の後に戻り、戻った先の通常欄を答えた
        g.OnObservation(gen, FieldKind.Normal, 1500, W, isEvent: false, end: 1510);
        g.Enqueue(ShiftTab, 1501, W);
        Event(g, gen, 1520, FieldKind.Normal);
        var r = g.TakeResolved(1530);
        Assert.Equal("[Tab]", Hidden(r));
        Assert.Equal("", Visible(r));
    }

    [Fact]
    public void G_the_key_query_that_straddles_the_next_boundary_is_not_used()
    {
        var g = NewGate(out var gen);
        Query(g, gen, 990, FieldKind.Normal);
        g.Enqueue(Tab, 1000, W);                                  // パスワード欄へ（イベントは届かない）
        g.Enqueue(Ch('Q'), 1010, W);
        g.OnObservation(gen, FieldKind.Normal, 1130, W, isEvent: false, end: 1140); // Q の照会。戻る前に Shift+Tab が処理された
        g.Enqueue(ShiftTab, 1131, W);
        Query(g, gen, 1260, FieldKind.Normal);
        var r = g.TakeResolved(1270);
        Assert.Contains("Q", Hidden(r));
        Assert.DoesNotContain("Q", Visible(r));
    }

    /// <summary>工程 G（T-08 で検出）: Ctrl を押してすぐ C を押すショートカットが、Ctrl の押下を区切りにして捨てられない。</summary>
    [Fact]
    public void G_a_shortcut_right_after_its_modifier_is_shown()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Tab, 1000, W);
        Query(g, gen, 1120, FieldKind.Normal);
        g.Enqueue(new KeyInput(VirtualKeys.LControl, true, KeyModifiers.Control), 1500, W);
        g.Enqueue(new KeyInput('C', true, KeyModifiers.Control), 1501, W);
        Query(g, gen, 1625, FieldKind.Normal);
        var r = g.TakeResolved(1630);
        Assert.Contains(r, k => k.Input.VirtualKey == 'C' && !k.Hidden);
        Assert.DoesNotContain(r, k => k.Hidden);
    }

    /// <summary>工程 G（T-08 で検出）: 区切りのキーがすぐ判定されて列が空になっても、次の区間の照会を依頼する。</summary>
    [Fact]
    public void G_a_query_is_requested_for_the_segment_after_a_boundary_even_with_nothing_pending()
    {
        var g = NewGate(out var gen);
        g.Enqueue(new KeyInput(VirtualKeys.Return, true, KeyModifiers.None, "\r"), 1000, W);
        Assert.Single(g.TakeResolved(1000));      // 区切りの時点で判定（R4）。列は空
        Assert.Equal(1120, g.NextQueryAt);        // それでも区切りの後の区間の照会を依頼する
        Query(g, gen, 1120, FieldKind.Normal);
        Assert.Null(g.NextQueryAt);
        g.Enqueue(new KeyInput(VirtualKeys.LControl, true, KeyModifiers.Control), 1312, W);
        g.Enqueue(new KeyInput('C', true, KeyModifiers.Control), 1313, W);
        var r = g.TakeResolved(1313);             // Ctrl+C は区切り。区間の照会があるのですぐ判定できる
        Assert.Equal(2, r.Count);
        Assert.DoesNotContain(r, k => k.Hidden);
    }

    // ---- (4) 高速の往復の組み合わせ ----

    [Theory]
    [InlineData(0, "none")]
    [InlineData(20, "none")]
    [InlineData(50, "none")]
    [InlineData(0, "late")]
    [InlineData(20, "late")]
    [InlineData(50, "late")]
    [InlineData(0, "stale")]
    [InlineData(20, "stale")]
    [InlineData(50, "stale")]
    public void Fast_round_trip_never_shows_the_password_key(int gap, string events)
    {
        var g = NewGate(out var gen);
        long t0 = 1000, tq = t0 + gap + 1, ts = tq + gap + 1, ta = ts + gap + 1, tb = ta + gap + 1;
        g.Enqueue(Tab, t0, W);
        g.Enqueue(Ch('Q'), tq, W);
        g.Enqueue(ShiftTab, ts, W);
        g.Enqueue(Ch('a'), ta, W);
        g.Enqueue(Ch('b'), tb, W);
        if (events == "late") Event(g, gen, ts + 5, FieldKind.Password); // パスワード欄のイベントが Shift+Tab の後に届く
        if (events == "stale") Query(g, gen, t0 + Settle, FieldKind.Normal); // 照会が移る前の欄を返す
        Query(g, gen, tb + Settle + 10, FieldKind.Normal);
        var r = g.TakeResolved(tb + Settle + 20);

        Assert.Contains("Q", Hidden(r));
        Assert.DoesNotContain("Q", Visible(r));
        Assert.Equal(1, r.Count(k => k.Hidden && k.Input.Modifiers == KeyModifiers.Shift)); // Shift+Tab は出ない
        if (events != "late") Assert.Equal("ab", Visible(r).Replace("[Tab]", ""));
        Assert.Equal(0, g.PendingCount);
    }

    // ---- (5) 出た直後 ----

    [Fact]
    public void Keys_after_leaving_are_judged_by_the_next_segment()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 500, FieldKind.Password);
        g.Enqueue(ShiftTab, 1000, W);
        g.Enqueue(Ch('a'), 1030, W);
        Event(g, gen, 1050, FieldKind.Normal);
        Query(g, gen, 1150, FieldKind.Normal);
        var r = g.TakeResolved(1160);
        Assert.Equal("[Tab]", Hidden(r));
        Assert.Equal("a", Visible(r));
    }

    // ---- (6) 「パスワードを表示」 ----

    [Fact]
    public void Reveal_then_mask_in_the_same_field()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 10, FieldKind.Password);
        g.Enqueue(Ch('Q'), 100, W);
        Query(g, gen, 220, FieldKind.Password);
        Assert.Equal("Q", Hidden(g.TakeResolved(230)));
        // 300 で伏字が外れる（フォーカスは動かない）
        g.Enqueue(Ch('a'), 400, W);
        Query(g, gen, 520, FieldKind.Normal);
        Assert.Equal("a", Hidden(g.TakeResolved(530))); // 外れてから最初の照会までのキーは隠れることがある
        g.Enqueue(Ch('b'), 600, W);
        Query(g, gen, 720, FieldKind.Normal);
        Assert.Equal("b", Visible(g.TakeResolved(730)));
        // 800 で伏字に戻る
        g.Enqueue(Ch('z'), 900, W);
        Query(g, gen, 1020, FieldKind.Password);
        Assert.Equal("z", Hidden(g.TakeResolved(1030)));
    }

    [Fact]
    public void Polls_while_in_a_password_field_to_notice_reveal()
    {
        var g = new PasswordInputGate(Settle, Timeout, poll: 150);
        var gen = g.Start(0);
        Query(g, gen, 0, FieldKind.Normal);
        Assert.Null(g.NextQueryAt);               // 通常欄では、キーが無ければ照会しない
        Event(g, gen, 100, FieldKind.Password);
        Assert.Equal(250, g.NextQueryAt);         // パスワード欄ではキーが無くても照会する
        Query(g, gen, 250, FieldKind.Password);
        Assert.Equal(400, g.NextQueryAt);
        g.OnQueryFailed(gen, 400);
        Assert.Equal(550, g.NextQueryAt);         // 失敗しても間隔を空ける
        Query(g, gen, 550, FieldKind.Normal);     // 伏字が外れた
        Assert.Null(g.NextQueryAt);
        g.Enqueue(Ch('a'), 600, W);
        Query(g, gen, 720, FieldKind.Normal);
        Assert.Equal("a", Visible(g.TakeResolved(730)));
    }

    // ---- (7) 照会の失敗・時間切れ ----

    [Fact]
    public void Failure_hides_only_when_password_is_confirmed_in_the_same_segment_and_window()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 10, FieldKind.Password, W);
        g.Enqueue(Ch('Q'), 100, W);
        g.Enqueue(Ch('x'), 110, W2);              // 別の窓
        g.OnQueryFailed(gen, 230);
        var r = g.TakeResolved(240);
        Assert.Equal("Q", Hidden(r));
        Assert.Equal("x", Visible(r));

        g.Enqueue(Tab, 300, W);                   // パスワード欄で押した区切り（区間の照会は失敗のみ）
        g.Enqueue(Ch('y'), 400, W);               // 区切りの後
        g.OnQueryFailed(gen, 520);
        r = g.TakeResolved(530);
        Assert.Equal("[Tab]", Hidden(r));
        Assert.Equal("y", Visible(r));
    }

    [Fact]
    public void Timeout_decides_with_what_is_known()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        Assert.Equal(520, g.NextTimeoutAt);
        Assert.Empty(g.TakeResolved(519));
        var r = g.TakeResolved(520);
        Assert.Equal("a", Visible(r)); // 調べられないときは表示する
        Assert.Equal(1, g.TimedOutCount);

        Event(g, gen, 600, FieldKind.Password);
        g.Enqueue(Ch('Q'), 700, W);
        Assert.Equal("Q", Hidden(g.TakeResolved(1120))); // 確認済みのパスワード欄
    }

    [Fact]
    public void A_late_old_result_does_not_override_a_newer_observation()
    {
        var g = NewGate(out var gen);
        Event(g, gen, 300, FieldKind.Password);
        Assert.True(g.ConsumePasswordObserved());
        Query(g, gen, 220, FieldKind.Normal); // 時間切れの後に遅れて届いた、より古い照会
        Assert.False(g.ConsumePasswordObserved());
        g.Enqueue(Ch('Q'), 400, W);
        Query(g, gen, 520, FieldKind.Normal);
        Assert.Equal("Q", Hidden(g.TakeResolved(530))); // (a): 押した時点の最新の観測は 300 のパスワード欄
    }

    // ---- (8)(9) 回の番号と時刻の新旧 ----

    [Fact]
    public void Notices_of_an_old_run_are_ignored()
    {
        var g = new PasswordInputGate(Settle, Timeout);
        var gen1 = g.Start(0);
        Assert.Empty(g.Stop());
        var gen2 = g.Start(1000);
        Assert.NotEqual(gen1, gen2);
        g.Enqueue(Ch('Q'), 1100, W);
        Assert.False(g.OnObservation(gen1, FieldKind.Normal, 1130, W, isEvent: true));  // 新しいキーと新しい回のイベントの間
        Event(g, gen2, 1150, FieldKind.Password);
        Assert.False(g.OnObservation(gen1, FieldKind.Normal, 1300, W, isEvent: false)); // その後
        Assert.False(g.OnQueryFailed(gen1, 1300));
        Query(g, gen2, 1220, FieldKind.Password);
        Assert.Equal("Q", Hidden(g.TakeResolved(1230)));

        g.Stop();
        Assert.False(g.OnObservation(gen2, FieldKind.Normal, 1400, W, isEvent: false)); // 停止後
        Assert.False(g.IsActive);
    }

    [Fact]
    public void An_event_after_a_query_start_is_newer_than_that_query()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        Event(g, gen, 250, FieldKind.Password);   // 照会（220 開始）より後に動いた
        Query(g, gen, 220, FieldKind.Normal);     // 結果は後から届く
        // a は通常欄で押したが、判定の時点で入力先がパスワード欄に移っているので出さない（入欄で表示を消すのと同じ）
        Assert.Equal("a", Hidden(g.TakeResolved(260)));
        g.Enqueue(Ch('Q'), 300, W);
        Query(g, gen, 420, FieldKind.Normal);
        Assert.Equal("Q", Hidden(g.TakeResolved(430)));
    }

    // ---- (10)〜(12) ----

    [Fact]
    public void Stop_drops_presses_and_returns_releases()
    {
        var g = NewGate(out _);
        g.Enqueue(Ch('a'), 100, W);
        g.Enqueue(Up('A'), 110, W);
        g.Enqueue(Ch('b'), 120, W);
        var ups = g.Stop();
        Assert.Single(ups);
        Assert.False(ups[0].IsKeyDown);
        Assert.Equal(0, g.PendingCount);
    }

    [Fact]
    public void Password_observation_signals_clearing_only_when_it_is_the_latest()
    {
        var g = NewGate(out var gen, FieldKind.Password);
        Assert.True(g.ConsumePasswordObserved()); // ON にした時点でパスワード欄
        Assert.False(g.ConsumePasswordObserved());
        Event(g, gen, 100, FieldKind.Normal);
        Event(g, gen, 50, FieldKind.Password);
        Assert.False(g.ConsumePasswordObserved());
    }

    /// <summary>コードレビュー R2: 観測が上限の件数に達していても、最新のパスワード欄の観測で消去を通知する。</summary>
    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    public void R2_password_observation_signals_clearing_at_the_observation_limit(int prior)
    {
        var g = new PasswordInputGate(Settle, Timeout);
        var gen = g.Start(0);
        for (var i = 1; i <= prior; i++) Event(g, gen, i, FieldKind.Normal);
        Event(g, gen, prior + 1, FieldKind.Password);
        Assert.True(g.ConsumePasswordObserved());
        Event(g, gen, prior + 2, FieldKind.Normal);
        Event(g, gen, prior, FieldKind.Password);   // 遅れて届いた古いパスワード欄の観測では消さない
        Assert.False(g.ConsumePasswordObserved());
    }

    [Fact]
    public void Resolved_keys_keep_the_window_they_were_typed_in()
    {
        var g = NewGate(out var gen);
        g.Enqueue(Ch('a'), 100, W);
        g.Enqueue(Ch('b'), 110, W2);
        Query(g, gen, 240, FieldKind.Normal);
        var r = g.TakeResolved(250);
        Assert.Equal(new[] { W, W2 }, r.Select(k => k.Foreground));
    }

    [Fact]
    public void Initial_query_is_requested_at_start()
    {
        var g = new PasswordInputGate(Settle, Timeout);
        var gen = g.Start(500);
        Assert.Equal(500, g.NextQueryAt);
        Query(g, gen, 501, FieldKind.Normal);
        Assert.Null(g.NextQueryAt);
    }

    [Theory]
    [InlineData(VirtualKeys.Tab, KeyModifiers.None, "\t", true)]
    [InlineData(VirtualKeys.Return, KeyModifiers.None, "\r", true)]
    [InlineData(VirtualKeys.Left, KeyModifiers.None, null, true)]
    [InlineData(VirtualKeys.LWin, KeyModifiers.Win, null, false)]          // 修飾キー単独は区切りにしない（工程 G）
    [InlineData(VirtualKeys.LControl, KeyModifiers.Control, null, false)]
    [InlineData(VirtualKeys.LMenu, KeyModifiers.Alt, null, false)]
    [InlineData(0x4C, KeyModifiers.Control, null, true)]
    [InlineData(VirtualKeys.Tab, KeyModifiers.Alt, null, true)]
    [InlineData(0x41, KeyModifiers.None, "a", false)]
    [InlineData(0x41, KeyModifiers.Shift, "A", false)]
    [InlineData(VirtualKeys.Space, KeyModifiers.None, " ", false)]
    [InlineData(VirtualKeys.Back, KeyModifiers.None, "\b", false)]
    [InlineData(VirtualKeys.LShift, KeyModifiers.Shift, null, false)]
    [InlineData(VirtualKeys.Capital, KeyModifiers.None, null, false)]
    [InlineData(VirtualKeys.OemAuto, KeyModifiers.None, null, false)]
    public void Boundary_keys(int vk, KeyModifiers mods, string? text, bool expected)
    {
        Assert.Equal(expected, PasswordInputGate.IsBoundary(new KeyInput(vk, true, mods, text)));
        Assert.False(PasswordInputGate.IsBoundary(new KeyInput(vk, false, mods, text)));
    }

    [Fact]
    public void Long_typing_in_one_field_does_not_grow_without_bound()
    {
        var g = NewGate(out var gen);
        for (var i = 0; i < 5000; i++)
        {
            long t = 1000 + i * 10;
            g.Enqueue(Ch('a'), t, W);
            Query(g, gen, t + Settle, FieldKind.Normal);
            Assert.Equal("a", Visible(g.TakeResolved(t + Settle + 1)));
        }
        Assert.Equal(0, g.PendingCount);
    }
}
