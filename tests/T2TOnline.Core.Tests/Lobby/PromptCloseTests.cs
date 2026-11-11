using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>When a prompt of the game's is over, and with which answer: whoever asked is always told, once, however the prompt went.</summary>
public class PromptCloseTests
{
    private const double Frame = 1.0 / 60;

    // A two-button prompt as the game's menu shows it: up, its close running, closed (hidden, not yet destroyed), and destroyed.
    private static readonly PromptLook Up = new(YesNo: true, ResultSent: false, First: false, Second: false, Gone: false, Hiding: false, Shown: true);
    private static readonly PromptLook Hiding = Up with { Hiding = true, Shown = false };
    private static readonly PromptLook Hidden = Up with { Shown = false };
    private static readonly PromptLook Destroyed = Up with { Gone = true, Shown = false };

    // The results the game sends: the pressed button as the close ends, Cancel for Back and for a menu destroyed with none sent.
    private static PromptLook Sent(PromptLook look, bool first = false, bool second = false) => look with { ResultSent = true, First = first, Second = second };

    [Fact]
    public void A_prompt_that_is_up_goes_on()
    {
        var close = new PromptClose();
        close.Begin();
        for (int frame = 0; frame < 600; frame++)
        {
            Assert.Equal(PromptAnswer.None, close.Look(Up, frame * Frame));
        }
    }

    [Fact]
    public void A_pressed_button_is_its_answer_as_the_close_ends()
    {
        var first = new PromptClose();
        first.Begin();
        Assert.Equal(PromptAnswer.None, first.Look(Hiding, 10));
        Assert.Equal(PromptAnswer.First, first.Look(Sent(Hidden, first: true), 10.2));

        var second = new PromptClose();
        second.Begin();
        Assert.Equal(PromptAnswer.None, second.Look(Hiding, 10));
        Assert.Equal(PromptAnswer.Second, second.Look(Sent(Hidden, second: true), 10.2));
    }

    [Fact]
    public void Back_is_neither_button_as_the_close_ends()
    {
        var close = new PromptClose();
        close.Begin();
        Assert.Equal(PromptAnswer.None, close.Look(Hiding, 10));
        Assert.Equal(PromptAnswer.Neither, close.Look(Sent(Hidden), 10.2));
    }

    [Fact]
    public void A_result_is_read_from_a_menu_already_destroyed()
    {
        // The frame the result came was missed (the menu's disposal follows its close at once): the button pressed still counts.
        var close = new PromptClose();
        close.Begin();
        Assert.Equal(PromptAnswer.None, close.Look(Hiding, 10));
        Assert.Equal(PromptAnswer.First, close.Look(Sent(Destroyed, first: true), 10.2));

        var other = new PromptClose();
        other.Begin();
        Assert.Equal(PromptAnswer.Second, other.Look(Sent(Destroyed, second: true), 10.2));
    }

    [Fact]
    public void A_prompt_the_game_hides_and_disposes_is_neither_button_one_frame_after_it_has_gone()
    {
        // The game's plain hide sends no result; its close over, the menu is destroyed, and its destruction sends Cancel.
        var close = new PromptClose();
        close.Begin();
        double now = 10;
        for (; now < 10.22; now += Frame)
        {
            Assert.Equal(PromptAnswer.None, close.Look(Hiding, now));
        }

        Assert.Equal(PromptAnswer.None, close.Look(Hidden, now));
        Assert.Equal(PromptAnswer.Neither, close.Look(Sent(Destroyed), now + Frame));
    }

    [Fact]
    public void A_prompt_destroyed_with_no_result_ever_is_neither_button_after_the_wait_for_one()
    {
        // A menu that goes with its scene, its result never sent: the answer still comes, and soon.
        var close = new PromptClose();
        close.Begin();
        double now = 10;
        Assert.Equal(PromptAnswer.None, close.Look(Destroyed, now));
        PromptAnswer answer = PromptAnswer.None;
        int frames = 0;
        while (answer == PromptAnswer.None && frames < 600)
        {
            now += Frame;
            frames++;
            answer = close.Look(Destroyed, now);
        }

        Assert.Equal(PromptAnswer.Neither, answer);
        Assert.InRange(now - 10, PromptClose.AnswerSeconds, PromptClose.AnswerSeconds + (2 * Frame));
    }

    [Fact]
    public void A_closed_prompt_waits_a_moment_for_its_result()
    {
        var close = new PromptClose();
        close.Begin();
        Assert.Equal(PromptAnswer.None, close.Look(Hiding, 10));
        Assert.Equal(PromptAnswer.None, close.Look(Hidden, 10.2));
        Assert.Equal(PromptAnswer.None, close.Look(Hidden, 10.2 + PromptClose.AnswerSeconds - 0.01));

        // The result came within the wait: it's the answer.
        Assert.Equal(PromptAnswer.First, close.Look(Sent(Hidden, first: true), 10.2 + PromptClose.AnswerSeconds));
    }

    [Fact]
    public void A_notice_is_over_as_soon_as_its_close_starts_or_it_has_gone()
    {
        PromptLook notice = Up with { YesNo = false };
        var close = new PromptClose();
        close.Begin();
        Assert.Equal(PromptAnswer.None, close.Look(notice, 10));
        Assert.Equal(PromptAnswer.Neither, close.Look(notice with { Hiding = true, Shown = false }, 10.1));

        var gone = new PromptClose();
        gone.Begin();
        Assert.Equal(PromptAnswer.Neither, gone.Look(notice with { Gone = true, Shown = false }, 10.1));
    }

    [Fact]
    public void A_prompt_closed_by_whoever_asked_is_neither_button_at_once_whatever_was_pressed()
    {
        var close = new PromptClose();
        close.Begin();
        Assert.Equal(PromptAnswer.None, close.Look(Up, 10));
        close.ClosedByAsker();
        Assert.True(close.ByAsker);
        Assert.Equal(PromptAnswer.None, close.Look(Up, 10.01)); // its hide hasn't started
        Assert.Equal(PromptAnswer.Neither, close.Look(Hiding, 10.02));

        // What it asked about has gone: a button pressed in that very frame is no one's.
        var pressed = new PromptClose();
        pressed.Begin();
        pressed.ClosedByAsker();
        Assert.Equal(PromptAnswer.Neither, pressed.Look(Sent(Hidden, second: true), 10.2));
    }

    [Fact]
    public void A_close_that_runs_too_long_is_taken_as_over_and_said()
    {
        var close = new PromptClose();
        close.Begin();
        Assert.Equal(PromptAnswer.None, close.Look(Hiding, 10));
        Assert.Equal(PromptAnswer.None, close.Look(Hiding, 10 + PromptClose.CloseSeconds - 0.01));
        Assert.Equal(PromptAnswer.NeitherLate, close.Look(Hiding, 10 + PromptClose.CloseSeconds + 0.01));
    }

    [Fact]
    public void The_next_prompt_starts_afresh()
    {
        var close = new PromptClose();
        close.Begin();
        close.ClosedByAsker();
        Assert.Equal(PromptAnswer.Neither, close.Look(Hiding, 10));

        close.Begin();
        Assert.False(close.ByAsker);
        Assert.Equal(PromptAnswer.None, close.Look(Up, 100));
        Assert.Equal(PromptAnswer.None, close.Look(Hiding, 100.1));
        Assert.Equal(PromptAnswer.None, close.Look(Hidden, 100.3)); // the wait for its result starts here, not at the last prompt's close
        Assert.Equal(PromptAnswer.Neither, close.Look(Hidden, 100.3 + PromptClose.AnswerSeconds + 0.01));
    }
}
