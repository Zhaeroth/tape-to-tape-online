using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The untested-build notice worded for the way the game differs (newer, older, an update waiting, other files, unknown).</summary>
public class UntestedBuildNoticeTests
{
    private const long Tested = 26471720;
    private static readonly long[] Ours = { Tested };

    [Theory]
    [InlineData(26500000, 26500000, GameVersionAge.Newer)]
    [InlineData(26400000, 26400000, GameVersionAge.Older)]
    [InlineData(26400000, 26471720, GameVersionAge.UpdateWaiting)]
    [InlineData(26471720, 26500000, GameVersionAge.UpdateWaiting)]
    [InlineData(26471720, 26471720, GameVersionAge.SameSteamBuild)]
    [InlineData(26471720, 0, GameVersionAge.SameSteamBuild)]
    [InlineData(26500000, 0, GameVersionAge.Newer)]
    [InlineData(0, 0, GameVersionAge.Unknown)]
    [InlineData(-5, 26471720, GameVersionAge.Unknown)]
    public void The_way_the_game_differs_comes_from_steams_build_numbers(long installed, long target, GameVersionAge age)
    {
        Assert.Equal(age, UntestedBuildNotice.Age(installed, target, Ours));
    }

    [Fact]
    public void Between_two_tested_builds_or_with_none_it_cant_be_told()
    {
        Assert.Equal(GameVersionAge.Unknown, UntestedBuildNotice.Age(26450000, 26450000, new long[] { 26400000, 26471720 }));
        Assert.Equal(GameVersionAge.Unknown, UntestedBuildNotice.Age(26450000, 26450000, Array.Empty<long>()));
        Assert.Equal(GameVersionAge.Newer, UntestedBuildNotice.Age(26500000, 26500000, new long[] { 26400000, 26471720 }));
        Assert.Equal(GameVersionAge.Older, UntestedBuildNotice.Age(26300000, 26300000, new long[] { 26400000, 26471720 }));
    }

    [Fact]
    public void A_newer_game_or_an_unknown_one_names_the_releases_page()
    {
        foreach (GameVersionAge age in new[] { GameVersionAge.Newer, GameVersionAge.Unknown })
        {
            Assert.Contains(ProjectLinks.ReleasesShort, UntestedBuildNotice.Advice(age, ProjectLinks.ReleasesShort), StringComparison.Ordinal);
        }

        // An older game, or an update waiting, needs Steam, not a new release.
        Assert.Contains("Let Steam update", UntestedBuildNotice.Advice(GameVersionAge.Older, ProjectLinks.ReleasesShort), StringComparison.Ordinal);
        Assert.Contains("Let Steam finish", UntestedBuildNotice.Advice(GameVersionAge.UpdateWaiting, ProjectLinks.ReleasesShort), StringComparison.Ordinal);
        Assert.Contains("Verify integrity", UntestedBuildNotice.Advice(GameVersionAge.SameSteamBuild, ProjectLinks.ReleasesShort), StringComparison.Ordinal);
    }

    [Fact]
    public void The_unknown_text_covers_both_ways()
    {
        string advice = UntestedBuildNotice.Advice(GameVersionAge.Unknown, "example.org/releases");
        Assert.Contains("If Steam has an update for the game, let it finish", advice, StringComparison.Ordinal);
        Assert.Contains("a T2T Online made for this game version, at example.org/releases", advice, StringComparison.Ordinal);
        Assert.Equal("this game version isn't the one this T2T Online was made for", UntestedBuildNotice.Reason(GameVersionAge.Unknown));
        Assert.Equal("the game was updated after this T2T Online was made", UntestedBuildNotice.Reason(GameVersionAge.Newer));
    }

    [Fact]
    public void Every_text_says_the_game_plays_normally()
    {
        foreach (GameVersionAge age in Enum.GetValues<GameVersionAge>())
        {
            Assert.StartsWith("The game plays normally.", UntestedBuildNotice.Advice(age, ProjectLinks.ReleasesShort), StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(UntestedBuildNotice.Reason(age)));
        }
    }

    [Fact]
    public void The_log_says_the_numbers_and_never_the_address()
    {
        foreach (GameVersionAge age in Enum.GetValues<GameVersionAge>())
        {
            string line = UntestedBuildNotice.ForLog(age, 26500000, 26500000, Ours);
            Assert.DoesNotContain("github", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/", line, StringComparison.Ordinal);
        }

        Assert.Equal("the game is newer than this T2T Online's (Steam build 26500000; T2T Online's: 26471720)",
            UntestedBuildNotice.ForLog(GameVersionAge.Newer, 26500000, 26500000, Ours));
        Assert.Equal("Steam has build 26500000 waiting to install over 26471720 (T2T Online's: 26471720)",
            UntestedBuildNotice.ForLog(GameVersionAge.UpdateWaiting, 26471720, 26500000, Ours));
        Assert.Equal("whether the game is newer or older can't be told (no Steam app manifest beside the game folder)",
            UntestedBuildNotice.ForLog(GameVersionAge.Unknown, 0, 0, Ours, "no Steam app manifest beside the game folder"));
    }
}
