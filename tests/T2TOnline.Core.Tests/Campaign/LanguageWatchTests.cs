using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class LanguageWatchTests
{
    [Fact]
    public void The_first_read_is_the_starting_language_and_a_change_is_seen_once()
    {
        var watch = new LanguageWatch();
        string language = "English";
        Assert.Null(watch.Poll(0, () => language));
        Assert.Equal("English", watch.Current);
        Assert.Null(watch.Poll(500_000, () => language));

        language = "German";
        LanguageChange? change = watch.Poll(1_000_000, () => language);
        Assert.Equal(new LanguageChange("English", "German", 1), change);
        Assert.Equal("German", watch.Current);
        Assert.Null(watch.Poll(1_500_000, () => language));

        language = "Chinese (Simplified)";
        Assert.Equal(new LanguageChange("German", "Chinese (Simplified)", 2), watch.Poll(2_000_000, () => language));
        Assert.Equal(2, watch.Changes);
    }

    [Fact]
    public void The_language_is_read_at_most_twice_a_second()
    {
        var watch = new LanguageWatch();
        int asked = 0;
        string? Read()
        {
            asked++;
            return "English";
        }

        for (long now = 0; now < 2_000_000; now += 16_667)
        {
            _ = watch.Poll(now, Read); // sixty frames a second for two seconds
        }

        Assert.InRange(asked, 4, 5);
        Assert.Equal(asked, watch.Reads);
    }

    [Fact]
    public void A_read_of_nothing_changes_nothing_and_is_tried_again()
    {
        var watch = new LanguageWatch();
        Assert.Null(watch.Poll(0, () => null));
        Assert.Null(watch.Current);
        Assert.Null(watch.Poll(500_000, () => string.Empty));
        Assert.Null(watch.Current);
        Assert.Null(watch.Poll(1_000_000, () => "English"));
        Assert.Null(watch.Poll(1_500_000, () => null));
        Assert.Equal("English", watch.Current);
        Assert.Null(watch.Poll(2_000_000, () => "English"));
        Assert.Equal(0, watch.Changes);
    }

    [Fact]
    public void A_clock_that_goes_back_reads_at_once_and_a_reset_starts_over()
    {
        var watch = new LanguageWatch();
        Assert.Null(watch.Poll(50_000_000, () => "English"));
        Assert.Equal(new LanguageChange("English", "French", 1), watch.Poll(10, () => "French"));

        watch.Reset();
        Assert.Null(watch.Current);
        Assert.Equal((0, 0), (watch.Changes, watch.Reads));
        Assert.Null(watch.Poll(0, () => "Spanish"));
        Assert.Equal("Spanish", watch.Current);
    }

    [Fact]
    public void A_reader_that_throws_reaches_the_caller_and_the_watch_stays_as_it_was()
    {
        var watch = new LanguageWatch();
        Assert.Null(watch.Poll(0, () => "English"));
        Assert.Throws<InvalidOperationException>(() => watch.Poll(500_000, () => throw new InvalidOperationException("the game threw")));
        Assert.Equal("English", watch.Current);
        Assert.Throws<ArgumentNullException>(() => watch.Poll(0, null!));
    }

    [Fact]
    public void A_language_by_another_case_is_another_language()
    {
        var watch = new LanguageWatch();
        Assert.Null(watch.Poll(0, () => "German"));
        Assert.NotNull(watch.Poll(500_000, () => "german"));
    }
}
