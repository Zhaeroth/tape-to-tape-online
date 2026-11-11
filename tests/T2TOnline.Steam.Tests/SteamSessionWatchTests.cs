using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamSessionWatchTests
{
    private const ulong Friend = 76561199000000002UL;
    private const ulong Other = 76561199000000003UL;

    /// <summary>A scripted Steam: each peer's session state and end reason, and every send with its flags.</summary>
    private sealed class FakeSessions : ISteamSessionApi
    {
        public Dictionary<ulong, SteamNetworkingConnectionState> States { get; } = new();

        public List<(ulong Peer, int Channel, SteamSendType Flags)> Sends { get; } = new();

        /// <summary>
        /// Peers whose session is broken: a send without the restart flag gets <see cref="BrokenAnswer"/>, one with it starts
        /// a new session.
        /// </summary>
        public HashSet<ulong> Broken { get; } = new();

        /// <summary>What a broken session answers a send without the flag: ConnectFailed, as GameNetworkingSockets does.</summary>
        public SteamResult BrokenAnswer { get; set; } = SteamResult.ConnectFailed;

        /// <summary>When set, every send gets this answer (a full send buffer, say).</summary>
        public SteamResult? Answer { get; set; }

        public int EndReason { get; set; } = 4002;

        public string EndDebug { get; set; } = "Timed out";

        public int EndReads { get; private set; }

        public SteamResult Send(ulong peer, int channel, ReadOnlySpan<byte> payload, SteamSendType flags)
        {
            Sends.Add((peer, channel, flags));
            if (Answer is SteamResult answer)
            {
                return answer;
            }

            if (!Broken.Contains(peer))
            {
                return SteamResult.OK;
            }

            if ((flags & SteamSendType.AutoRestartBrokenSession) == 0)
            {
                return BrokenAnswer;
            }

            Broken.Remove(peer);
            States[peer] = SteamNetworkingConnectionState.Connecting;
            return SteamResult.OK;
        }

        public SteamNetworkingConnectionState GetState(ulong peer) => States.TryGetValue(peer, out SteamNetworkingConnectionState state) ? state : SteamNetworkingConnectionState.None;

        public (int Reason, string Debug) GetEnd(ulong peer)
        {
            EndReads++;
            return (EndReason, EndDebug);
        }

        /// <summary>Every close asked for, in order.</summary>
        public List<ulong> Closed { get; } = new();

        /// <summary>What a close answers (Steam had the session: Closed).</summary>
        public SteamSessionClose CloseAnswer { get; set; } = SteamSessionClose.Closed;

        /// <summary>When set, a close throws this (a fake's failure; the real API never throws).</summary>
        public Exception? CloseThrows { get; set; }

        public SteamSessionClose Close(ulong peer, out string failure)
        {
            Closed.Add(peer);
            if (CloseThrows != null)
            {
                throw CloseThrows;
            }

            failure = CloseAnswer == SteamSessionClose.Failed ? "SEHException: External component has thrown an exception." : string.Empty;
            return CloseAnswer;
        }
    }

    private static List<SteamSessionChange> Take(SteamSessionWatch watch)
    {
        var changes = new List<SteamSessionChange>();
        while (watch.TryTakeChange(out SteamSessionChange change))
        {
            changes.Add(change);
        }

        return changes;
    }

    [Fact]
    public void A_send_goes_out_once_with_its_flags_and_without_the_restart_flag()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);

        SteamResult result = watch.Send(Friend, 1, new byte[] { 1, 2 }, SteamSendType.ReliableNoNagle | SteamSendType.AutoRestartBrokenSession);

        Assert.Equal(SteamResult.OK, result);
        Assert.Equal(new[] { (Friend, 1, SteamSendType.ReliableNoNagle) }, sessions.Sends);
        Assert.Equal(0, watch.Restarts);
    }

    [Fact]
    public void A_broken_session_is_noted_with_its_end_reason_then_restarted_with_the_same_message()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.Connected;
        watch.Poll();
        Take(watch);
        sessions.States[Friend] = SteamNetworkingConnectionState.ProblemDetectedLocally;
        sessions.Broken.Add(Friend);

        SteamResult result = watch.Send(Friend, 2, new byte[] { 9 }, SteamSendType.Reliable);

        Assert.Equal(SteamResult.OK, result);
        Assert.Equal(new[] { (Friend, 2, SteamSendType.Reliable), (Friend, 2, SteamSendType.Reliable | SteamSendType.AutoRestartBrokenSession) }, sessions.Sends);
        Assert.Equal(1, watch.Restarts);
        SteamSessionChange change = Assert.Single(Take(watch));
        Assert.Equal(new SteamSessionChange(Friend, SteamNetworkingConnectionState.Connected, SteamNetworkingConnectionState.ProblemDetectedLocally, Broke: true, 4002, "Timed out"), change);
    }

    [Fact]
    public void A_broken_session_answering_NoConnection_as_the_SDK_header_says_is_restarted_too()
    {
        var sessions = new FakeSessions { BrokenAnswer = SteamResult.NoConnection };
        var watch = new SteamSessionWatch(sessions);
        sessions.Broken.Add(Friend);

        Assert.Equal(SteamResult.OK, watch.Send(Friend, 1, new byte[] { 1 }, SteamSendType.ReliableNoNagle));

        Assert.Equal(new[] { (Friend, 1, SteamSendType.ReliableNoNagle), (Friend, 1, SteamSendType.ReliableNoNagle | SteamSendType.AutoRestartBrokenSession) }, sessions.Sends);
        Assert.Equal(1, watch.Restarts);
    }

    [Fact]
    public void Other_refusals_are_returned_as_they_are_without_a_restart()
    {
        var sessions = new FakeSessions { Answer = SteamResult.LimitExceeded };
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);

        Assert.Equal(SteamResult.LimitExceeded, watch.Send(Friend, 2, new byte[] { 1 }, SteamSendType.Reliable));

        Assert.Single(sessions.Sends);
        Assert.Equal(0, watch.Restarts);
        Assert.Empty(Take(watch));
    }

    [Fact]
    public void Sends_to_this_PCs_own_account_always_carry_the_restart_flag_and_are_not_counted()
    {
        // The one-PC test: Steam's session with yourself reads as closed after its first message, so without the
        // flag every later message to yourself was refused and the test never got past hello.
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions) { Loopback = Friend };
        watch.Watch(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.ClosedByPeer;
        sessions.Broken.Add(Friend);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(SteamResult.OK, watch.Send(Friend, 17, new byte[] { 1 }, SteamSendType.ReliableNoNagle));
            sessions.Broken.Add(Friend); // closed again after each message, as Steam's loopback does
        }

        Assert.All(sessions.Sends, s => Assert.Equal(SteamSendType.ReliableNoNagle | SteamSendType.AutoRestartBrokenSession, s.Flags));
        Assert.Equal(3, sessions.Sends.Count);
        Assert.Equal(0, watch.Restarts);
        Assert.Empty(Take(watch));

        // Another peer still goes without the flag first.
        Assert.Equal(SteamResult.OK, watch.Send(Other, 1, new byte[] { 1 }, SteamSendType.ReliableNoNagle));
        Assert.Equal((Other, 1, SteamSendType.ReliableNoNagle), sessions.Sends[^1]);
    }

    [Fact]
    public void An_unknown_own_account_puts_no_peer_on_the_loopback_path()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions) { Loopback = 0 };

        watch.Send(Friend, 1, new byte[] { 1 }, SteamSendType.ReliableNoNagle);

        Assert.Equal(new[] { (Friend, 1, SteamSendType.ReliableNoNagle) }, sessions.Sends);
    }

    [Fact]
    public void A_broken_session_with_an_unwatched_peer_is_restarted_without_a_change()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        sessions.Broken.Add(Other);

        Assert.Equal(SteamResult.OK, watch.Send(Other, 1, new byte[] { 1 }, SteamSendType.ReliableNoNagle));

        Assert.Equal(1, watch.Restarts);
        Assert.Empty(Take(watch));
    }

    [Fact]
    public void Polling_reports_every_change_and_only_a_connected_session_ending_is_a_break()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);

        var seen = new List<SteamSessionChange>();
        foreach (SteamNetworkingConnectionState state in new[]
        {
            SteamNetworkingConnectionState.Connecting, SteamNetworkingConnectionState.FindingRoute, SteamNetworkingConnectionState.Connected,
            SteamNetworkingConnectionState.Connected, SteamNetworkingConnectionState.ClosedByPeer, SteamNetworkingConnectionState.Connecting,
            SteamNetworkingConnectionState.Connected,
        })
        {
            sessions.States[Friend] = state;
            watch.Poll();
            seen.AddRange(Take(watch));
        }

        Assert.Equal(
            new[]
            {
                (SteamNetworkingConnectionState.None, SteamNetworkingConnectionState.Connecting, false),
                (SteamNetworkingConnectionState.Connecting, SteamNetworkingConnectionState.FindingRoute, false),
                (SteamNetworkingConnectionState.FindingRoute, SteamNetworkingConnectionState.Connected, false),
                (SteamNetworkingConnectionState.Connected, SteamNetworkingConnectionState.ClosedByPeer, true),
                (SteamNetworkingConnectionState.ClosedByPeer, SteamNetworkingConnectionState.Connecting, false),
                (SteamNetworkingConnectionState.Connecting, SteamNetworkingConnectionState.Connected, false),
            },
            seen.ConvertAll(c => (c.From, c.To, c.Broke)));
        Assert.Equal((4002, "Timed out"), (seen[3].EndReason, seen[3].EndDebug));
        Assert.Equal((0, string.Empty), (seen[2].EndReason, seen[2].EndDebug));
        Assert.Equal(1, sessions.EndReads); // only for the session that ended, not every poll
    }

    [Fact]
    public void A_session_replaced_by_the_peers_new_one_is_a_break()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.Connected;
        watch.Poll();
        Take(watch);

        sessions.States[Friend] = SteamNetworkingConnectionState.Connecting;
        watch.Poll();

        SteamSessionChange change = Assert.Single(Take(watch));
        Assert.True(change.Broke);
        Assert.Equal(SteamNetworkingConnectionState.Connecting, change.To);
    }

    [Fact]
    public void A_break_the_poll_already_saw_is_not_reported_again_by_a_send()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.Connected;
        watch.Poll();
        sessions.States[Friend] = SteamNetworkingConnectionState.ProblemDetectedLocally;
        watch.Poll();
        Assert.Contains(Take(watch), c => c.Broke);
        sessions.Broken.Add(Friend);

        watch.Send(Friend, 0, new byte[] { 1 }, SteamSendType.UnreliableNoDelay);

        Assert.DoesNotContain(Take(watch), c => c.Broke);
        Assert.Equal(1, watch.Restarts);
    }

    [Fact]
    public void A_session_never_connected_does_not_break()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.Connecting;
        watch.Poll();
        sessions.States[Friend] = SteamNetworkingConnectionState.ProblemDetectedLocally;
        watch.Poll();

        Assert.DoesNotContain(Take(watch), c => c.Broke);
    }

    [Fact]
    public void Forgotten_and_cleared_peers_are_not_read()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);
        watch.Watch(Other);
        watch.Watch(0);
        Assert.True(watch.IsWatching(Friend));
        Assert.False(watch.IsWatching(0));

        watch.Forget(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.Connected;
        sessions.States[Other] = SteamNetworkingConnectionState.Connected;
        watch.Poll();
        Assert.Equal(new[] { Other }, Take(watch).ConvertAll(c => c.Peer));

        sessions.States[Other] = SteamNetworkingConnectionState.ClosedByPeer;
        watch.Poll();
        watch.Clear();
        Assert.False(watch.IsWatching(Other));
        Assert.Empty(Take(watch));
    }

    [Fact]
    public void A_close_stops_watching_the_peer_first_so_the_closed_session_is_no_break()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Friend);
        sessions.States[Friend] = SteamNetworkingConnectionState.Connected;
        watch.Poll();
        Take(watch);

        Assert.Equal(SteamSessionClose.Closed, watch.Close(Friend));
        sessions.States[Friend] = SteamNetworkingConnectionState.None;
        watch.Poll();

        Assert.Equal(new[] { Friend }, sessions.Closed);
        Assert.False(watch.IsWatching(Friend));
        Assert.Empty(Take(watch));
        Assert.Equal(1, watch.Closes);
        Assert.False(watch.ClosesOff);
    }

    [Fact]
    public void The_one_pc_tests_own_session_and_no_peer_are_never_closed()
    {
        var sessions = new FakeSessions();
        var watch = new SteamSessionWatch(sessions) { Loopback = Other };

        Assert.Equal(SteamSessionClose.NotAllowed, watch.Close(Other));
        Assert.Equal(SteamSessionClose.NotAllowed, watch.Close(0));

        Assert.Empty(sessions.Closed);
        Assert.Equal(0, watch.Closes);
    }

    [Fact]
    public void Steams_answers_without_a_session_or_without_steam_come_back_as_they_are_and_closing_stays_on()
    {
        var sessions = new FakeSessions { CloseAnswer = SteamSessionClose.NoSession };
        var watch = new SteamSessionWatch(sessions);
        Assert.Equal(SteamSessionClose.NoSession, watch.Close(Friend));

        sessions.CloseAnswer = SteamSessionClose.Unavailable;
        Assert.Equal(SteamSessionClose.Unavailable, watch.Close(Friend));

        sessions.CloseAnswer = SteamSessionClose.Closed;
        Assert.Equal(SteamSessionClose.Closed, watch.Close(Other));
        Assert.Equal(new[] { Friend, Friend, Other }, sessions.Closed);
        Assert.False(watch.ClosesOff);
        Assert.Equal(1, watch.Closes);
    }

    [Fact]
    public void A_failed_close_turns_closing_off_and_nothing_else()
    {
        var sessions = new FakeSessions { CloseAnswer = SteamSessionClose.Failed };
        var watch = new SteamSessionWatch(sessions);
        watch.Watch(Other);

        Assert.Equal(SteamSessionClose.Failed, watch.Close(Friend));
        Assert.True(watch.ClosesOff);
        Assert.StartsWith("SEHException", watch.CloseFailure, StringComparison.Ordinal);

        // Every later close is off without asking Steam; sends and session reads go on as before.
        sessions.CloseAnswer = SteamSessionClose.Closed;
        Assert.Equal(SteamSessionClose.Off, watch.Close(Other));
        Assert.Equal(new[] { Friend }, sessions.Closed);
        Assert.True(watch.IsWatching(Other));
        Assert.Equal(SteamResult.OK, watch.Send(Other, 1, new byte[] { 1 }, SteamSendType.ReliableNoNagle));
        sessions.States[Other] = SteamNetworkingConnectionState.Connected;
        watch.Poll();
        Assert.Single(Take(watch));
    }

    [Fact]
    public void A_close_that_throws_is_a_failure_never_an_exception()
    {
        var sessions = new FakeSessions { CloseThrows = new InvalidOperationException("thrown by the fake") };
        var watch = new SteamSessionWatch(sessions);

        Assert.Equal(SteamSessionClose.Failed, watch.Close(Friend));
        Assert.True(watch.ClosesOff);
        Assert.Equal(nameof(InvalidOperationException), watch.CloseFailure);
        Assert.Equal(SteamSessionClose.Off, watch.Close(Friend));
    }
}
