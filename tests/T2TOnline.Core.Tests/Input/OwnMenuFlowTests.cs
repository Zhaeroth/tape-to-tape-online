using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Tests.Input;

public class OwnMenuFlowTests
{
    private const long Second = 1_000_000;

    [Fact]
    public void A_remap_goes_from_idle_through_open_and_closing_back_to_idle()
    {
        var flow = new OwnMenuFlow();
        Assert.Equal(OwnMenuState.Idle, flow.State);
        Assert.False(flow.HoldsDevice);
        Assert.Equal(PadAwayReason.Unplugged, flow.AwayReason);

        Assert.True(flow.TryStart(OwnMenuKind.Remapper, 10 * Second));
        Assert.Equal((OwnMenuState.Opening, OwnMenuKind.Remapper), (flow.State, flow.Kind));
        Assert.True(flow.HoldsDevice);
        Assert.Equal(PadAwayReason.ChangingControls, flow.AwayReason);

        Assert.True(flow.Shown(11 * Second));
        Assert.Equal(OwnMenuState.Open, flow.State);
        Assert.Equal(1, flow.Opened);
        Assert.Equal(30 * Second, flow.ShownMicros(41 * Second));

        Assert.True(flow.Closed(50 * Second));
        Assert.Equal(OwnMenuState.Closing, flow.State);
        Assert.Equal(39 * Second, flow.ShownMicros(60 * Second)); // shown until it closed
        Assert.True(flow.HoldsDevice);

        Assert.True(flow.Finish(51 * Second));
        Assert.Equal((OwnMenuState.Idle, OwnMenuKind.None), (flow.State, flow.Kind));
        Assert.False(flow.HoldsDevice);
        Assert.Equal(41 * Second, flow.LastHeldMicros);
        Assert.Equal(0, flow.FellBack);
    }

    [Fact]
    public void The_settings_menu_tells_the_host_its_own_reason()
    {
        var flow = new OwnMenuFlow();
        Assert.True(flow.TryStart(OwnMenuKind.Settings, 0));
        Assert.Equal(PadAwayReason.InSettings, flow.AwayReason);
    }

    [Fact]
    public void A_second_request_while_one_is_under_way_is_refused_and_the_first_goes_on()
    {
        var flow = new OwnMenuFlow();
        Assert.False(flow.TryStart(OwnMenuKind.None, 0));
        Assert.True(flow.TryStart(OwnMenuKind.Remapper, 0));
        Assert.False(flow.TryStart(OwnMenuKind.Settings, Second));
        Assert.Equal(OwnMenuKind.Remapper, flow.Kind);
        flow.Shown(Second);
        Assert.False(flow.TryStart(OwnMenuKind.Remapper, 2 * Second));
        flow.Closed(3 * Second);
        Assert.False(flow.TryStart(OwnMenuKind.Remapper, 3 * Second));
        Assert.Equal(OwnMenuState.Closing, flow.State);
    }

    [Fact]
    public void Steps_out_of_order_answer_false_and_change_nothing()
    {
        var flow = new OwnMenuFlow();
        Assert.False(flow.Shown(0));
        Assert.False(flow.Closed(0));
        Assert.False(flow.Finish(0));
        Assert.False(flow.Abort(0, fellBack: true));
        Assert.Equal(OwnMenuState.Idle, flow.State);
        Assert.Equal(0, flow.FellBack);

        flow.TryStart(OwnMenuKind.Settings, 0);
        Assert.False(flow.Finish(Second)); // not closing yet
        Assert.True(flow.Shown(Second));
        Assert.False(flow.Shown(2 * Second));
        Assert.Equal(1, flow.Opened);
    }

    [Fact]
    public void A_menu_that_never_shows_times_out_once_and_falls_back()
    {
        var flow = new OwnMenuFlow();
        flow.TryStart(OwnMenuKind.Remapper, 0);
        Assert.Equal(OwnMenuTick.None, flow.Update(OwnMenuFlow.OpenTimeoutMicros));
        Assert.Equal(OwnMenuTick.OpenTimedOut, flow.Update(OwnMenuFlow.OpenTimeoutMicros + 1));
        Assert.Equal(OwnMenuTick.None, flow.Update(OwnMenuFlow.OpenTimeoutMicros + Second)); // said once

        Assert.True(flow.Abort(OwnMenuFlow.OpenTimeoutMicros + Second, fellBack: true));
        Assert.Equal(OwnMenuState.Idle, flow.State);
        Assert.Equal(1, flow.FellBack);
        Assert.Equal(0, flow.Opened);
    }

    [Fact]
    public void An_open_menu_has_no_time_limit_but_a_closing_one_does()
    {
        var flow = new OwnMenuFlow();
        flow.TryStart(OwnMenuKind.Settings, 0);
        flow.Shown(Second);
        Assert.Equal(OwnMenuTick.None, flow.Update(3600 * Second)); // as on the couch: as long as the friend likes

        flow.Closed(3600 * Second);
        Assert.Equal(OwnMenuTick.None, flow.Update((3600 * Second) + OwnMenuFlow.CloseTimeoutMicros));
        Assert.Equal(OwnMenuTick.CloseTimedOut, flow.Update((3600 * Second) + OwnMenuFlow.CloseTimeoutMicros + 1));
        Assert.Equal(OwnMenuTick.None, flow.Update((3601 * Second) + OwnMenuFlow.CloseTimeoutMicros));
        Assert.True(flow.Finish(3610 * Second));
    }

    [Theory]
    [InlineData(OwnMenuState.Opening)]
    [InlineData(OwnMenuState.Open)]
    [InlineData(OwnMenuState.Closing)]
    public void A_session_ending_in_any_state_leaves_it_idle(OwnMenuState at)
    {
        var flow = new OwnMenuFlow();
        flow.TryStart(OwnMenuKind.Remapper, 0);
        if (at >= OwnMenuState.Open)
        {
            flow.Shown(Second);
        }

        if (at == OwnMenuState.Closing)
        {
            flow.Closed(2 * Second);
        }

        Assert.Equal(at, flow.State);
        Assert.True(flow.Abort(5 * Second, fellBack: false));
        Assert.Equal((OwnMenuState.Idle, OwnMenuKind.None), (flow.State, flow.Kind));
        Assert.Equal(0, flow.FellBack);
        Assert.Equal(5 * Second, flow.LastHeldMicros);

        // And the next one starts afresh.
        Assert.True(flow.TryStart(OwnMenuKind.Settings, 6 * Second));
        Assert.Equal(OwnMenuTick.None, flow.Update(6 * Second));
    }

    [Fact]
    public void Settings_files_are_allowed_while_a_menu_is_under_way_and_for_a_grace_after()
    {
        var flow = new OwnMenuFlow();
        Assert.False(flow.AllowsSettingsSaves(0));
        Assert.Equal(OwnMenuTick.None, flow.Update(0));

        flow.TryStart(OwnMenuKind.Settings, Second);
        Assert.True(flow.AllowsSettingsSaves(Second));
        flow.Shown(2 * Second);
        Assert.True(flow.AllowsSettingsSaves(100 * Second));
        flow.Closed(100 * Second);
        Assert.True(flow.AllowsSettingsSaves(101 * Second));
        flow.Finish(101 * Second);

        long end = (101 * Second) + OwnMenuFlow.AllowanceGraceMicros;
        Assert.True(flow.AllowsSettingsSaves(end - 1));
        Assert.Equal(OwnMenuTick.None, flow.Update(end - 1));
        Assert.False(flow.AllowsSettingsSaves(end));
        Assert.Equal(OwnMenuTick.AllowanceOver, flow.Update(end));
        Assert.Equal(OwnMenuTick.None, flow.Update(end + Second)); // said once
    }

    [Fact]
    public void An_aborted_menu_keeps_the_same_grace()
    {
        var flow = new OwnMenuFlow();
        flow.TryStart(OwnMenuKind.Settings, 0);
        flow.Abort(Second, fellBack: false);
        Assert.True(flow.AllowsSettingsSaves(Second + OwnMenuFlow.AllowanceGraceMicros - 1));
        Assert.Equal(OwnMenuTick.AllowanceOver, flow.Update(Second + OwnMenuFlow.AllowanceGraceMicros));
    }

    [Fact]
    public void A_new_menu_within_the_grace_holds_the_allowance_until_it_finishes()
    {
        var flow = new OwnMenuFlow();
        flow.TryStart(OwnMenuKind.Settings, 0);
        flow.Abort(Second, fellBack: false);
        flow.TryStart(OwnMenuKind.Remapper, 2 * Second);
        flow.Shown(3 * Second);
        Assert.Equal(OwnMenuTick.None, flow.Update(60 * Second)); // no "allowance over" while one is under way
        Assert.True(flow.AllowsSettingsSaves(60 * Second));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(254, 255)]
    [InlineData(255, 1)]
    public void Generations_run_from_1_to_255_and_round_again(byte current, byte next)
    {
        Assert.Equal(next, OwnMenuFlow.NextGeneration(current));
    }

    [Fact]
    public void Every_generation_is_new_for_255_reads_in_a_row()
    {
        byte generation = 0;
        var seen = new System.Collections.Generic.HashSet<byte>();
        for (int i = 0; i < 255; i++)
        {
            generation = OwnMenuFlow.NextGeneration(generation);
            Assert.NotEqual(0, generation);
            Assert.True(seen.Add(generation));
        }
    }
}
