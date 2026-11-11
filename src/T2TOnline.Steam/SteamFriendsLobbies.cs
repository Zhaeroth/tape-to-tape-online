using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>A friend who is in a lobby of the app we asked about.</summary>
/// <param name="FriendSteamId">The friend's Steam ID.</param>
/// <param name="LobbySteamId">The lobby's Steam ID, ready for <see cref="SteamLobbies.JoinLobby"/>.</param>
/// <param name="PersonaName">The friend's persona name, for display and logs.</param>
public readonly record struct FriendLobby(ulong FriendSteamId, ulong LobbySteamId, string PersonaName);

/// <summary>
/// Finds the host's lobby from a friend's PC without callbacks: Steam's friends list says which friends are playing
/// this app and which lobby each is in (friends-only and public lobbies are visible to friends).
/// </summary>
public static class SteamFriendsLobbies
{
    /// <summary>Safety cap on how many friends are scanned (Steam's own friend limit is lower).</summary>
    public const int MaxFriendsScanned = 4000;

    /// <summary>
    /// Every regular friend (<see cref="SteamFriendFilter.Immediate"/>) currently playing <paramref name="appId"/>
    /// itself (not a mod or shortcut of it) with a non-zero lobby ID. Empty when Steam isn't available. Allocates;
    /// call it on a timer or when a menu opens, not every frame. Use <see cref="SteamRuntime.AppId"/> for this game.
    /// </summary>
    public static IReadOnlyList<FriendLobby> FindFriendLobbies(uint appId) => FindFriendLobbies(appId, NativeFriendSource.Instance);

    internal static IReadOnlyList<FriendLobby> FindFriendLobbies(uint appId, IFriendSource source)
    {
        var found = new List<FriendLobby>();
        if (appId == 0)
        {
            return found;
        }

        int count = Math.Min(source.GetFriendCount(SteamFriendFilter.Immediate), MaxFriendsScanned);
        for (int i = 0; i < count; i++)
        {
            ulong friend = source.GetFriendByIndex(i, SteamFriendFilter.Immediate);
            if (friend == 0 || !source.TryGetFriendGamePlayed(friend, out FriendGameInfo game))
            {
                continue;
            }

            if (game.IsPlayingApp(appId) && game.LobbyId != 0)
            {
                found.Add(new FriendLobby(friend, game.LobbyId, source.GetFriendPersonaName(friend)));
            }
        }

        return found;
    }

    private sealed class NativeFriendSource : IFriendSource
    {
        public static readonly NativeFriendSource Instance = new();

        public int GetFriendCount(SteamFriendFilter flags) => SteamFriends.GetFriendCount(flags);

        public ulong GetFriendByIndex(int index, SteamFriendFilter flags) => SteamFriends.GetFriendByIndex(index, flags);

        public bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info) => SteamFriends.TryGetFriendGamePlayed(steamId, out info);

        public string GetFriendPersonaName(ulong steamId) => SteamFriends.GetFriendPersonaName(steamId);
    }
}

/// <summary>The friends-list calls <see cref="SteamFriendsLobbies"/> needs; Steam in the game, a fake in tests.</summary>
internal interface IFriendSource
{
    int GetFriendCount(SteamFriendFilter flags);

    ulong GetFriendByIndex(int index, SteamFriendFilter flags);

    bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info);

    string GetFriendPersonaName(ulong steamId);
}
