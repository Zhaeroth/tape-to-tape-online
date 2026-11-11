using System;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The words a run's lines are made of: its run's end tally, its team read from a run file on both PCs,
/// a friend's own run around a session, the run a run-end shows, an event's choices and picks, a
/// lineup against the next checkpoint.
/// </summary>
public class RunDiagnosticsTests
{
    private const string Freddy = "{\"__type\":\"State.ForwardDataModelV2,MainScriptsAssembly\",\"id\":\"id-freddy\",\"firstName\":\"Freddy\",\"lastName\":\"Kovalski\",\"number\":50}";
    private const string Boring = "{\"__type\":\"State.ForwardDataModelV2,MainScriptsAssembly\",\"id\":\"id-boring\",\"firstName\":\"Boring\",\"lastName\":\"Dude\",\"number\":93}";
    private const string Goalie = "{\"__type\":\"State.GoaltenderDataModelV2,MainScriptsAssembly\",\"id\":\"id-goalie\",\"firstName\":\"Gary\",\"lastName\":\"Goal\",\"number\":1}";

    private static byte[] RunFile(string run, params string[] seats) =>
        Encoding.UTF8.GetBytes($"{{\"runData\":{{\"__type\":\"State.RunDataModelV2\",\"value\":{{\"runID\":\"{run}\",\"squadId\":\"tchel\",\"team\":{{\"id\":\"t\",\"skaters\":[{string.Join(",", seats)}]}}}}}}}}");

    private static RunTeamRoster Roster(string run, params string[] seats)
    {
        Assert.True(RunTeamRoster.TryRead(RunFile(run, seats), out RunTeamRoster? roster));
        return roster!;
    }

    [Fact]
    public void A_run_files_team_reads_its_skaters_and_leaves_out_the_goalie_and_empty_seats()
    {
        RunTeamRoster roster = Roster("run-a", Freddy, "null", Goalie, Boring);

        Assert.Equal("run-a", roster.RunId);
        Assert.Equal(new[] { "id-freddy", "id-boring" }, roster.Skaters.Select(s => s.Id));
        Assert.Equal("skater id-freddy #50", roster.Skaters[0].ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"runData\":3}")]
    [InlineData("{\"runData\":{\"value\":{\"runID\":\"a\"}}}")]
    public void What_isnt_a_run_files_team_reads_as_none(string text)
    {
        Assert.False(RunTeamRoster.TryRead(Encoding.UTF8.GetBytes(text), out RunTeamRoster? roster));
        Assert.Null(roster);
    }

    [Fact]
    public void A_skater_with_odd_fields_is_read_tolerantly()
    {
        RunTeamRoster roster = Roster("run-a", "{\"id\":\"a\",\"number\":\"x\",\"name\":\"Just Name\"}", "{\"firstName\":\"no id\"}", "7");

        RosterSkater only = Assert.Single(roster.Skaters);
        Assert.Equal("a", only.Id);
        Assert.Equal(0, only.Number);
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_reads()
    {
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(RunFile("run-a", Freddy)).ToArray();
        Assert.True(RunTeamRoster.TryRead(bytes, out RunTeamRoster? roster));
        Assert.Single(roster!.Skaters);
    }

    [Fact]
    public void Too_many_seats_are_cut()
    {
        string[] seats = Enumerable.Range(0, RunTeamRoster.MaxSkaters + 10).Select(i => $"{{\"id\":\"s{i}\"}}").ToArray();
        Assert.Equal(RunTeamRoster.MaxSkaters, Roster("r", seats).Skaters.Count);
    }

    [Fact]
    public void A_team_change_names_who_came_and_who_went()
    {
        RunTeamRoster before = Roster("run-a", Boring);
        RunTeamRoster after = Roster("run-a", Freddy);

        Assert.Equal("+skater id-freddy #50, -skater id-boring #93 (now 1 skaters)", after.ChangesSince(before));
    }

    [Fact]
    public void The_same_team_another_run_or_no_earlier_checkpoint_is_no_change()
    {
        RunTeamRoster team = Roster("run-a", Freddy, Boring);

        Assert.Null(Roster("run-a", Boring, Freddy).ChangesSince(team));
        Assert.Null(Roster("run-b", Freddy).ChangesSince(team));
        Assert.Null(team.ChangesSince(null));
    }

    [Fact]
    public void A_big_change_is_cut_in_its_line()
    {
        string[] seats = Enumerable.Range(0, RunTeamRoster.MaxNamed + 4).Select(i => $"{{\"id\":\"s{i}\",\"name\":\"N{i}\"}}").ToArray();
        string? changes = Roster("r", seats).ChangesSince(Roster("r"));

        Assert.Contains("+4 more", changes, StringComparison.Ordinal);
        Assert.Contains($"skater s{RunTeamRoster.MaxNamed - 1} #0", changes, StringComparison.Ordinal);
        Assert.DoesNotContain($"skater s{RunTeamRoster.MaxNamed} ", changes, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_skaters_typed_names_are_never_written()
    {
        // A custom team's skaters, named by a player; on a friend the run file is the host's bytes.
        const string Riley = "{\"__type\":\"State.ForwardDataModelV2,MainScriptsAssembly\",\"id\":\"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d\",\"firstName\":\"Riley\",\"lastName\":\"Casey\",\"name\":\"Riley Casey\",\"number\":7}";
        const string Sam = "{\"__type\":\"State.ForwardDataModelV2,MainScriptsAssembly\",\"id\":\"5b9c1d2e-3f40-4a51-8b62-7c83d94e0a15\",\"firstName\":\"Sam\",\"lastName\":\"Alex\",\"name\":\"Sam Alex\",\"number\":12}";
        RunTeamRoster before = Roster("run-a", Riley);
        RunTeamRoster after = Roster("run-a", Sam);

        string? changes = after.ChangesSince(before);
        Assert.Equal("+skater 5b9c1d2e-3f40-4a51-8b62-7c83d94e0a15 #12, -skater 1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d #7 (now 1 skaters)", changes);
        foreach (string typed in new[] { "Riley", "Casey", "Sam", "Alex" })
        {
            Assert.DoesNotContain(typed, changes, StringComparison.Ordinal);
            Assert.DoesNotContain(typed, string.Join(" ", after.Skaters.Concat(before.Skaters)), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Riley Casey")]
    [InlineData("id\nINFO  forged")]
    [InlineData("Ríley")]
    [InlineData("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d-1a2b3c4d")] // 45 characters: an id
    [InlineData("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d-1a2b3c4d-5e6f")] // 50 characters: too long to be one
    public void A_hosts_id_that_isnt_shaped_like_one_is_not_written_as_sent(string id)
    {
        string seat = $"{{\"id\":{System.Text.Json.JsonSerializer.Serialize(id)},\"firstName\":\"Riley\",\"lastName\":\"Casey\",\"number\":7}}";
        string? changes = Roster("run-a", seat).ChangesSince(Roster("run-a"));
        bool written = id.Length <= ObjectEntryLogText.MaxIdLength && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

        Assert.Equal(written ? $"+skater {id} #7 (now 1 skaters)" : "+a skater #7 (now 1 skaters)", changes);
        Assert.DoesNotContain("Riley", changes, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_unshaped_ids_still_count_as_two_skaters()
    {
        // What came and went is still said by count and number when an id can't be written.
        string? changes = Roster("run-a", "{\"id\":\"Riley Casey\",\"number\":7}", "{\"id\":\"Sam Alex\",\"number\":9}").ChangesSince(Roster("run-a", "{\"id\":\"Sam Alex\",\"number\":9}"));
        Assert.Equal("+a skater #7 (now 2 skaters)", changes);
    }

    [Fact]
    public void Ones_own_run_in_words()
    {
        Assert.Equal("none", OwnRunCheck.Words(null));
        Assert.Equal("none", OwnRunCheck.Words(new RunFileSummary("tchel", 1, 1, string.Empty, false)));
        Assert.Equal("run a, squad tchel, act 2, floor 3", OwnRunCheck.Words(new RunFileSummary("tchel", 2, 3, "a", false)));

        // The ids are read from a file: one that isn't shaped like an id is written by its length.
        Assert.Equal("run a key of 11 characters, squad a key of 8 characters, act 2, floor 3", OwnRunCheck.Words(new RunFileSummary("Sam Alex", 2, 3, "Riley Casey", false)));
    }

    [Fact]
    public void Ones_own_run_is_compared_with_what_memory_holds()
    {
        var run = new RunFileSummary("tchel", 2, 3, "a", false);

        Assert.Equal(OwnRunVerdict.Same, OwnRunCheck.Compare(run, run with { Failed = true }));
        Assert.Equal(OwnRunVerdict.Same, OwnRunCheck.Compare(null, new RunFileSummary(string.Empty, 0, 0, string.Empty, false)));
        Assert.Equal(OwnRunVerdict.SameRunElsewhere, OwnRunCheck.Compare(run, run with { Floor = 4 }));
        Assert.Equal(OwnRunVerdict.Other, OwnRunCheck.Compare(run, run with { RunId = "b" }));
        Assert.Equal(OwnRunVerdict.Other, OwnRunCheck.Compare(run, run with { SquadId = "other" }));
        Assert.Equal(OwnRunVerdict.Other, OwnRunCheck.Compare(run, null));
        Assert.Equal(OwnRunVerdict.Other, OwnRunCheck.Compare(null, run));
    }

    [Fact]
    public void Ones_own_runs_line_says_what_was_found()
    {
        var run = new RunFileSummary("tchel", 2, 3, "a", false);

        Assert.Equal("Run sync: your own run is in memory again: run a, squad tchel, act 2, floor 3 (the same as before following; RunDataV2.es3 unchanged since the session's start).",
            OwnRunCheck.Line(run, run, known: true, fileSame: true, "RunDataV2.es3"));
        Assert.Contains("NOT the same as before following: run a", OwnRunCheck.Line(run, run with { RunId = "b" }, true, false, "f"), StringComparison.Ordinal);
        Assert.Contains("f changed since", OwnRunCheck.Line(run, run with { RunId = "b" }, true, false, "f"), StringComparison.Ordinal);
        Assert.Contains("which was at act 2, floor 3", OwnRunCheck.Line(run, run with { Act = 3 }, true, null, "f"), StringComparison.Ordinal);
        Assert.Contains("f not checked since", OwnRunCheck.Line(run, run, true, null, "f"), StringComparison.Ordinal);
        Assert.Contains("isn't compared", OwnRunCheck.Line(null, run, known: false, null, "f"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_ends_tally_says_what_came()
    {
        var ends = new ProgressRunEnds();
        ends.HistorySaved();
        ends.Squad("gauntlet", won: true, 2);
        ends.Squad("tchel", won: false, 1);
        ends.Unlocked("unlock_a");
        Assert.True(ends.Failed(2));

        Assert.Equal("; run's end: 1 history entry saved, 2 squad result(s) recorded (gauntlet win on difficulty 2, tchel loss on difficulty 1), 1 unlock(s) (unlock_a), 2 entries failed; 3 batch(es) that came again were already applied here (skipped)",
            ends.Part(3));
    }

    [Fact]
    public void A_run_ends_tally_with_nothing_still_says_so()
    {
        Assert.Equal("; run's end: 0 history entries saved, 0 squad result(s) recorded, 0 unlock(s), 0 entries failed; 0 batch(es) that came again were already applied here (skipped)",
            new ProgressRunEnds().Part(0));
    }

    [Fact]
    public void Only_the_first_failing_batches_are_warned_about_all_are_counted()
    {
        var ends = new ProgressRunEnds();
        foreach (int i in Enumerable.Range(0, ProgressRunEnds.MaxFailureLines))
        {
            Assert.True(ends.Failed(1));
        }

        Assert.False(ends.Failed(3));
        Assert.Equal(ProgressRunEnds.MaxFailureLines + 3, ends.FailedEntries);
    }

    [Fact]
    public void A_long_list_of_results_is_cut()
    {
        var ends = new ProgressRunEnds();
        foreach (int i in Enumerable.Range(0, ProgressRunEnds.MaxNamed + 2))
        {
            ends.Unlocked($"u{i}");
            ends.Squad($"s{i}", true, 0);
        }

        string part = ends.Part(0);
        Assert.Contains($"{ProgressRunEnds.MaxNamed + 2} unlock(s)", part, StringComparison.Ordinal);
        Assert.Contains("2 more", part, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "won")]
    [InlineData(false, true, "conceded")]
    [InlineData(false, false, "lost")]
    public void A_runs_outcome_in_words(bool win, bool conceded, string outcome)
    {
        Assert.Equal(outcome, new RunHistoryData { IsWin = win, IsConceded = conceded }.Outcome);
    }

    [Fact]
    public void A_run_in_words()
    {
        var run = new RunHistoryData
        {
            RunId = "abc",
            SquadId = "gauntlet",
            DifficultyLevel = 2,
            IsWin = true,
            RunTime = 1864.5f,
            Team = new TeamSnapshot { Forwards = new[] { new ForwardSnapshot(), new ForwardSnapshot() } },
            Matches = new[] { new MatchLine() },
        };

        Assert.Equal("run abc: won, squad gauntlet difficulty 2, 2 skater(s), 1 match(es), run time 31:04", run.Words());
        Assert.Equal("run an empty key: lost, squad an empty key difficulty 0, 0 skater(s), 0 match(es), run time 0:00", new RunHistoryData { RunTime = float.NaN }.Words());
    }

    [Fact]
    public void An_events_choices_in_words()
    {
        TextRecipe gameWords = TextRecipe.ObjectName(ChoiceObjectRef.Object1);
        var set = new EventChoiceSet
        {
            Choices = new[]
            {
                new EventChoiceData
                {
                    TranslatedButtonName = "Sign him",
                    ButtonName = "v2ui/event_choice/sign",
                    ButtonRecipe = TextRecipe.Term("v2ui/event_choice/sign"),
                    FreeAgent = new FreeAgentData { Id = "fa-0f2b7633", FirstName = "Freddy", LastName = "Kovalski", Number = 50 },
                    Conditions = new[] { FreeAgentCondition.MaxGames(3, 0) },
                },
                new EventChoiceData { ButtonName = "term/skip", Object1 = new EventObject { PreviewName = "Bulk up" } },
                new EventChoiceData { TranslatedButtonName = "Coach Potato", ButtonRecipe = gameWords, HasOnTakeover = true },
                new EventChoiceData { TranslatedButtonName = "Heal them" },
            },
        };

        // This PC's own set (the host's): its game's words in clear; a text with no recipe as its shape.
        Assert.Equal(
            "1: \"Sign him\" [free agent fa-0f2b7633 #50, max games 3 (0 played)]; 2: \"term/skip\" [preview \"Bulk up\"]; " +
            "3: \"Coach Potato\" [takeover]; 4: (a text: 9 characters)",
            set.ChoiceWords(own: true, HostText.TextShape));

        // A host's set on a friend: the host's texts by the recipes it sent, or their shape; a preview's name by its shape.
        Assert.Equal(
            "1: (a recipe) term \"v2ui/event_choice/sign\" [free agent fa-0f2b7633 #50, max games 3 (0 played)]; 2: \"term/skip\" [preview (a text: 7 characters)]; " +
            "3: (a recipe) name of object 1 [takeover]; 4: (a text: 9 characters)",
            set.ChoiceWords(own: false, HostText.TextShape));
        Assert.Equal("choice 3 of 4 ((a recipe) name of object 1 [takeover])", set.PickWords(2, own: false, HostText.TextShape));
        Assert.Equal("a choice this set doesn't have (0 of 4)", set.PickWords(-1, own: false, HostText.TextShape));
        Assert.Equal("a choice this set doesn't have (5 of 4)", set.PickWords(4, own: false, HostText.TextShape));
        Assert.Equal("\"\"", new EventChoiceData().Words(own: false, HostText.TextShape));
    }

    [Fact]
    public void A_buttons_words_made_with_a_name_are_never_written()
    {
        // "New: <skill> for <skater>": the skater's name comes in as a literal (a custom skater's is typed).
        TextRecipe withName = TextRecipe.Format(TextRecipe.Term("v2ui/event_choice/new_for"), TextRecipe.ObjectName(ChoiceObjectRef.Object1), TextRecipe.Literal("Riley Casey"));
        var choice = new EventChoiceData { ButtonName = "v2ui/event_choice/new_for", TranslatedButtonName = "New: Dragon Shout+ for Riley Casey", ButtonRecipe = withName };
        string own = choice.Words(own: true, HostText.TextShape);
        string sent = choice.Words(own: false, HostText.TextShape);
        Assert.Equal("(a text: 34 characters)", own);
        Assert.Equal("(a recipe) format(term \"v2ui/event_choice/new_for\", name of object 1, a text (11 characters))", sent);
        Assert.DoesNotContain("Riley", own + sent, StringComparison.Ordinal);

        // A team's name can be a custom team's; a host's button term can be any text.
        var team = new EventChoiceData { TranslatedButtonName = "Alex FC", ButtonRecipe = TextRecipe.TeamName(ToastTeam.Run) };
        Assert.Equal("(a text: 7 characters)", team.Words(own: true, HostText.TextShape));
        Assert.Equal("\"a key of 11 characters\"", new EventChoiceData { ButtonName = "Riley Casey" }.Words(own: false, HostText.TextShape));
    }

    [Fact]
    public void A_free_agent_goes_by_its_id_never_its_name()
    {
        var choice = new EventChoiceData { FreeAgent = new FreeAgentData { Id = "fa-1", Name = "Mononym", Number = 7 } };
        Assert.Equal("\"\" [free agent fa-1 #7]", choice.Words(own: true, HostText.TextShape));
        var named = new EventChoiceData { FreeAgent = new FreeAgentData { Id = "Riley Casey", Number = 7 } };
        Assert.Equal("\"\" [free agent a key of 11 characters #7]", named.Words(own: false, HostText.TextShape));
    }

    [Fact]
    public void A_lineup_against_the_next_checkpoint_in_words()
    {
        // The menu's cards: its starters' list (five skaters in the menu's own order, then the goalie), then its bench with
        // its empty seats. The run's roster: five starters, then the bench; no goalie.
        string?[] shown = { "c", "a", "b", "d", "e", "g", "f", string.Empty, string.Empty };
        string?[] roster = { "a", "b", "c", "d", "e", "f", null, null };

        Assert.Equal("has that lineup", LineupOrder.AgainstRun(shown, 6, roster, "g"));

        // The host swapped c (a starter) with f (on the bench); the run still has the old lineup.
        string?[] swapped = { "f", "a", "b", "d", "e", "g", "c", string.Empty, string.Empty };
        string other = LineupOrder.AgainstRun(swapped, 6, roster, "g");
        Assert.StartsWith("has those skaters with other starters (1 the lineup started are on the run's bench (f); 1 from its bench start in the run (c)", other, StringComparison.Ordinal);

        Assert.StartsWith("has another lineup (", LineupOrder.AgainstRun(shown, 6, new[] { "a", "b", "c", "d", "e", "x" }, "g"), StringComparison.Ordinal);
        Assert.StartsWith("has those skaters (", LineupOrder.AgainstRun(swapped, -1, roster, "g"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_goalies_card_and_the_bench_count_once_against_the_run()
    {
        // Before, the goalie's card read as a skater missing from the run and the bench as counted twice.
        string?[] shown = { "lw", "c", "rw", "ld", "rd", "goalie", "b1", string.Empty, string.Empty, string.Empty, string.Empty };
        string?[] roster = { "lw", "rw", "c", "ld", "rd", "b1", null, null, null, null };
        Assert.Equal("has that lineup", LineupOrder.AgainstRun(shown, 6, roster, "goalie"));
        Assert.StartsWith("has another lineup (1 the host's lineup showed aren't in the run (goalie)", LineupOrder.AgainstRun(shown, 6, roster, null), StringComparison.Ordinal);
    }
}
