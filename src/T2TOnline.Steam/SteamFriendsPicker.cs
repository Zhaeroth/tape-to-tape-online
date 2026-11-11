using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>What an online friend is doing, as far as Steam says without asking them (their game info).</summary>
public enum SteamFriendActivity
{
    /// <summary>Online, in no game (or one Steam doesn't show us).</summary>
    Online,

    /// <summary>Playing this game, in no lobby (its menus, an exhibition, a run of their own).</summary>
    InThisGame,

    /// <summary>Playing this game, in a lobby (a session of this mod, theirs or someone's).</summary>
    InThisGameLobby,

    /// <summary>Playing another game (Steam's game info has its ID, but the name would need another call; not shown).</summary>
    InOtherGame,
}

/// <summary>An online friend, for the lobby's invite list.</summary>
/// <param name="SteamId">The friend's Steam ID: what <see cref="SteamLobbies.InviteUser"/> takes.</param>
/// <param name="Name">Their persona name, as Steam has it (raw: clean it before showing it).</param>
/// <param name="State">Online, busy, away...</param>
/// <param name="Activity">What they're doing.</param>
/// <param name="LobbyId">The lobby they're in when <paramref name="Activity"/> is <see cref="SteamFriendActivity.InThisGameLobby"/>, else 0.</param>
public readonly record struct SteamPickerFriend(ulong SteamId, string Name, SteamPersonaState State, SteamFriendActivity Activity, ulong LobbyId);

/// <summary>
/// The friends the lobby's invite list offers: those whose Steam status says they are online, with what they're doing, best
/// candidates first (playing this game, then in a lobby of it, then online ones, busy ones, away ones; each by name).
/// Offline friends are left out: an invite to one only waits for them. Read from Steam's local friends cache, no network, but
/// it walks the whole friends list (up to <see cref="SteamFriendsLobbies.MaxFriendsScanned"/>): call it when the list opens
/// and then at most every couple of seconds, not every frame. Empty when Steam isn't available. Never throws.
/// </summary>
public static class SteamFriendsPicker
{
    /// <summary>How many friends are returned by default.</summary>
    public const int DefaultMaxFriends = 100;

    /// <summary>The online friends for <paramref name="appId"/> (<see cref="SteamRuntime.AppId"/> for this game), at most <paramref name="maxFriends"/>.</summary>
    public static IReadOnlyList<SteamPickerFriend> Read(uint appId, int maxFriends = DefaultMaxFriends) =>
        Read(appId, maxFriends, NativePickerSource.Instance);

    internal static IReadOnlyList<SteamPickerFriend> Read(uint appId, int maxFriends, IPickerSource source)
    {
        var found = new List<SteamPickerFriend>();
        if (maxFriends <= 0)
        {
            return found;
        }

        int count = Math.Min(source.GetFriendCount(SteamFriendFilter.Immediate), SteamFriendsLobbies.MaxFriendsScanned);
        for (int i = 0; i < count; i++)
        {
            ulong friend = source.GetFriendByIndex(i, SteamFriendFilter.Immediate);
            if (friend == 0)
            {
                continue;
            }

            SteamPersonaState state = source.GetFriendPersonaState(friend);
            if (state is SteamPersonaState.Offline or SteamPersonaState.Unknown)
            {
                continue;
            }

            SteamFriendActivity activity = SteamFriendActivity.Online;
            ulong lobby = 0;
            if (source.TryGetFriendGamePlayed(friend, out FriendGameInfo game))
            {
                if (appId != 0 && game.IsPlayingApp(appId))
                {
                    lobby = game.LobbyId;
                    activity = lobby != 0 ? SteamFriendActivity.InThisGameLobby : SteamFriendActivity.InThisGame;
                }
                else
                {
                    activity = SteamFriendActivity.InOtherGame;
                }
            }

            found.Add(new SteamPickerFriend(friend, source.GetFriendPersonaName(friend), state, activity, lobby));
        }

        found.Sort(Compare);
        if (found.Count > maxFriends)
        {
            found.RemoveRange(maxFriends, found.Count - maxFriends);
        }

        return found;
    }

    /// <summary>Best candidate first: this game's players, those in a lobby of it, then by how reachable they look, each by name.</summary>
    internal static int Compare(SteamPickerFriend a, SteamPickerFriend b)
    {
        int byRank = Rank(a).CompareTo(Rank(b));
        if (byRank != 0)
        {
            return byRank;
        }

        int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : a.SteamId.CompareTo(b.SteamId);
    }

    private static int Rank(SteamPickerFriend friend) => friend.Activity switch
    {
        SteamFriendActivity.InThisGame => 0,
        SteamFriendActivity.InThisGameLobby => 1,
        _ => friend.State switch
        {
            SteamPersonaState.Online or SteamPersonaState.LookingToPlay or SteamPersonaState.LookingToTrade => 2,
            SteamPersonaState.Busy => 3,
            _ => 4,
        },
    };

    private sealed class NativePickerSource : IPickerSource
    {
        public static readonly NativePickerSource Instance = new();

        public int GetFriendCount(SteamFriendFilter flags) => SteamFriends.GetFriendCount(flags);

        public ulong GetFriendByIndex(int index, SteamFriendFilter flags) => SteamFriends.GetFriendByIndex(index, flags);

        public bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info) => SteamFriends.TryGetFriendGamePlayed(steamId, out info);

        public string GetFriendPersonaName(ulong steamId) => SteamFriends.GetFriendPersonaName(steamId);

        public SteamPersonaState GetFriendPersonaState(ulong steamId) => SteamFriends.GetFriendPersonaState(steamId);
    }
}

/// <summary>The friends-list calls <see cref="SteamFriendsPicker"/> needs; Steam in the game, a fake in tests.</summary>
internal interface IPickerSource : IFriendSource
{
    SteamPersonaState GetFriendPersonaState(ulong steamId);
}
