using System;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>
/// A friend's budget for looking up the host's event arguments (with 8 a second, a puck collision's hit
/// object is held back right after a goal, and the game's CollisionVFX throws).
/// </summary>
public class LookupBudgetTests
{
    [Fact]
    public void Scene_paths_get_their_own_larger_budget_a_second_and_a_cap_a_frame()
    {
        var budget = new LookupBudget();
        int taken = 0;
        for (int frame = 0; frame < 10; frame++)
        {
            for (int i = 0; i < 20; i++)
            {
                taken += budget.TryTake(LookupKind.ScenePath, 0.1 * frame / 10, frame, urgent: false) ? 1 : 0;
            }
        }

        // 16 a frame for four frames, then the second's 64 are spent.
        Assert.Equal(LookupBudget.ScenePathsPerSecond, taken);
        Assert.Equal(200 - 64, budget.ScenePathsHeldBack);
        Assert.Equal(0, budget.ScansHeldBack);
    }

    [Fact]
    public void A_frame_takes_at_most_its_cap_of_scene_paths()
    {
        var budget = new LookupBudget();
        int taken = 0;
        for (int i = 0; i < 40; i++)
        {
            taken += budget.TryTake(LookupKind.ScenePath, 5.0, 7, urgent: false) ? 1 : 0;
        }

        Assert.Equal(LookupBudget.ScenePathsPerFrame, taken);
        Assert.True(budget.TryTake(LookupKind.ScenePath, 5.0, 8, urgent: false), "the next frame starts a new cap");
    }

    [Fact]
    public void Scans_keep_eight_a_second_whatever_the_frame_and_the_window_starts_again_after_a_second()
    {
        var budget = new LookupBudget();
        int taken = 0;
        for (int i = 0; i < 20; i++)
        {
            taken += budget.TryTake(LookupKind.Scan, 10.0, i, urgent: false) ? 1 : 0;
        }

        Assert.Equal(LookupBudget.ScansPerSecond, taken);
        Assert.False(budget.TryTake(LookupKind.Scan, 10.99, 30, urgent: false));
        Assert.True(budget.TryTake(LookupKind.Scan, 11.0, 31, urgent: false));
    }

    [Fact]
    public void The_two_kinds_are_independent()
    {
        var budget = new LookupBudget();
        for (int i = 0; i < LookupBudget.ScansPerSecond; i++)
        {
            Assert.True(budget.TryTake(LookupKind.Scan, 1.0, 1, urgent: false));
        }

        Assert.False(budget.TryTake(LookupKind.Scan, 1.0, 1, urgent: false));
        Assert.True(budget.TryTake(LookupKind.ScenePath, 1.0, 1, urgent: false), "spent scans don't hold a scene path back");
    }

    [Fact]
    public void An_urgent_lookup_runs_past_a_spent_budget_up_to_its_own_frame_cap()
    {
        var budget = new LookupBudget();
        for (int i = 0; i < LookupBudget.ScenePathsPerFrame; i++)
        {
            budget.TryTake(LookupKind.ScenePath, 2.0, 3, urgent: false);
        }

        Assert.False(budget.TryTake(LookupKind.ScenePath, 2.0, 3, urgent: false));
        int urgent = 0;
        for (int i = 0; i < 40; i++)
        {
            urgent += budget.TryTake(LookupKind.ScenePath, 2.0, 3, urgent: true) ? 1 : 0;
        }

        Assert.Equal(LookupBudget.UrgentPerFrame, urgent);
        Assert.Equal(LookupBudget.UrgentPerFrame, budget.Urgent);
        Assert.Equal(40 - LookupBudget.UrgentPerFrame, budget.UrgentHeldBack);
        Assert.True(budget.TryTake(LookupKind.ScenePath, 2.0, 4, urgent: true), "a new frame, a new urgent cap");
    }

    [Fact]
    public void Urgent_lookups_count_against_their_kind_so_ordinary_ones_wait()
    {
        var budget = new LookupBudget();
        for (int i = 0; i < LookupBudget.ScansPerSecond; i++)
        {
            Assert.True(budget.TryTake(LookupKind.Scan, 0, i, urgent: true));
        }

        Assert.False(budget.TryTake(LookupKind.Scan, 0.5, 20, urgent: false));
        Assert.Equal(LookupBudget.ScansPerSecond, budget.Scans);
    }

    [Theory]
    [InlineData("go:standard_game/Hockey#0/Boards#0", true, LookupKind.ScenePath)]
    [InlineData("go:standard_game/Hockey#0/Boards#0", false, LookupKind.Scan)]
    [InlineData("go:DontDestroyOnLoad/SoundManager#0", false, LookupKind.Scan)]
    [InlineData("asset:TeamData_Officials", true, LookupKind.Scan)]
    [InlineData("go:", true, LookupKind.Scan)]
    [InlineData(null, true, LookupKind.Scan)]
    public void The_kind_follows_the_text_and_whether_its_scene_is_listed(string? text, bool listed, LookupKind kind)
    {
        string? asked = null;
        Assert.Equal(kind, LookupBudget.KindOf(text, scene =>
        {
            asked = scene;
            return listed;
        }));
        if (text is { Length: > 4 } && text.StartsWith("go:", StringComparison.Ordinal))
        {
            Assert.Equal(text[3..text.IndexOf('/', StringComparison.Ordinal)], asked);
        }
    }

    [Fact]
    public void A_reference_is_a_replay_id_or_a_reference_text_and_nothing_else()
    {
        Assert.True(LookupBudget.IsReference(NetValue.FromRef(12)));
        Assert.True(LookupBudget.IsReference(NetValue.FromString("go:a/b#0")));
        Assert.True(LookupBudget.IsReference(NetValue.FromString("asset:Puck")));
        Assert.True(LookupBudget.IsReference(NetValue.FromString("player:2")));
        Assert.False(LookupBudget.IsReference(NetValue.FromString("Riley Ice")));
        Assert.False(LookupBudget.IsReference(NetValue.Null));
        Assert.False(LookupBudget.IsReference(NetValue.FromInt(4)));
    }

    [Fact]
    public void Describe_names_each_kind_and_clear_starts_over()
    {
        var budget = new LookupBudget();
        budget.TryTake(LookupKind.ScenePath, 0, 0, urgent: false);
        budget.TryTake(LookupKind.Scan, 0, 0, urgent: true);
        Assert.Equal("scene paths 1 (held back 0), scans 1 (held back 0), 1 urgent outside the budget", budget.Describe());
        budget.Clear();
        Assert.Equal("scene paths 0 (held back 0), scans 0 (held back 0)", budget.Describe());
        Assert.Equal(0, budget.HeldBack);
    }
}
