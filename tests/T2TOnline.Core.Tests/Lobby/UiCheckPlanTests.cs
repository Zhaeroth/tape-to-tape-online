using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class UiCheckPlanTests
{
    [Fact]
    public void Stage_ones_plan_has_no_problem()
    {
        Assert.Null(UiCheckPlan.Problem(UiCheckPlan.StageOne));
    }

    [Fact]
    public void The_self_driving_tours_plan_has_no_problem()
    {
        Assert.Null(UiCheckPlan.Problem(UiCheckPlan.SelfDrive));
    }

    [Fact]
    public void The_pad_walks_plan_has_no_problem()
    {
        Assert.Null(UiCheckPlan.Problem(UiCheckPlan.PadWalk));
    }

    [Fact]
    public void The_one_pc_tours_plan_has_no_problem()
    {
        Assert.Null(UiCheckPlan.Problem(UiCheckPlan.OnePc));
    }

    [Theory]
    [InlineData("padwalk")]
    [InlineData(" padwalk ")]
    public void The_first_line_of_run_txt_picks_the_pad_walk(string line)
    {
        Assert.Same(UiCheckPlan.PadWalk, Tour(line));
    }

    [Theory]
    [InlineData("onepc")]
    [InlineData(" onepc ")]
    public void The_first_line_of_run_txt_picks_the_one_pc_tour(string line)
    {
        Assert.Same(UiCheckPlan.OnePc, Tour(line));
    }

    [Fact]
    public void The_keyboard_walks_plan_has_no_problem()
    {
        Assert.Null(UiCheckPlan.Problem(UiCheckPlan.KbWalk));
    }

    [Theory]
    [InlineData("kbwalk")]
    [InlineData(" KbWalk ")]
    public void The_first_line_of_run_txt_picks_the_keyboard_walk(string line)
    {
        Assert.Same(UiCheckPlan.KbWalk, Tour(line));
    }

    [Fact]
    public void The_spike_is_folded_into_the_walks_and_screens()
    {
        Assert.False(UiCheckRequest.TryParse("spike", UiCheckPlan.Tours, out _, out string problem));
        Assert.StartsWith("no tour is called", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(UiCheckPlan.Tours.SelectMany(t => t.Steps), s => s.Id.StartsWith("st_", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pad_walk_goes_in_the_order_a_person_would_and_ends_at_the_title()
    {
        string[] ids = UiCheckPlan.PadWalk.Select(s => s.Id).ToArray();
        Assert.Equal(new[]
        {
            "pw_start", "pw_group", "pw_join", "pw_host", "pw_moves", "pw_tabs", "pw_selectors", "pw_invite", "pw_hold", "pw_count", "pw_backout", "pw_newrun",
            "pw_lobby_settings", "pw_notice", "pw_close", "pw_mash", "pw_settings", "pw_online_settings", "pw_backups", "pw_report", "pw_quick", "finish",
        }, ids);
    }

    [Fact]
    public void The_pad_walks_settings_screens_come_with_no_session_up_and_before_the_match()
    {
        // The title's own Settings, the Backups screen (usable only at the title out of a session) and the reports: after the lobby closed, before the
        // match's pause menu checks the same tab locked.
        string[] ids = UiCheckPlan.PadWalk.Select(s => s.Id).ToArray();
        int closed = Array.IndexOf(ids, "pw_close");
        int quick = Array.IndexOf(ids, "pw_quick");
        Assert.All(new[] { "pw_settings", "pw_online_settings", "pw_backups", "pw_report" }, id =>
        {
            int at = Array.IndexOf(ids, id);
            Assert.True(at > closed && at < quick, id);
        });
        Assert.True(Array.IndexOf(ids, "pw_lobby_settings") < closed);
    }

    [Fact]
    public void The_keyboard_walk_joins_first_and_walks_each_new_screen_then_the_lobby()
    {
        string[] ids = UiCheckPlan.KbWalk.Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "kb_start", "kb_settings", "kb_online_settings", "kb_backups", "kb_report", "kb_lobby", "finish" }, ids);
        Assert.Equal(TimeSpan.FromMinutes(6), UiCheckPlan.Tours.Single(t => t.Name == "kbwalk").Limit);
    }

    [Fact]
    public void No_tour_with_real_input_changes_the_language_or_the_window()
    {
        Assert.All(UiCheckPlan.PadWalk.Concat(UiCheckPlan.OnePc).Concat(UiCheckPlan.KbWalk).Concat(OfflineStart.Steps),
            s => Assert.True(s.Kind is UiCheckStepKind.Plain or UiCheckStepKind.Finish, s.Id));
    }

    [Theory]
    [InlineData("screens")]
    [InlineData("screens en")]
    [InlineData("screens quick")]
    [InlineData("all")]
    public void Every_screens_tour_pictures_the_new_screens_before_any_language_or_window_change(string name)
    {
        IReadOnlyList<UiCheckStep> steps = Tour(name);
        List<string> ids = steps.Select(s => s.Id).ToList();
        int firstChange = steps.ToList().FindIndex(s => s.Kind is UiCheckStepKind.Language or UiCheckStepKind.Window);
        foreach (string id in new[] { "scr_online_group", "scr_settings_online", "scr_report", "scr_backups" })
        {
            int at = ids.IndexOf(id);
            Assert.True(at > ids.IndexOf("scr_group"), $"{name}: {id}");
            Assert.True(firstChange < 0 || at < firstChange, $"{name}: {id}");
        }
    }

    [Fact]
    public void The_offline_walk_comes_after_the_state_it_reads()
    {
        Assert.Equal("off_state", OfflineStart.Steps[0].Id);
        Assert.All(OfflineStart.Steps.Skip(1), s => Assert.True(s.Id.StartsWith("off_", StringComparison.Ordinal) || s.Kind == UiCheckStepKind.Finish, s.Id));
    }

    [Theory]
    [InlineData("selfdrive", true)]
    [InlineData(" selfdrive ", true)]
    [InlineData("SelfDrive", true)]
    [InlineData("all", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_first_line_of_run_txt_picks_the_tour(string? line, bool selfDrive)
    {
        Assert.Same(selfDrive ? UiCheckPlan.SelfDrive : UiCheckPlan.All, Tour(line));
    }

    [Theory]
    [InlineData("anything else")]
    [InlineData("anything")]
    [InlineData("screens fr")]
    public void A_tour_the_plan_doesnt_declare_is_refused(string line)
    {
        Assert.False(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem));
        Assert.Null(request);
        Assert.StartsWith("no tour is called", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_one_covers_its_steps_in_their_order()
    {
        string[] ids = UiCheckPlan.StageOne.Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "title", "mainwalk", "group", "selection", "back", "shell", "tenopens", "widgets", "handover", "unload", "languages", "sizes", "finish" }, ids);
    }

    [Fact]
    public void Real_menus_that_write_run_before_any_language_or_window_change()
    {
        List<UiCheckStep> steps = UiCheckPlan.StageOne.ToList();
        int firstChange = steps.FindIndex(s => s.Kind is UiCheckStepKind.Language or UiCheckStepKind.Window);
        Assert.True(firstChange > 0);
        Assert.All(steps.Skip(firstChange), s => Assert.False(s.Needs.HasFlag(UiCheckNeeds.PlayersSettings), s.Id));
    }

    [Fact]
    public void A_step_needing_the_players_settings_after_a_change_is_refused()
    {
        var steps = new[]
        {
            new UiCheckStep("a", "a", UiCheckStepKind.Window),
            new UiCheckStep("b", "b", Needs: UiCheckNeeds.PlayersSettings),
            new UiCheckStep("z", "z", UiCheckStepKind.Finish),
        };
        Assert.Contains("\"b\"", UiCheckPlan.Problem(steps), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_must_end_by_restoring()
    {
        Assert.NotNull(UiCheckPlan.Problem(new[] { new UiCheckStep("a", "a") }));
        Assert.NotNull(UiCheckPlan.Problem(new[] { new UiCheckStep("z", "z", UiCheckStepKind.Finish), new UiCheckStep("a", "a") }));
        Assert.NotNull(UiCheckPlan.Problem(Array.Empty<UiCheckStep>()));
    }

    [Fact]
    public void Ids_are_unique_and_can_name_files()
    {
        Assert.NotNull(UiCheckPlan.Problem(new[] { new UiCheckStep("a", "a"), new UiCheckStep("a", "b"), new UiCheckStep("z", "z", UiCheckStepKind.Finish) }));
        Assert.NotNull(UiCheckPlan.Problem(new[] { new UiCheckStep("a/b", "a"), new UiCheckStep("z", "z", UiCheckStepKind.Finish) }));
        Assert.NotNull(UiCheckPlan.Problem(new[] { new UiCheckStep(string.Empty, "a"), new UiCheckStep("z", "z", UiCheckStepKind.Finish) }));
    }

    [Fact]
    public void The_tour_has_its_languages_sizes_and_default_limit()
    {
        Assert.Equal(new[] { "German", "Chinese", "Japanese", "Russian" }, UiCheckPlan.Languages);
        Assert.Equal(new[] { (16, 9), (16, 10), (21, 9), (4, 3) }, UiCheckPlan.Aspects);
        Assert.Equal(TimeSpan.FromMinutes(8), UiCheckPlan.DefaultLimit);
    }

    [Theory]
    [InlineData("screens", "scr_langs", true)]
    [InlineData("screens en", "scr_langs", false)]
    [InlineData("all", "tenopens", true)]
    [InlineData(null, "tenopens", true)]
    [InlineData("screens quick", "scr_sizes", false)]
    [InlineData("screens", "scr_sizes", true)]
    public void A_tour_is_picked_by_name(string? name, string step, bool has)
    {
        IReadOnlyList<UiCheckStep> tour = Tour(name);
        Assert.Equal(has, tour.Any(s => s.Id == step));
        Assert.Null(UiCheckPlan.Problem(tour));
    }

    [Fact]
    public void Window_sizes_keep_each_shape_at_the_displays_own_width_and_never_exceed_it()
    {
        Assert.Equal(new[] { (1024, 576), (1024, 640), (1024, 438), (1024, 768) }, UiCheckPlan.SizesFor(1024, 768));
        Assert.Equal(new[] { (1920, 1080), (1728, 1080), (2520, 1080), (1440, 1080) }, UiCheckPlan.SizesFor(2560, 1080));
    }

    /// <summary>The steps run.txt's first line asks for, as the tour reads it.</summary>
    private static IReadOnlyList<UiCheckStep> Tour(string? line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        return request!.Tour.Steps;
    }
}
