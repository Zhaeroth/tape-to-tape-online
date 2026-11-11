using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Session;

/// <summary>
/// The achievements the game unlocks straight from a match or run event (D34, D48), by their Steam API names: the ones a host
/// shares with the friends who shared the play they came from. Every other achievement counts something on a player's own
/// profile (a lifetime of hat tricks, the bench quests, every squad won, and the unlock registry's, which follow the
/// profile's unlocks), and each player earns those from their own progress.
/// <list type="bullet">
/// <item><c>AchievementManager.OnWinByMercy</c>: MERCY (an elite's mercy win).</item>
/// <item><c>AchievementManager.OnGoalScored</c>: SCORING_SQUAD (every skater scored).</item>
/// <item><c>AchievementManager.OnGameWin</c>, after the end-of-game animation: ELITE_WIN (an elite match won) and WIN_ACT2
/// (act 2's boss match won). Act 1's boss win unlocks nothing there on the Nov 1 build.</item>
/// <item><c>AchievementManager.OnHeadshotGoal</c>: HEADSHOT_REDIRECT.</item>
/// <item><c>AchievementManager.OnRunWon</c>: WIN_ACT3 (the run won).</item>
/// <item><c>AchievementManager.OnGawainEvolved</c>: GAWAIN (a boss win evolves him).</item>
/// </list>
/// Game-independent.
/// </summary>
public static class SharedMoments
{
    private static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        "MERCY", "SCORING_SQUAD", "ELITE_WIN", "WIN_ACT2", "HEADSHOT_REDIRECT", "WIN_ACT3", "GAWAIN",
    };

    /// <summary>The shared moments' Steam API names.</summary>
    public static IReadOnlySet<string> Names => All;

    /// <summary>True when <paramref name="name"/> is a shared moment's Steam API name (ordinal).</summary>
    public static bool Contains(string? name) => name != null && All.Contains(name);

    /// <summary>
    /// What a shared moment comes from: the run's win (WIN_ACT3) and Gawain's evolution (GAWAIN, from a boss win's evolution
    /// of the run's squad) are the run's; the rest are the match they were won in.
    /// </summary>
    public static MomentSource SourceOf(string name) => name is "WIN_ACT3" or "GAWAIN" ? MomentSource.Run : MomentSource.Match;
}
