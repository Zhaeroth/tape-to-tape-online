using System;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>A friend's reasons for not loading or playing the host's match: their words, and what the host's log keeps of them.</summary>
public class MatchLoadFailuresTests
{
    // A fixed reason of this build's (the check tour's stand-in sends one): written as it is.
    private static readonly string[] Fixed = { "the check tour's stand-in reports that it couldn't load this match" };

    [Fact]
    public void Each_kind_makes_the_words_a_friend_has_always_sent()
    {
        // The friend's own log, its screen and the bytes the host gets read as they always did.
        Assert.Equal("couldn't load the host's match: its arena isn't here", MatchLoadFailures.Reason(MatchLoadFailure.NotLoaded, "its arena isn't here"));
        Assert.Equal("the host's match didn't load within 90 s (matches loaded so far: 2; still waiting for it)",
            MatchLoadFailures.Reason(MatchLoadFailure.NotLoadedInTime, "90 s (matches loaded so far: 2; still waiting for it)"));
        Assert.Equal("couldn't play the host's match: no replay scene", MatchLoadFailures.Reason(MatchLoadFailure.NotPlayed, "no replay scene"));
        Assert.Equal("playback of the host's match stopped here (the match unloaded)", MatchLoadFailures.Reason(MatchLoadFailure.PlaybackStopped, "the match unloaded"));
        Assert.Equal("following the host's match failed (NullReferenceException); see the log",
            MatchLoadFailures.Reason(MatchLoadFailure.FollowingFailed, "NullReferenceException"));
        Assert.Equal("playback of the host's match stopped here ()", MatchLoadFailures.Reason(MatchLoadFailure.PlaybackStopped, null));
    }

    [Fact]
    public void The_hosts_log_keeps_every_kinds_opening_words_and_only_the_length_of_the_rest()
    {
        foreach (MatchLoadFailure kind in Enum.GetValues<MatchLoadFailure>())
        {
            string leadIn = MatchLoadFailures.LeadIn(kind);
            Assert.Contains(leadIn, MatchLoadFailures.LeadIns);
            Assert.DoesNotContain(leadIn, Fixed);

            // A modified friend's own words after the opening ones never read.
            string sent = MatchLoadFailures.Reason(kind, "Riley was here");
            string logged = MatchLoadFailures.ForLog(sent, Fixed);
            Assert.Equal($"{leadIn}[{sent.Length - leadIn.Length} more character(s)]", logged);
            Assert.DoesNotContain("Riley", logged, StringComparison.Ordinal);
        }

        Assert.Equal(Enum.GetValues<MatchLoadFailure>().Length, MatchLoadFailures.LeadIns.Count);
        Assert.Equal("couldn't load the host's match: [20 more character(s)]",
            MatchLoadFailures.ForLog("couldn't load the host's match: its arena isn't here", Fixed));
    }

    [Fact]
    public void A_fixed_reason_reads_whole_and_any_other_by_its_length()
    {
        Assert.Equal(Fixed[0], MatchLoadFailures.ForLog(Fixed[0], Fixed));
        Assert.Equal("a reason of 14 character(s)", MatchLoadFailures.ForLog("Riley was here", Fixed));
        Assert.Equal("(none)", MatchLoadFailures.ForLog(string.Empty, Fixed));
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchLoadFailures.Reason((MatchLoadFailure)99, "x"));
    }
}
