using System;
using System.Linq;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

/// <summary>
/// The export probe's rules, with a scripted export table instead of steam_api64: a missing required export turns every
/// binding off, a missing optional one only its own calls, and either version of an interface accessor will do.
/// </summary>
[Collection(SharedSteamRuntime.Name)]
public sealed class SteamRuntimeProbeTests : IDisposable
{
    private const ulong Peer = 76561199000000001UL;

    public void Dispose() => SteamRuntime.Reset();

    [Fact]
    public void With_every_export_present_the_bindings_are_ready_and_use_the_newest_accessors()
    {
        SteamRuntime.Reset(_ => true);

        Assert.True(SteamRuntime.BindingsReady);
        Assert.Empty(SteamRuntime.MissingExports);
        Assert.True(SteamRuntime.HasExport(nameof(SteamNative.SteamUtilsV011)));
        Assert.True(SteamRuntime.HasExport(nameof(SteamNative.SteamFriendsV018)));
        Assert.True(SteamRuntime.HasExport(nameof(SteamNative.SetLobbyJoinable)));
        Assert.Contains("has all", SteamRuntime.Status, StringComparison.Ordinal);
        Assert.Contains("SteamAPI_SteamUtils_v011", SteamRuntime.Status, StringComparison.Ordinal);
        Assert.Contains("SteamAPI_SteamFriends_v018", SteamRuntime.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_required_export_turns_every_binding_off()
    {
        SteamRuntime.Reset(name => name != "SteamAPI_ISteamMatchmaking_CreateLobby");

        Assert.False(SteamRuntime.BindingsReady);
        Assert.Equal(new[] { "SteamAPI_ISteamMatchmaking_CreateLobby" }, SteamRuntime.MissingExports);
        Assert.False(SteamRuntime.HasExport(nameof(SteamNative.JoinLobby)));
        Assert.False(SteamRuntime.HasExport(nameof(SteamNative.SetLobbyJoinable)));
        Assert.Contains("lacks 1 of", SteamRuntime.Status, StringComparison.Ordinal);
        Assert.Contains("SteamAPI_ISteamMatchmaking_CreateLobby", SteamRuntime.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_optional_export_only_turns_its_own_calls_off()
    {
        SteamRuntime.Reset(name => name != "SteamAPI_ISteamMatchmaking_SetLobbyJoinable");

        Assert.True(SteamRuntime.BindingsReady);
        Assert.Equal(new[] { "SteamAPI_ISteamMatchmaking_SetLobbyJoinable" }, SteamRuntime.MissingExports);
        Assert.False(SteamRuntime.HasExport(nameof(SteamNative.SetLobbyJoinable)));
        Assert.True(SteamRuntime.HasExport(nameof(SteamNative.CreateLobby)));
        Assert.Contains("SteamAPI_ISteamMatchmaking_SetLobbyJoinable", SteamRuntime.Status, StringComparison.Ordinal);

        Assert.False(SteamLobbies.SetJoinable(1, false));
        Assert.True(SteamRuntime.BindingsReady); // Refused before any native call, so nothing failed.
    }

    [Fact]
    public void Without_has_friend_steam_cant_say_who_is_a_friend_and_says_why()
    {
        SteamRuntime.Reset(name => name != "SteamAPI_ISteamFriends_HasFriend");

        Assert.True(SteamRuntime.BindingsReady);
        Assert.False(SteamFriends.CanCheckFriends);
        Assert.False(SteamFriends.TryIsFriend(Peer, out bool isFriend));
        Assert.False(isFriend);
        Assert.Equal("steam_api64 has no SteamAPI_ISteamFriends_HasFriend", SteamFriends.FriendCheckProblem(Peer));
        Assert.True(SteamRuntime.BindingsReady); // refused before any native call
    }

    [Fact]
    public void With_has_friend_the_check_can_ask_and_a_failure_is_the_friends_interface_or_the_id()
    {
        SteamRuntime.Reset(_ => true);

        Assert.True(SteamFriends.CanCheckFriends);
        Assert.Equal("no Steam ID to ask about", SteamFriends.FriendCheckProblem(0));
        Assert.Equal("Steam's friends interface isn't available", SteamFriends.FriendCheckProblem(Peer));
    }

    [Theory]
    [InlineData("SteamAPI_SteamUtils_v010", nameof(SteamNative.SteamUtilsV011))]
    [InlineData("SteamAPI_SteamUtils_v011", nameof(SteamNative.SteamUtilsV010))]
    [InlineData("SteamAPI_SteamFriends_v017", nameof(SteamNative.SteamFriendsV018))]
    [InlineData("SteamAPI_SteamFriends_v018", nameof(SteamNative.SteamFriendsV017))]
    public void Either_version_of_an_interface_accessor_is_enough(string absent, string used)
    {
        SteamRuntime.Reset(name => name != absent);

        Assert.True(SteamRuntime.BindingsReady);
        Assert.Empty(SteamRuntime.MissingExports);
        Assert.True(SteamRuntime.HasExport(used));
    }

    [Fact]
    public void An_interface_without_any_accessor_version_is_a_missing_required_export()
    {
        SteamRuntime.Reset(name => !name.StartsWith("SteamAPI_SteamUtils_v", StringComparison.Ordinal));

        Assert.False(SteamRuntime.BindingsReady);
        Assert.Equal(new[] { "SteamAPI_SteamUtils_v010", "SteamAPI_SteamUtils_v011" }, SteamRuntime.MissingExports);
        Assert.Contains("one of SteamAPI_SteamUtils_v010/SteamAPI_SteamUtils_v011", SteamRuntime.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void With_every_optional_export_missing_their_calls_fail_without_touching_native_code()
    {
        SteamRuntime.Reset(name => !SteamRuntime.OptionalExports.Contains(name));
        Assert.True(SteamRuntime.BindingsReady);
        Assert.Equal(SteamRuntime.OptionalExports, SteamRuntime.MissingExports);

        // Each of these needs an optional export. Without steam_api64 any native call would fail and turn the bindings
        // off, so still being ready afterwards proves every one returned before calling Steam.
        Assert.Equal(SteamSessionClose.Unavailable, SteamMessages.CloseSession(Peer, out string closeFailure));
        Assert.Empty(closeFailure);
        Assert.False(SteamMessages.CloseChannel(Peer, 0));
        Assert.Equal(SteamPing.Failed, SteamPing.Estimate("a=1", "b=2"));
        Assert.Equal(SteamPing.Failed, SteamPing.EstimateFromLocalHost("a=1"));
        Assert.False(SteamPing.CheckPingDataUpToDate(60));
        Assert.False(SteamNetworking.SetConfig(SteamNetworkingConfigValue.TimeoutConnected, SteamNetworkingConfigScope.Global, 0, 10000));
        Assert.False(SteamNetworking.SetConfig(SteamNetworkingConfigValue.FakePacketLossRecv, SteamNetworkingConfigScope.Global, 0, 1f));
        Assert.False(SteamLobbies.SetLobbyMemberData(1, "ping", "x"));
        Assert.Equal(string.Empty, SteamLobbies.GetLobbyMemberData(1, Peer, "ping"));
        Assert.False(SteamLobbies.SetJoinable(1, true));
        Assert.False(SteamLobbies.SetLobbyType(1, SteamLobbyType.Private));
        Assert.False(SteamLobbies.RequestLobbyData(1));
        Assert.Equal(0, SteamLobbies.GetLobbyDataCount(1));
        Assert.False(SteamFriends.SetRichPresence("connect", "x"));
        Assert.False(SteamFriends.ClearRichPresence());
        Assert.False(SteamFriends.TryIsFriend(Peer, out bool isFriend));
        Assert.False(isFriend);
        Assert.False(SteamFriends.IsFriend(Peer));
        Assert.Null(SteamAvatars.RequestUserInformation(Peer));
        Assert.Equal(SteamAvatars.Unavailable, SteamAvatars.GetHandle(Peer, SteamAvatarSize.Small));
        Assert.Equal(SteamAvatars.Unavailable, SteamAvatars.GetHandle(Peer, SteamAvatarSize.Medium));
        Assert.Equal(SteamAvatars.Unavailable, SteamAvatars.GetHandle(Peer, SteamAvatarSize.Large));
        Assert.False(SteamAvatars.TryGetSize(5, out _, out _));
        byte[]? pixels = null;
        Assert.False(SteamAvatars.TryRead(5, ref pixels, out _, out _));
        Assert.Equal(SteamPersonaState.Unknown, SteamFriends.GetFriendPersonaState(Peer));
        Assert.False(SteamLobbies.InviteUser(109775240917663232UL, Peer));
        Assert.Equal(SteamOverlayResult.Unavailable, SteamOverlay.OpenProfile(Peer));

        Assert.True(SteamRuntime.BindingsReady);
        Assert.DoesNotContain("native call failed", SteamRuntime.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_forgets_a_native_failure()
    {
        SteamRuntime.Reset(_ => true);
        SteamRuntime.ReportFailure(new InvalidOperationException("boom"));
        Assert.False(SteamRuntime.BindingsReady);
        Assert.Contains("boom", SteamRuntime.Status, StringComparison.Ordinal);

        SteamRuntime.Reset(_ => true);
        Assert.True(SteamRuntime.BindingsReady);
    }
}
