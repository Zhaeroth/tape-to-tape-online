using System;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>The golfer comic's marks at a session's end (D42's amendment): written once when memory has strips the disk lacks.</summary>
public class ComicMarkWatchTests
{
    [Fact]
    public void Marks_new_since_the_disk_are_owed_and_named_in_order()
    {
        var watch = new ComicMarkWatch();
        Assert.False(watch.Known);
        watch.OnDisk(0b101); // strips 1 and 3 were on disk when following began
        ComicMarkVerdict verdict = watch.Judge(0b1_1111, profileLoaded: true, savable: true);
        Assert.True(verdict.Owed);
        Assert.Equal(new[] { 2, 4, 5 }, verdict.NewStrips);
        Assert.Empty(verdict.Why);

        watch.OnDisk(0b1_1111); // an allowed save wrote them
        ComicMarkVerdict done = watch.Judge(0b1_1111, true, true);
        Assert.False(done.Owed);
        Assert.Empty(done.NewStrips);
        Assert.Empty(done.Why); // nothing to say: nothing was marked
    }

    [Fact]
    public void Nothing_is_owed_when_memory_has_nothing_the_disk_lacks()
    {
        var watch = new ComicMarkWatch();
        watch.OnDisk(0b111);
        Assert.False(watch.Judge(0b111, true, true).Owed);
        ComicMarkVerdict fewer = watch.Judge(0b001, true, true); // a profile reloaded meanwhile: never written back smaller
        Assert.False(fewer.Owed);
        Assert.Empty(fewer.NewStrips);
    }

    [Fact]
    public void Marks_are_not_owed_when_the_disk_was_never_read_and_say_why_otherwise()
    {
        var unknown = new ComicMarkWatch();
        ComicMarkVerdict blind = unknown.Judge(0b1, true, true);
        Assert.False(blind.Owed);
        Assert.Contains("weren't read", blind.Why, StringComparison.Ordinal);

        var watch = new ComicMarkWatch();
        watch.OnDisk(0);
        ComicMarkVerdict notLoaded = watch.Judge(0b10, profileLoaded: false, savable: true);
        Assert.False(notLoaded.Owed);
        Assert.Equal(new[] { 2 }, notLoaded.NewStrips);
        Assert.Contains("hasn't loaded", notLoaded.Why, StringComparison.Ordinal);

        ComicMarkVerdict blocked = watch.Judge(0b10, profileLoaded: true, savable: false); // a failed load, cheats, or a restore waiting for the restart
        Assert.False(blocked.Owed);
        Assert.Equal(new[] { 2 }, blocked.NewStrips);
        Assert.Contains("won't save the profile", blocked.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Strips_cover_all_thirty_one_bits_and_not_the_sign_bit()
    {
        Assert.Empty(ComicMarkWatch.Strips(0));
        Assert.Equal(Enumerable.Range(1, ComicMarkWatch.MaxStrips), ComicMarkWatch.Strips(int.MaxValue));
        Assert.Empty(ComicMarkWatch.Strips(int.MinValue));
        Assert.Equal("none", ComicMarkWatch.Describe(0));
        Assert.Equal("1, 3, 31", ComicMarkWatch.Describe(1 | (1 << 2) | (1 << 30)));

        var watch = new ComicMarkWatch();
        watch.OnDisk(-5); // a nonsense mark count reads as none
        Assert.True(watch.Known);
        Assert.Equal(0, watch.Marks);
    }
}
