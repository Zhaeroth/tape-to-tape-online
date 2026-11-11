using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>The run-start carrier's self-test compares a run written, loaded and written again, value by value.</summary>
public class RunFileDiffTests
{
    private const string Run =
        "﻿{\n\t\"runData\" : {\n\t\t\"__type\" : \"State.RunDataModelV2,MainScriptsAssembly\",\n\t\t\"value\" : {\n\t\t\t\"squadId\" : \"vanilla\",\n" +
        "\t\t\t\"team\" : {\"forwards\" : [{\"id\" : \"a\", \"speed\" : 68}, {\"id\" : \"b\", \"speed\" : 70.0}]},\n\t\t\t\"isRunFailed\" : false,\n\t\t}\n\t},\n" +
        "\t// the run's stats\n\t\"runTime\" : {\"__type\" : \"float\", \"value\" : 12.5}\n}";

    // A shape that tells a test which text it was given, without writing it: its length, and whether it is "Riley".
    private static string Shape(string? text) => $"{text?.Length ?? 0} characters{(text == "Riley" ? ", R" : string.Empty)}";

    private static bool Compare(string first, string second, out List<string> differences, out int total, int max = 10) =>
        RunFileDiff.TryCompare(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second), max, Shape, out differences, out total, out _);

    [Fact]
    public void The_same_run_has_no_differences()
    {
        Assert.True(RunFileDiff.TryCompare(Encoding.UTF8.GetBytes(Run), Encoding.UTF8.GetBytes(Run), 10, Shape, out List<string> differences, out int total, out int values));
        Assert.Empty(differences);
        Assert.Equal(0, total);
        Assert.Equal(9, values);

        // Member order and number spelling don't count; white space doesn't either.
        string reordered = Run.Replace("\"id\" : \"b\", \"speed\" : 70.0", "\"speed\" : 70, \"id\" : \"b\"", StringComparison.Ordinal);
        Assert.True(Compare(Run, reordered, out differences, out total));
        Assert.Equal(0, total);
    }

    [Fact]
    public void Each_difference_is_named_by_its_path()
    {
        string changed = Run.Replace("\"speed\" : 68", "\"speed\" : 69", StringComparison.Ordinal).Replace("\"vanilla\"", "\"defense\"", StringComparison.Ordinal)
            .Replace("\"isRunFailed\" : false", "\"isRunFailed\" : true", StringComparison.Ordinal);
        Assert.True(Compare(Run, changed, out List<string> differences, out int total));
        Assert.Equal(3, total);
        Assert.Contains("runData.value.squadId: \"vanilla\", then \"defense\"", differences);
        Assert.Contains("runData.value.team.forwards[0].speed: 68, then 69", differences);
        Assert.Contains("runData.value.isRunFailed: false, then true", differences);

        // Missing members, extra members, a list of another length, another kind of value.
        string other = Run.Replace("{\"id\" : \"a\", \"speed\" : 68}, ", string.Empty, StringComparison.Ordinal).Replace("\"isRunFailed\" : false", "\"isRunFailed\" : {}", StringComparison.Ordinal)
            .Replace("\"squadId\" : \"vanilla\"", "\"squad\" : \"vanilla\"", StringComparison.Ordinal);
        Assert.True(Compare(Run, other, out differences, out total));
        Assert.Contains("runData.value.team.forwards.length: 2, then 1", differences);
        Assert.Contains("runData.value.squadId: \"vanilla\", then missing", differences);
        Assert.Contains("runData.value.squad: missing, then \"vanilla\"", differences);
        Assert.Contains("runData.value.isRunFailed: false, then an object", differences);

        // Only so many are kept; all are counted.
        Assert.True(Compare(Run, changed, out differences, out total, max: 1));
        Assert.Single(differences);
        Assert.Equal(3, total);
    }

    [Fact]
    public void A_names_text_is_written_by_its_shape_only()
    {
        // A custom team's names are typed by a player: whatever differs under a member that holds a name, the line holds none of it.
        const string Team =
            "{\"runData\" : {\"value\" : {\"squadId\" : \"vanilla\", \"team\" : {\"teamName\" : \"Alex Rockets\", \"city\" : \"Casey Town\", \"nickname\" : \"ALX\", " +
            "\"skaters\" : [{\"id\" : \"id-a\", \"firstName\" : \"Riley\", \"lastName\" : \"Casey\", \"name\" : \"Riley Casey\"}], " +
            "\"scorers\" : [{\"displayName\" : \"Riley Casey\", \"goals\" : 2}]}}}}";
        string renamed = Team.Replace("Riley", "Sam", StringComparison.Ordinal).Replace("Alex Rockets", "Sam Rockets", StringComparison.Ordinal)
            .Replace("Casey Town", "Sam Town", StringComparison.Ordinal).Replace("ALX", "SAM", StringComparison.Ordinal);
        Assert.True(Compare(Team, renamed, out List<string> differences, out int total));
        Assert.Equal(6, total);
        string all = string.Join("\n", differences);
        foreach (string typed in new[] { "Riley", "Casey", "Alex", "Sam", "ALX", "SAM" })
        {
            Assert.DoesNotContain(typed, all, StringComparison.Ordinal);
        }

        Assert.Contains("runData.value.team.skaters[0].firstName: (5 characters, R), then (3 characters)", differences);
        Assert.Contains("runData.value.team.skaters[0].name: (11 characters), then (9 characters)", differences);
        Assert.Contains("runData.value.team.teamName: (12 characters), then (11 characters)", differences);
        Assert.Contains("runData.value.team.scorers[0].displayName: (11 characters), then (9 characters)", differences);

        // A name that went missing, or became another kind of value, is still only its shape.
        string gone = Team.Replace("\"firstName\" : \"Riley\", ", string.Empty, StringComparison.Ordinal).Replace("\"lastName\" : \"Casey\"", "\"lastName\" : 7", StringComparison.Ordinal);
        Assert.True(Compare(Team, gone, out differences, out _));
        Assert.Contains("runData.value.team.skaters[0].firstName: (5 characters, R), then missing", differences);
        Assert.Contains("runData.value.team.skaters[0].lastName: (5 characters), then 7", differences);
        Assert.True(Compare(gone, Team, out differences, out _));
        Assert.Contains("runData.value.team.skaters[0].firstName: missing, then (5 characters, R)", differences);

        // An id is not a name: it is written as before.
        Assert.True(Compare(Team, Team.Replace("id-a", "id-b", StringComparison.Ordinal), out differences, out _));
        Assert.Equal(new[] { "runData.value.team.skaters[0].id: \"id-a\", then \"id-b\"" }, differences);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\" : ")]
    [InlineData("{\"a\" : 1} trailing")]
    public void What_isnt_json_is_refused(string text)
    {
        Assert.False(Compare(Run, text, out List<string> differences, out _));
        Assert.Empty(differences);
        Assert.False(Compare(text, Run, out _, out _));
    }

    [Fact]
    public void Deep_nesting_is_cut_off_without_throwing()
    {
        string deep = new string('[', RunFileDiff.MaxDepth + 4) + new string(']', RunFileDiff.MaxDepth + 4);
        Assert.True(Compare(deep, deep, out List<string> differences, out int total));
        Assert.Equal(1, total);
        Assert.Contains("nested too deep", differences[0], StringComparison.Ordinal);
        Assert.False(Compare(new string('[', 200) + new string(']', 200), deep, out _, out _));
    }
}
