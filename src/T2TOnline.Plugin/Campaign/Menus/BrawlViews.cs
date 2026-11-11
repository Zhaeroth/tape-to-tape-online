#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppFighting;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The brawl offer and its fight controls (<c>BrawlFightMenu</c>, opened by <c>BrawlChallengeSystem.ShowOfferMenu</c> and
/// <c>ShowFightControls</c>) as a real view on a friend (v1). The menu shows the accept prompt (<c>_acceptPrompt</c>)
/// while an offer is open, and the fight controls (<c>_fightControls</c>) once the brawl started: its <c>Show</c> picks
/// the panel from the game state (Fight shows the controls through <c>ShowControls</c>), its <c>OnGameStateChanged</c>
/// listener switches to the controls when the state becomes Fight, and the end of its <c>Show</c> hides it again when the
/// state isn't Fight and no offer is open (verified in the game's code: <c>&lt;Show&gt;d__9</c>:
/// <c>get_IsOfferActive</c>, then the virtual <c>Hide</c>).
/// <list type="bullet">
/// <item>Host: whether the brawl started (<c>m_IsBrawlStarted</c>) and the offer's seconds left, in the menu's data; read
/// again at once when its fight controls show (<c>ShowControls</c>, <see cref="MenuPatches"/>).</item>
/// <item>Friend, while it shows the host's match: the friend's game makes no offers of its own (client mode holds
/// <c>TryOfferChallenge</c>) and its game state changes without the listener's event (the HUD mirror writes the field),
/// so its menu would hide itself at once. The offer's deadline (<c>BrawlChallengeSystem._offerDeadline</c>, which
/// <c>IsOfferActive</c> compares with <c>Time.time</c>) is set here to the host's seconds left while the host's offer
/// is open, so the game's own <c>Show</c> keeps the accept prompt up; it's put back to "no offer" (-1, the game's own
/// value) when the host's menu closes. The fight controls show with the game's own <c>ShowControls</c> once the host's
/// brawl started. The glyphs on it follow this PC's own device (the prompt pin): LB+RB on a pad, Q+E on the keyboard, which
/// the host relays as this player's accept (<c>BrawlComboRelay</c>).</item>
/// </list>
/// The menu never shows as text on a friend (a text panel over the match would hide the play; before v1 friends saw
/// nothing): when it can't be shown for real, the log says why and the screen stays as it is.
/// </summary>
internal static class BrawlViews
{
    /// <summary>The brawl offer and fight controls menu's class.</summary>
    public const string Type = "BrawlFightMenu";

    /// <summary>Value: the host's brawl started (the fight controls show).</summary>
    public const string FightingKey = "brawl.fighting";

    /// <summary>Value: whole seconds left in the host's offer (0 once it's over or accepted).</summary>
    public const string LeftKey = "brawl.left";

    // The game's "no offer" deadline (BrawlChallengeSystem..cctor, CancelOffer, AcceptOffer: -1f).
    private const float NoOffer = -1f;

    // A friend's offer is held this much longer than the host's seconds left: the menu's Show checks the offer only after
    // its own opening animation, and the host's close (which closes this copy) comes over the network.
    private const float MarginSeconds = 1.5f;
    private const int MaxSeconds = 60;

    private static float _held = float.NaN;

    /// <summary>Host: the brawl menu's state, for a friend's copy.</summary>
    public static void Read(BrawlFightMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        values.Add(new(FightingKey, NetValue.FromBool(menu.m_IsBrawlStarted)));
        float left = BrawlChallengeSystem.OfferSecondsRemaining;
        int seconds = float.IsFinite(left) && left > 0 ? (int)Math.Min(Math.Ceiling(left), MaxSeconds) : 0;
        values.Add(new(LeftKey, NetValue.FromInt(seconds)));
    }

    /// <summary>The host's brawl started (its data says so).</summary>
    public static bool Fighting(MenuData data) => data.Flag(FightingKey);

    /// <summary>Whole seconds left in the host's offer (0..60).</summary>
    public static int SecondsLeft(MenuData data) => (int)Math.Clamp(data.Whole(LeftKey), 0, MaxSeconds);

    /// <summary>
    /// Friend: holds this PC's offer open for <paramref name="seconds"/> (plus a margin), so the game's own menu stays up as
    /// the host's does. Returns the deadline set.
    /// </summary>
    public static float HoldOffer(int seconds)
    {
        float deadline = UnityEngine.Time.time + Math.Clamp(seconds, 0, MaxSeconds) + MarginSeconds;
        BrawlChallengeSystem._offerDeadline = deadline;
        _held = deadline;
        return deadline;
    }

    /// <summary>
    /// Friend: the offer this PC held for the host's menu goes back to "no offer" (only when it's still the one set here).
    /// True when it was put back.
    /// </summary>
    public static bool ReleaseOffer()
    {
        if (float.IsNaN(_held))
        {
            return false;
        }

        bool ours = BrawlChallengeSystem._offerDeadline == _held;
        _held = float.NaN;
        if (ours)
        {
            BrawlChallengeSystem._offerDeadline = NoOffer;
        }

        return ours;
    }

    /// <summary>Friend: shows the fight controls on <paramref name="menu"/> the game's own way. False when they already show.</summary>
    public static bool ShowControls(BrawlFightMenu menu)
    {
        if (menu.m_IsBrawlStarted)
        {
            return false;
        }

        menu.ShowControls();
        return true;
    }
}
#endif
