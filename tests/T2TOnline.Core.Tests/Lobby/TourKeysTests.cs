using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TourKeysTests
{
    private static readonly string[] PadWalkIds =
    {
        "pw_start", "pw_group", "pw_join", "pw_host", "pw_moves", "pw_tabs", "pw_selectors", "pw_invite", "pw_hold", "pw_count", "pw_backout", "pw_newrun",
        "pw_lobby_settings", "pw_notice", "pw_close", "pw_mash", "pw_settings", "pw_online_settings", "pw_backups", "pw_report", "pw_quick", "finish",
    };

    private static readonly string[] SelfDriveIds =
    {
        "sd_period", "sd_browser", "sd_host", "sd_start", "sd_match", "sd_return", "sd_backout", "sd_cancel", "sd_close", "sd_fail", "sd_report", "finish",
    };

    private static readonly string[] OnePcIds =
    {
        "op_host", "op_arrive", "op_board", "op_close_prompt", "op_start", "op_match", "op_drop", "op_failload", "op_kick", "op_close", "finish",
    };

    [Theory]
    [InlineData("padwalk")]
    [InlineData("padwalk endonline=off")]
    public void Without_endonline_on_the_pad_walk_runs_its_steps_as_before(string line)
    {
        IReadOnlyList<UiCheckStep> plan = Plan(line);
        Assert.Same(UiCheckPlan.PadWalk, plan);
        Assert.Equal(PadWalkIds, plan.Select(s => s.Id).ToArray());
        Assert.Null(UiCheckPlan.PlanLine(plan));
    }

    [Fact]
    public void With_endonline_on_the_end_and_the_titles_new_run_come_last_before_the_finish()
    {
        IReadOnlyList<UiCheckStep> plan = Plan("padwalk endonline=on");
        Assert.Same(UiCheckPlan.PadWalkWithEndOnline, plan);
        Assert.Equal(PadWalkIds[..^1].Concat(new[] { "pw_end_online", "pw_title_newrun", "finish" }).ToArray(), plan.Select(s => s.Id).ToArray());
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal("padwalk: the plan has 24 steps, pw_end_online and pw_title_newrun after pw_quick (endonline=on)", UiCheckPlan.PlanLine(plan));
    }

    [Theory]
    [InlineData("selfdrive")]
    [InlineData("selfdrive team=last")]
    public void Without_team_custom_the_self_driving_tour_runs_its_steps_as_before(string line)
    {
        IReadOnlyList<UiCheckStep> plan = Plan(line);
        Assert.Same(UiCheckPlan.SelfDrive, plan);
        Assert.Equal(SelfDriveIds, plan.Select(s => s.Id).ToArray());
        Assert.Null(UiCheckPlan.PlanLine(plan));
    }

    [Fact]
    public void With_team_custom_the_team_is_read_before_the_start_and_the_log_after_the_return()
    {
        IReadOnlyList<UiCheckStep> plan = Plan("selfdrive team=custom");
        Assert.Same(UiCheckPlan.SelfDriveWithCustomTeam, plan);
        Assert.Equal(new[]
        {
            "sd_period", "sd_browser", "sd_host", "sd_custom_team", "sd_start", "sd_match", "sd_return", "sd_custom_names", "sd_backout", "sd_cancel", "sd_close", "sd_fail", "sd_report", "finish",
        }, plan.Select(s => s.Id).ToArray());
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal("selfdrive: the plan has 14 steps, sd_custom_team before sd_start and sd_custom_names after sd_return (team=custom)", UiCheckPlan.PlanLine(plan));
    }

    [Theory]
    [InlineData("onepc")]
    [InlineData("onepc dc=off")]
    public void Without_dc_on_the_one_pc_tour_runs_its_steps_as_before(string line)
    {
        IReadOnlyList<UiCheckStep> plan = Plan(line);
        Assert.Same(UiCheckPlan.OnePc, plan);
        Assert.Equal(OnePcIds, plan.Select(s => s.Id).ToArray());
        Assert.Null(UiCheckPlan.PlanLine(plan));
    }

    [Fact]
    public void With_dc_on_direct_connections_is_pressed_with_the_friend_in_before_the_close_prompt()
    {
        IReadOnlyList<UiCheckStep> plan = Plan("onepc dc=on");
        Assert.Same(UiCheckPlan.OnePcWithDirectConnections, plan);
        string[] ids = plan.Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "op_host", "op_arrive", "op_board", "op_direct", "op_close_prompt", "op_start", "op_match", "op_drop", "op_failload", "op_kick", "op_close", "finish" }, ids);
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal("onepc: the plan has 12 steps, op_direct after op_board (dc=on)", UiCheckPlan.PlanLine(plan));
    }

    [Theory]
    [InlineData("padwalk endonline=yes", "\"endonline\" takes on or off, not \"yes\"")]
    [InlineData("padwalk dc=on", "the tour \"padwalk\" takes no key \"dc\" (it takes endonline)")]
    [InlineData("selfdrive team=game", "\"team\" takes custom or last, not \"game\"")]
    [InlineData("selfdrive endonline=on", "the tour \"selfdrive\" takes no key \"endonline\" (it takes team)")]
    [InlineData("onepc dc=yes", "\"dc\" takes on or off, not \"yes\"")]
    [InlineData("onepc team=custom", "the tour \"onepc\" takes no key \"team\" (it takes dc)")]
    [InlineData("kbwalk endonline=on", "the tour \"kbwalk\" takes no arguments (endonline)")]
    [InlineData("screens dc=on", "the tour \"screens\" takes no arguments (dc)")]
    [InlineData("campaign act1 team=custom", "the tour \"campaign act1\" takes no key \"team\"")]
    public void A_key_a_tour_doesnt_declare_or_a_value_it_doesnt_allow_is_refused_before_anything_runs(string line, string why)
    {
        Assert.False(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem));
        Assert.Null(request);
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_step_a_key_adds_declares_its_start_and_the_end_and_new_run_walk_home()
    {
        foreach (UiCheckStep step in UiCheckPlan.KeyedPlans.SelectMany(p => p))
        {
            Assert.True(TourStarts.Of(step.Id) != TourPlace.None, step.Id);
        }

        Assert.Equal(TourPlace.Main, TourStarts.Of(UiCheckPlan.EndOnlineStep));
        Assert.True(TourStarts.EndsAtMain(UiCheckPlan.EndOnlineStep));
        Assert.Equal(TourPlace.Main, TourStarts.Of(UiCheckPlan.TitleNewRunStep));
        Assert.True(TourStarts.EndsAtMain(UiCheckPlan.TitleNewRunStep));
        Assert.Equal(TourPlace.Lobby, TourStarts.Of(UiCheckPlan.CustomTeamStep));
        Assert.Equal(TourPlace.Lobby, TourStarts.Of(UiCheckPlan.CustomNamesStep));
        Assert.Equal(TourPlace.Lobby, TourStarts.Of(UiCheckPlan.DirectConnectionsStep));
        Assert.False(TourStarts.EndsAtMain(UiCheckPlan.DirectConnectionsStep));
    }

    [Fact]
    public void The_titles_new_run_is_always_the_last_step_before_the_finish()
    {
        // It acts on the player's own solo run's title buttons: nothing presses after it but the finish.
        IReadOnlyList<UiCheckStep> plan = UiCheckPlan.PadWalkWithEndOnline;
        Assert.Equal(UiCheckPlan.TitleNewRunStep, plan[^2].Id);
        Assert.Equal(UiCheckPlan.EndOnlineStep, plan[^3].Id);
    }

    [Fact]
    public void The_keyed_plans_are_the_tours_own_kind()
    {
        Assert.True(UiCheckPlan.IsWalkPlan(UiCheckPlan.PadWalk));
        Assert.True(UiCheckPlan.IsWalkPlan(UiCheckPlan.PadWalkWithEndOnline));
        Assert.True(UiCheckPlan.IsWalkPlan(UiCheckPlan.OnePc));
        Assert.True(UiCheckPlan.IsWalkPlan(UiCheckPlan.OnePcWithDirectConnections));
        Assert.True(UiCheckPlan.IsWalkPlan(UiCheckPlan.KbWalk));
        Assert.False(UiCheckPlan.IsWalkPlan(UiCheckPlan.SelfDrive));
        Assert.True(UiCheckPlan.IsSelfDrivePlan(UiCheckPlan.SelfDrive));
        Assert.True(UiCheckPlan.IsSelfDrivePlan(UiCheckPlan.SelfDriveWithCustomTeam));
        Assert.False(UiCheckPlan.IsSelfDrivePlan(UiCheckPlan.PadWalk));
        Assert.All(UiCheckPlan.KeyedPlans, p => Assert.Null(UiCheckPlan.Problem(p)));
    }

    [Fact]
    public void A_campaign_tour_still_gets_its_steps_from_its_own_rule()
    {
        Assert.True(UiCheckRequest.TryParse("campaign act1 kbhost=on", UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Same(CampaignTour.StepsFor(request!), UiCheckPlan.StepsFor(request!));
        Assert.True(UiCheckRequest.TryParse("screens", UiCheckPlan.Tours, out request, out problem), problem);
        Assert.Same(UiCheckPlan.Screens, UiCheckPlan.StepsFor(request!));
    }

    private static IReadOnlyList<UiCheckStep> Plan(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        return UiCheckPlan.StepsFor(request!);
    }
}
