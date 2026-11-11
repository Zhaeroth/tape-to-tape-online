using System;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PadStateTests
{
    [Theory]
    [InlineData(-2f, -32767)]
    [InlineData(-1f, -32767)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 16384)]
    [InlineData(1f, 32767)]
    [InlineData(float.NaN, 0)]
    public void Stick_values_clamp_to_raw(float value, short expected) => Assert.Equal(expected, PadState.StickToRaw(value));

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 0)]
    [InlineData(1f, 65535)]
    [InlineData(5f, 65535)]
    public void Trigger_values_clamp_to_raw(float value, ushort expected) => Assert.Equal(expected, PadState.TriggerToRaw(value));

    [Fact]
    public void Axes_read_back_normalized()
    {
        var state = new PadState(PadButtons.None, 0, 32767, -32767, 0, 16384, 65535, 0);
        Assert.Equal(1f, state.GetAxis(PadAxis.LeftStickX));
        Assert.Equal(-1f, state.GetAxis(PadAxis.LeftStickY));
        Assert.Equal(0f, state.GetAxis(PadAxis.RightStickX));
        Assert.Equal(0.5f, state.GetAxis(PadAxis.RightStickY), 3);
        Assert.Equal(1f, state.GetAxis(PadAxis.LeftTrigger));
        Assert.Equal(0f, state.GetAxis(PadAxis.RightTrigger));
    }

    [Fact]
    public void Unknown_buttons_and_counter_bits_are_masked_off()
    {
        var state = new PadState((PadButtons)0xFFFF, uint.MaxValue, 0, 0, 0, 0, 0, 0);
        Assert.Equal(PadButtons.All, state.Buttons);
        Assert.Equal(PadState.PressCounterMask, state.PressCounters);
    }

    [Fact]
    public void Equality_covers_every_field()
    {
        var a = new PadState(PadButtons.A, 1, 1, 2, 3, 4, 5, 6);
        Assert.Equal(a, new PadState(PadButtons.A, 1, 1, 2, 3, 4, 5, 6));
        Assert.NotEqual(a, new PadState(PadButtons.B, 1, 1, 2, 3, 4, 5, 6));
        Assert.NotEqual(a, new PadState(PadButtons.A, 2, 1, 2, 3, 4, 5, 6));
        Assert.NotEqual(a, new PadState(PadButtons.A, 1, 1, 2, 3, 4, 5, 7));
        Assert.True(a == new PadState(PadButtons.A, 1, 1, 2, 3, 4, 5, 6));
    }

    [Fact]
    public void Counter_index_is_checked() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PadState.Neutral.GetPressCounter(PadState.ButtonCount));
}

public class PadSamplerTests
{
    [Fact]
    public void Each_new_press_advances_that_buttons_counter()
    {
        var sampler = new PadSampler();
        sampler.ObserveButtons(PadButtons.A);
        sampler.ObserveButtons(PadButtons.A);
        sampler.ObserveButtons(PadButtons.None);
        sampler.ObserveButtons(PadButtons.A | PadButtons.X);

        PadState state = sampler.Capture(0, 0, 0, 0, 0, 0);
        Assert.Equal(2, state.GetPressCounter(0));
        Assert.Equal(1, state.GetPressCounter(2));
        Assert.Equal(0, state.GetPressCounter(1));
        Assert.Equal(PadButtons.A | PadButtons.X, state.Buttons);
    }

    [Fact]
    public void A_tap_between_captures_is_kept_in_the_counter()
    {
        var sampler = new PadSampler();
        PadState before = sampler.Capture(0, 0, 0, 0, 0, 0);
        sampler.ObserveButtons(PadButtons.B);
        sampler.ObserveButtons(PadButtons.None);
        PadState after = sampler.Capture(0, 0, 0, 0, 0, 0);

        Assert.False(after.IsHeld(PadButtons.B));
        Assert.Equal(PadButtons.B, PadTaps.Between(before, after));
    }

    [Fact]
    public void Counters_wrap_after_four_presses()
    {
        var sampler = new PadSampler();
        for (int i = 0; i < 5; i++)
        {
            sampler.ObserveButtons(PadButtons.Y);
            sampler.ObserveButtons(PadButtons.None);
        }

        Assert.Equal(1, sampler.Capture(0, 0, 0, 0, 0, 0).GetPressCounter(3));
    }

    [Fact]
    public void Release_all_keeps_counters()
    {
        var sampler = new PadSampler();
        sampler.ObserveButtons(PadButtons.Start);
        sampler.ReleaseAll();
        PadState state = sampler.Capture(0, 0, 0, 0, 0, 0);
        Assert.Equal(PadButtons.None, state.Buttons);
        Assert.Equal(1, state.GetPressCounter(7));
    }
}

public class PadTapsTests
{
    private static PadState Make(PadButtons held, params int[] countersByButton)
    {
        uint counters = 0;
        for (int i = 0; i < countersByButton.Length; i++)
        {
            counters |= (uint)(countersByButton[i] & 3) << (i * 2);
        }

        return new PadState(held, counters, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void An_ordinary_press_is_not_a_tap()
    {
        Assert.Equal(PadButtons.None, PadTaps.Between(Make(PadButtons.None, 0), Make(PadButtons.A, 1)));
    }

    [Fact]
    public void A_release_is_not_a_tap()
    {
        Assert.Equal(PadButtons.None, PadTaps.Between(Make(PadButtons.A, 1), Make(PadButtons.None, 1)));
    }

    [Fact]
    public void Press_and_release_between_samples_is_a_tap()
    {
        Assert.Equal(PadButtons.A, PadTaps.Between(Make(PadButtons.None, 0), Make(PadButtons.None, 1)));
    }

    [Fact]
    public void Release_and_repress_between_samples_is_a_tap()
    {
        Assert.Equal(PadButtons.A, PadTaps.Between(Make(PadButtons.A, 1), Make(PadButtons.A, 2)));
    }

    [Fact]
    public void Counter_wraparound_is_handled()
    {
        Assert.Equal(PadButtons.A, PadTaps.Between(Make(PadButtons.None, 3), Make(PadButtons.None, 0)));
    }
}
