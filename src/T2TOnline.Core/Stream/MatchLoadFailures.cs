using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Stream;

/// <summary>What kind of failure stopped a friend's game loading or playing the host's match (<see cref="MatchLoadFailures"/>).</summary>
public enum MatchLoadFailure
{
    /// <summary>The host's setup couldn't be loaded here: "couldn't load the host's match: ...".</summary>
    NotLoaded,

    /// <summary>The load didn't end in time: "the host's match didn't load within ...".</summary>
    NotLoadedInTime,

    /// <summary>The loaded match couldn't be played: "couldn't play the host's match: ...".</summary>
    NotPlayed,

    /// <summary>Playback stopped while the host's match went on: "playback of the host's match stopped here (...)".</summary>
    PlaybackStopped,

    /// <summary>An error while following the host's match: "following the host's match failed (...); see the log".</summary>
    FollowingFailed,
}

/// <summary>
/// A friend's reasons for not loading or playing the host's match, as the friend makes them and as the host's log writes
/// them. A reason is its kind's opening words, a part that varies (what the game said, a time, an error's type) and the
/// kind's closing words; the friend's log and the host's screen have it in full. The host's log writes the opening words
/// and only the length of the rest (<see cref="ForLog"/>): a reason can quote the host's setup, and a modified friend can
/// send any text. A reason is only made through its kind (<see cref="Reason"/>), so its opening words are always in
/// <see cref="LeadIns"/>. Game-independent; never throws but for a kind that isn't one.
/// </summary>
public static class MatchLoadFailures
{
    /// <summary>Every kind's opening words, as the host's log keeps them.</summary>
    public static IReadOnlyList<string> LeadIns { get; } = Enum.GetValues<MatchLoadFailure>().Select(LeadIn).ToArray();

    /// <summary>The opening words of <paramref name="kind"/>'s reason.</summary>
    public static string LeadIn(MatchLoadFailure kind) => kind switch
    {
        MatchLoadFailure.NotLoaded => "couldn't load the host's match: ",
        MatchLoadFailure.NotLoadedInTime => "the host's match didn't load within ",
        MatchLoadFailure.NotPlayed => "couldn't play the host's match: ",
        MatchLoadFailure.PlaybackStopped => "playback of the host's match stopped here (",
        MatchLoadFailure.FollowingFailed => "following the host's match failed (",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a kind of match load failure"),
    };

    /// <summary>
    /// A reason of <paramref name="kind"/> with <paramref name="detail"/> in it (none: nothing): "couldn't load the host's
    /// match: it has no arena", "playback of the host's match stopped here (the match unloaded)".
    /// </summary>
    public static string Reason(MatchLoadFailure kind, string? detail)
    {
        string closing = kind switch
        {
            MatchLoadFailure.PlaybackStopped => ")",
            MatchLoadFailure.FollowingFailed => "); see the log",
            _ => string.Empty,
        };
        return LeadIn(kind) + detail + closing;
    }

    /// <summary>
    /// A friend's reason for the host's log: one of <paramref name="fixedReasons"/> (this build's reasons that are always the
    /// same words) as it is, one that begins with a kind's opening words as those words and the length of the rest
    /// (<c>couldn't load the host's match: [24 more character(s)]</c>), any other by its length.
    /// </summary>
    public static string ForLog(string? sent, IReadOnlyCollection<string> fixedReasons) => SentText.Reason(sent, fixedReasons, LeadIns);
}
