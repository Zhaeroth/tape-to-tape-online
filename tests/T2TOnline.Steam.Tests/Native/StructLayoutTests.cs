using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests.Native;

/// <summary>
/// Every interop struct against the Windows x64 layout of the Steamworks SDK headers. The expected numbers were
/// printed by MSVC x64 (sizeof/offsetof) compiling the SDK headers, not derived from these types.
/// </summary>
public class StructLayoutTests
{
    private static void AssertLayout<T>(int size, params (string Field, int Offset)[] fields)
        where T : unmanaged
    {
        Assert.Equal(size, Marshal.SizeOf<T>());
        // Blittable: the managed layout is the native one, so pointers into these structs are safe.
        Assert.Equal(size, Unsafe.SizeOf<T>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        foreach ((string field, int offset) in fields)
        {
            Assert.True(offset == (int)Marshal.OffsetOf<T>(field), $"{typeof(T).Name}.{field}: expected {offset}, got {Marshal.OffsetOf<T>(field)}");
        }
    }

    [Fact]
    public void SteamNetworkingIdentity_is_136_bytes_with_the_steam_id_at_8() =>
        AssertLayout<SteamNetworkingIdentity>(136, ("_type", 0), ("_byteCount", 4), ("_steamId64", 8));

    [Fact]
    public void SteamNetworkingIdentity_offset_constants_match_the_struct()
    {
        Assert.Equal(SteamNetworkingIdentity.SizeInBytes, Marshal.SizeOf<SteamNetworkingIdentity>());
        Assert.Equal(SteamNetworkingIdentity.TypeOffset, (int)Marshal.OffsetOf<SteamNetworkingIdentity>("_type"));
        Assert.Equal(SteamNetworkingIdentity.ByteCountOffset, (int)Marshal.OffsetOf<SteamNetworkingIdentity>("_byteCount"));
        Assert.Equal(SteamNetworkingIdentity.SteamId64Offset, (int)Marshal.OffsetOf<SteamNetworkingIdentity>("_steamId64"));
    }

    [Fact]
    public void SteamNetworkingMessage_matches_the_sdk() =>
        AssertLayout<SteamNetworkingMessage>(
            216,
            ("_data", 0),
            ("_size", 8),
            ("_connection", 12),
            ("_identityPeer", 16),
            ("_connectionUserData", 152),
            ("_timeReceived", 160),
            ("_messageNumber", 168),
            ("_freeData", 176),
            ("_release", 184),
            ("_channel", 192),
            ("_flags", 196),
            ("_userData", 200),
            ("_lane", 208),
            ("_padding", 210));

    [Fact]
    public void SteamNetworkingMessage_offset_constants_match_the_struct()
    {
        Assert.Equal(SteamNetworkingMessage.SizeInBytes, Marshal.SizeOf<SteamNetworkingMessage>());
        Assert.Equal(SteamNetworkingMessage.DataOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_data"));
        Assert.Equal(SteamNetworkingMessage.SizeOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_size"));
        Assert.Equal(SteamNetworkingMessage.IdentityPeerOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_identityPeer"));
        Assert.Equal(SteamNetworkingMessage.TimeReceivedOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_timeReceived"));
        Assert.Equal(SteamNetworkingMessage.MessageNumberOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_messageNumber"));
        Assert.Equal(SteamNetworkingMessage.ChannelOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_channel"));
        Assert.Equal(SteamNetworkingMessage.FlagsOffset, (int)Marshal.OffsetOf<SteamNetworkingMessage>("_flags"));
    }

    [Fact]
    public void SteamNetConnectionInfo_matches_the_sdk()
    {
        AssertLayout<SteamNetConnectionInfo>(
            696,
            ("_remoteIdentity", 0),
            ("_userData", 136),
            ("_listenSocket", 144),
            ("_popRemote", 168),
            ("_popRelay", 172),
            ("_state", 176),
            ("_endReason", 180),
            ("_flags", 440));
        Assert.Equal(184, SteamNetConnectionInfo.EndDebugOffset);
        Assert.Equal(312, SteamNetConnectionInfo.ConnectionDescriptionOffset);
        Assert.Equal(128, SteamNetConnectionInfo.StringLength);
        Assert.Equal(SteamNetConnectionInfo.SizeInBytes, Marshal.SizeOf<SteamNetConnectionInfo>());
    }

    [Fact]
    public void SteamNetConnectionRealTimeStatus_matches_the_sdk()
    {
        AssertLayout<SteamNetConnectionRealTimeStatus>(
            120,
            ("_state", 0),
            ("_ping", 4),
            ("_qualityLocal", 8),
            ("_qualityRemote", 12),
            ("_outPacketsPerSecond", 16),
            ("_outBytesPerSecond", 20),
            ("_inPacketsPerSecond", 24),
            ("_inBytesPerSecond", 28),
            ("_sendRateBytesPerSecond", 32),
            ("_pendingUnreliable", 36),
            ("_pendingReliable", 40),
            ("_sentUnackedReliable", 44),
            ("_queueTimeMicroseconds", 48),
            ("_maxJitterMicroseconds", 56));
        Assert.Equal(SteamNetConnectionRealTimeStatus.SizeInBytes, Marshal.SizeOf<SteamNetConnectionRealTimeStatus>());
    }

    [Fact]
    public void SteamRelayNetworkStatus_matches_the_sdk()
    {
        AssertLayout<SteamRelayNetworkStatus>(
            272,
            ("_availability", 0),
            ("_pingMeasurementInProgress", 4),
            ("_networkConfigAvailability", 8),
            ("_anyRelayAvailability", 12));
        Assert.Equal(16, SteamRelayNetworkStatus.DebugMessageOffset);
        Assert.Equal(256, SteamRelayNetworkStatus.DebugMessageLength);
    }

    [Fact]
    public void SteamNetworkPingLocation_is_512_opaque_bytes()
    {
        AssertLayout<SteamNetworkPingLocation>(512);
        Assert.Equal(SteamNetworkPingLocation.SizeInBytes, Marshal.SizeOf<SteamNetworkPingLocation>());
    }

    [Fact]
    public void LobbyCreated_matches_the_sdk()
    {
        AssertLayout<LobbyCreated>(16, ("_result", 0), ("_lobbyId", 8));
        Assert.Equal(500 + 13, LobbyCreated.CallbackId);
        Assert.Equal(LobbyCreated.SizeInBytes, Marshal.SizeOf<LobbyCreated>());
    }

    [Fact]
    public void LobbyEnter_matches_the_sdk()
    {
        AssertLayout<LobbyEnter>(24, ("_lobbyId", 0), ("_chatPermissions", 8), ("_locked", 12), ("_enterResponse", 16));
        Assert.Equal(500 + 4, LobbyEnter.CallbackId);
        Assert.Equal(LobbyEnter.SizeInBytes, Marshal.SizeOf<LobbyEnter>());
    }

    [Fact]
    public void FriendGameInfo_matches_the_sdk()
    {
        AssertLayout<FriendGameInfo>(24, ("_gameId", 0), ("_gameIp", 8), ("_gamePort", 12), ("_queryPort", 14), ("_lobbyId", 16));
        Assert.Equal(FriendGameInfo.SizeInBytes, Marshal.SizeOf<FriendGameInfo>());
    }

    [Fact]
    public void Lobby_results_read_back_from_raw_sdk_bytes()
    {
        var created = default(LobbyCreated);
        Span<byte> createdBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref created, 1));
        BitConverter.TryWriteBytes(createdBytes[0..], 1);
        BitConverter.TryWriteBytes(createdBytes[8..], 109775240917663232UL);
        Assert.Equal(SteamResult.OK, created.Result);
        Assert.Equal(109775240917663232UL, created.LobbyId);

        var entered = default(LobbyEnter);
        Span<byte> enteredBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref entered, 1));
        BitConverter.TryWriteBytes(enteredBytes[0..], 109775240917663232UL);
        BitConverter.TryWriteBytes(enteredBytes[8..], 0x1Fu);
        enteredBytes[12] = 1;
        BitConverter.TryWriteBytes(enteredBytes[16..], 4u);
        Assert.Equal(109775240917663232UL, entered.LobbyId);
        Assert.Equal(0x1Fu, entered.ChatPermissions);
        Assert.True(entered.IsLocked);
        Assert.Equal(SteamChatRoomEnterResponse.Full, entered.EnterResponse);
    }
}
