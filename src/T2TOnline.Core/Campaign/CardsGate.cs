namespace T2TOnline.Core.Campaign;

/// <summary>Why a friend's held event cards come in now.</summary>
public enum CardsRelease
{
    /// <summary>They keep waiting.</summary>
    Hold = 0,

    /// <summary>The host's cards are in.</summary>
    HostCards = 1,

    /// <summary>The host's intro is over (its cards came, or its screen went past them).</summary>
    HostIntroOver = 2,

    /// <summary>The host's state stopped saying (a menu value that went): nothing to wait for.</summary>
    HostSilent = 3,

    /// <summary>They waited <see cref="CardsGate.MaxHoldSeconds"/>: in anyway.</summary>
    TimedOut = 4,
}

/// <summary>
/// The event cards on a friend come in when the host's do.
/// On the host's screen the cards come in only after the NPC's last line (the intro awaits the NPC's dialogue, then
/// <c>EventChoiceMenu.SetChoicesShown(true, false)</c>); a friend's intro has no dialogue of its own to wait for (the host's
/// lines play beside it), so its own reveal is held while the host's cards aren't in, and made by the mod once they are.
/// A host that doesn't say (the value never came) is never waited for. Pure logic; the glue refuses and makes the reveal.
/// </summary>
public static class CardsGate
{
    /// <summary>The host's intro state when it's over (<c>EventCapture.IntroOver</c>).</summary>
    public const int HostIntroOver = 2;

    /// <summary>The longest a friend's cards wait for the host's (a long speech read slowly fits).</summary>
    public const double MaxHoldSeconds = 120;

    /// <summary>
    /// True when this PC's own reveal is held: a reveal (<paramref name="shown"/>, animated: not <paramref name="instant"/>)
    /// that its intro makes (<paramref name="playingIntro"/>), not the mod (<paramref name="byTheMod"/>), while the host
    /// says its cards aren't in (<paramref name="hostCards"/> 0; -1: it hasn't said) and its intro isn't over
    /// (<paramref name="hostIntro"/>).
    /// </summary>
    public static bool Holds(bool shown, bool instant, bool playingIntro, bool byTheMod, int hostCards, int hostIntro) =>
        shown && !instant && playingIntro && !byTheMod && hostCards == 0 && hostIntro != HostIntroOver;

    /// <summary>Whether held cards come in now, after <paramref name="heldSeconds"/>, from the host's cards and intro state (-1: not said).</summary>
    public static CardsRelease Release(int hostCards, int hostIntro, double heldSeconds) =>
        hostCards == 1 ? CardsRelease.HostCards
        : hostIntro == HostIntroOver ? CardsRelease.HostIntroOver
        : hostCards < 0 ? CardsRelease.HostSilent
        : heldSeconds >= MaxHoldSeconds ? CardsRelease.TimedOut
        : CardsRelease.Hold;
}
