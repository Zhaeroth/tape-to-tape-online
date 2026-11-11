using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class OwnNameWatchTests
{
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;

    private static readonly byte[] Key = Enumerable.Range(1, LogTags.KeyBytes).Select(b => (byte)b).ToArray();

    [Fact]
    public void The_first_read_is_due_at_once_and_the_next_after_the_interval()
    {
        var watch = new OwnNameWatch();
        Assert.True(watch.Due(0));
        Assert.Equal(OwnNameLook.First, watch.Seen(Alex, "Alex", 5));
        Assert.False(watch.Due(5 + OwnNameWatch.LookSeconds - 0.1));
        Assert.True(watch.Due(5 + OwnNameWatch.LookSeconds));
    }

    [Fact]
    public void The_same_name_again_is_nothing_new()
    {
        var watch = new OwnNameWatch();
        watch.Seen(Alex, "Alex", 0);
        OwnNameLook look = watch.Seen(Alex, "Alex", 30);
        Assert.Equal(OwnNameLook.Same, look);
        Assert.Null(watch.Line(look, "Steam signed in"));
        Assert.True(watch.Known);
    }

    [Theory]
    [InlineData(Alex, "Alex the Second")]
    [InlineData(Sam, "Alex")]
    public void A_rename_or_another_account_is_a_change(ulong id, string name)
    {
        var watch = new OwnNameWatch();
        watch.Seen(Alex, "Alex", 0);
        Assert.Equal(OwnNameLook.Changed, watch.Seen(id, name, 30));
        Assert.Equal(OwnNameLook.Same, watch.Seen(id, name, 60));
    }

    [Theory]
    [InlineData(0UL, "Alex")]
    [InlineData(Alex, "")]
    [InlineData(Alex, "   ")]
    [InlineData(Alex, null)]
    [InlineData(Alex, "[unknown]")]
    public void No_id_or_no_name_is_not_signed_in_said_once(ulong id, string? name)
    {
        var watch = new OwnNameWatch();
        OwnNameLook first = watch.Seen(id, name, 0);
        Assert.Equal(OwnNameLook.NotSignedIn, first);
        Assert.NotNull(watch.Line(first, "a report"));
        Assert.Equal(OwnNameLook.StillNotSignedIn, watch.Seen(id, name, 30));
        Assert.Null(watch.Line(OwnNameLook.StillNotSignedIn, "a report"));
        Assert.False(watch.Known);
    }

    [Fact]
    public void Steam_signing_in_later_gives_the_first_name_then()
    {
        var watch = new OwnNameWatch();
        watch.Seen(0, string.Empty, 0);
        Assert.Equal(OwnNameLook.First, watch.Seen(Alex, "Alex", 30));
    }

    [Fact]
    public void Steam_gone_after_a_name_was_known_says_nothing_the_cleaner_still_has_it()
    {
        var watch = new OwnNameWatch();
        watch.Seen(Alex, "Alex", 0);
        OwnNameLook look = watch.Seen(0, string.Empty, 30);
        Assert.Equal(OwnNameLook.NotSignedIn, look);
        Assert.Null(watch.Line(look, "a report"));
    }

    [Fact]
    public void No_line_holds_the_name()
    {
        const string name = "Riley Quinn";
        var watch = new OwnNameWatch();
        string? first = watch.Line(watch.Seen(Alex, name, 0), "Steam signed in");
        string? changed = watch.Line(watch.Seen(Alex, name + " 2", 30), "a report");
        Assert.NotNull(first);
        Assert.NotNull(changed);
        Assert.DoesNotContain("Riley", first, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Riley", changed, System.StringComparison.Ordinal);
        Assert.Contains("Steam signed in", first, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_registered_before_any_lobby_cleans_the_mod_loader_credit_line()
    {
        // The player's own name, read at sign-in: a report before any lobby still finds it in MelonLoader's log, where the mod's
        // credit can be the same text.
        var tags = new LogTags(Key, shared: true);
        var scrub = new LogScrub(tags);
        var watch = new OwnNameWatch();
        if (watch.Seen(Alex, "Riley", 0) == OwnNameLook.First)
        {
            Assert.True(scrub.RegisterName(Alex, "Riley"));
        }

        var counts = new LogScrubCounts();
        Assert.Equal($"[18:00:02.250] by {tags.Player(Alex)}", scrub.Scrub("[18:00:02.250] by Riley", counts));
        Assert.Equal(1, counts.Names);
    }

    [Fact]
    public void A_renamed_own_name_cleans_the_old_and_the_new()
    {
        var tags = new LogTags(Key, shared: true);
        var scrub = new LogScrub(tags);
        scrub.RegisterName(Alex, "Riley");
        Assert.True(scrub.RegisterName(Alex, "Casey"));
        Assert.Equal($"{tags.Player(Alex)} was {tags.Player(Alex)}", scrub.Scrub("Casey was Riley"));
    }
}
