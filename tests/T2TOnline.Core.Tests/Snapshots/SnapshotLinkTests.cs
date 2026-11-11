using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Tests.Snapshots;

public class SnapshotLinkTests
{
    [Fact]
    public void The_first_snapshot_is_a_keyframe_and_acked_ones_become_delta_bases()
    {
        var history = new SnapshotHistory(128);
        var peer = new SnapshotPeer();
        SnapshotFrame first = Frame(1, seed: 1);
        history.Add(first);
        Assert.Equal(SnapshotPlan.Keyframe, peer.Plan(first, history, out SnapshotFrame? baseFrame));
        Assert.Null(baseFrame);

        peer.OnAck(1, needKeyframe: false);
        SnapshotFrame second = Next(first, 2);
        history.Add(second);
        Assert.Equal(SnapshotPlan.Delta, peer.Plan(second, history, out baseFrame));
        Assert.Same(first, baseFrame);
        Assert.Equal(1, peer.Keyframes);
        Assert.Equal(1, peer.Deltas);
    }

    [Fact]
    public void A_keyframe_request_or_a_stale_ack_gives_a_keyframe()
    {
        var history = new SnapshotHistory(128);
        var peer = new SnapshotPeer();
        SnapshotFrame frame = Frame(1, seed: 2);
        history.Add(frame);
        peer.OnAck(1, needKeyframe: true);
        SnapshotFrame next = Next(frame, 2);
        history.Add(next);
        Assert.Equal(SnapshotPlan.Keyframe, peer.Plan(next, history, out _));

        peer.OnAck(1, needKeyframe: false);
        SnapshotFrame far = Next(frame, 1 + SnapshotPeer.MaxBaseAge + 1);
        Assert.Equal(SnapshotPlan.Keyframe, peer.Plan(far, history, out _));
    }

    [Fact]
    public void Without_acks_keyframes_are_spaced_out_and_the_snapshots_between_held_until_one_is_acked()
    {
        var history = new SnapshotHistory(128);
        var peer = new SnapshotPeer();
        SnapshotFrame frame = Frame(1, seed: 6);
        var keyframes = new List<uint>();
        for (uint seq = 1; seq <= 30; seq++)
        {
            frame = seq == 1 ? frame : Next(frame, seq);
            history.Add(frame);
            if (peer.Plan(frame, history, out _) == SnapshotPlan.Keyframe)
            {
                keyframes.Add(seq);
            }
        }

        // A dead link: a keyframe every KeyframeSpacing snapshots, nothing between.
        Assert.Equal(new uint[] { 1, 7, 13, 19, 25 }, keyframes);
        Assert.Equal(25, peer.Held);
        Assert.Equal(0, peer.Deltas);

        // The friend acknowledges keyframe 25: deltas against it at once.
        peer.OnAck(25, needKeyframe: false);
        frame = Next(frame, 31);
        history.Add(frame);
        Assert.Equal(SnapshotPlan.Delta, peer.Plan(frame, history, out SnapshotFrame? baseFrame));
        Assert.Equal(25u, baseFrame!.Sequence);

        // A new match (or a rejoin) starts over with a keyframe straight away.
        peer.Reset();
        frame = Next(frame, 32);
        history.Add(frame);
        Assert.Equal(SnapshotPlan.Keyframe, peer.Plan(frame, history, out _));
    }

    [Fact]
    public void Older_acks_arriving_late_never_move_the_base_back()
    {
        var history = new SnapshotHistory(128);
        var peer = new SnapshotPeer();
        SnapshotFrame frame = Frame(10, seed: 7);
        history.Add(frame);
        peer.Plan(frame, history, out _);
        peer.OnAck(10, needKeyframe: false);
        peer.OnAck(7, needKeyframe: false);
        Assert.Equal(10u, peer.Acked);
    }

    [Fact]
    public void An_ack_for_a_snapshot_never_sent_is_ignored()
    {
        // Another match's acknowledgement, or a corrupt one: believing it would pin the delta base out of reach.
        var history = new SnapshotHistory(128);
        var peer = new SnapshotPeer();
        SnapshotFrame frame = Frame(5, seed: 8);
        history.Add(frame);
        Assert.Equal(SnapshotPlan.Keyframe, peer.Plan(frame, history, out _));
        peer.OnAck(13_000, needKeyframe: false);
        Assert.Equal(0u, peer.Acked);
        Assert.Equal(1, peer.BogusAcks);

        peer.OnAck(5, needKeyframe: false);
        SnapshotFrame next = Next(frame, 6);
        history.Add(next);
        Assert.Equal(SnapshotPlan.Delta, peer.Plan(next, history, out SnapshotFrame? baseFrame));
        Assert.Equal(5u, baseFrame!.Sequence);
    }

    [Fact]
    public void Over_a_lossy_reordering_link_every_snapshot_that_arrives_decodes()
    {
        // 60 Hz host, 25% loss, up to 3 snapshots of reordering, acks lost too.
        var random = new Random(11);
        var history = new SnapshotHistory(128);
        var peer = new SnapshotPeer();
        var receiver = new SnapshotReceiver();
        var inFlight = new List<(int Due, byte[] Bytes)>();
        SnapshotFrame frame = Frame(1, seed: 3);
        int arrived = 0;
        for (int tick = 1; tick <= 600; tick++)
        {
            if (tick > 1)
            {
                frame = Next(frame, (uint)tick);
            }

            history.Add(frame);
            if (peer.Plan(frame, history, out SnapshotFrame? baseFrame) != SnapshotPlan.Hold && random.Next(100) >= 25)
            {
                var writer = new BitWriter();
                SnapshotCodec.Write(writer, frame, baseFrame);
                inFlight.Add((tick + random.Next(0, 4), writer.AsSpan().ToArray()));
            }

            foreach ((int due, byte[] bytes) in inFlight.Where(m => m.Due <= tick).ToList())
            {
                inFlight.Remove((due, bytes));
                var reader = new BitReader(bytes);
                Assert.True(receiver.TryReceive(ref reader, out _), $"a snapshot failed to decode at tick {tick}");
                arrived++;
            }

            if (receiver.AckDue(tick * 16_667L) && random.Next(100) >= 25)
            {
                peer.OnAck(receiver.Newest, receiver.NeedsKeyframe);
            }
        }

        Assert.True(arrived > 400);
        Assert.Equal(0, receiver.MissingBase);
        Assert.True(peer.Deltas > peer.Keyframes * 10, $"{peer.Deltas} deltas, {peer.Keyframes} keyframes");
    }

    [Fact]
    public void A_missing_base_asks_for_a_keyframe_until_one_arrives()
    {
        var hostHistory = new SnapshotHistory(128);
        SnapshotFrame first = Frame(1, seed: 4);
        SnapshotFrame second = Next(first, 2);
        hostHistory.Add(first);
        var receiver = new SnapshotReceiver();

        var delta = new BitWriter();
        SnapshotCodec.Write(delta, second, first);
        var reader = new BitReader(delta.AsSpan());
        Assert.False(receiver.TryReceive(ref reader, out _));
        Assert.True(receiver.NeedsKeyframe);
        Assert.True(receiver.AckDue(0));

        var key = new BitWriter();
        SnapshotCodec.Write(key, Next(second, 3), null);
        reader = new BitReader(key.AsSpan());
        Assert.True(receiver.TryReceive(ref reader, out _));
        Assert.False(receiver.NeedsKeyframe);
        Assert.Equal(3u, receiver.Newest);
    }

    [Fact]
    public void Acks_are_rate_limited_and_only_sent_when_something_changed()
    {
        var receiver = new SnapshotReceiver();
        var key = new BitWriter();
        SnapshotCodec.Write(key, Frame(5, seed: 5), null);
        var reader = new BitReader(key.AsSpan());
        Assert.True(receiver.TryReceive(ref reader, out _));
        Assert.True(receiver.AckDue(1_000_000));
        Assert.False(receiver.AckDue(1_010_000));
        Assert.False(receiver.AckDue(2_000_000));
    }

    [Fact]
    public void The_new_messages_round_trip_and_reject_garbage()
    {
        var writer = new BitWriter();
        SnapshotAckMessage.Write(writer, 123_456, needKeyframe: true);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(SnapshotAckMessage.TryRead(ref reader, out uint newest, out bool need));
        Assert.Equal(123_456u, newest);
        Assert.True(need);

        var setup = new List<KeyValuePair<string, NetValue>>
        {
            new("team.home", NetValue.FromString("team_a")),
            new("rules.periods", NetValue.FromInt(3)),
        };
        ulong hash = MatchSetupMessage.Hash(setup);
        Assert.Equal(hash, MatchSetupMessage.Hash(setup.AsEnumerable().Reverse()));
        writer = new BitWriter();
        MatchSetupMessage.Write(writer, hash, setup);
        reader = new BitReader(writer.AsSpan());
        var read = new List<KeyValuePair<string, NetValue>>();
        Assert.True(MatchSetupMessage.TryRead(ref reader, out ulong readHash, read));
        Assert.Equal(hash, readHash);
        Assert.Equal(setup, read);
        Assert.NotEqual(hash, MatchSetupMessage.Hash(new[] { new KeyValuePair<string, NetValue>("team.home", NetValue.FromString("team_b")) }));

        writer = new BitWriter();
        MatchReadyMessage.Write(writer, hash, ready: true);
        reader = new BitReader(writer.AsSpan());
        Assert.True(MatchReadyMessage.TryRead(ref reader, out ulong readyHash, out bool ready, out string reason));
        Assert.Equal(hash, readyHash);
        Assert.True(ready);
        Assert.Empty(reason);

        writer = new BitWriter();
        MatchReadyMessage.Write(writer, hash, ready: false, "no such team here");
        reader = new BitReader(writer.AsSpan());
        Assert.True(MatchReadyMessage.TryRead(ref reader, out _, out ready, out reason));
        Assert.False(ready);
        Assert.Equal("no such team here", reason);

        writer = new BitWriter();
        ObjectTableMessage.Write(writer, new[] { new ObjectEntry(4, 77, "F:H:x", "Skater", new ushort[] { 9, 65_000 }) });
        reader = new BitReader(writer.AsSpan());
        var entries = new List<ObjectEntry>();
        Assert.True(ObjectTableMessage.TryRead(ref reader, entries));
        Assert.Equal(new ushort[] { 9, 65_000 }, entries[0].Components);

        // A huge count, then nothing: rejected, not thrown.
        writer = new BitWriter();
        writer.WriteVarUInt(1);
        writer.WriteVarUInt(4);
        writer.WriteVarInt(77);
        writer.WriteShortString("k");
        writer.WriteShortString("n");
        writer.WriteVarUInt(1_000_000);
        reader = new BitReader(writer.AsSpan());
        Assert.False(ObjectTableMessage.TryRead(ref reader, new List<ObjectEntry>()));

        reader = new BitReader(new byte[] { 0xFF, 0xFF });
        Assert.False(MatchSetupMessage.TryRead(ref reader, out _, new List<KeyValuePair<string, NetValue>>()));
    }

    private static SnapshotFrame Frame(uint sequence, int seed)
    {
        var random = new Random(seed);
        var frame = new SnapshotFrame { Sequence = sequence, HostTimeMicros = sequence * 16_667L, TimeScale = 1 };
        for (int i = 0; i < 30; i++)
        {
            var state = new byte[80];
            random.NextBytes(state);
            frame.Objects[(ushort)(i + 1)] = state;
        }

        return frame;
    }

    /// <summary>The next snapshot: a few objects move a little.</summary>
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
