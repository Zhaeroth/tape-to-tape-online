using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class NurseResultPlanTests
{
    [Theory]
    [InlineData(2)] // swap
    [InlineData(3)] // exchange
    [InlineData(4)] // sacrifice
    [InlineData(5)] // injury for a talent
    [InlineData(6)] // injury for an attribute
    [InlineData(7)] // heal all
    [InlineData(10)]
    [InlineData(-1)]
    public void Results_with_no_gated_effect_do_nothing(int procedure)
    {
        NurseResultPlan plan = NurseResultPlan.For(procedure);
        Assert.False(plan.Any);
        Assert.Equal(0f, plan.CloseAfterSeconds);
        Assert.Equal("nothing", plan.ToString());
    }

    [Fact]
    public void A_transplant_hits_and_closes_itself()
    {
        NurseResultPlan plan = NurseResultPlan.For(NurseResultPlan.Transplant);
        Assert.True(plan.Any);
        Assert.Equal(NurseResultShake.SwapPopup, plan.Hit);
        Assert.Equal(1.35f, plan.CloseAfterSeconds);
        Assert.False(plan.SurgeryTooltips || plan.HealSound || plan.HealPunch || plan.CloneCost);
        Assert.Equal("hit, closes by itself in 1.35 s", plan.ToString());
    }

    [Fact]
    public void A_surgery_hits_shows_its_tooltips_and_waits_for_a_press()
    {
        NurseResultPlan plan = NurseResultPlan.For(NurseResultPlan.ReplaceTalent);
        Assert.Equal(NurseResultShake.InjuryPopup, plan.Hit);
        Assert.True(plan.SurgeryTooltips);
        Assert.Equal(0f, plan.CloseAfterSeconds);
    }

    [Fact]
    public void Frozen_peas_heal_with_their_title_sound_and_punch_and_close_themselves()
    {
        NurseResultPlan plan = NurseResultPlan.For(NurseResultPlan.FrozenPeas);
        Assert.Equal(NurseResultShake.None, plan.Hit);
        Assert.True(plan.ProcedureTitle && plan.HealedIcons && plan.HealSound && plan.HealPunch);
        Assert.Equal(1.45f, plan.CloseAfterSeconds);
    }

    [Fact]
    public void A_clone_hits_shows_its_cost_and_closes_itself()
    {
        NurseResultPlan plan = NurseResultPlan.For(NurseResultPlan.Cloning);
        Assert.Equal(NurseResultShake.SwapPopup, plan.Hit);
        Assert.True(plan.CloneCost && plan.CostPunch);
        Assert.Equal(1.41f, plan.CloseAfterSeconds);
        Assert.Contains("the clone's cost", plan.ToString());
    }
}
