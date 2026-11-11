using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class OwnSettingsFilesTests
{
    [Theory]
    [InlineData("config.es3")]
    [InlineData("GameplaySettings.es3")]
    [InlineData("gameplaysettings.ES3")]
    [InlineData("config.es3.tmp")]
    [InlineData("config.es3.bac")]
    [InlineData("GameplaySettings.es3.tmp.bak")]
    [InlineData(@"C:\Users\x\AppData\LocalLow\Excellent Rectangle\Tape to Tape\config.es3")]
    [InlineData("/home/x/.config/GameplaySettings.es3")]
    public void The_settings_menus_own_files_and_their_copies_are_allowed(string path)
    {
        Assert.True(OwnSettingsFiles.Allows(path));
    }

    [Theory]
    [InlineData("RunDataV2.es3")]
    [InlineData("RunDataV2.es3.tmp")]
    [InlineData("profileV2.es3")]
    [InlineData("profile.es3")]
    [InlineData("lifetime-stats.es3")]
    [InlineData("RunHistoryIndex.es3")]
    [InlineData("config.es3.bac.tmp")] // nobody's copy
    [InlineData("config.es3x")]
    [InlineData("xconfig.es3")]
    [InlineData("config")]
    [InlineData("")]
    [InlineData(null)]
    public void Run_data_profiles_stats_and_anything_else_are_not(string? path)
    {
        Assert.False(OwnSettingsFiles.Allows(path));
    }

    [Fact]
    public void The_names_are_the_two_settings_files()
    {
        Assert.Equal(new[] { "config.es3", "GameplaySettings.es3" }, OwnSettingsFiles.Names);
        Assert.DoesNotContain(OwnSettingsFiles.Names, name => OwnSettingsFiles.Allows(name.Replace(".es3", ".json", System.StringComparison.Ordinal)));
    }
}
