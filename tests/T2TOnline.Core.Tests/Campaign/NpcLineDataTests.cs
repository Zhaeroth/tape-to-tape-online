using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class NpcLineDataTests
{
    /// <summary>A visit line from a pool, with an anchor side.</summary>
    public static NpcLineData Sample(int number = 2) => new()
    {
        Number = number,
        Source = NpcLineSource.Visit,
        Index = 3,
        Pool = "CoachDialoguePool",
        Sequence = "coach_visit_4",
        AnchorSide = 1,
    };

    public static byte[] Encoded(NpcLineData line)
    {
        var writer = new BitWriter();
        line.Write(writer);
        return writer.ToArray();
    }

    private static NpcLineData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = NpcLineData.TryRead(ref reader, out NpcLineData? line);
        Assert.Equal(ok, line != null);
        return ok && reader.RemainingBits < 8 ? line : null;
    }

    [Fact]
    public void A_line_round_trips_field_by_field()
    {
        NpcLineData read = Read(Encoded(Sample()))!;
        Assert.NotNull(read);
        Assert.Equal((2, NpcLineSource.Visit, 3, "CoachDialoguePool", "coach_visit_4", (int?)1), (read.Number, read.Source, read.Index, read.Pool, read.Sequence, read.AnchorSide));
        Assert.Contains("visit 4 of pool \"CoachDialoguePool\"", read.ToString(), StringComparison.Ordinal);

        // A line found by name, with no pool and no anchor side; the largest number, index and side.
        var other = new NpcLineData { Number = ushort.MaxValue, Source = NpcLineSource.Other, Sequence = "milestone_3" };
        NpcLineData second = Read(Encoded(other))!;
        Assert.Equal((ushort.MaxValue, (string?)null, (int?)null), (second.Number, second.Pool, second.AnchorSide));
        var largest = new NpcLineData { Source = NpcLineSource.PoolEntry, Index = NpcLineData.MaxIndex, Pool = new string('p', 128), Sequence = new string('s', 128), AnchorSide = NpcLineData.MaxAnchorSide };
        Assert.Null(largest.Problem());
        Assert.Equal(Encoded(largest), Encoded(Read(Encoded(largest))!));
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        yield return new object[] { "number 0", new NpcLineData { Number = 0, Source = NpcLineSource.FirstVisit, Sequence = "s" } };
        yield return new object[] { "number over 65535", new NpcLineData { Number = 65536, Source = NpcLineSource.FirstVisit, Sequence = "s" } };
        yield return new object[] { "no source", new NpcLineData { Source = NpcLineSource.None, Sequence = "s" } };
        yield return new object[] { "an unknown source", new NpcLineData { Source = (NpcLineSource)6, Sequence = "s" } };
        yield return new object[] { "an index on a first-visit line", new NpcLineData { Source = NpcLineSource.FirstVisit, Index = 1, Sequence = "s" } };
        yield return new object[] { "an index past the cap", new NpcLineData { Source = NpcLineSource.PoolEntry, Index = NpcLineData.MaxIndex + 1, Sequence = "s" } };
        yield return new object[] { "a negative index", new NpcLineData { Source = NpcLineSource.Visit, Index = -1, Sequence = "s" } };
        yield return new object[] { "no sequence", new NpcLineData { Source = NpcLineSource.Other, Sequence = string.Empty } };
        yield return new object[] { "a long sequence", new NpcLineData { Source = NpcLineSource.Other, Sequence = new string('s', 129) } };
        yield return new object[] { "an empty pool", new NpcLineData { Source = NpcLineSource.Other, Pool = string.Empty, Sequence = "s" } };
        yield return new object[] { "a pool with a line break", new NpcLineData { Source = NpcLineSource.Other, Pool = "a\nb", Sequence = "s" } };
        yield return new object[] { "an anchor side past the cap", new NpcLineData { Source = NpcLineSource.Other, Sequence = "s", AnchorSide = NpcLineData.MaxAnchorSide + 1 } };
        yield return new object[] { "a negative anchor side", new NpcLineData { Source = NpcLineSource.Other, Sequence = "s", AnchorSide = -1 } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Lines_out_of_their_limits_are_refused_when_written_and_never_sent(string what, NpcLineData line)
    {
        Assert.NotNull(line.Problem());
        Assert.ThrowsAny<ArgumentException>(() => line.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.NpcLine(3, line));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Crafted_lines_that_break_a_rule_are_refused()
    {
        byte[] good = Encoded(Sample());
        Assert.NotNull(Read(good));

        // Number 0, source 0, an index on a source that has none, an anchor byte past the cap.
        byte[] bytes = (byte[])good.Clone();
        bytes[0] = 0;
        bytes[1] = 0;
        Assert.Null(Read(bytes));
        bytes = (byte[])good.Clone();
        bytes[2] = 0;
        Assert.Null(Read(bytes));
        bytes = (byte[])good.Clone();
        bytes[2] = (byte)NpcLineSource.NoChoices;
        Assert.Null(Read(bytes));
        Assert.NotNull(Read(Crafted(NpcLineData.MaxAnchorSide + 1)));
        Assert.Null(Read(Crafted(NpcLineData.MaxAnchorSide + 2)));
        Assert.Null(Read(Crafted(255)));

        // Cut short at every length; something after it.
        for (int length = 0; length < good.Length; length++)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));

        // A first-visit line with no pool, its anchor byte as given.
        static byte[] Crafted(int anchorByte)
        {
            var writer = new BitWriter();
            writer.WriteUInt16(1);
            writer.WriteByte((byte)NpcLineSource.FirstVisit);
            writer.WriteVarUInt(0);
            writer.WriteBool(false); // no pool
            writer.WriteVarUInt(1);
            writer.WriteBytes("s"u8);
            writer.WriteByte((byte)anchorByte);
            return writer.ToArray();
        }
    }

    [Fact]
    public void An_attachment_of_a_line_round_trips_and_is_only_a_line()
    {
        MenuAttachment sent = MenuAttachment.NpcLine(13, Sample());
        Assert.Equal((MenuAttachmentKind.NpcLine, MenuAttachment.NpcLineFormat), (sent.Kind, sent.Format));
        Assert.Contains("NPC line for #13", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetEventChoices(out _));
        Assert.False(sent.TryGetPrompt(out _));
        Assert.False(sent.TryGetRunSummary(out _));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetNpcLine(out NpcLineData? line));
        Assert.Equal(Encoded(Sample()), Encoded(line!));

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.NpcLine(0, Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.NpcLine(1, null!));
        Assert.False(MenuAttachment.EventChoices(13, EventChoiceSetTests.Sample()).TryGetNpcLine(out _));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = { Encoded(Sample()), Encoded(new NpcLineData { Source = NpcLineSource.FirstVisit, Sequence = "first" }) };
        var random = new Random(3);
        int read = 0;
        for (int round = 0; round < 6000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            if (round % 4 == 3)
            {
                bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);
            }
            else
            {
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            var reader = new BitReader(bytes);
            if (!NpcLineData.TryRead(ref reader, out NpcLineData? line))
            {
                Assert.Null(line);
                continue;
            }

            read++;
            Assert.Null(line!.Problem());
            Assert.Equal(Encoded(line), Encoded(Read(Encoded(line))!));
            _ = line.ToString();
        }

        Assert.True(read > 0, "some changed lines should still read");
    }
}
