using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// How many cards each of the lineup-with-benchwarmers menu's three lists shows: the starters' seats, the bench's seats and
/// the benchwarmers (the menu's pool).
/// </summary>
/// <param name="Starters">The starters' seats (empty ones too).</param>
/// <param name="Bench">The bench's seats (empty ones too).</param>
/// <param name="Pool">The benchwarmers' cards.</param>
public readonly record struct BenchwarmerCardCounts(int Starters, int Bench, int Pool)
{
    /// <summary>Every card of the three lists.</summary>
    public int Total => Starters + Bench + Pool;

    /// <summary>For logs: "5 starter seat(s), 4 bench seat(s) and 9 benchwarmer(s)".</summary>
    public override string ToString() => $"{Starters} starter seat(s), {Bench} bench seat(s) and {Pool} benchwarmer(s)";
}

/// <summary>
/// The lineup with benchwarmers (the game's <c>BenchwarmerMenu</c>) on a friend (D68, v1.1): its cards in order (the
/// starters', the bench's, then the benchwarmers'), each a skater id or "" for an empty seat. A friend's copy follows the
/// host's with the menu's own swap (<c>SwapPlayerCards</c>) as <see cref="LineupOrder.Swaps(IReadOnlyList{string}, IReadOnlyList{string}, int)"/>
/// works them out, once its three lists have as many cards as the host's (<see cref="Mismatch"/>); after the host's first
/// real checkpoint, the run's team should hold the skaters the host's lineup showed last (<see cref="Difference"/>).
/// </summary>
public static class BenchwarmerLineup
{
    /// <summary>Most cards followed (five starters, a bench and a pool of up to <see cref="RunPreviewSet.MaxPool"/>, with room).</summary>
    public const int MaxCards = 40;

    private const int MaxNamed = 4;

    /// <summary>The swaps that turn <paramref name="here"/> into <paramref name="host"/> (<see cref="LineupOrder"/>'s rules, up to <see cref="MaxCards"/>).</summary>
    public static IReadOnlyList<(int A, int B)>? Swaps(IReadOnlyList<string> here, IReadOnlyList<string> host) => LineupOrder.Swaps(here, host, MaxCards);

    /// <summary>Why this PC's cards can't follow the host's ("" when each list has as many cards as the host's, within <see cref="MaxCards"/>).</summary>
    public static string Mismatch(BenchwarmerCardCounts here, BenchwarmerCardCounts host)
    {
        if (host.Total > MaxCards)
        {
            return $"the host's lineup has {host.Total} cards (at most {MaxCards} are followed)";
        }

        return here == host ? string.Empty : $"this PC's menu has {here}, the host's {host}";
    }

    /// <summary>
    /// The skaters the host's lineup showed (its starters' and bench's cards) that the run's team (its forwards and bench)
    /// doesn't have, by id, empty seats left out, in any order (none: the run has every one).
    /// </summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string?> shown, IEnumerable<string?> team)
    {
        Compare(shown, team, out List<string> missing, out _);
        return missing;
    }

    /// <summary>
    /// The skaters the host's lineup showed (its starters' and bench's cards) against the run's team (its forwards and bench),
    /// by id, empty seats left out, in any order: null when they're the same skaters; else what differs, in words (a few
    /// names of each side).
    /// </summary>
    public static string? Difference(IEnumerable<string?> shown, IEnumerable<string?> team)
    {
        Compare(shown, team, out List<string> missing, out List<string> extra);
        if (missing.Count == 0 && extra.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"{missing.Count} the host's lineup showed aren't in the run ({Names(missing)})");
        }

        if (extra.Count > 0)
        {
            parts.Add($"{extra.Count} in the run weren't on it ({Names(extra)})");
        }

        return string.Join("; ", parts);
    }

    private static void Compare(IEnumerable<string?> shown, IEnumerable<string?> team, out List<string> missing, out List<string> extra)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(team);
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string? id in shown)
        {
            if (!string.IsNullOrEmpty(id))
            {
                left[id] = left.GetValueOrDefault(id) + 1;
            }
        }

        extra = new List<string>();
        foreach (string? id in team)
        {
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (left.GetValueOrDefault(id) > 0)
            {
                left[id]--;
            }
            else
            {
                extra.Add(id);
            }
        }

        missing = left.Where(p => p.Value > 0).SelectMany(p => Enumerable.Repeat(p.Key, p.Value)).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static string Names(List<string> ids) =>
        string.Join(", ", ids.Take(MaxNamed).Select(id => Diagnostics.HostText.KeyForLog(id))) + (ids.Count > MaxNamed ? ", …" : string.Empty);
}
