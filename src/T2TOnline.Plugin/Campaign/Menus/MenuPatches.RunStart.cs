#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppRogue.BenchSnapshots;
using Il2CppRogue.Rewards;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The run-start menus' patches (Hooks/hooks.txt, "v1: the run start"): all
/// on unfolded, non-generic methods of <c>OldSquadMenu</c> and <c>ProfileData</c>.
/// <list type="bullet">
/// <item>Host: what each of the old squad menu's four rolls returned (postfixes on its generators; the host's game rolls as
/// ever), and each pick (postfixes on its own click handlers and on its exit), for friends
/// (<see cref="RunStartCapture"/>).</item>
/// <item>Friend, for the view it opens (or any old squad menu while it follows a host, or one of its views that opened
/// late): the four rolls never run (the host's offers come instead, or nothing), and the menu's exit runs only at the
/// host's exit step, on the view's menu (it applies nothing: <c>applyChoice = false</c>; it plays its own animations, hide
/// and transition). The apply is refused besides (a backstop to <c>applyChoice = false</c>).</item>
/// <item>Friend, whenever it follows a host: a benchwarmer reveal is never written into this player's profile
/// (<c>ProfileData.UnlockBenchwarmer</c>: the host's lineup menu reveals the host's; this player gets their own later).</item>
/// <item>The lineup with benchwarmers (<c>BenchwarmerMenu</c>, <c>SkaterCard.ShowUnlock</c> and the snapshot service's hot and
/// cold reads; <see cref="BenchViews"/>): host, its loaded pool, swaps and toggles go out; friend, its view's random fill,
/// confirm and way back are refused, its pool and hot and cold are the host's, and a reveal of a benchwarmer the host shows
/// locked isn't played.</item>
/// </list>
/// The real old squad menu is shown on a friend's PC only when every friend-side patch went in (<see cref="RunStartReady"/>),
/// the lineup likewise (<see cref="BenchReady"/>).
/// </summary>
internal static partial class MenuPatches
{
    private static int _benchwarmersKept;

    /// <summary>True when every patch the real old squad menu needs on a friend went in (otherwise it shows as text).</summary>
    public static bool RunStartReady { get; private set; }

    /// <summary>The run-start menus' patches; <paramref name="shows"/> and <paramref name="closes"/>: the menus' own open and close patches went in.</summary>
    private static void InstallRunStart(HarmonyLib.Harmony harmony, List<string> failed, bool shows, bool closes)
    {
        // Left over from a session that ended mid-write, or a crash: the run-start preview files.
        RunPreview.CleanUp("start-up");

        // A friend's own opens bind to the host's menu; the rolls are the host's (host: read; friend: replaced).
        bool ready = Patch(harmony, failed, typeof(OldSquadMenu), "InitData", postfix: nameof(InitDataPostfix));
        ready &= Patch(harmony, failed, typeof(OldSquadMenu), "GenerateSuperStarSkaters", prefix: nameof(SuperStarsPrefix), postfix: nameof(SuperStarsPostfix));
        ready &= Patch(harmony, failed, typeof(OldSquadMenu), "GeneratePowerUpRewards", prefix: nameof(PowerUpsPrefix), postfix: nameof(PowerUpsPostfix));
        ready &= Patch(harmony, failed, typeof(OldSquadMenu), "GenerateRandomSkaters", prefix: nameof(RandomSkatersPrefix), postfix: nameof(RandomSkatersPostfix));
        ready &= Patch(harmony, failed, typeof(OldSquadMenu), "GenerateRelics", prefix: nameof(RelicsPrefix), postfix: nameof(RelicsPostfix));
        ready &= Patch(harmony, failed, typeof(OldSquadMenu), "ApplyAndExit", prefix: nameof(OldSquadExitPrefix), postfix: nameof(OldSquadExitPostfix));
        RunStartReady = ready && shows && closes;

        // The host's picks (its own handlers ran; a friend's run them only through its view, never from input).
        Patch(harmony, failed, typeof(OldSquadMenu), "OnClickPowerUp", postfix: nameof(OldSquadPowerUpPostfix));
        Patch(harmony, failed, typeof(OldSquadMenu), "OnClickSuperStar", postfix: nameof(OldSquadSuperStarPostfix));
        Patch(harmony, failed, typeof(OldSquadMenu), "OnClickRelic", postfix: nameof(OldSquadRelicPostfix));

        // Backstops on a friend: no apply, and no benchwarmer reveal written into this profile.
        Patch(harmony, failed, typeof(OldSquadMenu), "ApplyCurrentSelection", prefix: nameof(OldSquadApplyPrefix));
        Patch(harmony, failed, typeof(Il2Cpp.ProfileData), "UnlockBenchwarmer", prefix: nameof(UnlockBenchwarmerPrefix));

        // The lineup with benchwarmers (BenchViews, FriendMenus.Bench.cs): host, its loaded pool and each swap and toggle go
        // out; friend, its view binds, takes the host's benchwarmers once its own are loaded, never rolls its random fill,
        // never confirms or backs out, shows the host's hot and cold, and plays no reveal of a benchwarmer the host shows
        // locked. Shown for real only when every friend-side patch went in.
        bool bench = Patch(harmony, failed, typeof(BenchwarmerMenu), "InitData", postfix: nameof(InitDataPostfix));
        bench &= Patch(harmony, failed, typeof(BenchwarmerMenu), "LoadCurrentTeam", postfix: nameof(BenchLoadedPostfix));
        bench &= Patch(harmony, failed, typeof(BenchwarmerMenu), "AutoSelect", prefix: nameof(BenchAutoSelectPrefix));
        bench &= Patch(harmony, failed, typeof(BenchwarmerMenu), "Confirm", prefix: nameof(BenchConfirmPrefix));
        bench &= Patch(harmony, failed, typeof(BenchwarmerMenu), "Back", prefix: nameof(BenchBackPrefix));
        bench &= Patch(harmony, failed, typeof(SkaterCard), "ShowUnlock", prefix: nameof(BenchRevealPrefix));
        bench &= Patch(harmony, failed, typeof(BenchSnapshotService), "IsHotBenchwarmer", prefix: nameof(BenchHotPrefix));
        bench &= Patch(harmony, failed, typeof(BenchSnapshotService), "IsColdBenchwarmer", prefix: nameof(BenchColdPrefix));
        BenchReady = bench && shows && closes;
        Patch(harmony, failed, typeof(BenchwarmerMenu), "SwapPlayerCards", postfix: nameof(BenchChangedPostfix));
        Patch(harmony, failed, typeof(BenchwarmerMenu), "OnTogglePlayerCard", postfix: nameof(BenchChangedPostfix));
    }

    /// <summary>True when every patch the real lineup with benchwarmers needs on a friend went in (otherwise it shows as text).</summary>
    public static bool BenchReady { get; private set; }

    /// <summary>
    /// The lineup menu loaded its team and benchwarmers (inside its Show): the host's go out with its run; a friend's view of
    /// the host's takes the host's benchwarmers in place of this profile's.
    /// </summary>
    private static void BenchLoadedPostfix(BenchwarmerMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                RunStartCapture.BenchLoaded(__instance);
            }

            Friend?.BenchLoaded(__instance);
        }
        catch (Exception ex)
        {
            Failed("lineup load", ex);
        }
    }

    /// <summary>The host's lineup swapped or toggled a card: its cards go out now.</summary>
    private static void BenchChangedPostfix(BenchwarmerMenu __instance)
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

    /// <summary>The random fill of empty starter seats in the lineup this PC shows for the host: refused (the host's fill comes as its cards).</summary>
    private static bool BenchAutoSelectPrefix(BenchwarmerMenu __instance) =>
        RefuseOurs(__instance, "lineup fill", "Menus: the lineup's random fill was refused on this PC (the host's game fills its lineup; its cards are followed here).");

    /// <summary>The confirm of the lineup this PC shows for the host: refused (it would apply its roster; the host's close closes it).</summary>
    private static bool BenchConfirmPrefix(BenchwarmerMenu __instance) =>
        RefuseOurs(__instance, "lineup confirm", "Menus: a confirm in the host's lineup shown here was refused (the host's game confirms; it closes with the host's).");

    /// <summary>The way back out of the lineup this PC shows for the host: refused (the host's game decides).</summary>
    private static bool BenchBackPrefix(BenchwarmerMenu __instance) =>
        RefuseOurs(__instance, "lineup back", "Menus: a way back out of the host's lineup shown here was refused (the host's game decides).");

    /// <summary>
    /// A benchwarmer card's unlock reveal: refused on the lineup this PC shows for the host when the host's card shows that
    /// benchwarmer locked (this profile's own reveal would show it unlocked here).
    /// </summary>
    private static bool BenchRevealPrefix(SkaterCard __instance)
    {
        try
        {
            if (Friend?.BenchRevealRefused(__instance) == true)
            {
                ReportOnce("lineup reveal", "Menus: this PC's own reveal of a benchwarmer the host's lineup shows locked wasn't played (its card stays locked, as on the host's screen).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("lineup reveal", ex);
        }

        return true;
    }

    /// <summary>A benchwarmer's hot status: the host's for one this PC built for the host's lineup; this profile's otherwise.</summary>
    private static bool BenchHotPrefix(ForwardData forward, ref bool __result) => BenchStatus(forward, hot: true, ref __result);

    /// <summary>A benchwarmer's cold status: the host's for one this PC built for the host's lineup; this profile's otherwise.</summary>
    private static bool BenchColdPrefix(ForwardData forward, ref bool __result) => BenchStatus(forward, hot: false, ref __result);

    private static bool BenchStatus(ForwardData forward, bool hot, ref bool __result)
    {
        try
        {
            if (forward != null && Friend?.BenchSkater(forward) is { } host)
            {
                __result = hot ? host.Hot : host.Cold;
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("lineup hot or cold", ex);
        }

        return true;
    }

    /// <summary>The old squad menu rolls its superstars: a friend's never does (the host's come instead); the host's goes out.</summary>
    private static bool SuperStarsPrefix(OldSquadMenu __instance, ref Il2CppList.List<ForwardData> __result) =>
        Rolled(__instance, RunStartCapture.Offer.SuperStars, ref __result);

    /// <summary>The old school draft rolls its random skaters (its roll triggers their pickups): never on a friend.</summary>
    private static bool RandomSkatersPrefix(OldSquadMenu __instance, ref Il2CppList.List<ForwardData> __result) =>
        Rolled(__instance, RunStartCapture.Offer.RandomSkaters, ref __result);

    /// <summary>The old school draft rolls its power-ups: never on a friend.</summary>
    private static bool PowerUpsPrefix(OldSquadMenu __instance, ref Il2CppList.List<PowerUpReward> __result)
    {
        FriendMenus? friend = Friend;
        if (OwnOldSquad(__instance, friend))
        {
            return true;
        }

        try
        {
            __result = friend?.OldSquadPowerUps(__instance) ?? new Il2CppList.List<PowerUpReward>();
        }
        catch (Exception ex)
        {
            Failed("old squad roll", ex);
            __result = new Il2CppList.List<PowerUpReward>();
        }

        return false;
    }

    /// <summary>The old school draft rolls its relics: never on a friend.</summary>
    private static bool RelicsPrefix(OldSquadMenu __instance, ref Il2CppList.List<RelicReward> __result)
    {
        FriendMenus? friend = Friend;
        if (OwnOldSquad(__instance, friend))
        {
            return true;
        }

        try
        {
            __result = friend?.OldSquadRelics(__instance) ?? new Il2CppList.List<RelicReward>();
        }
        catch (Exception ex)
        {
            Failed("old squad roll", ex);
            __result = new Il2CppList.List<RelicReward>();
        }

        return false;
    }

    /// <summary>
    /// A skater roll on a friend (following, even with the views switched off, or a copy of the host's that opened late):
    /// the host's skaters for the view's menu, none for any other. Never the game's own roll there, whatever fails.
    /// </summary>
    private static bool Rolled(OldSquadMenu menu, RunStartCapture.Offer offer, ref Il2CppList.List<ForwardData> __result)
    {
        FriendMenus? friend = Friend;
        if (OwnOldSquad(menu, friend))
        {
            return true;
        }

        try
        {
            __result = friend?.OldSquadSkaters(menu, offer) ?? new Il2CppList.List<ForwardData>();
        }
        catch (Exception ex)
        {
            Failed("old squad roll", ex);
            __result = new Il2CppList.List<ForwardData>();
        }

        return false;
    }

    /// <summary>
    /// True when an old squad menu's rolls and exit are this game's own: this PC follows no host, and the menu isn't a copy
    /// of the host's that opened late (after a session, a request that timed out can wait up to 30 s; the friend's side is
    /// still given then). Never throws: a failure counts, and the menu is left to the game.
    /// </summary>
    private static bool OwnOldSquad(OldSquadMenu menu, FriendMenus? friend)
    {
        if (FollowingHost)
        {
            return false;
        }

        try
        {
            return friend == null || !friend.Shows(menu);
        }
        catch (Exception ex)
        {
            Failed("old squad owner", ex);
            return true;
        }
    }

    private static void SuperStarsPostfix(OldSquadMenu __instance, Il2CppList.List<ForwardData> __result) => Offered(__instance, RunStartCapture.Offer.SuperStars, __result);

    private static void RandomSkatersPostfix(OldSquadMenu __instance, Il2CppList.List<ForwardData> __result) => Offered(__instance, RunStartCapture.Offer.RandomSkaters, __result);

    private static void PowerUpsPostfix(OldSquadMenu __instance, Il2CppList.List<PowerUpReward> __result) => Offered(__instance, RunStartCapture.Offer.PowerUps, __result);

    private static void RelicsPostfix(OldSquadMenu __instance, Il2CppList.List<RelicReward> __result) => Offered(__instance, RunStartCapture.Offer.Relics, __result);

    /// <summary>Host: one of the old squad menu's rolls returned (its lists go to friends with its run once the last is in).</summary>
    private static void Offered(OldSquadMenu menu, RunStartCapture.Offer offer, Il2CppSystem.Object? list)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                RunStartCapture.Offered(menu, offer, list);
            }
        }
        catch (Exception ex)
        {
            Failed("old squad offers", ex);
        }
    }

    /// <summary>
    /// The old squad menu applies its picks and leaves. On a friend, only the view's menu at the host's exit step runs it: with
    /// <c>applyChoice = false</c> it applies nothing (its apply returns at once, and is refused besides), and it plays its animations,
    /// final sound, hide and transition as on the host's screen. Any other call is refused: the view's pick plays here and its menu
    /// closes when the host's does.
    /// </summary>
    private static bool OldSquadExitPrefix(OldSquadMenu __instance)
    {
        FriendMenus? friend = Friend;
        if (OwnOldSquad(__instance, friend))
        {
            return true;
        }

        try
        {
            if (friend != null)
            {
                if (friend.OldSquadExit(__instance))
                {
                    return true;
                }
            }
            else
            {
                __instance.m_IsExiting = true;
            }
        }
        catch (Exception ex)
        {
            Failed("old squad exit", ex);
        }

        return false;
    }

    /// <summary>
    /// Host: the old squad menu applied its picks and is leaving. Its picks go first: in the superstar pick its exit runs
    /// inside the superstar's own click handler (<c>OnClickSuperStar</c>), so this postfix runs before that
    /// handler's, and a friend that got the exit first would play it and skip the pick. A pick already sent isn't sent again.
    /// </summary>
    private static void OldSquadExitPostfix(OldSquadMenu __instance)
    {
        try
        {
            if (Views is not { Hosting: true, HasFriends: true })
            {
                return;
            }

            bool draft = (int)__instance.m_Mode == RunPreviewSet.FullSequence;
            if (draft)
            {
                RunStartCapture.Picked(__instance, RunStartCapture.PowerUpStep, __instance.m_CurrentPowerUpChoice);
            }

            RunStartCapture.Picked(__instance, RunStartCapture.SuperStarStep, __instance.m_CurrentSkaterChoice);
            if (draft)
            {
                RunStartCapture.Picked(__instance, RunStartCapture.RelicStep, __instance.m_CurrentRelicChoice);
            }

            RunStartCapture.Picked(__instance, RunStartCapture.ExitStep, Math.Max(0, __instance.m_CurrentSkaterChoice));
        }
        catch (Exception ex)
        {
            Failed("old squad pick", ex);
        }
    }

    private static void OldSquadPowerUpPostfix(OldSquadMenu __instance, AbstractNavigable nav) => Picked(__instance, RunStartCapture.PowerUpStep, () => __instance.m_CurrentPowerUpChoice);

    private static void OldSquadSuperStarPostfix(OldSquadMenu __instance, AbstractNavigable nav) => Picked(__instance, RunStartCapture.SuperStarStep, () => __instance.m_CurrentSkaterChoice);

    private static void OldSquadRelicPostfix(OldSquadMenu __instance, AbstractNavigable nav) => Picked(__instance, RunStartCapture.RelicStep, () => __instance.m_CurrentRelicChoice);

    /// <summary>Host: a pick in its old squad menu goes to friends (the menu's own handler took it).</summary>
    private static void Picked(OldSquadMenu menu, string step, Func<int> index)
    {
        try
        {
            if (Views is { Hosting: true, HasFriends: true })
            {
                RunStartCapture.Picked(menu, step, index());
            }
        }
        catch (Exception ex)
        {
            Failed("old squad pick", ex);
        }
    }

    /// <summary>A friend's old squad menu never applies a pick to the run (a backstop: its data says not to, and its exit is refused).</summary>
    private static bool OldSquadApplyPrefix() => RefuseEvent("old squad apply", "Menus: the old squad menu's apply was refused on this PC (the host's game adds the picks; its run comes with its next save).");

    /// <summary>
    /// A benchwarmer reveal written into this player's profile: never while following a host (the host's lineup menu reveals
    /// the host's benchwarmers; this player's own menu reveals theirs, later, in their own run).
    /// </summary>
    private static bool UnlockBenchwarmerPrefix(string skaterId)
    {
        try
        {
            if (FollowingHost)
            {
                if (++_benchwarmersKept <= 5)
                {
                    Diagnostics.ModLog.Info($"Menus: kept \"{Core.Diagnostics.SafeText.Clean(skaterId, 48)}\" out of this profile's revealed benchwarmers " +
                        "(it's the host's reveal; this player gets their own later).");
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("benchwarmer reveal block", ex);
            return !FollowingHost;
        }

        return true;
    }
}
#endif
