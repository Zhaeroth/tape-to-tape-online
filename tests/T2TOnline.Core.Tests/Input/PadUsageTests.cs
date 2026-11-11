using System;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PadUsageTests
{
    [Fact]
    public void A_trigger_nobody_pulled_is_for_the_log_never_for_the_screen()
    {
        // Either trigger skates backwards: LT used, RT left alone, on a healthy pad.
        var usage = new PadUsage();
        usage.Observe(PadButtons.None, 0f, 0f, 0f, 0f, 1f, 0f);
        Assert.NotNull(usage.TriggerNotice(steamInput: false));
        Assert.NotNull(usage.TriggerNotice(steamInput: true));
        Assert.False(usage.TriggerStoppedShort);
    }

    [Theory]
    [InlineData(1f, 0.38f)]
    [InlineData(0.38f, 1f)]
    public void A_trigger_that_moved_and_stopped_short_is_for_the_screen(float left, float right)
    {
        var usage = new PadUsage();
        usage.Observe(PadButtons.None, 0f, 0f, 0f, 0f, left, right);
        Assert.NotNull(usage.TriggerNotice(steamInput: false));
        Assert.True(usage.TriggerStoppedShort);
    }

    [Theory]
    [InlineData(1f, 0.2f)]
    [InlineData(0.2f, 1f)]
    [InlineData(1f, 0.06f)]
    public void A_trigger_only_brushed_while_the_other_is_used_is_for_the_log_never_for_the_screen(float left, float right)
    {
        // A finger resting on the trigger a player doesn't use: the peak is real, and says nothing about the pad.
        var usage = new PadUsage();
        usage.Observe(PadButtons.None, 0f, 0f, 0f, 0f, left, right);
        Assert.NotNull(usage.TriggerNotice(steamInput: false));
        Assert.False(usage.TriggerStoppedShort);
    }

    [Fact]
    public void A_trigger_pulled_as_far_as_the_floor_counts_as_pulled()
    {
        var usage = new PadUsage();
        usage.Observe(PadButtons.None, 0f, 0f, 0f, 0f, 1f, TriggerCheck.PulledFloor);
        Assert.True(usage.TriggerStoppedShort);
    }

    [Theory]
    [InlineData(0f, 0f)] // nothing pulled
    [InlineData(1f, 1f)] // both fine
    [InlineData(0.3f, 0.3f)] // both short: nothing to compare with
    [InlineData(0.3f, 0f)]
    public void Without_the_other_trigger_past_the_threshold_the_screen_says_nothing(float left, float right)
    {
        var usage = new PadUsage();
        usage.Observe(PadButtons.None, 0f, 0f, 0f, 0f, left, right);
        Assert.False(usage.TriggerStoppedShort);
    }

    [Fact]
    public void Nothing_read_says_so()
    {
        Assert.Equal("nothing used", new PadUsage().Describe());
    }

    [Fact]
    public void Buttons_and_axes_past_half_way_count_and_the_rest_are_named_as_never()
    {
        var usage = new PadUsage();
        usage.Observe(PadButtons.A | PadButtons.LeftBumper, 0.2f, -0.9f, 0f, 0f, 0.8f, 0.49f);
        usage.Observe(PadButtons.B, 0f, 0f, 0f, 0f, 0f, 0f);

        Assert.Equal(PadButtons.A | PadButtons.B | PadButtons.LeftBumper, usage.Buttons);
        Assert.Equal((1 << (int)PadAxis.LeftStickY) | (1 << (int)PadAxis.LeftTrigger), usage.Axes);
        string line = usage.Describe();
        Assert.StartsWith("used A, B, LeftBumper, LeftStickY, LeftTrigger; never ", line);
        Assert.Contains("RightTrigger", line[line.IndexOf("never", StringComparison.Ordinal)..]);
        Assert.Contains("X", line[line.IndexOf("never", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Everything_used_has_no_never_part()
    {
        var usage = new PadUsage();
        usage.Observe(PadButtons.All, 1f, 1f, -1f, -1f, 1f, 1f);

        Assert.DoesNotContain("never", usage.Describe());
    }

    [Fact]
    public void Missing_elements_are_named_and_out_of_range_buttons_ignored()
    {
        Assert.Equal("RightStick, DPadUp, RightTrigger", PadUsage.Missing(new[] { 9, 10, 40, -1 }, new[] { PadAxis.RightTrigger }));
        Assert.Equal(string.Empty, PadUsage.Missing(Array.Empty<int>(), Array.Empty<PadAxis>()));
    }
}
