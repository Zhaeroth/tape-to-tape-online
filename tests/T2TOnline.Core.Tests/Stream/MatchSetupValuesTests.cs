using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>
/// How a loaded match's setup differs from the host's, for a log line: a team's name is a custom team's typed by a player, and
/// a host chooses every key and value it sends, so the line says where the setups differ without writing either's text.
/// </summary>
public class MatchSetupValuesTests
{
    // A shape that tells a test which text it was given, without writing it: its length, and whether it is "Riley's Rockets".
    private static string Shape(string? text) => $"{text?.Length ?? 0} characters{(text == "Riley's Rockets" ? ", R" : string.Empty)}";

    private static List<KeyValuePair<string, NetValue>> Setup(params (string Key, NetValue Value)[] values)
    {
        var setup = new List<KeyValuePair<string, NetValue>>();
        foreach ((string key, NetValue value) in values)
        {
            setup.Add(new KeyValuePair<string, NetValue>(key, value));
        }

        return setup;
    }

    [Fact]
    public void Setups_that_agree_have_no_differences()
    {
        var host = Setup(("home.name", NetValue.FromString("Riley's Rockets")), ("home.team", NetValue.FromString("1ca285e4-0000-4000-8000-000000000001")),
            ("rules.periods", NetValue.FromInt(3)));
        Assert.Empty(MatchSetupValues.Differences(host, Setup(("rules.periods", NetValue.FromInt(3)), ("home.team", NetValue.FromString("1ca285e4-0000-4000-8000-000000000001")),
            ("home.name", NetValue.FromString("Riley's Rockets"))), Shape));
    }

    [Fact]
    public void A_teams_name_and_nickname_are_written_by_their_shape_only()
    {
        var host = Setup(("home.name", NetValue.FromString("Riley's Rockets")), ("home.nickname", NetValue.FromString("RIL")),
            ("visitor.name", NetValue.FromString("Calaveras")));
        var loaded = Setup(("home.name", NetValue.FromString("Sam Alex FC")), ("home.nickname", NetValue.FromString("SAM")),
            ("visitor.name", NetValue.FromString("Calaveras")));

        string differences = MatchSetupValues.Differences(host, loaded, Shape);

        Assert.Equal("home.name (15 characters, R) vs (11 characters); home.nickname (3 characters) vs (3 characters)", differences);
        foreach (string typed in new[] { "Riley", "Rockets", "Sam", "Alex", "RIL", "SAM" })
        {
            Assert.DoesNotContain(typed, differences, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_hosts_key_or_text_that_isnt_shaped_like_an_id_is_written_by_its_length()
    {
        var host = Setup(("home.goalie", NetValue.FromString("Riley Casey")), ("Riley Casey's key", NetValue.FromInt(1)), ("referee", NetValue.FromString("ref-02")));
        var loaded = Setup(("home.goalie", NetValue.FromString("goalie-07")), ("referee", NetValue.FromString("ref-01")), ("home.extra", NetValue.FromString(string.Empty)));

        string differences = MatchSetupValues.Differences(host, loaded, Shape);

        Assert.Equal(
            "a key of 17 characters missing here; home.goalie \"a key of 11 characters\" vs \"goalie-07\"; referee \"ref-02\" vs \"ref-01\"; home.extra only here",
            differences);
        Assert.DoesNotContain("Riley", differences, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_flags_and_ids_read_as_before()
    {
        var host = Setup(("rules.periods", NetValue.FromInt(3)), ("home.random", NetValue.FromBool(true)), ("home.slot.0", NetValue.FromString("1a2b3c4d-5e6f")),
            ("rules.periodLength", NetValue.FromFloat(2.5f)), ("home.skin.0", NetValue.FromString(string.Empty)));
        var loaded = Setup(("rules.periods", NetValue.FromInt(2)), ("home.random", NetValue.FromBool(false)), ("home.slot.0", NetValue.FromString("5b9c1d2e-3f40")),
            ("rules.periodLength", NetValue.FromFloat(3f)), ("home.skin.0", NetValue.FromString("Skin_Body_02")));

        Assert.Equal(
            "home.random true vs false; home.skin.0 \"\" vs \"Skin_Body_02\"; home.slot.0 \"1a2b3c4d-5e6f\" vs \"5b9c1d2e-3f40\"; rules.periodLength 2.5 vs 3; rules.periods 3 vs 2",
            MatchSetupValues.Differences(host, loaded, Shape));
    }
}
