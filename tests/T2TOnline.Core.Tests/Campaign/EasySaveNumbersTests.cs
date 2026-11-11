using System;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A run file in which Easy Save wrote a float that isn't finite (a bare NaN or Infinity) is still a run the mod can read.</summary>
public class EasySaveNumbersTests
{
    // Shaped like an Easy Save 3 run file; the values are made up. {0} is the run time's value, {1} a skater's counter.
    private static byte[] RunFile(string runTime, string counter = "0") => Encoding.UTF8.GetBytes(
        "{\r\n\t\"runData\" : {\r\n\t\t\"__type\" : \"State.RunDataModelV2,MainScriptsAssembly\",\r\n\t\t\"value\" : {\r\n" +
        "\t\t\t\"isRunFailed\" : false,\r\n\t\t\t\"team\" : {\"skaters\" : [{\"id\" : \"a\", \"injuryCounter\" : " + counter + "}]},\r\n" +
        "\t\t\t\"currentMap\" : \"NaN, Infinity and \\\"-Infinity\\\" in a text\",\r\n\t\t\t\"currentAct\" : 2,\r\n\t\t\t\"currentFloor\" : 7,\r\n" +
        "\t\t\t\"squadId\" : \"oldschool\",\r\n\t\t\t\"runID\" : \"0b6f3c1e-1111-2222-3333-444455556666\"\r\n\t\t}\r\n\t},\r\n" +
        "\t\"runTime\" : {\r\n\t\t\"__type\" : \"float\",\r\n\t\t\"value\" : " + runTime + "\r\n\t}\r\n}");

    private static string Text(byte[]? bytes) => bytes == null ? "(none)" : Encoding.UTF8.GetString(bytes);

    [Theory]
    [InlineData("NaN", "0")]
    [InlineData("Infinity", "0")]
    [InlineData("-Infinity", "0")]
    [InlineData("812.5", "NaN")]
    [InlineData("NaN", "-Infinity")]
    public void A_run_file_with_a_non_finite_float_still_reads_as_its_run(string runTime, string counter)
    {
        // The game's own loader takes such a file, and the run file check lets it pass: the co-op slot's row, a kept
        // copy for a friend who restarts and a backup's co-op run must not call it unreadable.
        Assert.True(RunFileSummary.TryRead(RunFile(runTime, counter), out RunFileSummary? run));

        Assert.Equal(new RunFileSummary("oldschool", 2, 7, "0b6f3c1e-1111-2222-3333-444455556666", false), run);
    }

    [Fact]
    public void A_file_of_plain_numbers_is_read_as_before_and_isnt_copied()
    {
        Assert.True(RunFileSummary.TryRead(RunFile("812.5"), out RunFileSummary? run));
        Assert.Equal("oldschool", run!.SquadId);
        Assert.Null(EasySaveNumbers.WithoutNonFinite(RunFile("812.5"))); // the words inside its text don't count
    }

    [Theory]
    [InlineData("{\"a\":NaN}", "{\"a\":null}")]
    [InlineData("{\"a\" : -Infinity, \"b\":[Infinity,NaN ,1]}", "{\"a\" : null, \"b\":[null,null ,1]}")]
    [InlineData("NaN", "null")]
    [InlineData("[NaN]", "[null]")]
    public void Each_bare_non_finite_word_becomes_null(string json, string expected)
    {
        Assert.Equal(expected, Text(EasySaveNumbers.WithoutNonFinite(Encoding.UTF8.GetBytes(json))));
    }

    [Theory]
    [InlineData("{\"a\":\"NaN\"}")] // a text
    [InlineData("{\"NaN\":1}")] // a key
    [InlineData("{\"a\":\"x\\\"\",\"b\":\"Infinity\"}")] // after an escaped quote
    [InlineData("{\"a\":NaNo}")] // another word
    [InlineData("{\"a\":xNaN}")]
    [InlineData("{\"a\":Infinit}")]
    [InlineData("{\"a\":-1}")]
    [InlineData("")]
    [InlineData("\"unfinished NaN")]
    [InlineData("\\")]
    public void Nothing_else_is_touched(string json)
    {
        Assert.Null(EasySaveNumbers.WithoutNonFinite(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("{\"a\":NaN}", "{\"a\":\"NaN\"}")]
    [InlineData("{\"a\" : -Infinity, \"b\":[Infinity,NaN ,1]}", "{\"a\" : \"-Infinity\", \"b\":[\"Infinity\",\"NaN\" ,1]}")]
    [InlineData("[NaN]", "[\"NaN\"]")]
    public void Each_bare_non_finite_word_can_become_a_text_of_itself(string json, string expected)
    {
        Assert.Equal(expected, Text(EasySaveNumbers.WithNonFiniteAsText(Encoding.UTF8.GetBytes(json))));
        Assert.Null(EasySaveNumbers.WithNonFiniteAsText(Encoding.UTF8.GetBytes("{\"a\":\"NaN\",\"b\":1.5}")));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void A_run_files_team_reads_with_a_non_finite_float(string counter)
    {
        // The team lines on both PCs (a checkpoint's "team changed") read the same run file the game loads.
        Assert.True(RunTeamRoster.TryRead(RunFile("812.5", counter), out RunTeamRoster? roster));
        Assert.Equal("0b6f3c1e-1111-2222-3333-444455556666", roster!.RunId);
        Assert.Equal("a", Assert.Single(roster.Skaters).Id);

        // Damage other than Easy Save's numbers stays refused.
        Assert.False(RunTeamRoster.TryRead(Encoding.UTF8.GetBytes("{\"runData\":{\"value\":{\"team\":{}}},\"x\":NaN"), out _));
    }

    [Fact]
    public void The_run_file_compare_reads_non_finite_floats_and_tells_them_apart()
    {
        Assert.True(RunFileDiff.TryCompare(RunFile("NaN", "Infinity"), RunFile("NaN", "Infinity"), 10, HostText.TextShape, out var differences, out int total, out int values));
        Assert.Empty(differences);
        Assert.Equal(0, total);
        Assert.True(values > 0);

        // A NaN that became an Infinity, or a number, is a difference, named by its path and its words.
        Assert.True(RunFileDiff.TryCompare(RunFile("NaN"), RunFile("Infinity", "-Infinity"), 10, HostText.TextShape, out differences, out total, out _));
        Assert.Equal(2, total);
        Assert.Contains("runTime.value: \"NaN\", then \"Infinity\"", differences);
        Assert.Contains("runData.value.team.skaters[0].injuryCounter: 0, then \"-Infinity\"", differences);
    }

    [Fact]
    public void What_still_isnt_a_run_file_reads_as_none()
    {
        // The second look is only for Easy Save's numbers: other damage stays refused, and never throws.
        Assert.False(RunFileSummary.TryRead(Encoding.UTF8.GetBytes("{\"runData\":{\"value\":{\"squadId\":\"a\"},\"x\":NaN"), out RunFileSummary? cut));
        Assert.Null(cut);
        Assert.False(RunFileSummary.TryRead(Encoding.UTF8.GetBytes("{\"other\":NaN}"), out _));
        Assert.False(RunFileSummary.TryRead(Encoding.UTF8.GetBytes("NaN"), out _));

        var random = new Random(7);
        byte[] good = RunFile("NaN", "Infinity");
        for (int i = 0; i < 2000; i++)
        {
            byte[] noise = (byte[])good.Clone();
            for (int k = random.Next(1, 5); k > 0; k--)
            {
                noise[random.Next(noise.Length)] = (byte)"\"\\{}[],:Na-I n"[random.Next(14)];
            }

            _ = RunFileSummary.TryRead(noise, out _);
            _ = EasySaveNumbers.WithoutNonFinite(noise);
        }
    }
}
