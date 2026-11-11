using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TourGateTests
{
    public static IEnumerable<object[]> EveryStep() =>
        UiCheckPlan.Tours.SelectMany(t => t.Steps.Select(s => new object[] { t.Name, s.Id })).Distinct(new RowById());

    [Theory]
    [MemberData(nameof(EveryStep))]
    public void Every_step_of_every_tour_declares_where_it_starts(string tour, string stepId)
    {
        Assert.True(TourStarts.Of(stepId) != TourPlace.None, $"{tour}'s step {stepId} declares no start: it would never run");
    }

    [Fact]
    public void Every_declared_step_belongs_to_a_tour()
    {
        var ids = new HashSet<string>(UiCheckPlan.Tours.SelectMany(t => t.Steps).Concat(CampaignTour.StepsWithKeyboardHost).Concat(UiCheckPlan.KeyedPlans.SelectMany(p => p)).Concat(CampaignTour.KeyedSteps).Select(s => s.Id), StringComparer.Ordinal); // and the steps keys add
        foreach (string id in TourStarts.All.Keys)
        {
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public void The_finish_runs_anywhere_the_tour_knows_but_never_on_a_screen_it_never_opens()
    {
        Assert.Equal(TourPlace.Any, TourStarts.Of("finish"));
        Assert.Equal(0, (int)(TourPlace.Any & (TourPlace.OtherScreen | TourPlace.Unknown)));
    }

    [Fact]
    public void The_steps_that_walk_home_must_end_at_the_titles_main()
    {
        foreach (string id in new[] { "pw_settings", "pw_online_settings", "pw_backups", "pw_report", "kb_settings", "kb_lobby", "pw_quick", "pw_close", "op_close", "cp_close" })
        {
            Assert.True(TourStarts.EndsAtMain(id), id);
        }

        Assert.False(TourStarts.EndsAtMain("pw_moves"));
        Assert.False(TourStarts.EndsAtMain("finish"));
        Assert.False(TourStarts.EndsAtMain("nothing_declared"));
    }

    [Theory]
    [InlineData("pw_quick", TourPlace.Main, TourGateKind.Run)]
    [InlineData("pw_quick", TourPlace.TourScreen, TourGateKind.CantStart)]
    [InlineData("pw_moves", TourPlace.Lobby, TourGateKind.Run)]
    [InlineData("pw_moves", TourPlace.LobbyCovered, TourGateKind.CantStart)]
    [InlineData("pw_settings", TourPlace.TourScreen, TourGateKind.Run)]
    [InlineData("pw_settings", TourPlace.Group, TourGateKind.Run)]
    [InlineData("pw_settings", TourPlace.Map, TourGateKind.CantStart)]
    [InlineData("cp_first", TourPlace.Map, TourGateKind.Run)]
    [InlineData("cp_first", TourPlace.Main, TourGateKind.CantStart)]
    [InlineData("cp_end", TourPlace.Lobby, TourGateKind.Run)]
    [InlineData("rp_record", TourPlace.Match, TourGateKind.Run)]
    [InlineData("rp_record", TourPlace.Loading, TourGateKind.CantStart)]
    [InlineData("title", TourPlace.PressStart, TourGateKind.Run)]
    [InlineData("finish", TourPlace.Match, TourGateKind.Run)]
    public void A_step_runs_only_where_it_declares(string stepId, TourPlace now, TourGateKind wanted)
    {
        var gate = new TourGate();
        Assert.Equal(wanted, gate.Before(stepId, now, null).Kind);
    }

    [Theory]
    [InlineData("pw_start")]
    [InlineData("kb_start")]
    [InlineData("op_host")]
    [InlineData("cp_host")]
    [InlineData("rp_playnow")]
    public void A_first_step_that_presses_on_mains_rows_never_starts_in_another_of_the_titles_groups(string stepId)
    {
        // Another of the title's groups is the game's own (Campaign, Play, Practice, Customize): A there is on the player's rows.
        Assert.Equal(TourGateKind.Run, new TourGate().Before(stepId, TourPlace.PressStart, null).Kind);
        Assert.Equal(TourGateKind.Run, new TourGate().Before(stepId, TourPlace.Main, null).Kind);
        Assert.Equal(TourGateKind.CantStart, new TourGate().Before(stepId, TourPlace.Group, "Campaign").Kind);
        Assert.Equal(TourGateKind.CantStart, new TourGate().Before(stepId, TourPlace.TourScreen, "Settings").Kind);
    }

    [Fact]
    public void A_step_that_cant_start_says_where_it_wanted_and_where_the_game_is_and_presses_nothing()
    {
        var gate = new TourGate();
        TourGateVerdict verdict = gate.Before("pw_quick", TourPlace.TourScreen, "Settings");
        Assert.Equal(TourGateKind.CantStart, verdict.Kind);
        Assert.Equal("can't start: wanted the title's Main, the game at a screen the tours open over the title (Settings); nothing was pressed", verdict.Why);
        Assert.False(gate.Stopped);
    }

    [Fact]
    public void Two_steps_in_a_row_that_cant_start_stop_the_tour()
    {
        var gate = new TourGate();
        Assert.Equal(TourGateKind.CantStart, gate.Before("pw_notice", TourPlace.LobbyCovered, "CustomizableDismissablePrompt").Kind);
        TourGateVerdict second = gate.Before("pw_close", TourPlace.LobbyCovered, "CustomizableDismissablePrompt");
        Assert.Equal(TourGateKind.Stop, second.Kind);
        Assert.True(gate.Stopped);
        Assert.Equal(("before", "pw_close"), gate.StoppedAt);
        Assert.StartsWith("2 steps in a row can't start (pw_notice: wanted the lobby, the game at a game screen over the lobby (CustomizableDismissablePrompt); pw_close: ", second.Why, StringComparison.Ordinal);

        // Stopped stays stopped, whatever comes.
        Assert.Equal(TourGateKind.Stop, gate.Before("pw_mash", TourPlace.Main, null).Kind);
    }

    [Fact]
    public void A_step_that_runs_between_two_that_cant_start_resets_the_count()
    {
        var gate = new TourGate();
        Assert.Equal(TourGateKind.CantStart, gate.Before("pw_notice", TourPlace.LobbyCovered, null).Kind);
        Assert.Equal(TourGateKind.Run, gate.Before("pw_close", TourPlace.Lobby, null).Kind);
        Assert.Equal(TourGateKind.CantStart, gate.Before("pw_mash", TourPlace.Group, null).Kind);
        Assert.False(gate.Stopped);
    }

    [Theory]
    [InlineData(TourPlace.OtherScreen)]
    [InlineData(TourPlace.Unknown)]
    public void A_screen_the_tours_never_open_or_cant_read_stops_the_tour_at_once(TourPlace now)
    {
        var gate = new TourGate();
        TourGateVerdict verdict = gate.Before("finish", now, "ChooseMetaMenu");
        Assert.Equal(TourGateKind.Stop, verdict.Kind);
        Assert.Contains("a screen the tours never open or can't read", verdict.Why, StringComparison.Ordinal);

        var after = new TourGate();
        Assert.NotNull(after.After("pw_moves", now, null));
        Assert.Equal(("after", "pw_moves"), after.StoppedAt);
    }

    [Fact]
    public void A_step_that_must_end_at_main_and_doesnt_stops_the_tour()
    {
        var gate = new TourGate();
        Assert.Null(gate.After("pw_report", TourPlace.Main, null));
        Assert.False(gate.Stopped);
        string? why = gate.After("pw_report", TourPlace.TourScreen, "Settings");
        Assert.Equal("it ended away from the title's Main (the game at a screen the tours open over the title (Settings))", why);
        Assert.Equal(("after", "pw_report"), gate.StoppedAt);
    }

    [Fact]
    public void A_step_that_may_end_anywhere_it_knows_goes_on()
    {
        var gate = new TourGate();
        Assert.Null(gate.After("pw_host", TourPlace.Lobby, null));
        Assert.Null(gate.After("cp_first", TourPlace.Map, null));
        Assert.False(gate.Stopped);
    }

    [Fact]
    public void An_undeclared_step_never_starts()
    {
        var gate = new TourGate();
        TourGateVerdict verdict = gate.Before("not_a_step", TourPlace.Main, null);
        Assert.Equal(TourGateKind.CantStart, verdict.Kind);
        Assert.Contains("declares no start", verdict.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_starts_a_new_tour()
    {
        var gate = new TourGate();
        _ = gate.Before("pw_moves", TourPlace.OtherScreen, null);
        Assert.True(gate.Stopped);
        gate.Reset();
        Assert.False(gate.Stopped);
        Assert.Equal(TourGateKind.Run, gate.Before("pw_moves", TourPlace.Lobby, null).Kind);
    }

    [Fact]
    public void The_stopped_line_is_exact()
    {
        Assert.Equal(
            "STOPPED: after pw_report: it ended away from the title's Main (the game at a screen the tours open over the title (Settings)); not run: pw_quick, finish; the game quits with every hold on.",
            TourGate.StoppedLine("after", "pw_report", "it ended away from the title's Main (the game at a screen the tours open over the title (Settings))", new[] { "pw_quick", "finish" }));
        Assert.Equal("STOPPED: before finish: x; not run: none; the game quits with every hold on.", TourGate.StoppedLine("before", "finish", "x", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Settings", true)]
    [InlineData("CustomizableYesNoPrompt", true)]
    [InlineData("CustomizableDismissablePrompt", true)]
    [InlineData("Glossary", true)]
    [InlineData("ControllerAssignment", true)]
    [InlineData("ChooseMetaMenu", false)]
    [InlineData("OldSquadMenu", false)]
    [InlineData("RunEnd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_tours_own_screens_over_the_title_are_known_by_name(string? menuId, bool tours)
    {
        Assert.Equal(tours, TourStarts.IsTourScreen(menuId));
    }

    [Fact]
    public void Transitions_are_known_by_name()
    {
        Assert.True(TourStarts.IsTransition("WooshLoadingMenu"));
        Assert.True(TourStarts.IsTransition("WooshProgressMenu"));
        Assert.False(TourStarts.IsTransition("Settings"));
        Assert.False(TourStarts.IsTransition(null));
    }

    [Fact]
    public void Places_read_in_words()
    {
        Assert.Equal("the title's Main", TourStarts.Describe(TourPlace.Main));
        Assert.Equal("the lobby or the world map", TourStarts.Describe(TourPlace.Map | TourPlace.Lobby));
        Assert.Equal("the title or a screen the tours open over it", TourStarts.Describe(TourPlace.Home));
        Assert.Equal("nowhere", TourStarts.Describe(TourPlace.None));
    }

    private sealed class RowById : IEqualityComparer<object[]>
    {
        public bool Equals(object[]? x, object[]? y) => string.Equals(x?[1] as string, y?[1] as string, StringComparison.Ordinal);

        public int GetHashCode(object[] obj) => StringComparer.Ordinal.GetHashCode((string)obj[1]);
    }
}
