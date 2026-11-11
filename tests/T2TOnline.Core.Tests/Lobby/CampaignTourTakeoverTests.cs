using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>A takeover's screen in a campaign tour (the Nurse's procedure screen): the pad's presses on it, when the walk goes on, and the lines.</summary>
public class CampaignTourTakeoverTests
{
    [Fact]
    public void A_first_and_after_every_stage_that_moved_on()
    {
        Assert.Equal(PadButtons.A, CampaignTour.TakeoverPress(0));
        Assert.Equal(PadButtons.A, CampaignTour.TakeoverPress(-3));
    }

    [Fact]
    public void After_an_A_that_moved_nothing_the_d_pad_down_twice_then_right_once_with_an_A_between()
    {
        PadButtons[] presses = Enumerable.Range(0, 14).Select(CampaignTour.TakeoverPress).ToArray();
        Assert.Equal(
            new[]
            {
                PadButtons.A, PadButtons.DPadDown, PadButtons.A, PadButtons.DPadDown, PadButtons.A, PadButtons.DPadRight, PadButtons.A,
                PadButtons.DPadDown, PadButtons.A, PadButtons.DPadDown, PadButtons.A, PadButtons.DPadRight, PadButtons.A, PadButtons.DPadDown,
            },
            presses);
    }

    [Fact]
    public void The_screens_back_is_never_pressed()
    {
        Assert.DoesNotContain(Enumerable.Range(0, 200).Select(CampaignTour.TakeoverPress), p => p == PadButtons.B);
    }

    [Theory]
    [InlineData(CampaignEventEnd.Closed, true, true)]
    [InlineData(CampaignEventEnd.LeftByBack, true, true)]
    [InlineData(CampaignEventEnd.Closed, false, false)]
    [InlineData(CampaignEventEnd.Stuck, true, false)]
    [InlineData(CampaignEventEnd.OtherScreen, true, false)]
    [InlineData(CampaignEventEnd.NotOpened, true, false)]
    public void The_walk_goes_on_only_once_the_pad_left_the_screen_and_the_map_is_free(CampaignEventEnd end, bool mapFree, bool goesOn)
    {
        Assert.Equal(goesOn, CampaignTour.WalkGoesOnAfter(end, mapFree));
    }

    [Fact]
    public void The_lines()
    {
        Assert.Equal(
            "campaign: node 15 (Nurse): its pick took the event screen over (TRANSPLANT, the procedure screen): the walker's map walk paused; the pad drives the screen to its end",
            CampaignTour.TakeoverLine(15, "Nurse", "TRANSPLANT, the procedure screen"));
        Assert.Equal(
            "campaign: node 0 (?): its pick took the event screen over (the procedure screen): the walker's map walk paused; the pad drives the screen to its end",
            CampaignTour.TakeoverLine(-1, null, "the procedure screen"));
        Assert.Equal(
            "campaign: the takeover's screen (TRANSPLANT, the procedure screen): closed by the pad in 21.3 s; 12 press(es), 5 stage(s) on; the map free again: the walker's map walk goes on",
            CampaignTour.TakeoverEndLine("TRANSPLANT, the procedure screen", CampaignEventEnd.Closed, 21.34, 12, 5, mapFree: true));
        Assert.Equal(
            "campaign: the takeover's screen (TRANSPLANT, the procedure screen): closed by the pad in 21.3 s; 12 press(es), 5 stage(s) on; the map not free again: the walk stops here",
            CampaignTour.TakeoverEndLine("TRANSPLANT, the procedure screen", CampaignEventEnd.Closed, 21.34, 12, 5, mapFree: false));
        Assert.Equal(
            "campaign: the takeover's screen (the procedure screen): the pad couldn't leave it in 90.0 s; 100 press(es), 0 stage(s) on; the walk stops here",
            CampaignTour.TakeoverEndLine("the procedure screen", CampaignEventEnd.Stuck, 90, 100, -1, mapFree: false));
        Assert.Equal(
            "campaign: an event screen is still up (taken over by its own screen): the pad leaves it first: closed by the pad in 9.0 s, 6 press(es)",
            CampaignTour.LeaveEventLine(takenOver: true, CampaignEventEnd.Closed, 9, 6));
        Assert.Equal(
            "campaign: an event screen is still up: the pad leaves it first: left with no pick by the pad's skip in 2.5 s, 2 press(es)",
            CampaignTour.LeaveEventLine(takenOver: false, CampaignEventEnd.LeftByBack, 2.5, 2));
    }
}
