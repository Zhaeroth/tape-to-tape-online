using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The lineup menu's cards in order, for a friend's copy of the host's lineup screen (D43, v1): the swaps of the game's own
/// card swap (<c>LineupMenu.SwapPlayerCards</c>) that turn this PC's order into the host's, and a team's print (its skaters
/// by id, in order), which tells a friend its copy of the team the screen was opened on is the host's. A card is a skater
/// id; an empty seat is "".
/// </summary>
public static class LineupOrder
{
    /// <summary>Most cards a lineup is followed with (the game shows a line, the goalie and the bench: about a dozen).</summary>
    public const int MaxCards = 32;

    /// <summary>
    /// The swaps that turn <paramref name="current"/> into <paramref name="wanted"/>, in order: each swaps the cards at its two
    /// places (each place a card's index in the order). None when they're the same; null when <paramref name="wanted"/> isn't
    /// an order of the same cards (another team, a skater added or gone), or either has more than <see cref="MaxCards"/>.
    /// A card already where it should be is never moved.
    /// </summary>
    public static IReadOnlyList<(int A, int B)>? Swaps(IReadOnlyList<string> current, IReadOnlyList<string> wanted) => Swaps(current, wanted, MaxCards);

    /// <summary>
    /// <see cref="Swaps(IReadOnlyList{string}, IReadOnlyList{string})"/> for a menu with more cards (the lineup with
    /// benchwarmers: <see cref="BenchwarmerLineup.MaxCards"/>): null past <paramref name="maxCards"/>.
    /// </summary>
    public static IReadOnlyList<(int A, int B)>? Swaps(IReadOnlyList<string> current, IReadOnlyList<string> wanted, int maxCards)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);
        if (current.Count != wanted.Count || current.Count > maxCards || !SameCards(current, wanted))
        {
            return null;
        }

        var now = new string[current.Count];
        for (int i = 0; i < now.Length; i++)
        {
            now[i] = current[i] ?? string.Empty;
        }

        var swaps = new List<(int A, int B)>();
        for (int i = 0; i < now.Length; i++)
        {
            string want = wanted[i] ?? string.Empty;
            if (now[i] == want)
            {
                continue;
            }

            // The card that belongs here, from a place that's wrong too (so nothing in place moves).
            int from = -1;
            for (int j = i + 1; j < now.Length && from < 0; j++)
            {
                if (now[j] == want && now[j] != (wanted[j] ?? string.Empty))
                {
                    from = j;
                }
            }

            if (from < 0)
            {
                return null; // never with the same cards: every wanted card is at a wrong place further on
            }

            (now[i], now[from]) = (now[from], now[i]);
            swaps.Add((i, from));
        }

        return swaps;
    }

    /// <summary>
    /// A team's print: its skaters' ids in order (an empty seat or a missing skater as ""), hashed. Two PCs with the same
    /// team in the same order print the same; any other order or skater prints otherwise (but for a 64-bit collision).
    /// </summary>
    public static long Print(IEnumerable<string?> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ulong hash = Fnv.OffsetBasis;
        int count = 0;
        foreach (string? id in ids)
        {
            hash = Fnv.Add(hash, Encoding.UTF8.GetBytes(id ?? string.Empty));
            hash = Fnv.Add(hash, 0); // the separator: "ab","c" isn't "a","bc"
            count++;
        }

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, count);
        hash = Fnv.Add(hash, length);
        return unchecked((long)hash);
    }

    /// <summary>
    /// The places a run team's starters fill at the front of its roster: a run's team has no lines, so the game reads its
    /// starters from the roster's first places and its bench from the rest (<c>TeamData.GetForward</c>, <c>GetBench</c>).
    /// </summary>
    public const int StarterPlaces = 5;

    /// <summary>Most skaters named in each part of a line from <see cref="AgainstRun"/>.</summary>
    private const int NamedInLine = 4;

    /// <summary>
    /// For logs, a run's team against the lineup a friend's copy showed last: "has that lineup" (the same skaters, the same
    /// starters), "has those skaters with other starters (...)", "has those skaters" (the host's data didn't say which cards
    /// were its starters'), or "has another lineup (...)" with what differs. <paramref name="shown"/>: the lineup's cards in
    /// order, the first <paramref name="starters"/> of them its starters' list's (-1: not said). The goalie's card
    /// (<paramref name="goalie"/>: the team's goalie, whom a run's roster doesn't hold) and empty seats are left out.
    /// <paramref name="roster"/>: the run team's roster in order, its first <see cref="StarterPlaces"/> places the starters.
    /// Places among the starters aren't compared: the menu lists its starters in its own order.
    /// </summary>
    public static string AgainstRun(IReadOnlyList<string?> shown, int starters, IReadOnlyList<string?> roster, string? goalie)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(roster);
        bool Skater(string? id) => !string.IsNullOrEmpty(id) && !string.Equals(id, goalie, StringComparison.Ordinal);
        string? difference = BenchwarmerLineup.Difference(shown.Where(Skater), roster.Where(id => !string.IsNullOrEmpty(id)));
        if (difference != null)
        {
            return $"has another lineup ({difference})";
        }

        if (starters < 0 || starters > shown.Count)
        {
            return "has those skaters (the host's data didn't say which cards were its starters')";
        }

        var started = new HashSet<string>(shown.Take(starters).Where(Skater).Select(id => id!), StringComparer.Ordinal);
        var starting = new HashSet<string>(roster.Take(StarterPlaces).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!), StringComparer.Ordinal);
        if (started.SetEquals(starting))
        {
            return "has that lineup";
        }

        List<string> benched = started.Except(starting, StringComparer.Ordinal).ToList();
        List<string> promoted = starting.Except(started, StringComparer.Ordinal).ToList();
        return $"has those skaters with other starters ({benched.Count} the lineup started are on the run's bench ({Named(benched)}); " +
            $"{promoted.Count} from its bench start in the run ({Named(promoted)}))";
    }

    private static string Named(List<string> ids) =>
        string.Join(", ", ids.Take(NamedInLine).Select(id => Diagnostics.HostText.KeyForLog(id))) + (ids.Count > NamedInLine ? $", {ids.Count - NamedInLine} more" : string.Empty);

    /// <summary>True when both hold the same cards, as many times each (empty seats included).</summary>
    private static bool SameCards(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string card in a)
        {
            string key = card ?? string.Empty;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (string card in b)
        {
            string key = card ?? string.Empty;
            int left = counts.GetValueOrDefault(key) - 1;
            if (left < 0)
            {
                return false;
            }

            counts[key] = left;
        }

        return true;
    }
}
