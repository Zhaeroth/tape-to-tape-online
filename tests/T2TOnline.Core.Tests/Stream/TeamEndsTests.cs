using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class TeamEndsTests
{
    private static readonly TeamEnds HomeSouth = new(HomeNorth: false, VisitorNorth: true);
    private static readonly TeamEnds HomeNorth = new(HomeNorth: true, VisitorNorth: false);

    [Fact]
    public void Nothing_happens_until_both_of_the_hosts_values_are_known()
    {
        var follower = new TeamEndsFollower();
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 0));
        Assert.True(follower.Take(TeamEnds.HomeKey, NetValue.FromBool(true)));
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 0));
        Assert.Null(follower.Followed);
    }

    [Fact]
    public void The_same_ends_at_the_start_move_nothing_and_a_later_switch_moves_both_teams()
    {
        var follower = new TeamEndsFollower();
        Host(follower, HomeSouth);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 0));
        Assert.Equal(HomeSouth, follower.Followed);

        // The end-of-period screen closes on the host: its teams switch ends.
        Host(follower, HomeNorth);
        EndsStep step = follower.Next(HomeSouth, 0);
        Assert.Equal(new EndsStep(EndsChange.Switch, HomeNorth, MoveHome: true, MoveVisitor: true), step);
        Assert.True(step.Moves);
        Assert.Equal(1, follower.Switches);
        Assert.Equal(0, follower.Starts);

        // Followed: the next full set of values (every 5 s) moves nothing.
        Host(follower, HomeNorth);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeNorth, 0));
    }

    [Fact]
    public void A_friend_joining_after_the_switch_moves_its_teams_at_the_start()
    {
        // Period 2: the friend's freshly loaded match has the first period's ends.
        var follower = new TeamEndsFollower();
        Host(follower, HomeNorth);
        Assert.Equal(new EndsStep(EndsChange.Start, HomeNorth, true, true), follower.Next(HomeSouth, 0));
        Assert.Equal(1, follower.Starts);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeNorth, 0));
    }

    [Fact]
    public void Every_switch_is_followed_period_after_period()
    {
        var follower = new TeamEndsFollower();
        TeamEnds local = HomeSouth;
        Host(follower, HomeSouth);
        Assert.False(follower.Next(local, 0).Moves);
        foreach (TeamEnds host in new[] { HomeNorth, HomeSouth, HomeNorth })
        {
            Host(follower, host);
            EndsStep step = follower.Next(local, 0);
            Assert.Equal(EndsChange.Switch, step.Change);
            Assert.Equal(host, step.Target);
            local = step.Target;
        }

        Assert.Equal(3, follower.Switches);
    }

    [Fact]
    public void Teams_that_left_the_hosts_ends_on_their_own_are_moved_back_as_a_drift()
    {
        var follower = new TeamEndsFollower();
        Host(follower, HomeNorth);
        Assert.False(follower.Next(HomeNorth, 0).Moves);
        Host(follower, HomeNorth);
        Assert.Equal(new EndsStep(EndsChange.Drift, HomeNorth, true, true), follower.Next(HomeSouth, 0));
        Assert.Equal(1, follower.Drifts);
    }

    [Fact]
    public void Only_the_team_at_the_wrong_end_moves()
    {
        var follower = new TeamEndsFollower();
        Host(follower, HomeNorth);
        Assert.Equal(new EndsStep(EndsChange.Start, HomeNorth, MoveHome: false, MoveVisitor: true), follower.Next(new TeamEnds(true, true), 0));
    }

    [Fact]
    public void Both_teams_at_one_end_that_stays_is_refused_once_per_episode_and_never_moves_anything()
    {
        var follower = new TeamEndsFollower();
        var bothNorth = new TeamEnds(true, true);
        Host(follower, bothNorth);
        EndsStep between = follower.Next(HomeSouth, 0);
        Assert.Equal(EndsChange.Between, between.Change);
        Assert.False(between.Moves);

        // Still there just short of the settle time: nothing; at it: refused, once.
        Host(follower, bothNorth);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, TeamEndsFollower.SettleMicros - 1));
        Host(follower, bothNorth);
        EndsStep refused = follower.Next(HomeSouth, TeamEndsFollower.SettleMicros);
        Assert.Equal(EndsChange.Refused, refused.Change);
        Assert.False(refused.Moves);
        Host(follower, bothNorth);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 5 * TeamEndsFollower.SettleMicros));
        Assert.Equal(1, follower.Betweens);
        Assert.Equal(1, follower.Refusals);
        Assert.Null(follower.Followed);

        // A good pair afterwards is the start; the same bad pair again later is a new episode.
        Host(follower, HomeNorth);
        Assert.Equal(EndsChange.Start, follower.Next(HomeSouth, 6 * TeamEndsFollower.SettleMicros).Change);
        Host(follower, bothNorth);
        Assert.Equal(EndsChange.Between, follower.Next(HomeNorth, 7 * TeamEndsFollower.SettleMicros).Change);
        Host(follower, bothNorth);
        Assert.Equal(EndsChange.Refused, follower.Next(HomeNorth, 9 * TeamEndsFollower.SettleMicros).Change);
        Assert.Equal(2, follower.Betweens);
        Assert.Equal(2, follower.Refusals);
    }

    [Fact]
    public void A_switch_seen_half_done_is_counted_then_followed_whole_with_no_refusal()
    {
        // The host's game switches the home team first; the visiting team's listener raises PlayingForwardsChanged before its
        // own switch, and the values sent for it have both teams at the north end. The whole pair follows in the same raise.
        var follower = new TeamEndsFollower();
        Host(follower, HomeSouth);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 1_000));

        Host(follower, new TeamEnds(true, true));
        Assert.Equal(new EndsStep(EndsChange.Between, new TeamEnds(true, true), false, false), follower.Next(HomeSouth, 2_000));
        Assert.Equal(HomeSouth, follower.Followed);

        Host(follower, HomeNorth);
        Assert.Equal(new EndsStep(EndsChange.Switch, HomeNorth, true, true), follower.Next(HomeSouth, 2_000));
        Assert.Equal(1, follower.Betweens);
        Assert.Equal(0, follower.Refusals);
        Assert.Equal(1, follower.Switches);

        // The next period's switch, the visiting team first this time (both at the south end): the same.
        Host(follower, new TeamEnds(false, false));
        Assert.Equal(EndsChange.Between, follower.Next(HomeNorth, 60_000_000).Change);
        Host(follower, HomeSouth);
        Assert.Equal(EndsChange.Switch, follower.Next(HomeNorth, 60_000_000).Change);
        Assert.Equal(2, follower.Betweens);
        Assert.Equal(0, follower.Refusals);
    }

    [Fact]
    public void Any_host_times_are_taken_without_an_overflow_and_a_clock_that_goes_back_refuses_nothing()
    {
        var bothNorth = new TeamEnds(true, true);
        foreach ((long first, long later) in new[] { (long.MinValue, long.MaxValue), (long.MaxValue, long.MinValue), (0L, -1L), (-5L, long.MaxValue) })
        {
            var follower = new TeamEndsFollower();
            Host(follower, bothNorth);
            Assert.Equal(EndsChange.Between, follower.Next(HomeSouth, first).Change);
            Host(follower, bothNorth);
            EndsChange expected = later >= first ? EndsChange.Refused : EndsChange.None;
            Assert.Equal(expected, follower.Next(HomeSouth, later).Change);
        }
    }

    [Fact]
    public void Only_the_two_keys_are_taken_and_only_flags_count()
    {
        var follower = new TeamEndsFollower();
        Assert.False(follower.Take("north", NetValue.FromBool(true)));
        Assert.False(follower.Take("score.home", NetValue.FromInt(2)));
        Assert.False(follower.Take("north.homeX", NetValue.FromBool(true)));

        // Taken (so written nowhere else), but not a flag: ignored.
        Assert.True(follower.Take(TeamEnds.HomeKey, NetValue.FromInt(1)));
        Assert.True(follower.Take(TeamEnds.VisitorKey, NetValue.Null));
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 0));

        Assert.True(follower.Take(TeamEnds.HomeKey, NetValue.FromBool(true)));
        Assert.True(follower.Take(TeamEnds.VisitorKey, NetValue.FromBool(false)));
        Assert.Equal(EndsChange.Start, follower.Next(HomeSouth, 0).Change);
    }

    [Fact]
    public void Reset_forgets_the_host_and_the_counts()
    {
        var follower = new TeamEndsFollower();
        Host(follower, HomeNorth);
        Assert.True(follower.Next(HomeSouth, 0).Moves);
        Host(follower, new TeamEnds(true, true));
        Assert.Equal(EndsChange.Between, follower.Next(HomeSouth, 0).Change);
        follower.Reset();
        Assert.Null(follower.Followed);
        Assert.Equal(0, follower.Starts);
        Assert.Equal(0, follower.Betweens);
        Assert.Equal(EndsStep.Nothing, follower.Next(HomeSouth, 0));
    }

    [Fact]
    public void Toward_puts_this_pcs_own_ends_back_and_never_follows_a_pair_at_one_end()
    {
        Assert.Equal(new EndsStep(EndsChange.Restore, HomeSouth, true, true), EndsStep.Toward(EndsChange.Restore, HomeSouth, HomeNorth));
        Assert.Equal(EndsStep.Nothing, EndsStep.Toward(EndsChange.Restore, HomeSouth, HomeSouth));
        Assert.Equal(EndsStep.Nothing, EndsStep.Toward(EndsChange.Restore, new TeamEnds(false, false), HomeNorth));
    }

    [Fact]
    public void Ends_read_as_words_and_keys_match_the_hud_naming()
    {
        Assert.Equal("home north, visitor south", HomeNorth.ToString());
        Assert.True(HomeNorth.OnePerEnd);
        Assert.False(new TeamEnds(false, false).OnePerEnd);

        // The HUD's per-team keys are "<kind>.<home|visitor>" (score.home, line.visitor).
        Assert.Equal("north.home", TeamEnds.HomeKey);
        Assert.Equal("north.visitor", TeamEnds.VisitorKey);
    }

    private static void Host(TeamEndsFollower follower, TeamEnds ends)
    {
        Assert.True(follower.Take(TeamEnds.HomeKey, NetValue.FromBool(ends.HomeNorth)));
        Assert.True(follower.Take(TeamEnds.VisitorKey, NetValue.FromBool(ends.VisitorNorth)));
    }
}
