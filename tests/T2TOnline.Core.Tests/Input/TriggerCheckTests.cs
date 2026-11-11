using System;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

/// <summary>
/// A trigger's peak and what the player is told (a pad set up by Steam Input can send an RT that never reaches the
/// host's game): the peaks a pad and a controller check keep, the outcomes, the advice.
/// </summary>
public class TriggerCheckTests
{
    private static readonly PadCheckPad Steam = new("Steam Controller", "Steam Controller", "a generic pad", "a generic pad", "what you'd play with online", PadCheckMaps.Defaults, 0);

    [Fact]
    public void Outcomes_follow_the_games_threshold()
    {
        Assert.Equal(TriggerOutcome.NeverMoved, TriggerCheck.Of(0f));
        Assert.Equal(TriggerOutcome.NeverMoved, TriggerCheck.Of(0.049f));
        Assert.Equal(TriggerOutcome.NeverMoved, TriggerCheck.Of(float.NaN));
        Assert.Equal(TriggerOutcome.Under, TriggerCheck.Of(0.05f));
        Assert.Equal(TriggerOutcome.Under, TriggerCheck.Of(0.49f));
        Assert.Equal(TriggerOutcome.Reached, TriggerCheck.Of(0.5f));
        Assert.Equal(TriggerOutcome.Reached, TriggerCheck.Of(1f));
        Assert.Equal("0.38", TriggerCheck.Format(0.3812f));
        Assert.Equal("1.00", TriggerCheck.Format(7f));
        Assert.Equal("0.00", TriggerCheck.Format(float.NegativeInfinity));
    }

    [Fact]
    public void The_steam_input_profile_is_rewireds_steam_controller()
    {
        Assert.True(TriggerCheck.IsSteamInput(new Guid("2694F4B9-9D84-4F55-9EE8-78FBBA744B7D")));
        Assert.False(TriggerCheck.IsSteamInput(Guid.Empty));
    }

    [Fact]
    public void A_trigger_that_never_moved_while_the_other_did_points_at_steam_inputs_layout()
    {
        string? advice = TriggerCheck.Advice("RT", 0f, "LT", 1f, steamInput: true);
        Assert.NotNull(advice);
        Assert.StartsWith("RT never moved while LT did: if you pulled it, Steam Input's layout for Tape to Tape isn't sending the right trigger", advice, StringComparison.Ordinal);
        Assert.Contains("set Right Trigger to the gamepad's Right Trigger (analog)", advice, StringComparison.Ordinal);
        Assert.StartsWith("RT never moved while LT did: if you pulled it, this pad doesn't send its right trigger", TriggerCheck.Advice("RT", 0f, "LT", 1f, steamInput: false), StringComparison.Ordinal);
        Assert.Contains("isn't sending the left trigger", TriggerCheck.Advice("LT", 0f, "RT", 1f, steamInput: true), StringComparison.Ordinal);

        // Neither moved: it may just not have been pulled yet.
        Assert.Null(TriggerCheck.Advice("RT", 0f, "LT", 0.2f, steamInput: true));
        Assert.Null(TriggerCheck.Advice("RT", 0.9f, "LT", 1f, steamInput: true));
    }

    [Fact]
    public void A_trigger_that_stops_short_says_where_whatever_the_other_did()
    {
        string? advice = TriggerCheck.Advice("RT", 0.38f, "LT", 0f, steamInput: true);
        Assert.StartsWith("RT reaches 0.38 at most: the game's trigger actions need more than 0.50, on a couch too. In Steam,", advice, StringComparison.Ordinal);
        Assert.Contains("analog, full range", advice, StringComparison.Ordinal);
        Assert.EndsWith("(a worn trigger, or its range set lower in the pad's own software).", TriggerCheck.Advice("LT", 0.2f, "RT", 1f, steamInput: false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_notice_needs_the_other_trigger_to_have_reached()
    {
        Assert.Null(TriggerCheck.Notice("RT", 0f, "LT", 0.3f, steamInput: true));
        Assert.Null(TriggerCheck.Notice("RT", 0.6f, "LT", 1f, steamInput: true));
        Assert.Contains("Steam Input's layout may not send it", TriggerCheck.Notice("RT", 0f, "LT", 1f, steamInput: true), StringComparison.Ordinal);
        Assert.StartsWith("Your pad's RT reaches 0.31 at most", TriggerCheck.Notice("RT", 0.31f, "LT", 1f, steamInput: false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pads_usage_keeps_each_axis_peak_and_names_them()
    {
        var usage = new PadUsage();
        usage.Observe(PadButtons.A, 0.2f, -0.31f, 0f, 0.9f, 1f, 0.3f);
        usage.Observe(PadButtons.None, -0.1f, 0f, float.NaN, 0f, 0.5f, 0.12f);

        Assert.Equal(0.3f, usage.Peak(PadAxis.RightTrigger));
        Assert.Equal(0.31f, usage.Peak(PadAxis.LeftStickY));
        Assert.Equal(0f, usage.Peak(PadAxis.RightStickX));
        Assert.Equal(0f, usage.Peak((PadAxis)9));
        string line = usage.DescribeWithPeaks();
        Assert.Contains("; never ", line, StringComparison.Ordinal);
        Assert.Contains("; trigger peaks LT 1.00 RT 0.30; sticks short of 0.50: LeftStickX 0.20, LeftStickY 0.31", line, StringComparison.Ordinal);
        Assert.EndsWith("trigger peaks LT 0.00 RT 0.00", new PadUsage().DescribeWithPeaks(), StringComparison.Ordinal);
        Assert.StartsWith("Your pad's RT reaches 0.30 at most", usage.TriggerNotice(steamInput: true), StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_names_a_short_trigger_with_its_peak_and_advises()
    {
        var check = new PadCheck(steamInput: true);
        int version = check.Version;
        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 1f, 0.38f);
        Assert.True(check.Version > version);
        Assert.Equal(0.38f, check.Peak(PadCheck.IndexOf(PadAxis.RightTrigger)));

        PadCheckVerdict verdict = check.Verdict();
        Assert.Contains("not yet: B, X, Y, LB, RB, RT (0.38 at most), Back", verdict.Text, StringComparison.Ordinal);
        Assert.EndsWith("set Right Trigger to the gamepad's Right Trigger, analog, full range; then press Reset here.", verdict.Text, StringComparison.Ordinal);

        // The peak moving on remakes the texts once per 0.05, not every frame.
        version = check.Version;
        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 1f, 0.39f);
        Assert.Equal(version, check.Version);
        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 1f, 0.41f);
        Assert.Equal(version + 1, check.Version);

        check.Reset();
        Assert.Equal(0f, check.Peak(PadCheck.IndexOf(PadAxis.RightTrigger)));
        Assert.Null(check.TriggerAdvice());
    }

    [Fact]
    public void A_check_whose_rt_never_moves_while_lt_reads_says_steam_inputs_layout()
    {
        var check = new PadCheck(steamInput: true);
        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 1f, 0f);
        Assert.Contains("RT never moved while LT did: if you pulled it, Steam Input's layout", check.Verdict().Text, StringComparison.Ordinal);

        // A template that lacks RT says that instead.
        var lacking = new PadCheck(PadCheck.MissingMask(PadButtons.None, 1 << (int)PadAxis.RightTrigger), steamInput: true);
        lacking.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 1f, 0f);
        Assert.Null(lacking.TriggerAdvice());
        Assert.DoesNotContain("never moved", lacking.Verdict().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_line_names_the_peaks_not_read_and_what_the_triggers_give()
    {
        var check = new PadCheck();
        check.Observe(PadButtons.A, 0.31f, 0f, 0f, 0f, 1f, 0f);
        string[] actions = new string[PadCheck.ElementCount];
        Array.Fill(actions, string.Empty);
        actions[PadCheck.IndexOf(PadAxis.LeftTrigger)] = "Backwards Left, UI Left Trigger";

        string line = check.Summary(Steam with { ElementActions = actions }, "tab closed", TimeSpan.FromSeconds(5));
        Assert.Contains("; peaks of axes not read: RT 0.00, Left stick X 0.31 (the game needs 0.50); LT gives Backwards Left, UI Left Trigger; RT gives no action; maps:", line, StringComparison.Ordinal);
        Assert.DoesNotContain("gives", check.Summary(Steam, "x", TimeSpan.Zero), StringComparison.Ordinal);
    }
}
