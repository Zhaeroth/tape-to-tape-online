using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PadCheckTests
{
    private static readonly PadCheckPad Steam = new("Steam Controller", "Steam Controller", "a generic pad", "a generic pad", "what you'd play with online", PadCheckMaps.Defaults, 0);

    private static int IndexOf(string name) => PadCheck.Elements.Select((e, i) => (e, i)).Single(p => p.e.Name == name).i;

    private static void ReadEverything(PadCheck check)
    {
        check.Observe(PadButtons.All, -1f, -1f, -1f, -1f, 1f, 1f);
        check.Observe(PadButtons.None, 1f, 1f, 1f, 1f, 0f, 0f);
    }

    [Fact]
    public void The_twenty_elements_are_the_fourteen_buttons_and_six_axes_a_friend_sends_each_once()
    {
        IReadOnlyList<PadCheckElement> elements = PadCheck.Elements;

        Assert.Equal(20, elements.Count);
        Assert.Equal(20, PadCheck.ElementCount);
        Assert.Equal(elements.Count, elements.Select(e => e.Name).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, PadState.ButtonCount), elements.Where(e => !e.IsAxis).Select(e => e.Index).OrderBy(i => i));
        Assert.Equal(Enumerable.Range(0, PadState.AxisCount), elements.Where(e => e.IsAxis).Select(e => e.Index).OrderBy(i => i));
        Assert.All(elements, e => Assert.True(GamepadTemplateLayout.Carries(e.TemplateElementId)));
        Assert.Equal(20, elements.Select(e => e.TemplateElementId).Distinct().Count());
        Assert.Equal(4, elements.Count(e => e.TwoWay));
    }

    [Fact]
    public void Elements_name_what_a_player_knows_and_bind_as_the_template_does()
    {
        PadCheckElement rt = PadCheck.Elements[IndexOf("RT")];
        PadCheckElement dpadLeft = PadCheck.Elements[IndexOf("D-pad left")];
        PadCheckElement leftX = PadCheck.Elements[IndexOf("Left stick X")];
        PadCheckElement rightY = PadCheck.Elements[IndexOf("Right stick Y")];

        Assert.Equal(13, rt.TemplateElementId); // rightShoulder2
        Assert.Equal(BindingElement.Axis(PadAxis.RightTrigger), rt.Binding);
        Assert.False(rt.TwoWay);
        Assert.Equal(BindingElement.PadButton(12), dpadLeft.Binding);
        Assert.Equal(22, dpadLeft.TemplateElementId);
        Assert.Equal(("left", "right"), (leftX.NegativeName, leftX.PositiveName));
        Assert.Equal(("down", "up"), (rightY.NegativeName, rightY.PositiveName));
    }

    [Fact]
    public void Nothing_read_says_what_to_do_and_names_everything_as_not_yet()
    {
        var check = new PadCheck();

        PadCheckVerdict verdict = check.Verdict();

        Assert.Equal(PadCheckOutcome.NothingYet, verdict.Outcome);
        Assert.StartsWith("Nothing read yet", verdict.Text, StringComparison.Ordinal);
        Assert.Equal(20, check.NotYet().Count);
        Assert.False(check.AnyRead);
        Assert.Equal(-1, check.LastActive);
    }

    [Fact]
    public void A_button_lights_while_held_and_stays_read_after()
    {
        var check = new PadCheck();
        int a = IndexOf("A");

        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.True(check.IsActive(a));
        Assert.True(check.IsSeen(a));
        Assert.Equal(1f, check.Value(a));
        Assert.Equal(a, check.LastActive);

        check.Observe(PadButtons.None, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.False(check.IsActive(a));
        Assert.True(check.IsSeen(a));
        Assert.Equal(0f, check.Value(a));
        Assert.Equal(1, check.SeenCount);
        Assert.Equal(PadCheckOutcome.Partial, check.Verdict().Outcome);
        Assert.StartsWith("1 of 20 read; not yet: B, X, Y, LB, RB, LT, RT, ", check.Verdict().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_element_pressed_is_the_one_whose_actions_are_named()
    {
        var check = new PadCheck();

        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 0f, 0f);
        check.Observe(PadButtons.A | PadButtons.Start, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.Equal(IndexOf("Start"), check.LastActive);

        check.Observe(PadButtons.A | PadButtons.Start, 0f, 0f, 0f, 0f, 0f, 0.9f);
        Assert.Equal(IndexOf("RT"), check.LastActive);

        // Still held: nothing new became active, so it stays.
        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 0f, 0.9f);
        Assert.Equal(IndexOf("RT"), check.LastActive);
    }

    [Fact]
    public void A_trigger_lights_from_a_quarter_and_counts_as_read_from_half_way()
    {
        var check = new PadCheck();
        int rt = IndexOf("RT");

        check.Observe(PadButtons.None, 0f, 0f, 0f, 0f, 0f, 0.3f);
        Assert.True(check.IsActive(rt));
        Assert.False(check.IsSeen(rt));
        Assert.Equal(0.3f, check.Value(rt), 3);

        check.Observe(PadButtons.None, 0f, 0f, 0f, 0f, 0f, 0.5f);
        Assert.True(check.IsSeen(rt));

        check.Observe(PadButtons.None, 0f, 0f, 0f, 0f, 0f, 0.1f);
        Assert.False(check.IsActive(rt));
        Assert.True(check.IsSeen(rt));
    }

    [Fact]
    public void A_stick_axis_counts_as_read_only_once_it_went_both_ways()
    {
        var check = new PadCheck();
        int leftY = IndexOf("Left stick Y");

        check.Observe(PadButtons.None, 0f, 0.8f, 0f, 0f, 0f, 0f);
        Assert.True(check.IsActive(leftY));
        Assert.True(check.SeenPositive(leftY));
        Assert.False(check.SeenNegative(leftY));
        Assert.False(check.IsSeen(leftY));
        Assert.True(check.AnyRead);
        Assert.Contains("Left stick Y down", check.NotYet());

        check.Observe(PadButtons.None, 0f, -0.6f, 0f, 0f, 0f, 0f);
        Assert.True(check.IsSeen(leftY));
        Assert.DoesNotContain(check.NotYet(), n => n.StartsWith("Left stick Y", StringComparison.Ordinal));

        var other = new PadCheck();
        other.Observe(PadButtons.None, -0.9f, 0f, 0f, 0f, 0f, 0f);
        Assert.Contains("Left stick X right", other.NotYet());
    }

    [Fact]
    public void Everything_read_is_the_all_clear()
    {
        var check = new PadCheck();

        ReadEverything(check);

        PadCheckVerdict verdict = check.Verdict();
        Assert.Equal(PadCheckOutcome.Complete, verdict.Outcome);
        Assert.Equal("All 20 read: this controller sends everything a standard pad has.", verdict.Text);
        Assert.Equal(20, check.SeenCount);
        Assert.Empty(check.NotYet());
    }

    [Fact]
    public void Elements_the_template_lacks_are_never_read_and_the_verdict_says_they_never_reach_the_host()
    {
        int missing = PadCheck.MissingMask(PadButtons.Back, 1 << (int)PadAxis.RightTrigger);
        var check = new PadCheck(missing);
        int rt = IndexOf("RT");
        int back = IndexOf("Back");

        Assert.Equal((1 << rt) | (1 << back), missing);
        Assert.Equal(2, check.MissingCount);
        Assert.Equal(18, check.Readable);
        Assert.Equal(new[] { "RT", "Back" }, check.Lacks());

        ReadEverything(check);

        Assert.True(check.IsMissing(rt));
        Assert.False(check.IsSeen(rt));
        Assert.False(check.IsActive(rt));
        Assert.Equal(0f, check.Value(rt));
        Assert.Empty(check.NotYet());
        PadCheckVerdict verdict = check.Verdict();
        Assert.Equal(PadCheckOutcome.Lacking, verdict.Outcome);
        Assert.Equal("All 18 it has read. Its gamepad template lacks RT, Back: online those never reach the host.", verdict.Text);
    }

    [Fact]
    public void A_partial_check_of_a_lacking_pad_counts_what_it_has()
    {
        var check = new PadCheck(PadCheck.MissingMask(PadButtons.None, 1 << (int)PadAxis.RightTrigger));

        check.Observe(PadButtons.A, 0f, 0f, 0f, 0f, 0f, 0f);

        PadCheckVerdict verdict = check.Verdict();
        Assert.Equal(PadCheckOutcome.Partial, verdict.Outcome);
        Assert.StartsWith("1 of the 19 it has read; not yet: B, ", verdict.Text, StringComparison.Ordinal);
        Assert.EndsWith("Its gamepad template lacks RT: online that never reaches the host.", verdict.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RT,", verdict.Text[..verdict.Text.IndexOf("Its", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_moves_only_when_what_was_read_or_a_peak_changes()
    {
        var check = new PadCheck();
        int before = check.Version;

        check.Observe(PadButtons.None, 0.04f, 0f, 0f, 0f, 0.03f, 0f); // under one 0.05 step of a peak
        Assert.Equal(before, check.Version);

        check.Observe(PadButtons.B, 0f, 0f, 0f, 0f, 0f, 0f);
        int afterB = check.Version;
        Assert.NotEqual(before, afterB);

        check.Observe(PadButtons.B, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.Equal(afterB, check.Version);

        check.Observe(PadButtons.None, 0f, 0f, 0.7f, 0f, 0f, 0f);
        Assert.NotEqual(afterB, check.Version);
    }

    [Fact]
    public void Reset_forgets_what_was_read_but_not_what_the_pad_lacks()
    {
        var check = new PadCheck(PadCheck.MissingMask(PadButtons.Start, 0));
        ReadEverything(check);
        int version = check.Version;

        check.Reset();

        Assert.False(check.AnyRead);
        Assert.Equal(0, check.SeenCount);
        Assert.Equal(-1, check.LastActive);
        Assert.Equal(1, check.MissingCount);
        Assert.NotEqual(version, check.Version);
        Assert.Equal(PadCheckOutcome.NothingYet, check.Verdict().Outcome);
    }

    [Fact]
    public void Odd_axis_values_read_as_centred_or_clamped()
    {
        var check = new PadCheck();

        check.Observe(PadButtons.None, float.NaN, float.PositiveInfinity, 3f, -7f, float.NegativeInfinity, 0f);

        Assert.Equal(0f, check.Value(IndexOf("Left stick X")));
        Assert.Equal(0f, check.Value(IndexOf("Left stick Y")));
        Assert.Equal(1f, check.Value(IndexOf("Right stick X")));
        Assert.Equal(-1f, check.Value(IndexOf("Right stick Y")));
        Assert.Equal(0f, check.Value(IndexOf("LT")));
        Assert.Equal(0f, check.Value(-1));
        Assert.Equal(0f, check.Value(20));
        Assert.False(check.IsActive(25));
    }

    [Fact]
    public void The_log_line_names_the_pad_its_coverage_what_was_read_and_never_and_its_maps()
    {
        var check = new PadCheck(PadCheck.MissingMask(PadButtons.None, 1 << (int)PadAxis.RightTrigger));
        check.Observe(PadButtons.A | PadButtons.B, 0f, 0.9f, 0f, 0f, 0.7f, 0f);

        string line = check.Summary(Steam with { Maps = PadCheckMaps.Own, Differences = 12 }, "tab closed", TimeSpan.FromSeconds(42.7));

        Assert.StartsWith("Controller check (tab closed, 42 s): \"Steam Controller\" (hardware \"Steam Controller\"; this game sees a generic pad; " +
            "a host reads it as a generic pad; what you'd play with online); gamepad template: 19 of 20, lacks RT (online never pressed); " +
            "read 3 of 19: A, B, LT; never: X, ", line, StringComparison.Ordinal);
        Assert.Contains("Left stick Y down", line, StringComparison.Ordinal);
        Assert.EndsWith("; maps: its own, 12 bindings differ from the game's defaults (a host binds it from them).", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_line_of_a_full_check_on_the_defaults()
    {
        var check = new PadCheck();
        ReadEverything(check);

        string line = check.Summary(Steam, "panel closed", TimeSpan.Zero);

        Assert.Contains("gamepad template: all 20 elements; read 20 of 20: A, B, X, Y, LB, RB, LT, RT, Back, Start, ", line, StringComparison.Ordinal);
        Assert.Contains("; nothing missed; maps: the game's defaults (a host binds it from the game's gamepad maps).", line, StringComparison.Ordinal);
        Assert.Contains("(panel closed, 0 s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_line_of_nothing_read_and_of_no_pad()
    {
        string line = new PadCheck().Summary(Steam with { Maps = PadCheckMaps.Unknown, Differences = 1 }, "reset", TimeSpan.FromSeconds(-3));

        Assert.Contains("(reset, 0 s)", line, StringComparison.Ordinal);
        Assert.Contains("; read nothing; never: A, B, ", line, StringComparison.Ordinal);
        Assert.EndsWith("; maps: couldn't be read.", line, StringComparison.Ordinal);
        Assert.Equal("Controller check (tab closed): no gamepad connected.", PadCheck.NoPadSummary("tab closed", "no gamepad connected"));
        Assert.EndsWith("its own, 1 binding differs from the game's defaults (a host binds it from them).",
            new PadCheck().Summary(Steam with { Maps = PadCheckMaps.Own, Differences = 1 }, "x", TimeSpan.Zero), StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_line_needs_a_pad()
    {
        Assert.Throws<ArgumentNullException>(() => new PadCheck().Summary(null!, "x", TimeSpan.Zero));
    }
}
