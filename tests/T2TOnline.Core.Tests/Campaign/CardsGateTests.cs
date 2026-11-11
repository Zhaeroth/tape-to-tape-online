using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class CardsGateTests
{
    private const int Playing = 1;
    private const int Over = CardsGate.HostIntroOver;

    [Fact]
    public void The_intros_own_reveal_waits_while_the_hosts_cards_arent_in()
    {
        Assert.True(CardsGate.Holds(shown: true, instant: false, playingIntro: true, byTheMod: false, hostCards: 0, hostIntro: Playing));

        // The host's cards are in, or its intro is over: no wait.
        Assert.False(CardsGate.Holds(true, false, true, false, hostCards: 1, hostIntro: Playing));
        Assert.False(CardsGate.Holds(true, false, true, false, hostCards: 0, hostIntro: Over));
    }

    [Fact]
    public void Only_the_intros_animated_reveal_is_held()
    {
        // Putting the cards away, the setup's instant hide, a reveal after the intro, and the mod's own reveal (its release,
        // the Nurse's takeover) all go through.
        Assert.False(CardsGate.Holds(shown: false, instant: false, playingIntro: true, byTheMod: false, hostCards: 0, hostIntro: Playing));
        Assert.False(CardsGate.Holds(shown: true, instant: true, playingIntro: true, byTheMod: false, hostCards: 0, hostIntro: Playing));
        Assert.False(CardsGate.Holds(shown: true, instant: false, playingIntro: false, byTheMod: false, hostCards: 0, hostIntro: Playing));
        Assert.False(CardsGate.Holds(shown: true, instant: false, playingIntro: true, byTheMod: true, hostCards: 0, hostIntro: Playing));
    }

    [Fact]
    public void A_host_that_doesnt_say_is_never_waited_for()
    {
        Assert.False(CardsGate.Holds(true, false, true, false, hostCards: -1, hostIntro: Playing));
        Assert.False(CardsGate.Holds(true, false, true, false, hostCards: -1, hostIntro: -1));
        Assert.Equal(CardsRelease.HostSilent, CardsGate.Release(hostCards: -1, hostIntro: Playing, heldSeconds: 1));
    }

    [Fact]
    public void Held_cards_come_in_with_the_hosts_or_its_intros_end_or_at_the_cap()
    {
        Assert.Equal(CardsRelease.Hold, CardsGate.Release(0, Playing, 0));
        Assert.Equal(CardsRelease.Hold, CardsGate.Release(0, Playing, CardsGate.MaxHoldSeconds - 0.1));
        Assert.Equal(CardsRelease.HostCards, CardsGate.Release(1, Playing, 9.4));
        Assert.Equal(CardsRelease.HostCards, CardsGate.Release(1, Over, 9.4));
        Assert.Equal(CardsRelease.HostIntroOver, CardsGate.Release(0, Over, 3));
        Assert.Equal(CardsRelease.TimedOut, CardsGate.Release(0, Playing, CardsGate.MaxHoldSeconds));
    }
}
