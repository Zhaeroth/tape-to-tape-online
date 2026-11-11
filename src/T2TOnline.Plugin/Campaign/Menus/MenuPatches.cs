#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using MelonLoader;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The menu views' patches (Hooks/hooks.txt, "menus as views"): all on non-generic, unfolded
/// methods. They stay installed for the game's life (D38) and do nothing unless this PC hosts (the capture points),
/// follows a host (the friend's binding, blocks and backstops), or still waits for a menu it asked to open that the host
/// no longer shows (closed once it's up, even after the session). Each is guarded: a failure is logged and counted, never
/// thrown into the game, and after <see cref="MaxFailures"/> in one online session the menu views switch themselves off
/// until the next (<see cref="NewSession"/>).
/// </summary>
internal static partial class MenuPatches
{
    private const int MaxFailures = 20;
    private const int MaxFailureLogs = 3;

    private static readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static int _failures;
    private static bool _off;
    private static bool _ticking;

    /// <summary>Installs every patch; adds a line to <paramref name="failed"/> for each that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(failed);
        if (!_ticking)
        {
            // Late menus are closed between sessions too, so this runs from the game's own update, not the session's.
            MelonEvents.OnUpdate.Subscribe(Tick);
            _ticking = true;
        }

        // Capture (host) and close detection (both): every open crosses T2TMenu.Show; every close Hide_Internal.
        bool shows = Patch(harmony, failed, typeof(T2TMenu), "Show", prefix: nameof(ShowPrefix));
        bool closes = Patch(harmony, failed, typeof(AbstractMenu), "Hide_Internal", prefix: nameof(HidePrefix));

        // A friend's own opens bind to the host's menu by the data object handed to InitData (the concrete overrides only).
        foreach (Type menu in new[] { typeof(RelicRewardMenu), typeof(SkaterPowerupRewardMenu), typeof(ChooseMetaMenu), typeof(RunEndMenu) })
        {
            Patch(harmony, failed, menu, "InitData", postfix: nameof(InitDataPostfix));
        }

        // The real in-match menu on a friend: its Show draws with the host's
        // numbers and the end-of-game context, which runs none of the pause or period flow; its profile stats send, resume
        // input and resume, the give-up paths and the game's end-of-period step are refused there. It's shown for real
        // only when every one of these went in. Not patched: OnChangeLineInput, whose Rewired event struct isn't blittable
        // (a detour on it would sit on the host's own pause menu too); a menu of ours never registers it (its
        // RegisterResumeInput is refused), and a following friend's own UI input is locked.
        bool game = Patch(harmony, failed, typeof(GameMenu), "InitData", postfix: nameof(InitDataPostfix));
        game &= Patch(harmony, failed, typeof(GameMenu), "Show", prefix: nameof(GameMenuShowPrefix), postfix: nameof(GameMenuShowPostfix));
        game &= Patch(harmony, failed, typeof(GameMenu), "SendEndOfGameStats", prefix: nameof(EndOfGameStatsPrefix));
        game &= Patch(harmony, failed, typeof(GameMenu), "RegisterResumeInput", prefix: nameof(ResumeInputPrefix));
        game &= Patch(harmony, failed, typeof(GameMenu), "OnResumeClicked", prefix: nameof(ResumeClickPrefix));
        game &= Patch(harmony, failed, typeof(GameMenu), "OnBackInput", prefix: nameof(ResumeBackPrefix));
        game &= Patch(harmony, failed, typeof(GameMenu), "EndGameEarly", prefix: nameof(EndGameEarlyPrefix));
        game &= Patch(harmony, failed, typeof(Il2CppState.EndPeriodHandler), "EndPeriod", prefix: nameof(EndPeriodPrefix));
        GameMenuReady = game && shows && closes;

        // The glossary (display only): a friend binds its copy; the host sends its tab and entry.
        GlossaryReady = Patch(harmony, failed, typeof(GlossaryMenu), "InitData", postfix: nameof(InitDataPostfix)) && shows && closes;
        Patch(harmony, failed, typeof(GameMenu), "SelectTab_Internal", postfix: nameof(GameTabPostfix));
        Patch(harmony, failed, typeof(GlossaryMenu), "SelectTab", postfix: nameof(GlossaryTabPostfix));
        Patch(harmony, failed, typeof(GlossaryMenu), "ShowEntry", postfix: nameof(GlossaryEntryPostfix));

        // The run-end menu (RunEndView): the host's sort goes out; a friend's copy never steps through this PC's own run
        // history or logs the run's end to analytics (backstops: its shortcuts are refused, and history mode skips both).
        Patch(harmony, failed, typeof(RunEndMenu), "RefreshOrder", postfix: nameof(RunEndSortPostfix));
        Patch(harmony, failed, typeof(RunEndMenu), "OnPreviousRun", prefix: nameof(RunEndHistoryStepPrefix));
        Patch(harmony, failed, typeof(RunEndMenu), "OnNextRun", prefix: nameof(RunEndHistoryStepPrefix));
        Patch(harmony, failed, typeof(RunEndMenu), "TriggerAnalyticsEvent", prefix: nameof(RunEndAnalyticsPrefix));

        // The event menus (MenuPatches.Events.cs): the host's new sets of choices go out; a friend's view binds, takes the
        // host's choices in place of the event's roll, and every action and profile write is refused. Events show for real
        // only when every friend-side patch went in.
        InstallEvents(harmony, failed, shows, closes);

        // The other real views (MenuPatches.Misc.cs): prompts, the new-unlocks menu, the lineup, the map's own menu and the
        // Canada curse.
        InstallMisc(harmony, failed, shows, closes);

        // The run-start menus (MenuPatches.RunStart.cs): the old squad menu's rolls and picks go out; a friend's view binds,
        // takes the host's offers in place of its rolls and never applies; no benchwarmer reveal is written on a friend.
        InstallRunStart(harmony, failed, shows, closes);

        // The title's menus (MenuPatches.Title.cs): the unlockables' states and the run history's pages go out; a friend's
        // unlockables take the host's states as they're made.
        InstallTitleScreens(harmony, failed, shows, closes);

        // The controller screen (MenuPatches.Controllers.cs): the host's changes and feedback go out; a friend's view binds,
        // its icons take no input of this PC's and show the host's state.
        InstallControllers(harmony, failed, shows, closes);

        // The brawl offer and its fight controls (MenuPatches.Brawl.cs): the host's fight controls go out at once.
        InstallBrawl(harmony, failed);

        // The menu exits (MenuPatches.Exits.cs): the host's back input, for its closes' reason.
        InstallExits(harmony, failed);

        // The shared cursor, click results and list selectors (host).
        Patch(harmony, failed, typeof(NavigationController), "SetCurrentNavigable", postfix: nameof(SelectionPostfix));
        Patch(harmony, failed, typeof(AbstractNavigable), "ForceClick", postfix: nameof(ClickPostfix));
        Patch(harmony, failed, typeof(CycleList), "SelectIndex", postfix: nameof(ValuePostfix));

        // A shortcut's hold and release (host): "hold for details" shows the keyword boxes, a skater's talents and the like
        // only while it's down; a friend's copy fires the same shortcut's own hold events (DetailsHolds). The friend's lock
        // has its own prefix on this method (FriendUi).
        Patch(harmony, failed, typeof(UIInputShortcutBase), "FireEvent", prefix: nameof(ShortcutEventPrefix), postfix: nameof(ShortcutEventPostfix));

        // A press on a shortcut that can't take it. Not on InputPressedWhileCantTrigger: the game's compiler inlined it in Release
        // (verified in the game's code) and its own copy has no caller, so a patch there would never run.
        Patch(harmony, failed, typeof(UIInputShortcutBase), "Release", prefix: nameof(ShortcutReleasePrefix));

        // Rerolls roll inside the reward menus: the host's new offers go out; a friend's never roll (blocked).
        Patch(harmony, failed, typeof(RelicRewardMenu), "RerollChoicesNow", prefix: nameof(RerollPrefix), postfix: nameof(RerollPostfix));
        Patch(harmony, failed, typeof(SkaterPowerupRewardMenu), "RerollChoicesNow", prefix: nameof(RerollPrefix), postfix: nameof(RerollPostfix));

        // The menus' own steps (host), and the squad menu's commit, refused on a friend as a backstop to DoNotSetChoice.
        Patch(harmony, failed, typeof(SkaterPowerupRewardMenu), "SetToState", postfix: nameof(PowerUpStepPostfix));
        Patch(harmony, failed, typeof(ChooseMetaMenu), "SetState", postfix: nameof(SquadStepPostfix));
        Patch(harmony, failed, typeof(ChooseMetaMenu), "ConfirmSelections", prefix: nameof(ConfirmPrefix));
    }

    /// <summary>True when every patch the real in-match menu needs on a friend went in (otherwise it shows as text).</summary>
    public static bool GameMenuReady { get; private set; }

    /// <summary>True when the patches the real glossary needs on a friend went in.</summary>
    public static bool GlossaryReady { get; private set; }

    /// <summary>A new online session: the patches' failures in the last one don't count against it, and quiet patches act again.</summary>
    public static void NewSession()
    {
        _failures = 0;
        _off = false;
        Failures.Clear();
    }

    private static MenuViews? Views => _off || MenuViews.Instance is not { Off: false } views ? null : views;

    /// <summary>
    /// The friend's side when it follows a host, or still waits for a late menu to close (the views or these patches off or
    /// not: a menu asked for before they went off is still closed once it's up), else null.
    /// </summary>
    private static FriendMenus? Friend =>
        MenuViews.Instance is not { } views ? null
        : (!_off && views.Following && !views.Off) || views.Friend.Expecting ? views.Friend
        : null;

    /// <summary>This PC follows a host: the backstops hold whether the views are on or not.</summary>
    private static bool FollowingHost => MenuViews.Instance?.Following == true;

    /// <summary>Patches one method; false (with a line in <paramref name="failed"/>) when it couldn't go in.</summary>
    private static bool Patch(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, string? prefix = null, string? postfix = null)
    {
        try
        {
            MethodInfo? target = AccessTools.DeclaredMethod(type, method);
            if (target == null)
            {
                failed.Add($"menus: {type.Name}.{method} not found");
                return false;
            }

            harmony.Patch(
                target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(MenuPatches).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(MenuPatches).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
            return true;
        }
        catch (Exception ex)
        {
            failed.Add($"menus: {type.Name}.{method}: {ModLog.SafeErrorText(ex)}");
            return false;
        }
    }

    /// <summary>A menu is showing (fresh or retained): the host tracks it; a friend checks it's one it asked for.</summary>
    private static void ShowPrefix(T2TMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                // A transition the host's menu exit shows is noted for that menu's close (HostTransitions).
                HostTransitions.Shown(__instance);
                views.Host.Showing(__instance, views.HasFriends);
            }

            FriendTransitions.Showing(__instance);
            Friend?.Showing(__instance, FollowingHost);
        }
        catch (Exception ex)
        {
            Failed("show", ex);
        }
    }

    /// <summary>A menu is closing: the host sends the close; a friend notices its game closing a view by itself.</summary>
    private static void HidePrefix(AbstractMenu __instance)
    {
        try
        {
            // A real in-match menu of ours puts back what its open changed (its backdrop above all), however it closes,
            // session or not, views on or off.
            GameMenuViews.Hiding(__instance);
        }
        catch (Exception ex)
        {
            Failed("in-match menu close", ex);
        }

        try
        {
            if (Views is { Hosting: true } views)
            {
                views.Host.Hiding(__instance);
            }

            if (Views is { Following: true } following)
            {
                following.Friend.Hiding(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("close", ex);
        }
    }

    /// <summary>A menu got its data: on a friend, this may be the view it asked to open (or one the host no longer shows).</summary>
    private static void InitDataPostfix(AbstractMenu __instance, Il2CppSystem.Object data)
    {
        try
        {
            // An in-match menu given data is the game's own until it's bound again as one of ours just below.
            GameMenuViews.Unmark(__instance);
            Friend?.Bound(__instance, data);
        }
        catch (Exception ex)
        {
            Failed("data", ex);
        }
    }

    /// <summary>The cursor moved: the host sends it when it's in a menu.</summary>
    private static void SelectionPostfix(NavigationController __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Selected(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("selection", ex);
        }
    }

    /// <summary>
    /// A shortcut is about to fire its trigger: the host notes where it is, as its handlers may close its menu before they
    /// return (its press goes to friends with that close, <see cref="HostMenus.PressStarting"/>).
    /// </summary>
    private static void ShortcutEventPrefix(UIInputShortcutBase __instance, UIInputShortcutBase.EEventType t)
    {
        try
        {
            if (t == UIInputShortcutBase.EEventType.Trigger && Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.PressStarting(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("shortcut press", ex);
        }
    }

    /// <summary>
    /// Host: a shortcut's <c>Release</c>. From a state it can't trigger in, with no hold to end, it's a press on a disabled shortcut
    /// (<see cref="MenuPress.RaisesCantTrigger"/>): when it has a sound in a shared menu, it goes to friends. Any other release is left at once.
    /// </summary>
    private static void ShortcutReleasePrefix(UIInputShortcutBase __instance, bool isFromCantTriggerState)
    {
        if (!isFromCantTriggerState)
        {
            return;
        }

        try
        {
            if (Views is { Hosting: true } views && views.HasFriends &&
                MenuPress.RaisesCantTrigger(isFromCantTriggerState, __instance.m_IsButtonPressed, __instance.m_RequiredHoldTime, __instance.m_AllowHoldLoop) &&
                views.Host.PressedDisabled(__instance))
            {
                ReportOnce("disabled shortcut press", "Menus: a press on a disabled shortcut in a shared menu has a sound here; it goes to friends' copies (further ones aren't logged).");
            }
        }
        catch (Exception ex)
        {
            Failed("disabled shortcut press", ex);
        }
    }

    /// <summary>
    /// A shortcut fired one of its events (<paramref name="t"/>): a hold going down or up in a shared menu goes to friends,
    /// and a trigger's press (for its sound, MenuAudio). Any other event is left at once.
    /// </summary>
    private static void ShortcutEventPostfix(UIInputShortcutBase __instance, UIInputShortcutBase.EEventType t)
    {
        try
        {
            if (t != UIInputShortcutBase.EEventType.Trigger && DetailsHolds.IsHoldEvent(t, out bool held) &&
                Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Held(__instance, held);
            }
            else if (t == UIInputShortcutBase.EEventType.Trigger && Views is { Hosting: true } hosting && hosting.HasFriends)
            {
                hosting.Host.Pressed(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("details hold", ex);
        }
    }

    /// <summary>An element was clicked (its handlers have run and set the result): the host sends the feedback.</summary>
    private static void ClickPostfix(AbstractNavigable __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Clicked(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("click", ex);
        }
    }

    /// <summary>A list selector took a value: the host sends it.</summary>
    private static void ValuePostfix(CycleList __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Value(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("list selector", ex);
        }
    }

    /// <summary>A friend's reward menu never rerolls: the host's new offers come instead (D40).</summary>
    private static bool RerollPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("reroll", "Menus: blocked a reroll on this PC (rolls happen on the host only; its new offers are shown here).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("reroll block", ex);
        }

        return true;
    }

    /// <summary>The host's reward menu rerolled: its new offers go out.</summary>
    private static void RerollPostfix(T2TMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                views.Host.Rerolled(__instance, views.HasFriends);
            }
        }
        catch (Exception ex)
        {
            Failed("reroll", ex);
        }
    }

    /// <summary>The host's power-up menu moved between picking a power-up and picking its skater.</summary>
    private static void PowerUpStepPostfix(SkaterPowerupRewardMenu __instance, bool keepSelectedPowerUp)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                if (__instance.m_CurrentPowerUpChoice >= 0)
                {
                    views.Host.Step(__instance, HostMenus.PowerUpChoice, 0, __instance.m_CurrentPowerUpChoice);
                }

                views.Host.Step(__instance, HostMenus.State, keepSelectedPowerUp ? 1 : 0, (int)__instance.State);
            }
        }
        catch (Exception ex)
        {
            Failed("power-up step", ex);
        }
    }

    /// <summary>The host's squad menu moved between picking a squad and picking the difficulty (or exited).</summary>
    private static void SquadStepPostfix(ChooseMetaMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Step(__instance, HostMenus.State, 0, (int)__instance.m_CurrentState);
            }
        }
        catch (Exception ex)
        {
            Failed("squad step", ex);
        }
    }

    /// <summary>
    /// A friend's squad menu never commits (it would reset the run and write the profile): reported as done, with nothing
    /// written. Its data already says <c>DoNotSetChoice</c>; this is the backstop.
    /// </summary>
    private static bool ConfirmPrefix(ref bool __result)
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("confirm", "Menus: the squad menu's commit was skipped on this PC (the host's game starts the run).");
                __result = true;
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("squad commit block", ex);
        }

        return true;
    }

    /// <summary>
    /// The host's run-end menu sorted its rows (its own sort keys, and its opening): the sort goes out as two state values,
    /// then the cursor's path again (the rows moved under it).
    /// </summary>
    private static void RunEndSortPostfix(RunEndMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Step(__instance, HostMenus.Sort, 0, (int)__instance.m_CurSortType);
                views.Host.Step(__instance, HostMenus.Sort, 1, (int)__instance.m_CurSortOrder);
                views.Host.Reselect();
            }
        }
        catch (Exception ex)
        {
            Failed("run-end sort", ex);
        }
    }

    /// <summary>A friend's run-end menu never steps to another run (it would load this PC's own run history).</summary>
    private static bool RunEndHistoryStepPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("run-end step", "Menus: the run-end menu's step to another run was skipped on this PC (it shows the host's run only).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("run-end step block", ex);
        }

        return true;
    }

    /// <summary>A friend's run-end menu never logs the run's end to analytics (the host's run, and analytics is off anyway).</summary>
    private static bool RunEndAnalyticsPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("run-end analytics", "Menus: the run-end menu's analytics event was skipped on this PC.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("run-end analytics block", ex);
        }

        return true;
    }

    /// <summary>
    /// The in-match menu is drawing itself: one this PC opened for the host gets the host's numbers and the end-of-game
    /// context for this call (<see cref="GameMenuViews.BeforeShow"/>). The game's own menus are left alone.
    /// </summary>
    private static void GameMenuShowPrefix(GameMenu __instance)
    {
        try
        {
            GameMenuViews.BeforeShow(__instance);
        }
        catch (Exception ex)
        {
            Failed("in-match menu show", ex);
        }
    }

    /// <summary>The in-match menu has drawn itself: what its prefix changed is put back, its numbers checked and logged.</summary>
    private static void GameMenuShowPostfix(GameMenu __instance)
    {
        try
        {
            GameMenuViews.AfterShow(__instance);
        }
        catch (Exception ex)
        {
            Failed("in-match menu shown", ex);
        }
    }

    /// <summary>
    /// The end-of-match profile stats (games played and won, then a stats save): never from a menu this PC opened for the
    /// host (its end-of-game context is borrowed), and never on a friend's PC (the host forwards its stat operations, D42).
    /// </summary>
    private static bool EndOfGameStatsPrefix()
    {
        try
        {
            if (FollowingHost || GameMenuViews.InSwappedShow)
            {
                ReportOnce("end-of-game stats", "Menus: the in-match menu's end-of-game stats send was skipped on this PC (the host's game counts the match and forwards its stats).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("end-of-game stats block", ex);
        }

        return true;
    }

    /// <summary>A real in-match menu of ours (or one still opening) takes no resume, confirm or change-line input of its own.</summary>
    private static bool ResumeInputPrefix(GameMenu __instance)
    {
        try
        {
            if (GameMenuViews.IsOurs(__instance) || Friend?.WaitingFor(MenuPayloads.Game) == true)
            {
                ReportOnce("resume input", "Menus: the host's in-match menu shown here takes no resume input of its own (it closes when the host's does).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("resume input block", ex);
        }

        return true;
    }

    /// <summary>
    /// A real in-match menu of ours never resumes the match (that would run its own close: the period's end, the time scale).
    /// <c>ResumeGame</c> is inlined in both its callers and its own copy is never called (checked by listing every call to it in the game's code), so a click and a back press are each refused here.
    /// </summary>
    private static bool ResumeClickPrefix(GameMenu __instance) => !RefusesResume(__instance);

    /// <summary>A back press on a menu of ours: what the game's own code answers while the menu ignores input.</summary>
    private static bool ResumeBackPrefix(GameMenu __instance, ref EBackResult __result)
    {
        if (!RefusesResume(__instance))
        {
            return true;
        }

        __result = EBackResult.SkipRemaining;
        return false;
    }

    /// <summary>True for a menu of ours (said once); never throws.</summary>
    private static bool RefusesResume(GameMenu menu)
    {
        try
        {
            if (GameMenuViews.IsOurs(menu))
            {
                ReportOnce("resume", "Menus: a resume in the host's in-match menu shown here was refused (it closes when the host's does).");
                return true;
            }
        }
        catch (Exception ex)
        {
            Failed("resume block", ex);
        }

        return false;
    }

    /// <summary>Conceding or quitting from the in-match menu never happens on a friend's PC (its prompts are the host's).</summary>
    private static bool EndGameEarlyPrefix(GameMenu __instance)
    {
        try
        {
            if (FollowingHost || GameMenuViews.IsOurs(__instance))
            {
                ReportOnce("end game early", "Menus: ending the match early from the in-match menu was refused on this PC (the host's game decides).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("end game early block", ex);
        }

        return true;
    }

    /// <summary>
    /// The game's step from a period's end (or the match's) to the next period or the end-of-match flow: never on a
    /// friend's PC, whose match is the host's (a backstop: only the in-match menu's own close calls it).
    /// </summary>
    private static bool EndPeriodPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("end period", "Menus: the game's end-of-period step was refused on this PC (the host's game moves the match on).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("end period block", ex);
        }

        return true;
    }

    /// <summary>The host's in-match menu moved to another tab (its index among the menu's tabs).</summary>
    private static void GameTabPostfix(GameMenu __instance, int index)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Step(__instance, HostMenus.Tab, 0, index);
            }
        }
        catch (Exception ex)
        {
            Failed("in-match menu tab", ex);
        }
    }

    /// <summary>The host's glossary moved to another tab.</summary>
    private static void GlossaryTabPostfix(GlossaryMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.Step(__instance, HostMenus.Tab, 0, __instance.m_CurTabIndex);
            }
        }
        catch (Exception ex)
        {
            Failed("glossary tab", ex);
        }
    }

    /// <summary>The host's glossary shows another entry (by its index among the entries ordered by id).</summary>
    private static void GlossaryEntryPostfix(GlossaryMenu __instance, GlossaryEntrySO entry)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends && GameMenuCapture.EntryIndex(__instance, entry) is >= 0 and int index)
            {
                views.Host.Step(__instance, HostMenus.Entry, 0, index);
            }
        }
        catch (Exception ex)
        {
            Failed("glossary entry", ex);
        }
    }

    /// <summary>Every frame of the game: closes menus that opened here after the host closed them (or the session ended).</summary>
    private static void Tick()
    {
        // The host's menu transitions shown here, session or not, patches off or not (FriendTransitions guards itself): one
        // shown late is never left up.
        FriendTransitions.Tick();

        try
        {
            // Session or not, views on or off: a real in-match menu of ours is never left changing this PC's match or
            // holding its backdrop.
            GameMenuViews.Tick();
        }
        catch (Exception ex)
        {
            Failed("in-match menu watch", ex);
        }

        try
        {
            // These patches off or not: a menu asked for before they went off is still closed once it's up.
            MenuViews.Instance?.Tick();
        }
        catch (Exception ex)
        {
            Failed("late menus", ex);
        }
    }

    private static void ReportOnce(string key, string message)
    {
        if (Reported.Add(key))
        {
            ModLog.Info(message);
        }
    }

    /// <summary>A patch failed: logged (the first few per patch), counted, and the views switched off after too many.</summary>
    private static void Failed(string patch, Exception ex)
    {
        int count = Failures.GetValueOrDefault(patch) + 1;
        Failures[patch] = count;
        if (count <= MaxFailureLogs)
        {
            ModLog.Error($"Menus: the {patch} patch failed{(count == MaxFailureLogs ? " (further failures are only counted)" : string.Empty)}", ex);
        }

        _failures++;
        if (_failures == MaxFailures)
        {
            // The views first close what friends see (and what a friend shows), then the patches go quiet, but for closing
            // a menu this PC asked for that opens late (Friend, Tick).
            try
            {
                MenuViews.Instance?.SwitchOff($"{MaxFailures} patch failures");
            }
            catch (Exception switchOff)
            {
                ModLog.Error("Menus: couldn't switch the menu views off cleanly", switchOff);
            }

            _off = true;
        }
    }
}
#endif
