using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// Steam's flat C API (steam_api_flat.h, steam_api.h), called directly. Every <c>EntryPoint</c> here is checked against
/// the exports of the game's steam_api64.dll after each game update, and at run time by
/// <see cref="SteamRuntime"/> before any of them is called.
/// <para>
/// Rules: the library is already loaded by the game (Unity's plugin loader), and a bare-name DllImport resolves to that
/// loaded module. These calls never initialise Steam, run callbacks or register callbacks: the game owns Steam's
/// manual callback dispatch through Facepunch.Steamworks. The one function pointer handed to Steam is the developer-only
/// debug output (<see cref="SetDebugOutputFunction"/>, which isn't a callback of that dispatch). C++ <c>bool</c> is one byte, so every bool is marshalled as
/// <see cref="UnmanagedType.U1"/>. Strings go in as null-terminated UTF-8 byte arrays (CA2101 flags any
/// string marshalling) and come back as pointers decoded by <see cref="NativeStrings"/>. Interface pointers
/// (<c>self</c>) come from the versioned accessors in this class and are never cached across calls.
/// </para>
/// <para>
/// Every binding is required unless marked <see cref="OptionalExportAttribute"/>; accessors marked
/// <see cref="InterfaceAccessorAttribute"/> are alternative versions of one interface, and one of them is required.
/// </para>
/// Nothing outside <see cref="T2TOnline.Steam"/> calls these directly: the managed layer wraps each call so a missing
/// library, export or interface becomes a failure value.
/// </summary>
internal static class SteamNative
{
    /// <summary>The library name, as DllImport probes it (steam_api64.dll on Windows x64).</summary>
    public const string Library = "steam_api64";

    // Interface accessors (steam_api_flat.h). Each returns null until Steam is initialised. The game's steam_api64 has
    // SteamUtils v010 and SteamFriends v017; the current SDK has v011 and v018, whose flat methods called here have the
    // same signatures, so either version is accepted.

    [DllImport(Library, EntryPoint = "SteamAPI_SteamNetworkingMessages_SteamAPI_v002", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamNetworkingMessages();

    [DllImport(Library, EntryPoint = "SteamAPI_SteamNetworkingUtils_SteamAPI_v004", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamNetworkingUtils();

    [DllImport(Library, EntryPoint = "SteamAPI_SteamMatchmaking_v009", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamMatchmaking();

    [InterfaceAccessor("ISteamUtils")]
    [DllImport(Library, EntryPoint = "SteamAPI_SteamUtils_v010", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamUtilsV010();

    [InterfaceAccessor("ISteamUtils")]
    [DllImport(Library, EntryPoint = "SteamAPI_SteamUtils_v011", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamUtilsV011();

    [DllImport(Library, EntryPoint = "SteamAPI_SteamUser_v023", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamUser();

    [InterfaceAccessor("ISteamFriends")]
    [DllImport(Library, EntryPoint = "SteamAPI_SteamFriends_v017", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamFriendsV017();

    [InterfaceAccessor("ISteamFriends")]
    [DllImport(Library, EntryPoint = "SteamAPI_SteamFriends_v018", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint SteamFriendsV018();

    /// <summary>The client pipe Steam was initialised with (0 if not initialised); needed by manual dispatch calls.</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_GetHSteamPipe", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetHSteamPipe();

    // SteamNetworkingIdentity and SteamNetworkingMessage_t helpers.

    [DllImport(Library, EntryPoint = "SteamAPI_SteamNetworkingIdentity_Clear", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IdentityClear(ref SteamNetworkingIdentity self);

    [DllImport(Library, EntryPoint = "SteamAPI_SteamNetworkingIdentity_SetSteamID64", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IdentitySetSteamId64(ref SteamNetworkingIdentity self, ulong steamId);

    [DllImport(Library, EntryPoint = "SteamAPI_SteamNetworkingIdentity_GetSteamID64", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong IdentityGetSteamId64(in SteamNetworkingIdentity self);

    [DllImport(Library, EntryPoint = "SteamAPI_SteamNetworkingMessage_t_Release", CallingConvention = CallingConvention.Cdecl)]
    public static extern void MessageRelease(nint message);

    // ISteamNetworkingMessages (v002).

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingMessages_SendMessageToUser", CallingConvention = CallingConvention.Cdecl)]
    public static extern SteamResult SendMessageToUser(nint self, in SteamNetworkingIdentity identityRemote, in byte data, uint size, SteamSendType sendFlags, int remoteChannel);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ReceiveMessagesOnChannel(nint self, int localChannel, [Out] nint[] messages, int maxMessages);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AcceptSessionWithUser(nint self, in SteamNetworkingIdentity identityRemote);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CloseSessionWithUser(nint self, in SteamNetworkingIdentity identityRemote);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingMessages_CloseChannelWithUser", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CloseChannelWithUser(nint self, in SteamNetworkingIdentity identityRemote, int localChannel);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo", CallingConvention = CallingConvention.Cdecl)]
    public static extern SteamNetworkingConnectionState GetSessionConnectionInfo(nint self, in SteamNetworkingIdentity identityRemote, out SteamNetConnectionInfo connectionInfo, out SteamNetConnectionRealTimeStatus quickStatus);

    // ISteamNetworkingUtils (v004).

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess", CallingConvention = CallingConvention.Cdecl)]
    public static extern void InitRelayNetworkAccess(nint self);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus", CallingConvention = CallingConvention.Cdecl)]
    public static extern SteamNetworkingAvailability GetRelayNetworkStatus(nint self, out SteamRelayNetworkStatus details);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation", CallingConvention = CallingConvention.Cdecl)]
    public static extern float GetLocalPingLocation(nint self, out SteamNetworkPingLocation result);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations", CallingConvention = CallingConvention.Cdecl)]
    public static extern int EstimatePingTimeBetweenTwoLocations(nint self, in SteamNetworkPingLocation location1, in SteamNetworkPingLocation location2);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeFromLocalHost", CallingConvention = CallingConvention.Cdecl)]
    public static extern int EstimatePingTimeFromLocalHost(nint self, in SteamNetworkPingLocation remoteLocation);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ConvertPingLocationToString(nint self, in SteamNetworkPingLocation location, [Out] byte[] buffer, int bufferSize);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_ParsePingLocationString", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool ParsePingLocationString(nint self, byte[] text, out SteamNetworkPingLocation result);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_CheckPingDataUpToDate", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CheckPingDataUpToDate(nint self, float maxAgeSeconds);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetLocalTimestamp", CallingConvention = CallingConvention.Cdecl)]
    public static extern long GetLocalTimestamp(nint self);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetGlobalConfigValueInt32(nint self, SteamNetworkingConfigValue value, int data);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetGlobalConfigValueFloat(nint self, SteamNetworkingConfigValue value, float data);

    /// <summary><c>SetConfigValue</c> with an int32 argument (<c>pArg</c> points at the value).</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_SetConfigValue", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetConfigValueInt32(nint self, SteamNetworkingConfigValue value, SteamNetworkingConfigScope scope, nint scopeObject, SteamNetworkingConfigDataType dataType, in int data);

    /// <summary><c>SetConfigValue</c> with a float argument (<c>pArg</c> points at the value).</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_SetConfigValue", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetConfigValueFloat(nint self, SteamNetworkingConfigValue value, SteamNetworkingConfigScope scope, nint scopeObject, SteamNetworkingConfigDataType dataType, in float data);

    /// <summary>
    /// <c>SetDebugOutputFunction</c>: Steam's networking debug output at <paramref name="detailLevel"/> and finer goes to
    /// <paramref name="function"/> (a Cdecl <c>void (ESteamNetworkingSocketsDebugOutputType, const char*)</c>), from whichever
    /// thread prints it; a null function or <see cref="SteamNetworkingDebugOutputType.None"/> unregisters. For
    /// <see cref="SteamDebugOutput"/> only.
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetDebugOutputFunction(nint self, SteamNetworkingDebugOutputType detailLevel, nint function);

    /// <summary><c>GetConfigValue</c>: <paramref name="resultSize"/> is the buffer size in, bytes written (or needed) out.</summary>
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetConfigValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern SteamNetworkingGetConfigValueResult GetConfigValue(nint self, SteamNetworkingConfigValue value, SteamNetworkingConfigScope scope, nint scopeObject, out SteamNetworkingConfigDataType dataType, ref byte result, ref nuint resultSize);

    // ISteamMatchmaking (v009). Plain calls only; no callbacks are registered.

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_CreateLobby", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong CreateLobby(nint self, SteamLobbyType lobbyType, int maxMembers);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_JoinLobby", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong JoinLobby(nint self, ulong lobbyId);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_LeaveLobby", CallingConvention = CallingConvention.Cdecl)]
    public static extern void LeaveLobby(nint self, ulong lobbyId);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNumLobbyMembers(nint self, ulong lobbyId);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong GetLobbyMemberByIndex(nint self, ulong lobbyId, int member);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_GetLobbyData", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetLobbyData(nint self, ulong lobbyId, byte[] key);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_SetLobbyData", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetLobbyData(nint self, ulong lobbyId, byte[] key, byte[] value);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_GetLobbyDataCount", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetLobbyDataCount(nint self, ulong lobbyId);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_GetLobbyMemberData", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetLobbyMemberData(nint self, ulong lobbyId, ulong userId, byte[] key);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_SetLobbyMemberData", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetLobbyMemberData(nint self, ulong lobbyId, byte[] key, byte[] value);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_RequestLobbyData", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool RequestLobbyData(nint self, ulong lobbyId);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_SetLobbyType", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetLobbyType(nint self, ulong lobbyId, SteamLobbyType lobbyType);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_SetLobbyJoinable", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetLobbyJoinable(nint self, ulong lobbyId, [MarshalAs(UnmanagedType.U1)] bool joinable);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_GetLobbyOwner", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong GetLobbyOwner(nint self, ulong lobbyId);

    /// <summary>
    /// <c>InviteUserToLobby</c>: true if the invite was sent. Steam doesn't check that the invitee got it: false only when
    /// the caller isn't in the lobby, has no Steam connection, or the invitee isn't a valid user.
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamMatchmaking_InviteUserToLobby", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool InviteUserToLobby(nint self, ulong lobbyId, ulong inviteeId);

    // ISteamUtils (v010 or v011): call-result polling.

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_GetAppID", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint GetAppId(nint self);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_IsAPICallCompleted", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool IsApiCallCompleted(nint self, ulong apiCall, [MarshalAs(UnmanagedType.U1)] out bool failed);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_GetAPICallFailureReason", CallingConvention = CallingConvention.Cdecl)]
    public static extern SteamApiCallFailure GetApiCallFailureReason(nint self, ulong apiCall);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_GetAPICallResult", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetApiCallResult(nint self, ulong apiCall, ref byte callback, int callbackSize, int callbackExpected, [MarshalAs(UnmanagedType.U1)] out bool failed);

    /// <summary><c>IsOverlayEnabled</c>: true when Steam's in-game overlay is up for this game (the invite window needs it).</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_IsOverlayEnabled", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool IsOverlayEnabled(nint self);

    /// <summary>
    /// <c>GetImageSize</c>: the width and height of an image handle (an avatar's), or false for a handle Steam doesn't know.
    /// Never trust the sizes the docs give for the avatars: read this.
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_GetImageSize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetImageSize(nint self, int image, out uint width, out uint height);

    /// <summary>
    /// <c>GetImageRGBA</c>: writes an image's pixels (4 bytes each, R G B A, <paramref name="destSize"/> at least
    /// width x height x 4) to <paramref name="dest"/>; false for an unknown handle or a buffer that is too small.
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUtils_GetImageRGBA", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetImageRGBA(nint self, int image, ref byte dest, int destSize);

    /// <summary><c>SteamAPI_ManualDispatch_GetAPICallResult</c> (steam_api.h): the manual-dispatch way to fetch a result.</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ManualDispatch_GetAPICallResult", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool ManualDispatchGetApiCallResult(int steamPipe, ulong apiCall, ref byte callback, int callbackSize, int callbackExpected, [MarshalAs(UnmanagedType.U1)] out bool failed);

    // ISteamUser (v023).

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamUser_GetSteamID", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong GetSteamId(nint self);

    // ISteamFriends (v017 or v018).

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetPersonaName", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetPersonaName(nint self);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetFriendPersonaName", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetFriendPersonaName(nint self, ulong friendId);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetFriendCount", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetFriendCount(nint self, SteamFriendFilter friendFlags);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetFriendByIndex", CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong GetFriendByIndex(nint self, int friendIndex, SteamFriendFilter friendFlags);

    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetFriendGamePlayed", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetFriendGamePlayed(nint self, ulong friendId, out FriendGameInfo gameInfo);

    /// <summary><c>HasFriend</c>: true if the user meets any of <paramref name="friendFlags"/> (an <c>int</c> of <c>EFriendFlags</c>).</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_HasFriend", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HasFriend(nint self, ulong friendId, SteamFriendFilter friendFlags);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_SetRichPresence", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetRichPresence(nint self, byte[] key, byte[] value);

    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_ClearRichPresence", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ClearRichPresence(nint self);

    /// <summary>
    /// <c>ActivateGameOverlayInviteDialog</c>: opens Steam's overlay on its invite window for lobby
    /// <paramref name="lobbyId"/>; a friend who accepts gets <c>GameLobbyJoinRequested_t</c> (or <c>+connect_lobby</c> on
    /// the command line when their game isn't running).
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ActivateGameOverlayInviteDialog(nint self, ulong lobbyId);

    /// <summary><c>GetFriendPersonaState</c>: an <c>EPersonaState</c>; only known for friends and users sharing a lobby, otherwise offline.</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetFriendPersonaState", CallingConvention = CallingConvention.Cdecl)]
    public static extern SteamPersonaState GetFriendPersonaState(nint self, ulong friendId);

    /// <summary>
    /// <c>RequestUserInformation</c>: asks Steam for a user's name and (with <paramref name="requireNameOnly"/> false) avatar.
    /// True means the request was made and the data is on its way; false means everything is known already.
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_RequestUserInformation", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool RequestUserInformation(nint self, ulong userId, [MarshalAs(UnmanagedType.U1)] bool requireNameOnly);

    /// <summary><c>GetSmallFriendAvatar</c>: an image handle (0: no avatar set, -1: not loaded yet, ask again).</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetSmallFriendAvatar", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSmallFriendAvatar(nint self, ulong friendId);

    /// <summary><c>GetMediumFriendAvatar</c>: as <see cref="GetSmallFriendAvatar"/>.</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetMediumFriendAvatar", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetMediumFriendAvatar(nint self, ulong friendId);

    /// <summary><c>GetLargeFriendAvatar</c>: as <see cref="GetSmallFriendAvatar"/>.</summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_GetLargeFriendAvatar", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetLargeFriendAvatar(nint self, ulong friendId);

    /// <summary>
    /// <c>ActivateGameOverlayToUser</c>: opens the overlay on a dialog about a user (<c>"steamid"</c> is their profile,
    /// <c>"chat"</c> a chat with them).
    /// </summary>
    [OptionalExport]
    [DllImport(Library, EntryPoint = "SteamAPI_ISteamFriends_ActivateGameOverlayToUser", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ActivateGameOverlayToUser(nint self, byte[] dialog, ulong steamId);
}
