using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// How a check tour ends when it was to leave the game open (started by its key, or stopped by a person's press). Its
/// holds keep the player's data off the disk, not out of the game's memory: a refused save leaves what the game meant to
/// write in memory, and the game writes it at its next save once the save guard lets go (at the latest at quit:
/// <c>ProfileData.OnApplicationQuit</c> saves the profile). So a tour that played anything the game keeps (a match's
/// stats, a run's unlocks and visits, the mod's own records held in memory), that ends away from the title screen, or that
/// couldn't put something back, quits the game with every hold on instead, as a tour from <c>run.txt</c> does when it ends.
/// </summary>
public static class TourEnd
{
    /// <summary>
    /// The game's settings files: what a tour changes in them (the window, the language) it puts back itself before it ends
    /// (and says so when it can't), so a refused write of one leaves nothing of the tour's in memory.
    /// </summary>
    public static IReadOnlyList<string> SettingsFiles { get; } = new[] { "config.es3", "GameplaySettings.es3" };

    /// <summary>
    /// True when a save write the guard refused during a tour leaves some of the player's data in the game's memory only: it
    /// names a file that is neither one of the game's settings files (<see cref="SettingsFiles"/>) nor the tour's own run file
    /// (<see cref="TourWrites.RunFileName"/>), each by its name with Easy Save's copies under their original, or it names no
    /// file at all (what it was for can't be told). Never throws.
    /// </summary>
    public static bool HoldsPlayerData(IEnumerable<string?> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        bool any = false;
        foreach (string? name in fileNames)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            any = true;
            string file = EasySaveCopies.Original(name);
            if (!IsSettingsFile(file) && !string.Equals(file, TourWrites.RunFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !any;
    }

    /// <summary>
    /// Why a tour that was to leave the game open quits it instead, with every hold on; null when it may let the holds go.
    /// <paramref name="playerDataHeld"/>: the player's data the holds kept in memory only (game saves refused for it, the
    /// mod's records held); <paramref name="atTitle"/>: the game is at its title screen; <paramref name="allPutBack"/>: the
    /// tour put back everything it changed; <paramref name="stopped"/>: why the tour stopped having lost its place
    /// (<see cref="TourGate"/>), or null (a stopped tour put back only what needs no press, so it never stays open).
    /// </summary>
    public static string? QuitReason(long playerDataHeld, bool atTitle, bool allPutBack, string? stopped = null)
    {
        var reasons = new List<string>(4);
        if (!string.IsNullOrEmpty(stopped))
        {
            reasons.Add($"the tour stopped ({stopped}) and put back only what needs no press");
        }

        if (playerDataHeld > 0)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture,
                $"the game holds what the tour played ({playerDataHeld} of its saves refused or records kept in memory), which its next save would write"));
        }

        if (!atTitle)
        {
            reasons.Add("the game isn't at its title screen (a match or a run the tour started is still on)");
        }

        if (!allPutBack)
        {
            reasons.Add("something the tour changed wasn't put back (said above), and the game would save it");
        }

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    private static bool IsSettingsFile(string file)
    {
        foreach (string settings in SettingsFiles)
        {
            if (string.Equals(file, settings, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
