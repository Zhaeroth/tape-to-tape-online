using System;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class RunFileSummaryTests
{
    // Shaped like an Easy Save 3 run file (keys wrapping {"__type", "value"}, tab-indented, CRLF); the values are made up.
    private const string RunFile =
        "{\r\n\t\"runData\" : {\r\n\t\t\"__type\" : \"State.RunDataModelV2,MainScriptsAssembly\",\r\n\t\t\"value\" : {\r\n" +
        "\t\t\t\"isRunFailed\" : false,\r\n\t\t\t\"team\" : {\"skaters\" : [{\"id\" : \"a\"}, {\"id\" : \"b\"}]},\r\n" +
        "\t\t\t\"currentMap\" : \"{\\\"nodes\\\":[]}\",\r\n\t\t\t\"currentAct\" : 2,\r\n\t\t\t\"currentFloor\" : 7,\r\n" +
        "\t\t\t\"squadId\" : \"oldschool\",\r\n\t\t\t\"runID\" : \"0b6f3c1e-1111-2222-3333-444455556666\"\r\n\t\t}\r\n\t},\r\n" +
        "\t\"runTime\" : {\r\n\t\t\"__type\" : \"float\",\r\n\t\t\"value\" : 812.5\r\n\t}\r\n}";

    [Fact]
    public void The_squad_act_floor_and_id_are_read_from_the_run()
    {
        Assert.True(RunFileSummary.TryRead(Encoding.UTF8.GetBytes(RunFile), out RunFileSummary? run));
        Assert.Equal(new RunFileSummary("oldschool", 2, 7, "0b6f3c1e-1111-2222-3333-444455556666", false), run);
    }

    [Fact]
    public void A_byte_order_mark_and_trailing_commas_are_fine()
    {
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"runData\":{\"value\":{\"squadId\":\"vanilla\",\"currentAct\":1,},},}")).ToArray();
        Assert.True(RunFileSummary.LooksLikeJson(bytes));
        Assert.True(RunFileSummary.TryRead(bytes, out RunFileSummary? run));
        Assert.Equal("vanilla", run!.SquadId);
        Assert.Equal(1, run.Act);
        Assert.Equal(0, run.Floor);
    }

    [Fact]
    public void Missing_or_odd_fields_are_left_at_their_defaults()
    {
        Assert.True(RunFileSummary.TryRead(Encoding.UTF8.GetBytes("{\"runData\":{\"squadId\":7,\"currentAct\":\"2\",\"currentFloor\":3.0,\"isRunFailed\":true}}"), out RunFileSummary? run));
        Assert.Equal(new RunFileSummary(string.Empty, 0, 3, string.Empty, true), run);
    }

    [Fact]
    public void A_string_that_parses_but_cant_be_read_is_left_at_its_default()
    {
        // JSON parsing doesn't check a string's bytes; reading it threw (invalid UTF-8, an escaped lone surrogate).
        byte[] invalid = Encoding.UTF8.GetBytes("{\"runData\":{\"squadId\":\"ab_\",\"currentAct\":2,\"runID\":\"x\\uD800\"}}");
        invalid[Array.IndexOf(invalid, (byte)'_')] = 0xE3;
        Assert.True(RunFileSummary.TryRead(invalid, out RunFileSummary? run));
        Assert.Equal(new RunFileSummary(string.Empty, 2, 0, string.Empty, false), run);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"profile\":{}}")]
    [InlineData("{\"runData\":\"text\"}")]
    [InlineData("{\"runData\":{")]
    public void Anything_else_is_not_a_run(string text)
    {
        Assert.False(RunFileSummary.TryRead(Encoding.UTF8.GetBytes(text), out RunFileSummary? run));
        Assert.Null(run);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData(" \r\n\t{", true)]
    [InlineData("[]", false)]
    [InlineData("", false)]
    public void Json_is_told_from_other_bytes(string text, bool json)
    {
        Assert.Equal(json, RunFileSummary.LooksLikeJson(Encoding.UTF8.GetBytes(text)));
        Assert.False(RunFileSummary.LooksLikeJson(new byte[] { 0x1F, 0x8B, 0x08 })); // gzip
    }
}
