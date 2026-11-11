using System;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A list of save files in a log line: the ones a diagnosis asks about by name, numbered slots as a range and a count.</summary>
public class SaveFileListTextTests
{
    private static string Slot(int slot) => string.Create(CultureInfo.InvariantCulture, $"RunHistory_{slot:D2}.es3");

    [Fact]
    public void The_run_history_s_index_and_500_slots_are_one_short_piece()
    {
        // What the save guard lets through for a friend's run history entry: 501 names, about 10 KB when each is named.
        string[] names = new[] { "RunHistoryIndex.es3" }.Concat(Enumerable.Range(0, 500).Select(Slot)).ToArray();

        string text = SaveFileListText.Describe(names);

        Assert.Equal("RunHistoryIndex.es3, RunHistory_00.es3 to RunHistory_499.es3 (500 files)", text);
        Assert.True(string.Join(", ", names).Length > 9000);
    }

    [Fact]
    public void The_profile_and_the_stats_are_named()
    {
        Assert.Equal("profileV2.es3, statsV2.es3", SaveFileListText.Describe(new[] { "profileV2.es3", "statsV2.es3" }));
        Assert.Equal("profileV2.es3", SaveFileListText.Describe(new[] { "profileV2.es3" }));
        Assert.Equal("config.es3, GameplaySettings.es3", SaveFileListText.Describe(new[] { "config.es3", "GameplaySettings.es3" }));
    }

    [Fact]
    public void A_few_numbered_files_are_still_named_one_by_one()
    {
        // Under the count that makes a range each is named; from that count on they are one piece.
        Assert.Equal("RunHistory_00.es3, RunHistory_01.es3, RunHistory_02.es3",
            SaveFileListText.Describe(Enumerable.Range(0, SaveFileListText.GroupFrom - 1).Select(Slot)));
        Assert.Equal("RunHistory_00.es3 to RunHistory_03.es3 (4 files)",
            SaveFileListText.Describe(Enumerable.Range(0, SaveFileListText.GroupFrom).Select(Slot)));
    }

    [Fact]
    public void A_family_with_gaps_says_its_count_and_its_ends_not_a_range()
    {
        string text = SaveFileListText.Describe(new[] { Slot(480), Slot(2), Slot(7), Slot(9), "statsV2.es3" });

        Assert.Equal("4 files from RunHistory_02.es3 to RunHistory_480.es3, statsV2.es3", text);
    }

    [Fact]
    public void Each_name_counts_once_whatever_its_case_and_a_family_stands_where_its_first_file_stood()
    {
        string text = SaveFileListText.Describe(new[] { "profileV2.es3", Slot(1), "PROFILEV2.ES3", Slot(0), Slot(1), Slot(2), Slot(3), "statsV2.es3" });

        Assert.Equal("profileV2.es3, RunHistory_00.es3 to RunHistory_03.es3 (4 files), statsV2.es3", text);
    }

    [Fact]
    public void Two_families_are_kept_apart()
    {
        string[] names = Enumerable.Range(0, 5).Select(Slot).Concat(Enumerable.Range(1, 4).Select(i => $"coop-{i}.es3")).ToArray();

        Assert.Equal("RunHistory_00.es3 to RunHistory_04.es3 (5 files), coop-1.es3 to coop-4.es3 (4 files)", SaveFileListText.Describe(names));
    }

    [Fact]
    public void A_long_list_of_other_names_is_cut_with_a_count()
    {
        string[] names = Enumerable.Range(0, SaveFileListText.MostNamed + 3).Select(i => $"file{(char)('a' + i)}.es3").ToArray();

        string text = SaveFileListText.Describe(names);

        Assert.EndsWith(" and 3 more", text, StringComparison.Ordinal);
        Assert.Contains("filea.es3", text, StringComparison.Ordinal);
        Assert.DoesNotContain(names[^1], text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_null_and_odd_names_never_throw()
    {
        Assert.Equal("none", SaveFileListText.Describe(null));
        Assert.Equal("none", SaveFileListText.Describe(Array.Empty<string>()));
        Assert.Equal("none", SaveFileListText.Describe(new string?[] { null, string.Empty }));
        Assert.Equal("7, 12345678901234567890.es3, .es3, noextension9", SaveFileListText.Describe(new[] { "7", "12345678901234567890.es3", ".es3", "noextension9" }));
    }
}
