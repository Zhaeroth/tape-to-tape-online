using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests.Native;

/// <summary>Enum and constant values against the Steamworks SDK headers (printed by MSVC from the headers).</summary>
public class SdkValueTests
{
    [Theory]
    [InlineData(SteamNetworkingConfigValue.Invalid, 0)]
    [InlineData(SteamNetworkingConfigValue.FakePacketLossSend, 2)]
    [InlineData(SteamNetworkingConfigValue.FakePacketLossRecv, 3)]
    [InlineData(SteamNetworkingConfigValue.FakePacketLagSend, 4)]
    [InlineData(SteamNetworkingConfigValue.FakePacketLagRecv, 5)]
    [InlineData(SteamNetworkingConfigValue.FakePacketReorderSend, 6)]
    [InlineData(SteamNetworkingConfigValue.FakePacketReorderRecv, 7)]
    [InlineData(SteamNetworkingConfigValue.FakePacketReorderTime, 8)]
    [InlineData(SteamNetworkingConfigValue.SendBufferSize, 9)]
    [InlineData(SteamNetworkingConfigValue.SendRateMin, 10)]
    [InlineData(SteamNetworkingConfigValue.SendRateMax, 11)]
    [InlineData(SteamNetworkingConfigValue.NagleTime, 12)]
    [InlineData(SteamNetworkingConfigValue.LogLevelP2PRendezvous, 17)]
    [InlineData(SteamNetworkingConfigValue.TimeoutInitial, 24)]
    [InlineData(SteamNetworkingConfigValue.TimeoutConnected, 25)]
    [InlineData(SteamNetworkingConfigValue.FakePacketDupSend, 26)]
    [InlineData(SteamNetworkingConfigValue.FakePacketDupRecv, 27)]
    [InlineData(SteamNetworkingConfigValue.FakePacketDupTimeMax, 28)]
    [InlineData(SteamNetworkingConfigValue.MtuPacketSize, 32)]
    [InlineData(SteamNetworkingConfigValue.FakePacketJitterSendAvg, 53)]
    [InlineData(SteamNetworkingConfigValue.FakePacketJitterSendMax, 54)]
    [InlineData(SteamNetworkingConfigValue.FakePacketJitterSendPct, 55)]
    [InlineData(SteamNetworkingConfigValue.FakePacketJitterRecvAvg, 56)]
    [InlineData(SteamNetworkingConfigValue.FakePacketJitterRecvMax, 57)]
    [InlineData(SteamNetworkingConfigValue.FakePacketJitterRecvPct, 58)]
    [InlineData(SteamNetworkingConfigValue.P2PStunServerList, 103)]
    [InlineData(SteamNetworkingConfigValue.P2PTransportIceEnable, 104)]
    [InlineData(SteamNetworkingConfigValue.P2PTransportIcePenalty, 105)]
    [InlineData(SteamNetworkingConfigValue.P2PTransportSdrPenalty, 106)]
    public void Config_values_have_their_sdk_numbers(SteamNetworkingConfigValue value, int expected) =>
        Assert.Equal(expected, (int)value);

    [Fact]
    public void Config_scope_and_data_types_have_their_sdk_numbers()
    {
        Assert.Equal(1, (int)SteamNetworkingConfigScope.Global);
        Assert.Equal(2, (int)SteamNetworkingConfigScope.SocketsInterface);
        Assert.Equal(3, (int)SteamNetworkingConfigScope.ListenSocket);
        Assert.Equal(4, (int)SteamNetworkingConfigScope.Connection);
        Assert.Equal(1, (int)SteamNetworkingConfigDataType.Int32);
        Assert.Equal(2, (int)SteamNetworkingConfigDataType.Int64);
        Assert.Equal(3, (int)SteamNetworkingConfigDataType.Float);
        Assert.Equal(4, (int)SteamNetworkingConfigDataType.String);
        Assert.Equal(5, (int)SteamNetworkingConfigDataType.Ptr);
        Assert.Equal(-1, (int)SteamNetworkingGetConfigValueResult.BadValue);
        Assert.Equal(-2, (int)SteamNetworkingGetConfigValueResult.BadScopeObject);
        Assert.Equal(-3, (int)SteamNetworkingGetConfigValueResult.BufferTooSmall);
        Assert.Equal(1, (int)SteamNetworkingGetConfigValueResult.OK);
        Assert.Equal(2, (int)SteamNetworkingGetConfigValueResult.OKInherited);
    }

    [Fact]
    public void Ice_candidate_values_match_the_sdk()
    {
        Assert.Equal(-1, SteamIceCandidates.Default);
        Assert.Equal(0, SteamIceCandidates.Disable);
        Assert.Equal(1, SteamIceCandidates.Relay);
        Assert.Equal(2, SteamIceCandidates.Private);
        Assert.Equal(4, SteamIceCandidates.Public);
        Assert.Equal(0x7fffffff, SteamIceCandidates.All);
    }

    [Theory]
    [InlineData(SteamSendType.Unreliable, 0)]
    [InlineData(SteamSendType.NoNagle, 1)]
    [InlineData(SteamSendType.NoDelay, 4)]
    [InlineData(SteamSendType.UnreliableNoDelay, 5)]
    [InlineData(SteamSendType.Reliable, 8)]
    [InlineData(SteamSendType.ReliableNoNagle, 9)]
    [InlineData(SteamSendType.UseCurrentThread, 16)]
    [InlineData(SteamSendType.AutoRestartBrokenSession, 32)]
    public void Send_types_have_their_sdk_numbers(SteamSendType value, int expected) =>
        Assert.Equal(expected, (int)value);

    [Fact]
    public void Max_send_size_is_the_sdk_limit() => Assert.Equal(524288, SteamMessages.MaxSendSize);

    [Fact]
    public void Connection_states_match_the_sdk()
    {
        Assert.Equal(0, (int)SteamNetworkingConnectionState.None);
        Assert.Equal(1, (int)SteamNetworkingConnectionState.Connecting);
        Assert.Equal(2, (int)SteamNetworkingConnectionState.FindingRoute);
        Assert.Equal(3, (int)SteamNetworkingConnectionState.Connected);
        Assert.Equal(4, (int)SteamNetworkingConnectionState.ClosedByPeer);
        Assert.Equal(5, (int)SteamNetworkingConnectionState.ProblemDetectedLocally);
        Assert.Equal(-1, (int)SteamNetworkingConnectionState.FinWait);
        Assert.Equal(-2, (int)SteamNetworkingConnectionState.Linger);
        Assert.Equal(-3, (int)SteamNetworkingConnectionState.Dead);
    }

    [Fact]
    public void Availability_values_match_the_sdk()
    {
        Assert.Equal(-102, (int)SteamNetworkingAvailability.CannotTry);
        Assert.Equal(-101, (int)SteamNetworkingAvailability.Failed);
        Assert.Equal(-100, (int)SteamNetworkingAvailability.Previously);
        Assert.Equal(-10, (int)SteamNetworkingAvailability.Retrying);
        Assert.Equal(0, (int)SteamNetworkingAvailability.Unknown);
        Assert.Equal(1, (int)SteamNetworkingAvailability.NeverTried);
        Assert.Equal(2, (int)SteamNetworkingAvailability.Waiting);
        Assert.Equal(3, (int)SteamNetworkingAvailability.Attempting);
        Assert.Equal(100, (int)SteamNetworkingAvailability.Current);
    }

    [Fact]
    public void Identity_types_match_the_sdk()
    {
        Assert.Equal(0, (int)SteamNetworkingIdentityType.Invalid);
        Assert.Equal(1, (int)SteamNetworkingIdentityType.IPAddress);
        Assert.Equal(2, (int)SteamNetworkingIdentityType.GenericString);
        Assert.Equal(3, (int)SteamNetworkingIdentityType.GenericBytes);
        Assert.Equal(4, (int)SteamNetworkingIdentityType.UnknownType);
        Assert.Equal(16, (int)SteamNetworkingIdentityType.SteamId);
        Assert.Equal(17, (int)SteamNetworkingIdentityType.XboxPairwiseId);
        Assert.Equal(18, (int)SteamNetworkingIdentityType.SonyPsn);
    }

    [Fact]
    public void Results_match_the_sdk()
    {
        Assert.Equal(0, (int)SteamResult.None);
        Assert.Equal(1, (int)SteamResult.OK);
        Assert.Equal(2, (int)SteamResult.Fail);
        Assert.Equal(3, (int)SteamResult.NoConnection);
        Assert.Equal(8, (int)SteamResult.InvalidParam);
        Assert.Equal(11, (int)SteamResult.InvalidState);
        Assert.Equal(15, (int)SteamResult.AccessDenied);
        Assert.Equal(16, (int)SteamResult.Timeout);
        Assert.Equal(25, (int)SteamResult.LimitExceeded);
        Assert.Equal(35, (int)SteamResult.ConnectFailed);
        Assert.Equal(41, (int)SteamResult.Ignored);
        Assert.Equal(84, (int)SteamResult.RateLimitExceeded);
    }

    [Fact]
    public void Lobby_and_call_values_match_the_sdk()
    {
        Assert.Equal(0, (int)SteamLobbyType.Private);
        Assert.Equal(1, (int)SteamLobbyType.FriendsOnly);
        Assert.Equal(2, (int)SteamLobbyType.Public);
        Assert.Equal(3, (int)SteamLobbyType.Invisible);
        Assert.Equal(4, (int)SteamLobbyType.PrivateUnique);
        Assert.Equal(-1, (int)SteamApiCallFailure.None);
        Assert.Equal(0, (int)SteamApiCallFailure.SteamGone);
        Assert.Equal(1, (int)SteamApiCallFailure.NetworkFailure);
        Assert.Equal(2, (int)SteamApiCallFailure.InvalidHandle);
        Assert.Equal(3, (int)SteamApiCallFailure.MismatchedCallback);
        Assert.Equal(1, (int)SteamChatRoomEnterResponse.Success);
        Assert.Equal(2, (int)SteamChatRoomEnterResponse.DoesntExist);
        Assert.Equal(3, (int)SteamChatRoomEnterResponse.NotAllowed);
        Assert.Equal(4, (int)SteamChatRoomEnterResponse.Full);
        Assert.Equal(5, (int)SteamChatRoomEnterResponse.Error);
        Assert.Equal(6, (int)SteamChatRoomEnterResponse.Banned);
        Assert.Equal(7, (int)SteamChatRoomEnterResponse.Limited);
        Assert.Equal(8, (int)SteamChatRoomEnterResponse.ClanDisabled);
        Assert.Equal(9, (int)SteamChatRoomEnterResponse.CommunityBan);
        Assert.Equal(10, (int)SteamChatRoomEnterResponse.MemberBlockedYou);
        Assert.Equal(11, (int)SteamChatRoomEnterResponse.YouBlockedMember);
        Assert.Equal(15, (int)SteamChatRoomEnterResponse.RateLimitExceeded);
    }

    [Fact]
    public void Friend_filters_and_connection_traits_match_the_sdk()
    {
        Assert.Equal(0x00, (int)SteamFriendFilter.None);
        Assert.Equal(0x01, (int)SteamFriendFilter.Blocked);
        Assert.Equal(0x02, (int)SteamFriendFilter.FriendshipRequested);
        Assert.Equal(0x04, (int)SteamFriendFilter.Immediate);
        Assert.Equal(0x08, (int)SteamFriendFilter.ClanMember);
        Assert.Equal(0x10, (int)SteamFriendFilter.OnGameServer);
        Assert.Equal(0x80, (int)SteamFriendFilter.RequestingFriendship);
        Assert.Equal(0x100, (int)SteamFriendFilter.RequestingInfo);
        Assert.Equal(0x200, (int)SteamFriendFilter.Ignored);
        Assert.Equal(0x400, (int)SteamFriendFilter.IgnoredFriend);
        Assert.Equal(0x1000, (int)SteamFriendFilter.ChatMember);
        Assert.Equal(0xFFFF, (int)SteamFriendFilter.All);
        Assert.Equal(1, (int)SteamConnectionTraits.Unauthenticated);
        Assert.Equal(2, (int)SteamConnectionTraits.Unencrypted);
        Assert.Equal(4, (int)SteamConnectionTraits.LoopbackBuffers);
        Assert.Equal(8, (int)SteamConnectionTraits.Fast);
        Assert.Equal(16, (int)SteamConnectionTraits.Relayed);
        Assert.Equal(32, (int)SteamConnectionTraits.DualWifi);
    }

    [Fact]
    public void Persona_states_match_the_sdk()
    {
        Assert.Equal(0, (int)SteamPersonaState.Offline);
        Assert.Equal(1, (int)SteamPersonaState.Online);
        Assert.Equal(2, (int)SteamPersonaState.Busy);
        Assert.Equal(3, (int)SteamPersonaState.Away);
        Assert.Equal(4, (int)SteamPersonaState.Snooze);
        Assert.Equal(5, (int)SteamPersonaState.LookingToTrade);
        Assert.Equal(6, (int)SteamPersonaState.LookingToPlay);
        Assert.Equal(-1, (int)SteamPersonaState.Unknown); // not Steam's: this library's "couldn't ask"
        Assert.Equal(typeof(int), System.Enum.GetUnderlyingType(typeof(SteamPersonaState)));
    }

    [Fact]
    public void Avatar_return_codes_and_limits_match_the_sdk()
    {
        Assert.Equal(0, SteamAvatars.NoAvatar);
        Assert.Equal(-1, SteamAvatars.Loading);
        Assert.True(SteamAvatars.Unavailable < SteamAvatars.Loading);
        Assert.Equal(4, SteamAvatars.BytesPerPixel);
    }

    [Fact]
    public void Ping_sentinels_match_the_sdk()
    {
        Assert.Equal(-1, SteamPing.Failed);
        Assert.Equal(-2, SteamPing.Unknown);
        Assert.Equal(1024, SteamPing.MaxLocationLength);
    }
}
