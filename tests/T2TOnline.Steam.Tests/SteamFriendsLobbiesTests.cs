using System.Collections.Generic;
using System.Linq;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamFriendsLobbiesTests
{
    private const uint TapeToTape = 1566200;
    private const ulong LobbyA = 109775240917663232UL;
    private const ulong LobbyB = 109775240917663233UL;

    private sealed class FakeFriends : IFriendSource
    {
        private readonly List<(ulong Id, string Name, FriendGameInfo? Game)> _friends = new();

        public SteamFriendFilter? LastFilter { get; private set; }

        public int? CountOverride { get; set; }

        public FakeFriends Add(ulong id, string name, FriendGameInfo? game)
        {
            _friends.Add((id, name, game));
            return this;
        }

        public int GetFriendCount(SteamFriendFilter flags)
        {
            LastFilter = flags;
            return CountOverride ?? _friends.Count;
        }

        public ulong GetFriendByIndex(int index, SteamFriendFilter flags) => index < _friends.Count ? _friends[index].Id : 0;

        public bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info)
        {
            FriendGameInfo? game = _friends.First(f => f.Id == steamId).Game;
            info = game ?? default;
            return game.HasValue;
        }

        public string GetFriendPersonaName(ulong steamId) => _friends.First(f => f.Id == steamId).Name;
    }

    [Fact]
    public void Finds_friends_in_a_lobby_of_this_app_only()
    {
        var friends = new FakeFriends()
            .Add(1, "host", new FriendGameInfo(TapeToTape, LobbyA))
            .Add(2, "in menus, no lobby", new FriendGameInfo(TapeToTape, 0))
            .Add(3, "other game", new FriendGameInfo(480, LobbyB))
            .Add(4, "offline", null)
            .Add(5, "mod of this app", new FriendGameInfo(TapeToTape | (1UL << 24) | (7UL << 32), LobbyB))
            .Add(6, "second host", new FriendGameInfo(TapeToTape, LobbyB));

        IReadOnlyList<FriendLobby> found = SteamFriendsLobbies.FindFriendLobbies(TapeToTape, friends);

        Assert.Equal(
            new[] { new FriendLobby(1, LobbyA, "host"), new FriendLobby(6, LobbyB, "second host") },
            found);
        Assert.Equal(SteamFriendFilter.Immediate, friends.LastFilter);
    }

    [Fact]
    public void Skips_empty_friend_slots()
    {
        var friends = new FakeFriends().Add(1, "host", new FriendGameInfo(TapeToTape, LobbyA));
        friends.CountOverride = 3; // Steam says 3, but only one index resolves.

        Assert.Single(SteamFriendsLobbies.FindFriendLobbies(TapeToTape, friends));
    }

    [Fact]
    public void App_zero_finds_nothing()
    {
        var friends = new FakeFriends().Add(1, "weird", new FriendGameInfo(0, LobbyA));

        Assert.Empty(SteamFriendsLobbies.FindFriendLobbies(0, friends));
    }
}
