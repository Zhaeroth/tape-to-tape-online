#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Friend: what this PC's own stream of the host's match showed, for the shared moments (D135, <see cref="MatchView"/>):
/// the score its HUD last showed (the HUD channel's <c>score.home</c> and <c>score.visitor</c>, which the host sends in
/// full every few seconds, and right before each event) and three of the stream's events: the match's end
/// (<c>GameplayEvents.EndOfGame</c>, which the game's <c>GameManager.RequestEndOfGame</c> raises, after the last goal's
/// replay when a goal ends it), the mercy rule on an elite (<c>RogueEvents.WinByMercy</c> with its flag set) and a
/// headshot goal (<c>RogueEvents.HeadshotGoal</c>). Fed by the HUD and event channels as they play
/// (managed values only: no interop), started over with each playback, read by the session once a frame. Never throws.
/// </summary>
internal static class HostMatchWitness
{
    private const string HomeScoreKey = "score.home";
    private const string VisitorScoreKey = "score.visitor";
    private const string MercyEvent = "RogueEvents.WinByMercy";
    private const string HeadshotEvent = "RogueEvents.HeadshotGoal";
    private const string EndEvent = "GameplayEvents.EndOfGame";

    /// <summary>The home team's goals as the HUD last showed them (-1: none yet in this playback).</summary>
    public static int HomeGoals { get; private set; } = -1;

    /// <summary>The visitors' goals as the HUD last showed them (-1: none yet in this playback).</summary>
    public static int VisitorGoals { get; private set; } = -1;

    /// <summary>The signs this playback's events carried.</summary>
    public static MatchSigns Signs { get; private set; }

    /// <summary>True once this playback's stream carried the match's end (its score is final by then).</summary>
    public static bool EndSeen { get; private set; }

    /// <summary>A new playback of a host's match starts: nothing is known of it yet.</summary>
    public static void Begin()
    {
        HomeGoals = VisitorGoals = -1;
        Signs = MatchSigns.None;
        EndSeen = false;
    }

    /// <summary>A HUD value from the host's stream (every one passes: two compares).</summary>
    public static void NoteHud(string key, NetValue value)
    {
        if (value.Kind != NetValueKind.Whole || value.WholeValue is < 0 or > MatchView.MaxNumber)
        {
            return;
        }

        if (string.Equals(key, HomeScoreKey, StringComparison.Ordinal))
        {
            HomeGoals = (int)value.WholeValue;
        }
        else if (string.Equals(key, VisitorScoreKey, StringComparison.Ordinal))
        {
            VisitorGoals = (int)value.WholeValue;
        }
    }

    /// <summary>An event from the host's stream, by name, with its values as sent (every one passes: three compares).</summary>
    public static void NoteEvent(string name, IReadOnlyList<NetValue> values)
    {
        if (string.Equals(name, EndEvent, StringComparison.Ordinal))
        {
            EndSeen = true;
        }
        else if (string.Equals(name, MercyEvent, StringComparison.Ordinal))
        {
            // Its one value: the game's isElite (MERCY unlocks only for an elite's mercy win).
            if (values.Count == 1 && values[0].Kind == NetValueKind.Flag && values[0].WholeValue != 0)
            {
                Signs |= MatchSigns.EliteMercy;
            }
        }
        else if (string.Equals(name, HeadshotEvent, StringComparison.Ordinal))
        {
            Signs |= MatchSigns.HeadshotGoal;
        }
    }
}
#endif
