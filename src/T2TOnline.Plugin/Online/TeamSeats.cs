#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppScriptableStates;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The game's side of seating every lobby player on a campaign's team (D145): the hook at the campaign's start, and the game's
/// own memory of sides read and written. The session decides who (<c>OnlineSession.TeamSeats.cs</c>, Core's
/// <see cref="TeamSeatBook"/>).
/// </summary>
/// <remarks>
/// The game's two campaign starts (New Run's <c>StartNewRun</c>, after its squad screen, and Continue's <c>TryContinue</c>) and
/// nothing else call <c>TitleScreen.AssignDefaultControllers</c>: it clears the team selection's player lists and, unless the
/// squad is the solo one, adds every player whose side <c>ControllerAssignmentState</c> remembers as the right one (the
/// campaign's team), else the first pad player alone. The memory is two static dictionaries kept for the game's life,
/// written only by the controller screen's save (<c>RememberControllerSide</c>, keyed by game player id: the screen's icon
/// index is the id), never saved to a file. A prefix there writes it as the screen's save would, so the game's own code
/// seats the players; it never skips the game's method.
/// </remarks>
internal static class TeamSeats
{
    private const string SoloSquad = "solo";
    private const int LoggedErrors = 3;

    private static HarmonyLib.Harmony? _harmony;
    private static int _errors;

    /// <summary>True once the start's hook is in.</summary>
    public static bool Installed { get; private set; }

    /// <summary>
    /// The session's seating, called by the hook at a campaign's start (the title whose start it is). Set once, with the session; it writes only while this
    /// PC hosts, so a friend's game and a player who never hosts keep the game's own seating. Null before the session is made.
    /// </summary>
    public static Action<TitleScreen>? Starting { get; set; }

    /// <summary>A friend's game player went for good (its id, their tag): the session puts the id's side back.</summary>
    public static Action<int, string>? Gone { get; set; }

    /// <summary>Puts the start's hook in, once (its own Harmony id, as the session's other late patches). A failure is a warning: the game then seats by its own rule.</summary>
    public static void Install()
    {
        if (_harmony != null)
        {
            return;
        }

        _harmony = new HarmonyLib.Harmony("t2tonline.teamseats");
        var failed = new List<string>();
        ScenePatches.Prefix(_harmony, failed, typeof(TitleScreen), nameof(TitleScreen.AssignDefaultControllers), typeof(TeamSeats), nameof(AssignDefaultControllersPrefix));
        Installed = failed.Count == 0;
        if (!Installed)
        {
            ModLog.Warn($"Team seats: the campaign start's seating couldn't be installed ({string.Join(", ", failed)}); a campaign seats by the game's own rule (the sides its controller screen saved, else its first pad player).");
        }
    }

    /// <summary>The game's memory of sides by game player id, or null (with why) if it can't be read. Never throws.</summary>
    public static Dictionary<int, int>? ReadMemory(out string problem)
    {
        problem = string.Empty;
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, ControllerAssignmentPlayer.ESide>? sides = ControllerAssignmentState.s_ControllerSide;
            if (sides == null)
            {
                problem = "the game has no memory of sides";
                return null;
            }

            var memory = new Dictionary<int, int>();
            for (int id = 0; id <= TeamSides.MaxGamePlayer; id++)
            {
                if (sides.TryGetValue(id, out ControllerAssignmentPlayer.ESide side))
                {
                    memory[id] = (int)side;
                }
            }

            return memory;
        }
        catch (Exception ex)
        {
            problem = ModLog.SafeErrorText(ex);
            return null;
        }
    }

    /// <summary>Makes one write in the game's memory: the game's own <c>RememberControllerSide</c>, or the id removed. False, with why, if it failed. Never throws.</summary>
    public static bool Apply(SideWrite write, out string problem)
    {
        problem = string.Empty;
        try
        {
            if (write.Side is int side)
            {
                ControllerAssignmentState.RememberControllerSide(write.GamePlayer, (ControllerAssignmentPlayer.ESide)side);
            }
            else
            {
                ControllerAssignmentState.s_ControllerSide?.Remove(write.GamePlayer);
            }

            return true;
        }
        catch (Exception ex)
        {
            problem = ModLog.SafeErrorText(ex);
            return false;
        }
    }

    /// <summary>Makes every write, telling the book of each one made. Returns the first failure's text, or null. Never throws.</summary>
    public static string? ApplyAll(IReadOnlyList<SideWrite> writes, TeamSeatBook? book)
    {
        string? failed = null;
        foreach (SideWrite write in writes)
        {
            if (Apply(write, out string problem))
            {
                book?.Written(write);
            }
            else
            {
                failed ??= string.Create(System.Globalization.CultureInfo.InvariantCulture, $"game player {write.GamePlayer}: {problem}");
            }
        }

        return failed;
    }

    /// <summary>True when the run plays the solo squad (the game's own test at its start). False, with why, when unread.</summary>
    public static bool IsSoloSquad(out string problem)
    {
        problem = string.Empty;
        try
        {
            CampaignUtil? util = CampaignUtil.Instance;
            if (util == null)
            {
                problem = "the campaign's squad isn't readable";
                return false;
            }

            return util.IsPlayingSquad(SoloSquad, false);
        }
        catch (Exception ex)
        {
            problem = ModLog.SafeErrorText(ex);
            return false;
        }
    }

    /// <summary>True when the game reads <paramref name="player"/> as a pad player (its own test, with virtual pads counted). False when unread.</summary>
    public static bool IsPad(Player player)
    {
        try
        {
            return player.IsUsingGamepad();
        }
        catch (Exception)
        {
            return false; // read as the keyboard: the host's default then prefers another player that reads as a pad
        }
    }

    /// <summary>
    /// The team selection asset a campaign's matches seat from: <paramref name="known"/> (the one the start filled) while it's
    /// alive, else the loaded one. Null if none.
    /// </summary>
    public static TeamSelection? TeamList(TeamSelection? known)
    {
        if (known != null && !known.WasCollected)
        {
            return known;
        }

        foreach (UnityEngine.Object asset in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<TeamSelection>()))
        {
            if (asset?.TryCast<TeamSelection>() is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The hook: the session seats the lobby before the game's own seating runs. Never throws into the game's start, never skips it.</summary>
    private static void AssignDefaultControllersPrefix(TitleScreen __instance)
    {
        try
        {
            Starting?.Invoke(__instance);
        }
        catch (Exception ex)
        {
            if (_errors++ < LoggedErrors)
            {
                ModLog.Error("Team seats: seating the lobby at the campaign's start failed; the game seats by its own rule this start", ex);
            }
        }
    }
}
#endif
