using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class LateSpeechCoverTests
{
    private static readonly SpeechProgress None = SpeechProgress.None;

    private static SpeechProgress Line(int key, SpeechStage stage) => SpeechProgress.Line(key, stage);

    [Fact]
    public void The_host_is_past_the_first_line_once_it_advanced_it_or_the_second_speaker_started()
    {
        Assert.False(LateSpeechCover.PastFirstLine(None, None));
        Assert.False(LateSpeechCover.PastFirstLine(Line(1, SpeechStage.Started), None));
        Assert.False(LateSpeechCover.PastFirstLine(Line(1, SpeechStage.Shown), None));
        Assert.True(LateSpeechCover.PastFirstLine(Line(1, SpeechStage.Advanced), None));
        Assert.True(LateSpeechCover.PastFirstLine(Line(1, SpeechStage.Advanced), Line(1, SpeechStage.Started)));
        Assert.True(LateSpeechCover.PastFirstLine(Line(2, SpeechStage.Started), Line(1, SpeechStage.Advanced)));
        Assert.True(LateSpeechCover.PastFirstLine(None, Line(1, SpeechStage.Started)));
    }

    [Fact]
    public void A_bubble_is_on_the_hosts_line_unless_the_host_has_gone_past_it()
    {
        Assert.False(LateSpeechCover.OnHostsLine(0, SpeechPhase.None, None)); // no line here yet
        Assert.False(LateSpeechCover.OnHostsLine(1, SpeechPhase.None, Line(1, SpeechStage.Started))); // its bubble has gone
        Assert.False(LateSpeechCover.OnHostsLine(1, SpeechPhase.Typing, Line(2, SpeechStage.Started))); // behind
        Assert.False(LateSpeechCover.OnHostsLine(1, SpeechPhase.Waiting, Line(1, SpeechStage.Advanced))); // the host's moved on
        Assert.False(LateSpeechCover.OnHostsLine(1, SpeechPhase.Idle, Line(1, SpeechStage.Advanced))); // pressed, going away
        Assert.True(LateSpeechCover.OnHostsLine(1, SpeechPhase.Idle, Line(1, SpeechStage.Started))); // its own fade in: it shows from here
        Assert.True(LateSpeechCover.OnHostsLine(1, SpeechPhase.Typing, Line(1, SpeechStage.Shown)));
        Assert.True(LateSpeechCover.OnHostsLine(1, SpeechPhase.Typing, None)); // a moment ahead of the host's
        Assert.True(LateSpeechCover.OnHostsLine(2, SpeechPhase.Typing, Line(1, SpeechStage.Advanced)));
    }

    [Fact]
    public void A_speech_followed_from_its_start_is_left_alone()
    {
        var cover = new LateSpeechCover();
        Assert.Equal(LateSpeechStep.None, cover.Next(hostPastFirst: false, linesHere: false, arrived: false, finished: false, now: 1));
        Assert.Equal(LateSpeechStep.None, cover.Next(false, linesHere: true, arrived: true, finished: false, now: 2));
        Assert.True(cover.Over);
        Assert.False(cover.Hidden);

        // Over: the host's later presses don't hide anything.
        Assert.Equal(LateSpeechStep.None, cover.Next(hostPastFirst: true, false, false, false, 3));
        Assert.False(cover.Hidden);
    }

    [Fact]
    public void Late_it_hides_before_its_first_line_and_shows_on_the_hosts_line()
    {
        var cover = new LateSpeechCover();
        Assert.Equal(LateSpeechStep.Hide, cover.Next(hostPastFirst: true, linesHere: false, arrived: false, finished: false, now: 1));
        Assert.True(cover.Hidden);

        // Its lines run up to the host's, unseen.
        Assert.Equal(LateSpeechStep.None, cover.Next(true, linesHere: true, arrived: false, false, 2));
        Assert.True(cover.Hidden);
        Assert.Equal(LateSpeechStep.Show, cover.Next(true, true, arrived: true, false, 3.5));
        Assert.False(cover.Hidden);
        Assert.True(cover.Over);
        Assert.Equal(LateSpeechEnd.Arrived, cover.Ended);
        Assert.Equal(2.5, cover.HiddenFor);
        Assert.Equal(LateSpeechStep.None, cover.Next(true, true, true, false, 4));
    }

    [Fact]
    public void The_host_past_the_first_line_after_this_PCs_started_is_a_plain_follow()
    {
        var cover = new LateSpeechCover();
        Assert.Equal(LateSpeechStep.None, cover.Next(hostPastFirst: true, linesHere: true, arrived: true, finished: false, now: 1));
        Assert.True(cover.Over);
        Assert.False(cover.Hidden);
    }

    [Fact]
    public void A_speech_over_on_the_host_shows_when_this_PCs_is_over()
    {
        var cover = new LateSpeechCover();
        cover.Next(true, false, false, false, 1);
        Assert.Equal(LateSpeechStep.Show, cover.Next(true, true, arrived: false, finished: true, now: 2));
        Assert.Equal(LateSpeechEnd.Finished, cover.Ended);
    }

    [Fact]
    public void Its_cards_already_up_it_never_hides()
    {
        var cover = new LateSpeechCover();
        Assert.Equal(LateSpeechStep.None, cover.Next(true, false, false, finished: true, now: 1));
        Assert.True(cover.Over);
    }

    [Fact]
    public void It_never_stays_hidden_past_its_limit()
    {
        var cover = new LateSpeechCover();
        cover.Next(true, false, false, false, 10);
        Assert.Equal(LateSpeechStep.None, cover.Next(true, true, false, false, 10 + LateSpeechCover.MaxHiddenSeconds - 0.01));
        Assert.Equal(LateSpeechStep.Show, cover.Next(true, true, false, false, 10 + LateSpeechCover.MaxHiddenSeconds));
        Assert.Equal(LateSpeechEnd.TimedOut, cover.Ended);
    }
}
