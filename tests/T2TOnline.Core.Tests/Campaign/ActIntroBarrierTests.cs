using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class ActIntroBarrierTests
{
    private const ulong Alex = 1;
    private const ulong Sam = 2;

    [Fact]
    public void Nothing_is_held_without_friends()
    {
        var barrier = new ActIntroBarrier();
        Assert.False(barrier.Start(2, System.Array.Empty<ulong>(), 0));
        Assert.False(barrier.Holding);
        Assert.False(barrier.ShouldRelease(10, out _));
    }

    [Fact]
    public void The_hosts_intro_waits_until_every_friend_follows_the_intros_map_view()
    {
        var barrier = new ActIntroBarrier();
        Assert.True(barrier.Start(2, new[] { Alex, Sam }, now: 10));
        Assert.Equal(2, barrier.Act);

        // Reports before the intro's view is published (the last map's) don't count.
        barrier.Report(Alex, epoch: 4, following: true, now: 10.1);
        barrier.ViewPublished(7);
        barrier.ViewPublished(9); // a later cursor view doesn't move the mark
        barrier.Report(Alex, 6, true, 10.5);
        Assert.False(barrier.ShouldRelease(10.6, out _));

        barrier.Report(Alex, 7, true, 11.2);
        barrier.Report(Sam, 8, following: false, now: 11.3); // still on its way
        Assert.False(barrier.ShouldRelease(11.4, out _));
        barrier.Report(Sam, 9, true, 11.9);
        barrier.Report(Alex, 9, true, 12.5); // a second report keeps the first arrival
        Assert.True(barrier.ShouldRelease(12, out string why));
        Assert.Equal("every friend's game has the map", why);
        Assert.False(barrier.Holding);
        Assert.Equal(2, barrier.Held, 3);
        Assert.Equal("alex had it after 1200 ms, sam had it after 1900 ms", barrier.DescribeWaits(Name));
        Assert.False(barrier.ShouldRelease(13, out _));
    }

    [Fact]
    public void A_slow_friend_is_waited_for_at_most_six_seconds_and_one_that_leaves_not_at_all()
    {
        var barrier = new ActIntroBarrier();
        barrier.Start(1, new[] { Alex, Sam }, 0);
        barrier.ViewPublished(3);
        barrier.Report(Alex, 3, true, 1);
        Assert.False(barrier.ShouldRelease(ActIntroBarrier.MaxWaitSeconds - 0.01, out _));
        Assert.True(barrier.ShouldRelease(ActIntroBarrier.MaxWaitSeconds, out string why));
        Assert.Equal("1 friend(s) still loading the map after 6 s", why);
        Assert.Equal("alex had it after 1000 ms, sam didn't", barrier.DescribeWaits(Name));

        barrier.Start(1, new[] { Alex, Sam }, 20);
        barrier.ViewPublished(5);
        barrier.Report(Alex, 5, true, 21);
        barrier.FriendLeft(Sam);
        Assert.True(barrier.ShouldRelease(21.1, out why));
        Assert.Equal("every friend's game has the map", why);

        barrier.Start(1, new[] { Alex }, 30);
        barrier.FriendLeft(Alex);
        Assert.True(barrier.ShouldRelease(30.1, out why));
        Assert.Equal("no friend is left in the session", why);
        Assert.Equal("no friends", barrier.DescribeWaits(Name));

        barrier.Start(1, new[] { Alex }, 40);
        barrier.Abandon();
        Assert.False(barrier.Holding);
        Assert.False(barrier.ShouldRelease(99, out _));
    }

    [Fact]
    public void A_friends_intro_waits_for_the_hosts_start_at_most_eight_seconds()
    {
        var follow = new ActIntroFollow();
        Assert.True(follow.Arrived(2, hostStarted: true, now: 0));
        Assert.Equal(0, follow.Pending);

        Assert.False(follow.Arrived(2, hostStarted: false, now: 10));
        Assert.Equal(2, follow.Pending);
        Assert.False(follow.Due(false, 11, out _));
        Assert.True(follow.Due(true, 12, out bool timedOut));
        Assert.False(timedOut);
        follow.Played();
        Assert.False(follow.Due(true, 13, out _));

        follow.Arrived(3, false, 20);
        Assert.False(follow.Due(false, 20 + ActIntroFollow.MaxWaitSeconds - 0.01, out _));
        Assert.True(follow.Due(false, 20 + ActIntroFollow.MaxWaitSeconds, out timedOut));
        Assert.True(timedOut);
        follow.Reset();
        Assert.Equal(0, follow.Pending);
    }

    private static string Name(ulong friend) => friend == Alex ? "alex" : "sam";
}
