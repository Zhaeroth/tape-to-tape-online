using System;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// D34 amended, D135: each hosting session has an id in the welcome, and a friend keeps a record of the play it shared
/// in it (the host's matches it played, the co-op run held), naming no host, so after a restart and a rejoin of the same
/// hosting session the moments owed for that play unlock.
/// </summary>
public class SharedPlayTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1_000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.8.0", 0x1122334455667788);
    private static readonly DateTime At = new(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_welcome_names_the_hosting_session_and_a_new_hosting_session_has_another()
    {
        var net = new FakeNetwork { LatencyMicros = 10 * Ms };
        var host = new HostSession(Identity, net.For(HostId), 3, hostingSession: 0x1234_5678_9abc_def0);
        Assert.Equal(0x1234_5678_9abc_def0UL, host.HostingSession);
        host.SetMembers(new[] { HostId, FriendId });
        var friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 0, net.For(FriendId));
        friend.Start(net.Now);
        for (int i = 0; i < 1000 && friend.State == FriendSessionState.Connecting; i++)
        {
            net.Now += Ms;
            net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    host.Receive(from, channel, bytes, net.Now);
                }
                else
                {
                    friend.Receive(from, channel, bytes, net.Now);
                }
            });
            while (host.TryTakeEvent(out HostEvent e))
            {
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    host.Accept(e.SteamId, 1, net.Now);
                }
            }

            host.Update(net.Now);
            friend.Update(net.Now);
        }

        Assert.Equal(FriendSessionState.Joined, friend.State);
        Assert.Equal(0x1234_5678_9abc_def0UL, friend.Welcome.HostingSession);

        // Made when not given: random, never 0, one per hosting session.
        var ids = Enumerable.Range(0, 50).Select(_ => new HostSession(Identity, net.For(HostId)).HostingSession).ToList();
        Assert.DoesNotContain(0UL, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void The_record_keeps_the_newest_sessions_and_reads_back_what_it_wrote()
    {
        var record = new SharedPlayRecord();
        Assert.False(record.NoteMatch(0, 3, At)); // not a session
        Assert.False(record.NoteMatch(0xabc, 0, At)); // not a match
        Assert.False(record.NoteRun(0, At));
        Assert.True(record.NoteMatch(0xabc, 3, At));
        Assert.False(record.NoteMatch(0xabc, 3, At.AddHours(1))); // known already
        Assert.True(record.NoteRun(0xabc, At));
        Assert.False(record.NoteRun(0xabc, At));
        for (ulong s = 1; s <= SharedPlayRecord.MaxKept; s++)
        {
            record.NoteMatch(s, (int)s, At.AddMinutes(s));
        }

        Assert.Equal(SharedPlayRecord.MaxKept, record.Count);
        Assert.False(record.Has(0xabc)); // the oldest went
        Assert.True(record.PlayedMatch(3, 3));
        Assert.False(record.PlayedMatch(3, 4));
        Assert.False(record.HeldRun(3));
        Assert.False(record.Has(0));

        SharedPlayRecord read = SharedPlayRecord.Parse(record.Format());
        Assert.Equal(record.Format(), read.Format());
        Assert.True(read.PlayedMatch(8, 8));
        Assert.False(read.HadHostIds);
    }

    [Fact]
    public void A_session_keeps_its_newest_matches_and_whether_the_run_was_held()
    {
        var record = new SharedPlayRecord();
        for (int match = 1; match <= SharedPlayRecord.MaxMatches + 5; match++)
        {
            record.NoteMatch(0xabc, match, At);
        }

        record.NoteRun(0xabc, At);
        Assert.Equal(SharedPlayRecord.MaxMatches, record.MatchCount(0xabc));
        Assert.False(record.PlayedMatch(0xabc, 5)); // the lowest went
        Assert.True(record.PlayedMatch(0xabc, 6));
        Assert.Equal(SharedPlayRecord.MaxMatches + 5, record.NewestMatch(0xabc));

        SharedPlayRecord read = SharedPlayRecord.Parse(record.Format());
        Assert.True(read.HeldRun(0xabc));
        Assert.Equal(SharedPlayRecord.MaxMatches, read.MatchCount(0xabc));
    }

    [Fact]
    public void The_record_names_no_host_and_the_old_format_reads_without_its_host_ids()
    {
        string old = "# T2T Online: the hosting sessions whose play this PC shared\r\n" +
            "host 76561199000000001 session 00000000000000ab at 2026-10-31T21:00:00.0000000Z\r\n" +
            "host 76561199000000002 session 00000000000000ac at 2026-10-31T22:00:00.0000000Z\n";
        SharedPlayRecord record = SharedPlayRecord.Parse(old);
        Assert.True(record.HadHostIds);
        Assert.Equal(2, record.Count);
        Assert.True(record.Has(0xab));
        Assert.False(record.PlayedMatch(0xab, 1)); // no play known: the old format kept none
        Assert.False(record.HeldRun(0xab));

        string written = record.Format();
        Assert.DoesNotContain("7656119900000000", written, StringComparison.Ordinal);
        Assert.DoesNotContain("host ", written, StringComparison.Ordinal);
        Assert.False(SharedPlayRecord.Parse(written).HadHostIds);
        Assert.Equal(2, SharedPlayRecord.Parse(written).Count);
    }

    [Fact]
    public void A_record_edited_or_damaged_keeps_what_still_reads()
    {
        string text = "# a comment\r\n" +
            "session 00000000000000ab at 2026-10-31T21:00:00.0000000Z run 1 matches 3,4\r\n" +
            "session 00000000000000ac at 2026-10-31T21:00:00.0000000Z run 2 matches 3\n" +
            "session nothex at 2026-10-31T21:00:00.0000000Z run 0 matches -\n" +
            "session 00000000000000ad at 2026-10-31T21:00:00.0000000Z run 0 matches 1,x\n" +
            "session 0000000000000000 at 2026-10-31T21:00:00.0000000Z run 0 matches 1\n" +
            "session\t00000000000000ae\tat\t2026-10-31T22:00:00Z\trun\t0\tmatches\t-\n" +
            "host 100 session 00000000000000af\n" +
            "garbage line\n";
        SharedPlayRecord record = SharedPlayRecord.Parse(text);
        Assert.Equal(2, record.Count);
        Assert.True(record.PlayedMatch(0xab, 4));
        Assert.True(record.HeldRun(0xab));
        Assert.True(record.Has(0xae));
        Assert.Equal(0, record.MatchCount(0xae));
        Assert.Equal(0, SharedPlayRecord.Parse(null).Count);
    }

    [Fact]
    public void What_was_seen_live_is_noted_once_and_a_rejoin_of_the_same_hosting_session_keeps_it()
    {
        var record = new SharedPlayRecord();
        var gate = new SharedPlayGate(record);
        Assert.False(gate.Welcomed(0xabc));
        Assert.False(gate.Played(3));
        Assert.True(gate.NoteMatch(3, At, out bool noted));
        Assert.True(noted);
        Assert.False(gate.NoteMatch(3, At, out noted));
        Assert.False(noted); // once
        Assert.True(record.PlayedMatch(0xabc, 3));

        // A rejoin of the same hosting session keeps it, another hosting session doesn't, and neither does the end of this one.
        Assert.True(gate.Welcomed(0xabc));
        Assert.True(gate.Played(3));
        Assert.False(gate.Welcomed(0xdef));
        Assert.False(gate.Played(3));
        Assert.Equal(0, gate.NewestPlayed);
        gate.Welcomed(0xabc);
        Assert.True(gate.Played(3)); // from the record
        gate.Clear();
        Assert.False(gate.Played(3));
        Assert.Equal(0UL, gate.Session);
        Assert.False(gate.SharedBefore);
    }

    [Fact]
    public void The_one_pc_test_has_no_session_and_nothing_is_kept()
    {
        var record = new SharedPlayRecord();
        var gate = new SharedPlayGate(record);
        Assert.True(gate.NoteMatch(1, At, out bool noted));
        Assert.False(noted);
        Assert.True(gate.NoteRun(At, out noted));
        Assert.False(noted);
        Assert.True(gate.Played(1));
        Assert.True(gate.HeldRun);
        Assert.Equal(0, record.Count);
    }

    [Fact]
    public void The_welcomes_layout_is_unchanged()
    {
        var writer = new T2TOnline.Core.Serialization.BitWriter();
        new WelcomeMessage(2, SessionPhase.Menus, 0x0102030405060708).Write(writer);
        Assert.Equal(new byte[] { 2, 1, 8, 7, 6, 5, 4, 3, 2, 1 }, writer.ToArray());
    }
}
