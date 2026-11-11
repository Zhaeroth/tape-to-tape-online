using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;
using T2TOnline.Core.Tests.Snapshots;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// Three friends at once (four PCs), each on a link of their own, through real sessions and the match
/// stream wired as the plugin wires them: one friend's trouble must never touch the others.
/// </summary>
public class MultiFriendTests
{
    private const ulong HostId = 100;
    private const long Ms = 1000;
    private static readonly ulong[] FriendIds = { 201, 202, 203 };
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.5.0-m3", 0x1122334455667788);

    [Fact]
    public void Three_friends_on_different_links_each_get_the_whole_stream()
    {
        var h = new Harness(seed: 9);
        h.Link(201, latencyMs: 10, jitterMs: 2, loss: 0);
        h.Link(202, latencyMs: 30, jitterMs: 10, loss: 0.02);
        h.Link(203, latencyMs: 60, jitterMs: 20, loss: 0.05);
        h.JoinAll();
        h.StartMatch();

        h.PlaySnapshots(600, hudEvery: 60);

        foreach (ulong id in FriendIds)
        {
            Friend friend = h.Friends[id];
            StreamFriend streamed = h.Stream.Find(id)!;
            Assert.True(streamed.Streaming);
            Assert.InRange(friend.Snapshots.Count, 540, 600);
            Assert.Equal(10, friend.Messages.Count);
            Assert.Equal(0, friend.Client.Receiver.Malformed);
            Assert.Equal(0, friend.Client.Receiver.MissingBase);
            Assert.True(streamed.Peer.Deltas > streamed.Peer.Keyframes * 20, $"friend {id}: {streamed.Peer.Deltas} deltas, {streamed.Peer.Keyframes} keyframes");
            Assert.Equal(0, h.Host.Find(id)!.Link.Malformed + friend.Session.Link.Malformed);
        }

        // Each friend acknowledges its own way: the slower the link, the further behind its delta base.
        Assert.True(h.Stream.Find(201)!.Peer.Acked >= h.Stream.Find(203)!.Peer.Acked);
    }

    [Fact]
    public void One_friend_s_dead_link_leaves_the_others_untouched_and_it_resumes_by_itself()
    {
        var h = new Harness(seed: 4);
        foreach (ulong id in FriendIds)
        {
            h.Link(id, latencyMs: 20, jitterMs: 5, loss: 0);
        }

        h.JoinAll();
        h.StartMatch();
        h.PlaySnapshots(120);
        Dictionary<ulong, long> keyframesBefore = FriendIds.ToDictionary(id => id, id => h.Stream.Find(id)!.Peer.Keyframes);
        Dictionary<ulong, long> heldBefore = FriendIds.ToDictionary(id => id, id => h.Stream.Find(id)!.Peer.Held); // the start, before the first ack

        // Friend 202's link goes down for 5 s (under the session timeout), then comes back.
        h.Links[202].Cut = true;
        h.PlaySnapshots(300);
        h.Links[202].Cut = false;
        h.PlaySnapshots(120);

        // The first MaxBaseAge snapshots of the drop still go as deltas against the last acknowledged one; after that, a
        // keyframe every KeyframeSpacing and the rest held.
        StreamFriend down = h.Stream.Find(202)!;
        long keyframesDuringDrop = down.Peer.Keyframes - keyframesBefore[202];
        int afterBaseAge = 300 - SnapshotPeer.MaxBaseAge;
        Assert.InRange(keyframesDuringDrop, afterBaseAge / SnapshotPeer.KeyframeSpacing, (afterBaseAge / SnapshotPeer.KeyframeSpacing) + 4);
        Assert.InRange(down.Peer.Held, afterBaseAge * 4 / 6, afterBaseAge);
        Assert.True(down.Peer.Acked > 500, "after the drop, friend 202 acknowledges the live stream again");
        Assert.Equal(0, h.Friends[202].Client.Receiver.MissingBase);
        foreach (ulong id in new ulong[] { 201, 203 })
        {
            Assert.Equal(keyframesBefore[id], h.Stream.Find(id)!.Peer.Keyframes);
            Assert.Equal(heldBefore[id], h.Stream.Find(id)!.Peer.Held);
            Assert.InRange(h.Friends[id].Snapshots.Count, 535, 540);
        }
    }

    [Fact]
    public void A_cut_shorter_than_the_reliable_timeout_delivers_its_backlog_once_and_in_order()
    {
        var h = new Harness(seed: 6);
        foreach (ulong id in FriendIds)
        {
            h.Link(id, latencyMs: 20, jitterMs: 5, loss: 0);
        }

        h.JoinAll();
        h.StartMatch();
        h.PlaySnapshots(60, hudEvery: 10);
        int messagesBefore = h.Friends[202].Messages.Count;
        int snapshotsBefore = h.Friends[202].Snapshots.Count;

        // Friend 202's link is down for about 5 s: the HUD sent meanwhile waits (Steam keeps resending reliable messages),
        // the snapshots are lost.
        h.Links[202].Cut = true;
        h.PlaySnapshots(300, hudEvery: 10);
        Assert.Equal(messagesBefore, h.Friends[202].Messages.Count);
        Assert.Equal(snapshotsBefore, h.Friends[202].Snapshots.Count);
        h.Links[202].Cut = false;
        h.PlaySnapshots(60, hudEvery: 10);

        // Every friend has every HUD message exactly once, in the order sent: the backlog arrived when the link came back.
        long[] sent = Enumerable.Range(1, 42).Select(i => i * 10L).ToArray();
        foreach (ulong id in FriendIds)
        {
            Assert.Equal(sent, h.Friends[id].Messages.Select(HudClock).ToArray());
            Assert.Equal(0, h.Friends[id].Client.Malformed + h.Friends[id].Client.Overflowed);
        }

        Assert.True(h.Friends[202].Snapshots.Count < h.Friends[201].Snapshots.Count - 250, "the cut lost the realtime snapshots");
        Assert.True(h.Stream.Find(202)!.Streaming);
        Assert.Equal(FriendSessionState.Joined, h.Friends[202].Session.State);
    }

    [Fact]
    public void Achievements_reach_every_joined_friend_and_a_returning_one_gets_what_it_missed()
    {
        var h = new Harness(seed: 2);
        foreach (ulong id in FriendIds)
        {
            h.Link(id, latencyMs: 15, jitterMs: 0, loss: 0);
        }

        h.JoinAll();
        h.Earn("GOAL_FIRST");
        h.Run(100 * Ms);
        Assert.All(FriendIds, id => Assert.Equal(new[] { "GOAL_FIRST" }, h.Friends[id].Achievements));

        // Friend 203 drops long enough to be timed out; meanwhile the host earns another.
        h.Links[203].Cut = true;
        h.Run(SessionTiming.GoneAfterMicros + (500 * Ms));
        Assert.Null(h.Host.Find(203));
        h.Earn("HAT_TRICK");
        h.Earn("HAT_TRICK"); // earned twice: sent once
        h.Run(100 * Ms);
        Assert.Equal(new[] { "GOAL_FIRST", "HAT_TRICK" }, h.Friends[201].Achievements);
        Assert.Equal(new[] { "GOAL_FIRST" }, h.Friends[203].Achievements);

        // It comes back with a new session: it gets everything it's owed (its seat was kept, so the run was its too).
        h.Links[203].Cut = false;
        h.Rejoin(203);
        h.Run(100 * Ms);
        Assert.Equal(new[] { "GOAL_FIRST", "GOAL_FIRST", "HAT_TRICK" }, h.Friends[203].Achievements);

        // A friend joining for the first time gets nothing from before.
        Assert.Empty(h.Ledger.OwedTo(999));
    }

    [Fact]
    public void Each_friend_s_playout_report_reaches_the_host_as_its_own()
    {
        var h = new Harness(seed: 3);
        foreach (ulong id in FriendIds)
        {
            h.Link(id, latencyMs: 20, jitterMs: 0, loss: 0);
        }

        h.JoinAll();
        h.StartMatch();
        h.PlaySnapshots(30);
        foreach (ulong id in FriendIds)
        {
            h.Friends[id].Client.SendReport(new StreamReport((uint)id * 100, 30_000, 4_000, 60, 2, 1, 60, 0));
        }

        h.Run(100 * Ms);
        foreach (ulong id in FriendIds)
        {
            StreamFriend friend = h.Stream.Find(id)!;
            Assert.Equal((uint)id * 100, friend.Report.RenderBehindMicros);
            Assert.True(friend.ReportMicros > 0);
        }

        // A new match forgets the old reports.
        h.Stream.BeginMatch(new[] { new KeyValuePair<string, NetValue>("match.id", NetValue.FromInt(2)) });
        Assert.All(FriendIds, id => Assert.Equal(long.MinValue, h.Stream.Find(id)!.ReportMicros));
    }

    [Fact]
    public void Progress_and_report_messages_round_trip_and_reject_garbage()
    {
        var writer = new BitWriter();
        AchievementUnlockedMessage.Write(writer, new AchievementMoment("ACH_HAT_TRICK", 12, true));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(AchievementUnlockedMessage.TryRead(ref reader, out AchievementMoment moment));
        Assert.Equal(new AchievementMoment("ACH_HAT_TRICK", 12, true), moment);

        foreach (string bad in new[] { string.Empty, " LEADING", "TRAILING ", "TAB\tHERE", "ÉTÉ" })
        {
            writer = new BitWriter();
            writer.WriteShortString(bad, AchievementUnlockedMessage.MaxNameBytes);
            writer.WriteUInt32(1);
            writer.WriteByte(0);
            reader = new BitReader(writer.AsSpan());
            Assert.False(AchievementUnlockedMessage.TryRead(ref reader, out _), bad);
        }

        // A match number past int.MaxValue, a flag this version doesn't know, and a message cut short are refused.
        foreach ((uint match, byte flags, bool cut) in new[] { (0x8000_0000u, (byte)0, false), (1u, (byte)0x02, false), (1u, (byte)0, true) })
        {
            writer = new BitWriter();
            writer.WriteShortString("MERCY", AchievementUnlockedMessage.MaxNameBytes);
            writer.WriteUInt32(match);
            if (!cut)
            {
                writer.WriteByte(flags);
            }

            reader = new BitReader(writer.AsSpan());
            Assert.False(AchievementUnlockedMessage.TryRead(ref reader, out _), $"{match} {flags} {cut}");
        }

        var report = new StreamReport(14_600, 31_800, 4_100, 144, 3, 1, 60, 0);
        writer = new BitWriter();
        StreamReportMessage.Write(writer, report);
        reader = new BitReader(writer.AsSpan());
        Assert.True(StreamReportMessage.TryRead(ref reader, out StreamReport read));
        Assert.Equal(report, read);

        // More held frames than frames, a value past 32 bits, a truncated message: all rejected, none thrown.
        writer = new BitWriter();
        StreamReportMessage.Write(writer, report with { HeldFrames = 200 });
        reader = new BitReader(writer.AsSpan());
        Assert.False(StreamReportMessage.TryRead(ref reader, out _));
        writer = new BitWriter();
        writer.WriteVarUInt(ulong.MaxValue);
        reader = new BitReader(writer.AsSpan());
        Assert.False(StreamReportMessage.TryRead(ref reader, out _));
        reader = new BitReader(new byte[] { 0x80 });
        Assert.False(StreamReportMessage.TryRead(ref reader, out _));
    }

    private static long HudClock(TimedMessage message)
    {
        var values = new List<KeyValuePair<string, NetValue>>();
        var reader = new BitReader(message.Payload);
        Assert.Equal(MessageType.HudState, message.Type);
        Assert.True(HudStateMessage.TryRead(ref reader, values));
        return (long)values.Single(p => p.Key == "clock").Value.Number;
    }

    private sealed class Friend
    {
        public Friend(FriendSession session, MatchStreamClient client)
        {
            Session = session;
            Client = client;
        }

        public FriendSession Session { get; set; }

        public MatchStreamClient Client { get; }

        public List<ArrivedSnapshot> Snapshots { get; } = new();

        public List<TimedMessage> Messages { get; } = new();

        public List<string> Achievements { get; } = new();
    }

    /// <summary>A host with three friends, wired as the plugin wires them (join → stream friend, achievements through the ledger).</summary>
    private sealed class Harness
    {
        private readonly ulong[] _sequences = new ulong[256];
        private SnapshotFrame? _frame;
        private uint _sequence;

        public Harness(int seed)
        {
            Net = new FakeNetwork(seed);
            Host = new HostSession(Identity, Net.For(HostId));
            Host.SetMembers(FriendIds.Prepend(HostId));
            Stream = new MatchStreamHost(Net.For(HostId));
            Host.StreamMessages = Stream.Receive;
            foreach (ulong id in FriendIds)
            {
                var client = new MatchStreamClient(Net.For(id), HostId);
                Friends[id] = new Friend(NewSession(id, client), client);
            }
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        public MatchStreamHost Stream { get; }

        public AchievementLedger Ledger { get; } = new();

        public Dictionary<ulong, Friend> Friends { get; } = new();

        public Dictionary<ulong, FakeNetwork.LinkProfile> Links => Net.Links;

        public void Link(ulong id, long latencyMs, long jitterMs, double loss) =>
            Net.Links[id] = new FakeNetwork.LinkProfile { Latency = latencyMs * Ms, Jitter = jitterMs * Ms, Loss = loss };

        public void JoinAll()
        {
            foreach (Friend friend in Friends.Values)
            {
                friend.Session.Start(Net.Now);
            }

            for (int i = 0; i < 2000 && Friends.Values.Any(f => f.Session.State != FriendSessionState.Joined); i++)
            {
                Step(Ms);
            }

            Assert.All(Friends.Values, f => Assert.Equal(FriendSessionState.Joined, f.Session.State));
        }

        public void Rejoin(ulong id)
        {
            Friend friend = Friends[id];
            friend.Session = NewSession(id, friend.Client);
            friend.Session.Start(Net.Now);
            for (int i = 0; i < 2000 && friend.Session.State != FriendSessionState.Joined; i++)
            {
                Step(Ms);
            }

            Assert.Equal(FriendSessionState.Joined, friend.Session.State);
        }

        public void StartMatch()
        {
            Stream.BeginMatch(new[] { new KeyValuePair<string, NetValue>("match.id", NetValue.FromInt(1)) });
            Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(1, 77, "P:body", "Puck") }), 0);
            Run(200 * Ms);
            foreach (Friend friend in Friends.Values)
            {
                Assert.True(friend.Client.SendReady(friend.Client.SetupHash, true));
            }

            Run(200 * Ms);
            Assert.All(FriendIds, id => Assert.True(Stream.Find(id)!.Streaming));
        }

        /// <summary>The host earns a run moment of the shared run: every seat shares it, a dropped friend's included.</summary>
        public void Earn(string name)
        {
            MomentShares shares = Ledger.Record(name, MomentSource.Run, match: 0, runShared: true, FriendIds);
            foreach (ulong id in shares.NewlyOwed)
            {
                if (Host.Find(id) is { IsJoined: true })
                {
                    Host.SendAchievement(id, new AchievementMoment(name, 0, true));
                }
            }
        }

        public void PlaySnapshots(int count, int hudEvery = 0)
        {
            for (int i = 0; i < count; i++)
            {
                _sequence++;
                _frame = _frame == null ? First() : Next(_frame, _sequence);
                Stream.Snapshot(_frame);
                if (hudEvery > 0 && _sequence % hudEvery == 0)
                {
                    Write(MessageType.HudState, Channel.Events, w => HudStateMessage.Write(w, new[] { new KeyValuePair<string, NetValue>("clock", NetValue.FromFloat(_sequence)) }), _frame.HostTimeMicros);
                }

                Step(16_667);
            }

            Run(300 * Ms);
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Step(Ms);
            }
        }

        private void Write(MessageType type, Channel channel, Action<BitWriter> payload, long time)
        {
            var writer = new BitWriter();
            new MessageHeader(type, _sequences[(byte)type]++, 0, MatchStreamHost.StreamTime(time)).Write(writer);
            payload(writer);
            Stream.Message(type, channel, writer.AsSpan());
        }

        private FriendSession NewSession(ulong id, MatchStreamClient client) =>
            new(Identity, HostId, InputKind.Gamepad, 7, Net.For(id)) { StreamMessages = client.Receive };

        private void Step(long duration)
        {
            Net.Now += duration;
            Net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    Host.Receive(from, channel, bytes, Net.Now);
                }
                else
                {
                    Friends[to].Session.Receive(from, channel, bytes, Net.Now);
                }
            });
            Host.Update(Net.Now);
            while (Host.TryTakeEvent(out HostEvent e))
            {
                switch (e.Kind)
                {
                    case HostEventKind.JoinRequested:
                        Host.Accept(e.SteamId, (byte)(e.SteamId - 200), Net.Now);
                        break;
                    case HostEventKind.Joined or HostEventKind.Rejoined:
                        Stream.AddFriend(e.SteamId);
                        foreach (AchievementMoment owed in Ledger.OwedTo(e.SteamId))
                        {
                            Host.SendAchievement(e.SteamId, owed);
                        }

                        break;
                    case HostEventKind.Left:
                        Stream.RemoveFriend(e.SteamId);
                        break;
                }
            }

            foreach (Friend friend in Friends.Values)
            {
                friend.Session.Update(Net.Now);
                friend.Client.Update(Net.Now);
                while (friend.Session.TryTakeEvent(out FriendEvent e))
                {
                    if (e.Kind == FriendEventKind.AchievementEarned)
                    {
                        friend.Achievements.Add(e.Achievement);
                    }
                }

                while (friend.Client.TryTakeSnapshot(out ArrivedSnapshot snapshot))
                {
                    friend.Snapshots.Add(snapshot);
                }

                while (friend.Client.TryTakeMessage(out TimedMessage message))
                {
                    friend.Messages.Add(message);
                }
            }
        }

        private static SnapshotFrame First()
        {
            var random = new Random(3);
            var frame = new SnapshotFrame { Sequence = 1, HostTimeMicros = 16_667, TimeScale = 1 };
            for (int i = 0; i < 30; i++)
            {
                var state = new byte[80];
                random.NextBytes(state);
                frame.Objects[(ushort)(i + 1)] = state;
            }

            return frame;
        }

        private static SnapshotFrame Next(SnapshotFrame previous, uint sequence)
        {
            SnapshotFrame next = TestFrames.Copy(previous);
            next.Sequence = sequence;
            next.HostTimeMicros = sequence * 16_667L;
            foreach (ushort id in next.Objects.Keys.Take(10).ToList())
            {
                byte[] state = (byte[])next.Objects[id].Clone();
                state[(int)(sequence % 70)] ^= 0x11;
                next.Objects[id] = state;
            }

            return next;
        }
    }
}
