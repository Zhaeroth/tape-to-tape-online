using System;
using System.Linq;
using System.Threading.Tasks;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TourHoldTallyTests
{
    [Fact]
    public void A_new_tally_says_nothing_was_held()
    {
        var tally = new TourHoldTally();
        Assert.Equal("achievements held back 0; allowances refused 0; writes refused 0; settings held 0; invites held 0; joins refused 0", tally.Summary());
    }

    [Fact]
    public void Each_hold_is_counted_in_its_own_place()
    {
        var tally = new TourHoldTally();
        tally.AchievementHeld();
        tally.AchievementHeld();
        tally.AllowanceRefused();
        tally.WriteRefused();
        tally.WriteRefused();
        tally.WriteRefused();
        tally.SettingHeld();
        Assert.Equal(2, tally.AchievementsHeld);
        Assert.Equal(1, tally.AllowancesRefused);
        Assert.Equal(3, tally.WritesRefused);
        Assert.Equal(1, tally.SettingsHeld);
        Assert.Equal("achievements held back 2; allowances refused 1; writes refused 3; settings held 1; invites held 0; joins refused 0", tally.Summary());
    }

    [Fact]
    public void Writes_that_hold_the_players_data_are_counted_apart_and_reset_with_the_tour()
    {
        var tally = new TourHoldTally();
        tally.WriteRefused(playerData: false); // the game's settings, which the tour puts back
        Assert.Equal(1, tally.WritesRefused);
        Assert.Equal(0, tally.PlayerDataHeld);
        tally.WriteRefused(); // a record of the mod's held in memory
        tally.WriteRefused(playerData: true);
        Assert.Equal(3, tally.WritesRefused);
        Assert.Equal(2, tally.PlayerDataHeld);
        Assert.Equal("achievements held back 0; allowances refused 0; writes refused 3; settings held 0; invites held 0; joins refused 0", tally.Summary());
        tally.Reset();
        Assert.Equal(0, tally.PlayerDataHeld);
    }

    [Fact]
    public void A_tour_that_turned_nobody_away_has_no_reach_problem()
    {
        var tally = new TourHoldTally();
        tally.AchievementHeld();
        tally.WriteRefused();
        tally.SettingHeld();
        Assert.Null(tally.ReachProblem());
    }

    [Fact]
    public void A_held_invite_is_counted_and_fails_the_tour()
    {
        var tally = new TourHoldTally();
        tally.InviteHeld();
        Assert.Equal(1, tally.InvitesHeld);
        Assert.Equal(0, tally.JoinsRefused);
        Assert.Equal("achievements held back 0; allowances refused 0; writes refused 0; settings held 0; invites held 1; joins refused 0", tally.Summary());
        string? problem = tally.ReachProblem();
        Assert.NotNull(problem);
        Assert.StartsWith("1 invite(s) held and 0 join(s) refused: a tour never invites or joins anyone", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_join_is_counted_and_fails_the_tour_as_a_held_invite_does()
    {
        var tally = new TourHoldTally();
        tally.JoinRefused();
        tally.JoinRefused();
        Assert.Equal(2, tally.JoinsRefused);
        Assert.Equal("achievements held back 0; allowances refused 0; writes refused 0; settings held 0; invites held 0; joins refused 2", tally.Summary());
        Assert.StartsWith("0 invite(s) held and 2 join(s) refused", tally.ReachProblem(), StringComparison.Ordinal);
        tally.Reset();
        Assert.Equal(0, tally.JoinsRefused);
        Assert.Null(tally.ReachProblem());
    }

    [Fact]
    public void The_settings_count_is_still_the_digits_after_the_last_settings_held()
    {
        // The settings walk reads its count from the summary part this way (LobbyUiCheck.SettingsWalk.cs).
        var tally = new TourHoldTally();
        for (int i = 0; i < 12; i++)
        {
            tally.SettingHeld();
        }

        tally.InviteHeld();
        string text = tally.Summary();
        int start = text.LastIndexOf("settings held ", StringComparison.Ordinal) + "settings held ".Length;
        int end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        Assert.Equal("12", text[start..end]);
    }

    [Fact]
    public void A_line_is_said_once_per_key_until_the_next_tour()
    {
        var tally = new TourHoldTally();
        Assert.True(tally.First("achievement MERCY"));
        Assert.False(tally.First("achievement MERCY"));
        Assert.True(tally.First("achievement ELITE_WIN"));
        Assert.True(tally.First("settings"));
        Assert.False(tally.First("settings"));
        Assert.Throws<ArgumentNullException>(() => tally.First(null!));
    }

    [Fact]
    public void A_new_tour_starts_from_nothing()
    {
        var tally = new TourHoldTally();
        tally.AchievementHeld();
        tally.AllowanceRefused();
        tally.WriteRefused();
        tally.SettingHeld();
        tally.InviteHeld();
        tally.JoinRefused();
        Assert.True(tally.First("settings"));
        tally.Reset();
        Assert.Equal("achievements held back 0; allowances refused 0; writes refused 0; settings held 0; invites held 0; joins refused 0", tally.Summary());
        Assert.True(tally.First("settings"));
    }

    [Fact]
    public void Counts_from_many_threads_add_up()
    {
        var tally = new TourHoldTally();
        Parallel.For(0, 4000, i =>
        {
            tally.WriteRefused();
            if (i % 4 == 0)
            {
                tally.AllowanceRefused();
            }
        });
        int firsts = Enumerable.Range(0, 64).AsParallel().Count(_ => tally.First("one key"));
        Assert.Equal(4000, tally.WritesRefused);
        Assert.Equal(1000, tally.AllowancesRefused);
        Assert.Equal(1, firsts);
    }
}
