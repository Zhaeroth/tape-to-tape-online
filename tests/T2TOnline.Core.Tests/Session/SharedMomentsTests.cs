using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// Which achievements a host shares (D34, D48): the ones the game unlocks straight from a match or run event, which a
/// friend's game can't reach by itself; never one that counts a player's own progress.
/// </summary>
public class SharedMomentsTests
{
    [Theory]
    [InlineData("MERCY")]
    [InlineData("SCORING_SQUAD")]
    [InlineData("ELITE_WIN")]
    [InlineData("WIN_ACT2")] // AchievementManager.OnGameWin, act 2's boss won (Nov 1 build)
    [InlineData("HEADSHOT_REDIRECT")]
    public void A_match_events_achievement_is_shared_with_the_friends_who_watched_that_match(string name)
    {
        Assert.True(SharedMoments.Contains(name));
        Assert.Equal(MomentSource.Match, SharedMoments.SourceOf(name));
    }

    [Theory]
    [InlineData("WIN_ACT3")]
    [InlineData("GAWAIN")]
    public void A_run_events_achievement_is_shared_with_everyone_on_the_shared_run(string name)
    {
        Assert.True(SharedMoments.Contains(name));
        Assert.Equal(MomentSource.Run, SharedMoments.SourceOf(name));
    }

    [Theory]
    [InlineData("KNOCKDOWN_SWEEP")] // an unlock's achievement (the unlock registry), on the Nov 1 build
    [InlineData("UNLOCK_V2_SWEET_SPOT")]
    [InlineData("HAT_TRICK")]
    [InlineData("WIN_ALL_SQUADS")]
    [InlineData("ALL_BENCH_QUESTS")]
    [InlineData("BENCH_QUEST_KIRBY")]
    [InlineData("WIN_ACT1")] // act 1's boss win unlocks nothing in OnGameWin on the Nov 1 build
    [InlineData("win_act2")]
    [InlineData("")]
    [InlineData(null)]
    public void An_achievement_of_a_players_own_progress_is_never_sent(string? name) => Assert.False(SharedMoments.Contains(name));

    [Fact]
    public void There_are_seven_shared_moments() => Assert.Equal(7, SharedMoments.Names.Count);
}
