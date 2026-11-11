using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Input;

/// <summary>A keyboard friend's stream end to end, sender to receiver, without a session.</summary>
public class KeyboardMouseStreamTests
{
    private const long Second = 1_000_000;
    private const long Ms = 1000;
    private const long Frame = Second / PadInputSender.FrameRate;

    [Fact]
    public void The_sender_emits_one_frame_per_120_Hz_tick_and_tags_its_generation()
    {
        var sender = new KeyboardMouseSender(Second, generation: 3, elementCount: 10);
        Assert.True(sender.Capture(0, 0, 0));
        Assert.False(sender.Capture(Frame / 2, 0b1, 0b1));
        Assert.True(sender.Capture(Frame + 1, 0b1, 0));
        Assert.Equal(2u, sender.FramesCaptured);

        var writer = new BitWriter();
        sender.WritePacket(writer);
        byte[] packet = writer.ToArray();
        Assert.Equal(3, packet[0]);
        var reader = new BitReader(packet.AsSpan(1));
        var frames = new KeyboardMouseState[8];
        Assert.True(KeyboardMouseCodec.TryRead(ref reader, 10, frames, out uint newest, out int count));
        Assert.Equal((1u, 2), (newest, count));
        Assert.True(frames[1].IsHeld(0));
        Assert.Equal(1, frames[1].GetPressCounter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyboardMouseSender(Second, 0, 10));
        Assert.Throws<InvalidOperationException>(() => new KeyboardMouseSender(Second, 1, 10).WritePacket(new BitWriter()));
    }

    [Fact]
    public void Until_a_table_is_in_use_everything_is_stale_and_the_output_neutral()
    {
        var sender = new KeyboardMouseSender(Second, 1, 4);
        var receiver = new KeyboardMouseReceiver(Second);
        var writer = new BitWriter();
        sender.Capture(0, 0b11, 0);
        Assert.True(Deliver(sender, receiver, writer));
        Assert.Equal(1, receiver.StaleGeneration);
        Assert.Equal(0, receiver.Packets);
        Assert.Equal((KeyboardMouseState.Neutral, 0UL), receiver.Update(Second));

        receiver.UseTable(1, 4);
        sender.Capture(Frame + 1, 0b11, 0);
        Assert.True(Deliver(sender, receiver, writer));
        Assert.Equal(1, receiver.Packets);

        // Another table: playout starts over, and the old generation's packets are stale again.
        receiver.UseTable(2, 4);
        Assert.False(receiver.Playout.IsStarted);
        sender.Capture((2 * Frame) + 1, 0b11, 0);
        Assert.True(Deliver(sender, receiver, writer));
        Assert.Equal(2, receiver.StaleGeneration);
    }

    [Fact]
    public void Presses_made_before_the_table_was_in_use_do_not_come_out_late()
    {
        const ulong Escape = 1UL << 3;
        const ulong Space = 1UL << 5;
        var sender = new KeyboardMouseSender(Second, 1, 8);
        var receiver = new KeyboardMouseReceiver(Second);
        var writer = new BitWriter();
        const long Step = Frame + 1; // a whole frame each time on both clocks

        // Before the host has the friend's table (their controls on their way, or held back up to a second after a quick
        // rejoin): Escape pressed and let go, then Space pressed and held. Both frames are stale on the host.
        long now = 0;
        Assert.True(sender.Capture(now, 0, Escape));
        Assert.True(Deliver(sender, receiver, writer));
        now += Step;
        Assert.True(sender.Capture(now, Space, Space));
        Assert.True(Deliver(sender, receiver, writer));
        Assert.Equal(2, receiver.StaleGeneration);

        // The table arrives: no late Escape; Space shows as held.
        receiver.UseTable(1, 8);
        ulong taps = 0;
        KeyboardMouseState state = default;
        for (int i = 0; i < 20; i++)
        {
            now += Step;
            if (sender.Capture(now, Space, 0))
            {
                Deliver(sender, receiver, writer);
            }

            (state, ulong tapsNow) = receiver.Update(now);
            taps |= tapsNow;
        }

        Assert.Equal(0UL, taps);
        Assert.Equal(0, receiver.Taps);
        Assert.True(state.IsHeld(5));

        // An Escape tap from now on comes out once.
        now += Step;
        Assert.True(sender.Capture(now, Space, Escape));
        Deliver(sender, receiver, writer);
        for (int i = 0; i < 20; i++)
        {
            (_, ulong tapsNow) = receiver.Update(now);
            taps |= tapsNow;
            now += Step;
            if (sender.Capture(now, Space, 0))
            {
                Deliver(sender, receiver, writer);
            }
        }

        Assert.Equal(Escape, taps);
        Assert.Equal(1, receiver.Taps);
    }

    [Fact]
    public void Generation_zero_and_bad_frames_are_malformed()
    {
        var receiver = new KeyboardMouseReceiver(Second);
        receiver.UseTable(1, 8);
        var zero = new BitReader(new byte[] { 0, 1, 2, 3 });
        Assert.False(receiver.Receive(ref zero));
        var empty = new BitReader(Array.Empty<byte>());
        Assert.False(receiver.Receive(ref empty));
        var truncated = new BitReader(new byte[] { 1, 1, 2 });
        Assert.False(receiver.Receive(ref truncated));
        Assert.Equal(3, receiver.MalformedPackets);
        Assert.Throws<ArgumentOutOfRangeException>(() => receiver.UseTable(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => receiver.UseTable(1, 65));
    }

    [Fact]
    public void A_tap_between_two_frames_comes_out_once()
    {
        var sender = new KeyboardMouseSender(Second, 1, 40);
        var receiver = new KeyboardMouseReceiver(Second);
        receiver.UseTable(1, 40);
        var writer = new BitWriter();
        long now = 0;
        ulong taps = 0;
        for (int i = 0; i < 60; i++, now += 4 * Ms)
        {
            ulong pressed = i == 20 ? 1UL << 37 : 0; // down and up between two polls
            if (sender.Capture(now, 0, pressed))
            {
                Deliver(sender, receiver, writer);
            }

            (KeyboardMouseState state, ulong tapsNow) = receiver.Update(now);
            Assert.False(state.IsHeld(37));
            taps |= tapsNow;
            Assert.True(BitOperationsCount(tapsNow) <= 1);
        }

        Assert.Equal(1UL << 37, taps);
        Assert.Equal(1, receiver.Taps);
    }

    [Fact]
    public void Random_key_holds_survive_jitter_and_loss_within_a_frame()
    {
        // 30 s of random holds on a 32-key table, 30-50 ms one way with 5 % loss, sampled at 240 Hz.
        var random = new Random(23);
        var sender = new KeyboardMouseSender(Second, 7, 32);
        var receiver = new KeyboardMouseReceiver(Second);
        receiver.UseTable(7, 32);
        var writer = new BitWriter();
        var inFlight = new List<(long Due, byte[] Packet)>();
        var friendHolds = new HoldLengths();
        var hostHolds = new HoldLengths();
        int[] keys = { 0, 5, 17, 31 };
        ulong held = 0;
        var nextChange = new long[keys.Length];
        long step = Second / 240;
        for (long now = 0; now < 31 * Second; now += step)
        {
            for (int k = 0; k < keys.Length; k++)
            {
                if (now >= nextChange[k] && now < 30 * Second)
                {
                    held ^= 1UL << keys[k];
                    nextChange[k] = now + (random.Next(40, 600) * Ms);
                }
            }

            if (now >= 30 * Second)
            {
                held = 0;
            }

            friendHolds.Observe(now, held);
            if (sender.Capture(now, held, 0))
            {
                writer.Reset();
                sender.WritePacket(writer);
                if (random.NextDouble() >= 0.05)
                {
                    inFlight.Add((now + (30 * Ms) + (long)(random.NextDouble() * 20 * Ms), writer.ToArray()));
                }
            }

            foreach ((long due, byte[] packet) in inFlight.Where(p => p.Due <= now).OrderBy(p => p.Due).ToList())
            {
                inFlight.Remove((due, packet));
                var reader = new BitReader(packet);
                Assert.True(receiver.Receive(ref reader));
            }

            (KeyboardMouseState state, ulong taps) = receiver.Update(now);
            hostHolds.Observe(now, state.Held | taps);
        }

        var errorsMs = new List<double>();
        foreach (int key in keys)
        {
            Assert.Equal(friendHolds.Lengths[key].Count, hostHolds.Lengths[key].Count);
            errorsMs.AddRange(friendHolds.Lengths[key].Zip(hostHolds.Lengths[key], (sent, got) => Math.Abs(got - sent) / 1000.0));
        }

        Assert.True(errorsMs.Count > 150);
        Assert.True(errorsMs.Count(e => e > 12.5) <= errorsMs.Count / 20, $"{errorsMs.Count(e => e > 12.5)} of {errorsMs.Count} holds off by more than a frame and a poll");
        Assert.True(receiver.Holds.OverOneFrame <= receiver.Holds.Count / 20, $"playout changed {receiver.Holds.OverOneFrame} of {receiver.Holds.Count} holds by more than a frame");
        Assert.Equal(errorsMs.Count, receiver.Holds.Count);
        Assert.Equal(0, receiver.MalformedPackets);
    }

    private static int BitOperationsCount(ulong value) => System.Numerics.BitOperations.PopCount(value);

    private static bool Deliver(KeyboardMouseSender sender, KeyboardMouseReceiver receiver, BitWriter writer)
    {
        writer.Reset();
        sender.WritePacket(writer);
        var reader = new BitReader(writer.AsSpan());
        return receiver.Receive(ref reader);
    }

    /// <summary>Hold lengths per element, in the order they end.</summary>
    private sealed class HoldLengths
    {
        private readonly long[] _since = new long[64];
        private ulong _held;

        public List<long>[] Lengths { get; } = Enumerable.Range(0, 64).Select(_ => new List<long>()).ToArray();

        public void Observe(long now, ulong held)
        {
            ulong changed = held ^ _held;
            for (int i = 0; i < 64; i++)
            {
                if ((changed & (1UL << i)) == 0)
                {
                    continue;
                }

                if ((held & (1UL << i)) != 0)
                {
                    _since[i] = now;
                }
                else
                {
                    Lengths[i].Add(now - _since[i]);
                }
            }

            _held = held;
        }
    }
}
