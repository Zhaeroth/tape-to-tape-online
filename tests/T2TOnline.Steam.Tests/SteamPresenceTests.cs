using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Steam.Tests;

/// <summary>The rich presence that makes Steam's "Join Game" work: what's set, only when it changed, and cleared after.</summary>
public class SteamPresenceTests
{
    private const ulong Lobby = 109775240000000001UL;

    [Fact]
    public void Hosting_sets_connect_status_and_group()
    {
        var steam = new FakeRichPresence();
        var presence = new SteamPresence(steam.Set);

        Assert.True(presence.Show(new SteamPresenceState(Lobby, Hosting: true, 2, 4)));

        Assert.Equal("+connect_lobby 109775240000000001", steam.Keys[SteamPresence.ConnectKey]);
        Assert.Equal("T2T Online: hosting a co-op game · 2 of 4 players", steam.Keys[SteamPresence.StatusKey]);
        Assert.Equal("109775240000000001", steam.Keys[SteamPresence.GroupKey]);
        Assert.Equal("2", steam.Keys[SteamPresence.GroupSizeKey]);
        Assert.True(SteamJoinRequests.TryParseConnect(steam.Keys[SteamPresence.ConnectKey], out ulong lobby));
        Assert.Equal(Lobby, lobby);
        Assert.Equal(0, presence.Refused);
    }

    [Fact]
    public void Only_what_changed_is_sent_and_nothing_when_nothing_did()
    {
        var steam = new FakeRichPresence();
        var presence = new SteamPresence(steam.Set);
        var state = new SteamPresenceState(Lobby, Hosting: true, 1, 4);
        presence.Show(state);
        steam.Calls.Clear();

        // Every frame asks again: nothing goes to Steam.
        Assert.False(presence.Show(state));
        Assert.False(presence.Show(state with { MaxPlayers = 4 }));
        Assert.Empty(steam.Calls);

        // A friend joins: the status and the group's size change, the lobby doesn't.
        Assert.True(presence.Show(state with { Players = 2 }));
        Assert.Equal(new[] { SteamPresence.StatusKey, SteamPresence.GroupSizeKey }, steam.Calls.Select(c => c.Key));
    }

    [Fact]
    public void Clearing_deletes_only_the_keys_it_set()
    {
        var steam = new FakeRichPresence();
        steam.Keys["steam_display"] = "#SomeoneElses";
        var presence = new SteamPresence(steam.Set);
        Assert.False(presence.Show(null)); // nothing set yet: nothing to clear

        presence.Show(new SteamPresenceState(Lobby, Hosting: false, 3, 4));
        Assert.True(presence.Show(null));

        Assert.Null(presence.Shown);
        Assert.Equal(new[] { "steam_display" }, steam.Keys.Keys);
        Assert.All(steam.Calls.Skip(4), c => Assert.Null(c.Value)); // the four deletes
        Assert.False(presence.Show(null));
    }

    [Fact]
    public void A_friend_shows_a_friends_game_and_a_new_lobby_moves_join_game()
    {
        var steam = new FakeRichPresence();
        var presence = new SteamPresence(steam.Set);

        presence.Show(new SteamPresenceState(Lobby, Hosting: false, 2, 4));
        Assert.Equal("T2T Online: in a friend's co-op game · 2 of 4 players", steam.Keys[SteamPresence.StatusKey]);

        // The host's lobby was lost and made again: connect and group follow it.
        presence.Show(new SteamPresenceState(Lobby + 1, Hosting: false, 2, 4));
        Assert.Equal(SteamJoinRequests.ConnectString(Lobby + 1), steam.Keys[SteamPresence.ConnectKey]);
        Assert.Equal((Lobby + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), steam.Keys[SteamPresence.GroupKey]);
    }

    [Fact]
    public void The_status_names_nobody()
    {
        // Everyone on the player's Steam friends list sees it, strangers to the host too (D129).
        Assert.Equal("T2T Online: in a friend's co-op game · 2 of 4 players", SteamPresence.Status(new SteamPresenceState(Lobby, false, 2, 4)));
        Assert.Equal("T2T Online: in a friend's co-op game · 4 of 4 players", SteamPresence.Status(new SteamPresenceState(Lobby, false, 4, 4)));
        Assert.Equal("T2T Online: hosting a co-op game · 1 of 4 players", SteamPresence.Status(new SteamPresenceState(Lobby, true, 0, 4)));
    }

    [Fact]
    public void A_refused_key_is_counted_and_not_asked_again_until_it_changes()
    {
        var steam = new FakeRichPresence { Refuse = SteamPresence.StatusKey };
        var presence = new SteamPresence(steam.Set);
        var state = new SteamPresenceState(Lobby, Hosting: true, 1, 4);

        presence.Show(state);
        Assert.Equal(1, presence.Refused);
        Assert.False(presence.Show(state));
        Assert.Equal(1, presence.Refused);

        presence.Show(state with { Players = 2 });
        Assert.Equal(2, presence.Refused);
    }

    /// <summary>Steam's rich presence keys, as a dictionary; a null or empty value deletes the key, as in Steam.</summary>
    private sealed class FakeRichPresence
    {
        public Dictionary<string, string> Keys { get; } = new();

        public List<(string Key, string? Value)> Calls { get; } = new();

        public string? Refuse { get; init; }

        public bool Set(string key, string? value)
        {
            Calls.Add((key, value));
            if (key == Refuse)
            {
                return false;
            }

            if (string.IsNullOrEmpty(value))
            {
                Keys.Remove(key);
            }
            else
            {
                Keys[key] = value;
            }

            return true;
        }
    }
}
