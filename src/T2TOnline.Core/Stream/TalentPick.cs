using System;
using System.Globalization;

namespace T2TOnline.Core.Stream;

/// <summary>Which of the game's match-time relics made a <see cref="TalentPick"/>.</summary>
public enum PickSource
{
    /// <summary>Hot Cake (<c>HotCakeController</c>): a random teammate's random talent, given to every other teammate.</summary>
    HotCake = 1,

    /// <summary>The hot and cold relic (<c>HotColdRelicController</c>): a random hot and a random cold skater, each given its talent.</summary>
    HotCold = 2,

    /// <summary>
    /// The bench borrower talent (<c>BenchBorrowerController</c>): its skater borrows a random talent of a bench skater
    /// while it plays (at the match's start and each line change).
    /// </summary>
    BenchBorrower = 3,
}

/// <summary>
/// One talent a host's relic gave a skater for this match from a random pick of its own game. A friend's game rolls its
/// own and gets other skaters or another talent, so a friend's skaters take the host's picks instead (the plugin's
/// <c>Match/MatchPicks</c>): talent by id and level, as the game's own talent repository makes it. Carried as text
/// "source|id|level". Parsing never throws; a bad value is refused.
/// </summary>
/// <param name="Source">The relic that made the pick.</param>
/// <param name="TalentId">The talent's id (<c>PowerUp.id</c>).</param>
/// <param name="Level">The talent's level.</param>
public readonly record struct TalentPick(PickSource Source, string TalentId, int Level)
{
    /// <summary>The longest talent id taken (the game's ids are 32 or 36 characters).</summary>
    public const int MaxIdLength = 64;

    /// <summary>The highest talent level taken.</summary>
    public const int MaxLevel = 99;

    /// <summary>"1|ba0af0a4-...|2": the pick as its HUD text.</summary>
    public string Format() => string.Create(CultureInfo.InvariantCulture, $"{(int)Source}|{TalentId}|{Level}");

    /// <summary>Reads a pick's HUD text; false for anything else (an unknown source, an id out of shape, a level out of range).</summary>
    public static bool TryParse(string? text, out TalentPick pick)
    {
        pick = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxIdLength + 8)
        {
            return false;
        }

        string[] parts = text.Split('|');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int source) || !Enum.IsDefined((PickSource)source) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int level) || level > MaxLevel ||
            parts[1].Length is 0 or > MaxIdLength || !IsId(parts[1]))
        {
            return false;
        }

        pick = new TalentPick((PickSource)source, parts[1], level);
        return true;
    }

    private static bool IsId(string id)
    {
        foreach (char c in id)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
