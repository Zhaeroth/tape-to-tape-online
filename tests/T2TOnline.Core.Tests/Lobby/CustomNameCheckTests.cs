using System;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class CustomNameCheckTests
{
    private const string Cleaner = "12:00:01.000 INFO  Log: the log's cleaner knows 7 custom name(s) of teams, skaters, goalies and logos; a line that slips one writes \"a custom name (<length>, #<hash>)\".";
    private const string Setup = "12:00:02.000 INFO  Online: sent this match's setup to 0 friend(s) (a custom team (13 characters, #4be1) vs Spartans, rule Exhibition; setup 0123456789abcdef); stream at 60 Hz.";

    [Fact]
    public void A_clean_log_with_both_lines_passes_and_says_counts_only()
    {
        var check = new CustomNameCheck(new[] { "Riverside Owls", "Riverside", "OWL", "Casey Rivers", "custom_owl.png" });
        foreach (string line in new[] { "12:00:00.000 INFO  T2T Online 0.12.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-11.", Cleaner, Setup })
        {
            check.Read(line);
        }

        Assert.Null(check.Problem());
        Assert.Equal(5, check.Held);
        Assert.Equal(5, check.Checked);
        Assert.Equal(0, check.TooShort);
        Assert.Equal(7, check.CleanerCount);
        Assert.True(check.CustomTeamSetupSeen);
        string summary = check.Summary();
        Assert.Equal("5 typed name(s) held, 5 checked (0 too short), 0 found in 0 of 3 line(s); the cleaner knows 7; a setup names a custom team by its shape; the backstop's label in 0 line(s)", summary);
        Assert.DoesNotContain("Riverside", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_in_any_line_fails_with_the_line_numbers_and_never_the_name()
    {
        var check = new CustomNameCheck(new[] { "Riverside Owls", "Casey Rivers" });
        check.Read(Cleaner);
        check.Read(Setup);
        check.Read("12:00:03.000 INFO  Chains: skater Casey Rivers took the puck.");
        check.Read("12:00:04.000 INFO  Match picks: Riverside Owls vs Spartans.");
        string? problem = check.Problem();
        Assert.NotNull(problem);
        Assert.Equal("2 of the 2 typed name(s) found in 2 line(s) of the session log (lines 3, 4)", problem);
        Assert.DoesNotContain("Riverside", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("Casey", check.Summary(), StringComparison.Ordinal);
        Assert.Equal(new[] { 3, 4 }, check.HitLines);
    }

    [Theory]
    [InlineData("Owls", "the Owls won", true)]
    [InlineData("Owls", "Owls", true)]
    [InlineData("Owls", "(Owls)", true)]
    [InlineData("Owls", "the NightOwls won", false)] // inside a longer word: a letter right before it
    [InlineData("Owls", "Owlsome", false)]
    [InlineData("Owls", "owls", false)] // as typed: case and all
    [InlineData("Casey Rivers", "skater Casey Rivers.", true)]
    [InlineData("custom_owl.png", "loaded custom_owl.png;", true)]
    public void A_name_counts_where_it_stands_alone_as_typed(string name, string line, bool holds)
    {
        Assert.Equal(holds, CustomNameCheck.Holds(line, name));
    }

    [Fact]
    public void Short_empty_and_repeated_names_are_counted_not_looked_for()
    {
        var check = new CustomNameCheck(new[] { "Al", "", null, "  ", "Sam", "Sam", "Riley" });
        Assert.Equal(3, check.Held); // Al, Sam, Riley
        Assert.Equal(1, check.TooShort);
        Assert.Equal(2, check.Checked);
        check.Read("Al was here");
        Assert.Equal(0, check.HitCount);
    }

    [Fact]
    public void A_log_without_the_cleaners_line_or_a_custom_setup_fails_and_says_which()
    {
        var check = new CustomNameCheck(new[] { "Riverside Owls" });
        check.Read("12:00:02.000 INFO  Online: sent this match's setup to 0 friend(s) (Spartans vs Vikings, rule Exhibition).");
        Assert.Equal("no line of the log's cleaner's count of custom names; no match setup line names a custom team by its shape", check.Problem());

        var zero = new CustomNameCheck(new[] { "Riverside Owls" });
        zero.Read("Log: the log's cleaner knows 0 custom name(s) of teams");
        zero.Read(Setup);
        Assert.Equal("the log's cleaner knows 0 custom names", zero.Problem());
    }

    [Fact]
    public void The_cleaner_count_is_the_highest_read_and_a_custom_team_outside_a_setup_line_is_not_the_setup()
    {
        var check = new CustomNameCheck(new[] { "Riverside Owls" });
        check.Read("Log: the log's cleaner knows 3 custom name(s)");
        check.Read("Log: the log's cleaner knows 12 custom name(s)");
        check.Read("Custom content: a custom team (13 characters, #4be1) can't be sent");
        Assert.Equal(12, check.CleanerCount);
        Assert.False(check.CustomTeamSetupSeen);
    }

    [Fact]
    public void The_backstops_label_is_counted_with_its_lines()
    {
        var check = new CustomNameCheck(new[] { "Riverside Owls" });
        check.Read(Cleaner);
        check.Read(Setup);
        check.Read("12:00:05.000 INFO  Zaps: a custom name (14 characters, #9a0c) zapped.");
        Assert.Null(check.Problem());
        Assert.Equal(1, check.BackstopCount); // not the cleaner's own line, which names the label it writes
        Assert.Equal(new[] { 3 }, check.BackstopLines);
        Assert.EndsWith("the backstop's label in 1 line(s) (lines 3)", check.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_first_lines_are_kept_and_the_rest_counted()
    {
        var check = new CustomNameCheck(new[] { "Riverside Owls" });
        for (int i = 0; i < 12; i++)
        {
            check.Read("Riverside Owls");
        }

        Assert.Equal(CustomNameCheck.MaxLinesKept, check.HitLines.Count);
        Assert.Contains("lines 1, 2, 3, 4, 5, 6, 7, 8, and 4 more", check.Problem(), StringComparison.Ordinal);
    }
}
