using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class NurseFollowerTests
{
    private static NurseStepsData Host(params NurseStep[] steps) => new() { Procedure = 1, StartStage = 1, StartFocus = 0, Steps = steps };

    private static NurseStep Step(int number, NurseStepKind kind, int stage, int focus, int arg = 0) =>
        new() { Number = number, Kind = kind, Arg = arg, Stage = stage, Focus = focus, Action = kind is NurseStepKind.Press or NurseStepKind.Release ? "UIDetails" : null };

    [Fact]
    public void Steps_are_replayed_in_order_once_the_screen_takes_input()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Step(1, NurseStepKind.Direction, 1, 1, arg: 2), Step(2, NurseStepKind.Confirm, 2, 0));
        Assert.Equal(NurseFollowAction.None, follower.Opened(host, stage: 1).Action);

        // Not ready yet (its entrance still plays): it waits.
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, ready: false, stage: 1, focus: 0, now: 0).Action);

        NurseFollowStep first = follower.Next(host, ready: true, stage: 1, focus: 0, now: 0.5);
        Assert.Equal((NurseFollowAction.Apply, 1), (first.Action, first.Step!.Number));
        Assert.Equal(1, follower.Applied);

        // The direction moved the focus here as on the host's: the confirm comes next.
        NurseFollowStep second = follower.Next(host, ready: true, stage: 1, focus: 1, now: 0.6);
        Assert.Equal((NurseFollowAction.Apply, 2), (second.Action, second.Step!.Number));
        Assert.Equal(NurseFollowAction.None, follower.Next(host, ready: true, stage: 2, focus: 0, now: 0.7).Action);
        Assert.Equal(NurseFollowAction.None, follower.Next(host, ready: true, stage: 2, focus: 0, now: 0.8).Action); // nothing new
        Assert.False(follower.FellBack);
    }

    [Fact]
    public void A_focus_that_differs_after_a_step_is_put_on_the_hosts()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Step(1, NurseStepKind.Direction, 1, 3, arg: 1));
        Assert.Equal(NurseFollowAction.Apply, follower.Next(host, true, 1, 0, 0).Action);

        // The move landed elsewhere here (a held direction overshot on the host): the host's option is focused.
        NurseFollowStep refocus = follower.Next(host, true, 1, 2, 0.1);
        Assert.Equal((NurseFollowAction.Refocus, 3), (refocus.Action, refocus.Focus));
        Assert.Equal(NurseFollowAction.None, follower.Next(host, true, 1, 3, 0.2).Action);
    }

    [Fact]
    public void A_focus_step_is_a_refocus_and_needs_no_input()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Step(1, NurseStepKind.Focus, 1, 4, arg: 4), Step(2, NurseStepKind.Focus, 1, 4, arg: 4));
        NurseFollowStep refocus = follower.Next(host, ready: false, stage: 1, focus: 0, now: 0);
        Assert.Equal((NurseFollowAction.Refocus, 4), (refocus.Action, refocus.Focus));

        // Already on it: nothing to do, and the next one is handled too.
        Assert.Equal(NurseFollowAction.None, follower.Next(host, false, 1, 4, 0.1).Action);
        Assert.Equal(2, follower.Applied);
    }

    [Fact]
    public void A_stage_that_doesnt_reach_the_hosts_falls_back_after_a_moment()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Step(1, NurseStepKind.Confirm, 2, 0));
        Assert.Equal(NurseFollowAction.Apply, follower.Next(host, true, 1, 0, 0).Action);
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, true, 1, 0, 0.5).Action);
        NurseFollowStep fallback = follower.Next(host, true, 3, 0, 0 + NurseFollower.SettleSeconds + 0.1);
        Assert.Equal(NurseFollowAction.Fallback, fallback.Action);
        Assert.Contains("after the host's step 1: Confirm (stage 2, option 0) the screen here is at stage 3", fallback.Why, System.StringComparison.Ordinal);
        Assert.True(follower.FellBack);
        Assert.Equal(NurseFollowAction.None, follower.Next(host, true, 2, 0, 5).Action); // nothing more once fallen back
    }

    [Fact]
    public void Steps_missed_fall_back()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Enumerable.Range(3, 2).Select(n => Step(n, NurseStepKind.Back, 1, 0)).ToArray());
        NurseFollowStep fallback = follower.Next(host, true, 1, 0, 0);
        Assert.Equal(NurseFollowAction.Fallback, fallback.Action);
        Assert.Contains("steps 1 to 2 of the host's were missed here", fallback.Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_that_opens_too_far_behind_or_on_another_stage_falls_back()
    {
        NurseStepsData far = Host(Enumerable.Range(1, NurseFollower.MaxBehind + 1).Select(n => Step(n, NurseStepKind.Direction, 1, 0, arg: 1)).ToArray());
        Assert.Contains("5 steps ahead", new NurseFollower().Opened(far, 1).Why, System.StringComparison.Ordinal);

        NurseStepsData near = Host(Enumerable.Range(1, NurseFollower.MaxBehind).Select(n => Step(n, NurseStepKind.Direction, 1, 0, arg: 1)).ToArray());
        Assert.Equal(NurseFollowAction.None, new NurseFollower().Opened(near, 1).Action);

        NurseFollowStep other = new NurseFollower().Opened(Host(), 12);
        Assert.Equal(NurseFollowAction.Fallback, other.Action);
        Assert.Contains("opened here at stage 12, the host's at stage 1", other.Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_automatic_step_waits_for_the_screen_here_to_make_it()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Step(1, NurseStepKind.Auto, 5, 1), Step(2, NurseStepKind.Confirm, 6, 0));
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, true, 4, 0, 0).Action);
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, true, 4, 0, 2).Action);

        // It got there by itself: the step is handled, the focus put right, then the confirm.
        Assert.Equal(NurseFollowAction.None, follower.Next(host, true, 5, 0, 2.5).Action);
        NurseFollowStep refocus = follower.Next(host, true, 5, 0, 2.6);
        Assert.Equal((NurseFollowAction.Refocus, 1), (refocus.Action, refocus.Focus));
        Assert.Equal(NurseFollowAction.Apply, follower.Next(host, true, 5, 1, 2.7).Action);

        // One that never comes falls back.
        var stuck = new NurseFollower();
        Assert.Equal(NurseFollowAction.Wait, stuck.Next(host, true, 4, 0, 0).Action);
        Assert.Equal(NurseFollowAction.Fallback, stuck.Next(host, true, 4, 0, NurseFollower.AutoSeconds + 0.1).Action);
    }

    [Fact]
    public void A_screen_that_takes_no_input_gets_its_reveal_ended_then_falls_back()
    {
        var follower = new NurseFollower();
        NurseStepsData host = Host(Step(1, NurseStepKind.Confirm, 12, -1));
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, false, 1, 0, 0).Action);
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, false, 1, 0, NurseFollower.ReadySeconds).Action);
        Assert.Equal(NurseFollowAction.SkipReveal, follower.Next(host, false, 1, 0, NurseFollower.ReadySeconds + 0.1).Action);
        Assert.Equal(NurseFollowAction.Wait, follower.Next(host, false, 1, 0, NurseFollower.ReadySeconds + 0.2).Action);
        NurseFollowStep fallback = follower.Next(host, false, 1, 0, (2 * NurseFollower.ReadySeconds) + 0.1);
        Assert.Equal(NurseFollowAction.Fallback, fallback.Action);
        Assert.Contains("took no input for 4 s", fallback.Why, System.StringComparison.Ordinal);

        // Ready again in time after the skip: the step goes.
        var late = new NurseFollower();
        late.Next(host, false, 1, 0, 0);
        Assert.Equal(NurseFollowAction.SkipReveal, late.Next(host, false, 1, 0, NurseFollower.ReadySeconds + 0.1).Action);
        Assert.Equal(NurseFollowAction.Apply, late.Next(host, true, 1, 0, NurseFollower.ReadySeconds + 0.5).Action);
    }

    [Fact]
    public void A_lineup_step_is_passed_over_and_a_steps_window_that_grows_is_followed()
    {
        var follower = new NurseFollower();
        var steps = new List<NurseStep> { Step(1, NurseStepKind.Lineup, 1, 0) };
        Assert.Equal(NurseFollowAction.None, follower.Next(Host(steps.ToArray()), true, 1, 0, 0).Action);
        Assert.Equal(1, follower.Applied);

        // The same step sent again (a newer window) is never applied twice.
        steps.Add(Step(2, NurseStepKind.Press, 1, 0));
        NurseFollowStep press = follower.Next(Host(steps.ToArray()), true, 1, 0, 1);
        Assert.Equal((NurseFollowAction.Apply, NurseStepKind.Press), (press.Action, press.Step!.Kind));
        Assert.Equal(NurseFollowAction.None, follower.Next(Host(steps.ToArray()), true, 1, 0, 1.1).Action);
        Assert.Equal(2, follower.Applied);
    }

    [Fact]
    public void A_fallback_keeps_its_first_reason()
    {
        var follower = new NurseFollower();
        Assert.Equal(NurseFollowAction.Fallback, follower.Fall("the screen closed early").Action);
        Assert.Equal("the screen closed early", follower.Fall("another reason").Why);
        Assert.Equal("the screen closed early", follower.Why);
    }

    private static SpeechProgress Line(int key, SpeechStage stage) => SpeechProgress.None.Raised(key, stage);

    [Fact]
    public void A_host_press_that_shows_a_butcher_line_shows_it_here_once()
    {
        var line = new NurseLineFollow();
        SpeechProgress shown = Line(2, SpeechStage.Shown);
        Assert.True(line.Wanted(shown, inStep: true));
        Assert.True(line.Press(shown, SpeechPhase.Typing));

        // Dealt with: not again for this line.
        Assert.False(line.Wanted(shown, inStep: true));

        // The next line asks again once a press shows it.
        Assert.False(line.Wanted(Line(3, SpeechStage.Started), inStep: true));
        Assert.True(line.Wanted(Line(3, SpeechStage.Shown), inStep: true));
    }

    [Fact]
    public void A_butcher_line_already_shown_here_is_never_pressed()
    {
        // A press on a line that's waiting would play the bubble's "next line" sound: UntilNext lines wait for no press.
        var line = new NurseLineFollow();
        SpeechProgress shown = Line(1, SpeechStage.Shown);
        Assert.True(line.Wanted(shown, inStep: true));
        Assert.False(line.Press(shown, SpeechPhase.Waiting));
        Assert.False(line.Wanted(shown, inStep: true));
    }

    [Fact]
    public void A_butcher_line_not_up_here_yet_is_waited_for()
    {
        var line = new NurseLineFollow();
        SpeechProgress shown = Line(1, SpeechStage.Shown);
        Assert.False(line.Press(shown, SpeechPhase.None));
        Assert.False(line.Press(shown, SpeechPhase.Idle));
        Assert.True(line.Wanted(shown, inStep: true));
        Assert.True(line.Press(shown, SpeechPhase.Typing));
    }

    [Fact]
    public void A_butcher_line_is_left_alone_until_the_screen_here_is_in_step()
    {
        // Behind the host's steps, the line here isn't the host's yet: its step brings it, nothing presses to catch up.
        var line = new NurseLineFollow();
        SpeechProgress shown = Line(4, SpeechStage.Shown);
        Assert.False(line.Wanted(shown, inStep: false));
        Assert.True(line.Wanted(shown, inStep: true));

        // No line said, or one only started: nothing to do either way.
        Assert.False(line.Wanted(SpeechProgress.None, inStep: true));
        Assert.False(line.Wanted(Line(4, SpeechStage.Started), inStep: true));
    }
}
