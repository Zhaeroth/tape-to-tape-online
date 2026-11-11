using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Diagnostics;

/// <summary>How the installed game differs from the game builds the mod was made for (<see cref="UntestedBuildNotice.Age"/>).</summary>
public enum GameVersionAge
{
    /// <summary>It can't be told (no Steam build number, or one between two tested builds): the notice is worded both ways.</summary>
    Unknown,

    /// <summary>The game was updated after the mod's release: a newer T2T Online brings online back.</summary>
    Newer,

    /// <summary>The game is older than the build the mod was made for: a Steam update brings online back.</summary>
    Older,

    /// <summary>Steam has another build to install for the game: once it's in, the start says again.</summary>
    UpdateWaiting,

    /// <summary>Steam's build number is a tested one, yet the game's files differ from it: Steam's file check puts them right.</summary>
    SameSteamBuild,
}

/// <summary>
/// The corner notice for a game build T2T Online wasn't made for (D12: online stays off): why, and what brings online back, worded for the
/// way the game differs. Steam's build number (<see cref="SteamAppManifest"/>) against the tested builds' says whether the game is newer or
/// older; when it can't be told, one text covers both ways. English, as the corner notice is; the releases page comes from
/// <see cref="ProjectLinks"/> and is never in a log line.
/// </summary>
public static class UntestedBuildNotice
{
    /// <summary>
    /// How the game differs: <paramref name="installed"/> and <paramref name="target"/> from Steam's manifest (0: not known), against the
    /// Steam build numbers of the tested builds.
    /// </summary>
    public static GameVersionAge Age(long installed, long target, IReadOnlyCollection<long> tested)
    {
        ArgumentNullException.ThrowIfNull(tested);
        if (installed <= 0 || tested.Count == 0)
        {
            return GameVersionAge.Unknown;
        }

        if (target > 0 && target != installed)
        {
            return GameVersionAge.UpdateWaiting;
        }

        if (tested.Contains(installed))
        {
            return GameVersionAge.SameSteamBuild;
        }

        return installed > tested.Max() ? GameVersionAge.Newer : installed < tested.Min() ? GameVersionAge.Older : GameVersionAge.Unknown;
    }

    /// <summary>Why online is off, for the notice's first line ("online is off: ...").</summary>
    public static string Reason(GameVersionAge age) => age switch
    {
        GameVersionAge.Newer => "the game was updated after this T2T Online was made",
        GameVersionAge.Older => "this game version is older than the one this T2T Online was made for",
        GameVersionAge.UpdateWaiting => "Steam has an update for the game waiting",
        GameVersionAge.SameSteamBuild => "the game's files aren't the ones this T2T Online was made for",
        _ => "this game version isn't the one this T2T Online was made for",
    };

    /// <summary>What brings online back, for the notice's second line; <paramref name="releases"/> is where the releases are (<see cref="ProjectLinks.ReleasesShort"/>).</summary>
    public static string Advice(GameVersionAge age, string releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return age switch
        {
            GameVersionAge.Newer => $"The game plays normally. Online comes back with a T2T Online made for this game version: look for it at {releases}.",
            GameVersionAge.Older => "The game plays normally. Let Steam update Tape to Tape (Steam's Downloads page), then start the game again.",
            GameVersionAge.UpdateWaiting => "The game plays normally. Let Steam finish the update (Steam's Downloads page), then start the game again.",
            GameVersionAge.SameSteamBuild => "The game plays normally. In Steam, check the game's files (Properties > Installed Files > Verify integrity), then start the game again.",
            _ => $"The game plays normally. If Steam has an update for the game, let it finish; otherwise online comes back with a T2T Online made for this game version, at {releases}.",
        };
    }

    /// <summary>The log's words for <paramref name="age"/>, with the numbers (no address, no path).</summary>
    public static string ForLog(GameVersionAge age, long installed, long target, IReadOnlyCollection<long> tested, string? unknownWhy = null)
    {
        ArgumentNullException.ThrowIfNull(tested);
        string ours = tested.Count == 0 ? "none" : string.Join(", ", tested.OrderBy(b => b).Select(b => b.ToString(CultureInfo.InvariantCulture)));
        string here = installed.ToString(CultureInfo.InvariantCulture);
        return age switch
        {
            GameVersionAge.Newer => $"the game is newer than this T2T Online's (Steam build {here}; T2T Online's: {ours})",
            GameVersionAge.Older => $"the game is older than this T2T Online's (Steam build {here}; T2T Online's: {ours})",
            GameVersionAge.UpdateWaiting => string.Create(CultureInfo.InvariantCulture, $"Steam has build {target} waiting to install over {here} (T2T Online's: {ours})"),
            GameVersionAge.SameSteamBuild => $"Steam's build {here} is a tested one, but the game's files differ from it",
            _ => installed > 0
                ? $"whether the game is newer or older can't be told (Steam build {here}; T2T Online's: {ours})"
                : $"whether the game is newer or older can't be told ({(string.IsNullOrEmpty(unknownWhy) ? "no Steam build number" : unknownWhy)})",
        };
    }
}
