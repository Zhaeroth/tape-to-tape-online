using System;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>The host's looping sounds (message 37): starts, moves and stops, every value outside the game's refused.</summary>
public class SoundLoopMessageTests
{
    [Fact]
    public void Starts_of_every_kind_moves_and_stops_round_trip()
    {
        for (int kind = 0; kind < LoopKinds.Count; kind++)
        {
            RoundTrip(SoundLoopMessage.Start((uint)(kind + 1), (LoopKind)kind, 1.5f, -0.25f, 30f));
        }

        RoundTrip(SoundLoopMessage.Move(uint.MaxValue, -SoundLoopMessage.MaxCoordinate, 0, SoundLoopMessage.MaxCoordinate));
        RoundTrip(SoundLoopMessage.Stop(1));
        RoundTrip(SoundLoopMessage.Stop(300));
    }

    [Fact]
    public void Each_op_carries_only_what_it_needs()
    {
        Assert.Equal(1 + 1 + 1 + 12, Write(SoundLoopMessage.Start(5, LoopKind.Magneto, 1, 2, 3)).Length);
        Assert.Equal(1 + 1 + 12, Write(SoundLoopMessage.Move(5, 1, 2, 3)).Length);
        Assert.Equal(1 + 1, Write(SoundLoopMessage.Stop(5)).Length);
        Assert.Equal(SoundLoopMessage.MaxPayloadBytes, Write(SoundLoopMessage.Start(uint.MaxValue, LoopKind.FusRoDahCharge, 1, 2, 3)).Length);
    }

    private static readonly SoundLoopMessage[] OutOfRange =
    {
        SoundLoopMessage.Stop(0),
        SoundLoopMessage.Start(0, LoopKind.Magnet, 0, 0, 0),
        SoundLoopMessage.Start(1, (LoopKind)LoopKinds.Count, 0, 0, 0),
        SoundLoopMessage.Start(1, (LoopKind)255, 0, 0, 0),
        SoundLoopMessage.Start(1, LoopKind.Magnet, float.NaN, 0, 0),
        SoundLoopMessage.Move(1, 0, float.PositiveInfinity, 0),
        SoundLoopMessage.Move(1, 0, 0, float.NegativeInfinity),
        SoundLoopMessage.Move(1, SoundLoopMessage.MaxCoordinate + 1, 0, 0),
        SoundLoopMessage.Start(1, LoopKind.RetroRocket, 0, -SoundLoopMessage.MaxCoordinate - 1, 0),
        new SoundLoopMessage(1, (SoundLoopOp)3, LoopKind.Magnet, 0, 0, 0),
    };

    [Fact]
    public void A_value_out_of_range_is_never_written_and_never_read()
    {
        foreach (SoundLoopMessage message in OutOfRange)
        {
            Assert.False(message.IsValid, message.ToString());
            Assert.Throws<InvalidOperationException>(() => Write(message));
            Assert.False(Read(Raw(message), out _), message.ToString());
        }
    }

    [Fact]
    public void A_handle_past_32_bits_is_refused()
    {
        var writer = new BitWriter();
        writer.WriteVarUInt((ulong)uint.MaxValue + 1);
        writer.WriteByte((byte)SoundLoopOp.Stop);
        Assert.False(Read(writer.ToArray(), out _));
    }

    [Fact]
    public void A_truncated_message_is_refused()
    {
        foreach (SoundLoopMessage message in new[] { SoundLoopMessage.Start(300, LoopKind.ExplodingPuck, 1, 2, 3), SoundLoopMessage.Move(2, 1, 2, 3), SoundLoopMessage.Stop(70_000) })
        {
            byte[] full = Write(message);
            for (int length = 0; length < full.Length; length++)
            {
                Assert.False(Read(full[..length], out _), $"{message.Op}: {length} bytes");
            }
        }
    }

    [Fact]
    public void Random_bytes_never_throw_and_what_reads_is_valid_and_round_trips()
    {
        var random = new Random(37);
        byte[][] seeds = { Write(SoundLoopMessage.Start(9, LoopKind.Magnet, 4, 0.5f, -12)), Write(SoundLoopMessage.Move(9, 4, 0.5f, -12)), Write(SoundLoopMessage.Stop(9)) };
        int taken = 0;
        for (int i = 0; i < 20_000; i++)
        {
            byte[] bytes = (byte[])seeds[random.Next(seeds.Length)].Clone();
            if (random.Next(3) == 0)
            {
                bytes = new byte[random.Next(0, 24)];
                random.NextBytes(bytes);
            }
            else
            {
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            }

            var reader = new BitReader(bytes);
            if (!SoundLoopMessage.TryRead(ref reader, out SoundLoopMessage message))
            {
                continue;
            }

            taken++;
            Assert.True(message.IsValid);
            RoundTrip(message);
        }

        Assert.True(taken > 1000, $"{taken} taken");
    }

    [Fact]
    public void Every_kind_has_a_name_for_the_log()
    {
        Assert.Equal("magnet, gravity well, magneto, magnet rebound, magnet safe bounce, retro rocket, exploding puck, fus-ro-dah charge",
            string.Join(", ", Enumerable.Range(0, LoopKinds.Count).Select(k => LoopKinds.Name((LoopKind)k))));
        Assert.Equal("kind 9", LoopKinds.Name((LoopKind)9));
    }

    private static void RoundTrip(SoundLoopMessage message)
    {
        byte[] bytes = Write(message);
        var reader = new BitReader(bytes);
        Assert.True(SoundLoopMessage.TryRead(ref reader, out SoundLoopMessage read));
        Assert.Equal(message, read);
        Assert.Equal(bytes.Length * 8, reader.BitPosition);
    }

    private static byte[] Write(SoundLoopMessage message)
    {
        var writer = new BitWriter();
        message.Write(writer);
        return writer.ToArray();
    }

    /// <summary>The message's fields as a hostile host would write them, without the writer's checks.</summary>
    private static byte[] Raw(SoundLoopMessage message)
    {
        var writer = new BitWriter();
        writer.WriteVarUInt(message.Handle);
        writer.WriteByte((byte)message.Op);
        if (message.Op == SoundLoopOp.Start)
        {
            writer.WriteByte((byte)message.Kind);
        }

        if (message.Op != SoundLoopOp.Stop)
        {
            writer.WriteSingle(message.X);
            writer.WriteSingle(message.Y);
            writer.WriteSingle(message.Z);
        }

        return writer.ToArray();
    }

    private static bool Read(byte[] bytes, out SoundLoopMessage message)
    {
        var reader = new BitReader(bytes);
        return SoundLoopMessage.TryRead(ref reader, out message);
    }
}
