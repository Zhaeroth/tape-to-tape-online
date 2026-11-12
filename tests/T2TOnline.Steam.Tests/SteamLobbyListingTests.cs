namespace T2TOnline.Steam.Tests;

/// <summary>What a friend's lobby data may say, read as untrusted text: the save, the mode, the state, the players, the versions.</summary>
public class SteamLobbyListingTests
{
    [Theory]
    [InlineData("Tchelios,2,14", "Tchelios", 2, 14)]
    [InlineData("A squad with spaces,1,0", "A squad with spaces", 1, 0)]
    [InlineData("x,0,0", "x", 0, 0)]
    [InlineData("Squad_9,99,999", "Squad_9", 99, 999)]
    public void A_save_text_is_squad_act_and_floor(string text, string squad, int act, int floor)
    {
        Assert.True(ListedSave.TryParse(text, out ListedSave save));
        Assert.Equal(new ListedSave(squad, act, floor), save);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tchelios")]
    [InlineData("Tchelios,2")]
    [InlineData("Tchelios,2,14,5")]
    [InlineData(",2,14")]
    [InlineData(" Tchelios,2,14")]
    [InlineData("Tchelios ,2,14")]
    [InlineData("Tchelios,-1,14")]
    [InlineData("Tchelios,2,-14")]
    [InlineData("Tchelios,100,14")]
    [InlineData("Tchelios,2,1000")]
    [InlineData("Tchelios,two,14")]
    [InlineData("Tchelios,2,14 ")]
    [InlineData("Tchelios,2,+14")]
    [InlineData("Tchelios\n,2,14")]
    [InlineData("Tchelios,99999,14")]
    public void Anything_else_is_no_save(string? text)
    {
        Assert.False(ListedSave.TryParse(text, out _));
    }

    [Fact]
    public void A_squad_over_the_limit_is_no_save_and_the_whole_text_is_bounded()
    {
        Assert.True(ListedSave.TryParse(new string('s', ListedSave.MaxSquadLength) + ",1,1", out _));
        Assert.False(ListedSave.TryParse(new string('s', ListedSave.MaxSquadLength + 1) + ",1,1", out _));
        Assert.False(ListedSave.TryParse(new string('s', 100_000), out _));
    }

    [Fact]
    public void A_save_published_is_a_save_read()
    {
        var save = new ListedSave("Tchelios, the Squad", 3, 21);

        string text = save.ToText();

        Assert.Equal("Tchelios the Squad,3,21", text); // commas can't be in the squad: they separate the parts
        Assert.True(ListedSave.TryParse(text, out ListedSave read));
        Assert.Equal(new ListedSave("Tchelios the Squad", 3, 21), read);
    }

    [Fact]
    public void A_squad_is_cleaned_and_cut_for_publishing_and_nothing_left_means_no_save()
    {
        Assert.Equal("ab", ListedSave.CleanSquad("  a,\tb\n "));
        Assert.Equal(ListedSave.MaxSquadLength, ListedSave.CleanSquad(new string('q', 200)).Length);
        Assert.Equal(string.Empty, ListedSave.CleanSquad(null));
        Assert.Equal(string.Empty, new ListedSave(",\n,", 1, 1).ToText());
        Assert.Equal("a,99,999", new ListedSave("a", 1000, 5000).ToText());
        Assert.Equal("a,0,0", new ListedSave("a", -3, -3).ToText());
    }

    [Theory]
    [InlineData("campaign", ListedMode.Campaign)]
    [InlineData("quick", ListedMode.Quick)]
    [InlineData("", ListedMode.Unknown)]
    [InlineData("Campaign", ListedMode.Unknown)]
    [InlineData("exhibition", ListedMode.Unknown)]
    [InlineData(null, ListedMode.Unknown)]
    public void The_mode_is_one_of_the_two_words_or_unknown(string? text, ListedMode mode)
    {
        Assert.Equal(mode, ListedLobby.ParseMode(text));
    }

    [Theory]
    [InlineData("lobby", ListedState.Lobby)]
    [InlineData("play", ListedState.Playing)]
    [InlineData("", ListedState.Unknown)]
    [InlineData("playing", ListedState.Unknown)]
    [InlineData(null, ListedState.Unknown)]
    public void The_state_is_one_of_the_two_words_or_unknown(string? text, ListedState state)
    {
        Assert.Equal(state, ListedLobby.ParseState(text));
    }

    [Fact]
    public void Modes_and_states_are_published_as_the_words_they_are_read_from()
    {
        foreach (ListedMode mode in new[] { ListedMode.Campaign, ListedMode.Quick })
        {
            Assert.Equal(mode, ListedLobby.ParseMode(ListedLobby.ModeText(mode)));
        }

        foreach (ListedState state in new[] { ListedState.Lobby, ListedState.Playing })
        {
            Assert.Equal(state, ListedLobby.ParseState(ListedLobby.StateText(state)));
        }

        Assert.Equal(string.Empty, ListedLobby.ModeText(ListedMode.Unknown));
        Assert.Equal(string.Empty, ListedLobby.StateText(ListedState.Unknown));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("10", 10)]
    [InlineData("99", 99)]
    [InlineData("0", 0)]
    [InlineData("100", 0)]
    [InlineData("-1", 0)]
    [InlineData("", 0)]
    [InlineData("four", 0)]
    [InlineData("4 ", 0)]
    [InlineData(null, 0)]
    public void The_player_count_is_one_to_ninety_nine_or_absent(string? text, int players)
    {
        Assert.Equal(players, ListedLobby.ParsePlayers(text));
    }

    [Theory]
    [InlineData("76561199000000001", 76561199000000001UL)]
    [InlineData("0", 0UL)]
    [InlineData("", 0UL)]
    [InlineData("-5", 0UL)]
    [InlineData("abc", 0UL)]
    [InlineData("999999999999999999999999", 0UL)]
    [InlineData(null, 0UL)]
    public void The_host_is_a_steam_id_or_absent(string? text, ulong id)
    {
        Assert.Equal(id, ListedLobby.ParseHost(text));
    }

    [Theory]
    [InlineData("0.9.0-rc", "7", "abc123", ListedCompat.Compatible)]
    [InlineData("0.9.0-rc", "7", "ABC123", ListedCompat.Compatible)]
    [InlineData("0.9.1-rc", "7", "abc123", ListedCompat.Compatible)] // another release of the same protocol
    [InlineData("", "7", "abc123", ListedCompat.NotT2TOnline)]
    [InlineData(null, null, null, ListedCompat.NotT2TOnline)]
    [InlineData("0.8.0-rc", "6", "abc123", ListedCompat.TheirsIsOlder)]
    [InlineData("0.10.0-rc", "8", "abc123", ListedCompat.YoursIsOlder)]
    [InlineData("0.9.0-rc", "", "abc123", ListedCompat.UnknownVersion)]
    [InlineData("0.9.0-rc", "seven", "abc123", ListedCompat.UnknownVersion)]
    [InlineData("0.9.0-rc", "7777777", "abc123", ListedCompat.UnknownVersion)]
    [InlineData("0.9.0-rc", "7", "def456", ListedCompat.OtherGameBuild)]
    [InlineData("0.9.0-rc", "7", "", ListedCompat.OtherGameBuild)]
    public void Versions_are_judged_by_protocol_first_then_game_build(string? mod, string? protocol, string? build, ListedCompat expected)
    {
        Assert.Equal(expected, ListedLobby.CheckCompat(mod, protocol, build, 7, "abc123"));
    }

    [Fact]
    public void A_listing_is_the_same_as_one_with_every_value_equal()
    {
        SteamLobbyListing Make(int players, string friend = "Sam") => new(1, 0)
        {
            HostId = 5,
            HostName = "Alex",
            FriendNames = new[] { friend },
            FriendsSeen = 1,
            Answer = ListingAnswer.Answered,
            Players = players,
            Save = new ListedSave("a", 1, 2),
        };

        Assert.True(Make(2).SameAs(Make(2)));
        Assert.False(Make(2).SameAs(Make(3)));
        Assert.False(Make(2).SameAs(Make(2, "Casey")));
        Assert.Empty(new SteamLobbyListing(1, 0).FriendNames);
        Assert.Equal(-1, new SteamLobbyListing(1, 0).PingMs);
    }
}
