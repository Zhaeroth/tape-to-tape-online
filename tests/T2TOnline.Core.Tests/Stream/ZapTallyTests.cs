using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>A friend's zap lines in one match: what is written as it happens, and the match's closing line.</summary>
public class ZapTallyTests
{
    [Fact]
    public void An_honest_match_closes_with_the_line_it_always_had()
    {
        var tally = new ZapTally();
        Assert.Null(tally.EndLine());

        tally.AddZapped();
        tally.AddZapped();
        tally.AddReverted();
        Assert.Equal("Zaps: 2 of the host's zap(s) and 1 revert(s) applied to this PC's skaters.", tally.EndLine());

        // A few zaps into a skater this PC lacks are each written as they come; the closing line is unchanged.
        Assert.True(tally.AddNotFollowed());
        Assert.True(tally.AddNotFollowed());
        Assert.Equal("Zaps: 2 of the host's zap(s) and 1 revert(s) applied to this PC's skaters.", tally.EndLine());
    }

    [Fact]
    public void Only_a_matchs_first_few_zaps_into_a_skater_this_pc_lacks_are_written_and_the_rest_are_counted()
    {
        var tally = new ZapTally();
        for (int i = 0; i < ZapTally.NotFollowedSaid; i++)
        {
            Assert.True(tally.AddNotFollowed());
        }

        // A host sending unknown forms without end: not one more line until the match closes, and then one.
        for (int i = 0; i < 1000; i++)
        {
            Assert.False(tally.AddNotFollowed());
        }

        Assert.Equal((1003, 1000), (tally.NotFollowed, tally.NotFollowedUnsaid));
        Assert.Equal("Zaps: 0 of the host's zap(s) and 0 revert(s) applied to this PC's skaters; 1000 more zap(s) into a skater this PC " +
            "doesn't have weren't followed (only a match's first 3 are written).", tally.EndLine());
    }

    [Fact]
    public void A_new_match_starts_its_budget_again()
    {
        var tally = new ZapTally();
        for (int i = 0; i < 5; i++)
        {
            tally.AddNotFollowed();
        }

        tally.AddZapped();
        tally.Begin();
        Assert.Equal((0, 0, 0), (tally.Zapped, tally.Reverted, tally.NotFollowed));
        Assert.Null(tally.EndLine());
        Assert.True(tally.AddNotFollowed());
    }
}
