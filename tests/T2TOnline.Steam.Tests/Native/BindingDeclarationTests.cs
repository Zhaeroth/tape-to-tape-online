using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests.Native;

/// <summary>Static checks of every P/Invoke declaration: the mistakes that would otherwise only show up in game.</summary>
public class BindingDeclarationTests
{
    private static readonly MethodInfo[] Imports = typeof(SteamNative)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null)
        .ToArray();

    // The flat-API exports the bindings call: the same list is checked against the exports of the game's steam_api64.dll
    // after each game update.
    private static readonly string[] ExpectedExports =
    {
        "SteamAPI_GetHSteamPipe",
        "SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog",
        "SteamAPI_ISteamFriends_ActivateGameOverlayToUser",
        "SteamAPI_ISteamFriends_ClearRichPresence",
        "SteamAPI_ISteamFriends_GetFriendByIndex",
        "SteamAPI_ISteamFriends_GetFriendCount",
        "SteamAPI_ISteamFriends_GetFriendGamePlayed",
        "SteamAPI_ISteamFriends_GetFriendPersonaName",
        "SteamAPI_ISteamFriends_GetFriendPersonaState",
        "SteamAPI_ISteamFriends_GetLargeFriendAvatar",
        "SteamAPI_ISteamFriends_GetMediumFriendAvatar",
        "SteamAPI_ISteamFriends_GetPersonaName",
        "SteamAPI_ISteamFriends_GetSmallFriendAvatar",
        "SteamAPI_ISteamFriends_HasFriend",
        "SteamAPI_ISteamFriends_RequestUserInformation",
        "SteamAPI_ISteamFriends_SetRichPresence",
        "SteamAPI_ISteamMatchmaking_CreateLobby",
        "SteamAPI_ISteamMatchmaking_GetLobbyData",
        "SteamAPI_ISteamMatchmaking_GetLobbyDataCount",
        "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex",
        "SteamAPI_ISteamMatchmaking_GetLobbyMemberData",
        "SteamAPI_ISteamMatchmaking_GetLobbyOwner",
        "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers",
        "SteamAPI_ISteamMatchmaking_InviteUserToLobby",
        "SteamAPI_ISteamMatchmaking_JoinLobby",
        "SteamAPI_ISteamMatchmaking_LeaveLobby",
        "SteamAPI_ISteamMatchmaking_RequestLobbyData",
        "SteamAPI_ISteamMatchmaking_SetLobbyData",
        "SteamAPI_ISteamMatchmaking_SetLobbyJoinable",
        "SteamAPI_ISteamMatchmaking_SetLobbyMemberData",
        "SteamAPI_ISteamMatchmaking_SetLobbyType",
        "SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser",
        "SteamAPI_ISteamNetworkingMessages_CloseChannelWithUser",
        "SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser",
        "SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo",
        "SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel",
        "SteamAPI_ISteamNetworkingMessages_SendMessageToUser",
        "SteamAPI_ISteamNetworkingUtils_CheckPingDataUpToDate",
        "SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString",
        "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations",
        "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeFromLocalHost",
        "SteamAPI_ISteamNetworkingUtils_GetConfigValue",
        "SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation",
        "SteamAPI_ISteamNetworkingUtils_GetLocalTimestamp",
        "SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus",
        "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess",
        "SteamAPI_ISteamNetworkingUtils_ParsePingLocationString",
        "SteamAPI_ISteamNetworkingUtils_SetConfigValue",
        "SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction",
        "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat",
        "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32",
        "SteamAPI_ISteamUser_GetSteamID",
        "SteamAPI_ISteamUtils_GetAPICallFailureReason",
        "SteamAPI_ISteamUtils_GetAPICallResult",
        "SteamAPI_ISteamUtils_GetAppID",
        "SteamAPI_ISteamUtils_GetImageRGBA",
        "SteamAPI_ISteamUtils_GetImageSize",
        "SteamAPI_ISteamUtils_IsAPICallCompleted",
        "SteamAPI_ISteamUtils_IsOverlayEnabled",
        "SteamAPI_ManualDispatch_GetAPICallResult",
        "SteamAPI_SteamFriends_v017",
        "SteamAPI_SteamFriends_v018",
        "SteamAPI_SteamMatchmaking_v009",
        "SteamAPI_SteamNetworkingIdentity_Clear",
        "SteamAPI_SteamNetworkingIdentity_GetSteamID64",
        "SteamAPI_SteamNetworkingIdentity_SetSteamID64",
        "SteamAPI_SteamNetworkingMessage_t_Release",
        "SteamAPI_SteamNetworkingMessages_SteamAPI_v002",
        "SteamAPI_SteamNetworkingUtils_SteamAPI_v004",
        "SteamAPI_SteamUser_v023",
        "SteamAPI_SteamUtils_v010",
        "SteamAPI_SteamUtils_v011",
    };

    // Exports an online session can do without: fallbacks, and calls the plugin doesn't make or has a fallback for. A
    // missing one only turns off the calls that need it.
    private static readonly string[] ExpectedOptionalExports =
    {
        "SteamAPI_GetHSteamPipe",
        "SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog",
        "SteamAPI_ISteamFriends_ActivateGameOverlayToUser",
        "SteamAPI_ISteamFriends_ClearRichPresence",
        "SteamAPI_ISteamFriends_GetFriendPersonaState",
        "SteamAPI_ISteamFriends_GetLargeFriendAvatar",
        "SteamAPI_ISteamFriends_GetMediumFriendAvatar",
        "SteamAPI_ISteamFriends_GetSmallFriendAvatar",
        "SteamAPI_ISteamFriends_HasFriend",
        "SteamAPI_ISteamFriends_RequestUserInformation",
        "SteamAPI_ISteamFriends_SetRichPresence",
        "SteamAPI_ISteamMatchmaking_GetLobbyDataCount",
        "SteamAPI_ISteamMatchmaking_GetLobbyMemberData",
        "SteamAPI_ISteamMatchmaking_InviteUserToLobby",
        "SteamAPI_ISteamMatchmaking_RequestLobbyData",
        "SteamAPI_ISteamMatchmaking_SetLobbyJoinable",
        "SteamAPI_ISteamMatchmaking_SetLobbyMemberData",
        "SteamAPI_ISteamMatchmaking_SetLobbyType",
        "SteamAPI_ISteamNetworkingMessages_CloseChannelWithUser",
        "SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser",
        "SteamAPI_ISteamNetworkingUtils_CheckPingDataUpToDate",
        "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations",
        "SteamAPI_ISteamNetworkingUtils_EstimatePingTimeFromLocalHost",
        "SteamAPI_ISteamNetworkingUtils_ParsePingLocationString",
        "SteamAPI_ISteamNetworkingUtils_SetConfigValue",
        "SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction",
        "SteamAPI_ISteamUtils_GetImageRGBA",
        "SteamAPI_ISteamUtils_GetImageSize",
        "SteamAPI_ISteamUtils_IsOverlayEnabled",
        "SteamAPI_ManualDispatch_GetAPICallResult",
    };

    [Fact]
    public void The_bound_exports_are_exactly_the_reviewed_list()
    {
        Assert.Equal(ExpectedExports.OrderBy(n => n, StringComparer.Ordinal), SteamRuntime.BoundExports);
    }

    [Fact]
    public void The_optional_exports_are_exactly_the_reviewed_list()
    {
        Assert.Equal(ExpectedOptionalExports.OrderBy(n => n, StringComparer.Ordinal), SteamRuntime.OptionalExports);
    }

    [Fact]
    public void Methods_sharing_an_export_agree_on_whether_it_is_optional()
    {
        foreach (IGrouping<string, MethodInfo> export in Imports.GroupBy(m => m.GetCustomAttribute<DllImportAttribute>()!.EntryPoint!))
        {
            Assert.Single(export.Select(m => m.IsDefined(typeof(OptionalExportAttribute))).Distinct());
        }
    }

    [Fact]
    public void Interface_accessors_come_in_versions_with_one_signature()
    {
        MethodInfo[] accessors = Imports.Where(m => m.IsDefined(typeof(InterfaceAccessorAttribute))).ToArray();
        var versions = accessors
            .GroupBy(m => m.GetCustomAttribute<InterfaceAccessorAttribute>()!.InterfaceName)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GetCustomAttribute<DllImportAttribute>()!.EntryPoint).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(new[] { "SteamAPI_SteamUtils_v010", "SteamAPI_SteamUtils_v011" }, versions["ISteamUtils"]);
        Assert.Equal(new[] { "SteamAPI_SteamFriends_v017", "SteamAPI_SteamFriends_v018" }, versions["ISteamFriends"]);
        Assert.Equal(2, versions.Count);
        Assert.All(accessors, m =>
        {
            Assert.Empty(m.GetParameters());
            Assert.Equal(typeof(nint), m.ReturnType);
            Assert.False(m.IsDefined(typeof(OptionalExportAttribute)));
        });
    }

    [Theory]
    [InlineData(nameof(SteamNative.HasFriend), typeof(bool), new[] { typeof(nint), typeof(ulong), typeof(SteamFriendFilter) })]
    [InlineData(nameof(SteamNative.SetLobbyJoinable), typeof(bool), new[] { typeof(nint), typeof(ulong), typeof(bool) })]
    [InlineData(nameof(SteamNative.RequestLobbyData), typeof(bool), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.GetLobbyDataCount), typeof(int), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.ActivateGameOverlayInviteDialog), typeof(void), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.IsOverlayEnabled), typeof(bool), new[] { typeof(nint) })]
    [InlineData(nameof(SteamNative.SetRichPresence), typeof(bool), new[] { typeof(nint), typeof(byte[]), typeof(byte[]) })]
    [InlineData(nameof(SteamNative.InviteUserToLobby), typeof(bool), new[] { typeof(nint), typeof(ulong), typeof(ulong) })]
    [InlineData(nameof(SteamNative.RequestUserInformation), typeof(bool), new[] { typeof(nint), typeof(ulong), typeof(bool) })]
    [InlineData(nameof(SteamNative.GetSmallFriendAvatar), typeof(int), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.GetMediumFriendAvatar), typeof(int), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.GetLargeFriendAvatar), typeof(int), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.GetFriendPersonaState), typeof(SteamPersonaState), new[] { typeof(nint), typeof(ulong) })]
    [InlineData(nameof(SteamNative.ActivateGameOverlayToUser), typeof(void), new[] { typeof(nint), typeof(byte[]), typeof(ulong) })]
    public void Friend_and_lobby_checks_match_their_flat_signatures(string method, Type returns, Type[] parameters)
    {
        // steam_api_flat.h: bool HasFriend(ISteamFriends*, uint64_steamid, int iFriendFlags);
        // bool SetLobbyJoinable(ISteamMatchmaking*, uint64_steamid, bool); bool RequestLobbyData(ISteamMatchmaking*,
        // uint64_steamid); int GetLobbyDataCount(ISteamMatchmaking*, uint64_steamid);
        // void ActivateGameOverlayInviteDialog(ISteamFriends*, uint64_steamid steamIDLobby); bool IsOverlayEnabled(ISteamUtils*);
        // bool SetRichPresence(ISteamFriends*, const char*, const char*).
        // bool InviteUserToLobby(ISteamMatchmaking*, uint64_steamid lobby, uint64_steamid invitee);
        // bool RequestUserInformation(ISteamFriends*, uint64_steamid, bool); int Get{Small,Medium,Large}FriendAvatar(ISteamFriends*, uint64_steamid);
        // EPersonaState GetFriendPersonaState(ISteamFriends*, uint64_steamid);
        // void ActivateGameOverlayToUser(ISteamFriends*, const char* dialog, uint64_steamid).
        MethodInfo import = Imports.Single(m => m.Name == method);
        Assert.Equal(returns, import.ReturnType);
        Assert.Equal(parameters, import.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(SteamFriendFilter)));
        Assert.Equal(4, (int)SteamFriendFilter.Immediate);
    }

    [Fact]
    public void The_image_calls_match_their_flat_signatures()
    {
        // steam_api_flat.h: bool GetImageSize(ISteamUtils*, int iImage, uint32* pnWidth, uint32* pnHeight);
        // bool GetImageRGBA(ISteamUtils*, int iImage, uint8* pubDest, int nDestBufferSize).
        MethodInfo size = Imports.Single(m => m.Name == nameof(SteamNative.GetImageSize));
        Assert.Equal(typeof(bool), size.ReturnType);
        Assert.Equal(new[] { typeof(nint), typeof(int), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() }, size.GetParameters().Select(p => p.ParameterType));
        Assert.All(size.GetParameters().Skip(2), p => Assert.True(p.IsOut));

        MethodInfo rgba = Imports.Single(m => m.Name == nameof(SteamNative.GetImageRGBA));
        Assert.Equal(typeof(bool), rgba.ReturnType);
        Assert.Equal(new[] { typeof(nint), typeof(int), typeof(byte).MakeByRefType(), typeof(int) }, rgba.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void The_debug_output_call_matches_its_flat_signature()
    {
        // steam_api_flat.h: void SetDebugOutputFunction(ISteamNetworkingUtils*, ESteamNetworkingSocketsDebugOutputType eDetailLevel,
        // FSteamNetworkingSocketsDebugOutput pfnFunc), the function a plain pointer; the enum an int.
        MethodInfo import = Imports.Single(m => m.Name == nameof(SteamNative.SetDebugOutputFunction));
        Assert.Equal(typeof(void), import.ReturnType);
        Assert.Equal(new[] { typeof(nint), typeof(SteamNetworkingDebugOutputType), typeof(nint) }, import.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(SteamNetworkingDebugOutputType)));
        Assert.Equal(0, (int)SteamNetworkingDebugOutputType.None);
        Assert.Equal(5, (int)SteamNetworkingDebugOutputType.Msg);
        Assert.Equal(6, (int)SteamNetworkingDebugOutputType.Verbose);
        Assert.Equal(17, (int)SteamNetworkingConfigValue.LogLevelP2PRendezvous);
    }

    [Fact]
    public void Every_import_is_cdecl_against_steam_api64_with_an_explicit_export_name()
    {
        Assert.NotEmpty(Imports);
        foreach (MethodInfo method in Imports)
        {
            DllImportAttribute import = method.GetCustomAttribute<DllImportAttribute>()!;
            Assert.Equal("steam_api64", import.Value);
            Assert.Equal(CallingConvention.Cdecl, import.CallingConvention);
            Assert.StartsWith("SteamAPI_", import.EntryPoint, StringComparison.Ordinal);
            Assert.NotEqual(method.Name, import.EntryPoint);
        }
    }

    [Fact]
    public void Every_bool_crosses_as_one_byte()
    {
        foreach (MethodInfo method in Imports)
        {
            foreach (ParameterInfo parameter in method.GetParameters().Append(method.ReturnParameter))
            {
                Type type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                if (type == typeof(bool))
                {
                    MarshalAsAttribute? marshalAs = parameter.GetCustomAttribute<MarshalAsAttribute>();
                    Assert.True(marshalAs?.Value == UnmanagedType.U1, $"{method.Name}: bool '{parameter.Name ?? "return"}' must be [MarshalAs(UnmanagedType.U1)]");
                }
            }
        }
    }

    [Fact]
    public void Every_parameter_is_blittable_or_a_utf8_byte_array()
    {
        foreach (MethodInfo method in Imports)
        {
            foreach (ParameterInfo parameter in method.GetParameters().Append(method.ReturnParameter))
            {
                Type type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                bool ok = type == typeof(void) || type == typeof(bool) || type.IsPrimitive || type.IsEnum ||
                    type == typeof(byte[]) || type == typeof(nint[]) ||
                    (type.IsValueType && !IsReferenceOrContainsReferences(type) && Marshal.SizeOf(type) == UnsafeSizeOf(type));
                Assert.True(ok, $"{method.Name}: parameter '{parameter.Name ?? "return"}' has non-blittable type {type}");
                Assert.NotEqual(typeof(string), type);
            }
        }
    }

    private static bool IsReferenceOrContainsReferences(Type type) =>
        (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(type).Invoke(null, null)!;

    private static int UnsafeSizeOf(Type type) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
}
