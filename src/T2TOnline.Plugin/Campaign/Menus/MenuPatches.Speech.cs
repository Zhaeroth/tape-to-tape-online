#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The speech mirror's and the event cards' patches (v1; Hooks/hooks.txt, "the speech mirror"). Both targets
/// are unfolded and non-generic, and take no small struct by value.
/// <list type="bullet">
/// <item><c>SpeechBubbleView.RequestSkip</c> (prefix): every press a speech bubble takes (its own input poll, or a
/// controller's <c>Skip</c>). Host: a press on a mirrored speaker's bubble raises its line's stage for friends
/// (<see cref="SpeechMirror.HostPressed"/>; reads only). Friend: a press on a bubble that follows the host's that isn't the
/// mod's own is refused (<see cref="SpeechMirror.FriendRefuses"/>).</item>
/// <item><c>EventChoiceMenu.SetChoicesShown(bool, bool)</c> (prefix): host, whether its cards are in (event state item
/// <see cref="EventCapture.Cards"/>); friend, its intro's own reveal waits for the host's cards
/// (<see cref="FriendMenus.HoldCards"/>).</item>
/// </list>
/// </summary>
internal static partial class MenuPatches
{
    private static readonly Dictionary<IntPtr, bool> HostCards = new();
    private static readonly HashSet<IntPtr> HostExits = new();

    /// <summary>
    /// True when speech presses can be mirrored (the <c>RequestSkip</c> patch went in). Without it a host still sends each
    /// line's start, so a friend's line advances when the host's next one starts.
    /// </summary>
    public static bool SpeechSkipReady { get; private set; }

    /// <summary>True when a friend's cards can wait for the host's (the <c>SetChoicesShown</c> patch went in).</summary>
    public static bool CardsReady { get; private set; }

    /// <summary>
    /// Host: whether <paramref name="menu"/>'s cards are in (its last <c>SetChoicesShown</c>): 1 or 0, or -1 before its first.
    /// </summary>
    public static int HostCardsShown(EventChoiceMenu menu) => HostCards.TryGetValue(menu.Pointer, out bool shown) ? (shown ? 1 : 0) : -1;

    /// <summary>Host: true once <paramref name="menu"/>'s exit started (<c>EventChoiceMenu.Exit</c> ran since it opened).</summary>
    public static bool HostExiting(EventChoiceMenu menu) => HostExits.Contains(menu.Pointer);

    /// <summary>Host: <paramref name="menu"/> closed; its cards' and exit's state is forgotten.</summary>
    public static void ForgetHostCards(IntPtr menu)
    {
        HostCards.Remove(menu);
        HostExits.Remove(menu);
    }

    /// <summary>Host: hosting stopped; every menu's cards' and exit's state is forgotten.</summary>
    public static void ClearHostCards()
    {
        HostCards.Clear();
        HostExits.Clear();
    }

    /// <summary>The speech mirror's and the cards' patches, and the node rewards'.</summary>
    private static void InstallSpeech(HarmonyLib.Harmony harmony, List<string> failed)
    {
        SpeechSkipReady = Patch(harmony, failed, typeof(SpeechBubbleView), "RequestSkip", prefix: nameof(SpeechSkipPrefix));
        CardsReady = Patch(harmony, failed, typeof(EventChoiceMenu), "SetChoicesShown", prefix: nameof(ChoicesShownPrefix));

        // The General Manager's rerolled map node rewards (MenuPatches.NodeRewards.cs).
        InstallNodeRewards(harmony, failed);
    }

    /// <summary>
    /// A speech bubble takes a press (before it acts). Host: a mirrored speaker's line goes forward for friends. Friend: a
    /// press that isn't the mod's on a bubble that follows the host's is refused.
    /// </summary>
    private static bool SpeechSkipPrefix(SpeechBubbleView __instance)
    {
        try
        {
            if (__instance == null)
            {
                return true;
            }

            if (Views is { Hosting: true } && SpeechMirror.HostSpeaking)
            {
                SpeechMirror.HostPressed(__instance);
            }
            else if (FollowingHost && SpeechMirror.FriendRefuses(__instance))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("speech press", ex);
        }

        return true;
    }

    /// <summary>
    /// An event menu's cards are shown or put away. Host: noted for friends (its cards are in or not). Friend: its intro's own
    /// reveal waits while the host's cards aren't in (the mod makes it once they are).
    /// </summary>
    private static bool ChoicesShownPrefix(EventChoiceMenu __instance, bool shown, bool instant)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                if (HostCards.Count > 16)
                {
                    HostCards.Clear(); // menus that never closed through Hide_Internal (a scene unload): none outlives its scene
                }

                HostCards[__instance.Pointer] = shown;
            }
            else if (Friend is { } friend && friend.HoldCards(__instance, shown, instant))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("event cards", ex);
        }

        return true;
    }

    /// <summary>
    /// Host: an event menu's exit starts (<c>EventChoiceMenu.Exit</c>, whose <c>ExitAsync</c> puts its cards away and
    /// awaits its NPC's goodbye, before its close): noted for friends. Reads only; a friend's own exit is refused.
    /// </summary>
    private static void NoteHostExit(EventChoiceMenu menu)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                if (HostExits.Count > 16)
                {
                    HostExits.Clear(); // menus that never closed through Hide_Internal (a scene unload): none outlives its scene
                }

                HostExits.Add(menu.Pointer);
            }
        }
        catch (Exception ex)
        {
            Failed("event exit", ex);
        }
    }
}
#endif
