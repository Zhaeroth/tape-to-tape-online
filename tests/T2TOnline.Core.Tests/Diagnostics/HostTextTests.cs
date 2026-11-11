using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class HostTextTests
{
    [Theory]
    [InlineData("clock")]
    [InlineData("score.home")]
    [InlineData("stamina.visitor.4")]
    [InlineData("player.2.color.off")]
    [InlineData("fmod/seconds_remaining")]
    [InlineData("fmod/closeSaves")]
    [InlineData("audio.slowmo")]
    public void A_key_shaped_like_one_is_written_as_it_is(string key)
    {
        Assert.Equal(key, HostText.KeyForLog(key));
    }

    [Theory]
    [InlineData("Riley Casey was here", "a key of 20 characters")]
    [InlineData("score.home\nINFO forged line", "a key of 27 characters")]
    [InlineData("clé", "a key of 3 characters")]
    [InlineData("a=b", "a key of 3 characters")]
    public void Any_other_text_is_written_only_as_its_length(string key, string expected)
    {
        Assert.Equal(expected, HostText.KeyForLog(key));
    }

    [Fact]
    public void A_long_or_empty_key_is_never_written()
    {
        Assert.Equal($"a key of {HostText.MaxKeyLength + 1} characters", HostText.KeyForLog(new string('a', HostText.MaxKeyLength + 1)));
        Assert.Equal(new string('a', HostText.MaxKeyLength), HostText.KeyForLog(new string('a', HostText.MaxKeyLength)));
        Assert.Equal("an empty key", HostText.KeyForLog(string.Empty));
        Assert.Equal("an empty key", HostText.KeyForLog(null));
    }

    [Fact]
    public void A_hosts_field_names_are_counted_never_written()
    {
        string[] own = { "arg0.shooter", "arg0.power", "arg0.isCritical" };
        string[] host = { "arg0.shooter", "Riley Casey lives at 12 Example Road", "arg0.isCritical", "one more" };
        string line = HostText.FieldsDifference(host, own);
        Assert.Equal("the host's 4 against this build's 3 (arg0.shooter, arg0.power, arg0.isCritical); the first that differs is field 2", line);
        Assert.DoesNotContain("Riley", line, StringComparison.Ordinal);
        Assert.DoesNotContain("one more", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shorter_or_longer_list_differs_at_its_first_missing_field()
    {
        Assert.Equal("the host's 1 against this build's 2 (arg0, arg1); the first that differs is field 2", HostText.FieldsDifference(new[] { "arg0" }, new[] { "arg0", "arg1" }));
        Assert.Equal("the host's 1 against this build's 0 (none); the first that differs is field 1", HostText.FieldsDifference(new[] { "x" }, Array.Empty<string>()));
        Assert.Equal("the host's 1 against this build's 1 (arg0); no field differs", HostText.FieldsDifference(new[] { "arg0" }, new[] { "arg0" }));
    }

    [Fact]
    public void This_builds_own_names_are_cut_to_a_line()
    {
        string[] own = new string[40];
        for (int i = 0; i < own.Length; i++)
        {
            own[i] = $"arg0.field{i:00}";
        }

        string line = HostText.FieldsDifference(Array.Empty<string>(), own);
        Assert.Contains("...", line, StringComparison.Ordinal);
        Assert.True(line.Length < HostText.MaxOwnNamesLength + 120);
    }

    [Fact]
    public void Both_lists_are_needed()
    {
        Assert.Throws<ArgumentNullException>(() => HostText.FieldsDifference(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => HostText.FieldsDifference(Array.Empty<string>(), null!));
    }
}
