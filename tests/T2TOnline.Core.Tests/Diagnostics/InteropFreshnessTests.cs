using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class InteropFreshnessTests
{
    // The file's shape, as MelonLoader 0.7.3 writes it (with line endings of either kind).
    private const string Config =
        "[Il2CppAssemblyGenerator]\r\nGameAssemblyHash = \"AE71CF\"\r\nUnityVersion = \"6000.0.79\"\r\nOldFiles = [ \"Assembly-CSharp.dll\" ]\r\n";

    [Theory]
    [InlineData(Config, "AE71CF")]
    [InlineData("[Il2CppAssemblyGenerator]\nGameAssemblyHash=\"AB\"\n", "AB")]
    [InlineData("[Il2CppAssemblyGenerator]\nGameAssemblyHash = \"\"\n", null)]
    [InlineData("[Il2CppAssemblyGenerator]\nUnityVersion = \"6000.0.79\"\n", null)]
    [InlineData("GameAssemblyHashOther = \"AB\"\n", null)]
    [InlineData("", null)]
    public void The_recorded_hash_is_read_from_the_generators_config(string text, string? expected) =>
        Assert.Equal(expected, InteropFreshness.ReadGameAssemblyHash(text));

    [Theory]
    [InlineData(true, false, true)] // the force switch
    [InlineData(false, true, true)] // Cpp2IL or its plugin gone (an antivirus)
    [InlineData(false, false, false)]
    public void The_force_switch_and_a_missing_tool_regenerate_whatever_the_hash(bool forced, bool toolMissing, bool regenerates)
    {
        Assert.Equal(regenerates, InteropFreshness.WillRegenerate("AE71CF", "AE71CF", forced, toolMissing));
        Assert.Equal(regenerates, InteropFreshness.Why("AE71CF", "AE71CF", forced, toolMissing).Length > 0);
    }

    [Fact]
    public void A_changed_game_regenerates_with_its_reason() =>
        Assert.Equal("the game changed since MelonLoader made them", InteropFreshness.Why("AE71CF", "F756BC", false, false));

    [Fact]
    public void The_same_game_is_left_alone() =>
        Assert.False(InteropFreshness.WillRegenerate("AE71CF", "AE71CF"));

    [Theory]
    [InlineData("AE71CF", "F756BC")] // a game update
    [InlineData("", "AE71CF")] // the generator has never finished
    [InlineData(null, "AE71CF")]
    [InlineData("ae71cf", "AE71CF")] // the generator compares exactly, so this does too
    public void The_generator_runs_whenever_the_hash_isnt_exactly_the_games(string? recorded, string current) =>
        Assert.True(InteropFreshness.WillRegenerate(recorded, current));
}
