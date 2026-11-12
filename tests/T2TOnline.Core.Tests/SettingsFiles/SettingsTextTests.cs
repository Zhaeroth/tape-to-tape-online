using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.SettingsFiles;

namespace T2TOnline.Core.Tests.SettingsFiles;

public class SettingsTextTests
{
    /// <summary>A shared file as MelonLoader 0.7.3 writes it (Tomlet: the description as a comment, keys bare).</summary>
    private const string SharedFile =
        "[OtherMod]\n# Another mod's setting.\nSpeed = 7\n\n[T2TOnline]\n# Developer: one-way delay.\nLoopbackDelayMs = 0\n" +
        "# Developer: measure the game's replay snapshots during matches.\nSnapshotProbe = true\nCoopSlot = 3\nOnlineInput = \"Auto\"\n";

    [Theory]
    [InlineData(SharedFile, true)]
    [InlineData("[T2TOnline]\r\nCoopSlot = 3\r\n", true)]
    [InlineData("  [ T2TOnline ]  # ours\n", true)]
    [InlineData("[\"T2TOnline\"]\nCoopSlot = 3\n", true)]
    [InlineData("['T2TOnline']\n", true)]
    [InlineData("[T2TOnline]", true)]
    [InlineData("[OtherMod]\nSpeed = 7\n", false)]
    [InlineData("[OtherMod]\nT2TOnline = 1\n", false)]
    [InlineData("[T2TOnline.Sub]\nx = 1\n", false)]
    [InlineData("[[T2TOnline]]\nx = 1\n", false)]
    [InlineData("# [T2TOnline]\n", false)]
    [InlineData("[t2tonline]\n", false)]
    [InlineData("[OtherMod]\nNotes = \"\"\"\n[T2TOnline]\n\"\"\"\n", false)]
    [InlineData("[OtherMod]\nNotes = '''\n[T2TOnline]\n'''\n[T2TOnline]\n", true)]
    [InlineData("[OtherMod]\nNotes = \"\"\"one line\"\"\"\n[T2TOnline]\n", true)]
    [InlineData("[\"T2TOnline\n", false)]
    [InlineData("[", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_section_is_found_only_as_a_table_header(string? toml, bool found)
    {
        Assert.Equal(found, SettingsText.HasSection(toml, "T2TOnline"));
    }

    [Fact]
    public void No_section_name_finds_nothing()
    {
        Assert.False(SettingsText.HasSection(SharedFile, string.Empty));
    }

    [Theory]
    [InlineData(true, true, SettingsOpening.UseOwnFile)]
    [InlineData(true, false, SettingsOpening.UseOwnFile)]
    [InlineData(false, true, SettingsOpening.MoveFromSharedFile)]
    [InlineData(false, false, SettingsOpening.CreateOwnFile)]
    public void The_own_file_wins_and_the_shared_one_is_moved_once(bool own, bool shared, SettingsOpening expected)
    {
        Assert.Equal(expected, SettingsText.Decide(own, shared));
    }

    [Fact]
    public void A_summary_lists_names_and_values_or_says_none()
    {
        Assert.Equal("none", SettingsText.Summary(Array.Empty<KeyValuePair<string, string>>()));
        Assert.Equal("CoopSlot = 3, DevTools = true", SettingsText.Summary(new[] { new KeyValuePair<string, string>("CoopSlot", "3"), new KeyValuePair<string, string>("DevTools", "true") }));
    }

    [Fact]
    public void A_long_summary_is_cut_at_a_whole_setting()
    {
        List<KeyValuePair<string, string>> many = Enumerable.Range(0, 60).Select(i => new KeyValuePair<string, string>($"Setting{i:00}", "123")).ToList();

        string summary = SettingsText.Summary(many);

        Assert.True(summary.Length <= SettingsText.SummaryMaxLength, summary.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith(", ...", summary, StringComparison.Ordinal);
        Assert.StartsWith("Setting00 = 123, Setting01 = 123", summary, StringComparison.Ordinal);
        Assert.Equal("...", SettingsText.Summary(new[] { new KeyValuePair<string, string>(new string('x', 500), "1") }));
        Assert.Throws<ArgumentNullException>(() => SettingsText.Summary(null!));
    }
}
