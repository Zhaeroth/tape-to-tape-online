using System;
using System.Collections.Generic;
using System.IO;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Snapshots;

public class StreamAndTimelineTests
{
    private static readonly MatchStreamHeader Header = new(ProtocolInfo.Version, "0.3.0-m2", 0x0E1B540314D591A1, 638_000_000_000_000_000, "test match");

    [Fact]
    public void A_stream_file_round_trips()
    {
        var file = new MemoryStream();
        using (var writer = new MatchStreamWriter(file, Header))
        {
            writer.Write(0, Channel.Bulk, new byte[] { 1, 2, 3 });
            writer.Write(19_000, Channel.Realtime, new byte[] { 4 });
            writer.Write(20_000, Channel.Events, Array.Empty<byte>());
        }

        // MemoryStream.ToArray still works after the writer closed it.
        Assert.True(MatchStreamReader.TryOpen(new MemoryStream(file.ToArray()), out MatchStreamReader? reader, out string problem), problem);
        using (reader)
        {
            Assert.Equal(Header, reader!.Header);
            var records = new List<MatchStreamRecord>();
            while (reader.TryRead(out MatchStreamRecord record))
            {
                records.Add(record);
            }

            Assert.Equal(3, records.Count);
            Assert.Equal(19_000, records[1].TimeMicros);
            Assert.Equal(Channel.Realtime, records[1].Channel);
            Assert.Equal(new byte[] { 4 }, records[1].Message);
            Assert.Empty(records[2].Message);
            Assert.False(reader.Truncated);
        }
    }

    [Fact]
    public void A_cut_off_stream_reads_up_to_the_last_whole_record()
    {
        var file = new MemoryStream();
        using (var writer = new MatchStreamWriter(file, Header))
        {
            writer.Write(0, Channel.Bulk, new byte[100]);
            writer.Write(1, Channel.Bulk, new byte[100]);
        }

        byte[] whole = file.ToArray();
        var cut = new MemoryStream(whole.AsSpan(0, whole.Length - 30).ToArray());
        Assert.True(MatchStreamReader.TryOpen(cut, out MatchStreamReader? reader, out _));
        using (reader)
        {
            Assert.True(reader!.TryRead(out _));
            Assert.False(reader.TryRead(out _));
            Assert.True(reader.Truncated);
        }
    }

    [Fact]
    public void Something_else_is_not_a_stream()
    {
        Assert.False(MatchStreamReader.TryOpen(new MemoryStream(new byte[] { 1, 2, 3 }), out _, out string problem));
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void Net_values_round_trip()
    {
        var values = new List<NetValue>
        {
            NetValue.Null, NetValue.FromBool(true), NetValue.FromInt(-123456789012), NetValue.FromFloat(1.5f),
            NetValue.FromDouble(Math.PI), NetValue.FromString("Gedeon Lamoustache"), NetValue.FromVector2(1, 2),
            NetValue.FromVector3(1, 2, 3), NetValue.FromQuaternion(0, 0, 0, 1), NetValue.FromColor(1, 0.5f, 0, 1),
            NetValue.FromRef(65535), NetValue.FromBytes(new byte[] { 9, 8, 7 }),
        };
        var writer = new BitWriter();
        NetValue.WriteList(writer, values);
        var reader = new BitReader(writer.AsSpan());
        var decoded = new List<NetValue>();

        Assert.True(NetValue.TryReadList(ref reader, decoded));
        Assert.Equal(values, decoded);
    }

    [Fact]
    public void Garbage_values_never_throw()
    {
        var random = new Random(9);
        var buffer = new byte[64];
        for (int i = 0; i < 20_000; i++)
        {
            random.NextBytes(buffer);
            var reader = new BitReader(buffer.AsSpan(0, random.Next(0, buffer.Length)));
            var values = new List<NetValue>();
            bool read = NetValue.TryReadList(ref reader, values);

            // Whatever the bytes say: never more values than a list may hold, and a list that read is whole.
            Assert.True(values.Count <= 256);
            Assert.False(read && reader.IsOverrun);
        }
    }

    [Fact]
    public void A_list_longer_than_the_caller_allows_is_refused()
    {
        var nine = new List<NetValue>();
        for (int i = 0; i < 9; i++)
        {
            nine.Add(NetValue.FromInt(i));
        }

        var writer = new BitWriter();
        NetValue.WriteList(writer, nine);
        var reader = new BitReader(writer.AsSpan());
        var decoded = new List<NetValue>();
        Assert.False(NetValue.TryReadList(ref reader, decoded, maxCount: 8));
        Assert.Empty(decoded);

        reader = new BitReader(writer.AsSpan());
        Assert.True(NetValue.TryReadList(ref reader, decoded, maxCount: 9));
        Assert.Equal(nine, decoded);
    }

    [Fact]
    public void The_timeline_samples_between_snapshots_and_holds_past_the_newest()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(new SnapshotFrame { Sequence = 2, HostTimeMicros = 200 });
        timeline.Add(new SnapshotFrame { Sequence = 1, HostTimeMicros = 100 }); // late arrival
        timeline.Add(new SnapshotFrame { Sequence = 3, HostTimeMicros = 300 });

        Assert.False(timeline.TrySample(50, out _, out _, out _));
        Assert.True(timeline.TrySample(150, out SnapshotFrame from, out SnapshotFrame to, out float t));
        Assert.Equal((1u, 2u), (from.Sequence, to.Sequence));
        Assert.Equal(0.5f, t, 3);
        Assert.True(timeline.TrySample(999, out from, out to, out t));
        Assert.Equal((3u, 3u, 1f), (from.Sequence, to.Sequence, t));

        timeline.TrimBefore(250);
        Assert.Equal(2, timeline.Count);
        Assert.Equal(200, timeline.OldestTime);
    }

    [Fact]
    public void A_full_timeline_drops_its_oldest_and_refuses_anything_older()
    {
        var timeline = new SnapshotTimeline(capacity: 3);
        foreach (uint sequence in new uint[] { 1, 2, 4 })
        {
            Assert.True(timeline.Add(new SnapshotFrame { Sequence = sequence, HostTimeMicros = sequence * 100 }));
        }

        // A late frame between two others goes between them; the oldest makes room.
        Assert.True(timeline.Add(new SnapshotFrame { Sequence = 3, HostTimeMicros = 300 }));
        Assert.Equal(3, timeline.Count);
        Assert.Equal(200, timeline.OldestTime);
        Assert.True(timeline.TrySample(350, out SnapshotFrame from, out SnapshotFrame to, out float t));
        Assert.Equal((3u, 4u, 0.5f), (from.Sequence, to.Sequence, t));

        // Older than everything held, while full: refused.
        Assert.False(timeline.Add(new SnapshotFrame { Sequence = 1, HostTimeMicros = 100 }));
        Assert.Equal(200, timeline.OldestTime);
        Assert.True(timeline.Add(new SnapshotFrame { Sequence = 5, HostTimeMicros = 500 }));
        Assert.Equal((300L, 500L), (timeline.OldestTime, timeline.NewestTime));
    }

    [Fact]
    public void A_corrupt_record_length_mid_file_keeps_what_came_before()
    {
        var file = new MemoryStream();
        using (var writer = new MatchStreamWriter(file, Header))
        {
            writer.Write(0, Channel.Bulk, new byte[] { 1 });
            writer.Write(1, Channel.Bulk, new byte[] { 2 });
            writer.Write(2, Channel.Bulk, new byte[] { 3 });
        }

        // Each record is a u32 length, then 8 + 1 + 1 bytes; the second record's length becomes absurd.
        byte[] bytes = file.ToArray();
        int second = bytes.Length - (2 * (4 + 10));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, second);
        Assert.True(MatchStreamReader.TryOpen(new MemoryStream(bytes), out MatchStreamReader? reader, out _));
        using (reader)
        {
            Assert.True(reader.TryRead(out MatchStreamRecord first));
            Assert.Equal(new byte[] { 1 }, first.Message);
            Assert.False(reader.TryRead(out _));
            Assert.True(reader.Truncated);
            Assert.Empty(reader.ReadError);
        }

        // Too short to hold its own time and channel: the same.
        BitConverter.GetBytes(3u).CopyTo(bytes, second);
        Assert.True(MatchStreamReader.TryOpen(new MemoryStream(bytes), out reader, out _));
        using (reader)
        {
            Assert.True(reader.TryRead(out _));
            Assert.False(reader.TryRead(out _));
            Assert.True(reader.Truncated);
        }
    }

    [Fact]
    public void The_writer_refuses_a_record_the_reader_would_stop_at()
    {
        var file = new MemoryStream();
        using (var writer = new MatchStreamWriter(file, Header))
        {
            long before = writer.Bytes;
            Assert.False(writer.Write(0, Channel.Bulk, new byte[MatchStreamReader.MaxRecordBytes]));
            Assert.Equal((1L, 0L, before), (writer.Refused, writer.Records, writer.Bytes));
            Assert.True(writer.Write(0, Channel.Bulk, new byte[MatchStreamReader.MaxRecordBytes - 9])); // exactly the limit
            Assert.True(writer.Write(1, Channel.Bulk, new byte[] { 7 }));
        }

        Assert.True(MatchStreamReader.TryOpen(new MemoryStream(file.ToArray()), out MatchStreamReader? reader, out _));
        using (reader)
        {
            Assert.True(reader.TryRead(out MatchStreamRecord big));
            Assert.Equal(MatchStreamReader.MaxRecordBytes - 9, big.Message.Length);
            Assert.True(reader.TryRead(out MatchStreamRecord last));
            Assert.Equal(new byte[] { 7 }, last.Message);
            Assert.False(reader.Truncated);
        }
    }

    [Fact]
    public void A_read_error_is_a_problem_not_an_exception()
    {
        var file = new MemoryStream();
        using (var writer = new MatchStreamWriter(file, Header))
        {
            writer.Write(0, Channel.Bulk, new byte[] { 1 });
            writer.Write(1, Channel.Bulk, new byte[] { 2 });
        }

        byte[] bytes = file.ToArray();
        // The disk fails partway through the second record, then while reading the header.
        Assert.True(MatchStreamReader.TryOpen(new FailingStream(bytes, failAt: bytes.Length - 5), out MatchStreamReader? reader, out _));
        using (reader)
        {
            Assert.True(reader.TryRead(out _));
            Assert.False(reader.TryRead(out _));
            Assert.True(reader.Truncated);
            Assert.Contains("disk", reader.ReadError);
        }

        Assert.False(MatchStreamReader.TryOpen(new FailingStream(bytes, failAt: 10), out _, out string problem));
        Assert.Contains("disk", problem);
        Assert.False(MatchRecording.TryRead(new FailingStream(bytes, failAt: 10), out MatchRecording? recording, out problem));
        Assert.Null(recording);
        Assert.Contains("disk", problem);
    }

    [Fact]
    public void Net_values_past_their_limits_are_rejected()
    {
        Assert.True(Reads(w => NetValue.FromBytes(new byte[NetValue.MaxBytes]).Write(w)));
        Assert.False(Reads(w =>
        {
            w.WriteBits((ulong)NetValueKind.Blob, 4);
            w.WriteVarUInt(NetValue.MaxBytes + 1);
            w.WriteBytes(new byte[NetValue.MaxBytes + 1]);
        }));
        Assert.False(Reads(w =>
        {
            w.WriteBits((ulong)NetValueKind.Reference, 4);
            w.WriteVarUInt(ushort.MaxValue + 1);
        }));
        Assert.False(Reads(w => w.WriteBits(15, 4))); // an unknown kind

        static bool Reads(Action<BitWriter> write)
        {
            var writer = new BitWriter();
            write(writer);
            var reader = new BitReader(writer.AsSpan());
            return NetValue.TryRead(ref reader, out _);
        }
    }

    /// <summary>A stream over <c>bytes</c> whose reads fail with an I/O error from <c>failAt</c> on.</summary>
    private sealed class FailingStream : MemoryStream
    {
        private readonly int _failAt;

        public FailingStream(byte[] bytes, int failAt)
            : base(bytes, writable: false) => _failAt = failAt;

        public override int Read(byte[] buffer, int offset, int count) => Position + count > _failAt ? throw new IOException("the disk failed") : base.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => Position + buffer.Length > _failAt ? throw new IOException("the disk failed") : base.Read(buffer);

        public override int ReadByte() => Position + 1 > _failAt ? throw new IOException("the disk failed") : base.ReadByte();
    }
}
