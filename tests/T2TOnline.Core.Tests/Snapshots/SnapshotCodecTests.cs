using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Tests.Snapshots;

public class SnapshotCodecTests
{
    [Fact]
    public void A_keyframe_round_trips()
    {
        SnapshotFrame frame = Frame(1, objects: 40, seed: 1);
        frame.Camera = new CameraPose(1, 2, 3, 0, 0, 0.7071f, 0.7071f, 35, false);
        frame.GroupScales.Add((2, 0.25f));

        SnapshotFrame decoded = RoundTrip(frame, null, new SnapshotHistory());

        AssertSame(frame, decoded);
        Assert.Equal(frame.Camera, decoded.Camera);
        Assert.Equal(frame.GroupScales, decoded.GroupScales);
    }

    [Fact]
    public void A_delta_carries_changes_additions_and_removals()
    {
        SnapshotFrame first = Frame(1, objects: 30, seed: 2);
        SnapshotFrame second = TestFrames.Copy(first);
        second.Sequence = 2;
        second.HostTimeMicros = 20_000;
        byte[] changed = (byte[])second.Objects[6].Clone();
        changed[3] ^= 0xFF;
        changed[^1] ^= 0x10;
        second.Objects[6] = changed;
        second.Objects[9] = new byte[] { 1, 2, 3 }; // a length change goes in full
        second.Objects.Remove(12);
        second.Objects[900] = new byte[] { 42 };

        var history = new SnapshotHistory();
        history.Add(RoundTrip(first, null, history));
        SnapshotFrame decoded = RoundTrip(second, first, history);

        AssertSame(second, decoded);
    }

    [Fact]
    public void A_delta_is_much_smaller_than_a_keyframe()
    {
        SnapshotFrame first = Frame(1, objects: 35, seed: 3, stateBytes: 150);
        SnapshotFrame second = TestFrames.Copy(first);
        second.Sequence = 2;

        // Twelve objects move a little, as skaters, goalies and the puck do between snapshots.
        foreach (ushort id in second.Objects.Keys.Take(12).ToList())
        {
            byte[] state = (byte[])second.Objects[id].Clone();
            state[10] ^= 0x01;
            state[11] ^= 0x22;
            state[40] ^= 0x05;
            second.Objects[id] = state;
        }

        int keyframe = Size(second, null);
        int delta = Size(second, first);

        Assert.True(delta * 10 < keyframe, $"delta {delta} B vs keyframe {keyframe} B");
    }

    [Fact]
    public void A_delta_without_its_base_asks_for_a_keyframe()
    {
        SnapshotFrame first = Frame(1, objects: 5, seed: 4);
        SnapshotFrame second = TestFrames.Copy(first);
        second.Sequence = 2;
        var writer = new BitWriter();
        SnapshotCodec.Write(writer, second, first);
        var reader = new BitReader(writer.AsSpan());

        Assert.False(SnapshotCodec.TryRead(ref reader, new SnapshotHistory(), out _, out bool missingBase));
        Assert.True(missingBase);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(5);
        var history = new SnapshotHistory();
        history.Add(Frame(1, objects: 10, seed: 5));
        var buffer = new byte[300];
        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(0, buffer.Length);
            random.NextBytes(buffer);
            var reader = new BitReader(buffer.AsSpan(0, length));
            SnapshotCodec.TryRead(ref reader, history, out _, out _);
        }
    }

    [Fact]
    public void A_compression_bomb_is_refused()
    {
        // A tiny deflate stream that inflates far past the limit.
        byte[] bomb = Compression.Deflate(new byte[SnapshotCodec.MaxBodyBytes * 4]);
        Assert.True(bomb.Length * 64 < SnapshotCodec.MaxBodyBytes * 4, $"{bomb.Length} B"); // expands well over 64 times
        Assert.False(Compression.TryInflate(bomb, SnapshotCodec.MaxBodyBytes, out _));
    }

    [Fact]
    public void Camera_poses_blend_along_the_short_arc()
    {
        var a = new CameraPose(0, 0, 0, 0, 0, 0, 1, 30, false);
        var b = new CameraPose(10, 0, 0, 0, 0, 0, -1, 50, false); // the same rotation, negated
        CameraPose mid = CameraPose.Lerp(a, b, 0.5f);

        Assert.Equal(5, mid.X, 3);
        Assert.Equal(40, mid.Lens, 3);
        Assert.Equal(1, Math.Abs(mid.QW), 3);
    }

    private static SnapshotFrame RoundTrip(SnapshotFrame frame, SnapshotFrame? baseFrame, SnapshotHistory history)
    {
        var writer = new BitWriter();
        SnapshotCodec.Write(writer, frame, baseFrame);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(SnapshotCodec.TryRead(ref reader, history, out SnapshotFrame decoded, out bool missingBase));
        Assert.False(missingBase);
        return decoded;
    }

    private static int Size(SnapshotFrame frame, SnapshotFrame? baseFrame)
    {
        var writer = new BitWriter();
        SnapshotCodec.Write(writer, frame, baseFrame);
        return writer.ByteLength;
    }

    private static void AssertSame(SnapshotFrame expected, SnapshotFrame actual)
    {
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.HostTimeMicros, actual.HostTimeMicros);
        Assert.Equal(expected.TimeScale, actual.TimeScale);
        Assert.Equal(expected.Objects.Keys, actual.Objects.Keys);
        foreach (KeyValuePair<ushort, byte[]> pair in expected.Objects)
        {
            Assert.Equal(pair.Value, actual.Objects[pair.Key]);
        }
    }

    private static SnapshotFrame Frame(uint sequence, int objects, int seed, int stateBytes = 60)
    {
        var random = new Random(seed);
        var frame = new SnapshotFrame { Sequence = sequence, HostTimeMicros = sequence * 19_000L, TimeScale = 1 };
        for (int i = 0; i < objects; i++)
        {
            var state = new byte[stateBytes];
            random.NextBytes(state);
            frame.Objects[(ushort)(i * 3)] = state;
        }

        return frame;
    }
}
