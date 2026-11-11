using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class RunCheckpointTests
{
    private static Dictionary<RunCheckpointPart, byte[]> Parts(string run = "run", string stats = "stats") => new()
    {
        [RunCheckpointPart.Run] = Encoding.UTF8.GetBytes(run),
        [RunCheckpointPart.RunStats] = Encoding.UTF8.GetBytes(stats),
    };

    private static byte[] Written(RunCheckpoint checkpoint)
    {
        var writer = new BitWriter();
        Assert.True(RunCheckpointMessage.Write(writer, checkpoint));
        return writer.ToArray();
    }

    private static bool Read(byte[] bytes, out RunCheckpoint? checkpoint)
    {
        var reader = new BitReader(bytes);
        return RunCheckpointMessage.TryRead(ref reader, out checkpoint);
    }

    [Fact]
    public void A_checkpoint_round_trips_compressed()
    {
        string map = string.Concat(System.Linq.Enumerable.Repeat("{\"node\":{\"x\":1,\"y\":2,\"type\":\"Match\"}},", 2000));
        var checkpoint = new RunCheckpoint(7, Parts(map, "{\"goals\":3}"));
        byte[] bytes = Written(checkpoint);
        Assert.True(bytes.Length < checkpoint.RawBytes / 10, $"{bytes.Length} bytes for {checkpoint.RawBytes}");

        Assert.True(Read(bytes, out RunCheckpoint? read));
        Assert.Equal(7u, read!.Sequence);
        Assert.Equal(checkpoint.Hash, read.Hash);
        Assert.Equal(map, Encoding.UTF8.GetString(read.Get(RunCheckpointPart.Run)!));
        Assert.Equal("{\"goals\":3}", Encoding.UTF8.GetString(read.Get(RunCheckpointPart.RunStats)!));
    }

    [Fact]
    public void The_hash_covers_every_part_and_ignores_their_order()
    {
        var a = new RunCheckpoint(1, Parts());
        var reordered = new Dictionary<RunCheckpointPart, byte[]>
        {
            [RunCheckpointPart.RunStats] = Encoding.UTF8.GetBytes("stats"),
            [RunCheckpointPart.Run] = Encoding.UTF8.GetBytes("run"),
        };
        Assert.Equal(a.Hash, new RunCheckpoint(2, reordered).Hash);
        Assert.NotEqual(a.Hash, new RunCheckpoint(1, Parts("run", "statz")).Hash);
        // Moving a byte from one part to the other changes the hash (lengths are hashed too).
        Assert.NotEqual(new RunCheckpoint(1, Parts("ab", "c")).Hash, new RunCheckpoint(1, Parts("a", "bc")).Hash);
    }

    [Fact]
    public void A_changed_byte_is_refused()
    {
        byte[] bytes = Written(new RunCheckpoint(3, Parts()));
        for (int i = 0; i < bytes.Length; i++)
        {
            byte[] copy = (byte[])bytes.Clone();
            copy[i] ^= 0x10;
            // A flip in the sequence number, or in a deflate stream's unused last bits, still reads; anything accepted is
            // exactly the host's run (the hash covers the inflated parts), whatever its number.
            if (Read(copy, out RunCheckpoint? read))
            {
                Assert.Equal(new RunCheckpoint(3, Parts()).Hash, read!.Hash);
                Assert.Equal("run", Encoding.UTF8.GetString(read.Get(RunCheckpointPart.Run)!));
            }
        }
    }

    [Fact]
    public void A_truncated_checkpoint_is_refused()
    {
        byte[] bytes = Written(new RunCheckpoint(3, Parts()));
        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.False(Read(bytes[..length], out _));
        }
    }

    [Fact]
    public void Unknown_repeated_or_missing_parts_are_refused()
    {
        Assert.False(Read(Handmade(writer => writer.WriteByte(0)), out _)); // no parts
        Assert.False(Read(Handmade(writer => { writer.WriteByte(1); WritePart(writer, (RunCheckpointPart)9, "x"); }), out _));
        Assert.False(Read(Handmade(writer =>
        {
            writer.WriteByte(2);
            WritePart(writer, RunCheckpointPart.Run, "x");
            WritePart(writer, RunCheckpointPart.Run, "y");
        }), out _));
        Assert.False(Read(Handmade(writer => writer.WriteByte(RunCheckpoint.MaxParts + 1)), out _));
    }

    [Fact]
    public void Sizes_are_checked_before_anything_is_allocated()
    {
        // A part claiming more than the limit, and one claiming more compressed bytes than the message holds.
        Assert.False(Read(Handmade(writer =>
        {
            writer.WriteByte(1);
            writer.WriteByte((byte)RunCheckpointPart.Run);
            writer.WriteVarUInt(RunCheckpoint.MaxPartBytes + 1UL);
            writer.WriteVarUInt(4);
            writer.WriteBytes(new byte[4]);
        }), out _));
        Assert.False(Read(Handmade(writer =>
        {
            writer.WriteByte(1);
            writer.WriteByte((byte)RunCheckpointPart.Run);
            writer.WriteVarUInt(10);
            writer.WriteVarUInt(int.MaxValue);
        }), out _));
    }

    [Fact]
    public void A_part_that_inflates_past_its_stated_length_is_refused()
    {
        byte[] bomb = Compression.Deflate(new byte[100_000]);
        Assert.False(Read(Handmade(writer =>
        {
            writer.WriteByte(1);
            writer.WriteByte((byte)RunCheckpointPart.Run);
            writer.WriteVarUInt(10);
            writer.WriteVarUInt((ulong)bomb.Length);
            writer.WriteBytes(bomb);
            writer.WriteUInt64(0);
        }), out _));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(4);
        byte[] real = Written(new RunCheckpoint(3, Parts()));
        for (int i = 0; i < 3000; i++)
        {
            byte[] junk = new byte[random.Next(0, 80)];
            random.NextBytes(junk);
            if (i % 2 == 0 && junk.Length > 6)
            {
                Array.Copy(real, junk, Math.Min(6, real.Length)); // a plausible start
            }

            _ = Read(junk, out _);
        }
    }

    [Fact]
    public void A_run_too_big_for_one_message_is_not_written()
    {
        var random = new Random(5);
        byte[] noise = new byte[RunCheckpointMessage.MaxMessageBytes + 1000]; // incompressible
        random.NextBytes(noise);
        var writer = new BitWriter();
        Assert.False(RunCheckpointMessage.Write(writer, new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = noise })));
    }

    [Fact]
    public void Checkpoints_need_known_parts_within_the_limits()
    {
        Assert.Throws<ArgumentException>(() => new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]>()));
        Assert.Throws<ArgumentException>(() => new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.None] = new byte[1] }));
        Assert.Throws<ArgumentException>(() => new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = new byte[RunCheckpoint.MaxPartBytes + 1] }));
    }

    [Fact]
    public void The_store_keeps_the_newest_across_wraparound()
    {
        var store = new CheckpointStore();
        Assert.True(store.Has(0));
        Assert.False(store.Has(1));
        Assert.True(store.Offer(new RunCheckpoint(uint.MaxValue, Parts())));
        Assert.True(store.Offer(new RunCheckpoint(2, Parts("later"))));
        Assert.False(store.Offer(new RunCheckpoint(1, Parts("older"))));
        Assert.False(store.Offer(new RunCheckpoint(2, Parts("same number"))));
        Assert.Equal(2u, store.Latest!.Sequence);
        Assert.True(store.Has(uint.MaxValue));
        Assert.True(store.Has(2));
        Assert.False(store.Has(3));
        Assert.Equal(2, store.Accepted);
        Assert.Equal(2, store.Ignored);
    }

    [Fact]
    public void The_publisher_numbers_changes_only_and_sends_each_friend_the_newest_once()
    {
        var publisher = new CheckpointPublisher();
        Assert.Null(publisher.PendingFor(10));
        RunCheckpoint first = publisher.Publish(Parts())!;
        Assert.Equal(1u, first.Sequence);
        Assert.Null(publisher.Publish(Parts())); // the same run saved again
        Assert.Equal(1, publisher.Unchanged);

        Assert.Same(first, publisher.PendingFor(10));
        publisher.MarkSent(10, first.Sequence);
        Assert.Null(publisher.PendingFor(10));
        Assert.Same(first, publisher.PendingFor(20)); // a friend who joined later

        RunCheckpoint second = publisher.Publish(Parts("changed"))!;
        Assert.Equal(2u, second.Sequence);
        Assert.Same(second, publisher.PendingFor(10));
        publisher.MarkSent(10, 2);
        publisher.Forget(10); // left and came back
        Assert.Same(second, publisher.PendingFor(10));
    }

    private static void WritePart(BitWriter writer, RunCheckpointPart kind, string text)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        byte[] deflated = Compression.Deflate(raw);
        writer.WriteByte((byte)kind);
        writer.WriteVarUInt((ulong)raw.Length);
        writer.WriteVarUInt((ulong)deflated.Length);
        writer.WriteBytes(deflated);
    }

    private static byte[] Handmade(Action<BitWriter> body)
    {
        var writer = new BitWriter();
        writer.WriteUInt32(1);
        body(writer);
        writer.WriteUInt64(0);
        return writer.ToArray();
    }
}
