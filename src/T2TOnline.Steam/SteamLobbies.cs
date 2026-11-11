using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// <c>ISteamMatchmaking</c> lobby calls, without callbacks: create and join return a <see cref="SteamCallResult{T}"/> to
/// poll, and everything else is read on demand. Keys and values are UTF-8; a string containing NUL is rejected. None of
/// these throw.
/// <para>
/// Lobby data is readable for a lobby you are in, and for any other lobby (a friend's, before deciding to join it) once
/// Steam has fetched it: call <see cref="RequestLobbyData"/> once, then poll <see cref="GetLobbyDataCount"/> each frame.
/// It stays 0 until the data arrives, then <see cref="GetLobbyData"/> reads the keys. Give up after a few seconds: a
/// lobby that no longer exists never gets data, and neither does anything while Steam is offline. Steam announces the
/// arrival with a <c>LobbyDataUpdate_t</c> broadcast that the game's dispatcher consumes, but the data itself lands in
/// the Steam client's lobby cache, which is what these calls read, so polling sees it all the same.
/// </para>
/// <para>
/// Steam also posts <c>LobbyEnter_t</c>, <c>LobbyDataUpdate_t</c> and <c>LobbyChatUpdate_t</c> as broadcast callbacks,
/// which the game's Facepunch dispatcher receives and forwards to any of its own <c>SteamMatchmaking</c> event handlers.
/// </para>
/// <para>
/// Not called by the mod yet (kept for invites and rich presence), so never run against the real DLL, only tested
/// without Steam: <see cref="SetLobbyMemberData"/>, <see cref="GetLobbyMemberData"/>, <see cref="SetJoinable"/> and
/// <see cref="SetLobbyType"/>. The mod's lobby itself is <see cref="SteamLobbyLink"/>.
/// </para>
/// </summary>
public static class SteamLobbies
{
    /// <summary>
    /// Creates a lobby that the local user joins and owns. Poll the result; on success <see cref="LobbyCreated.LobbyId"/>
    /// is the lobby. When Steam isn't available the result has already failed with
    /// <see cref="SteamCallResultFailure.SteamUnavailable"/>, and when Steam refuses to start the call with
    /// <see cref="SteamCallResultFailure.NoHandle"/>.
    /// </summary>
    public static SteamCallResult<LobbyCreated> CreateLobby(SteamLobbyType lobbyType, int maxMembers, SteamCallResultPath path = SteamCallResultPath.SteamUtils, int maxPolls = 0)
    {
        nint self = SteamInterfaces.Matchmaking();
        if (self == 0)
        {
            return SteamCallResult<LobbyCreated>.FailedAtStart(LobbyCreated.CallbackId, path, SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);
        }

        try
        {
            ulong handle = SteamNative.CreateLobby(self, lobbyType, maxMembers);
            return new SteamCallResult<LobbyCreated>(handle, LobbyCreated.CallbackId, path, maxPolls);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return SteamCallResult<LobbyCreated>.FailedAtStart(LobbyCreated.CallbackId, path, SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);
        }
    }

    /// <summary>
    /// Joins a lobby. Poll the result; joined if <see cref="LobbyEnter.EnterResponse"/> is
    /// <see cref="SteamChatRoomEnterResponse.Success"/>. Lobby 0 fails at once with <see cref="SteamCallResultFailure.NoHandle"/>.
    /// </summary>
    public static SteamCallResult<LobbyEnter> JoinLobby(ulong lobbyId, SteamCallResultPath path = SteamCallResultPath.SteamUtils, int maxPolls = 0)
    {
        nint self = SteamInterfaces.Matchmaking();
        if (self == 0)
        {
            return SteamCallResult<LobbyEnter>.FailedAtStart(LobbyEnter.CallbackId, path, SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);
        }

        if (lobbyId == 0)
        {
            return SteamCallResult<LobbyEnter>.FailedAtStart(LobbyEnter.CallbackId, path, SteamCallResultFailure.NoHandle, SteamApiCallFailure.None);
        }

        try
        {
            ulong handle = SteamNative.JoinLobby(self, lobbyId);
            return new SteamCallResult<LobbyEnter>(handle, LobbyEnter.CallbackId, path, maxPolls);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return SteamCallResult<LobbyEnter>.FailedAtStart(LobbyEnter.CallbackId, path, SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);
        }
    }

    /// <summary>Leaves a lobby. False only when Steam isn't available.</summary>
    public static bool LeaveLobby(ulong lobbyId)
    {
        nint self = SteamInterfaces.Matchmaking();
        if (self == 0)
        {
            return false;
        }

        try
        {
            SteamNative.LeaveLobby(self, lobbyId);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Sets a lobby data key (owner only). Steam sends the change to every member.</summary>
    public static bool SetLobbyData(ulong lobbyId, string key, string value)
    {
        nint self = SteamInterfaces.Matchmaking();
        byte[]? keyUtf8 = NativeStrings.ToUtf8(key);
        byte[]? valueUtf8 = NativeStrings.ToUtf8(value);
        if (self == 0 || keyUtf8 is null || valueUtf8 is null)
        {
            return false;
        }

        try
        {
            return SteamNative.SetLobbyData(self, lobbyId, keyUtf8, valueUtf8);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// A lobby data value, or an empty string if it's unset, unknown (for a lobby you aren't in: not fetched yet), or Steam
    /// isn't available.
    /// </summary>
    public static string GetLobbyData(ulong lobbyId, string key)
    {
        nint self = SteamInterfaces.Matchmaking();
        byte[]? keyUtf8 = NativeStrings.ToUtf8(key);
        if (self == 0 || keyUtf8 is null)
        {
            return string.Empty;
        }

        try
        {
            return NativeStrings.FromPointer(SteamNative.GetLobbyData(self, lobbyId, keyUtf8));
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return string.Empty;
        }
    }

    /// <summary>
    /// How many data keys Steam has for a lobby: 0 if it has none (for a lobby you aren't in, until
    /// <see cref="RequestLobbyData"/>'s answer has arrived, which makes this the arrival signal to poll), or Steam isn't
    /// available or lacks this optional export.
    /// </summary>
    public static int GetLobbyDataCount(ulong lobbyId)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.GetLobbyDataCount));
        if (self == 0 || lobbyId == 0)
        {
            return 0;
        }

        try
        {
            return Math.Max(0, SteamNative.GetLobbyDataCount(self, lobbyId));
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>Sets the local user's member data in a lobby (each member can set only their own).</summary>
    public static bool SetLobbyMemberData(ulong lobbyId, string key, string value)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.SetLobbyMemberData));
        byte[]? keyUtf8 = NativeStrings.ToUtf8(key);
        byte[]? valueUtf8 = NativeStrings.ToUtf8(value);
        if (self == 0 || keyUtf8 is null || valueUtf8 is null)
        {
            return false;
        }

        try
        {
            SteamNative.SetLobbyMemberData(self, lobbyId, keyUtf8, valueUtf8);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>A member's data value, or an empty string.</summary>
    public static string GetLobbyMemberData(ulong lobbyId, ulong userId, string key)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.GetLobbyMemberData));
        byte[]? keyUtf8 = NativeStrings.ToUtf8(key);
        if (self == 0 || keyUtf8 is null)
        {
            return string.Empty;
        }

        try
        {
            return NativeStrings.FromPointer(SteamNative.GetLobbyMemberData(self, lobbyId, userId, keyUtf8));
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return string.Empty;
        }
    }

    /// <summary>Number of members, or 0 if you aren't in the lobby or Steam isn't available.</summary>
    public static int GetMemberCount(ulong lobbyId)
    {
        nint self = SteamInterfaces.Matchmaking();
        if (self == 0)
        {
            return 0;
        }

        try
        {
            return Math.Max(0, SteamNative.GetNumLobbyMembers(self, lobbyId));
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>The member at <paramref name="index"/> (0 to <see cref="GetMemberCount"/> - 1), or 0.</summary>
    public static ulong GetMemberByIndex(ulong lobbyId, int index)
    {
        nint self = SteamInterfaces.Matchmaking();
        if (self == 0 || index < 0)
        {
            return 0;
        }

        try
        {
            return SteamNative.GetLobbyMemberByIndex(self, lobbyId, index);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>Every member's Steam ID (empty if you aren't in the lobby). Allocates; meant for timers, not every frame.</summary>
    public static ulong[] GetMembers(ulong lobbyId)
    {
        int count = GetMemberCount(lobbyId);
        var members = new ulong[count];
        int found = 0;
        for (int i = 0; i < count; i++)
        {
            ulong member = GetMemberByIndex(lobbyId, i);
            if (member != 0)
            {
                members[found++] = member;
            }
        }

        return found == count ? members : members[..found];
    }

    /// <summary>The lobby owner (the host), or 0 if unknown.</summary>
    public static ulong GetOwner(ulong lobbyId)
    {
        nint self = SteamInterfaces.Matchmaking();
        if (self == 0)
        {
            return 0;
        }

        try
        {
            return SteamNative.GetLobbyOwner(self, lobbyId);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>
    /// Opens or closes the lobby to new members (owner only; members already in stay). False when Steam refuses or can't
    /// be asked.
    /// </summary>
    public static bool SetJoinable(ulong lobbyId, bool joinable)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.SetLobbyJoinable));
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.SetLobbyJoinable(self, lobbyId, joinable);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>Changes the lobby type (owner only).</summary>
    public static bool SetLobbyType(ulong lobbyId, SteamLobbyType lobbyType)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.SetLobbyType));
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.SetLobbyType(self, lobbyId, lobbyType);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// Asks Steam to fetch a lobby's data (for a lobby you aren't in, such as a friend's from
    /// <see cref="SteamFriendsLobbies.FindFriendLobbies(uint)"/>; never needed for a lobby you're in). True if the request was
    /// sent; false if Steam isn't available, not connected to the Steam servers, or lacks this optional export. The
    /// answer arrives as a <c>LobbyDataUpdate_t</c> callback we don't see, so poll <see cref="GetLobbyDataCount"/> until
    /// it's above 0 (it never will be if the lobby no longer exists), then read the keys.
    /// </summary>
    public static bool RequestLobbyData(ulong lobbyId)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.RequestLobbyData));
        if (self == 0 || lobbyId == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.RequestLobbyData(self, lobbyId);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// Sends a Steam invite to <paramref name="inviteeId"/> for a lobby we are in (a friend gets Steam's "invited you to a
    /// game" notification; accepting it starts their game with <c>+connect_lobby</c>, or raises
    /// <c>GameLobbyJoinRequested_t</c> if it runs). True only means the invite went out: Steam doesn't check that the
    /// invitee got it. False when it didn't go out (we aren't in the lobby, Steam has no connection, the user isn't valid,
    /// Steam isn't available or lacks this optional export).
    /// </summary>
    public static bool InviteUser(ulong lobbyId, ulong inviteeId)
    {
        nint self = SteamInterfaces.Matchmaking(nameof(SteamNative.InviteUserToLobby));
        if (self == 0 || lobbyId == 0 || inviteeId == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.InviteUserToLobby(self, lobbyId, inviteeId);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }
}
