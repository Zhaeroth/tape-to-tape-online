using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests.Native;

public class InteropValueTests
{
    private static Span<byte> BytesOf<T>(ref T value)
        where T : unmanaged => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));

    [Fact]
    public void Steam_id_identity_has_the_bytes_SetSteamID64_writes()
    {
        const ulong id = 76561199000000001UL;
        var identity = SteamNetworkingIdentity.FromSteamId(id);
        Span<byte> bytes = BytesOf(ref identity);

        Assert.Equal(16, BitConverter.ToInt32(bytes[0..4]));
        Assert.Equal(8, BitConverter.ToInt32(bytes[4..8]));
        Assert.Equal(id, BitConverter.ToUInt64(bytes[8..16]));
        Assert.True(bytes[16..].IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(SteamNetworkingIdentityType.SteamId, identity.Type);
        Assert.Equal(8, identity.ByteCount);
        Assert.Equal(id, identity.SteamId64);
    }

    [Fact]
    public void Clear_zeroes_the_identity()
    {
        var identity = SteamNetworkingIdentity.FromSteamId(42);
        identity.Clear();

        Assert.True(BytesOf(ref identity).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(SteamNetworkingIdentityType.Invalid, identity.Type);
        Assert.Equal(0UL, identity.SteamId64);
    }

    [Fact]
    public void SteamId64_is_zero_for_other_identity_types()
    {
        var identity = SteamNetworkingIdentity.FromSteamId(42);
        BitConverter.TryWriteBytes(BytesOf(ref identity), (int)SteamNetworkingIdentityType.GenericBytes);

        Assert.Equal(0UL, identity.SteamId64);
    }

    [Fact]
    public void Connection_info_reads_its_strings_and_flags()
    {
        var info = default(SteamNetConnectionInfo);
        Span<byte> bytes = BytesOf(ref info);
        Encoding.UTF8.GetBytes("Timed out", bytes[SteamNetConnectionInfo.EndDebugOffset..]);
        Encoding.UTF8.GetBytes("#1234 P2P steamid:7656 vport 0", bytes[SteamNetConnectionInfo.ConnectionDescriptionOffset..]);
        BitConverter.TryWriteBytes(bytes[176..], (int)SteamNetworkingConnectionState.ProblemDetectedLocally);
        BitConverter.TryWriteBytes(bytes[180..], 4001);
        BitConverter.TryWriteBytes(bytes[172..], (uint)(('f' << 16) | ('r' << 8) | 'a'));
        BitConverter.TryWriteBytes(bytes[440..], (int)SteamConnectionTraits.Relayed);

        Assert.Equal("Timed out", info.EndDebug);
        Assert.Equal("#1234 P2P steamid:7656 vport 0", info.ConnectionDescription);
        Assert.Equal(SteamNetworkingConnectionState.ProblemDetectedLocally, info.State);
        Assert.Equal(4001, info.EndReason);
        Assert.Equal("fra", SteamPopId.ToCode(info.RelayPopId));
        Assert.True(info.IsRelayed);
    }

    [Fact]
    public void Unterminated_or_invalid_strings_are_bounded_and_never_throw()
    {
        var info = default(SteamNetConnectionInfo);
        Span<byte> bytes = BytesOf(ref info);
        bytes.Slice(SteamNetConnectionInfo.EndDebugOffset, SteamNetConnectionInfo.StringLength).Fill((byte)'x');
        bytes.Slice(SteamNetConnectionInfo.ConnectionDescriptionOffset, 4).Fill(0xFF);

        Assert.Equal(new string('x', SteamNetConnectionInfo.StringLength), info.EndDebug);
        Assert.Equal(4, info.ConnectionDescription.Length);
    }

    [Fact]
    public void Relay_status_reads_its_fields()
    {
        var status = default(SteamRelayNetworkStatus);
        Span<byte> bytes = BytesOf(ref status);
        BitConverter.TryWriteBytes(bytes[0..], (int)SteamNetworkingAvailability.Current);
        BitConverter.TryWriteBytes(bytes[4..], 1);
        BitConverter.TryWriteBytes(bytes[8..], (int)SteamNetworkingAvailability.Current);
        BitConverter.TryWriteBytes(bytes[12..], (int)SteamNetworkingAvailability.Attempting);
        Encoding.UTF8.GetBytes("Pinging relays", bytes[SteamRelayNetworkStatus.DebugMessageOffset..]);

        Assert.Equal(SteamNetworkingAvailability.Current, status.Availability);
        Assert.True(status.IsPingMeasurementInProgress);
        Assert.Equal(SteamNetworkingAvailability.Attempting, status.AnyRelayAvailability);
        Assert.Equal("Pinging relays", status.DebugMessage);
    }

    [Theory]
    [InlineData(0u, "")]
    [InlineData((uint)(('f' << 16) | ('r' << 8) | 'a'), "fra")]
    [InlineData((uint)(('d' << 24) | ('a' << 16) | ('b' << 8) | 'c'), "abcd")]
    [InlineData((uint)('s' << 16), "s")]
    [InlineData((uint)((0x01 << 16) | ('x' << 8) | 'y'), "?xy")]
    public void Pop_ids_decode_like_the_sdk(uint popId, string expected) => Assert.Equal(expected, SteamPopId.ToCode(popId));

    [Fact]
    public void Friend_game_info_decodes_the_game_id()
    {
        var plain = new FriendGameInfo(1566200, 109775240917663232UL);
        Assert.Equal(1566200u, plain.AppId);
        Assert.Equal(FriendGameInfo.GameIdTypeApp, plain.GameIdType);
        Assert.True(plain.IsPlayingApp(1566200));
        Assert.False(plain.IsPlayingApp(480));

        var mod = new FriendGameInfo(1566200UL | (1UL << 24) | (0x8000_0000UL << 32), 0);
        Assert.Equal(1566200u, mod.AppId);
        Assert.Equal(1, mod.GameIdType);
        Assert.False(mod.IsPlayingApp(1566200));
    }

    [Fact]
    public void Utf8_strings_are_null_terminated_and_reject_embedded_nul()
    {
        Assert.Equal(new byte[] { (byte)'k', (byte)'e', (byte)'y', 0 }, NativeStrings.ToUtf8("key"));
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0 }, NativeStrings.ToUtf8("é"));
        Assert.Equal(new byte[] { 0 }, NativeStrings.ToUtf8(string.Empty));
        Assert.Null(NativeStrings.ToUtf8("a\0b"));
        Assert.Null(NativeStrings.ToUtf8(null));
        Assert.Equal(string.Empty, NativeStrings.FromPointer(0));
    }

    [Fact]
    public void Utf8_strings_read_back_from_native_memory()
    {
        nint text = Marshal.StringToCoTaskMemUTF8("Alex é");
        try
        {
            Assert.Equal("Alex é", NativeStrings.FromPointer(text));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [Fact]
    public void Struct_buffer_reads_reject_out_of_range_slices()
    {
        var status = default(SteamRelayNetworkStatus);
        Assert.Equal(string.Empty, NativeStrings.FromStructBuffer(in status, 260, 20));
        Assert.Equal(string.Empty, NativeStrings.FromStructBuffer(in status, -1, 4));
        Assert.Equal(SteamRelayNetworkStatus.SizeInBytes, Unsafe.SizeOf<SteamRelayNetworkStatus>());
    }
}
