using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class SpeechFollowTests
{
    private static SpeechProgress Host(int key, SpeechStage stage) => SpeechProgress.Line(key, stage);

    /// <summary>A follower that has seen <paramref name="lines"/> lines (objects 100, 101...), the last in <paramref name="phase"/>.</summary>
    private static SpeechFollow At(int lines, SpeechPhase phase)
    {
        var follow = new SpeechFollow();
        follow.Begin(0, SpeechPhase.None);
        for (int i = 0; i < lines; i++)
        {
            Assert.True(follow.Observe(100 + i, SpeechPhase.Typing));
        }

        follow.Observe(lines == 0 ? 0 : 100 + lines - 1, phase);
        return follow;
    }

    [Fact]
    public void The_counter_counts_each_new_line_object_once()
    {
        var counter = new SpeechLineCounter();
        counter.Begin(0, SpeechPhase.None);
        Assert.Equal(0, counter.Key);
        Assert.True(counter.Observe(500, SpeechPhase.Typing));
        Assert.False(counter.Observe(500, SpeechPhase.Waiting));
        Assert.Equal(1, counter.Key);

        // A sequence's next line on the same bubble: a new object, whatever the bubble's phase still says.
        Assert.True(counter.Observe(501, SpeechPhase.Waiting));
        Assert.False(counter.Observe(501, SpeechPhase.Typing));
        Assert.Equal(2, counter.Key);
    }

    [Fact]
    public void A_line_after_the_bubble_has_gone_is_new_even_at_the_memory_of_the_last()
    {
        // A speech of single lines (the Canada curse): Pucklin's line 1, its bubble gone while Hatfreeze speaks, and line 2's
        // copy made where line 1's was (nothing held it any more). Followed, a missed line here would hang a friend's dialogue.
        var counter = new SpeechLineCounter();
        counter.Begin(0, SpeechPhase.None);
        Assert.True(counter.Observe(500, SpeechPhase.Idle));
        Assert.False(counter.Observe(500, SpeechPhase.Typing));
        Assert.False(counter.Observe(0, SpeechPhase.None));
        Assert.False(counter.Observe(0, SpeechPhase.None));
        Assert.True(counter.Observe(500, SpeechPhase.Idle));
        Assert.Equal(2, counter.Key);
        Assert.False(counter.Observe(500, SpeechPhase.Typing));

        // What a speaker showed when its speech began stays uncounted while it's up, and the next after it goes is a line.
        counter.Begin(77, SpeechPhase.Idle);
        Assert.False(counter.Observe(77, SpeechPhase.Idle));
        Assert.False(counter.Observe(0, SpeechPhase.None));
        Assert.True(counter.Observe(77, SpeechPhase.Typing));
        Assert.Equal(1, counter.Key);
    }

    [Fact]
    public void A_waiting_line_whose_press_has_come_reads_as_idle_and_gets_no_more_presses()
    {
        // The game never puts a bubble's phase back: the glue says Idle for a waiting line whose press has come, so one press
        // takes the line and nothing more goes to it, nor to the next line before it types.
        SpeechFollow follow = At(1, SpeechPhase.Waiting);
        SpeechProgress advanced = Host(1, SpeechStage.Advanced);
        Assert.Equal(SpeechFollowAction.Skip, follow.Next(advanced, 0).Action);
        follow.Observe(100, SpeechPhase.Idle);
        Assert.Equal(SpeechFollowAction.None, follow.Next(advanced, 0.02).Action);
        follow.Observe(101, SpeechPhase.Idle);
        Assert.Equal(SpeechFollowAction.None, follow.Next(Host(2, SpeechStage.Advanced), 0.1).Action);
        follow.Observe(101, SpeechPhase.Typing);
        Assert.Equal(SpeechFollowEvent.ShownFirst, follow.Next(Host(2, SpeechStage.Advanced), 0.2).Event);
        Assert.Equal(2, follow.Presses);
    }

    [Fact]
    public void When_the_hosts_speech_is_over_every_line_here_runs_out()
    {
        // A line the host's never said (own 2, host 1; or nothing from the host at all), or one the host's last press didn't
        // reach here: typing shows in full, waiting advances, nothing happens with no bubble or between lines.
        SpeechFollow ahead = At(2, SpeechPhase.Typing);
        Assert.Equal(SpeechFollowAction.None, ahead.Next(Host(1, SpeechStage.Advanced), 0).Action);
        SpeechFollowStep step = ahead.Next(Host(1, SpeechStage.Advanced), 0.02, hostOver: true);
        Assert.Equal((SpeechFollowAction.Skip, SpeechFollowEvent.HostOver, 2), (step.Action, step.Event, step.Key));
        ahead.Observe(101, SpeechPhase.Waiting);
        Assert.Equal(SpeechFollowAction.Skip, ahead.Next(SpeechProgress.None, 0.04, hostOver: true).Action);
        ahead.Observe(101, SpeechPhase.Idle);
        Assert.Equal(SpeechFollowAction.None, ahead.Next(SpeechProgress.None, 0.06, hostOver: true).Action);
        ahead.Observe(0, SpeechPhase.None);
        Assert.Equal(SpeechFollowAction.None, ahead.Next(SpeechProgress.None, 0.08, hostOver: true).Action);

        // What the host's progress already says keeps its own meaning (no one is told something's wrong).
        Assert.Equal(SpeechFollowEvent.Advanced, At(1, SpeechPhase.Waiting).Next(Host(1, SpeechStage.Advanced), 0, hostOver: true).Event);
        Assert.Equal(SpeechFollowEvent.CaughtUp, At(1, SpeechPhase.Waiting).Next(Host(2, SpeechStage.Started), 0, hostOver: true).Event);
        Assert.Equal(SpeechFollowEvent.HostOver, At(1, SpeechPhase.Waiting).Next(Host(1, SpeechStage.Started), 0, hostOver: true).Event);
    }

    [Fact]
    public void What_the_speaker_shows_when_its_speech_begins_isnt_counted()
    {
        var counter = new SpeechLineCounter();
        counter.Begin(77, SpeechPhase.Waiting);
        Assert.False(counter.Observe(77, SpeechPhase.Waiting));
        Assert.Equal(0, counter.Key);
        Assert.True(counter.Observe(78, SpeechPhase.Typing));
        Assert.Equal(1, counter.Key);

        // A late start: the first new line is the key asked for.
        counter.Begin(0, SpeechPhase.None, firstKey: 4);
        Assert.True(counter.Observe(79, SpeechPhase.Typing));
        Assert.Equal(4, counter.Key);

        // Out-of-range first keys are clamped.
        counter.Begin(0, SpeechPhase.None, firstKey: 0);
        counter.Observe(80, SpeechPhase.Typing);
        Assert.Equal(1, counter.Key);
        counter.Begin(0, SpeechPhase.None, firstKey: 9999);
        counter.Observe(81, SpeechPhase.Typing);
        Assert.Equal(SpeechProgress.MaxKey, counter.Key);
    }

    [Fact]
    public void Without_the_line_object_each_start_of_typing_is_a_line()
    {
        var counter = new SpeechLineCounter();
        counter.Begin(SpeechLineCounter.Unknown, SpeechPhase.Typing);
        Assert.False(counter.Observe(SpeechLineCounter.Unknown, SpeechPhase.Typing)); // already typing when it began
        Assert.False(counter.Observe(SpeechLineCounter.Unknown, SpeechPhase.Waiting));
        Assert.True(counter.Observe(SpeechLineCounter.Unknown, SpeechPhase.Typing));
        Assert.False(counter.Observe(SpeechLineCounter.Unknown, SpeechPhase.Typing));
        Assert.False(counter.Observe(SpeechLineCounter.Unknown, SpeechPhase.Idle));
        Assert.True(counter.Observe(SpeechLineCounter.Unknown, SpeechPhase.Typing));
        Assert.Equal(2, counter.Key);
    }

    [Fact]
    public void A_speech_longer_than_the_cap_stays_at_the_cap()
    {
        var counter = new SpeechLineCounter();
        counter.Begin(0, SpeechPhase.None);
        for (int i = 1; i <= SpeechProgress.MaxKey + 3; i++)
        {
            Assert.True(counter.Observe(i, SpeechPhase.Typing));
        }

        Assert.Equal(SpeechProgress.MaxKey, counter.Key);
        Assert.True(counter.Saturated);
    }

    public static TheoryData<int, int, SpeechStage, SpeechPhase, SpeechFollowAction, SpeechFollowEvent> Cases()
    {
        var data = new TheoryData<int, int, SpeechStage, SpeechPhase, SpeechFollowAction, SpeechFollowEvent>();

        // Behind the host: whatever its stage, a typing or waiting line is skipped to catch up.
        foreach (SpeechStage stage in Enum.GetValues<SpeechStage>())
        {
            data.Add(1, 3, stage, SpeechPhase.Typing, SpeechFollowAction.Skip, SpeechFollowEvent.CaughtUp);
            data.Add(1, 3, stage, SpeechPhase.Waiting, SpeechFollowAction.Skip, SpeechFollowEvent.CaughtUp);
            data.Add(1, 3, stage, SpeechPhase.Idle, SpeechFollowAction.None, SpeechFollowEvent.None);
            data.Add(1, 3, stage, SpeechPhase.None, SpeechFollowAction.None, SpeechFollowEvent.None);
        }

        // The same line.
        data.Add(2, 2, SpeechStage.Started, SpeechPhase.Typing, SpeechFollowAction.None, SpeechFollowEvent.None);
        data.Add(2, 2, SpeechStage.Started, SpeechPhase.Waiting, SpeechFollowAction.None, SpeechFollowEvent.None);
        data.Add(2, 2, SpeechStage.Shown, SpeechPhase.Typing, SpeechFollowAction.Skip, SpeechFollowEvent.ShownInFull);
        data.Add(2, 2, SpeechStage.Shown, SpeechPhase.Waiting, SpeechFollowAction.None, SpeechFollowEvent.None);
        data.Add(2, 2, SpeechStage.Advanced, SpeechPhase.Typing, SpeechFollowAction.Skip, SpeechFollowEvent.ShownFirst);
        data.Add(2, 2, SpeechStage.Advanced, SpeechPhase.Waiting, SpeechFollowAction.Skip, SpeechFollowEvent.Advanced);
        data.Add(2, 2, SpeechStage.Advanced, SpeechPhase.Idle, SpeechFollowAction.None, SpeechFollowEvent.None);
        data.Add(2, 2, SpeechStage.Advanced, SpeechPhase.None, SpeechFollowAction.None, SpeechFollowEvent.None);

        // Ahead of the host (it's behind, or this line advanced by itself): never past it.
        foreach (SpeechStage stage in Enum.GetValues<SpeechStage>())
        {
            data.Add(3, 2, stage, SpeechPhase.Typing, SpeechFollowAction.None, SpeechFollowEvent.None);
            data.Add(3, 2, stage, SpeechPhase.Waiting, SpeechFollowAction.None, SpeechFollowEvent.None);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_line_stage_and_phase_gets_its_step(int own, int host, SpeechStage stage, SpeechPhase phase, SpeechFollowAction action, SpeechFollowEvent meaning)
    {
        SpeechFollow follow = At(own, phase);
        SpeechFollowStep step = follow.Next(Host(host, stage), 10);
        Assert.Equal((action, meaning), (step.Action, step.Event));
        if (action == SpeechFollowAction.Skip)
        {
            Assert.Equal(own, step.Key);
        }
    }

    [Fact]
    public void No_host_progress_means_nothing_to_do()
    {
        SpeechFollow follow = At(2, SpeechPhase.Waiting);
        Assert.Equal(default, follow.Next(SpeechProgress.None, 1));

        // Nor before this PC's first line.
        SpeechFollow fresh = At(0, SpeechPhase.None);
        Assert.Equal(default, fresh.Next(Host(1, SpeechStage.Advanced), 1));
    }

    [Fact]
    public void A_line_still_typing_at_the_hosts_advance_shows_in_full_then_advances()
    {
        SpeechFollow follow = At(1, SpeechPhase.Typing);
        SpeechProgress host = Host(1, SpeechStage.Advanced);
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.ShownFirst, 1), follow.Next(host, 1.00));

        // The game takes a frame: the same skip again, said once.
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.None, 1), follow.Next(host, 1.02));
        follow.Observe(100, SpeechPhase.Waiting);
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.Advanced, 1), follow.Next(host, 1.03));

        // Advanced: the next line starts here; the host hasn't got there yet, nothing to do.
        follow.Observe(0, SpeechPhase.None);
        Assert.Equal(default, follow.Next(host, 1.10));
        Assert.True(follow.Observe(101, SpeechPhase.Typing));
        Assert.Equal(default, follow.Next(host, 1.15));
        Assert.Equal((2, 1, 1, 2), (follow.Lines, follow.ShownFirst, follow.Advanced, follow.Presses));
    }

    [Fact]
    public void A_friend_behind_catches_up_one_skip_a_frame_and_counts_each_line_once()
    {
        SpeechFollow follow = At(1, SpeechPhase.Typing);
        SpeechProgress host = Host(3, SpeechStage.Started); // the host pressed through two lines quickly
        double now = 5;
        for (int line = 1; line <= 2; line++)
        {
            Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.CaughtUp, line), follow.Next(host, now += 0.016));
            follow.Observe(100 + line - 1, SpeechPhase.Waiting);
            Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.CaughtUp, line), follow.Next(host, now += 0.016));
            follow.Observe(0, SpeechPhase.None);
            Assert.Equal(default, follow.Next(host, now += 0.1));
            Assert.True(follow.Observe(100 + line, SpeechPhase.Typing));
        }

        // On the host's line now: it waits for the host's press.
        Assert.Equal(3, follow.OwnKey);
        Assert.Equal(default, follow.Next(host, now += 0.016));
        Assert.Equal((2, 4), (follow.CaughtUp, follow.Presses));
    }

    [Fact]
    public void A_bubble_that_doesnt_move_is_said_once_then_skipped_slowly()
    {
        SpeechFollow follow = At(2, SpeechPhase.Waiting);
        SpeechProgress host = Host(2, SpeechStage.Advanced);
        Assert.Equal(SpeechFollowEvent.Advanced, follow.Next(host, 0).Event);
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.None, 2), follow.Next(host, 1.0));
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.Stuck, 2), follow.Next(host, SpeechFollow.StuckSeconds));
        Assert.Equal(default, follow.Next(host, SpeechFollow.StuckSeconds + 0.1));
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.None, 2), follow.Next(host, SpeechFollow.StuckSeconds + SpeechFollow.StuckRetrySeconds));
        Assert.Equal(default, follow.Next(host, SpeechFollow.StuckSeconds + SpeechFollow.StuckRetrySeconds + 0.1));
        Assert.Equal(1, follow.Presses);

        // Moving on starts the clock again.
        follow.Observe(0, SpeechPhase.None);
        follow.Observe(500, SpeechPhase.Waiting);
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.CaughtUp, 3), follow.Next(Host(4, SpeechStage.Started), 10));
        Assert.Equal(SpeechFollowEvent.None, follow.Next(Host(4, SpeechStage.Started), 10.5).Event);
    }

    [Fact]
    public void A_host_line_this_speech_never_gets_to_is_said_once_a_speech()
    {
        // This PC's sequence has 2 lines; the host is on its third.
        SpeechFollow follow = At(2, SpeechPhase.None);
        SpeechProgress host = Host(3, SpeechStage.Started);
        Assert.Equal(default, follow.Next(host, 10));
        Assert.Equal(default, follow.Next(host, 10 + SpeechFollow.MissingSeconds - 0.1));
        Assert.Equal(new SpeechFollowStep(SpeechFollowAction.None, SpeechFollowEvent.Missing, 3), follow.Next(host, 10 + SpeechFollow.MissingSeconds));
        Assert.Equal(default, follow.Next(host, 20));

        // A later one isn't said again; a new speech can say it again.
        Assert.Equal(default, follow.Next(Host(4, SpeechStage.Started), 21 + SpeechFollow.MissingSeconds));
        follow.Begin(0, SpeechPhase.None);
        follow.Observe(200, SpeechPhase.Typing);
        follow.Observe(0, SpeechPhase.None);
        Assert.Equal(default, follow.Next(host, 30));
        Assert.Equal(SpeechFollowEvent.Missing, follow.Next(host, 30 + SpeechFollow.MissingSeconds).Event);

        // A bubble in between starts the wait again; before this speech's first line nothing is missing.
        SpeechFollow waiting = At(1, SpeechPhase.None);
        Assert.Equal(default, waiting.Next(host, 0));
        waiting.Observe(100, SpeechPhase.Waiting);
        Assert.Equal(SpeechFollowAction.Skip, waiting.Next(host, 2).Action);
        waiting.Observe(0, SpeechPhase.None);
        Assert.Equal(default, waiting.Next(host, 2.5));
        Assert.Equal(default, waiting.Next(host, 2.5 + SpeechFollow.MissingSeconds - 0.1));
        SpeechFollow none = At(0, SpeechPhase.None);
        Assert.Equal(default, none.Next(host, 0));
        Assert.Equal(default, none.Next(host, 100));
    }

    [Fact]
    public void A_late_start_counts_from_the_hosts_line()
    {
        var follow = new SpeechFollow();
        follow.Begin(0, SpeechPhase.None, firstKey: 4);
        Assert.Equal(4, follow.FirstKey);
        Assert.True(follow.Observe(900, SpeechPhase.Typing));
        Assert.Equal(4, follow.OwnKey);
        Assert.Equal(default, follow.Next(Host(4, SpeechStage.Started), 1));
        Assert.Equal(SpeechFollowEvent.ShownInFull, follow.Next(Host(4, SpeechStage.Shown), 1.1).Event);

        // Begin clears the counts.
        follow.Begin(0, SpeechPhase.None);
        Assert.Equal((0, 0, 0, 1), (follow.Lines, follow.Presses, follow.OwnKey, follow.FirstKey));
    }
}
