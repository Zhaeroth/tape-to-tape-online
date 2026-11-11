using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class NpcLinePlaybackTests
{
    private const int Playing = 1;
    private const int Over = NpcLinePlayback.HostIntroOver;

    [Fact]
    public void A_line_waits_for_the_screen_then_plays_once()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(new NpcLineStep(NpcLineAction.None, 0), playback.Step(0, 0, ready: true, Playing, 1));
        Assert.Equal(new NpcLineStep(NpcLineAction.Wait, 1), playback.Step(1, 1, ready: false, Playing, 1.5));
        Assert.Equal(new NpcLineStep(NpcLineAction.Wait, 1), playback.Step(1, 1, ready: false, Playing, 4));
        Assert.Equal(new NpcLineStep(NpcLineAction.Play, 1), playback.Step(1, 1, ready: true, Playing, 5));
        Assert.Equal((1, 1), (playback.Handled, playback.Playing));

        // Played once: the same line again does nothing.
        Assert.Equal(NpcLineAction.None, playback.Step(1, 1, ready: true, Playing, 6).Action);
    }

    [Fact]
    public void The_host_intro_ending_cuts_the_line_once()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(NpcLineAction.Play, playback.Step(1, 0, ready: true, Playing, 0.2).Action);
        Assert.Equal(new NpcLineStep(NpcLineAction.Cut, 1), playback.Step(1, 0, ready: true, Over, 3));
        Assert.Equal(NpcLineAction.None, playback.Step(1, 0, ready: true, Over, 3.1).Action);
    }

    [Fact]
    public void A_line_after_the_host_intro_ended_is_left_out()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(new NpcLineStep(NpcLineAction.DropIntroOver, 1), playback.Step(1, 0, ready: true, Over, 0.1));
        Assert.Equal((1, 0), (playback.Handled, playback.Playing));
        Assert.Equal(NpcLineAction.None, playback.Step(1, 0, ready: true, Over, 0.2).Action);

        // Also while the screen here isn't ready yet.
        var waiting = new NpcLinePlayback();
        Assert.Equal(NpcLineAction.Wait, waiting.Step(2, 0, ready: false, Playing, 0.1).Action);
        Assert.Equal(new NpcLineStep(NpcLineAction.DropIntroOver, 2), waiting.Step(2, 0, ready: false, Over, 0.5));
    }

    [Fact]
    public void A_line_that_waits_too_long_is_left_out()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(NpcLineAction.Wait, playback.Step(1, 2, ready: false, Playing, 2 + NpcLinePlayback.WaitSeconds).Action);
        Assert.Equal(new NpcLineStep(NpcLineAction.DropTooLate, 1), playback.Step(1, 2, ready: false, Playing, 2.01 + NpcLinePlayback.WaitSeconds));
        Assert.Equal(NpcLineAction.None, playback.Step(1, 2, ready: true, Playing, 13).Action);
    }

    [Fact]
    public void A_newer_line_replaces_the_one_playing()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(NpcLineAction.Play, playback.Step(1, 0, ready: true, Playing, 0.1).Action);
        Assert.Equal(new NpcLineStep(NpcLineAction.Play, 3), playback.Step(3, 1, ready: true, Playing, 1.1));
        Assert.Equal((3, 3), (playback.Handled, playback.Playing));

        // An older number that comes late (a rejoin's copy) is never played.
        Assert.Equal(NpcLineAction.None, playback.Step(2, 1.5, ready: true, Playing, 1.6).Action);
    }

    [Fact]
    public void An_unknown_host_intro_state_counts_as_not_over()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(NpcLineAction.Play, playback.Step(1, 0, ready: true, -1, 0.1).Action);
        Assert.Equal(NpcLineAction.None, playback.Step(1, 0, ready: true, 0, 0.2).Action);
    }

    [Fact]
    public void A_stopped_line_isnt_cut_again()
    {
        var playback = new NpcLinePlayback();
        Assert.Equal(NpcLineAction.Play, playback.Step(1, 0, ready: true, Playing, 0.1).Action);
        playback.Stopped();
        Assert.Equal(0, playback.Playing);
        Assert.Equal(NpcLineAction.None, playback.Step(1, 0, ready: true, Over, 1).Action);
    }
}
