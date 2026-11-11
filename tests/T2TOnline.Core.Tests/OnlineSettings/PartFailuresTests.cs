using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

/// <summary>When a failing part of the native screens' layer is switched off for the game.</summary>
public class PartFailuresTests
{
    [Fact]
    public void A_part_that_fails_every_frame_goes_off_at_its_tenth_failure()
    {
        var part = new PartFailures();
        for (long tick = 1; tick < PartFailures.MaxFailures; tick++)
        {
            Assert.False(part.Failed(tick));
            Assert.False(part.Off);
        }

        Assert.True(part.Failed(PartFailures.MaxFailures));
        Assert.True(part.Off);
    }

    [Theory]
    [InlineData(15)] // four looks a second
    [InlineData(60)] // one look a second
    public void A_part_that_fails_at_every_look_and_idles_through_the_frames_between_goes_off_too(int framesPerLook)
    {
        // The part works every so many frames only; the frames between return at once and count as clean. Failures "in a row" start again at
        // each of those, and the part would fail, and say so, for as long as the game runs.
        var part = new PartFailures();
        int failures = 0;
        long tick = 0;
        while (!part.Off && tick < 100_000)
        {
            tick++;
            if (tick % framesPerLook == 0)
            {
                failures++;
                bool wentOff = part.Failed(tick);
                Assert.Equal(failures == PartFailures.MaxFailures, wentOff);
            }
            else
            {
                part.Succeeded(tick);
            }
        }

        Assert.True(part.Off);
        Assert.Equal(PartFailures.MaxFailures, failures);
        Assert.Equal((long)PartFailures.MaxFailures * framesPerLook, tick);
    }

    [Fact]
    public void A_failure_of_its_own_is_forgotten_after_a_clean_half_minute()
    {
        var part = new PartFailures();
        Assert.False(part.Failed(100));
        Assert.Equal(1, part.Failures);

        part.Succeeded(100 + PartFailures.CleanTicks); // not yet past the stretch
        Assert.Equal(1, part.Failures);
        part.Succeeded(101 + PartFailures.CleanTicks);
        Assert.Equal(0, part.Failures);
    }

    [Fact]
    public void Failures_far_apart_never_switch_a_part_off()
    {
        // No clean tick is told between them (a part that fails each time it is ticked, but is ticked rarely): the stretch is still counted.
        var part = new PartFailures();
        long tick = 0;
        for (int i = 0; i < PartFailures.MaxFailures * 3; i++)
        {
            tick += PartFailures.CleanTicks + 1;
            Assert.False(part.Failed(tick));
            Assert.Equal(1, part.Failures);
        }

        Assert.False(part.Off);
    }

    [Fact]
    public void A_part_that_went_off_stays_off()
    {
        var part = new PartFailures();
        for (long tick = 1; tick <= PartFailures.MaxFailures; tick++)
        {
            part.Failed(tick);
        }

        Assert.True(part.Off);
        part.Succeeded(1_000_000);
        Assert.True(part.Off);
        Assert.False(part.Failed(1_000_001)); // said once, when it went off
        Assert.Equal(PartFailures.MaxFailures, part.Failures);
    }
}
