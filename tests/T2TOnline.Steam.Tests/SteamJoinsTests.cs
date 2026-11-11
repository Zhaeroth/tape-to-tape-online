using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Steam.Tests;

/// <summary>Steam's requests to join, as text (a friend's connect string, the command line) and while they wait.</summary>
public class SteamJoinsTests
{
    // Two synthetic lobby IDs of the form the mod's lobbies have (0x0186...: public universe, chat, lobby flags 0x60000).
    private const ulong Lobby = 109775240000000001UL;
    private const ulong OtherLobby = 109775240000000002UL;
    private const ulong Friend = 76561199000000001UL;

    [Fact]
    public void Real_lobby_ids_are_lobbies_and_other_steam_ids_are_not()
    {
        Assert.True(SteamJoinRequests.IsLobbyId(Lobby));
        Assert.True(SteamJoinRequests.IsLobbyId(OtherLobby));
        Assert.False(SteamJoinRequests.IsLobbyId(0));
        Assert.False(SteamJoinRequests.IsLobbyId(Friend)); // an individual account
        Assert.False(SteamJoinRequests.IsLobbyId(0x0170_0000_0000_0001UL)); // a group (clan)
        Assert.False(SteamJoinRequests.IsLobbyId(0x0180_0000_0000_0001UL)); // a chat that isn't a lobby
        Assert.False(SteamJoinRequests.IsLobbyId(0x0186_0000_0000_0000UL)); // no account
        Assert.False(SteamJoinRequests.IsLobbyId(0x0286_0000_78FB_64B0UL)); // another universe
        Assert.False(SteamJoinRequests.IsLobbyId(ulong.MaxValue));
    }

    [Fact]
    public void The_connect_string_reads_back_as_its_lobby()
    {
        string connect = SteamJoinRequests.ConnectString(Lobby);

        Assert.Equal("+connect_lobby 109775240000000001", connect);
        Assert.True(SteamJoinRequests.TryParseConnect(connect, out ulong lobby));
        Assert.Equal(Lobby, lobby);
        Assert.True(SteamJoinRequests.TryParseConnect("  +connect_lobby\t109775240000000001  ", out lobby));
        Assert.Equal(Lobby, lobby);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+connect_lobby")]
    [InlineData("+connect_lobby ")]
    [InlineData("+connect 109775240000000001")]
    [InlineData("+CONNECT_LOBBY 109775240000000001")]
    [InlineData("+connect_lobby=109775240000000001")]
    [InlineData("+connect_lobby 109775240000000001 +password x")]
    [InlineData("+connect_lobby -109775240000000001")]
    [InlineData("+connect_lobby +109775240000000001")]
    [InlineData("+connect_lobby 0x0186000000000001")]
    [InlineData("+connect_lobby 1097752400000000010000")]
    [InlineData("+connect_lobby 76561199000000001")]
    [InlineData("+connect_lobby 109,775,240,000,000,001")]
    [InlineData("connect_lobby 109775240000000001")]
    [InlineData("192.168.0.1:27015")]
    public void Anything_but_a_lobby_connect_string_is_refused(string? connect)
    {
        Assert.False(SteamJoinRequests.TryParseConnect(connect, out ulong lobby));
        Assert.Equal(0UL, lobby);
    }

    [Fact]
    public void A_connect_string_longer_than_steam_keeps_is_refused()
    {
        string padded = SteamJoinRequests.ConnectString(Lobby) + new string(' ', SteamJoinRequests.MaxConnectLength);

        Assert.False(SteamJoinRequests.TryParseConnect(padded, out _));
    }

    [Fact]
    public void Random_connect_strings_never_throw()
    {
        var random = new Random(7);
        const string alphabet = "+connect_lobby 0123456789 \t-=x\u0000\u00e9";
        for (int i = 0; i < 2000; i++)
        {
            char[] text = new char[random.Next(0, 60)];
            for (int c = 0; c < text.Length; c++)
            {
                text[c] = alphabet[random.Next(alphabet.Length)];
            }

            if (SteamJoinRequests.TryParseConnect(new string(text), out ulong lobby))
            {
                Assert.True(SteamJoinRequests.IsLobbyId(lobby));
            }
        }
    }

    [Fact]
    public void The_command_line_steam_starts_the_game_with_names_the_lobby()
    {
        // Steam's own form for an invite: the switch, then the ID as the next argument.
        Assert.True(SteamJoinRequests.TryFindInCommandLine(new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape\Tape to Tape.exe", "+connect_lobby", "109775240000000001" }, out ulong lobby));
        Assert.Equal(Lobby, lobby);

        // Among others, and with the program left out.
        Assert.True(SteamJoinRequests.TryFindInCommandLine(new[] { "-screen-fullscreen", "0", "+connect_lobby", "109775240000000002", "-popupwindow" }, out lobby));
        Assert.Equal(OtherLobby, lobby);

        // Both in one argument (a quoted connect string).
        Assert.True(SteamJoinRequests.TryFindInCommandLine(new[] { "game.exe", "+connect_lobby 109775240000000001" }, out lobby));
        Assert.Equal(Lobby, lobby);

        // A broken one first: the first valid one wins.
        Assert.True(SteamJoinRequests.TryFindInCommandLine(new[] { "+connect_lobby", "x", "+connect_lobby", "109775240000000002" }, out lobby));
        Assert.Equal(OtherLobby, lobby);
    }

    [Fact]
    public void A_command_line_without_a_lobby_joins_nothing()
    {
        Assert.False(SteamJoinRequests.TryFindInCommandLine(null, out ulong lobby));
        Assert.False(SteamJoinRequests.TryFindInCommandLine(Array.Empty<string>(), out lobby));
        Assert.False(SteamJoinRequests.TryFindInCommandLine(new[] { "game.exe" }, out lobby));
        Assert.False(SteamJoinRequests.TryFindInCommandLine(new[] { "game.exe", "+connect_lobby" }, out lobby));
        Assert.False(SteamJoinRequests.TryFindInCommandLine(new[] { "game.exe", "+connect_lobby", "76561199000000001" }, out lobby));
        Assert.False(SteamJoinRequests.TryFindInCommandLine(new[] { "game.exe", "+connect_lobby_other", "109775240000000001" }, out lobby));
        Assert.False(SteamJoinRequests.TryFindInCommandLine(new string[] { null!, "+connect_lobby", null! }, out lobby));
        Assert.Equal(0UL, lobby);
    }

    [Fact]
    public void Only_the_first_arguments_are_looked_at()
    {
        List<string> arguments = Enumerable.Repeat("-x", SteamJoinRequests.MaxArguments).ToList();
        arguments.AddRange(new[] { "+connect_lobby", "109775240000000001" });

        Assert.False(SteamJoinRequests.TryFindInCommandLine(arguments, out _));

        arguments.RemoveAt(0);
        arguments.RemoveAt(0);
        Assert.True(SteamJoinRequests.TryFindInCommandLine(arguments, out ulong lobby));
        Assert.Equal(Lobby, lobby);
    }

    [Fact]
    public void An_invite_waits_for_steam_and_is_taken_once()
    {
        var inbox = new SteamJoinInbox();
        Assert.Equal(SteamJoinPoll.None, inbox.Poll(TimeSpan.Zero, steamReady: true, gameStarted: true, out _));

        Assert.True(inbox.Offer(new SteamJoinRequest(Lobby, Friend, SteamJoinSource.Invite, TimeSpan.FromSeconds(10)), out SteamJoinRequest replaced));
        Assert.Equal(0UL, replaced.LobbyId);

        // Steam not ready: it waits (the game needn't have started for a request made while it runs).
        Assert.Equal(SteamJoinPoll.Waiting, inbox.Poll(TimeSpan.FromSeconds(11), steamReady: false, gameStarted: true, out SteamJoinRequest request));
        Assert.Equal(Lobby, request.LobbyId);
        Assert.Equal(SteamJoinPoll.Ready, inbox.Poll(TimeSpan.FromSeconds(12), steamReady: true, gameStarted: false, out request));
        Assert.Equal((Lobby, Friend, SteamJoinSource.Invite), (request.LobbyId, request.FromSteamId, request.Source));
        Assert.Equal(SteamJoinPoll.None, inbox.Poll(TimeSpan.FromSeconds(13), steamReady: true, gameStarted: true, out _));
    }

    [Fact]
    public void A_launch_from_an_invite_waits_for_the_game_to_start()
    {
        var inbox = new SteamJoinInbox();
        Assert.True(inbox.Offer(new SteamJoinRequest(Lobby, 0, SteamJoinSource.Launch, TimeSpan.Zero), out _));

        Assert.Equal(SteamJoinPoll.Waiting, inbox.Poll(TimeSpan.FromSeconds(30), steamReady: true, gameStarted: false, out _));
        Assert.Equal(SteamJoinPoll.Waiting, inbox.Poll(TimeSpan.FromSeconds(31), steamReady: false, gameStarted: true, out _));
        Assert.Equal(SteamJoinPoll.Ready, inbox.Poll(TimeSpan.FromSeconds(32), steamReady: true, gameStarted: true, out SteamJoinRequest request));
        Assert.Equal(SteamJoinSource.Launch, request.Source);
    }

    [Fact]
    public void A_request_that_waits_too_long_is_dropped()
    {
        var inbox = new SteamJoinInbox();
        inbox.Offer(new SteamJoinRequest(Lobby, Friend, SteamJoinSource.JoinGame, TimeSpan.FromSeconds(5)), out _);
        Assert.Equal(SteamJoinPoll.Waiting, inbox.Poll(TimeSpan.FromSeconds(5) + SteamJoinInbox.JoinWait, steamReady: false, gameStarted: true, out _));
        Assert.Equal(SteamJoinPoll.Expired, inbox.Poll(TimeSpan.FromSeconds(6) + SteamJoinInbox.JoinWait, steamReady: false, gameStarted: true, out SteamJoinRequest expired));
        Assert.Equal(Lobby, expired.LobbyId);
        Assert.Equal(SteamJoinPoll.None, inbox.Poll(TimeSpan.FromHours(1), steamReady: true, gameStarted: true, out _));

        // A launch waits as long, for the game's start-up.
        inbox.Offer(new SteamJoinRequest(Lobby, 0, SteamJoinSource.Launch, TimeSpan.Zero), out _);
        Assert.Equal(SteamJoinPoll.Waiting, inbox.Poll(SteamJoinInbox.LaunchWait - TimeSpan.FromSeconds(1), steamReady: true, gameStarted: false, out _));
        Assert.Equal(SteamJoinPoll.Expired, inbox.Poll(SteamJoinInbox.LaunchWait + TimeSpan.FromSeconds(1), steamReady: true, gameStarted: false, out _));
    }

    [Fact]
    public void The_newest_request_replaces_one_still_waiting()
    {
        var inbox = new SteamJoinInbox();
        inbox.Offer(new SteamJoinRequest(Lobby, Friend, SteamJoinSource.Invite, TimeSpan.Zero), out _);

        Assert.True(inbox.Offer(new SteamJoinRequest(OtherLobby, Friend + 1, SteamJoinSource.JoinGame, TimeSpan.FromSeconds(1)), out SteamJoinRequest replaced));
        Assert.Equal(Lobby, replaced.LobbyId);
        Assert.Equal(SteamJoinPoll.Ready, inbox.Poll(TimeSpan.FromSeconds(2), steamReady: true, gameStarted: true, out SteamJoinRequest request));
        Assert.Equal(OtherLobby, request.LobbyId);
    }

    [Fact]
    public void A_request_for_something_that_isnt_a_lobby_is_never_kept()
    {
        var inbox = new SteamJoinInbox();
        inbox.Offer(new SteamJoinRequest(Lobby, Friend, SteamJoinSource.Invite, TimeSpan.Zero), out _);

        Assert.False(inbox.Offer(new SteamJoinRequest(Friend, Friend, SteamJoinSource.Invite, TimeSpan.Zero), out _));
        Assert.False(inbox.Offer(new SteamJoinRequest(0, Friend, SteamJoinSource.Launch, TimeSpan.Zero), out _));
        Assert.Equal(SteamJoinPoll.Ready, inbox.Poll(TimeSpan.Zero, steamReady: true, gameStarted: true, out SteamJoinRequest request));
        Assert.Equal(Lobby, request.LobbyId); // the good one is still the one

        inbox.Offer(new SteamJoinRequest(Lobby, Friend, SteamJoinSource.Invite, TimeSpan.Zero), out _);
        inbox.Clear();
        Assert.Equal(SteamJoinPoll.None, inbox.Poll(TimeSpan.Zero, steamReady: true, gameStarted: true, out _));
    }
}
