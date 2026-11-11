#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The controller screen's patches (v1; Hooks/hooks.txt, "the controller screen as a real view"): all on
/// unfolded, non-generic methods with no small by-value struct.
/// <list type="bullet">
/// <item>Host: every input on an icon (<c>BroadcastChange</c>, after the screen applied it) and every ready check
/// (<c>CheckReadyState</c>, the show's end included) marks the screen changed (<see cref="ControllerViews.MarkChanged"/>); every
/// feedback it plays (<c>PlayFeedback_Internal</c>) goes as a step; so does its rules page coming up or going
/// (<c>RefreshAnimNow</c>, which the page's open and close call: <see cref="ControllerViews.RulesLook"/>).</item>
/// <item>Friend: a screen shown for the host binds at its <c>InitData</c>; its icons register no input of this PC's
/// (<c>RegisterInputs</c> skipped) and restore none of this PC's remembered sides and positions
/// (<c>RestoreAssignationData</c> skipped); they're painted in the host's colours (<c>RefreshPlayerColor</c>) and take the
/// host's control flags (Input/VirtualPadCompat's <c>RefreshControllerTypeState</c> postfix). While following, any icon's
/// change from this PC's input is refused (<c>BroadcastChange</c>): only it reaches the exits that move skater control.</item>
/// </list>
/// The screen shows for real on a friend only when every friend-side patch went in (<see cref="ControllersReady"/>).
/// </summary>
internal static partial class MenuPatches
{
    /// <summary>True when every patch the real controller screen needs on a friend went in (otherwise it shows as text).</summary>
    public static bool ControllersReady { get; private set; }

    /// <summary>The controller screen's patches; <paramref name="shows"/> and <paramref name="closes"/>: the menus' own open and close patches went in.</summary>
    private static void InstallControllers(HarmonyLib.Harmony harmony, List<string> failed, bool shows, bool closes)
    {
        bool ready = Patch(harmony, failed, typeof(ControllerAssignmentPlayer), "BroadcastChange", prefix: nameof(ControllerInputPrefix), postfix: nameof(ControllerChangedPostfix));
        ready &= Patch(harmony, failed, typeof(ControllerAssignmentMenu), "InitData", postfix: nameof(InitDataPostfix));
        ready &= Patch(harmony, failed, typeof(ControllerAssignmentPlayer), "RegisterInputs", prefix: nameof(ControllerRegisterPrefix));
        ready &= Patch(harmony, failed, typeof(ControllerAssignmentMenu), "RestoreAssignationData", prefix: nameof(ControllerRestorePrefix));
        ready &= Patch(harmony, failed, typeof(ControllerAssignmentPlayer), "RefreshPlayerColor", postfix: nameof(ControllerColourPostfix));
        ControllersReady = ready && shows && closes;

        // Host only: the screen's settled state and its feedback.
        Patch(harmony, failed, typeof(ControllerAssignmentMenu), "CheckReadyState", postfix: nameof(ControllerReadyPostfix));
        Patch(harmony, failed, typeof(ControllerAssignmentMenu), "PlayFeedback_Internal", postfix: nameof(ControllerFeedbackPostfix));
        Patch(harmony, failed, typeof(ControllerAssignmentMenu), "RefreshAnimNow", postfix: nameof(ControllerLookPostfix));
    }

    /// <summary>
    /// A controller screen icon's change from this PC's input (a press, a stick, a controller added or removed): refused
    /// while this PC follows a host (logged once), so it never reaches the screen's exits, which move skater control. A
    /// screen shown for the host takes no input of this PC's at all; this is the backstop.
    /// </summary>
    private static bool ControllerInputPrefix(ControllerAssignmentPlayer __instance, ControllerAssignmentPlayer.EChange type)
    {
        try
        {
            if (FollowingHost || ControllerViews.Ours(__instance.Parent))
            {
                ReportOnce("controller input", $"Menus: refused a controller screen input on this PC ({type} on P{__instance.GamePlayerId + 1}): the host's screen decides, and " +
                    "this PC's own input never moves an icon while it follows (further ones are only refused).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("controller input block", ex);
        }

        return true;
    }

    /// <summary>The host's controller screen applied an icon's change: it's read and sent shortly (<see cref="ControllerViews.HostTick"/>).</summary>
    private static void ControllerChangedPostfix(ControllerAssignmentPlayer __instance, ControllerAssignmentPlayer.EChange type)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                ControllerViews.MarkChanged(__instance.Parent, $"{type} on P{__instance.GamePlayerId + 1}");
            }
        }
        catch (Exception ex)
        {
            Failed("controller change", ex);
        }
    }

    /// <summary>The host's controller screen checked who's ready (its show's end, or after a change): it's read and sent shortly.</summary>
    private static void ControllerReadyPostfix(ControllerAssignmentMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                ControllerViews.MarkChanged(__instance, "its ready check");
            }
        }
        catch (Exception ex)
        {
            Failed("controller ready check", ex);
        }
    }

    /// <summary>The host's controller screen refreshed its look: its rules page may have come up or gone (<see cref="ControllerViews.RulesLook"/>).</summary>
    private static void ControllerLookPostfix(ControllerAssignmentMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                ControllerViews.RulesLook(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("controller look", ex);
        }
    }

    /// <summary>The host's controller screen played a feedback: it goes to friends as a step (<see cref="ControllerViews.HostFeedback"/>).</summary>
    private static void ControllerFeedbackPostfix(ControllerAssignmentMenu __instance, Il2CppSystem.Collections.Generic.IReadOnlyList<ControllerAssignmentFeedbackElement> elements,
        EControllerAssignmentFeedback type, bool isNegativeFeedback, ControllerAssignmentFeedbackElement.EOptions options)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends && elements != null)
            {
                ControllerViews.HostFeedback(views.Host, __instance, elements.Pointer, (int)type, isNegativeFeedback, (int)options);
            }
        }
        catch (Exception ex)
        {
            Failed("controller feedback", ex);
        }
    }

    /// <summary>
    /// An icon of a screen shown for the host registers no input of this PC's: skipped, with any delegates a kept copy still
    /// had removed (<see cref="ControllerViews.CutOff"/>). No Rewired delegates on this PC's players, no write to this PC's
    /// static player colours.
    /// </summary>
    private static bool ControllerRegisterPrefix(ControllerAssignmentPlayer __instance)
    {
        try
        {
            if (ControllerViews.Ours(__instance.Parent))
            {
                ControllerViews.CutOff(__instance);
                ReportOnce("controller cut off", "Menus: the controller screen here shows the host's players; this PC's own input is cut off from its icons.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("controller input cut-off", ex);
        }

        return true;
    }

    /// <summary>A screen shown for the host doesn't restore this PC's remembered sides and positions: it shows the host's.</summary>
    private static bool ControllerRestorePrefix(ControllerAssignmentMenu __instance)
    {
        try
        {
            if (ControllerViews.Ours(__instance))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("controller restore block", ex);
        }

        return true;
    }

    /// <summary>An icon of a screen shown for the host is painted in the host's colour (after the game's own paint, from this PC's colours).</summary>
    private static void ControllerColourPostfix(ControllerAssignmentPlayer __instance)
    {
        try
        {
            if (ControllerViews.Ours(__instance.Parent))
            {
                ControllerViews.Paint(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("controller colour", ex);
        }
    }
}
#endif
