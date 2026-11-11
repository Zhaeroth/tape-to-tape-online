using System;

namespace T2TOnline.Steam.Tests;

/// <summary>How the Steam library's log lines name players and lobbies: by the plugin's tags, never by an ID.</summary>
[Collection(SharedSteamRuntime.Name)]
public class SteamLogTagsTests
{
    private const ulong Sam = 76561199000000002;
    private const ulong Lobby = 109775240917663232UL;

    [Fact]
    public void Without_a_tagger_every_player_and_lobby_is_unknown()
    {
        Func<ulong, string>? player = SteamLogTags.PlayerTag;
        Func<ulong, string>? lobby = SteamLogTags.LobbyTag;
        SteamLogTags.PlayerTag = null;
        SteamLogTags.LobbyTag = null;
        try
        {
            Assert.Equal(SteamLogTags.UnknownPlayer, SteamLogTags.Player(Sam));
            Assert.Equal(SteamLogTags.UnknownLobby, SteamLogTags.Lobby(Lobby));
        }
        finally
        {
            SteamLogTags.PlayerTag = player;
            SteamLogTags.LobbyTag = lobby;
        }
    }

    [Fact]
    public void The_taggers_name_them_and_a_failing_or_empty_one_never_costs_the_line()
    {
        Func<ulong, string>? player = SteamLogTags.PlayerTag;
        Func<ulong, string>? lobby = SteamLogTags.LobbyTag;
        try
        {
            SteamLogTags.PlayerTag = id => id == Sam ? "P#a0c1d2" : "P#000000";
            SteamLogTags.LobbyTag = _ => "L#7d20e1";
            Assert.Equal("P#a0c1d2", SteamLogTags.Player(Sam));
            Assert.Equal("L#7d20e1", SteamLogTags.Lobby(Lobby));

            SteamLogTags.PlayerTag = _ => throw new InvalidOperationException("tags down");
            SteamLogTags.LobbyTag = _ => string.Empty;
            Assert.Equal(SteamLogTags.UnknownPlayer, SteamLogTags.Player(Sam));
            Assert.Equal(SteamLogTags.UnknownLobby, SteamLogTags.Lobby(Lobby));
        }
        finally
        {
            SteamLogTags.PlayerTag = player;
            SteamLogTags.LobbyTag = lobby;
        }
    }
}
