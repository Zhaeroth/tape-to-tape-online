using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class MapRevealHoldTests
{
    private const ulong Alex = 1;
    private const ulong Sam = 2;

    private static MapRevealHold Begun(params ulong[] friends)
    {
        var hold = new MapRevealHold();
        hold.Begin(friends, 100);
        return hold;
    }

    [Fact]
    public void With_no_friends_nothing_waits()
    {
        var hold = new MapRevealHold();
        Assert.False(hold.Begin(System.Array.Empty<ulong>(), 1));
        Assert.False(hold.Active);
    }

    [Fact]
    public void It_waits_for_every_friend_then_as_long_as_the_hosts_reveal_took()
    {
        MapRevealHold hold = Begun(Alex, Sam);
        hold.ViewPublished(7, 101); // the host's map is in, its view goes out

        // The host's game wants to play it a second after the view (its transition's hide).
        const double due = 102;
        Assert.False(hold.Release(due, 102, out _));
        hold.Report(Alex, 7, following: true, 101.5);
        Assert.False(hold.Release(due, 102, out _)); // Sam's map isn't in
        hold.Report(Sam, 8, following: true, 101.7); // a later view counts
        Assert.False(hold.Release(due, 102.6, out _)); // their reveal comes a second after Sam's report: 102.7
        Assert.True(hold.Release(due, 102.7, out string why));
        Assert.Equal("every friend's game has the map", why);
    }

    [Fact]
    public void A_friend_who_had_it_long_before_lets_it_play_at_once()
    {
        MapRevealHold hold = Begun(Alex);
        hold.ViewPublished(7, 101);
        hold.Report(Alex, 7, true, 101.2);
        Assert.True(hold.Release(dueAt: 105, now: 105, out _) == false); // 1.2 + 4 s of reveal says 105.2
        Assert.True(hold.Release(dueAt: 105, now: 105.2, out _));
    }

    [Fact]
    public void Its_moment_before_the_view_means_no_reveal_to_wait_for()
    {
        MapRevealHold hold = Begun(Alex);
        hold.ViewPublished(7, 103);
        hold.Report(Alex, 7, true, 103.5);
        Assert.True(hold.Release(dueAt: 102, now: 103.5, out _));
    }

    [Fact]
    public void A_report_of_an_older_view_or_not_following_does_not_count()
    {
        MapRevealHold hold = Begun(Alex);
        hold.Report(Alex, 7, true, 100.1); // before the view
        hold.ViewPublished(7, 101);
        hold.Report(Alex, 6, true, 101.1);
        hold.Report(Alex, 7, following: false, 101.2);
        Assert.False(hold.Release(101, 101.5, out _));
        hold.Report(Alex, 7, true, 101.3);
        Assert.True(hold.Release(101, 101.5, out _));
    }

    [Fact]
    public void A_later_view_does_not_move_the_one_reports_count_from()
    {
        MapRevealHold hold = Begun(Alex);
        hold.ViewPublished(7, 101);
        hold.ViewPublished(9, 101.5); // the cursor moved
        hold.Report(Alex, 7, true, 101.6);
        Assert.True(hold.Release(101, 101.6, out _));
    }

    [Fact]
    public void It_never_waits_past_its_limit()
    {
        MapRevealHold hold = Begun(Alex);
        hold.ViewPublished(7, 101);
        const double due = 102;
        Assert.False(hold.Release(due, due + MapRevealHold.MaxHoldSeconds - 0.01, out _));
        Assert.True(hold.Release(due, due + MapRevealHold.MaxHoldSeconds, out string why));
        Assert.Equal("1 friend(s) still loading the map after 2 s", why);
    }

    [Fact]
    public void Its_limit_also_holds_when_the_friends_reveal_would_be_later()
    {
        MapRevealHold hold = Begun(Alex);
        hold.ViewPublished(7, 101);
        hold.Report(Alex, 7, true, 110);
        Assert.True(hold.Release(dueAt: 109, now: 109 + MapRevealHold.MaxHoldSeconds, out _));
    }

    [Fact]
    public void It_goes_on_when_no_friend_is_left()
    {
        MapRevealHold hold = Begun(Alex);
        hold.FriendLeft(Alex);
        Assert.True(hold.Release(100, 100, out string why));
        Assert.Equal("no friend is left in the session", why);
    }

    [Fact]
    public void It_stays_ready_for_a_while_then_expires()
    {
        MapRevealHold hold = Begun(Alex);
        Assert.False(hold.Expired(100 + MapRevealHold.ReadySeconds - 1));
        Assert.True(hold.Expired(100 + MapRevealHold.ReadySeconds));
        hold.End();
        Assert.False(hold.Active);
        Assert.False(hold.Expired(1000));
    }

    [Fact]
    public void A_new_map_starts_over()
    {
        MapRevealHold hold = Begun(Alex);
        hold.ViewPublished(7, 101);
        hold.Report(Alex, 7, true, 101.1);
        Assert.True(hold.Begin(new[] { Sam }, 200));
        hold.Report(Sam, 7, true, 200.1); // no view of this map yet
        Assert.False(hold.Release(200, 200.5, out _));
        Assert.Equal("Sam didn't", hold.DescribeWaits(_ => "Sam"));
    }

    [Fact]
    public void Each_friends_wait_is_named()
    {
        MapRevealHold hold = Begun(Alex, Sam);
        hold.ViewPublished(7, 101);
        hold.Report(Alex, 7, true, 101.25);
        Assert.Equal("Alex had it after 250 ms, Sam didn't", hold.DescribeWaits(f => f == Alex ? "Alex" : "Sam"));
        Assert.Equal("no friends", new MapRevealHold().DescribeWaits(_ => "x"));
    }
}
