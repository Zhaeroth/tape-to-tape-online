using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class MatchPresentationTests
{
    private const long Second = 1_000_000;

    // ------------------------------------------------------------------ keys

    [Fact]
    public void Every_presentation_key_is_recognised_and_other_hud_keys_are_not()
    {
        foreach (string key in new[]
        {
            PresentationKeys.IntroGo, PresentationKeys.IntroStep, PresentationKeys.IntroCanada, PresentationKeys.OutroPhase,
            PresentationKeys.OutroSeed, PresentationKeys.OutroSkip, PresentationKeys.OutroNext, PresentationKeys.OutroSpeech,
            PresentationKeys.OutroWaits, PresentationKeys.Banner, PresentationKeys.BannerDim, PresentationKeys.RewardFade,
            PresentationKeys.GolferForm(0), PresentationKeys.GolferWho(PresentationKeys.MaxGolfers - 1), PresentationKeys.GolferDrawn,
        })
        {
            Assert.True(PresentationKeys.IsKey(key), key);
        }

        foreach (string key in new[] { "clock", "score.home", "north.home", "fmod/intro", "players", "cover.period", "introduction", string.Empty })
        {
            Assert.False(PresentationKeys.IsKey(key), key);
        }

        Assert.Equal("golfer.form.3", PresentationKeys.GolferForm(3));
        Assert.Equal("golfer.who.7", PresentationKeys.GolferWho(7));
    }

    // ------------------------------------------------------------------ the host's intro step

    [Fact]
    public void The_hosts_intro_steps_through_running_revealed_presenting_and_over()
    {
        var tracker = new HostIntroTracker();
        Assert.Equal(IntroStep.None, tracker.Sample(initialized: false, holding: false, revealed: false, presenting: false));
        Assert.Equal(IntroStep.None, tracker.Sample(initialized: true, holding: true, revealed: false, presenting: false));
        Assert.Equal(IntroStep.Running, tracker.Sample(initialized: true, holding: false, revealed: false, presenting: false));
        Assert.Equal(IntroStep.Revealed, tracker.Sample(initialized: true, holding: false, revealed: true, presenting: false));
        Assert.Equal(IntroStep.Presenting, tracker.Sample(initialized: true, holding: false, revealed: true, presenting: true));
        Assert.Equal(IntroStep.Over, tracker.Sample(initialized: true, holding: false, revealed: true, presenting: false));
        Assert.Equal(IntroStep.Over, tracker.Step);
    }

    [Fact]
    public void A_host_that_starts_sampling_after_the_reveal_counts_as_past_its_intro()
    {
        var tracker = new HostIntroTracker();
        Assert.Equal(IntroStep.Over, tracker.Sample(true, false, true, false));

        tracker.Reset();
        Assert.Equal(IntroStep.Presenting, tracker.Sample(true, false, true, true)); // mid-presentation: still presenting
        Assert.Equal(IntroStep.Over, tracker.Sample(true, false, true, false));
    }

    // ------------------------------------------------------------------ the friend's intro gate

    [Fact]
    public void A_friends_intro_is_held_until_the_hosts_runs()
    {
        var gate = new IntroGate();
        Assert.Equal(IntroOpen.None, gate.Hold(0));
        Assert.True(gate.Holding);
        gate.Showing(1 * Second);

        // The host's first values: its loading barrier still holds its match.
        Assert.Equal(IntroOpen.None, gate.Message(go: false, step: IntroStep.None, now: 1 * Second));
        // A message that carries no intro value changes nothing.
        Assert.Equal(IntroOpen.None, gate.Message(go: null, step: null, now: 2 * Second));
        Assert.True(gate.Holding);

        Assert.Equal(IntroOpen.HostIntroRuns, gate.Message(go: true, step: IntroStep.Running, now: 3 * Second));
        Assert.False(gate.Holding);
        Assert.True(gate.Opened);
        Assert.Equal(3 * Second, gate.HeldMicros);

        // Once open it stays open: the game's own call goes through, and nothing holds again this match.
        Assert.Equal(IntroOpen.HostIntroRuns, gate.Hold(4 * Second));
        Assert.Equal(IntroOpen.None, gate.Tick(100 * Second));
    }

    [Fact]
    public void A_friend_that_joins_after_the_hosts_presentation_opens_at_once_and_says_so()
    {
        var gate = new IntroGate();
        gate.Hold(0);
        Assert.Equal(IntroOpen.HostPastIntro, gate.Message(go: true, step: IntroStep.Over, now: Second));

        // The host's values came before this PC's own call (out of order): the gate doesn't hold at all.
        gate.Reset();
        gate.Message(go: true, step: IntroStep.Running, now: 0);
        Assert.Equal(IntroOpen.HostIntroRuns, gate.Hold(Second));
        Assert.Equal(0, gate.HeldMicros);
    }

    [Fact]
    public void A_host_whose_values_never_carry_the_intro_opens_after_a_full_sets_period()
    {
        var gate = new IntroGate();
        gate.Hold(0);
        gate.Showing(0);

        // The first message heard may be a few changed values (just before an event): not yet a reason to open.
        Assert.Equal(IntroOpen.None, gate.Message(go: null, step: null, now: Second));
        Assert.Equal(IntroOpen.None, gate.Tick(Second + IntroGate.OlderHostMicros - 1));
        gate.Message(null, null, 4 * Second);
        Assert.Equal(IntroOpen.HostSaysNothing, gate.Tick(Second + IntroGate.OlderHostMicros));

        // A full set within the period carries the intro: the gate waits for the host's go.
        gate.Reset();
        gate.Hold(0);
        gate.Message(null, null, Second);
        gate.Message(false, IntroStep.None, 5 * Second);
        Assert.Equal(IntroOpen.None, gate.Tick(Second + IntroGate.OlderHostMicros));
        Assert.True(gate.Holding);
    }

    [Fact]
    public void A_held_intro_opens_after_the_longest_hold_or_a_silent_stream()
    {
        var gate = new IntroGate();
        gate.Hold(0);
        gate.Message(false, IntroStep.None, Second);
        Assert.Equal(IntroOpen.None, gate.Tick(IntroGate.MaxHoldMicros - 1));
        Assert.Equal(IntroOpen.TimedOut, gate.Tick(IntroGate.MaxHoldMicros));

        // The host's values came, then stopped.
        gate.Reset();
        gate.Hold(0);
        gate.Showing(0);
        gate.Message(false, IntroStep.None, 2 * Second);
        Assert.Equal(IntroOpen.None, gate.Tick(2 * Second + IntroGate.SilenceMicros - 1));
        Assert.Equal(IntroOpen.NoHostValues, gate.Tick(2 * Second + IntroGate.SilenceMicros));

        // Playback is on here but no value has come yet: the host's own match still loads (its stream starts after that), so
        // this PC, far ahead of a slow host, keeps waiting for longer than the silence after values.
        gate.Reset();
        gate.Hold(0);
        gate.Showing(2 * Second);
        Assert.Equal(IntroOpen.None, gate.Tick(2 * Second + IntroGate.SilenceMicros));
        Assert.Equal(IntroOpen.None, gate.Tick(2 * Second + IntroGate.FirstValuesMicros - 1));
        Assert.Equal(IntroOpen.NoHostValues, gate.Tick(2 * Second + IntroGate.FirstValuesMicros));

        // The longest hold outlasts the host's longest barrier (45 s, up to 120 s for custom content).
        Assert.True(IntroGate.MaxHoldMicros > (long)ContentHold.MaxHold.TotalSeconds * Second);

        // Messages keep it waiting while the host's barrier holds (other friends still loading).
        gate.Reset();
        gate.Hold(0);
        gate.Showing(0);
        for (long t = Second; t < 40 * Second; t += 5 * Second)
        {
            Assert.Equal(IntroOpen.None, gate.Message(false, IntroStep.None, t));
            Assert.Equal(IntroOpen.None, gate.Tick(t + Second));
        }

        Assert.True(gate.Holding);
    }

    [Fact]
    public void Releasing_opens_a_held_gate_only()
    {
        var gate = new IntroGate();
        Assert.False(gate.Release(0));
        gate.Hold(0);
        Assert.True(gate.Release(5 * Second));
        Assert.Equal(IntroOpen.Released, gate.Reason);
        Assert.Equal(5 * Second, gate.HeldMicros);
        Assert.False(gate.Release(6 * Second));
    }

    // ------------------------------------------------------------------ the Canada pick

    [Fact]
    public void A_friends_canada_wait_ends_once_the_hosts_pick_has_run_and_is_over()
    {
        var wait = new CanadaWait();
        wait.Start(0);
        Assert.Equal(CanadaWaitEnd.None, wait.Check(Second, connected: true));
        wait.Host(pickRunning: false, step: IntroStep.Running); // not started on the host yet
        Assert.Equal(CanadaWaitEnd.None, wait.Check(2 * Second, true));
        wait.Host(true, null);
        Assert.Equal(CanadaWaitEnd.None, wait.Check(10 * Second, true));
        wait.Host(false, null);
        Assert.Equal(CanadaWaitEnd.HostPicked, wait.Check(11 * Second, true));
        Assert.False(wait.Waiting);
        Assert.Equal(CanadaWaitEnd.None, wait.Check(12 * Second, true));
    }

    [Fact]
    public void A_canada_wait_ends_when_the_host_is_past_the_pick_or_it_waited_too_long_or_the_session_went()
    {
        var wait = new CanadaWait();
        wait.Host(true, null);
        wait.Host(false, null); // the host's pick was over before this PC's intro got there
        wait.Start(0);
        Assert.Equal(CanadaWaitEnd.HostPicked, wait.Check(0, true));

        wait.Reset();
        wait.Start(0);
        wait.Host(null, IntroStep.Revealed);
        Assert.Equal(CanadaWaitEnd.HostPastPick, wait.Check(Second, true));

        wait.Reset();
        wait.Start(0);
        Assert.Equal(CanadaWaitEnd.None, wait.Check(CanadaWait.MaxWaitMicros - 1, true));
        Assert.Equal(CanadaWaitEnd.TimedOut, wait.Check(CanadaWait.MaxWaitMicros, true));

        wait.Reset();
        wait.Start(0);
        Assert.Equal(CanadaWaitEnd.Released, wait.Check(Second, connected: false));
    }

    // ------------------------------------------------------------------ the host's values as read

    [Fact]
    public void Presentation_values_are_read_and_bad_ones_are_refused_without_throwing()
    {
        var values = new PresentationValues();
        Assert.False(values.Take("clock", NetValue.FromFloat(3f)));
        Assert.True(values.Take(PresentationKeys.IntroGo, NetValue.FromBool(true)));
        Assert.True(values.Take(PresentationKeys.IntroStep, NetValue.FromInt(3)));
        Assert.True(values.Take(PresentationKeys.IntroCanada, NetValue.FromBool(false)));
        Assert.True(values.IntroGoNow);
        Assert.Equal(IntroStep.Presenting, values.IntroStepNow);
        Assert.False(values.CanadaNow);
        values.EndMessage();
        Assert.Null(values.IntroGoNow);
        Assert.Null(values.IntroStepNow);

        foreach ((string key, NetValue bad) in new[]
        {
            (PresentationKeys.IntroGo, NetValue.FromInt(1)),
            (PresentationKeys.IntroStep, NetValue.FromInt(5)),
            (PresentationKeys.IntroStep, NetValue.FromInt(-1)),
            (PresentationKeys.OutroPhase, NetValue.FromInt(9)),
            (PresentationKeys.OutroPhase, NetValue.FromString("2")),
            (PresentationKeys.OutroNext, NetValue.FromInt(OutroSteps.MaxContinues + 1)),
            (PresentationKeys.OutroSeed, NetValue.FromFloat(1f)),
            (PresentationKeys.Banner, NetValue.FromString("yes")),
            (PresentationKeys.BannerDim, NetValue.FromInt(1)),
            (PresentationKeys.RewardFade, NetValue.FromString("yes")),
            (PresentationKeys.GolferForm(0), NetValue.FromInt(3)),
            (PresentationKeys.GolferWho(0), NetValue.FromInt(17)),
            (PresentationKeys.GolferDrawn, NetValue.FromInt(17)),
            (PresentationKeys.OutroSpeech, NetValue.FromInt(-1)),
            (PresentationKeys.OutroSpeech, NetValue.FromInt(SpeechProgress.MaxValue + 1)),
            (PresentationKeys.OutroSpeech, NetValue.FromInt(1)), // a key of 0 with a stage: no such progress
            (PresentationKeys.OutroSpeech, NetValue.FromInt(7)), // a stage of 3
            (PresentationKeys.OutroSpeech, NetValue.FromString("12")),
            (PresentationKeys.OutroWaits, NetValue.FromInt(1)),
        })
        {
            int refused = values.Refused;
            Assert.True(values.Take(key, bad), key);
            Assert.Equal(refused + 1, values.Refused);
        }

        Assert.Null(values.IntroGoNow);
        Assert.Null(values.Outro);
        Assert.Equal(0, values.OutroNext);
        Assert.True(values.OutroSpeech.IsNone);
        Assert.False(values.OutroWaits);
        Assert.Null(values.Banner);
        Assert.Null(values.BannerDim);
        Assert.Null(values.RewardFade);
        Assert.Equal(NetValue.Null, values.GolferDrawn);
        Assert.False(values.GolfersChanged);
        Assert.False(values.TryGolfer(0, out _, out _));

        // A newer host's golfer key is a presentation key, ignored without counting it as bad.
        int before = values.Refused;
        Assert.True(values.Take("golfer.aura.0", NetValue.FromInt(1)));
        Assert.Equal(before, values.Refused);

        // A golfer the host's stream doesn't carry comes as its scene path: nothing to follow, and not a bad value.
        Assert.True(values.Take(PresentationKeys.GolferWho(1), NetValue.FromString("go:standard_game/Golfer#3")));
        Assert.Equal(before, values.Refused);
        Assert.False(values.TryGolfer(1, out _, out _));

        // The comic's speech as one progress, and whether the host's page waits.
        Assert.True(values.Take(PresentationKeys.OutroSpeech, NetValue.FromInt(SpeechProgress.Line(3, SpeechStage.Shown).Value)));
        Assert.True(values.Take(PresentationKeys.OutroWaits, NetValue.FromBool(true)));
        Assert.Equal(SpeechProgress.Line(3, SpeechStage.Shown), values.OutroSpeech);
        Assert.True(values.OutroWaits);
        values.EndMessage();
        Assert.Equal(3, values.OutroSpeech.Key); // kept: a message carries only what changed
        values.Reset();
        Assert.True(values.OutroSpeech.IsNone);
        Assert.False(values.OutroWaits);
    }

    [Fact]
    public void Outro_values_keep_their_last_value_and_a_null_phase_means_no_outro()
    {
        var values = new PresentationValues();
        values.Take(PresentationKeys.OutroPhase, NetValue.FromInt((int)OutroPhase.Celebrating));
        values.Take(PresentationKeys.OutroSeed, NetValue.FromInt(-123456));
        values.Take(PresentationKeys.OutroSkip, NetValue.FromBool(true));
        values.Take(PresentationKeys.OutroNext, NetValue.FromInt(2));
        values.Take(PresentationKeys.Banner, NetValue.FromBool(true));
        values.Take(PresentationKeys.BannerDim, NetValue.FromBool(true));
        values.Take(PresentationKeys.RewardFade, NetValue.FromBool(false));
        values.EndMessage();
        Assert.Equal(OutroPhase.Celebrating, values.Outro);
        Assert.Equal(-123456, values.OutroSeed);
        Assert.True(values.OutroSkip);
        Assert.Equal(2, values.OutroNext);
        Assert.True(values.Banner);
        Assert.True(values.BannerDim);
        Assert.False(values.RewardFade);

        values.Take(PresentationKeys.OutroPhase, NetValue.Null);
        Assert.Null(values.Outro);

        values.Reset();
        Assert.Null(values.Outro);
        Assert.Equal(0, values.OutroSeed);
        Assert.Null(values.Banner);
        Assert.Null(values.BannerDim);
        Assert.Null(values.RewardFade);
    }

    [Fact]
    public void The_golfer_drawn_look_is_a_reference_or_none_and_a_change_is_noted_once()
    {
        var values = new PresentationValues();
        values.Take(PresentationKeys.GolferDrawn, NetValue.Null);
        Assert.False(values.GolfersChanged); // none, as it was

        values.Take(PresentationKeys.GolferDrawn, NetValue.FromRef(42));
        Assert.True(values.GolfersChanged);
        Assert.Equal(NetValue.FromRef(42), values.GolferDrawn);

        values.EndMessage();
        values.Take(PresentationKeys.GolferDrawn, NetValue.FromRef(42)); // the full set every 5 s
        Assert.False(values.GolfersChanged);
        Assert.Equal(NetValue.FromRef(42), values.GolferDrawn); // kept: a message carries only what changed

        values.Take(PresentationKeys.GolferDrawn, NetValue.Null);
        Assert.True(values.GolfersChanged);
        Assert.Equal(NetValue.Null, values.GolferDrawn);

        // A golfer the stream doesn't carry comes as its scene path: nothing to follow, not a bad value.
        int refused = values.Refused;
        values.Take(PresentationKeys.GolferDrawn, NetValue.FromString("go:standard_game/Golfer#3"));
        Assert.Equal(refused, values.Refused);
        Assert.NotEqual(NetValueKind.Reference, values.GolferDrawn.Kind);

        values.Reset();
        Assert.Equal(NetValue.Null, values.GolferDrawn);
    }

    [Fact]
    public void Golfer_forms_are_read_by_index_and_a_change_is_noted_once()
    {
        var values = new PresentationValues();
        values.Take(PresentationKeys.GolferWho(1), NetValue.FromRef(42));
        values.Take(PresentationKeys.GolferForm(1), NetValue.FromInt(1));
        Assert.True(values.GolfersChanged);
        Assert.True(values.TryGolfer(1, out NetValue who, out int form));
        Assert.Equal(NetValue.FromRef(42), who);
        Assert.Equal(1, form);
        Assert.False(values.TryGolfer(0, out _, out _));
        Assert.False(values.TryGolfer(-1, out _, out _));
        Assert.False(values.TryGolfer(PresentationKeys.MaxGolfers, out _, out _));

        values.EndMessage();
        values.Take(PresentationKeys.GolferForm(1), NetValue.FromInt(1)); // the full set every 5 s: same values
        Assert.False(values.GolfersChanged);
        values.Take(PresentationKeys.GolferForm(1), NetValue.FromInt(2));
        Assert.True(values.GolfersChanged);
        Assert.Equal("giant mutant", GolferForms.Name(2));
        Assert.Equal("mutant", GolferForms.Name(1));
        Assert.Equal("normal", GolferForms.Name(0));
        Assert.Equal("7", GolferForms.Name(7));
    }

    // ------------------------------------------------------------------ the golfer outro

    [Fact]
    public void A_friends_outro_starts_with_the_hosts_and_follows_its_skip_and_presses()
    {
        var steps = new OutroSteps();
        Assert.Equal(OutroAction.None, steps.Next(null, false, 0, false, false, canStart: true, now: 0));
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Waiting, false, 0, false, false, true, 0));
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Revealing, false, 0, false, false, canStart: false, now: 0));

        OutroAction action = steps.Next(OutroPhase.Revealing, false, 0, false, false, true, 0);
        Assert.Equal(OutroAction.Start, action);
        steps.Did(action);
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Celebrating, false, 0, false, false, true, Second));

        action = steps.Next(OutroPhase.Celebrating, hostSkipped: true, 0, false, false, true, 2 * Second);
        Assert.Equal(OutroAction.Skip, action);
        steps.Did(action);
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Comic, true, 0, false, false, true, 3 * Second));

        // The host pressed continue before this PC's comic waits for one: kept until it does.
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Comic, true, 1, false, pageWaits: false, true, 4 * Second));
        action = steps.Next(OutroPhase.Comic, true, 1, false, pageWaits: true, true, 5 * Second);
        Assert.Equal(OutroAction.Continue, action);
        steps.Did(action);
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Comic, true, 1, false, true, true, 6 * Second));

        // Two presses heard at once are applied one per wait.
        Assert.Equal(OutroAction.Continue, steps.Next(OutroPhase.Comic, true, 3, false, true, true, 7 * Second));
        steps.Did(OutroAction.Continue);
        Assert.Equal(OutroAction.Continue, steps.Next(OutroPhase.Comic, true, 3, false, true, true, 8 * Second));
        steps.Did(OutroAction.Continue);
        Assert.Equal(3, steps.Continued);

        // This PC's outro ends by itself: finished, nothing more.
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Done, true, 3, localDone: true, false, true, 9 * Second));
        Assert.True(steps.Finished);
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Done, true, 3, false, false, true, 99 * Second));
        Assert.Equal(OutroPhase.Waiting, steps.FirstSeen);
    }

    [Fact]
    public void A_friends_outro_is_ended_a_moment_after_the_hosts_is_over()
    {
        var steps = new OutroSteps();
        steps.Did(steps.Next(OutroPhase.Celebrating, false, 0, false, false, true, 0));
        steps.Did(steps.Next(OutroPhase.Done, false, 0, false, false, true, Second)); // skips first: the host is past it
        Assert.True(steps.Skipped);
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Done, false, 0, false, false, true, 2 * Second));
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Done, false, 0, false, false, true, 2 * Second + OutroSteps.GraceMicros - 1));
        Assert.Equal(OutroAction.CutShort, steps.Next(OutroPhase.Done, false, 0, false, false, true, 2 * Second + OutroSteps.GraceMicros));
        steps.Did(OutroAction.CutShort);
        Assert.True(steps.Finished);

        // The host's outro went away without a Done (null): the same.
        steps.Reset();
        steps.Did(steps.Next(OutroPhase.Celebrating, false, 0, false, false, true, 0));
        steps.Did(steps.Next(OutroPhase.Celebrating, true, 0, false, false, true, 0));
        Assert.Equal(OutroAction.None, steps.Next(null, true, 0, false, false, true, Second));
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Comic, true, 0, false, false, true, 2 * Second)); // back: the wait starts over
        Assert.Equal(OutroAction.None, steps.Next(null, true, 0, false, false, true, 3 * Second));
        Assert.Equal(OutroAction.CutShort, steps.Next(null, true, 0, false, false, true, 3 * Second + OutroSteps.GraceMicros));
    }

    [Fact]
    public void A_comic_that_waits_goes_on_when_the_hosts_outro_ends_with_no_press_for_it()
    {
        // The host's comic ended with the cup's exit (its last strip): it has no continue, but this PC's own strip does.
        var steps = new OutroSteps();
        steps.Did(steps.Next(OutroPhase.Comic, false, 0, false, false, true, 0));
        steps.Did(steps.Next(OutroPhase.Comic, false, 0, false, false, true, 0)); // straight on from its celebration
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Comic, false, 0, false, pageWaits: true, true, Second));

        OutroAction action = steps.Next(OutroPhase.Done, false, 0, false, pageWaits: true, true, 2 * Second);
        Assert.Equal(OutroAction.Continue, action);
        steps.Did(action);
        Assert.Equal(OutroAction.None, steps.Next(OutroPhase.Done, false, 0, false, pageWaits: false, true, 3 * Second));

        // Its own ending runs by itself; if it doesn't end, the cut-short still comes.
        Assert.Equal(OutroAction.CutShort, steps.Next(OutroPhase.Done, false, 0, false, false, true, 3 * Second + OutroSteps.GraceMicros));

        // The host's outro went away altogether (no director): the same.
        var gone = new OutroSteps();
        gone.Did(gone.Next(OutroPhase.Comic, false, 0, false, false, true, 0));
        gone.Did(gone.Next(OutroPhase.Comic, false, 0, false, false, true, 0));
        Assert.Equal(OutroAction.Continue, gone.Next(null, false, 0, false, pageWaits: true, true, Second));
    }

    [Fact]
    public void A_friend_joining_mid_outro_starts_at_the_comic_and_one_joining_after_it_starts_nothing()
    {
        var late = new OutroSteps();
        Assert.Equal(OutroAction.Start, late.Next(OutroPhase.Comic, true, 1, false, false, true, 0));
        late.Did(OutroAction.Start);
        Assert.Equal(OutroAction.Skip, late.Next(OutroPhase.Comic, true, 1, false, false, true, 0));
        late.Did(OutroAction.Skip);
        Assert.Equal(OutroAction.Continue, late.Next(OutroPhase.Comic, true, 1, false, true, true, Second));
        Assert.Equal(OutroPhase.Comic, late.FirstSeen);

        var after = new OutroSteps();
        Assert.Equal(OutroAction.None, after.Next(OutroPhase.Done, true, 2, false, false, true, 0));
        Assert.False(after.Started);
        Assert.Equal(OutroAction.None, after.Next(OutroPhase.Done, true, 2, false, false, true, 99 * Second));
    }

    [Fact]
    public void The_hosts_comic_presses_are_counted_once_per_wait()
    {
        var counter = new ComicPressCounter();
        Assert.Equal(0, counter.Sample(0, false));
        Assert.Equal(0, counter.Sample(100, completed: false));
        Assert.Equal(1, counter.Sample(100, completed: true));
        Assert.Equal(1, counter.Sample(100, completed: true));
        Assert.Equal(1, counter.Sample(200, completed: false));
        Assert.Equal(2, counter.Sample(200, completed: true));
        counter.Reset();
        Assert.Equal(0, counter.Count);
        for (int i = 1; i <= OutroSteps.MaxContinues + 5; i++)
        {
            counter.Sample(i, true);
        }

        Assert.Equal(OutroSteps.MaxContinues, counter.Count);
    }

    // ------------------------------------------------------------------ the comic's speech

    private static int Value(int key, SpeechStage stage) => SpeechProgress.Line(key, stage).Value;

    [Fact]
    public void The_hosts_speakers_combine_into_one_speech_whoever_speaks()
    {
        Assert.True(ComicSpeech.Combine(System.ReadOnlySpan<int>.Empty).IsNone);
        Assert.True(ComicSpeech.Combine(new[] { 0, 0, 0 }).IsNone);

        // Angus's line 1, advanced; Tom's line 1 now typing.
        Assert.Equal(SpeechProgress.Line(2, SpeechStage.Started), ComicSpeech.Combine(new[] { Value(1, SpeechStage.Advanced), Value(1, SpeechStage.Started), 0 }));

        // Shown in full, then advanced with no next line yet.
        Assert.Equal(SpeechProgress.Line(2, SpeechStage.Shown), ComicSpeech.Combine(new[] { Value(1, SpeechStage.Advanced), Value(1, SpeechStage.Shown) }));
        Assert.Equal(SpeechProgress.Line(2, SpeechStage.Advanced), ComicSpeech.Combine(new[] { Value(1, SpeechStage.Advanced), Value(1, SpeechStage.Advanced) }));

        // Angus again: three lines in all.
        Assert.Equal(SpeechProgress.Line(3, SpeechStage.Started), ComicSpeech.Combine(new[] { Value(2, SpeechStage.Started), Value(1, SpeechStage.Advanced) }));

        // A press the host's patch couldn't see (no press stage on an earlier line): still the lines, never further than started.
        Assert.Equal(SpeechProgress.Line(3, SpeechStage.Started), ComicSpeech.Combine(new[] { Value(2, SpeechStage.Advanced), Value(1, SpeechStage.Started) }));

        // Not a progress: as none. Past the cap: at the cap.
        Assert.Equal(SpeechProgress.Line(1, SpeechStage.Shown), ComicSpeech.Combine(new[] { 3, Value(1, SpeechStage.Shown), -8, 99999 }));
        Assert.Equal(SpeechProgress.MaxKey, ComicSpeech.Combine(new[] { Value(200, SpeechStage.Advanced), Value(200, SpeechStage.Advanced) }).Key);
    }

    [Fact]
    public void The_same_dialogue_is_followed_press_for_press()
    {
        var speech = new ComicSpeech();
        // No line here yet, whatever the host says.
        Assert.Equal(SpeechStage.Started, speech.StageFor(0, SpeechProgress.Line(1, SpeechStage.Advanced), ComicHost.Speaking, 0));

        SpeechProgress host = SpeechProgress.Line(1, SpeechStage.Started);
        Assert.Equal(SpeechStage.Started, speech.StageFor(1, host, ComicHost.Speaking, 1));
        Assert.Equal(SpeechStage.Shown, speech.StageFor(1, SpeechProgress.Line(1, SpeechStage.Shown), ComicHost.Speaking, 2));
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(1, SpeechProgress.Line(1, SpeechStage.Advanced), ComicHost.Speaking, 3));

        // The host's next line started before this PC's: the first is past on the host's screen, so it goes on here.
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(1, SpeechProgress.Line(2, SpeechStage.Started), ComicHost.Speaking, 3.1));
        Assert.Equal(SpeechStage.Started, speech.StageFor(2, SpeechProgress.Line(2, SpeechStage.Started), ComicHost.Speaking, 3.2));

        // However long the host reads, a line it's on doesn't go on by itself while it may still speak.
        Assert.Equal(SpeechStage.Started, speech.StageFor(2, SpeechProgress.Line(2, SpeechStage.Started), ComicHost.Speaking, 500));
        Assert.Equal(0, speech.RanOut);
    }

    [Fact]
    public void A_dialogue_longer_than_the_hosts_never_stalls()
    {
        // The host's cup strip has 4 lines, this profile's has 6 (or the narration's 15), speakers aside.
        var speech = new ComicSpeech();
        SpeechProgress host = SpeechProgress.Line(4, SpeechStage.Advanced);

        // Line 5 here, with no more of the host's: it waits while the host might still speak...
        Assert.Equal(SpeechStage.Started, speech.StageFor(5, host, ComicHost.Speaking, 10));
        Assert.Equal(SpeechStage.Started, speech.StageFor(5, host, ComicHost.Speaking, 100));

        // ...and goes on at a reading pace once the host's page waits for its continue, counted from the line's start.
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(5, host, ComicHost.Waits, 100));
        Assert.Equal(1, speech.RanOut);
        Assert.Equal(SpeechStage.Started, speech.StageFor(6, host, ComicHost.Waits, 101));
        Assert.Equal(SpeechStage.Started, speech.StageFor(6, host, ComicHost.Waits, 101 + ComicSpeech.ExtraLineSeconds - 0.01));
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(6, host, ComicHost.Waits, 101 + ComicSpeech.ExtraLineSeconds));
        Assert.Equal(2, speech.RanOut);

        // The host has moved on (its continue, or its outro is over): whatever's left goes at once.
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(7, host, ComicHost.MovedOn, 102));
        Assert.Equal(3, speech.RanOut);
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(7, host, ComicHost.MovedOn, 102.1));
        Assert.Equal(3, speech.RanOut); // a line counts once, however many frames it's pressed

        speech.Reset();
        Assert.Equal(0, speech.RanOut);
    }

    [Fact]
    public void A_host_with_no_speech_or_whose_presses_cant_be_seen_still_lets_a_friends_comic_finish()
    {
        // The host's comic is complete (no dialogue: straight to its continue); this profile's isn't.
        var speech = new ComicSpeech();
        Assert.Equal(SpeechStage.Started, speech.StageFor(1, SpeechProgress.None, ComicHost.Waits, 0));
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(1, SpeechProgress.None, ComicHost.Waits, ComicSpeech.ExtraLineSeconds));

        // The host's press patch is off: only its lines' starts are heard. Its last line never says advanced; its page waits.
        speech.Reset();
        SpeechProgress host = SpeechProgress.Line(4, SpeechStage.Started);
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(3, host, ComicHost.Speaking, 0)); // behind its newest line
        Assert.Equal(SpeechStage.Started, speech.StageFor(4, host, ComicHost.Speaking, 1));
        Assert.Equal(SpeechStage.Advanced, speech.StageFor(4, host, ComicHost.Waits, 1 + ComicSpeech.ExtraLineSeconds));
    }
}
