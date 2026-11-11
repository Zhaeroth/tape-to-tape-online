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

/// <summary>The live match stream end to end: host fan-out, friend receiving, through real sessions over a fake network.</summary>
public class MatchStreamTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.4.0-m3", 0x1122334455667788);

    [Fact]
    public void A_friend_gets_the_setup_then_the_whole_stream_once_ready_over_a_lossy_link()
    {
        var h = new Harness(new FakeNetwork(seed: 5) { LatencyMicros = 30 * Ms, JitterMicros = 15 * Ms, RealtimeLossFraction = 0.2 });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(100 * Ms);
        Assert.Equal(1, h.Client.SetupVersion);
        Assert.Equal(h.Stream.SetupHash, h.Client.SetupHash);
        Assert.Equal("team_a", h.Client.Setup.Single(p => p.Key == "team.home").Value.Text);

        // Recorded before the friend was ready: kept for them.
        h.Write(MessageType.EventTable, Channel.Events, w => EventTableMessage.Write(w, new[] { new EventEntry(1, "GameplayEvents.GoalScored", new[] { "team" }) }));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(1, 77, "P:body", "Puck") }));
        Assert.True(h.Client.SendReady(h.Client.SetupHash, true));
        h.Run(100 * Ms);
        Assert.True(h.Stream.Find(FriendId)!.Streaming);
        Assert.True(h.Stream.TakeFullStateRequest());
        Assert.False(h.Stream.TakeFullStateRequest());
        Assert.Single(h.Client.Objects);
        Assert.Single(h.Client.Events);

        SnapshotFrame frame = Frame(1, 0);
        var arrived = new List<ArrivedSnapshot>();
        var messages = new List<TimedMessage>();
        for (uint seq = 1; seq <= 600; seq++)
        {
            frame = Next(frame, seq, seq * 16_667L);
            h.Stream.Snapshot(frame);
            if (seq % 60 == 0)
            {
                h.Write(MessageType.HudState, Channel.Events, w => HudStateMessage.Write(w, new[] { new KeyValuePair<string, NetValue>("clock", NetValue.FromFloat(seq)) }), frame.HostTimeMicros);
            }

            h.Step(16_667);
            while (h.Client.TryTakeSnapshot(out ArrivedSnapshot snapshot))
            {
                arrived.Add(snapshot);
            }

            while (h.Client.TryTakeMessage(out TimedMessage message))
            {
                messages.Add(message);
            }
        }

        h.Run(100 * Ms);
        while (h.Client.TryTakeMessage(out TimedMessage message))
        {
            messages.Add(message);
        }

        StreamFriend friend = h.Stream.Find(FriendId)!;
        Assert.InRange(arrived.Count, 420, 560);
        Assert.Equal(0, h.Client.Receiver.Malformed);
        Assert.True(friend.Peer.Deltas > friend.Peer.Keyframes * 10, $"{friend.Peer.Deltas} deltas, {friend.Peer.Keyframes} keyframes");
        Assert.All(arrived, a => Assert.True(a.ArrivalMicros >= a.Frame.HostTimeMicros));
        Assert.Equal(10, messages.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i * 60 * 16_667L / 1000 * 1000), messages.Select(m => m.TimeMicros));
        Assert.Equal(0, h.Host.MalformedFromMembers + h.Host.Find(FriendId)!.Link.Malformed);
        Assert.Equal(0, h.Friend.Link.Malformed);
    }

    [Fact]
    public void A_friend_who_loaded_another_match_gets_nothing_until_they_load_this_one()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        h.Stream.BeginMatch(Setup("team_b"));
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(10 * Ms); // the ready (for team_a's hash, sent before team_b's setup arrived) gets there first
        StreamFriend friend = h.Stream.Find(FriendId)!;
        h.Run(50 * Ms);
        Assert.False(friend.Streaming);

        Assert.Equal(2, h.Client.SetupVersion);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        Assert.True(friend.Streaming);
        Assert.False(friend.LoadedWrongMatch);
    }

    [Fact]
    public void A_late_friend_gets_the_tables_again_and_a_keyframe_first()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(1, 77, "P:body", "Puck") }));
        SnapshotFrame frame = Frame(1, 0);
        for (uint seq = 1; seq <= 120; seq++)
        {
            frame = Next(frame, seq, seq * 16_667L);
            h.Stream.Snapshot(frame);
            h.Step(16_667);
        }

        Assert.False(h.Client.TryTakeSnapshot(out _));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(2, 78, "P:mesh", "Mesh") }));
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        frame = Next(frame, 121, 121 * 16_667L);
        h.Stream.Snapshot(frame);
        h.Run(50 * Ms);

        Assert.Equal(2, h.Client.Objects.Count);
        Assert.True(h.Client.TryTakeSnapshot(out ArrivedSnapshot first));
        Assert.Equal(121u, first.Frame.Sequence);
        Assert.Equal(1, h.Stream.Find(FriendId)!.Peer.Keyframes);
    }

    [Fact]
    public void A_skater_announced_again_with_other_components_reaches_the_player_once_keeping_its_first_identity()
    {
        // The host's skater gained a component during play (a talent's effect): the host announces its entry again.
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        var skater = new ObjectEntry(1, 77, "F:H:x", "Skater", new ushort[] { 10, 11 }, new uint[] { 0xA, 0xB });
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { skater }));
        h.Run(50 * Ms);
        Assert.False(h.Client.TryTakeChangedObject(out _));

        // The same list again changes nothing; another one (and a crafted key and name with it) only changes the list.
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { skater }));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[]
        {
            skater with { Key = "F:V:y", Name = "Another", Components = new ushort[] { 10, 11, 12 }, ComponentTypes = new uint[] { 0xA, 0xB, 0xC } },
        }));
        h.Run(50 * Ms);

        Assert.True(h.Client.TryTakeChangedObject(out ushort changed));
        Assert.Equal(1, changed);
        Assert.False(h.Client.TryTakeChangedObject(out _));
        Assert.Equal(1, h.Client.ComponentChanges);
        ObjectEntry now = h.Client.Objects[1];
        Assert.Equal("F:H:x", now.Key);
        Assert.Equal("Skater", now.Name);
        Assert.Equal(new ushort[] { 10, 11, 12 }, now.Components);
        Assert.Equal(new uint[] { 0xA, 0xB, 0xC }, now.ComponentTypes);
    }

    [Fact]
    public void A_friend_who_starts_later_gets_a_skaters_changed_list_after_its_first()
    {
        // The host keeps every table it sent and sends them again in order to a friend who starts later.
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        var skater = new ObjectEntry(1, 77, "F:H:x", "Skater", new ushort[] { 10, 11, 12 }, new uint[] { 0xA, 0xB, 0xC });
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { skater }));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { skater with { Components = new ushort[] { 10, 11 }, ComponentTypes = new uint[] { 0xA, 0xB } } }));

        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);

        Assert.Equal(new ushort[] { 10, 11 }, h.Client.Objects[1].Components);
        Assert.True(h.Client.TryTakeChangedObject(out ushort changed));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Changed_objects_waiting_are_capped_and_forgotten_when_playback_stops()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        int count = MatchStreamClient.MaxChangedObjects + 5;
        ObjectEntry[] first = Enumerable.Range(1, count).Select(i => new ObjectEntry((ushort)i, i, $"F:H:{i}", "Skater", new ushort[] { 1 }, new uint[] { 0xA })).ToArray();
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, first));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, first.Select(e => e with { Components = new ushort[] { 1, 2 }, ComponentTypes = new uint[] { 0xA, 0xB } }).ToArray()));
        h.Run(50 * Ms);

        Assert.Equal(count, h.Client.ComponentChanges);
        int waiting = 0;
        while (h.Client.TryTakeChangedObject(out _))
        {
            waiting++;
        }

        Assert.Equal(MatchStreamClient.MaxChangedObjects, waiting);

        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { first[0] }));
        h.Run(50 * Ms);
        h.Client.DropQueued();
        Assert.False(h.Client.TryTakeChangedObject(out _));
    }

    [Fact]
    public void A_host_announcing_more_objects_and_events_than_a_match_can_use_fills_the_tables_only_to_their_caps()
    {
        var client = new MatchStreamClient(new FakeNetwork().For(FriendId), HostId);
        Assert.True(client.Receive(HostId, Channel.Events, SetupMessage(Setup("team_a")), 0));

        // 2,000 new objects, each with the largest key, name and component list: half are past the cap.
        string text = new('x', BitWriter.MaxShortStringBytes);
        ushort[] components = Enumerable.Range(1, ObjectEntry.MaxComponents).Select(i => (ushort)i).ToArray();
        uint[] types = Enumerable.Range(1, ObjectEntry.MaxComponents).Select(i => (uint)i).ToArray();
        var refused = new List<bool>();
        for (int first = 1; first <= 2000; first += ObjectTableMessage.MaxEntries)
        {
            ObjectEntry[] entries = Enumerable.Range(first, Math.Min(ObjectTableMessage.MaxEntries, 2001 - first))
                .Select(id => new ObjectEntry((ushort)id, id, text, text, components, types)).ToArray();
            refused.Add(!client.Receive(HostId, Channel.Events, Built(MessageType.ObjectTable, w => ObjectTableMessage.Write(w, entries)), 0));
        }

        Assert.Equal(new[] { false, false, true, true }, refused);
        Assert.Equal(MatchStreamClient.MaxObjects, client.Objects.Count);
        Assert.False(client.Objects.ContainsKey(MatchStreamClient.MaxObjects + 1));

        // An object already in the full table still takes a new component list.
        ObjectEntry known = client.Objects[5];
        Assert.True(client.Receive(HostId, Channel.Events, Built(MessageType.ObjectTable, w => ObjectTableMessage.Write(w, new[] { known with { Components = new ushort[] { 7 }, ComponentTypes = new uint[] { 9 } } })), 0));
        Assert.Equal(new ushort[] { 7 }, client.Objects[5].Components);

        // 2,000 events: the second message is all past the cap; the table sent again (a late start) is still taken.
        EventEntry[] events = Enumerable.Range(1, 2000).Select(id => new EventEntry((ushort)id, text, new[] { text, text })).ToArray();
        Assert.True(client.Receive(HostId, Channel.Events, Built(MessageType.EventTable, w => EventTableMessage.Write(w, events.Take(EventTableMessage.MaxEntries).ToArray())), 0));
        Assert.False(client.Receive(HostId, Channel.Events, Built(MessageType.EventTable, w => EventTableMessage.Write(w, events.Skip(EventTableMessage.MaxEntries).ToArray())), 0));
        Assert.True(client.Receive(HostId, Channel.Events, Built(MessageType.EventTable, w => EventTableMessage.Write(w, events.Take(10).ToArray())), 0));
        Assert.Equal(MatchStreamClient.MaxEvents, client.Events.Count);
        Assert.Equal(3, client.Malformed);

        // The next match starts with empty tables.
        Assert.True(client.Receive(HostId, Channel.Events, SetupMessage(Setup("team_b")), 0));
        Assert.Empty(client.Objects);
        Assert.Empty(client.Events);
    }

    [Fact]
    public void The_end_of_a_match_reaches_the_friend_and_a_new_setup_starts_clean()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(1, 77, "P:body", "Puck") }));
        h.Run(50 * Ms);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        Assert.Single(h.Client.Objects);

        h.Stream.EndMatch();
        h.Run(50 * Ms);
        Assert.True(h.Client.MatchEnded);
        Assert.False(h.Stream.InMatch);
        Assert.False(h.Stream.Find(FriendId)!.Streaming);

        h.Stream.BeginMatch(Setup("team_a", matchId: 2));
        h.Run(50 * Ms);
        Assert.False(h.Client.MatchEnded);
        Assert.Equal(2, h.Client.SetupVersion);
        Assert.Empty(h.Client.Objects);
    }

    [Fact]
    public void A_friend_who_drops_and_rejoins_keeps_the_match_and_resumes_from_a_keyframe()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(1, 77, "P:body", "Puck") }));
        h.Run(50 * Ms);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        SnapshotFrame frame = Frame(1, 0);
        for (uint seq = 1; seq <= 60; seq++)
        {
            frame = Next(frame, seq, seq * 16_667L);
            h.Stream.Snapshot(frame);
            h.Step(16_667);
        }

        // The host drops them (session timeout), then they're back: the same setup comes again.
        h.Stream.RemoveFriend(FriendId);
        h.Stream.AddFriend(FriendId);
        h.Run(50 * Ms);
        Assert.Equal(1, h.Client.SetupVersion);
        Assert.Single(h.Client.Objects);
        Assert.False(h.Stream.Find(FriendId)!.Streaming);

        // Still playing: they say so again, and the stream picks up with a keyframe.
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        while (h.Client.TryTakeSnapshot(out _))
        {
        }

        frame = Next(frame, 61, 61 * 16_667L);
        h.Stream.Snapshot(frame);
        h.Run(50 * Ms);
        Assert.True(h.Client.TryTakeSnapshot(out ArrivedSnapshot resumed));
        Assert.Equal(61u, resumed.Frame.Sequence);
        Assert.Equal(1, h.Stream.Find(FriendId)!.Peer.Keyframes);
    }

    [Fact]
    public void A_friend_who_can_t_load_says_why_and_stops_being_waited_for()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        Assert.True(h.Stream.Find(FriendId)!.Loading);

        h.Client.SendReady(h.Client.SetupHash, false, "the home team (team_a) isn't on this PC");
        h.Run(50 * Ms);
        StreamFriend friend = h.Stream.Find(FriendId)!;
        Assert.False(friend.Loading);
        Assert.Equal("the home team (team_a) isn't on this PC", friend.LoadFailure);

        // A new match: they're expected again.
        h.Stream.BeginMatch(Setup("team_b"));
        Assert.True(friend.Loading);
    }

    [Fact]
    public void A_friend_back_after_the_host_left_the_match_hears_there_is_none()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        h.Stream.RemoveFriend(FriendId);
        h.Stream.EndMatch();
        h.Run(50 * Ms);
        Assert.False(h.Client.MatchEnded);

        h.Stream.AddFriend(FriendId);
        h.Run(50 * Ms);
        Assert.True(h.Client.MatchEnded);
    }

    [Fact]
    public void Garbage_from_either_side_is_counted_never_thrown()
    {
        var random = new Random(9);
        var net = new FakeNetwork();
        var host = new MatchStreamHost(net.For(HostId));
        host.AddFriend(FriendId);
        host.BeginMatch(Setup("team_a"));
        var client = new MatchStreamClient(net.For(FriendId), HostId);
        foreach (MessageType type in new[] { MessageType.MatchSetup, MessageType.MatchEnd, MessageType.Snapshot, MessageType.ObjectTable, MessageType.EventTable, MessageType.HudState, MessageType.SnapshotAck, MessageType.MatchReady })
        {
            for (int i = 0; i < 200; i++)
            {
                var writer = new BitWriter();
                new MessageHeader(type, (ulong)i, 0, 0).Write(writer);
                var junk = new byte[random.Next(0, 64)];
                random.NextBytes(junk);
                foreach (byte b in junk)
                {
                    writer.WriteByte(b);
                }

                byte[] bytes = writer.AsSpan().ToArray();
                client.Receive(HostId, Channel.Realtime, bytes, 0);
                client.Receive(HostId, Channel.Events, bytes, 0);
                host.Receive(FriendId, Channel.Realtime, bytes, 0);
                host.Receive(FriendId, Channel.Events, bytes, 0);
            }
        }

        Assert.True(client.Malformed > 0);
        Assert.True(host.Malformed > 0);
        Assert.False(client.Receive(999, Channel.Events, new byte[] { 1, 2, 3 }, 0));
        Assert.False(host.Receive(999, Channel.Events, new byte[] { 1, 2, 3 }, 0));
    }

    [Fact]
    public void A_stream_nobody_plays_stays_bounded()
    {
        var net = new FakeNetwork();
        var host = new MatchStreamHost(net.For(HostId));
        var client = new MatchStreamClient(net.For(FriendId), HostId);
        host.AddFriend(FriendId);
        host.BeginMatch(Setup("team_a"));
        Pump(net, host, client);
        client.SendReady(client.SetupHash, true);
        Pump(net, host, client);
        SnapshotFrame frame = Frame(1, 0);
        for (uint seq = 1; seq <= MatchStreamClient.MaxQueuedSnapshots + 100; seq++)
        {
            frame = Next(frame, seq, seq * 16_667L);
            host.Snapshot(frame);
            Pump(net, host, client);
            client.Update(seq * 16_667L);
        }

        // Snapshot 2 is held back: the first keyframe's ack only gets to the host after it.
        long sent = host.Find(FriendId)!.SnapshotsSent;
        Assert.Equal(MatchStreamClient.MaxQueuedSnapshots + 99, sent);
        Assert.Equal(MatchStreamClient.MaxQueuedSnapshots, client.QueuedSnapshots);
        Assert.Equal(sent - MatchStreamClient.MaxQueuedSnapshots, client.Overflowed);
        Assert.True(client.TryTakeSnapshot(out ArrivedSnapshot oldest));
        Assert.Equal(101u, oldest.Frame.Sequence);
    }

    [Fact]
    public void A_friend_whose_session_restarted_gets_the_match_again_and_resumes_from_a_keyframe()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[] { new ObjectEntry(1, 77, "P:body", "Puck") }));
        h.Run(50 * Ms);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        SnapshotFrame frame = Frame(1, 0);
        for (uint seq = 1; seq <= 30; seq++)
        {
            frame = Next(frame, seq, seq * 16_667L);
            h.Stream.Snapshot(frame);
            h.Step(16_667);
        }

        StreamFriend friend = h.Stream.Find(FriendId)!;
        Assert.True(friend.Peer.Deltas > 0);

        // The friend's game restarted its session without the host noticing: a fresh session and stream state.
        h.RestartFriend();
        h.Run(50 * Ms);
        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
        Assert.True(h.Host.TryTakeEvent(out HostEvent rejoined));
        while (rejoined.Kind != HostEventKind.Rejoined && h.Host.TryTakeEvent(out rejoined))
        {
        }

        Assert.Equal(HostEventKind.Rejoined, rejoined.Kind);
        h.Stream.AddFriend(FriendId); // what the plugin does on Rejoined
        Assert.Same(friend, h.Stream.Find(FriendId));
        Assert.False(friend.Streaming);
        h.Run(50 * Ms);
        Assert.Equal((1, h.Stream.SetupHash), (h.Client.SetupVersion, h.Client.SetupHash));

        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        Assert.True(friend.Streaming);
        Assert.Single(h.Client.Objects); // the tables again
        long keyframes = friend.Peer.Keyframes;
        frame = Next(frame, 31, 31 * 16_667L);
        h.Stream.Snapshot(frame);
        h.Run(50 * Ms);
        Assert.True(h.Client.TryTakeSnapshot(out ArrivedSnapshot resumed));
        Assert.Equal(31u, resumed.Frame.Sequence);
        Assert.Equal(keyframes + 1, friend.Peer.Keyframes);
    }

    [Fact]
    public void A_previous_match_s_keyframe_arriving_after_the_next_setup_is_dropped()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(50 * Ms);
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);

        // A keyframe of the first match, held up on the way past the second match's setup.
        var stale = new BitWriter();
        new MessageHeader(MessageType.Snapshot, 0, 1, 0).Write(stale);
        Assert.True(SnapshotCodec.Write(stale, Frame(1, 16_667), null));
        h.Stream.BeginMatch(Setup("team_b"));
        h.Run(50 * Ms);
        Assert.Equal(2, h.Client.SetupVersion);
        long early = h.Client.EarlySnapshots;

        Assert.True(h.Client.Receive(HostId, Channel.Realtime, stale.AsSpan(), h.Net.Now));
        Assert.Equal(early + 1, h.Client.EarlySnapshots);
        Assert.False(h.Client.TryTakeSnapshot(out _));
        Assert.Equal(0u, h.Client.Receiver.Newest); // never a delta base, never acknowledged

        // Once this PC says it plays the second match, its keyframes decode.
        h.Client.SendReady(h.Client.SetupHash, true);
        h.Run(50 * Ms);
        h.Stream.Snapshot(Frame(1, 16_667));
        h.Run(50 * Ms);
        Assert.True(h.Client.TryTakeSnapshot(out _));
        Assert.Equal(early + 1, h.Client.EarlySnapshots);
    }

    [Fact]
    public void Every_stream_byte_to_a_friend_is_counted_and_oversize_realtime_sends_flagged()
    {
        var transport = new CountingTransport();
        var host = new MatchStreamHost(transport);
        host.AddFriend(FriendId);
        host.BeginMatch(Setup("team_a"));
        host.Message(MessageType.ObjectTable, Channel.Events, Message(MessageType.ObjectTable, 40));
        Assert.True(host.Receive(FriendId, Channel.Events, Ready(host.SetupHash), 0));
        SnapshotFrame keyframe = Frame(1, 16_667);
        host.Snapshot(keyframe); // 30 random states: about 2.4 KB, two or three packets
        Assert.True(host.Receive(FriendId, Channel.Realtime, Ack(1), 0));
        host.Snapshot(Next(keyframe, 2, 33_334)); // a small delta
        host.Message(MessageType.HudState, Channel.Events, Message(MessageType.HudState, 30));

        StreamFriend friend = host.Find(FriendId)!;
        Assert.Equal(6, transport.Sends); // match end, setup, table, keyframe, delta, HUD
        Assert.Equal(transport.Bytes, friend.BytesSent);
        Assert.Equal(1, friend.OversizeSends);
        Assert.Equal(2, friend.SnapshotsSent);
        Assert.Equal(0, friend.SendFailures);

        transport.Refuse = true;
        host.Message(MessageType.HudState, Channel.Events, Message(MessageType.HudState, 30));
        Assert.Equal(1, friend.SendFailures);
        Assert.Equal(transport.Bytes, friend.BytesSent);
    }

    [Fact]
    public void Friends_with_the_same_base_share_one_encoding()
    {
        var transport = new CountingTransport();
        var host = new MatchStreamHost(transport);
        const ulong Other = FriendId + 1;
        host.AddFriend(FriendId);
        host.AddFriend(Other);
        host.BeginMatch(Setup("team_a"));
        Assert.True(host.Receive(FriendId, Channel.Events, Ready(host.SetupHash), 0));
        Assert.True(host.Receive(Other, Channel.Events, Ready(host.SetupHash), 0));
        var receivers = new Dictionary<ulong, SnapshotReceiver> { [FriendId] = new(), [Other] = new() };
        transport.Sent = (peer, bytes) =>
        {
            var reader = new BitReader(bytes);
            Assert.True(MessageHeader.TryRead(ref reader, out MessageHeader header));
            if (header.Type == MessageType.Snapshot)
            {
                Assert.True(receivers[peer].TryReceive(ref reader, out _));
            }
        };

        // Both need a keyframe: one encoding.
        SnapshotFrame frame = Frame(1, 16_667);
        host.Snapshot(frame);
        Assert.Equal(1, host.SnapshotEncodings);

        // Both acknowledged 1: one delta for both.
        host.Receive(FriendId, Channel.Realtime, Ack(1), 0);
        host.Receive(Other, Channel.Realtime, Ack(1), 0);
        frame = Next(frame, 2, 33_334);
        host.Snapshot(frame);
        Assert.Equal(2, host.SnapshotEncodings);

        // Their bases part ways: one each, and each decodes against its own.
        host.Receive(FriendId, Channel.Realtime, Ack(2), 0);
        frame = Next(frame, 3, 50_001);
        host.Snapshot(frame);
        Assert.Equal(4, host.SnapshotEncodings);
        Assert.Equal(0, host.UnencodableSnapshots);
        Assert.All(receivers.Values, r => Assert.Equal((3u, 3L, 0L), (r.Newest, r.Decoded, r.MissingBase)));
    }

    [Fact]
    public void Queued_messages_are_capped_by_size_per_type_and_in_total()
    {
        var client = new MatchStreamClient(new FakeNetwork().For(FriendId), HostId);
        Assert.False(client.Receive(HostId, Channel.Events, Message(MessageType.HudState, MatchStreamClient.MaxHudStateBytes + 1), 0));
        Assert.False(client.Receive(HostId, Channel.Events, Message(MessageType.GameEvent, MatchStreamClient.MaxGameEventBytes + 1), 0));
        Assert.False(client.Receive(HostId, Channel.Events, Message(MessageType.SoundCall, MatchStreamClient.MaxSoundCallBytes + 1), 0));
        Assert.True(client.Receive(HostId, Channel.Events, Message(MessageType.SoundCall, MatchStreamClient.MaxSoundCallBytes), 0));
        Assert.Equal(3, client.Oversized);
        Assert.Equal(0, client.Malformed);

        // A host flooding the friend with the largest events it may send: the oldest go, the total stays bounded.
        for (ulong i = 1; i <= 400; i++)
        {
            Assert.True(client.Receive(HostId, Channel.Events, Message(MessageType.GameEvent, MatchStreamClient.MaxGameEventBytes, i), 0));
            Assert.InRange(client.QueuedBytes, 0, MatchStreamClient.MaxQueuedBytes);
        }

        Assert.True(client.Overflowed > 140);
        Assert.True(client.TryTakeMessage(out TimedMessage oldest));
        Assert.Equal(MessageType.GameEvent, oldest.Type);
        int taken = 1;
        while (client.TryTakeMessage(out _))
        {
            taken++;
        }

        Assert.Equal(401 - client.Overflowed, taken);
        Assert.Equal(0, client.QueuedBytes);
    }

    [Fact]
    public void A_rumble_reaches_the_friend_s_player_on_the_realtime_channel_and_an_oversized_one_is_refused()
    {
        var h = new Harness(new FakeNetwork(seed: 49) { LatencyMicros = 30 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(100 * Ms);
        Assert.True(h.Client.SendReady(h.Client.SetupHash, true));
        h.Run(100 * Ms);
        h.Write(MessageType.Rumble, Channel.Realtime, w => new RumbleMessage(1, 2).Write(w), 1_234_000);
        h.Run(100 * Ms);

        // Through the friend's session to the player's queue, with its stream time.
        Assert.True(h.Client.TryTakeMessage(out TimedMessage message));
        Assert.Equal((MessageType.Rumble, 1_234_000L), (message.Type, message.TimeMicros));
        var reader = new BitReader(message.Payload);
        Assert.True(RumbleMessage.TryRead(ref reader, out RumbleMessage rumble));
        Assert.Equal(new RumbleMessage(1, 2), rumble);
        Assert.Equal(0, h.Friend.Link.Malformed);

        Assert.False(h.Client.Receive(HostId, Channel.Realtime, Message(MessageType.Rumble, MatchStreamClient.MaxRumbleBytes + 1), 0));
        Assert.True(h.Client.Receive(HostId, Channel.Realtime, Message(MessageType.Rumble, MatchStreamClient.MaxRumbleBytes), 0));
        Assert.Equal(1, h.Client.Oversized);
    }

    [Fact]
    public void A_loop_s_start_stop_and_moves_reach_the_friend_s_player_in_order_and_an_oversized_one_is_refused()
    {
        var h = new Harness(new FakeNetwork(seed: 37) { LatencyMicros = 30 * Ms, JitterMicros = 5 * Ms });
        h.JoinNow();
        h.Stream.BeginMatch(Setup("team_a"));
        h.Run(100 * Ms);
        Assert.True(h.Client.SendReady(h.Client.SetupHash, true));
        h.Run(100 * Ms);
        h.Write(MessageType.SoundLoop, Channel.Events, SoundLoopMessage.Start(1, LoopKind.Magneto, 1, 2, 3).Write, 2_000_000);
        h.Run(20 * Ms);
        h.Write(MessageType.SoundLoop, Channel.Realtime, SoundLoopMessage.Move(1, 4, 2, 3).Write, 2_033_000);
        h.Run(20 * Ms);
        h.Write(MessageType.SoundLoop, Channel.Events, SoundLoopMessage.Stop(1).Write, 2_066_000);
        h.Run(100 * Ms);

        var loops = new List<SoundLoopMessage>();
        while (h.Client.TryTakeMessage(out TimedMessage message))
        {
            Assert.Equal(MessageType.SoundLoop, message.Type);
            var reader = new BitReader(message.Payload);
            Assert.True(SoundLoopMessage.TryRead(ref reader, out SoundLoopMessage loop));
            loops.Add(loop);
        }

        Assert.Equal(new[] { SoundLoopMessage.Start(1, LoopKind.Magneto, 1, 2, 3), SoundLoopMessage.Move(1, 4, 2, 3), SoundLoopMessage.Stop(1) }, loops);
        Assert.Equal(0, h.Friend.Link.Malformed);

        Assert.False(h.Client.Receive(HostId, Channel.Events, Message(MessageType.SoundLoop, MatchStreamClient.MaxSoundLoopBytes + 1), 0));
        Assert.True(h.Client.Receive(HostId, Channel.Events, Message(MessageType.SoundLoop, MatchStreamClient.MaxSoundLoopBytes), 0));
        Assert.Equal(1, h.Client.Oversized);
    }

    [Fact]
    public void Setup_values_given_twice_count_by_the_first_everywhere()
    {
        List<KeyValuePair<string, NetValue>> twice = Setup("team_a");
        twice.Add(new("team.home", NetValue.FromString("team_b")));
        Assert.Equal("team_a", MatchSetupValues.Get(twice, "team.home").Text);
        Assert.Equal(string.Empty, MatchSetupValues.Differences(twice, Setup("team_a"), Core.Diagnostics.HostText.TextShape));
        Assert.Contains("team.home", MatchSetupValues.Differences(twice, Setup("team_b"), Core.Diagnostics.HostText.TextShape));
    }

    [Fact]
    public void Setup_differences_name_what_differs_and_ignore_the_match_id()
    {
        List<KeyValuePair<string, NetValue>> host = Setup("team_a", matchId: 7);
        List<KeyValuePair<string, NetValue>> same = Setup("team_a", matchId: 1);
        Assert.Equal(string.Empty, MatchSetupValues.Differences(host, same, Core.Diagnostics.HostText.TextShape));

        List<KeyValuePair<string, NetValue>> other = Setup("team_b");
        other.RemoveAll(p => p.Key == "rules.periods");
        other.Add(new("rules.overtime", NetValue.FromBool(true)));
        string differences = MatchSetupValues.Differences(host, other, Core.Diagnostics.HostText.TextShape);
        Assert.Contains("team.home", differences);
        Assert.Contains("rules.periods missing here", differences);
        Assert.Contains("rules.overtime only here", differences);
        Assert.Equal("team_a", MatchSetupValues.Get(host, "team.home").Text);
        Assert.Equal(NetValueKind.Null, MatchSetupValues.Get(host, "nothing").Kind);
    }

    [Fact]
    public void The_delay_histogram_keeps_mean_and_percentiles_in_fixed_memory()
    {
        var histogram = new DelayHistogram();
        Assert.Equal(0, histogram.Percentile(0.95));
        for (int i = 1; i <= 1000; i++)
        {
            histogram.Add(i * 100);
        }

        histogram.Add(-50);
        histogram.Add(5_000_000);
        Assert.Equal(1002, histogram.Count);
        Assert.InRange(histogram.Percentile(0.95), 95_000, 95_250);
        Assert.InRange(histogram.Percentile(0.5), 50_000, 50_250);
        Assert.Equal(DelayHistogram.MaxMicros, histogram.Percentile(1));
        Assert.Equal(((1000 * 1001 / 2 * 100.0) + DelayHistogram.MaxMicros) / 1002, histogram.Mean, 3);
        histogram.Clear();
        Assert.Equal(0, histogram.Count);
    }

    [Fact]
    public void The_hosts_custom_content_reaches_a_joined_friend_through_its_session_with_the_setup_naming_it()
    {
        var h = new Harness(new FakeNetwork(seed: 9) { LatencyMicros = 40 * Ms, JitterMicros = 10 * Ms });
        h.JoinNow();
        var team = new CustomItem(CustomContentKind.Team, "team-1", System.Text.Encoding.UTF8.GetBytes("{\"Id\":\"team-1\",\"logoId\":\"custom_crest.png\"}"));
        var logo = new CustomItem(CustomContentKind.Logo, "custom_crest.png", CustomContentTests.Png(128, 128, extra: 300_000));
        var content = new CustomContentSender(h.Net.For(HostId));
        Assert.Equal(2, content.Send(FriendId, new[] { team, logo }));

        List<KeyValuePair<string, NetValue>> setup = Setup("team-1");
        CustomContentValues.Add((k, v) => setup.Add(new(k, v)), new[] { team.Ref, logo.Ref });
        h.Stream.BeginMatch(setup);
        h.Run(300 * Ms);

        Assert.True(CustomContentValues.TryRead(h.Client.Setup, out List<CustomRef> refs, out _));
        Assert.Equal(new[] { team.Ref, logo.Ref }, refs);
        Assert.All(refs, r => Assert.True(h.Client.Content.TryGet(r.Hash, out _)));
        Assert.True(h.Client.Content.TryGet(logo.Hash, out CustomItem? arrived));
        Assert.Equal(logo.Bytes, arrived!.Bytes);
        Assert.Equal(0, h.Client.Malformed);
        Assert.Equal(0, h.Friend.Link.Malformed);
    }

    private static void Pump(FakeNetwork net, MatchStreamHost host, MatchStreamClient client)
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
                client.Receive(from, channel, bytes, net.Now);
            }
        });
    }

    /// <summary>A stream message of <paramref name="type"/>, <paramref name="length"/> bytes in all, its payload zeros.</summary>
    private static byte[] Message(MessageType type, int length, ulong sequence = 0)
    {
        var writer = new BitWriter();
        new MessageHeader(type, sequence, 0, 0).Write(writer);
        writer.WriteBytes(new byte[length - writer.ByteLength]);
        return writer.ToArray();
    }

    /// <summary>A stream message of <paramref name="type"/> with the payload <paramref name="payload"/> writes.</summary>
    private static byte[] Built(MessageType type, Action<BitWriter> payload)
    {
        var writer = new BitWriter();
        new MessageHeader(type, 0, 0, 0).Write(writer);
        payload(writer);
        return writer.ToArray();
    }

    private static byte[] SetupMessage(List<KeyValuePair<string, NetValue>> setup) =>
        Built(MessageType.MatchSetup, w => MatchSetupMessage.Write(w, MatchSetupMessage.Hash(setup), setup));

    private static byte[] Ready(ulong setupHash)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.MatchReady, 0, 0, 0).Write(writer);
        MatchReadyMessage.Write(writer, setupHash, ready: true);
        return writer.ToArray();
    }

    private static byte[] Ack(uint newest)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.SnapshotAck, 0, 0, 0).Write(writer);
        SnapshotAckMessage.Write(writer, newest, needKeyframe: false);
        return writer.ToArray();
    }

    private static List<KeyValuePair<string, NetValue>> Setup(string home, int matchId = 1) => new()
    {
        new("match.id", NetValue.FromInt(matchId)),
        new("team.home", NetValue.FromString(home)),
        new("rules.periods", NetValue.FromInt(3)),
    };

    private static SnapshotFrame Frame(uint sequence, long time)
    {
        var random = new Random(3);
        var frame = new SnapshotFrame { Sequence = sequence, HostTimeMicros = time, TimeScale = 1 };
        for (int i = 0; i < 30; i++)
        {
            var state = new byte[80];
            random.NextBytes(state);
            frame.Objects[(ushort)(i + 1)] = state;
        }

        return frame;
    }

    private static SnapshotFrame Next(SnapshotFrame previous, uint sequence, long time)
    {
        SnapshotFrame next = TestFrames.Copy(previous);
        next.Sequence = sequence;
        next.HostTimeMicros = time;
        foreach (ushort id in next.Objects.Keys.Take(10).ToList())
        {
            byte[] state = (byte[])next.Objects[id].Clone();
            state[(int)(sequence % 70)] ^= 0x11;
            next.Objects[id] = state;
        }

        return next;
    }

    /// <summary>A transport that counts what it sends (or refuses everything), and can look at each message.</summary>
    private sealed class CountingTransport : ISessionTransport
    {
        public bool Refuse { get; set; }

        public int Sends { get; private set; }

        public long Bytes { get; private set; }

        public SentHandler? Sent { get; set; }

        public delegate void SentHandler(ulong peer, ReadOnlySpan<byte> message);

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (Refuse)
            {
                return false;
            }

            Sends++;
            Bytes += message.Length;
            Sent?.Invoke(peer, message);
            return true;
        }
    }

    /// <summary>A joined host and friend session with the stream wired through them, as the plugin does.</summary>
    private sealed class Harness
    {
        private readonly ulong[] _sequences = new ulong[256];

        public Harness(FakeNetwork net)
        {
            Net = net;
            Host = new HostSession(Identity, net.For(HostId));
            Host.SetMembers(new[] { HostId, FriendId });
            Stream = new MatchStreamHost(net.For(HostId));
            Host.StreamMessages = Stream.Receive;
            (Friend, Client) = NewFriend();
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        public FriendSession Friend { get; private set; }

        public MatchStreamHost Stream { get; }

        public MatchStreamClient Client { get; private set; }

        /// <summary>The friend's game starts its session and stream over (the host isn't told).</summary>
        public void RestartFriend()
        {
            (Friend, Client) = NewFriend();
            Friend.Start(Net.Now);
        }

        private (FriendSession Session, MatchStreamClient Client) NewFriend()
        {
            var client = new MatchStreamClient(Net.For(FriendId), HostId);
            return (new FriendSession(Identity, HostId, InputKind.Gamepad, 7, Net.For(FriendId)) { StreamMessages = client.Receive }, client);
        }

        public void JoinNow()
        {
            Friend.Start(Net.Now);
            for (int i = 0; i < 1000 && Friend.State == FriendSessionState.Connecting; i++)
            {
                Step(Ms);
                while (Host.TryTakeEvent(out HostEvent e))
                {
                    if (e.Kind == HostEventKind.JoinRequested)
                    {
                        Host.Accept(e.SteamId, 1, Net.Now);
                    }
                }
            }

            Assert.Equal(FriendSessionState.Joined, Friend.State);
            Stream.AddFriend(FriendId);
        }

        /// <summary>A recorder message: header (with the stream time) and payload, through the host's fan-out.</summary>
        public void Write(MessageType type, Channel channel, Action<BitWriter> payload, long time = 0)
        {
            var writer = new BitWriter();
            new MessageHeader(type, _sequences[(byte)type]++, 0, MatchStreamHost.StreamTime(time)).Write(writer);
            payload(writer);
            Stream.Message(type, channel, writer.AsSpan());
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Step(Ms);
            }
        }

        public void Step(long duration)
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
                    Friend.Receive(from, channel, bytes, Net.Now);
                }
            });
            Host.Update(Net.Now);
            Friend.Update(Net.Now);
            Client.Update(Net.Now);
        }
    }
}
