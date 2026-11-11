using System;

namespace T2TOnline.Steam.Native;

/// <summary>
/// The <c>EResult</c> values (steamclientpublic.h) this project checks for. Steam can return any other EResult too;
/// they come through as their raw number.
/// </summary>
public enum SteamResult
{
    /// <summary>
    /// <c>k_EResultNone</c> (0). Steam never returns it from the calls bound here; this library returns it when Steam
    /// isn't available (library, export or interface missing) or the arguments were rejected before calling Steam.
    /// </summary>
    None = 0,

    /// <summary><c>k_EResultOK</c>.</summary>
    OK = 1,

    /// <summary><c>k_EResultFail</c>: generic failure.</summary>
    Fail = 2,

    /// <summary><c>k_EResultNoConnection</c>: the session failed or the peer closed it.</summary>
    NoConnection = 3,

    /// <summary><c>k_EResultInvalidParam</c>.</summary>
    InvalidParam = 8,

    /// <summary><c>k_EResultInvalidState</c>.</summary>
    InvalidState = 11,

    /// <summary><c>k_EResultAccessDenied</c>.</summary>
    AccessDenied = 15,

    /// <summary><c>k_EResultTimeout</c>.</summary>
    Timeout = 16,

    /// <summary><c>k_EResultLimitExceeded</c>: e.g. the send buffer is full.</summary>
    LimitExceeded = 25,

    /// <summary>
    /// <c>k_EResultConnectFailed</c>: what <c>ISteamNetworkingMessages</c> answers a send without
    /// <see cref="SteamSendType.AutoRestartBrokenSession"/> on a session that broke (GameNetworkingSockets'
    /// <c>SendMessageToUser</c>; the SDK header says <see cref="NoConnection"/>).
    /// </summary>
    ConnectFailed = 35,

    /// <summary><c>k_EResultIgnored</c>: a <see cref="SteamSendType.NoDelay"/> message was dropped instead of queued.</summary>
    Ignored = 41,

    /// <summary><c>k_EResultRateLimitExceeded</c>.</summary>
    RateLimitExceeded = 84,
}

/// <summary><c>ELobbyType</c> (isteammatchmaking.h).</summary>
public enum SteamLobbyType
{
    /// <summary><c>k_ELobbyTypePrivate</c>: joinable by invite only.</summary>
    Private = 0,

    /// <summary><c>k_ELobbyTypeFriendsOnly</c>: joinable by friends and invitees, not listed.</summary>
    FriendsOnly = 1,

    /// <summary><c>k_ELobbyTypePublic</c>: listed and joinable by anyone.</summary>
    Public = 2,

    /// <summary><c>k_ELobbyTypeInvisible</c>: returned by search, not visible to friends.</summary>
    Invisible = 3,

    /// <summary><c>k_ELobbyTypePrivateUnique</c>: private, one per user.</summary>
    PrivateUnique = 4,
}

/// <summary><c>ESteamAPICallFailure</c> (isteamutils.h): why an API call result couldn't be returned.</summary>
public enum SteamApiCallFailure
{
    /// <summary><c>k_ESteamAPICallFailureNone</c>: no failure.</summary>
    None = -1,

    /// <summary><c>k_ESteamAPICallFailureSteamGone</c>: lost contact with the Steam client (also used here when Steam isn't available).</summary>
    SteamGone = 0,

    /// <summary><c>k_ESteamAPICallFailureNetworkFailure</c>: lost contact with the Steam servers.</summary>
    NetworkFailure = 1,

    /// <summary><c>k_ESteamAPICallFailureInvalidHandle</c>: the handle is unknown, or its result was already fetched.</summary>
    InvalidHandle = 2,

    /// <summary><c>k_ESteamAPICallFailureMismatchedCallback</c>: the expected callback id didn't match the call.</summary>
    MismatchedCallback = 3,
}

/// <summary><c>EChatRoomEnterResponse</c> (steamclientpublic.h): the outcome in <see cref="LobbyEnter"/>.</summary>
public enum SteamChatRoomEnterResponse
{
    /// <summary><c>k_EChatRoomEnterResponseSuccess</c>.</summary>
    Success = 1,

    /// <summary><c>k_EChatRoomEnterResponseDoesntExist</c>: the lobby no longer exists.</summary>
    DoesntExist = 2,

    /// <summary><c>k_EChatRoomEnterResponseNotAllowed</c>.</summary>
    NotAllowed = 3,

    /// <summary><c>k_EChatRoomEnterResponseFull</c>.</summary>
    Full = 4,

    /// <summary><c>k_EChatRoomEnterResponseError</c>.</summary>
    Error = 5,

    /// <summary><c>k_EChatRoomEnterResponseBanned</c>.</summary>
    Banned = 6,

    /// <summary><c>k_EChatRoomEnterResponseLimited</c>: limited user accounts can't join.</summary>
    Limited = 7,

    /// <summary><c>k_EChatRoomEnterResponseClanDisabled</c>.</summary>
    ClanDisabled = 8,

    /// <summary><c>k_EChatRoomEnterResponseCommunityBan</c>.</summary>
    CommunityBan = 9,

    /// <summary><c>k_EChatRoomEnterResponseMemberBlockedYou</c>.</summary>
    MemberBlockedYou = 10,

    /// <summary><c>k_EChatRoomEnterResponseYouBlockedMember</c>.</summary>
    YouBlockedMember = 11,

    /// <summary><c>k_EChatRoomEnterResponseRatelimitExceeded</c>.</summary>
    RateLimitExceeded = 15,
}

/// <summary><c>EFriendFlags</c> (isteamfriends.h): which relationships <c>GetFriendCount</c>/<c>GetFriendByIndex</c> enumerate.</summary>
[Flags]
public enum SteamFriendFilter
{
    /// <summary><c>k_EFriendFlagNone</c>.</summary>
    None = 0x00,

    /// <summary><c>k_EFriendFlagBlocked</c>.</summary>
    Blocked = 0x01,

    /// <summary><c>k_EFriendFlagFriendshipRequested</c>.</summary>
    FriendshipRequested = 0x02,

    /// <summary><c>k_EFriendFlagImmediate</c>: regular friends.</summary>
    Immediate = 0x04,

    /// <summary><c>k_EFriendFlagClanMember</c>.</summary>
    ClanMember = 0x08,

    /// <summary><c>k_EFriendFlagOnGameServer</c>.</summary>
    OnGameServer = 0x10,

    /// <summary><c>k_EFriendFlagRequestingFriendship</c>.</summary>
    RequestingFriendship = 0x80,

    /// <summary><c>k_EFriendFlagRequestingInfo</c>.</summary>
    RequestingInfo = 0x100,

    /// <summary><c>k_EFriendFlagIgnored</c>.</summary>
    Ignored = 0x200,

    /// <summary><c>k_EFriendFlagIgnoredFriend</c>.</summary>
    IgnoredFriend = 0x400,

    /// <summary><c>k_EFriendFlagChatMember</c>.</summary>
    ChatMember = 0x1000,

    /// <summary><c>k_EFriendFlagAll</c>.</summary>
    All = 0xFFFF,
}

/// <summary>
/// <c>EPersonaState</c> (isteamfriends.h): what a friend's Steam client says they are. Steam only knows it for friends and
/// users sharing a lobby; for anyone else it says <see cref="Offline"/>.
/// </summary>
public enum SteamPersonaState
{
    /// <summary>
    /// Not Steam's: this library couldn't ask (Steam isn't available or lacks <c>GetFriendPersonaState</c>), or Steam
    /// answered something this library doesn't know. Not the same as <see cref="Offline"/>.
    /// </summary>
    Unknown = -1,

    /// <summary><c>k_EPersonaStateOffline</c>: offline, or invisible to us.</summary>
    Offline = 0,

    /// <summary><c>k_EPersonaStateOnline</c>.</summary>
    Online = 1,

    /// <summary><c>k_EPersonaStateBusy</c>: "do not disturb".</summary>
    Busy = 2,

    /// <summary><c>k_EPersonaStateAway</c>: away from the keyboard for a while.</summary>
    Away = 3,

    /// <summary><c>k_EPersonaStateSnooze</c>: away for a long time.</summary>
    Snooze = 4,

    /// <summary><c>k_EPersonaStateLookingToTrade</c>.</summary>
    LookingToTrade = 5,

    /// <summary><c>k_EPersonaStateLookingToPlay</c>.</summary>
    LookingToPlay = 6,
}
