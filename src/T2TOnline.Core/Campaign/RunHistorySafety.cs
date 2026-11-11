using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a friend's game needs of a host's run history entry before its own run history keeps it (D42, D135). The game's
/// history page (<c>RunEndMenu.ShowHistoryEntry</c>) builds the entry's team without asking whether there is one, and
/// makes a <c>DateTime</c> of its time, which throws for a number that isn't a date; it shows each match with its
/// opponent's logo, and throws for an opponent this game doesn't have (<see cref="ShownMatches"/>); and it lists runs
/// newest first, the oldest making way once the history is full (<c>RunHistoryRepository.LoadRunList</c>,
/// <c>PickSlot</c>), so an entry dated far ahead would head the list for good. An honest host's entry always has its
/// team, its own clock's time and matches against the game's own teams; one that doesn't must not reach the friend's
/// files as it is, where it would break that page at every later launch.
/// Game-independent.
/// </summary>
public static class RunHistorySafety
{
    /// <summary>
    /// How far ahead of this PC's clock a host's entry may be dated and keep its time (the time is UTC, so two honest
    /// clocks differ by minutes; a clock set a day wrong is still taken at its word).
    /// </summary>
    public static readonly TimeSpan MostAhead = TimeSpan.FromDays(1);

    /// <summary>
    /// <paramref name="ticks"/> as a time the game's page can make a date of: itself when it is one, else the nearest
    /// (the first or the last date there is). For every entry of the game's own type made from a host's values.
    /// </summary>
    public static long AsDate(long ticks) => Math.Clamp(ticks, 0, DateTime.MaxValue.Ticks);

    /// <summary>Why <paramref name="entry"/> can't go into this PC's run history, or null when it can.</summary>
    public static string? Refusal(RunHistoryData entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Team == null ? "the run history entry not saved: it has no team, and this game's run history page can't show a run without one" : null;
    }

    /// <summary>
    /// The time this PC's run history keeps for an entry the host dated <paramref name="hostTicks"/> (UTC ticks), with
    /// this PC's clock at <paramref name="utcNow"/>: the host's own, unless it isn't a date or lies more than
    /// <see cref="MostAhead"/> ahead of this PC's clock; then this PC's time now, the run having just ended
    /// (<paramref name="replaced"/>). The result is always a date.
    /// </summary>
    public static long Timestamp(long hostTicks, DateTime utcNow, out bool replaced)
    {
        long now = Math.Clamp(utcNow.Ticks, 0, DateTime.MaxValue.Ticks);
        long latest = now > DateTime.MaxValue.Ticks - MostAhead.Ticks ? DateTime.MaxValue.Ticks : now + MostAhead.Ticks;
        replaced = hostTicks < 0 || hostTicks > latest;
        return replaced ? now : hostTicks;
    }

    /// <summary>
    /// The matches of <paramref name="matches"/> the game's page can show: those whose opponent
    /// (<see cref="MatchLine.TeamId"/>) is a team this PC's game has (<paramref name="hasTeam"/>, asked once per id). The
    /// page looks each match's opponent up among its own teams (<c>RunEndMenu.FindTeam</c>: no team for an id it doesn't
    /// have, or for none) and its match row then reads that team's logo without asking whether there is one
    /// (<c>RunMatchDetailsUI.Refresh</c>), so one match naming a team this game lacks would make the whole entry throw
    /// there. An honest host's matches are against the game's own teams, which both games have; a match left out is one
    /// row fewer on the page. The same list when every match can be shown (nothing is made); the order is kept.
    /// </summary>
    public static IReadOnlyList<MatchLine> ShownMatches(IReadOnlyList<MatchLine> matches, Func<string, bool> hasTeam)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(hasTeam);
        Dictionary<string, bool>? asked = null;
        List<MatchLine>? shown = null;
        for (int i = 0; i < matches.Count; i++)
        {
            MatchLine? match = matches[i];
            string? id = match?.TeamId;
            bool has = false;
            if (!string.IsNullOrEmpty(id))
            {
                asked ??= new Dictionary<string, bool>(StringComparer.Ordinal);
                if (!asked.TryGetValue(id, out has))
                {
                    asked[id] = has = hasTeam(id);
                }
            }

            if (has)
            {
                shown?.Add(match!);
                continue;
            }

            if (shown == null)
            {
                shown = new List<MatchLine>(matches.Count);
                for (int before = 0; before < i; before++)
                {
                    shown.Add(matches[before]);
                }
            }
        }

        return shown ?? matches;
    }
}

/// <summary>
/// The files the game's run history save may write (<c>RunHistoryRepository.SaveEntry</c>: one slot, then the index), for
/// the save guard's allowance on a friend's PC. The save picks the slot itself (the run's own, else the first free one,
/// else the oldest run's), so every slot the game has is named: 500 on the Nov 1 and Nov 6 builds
/// (<c>RunHistoryFiles.MaxRuns</c>). A slot left out would be refused without a word while the index still named the run.
/// Game-independent.
/// </summary>
public static class RunHistoryFileNames
{
    /// <summary>The game's index file.</summary>
    public const string Index = "RunHistoryIndex.es3";

    /// <summary>The game's slots (Nov 1 and Nov 6 builds), for when it can't say.</summary>
    public const int Slots = 500;

    /// <summary>Most slots ever named (each one is looked at once per save).</summary>
    public const int MostSlots = 2000;

    /// <summary>The game's name for slot <paramref name="slot"/> (<c>RunHistoryFiles.Slot</c>), for when it can't say.</summary>
    public static string Slot(int slot) => string.Create(CultureInfo.InvariantCulture, $"RunHistory_{slot:D2}.es3");

    /// <summary>
    /// The index (<paramref name="index"/>; the game's known name when it's null or empty) and every slot of a history of
    /// <paramref name="slots"/> runs (1 to <see cref="MostSlots"/>), each named by <paramref name="slotName"/> (the
    /// game's known name when that is null or gives none).
    /// </summary>
    public static string[] All(string? index, int slots, Func<int, string?>? slotName)
    {
        int count = Math.Clamp(slots, 1, MostSlots);
        string[] names = new string[count + 1];
        names[0] = string.IsNullOrEmpty(index) ? Index : index;
        for (int i = 0; i < count; i++)
        {
            string? name = slotName?.Invoke(i);
            names[i + 1] = string.IsNullOrEmpty(name) ? Slot(i) : name;
        }

        return names;
    }
}
