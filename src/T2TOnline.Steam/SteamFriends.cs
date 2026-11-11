using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// <c>ISteamFriends</c>: names for logs, the friends list, who is a friend, what friends are playing, and rich presence.
/// None of these throw; they return empty, 0 or false when Steam isn't available.
/// <para>
/// <see cref="SetRichPresence"/> is the mod's "Join Game" (<see cref="SteamPresence"/>, since v1). Not called by the mod,
/// so never run against the real DLL, only tested without Steam: <see cref="ClearRichPresence"/> (it would clear every key,
/// not just the mod's) and <see cref="IsFriend"/> (the mod uses <see cref="TryIsFriend"/>).
/// </para>
/// </summary>
public static class SteamFriends
{
    /// <summary>
    /// Called with the Steam ID and the name each time a persona name is read (<see cref="GetPersonaName"/>,
    /// <see cref="GetFriendPersonaName"/>), so the log's backstop knows every name the mod could write (D127). Optional;
    /// set once by the plugin. Must be cheap and thread-safe; a throw is swallowed.
    /// </summary>
    public static Action<ulong, string>? NameRead { get; set; }

    /// <summary>The local user's persona name.</summary>
    public static string GetPersonaName()
    {
        nint self = SteamInterfaces.Friends();
        if (self == 0)
        {
            return string.Empty;
        }

        try
        {
            string name = NativeStrings.FromPointer(SteamNative.GetPersonaName(self));
            return NameRead == null ? name : Read(SteamRuntime.LocalSteamId, name);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return string.Empty;
        }
    }

    /// <summary>
    /// A user's persona name, if Steam knows it (friends always; other lobby members once their persona data has
    /// arrived, which takes a moment after joining). For a user it knows nothing about Steam returns a placeholder
    /// (typically "[unknown]").
    /// </summary>
    public static string GetFriendPersonaName(ulong steamId)
    {
        nint self = SteamInterfaces.Friends();
        if (self == 0 || steamId == 0)
        {
            return string.Empty;
        }

        try
        {
            return Read(steamId, NativeStrings.FromPointer(SteamNative.GetFriendPersonaName(self, steamId)));
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return string.Empty;
        }
    }

    /// <summary>Tells <see cref="NameRead"/> about a name read, and returns it.</summary>
    internal static string Read(ulong steamId, string name)
    {
        if (NameRead is { } read && steamId != 0 && name.Length > 0)
        {
            try
            {
                read(steamId, name);
            }
            catch (Exception)
            {
                // The log's backstop only: never in the way of the name.
            }
        }

        return name;
    }

    /// <summary>Number of users matching <paramref name="flags"/> (usually <see cref="SteamFriendFilter.Immediate"/>).</summary>
    public static int GetFriendCount(SteamFriendFilter flags)
    {
        nint self = SteamInterfaces.Friends();
        if (self == 0)
        {
            return 0;
        }

        try
        {
            return Math.Max(0, SteamNative.GetFriendCount(self, flags));
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>The friend at <paramref name="index"/> for the same <paramref name="flags"/> as the count, or 0.</summary>
    public static ulong GetFriendByIndex(int index, SteamFriendFilter flags)
    {
        nint self = SteamInterfaces.Friends();
        if (self == 0 || index < 0)
        {
            return 0;
        }

        try
        {
            return SteamNative.GetFriendByIndex(self, index, flags);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>
    /// True if <paramref name="steamId"/> is one of the local user's regular friends (<see cref="SteamFriendFilter.Immediate"/>).
    /// False if not, for the local user (who isn't their own friend), and also when Steam can't answer: use
    /// <see cref="TryIsFriend"/> to tell those apart.
    /// </summary>
    public static bool IsFriend(ulong steamId) => TryIsFriend(steamId, out bool isFriend) && isFriend;

    /// <summary>
    /// Asks Steam's friends list whether <paramref name="steamId"/> is one of the local user's regular friends
    /// (<c>HasFriend</c> with <see cref="SteamFriendFilter.Immediate"/>; pending requests, blocked and ignored users
    /// aren't). False when Steam couldn't be asked (not available, a zero ID, or steam_api64 lacks
    /// <c>SteamAPI_ISteamFriends_HasFriend</c>, an optional export), and then <paramref name="isFriend"/> is false and
    /// means nothing.
    /// </summary>
    public static bool TryIsFriend(ulong steamId, out bool isFriend)
    {
        isFriend = false;
        nint self = SteamInterfaces.Friends(nameof(SteamNative.HasFriend));
        if (self == 0 || steamId == 0)
        {
            return false;
        }

        try
        {
            isFriend = SteamNative.HasFriend(self, steamId, SteamFriendFilter.Immediate);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            isFriend = false;
            return false;
        }
    }

    /// <summary>
    /// True when the bindings are ready and steam_api64 has <c>SteamAPI_ISteamFriends_HasFriend</c>, so
    /// <see cref="TryIsFriend"/> can ask (it may still fail if Steam's friends interface isn't there).
    /// </summary>
    public static bool CanCheckFriends => SteamRuntime.HasExport(nameof(SteamNative.HasFriend));

    /// <summary>
    /// Why <see cref="TryIsFriend"/> couldn't answer about <paramref name="steamId"/>, for the log: every Steam call off
    /// (with <see cref="SteamRuntime.Status"/>), the export missing, no ID, or Steam's friends interface not there. It names
    /// no player and no folder.
    /// </summary>
    public static string FriendCheckProblem(ulong steamId) =>
        !SteamRuntime.BindingsReady ? $"Steam's calls are off: {SteamRuntime.Status}"
        : !CanCheckFriends ? "steam_api64 has no SteamAPI_ISteamFriends_HasFriend"
        : steamId == 0 ? "no Steam ID to ask about"
        : "Steam's friends interface isn't available";

    /// <summary>
    /// True if <paramref name="steamId"/> is in a game, with the game, server and lobby in <paramref name="info"/>
    /// (zeroed otherwise). <see cref="FriendGameInfo.LobbyId"/> is set when the friend is in a lobby Steam lets us see.
    /// </summary>
    public static bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info)
    {
        info = default;
        nint self = SteamInterfaces.Friends();
        if (self == 0 || steamId == 0)
        {
            return false;
        }

        try
        {
            if (SteamNative.GetFriendGamePlayed(self, steamId, out info))
            {
                return true;
            }

            info = default;
            return false;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            info = default;
            return false;
        }
    }

    /// <summary>
    /// What a friend's Steam client says they are (online, busy, away...). Steam only knows it for friends and users sharing
    /// a lobby with us; anyone else reads <see cref="SteamPersonaState.Offline"/>. <see cref="SteamPersonaState.Unknown"/>
    /// when Steam can't be asked (not available, a zero ID, or steam_api64 lacks <c>GetFriendPersonaState</c>, an optional
    /// export), which says nothing about the friend.
    /// </summary>
    public static SteamPersonaState GetFriendPersonaState(ulong steamId)
    {
        nint self = SteamInterfaces.Friends(nameof(SteamNative.GetFriendPersonaState));
        if (self == 0 || steamId == 0)
        {
            return SteamPersonaState.Unknown;
        }

        try
        {
            SteamPersonaState state = SteamNative.GetFriendPersonaState(self, steamId);
            return state is >= SteamPersonaState.Offline and <= SteamPersonaState.LookingToPlay ? state : SteamPersonaState.Unknown;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return SteamPersonaState.Unknown;
        }
    }

    /// <summary>
    /// Sets a rich presence key for the local user (for example <c>connect</c> to show "Join game" in Steam, or
    /// <c>steam_player_group</c>). A null or empty <paramref name="value"/> deletes the key.
    /// </summary>
    public static bool SetRichPresence(string key, string? value)
    {
        nint self = SteamInterfaces.Friends(nameof(SteamNative.SetRichPresence));
        byte[]? keyUtf8 = NativeStrings.ToUtf8(key);
        byte[]? valueUtf8 = NativeStrings.ToUtf8(value ?? string.Empty);
        if (self == 0 || keyUtf8 is null || valueUtf8 is null)
        {
            return false;
        }

        try
        {
            return SteamNative.SetRichPresence(self, keyUtf8, valueUtf8);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Clears all of the local user's rich presence keys. False only when Steam isn't available.</summary>
    public static bool ClearRichPresence()
    {
        nint self = SteamInterfaces.Friends(nameof(SteamNative.ClearRichPresence));
        if (self == 0)
        {
            return false;
        }

        try
        {
            SteamNative.ClearRichPresence(self);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }
}
