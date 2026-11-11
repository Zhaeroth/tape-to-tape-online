using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Session;

/// <summary>What a shared achievement moment comes from (D34, D48).</summary>
public enum MomentSource
{
    /// <summary>A match (a mercy win, every skater scoring...): shared by the friends who watched that match's stream.</summary>
    Match,

    /// <summary>The run (its win, a boss win): shared by the friends in the session while the host plays the shared co-op run.</summary>
    Run,
}

/// <summary>What <see cref="AchievementLedger.Record"/> decided for a moment.</summary>
/// <param name="FirstTime">True the first time the host earned it in this hosting session.</param>
/// <param name="NewlyOwed">The friends it's owed to from now on (send it to those in the session now).</param>
/// <param name="NotShared">The friends in the session who didn't share the play it came from (it isn't theirs).</param>
public readonly record struct MomentShares(bool FirstTime, IReadOnlyList<ulong> NewlyOwed, IReadOnlyList<ulong> NotShared);

/// <summary>
/// The host's record of the shared moments earned while hosting (D34, D48), per friend: a
/// moment is owed to the friends who shared the play it came from. A match moment is theirs if they watched that match's
/// stream (a friend who dropped out of it, whose player the AI kept playing, still did); a run moment is everyone's in the
/// session while the host plays the shared co-op run, and nobody's on the host's solo run. Each friend is sent what they're
/// owed when it's earned, and all of it again whenever they join again: a dropped friend gets what was earned while they
/// were gone, a friend whose game restarted gets it all again, each with the match it came from and whether it came from the
/// run (<see cref="AchievementMoment"/>: the friend unlocks it only for play its own game shared, D135). Sending one twice is
/// harmless (the friend's game takes each moment of a match once). The game unlocks a moment every time its condition is
/// met, and a friend's game may refuse the first one for good (it joined that match in its last seconds, or dropped before
/// its end): earned again in a later match the friend watches, a moment is owed to them for that match instead, and goes
/// to them again.
/// </summary>
public sealed class AchievementLedger
{
    private readonly List<string> _earned = new();
    private readonly Dictionary<ulong, int> _watched = new();
    private readonly Dictionary<ulong, List<AchievementMoment>> _owed = new();

    /// <summary>Achievements earned while hosting, in order (each once).</summary>
    public IReadOnlyList<string> Earned => _earned;

    /// <summary>
    /// <paramref name="friend"/> is watching the stream of the host's match <paramref name="match"/> (the number the host's
    /// game loaded it as). Call every frame while they do: the newest match each friend watched is remembered.
    /// </summary>
    public void Watching(ulong friend, int match)
    {
        if (match > 0)
        {
            _watched[friend] = match;
        }
    }

    /// <summary>
    /// True when <paramref name="friend"/> shared the play a moment from <paramref name="source"/> came from: for a match
    /// moment, they watched match <paramref name="match"/>; for a run moment, the host plays the shared co-op run
    /// (<paramref name="runShared"/>).
    /// </summary>
    public bool Shared(ulong friend, MomentSource source, int match, bool runShared) =>
        source == MomentSource.Run ? runShared : Watched(friend, match);

    /// <summary>
    /// The host earned <paramref name="name"/> from <paramref name="source"/> (a match moment from match
    /// <paramref name="match"/>; a run moment while <paramref name="runShared"/> says whether the run is the shared one), with
    /// <paramref name="inSession"/> the friends in the session, dropped ones included. It's owed to each of them who shared
    /// that play, as the moment it is now (its match, and whether it's the run's). A friend is owed each moment once, for
    /// one match: the first it was earned in with them, until it's earned in a later match they watch, which takes its
    /// place (their game judges each match's moment on its own, and may have refused the earlier one). Earned again in the
    /// same match, or in a later one they don't watch, it's owed as before and nothing is new.
    /// </summary>
    public MomentShares Record(string name, MomentSource source, int match, bool runShared, IEnumerable<ulong> inSession)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(inSession);
        bool firstTime = !_earned.Contains(name);
        if (firstTime)
        {
            _earned.Add(name);
        }

        var moment = new AchievementMoment(name, Math.Max(0, match), source == MomentSource.Run);
        var newlyOwed = new List<ulong>();
        var notShared = new List<ulong>();
        foreach (ulong friend in inSession)
        {
            if (!Shared(friend, source, match, runShared))
            {
                notShared.Add(friend);
                continue;
            }

            if (!_owed.TryGetValue(friend, out List<AchievementMoment>? owed))
            {
                owed = new List<AchievementMoment>();
                _owed[friend] = owed;
            }

            int at = IndexOf(owed, name);
            if (at < 0)
            {
                owed.Add(moment);
                newlyOwed.Add(friend);
            }
            else if (moment.Match > owed[at].Match && Watched(friend, moment.Match))
            {
                // Earned again in a later match they watch: owed for that one from now on (in the same place: the order
                // earned stays), and sent again. Their game takes each match's moment once, and unlocks what it holds
                // already as "already yours".
                owed[at] = moment;
                newlyOwed.Add(friend);
            }
        }

        return new MomentShares(firstTime, newlyOwed, notShared);
    }

    /// <summary>True when the newest match <paramref name="friend"/> watched is <paramref name="match"/>.</summary>
    private bool Watched(ulong friend, int match) => match > 0 && _watched.TryGetValue(friend, out int watched) && watched == match;

    /// <summary>Where the moment named <paramref name="name"/> is in <paramref name="owed"/>, or -1.</summary>
    private static int IndexOf(List<AchievementMoment> owed, string name)
    {
        for (int i = 0; i < owed.Count; i++)
        {
            if (string.Equals(owed[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Everything owed to <paramref name="friend"/>, in the order earned, each with the play it came from: what they're sent
    /// when they join again.
    /// </summary>
    public IReadOnlyList<AchievementMoment> OwedTo(ulong friend) =>
        _owed.TryGetValue(friend, out List<AchievementMoment>? owed) ? owed.ToArray() : Array.Empty<AchievementMoment>();

    /// <summary>Forgets everything (hosting stopped).</summary>
    public void Clear()
    {
        _earned.Clear();
        _watched.Clear();
        _owed.Clear();
    }
}
