using System;
using System.IO;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TourEndTests
{
    [Theory]
    [InlineData("profileV2.es3")]
    [InlineData("statsV2.es3")]
    [InlineData("statsV2.es3.tmp")]
    [InlineData("profileV2.es3.bac")]
    [InlineData("RunDataV2.es3")]
    [InlineData("RunHistory_00.es3")]
    [InlineData("lifetime-stats.es3")]
    [InlineData("coop-3.es3")]
    public void A_refused_save_of_the_players_progress_holds_it_in_memory(string file)
    {
        Assert.True(TourEnd.HoldsPlayerData(new[] { file }));
        Assert.True(TourEnd.HoldsPlayerData(new[] { Path.Combine(Path.GetTempPath(), "saves", file) }));
    }

    [Theory]
    [InlineData("config.es3")]
    [InlineData("CONFIG.ES3")]
    [InlineData("config.es3.tmp")]
    [InlineData("config.es3.tmp.bak")]
    [InlineData("GameplaySettings.es3")]
    [InlineData("GameplaySettings.es3.bac")]
    [InlineData("tour-run.es3")]
    [InlineData("tour-run.es3.tmp")]
    public void A_refused_save_of_the_games_settings_or_the_tours_run_file_holds_none(string file)
    {
        Assert.False(TourEnd.HoldsPlayerData(new[] { file }));
        Assert.False(TourEnd.HoldsPlayerData(new string?[] { null, file }));
    }

    [Fact]
    public void A_write_naming_a_settings_file_and_a_save_holds_the_save()
    {
        Assert.True(TourEnd.HoldsPlayerData(new[] { "config.es3", "profileV2.es3" }));
        Assert.True(TourEnd.HoldsPlayerData(new[] { "GameplaySettings.es3.tmp", "statsV2.es3.tmp" }));
    }

    [Fact]
    public void A_write_naming_no_file_counts_as_the_players_data()
    {
        // What it was for can't be told: the tour treats it as kept in memory.
        Assert.True(TourEnd.HoldsPlayerData(Array.Empty<string?>()));
        Assert.True(TourEnd.HoldsPlayerData(new string?[] { null, string.Empty }));
        Assert.Throws<ArgumentNullException>(() => TourEnd.HoldsPlayerData(null!));
    }

    [Fact]
    public void A_tour_that_held_nothing_at_the_title_with_everything_back_may_stay_open()
    {
        Assert.Null(TourEnd.QuitReason(0, atTitle: true, allPutBack: true));
    }

    [Fact]
    public void Each_thing_the_game_would_save_makes_the_tour_quit_and_is_named()
    {
        string? held = TourEnd.QuitReason(3, atTitle: true, allPutBack: true);
        Assert.Equal("the game holds what the tour played (3 of its saves refused or records kept in memory), which its next save would write", held);

        string? away = TourEnd.QuitReason(0, atTitle: false, allPutBack: true);
        Assert.Equal("the game isn't at its title screen (a match or a run the tour started is still on)", away);

        string? notBack = TourEnd.QuitReason(0, atTitle: true, allPutBack: false);
        Assert.Equal("something the tour changed wasn't put back (said above), and the game would save it", notBack);

        string? all = TourEnd.QuitReason(1, atTitle: false, allPutBack: false);
        Assert.NotNull(all);
        Assert.Equal(3, all!.Split("; ").Length);
        Assert.DoesNotContain("NOT RESTORED", all, StringComparison.Ordinal); // a summary reader counts that phrase as a failed restore
    }

    [Fact]
    public void A_tour_that_stopped_quits_even_at_the_title_with_nothing_held()
    {
        string? stopped = TourEnd.QuitReason(0, atTitle: true, allPutBack: true, stopped: "2 steps in a row can't start (...)");
        Assert.Equal("the tour stopped (2 steps in a row can't start (...)) and put back only what needs no press", stopped);
        Assert.Null(TourEnd.QuitReason(0, atTitle: true, allPutBack: true, stopped: null));
        Assert.Null(TourEnd.QuitReason(0, atTitle: true, allPutBack: true, stopped: string.Empty));
        Assert.DoesNotContain("STOPPED", stopped!, StringComparison.Ordinal); // the summary's own STOPPED line is said once
    }
}
