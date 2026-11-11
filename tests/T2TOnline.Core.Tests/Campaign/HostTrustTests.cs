using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// D135: a host's progress counts on a friend's profile only for the hosting session the friend was welcomed into, and
/// within a rolling 24-hour budget per host and stat, bounded by this PC's own play with that host, that hosting again
/// doesn't start over; the ledger knows hosts only by a key this PC makes, and the host's match number rides in its match
/// setups.
/// </summary>
public class HostTrustTests
{
    private const ulong Welcome = 0x1111_2222_3333_4444;
    private const int Stat = 9; // TotalGoals: the scoring pace (20, and 10 a minute of matches played here)
    private const int OnIce = 59; // TotalPlayTimeOnIce: the clock pace (600, and 120 a minute of matches played here)
    private static readonly DateTime Noon = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Secret = Enumerable.Range(1, HostKeys.SecretBytes).Select(i => (byte)i).ToArray();
    private static readonly string Alex = HostKeys.Tag(Secret, 76561199000000001);
    private static readonly string Sam = HostKeys.Tag(Secret, 76561199000000002);

    private const ulong HostId = 76561199000000001;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.11.0", 0x1122334455667788);

    private static StatEntry Add(int value) => new(Stat, StatOp.IncrementBy, StatValue.Whole(value));

    private static byte[] WelcomeBytes(byte slot, ulong hostingSession)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.Welcome, 0, 0, 0).Write(writer);
        new WelcomeMessage(slot, SessionPhase.Menus, hostingSession).Write(writer);
        return writer.ToArray();
    }

    private static ProgressMessage Message(ulong session, uint sequence) =>
        new(session, 1, new ProgressBatch(sequence, 0, string.Empty, new ProgressEntry[] { Add(1) }));

    // ---------------------------------------------------------------- (a) the connection's session

    [Fact]
    public void Batches_and_status_count_only_for_the_session_this_pc_was_welcomed_into()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        Assert.Equal(ProgressOffer.Accepted, inbox.Offer(Message(Welcome, 1), Welcome));
        Assert.Equal(ProgressOffer.WrongSession, inbox.Offer(Message(Welcome + 1, 1), Welcome));
        Assert.False(inbox.Status(new ProgressStatus(Welcome + 1, true, 1, 9), Welcome));
        Assert.Equal((Welcome, 1, 2L), (inbox.Session, inbox.Held, inbox.WrongSession));
        Assert.Null(inbox.HostStatus);

        // Not joined (no welcome now): nothing counts.
        Assert.Equal(ProgressOffer.WrongSession, inbox.Offer(Message(Welcome, 2), 0));
        Assert.True(inbox.Status(new ProgressStatus(Welcome, true, 1, 2), Welcome));
        Assert.Equal(2u, inbox.HostStatus!.Value.Newest);
    }

    [Fact]
    public void An_honest_host_hosting_again_after_a_rejoin_is_a_new_welcome_and_counts()
    {
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        Assert.Equal(ProgressOffer.Accepted, inbox.Offer(Message(Welcome, 1), Welcome));
        Assert.True(inbox.TryPeek(0, out ProgressBatch? batch, out _));
        inbox.Done(batch!);

        // The host stopped hosting and hosted again; this PC rejoined: the new welcome names the new session.
        const ulong again = Welcome + 7;
        Assert.Equal(ProgressOffer.WrongSession, inbox.Offer(Message(Welcome, 2), again)); // the old session's, late
        Assert.Equal(ProgressOffer.Accepted, inbox.Offer(Message(again, 1), again));
        Assert.Equal(again, inbox.Session);
        Assert.Equal(1u, ledger.AppliedThrough(Welcome));
    }

    [Fact]
    public void A_new_session_per_batch_is_refused_and_the_budget_holds()
    {
        // The attack (0.10.0): one session id per batch, each a fresh 1,000 of decrements off one stat.
        var inbox = new ProgressInbox(new ProgressLedger());
        for (ulong session = 100; session < 105; session++)
        {
            ProgressOffer offer = inbox.Offer(Message(session, 1), Welcome);
            Assert.Equal(ProgressOffer.WrongSession, offer);
        }

        Assert.Equal(0UL, inbox.Session);

        // Even a host that hosts again for real (a human rejoin each time), with a long night played here, takes off no more
        // than it added to the stat in the day (and a little): here nothing, so none of its decrements of 100.
        var credit = new ProgressCredit();
        var ledger = new ProgressLedger();
        ledger.NotePlay(Alex, Noon, new PlayTally(4 * 3600, 40, 6 * 3600, 3));
        var take = new StatEntry(46, StatOp.IncrementBy, StatValue.Whole(-100));
        int taken = 0;
        for (ulong session = 2; session < 12; session++)
        {
            for (int i = 0; i < 10; i++)
            {
                taken += credit.TryGive(session, take, ledger, Alex, Noon) ? 1 : 0;
            }
        }

        Assert.Equal(0, taken);
        Assert.Equal(100, credit.OverBudget);

        // What it gave, it may take back (a goal against cancelled), up to the day's 1,000.
        Assert.True(credit.TryGive(20, new StatEntry(46, StatOp.IncrementBy, StatValue.Whole(5_000)), ledger, Alex, Noon));
        for (int i = 0; i < 20; i++)
        {
            taken += credit.TryGive(20 + (ulong)i, take, ledger, Alex, Noon) ? 1 : 0;
        }

        Assert.Equal(ProgressLimits.MaxStatDecrementPerHostDay / 100, taken);
    }

    [Fact]
    public void A_repeated_welcome_can_t_move_a_joined_friend_to_another_hosting_session()
    {
        // The session progress counts for is the joined welcome's; a host answering a hello again with another session
        // would start that session's limits over with no rejoin at all.
        var transport = new Capture();
        var friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 0, transport);
        friend.Start(0);
        friend.Receive(HostId, Channel.Events, WelcomeBytes(1, Welcome), 1);
        Assert.Equal(FriendSessionState.Joined, friend.State);

        // An honest repeat (a hello retry crossed the welcome): the same session, taken as before.
        friend.Receive(HostId, Channel.Events, WelcomeBytes(1, Welcome), 2);
        Assert.Equal(Welcome, friend.Welcome.HostingSession);
        Assert.Equal(0, friend.Link.Malformed);

        // Another hosting session in a repeat: refused (counted), the joined one stays.
        transport.Sent.Clear();
        friend.Receive(HostId, Channel.Events, WelcomeBytes(2, Welcome + 1), 3);
        Assert.Equal((Welcome, (byte)1), (friend.Welcome.HostingSession, friend.Welcome.Slot));
        Assert.Equal(1, friend.Link.Malformed);
        Assert.Empty(transport.Sent);
        Assert.Equal(FriendSessionState.Joined, friend.State);
    }

    // ---------------------------------------------------------------- (b) the budget per host

    [Fact]
    public void A_host_s_budget_holds_across_its_hosting_sessions_and_another_host_has_its_own()
    {
        // Half an hour of Alex's matches played here: at most 20 + 10 x 30 = 320 goals in the day.
        var credit = new ProgressCredit();
        var ledger = new ProgressLedger();
        ledger.NotePlay(Alex, Noon, new PlayTally(1800, 6, 2400, 1));
        Assert.True(credit.TryGive(1, Add(200), ledger, Alex, Noon));
        Assert.False(credit.TryGive(2, Add(150), ledger, Alex, Noon.AddHours(1))); // hosting again: still Alex's day
        Assert.True(credit.TryGive(2, Add(120), ledger, Alex, Noon.AddHours(1)));
        Assert.False(credit.TryGive(3, Add(1), ledger, Alex, Noon.AddHours(2)));

        // Sam has nothing played here: the floor only (a batch that comes before its play is noted).
        Assert.True(credit.TryGive(3, Add(20), ledger, Sam, Noon.AddHours(2)));
        Assert.False(credit.TryGive(3, Add(1), ledger, Sam, Noon.AddHours(2)));
        Assert.Equal((3, 0), (credit.OverBudget, credit.Refused));
        Assert.Equal((320d, 0d), ledger.BudgetUsed(Alex, Stat, Noon.AddHours(2)));

        // More play, more room: ten more minutes of Alex's matches.
        ledger.NotePlay(Alex, Noon.AddHours(2), new PlayTally(600, 2, 600, 0));
        Assert.True(credit.TryGive(3, Add(100), ledger, Alex, Noon.AddHours(2)));
        Assert.False(credit.TryGive(3, Add(1), ledger, Alex, Noon.AddHours(2)));
    }

    [Fact]
    public void The_window_rolls_hour_by_hour()
    {
        // Time on the ice: 600 and 2 s a second of matches played here. 1,000 s at noon: 2,600.
        var ledger = new ProgressLedger();
        ledger.NotePlay(Alex, Noon, new PlayTally(1000, 3, 1500, 1));
        Assert.True(ledger.TrySpend(Alex, OnIce, 2000, Noon));
        ledger.NotePlay(Alex, Noon.AddHours(10), new PlayTally(1000, 3, 1500, 0));
        Assert.True(ledger.TrySpend(Alex, OnIce, 2600, Noon.AddHours(10)));
        Assert.False(ledger.TrySpend(Alex, OnIce, 1, Noon.AddHours(23.9)));
        Assert.Equal(new PlayTally(2000, 6, 3000, 1), ledger.PlayWithin(Alex, Noon.AddHours(23.9)));

        // 24 hours on, noon's play and credit are out of the window together; the evening's still count.
        Assert.False(ledger.TrySpend(Alex, OnIce, 1, Noon.AddHours(24)));
        Assert.Equal((2600d, 0d), ledger.BudgetUsed(Alex, OnIce, Noon.AddHours(24)));
        Assert.Equal(new PlayTally(1000, 3, 1500, 0), ledger.PlayWithin(Alex, Noon.AddHours(24)));

        // 34 hours on, nothing: the floor only.
        Assert.True(ledger.TrySpend(Alex, OnIce, 600, Noon.AddHours(34)));
        Assert.False(ledger.TrySpend(Alex, OnIce, 1, Noon.AddHours(34)));

        // Other stats have budgets of their own; a stat can't lose more than it was given in the window (and a little once a
        // match of the host's played here in it: none has).
        Assert.True(ledger.TrySpend(Alex, Stat, 20, Noon.AddHours(34)));
        Assert.True(ledger.TrySpend(Alex, OnIce, -600, Noon.AddHours(34)));
        Assert.False(ledger.TrySpend(Alex, OnIce, -1, Noon.AddHours(34)));
        ledger.NotePlay(Alex, Noon.AddHours(34), new PlayTally(300, 1, 300, 0));
        Assert.True(ledger.TrySpend(Alex, OnIce, -5, Noon.AddHours(34)));
        Assert.False(ledger.TrySpend(Alex, OnIce, -1, Noon.AddHours(34)));
        Assert.True(ledger.TrySpend(Alex, OnIce, 0, Noon.AddHours(34))); // a record adds nothing
    }

    [Fact]
    public void A_clock_set_far_back_or_ahead_doesn_t_hold_a_budget_forever()
    {
        var ledger = new ProgressLedger();
        ledger.NotePlay(Alex, Noon, new PlayTally(1000, 3, 1500, 1));
        Assert.True(ledger.TrySpend(Alex, OnIce, 2600, Noon));

        // The clock set a month back: noon's play and credit are both out of the window (the floor only).
        Assert.Equal(PlayTally.None, ledger.PlayWithin(Alex, Noon.AddDays(-30)));
        Assert.True(ledger.TrySpend(Alex, OnIce, 600, Noon.AddDays(-30)));
        Assert.False(ledger.TrySpend(Alex, OnIce, 1, Noon.AddDays(-30)));
    }

    [Fact]
    public void At_most_so_many_hosts_are_kept_the_one_used_longest_ago_goes_first()
    {
        var ledger = new ProgressLedger();
        Assert.True(ledger.TrySpend(Alex, Stat, 20, Noon));
        for (ulong host = 1; host <= ProgressLedger.MaxBudgetHosts; host++)
        {
            Assert.True(ledger.TrySpend(HostKeys.Tag(Secret, 76561199000000010 + host), Stat, 1, Noon.AddMinutes(1)));
        }

        Assert.Equal(ProgressLedger.MaxBudgetHosts, ledger.BudgetHostCount);
        Assert.Equal((0d, 0d), ledger.BudgetUsed(Alex, Stat, Noon)); // Alex was used longest ago: forgotten

        // The newest stays when one more comes, even used in the same hour as the others.
        Assert.True(ledger.TrySpend(Sam, Stat, 5, Noon.AddMinutes(1)));
        Assert.Equal((5d, 0d), ledger.BudgetUsed(Sam, Stat, Noon));
        Assert.Equal(ProgressLedger.MaxBudgetHosts, ledger.BudgetHostCount);
    }

    [Fact]
    public void The_budget_is_saved_with_the_ledger_and_a_damaged_line_is_skipped()
    {
        var ledger = new ProgressLedger();
        ledger.SetApplied(Welcome, 3);
        ledger.NotePlay(Alex, Noon, new PlayTally(1800.4, 6, 3600, 1));
        Assert.True(ledger.TrySpend(Alex, OnIce, 2422.79, Noon));
        Assert.True(ledger.TrySpend(Alex, OnIce, -2, Noon));
        Assert.True(ledger.TrySpend(Alex, OnIce + 1, 7, Noon.AddHours(1)));
        Assert.True(ledger.Changed);

        string text = ledger.Format();
        Assert.Contains($"budget {Alex} ", text, StringComparison.Ordinal);
        Assert.Contains($"play {Alex} ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("76561199", text, StringComparison.Ordinal); // the key, never the ID
        ProgressLedger read = ProgressLedger.Parse(text + $"budget {Alex} notanhour 9:1:1\nbudget {Sam} 5 x:1:1 9:-1:0 9:1e9:0\nbudget\n" +
            $"play {Sam} 5 -1 1 1 1\nplay {Sam} 5 1e12 1 1 1\nplay {Sam} 5 1 1 1\nplay {new string('k', 65)} 5 1 1 1 1\n");
        Assert.Equal(text, read.Format());
        Assert.Equal((2422.79, 2d), read.BudgetUsed(Alex, OnIce, Noon));
        Assert.Equal(new PlayTally(1800.4, 6, 3600, 1), read.PlayWithin(Alex, Noon));
        Assert.Equal(3u, read.AppliedThrough(Welcome));
        Assert.Equal(1, read.BudgetHostCount);
        Assert.False(read.Changed);

        // A version before 0.11.0 reads a ledger with budget lines (it skips kinds it doesn't know); this one reads theirs.
        Assert.Equal(0, ProgressLedger.Parse("session 0000000000000abc 4\n").BudgetHostCount);
        Assert.Throws<ArgumentException>(() => ledger.TrySpend("not a key", Stat, 1, Noon));
    }

    [Fact]
    public void A_damaged_ledger_can_t_hold_more_than_the_window_s_worth_of_hours_or_hosts()
    {
        var lines = new List<string>();
        for (int hour = 0; hour < 200; hour++)
        {
            lines.Add($"budget {Alex} {hour} 9:1:0");
        }

        for (int host = 0; host < 100; host++)
        {
            lines.Add($"budget host{host} 300 9:1:0");
        }

        ProgressLedger read = ProgressLedger.Parse(string.Join("\n", lines));
        Assert.Equal(ProgressLedger.MaxBudgetHosts, read.BudgetHostCount);
        string[] written = read.Format().Split('\n');
        Assert.Equal(ProgressLedger.MaxBudgetHosts, written.Count(l => l.StartsWith("budget host", StringComparison.Ordinal)));
        Assert.DoesNotContain(written, l => l.StartsWith($"budget {Alex}", StringComparison.Ordinal)); // used longest ago
    }

    // ---------------------------------------------------------------- the key per host

    [Fact]
    public void A_host_s_key_is_stable_keyed_by_this_pc_s_secret_and_names_nobody()
    {
        Assert.Equal(Alex, HostKeys.Tag(Secret, 76561199000000001));
        Assert.NotEqual(Alex, Sam);
        Assert.Equal(HostKeys.TagBytes * 2, Alex.Length);
        Assert.True(ProgressLimits.IsPlausibleId(Alex));
        Assert.DoesNotContain("76561199000000001", Alex, StringComparison.Ordinal);

        byte[] other = (byte[])Secret.Clone();
        other[0] ^= 0xff;
        Assert.NotEqual(Alex, HostKeys.Tag(other, 76561199000000001));
        Assert.Throws<ArgumentException>(() => HostKeys.Tag(new byte[5], 1));
    }

    [Fact]
    public void A_hosting_session_s_key_is_bound_to_its_host_stable_and_never_0()
    {
        const ulong session = 0x1234_5678_9abc_def0;
        ulong mine = HostKeys.SessionTag(Secret, 76561199000000001, session);
        Assert.Equal(mine, HostKeys.SessionTag(Secret, 76561199000000001, session));
        Assert.NotEqual(0UL, mine);
        Assert.NotEqual(session, mine);

        // Another host naming the same session, the same host's other session, another PC's secret: other keys.
        Assert.NotEqual(mine, HostKeys.SessionTag(Secret, 76561199000000002, session));
        Assert.NotEqual(mine, HostKeys.SessionTag(Secret, 76561199000000001, session + 1));
        byte[] other = (byte[])Secret.Clone();
        other[31] ^= 0x01;
        Assert.NotEqual(mine, HostKeys.SessionTag(other, 76561199000000001, session));

        // No session (the one-PC test): none.
        Assert.Equal(0UL, HostKeys.SessionTag(Secret, 76561199000000001, 0));
        Assert.Throws<ArgumentException>(() => HostKeys.SessionTag(new byte[5], 1, 1));
    }

    [Fact]
    public void A_missing_or_corrupt_key_file_gives_a_new_key_and_says_why()
    {
        Assert.True(HostKeys.TryRead(Secret, out byte[] secret, out string why));
        Assert.Equal(Secret, secret);
        Assert.Empty(why);

        Assert.False(HostKeys.TryRead(null, out byte[] made, out why));
        Assert.Equal("none yet", why);
        Assert.Equal(HostKeys.SecretBytes, made.Length);

        Assert.False(HostKeys.TryRead(new byte[7], out byte[] replaced, out why));
        Assert.Equal($"7 bytes, not {HostKeys.SecretBytes}", why);
        Assert.NotEqual(Secret, replaced);

        Assert.False(HostKeys.TryRead(new byte[HostKeys.SecretBytes], out _, out why));
        Assert.Equal("all zero", why);
        Assert.False(HostKeys.TryRead(Array.Empty<byte>(), out _, out _));

        // Two new secrets differ: each PC's keys are its own.
        Assert.NotEqual(HostKeys.NewSecret(), HostKeys.NewSecret());
    }

    // ---------------------------------------------------------------- the match number in the setup

    [Fact]
    public void The_host_s_match_number_rides_in_the_setup_and_isn_t_a_difference()
    {
        var host = new List<KeyValuePair<string, NetValue>>
        {
            new("mode", NetValue.FromInt(1)),
            new(MatchSetupValues.MatchIdKey, NetValue.FromInt(12345)),
            new(MatchSetupValues.HostSerialKey, NetValue.FromInt(7)),
        };
        var loaded = new List<KeyValuePair<string, NetValue>> { new("mode", NetValue.FromInt(1)) };

        Assert.Equal(7, MatchSetupValues.HostSerialOf(host));
        Assert.Equal(0, MatchSetupValues.HostSerialOf(loaded));
        Assert.Empty(MatchSetupValues.Differences(host, loaded, Core.Diagnostics.HostText.TextShape));
        Assert.Equal(0, MatchSetupValues.HostSerialOf(new[] { new KeyValuePair<string, NetValue>(MatchSetupValues.HostSerialKey, NetValue.FromInt(-3)) }));
        Assert.Equal(0, MatchSetupValues.HostSerialOf(new[] { new KeyValuePair<string, NetValue>(MatchSetupValues.HostSerialKey, NetValue.FromInt(1L << 40)) }));
        Assert.Equal(0, MatchSetupValues.HostSerialOf(new[] { new KeyValuePair<string, NetValue>(MatchSetupValues.HostSerialKey, NetValue.FromString("7")) }));
    }

    private sealed class Capture : ISessionTransport
    {
        public List<(Channel Channel, byte[] Bytes)> Sent { get; } = new();

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            Sent.Add((channel, message.ToArray()));
            return true;
        }
    }
}
