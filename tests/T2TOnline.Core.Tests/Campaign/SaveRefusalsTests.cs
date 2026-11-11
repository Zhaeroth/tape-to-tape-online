using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// Which refused save writes are the guard's job and which are leaks (the guard refuses config.es3 at each match's load and
/// profileV2.es3 when a reward menu opens on a friend: those are no leak).
/// </summary>
public class SaveRefusalsTests
{
    [Theory]
    [InlineData("config.es3")]
    [InlineData("profileV2.es3")]
    [InlineData("PROFILEV2.ES3")]
    [InlineData("profileV2.es3.bac")]
    [InlineData("profileV2.es3.tmp")]
    [InlineData("statsV2.es3")]
    public void The_game_s_settings_profile_and_stats_are_expected(string file) =>
        Assert.Equal(RefusalKind.Expected, SaveRefusals.Classify(file));

    [Theory]
    [InlineData("RunDataV2.es3")]
    [InlineData("RunDataV2.es3.tmp.bak")]
    [InlineData("HostRun.es3")]
    [InlineData("coop-1.es3")]
    [InlineData("coop-3.es3.bac")]
    public void A_run_file_is_a_run_leak(string file) =>
        Assert.Equal(RefusalKind.Run, SaveRefusals.Classify(file));

    [Theory]
    [InlineData("GameplaySettings.es3")]
    [InlineData("RunHistoryIndex.es3")]
    [InlineData("lifetime-stats.es3")]
    [InlineData("profileV2.es3.bac.tmp")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_or_no_file_stays_a_leak(string? file) =>
        Assert.Equal(RefusalKind.Other, SaveRefusals.Classify(file));

    [Fact]
    public void A_write_naming_several_files_is_the_worst_of_them()
    {
        Assert.Equal(RefusalKind.Expected, SaveRefusals.Classify(new[] { "profileV2.es3", "profileV2.es3.bac" }));
        Assert.Equal(RefusalKind.Other, SaveRefusals.Classify(new[] { "profileV2.es3", "GameplaySettings.es3" }));
        Assert.Equal(RefusalKind.Run, SaveRefusals.Classify(new[] { "GameplaySettings.es3", "RunDataV2.es3", "config.es3" }));
        Assert.Equal(RefusalKind.Run, SaveRefusals.Classify(new[] { "RunDataV2.es3", "GameplaySettings.es3" }));
    }

    [Fact]
    public void A_write_naming_no_file_is_other()
    {
        Assert.Equal(RefusalKind.Other, SaveRefusals.Classify(Array.Empty<string?>()));
        Assert.Equal(RefusalKind.Other, SaveRefusals.Classify(new string?[] { null, "" }));
    }
}
