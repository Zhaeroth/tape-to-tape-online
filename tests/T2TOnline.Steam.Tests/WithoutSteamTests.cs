using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

/// <summary>
/// The test host has no initialised Steam (and normally no steam_api64 at all): every call must return its failure
/// value and nothing may throw.
/// </summary>
[Collection(SharedSteamRuntime.Name)]
public class WithoutSteamTests
{
    private const ulong Peer = 76561199000000001UL;

    [Fact]
    public void Steam_is_reported_unavailable_with_a_reason()
    {
        Assert.False(SteamRuntime.IsAvailable);
        Assert.Equal(0UL, SteamRuntime.LocalSteamId);
        Assert.Equal(0u, SteamRuntime.AppId);
        Assert.False(string.IsNullOrWhiteSpace(SteamRuntime.Status));
        Assert.NotNull(SteamRuntime.MissingExports);
    }

    [Fact]
    public void The_identity_self_check_runs_only_when_the_dll_is_there()
    {
        bool ok = SteamRuntime.VerifyIdentityLayout(out string detail);

        // The identity helpers need no Steam login, so if a steam_api64 is loadable here the check really runs.
        Assert.Equal(SteamRuntime.BindingsReady, ok);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void Messaging_calls_fail_cleanly()
    {
        Assert.Equal(SteamResult.None, SteamMessages.Send(Peer, 0, new byte[] { 1, 2, 3 }, SteamSendType.UnreliableNoDelay));
        Assert.Equal(SteamResult.None, SteamMessages.Send(Peer, 1, ReadOnlySpan<byte>.Empty, SteamSendType.Reliable));
        Assert.False(SteamMessages.AcceptSession(Peer));
        Assert.Equal(SteamSessionClose.Unavailable, SteamMessages.CloseSession(Peer, out string closeFailure));
        Assert.Empty(closeFailure);
        Assert.Equal(SteamSessionClose.NotAllowed, SteamMessages.CloseSession(0, out _));
        Assert.False(SteamMessages.CloseChannel(Peer, 0));
        Assert.Equal(SteamNetworkingConnectionState.None, SteamMessages.GetSessionInfo(Peer, out SteamNetConnectionInfo info, out SteamNetConnectionRealTimeStatus status));
        Assert.Equal(SteamNetworkingConnectionState.None, info.State);
        Assert.Equal(0, status.Ping);
        Assert.Equal(0, new SteamMessageReceiver().Drain(0, (_, _, _) => throw new InvalidOperationException("no messages expected")));
    }

    [Fact]
    public void Bad_send_arguments_are_rejected_before_steam()
    {
        Assert.Equal(SteamResult.InvalidParam, SteamMessages.Send(0, 0, new byte[] { 1 }, SteamSendType.Reliable));
        Assert.Equal(SteamResult.InvalidParam, SteamMessages.Send(Peer, -1, new byte[] { 1 }, SteamSendType.Reliable));
        Assert.Equal(SteamResult.InvalidParam, SteamMessages.Send(Peer, 0, new byte[SteamMessages.MaxSendSize + 1], SteamSendType.Reliable));
    }

    [Fact]
    public void Networking_utils_fail_cleanly()
    {
        Assert.False(SteamNetworking.InitRelayNetworkAccess());
        Assert.Equal(SteamNetworkingAvailability.Unknown, SteamNetworking.GetRelayNetworkStatus(out SteamRelayNetworkStatus relay));
        Assert.Equal(string.Empty, relay.DebugMessage);
        Assert.Equal(0, SteamNetworking.GetLocalTimestamp());
        Assert.False(SteamNetworking.EnableIce());
        Assert.False(SteamNetworking.SetIce(SteamIceCandidates.Disable));
        Assert.False(SteamNetworking.TryGetIce(out int ice));
        Assert.Equal(0, ice);
        Assert.False(SteamNetworking.SetGlobalConfig(SteamNetworkingConfigValue.FakePacketLagSend, 50));
        Assert.False(SteamNetworking.SetGlobalConfig(SteamNetworkingConfigValue.FakePacketLossSend, 5f));
        Assert.False(SteamNetworking.SetConfig(SteamNetworkingConfigValue.TimeoutConnected, SteamNetworkingConfigScope.Global, 0, 10000));
        Assert.False(SteamNetworking.SetConfig(SteamNetworkingConfigValue.FakePacketLossRecv, SteamNetworkingConfigScope.Global, 0, 1f));
        Assert.False(SteamNetworking.TryGetGlobalConfig(SteamNetworkingConfigValue.P2PTransportIceEnable, out int _));
        Assert.False(SteamNetworking.TryGetGlobalConfig(SteamNetworkingConfigValue.FakePacketLossSend, out float _));
        Assert.Equal(16, SteamNetworking.ApplyFakeConditions(new SteamFakeNetworkConditions { LagSendMs = 100 }));
    }

    [Fact]
    public void Ping_calls_fail_cleanly()
    {
        Assert.False(SteamPing.TryGetLocalLocation(out string location, out float age));
        Assert.Equal(string.Empty, location);
        Assert.True(age < 0);
        Assert.Equal(SteamPing.Failed, SteamPing.Estimate("a=1", "b=2"));
        Assert.Equal(SteamPing.Failed, SteamPing.EstimateFromLocalHost("a=1"));
        Assert.Equal(SteamPing.Failed, SteamPing.Estimate(null!, string.Empty));
        Assert.False(SteamPing.CheckPingDataUpToDate(60));
    }

    [Fact]
    public void Lobby_calls_fail_cleanly()
    {
        SteamCallResult<LobbyCreated> create = SteamLobbies.CreateLobby(SteamLobbyType.FriendsOnly, 4);
        Assert.Equal(SteamCallResultState.Failed, create.Poll());
        Assert.Equal(SteamApiCallFailure.SteamGone, create.FailureReason);
        Assert.Equal(SteamCallResultFailure.SteamUnavailable, create.Failure);
        Assert.False(create.ShouldTryOtherPath);

        SteamCallResult<LobbyEnter> join = SteamLobbies.JoinLobby(109775240917663232UL, SteamCallResultPath.ManualDispatch);
        Assert.Equal(SteamCallResultState.Failed, join.Poll());
        Assert.Equal(SteamCallResultPath.ManualDispatch, join.Path);
        Assert.False(join.ShouldTryOtherPath);

        Assert.False(SteamLobbies.LeaveLobby(1));
        Assert.False(SteamLobbies.SetLobbyData(1, "mod", "0.1.5"));
        Assert.False(SteamLobbies.SetLobbyData(1, "bad\0key", "x"));
        Assert.Equal(string.Empty, SteamLobbies.GetLobbyData(1, "mod"));
        Assert.False(SteamLobbies.SetLobbyMemberData(1, "ping", "x"));
        Assert.Equal(string.Empty, SteamLobbies.GetLobbyMemberData(1, Peer, "ping"));
        Assert.Equal(0, SteamLobbies.GetMemberCount(1));
        Assert.Equal(0UL, SteamLobbies.GetMemberByIndex(1, 0));
        Assert.Empty(SteamLobbies.GetMembers(1));
        Assert.Equal(0UL, SteamLobbies.GetOwner(1));
        Assert.False(SteamLobbies.SetJoinable(1, true));
        Assert.False(SteamLobbies.SetLobbyType(1, SteamLobbyType.Private));
        Assert.False(SteamLobbies.RequestLobbyData(1));
        Assert.Equal(0, SteamLobbies.GetLobbyDataCount(1));
    }

    [Fact]
    public void Friends_calls_fail_cleanly()
    {
        Assert.Equal(string.Empty, SteamFriends.GetPersonaName());
        Assert.Equal(string.Empty, SteamFriends.GetFriendPersonaName(Peer));
        Assert.Equal(0, SteamFriends.GetFriendCount(SteamFriendFilter.Immediate));
        Assert.Equal(0UL, SteamFriends.GetFriendByIndex(0, SteamFriendFilter.Immediate));
        Assert.False(SteamFriends.TryGetFriendGamePlayed(Peer, out FriendGameInfo game));
        Assert.Equal(0UL, game.LobbyId);
        Assert.False(SteamFriends.SetRichPresence("connect", "+connect_lobby 1"));
        Assert.False(SteamFriends.ClearRichPresence());
        Assert.False(SteamFriends.IsFriend(Peer));
        Assert.False(SteamFriends.TryIsFriend(Peer, out bool isFriend));
        Assert.False(isFriend);
        Assert.False(SteamFriends.CanCheckFriends);
        Assert.StartsWith("Steam's calls are off: ", SteamFriends.FriendCheckProblem(Peer), StringComparison.Ordinal);
        Assert.Empty(SteamFriendsLobbies.FindFriendLobbies(1566200));
    }

    [Fact]
    public void Overlay_calls_fail_cleanly()
    {
        Assert.Null(SteamOverlay.IsEnabled);
        Assert.Equal(SteamOverlayResult.Unavailable, SteamOverlay.OpenInviteDialog(109775240000000001UL));
        Assert.Equal(SteamOverlayResult.Unavailable, SteamOverlay.OpenInviteDialog(0));

        // Rich presence through Steam: every set refused (and counted), nothing thrown.
        var presence = new SteamPresence();
        Assert.True(presence.Show(new SteamPresenceState(109775240000000001UL, true, 1, 4)));
        Assert.Equal(4, presence.Refused);
        Assert.True(presence.Show(null));
    }

    [Fact]
    public void Avatar_calls_answer_as_if_steam_were_not_there()
    {
        Assert.Null(SteamAvatars.RequestUserInformation(Peer));
        Assert.Null(SteamAvatars.RequestUserInformation(0));
        foreach (SteamAvatarSize size in new[] { SteamAvatarSize.Small, SteamAvatarSize.Medium, SteamAvatarSize.Large })
        {
            Assert.Equal(SteamAvatars.Unavailable, SteamAvatars.GetHandle(Peer, size));
            Assert.Equal(SteamAvatars.Unavailable, SteamAvatars.GetHandle(0, size));
        }

        Assert.Equal(SteamAvatars.Unavailable, SteamAvatars.GetHandle(Peer, (SteamAvatarSize)99));
        Assert.False(SteamAvatars.TryGetSize(5, out int width, out int height));
        Assert.Equal(0, width + height);

        byte[]? buffer = null;
        Assert.False(SteamAvatars.TryRead(5, ref buffer, out width, out height));
        Assert.Null(buffer);
        Assert.Equal(0, width + height);
        Assert.False(SteamAvatars.TryRead(-1, ref buffer, out _, out _));

        // The poll on the real API: Steam can't be asked, so the avatar is given up on at once and not asked about again.
        var poll = new SteamAvatarPoll();
        var key = new SteamAvatarKey(Peer, SteamAvatarSize.Large);
        Assert.Equal(SteamAvatarPhase.Waiting, poll.Want(key, TimeSpan.Zero));
        Assert.Equal(1, poll.Poll(TimeSpan.Zero));
        Assert.Equal(SteamAvatarPhase.None, poll.PhaseOf(key));
        Assert.Equal(0, poll.Poll(TimeSpan.FromSeconds(5)));
        Assert.False(poll.TryNextFetch(out _, out _));
    }

    [Fact]
    public void People_and_invite_calls_answer_as_if_steam_were_not_there()
    {
        Assert.Equal(SteamPersonaState.Unknown, SteamFriends.GetFriendPersonaState(Peer));
        Assert.Equal(SteamPersonaState.Unknown, SteamFriends.GetFriendPersonaState(0));
        Assert.Empty(SteamFriendsPicker.Read(1566200));
        Assert.Empty(SteamFriendsPicker.Read(1566200, 5));
        Assert.False(SteamLobbies.InviteUser(109775240917663232UL, Peer));
        Assert.False(SteamLobbies.InviteUser(0, Peer));
        Assert.False(SteamLobbies.InviteUser(109775240917663232UL, 0));
        Assert.Equal(SteamOverlayResult.Unavailable, SteamOverlay.OpenProfile(Peer));
        Assert.Equal(SteamOverlayResult.Unavailable, SteamOverlay.OpenProfile(0));
        Assert.False(SteamOverlay.IsOpen);
    }

    [Fact]
    public void The_browser_and_the_advertiser_on_the_real_api_find_and_publish_nothing_and_do_not_throw()
    {
        var lines = new System.Collections.Generic.List<string>();
        var browser = new SteamLobbyBrowser(lines.Add, lines.Add);

        browser.Refresh(TimeSpan.Zero);
        for (int i = 0; i < 100; i++)
        {
            browser.Poll(TimeSpan.FromMilliseconds(i * 100), open: i % 2 == 0);
        }

        Assert.Empty(browser.Listings);
        browser.Clear();

        var warnings = new System.Collections.Generic.List<string>();
        var advertiser = new SteamLobbyAdvertiser(warnings.Add, warnings.Add);
        Assert.Equal(0, advertiser.Publish(109775240917663232UL, LobbyAdvert.Default, TimeSpan.Zero));
        Assert.NotEmpty(warnings); // Steam refused: said once per key
        Assert.Equal(0, advertiser.Publish(109775240917663232UL, LobbyAdvert.Default, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Call_results_can_be_built_for_any_handle()
    {
        var call = new SteamCallResult<LobbyCreated>(42, LobbyCreated.CallbackId);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamApiCallFailure.SteamGone, call.FailureReason);
        Assert.Equal(SteamCallResultFailure.SteamUnavailable, call.Failure);
    }

    [Fact]
    public void The_session_watch_sends_nothing_and_sees_nothing()
    {
        var watch = new SteamSessionWatch();
        watch.Watch(Peer);

        Assert.Equal(SteamResult.None, watch.Send(Peer, 1, new byte[] { 1 }, SteamSendType.Reliable));
        watch.Poll();
        Assert.False(watch.TryTakeChange(out _));
        Assert.Equal(0, watch.Restarts);
    }

    [Fact]
    public void The_lobby_link_fails_its_calls_and_leaves_nothing_behind()
    {
        var lines = new System.Collections.Generic.List<string>();
        var link = new SteamLobbyLink(lines.Add, lines.Add);

        link.Create(4, TimeSpan.Zero);
        Assert.Equal(SteamLobbyCall.Failed, link.Poll(TimeSpan.Zero));
        Assert.False(link.Active);
        Assert.False(string.IsNullOrWhiteSpace(link.Problem));

        link.Join(Peer, "host", TimeSpan.Zero);
        Assert.Equal(SteamLobbyCall.Failed, link.Poll(TimeSpan.Zero));
        link.Leave("test");
        link.PollAbandoned();
        Assert.Equal(0UL, link.Id);
    }
}
