using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Stream;

/// <summary>
/// Game-independent helpers for a campaign match's setup: the game type that
/// marks one, the text forms some of its values travel in (a map point "x,y", a list of whole numbers "1,2,3"), when a
/// friend's game holds the run checkpoint the match needs, and how a lineup named by asset ids maps onto a team's own
/// skaters. The values come from the host: every parser returns false on bad input and never throws.
/// </summary>
public static class CampaignMatchValues
{
    /// <summary>The setup's game type (the game's <c>TeamSelection.gameType</c>).</summary>
    public const string GameTypeKey = "mode.gameType";

    /// <summary>The game type of a campaign match (<c>ScriptableStates.GameType.Rogue</c>).</summary>
    public const long CampaignGameType = 1;

    /// <summary>The setup key for the run checkpoint a friend needs before it loads the match (0: none named).</summary>
    public const string RunCheckpointKey = "campaign.run";

    /// <summary>Most whole numbers in one list.</summary>
    public const int MaxListLength = 32;

    /// <summary>True when <paramref name="setup"/> is a campaign match's (game type Rogue).</summary>
    public static bool IsCampaign(IReadOnlyList<KeyValuePair<string, NetValue>>? setup)
    {
        if (setup == null)
        {
            return false;
        }

        NetValue value = MatchSetupValues.Get(setup, GameTypeKey);
        return value.Kind == NetValueKind.Whole && value.WholeValue == CampaignGameType;
    }

    /// <summary>The run checkpoint a campaign setup names (<see cref="RunCheckpointKey"/>); 0 when it names none or the value isn't a checkpoint number.</summary>
    public static uint NeededCheckpoint(IReadOnlyList<KeyValuePair<string, NetValue>>? setup)
    {
        if (setup == null)
        {
            return 0;
        }

        NetValue value = MatchSetupValues.Get(setup, RunCheckpointKey);
        return value.Kind == NetValueKind.Whole && value.WholeValue is > 0 and <= uint.MaxValue ? (uint)value.WholeValue : 0;
    }

    /// <summary>
    /// Host: why friends can't load the campaign match <paramref name="setup"/> describes, or null when they can (or it
    /// isn't a campaign match). <paramref name="campaignShared"/>: the host's game plays the run of its co-op slot, the one
    /// friends follow (D46). A friend's home team comes only from a run checkpoint, so a match whose setup names none (the
    /// host's run couldn't be sent for it) can't be loaded either: friends would wait for a run that comes only after the
    /// match, and the host's match would wait for them at its start.
    /// </summary>
    public static string? WhyNotShared(IReadOnlyList<KeyValuePair<string, NetValue>> setup, bool campaignShared)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (!IsCampaign(setup))
        {
            return null;
        }

        if (!campaignShared)
        {
            return "the host's game plays its solo run, not a co-op slot, so the campaign isn't shared (friends follow it from the title screen)";
        }

        return NeededCheckpoint(setup) == 0
            ? "it names no run checkpoint (the host's run couldn't be sent for it), so friends follow from the next world map"
            : null;
    }

    /// <summary>A map point as text: "x,y".</summary>
    public static string FormatPoint(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"{x},{y}");

    /// <summary>Reads "x,y" with both from 0 to <paramref name="max"/>. False for anything else.</summary>
    public static bool TryParsePoint(string? text, int max, out int x, out int y)
    {
        x = y = 0;
        if (!TryParseList(text, 2, 0, max, out int[] values) || values.Length != 2)
        {
            return false;
        }

        x = values[0];
        y = values[1];
        return true;
    }

    /// <summary>Whole numbers as text: "1,2,3" (empty for none).</summary>
    public static string FormatList(IEnumerable<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Reads "1,2,3": at most <paramref name="maxCount"/> (and <see cref="MaxListLength"/>) whole numbers, each from
    /// <paramref name="min"/> to <paramref name="max"/>. Empty text is an empty list. False for anything else (an empty
    /// entry, a sign or space the invariant culture doesn't take, a number out of range).
    /// </summary>
    public static bool TryParseList(string? text, int maxCount, int min, int max, out int[] values)
    {
        values = Array.Empty<int>();
        if (text == null)
        {
            return false;
        }

        if (text.Length == 0)
        {
            return true;
        }

        // Enough characters for the most numbers allowed, so a hostile string isn't split at all.
        int most = Math.Min(maxCount, MaxListLength);
        if (most <= 0 || text.Length > most * 12)
        {
            return false;
        }

        string[] parts = text.Split(',');
        if (parts.Length > most)
        {
            return false;
        }

        var parsed = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            {
                return false;
            }

            parsed[i] = value;
        }

        values = parsed;
        return true;
    }

    /// <summary>How many values a visitor forward's line has: shot power, speed, checking, shot accuracy, jersey number.</summary>
    public const int ForwardValueCount = 5;

    /// <summary>
    /// A visitor forward's line as the host sends it (<see cref="ForwardValueCount"/> whole numbers): false only when it
    /// isn't five whole numbers. The four stats are clamped to 0 to <paramref name="maxStat"/> (<paramref name="clamped"/>
    /// says so), and the jersey number is taken as it is: the Act 1 boss's prisoners wear numbers past any stat limit, and
    /// a number refused as out of range would refuse the whole boss match. A friend's copy of the
    /// visitor only shows these values, so an odd one must never stop the match from loading.
    /// </summary>
    public static bool TryParseForward(string? text, int maxStat, out int[] values, out bool clamped)
    {
        clamped = false;
        if (!TryParseList(text, ForwardValueCount, int.MinValue, int.MaxValue, out values) || values.Length != ForwardValueCount)
        {
            values = Array.Empty<int>();
            return false;
        }

        for (int i = 0; i < ForwardValueCount - 1; i++)
        {
            int stat = Math.Clamp(values[i], 0, maxStat);
            clamped |= stat != values[i];
            values[i] = stat;
        }

        return true;
    }

    /// <summary>
    /// True when a friend's game that applied run checkpoint <paramref name="applied"/> (0: none) holds what a match
    /// that needs <paramref name="needed"/> asks for: that checkpoint or a later one. A match that names none (0; the host
    /// had sent no checkpoint when it captured the match) takes any checkpoint, but not none.
    /// </summary>
    public static bool HoldsCheckpoint(uint applied, uint needed) =>
        applied != 0 && (needed == 0 || !SequenceMath.IsNewer(needed, applied));

    /// <summary>
    /// Maps a lineup named by asset ids (<paramref name="wanted"/>, one per slot; empty for an empty slot) onto a team's
    /// own skaters (<paramref name="idByOriginal"/>: each skater's own id by its asset id). <paramref name="ids"/> gets
    /// the team's ids in slot order (empty where the slot is empty or its skater isn't on the team), and
    /// <paramref name="missing"/> the asset ids that aren't. True when every wanted skater was found.
    /// </summary>
    public static bool TryMapSlots(IReadOnlyList<string> wanted, IReadOnlyDictionary<string, string> idByOriginal, out string[] ids, out List<string> missing)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(idByOriginal);
        ids = new string[wanted.Count];
        missing = new List<string>();
        for (int i = 0; i < wanted.Count; i++)
        {
            string original = wanted[i] ?? string.Empty;
            if (original.Length == 0)
            {
                ids[i] = string.Empty;
            }
            else if (idByOriginal.TryGetValue(original, out string? id) && !string.IsNullOrEmpty(id))
            {
                ids[i] = id;
            }
            else
            {
                ids[i] = string.Empty;
                missing.Add(original);
            }
        }

        return missing.Count == 0;
    }

    /// <summary>
    /// The places that put a team's roster in the host's order (a lineup change the host's game made in memory, which reaches
    /// its run file only at its next save): <paramref name="from"/>[i] is the place in <paramref name="here"/> whose skater
    /// goes to place i of <paramref name="host"/>. Each roster names its skaters by asset id, "" (or null) for an empty seat.
    /// True only when both hold the same skaters and as many empty seats, each skater once, at most
    /// <paramref name="maxRoster"/> places; a skater already in its place stays there. False otherwise (another team, a
    /// skater signed or lost since: an older run; or one asset twice, which asset ids can't tell apart), with
    /// <paramref name="from"/> empty: nothing is to be moved.
    /// </summary>
    public static bool TryOrderRoster(IReadOnlyList<string?> host, IReadOnlyList<string?> here, int maxRoster, out int[] from)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(here);
        from = Array.Empty<int>();
        int count = host.Count;
        if (count != here.Count || count > maxRoster || HasRepeat(host) || HasRepeat(here))
        {
            return false;
        }

        var places = new int[count];
        var taken = new bool[count];
        Array.Fill(places, -1);
        for (int i = 0; i < count; i++)
        {
            if ((host[i] ?? string.Empty) == (here[i] ?? string.Empty))
            {
                places[i] = i;
                taken[i] = true;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (places[i] >= 0)
            {
                continue;
            }

            string wanted = host[i] ?? string.Empty;
            for (int j = 0; j < count && places[i] < 0; j++)
            {
                if (!taken[j] && (here[j] ?? string.Empty) == wanted)
                {
                    places[i] = j;
                    taken[j] = true;
                }
            }

            if (places[i] < 0)
            {
                return false;
            }
        }

        from = places;
        return true;
    }

    /// <summary>True when a roster names one skater (not an empty seat) more than once.</summary>
    private static bool HasRepeat(IReadOnlyList<string?> roster)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? id in roster)
        {
            if (!string.IsNullOrEmpty(id) && !seen.Add(id))
            {
                return true;
            }
        }

        return false;
    }
}
