using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Tests.Snapshots;

/// <summary>Hostile or corrupt input: each is rejected or survived, never thrown on.</summary>
public class HardeningTests
{
    [Fact]
    public void A_snapshot_with_a_non_finite_camera_scale_or_time_scale_is_rejected()
    {
        var camera = new CameraPose(1, 2, 3, 0, 0, 0, 1, 40, false);
        Assert.True(Decodes(RawKeyframe(0, 1, camera, (1, 0.5f))));
        Assert.False(Decodes(RawKeyframe(0, 1, camera with { X = float.NaN })));
        Assert.False(Decodes(RawKeyframe(0, 1, null, (1, float.PositiveInfinity))));
        Assert.False(Decodes(RawKeyframe(0, float.NaN, null)));
    }

    [Fact]
    public void A_snapshot_with_an_absurd_host_time_is_rejected()
    {
        Assert.False(Decodes(RawKeyframe(-5, 1, null)));
        Assert.False(Decodes(RawKeyframe(SnapshotCodec.MaxHostTimeMicros + 1, 1, null)));
        Assert.True(Decodes(RawKeyframe(12_345, 1, null)));
        Assert.True(Decodes(RawKeyframe(SnapshotCodec.MaxHostTimeMicros, 1, null)));
    }

    [Fact]
    public void The_writer_never_writes_what_the_reader_rejects()
    {
        // What can be mended is: the bad camera and group scale left out, the time scale sent as 1.
        var frame = new SnapshotFrame { Sequence = 1, HostTimeMicros = 0, TimeScale = float.NaN, Camera = new CameraPose(float.NaN, 0, 0, 0, 0, 0, 1, 40, false) };
        frame.GroupScales.Add((1, float.PositiveInfinity));
        frame.GroupScales.Add((2, 0.5f));
        Assert.True(TryWrite(frame, null, out byte[] bytes));
        var reader = new BitReader(bytes);
        Assert.True(SnapshotCodec.TryRead(ref reader, new SnapshotHistory(), out SnapshotFrame decoded, out _));
        Assert.Null(decoded.Camera);
        Assert.Equal(1f, decoded.TimeScale);
        Assert.Equal(new[] { ((byte)2, 0.5f) }, decoded.GroupScales);

        // What can't, isn't written at all: a host time out of range, a body over the limit.
        Assert.False(TryWrite(new SnapshotFrame { Sequence = 1, HostTimeMicros = -1 }, null, out bytes));
        Assert.Empty(bytes);
        Assert.False(TryWrite(new SnapshotFrame { Sequence = 1, HostTimeMicros = SnapshotCodec.MaxHostTimeMicros + 1 }, null, out bytes));
        Assert.Empty(bytes);
        var huge = new SnapshotFrame { Sequence = 1 };
        var random = new Random(1);
        for (ushort id = 0; id < SnapshotFrame.MaxObjects; id++)
        {
            var state = new byte[300];
            random.NextBytes(state);
            huge.Objects[id] = state;
        }

        Assert.False(TryWrite(huge, null, out bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public void Whatever_the_writer_accepts_the_reader_decodes()
    {
        var random = new Random(17);
        float[] oddFloats = { 1, 0, -2.5f, 1e30f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon };
        long[] oddTimes = { 0, 1, 16_667, SnapshotCodec.MaxHostTimeMicros, SnapshotCodec.MaxHostTimeMicros + 1, -1, long.MinValue, long.MaxValue };
        SnapshotFrame? previous = null;
        int written = 0;
        for (uint sequence = 1; sequence <= 400; sequence++)
        {
            var frame = previous == null || random.Next(4) == 0 ? new SnapshotFrame() : TestFrames.Copy(previous);
            frame.Sequence = random.Next(20) == 0 ? (uint)random.Next() : sequence;
            frame.HostTimeMicros = random.Next(3) == 0 ? oddTimes[random.Next(oddTimes.Length)] : sequence * 16_667L;
            frame.TimeScale = oddFloats[random.Next(oddFloats.Length)];
            frame.GroupScales.Clear();
            for (int g = random.Next(4); g > 0; g--)
            {
                frame.GroupScales.Add(((byte)random.Next(256), oddFloats[random.Next(oddFloats.Length)]));
            }

            frame.Camera = random.Next(3) == 0 ? null : new CameraPose(
                oddFloats[random.Next(oddFloats.Length)], 1, 2, 0, 0, 0, 1, oddFloats[random.Next(oddFloats.Length)], random.Next(2) == 0);
            for (int change = random.Next(12); change > 0; change--)
            {
                var id = (ushort)random.Next(200);
                if (random.Next(5) == 0)
                {
                    frame.Objects.Remove(id);
                    continue;
                }

                var state = new byte[random.Next(0, 300)];
                random.NextBytes(state);
                frame.Objects[id] = state;
            }

            SnapshotFrame? baseFrame = previous != null && random.Next(3) > 0 ? previous : null;
            if (!TryWrite(frame, baseFrame, out byte[] bytes))
            {
                Assert.True(frame.HostTimeMicros is < 0 or > SnapshotCodec.MaxHostTimeMicros, "only an unsendable time is refused here");
                Assert.Empty(bytes);
                continue;
            }

            written++;
            var history = new SnapshotHistory();
            if (baseFrame != null)
            {
                history.Add(baseFrame); // the receiver has the base (which slot it lands in isn't the codec's business)
            }

            var reader = new BitReader(bytes);
            Assert.True(SnapshotCodec.TryRead(ref reader, history, out SnapshotFrame decoded, out bool missingBase), $"frame {sequence} didn't decode (missing base: {missingBase})");
            Assert.Equal(frame.Sequence, decoded.Sequence);
            Assert.Equal(frame.HostTimeMicros, decoded.HostTimeMicros);
            Assert.Equal(float.IsFinite(frame.TimeScale) ? frame.TimeScale : 1f, decoded.TimeScale);
            Assert.Equal(frame.GroupScales.Where(g => float.IsFinite(g.Scale)), decoded.GroupScales);
            Assert.Equal(frame.Camera is CameraPose pose && float.IsFinite(pose.X) && float.IsFinite(pose.Lens) ? frame.Camera : null, decoded.Camera);
            Assert.Equal(frame.Objects.Keys, decoded.Objects.Keys);
            Assert.All(frame.Objects, pair => Assert.Equal(pair.Value, decoded.Objects[pair.Key]));
            previous = decoded;
        }

        Assert.True(written > 250, $"{written} written");
    }

    [Fact]
    public void A_back_reference_past_32_bits_is_malformed_not_a_missing_base()
    {
        var body = new BitWriter();
        body.WriteVarUInt(5);                     // sequence
        body.WriteVarUInt((ulong)uint.MaxValue + 1); // back-reference no writer makes
        body.WriteVarInt(0);
        body.WriteSingle(1);
        body.WriteByte(0);
        body.WriteVarUInt(0);
        body.WriteVarUInt(0);
        var message = new BitWriter();
        message.WriteByte(0); // a delta: no flags
        message.WriteVarUInt((ulong)body.ByteLength);
        message.WriteBytes(body.AsSpan());
        var reader = new BitReader(message.AsSpan());

        Assert.False(SnapshotCodec.TryRead(ref reader, new SnapshotHistory(), out _, out bool missingBase));
        Assert.False(missingBase);
    }

    [Fact]
    public void An_overlong_varint_is_an_overrun_not_a_silent_truncation()
    {
        var bytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        var reader = new BitReader(bytes);
        reader.ReadVarUInt();
        Assert.True(reader.IsOverrun);

        var writer = new BitWriter();
        writer.WriteVarUInt(ulong.MaxValue);
        reader = new BitReader(writer.AsSpan());
        Assert.Equal(ulong.MaxValue, reader.ReadVarUInt());
        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void A_late_old_snapshot_never_evicts_a_newer_one_from_history()
    {
        var history = new SnapshotHistory(4);
        var newer = new SnapshotFrame { Sequence = 9 };
        history.Add(newer);
        history.Add(new SnapshotFrame { Sequence = 5 });
        Assert.True(history.TryGet(9, out SnapshotFrame? held));
        Assert.Same(newer, held);
    }

    [Fact]
    public void A_duplicate_among_equal_times_is_ignored()
    {
        var timeline = new SnapshotTimeline();
        var a = new SnapshotFrame { Sequence = 5, HostTimeMicros = 100 };
        Assert.True(timeline.Add(a));
        Assert.True(timeline.Add(new SnapshotFrame { Sequence = 6, HostTimeMicros = 100 }));
        Assert.False(timeline.Add(new SnapshotFrame { Sequence = 5, HostTimeMicros = 100 }));
        Assert.Equal(2, timeline.Count);
    }

    [Fact]
    public void One_snapshot_stamped_far_ahead_is_ignored_by_the_playout_clock()
    {
        var clock = new PlayoutClock();
        long render = 0;
        for (long t = 0; t < 4_000_000; t += 16_667)
        {
            clock.OnSnapshot(t, t + 30_000);
            render = clock.Advance(t + 30_000);
        }

        Assert.False(clock.OnSnapshot(60_000_000, 4_030_000));
        Assert.Equal(1, clock.Outliers);
        clock.OnSnapshot(4_000_000, 4_030_000);
        long after = clock.Advance(4_046_000);
        Assert.InRange(after, render, 4_000_000);
    }

    [Fact]
    public void Clock_sync_rejects_absurd_round_trips_and_never_throws()
    {
        var sync = new ClockSync();
        Assert.False(sync.AddSample(0, 0, 4_000_000_000_000_000_000));
        Assert.True(sync.AddSample(1_000, 50_000, 41_000));
        Assert.True(sync.AddSample(2_000, long.MaxValue / 2, 42_000));
        Assert.True(sync.HasEstimate);
    }

    [Fact]
    public void A_delta_can_not_grow_a_frame_past_what_a_keyframe_of_it_could_carry()
    {
        // XOR runs copy a whole state for a few bytes, so a frame's size isn't bounded by its message's.
        var history = new SnapshotHistory();
        SnapshotFrame start = RandomFrame(sequence: 1, firstId: 0, objects: 50, stateBytes: 4096);
        Assert.True(TryWrite(start, null, out byte[] keyframe));
        var reader = new BitReader(keyframe);
        Assert.True(SnapshotCodec.TryRead(ref reader, history, out SnapshotFrame held, out _));
        history.Add(held);

        // Up to the limit it's as ever.
        SnapshotFrame fits = RandomFrame(sequence: 2, firstId: 50, objects: 12, stateBytes: 4096);
        byte[] fitting = RawDelta(fits, baseSequence: 1);
        reader = new BitReader(fitting);
        Assert.True(SnapshotCodec.TryRead(ref reader, history, out SnapshotFrame decoded, out bool missing));
        Assert.False(missing);
        Assert.Equal(62, decoded.Objects.Count);
        Assert.True(decoded.Objects.Values.Sum(s => s.Length) <= SnapshotCodec.MaxFrameStateBytes);

        // One object over it, and the whole message is refused: malformed, not a base to ask a keyframe for.
        byte[] over = RawDelta(RandomFrame(sequence: 3, firstId: 50, objects: 20, stateBytes: 4096), baseSequence: 1);
        reader = new BitReader(over);
        Assert.False(SnapshotCodec.TryRead(ref reader, history, out _, out missing));
        Assert.False(missing);
    }

    [Fact]
    public void The_writer_refuses_a_delta_whose_frame_is_over_the_limit()
    {
        SnapshotFrame start = RandomFrame(sequence: 1, firstId: 0, objects: 50, stateBytes: 4096);
        SnapshotFrame next = TestFrames.Copy(start);
        next.Sequence = 2;
        foreach (KeyValuePair<ushort, byte[]> added in RandomFrame(sequence: 2, firstId: 50, objects: 20, stateBytes: 4096).Objects)
        {
            next.Objects[added.Key] = added.Value;
        }

        // Its delta alone is well under the body's limit; its whole frame isn't.
        Assert.False(TryWrite(next, start, out byte[] bytes));
        Assert.Empty(bytes);
    }

    private static SnapshotFrame RandomFrame(uint sequence, ushort firstId, int objects, int stateBytes)
    {
        var random = new Random((int)sequence + firstId);
        var frame = new SnapshotFrame { Sequence = sequence, HostTimeMicros = sequence * 16_667L };
        for (int i = 0; i < objects; i++)
        {
            var state = new byte[stateBytes];
            random.NextBytes(state);
            frame.Objects[(ushort)(firstId + i)] = state;
        }

        return frame;
    }

    /// <summary>A delta adding <paramref name="frame"/>'s objects to its base, built by hand as a hostile host could send it (the writer refuses an oversized one).</summary>
    private static byte[] RawDelta(SnapshotFrame frame, uint baseSequence)
    {
        var body = new BitWriter();
        body.WriteVarUInt(frame.Sequence);
        body.WriteVarUInt(frame.Sequence - baseSequence);
        body.WriteVarInt(frame.HostTimeMicros);
        body.WriteSingle(1f);
        body.WriteByte(0);
        body.WriteVarUInt(0);
        body.WriteVarUInt((ulong)frame.Objects.Count);
        int previous = -1;
        foreach ((ushort id, byte[] state) in frame.Objects)
        {
            body.WriteVarUInt((ulong)(uint)(id - previous - 1) << 1);
            previous = id;
            body.WriteVarUInt((ulong)state.Length);
            body.WriteBytes(state);
        }

        var message = new BitWriter();
        message.WriteByte(0);
        message.WriteVarUInt((ulong)body.ByteLength);
        message.WriteBytes(body.AsSpan());
        return message.ToArray();
    }

    private static bool Decodes(byte[] message)
    {
        var reader = new BitReader(message);
        return SnapshotCodec.TryRead(ref reader, new SnapshotHistory(), out _, out _);
    }

    private static bool TryWrite(SnapshotFrame frame, SnapshotFrame? baseFrame, out byte[] bytes)
    {
        var writer = new BitWriter();
        bool written = SnapshotCodec.Write(writer, frame, baseFrame);
        bytes = writer.ToArray();
        return written;
    }

    /// <summary>
    /// A keyframe with no objects, built by hand as a hostile or broken host could send it (the codec's writer refuses
    /// or mends these values). Flags: 1 keyframe, 2 deflated (not used here), 4 camera.
    /// </summary>
    private static byte[] RawKeyframe(long hostTime, float timeScale, CameraPose? camera, params (byte Group, float Scale)[] groups)
    {
        var body = new BitWriter();
        body.WriteVarUInt(1);
        body.WriteVarInt(hostTime);
        body.WriteSingle(timeScale);
        body.WriteByte((byte)groups.Length);
        foreach ((byte group, float scale) in groups)
        {
            body.WriteByte(group);
            body.WriteSingle(scale);
        }

        if (camera is CameraPose pose)
        {
            foreach (float value in new[] { pose.X, pose.Y, pose.Z, pose.QX, pose.QY, pose.QZ, pose.QW, pose.Lens })
            {
                body.WriteSingle(value);
            }

            body.WriteByte(pose.Orthographic ? (byte)1 : (byte)0);
        }

        body.WriteVarUInt(0); // nothing removed
        body.WriteVarUInt(0); // nothing changed
        var message = new BitWriter();
        message.WriteByte((byte)(1 | (camera != null ? 4 : 0)));
        message.WriteVarUInt((ulong)body.ByteLength);
        message.WriteBytes(body.AsSpan());
        return message.ToArray();
    }
}
