#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppShipmate.Porting;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.NativeLobby;

namespace T2TOnline.Plugin.Online;

/// <summary>A shared moment this PC's game earned while hosting, with the play it came from (<see cref="AchievementMirror.TryTakeEarned"/>).</summary>
/// <param name="Name">The achievement's Steam API name.</param>
/// <param name="Match">The host's newest loaded match when it was earned (<see cref="MatchSetup.LoadedSerial"/>; 0: none yet).</param>
/// <param name="RunShared">True when the host's game played the shared co-op run when it was earned (D48).</param>
internal readonly record struct EarnedMoment(string Name, int Match, bool RunShared);

/// <summary>
/// Achievements for everyone in the session (D34). Every unlock in the game goes through
/// <c>PlatformAchievements.Unlock(EPlatformAchievement)</c>, whose achievement's <c>mName</c> is its Steam API name.
/// <list type="bullet">
/// <item>Moments (Core's <see cref="SharedMoments"/>): the game unlocks these straight from a match or run event (a mercy
/// win, every skater scoring, an elite or act 2's boss won, the run won, a headshot redirect, Gawain evolving), calling
/// <c>Unlock</c> every time it happens, owned or not. On the host, a postfix on <c>Unlock</c> queues them
/// (<see cref="TryTakeEarned"/>), with the match they came from and whether the run is the shared co-op run, for the
/// session to send to the friends who shared that play (<see cref="SourceOf"/>; <c>Core.Session.AchievementLedger</c>):
/// whoever earns one, everyone playing together gets it, and nobody gets one from play they didn't share (a match they
/// never watched, the host's solo run; D48). All play runs on the host's game, and its checks count the human team, not
/// one player. A friend's game can't earn these itself: its checks listen to game events client mode detaches, and read the
/// live match only the host's game plays.</item>
/// <item>Progress: every other achievement counts something on a player's own profile (a lifetime of hat tricks, shop
/// collections, bench quests, and the unlock registry's, which follow the profile's unlocks). Copying the host's
/// completion would hand a friend "10 hat tricks" for one, so they're never sent: each player earns them from their own
/// progress, which the campaign's shared progress advances when playing together. Each one's first unlock in a game's life is
/// logged while online, on the host and on a friend, with what Steam made of it.</item>
/// <item>Friend: <see cref="UnlockHere"/> finds a moment by name in the game's own list and unlocks it through the
/// game's own call, on the friend's Steam account.</item>
/// <item>While the developer's check tour runs (<see cref="TourHolds.On"/>), every unlock is held back: the prefix skips
/// the game's call, so Steam is never told (the game's only path to Steam's achievements and stats is this call). The patch
/// goes in whenever a tour can run, online or not.</item>
/// </list>
/// Nothing here throws into the game.
/// </summary>
internal static class AchievementMirror
{
    private const int MaxPending = 64;
    private static readonly HashSet<string> KeptLogged = new(StringComparer.Ordinal);
    private static readonly Queue<EarnedMoment> Pending = new();
    private static bool _installed;
    private static bool _unlockingHere;
    private static int _failures;

    /// <summary>What the prefix found, for the postfix.</summary>
    internal enum UnlockCall
    {
        /// <summary>The game's call went on; Steam's state before it wasn't asked (or showed it locked).</summary>
        Plain,

        /// <summary>The game's call went on; Steam showed it unlocked before.</summary>
        OwnedBefore,

        /// <summary>Held back: a check tour runs, and the game's call was skipped.</summary>
        HeldBack,
    }

    /// <summary>True once <c>Unlock</c> is patched: a check tour runs only then (its unlocks would otherwise reach Steam).</summary>
    public static bool Installed => _installed;

    /// <summary>Host: true while hosting with sharing on (set by <see cref="OnlineSession"/>).</summary>
    public static Func<bool>? Sharing { get; set; }

    /// <summary>Friend: true while following a host (set by <see cref="OnlineSession"/>): this game's own unlocks are logged then too.</summary>
    public static Func<bool>? Following { get; set; }

    /// <summary>
    /// Host: the next shared moment this PC's game unlocked (or earned again) while hosting. Queued by the patch, taken
    /// by the session's own update, so nothing session-level runs inside the game's achievement code.
    /// </summary>
    public static bool TryTakeEarned(out EarnedMoment moment) => Pending.TryDequeue(out moment);

    /// <summary>What a shared moment comes from (<see cref="SharedMoments.SourceOf"/>).</summary>
    public static MomentSource SourceOf(string name) => SharedMoments.SourceOf(name);

    /// <summary>
    /// Patches <c>Unlock</c>, once: a prefix holds every unlock back while a check tour runs and otherwise notes whether an
    /// achievement about to be logged was this player's already, a postfix queues or logs it. False, logged, when it can't.
    /// </summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return true;
        }

        try
        {
            PatchUnlock(harmony);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Achievements: couldn't patch PlatformAchievements.Unlock; friends won't get the session's achievements", ex);
            return false;
        }
    }

    /// <summary>
    /// With online off but the runtime up (an untested build's offline tools) and the developer tools on, so a check tour can
    /// run: the patch goes in for its hold alone (<see cref="Install"/>). False, logged, when it can't: then no tour runs.
    /// </summary>
    public static bool InstallForTours(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return true;
        }

        try
        {
            PatchUnlock(harmony);
            ModLog.Info("Achievements: the check tours' hold is in (PlatformAchievements.Unlock patched; online is off, so nothing is shared).");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Achievements: the check tours' hold couldn't be installed ({ModLog.SafeErrorText(ex)}); no check tour runs this session.");
            return false;
        }
    }

    /// <summary>The patch on <c>Unlock</c> (a bool prefix and a postfix). Throws what Harmony throws.</summary>
    private static void PatchUnlock(HarmonyLib.Harmony harmony)
    {
        MethodInfo unlock = AccessTools.Method(typeof(PlatformAchievements), nameof(PlatformAchievements.Unlock));
        harmony.Patch(unlock, prefix: new HarmonyMethod(typeof(AchievementMirror), nameof(UnlockPrefix)),
            postfix: new HarmonyMethod(typeof(AchievementMirror), nameof(UnlockPostfix)));
        _installed = true;
    }

    /// <summary>
    /// Friend: unlocks a moment the host's game earned on this PC's Steam account, through the game's own call. Returns
    /// what happened, for the log: "unlocked", "already yours", or why not.
    /// </summary>
    public static string UnlockHere(string name)
    {
        EPlatformAchievement? achievement;
        if (!_installed)
        {
            return "not done (the mod's achievement patch isn't in)";
        }

        if (!SharedMoments.Contains(name))
        {
            return "not shared (it counts progress, so each player earns it from their own)";
        }

        try
        {
            achievement = Find(name);
        }
        catch (Exception ex)
        {
            return $"failed to look it up ({ModLog.SafeErrorText(ex)})";
        }

        if (achievement == null)
        {
            return "not done (this game has no achievement by that name)";
        }

        _unlockingHere = true;
        try
        {
            if (PlatformAchievements.IsUnlocked(achievement))
            {
                return "already yours";
            }

            PlatformAchievements.Unlock(achievement);
            return TourHolds.On ? "held back (the check tour runs; Steam isn't told)"
                : PlatformAchievements.IsUnlocked(achievement) ? "unlocked" : "asked Steam to unlock it (Steam hasn't confirmed yet)";
        }
        catch (Exception ex)
        {
            return $"failed ({ModLog.SafeErrorText(ex)})";
        }
        finally
        {
            _unlockingHere = false;
        }
    }

    /// <summary>
    /// What Steam made of an unlock the game just asked for (<paramref name="ownedBefore"/>: Steam showed it unlocked before
    /// the call, so nothing popped). Not every unlock the game reports is a Steam achievement: the first squads a profile
    /// unlocks (16-bit, Tchel, Violence) aren't in the game's Steam schema, so Steam shows nothing for them.
    /// </summary>
    private static string SteamSays(EPlatformAchievement achievement, bool ownedBefore)
    {
        try
        {
            return ownedBefore ? "it was already unlocked on this Steam account, so Steam showed nothing"
                : PlatformAchievements.IsUnlocked(achievement)
                ? "Steam shows it unlocked"
                : "Steam doesn't show it unlocked: it isn't one of the game's Steam achievements, or Steam hasn't confirmed it";
        }
        catch (Exception ex)
        {
            return $"Steam couldn't be asked: {ex.GetType().Name}";
        }
    }

    /// <summary>The game's achievement by Steam name, from every achievement it knows (its own 26 and the unlock registry's).</summary>
    private static EPlatformAchievement? Find(string name)
    {
        // The 26 built-in achievements are made by AchievementManager.Achievements' static constructor: touch it first.
        _ = AchievementManager.Achievements.MERCY;
        Il2CppSystem.Collections.Generic.List<EPlatformAchievement>? all = EPlatformAchievement.AllAchievements;
        if (all == null)
        {
            return null;
        }

        for (int i = 0; i < all.Count; i++)
        {
            EPlatformAchievement? achievement = all[i];
            if (achievement != null && string.Equals(achievement.mName, name, StringComparison.Ordinal))
            {
                return achievement;
            }
        }

        return null;
    }

    /// <summary>True when the postfix will log this unlock (its first while online in this game's life; not a moment the host sends).</summary>
    private static bool Logs(string name) =>
        !KeptLogged.Contains(name) && (Sharing?.Invoke() == true ? !SharedMoments.Contains(name) : Following?.Invoke() == true);

    private static bool UnlockPrefix(EPlatformAchievement achievement, out UnlockCall __state)
    {
        // A check tour on a player's own PC: the game's call is skipped, so Steam isn't told (a field read first, so nothing
        // that could fail comes before it).
        if (TourHolds.On)
        {
            __state = UnlockCall.HeldBack;
            try
            {
                TourHolds.AchievementHeld(achievement?.mName);
            }
            catch (Exception)
            {
                // only for the count and the line: the unlock stays held back
            }

            return false;
        }

        __state = UnlockCall.Plain;
        try
        {
            if (!_unlockingHere && achievement?.mName is { Length: > 0 } name && Logs(name) && PlatformAchievements.IsUnlocked(achievement))
            {
                __state = UnlockCall.OwnedBefore;
            }
        }
        catch (Exception)
        {
            // only for the log line: the unlock goes on
        }

        return true;
    }

    private static void UnlockPostfix(EPlatformAchievement achievement, UnlockCall __state)
    {
        try
        {
            // A friend's own unlock of a mirrored moment (on the one-PC test, the same game hosts) isn't news.
            if (_unlockingHere || achievement == null || achievement.mName is not { Length: > 0 } achievementName)
            {
                return;
            }

            if (__state == UnlockCall.HeldBack)
            {
                // A check tour's: a shared moment still goes to the tour's one-PC stand-in (whose unlock is held back the same
                // way), so the tour runs the mirror's path; nothing is said of an unlock that didn't happen.
                if (Sharing?.Invoke() == true && SharedMoments.Contains(achievementName) && Pending.Count < MaxPending)
                {
                    Pending.Enqueue(new EarnedMoment(achievementName, MatchSetup.LoadedSerial, RunSync.Instance?.HostOnCoopSlot == true));
                }

                return;
            }

            bool ownedBefore = __state == UnlockCall.OwnedBefore;

            if (Sharing?.Invoke() == true)
            {
                if (SharedMoments.Contains(achievementName))
                {
                    // What it came from is read now (plain fields): by the session's next update the match may have unloaded.
                    if (Pending.Count < MaxPending)
                    {
                        Pending.Enqueue(new EarnedMoment(achievementName, MatchSetup.LoadedSerial, RunSync.Instance?.HostOnCoopSlot == true));
                    }
                }
                else if (KeptLogged.Add(achievementName))
                {
                    ModLog.Info($"Achievements: the game unlocked {achievementName} here ({SteamSays(achievement, ownedBefore)}); it counts progress on this PC's own profile, " +
                        "so friends earn it from theirs (not sent).");
                }
            }
            else if (Following?.Invoke() == true && KeptLogged.Add(achievementName))
            {
                // A friend's own: the unlock registry's (this profile's own unlocks from shared progress), its collections and
                // bench quests. Said once per achievement and game, as on the host, so a play test shows it popped here.
                ModLog.Info($"Achievements: the game unlocked {SafeText.Clean(achievementName, 64)} here ({SteamSays(achievement, ownedBefore)}), from this player's own progress " +
                    $"while following{(SharedMoments.Contains(achievementName) ? " (a shared moment this game reached by itself)" : string.Empty)}.");
            }
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Achievements: couldn't pass an unlock on: {ModLog.SafeErrorText(ex)}");
            }
        }
    }
}
#endif
