using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>A friend's score digits and period markers after a jump no event of the stream explains.</summary>
public class ScoreboardRedrawTests
{
    [Fact]
    public void A_first_message_with_everything_redraws_at_once_and_once()
    {
        var redraw = new ScoreboardRedraw();
        redraw.Ask();
        Message(redraw, "clock", "period", "score.home", "line.home", "score.visitor");
        Assert.True(redraw.TakeAfterMessage());
        Assert.False(redraw.TakeAfterMessage());
        Assert.False(redraw.Due);
    }

    [Fact]
    public void A_late_joiners_first_message_of_only_what_changed_waits_for_the_full_set()
    {
        // The value just before an event comes first: the score isn't in it, and a redraw then would draw this PC's 0-0.
        var redraw = new ScoreboardRedraw();
        redraw.Ask();
        Message(redraw, "clock", "possession");
        Assert.False(redraw.TakeAfterMessage());
        Assert.True(redraw.Due);
        Message(redraw, "clock", "period", "score.home", "score.visitor");
        Assert.True(redraw.TakeAfterMessage());
    }

    [Fact]
    public void A_full_set_split_over_messages_redraws_at_its_end()
    {
        var redraw = new ScoreboardRedraw();
        redraw.Ask();
        Message(redraw, "clock", "period", "score.home");
        Assert.False(redraw.TakeAfterMessage());
        Message(redraw, "stamina.home.0", "score.visitor");
        Assert.True(redraw.TakeAfterMessage());
    }

    [Fact]
    public void A_rejoin_asks_again_for_new_values()
    {
        var redraw = new ScoreboardRedraw();
        redraw.Ask();
        Message(redraw, "period", "score.home", "score.visitor");
        Assert.True(redraw.TakeAfterMessage());

        // In play nothing redraws: the stream's own events do, with the game's own animation.
        Message(redraw, "period", "score.home", "score.visitor");
        Assert.False(redraw.TakeAfterMessage());

        redraw.Ask();
        Assert.False(redraw.TakeAfterMessage());
        Message(redraw, "score.home");
        Assert.False(redraw.TakeAfterMessage());
        Message(redraw, "period", "score.home", "score.visitor");
        Assert.True(redraw.TakeAfterMessage());
    }

    [Fact]
    public void Playback_over_forgets_it()
    {
        var redraw = new ScoreboardRedraw();
        redraw.Ask();
        redraw.Reset();
        Message(redraw, "period", "score.home", "score.visitor");
        Assert.False(redraw.TakeAfterMessage());
    }

    private static void Message(ScoreboardRedraw redraw, params string[] keys)
    {
        foreach (string key in keys)
        {
            redraw.Note(key);
        }
    }
}
