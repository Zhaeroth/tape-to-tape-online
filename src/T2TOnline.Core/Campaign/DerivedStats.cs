using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>How a derived stat is worked out from its two inputs.</summary>
public enum DerivedKind
{
    /// <summary>"{first}W-{second}L" (the game's <c>UpdateWonLostStats</c>).</summary>
    WonLost,

    /// <summary>first / (first + second), 0 when both are 0, kept between 0 and 1 (face-offs won, shot accuracy).</summary>
    Share,

    /// <summary>first / second, 0 when second is 0, kept between 0 and 1 (shot goal percentage, pass accuracy).</summary>
    Ratio,
}

/// <summary>A stat the game works out from two others rather than counting it.</summary>
/// <param name="Stat">The derived stat (the game's <c>StatType</c> number).</param>
/// <param name="Kind">How.</param>
/// <param name="First">The first input stat.</param>
/// <param name="Second">The second input stat.</param>
public sealed record DerivedStat(int Stat, DerivedKind Kind, int First, int Second);

/// <summary>
/// The stats the game sets outright from two of its own counters at the end of every match (Oct 29 build:
/// <c>EndOfGameStatsHandler.UpdateWonLostStats</c> and <c>UpdatePercentageStats</c> through
/// <c>UpdateCompoundStat&lt;T&gt;</c>): a record's "W-L" text and four percentages. They're
/// derived, so a friend's game works each out again from its own counters once a batch changed one of them, as its own
/// couch game would after the same match; the host's value is never sent. The lifetime value is worked out from the
/// lifetime counters, the campaign value from the campaign ones (the game does the second only in a match, where every one
/// of their counters changes).
/// </summary>
public static class DerivedStats
{
    /// <summary>The game's derived stats, in the order its end of a match works them out.</summary>
    public static IReadOnlyList<DerivedStat> All { get; } = new[]
    {
        new DerivedStat(3, DerivedKind.WonLost, 1, 2), // GamesWonLost from GamesWon, GamesLost
        new DerivedStat(7, DerivedKind.WonLost, 5, 6), // OTGamesWonLost from OTGamesWon, OTGamesLost
        new DerivedStat(81, DerivedKind.Share, 79, 80), // FaceOffsWinPercent from FaceOffsWon, FaceOffsLost
        new DerivedStat(47, DerivedKind.Ratio, 9, 45), // ShotGoalPercentage: TotalGoals over ShotsOnNet
        new DerivedStat(49, DerivedKind.Share, 45, 48), // ShotAccuracy from ShotsOnNet, MissedShots
        new DerivedStat(86, DerivedKind.Ratio, 85, 84), // PassAccuracy: PassesCompleted over PassesAttempted
    };

    /// <summary>True for a stat the game derives (a host never needs to send its value).</summary>
    public static bool IsDerived(int stat)
    {
        foreach (DerivedStat derived in All)
        {
            if (derived.Stat == stat)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The derived stats to work out again, and for which value, after the counters in <paramref name="changed"/> changed
    /// (each at most once, in <see cref="All"/>'s order; play-now and lifetime inputs give play-now and lifetime results).
    /// </summary>
    public static IReadOnlyList<(DerivedStat Stat, StatBucket Bucket)> Affected(IEnumerable<(int Stat, StatBucket Bucket)> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var touched = new HashSet<(int, StatBucket)>(changed);
        var result = new List<(DerivedStat, StatBucket)>();
        foreach (DerivedStat derived in All)
        {
            foreach (StatBucket bucket in new[] { StatBucket.Lifetime, StatBucket.Campaign, StatBucket.PlayNow })
            {
                if (touched.Contains((derived.First, bucket)) || touched.Contains((derived.Second, bucket)))
                {
                    result.Add((derived, bucket));
                }
            }
        }

        return result;
    }

    /// <summary>The value of a percentage (<see cref="DerivedKind.Share"/> or <see cref="DerivedKind.Ratio"/>), exactly as the game works it out.</summary>
    public static float Fraction(DerivedStat stat, int first, int second)
    {
        ArgumentNullException.ThrowIfNull(stat);

        // Single precision, as the game's code: (float)a / (float)b, then kept between 0 and 1 (0 for a zero denominator).
        int denominator = stat.Kind == DerivedKind.Share ? unchecked(first + second) : second;
        if (denominator == 0)
        {
            return 0f;
        }

        float value = (float)first / denominator;
        return value < 0f ? 0f : value > 1f ? 1f : value;
    }

    /// <summary>The text of a won-lost record (<see cref="DerivedKind.WonLost"/>), as the game writes it.</summary>
    public static string Text(DerivedStat stat, int first, int second)
    {
        ArgumentNullException.ThrowIfNull(stat);
        return string.Create(CultureInfo.InvariantCulture, $"{first}W-{second}L");
    }
}
