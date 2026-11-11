using System;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Input;

public class KeyboardMouseStateTests
{
    [Fact]
    public void Counters_live_in_two_words_and_wrap_at_four()
    {
        KeyboardMouseState state = KeyboardMouseState.Neutral.WithPress(0).WithPress(31).WithPress(32).WithPress(63);
        Assert.Equal(1, state.GetPressCounter(0));
        Assert.Equal(1, state.GetPressCounter(31));
        Assert.Equal(1, state.GetPressCounter(32));
        Assert.Equal(1, state.GetPressCounter(63));
        Assert.Equal(0, state.GetPressCounter(1));
        Assert.Equal(1UL << 62, state.CountersLow & (3UL << 62));
        Assert.Equal(1UL, state.CountersHigh & 3);

        for (int i = 0; i < 3; i++)
        {
            state = state.WithPress(5);
        }

        Assert.Equal(3, state.GetPressCounter(5));
        Assert.Equal(0, state.WithPress(5).GetPressCounter(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GetPressCounter(64));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WithPress(-1));
    }

    [Fact]
    public void Masked_keeps_only_the_tables_elements()
    {
        var state = new KeyboardMouseState(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
        KeyboardMouseState five = state.Masked(5);
        Assert.Equal(0b11111UL, five.Held);
        Assert.Equal((1UL << 10) - 1, five.CountersLow);
        Assert.Equal(0UL, five.CountersHigh);

        KeyboardMouseState forty = state.Masked(40);
        Assert.Equal((1UL << 40) - 1, forty.Held);
        Assert.Equal(ulong.MaxValue, forty.CountersLow);
        Assert.Equal((1UL << 16) - 1, forty.CountersHigh);
        Assert.Equal(state, state.Masked(64));
    }

    [Fact]
    public void Released_keeps_the_counters()
    {
        var state = new KeyboardMouseState(0b101, 7, 9);
        Assert.Equal(new KeyboardMouseState(0, 7, 9), state.Released());
        Assert.True(state.IsHeld(2));
        Assert.False(state.IsHeld(1));
        Assert.False(state.IsHeld(64));
    }
}

public class KeyboardMouseSamplerTests
{
    [Fact]
    public void A_press_counts_once_whether_seen_held_or_down()
    {
        var sampler = new KeyboardMouseSampler(8);
        sampler.Observe(held: 0b1, pressed: 0b1); // down this poll, and held
        sampler.Observe(held: 0b1, pressed: 0); // still held
        KeyboardMouseState state = sampler.Capture();
        Assert.Equal(1, state.GetPressCounter(0));
        Assert.True(state.IsHeld(0));

        sampler.Observe(held: 0, pressed: 0);
        sampler.Observe(held: 0b1, pressed: 0); // a press the input system missed as "down" still counts from held
        Assert.Equal(2, sampler.Capture().GetPressCounter(0));
    }

    [Fact]
    public void A_tap_between_polls_counts_without_being_held()
    {
        var sampler = new KeyboardMouseSampler(8);
        sampler.Observe(held: 0, pressed: 0b100);
        KeyboardMouseState state = sampler.Capture();
        Assert.False(state.IsHeld(2));
        Assert.Equal(1, state.GetPressCounter(2));
    }

    [Fact]
    public void Elements_outside_the_table_are_ignored_and_release_keeps_counters()
    {
        var sampler = new KeyboardMouseSampler(3);
        sampler.Observe(held: 0b1111_1111, pressed: 0b1111_1111);
        KeyboardMouseState state = sampler.Capture();
        Assert.Equal(0b111UL, state.Held);
        Assert.Equal(0, state.GetPressCounter(3));

        sampler.ReleaseAll();
        Assert.Equal(0UL, sampler.Held);
        Assert.Equal(1, sampler.Capture().GetPressCounter(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyboardMouseSampler(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyboardMouseSampler(65));
    }
}

public class KeyboardMouseCodecTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    public void Frames_round_trip_for_any_table_size(int elements)
    {
        var random = new Random(elements);
        var frames = new KeyboardMouseState[KeyboardMouseCodec.MaxFramesPerPacket];
        KeyboardMouseState previous = KeyboardMouseState.Neutral;
        for (int i = 0; i < frames.Length; i++)
        {
            // Some frames repeat the one before (one bit each), others change.
            previous = i % 3 == 1 ? previous : new KeyboardMouseState(NextULong(random), NextULong(random), NextULong(random)).Masked(elements);
            frames[i] = previous;
        }

        var writer = new BitWriter();
        KeyboardMouseCodec.Write(writer, elements, 1000, frames);
        var reader = new BitReader(writer.AsSpan());
        var read = new KeyboardMouseState[KeyboardMouseCodec.MaxFramesPerPacket];
        Assert.True(KeyboardMouseCodec.TryRead(ref reader, elements, read, out uint newest, out int count));
        Assert.Equal(1000u, newest);
        Assert.Equal(frames.Length, count);
        Assert.Equal(frames, read);
    }

    [Fact]
    public void Elements_beyond_the_table_are_not_sent()
    {
        var writer = new BitWriter();
        KeyboardMouseCodec.Write(writer, 4, 7, new[] { new KeyboardMouseState(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue) });
        var reader = new BitReader(writer.AsSpan());
        var read = new KeyboardMouseState[1];
        Assert.True(KeyboardMouseCodec.TryRead(ref reader, 4, read, out _, out _));
        Assert.Equal(new KeyboardMouseState(0b1111, 0xFF, 0), read[0]);

        // 32 bits newest frame, 3 bits count, 1 + 4 + 8 bits of frame.
        Assert.Equal((32 + 3 + 1 + 4 + 8 + 7) / 8, writer.ByteLength);
    }

    [Fact]
    public void An_unchanged_packet_costs_a_bit_per_frame()
    {
        var writer = new BitWriter();
        KeyboardMouseCodec.Write(writer, 32, 9, new KeyboardMouseState[8]);
        Assert.Equal((32 + 3 + 8 + 7) / 8, writer.ByteLength);

        // A typical changed frame on the default 32-key table: 1 + 32 + 64 bits.
        writer.Reset();
        KeyboardMouseCodec.Write(writer, 32, 9, new[] { new KeyboardMouseState(1, 1, 0) });
        Assert.Equal((32 + 3 + 97 + 7) / 8, writer.ByteLength);
    }

    [Fact]
    public void Bad_packets_and_sizes_are_refused()
    {
        var writer = new BitWriter();
        KeyboardMouseCodec.Write(writer, 64, 9, new[] { new KeyboardMouseState(5, 5, 5), new KeyboardMouseState(6, 6, 6) });
        byte[] packet = writer.ToArray();
        var destination = new KeyboardMouseState[8];

        var truncated = new BitReader(packet.AsSpan(0, packet.Length - 2));
        Assert.False(KeyboardMouseCodec.TryRead(ref truncated, 64, destination, out _, out int count));
        Assert.Equal(0, count);

        var small = new BitReader(packet);
        Assert.False(KeyboardMouseCodec.TryRead(ref small, 64, new KeyboardMouseState[1], out _, out _));

        foreach (int size in new[] { 0, -1, 65 })
        {
            var reader = new BitReader(packet);
            Assert.False(KeyboardMouseCodec.TryRead(ref reader, size, destination, out _, out _));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => KeyboardMouseCodec.Write(new BitWriter(), 0, 1, new KeyboardMouseState[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyboardMouseCodec.Write(new BitWriter(), 8, 1, new KeyboardMouseState[9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyboardMouseCodec.Write(new BitWriter(), 8, 1, ReadOnlySpan<KeyboardMouseState>.Empty));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(19);
        var destination = new KeyboardMouseState[8];
        for (int i = 0; i < 20_000; i++)
        {
            var bytes = new byte[random.Next(0, 120)];
            random.NextBytes(bytes);
            var reader = new BitReader(bytes);
            if (KeyboardMouseCodec.TryRead(ref reader, random.Next(-2, 70), destination, out _, out int count))
            {
                Assert.InRange(count, 1, 8);
            }

            // And through the receiver, whose first byte is the generation.
            var receiver = new KeyboardMouseReceiver(1_000_000);
            receiver.UseTable((byte)random.Next(1, 256), random.Next(1, 65));
            var again = new BitReader(bytes);
            receiver.Receive(ref again);
            receiver.Update(i);
        }
    }

    [Fact]
    public void Taps_are_presses_the_held_state_doesnt_explain()
    {
        var previous = new KeyboardMouseState(0, 0, 0);
        KeyboardMouseState held = previous.WithPress(1);
        held = new KeyboardMouseState(0b10, held.CountersLow, held.CountersHigh);
        Assert.Equal(0UL, KeyboardMouseTaps.Between(previous, held, 8)); // pressed and still held: shows in the state

        KeyboardMouseState tapped = previous.WithPress(3).WithPress(40);
        Assert.Equal((1UL << 3) | (1UL << 40), KeyboardMouseTaps.Between(previous, tapped, 64));
        Assert.Equal(1UL << 3, KeyboardMouseTaps.Between(previous, tapped, 8)); // outside the table: ignored

        KeyboardMouseState twice = new KeyboardMouseState(0b100, 0, 0).WithPress(2).WithPress(2);
        Assert.Equal(1UL << 2, KeyboardMouseTaps.Between(previous, twice, 8)); // two presses, one rise: one tap
    }

    private static ulong NextULong(Random random)
    {
        var bytes = new byte[8];
        random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}
