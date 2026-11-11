using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class HistoryPageDataTests
{
    /// <summary>Page 3 of 12, a host at UTC+01:00, the full run history sample.</summary>
    public static HistoryPageData Sample() => new()
    {
        Index = 2,
        Count = 12,
        HostUtcOffsetMinutes = 60,
        Entry = RunHistoryDataTests.Sample(),
    };

    public static byte[] Encoded(HistoryPageData page)
    {
        var writer = new BitWriter();
        page.Write(writer);
        return writer.ToArray();
    }

    private static HistoryPageData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = HistoryPageData.TryRead(ref reader, out HistoryPageData? page);
        Assert.Equal(ok, page != null);
        return ok && reader.RemainingBits < 8 ? page : null;
    }

    private static byte[] EntryBytes(RunHistoryData entry)
    {
        var writer = new BitWriter();
        entry.Write(writer);
        return writer.ToArray();
    }

    [Fact]
    public void A_page_round_trips_with_its_run()
    {
        HistoryPageData read = Read(Encoded(Sample()))!;
        Assert.NotNull(read);
        Assert.Equal((2, 12, 60), (read.Index, read.Count, read.HostUtcOffsetMinutes));
        Assert.Equal(EntryBytes(RunHistoryDataTests.Sample()), EntryBytes(read.Entry!));
        Assert.StartsWith("page 3 of 12 (run ", read.ToString(), StringComparison.Ordinal);

        // The extremes: the last page of the longest list, offsets at both caps.
        foreach (HistoryPageData page in new[]
        {
            Sample() with { Index = HistoryPageData.MaxCount - 1, Count = HistoryPageData.MaxCount, HostUtcOffsetMinutes = HistoryPageData.MaxOffsetMinutes },
            Sample() with { Index = 0, Count = 1, HostUtcOffsetMinutes = -HistoryPageData.MaxOffsetMinutes },
        })
        {
            Assert.Null(page.Problem());
            Assert.Equal(Encoded(page), Encoded(Read(Encoded(page))!));
        }

    }

    [Fact]
    public void The_log_text_leaves_the_hosts_offset_out()
    {
        // The offset travels for the friend's screen only; a log line never says where in the world the host is.
        foreach (int offset in new[] { -840, -330, 0, 60, 345, 840 })
        {
            string text = (Sample() with { HostUtcOffsetMinutes = offset }).ToString();
            Assert.DoesNotContain("UTC", text, StringComparison.Ordinal);
            Assert.DoesNotContain(":", text, StringComparison.Ordinal);
            Assert.EndsWith(")", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("900", (Sample() with { HostUtcOffsetMinutes = 900 }).Problem(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        yield return new object[] { "an empty list", Sample() with { Index = 0, Count = 0 } };
        yield return new object[] { "a list past the cap", Sample() with { Count = HistoryPageData.MaxCount + 1 } };
        yield return new object[] { "a page past the list", Sample() with { Index = 12 } };
        yield return new object[] { "a negative page", Sample() with { Index = -1 } };
        yield return new object[] { "an offset past +14:00", Sample() with { HostUtcOffsetMinutes = HistoryPageData.MaxOffsetMinutes + 1 } };
        yield return new object[] { "an offset past -14:00", Sample() with { HostUtcOffsetMinutes = -HistoryPageData.MaxOffsetMinutes - 1 } };
        yield return new object[] { "no run", Sample() with { Entry = null } };
        yield return new object[] { "a time before year 1", Sample() with { Entry = new RunHistoryData { RunId = "r", TimestampUtc = -1 } } };
        yield return new object[] { "a time past year 9999", Sample() with { Entry = new RunHistoryData { RunId = "r", TimestampUtc = DateTime.MaxValue.Ticks + 1 } } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Pages_out_of_their_limits_are_refused_when_written_and_never_sent(string what, HistoryPageData page)
    {
        Assert.False(string.IsNullOrEmpty(what));
        Assert.NotNull(page.Problem());
        Assert.ThrowsAny<ArgumentException>(() => page.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.HistoryPage(5, page));
    }

    [Fact]
    public void A_run_over_its_own_limits_is_refused_too()
    {
        HistoryPageData page = Sample() with { Entry = new RunHistoryData { RunId = new string('x', RunHistoryData.MaxTextBytes + 1) } };
        Assert.Null(page.Problem()); // the page itself is fine; the run's own check refuses it as it's written
        Assert.ThrowsAny<ArgumentException>(() => page.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.HistoryPage(5, page));
    }

    [Fact]
    public void Crafted_pages_that_break_a_rule_are_refused()
    {
        byte[] good = Encoded(Sample());
        Assert.NotNull(Read(good));
        for (int length = 0; length < good.Length; length += 3)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));
        byte[] entry = EntryBytes(RunHistoryDataTests.Sample());
        Assert.NotNull(Read(Page(0, 1, 0, entry)));
        Assert.NotNull(Read(Page(999, 1000, 840, entry)));
        Assert.Null(Read(Page(1, 1, 0, entry)));
        Assert.Null(Read(Page(0, 0, 0, entry)));
        Assert.Null(Read(Page(0, 1001, 0, entry)));
        Assert.Null(Read(Page(0, ushort.MaxValue, 0, entry)));
        Assert.Null(Read(Page(0, 1, 841, entry)));
        Assert.Null(Read(Page(0, 1, short.MinValue, entry)));
        Assert.Null(Read(Page(0, 1, 0, EntryBytes(new RunHistoryData { TimestampUtc = long.MaxValue }))));
        Assert.Null(Read(Page(0, 1, 0, Array.Empty<byte>())));

        static byte[] Page(ushort index, ushort count, short offset, byte[] entryBytes)
        {
            var writer = new BitWriter();
            writer.WriteUInt16(index);
            writer.WriteUInt16(count);
            writer.WriteInt16(offset);
            writer.WriteBytes(entryBytes);
            return writer.ToArray();
        }
    }

    [Fact]
    public void An_attachment_of_a_page_round_trips_and_is_only_a_page()
    {
        MenuAttachment sent = MenuAttachment.HistoryPage(31, Sample());
        Assert.Equal((MenuAttachmentKind.HistoryPage, MenuAttachment.HistoryPageFormat), (sent.Kind, sent.Format));
        Assert.Contains("history page for #31", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetRunSummary(out _));
        Assert.False(sent.TryGetUnlockStates(out _));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetHistoryPage(out HistoryPageData? page));
        Assert.Equal(Encoded(Sample()), Encoded(page!));

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.HistoryPage(0, Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.HistoryPage(1, null!));
        Assert.False(MenuAttachment.RunSummary(31, RunHistoryDataTests.Sample()).TryGetHistoryPage(out _));

        // A run summary's body isn't a page, and a page's body isn't a run summary.
        Assert.Null(UnlockStatesDataTests.Wire(31, MenuAttachmentKind.HistoryPage, MenuAttachment.HistoryPageFormat, EntryBytes(RunHistoryDataTests.Sample())));
        Assert.Null(UnlockStatesDataTests.Wire(31, MenuAttachmentKind.RunSummary, MenuAttachment.RunSummaryFormat, Encoded(Sample())));
        Assert.Null(UnlockStatesDataTests.Wire(31, MenuAttachmentKind.HistoryPage, 2, Encoded(Sample())));
    }

    [Fact]
    public void The_date_moves_to_show_the_hosts_wall_clock_here()
    {
        var utc = new DateTime(2026, 10, 31, 18, 30, 0, DateTimeKind.Utc);

        // Host at UTC+01:00, this PC at UTC-07:00: this PC's local time of the moved ticks is the host's 19:30.
        long moved = HistoryPageData.LocalTicks(utc.Ticks, 60, _ => TimeSpan.FromHours(-7));
        Assert.Equal(new DateTime(2026, 10, 31, 19, 30, 0), new DateTime(moved).AddHours(-7));

        // The same zone: nothing moves.
        Assert.Equal(utc.Ticks, HistoryPageData.LocalTicks(utc.Ticks, 120, _ => TimeSpan.FromHours(2)));

        // This PC changes its clocks between the run's instant and the moved one (a daylight-saving edge at 02:00 UTC):
        // the offset at the moved instant is the one its game uses, and the host's wall clock still shows.
        var edge = new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc);
        TimeSpan Local(DateTime at) => at < edge ? TimeSpan.FromHours(-4) : TimeSpan.FromHours(-5);
        var run = new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc); // the host at UTC+00:00 saw 01:00
        long shifted = HistoryPageData.LocalTicks(run.Ticks, 0, Local);
        var instant = new DateTime(shifted, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0), new DateTime(shifted + Local(instant).Ticks));

        // Never out of DateTime's range, whatever the offsets.
        Assert.Equal(0, HistoryPageData.LocalTicks(0, -840, _ => TimeSpan.FromHours(14)));
        Assert.Equal(DateTime.MaxValue.Ticks, HistoryPageData.LocalTicks(DateTime.MaxValue.Ticks, 840, _ => TimeSpan.FromHours(-14)));
        Assert.Equal(0, HistoryPageData.LocalTicks(-5, 0, _ => TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => HistoryPageData.LocalTicks(0, 0, null!));
    }

    [Theory]
    [InlineData(-300)] // the host in UTC-05:00
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(540)]
    public void Every_wall_clock_this_PC_has_shows_as_the_hosts_across_both_clock_changes(int hostOffset)
    {
        // This PC as New York in 2026: clocks forward at 07:00 UTC on March 8 (no 02:xx that day), back at 06:00 UTC on
        // November 1 (01:xx twice).
        var forward = new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc);
        var back = new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
        TimeSpan Here(DateTime at) => at >= forward && at < back ? TimeSpan.FromHours(-4) : TimeSpan.FromHours(-5);

        foreach (DateTime day in new[] { new DateTime(2026, 3, 8), new DateTime(2026, 11, 1) })
        {
            for (int minutes = -48 * 60; minutes <= 72 * 60; minutes += 10)
            {
                DateTime wall = day.AddMinutes(minutes);
                long utc = wall.Ticks - (hostOffset * TimeSpan.TicksPerMinute);
                var shown = new DateTime(HistoryPageData.LocalTicks(utc, hostOffset, Here), DateTimeKind.Utc);
                DateTime read = new DateTime(shown.Ticks + Here(shown).Ticks);
                if (wall >= new DateTime(2026, 3, 8, 2, 0, 0) && wall < new DateTime(2026, 3, 8, 3, 0, 0))
                {
                    // A time this PC's clock never shows: the nearest it can, an hour away (one hour a year, on one PC's
                    // clock change). Not out by more.
                    Assert.Equal(TimeSpan.FromHours(1), (wall - read).Duration());
                    continue;
                }

                Assert.Equal(wall, read);
            }
        }
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[] seed = Encoded(Sample() with { Entry = new RunHistoryData { RunId = "run-1", SquadId = "s", TimestampUtc = 638_000_000_000_000_000 } });
        var random = new Random(77);
        int read = 0;
        for (int round = 0; round < 6000; round++)
        {
            byte[] bytes = (byte[])seed.Clone();
            if (round % 4 == 3)
            {
                bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);
            }
            else
            {
                for (int flips = random.Next(1, 3); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            var reader = new BitReader(bytes);
            if (!HistoryPageData.TryRead(ref reader, out HistoryPageData? page))
            {
                Assert.Null(page);
                continue;
            }

            read++;
            Assert.Null(page!.Problem());
            Assert.InRange(page.Entry!.TimestampUtc, 0, DateTime.MaxValue.Ticks);
            _ = page.ToString();
        }

        Assert.True(read > 0, "some changed pages should still read");
    }
}
