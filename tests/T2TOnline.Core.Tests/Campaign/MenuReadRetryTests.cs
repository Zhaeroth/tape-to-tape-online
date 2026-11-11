using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuReadRetryTests
{
    // The host's menu refresh: 0.4 s, up to 1.6 s.
    private static MenuReadRetry Make() => new(0.4, 1.6);

    [Fact]
    public void A_menu_read_before_the_game_filled_it_is_read_again_and_its_text_comes_back()
    {
        MenuReadRetry retry = Make();
        Assert.True(retry.MayRead(0));

        // Its open: the first failure is logged, and the next read waits the refresh interval.
        Assert.Equal(MenuReadFailure.First, retry.Failed(10));
        Assert.Equal((1, 0.4), (retry.Failures, retry.Wait));
        Assert.False(retry.MayRead(10.3));
        Assert.True(retry.MayRead(10.5));

        // A moment later it reads: once said, and the count is forgotten.
        Assert.True(retry.Succeeded());
        Assert.Equal((0, 0.0), (retry.Failures, retry.Wait));
        Assert.True(retry.MayRead(10.5));
        Assert.False(retry.Succeeded());
    }

    [Fact]
    public void Three_failures_in_a_row_give_up_with_growing_waits_and_one_line_each()
    {
        MenuReadRetry retry = Make();
        Assert.Equal(MenuReadFailure.First, retry.Failed(0));
        Assert.Equal(0.4, retry.Wait);
        Assert.Equal(MenuReadFailure.Again, retry.Failed(0.4));
        Assert.Equal(0.8, retry.Wait);
        Assert.False(retry.MayRead(1.1));
        Assert.True(retry.MayRead(1.25));
        Assert.False(retry.GaveUp);

        Assert.Equal(MenuReadFailure.GaveUp, retry.Failed(1.25));
        Assert.True(retry.GaveUp);
        Assert.Equal(MenuReadRetry.MaxFailures, retry.Failures);
        Assert.False(retry.MayRead(1000));

        // Nothing more is said or counted once it gave up.
        Assert.Equal(MenuReadFailure.Again, retry.Failed(1000));
        Assert.Equal(MenuReadRetry.MaxFailures, retry.Failures);
    }

    [Fact]
    public void The_wait_stops_growing_at_the_longest_refresh()
    {
        var retry = new MenuReadRetry(0.4, 1.0);
        retry.Failed(0);
        retry.Failed(0);
        Assert.Equal(0.8, retry.Wait);

        var longer = new MenuReadRetry(1, 1);
        longer.Failed(0);
        longer.Failed(0);
        Assert.Equal(1, longer.Wait);
    }

    [Fact]
    public void A_menu_that_fails_again_after_working_is_quiet_and_gets_the_full_count_again()
    {
        MenuReadRetry retry = Make();
        Assert.Equal(MenuReadFailure.First, retry.Failed(0));
        Assert.Equal(MenuReadFailure.Again, retry.Failed(0.4));
        Assert.True(retry.Succeeded());

        // Its first failure was logged already, and so was its recovery: the next ones are quiet until a give-up.
        Assert.Equal(MenuReadFailure.Again, retry.Failed(5));
        Assert.Equal(MenuReadFailure.Again, retry.Failed(5.4));
        Assert.False(retry.Succeeded());
        Assert.Equal(MenuReadFailure.Again, retry.Failed(6));
        Assert.Equal(MenuReadFailure.Again, retry.Failed(6.4));
        Assert.Equal(MenuReadFailure.GaveUp, retry.Failed(7.2));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, 0.5)]
    [InlineData(1, double.NaN)]
    public void Waits_that_make_no_sense_are_refused(double first, double longest) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MenuReadRetry(first, longest));
}
