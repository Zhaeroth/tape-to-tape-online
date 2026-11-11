using System.Collections.Generic;
using System.Linq;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamFriendsPickerTests
{
    private const uint TapeToTape = 1566200;
    private const ulong Lobby = 109775240917663232UL;

    private sealed class FakePeople : IPickerSource
    {
        private readonly List<(ulong Id, string Name, SteamPersonaState State, FriendGameInfo? Game)> _friends = new();

        public FakePeople Add(ulong id, string name, SteamPersonaState state, FriendGameInfo? game = null)
        {
            _friends.Add((id, name, state, game));
            return this;
        }

        public int GetFriendCount(SteamFriendFilter flags) => flags == SteamFriendFilter.Immediate ? _friends.Count : 0;

        public ulong GetFriendByIndex(int index, SteamFriendFilter flags) => index < _friends.Count ? _friends[index].Id : 0;

        public bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info)
        {
            FriendGameInfo? game = _friends.First(f => f.Id == steamId).Game;
            info = game ?? default;
            return game.HasValue;
        }

        public string GetFriendPersonaName(ulong steamId) => _friends.First(f => f.Id == steamId).Name;

        public SteamPersonaState GetFriendPersonaState(ulong steamId) => _friends.First(f => f.Id == steamId).State;
    }

    [Fact]
    public void Offline_friends_and_friends_steam_cannot_say_anything_about_are_left_out()
    {
        var people = new FakePeople()
            .Add(1, "offline", SteamPersonaState.Offline)
            .Add(2, "unknown", SteamPersonaState.Unknown)
            .Add(3, "online", SteamPersonaState.Online)
            .Add(4, "away", SteamPersonaState.Away)
            .Add(5, "snoozing", SteamPersonaState.Snooze)
            .Add(6, "busy", SteamPersonaState.Busy);

        IReadOnlyList<SteamPickerFriend> friends = SteamFriendsPicker.Read(TapeToTape, 100, people);

        Assert.Equal(new[] { 3UL, 6UL, 4UL, 5UL }, friends.Select(f => f.SteamId));
    }

    [Fact]
    public void Players_of_this_game_come_first_then_those_in_a_lobby_of_it_then_online_busy_and_away()
    {
        var people = new FakePeople()
            .Add(1, "Away Al", SteamPersonaState.Away)
            .Add(2, "Busy Bo", SteamPersonaState.Busy)
            .Add(3, "Online Cy", SteamPersonaState.Online)
            .Add(4, "In a lobby Di", SteamPersonaState.Online, new FriendGameInfo(TapeToTape, Lobby))
            .Add(5, "In the game Ed", SteamPersonaState.Away, new FriendGameInfo(TapeToTape, 0))
            .Add(6, "Other game Flo", SteamPersonaState.Online, new FriendGameInfo(480, 0));

        IReadOnlyList<SteamPickerFriend> friends = SteamFriendsPicker.Read(TapeToTape, 100, people);

        Assert.Equal(new[] { "In the game Ed", "In a lobby Di", "Online Cy", "Other game Flo", "Busy Bo", "Away Al" }, friends.Select(f => f.Name));
        Assert.Equal(SteamFriendActivity.InThisGame, friends[0].Activity);
        Assert.Equal(SteamFriendActivity.InThisGameLobby, friends[1].Activity);
        Assert.Equal(Lobby, friends[1].LobbyId);
        Assert.Equal(SteamFriendActivity.Online, friends[2].Activity);
        Assert.Equal(SteamFriendActivity.InOtherGame, friends[3].Activity);
        Assert.Equal(0UL, friends[3].LobbyId);
        Assert.Equal(SteamPersonaState.Away, friends[0].State);
    }

    [Fact]
    public void Friends_of_the_same_kind_are_sorted_by_name_ignoring_case_and_then_by_id()
    {
        var people = new FakePeople()
            .Add(9, "zed", SteamPersonaState.Online)
            .Add(8, "Alex", SteamPersonaState.Online)
            .Add(7, "alex", SteamPersonaState.Online)
            .Add(6, "Beth", SteamPersonaState.Online);

        IReadOnlyList<SteamPickerFriend> friends = SteamFriendsPicker.Read(TapeToTape, 100, people);

        Assert.Equal(new[] { 7UL, 8UL, 6UL, 9UL }, friends.Select(f => f.SteamId));
    }

    [Fact]
    public void The_list_is_cut_to_the_cap_after_it_is_sorted_so_the_best_candidates_stay()
    {
        var people = new FakePeople();
        for (ulong i = 1; i <= 10; i++)
        {
            people.Add(i, $"Online {i:00}", SteamPersonaState.Online);
        }

        people.Add(99, "Playing it", SteamPersonaState.Online, new FriendGameInfo(TapeToTape, 0));

        IReadOnlyList<SteamPickerFriend> friends = SteamFriendsPicker.Read(TapeToTape, 3, people);

        Assert.Equal(new[] { 99UL, 1UL, 2UL }, friends.Select(f => f.SteamId));
        Assert.Empty(SteamFriendsPicker.Read(TapeToTape, 0, people));
        Assert.Empty(SteamFriendsPicker.Read(TapeToTape, -4, people));
    }

    [Fact]
    public void App_zero_matches_no_game_and_empty_friend_slots_are_skipped()
    {
        var people = new FakePeople().Add(1, "sam", SteamPersonaState.Online, new FriendGameInfo(0, Lobby));

        IReadOnlyList<SteamPickerFriend> friends = SteamFriendsPicker.Read(0, 100, people);

        Assert.Equal(SteamFriendActivity.InOtherGame, Assert.Single(friends).Activity);
        Assert.Empty(SteamFriendsPicker.Read(TapeToTape, 100, new FakePeople()));
    }
}
