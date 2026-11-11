using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class RunLoadHoldTests
{
    [Fact]
    public void A_trip_that_loads_nothing_never_waits()
    {
        Assert.Equal(RunLoadStep.Go, RunLoadHold.Decide(loads: false, held: true, waited: -1));
        Assert.Equal(RunLoadStep.Go, RunLoadHold.Decide(loads: false, held: true, waited: 10));
    }

    [Fact]
    public void A_load_waits_while_the_event_screen_is_up_or_closing()
    {
        Assert.Equal(RunLoadStep.Go, RunLoadHold.Decide(loads: true, held: false, waited: -1));
        Assert.Equal(RunLoadStep.Wait, RunLoadHold.Decide(loads: true, held: true, waited: -1));
        Assert.Equal(RunLoadStep.Wait, RunLoadHold.Decide(loads: true, held: true, waited: 0));
        Assert.Equal(RunLoadStep.Wait, RunLoadHold.Decide(loads: true, held: true, waited: RunLoadHold.MaxWaitSeconds - 0.01));
        Assert.Equal(RunLoadStep.GoAfterWait, RunLoadHold.Decide(loads: true, held: false, waited: 0.31));
        Assert.Equal(RunLoadStep.GoAfterWait, RunLoadHold.Decide(loads: true, held: false, waited: 0));
    }

    [Fact]
    public void A_load_never_waits_longer_than_the_cap()
    {
        Assert.Equal(RunLoadStep.GoAfterTimeout, RunLoadHold.Decide(loads: true, held: true, waited: RunLoadHold.MaxWaitSeconds));
        Assert.Equal(RunLoadStep.GoAfterTimeout, RunLoadHold.Decide(loads: true, held: true, waited: 60));
        Assert.True(RunLoadHold.ClosingSeconds < RunLoadHold.MaxWaitSeconds);
    }
}
