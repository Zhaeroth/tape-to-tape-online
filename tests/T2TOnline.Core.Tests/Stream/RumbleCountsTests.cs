using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>What each side's log says about rumble: the friend's outcomes, the host's per-player tally.</summary>
public class RumbleCountsTests
{
    [Fact]
    public void A_friend_s_match_names_what_played_what_was_limited_and_why_the_rest_didn_t()
    {
        var counts = new RumbleCounts();
        Add(counts, RumbleOutcome.Played, 38);
        Add(counts, RumbleOutcome.TooMany, 2);
        Add(counts, RumbleOutcome.TooLong, 1);
        Add(counts, RumbleOutcome.ForOthers, 77);
        Assert.Equal((41L, 3L, 0L, 118L), (counts.ForYou, counts.Limited, counts.Skipped, counts.Total));
        Assert.Equal("41 for your skater (Rewired player 1): 38 played on your pad, 3 limited (2 over 8 a second, 1 after 3 s of buzzing), 0 skipped; " +
            "77 for the other players", counts.Describe(1));

        Add(counts, RumbleOutcome.SettingOff, 4);
        Add(counts, RumbleOutcome.NoPad, 1);
        Add(counts, RumbleOutcome.Failed, 2);
        Assert.Equal("46 for your skater (Rewired player 1): 38 played on your pad, 3 limited (2 over 8 a second, 1 after 3 s of buzzing), 5 skipped " +
            "(your Controller rumble setting off 4, no pad (keyboard and mouse) 1); 77 for the other players; 2 refused or failed", counts.Describe(1));
    }

    [Fact]
    public void A_keyboard_friend_s_match_says_nothing_could_play()
    {
        var counts = new RumbleCounts();
        Add(counts, RumbleOutcome.NoPad, 12);
        Assert.Equal("12 for your skater (Rewired player 2): 0 played on your pad, 0 limited, 12 skipped (no pad (keyboard and mouse) 12); 0 for the other players",
            counts.Describe(2));
        Assert.Contains("this PC isn't a friend in a session", counts.Describe(-1), System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_adds_up_its_matches()
    {
        var session = new RumbleCounts();
        var match = new RumbleCounts();
        Add(match, RumbleOutcome.Played, 10);
        Add(match, RumbleOutcome.TooMany, 1);
        session.AddFrom(match);
        session.AddFrom(match);
        Assert.Equal((20L, 2L), (session[RumbleOutcome.Played], session.Limited));
        match.Clear();
        Assert.Equal(0, match.Total);
        Assert.Equal(22, session.Total);
    }

    [Fact]
    public void The_host_s_tally_names_each_friend_s_player_and_ignores_impossible_ones()
    {
        var match = new RumbleTally();
        Assert.Equal("none", match.Describe());
        for (int i = 0; i < 41; i++)
        {
            match.Add(1);
        }

        match.Add(3);
        match.Add(3);
        match.Add(-1);
        match.Add(11);
        Assert.Equal((43L, 41L, 2L, 0L), (match.Total, match.Of(1), match.Of(3), match.Of(11)));
        Assert.Equal("Rewired player 1: 41, Rewired player 3: 2", match.Describe());

        var session = new RumbleTally();
        session.AddFrom(match);
        session.AddFrom(match);
        Assert.Equal("Rewired player 1: 82, Rewired player 3: 4", session.Describe());
        match.Clear();
        Assert.Equal((0L, "none"), (match.Total, match.Describe()));
    }

    private static void Add(RumbleCounts counts, RumbleOutcome outcome, int times)
    {
        for (int i = 0; i < times; i++)
        {
            counts.Add(outcome);
        }
    }
}
