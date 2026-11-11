using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Snapshots;

/// <summary>A recording read back and played through a simulated link, as F4 and F2 do.</summary>
public class StreamSourceTests
{
    private const long Interval = 16_667;

    [Fact]
    public void A_recording_reads_back_with_its_setup_tables_and_messages()
    {
        MatchRecording stream = Load(Record(300));
        Assert.Equal(300, stream.Snapshots.Count);
        Assert.Equal(2, stream.Objects.Count);
        Assert.True(stream.HasComponentIds);
        Assert.Equal("team_a", stream.Setup.Single(p => p.Key == "team.home").Value.Text);
        Assert.Equal(5, stream.Messages.Count);
        Assert.Equal(0, stream.Malformed + stream.MissingBase);
        Assert.InRange(stream.Seconds, 4.9, 5.0);
    }

    [Fact]
    public void Without_a_link_every_snapshot_arrives_in_order_decoded_exactly()
    {
        List<SnapshotFrame> recorded = Frames(300);
        var source = new FileStreamSource(Load(Record(300)), new SimulatedLink(0, 0, 0));
        var snapshots = new List<ArrivedSnapshot>();
        var messages = new List<TimedMessage>();
        for (long now = 0; !source.Finished; now += 5_000)
        {
            source.Poll(now, snapshots, messages);
        }

        Assert.Equal(recorded.Select(f => f.Sequence), snapshots.Select(s => s.Frame.Sequence));
        for (int i = 0; i < recorded.Count; i++)
        {
            Assert.Equal(recorded[i].Objects.Keys, snapshots[i].Frame.Objects.Keys);
            Assert.All(recorded[i].Objects, pair => Assert.Equal(pair.Value, snapshots[i].Frame.Objects[pair.Key]));
            Assert.Equal(recorded[i].HostTimeMicros - recorded[0].HostTimeMicros, snapshots[i].ArrivalMicros);
        }

        Assert.Equal(5, messages.Count);
        Assert.Equal(0, source.DecodeFailures);
    }

    [Fact]
    public void A_lossy_jittery_link_loses_only_snapshots_and_never_reorders_messages()
    {
        var source = new FileStreamSource(Load(Record(600)), new SimulatedLink(40, 30, 20));
        var snapshots = new List<ArrivedSnapshot>();
        var messages = new List<TimedMessage>();
        for (long now = 0; !source.Finished; now += 7_000)
        {
            source.Poll(now, snapshots, messages);
            Assert.All(snapshots, s => Assert.True(s.ArrivalMicros <= now));
        }

        Assert.Equal(600 - source.Lost, snapshots.Count);
        Assert.InRange(source.Lost, 80, 160);
        Assert.Equal(0, source.DecodeFailures);
        Assert.Equal(messages.OrderBy(m => m.TimeMicros).Select(m => m.TimeMicros), messages.Select(m => m.TimeMicros));
        Assert.Equal(10, messages.Count);
        // Jitter reorders arrivals but every snapshot is still the one recorded.
        Assert.Equal(snapshots.Count, snapshots.Select(s => s.Frame.Sequence).Distinct().Count());
    }

    [Fact]
    public void Old_recordings_and_garbage_are_refused_with_a_reason()
    {
        Assert.False(MatchRecording.TryRead(new MemoryStream(new byte[] { 1, 2, 3, 4 }), out MatchRecording? stream, out string problem));
        Assert.NotEmpty(problem);
        Assert.Null(stream);
        Assert.False(MatchRecording.TryRead(new MemoryStream(Record(1)), out stream, out problem));
        Assert.Contains("fewer than two", problem);
        Assert.Null(stream);
        Assert.Throws<ArgumentException>(() => new FileStreamSource(RecordingWithoutSnapshots(), new SimulatedLink(0, 0, 0)));
    }

    private static MatchRecording RecordingWithoutSnapshots()
    {
        MatchRecording stream = Load(Record(2));
        stream.Snapshots.Clear();
        return stream;
    }

    private static MatchRecording Load(byte[] bytes)
    {
        Assert.True(MatchRecording.TryRead(new MemoryStream(bytes), out MatchRecording? stream, out string problem), problem);
        return stream!;
    }

    /// <summary>A recording as the recorder writes it: setup, object table, keyframes every 2 s with deltas between, HUD each second.</summary>
    private static byte[] Record(int count)
    {
        var output = new MemoryStream();
        var writer = new MatchStreamWriter(output, new MatchStreamHeader(ProtocolInfo.Version, "test", 1, 0, "test"));
        var buffer = new BitWriter();
        void Write(long time, MessageType type, Channel channel, Action<BitWriter> payload)
        {
            buffer.Reset();
            new MessageHeader(type, 0, 0, MatchStreamHost.StreamTime(time)).Write(buffer);
            payload(buffer);
            writer.Write(time, channel, buffer.AsSpan());
        }

        var setup = new List<KeyValuePair<string, NetValue>> { new("team.home", NetValue.FromString("team_a")) };
        Write(0, MessageType.MatchSetup, Channel.Events, w => MatchSetupMessage.Write(w, MatchSetupMessage.Hash(setup), setup));
        Write(0, MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, new[]
        {
            new ObjectEntry(1, 11, "P:body", "Puck", new ushort[] { 5, 6 }),
            new ObjectEntry(2, 12, "F:H:x", "Skater", new ushort[] { 7 }),
        }));
        SnapshotFrame? previous = null;
        long lastKeyframe = long.MinValue;
        foreach (SnapshotFrame frame in Frames(count))
        {
            bool keyframe = previous == null || frame.HostTimeMicros - lastKeyframe >= 2_000_000;
            lastKeyframe = keyframe ? frame.HostTimeMicros : lastKeyframe;
            SnapshotFrame? baseFrame = keyframe ? null : previous;
            Write(frame.HostTimeMicros, MessageType.Snapshot, Channel.Realtime, w => SnapshotCodec.Write(w, frame, baseFrame));
            if (frame.Sequence % 60 == 0)
            {
                Write(frame.HostTimeMicros, MessageType.HudState, Channel.Events, w => HudStateMessage.Write(w, new[] { new KeyValuePair<string, NetValue>("clock", NetValue.FromInt(frame.Sequence)) }));
            }

            previous = frame;
        }

        writer.Dispose();
        return output.ToArray();
    }

    private static List<SnapshotFrame> Frames(int count)
    {
        var random = new Random(4);
        var frames = new List<SnapshotFrame>();
        var state = new Dictionary<ushort, byte[]> { [1] = new byte[60], [2] = new byte[90] };
        random.NextBytes(state[1]);
        random.NextBytes(state[2]);
        for (uint i = 1; i <= count; i++)
        {
            var frame = new SnapshotFrame { Sequence = i, HostTimeMicros = 1_000_000 + (i * Interval), TimeScale = 1 };
            foreach (ushort id in state.Keys.ToList())
            {
                byte[] next = (byte[])state[id].Clone();
                next[(int)(i % (uint)next.Length)] ^= 0x5A;
                state[id] = next;
                frame.Objects[id] = next;
            }

            frames.Add(frame);
        }

        return frames;
    }
}
