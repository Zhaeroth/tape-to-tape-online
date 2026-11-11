using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.6.0-m4+3c337e6f0a1b2c3d4e5f", "0.6.0-m4")]
    [InlineData("0.6.0-m4", "0.6.0-m4")]
    [InlineData(" 0.6.1-m4.1 +abc", "0.6.1-m4.1")]
    [InlineData("", ReleaseVersion.Unknown)]
    [InlineData("+abc", ReleaseVersion.Unknown)]
    [InlineData(null, ReleaseVersion.Unknown)]
    public void Release_is_the_version_before_the_build_metadata(string? informational, string release) =>
        Assert.Equal(release, ReleaseVersion.Release(informational));

    [Theory]
    [InlineData("0.9.2-beta", "0.9.2 Beta")]
    [InlineData("0.9.2-beta+3c337e6f", "0.9.2 Beta")]
    [InlineData("0.9.2-BETA", "0.9.2 Beta")]
    [InlineData("0.9.1-m4", "0.9.1-m4")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("", ReleaseVersion.Unknown)]
    [InlineData(null, ReleaseVersion.Unknown)]
    public void A_beta_reads_as_a_word_on_screen_and_anything_else_as_it_is(string? release, string shown) =>
        Assert.Equal(shown, ReleaseVersion.ForScreen(release));

    [Theory]
    [InlineData("0.6.0-m4+3c337e6f0a1b2c3d4e5f", "3c337e6f0a1b")]
    [InlineData("0.6.0-m4+abc", "abc")]
    [InlineData("0.6.0-m4", "")]
    [InlineData(null, "")]
    public void Commit_is_the_build_metadata_cut_short(string? informational, string commit) =>
        Assert.Equal(commit, ReleaseVersion.Commit(informational));

    [Fact]
    public void Commit_needs_a_positive_length() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseVersion.Commit("1.0+abc", 0));

    [Theory]
    [InlineData("0.6.0-m4+aaa", "0.6.0-m4+bbb", true)]
    [InlineData("0.6.0-m4", "0.6.0-M4", true)]
    [InlineData("0.6.0-m4", "0.6.0-m4.1", false)]
    [InlineData("0.6.0-m4", "0.6.0", false)]
    [InlineData("", "", false)]
    [InlineData(null, "0.6.0-m4", false)]
    public void Same_release_ignores_build_metadata_and_never_matches_a_missing_version(string? one, string? other, bool same) =>
        Assert.Equal(same, ReleaseVersion.SameRelease(one, other));
}
