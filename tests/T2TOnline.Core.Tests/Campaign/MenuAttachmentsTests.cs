using System;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuAttachmentsTests
{
    private static byte[] Written(MenuAttachment attachment)
    {
        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, attachment));
        return writer.ToArray();
    }

    private static MenuAttachment? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read);
        Assert.Equal(ok, read != null);
        return read;
    }

    /// <summary>A message laid out by hand: any field can be wrong.</summary>
    private static byte[] Crafted(uint serial, byte kind, byte format, ulong bodyLength, byte[] deflated, ulong? deflatedLength = null, byte[]? after = null)
    {
        var writer = new BitWriter();
        writer.WriteUInt32(serial);
        writer.WriteByte(kind);
        writer.WriteByte(format);
        writer.WriteVarUInt(bodyLength);
        writer.WriteVarUInt(deflatedLength ?? (ulong)deflated.Length);
        writer.WriteBytes(deflated);
        if (after != null)
        {
            writer.WriteBytes(after);
        }

        return writer.ToArray();
    }

    /// <summary>A well-formed message around <paramref name="body"/> (deflated here), as a host would write it.</summary>
    private static byte[] Around(byte[] body, uint serial = 28, byte kind = (byte)MenuAttachmentKind.RunSummary, byte format = MenuAttachment.RunSummaryFormat) =>
        Crafted(serial, kind, format, (ulong)body.Length, Compression.Deflate(body));

    [Fact]
    public void A_run_summary_round_trips_field_by_field()
    {
        RunHistoryData sample = RunHistoryDataTests.Sample();
        MenuAttachment sent = MenuAttachment.RunSummary(28, sample);
        MenuAttachment read = Read(Written(sent))!;

        Assert.NotNull(read);
        Assert.Equal(28u, read.Serial);
        Assert.Equal(MenuAttachmentKind.RunSummary, read.Kind);
        Assert.Equal(MenuAttachment.RunSummaryFormat, read.Format);
        Assert.Equal(sent.ContentHash, read.ContentHash);
        Assert.Equal(sent.Body.ToArray(), read.Body.ToArray());
        Assert.True(read.TryGetRunSummary(out RunHistoryData? entry));
        Assert.Equal(RunHistoryDataTests.Encoded(sample), RunHistoryDataTests.Encoded(entry!));
        Assert.Equal(sample.Matches[0].Scorers.Single().Value.Goals, entry!.Matches[0].Scorers.Single().Value.Goals);
        Assert.Equal(RunHistoryDataTests.Encoded(sample).Length, read.BodyLength);
    }

    [Fact]
    public void The_body_goes_deflated_and_the_same_bytes_go_to_every_friend()
    {
        MenuAttachment sent = MenuAttachment.RunSummary(3, RunHistoryDataTests.Sample());
        byte[] first = Written(sent);
        byte[] again = Written(sent);
        Assert.Equal(first, again);
        Assert.True(first.Length < sent.BodyLength + 16, $"{first.Length} bytes for a {sent.BodyLength}-byte body");
        Assert.Contains("run summary for #3", sent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_that_cant_be_read_are_refused_when_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.RunSummary(0, RunHistoryDataTests.Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.RunSummary(1, null!));
        Assert.Throws<ArgumentException>(() => MenuAttachment.RunSummary(1, RunHistoryDataTests.Huge())); // over the size cap
        Assert.Throws<ArgumentException>(() => MenuAttachment.RunSummary(1, new RunHistoryData { RunId = new string('x', RunHistoryData.MaxTextBytes + 1) }));
        Assert.Throws<ArgumentException>(() => MenuAttachment.RunSummary(1, new RunHistoryData { RunTime = float.NaN })); // wouldn't read back
    }

    [Fact]
    public void A_truncated_message_is_refused_at_every_length()
    {
        byte[] bytes = Written(MenuAttachment.RunSummary(28, RunHistoryDataTests.Sample()));
        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.Null(Read(bytes[..length]));
        }

        Assert.NotNull(Read(bytes));
    }

    [Fact]
    public void Wrong_headers_are_refused()
    {
        byte[] body = RunHistoryDataTests.Encoded(RunHistoryDataTests.Sample());
        Assert.NotNull(Read(Around(body)));
        Assert.Null(Read(Around(body, serial: 0)));
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.None)));
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.EventChoices))); // a known kind, but not its body
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.NpcLine)));
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.Prompt)));
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.RunPreview)));
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.NurseRoster)));
        Assert.Null(Read(Around(body, kind: (byte)MenuAttachmentKind.NurseSteps)));
        Assert.Null(Read(Around(body, kind: 6))); // a kind from a newer version
        Assert.Null(Read(Around(body, kind: 255)));
        Assert.Null(Read(Around(body, format: 0)));
        Assert.Null(Read(Around(body, format: MenuAttachment.RunSummaryFormat + 1)));
    }

    [Fact]
    public void Lengths_that_dont_match_are_refused()
    {
        byte[] body = RunHistoryDataTests.Encoded(RunHistoryDataTests.Sample());
        byte[] deflated = Compression.Deflate(body);
        Assert.NotNull(Read(Crafted(5, 1, 1, (ulong)body.Length, deflated)));

        // The body's stated length: shorter (inflating stops there), longer, none, over the cap.
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length - 1, deflated)));
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length + 1, deflated)));
        Assert.Null(Read(Crafted(5, 1, 1, 0, deflated)));
        Assert.Null(Read(Crafted(5, 1, 1, MenuAttachment.MaxBodyBytes + 1, deflated)));
        Assert.Null(Read(Crafted(5, 1, 1, ulong.MaxValue, deflated)));

        // The deflated length: more than follows, less than follows, none, over the cap.
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length, deflated, deflatedLength: (ulong)deflated.Length + 1)));
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length, deflated, deflatedLength: (ulong)deflated.Length - 1)));
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length, deflated, deflatedLength: 0)));
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length, deflated, deflatedLength: MenuAttachmentMessage.MaxDeflatedBytes + 1)));

        // Anything after the body.
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)body.Length, deflated, after: new byte[] { 0 })));
    }

    [Fact]
    public void Bodies_that_arent_a_run_summary_are_refused()
    {
        byte[] entry = RunHistoryDataTests.Encoded(RunHistoryDataTests.Sample());

        // Padded with zeros (they deflate to almost nothing), cut short, random bytes, an entry that doesn't read back.
        Assert.Null(Read(Around(entry.Concat(new byte[4096]).ToArray())));
        Assert.Null(Read(Around(entry[..^1])));
        var random = new Random(87);
        byte[] noise = new byte[2000];
        random.NextBytes(noise);
        Assert.Null(Read(Around(noise)));
        var writer = new BitWriter();
        new RunHistoryData { RunTime = float.PositiveInfinity }.Write(writer);
        Assert.Null(Read(Around(writer.ToArray())));

        // A deflate stream that isn't one.
        Assert.Null(Read(Crafted(5, 1, 1, (ulong)entry.Length, noise[..300])));
    }

    [Fact]
    public void Inflating_stops_at_the_cap()
    {
        // A deflate bomb: 4 MB of zeros deflates to a few KB. Stated as the cap, or as anything smaller, it's refused once
        // inflating reaches the stated length, never inflated whole.
        byte[] bomb = Compression.Deflate(new byte[4 * 1024 * 1024]);
        Assert.True(bomb.Length < MenuAttachmentMessage.MaxDeflatedBytes);
        Assert.Null(Read(Crafted(5, 1, 1, MenuAttachment.MaxBodyBytes, bomb)));
        Assert.Null(Read(Crafted(5, 1, 1, 1000, bomb)));
        Assert.False(Compression.TryInflate(bomb, MenuAttachment.MaxBodyBytes, out _));
    }

    [Fact]
    public void A_run_summary_near_the_cap_still_goes_and_reads()
    {
        // Text that barely compresses: the deflated body is about as big as the body, and still within one message.
        RunHistoryData large = RunHistoryDataTests.Noisy(MenuAttachment.MaxBodyBytes * 9 / 10, 11);
        MenuAttachment sent = MenuAttachment.RunSummary(9, large);
        Assert.InRange(sent.BodyLength, MenuAttachment.MaxBodyBytes / 2, MenuAttachment.MaxBodyBytes);
        byte[] bytes = Written(sent);
        Assert.InRange(bytes.Length, sent.BodyLength / 2, MenuAttachmentMessage.MaxMessageBytes);
        MenuAttachment read = Read(bytes)!;
        Assert.NotNull(read);
        Assert.Equal(sent.ContentHash, read.ContentHash);

        // A message bigger than any attachment can be is refused before anything is read from it.
        Assert.Null(Read(new byte[MenuAttachmentMessage.MaxMessageBytes + 1]));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw()
    {
        byte[] seed = Written(MenuAttachment.RunSummary(28, RunHistoryDataTests.Sample()));
        var random = new Random(870);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])seed.Clone();
            int flips = random.Next(1, 6);
            for (int i = 0; i < flips; i++)
            {
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            }

            MenuAttachment? read = Read(round % 3 == 0 ? bytes[..random.Next(bytes.Length)] : bytes);
            if (read != null)
            {
                // Whatever reads is a known kind, for a menu, with a body that reads as its kind.
                Assert.NotEqual(0u, read.Serial);
                Assert.True(MenuAttachment.IsKnown(read.Kind));
                Assert.InRange(read.BodyLength, 1, MenuAttachment.MaxBodyBytes);
                Assert.True(read.TryGetRunSummary(out RunHistoryData? entry));
                Assert.True(entry!.Matches.Count <= RunHistoryData.MaxItems && entry.Scorers.Count <= RunHistoryData.MaxItems);
            }

            byte[] noise = new byte[random.Next(0, 64)];
            random.NextBytes(noise);
            _ = Read(noise);
        }
    }
}
