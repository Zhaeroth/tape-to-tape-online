using System;
using System.Globalization;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Lobby;

// An event whose pick hands its screen to a screen of its own (the Nurse's procedure screen: the event screen "taken over").
// The game's campaign walker drives only an event's choice menu: once a takeover's screen is up its cards are gone, and the
// walker gives up three seconds later ("zero enabled choices"). The tour pauses the walker's map walk while a takeover is up,
// drives that screen to its end by the pad as a player would, and lets the walk go on once the map is free again.
public static partial class CampaignTour
{
    /// <summary>How long the map may take to be free again (nothing over it, the map's tracker unlocked) after a takeover's screen closed.</summary>
    public const double TakeoverMapSeconds = 10;

    /// <summary>
    /// The pad's next press on a takeover's screen, <paramref name="sinceProgress"/> presses after its stage last moved on: A
    /// (the focused option: the screen's confirm moves to the next stage, or at its result closes it); after an A that moved
    /// nothing, the d-pad to the next option: down twice (the screen's lists wrap), then right once (a grid's next column).
    /// The screen's back does nothing at its first stage or its result, so it is never pressed.
    /// </summary>
    public static PadButtons TakeoverPress(int sinceProgress)
    {
        if (sinceProgress <= 0 || sinceProgress % 2 == 0)
        {
            return PadButtons.A;
        }

        return (sinceProgress / 2) % 3 == 2 ? PadButtons.DPadRight : PadButtons.DPadDown;
    }

    /// <summary>True when the map walk goes on after a takeover's screen ended this way (the pad left it and the map is free).</summary>
    public static bool WalkGoesOnAfter(CampaignEventEnd end, bool mapFree) => mapFree && end is CampaignEventEnd.Closed or CampaignEventEnd.LeftByBack;

    /// <summary>
    /// The line as the walk pauses: <c>campaign: node 15 (Nurse): its pick took the event screen over (TRANSPLANT, the
    /// procedure screen): the walker's map walk paused; the pad drives the screen to its end</c>.
    /// </summary>
    public static string TakeoverLine(int node, string? nodeType, string screen) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign: node {Math.Max(node, 0)} ({TypeText(nodeType)}): its pick took the event screen over ({screen}): the walker's map walk paused; the pad drives the screen to its end");

    /// <summary>
    /// The line after it: <c>campaign: the takeover's screen (TRANSPLANT, the procedure screen): closed by the pad in 21.3 s;
    /// 12 press(es), 5 stage(s) on; the map free again: the walker's map walk goes on</c>.
    /// </summary>
    public static string TakeoverEndLine(string screen, CampaignEventEnd end, double seconds, int presses, int stages, bool mapFree)
    {
        string how = end switch
        {
            CampaignEventEnd.Closed => "closed by the pad",
            CampaignEventEnd.LeftByBack => "left with no pick by the pad's skip",
            CampaignEventEnd.Stuck => "the pad couldn't leave it",
            CampaignEventEnd.NotOpened => "not up",
            _ => "a screen the tour doesn't press on came up",
        };

        string after = WalkGoesOnAfter(end, mapFree) ? "the map free again: the walker's map walk goes on"
            : end is CampaignEventEnd.Closed or CampaignEventEnd.LeftByBack ? "the map not free again: the walk stops here"
            : "the walk stops here";
        return string.Create(CultureInfo.InvariantCulture,
            $"campaign: the takeover's screen ({screen}): {how} in {Math.Max(seconds, 0):0.0} s; {Math.Max(presses, 0)} press(es), {Math.Max(stages, 0)} stage(s) on; {after}");
    }

    /// <summary>
    /// cp_end's line before the map's menu: <c>campaign: an event screen is still up (taken over): the pad leaves it first:
    /// closed by the pad in 9.0 s, 6 press(es)</c>.
    /// </summary>
    public static string LeaveEventLine(bool takenOver, CampaignEventEnd end, double seconds, int presses)
    {
        string how = end switch
        {
            CampaignEventEnd.Closed => "closed by the pad",
            CampaignEventEnd.LeftByBack => "left with no pick by the pad's skip",
            CampaignEventEnd.Stuck => "the pad couldn't leave it",
            CampaignEventEnd.NotOpened => "not up",
            _ => "a screen the tour doesn't press on came up",
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"campaign: an event screen is still up{(takenOver ? " (taken over by its own screen)" : string.Empty)}: the pad leaves it first: {how} in {Math.Max(seconds, 0):0.0} s, {Math.Max(presses, 0)} press(es)");
    }

    private static string TypeText(string? nodeType) => string.IsNullOrEmpty(nodeType) ? "?" : nodeType.Length > 32 ? nodeType[..32] : nodeType;
}
