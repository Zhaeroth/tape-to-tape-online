using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// The host keeps a Steam session only for a lobby member it admitted or is still deciding on, and closes the session of one
/// it refused or removed once the refusal has left: every path a real friend takes (a join, a rejoin after a drop, a refusal,
/// asking again) still gets through, and a peer that never sits can't keep one open.
/// </summary>
public class SessionAdmissionTests
{
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;
    private const ulong Riley = 76561199000000003;
    private const long Second = 1_000_000;

    /// <summary>One member poll for one peer: begin, judge, end.</summary>
    private static SessionVerdict Poll(SessionAdmission admission, ulong peer, long now, PeerStanding standing = PeerStanding.None, bool inLobby = true, bool refusalOut = true)
    {
        admission.BeginPass();
        SessionVerdict verdict = admission.Judge(peer, inLobby, standing, refusalOut, now);
        admission.EndPass();
        return verdict;
    }

    /// <summary>A poll every second from <paramref name="from"/> to <paramref name="to"/> (inclusive), its verdicts.</summary>
    private static List<SessionVerdict> PollEverySecond(SessionAdmission admission, ulong peer, long from, long to, PeerStanding standing = PeerStanding.None, bool inLobby = true, bool refusalOut = true)
    {
        var verdicts = new List<SessionVerdict>();
        for (long now = from; now <= to; now += Second)
        {
            verdicts.Add(Poll(admission, peer, now, standing, inLobby, refusalOut));
        }

        return verdicts;
    }

    [Fact]
    public void The_window_covers_a_joining_friends_every_hello()
    {
        // A friend says hello from their lobby entry for 20 s, and a first join nobody answered is retried 40 s more; the host
        // can't see them before they entered. The window is longer, with room for a host's own stalls.
        Assert.True(SessionAdmission.WindowMicros >= SessionTiming.HelloGiveUpMicros + SessionTiming.FirstJoinRetryMicros + (5 * Second));
        Assert.Equal(40 * Second, SessionTiming.FirstJoinRetryMicros);
    }

    [Fact]
    public void A_new_member_is_accepted_through_their_whole_join_and_joined_is_never_touched()
    {
        var admission = new SessionAdmission();
        List<SessionVerdict> joining = PollEverySecond(admission, Alex, 0, 60 * Second);
        Assert.All(joining, v => Assert.Equal(SessionVerdict.Accept, v));

        // Their hello was taken at the last second of their join: joined from then on, for as long as they play.
        List<SessionVerdict> playing = PollEverySecond(admission, Alex, 61 * Second, 3_600 * Second, PeerStanding.Joined);
        Assert.All(playing, v => Assert.Equal(SessionVerdict.Leave, v));
    }

    [Fact]
    public void A_member_who_never_says_hello_is_closed_once_after_the_window_then_left_alone()
    {
        var admission = new SessionAdmission();
        long window = SessionAdmission.WindowMicros;

        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, 0));
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, window - 1));
        Assert.Equal(SessionVerdict.CloseNoHello, Poll(admission, Riley, window));
        List<SessionVerdict> after = PollEverySecond(admission, Riley, window + Second, window + (600 * Second));
        Assert.All(after, v => Assert.Equal(SessionVerdict.Leave, v));
    }

    [Fact]
    public void Leaving_the_lobby_forgets_a_member_and_coming_back_starts_a_new_window()
    {
        var admission = new SessionAdmission();
        long window = SessionAdmission.WindowMicros;
        Poll(admission, Riley, 0);
        Assert.Equal(SessionVerdict.CloseNoHello, Poll(admission, Riley, window));

        // A pass without them (they left the lobby) forgets them.
        admission.BeginPass();
        admission.EndPass();
        Assert.Equal(0, admission.Count);

        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, window + (5 * Second)));
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, (2 * window) + (4 * Second)));
    }

    [Fact]
    public void A_kept_seat_is_accepted_for_as_long_as_it_is_kept_and_the_window_starts_when_it_ends()
    {
        var admission = new SessionAdmission();
        PollEverySecond(admission, Sam, 0, 10 * Second, PeerStanding.Joined);

        // They dropped mid-match: their seat is kept two minutes (or to the match's end), and every rejoin request is accepted.
        List<SessionVerdict> parked = PollEverySecond(admission, Sam, 11 * Second, 600 * Second, PeerStanding.Admitted);
        Assert.All(parked, v => Assert.Equal(SessionVerdict.Accept, v));

        // The seat went: a rejoin hello within the window (from the last moment the seat was kept) still gets through.
        long lastKept = 600 * Second;
        List<SessionVerdict> afterSeat = PollEverySecond(admission, Sam, lastKept + Second, lastKept + SessionAdmission.WindowMicros - Second);
        Assert.All(afterSeat, v => Assert.Equal(SessionVerdict.Accept, v));
        Assert.Equal(SessionVerdict.CloseNoHello, Poll(admission, Sam, lastKept + SessionAdmission.WindowMicros));
    }

    [Fact]
    public void A_friend_who_leaves_the_session_but_not_the_lobby_has_the_window_from_their_last_joined_moment()
    {
        var admission = new SessionAdmission();
        PollEverySecond(admission, Sam, 0, 1_000 * Second, PeerStanding.Joined);

        Assert.Equal(SessionVerdict.Accept, Poll(admission, Sam, 1_001 * Second));
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Sam, (1_000 * Second) + SessionAdmission.WindowMicros - 1));
        Assert.Equal(SessionVerdict.CloseNoHello, Poll(admission, Sam, (1_000 * Second) + SessionAdmission.WindowMicros));
    }

    [Fact]
    public void A_refusal_stops_accepting_and_closes_once_it_has_left_and_its_grace_has_passed()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        admission.Refused(Riley, 1 * Second);
        Assert.True(admission.ClosePending(Riley));

        // Not accepted any more; not closed while the refusal is still on its way, nor before its grace.
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Riley, 2 * Second, refusalOut: false));
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Riley, 3 * Second, refusalOut: true));
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Riley, 1 * Second + SessionAdmission.CloseAfterMicros, refusalOut: true));
        Assert.False(admission.ClosePending(Riley));

        // Closed for a while: nothing, and no second close.
        List<SessionVerdict> closed = PollEverySecond(admission, Riley, 5 * Second, (4 * Second) + SessionAdmission.ReopenAfterMicros - Second);
        Assert.All(closed, v => Assert.Equal(SessionVerdict.Leave, v));
    }

    [Fact]
    public void A_refusal_that_cannot_leave_is_closed_after_the_longest_wait_from_the_first_refusal()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        admission.Refused(Riley, 0);

        List<SessionVerdict> waiting = PollEverySecond(admission, Riley, Second, SessionAdmission.CloseAtMostMicros - Second, refusalOut: false);
        Assert.All(waiting, v => Assert.Equal(SessionVerdict.Leave, v));
        Assert.Equal(SessionVerdict.CloseRefusalStuck, Poll(admission, Riley, SessionAdmission.CloseAtMostMicros, refusalOut: false));
    }

    [Fact]
    public void Hellos_refused_over_and_over_cannot_put_the_close_off()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        long closedAt = -1;
        for (long now = 0; now <= 30 * Second && closedAt < 0; now += Second / 2)
        {
            admission.Refused(Riley, now); // a hello every half second, each refused
            if (Poll(admission, Riley, now, refusalOut: true) is SessionVerdict.CloseRefused or SessionVerdict.CloseRefusalStuck)
            {
                closedAt = now;
            }
        }

        Assert.Equal(SessionAdmission.CloseAtMostMicros, closedAt);
    }

    [Fact]
    public void A_refused_friend_who_asks_again_while_steam_still_lists_them_is_heard_after_the_reopen()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        admission.Refused(Riley, Second);
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Riley, 4 * Second));

        long reopen = (4 * Second) + SessionAdmission.ReopenAfterMicros;
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Riley, reopen - 1));
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, reopen));

        // Their hello is judged as before: refused again (the same reason they'd get), and closed again once that has left.
        admission.Refused(Riley, reopen + Second);
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Riley, reopen + (2 * Second)));
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Riley, reopen + Second + SessionAdmission.CloseAfterMicros));
    }

    [Fact]
    public void A_refused_friend_who_left_and_came_back_is_a_new_member_at_once()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        admission.Refused(Riley, Second);
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Riley, 4 * Second));

        admission.BeginPass(); // Steam's list no longer names them
        admission.EndPass();

        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, 6 * Second));
    }

    [Fact]
    public void A_refused_peer_seated_before_the_close_keeps_their_session()
    {
        var admission = new SessionAdmission();
        Poll(admission, Sam, 0);
        admission.Refused(Sam, Second); // the session was full...
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Sam, 2 * Second));

        // ...a seat freed and their next hello, already on its way, was taken: the close is off.
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Sam, 3 * Second, PeerStanding.Joined));
        Assert.False(admission.ClosePending(Sam));
        List<SessionVerdict> playing = PollEverySecond(admission, Sam, 4 * Second, 100 * Second, PeerStanding.Joined);
        Assert.All(playing, v => Assert.Equal(SessionVerdict.Leave, v));
    }

    [Fact]
    public void A_removal_is_a_refusal_once_and_a_removed_friend_asking_again_is_refused_and_closed_again()
    {
        var admission = new SessionAdmission();
        PollEverySecond(admission, Sam, 0, 10 * Second, PeerStanding.Joined);

        // Removed: their goodbye leaves, then the session closes.
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Sam, 11 * Second, PeerStanding.Removed, refusalOut: false));
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Sam, 14 * Second, PeerStanding.Removed, refusalOut: true));

        // Still in the lobby: after the reopen they're heard (no new refusal without a hello)...
        long reopen = (14 * Second) + SessionAdmission.ReopenAfterMicros;
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Sam, reopen, PeerStanding.Removed));
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Sam, reopen + (5 * Second), PeerStanding.Removed));

        // ...and their hello gets the Removed refusal (AcceptFriend), which closes the session again.
        admission.Refused(Sam, reopen + (6 * Second));
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Sam, reopen + (9 * Second), PeerStanding.Removed));
    }

    [Fact]
    public void A_removed_friend_out_of_the_lobby_is_closed_once_and_never_accepted()
    {
        var admission = new SessionAdmission();
        List<SessionVerdict> verdicts = PollEverySecond(admission, Sam, 0, 120 * Second, PeerStanding.Removed, inLobby: false);

        Assert.Equal(SessionVerdict.CloseRefused, verdicts.Single(v => v != SessionVerdict.Leave));
        Assert.Equal(SessionVerdict.CloseRefused, verdicts[3]);
    }

    [Fact]
    public void A_refusal_waiting_to_close_outlives_the_peer_leaving_the_lobby()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        admission.Refused(Riley, Second);

        // They left the lobby: the pass doesn't name them, and they're kept for the close.
        admission.BeginPass();
        admission.EndPass();
        var pending = new List<ulong>();
        admission.CopyPending(pending);
        Assert.Equal(new[] { Riley }, pending);

        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Riley, 5 * Second, inLobby: false));
        admission.BeginPass();
        admission.EndPass();
        Assert.Equal(0, admission.Count);
    }

    [Fact]
    public void A_refusal_for_a_peer_never_seen_in_the_lobby_still_closes()
    {
        var admission = new SessionAdmission();
        admission.Refused(Alex, 0); // a joined friend out of the lobby whose new hello no longer matched

        var pending = new List<ulong>();
        admission.CopyPending(pending);
        Assert.Equal(new[] { Alex }, pending);
        Assert.Equal(SessionVerdict.CloseRefused, Poll(admission, Alex, 3 * Second, inLobby: false));
    }

    [Fact]
    public void Someone_out_of_the_lobby_with_nothing_held_is_left_alone_and_not_held()
    {
        var admission = new SessionAdmission();

        Assert.Equal(SessionVerdict.Leave, Poll(admission, Riley, 0, inLobby: false));
        Assert.Equal(SessionVerdict.Leave, Poll(admission, Riley, 0, PeerStanding.Admitted, inLobby: false));
        Assert.Equal(0, admission.Count);
        Assert.Equal(SessionVerdict.Leave, Poll(admission, 0, 0));
    }

    [Fact]
    public void Past_the_limit_a_new_member_is_accepted_as_before_and_not_held()
    {
        var admission = new SessionAdmission();
        admission.BeginPass();
        for (ulong i = 0; i < SessionAdmission.MaxPeers; i++)
        {
            Assert.Equal(SessionVerdict.Accept, admission.Judge(Alex + 100 + i, true, PeerStanding.None, true, 0));
        }

        Assert.Equal(SessionVerdict.Accept, admission.Judge(Riley, true, PeerStanding.None, true, 0));
        Assert.Equal(SessionVerdict.Leave, admission.Judge(Sam, true, PeerStanding.Joined, true, 0));
        admission.EndPass();
        Assert.Equal(SessionAdmission.MaxPeers, admission.Count);
        Assert.False(admission.ClosePending(Riley));
    }

    [Fact]
    public void Clear_forgets_everyone()
    {
        var admission = new SessionAdmission();
        Poll(admission, Riley, 0);
        admission.Refused(Riley, 0);
        admission.Clear();

        Assert.Equal(0, admission.Count);
        Assert.False(admission.ClosePending(Riley));
        Assert.Equal(SessionVerdict.Accept, Poll(admission, Riley, SessionAdmission.WindowMicros * 3));
    }

    [Fact]
    public void A_peer_that_never_sits_is_never_accepted_longer_than_a_window_at_a_time_and_each_refusal_ends_in_a_close()
    {
        // A modified client staying in the lobby for an hour, saying hello just before each window ends, refused every time.
        var admission = new SessionAdmission();
        long acceptedSince = -1;
        long longestAccepted = 0;
        long refusedAt = -1;
        long longestToClose = 0;
        int closes = 0;
        for (long now = 0; now <= 3_600 * Second; now += Second)
        {
            SessionVerdict verdict = Poll(admission, Riley, now);
            if (verdict == SessionVerdict.Accept)
            {
                acceptedSince = acceptedSince < 0 ? now : acceptedSince;
                longestAccepted = System.Math.Max(longestAccepted, now - acceptedSince);
                if (now - acceptedSince >= SessionAdmission.WindowMicros - (2 * Second))
                {
                    admission.Refused(Riley, now);
                    refusedAt = now;
                    acceptedSince = -1;
                }
            }
            else if (verdict is SessionVerdict.CloseRefused or SessionVerdict.CloseRefusalStuck)
            {
                closes++;
                longestToClose = System.Math.Max(longestToClose, now - refusedAt);
            }
        }

        Assert.True(longestAccepted < SessionAdmission.WindowMicros);
        Assert.True(closes > 40);
        Assert.True(longestToClose <= SessionAdmission.CloseAtMostMicros);
    }
}
