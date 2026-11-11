using T2TOnline.Core.Hosting;

namespace T2TOnline.Core.Tests.Hosting;

/// <summary>When the AI takes a dropped friend's skater, and when it's handed back.</summary>
public class StandInTimerTests
{
    private const long Away = StandInTimer.AwayMicros;
    private const long Back = StandInTimer.BackMicros;

    [Fact]
    public void The_AI_takes_over_after_the_friend_has_been_away_long_enough_with_a_skater()
    {
        var timer = new StandInTimer();
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: true, 0));
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: true, Away - 1));
        Assert.Equal(StandInStep.Start, timer.Step(away: true, hasSkater: true, Away));
        Assert.True(timer.Active);
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: true, Away + 1)); // once
    }

    [Fact]
    public void Without_a_skater_it_waits_for_one()
    {
        var timer = new StandInTimer();
        timer.Step(true, false, 0);
        Assert.Equal(StandInStep.None, timer.Step(true, false, Away * 2)); // between matches
        Assert.Equal(StandInStep.Start, timer.Step(true, true, (Away * 2) + 1));
    }

    [Fact]
    public void Coming_back_before_the_AI_took_over_cancels_it()
    {
        var timer = new StandInTimer();
        timer.Step(true, true, 0);
        timer.Step(false, true, Away - 1);
        Assert.Equal(StandInStep.None, timer.Step(true, true, Away)); // away again: the count starts over
        Assert.Equal(StandInStep.None, timer.Step(true, true, (Away * 2) - 1));
        Assert.Equal(StandInStep.Start, timer.Step(true, true, Away * 2));
    }

    [Fact]
    public void The_skater_goes_back_once_the_friend_is_back_and_steady()
    {
        var timer = new StandInTimer();
        timer.Step(true, true, 0);
        timer.Step(true, true, Away);
        Assert.Equal(StandInStep.None, timer.Step(false, true, Away + 10));
        Assert.Equal(StandInStep.None, timer.Step(false, true, Away + 10 + Back - 1));
        Assert.Equal(StandInStep.HandBack, timer.Step(false, true, Away + 10 + Back));

        // The game owned the skater that frame: asked again next frame, still due.
        Assert.Equal(StandInStep.HandBack, timer.Step(false, true, Away + 20 + Back));
        timer.HandedBack();
        Assert.False(timer.Active);
        Assert.Equal(StandInStep.None, timer.Step(false, true, Away + 30 + Back));
    }

    [Fact]
    public void A_friend_whose_game_couldnt_load_the_match_is_stood_in_for_at_once_until_it_plays_it()
    {
        // Their game said it can't play this match: their pad would drive a skater they can't see. No 3 s wait.
        var timer = new StandInTimer();
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: false, 0, atOnce: true)); // the faceoff hasn't given them one yet
        Assert.Equal(StandInStep.Start, timer.Step(away: true, hasSkater: true, 1, atOnce: true));
        Assert.True(timer.Active);
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: true, Away * 10, atOnce: true)); // once

        // Their game plays the match now: handed back as for any friend who's back.
        Assert.Equal(StandInStep.None, timer.Step(away: false, hasSkater: true, Away * 10 + 1));
        Assert.Equal(StandInStep.HandBack, timer.Step(away: false, hasSkater: true, Away * 10 + 1 + Back));
    }

    [Fact]
    public void A_drop_still_waits_its_three_seconds_when_nothing_says_at_once()
    {
        var timer = new StandInTimer();
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: true, 0, atOnce: false));
        Assert.Equal(StandInStep.None, timer.Step(away: true, hasSkater: true, Away - 1));
        Assert.Equal(StandInStep.Start, timer.Step(away: true, hasSkater: true, Away));
    }

    [Fact]
    public void Going_away_again_starts_the_back_count_over()
    {
        var timer = new StandInTimer();
        timer.Step(true, true, 0);
        timer.Step(true, true, Away);
        timer.Step(false, true, Away + 1);
        timer.Step(true, true, Away + Back - 1); // a blip
        Assert.Equal(StandInStep.None, timer.Step(false, true, Away + Back));
        Assert.Equal(StandInStep.None, timer.Step(false, true, Away + (2 * Back) - 1));
        Assert.Equal(StandInStep.HandBack, timer.Step(false, true, Away + (2 * Back)));
    }
}
