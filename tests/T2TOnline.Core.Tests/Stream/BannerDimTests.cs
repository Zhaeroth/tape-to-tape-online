using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class BannerDimTests
{
    private static BannerDimFollow Dimmed(double at = 10)
    {
        var follow = new BannerDimFollow();
        Assert.False(follow.Message(false, at - 1));
        Assert.True(follow.Message(true, at));
        return follow;
    }

    [Fact]
    public void It_goes_up_when_the_hosts_dim_starts_and_only_then()
    {
        var follow = new BannerDimFollow();
        Assert.False(follow.Message(null, 1)); // an older message with no value yet
        Assert.False(follow.Message(false, 2));
        Assert.False(follow.Up);
        Assert.True(follow.Message(true, 3));
        Assert.True(follow.Up);
        Assert.False(follow.Message(true, 4)); // still up: not a second time
    }

    [Fact]
    public void A_friend_joining_in_the_middle_of_it_dims_there_too()
    {
        var follow = new BannerDimFollow();
        Assert.True(follow.Message(true, 1));
        Assert.True(follow.Up);
    }

    [Fact]
    public void It_comes_off_a_moment_after_the_hosts_dim_is_over()
    {
        BannerDimFollow follow = Dimmed();
        Assert.Equal(BannerDimEnd.None, follow.Watch(false, 12));
        Assert.False(follow.Message(false, 13));
        Assert.Equal(BannerDimEnd.None, follow.Watch(false, 13.5));
        Assert.Equal(BannerDimEnd.HostEnded, follow.Watch(false, 13 + BannerDimFollow.AfterHostSeconds));
        Assert.False(follow.Up);
        Assert.Equal(BannerDimEnd.None, follow.Watch(false, 14)); // once
    }

    [Fact]
    public void The_menu_taking_the_backdrop_over_takes_it_off_at_once()
    {
        BannerDimFollow follow = Dimmed();
        Assert.Equal(BannerDimEnd.MenuTookOver, follow.Watch(true, 10.1));
        Assert.False(follow.Up);
    }

    [Fact]
    public void A_drop_takes_it_off_and_it_does_not_come_back_while_the_host_still_dims()
    {
        BannerDimFollow follow = Dimmed();
        Assert.Equal(BannerDimEnd.None, follow.Watch(false, 10 + BannerDimFollow.SilenceSeconds - 0.1));
        Assert.Equal(BannerDimEnd.HostSilent, follow.Watch(false, 10 + BannerDimFollow.SilenceSeconds));
        Assert.False(follow.Message(true, 20)); // the link came back with the same dim
        Assert.False(follow.Up);
    }

    [Fact]
    public void It_never_outlives_its_limit_whatever_the_host_says()
    {
        BannerDimFollow follow = Dimmed();
        for (double t = 11; t < 10 + BannerDimFollow.MaxSeconds; t += 1)
        {
            Assert.False(follow.Message(true, t));
            Assert.Equal(BannerDimEnd.None, follow.Watch(false, t));
        }

        Assert.Equal(BannerDimEnd.TimedOut, follow.Watch(false, 10 + BannerDimFollow.MaxSeconds));
        Assert.False(follow.Up);
    }

    [Fact]
    public void A_dim_back_on_cancels_the_hosts_end()
    {
        BannerDimFollow follow = Dimmed();
        follow.Message(false, 11);
        follow.Message(true, 11.5);
        Assert.Equal(BannerDimEnd.None, follow.Watch(false, 13));
        Assert.True(follow.Up);
    }

    [Fact]
    public void A_new_host_dim_after_a_host_end_goes_up_again()
    {
        BannerDimFollow follow = Dimmed();
        follow.Message(false, 11);
        Assert.Equal(BannerDimEnd.HostEnded, follow.Watch(false, 12));
        Assert.True(follow.Message(true, 13));
    }

    [Fact]
    public void Taken_down_another_way_or_reset_it_is_not_up()
    {
        BannerDimFollow follow = Dimmed();
        follow.TakenDown();
        Assert.False(follow.Up);
        Assert.Equal(BannerDimEnd.None, follow.Watch(true, 11));

        follow = Dimmed();
        follow.Reset();
        Assert.False(follow.Up);
        Assert.True(follow.Message(true, 12)); // a new playback hears it as new
    }

    [Theory]
    [InlineData(BannerDimEnd.HostEnded, "the host's dim is over")]
    [InlineData(BannerDimEnd.MenuTookOver, "the in-match menu's blurred backdrop took over")]
    [InlineData(BannerDimEnd.HostSilent, "no HUD values from the host for 6 s")]
    [InlineData(BannerDimEnd.TimedOut, "it was up for 20 s")]
    public void Each_end_has_its_words(BannerDimEnd end, string words) => Assert.Equal(words, BannerDimFollow.Words(end));
}
