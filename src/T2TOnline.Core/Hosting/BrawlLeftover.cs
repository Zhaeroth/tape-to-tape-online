using System.Collections.Generic;

namespace T2TOnline.Core.Hosting;

/// <summary>Which of the game's two pieces of fight state the mod ends after a match (<see cref="BrawlLeftover.Plan"/>).</summary>
/// <param name="EndMatchmaker">The fight matchmaker is on: <c>BrawlMatchmakerSystem.EndBrawl</c>.</param>
/// <param name="EndHud">The brawl HUD's flag is on: <c>FightHpBarSystem.EndBrawl</c>.</param>
public readonly record struct BrawlLeftoverPlan(bool EndMatchmaker, bool EndHud)
{
    /// <summary>True when either is to be ended.</summary>
    public bool Acts => EndMatchmaker || EndHud;
}

/// <summary>What the mod's end of a match's left-over fight state came to: its one log line, and whether it is clean.</summary>
/// <param name="Clean">Both pieces off afterwards and no call failed: an INFO line; otherwise a WARN line.</param>
/// <param name="Line">The log line (no player, no name).</param>
public readonly record struct BrawlLeftoverReport(bool Clean, string Line);

/// <summary>
/// The game's fight state that a match leaves on when it ends during a fight. The game keeps two static pieces of a brawl:
/// the fight matchmaker (<c>BrawlMatchmakerSystem</c>, on while its matchmaker is set) and the brawl HUD's flag
/// (<c>FightHpBarSystem.IsBrawlActive</c>). Only a fight's own end sequence clears them, so a match conceded or quit during a
/// fight leaves both on, and while the matchmaker is on the game gives no player a skater that wasn't in the old fight
/// (<c>Player.TakeControlOfSkater</c>); a friend's mirrored brawl HUD can be left on the same way when their match goes
/// first. At a match scene's unload the mod ends what is on with the game's own end calls, in the end sequence's order
/// (the matchmaker, then the HUD), unless a live match is fighting or an accepted brawl is about to begin.
/// </summary>
public static class BrawlLeftover
{
    /// <summary>
    /// Which pieces to end: each one that is on, and none while a live match is in its fight state
    /// (<paramref name="matchFighting"/>) or an accepted brawl waits to begin (<paramref name="startPending"/>), whose fight
    /// the state belongs to.
    /// </summary>
    public static BrawlLeftoverPlan Plan(bool matchmakerOn, bool hudUp, bool matchFighting, bool startPending) =>
        matchFighting || startPending ? default : new BrawlLeftoverPlan(matchmakerOn, hudUp);

    /// <summary>True when a piece is on but a live match is fighting (or about to): the state is that fight's, left alone.</summary>
    public static bool LeftToLiveFight(bool matchmakerOn, bool hudUp, bool matchFighting, bool startPending) =>
        (matchmakerOn || hudUp) && (matchFighting || startPending);

    /// <summary>The line for <see cref="LeftToLiveFight"/>: what is on, and that the fight under way keeps it.</summary>
    public static string LeftAloneLine(bool matchmakerOn, bool hudUp) =>
        $"Brawl: a match's scene unloaded with the game's fight state on ({Pieces(matchmakerOn, hudUp, "was on", "was up")}) while a live match is fighting or about to: left to that fight.";

    /// <summary>
    /// The one line after the calls of <paramref name="plan"/>: what was on, what the game's two flags read afterwards
    /// (<paramref name="matchmakerOnAfter"/>, <paramref name="hudUpAfter"/>), and the first call that failed
    /// (<paramref name="problem"/>, or null). Clean when both are off and nothing failed. Only the matchmaker left on harms
    /// the next match (no player can take a skater while it is), so the line's end follows it.
    /// </summary>
    public static BrawlLeftoverReport Report(BrawlLeftoverPlan plan, bool matchmakerOnAfter, bool hudUpAfter, string? problem)
    {
        bool clean = !matchmakerOnAfter && !hudUpAfter && string.IsNullOrEmpty(problem);
        string before = Pieces(plan.EndMatchmaker, plan.EndHud, "was on", "was up");
        if (clean)
        {
            string after = plan.EndMatchmaker && plan.EndHud ? "the next match's players take skaters and the skaters' icons show as usual"
                : plan.EndMatchmaker ? "the next match's players take skaters as usual"
                : "the skaters' icons show as usual";
            return new BrawlLeftoverReport(true, $"Brawl: a match ended during a fight; the game's fight state ended by its own calls ({before}): {after}.");
        }

        string state = $"matchmaker {(matchmakerOnAfter ? "still on" : "off")}, brawl HUD {(hudUpAfter ? "still up" : "down")}";
        string means = matchmakerOnAfter ? "the next match may start with no player on a skater"
            : hudUpAfter ? "the next match's players take skaters, but the skaters' icons may stay hidden"
            : "both are off now, so the next match plays as usual";
        return new BrawlLeftoverReport(false,
            $"Brawl: a match ended during a fight ({before}); the game's own calls didn't end all of its fight state ({(string.IsNullOrEmpty(problem) ? string.Empty : problem + "; ")}{state}): {means}.");
    }

    // "matchmaker was on, brawl HUD was up", or one of them.
    private static string Pieces(bool matchmaker, bool hud, string on, string up)
    {
        var pieces = new List<string>(2);
        if (matchmaker)
        {
            pieces.Add($"matchmaker {on}");
        }

        if (hud)
        {
            pieces.Add($"brawl HUD {up}");
        }

        return pieces.Count == 0 ? "nothing on" : string.Join(", ", pieces);
    }
}
