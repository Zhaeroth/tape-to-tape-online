using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class DerivedStatsTests
{
    private static DerivedStat Of(int stat) => DerivedStats.All.Single(d => d.Stat == stat);

    [Fact]
    public void The_games_derived_stats_and_their_inputs()
    {
        // Oct 29 build: UpdateWonLostStats (GamesWon, GamesLost -> GamesWonLost; OT likewise), UpdatePercentageStats with
        // FaceOffs 79/80 -> 81, TotalGoals/ShotsOnNet -> 47, ShotsOnNet/MissedShots -> 49, PassesAttempted/Completed -> 86.
        Assert.Equal(new[] { 3, 7, 81, 47, 49, 86 }, DerivedStats.All.Select(d => d.Stat));
        Assert.True(DerivedStats.IsDerived(86));
        Assert.False(DerivedStats.IsDerived(9));
    }

    [Fact]
    public void A_won_lost_record_reads_as_the_games_text()
    {
        Assert.Equal("9W-1L", DerivedStats.Text(Of(3), 9, 1));
        Assert.Equal("0W-0L", DerivedStats.Text(Of(7), 0, 0));
    }

    [Fact]
    public void Percentages_are_worked_out_as_the_games_code_does()
    {
        // Share: won / (won + lost); 0 for none; kept between 0 and 1.
        Assert.Equal(19f / 37f, DerivedStats.Fraction(Of(81), 19, 18));
        Assert.Equal(0f, DerivedStats.Fraction(Of(81), 0, 0));
        Assert.Equal(1f, DerivedStats.Fraction(Of(49), 5, 0));

        // Ratio: goals over shots on net, completed over attempted; 0 for a zero denominator; never above 1.
        Assert.Equal(23f / 153f, DerivedStats.Fraction(Of(47), 23, 153));
        Assert.Equal(231f / 243f, DerivedStats.Fraction(Of(86), 231, 243));
        Assert.Equal(0f, DerivedStats.Fraction(Of(47), 4, 0));
        Assert.Equal(1f, DerivedStats.Fraction(Of(86), 300, 243));
        Assert.Equal(0f, DerivedStats.Fraction(Of(86), -3, 243));
    }

    [Fact]
    public void Only_the_derived_stats_whose_inputs_changed_are_worked_out_again_for_each_value()
    {
        var affected = DerivedStats.Affected(new[]
        {
            (1, StatBucket.Lifetime), (1, StatBucket.Campaign), // GamesWon
            (45, StatBucket.Campaign), // ShotsOnNet: shot goal percentage and shot accuracy
            (12, StatBucket.Campaign), // HatTricks: nothing derived from it
        });

        Assert.Equal(new[] { (3, StatBucket.Lifetime), (3, StatBucket.Campaign), (47, StatBucket.Campaign), (49, StatBucket.Campaign) },
            affected.Select(a => (a.Stat.Stat, a.Bucket)));
        Assert.Equal(new[] { (47, StatBucket.PlayNow) }, DerivedStats.Affected(new[] { (9, StatBucket.PlayNow) }).Select(a => (a.Stat.Stat, a.Bucket)));
    }
}
