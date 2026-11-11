using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class DevFileRetentionTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private static FileStamp Item(string name, int minutes, long megabytes = 1) => new(name, megabytes * 1024 * 1024, Start.AddMinutes(minutes));

    [Fact]
    public void Twenty_eight_captures_keep_the_newest_ten()
    {
        List<FileStamp> captures = Enumerable.Range(0, 28).Select(i => Item($"snapshots-{i:00}.t2tsnap", i)).ToList();

        IReadOnlyList<string> expired = DevFileRetention.Expired(captures, DevFileRetention.KeptCaptures);

        Assert.Equal(18, expired.Count);
        Assert.Equal("snapshots-17.t2tsnap", expired[0]); // newest of the expired first, oldest last
        Assert.Equal("snapshots-00.t2tsnap", expired[^1]);
        Assert.DoesNotContain("snapshots-18.t2tsnap", expired);
    }

    [Fact]
    public void Streams_stop_at_the_byte_limit_and_everything_older_goes()
    {
        // Newest first: 100 MB, 100 MB, 100 MB (over 250 in all), then a small one that would fit but is older.
        var streams = new[] { Item("d.t2tstream", 4, 100), Item("c.t2tstream", 3, 100), Item("b.t2tstream", 2, 100), Item("a.t2tstream", 1, 1) };

        Assert.Equal(new[] { "b.t2tstream", "a.t2tstream" }, DevFileRetention.Expired(streams, DevFileRetention.KeptStreams, DevFileRetention.MaxStreamBytes));
        Assert.Empty(DevFileRetention.Expired(streams, DevFileRetention.KeptStreams));
    }

    [Fact]
    public void The_newest_is_kept_even_alone_over_the_limit()
    {
        var streams = new[] { Item("big.t2tstream", 2, 300), Item("old.t2tstream", 1, 1) };

        Assert.Equal(new[] { "old.t2tstream" }, DevFileRetention.Expired(streams, 20, DevFileRetention.MaxStreamBytes));
    }

    [Fact]
    public void The_file_being_written_is_kept_whatever_its_time()
    {
        // A file just created can carry an older write time than the rest (a clock set back): it still stays.
        var files = new[] { Item("new.t2tstream", -60, 0), Item("b.t2tstream", 2), Item("a.t2tstream", 1) };

        Assert.Equal(new[] { "a.t2tstream" }, DevFileRetention.Expired(files, 2, long.MaxValue, protect: "NEW.t2tstream"));
        Assert.Equal(new[] { "new.t2tstream" }, DevFileRetention.Expired(files, 2));
    }

    [Fact]
    public void Ties_go_by_name_and_duplicates_count_once()
    {
        var files = new[] { Item("a.t2tsnap", 0), Item("b.t2tsnap", 0), Item("B.T2TSNAP", 0) };

        Assert.Equal(new[] { "a.t2tsnap" }, DevFileRetention.Expired(files, 1));
        Assert.Empty(DevFileRetention.Expired(Array.Empty<FileStamp>(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DevFileRetention.Expired(files, 0));
        Assert.Throws<ArgumentNullException>(() => DevFileRetention.Expired(null!, 1));
    }

    [Theory]
    [InlineData("20261105T100000Z", true)]
    [InlineData("20261105T100000", false)]
    [InlineData("20261105-100000Z", false)]
    [InlineData("summary", false)]
    [InlineData("run.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_tour_stamps_are_tours(string? name, bool tour)
    {
        Assert.Equal(tour, DevFileRetention.TryParseTour(name, out DateTime started));
        if (tour)
        {
            Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), started);
            Assert.Equal(DateTimeKind.Utc, started.Kind);
        }
    }

    [Fact]
    public void A_name_list_stops_at_a_few()
    {
        Assert.Equal("a, b", DevFileRetention.NameList(new[] { "a", "b" }));
        Assert.Equal("a, b and 3 more", DevFileRetention.NameList(new[] { "a", "b", "c", "d", "e" }, 2));
        Assert.Equal(string.Empty, DevFileRetention.NameList(Array.Empty<string>()));
    }
}
