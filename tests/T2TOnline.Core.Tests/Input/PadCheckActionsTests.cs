using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PadCheckActionsTests
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Shoot",
        [2] = "UI Submit",
        [3] = "Backwards Right",
        [4] = "UI Right Trigger",
        [5] = "Move Horizontal",
        [6] = "UI Horizontal",
        [7] = "Move Vertical",
        [8] = "UI Mouse Click",
        [9] = "Pass",
    };

    private static string Name(int id) => Names.TryGetValue(id, out string? name) ? name : $"action {id}";

    private static int IndexOf(string name) => PadCheck.Elements.Select((e, i) => (e, i)).Single(p => p.e.Name == name).i;

    [Fact]
    public void Each_element_lists_its_actions_once_in_the_bindings_order()
    {
        var bindings = new[]
        {
            new InputBinding(0, 1, BindingElement.PadButton(0)), // A: Shoot (Default)
            new InputBinding(2, 2, BindingElement.PadButton(0)), // A: UI Submit (UI)
            new InputBinding(4, 1, BindingElement.PadButton(0)), // A: Shoot again (another category)
            new InputBinding(0, 3, BindingElement.Axis(PadAxis.RightTrigger)),
            new InputBinding(2, 4, BindingElement.Axis(PadAxis.RightTrigger), Range: BindingRange.Positive),
        };

        string[] actions = PadCheckActions.ByElement(bindings, Name);

        Assert.Equal(PadCheck.ElementCount, actions.Length);
        Assert.Equal("Shoot, UI Submit", actions[IndexOf("A")]);
        Assert.Equal("Backwards Right, UI Right Trigger", actions[IndexOf("RT")]);
        Assert.Equal(string.Empty, actions[IndexOf("B")]);
        Assert.Equal(18, actions.Count(a => a.Length == 0));
    }

    [Fact]
    public void A_half_stick_says_which_way_and_a_button_on_an_actions_negative_side_says_so()
    {
        var bindings = new[]
        {
            new InputBinding(0, 5, BindingElement.Axis(PadAxis.LeftStickX)),
            new InputBinding(2, 6, BindingElement.Axis(PadAxis.LeftStickX), Range: BindingRange.Positive),
            new InputBinding(2, 6, BindingElement.Axis(PadAxis.LeftStickX), Negative: true, Range: BindingRange.Negative),
            new InputBinding(0, 7, BindingElement.PadButton(11), Negative: true), // D-pad down
            new InputBinding(0, 7, BindingElement.PadButton(10)), // D-pad up
            new InputBinding(0, 7, BindingElement.Axis(PadAxis.RightStickY), Range: BindingRange.Negative),
        };

        string[] actions = PadCheckActions.ByElement(bindings, Name);

        Assert.Equal("Move Horizontal, UI Horizontal (right), UI Horizontal (left)", actions[IndexOf("Left stick X")]);
        Assert.Equal("Move Vertical (-)", actions[IndexOf("D-pad down")]);
        Assert.Equal("Move Vertical", actions[IndexOf("D-pad up")]);
        Assert.Equal("Move Vertical (down)", actions[IndexOf("Right stick Y")]);
    }

    [Fact]
    public void Keys_and_skipped_actions_are_left_out()
    {
        var bindings = new[]
        {
            new InputBinding(0, 9, BindingElement.Key(32)),
            new InputBinding(0, 9, BindingElement.MouseButton(3)),
            new InputBinding(3, 8, BindingElement.Axis(PadAxis.RightTrigger)),
            new InputBinding(0, 3, BindingElement.Axis(PadAxis.RightTrigger)),
        };

        string[] actions = PadCheckActions.ByElement(bindings, Name, id => id == 8);

        Assert.Equal("Backwards Right", actions[IndexOf("RT")]);
        Assert.Equal(19, actions.Count(a => a.Length == 0));
        Assert.Throws<ArgumentNullException>(() => PadCheckActions.ByElement(null!, Name));
        Assert.Throws<ArgumentNullException>(() => PadCheckActions.ByElement(bindings, null!));
    }

    [Fact]
    public void The_detail_line_names_the_element_and_every_action_or_why_none()
    {
        int rt = IndexOf("RT");

        Assert.Equal("RT: Backwards Right, UI Right Trigger", PadCheckActions.Detail(rt, "Backwards Right, UI Right Trigger", missing: false));
        Assert.Equal("RT: no game action is bound to it in these maps.", PadCheckActions.Detail(rt, string.Empty, missing: false));
        Assert.Equal("RT: this pad's gamepad template lacks it, so it's never pressed online (it would give Backwards Right).",
            PadCheckActions.Detail(rt, "Backwards Right", missing: true));
        Assert.Equal("RT: this pad's gamepad template lacks it, so it's never pressed online.", PadCheckActions.Detail(rt, string.Empty, missing: true));
        Assert.StartsWith("element 40: ", PadCheckActions.Detail(40, "x", missing: false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_friends_coverage_is_what_came_against_what_its_maps_bind()
    {
        var given = new[] { "Move Horizontal", "Shoot", "Pause" };
        var bound = new[] { "Move Horizontal", "Shoot", "Backwards Right", "UI Right Trigger", "Shoot" };

        var coverage = ActionCoverage.Of(given, bound);

        Assert.Equal(given, coverage.Given);
        Assert.Equal(new[] { "Backwards Right", "UI Right Trigger" }, coverage.NotYet);
        Assert.Equal(4, coverage.Bound);
        Assert.Equal("2 of the 4 actions its maps bind given so far, and 1 other (the game has 62)", coverage.Line(62));
    }

    [Fact]
    public void A_friend_who_gave_nothing_or_everything()
    {
        Assert.Equal("0 of the 2 actions its maps bind given so far (the game has 62)", ActionCoverage.Of(Array.Empty<string>(), new[] { "A", "B" }).Line(62));
        var all = ActionCoverage.Of(new[] { "A", "B" }, new[] { "B", "A" });
        Assert.Empty(all.NotYet);
        Assert.Equal("2 of the 2 actions its maps bind given so far (the game has 2)", all.Line(2));
        Assert.Equal("0 of the 1 action its maps bind given so far (the game has 9)", ActionCoverage.Of(Array.Empty<string>(), new[] { "A" }).Line(9));
    }

    [Fact]
    public void Names_are_cut_with_how_many_more()
    {
        var names = new[] { "Move Horizontal", "Move Vertical", "Shoot", "Pass", "Backwards Right" };

        Assert.Equal("none", ActionCoverage.Names(Array.Empty<string>(), 100));
        Assert.Equal("Move Horizontal, Move Vertical, Shoot, Pass, Backwards Right", ActionCoverage.Names(names, 100));
        Assert.Equal("Move Horizontal, Move Vertical and 3 more", ActionCoverage.Names(names, 32));
        Assert.Equal("Move Horizontal and 4 more", ActionCoverage.Names(names, 3));
    }
}
