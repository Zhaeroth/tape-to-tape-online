#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The patches of the other menus shown for real on a friend (v1; Hooks/hooks.txt, "v1: prompts,
/// ..."): all on unfolded, non-generic methods.
/// <list type="bullet">
/// <item>Prompts (<see cref="PromptViews"/>): the two customizable prompts' <c>InitData</c> (host: their data goes with the
/// menu; friend: the binding); the friend's own yes, no and cancel clicks and a notice's dismissal on a prompt it shows for
/// the host are refused (backstops: its input is locked).</item>
/// <item>The new-unlocks menu (<see cref="UnlockViews"/>): the host's steps go out; a friend's binding, and its own next
/// press refused.</item>
/// <item>The lineup (<see cref="LineupViews"/>): the host's toggles and swaps go out; a friend's binding, and its leave (the
/// only path that applies a roster) refused.</item>
/// <item>The map's own menu: its way back to the title and its concede refused on a friend.</item>
/// <item>The Canada curse (<see cref="CanadaViews"/>): a friend's binding; its own pick and lineup press refused.</item>
/// </list>
/// Each kind shows for real only when every friend-side patch it needs went in. (The run-start menus' patches are in
/// MenuPatches.RunStart.cs.)
/// </summary>
internal static partial class MenuPatches
{
    /// <summary>True when every patch the real prompts need went in (otherwise they show as text).</summary>
    public static bool PromptReady { get; private set; }

    /// <summary>The other real views' patches; <paramref name="shows"/> and <paramref name="closes"/>: the menus' own open and close patches went in.</summary>
    private static void InstallMisc(HarmonyLib.Harmony harmony, List<string> failed, bool shows, bool closes)
    {
        bool prompts = Patch(harmony, failed, typeof(CustomizableYesNoPrompt), "InitData", postfix: nameof(PromptInitDataPostfix));
        prompts &= Patch(harmony, failed, typeof(CustomizableDismissablePrompt), "InitData", postfix: nameof(PromptInitDataPostfix));
        prompts &= Patch(harmony, failed, typeof(PromptTemplateMenu), "OnYesClick", prefix: nameof(PromptYesPrefix));
        prompts &= Patch(harmony, failed, typeof(PromptTemplateMenu), "OnNoClick", prefix: nameof(PromptNoPrefix));
        prompts &= Patch(harmony, failed, typeof(PromptTemplateMenu), "OnCancelClick", prefix: nameof(PromptCancelPrefix));
        prompts &= Patch(harmony, failed, typeof(DismissablePromptMenu), "PerformDismissal", prefix: nameof(PromptDismissPrefix));
        PromptReady = prompts && shows && closes;

        // The new-unlocks menu: the host's steps; a friend's binding, and its own next press refused.
        bool unlocks = Patch(harmony, failed, typeof(PermanentUnlockMenu), "InitData", postfix: nameof(InitDataPostfix));
        unlocks &= Patch(harmony, failed, typeof(PermanentUnlockMenu), "GoToNextUnlock", postfix: nameof(UnlockStepPostfix));
        unlocks &= Patch(harmony, failed, typeof(PermanentUnlockMenu), "OnNextClicked", prefix: nameof(UnlockNextPrefix));
        UnlocksReady = unlocks && shows && closes;

        // The lineup: the host's toggles and swaps; a friend's binding, and its leave refused (nothing is applied here).
        bool lineup = Patch(harmony, failed, typeof(LineupMenu), "InitData", postfix: nameof(InitDataPostfix));
        lineup &= Patch(harmony, failed, typeof(LineupMenu), "SwapPlayerCards", postfix: nameof(LineupChangedPostfix));
        lineup &= Patch(harmony, failed, typeof(LineupMenu), "OnTogglePlayerCard", postfix: nameof(LineupChangedPostfix));
        lineup &= Patch(harmony, failed, typeof(LineupMenu), "TryLeaveMenu", prefix: nameof(LineupLeavePrefix));
        LineupReady = lineup && shows && closes;

        // The map's own menu: its way back to the title (a run save and a scene load) and its concede refused on a friend.
        bool map = Patch(harmony, failed, typeof(WorldMapMenu), "BackToMainMenu", prefix: nameof(MapMenuTitlePrefix));
        map &= Patch(harmony, failed, typeof(WorldMapMenu), "OnConcedeClicked", prefix: nameof(MapMenuConcedePrefix));
        WorldMapReady = map && shows && closes;

        // The Canada curse: a friend's binding; its own pick and lineup press refused (its exit, started only by the pick,
        // is inlined there: OnClickCard runs ExitSequence's state machine itself).
        bool canada = Patch(harmony, failed, typeof(CanadaCurseMenu), "InitData", postfix: nameof(InitDataPostfix));
        canada &= Patch(harmony, failed, typeof(CanadaCurseMenu), "OnClickCard", prefix: nameof(CanadaPickPrefix));
        canada &= Patch(harmony, failed, typeof(CanadaCurseMenu), "OnLineupPressed", prefix: nameof(CanadaLineupPrefix));
        CanadaReady = canada && shows && closes;
    }

    /// <summary>True when every patch the real Canada curse menu needs went in (otherwise it shows as text).</summary>
    public static bool CanadaReady { get; private set; }

    /// <summary>A curse pick in the Canada curse menu this PC shows for the host: refused (the host's game picks; its exit adds the curse).</summary>
    private static bool CanadaPickPrefix(CanadaCurseMenu __instance) =>
        RefuseOurs(__instance, "Canada pick", "Menus: a curse pick in the host's Canada curse menu shown here was refused (the host's game picks; it closes with the host's).");

    /// <summary>The lineup press in the Canada curse menu this PC shows for the host: refused (the host's lineup comes as its own menu).</summary>
    private static bool CanadaLineupPrefix(CanadaCurseMenu __instance) =>
        RefuseOurs(__instance, "Canada lineup", "Menus: the lineup press in the host's Canada curse menu shown here was refused (the host's lineup shows as its own menu).");

    /// <summary>True when every patch the real lineup menu needs went in (otherwise it shows as text).</summary>
    public static bool LineupReady { get; private set; }

    /// <summary>True when every patch the real map menu needs went in (otherwise it shows as text).</summary>
    public static bool WorldMapReady { get; private set; }

    /// <summary>The host's lineup toggled or swapped a card: its cards go out now.</summary>
    private static void LineupChangedPostfix(LineupMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Changed(__instance, friends: true);
            }
        }
        catch (Exception ex)
        {
            Failed("lineup change", ex);
        }
    }

    /// <summary>A lineup's leave on a following friend: refused (it would apply its roster to this PC's copy of the host's team).</summary>
    private static bool LineupLeavePrefix() =>
        RefuseEvent("lineup leave", "Menus: the lineup menu's leave was refused on this PC (it would apply a lineup to the host's team here; it closes when the host's does).");

    /// <summary>The map menu's way back to the title on a following friend: refused (the host's game decides; it would save the run).</summary>
    private static bool MapMenuTitlePrefix() =>
        RefuseEvent("map menu title", "Menus: the map menu's way back to the title was refused on this PC (the host's game decides).");

    /// <summary>A concede from the map menu this PC shows for the host: refused (the host's game decides).</summary>
    private static bool MapMenuConcedePrefix(WorldMapMenu __instance) =>
        RefuseOurs(__instance, "map menu concede", "Menus: a concede in the host's map menu shown here was refused (the host's game decides).");

    /// <summary>True when every patch the real new-unlocks menu needs went in (otherwise it's a note).</summary>
    public static bool UnlocksReady { get; private set; }

    /// <summary>The host's new-unlocks menu moved to its next unlock (its Show's first move too): the index goes out.</summary>
    private static void UnlockStepPostfix(PermanentUnlockMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                UnlockViews.HostStepped(views.Host, __instance);
            }
        }
        catch (Exception ex)
        {
            Failed("new-unlocks step", ex);
        }
    }

    /// <summary>A next press in the new-unlocks menu this PC shows for the host: refused (it follows the host's).</summary>
    private static bool UnlockNextPrefix(PermanentUnlockMenu __instance) =>
        RefuseOurs(__instance, "new-unlocks next", "Menus: a next press in the new-unlocks menu shown here was refused (it follows the host's steps).");

    /// <summary>
    /// A customizable prompt got its data: the host sends it with the menu (a friend makes the same prompt from it); on a
    /// friend, this may be the view it asked to open.
    /// </summary>
    private static void PromptInitDataPostfix(AbstractMenu __instance, Il2CppSystem.Object data)
    {
        try
        {
            if (Views is { Hosting: true } views && !NativeLobby.Widgets.LobbyPrompts.IsAsking)
            {
                PromptViews.HostPrompt(views.Host, __instance, data, views.HasFriends);
            }

            Friend?.Bound(__instance, data);
        }
        catch (Exception ex)
        {
            Failed("prompt data", ex);
        }
    }

    /// <summary>A yes click in a prompt: named on the host; refused in one this PC shows for the host.</summary>
    private static bool PromptYesPrefix(PromptTemplateMenu __instance) => PromptClick(__instance, "yes");

    /// <summary>A no click in a prompt: named on the host; refused in one this PC shows for the host.</summary>
    private static bool PromptNoPrefix(PromptTemplateMenu __instance) => PromptClick(__instance, "no");

    /// <summary>A cancel click in a prompt: named on the host; refused in one this PC shows for the host.</summary>
    private static bool PromptCancelPrefix(PromptTemplateMenu __instance) => PromptClick(__instance, "cancel");

    /// <summary>A yes, no or cancel click in a prompt: the host's is named in its log; in a prompt this PC shows for the host it's refused (the host's game answers it).</summary>
    private static bool PromptClick(PromptTemplateMenu menu, string answer)
    {
        NameAnswer(menu, answer);
        return RefuseOurs(menu, "prompt answer", "Menus: an answer in the host's prompt shown here was refused (the host's game answers it; it closes with the host's).");
    }

    /// <summary>A notice's dismissal: the host's is named in its log; in one this PC shows for the host it's refused (it closes with the host's).</summary>
    private static bool PromptDismissPrefix(DismissablePromptMenu __instance)
    {
        NameAnswer(__instance, "dismissed");
        return RefuseOurs(__instance, "prompt dismissal", "Menus: a dismissal of the host's notice shown here was refused (it closes with the host's).");
    }

    /// <summary>Host: a prompt on the board was answered, for its log. Never throws.</summary>
    private static void NameAnswer(AbstractMenu menu, string answer)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                views.Host.Answered(menu, answer);
            }
        }
        catch (Exception ex)
        {
            Failed("prompt answer's log line", ex);
        }
    }

    /// <summary>Refuses a menu action on a view this PC shows for the host (logged once); the game's own menus act as ever.</summary>
    private static bool RefuseOurs(AbstractMenu menu, string key, string message)
    {
        try
        {
            if (MenuViews.Instance?.Friend.Shows(menu) == true)
            {
                ReportOnce(key, message);
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed($"{key} block", ex);
        }

        return true;
    }
}
#endif
