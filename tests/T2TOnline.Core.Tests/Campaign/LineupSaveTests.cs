using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class LineupSaveTests
{
    private static readonly long Before = LineupOrder.Print(new[] { "a", "b", "c", "d", "e", "f", null });
    private static readonly long After = LineupOrder.Print(new[] { "a", "b", "f", "d", "e", "c", null });

    [Fact]
    public void A_lineup_changed_on_a_quiet_map_saves_the_run()
    {
        Assert.Equal(LineupSaveStep.Save, LineupSave.Decide(shared: true, onMap: true, eventOpen: false, busy: false, Before, After));
        Assert.NotEmpty(LineupSave.Words(LineupSaveStep.Save));
    }

    [Fact]
    public void A_lineup_closed_without_a_change_saves_nothing()
    {
        Assert.Equal(LineupSaveStep.Unchanged, LineupSave.Decide(true, true, false, false, After, After));
        Assert.Equal(LineupSaveStep.Unchanged, LineupSave.Decide(true, false, true, true, After, After));
        Assert.Empty(LineupSave.Words(LineupSaveStep.Unchanged));
    }

    [Fact]
    public void A_team_never_seen_saved_in_this_session_is_saved()
    {
        // Hosting started with the run in memory: nothing says the file holds this team.
        Assert.Equal(LineupSaveStep.Save, LineupSave.Decide(true, true, false, false, null, After));
    }

    [Fact]
    public void Only_a_shared_campaign_is_saved()
    {
        Assert.Equal(LineupSaveStep.NotShared, LineupSave.Decide(false, true, false, false, Before, After));
        Assert.Empty(LineupSave.Words(LineupSaveStep.NotShared));
    }

    [Fact]
    public void The_games_own_next_save_brings_a_change_made_off_the_map_or_in_an_event()
    {
        Assert.Equal(LineupSaveStep.NotOnMap, LineupSave.Decide(true, false, false, false, Before, After));
        Assert.Equal(LineupSaveStep.EventOpen, LineupSave.Decide(true, true, true, false, Before, After));
        Assert.Equal(LineupSaveStep.Busy, LineupSave.Decide(true, true, false, true, Before, After));
        foreach (LineupSaveStep step in new[] { LineupSaveStep.NotOnMap, LineupSaveStep.EventOpen, LineupSaveStep.Busy })
        {
            Assert.NotEmpty(LineupSave.Words(step));
        }
    }
}
