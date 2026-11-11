using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

/// <summary>
/// Native-looking <c>SteamNetworkingMessage_t</c> blocks in unmanaged memory, handed out like
/// <c>ReceiveMessagesOnChannel</c> does. Release marks the message (in its <c>m_nUserData</c> field) so the tests can
/// prove every message is released exactly once; Receive and Release allocate nothing.
/// </summary>
internal sealed class FakeMessageQueue : ISteamMessageQueue, IDisposable
{
    private const int UserDataOffset = 200;
    private const long ReleasedMark = 0x52454C;

    private readonly Queue<nint> _waiting = new();
    private readonly List<nint> _allocations = new();
    private readonly List<nint> _messages = new();

    public int ReceiveCalls { get; private set; }

    public int Released { get; private set; }

    public int DoubleReleases { get; private set; }

    /// <summary>
    /// When set, Receive hands over at most this many messages but reports this count, like a misbehaving native call.
    /// </summary>
    public int? ReportedCount { get; set; }

    public int Waiting => _waiting.Count;

    public nint Add(ulong sender, ReadOnlySpan<byte> payload, SteamNetworkingIdentityType identityType = SteamNetworkingIdentityType.SteamId)
    {
        nint data = 0;
        if (payload.Length > 0)
        {
            data = Allocate(payload.Length);
            Marshal.Copy(payload.ToArray(), 0, data, payload.Length);
        }

        return AddRaw(data, payload.Length, sender, identityType);
    }

    public nint AddRaw(nint data, int size, ulong sender, SteamNetworkingIdentityType identityType = SteamNetworkingIdentityType.SteamId)
    {
        nint message = Allocate(SteamNetworkingMessage.SizeInBytes);
        for (int i = 0; i < SteamNetworkingMessage.SizeInBytes; i++)
        {
            Marshal.WriteByte(message, i, 0);
        }

        Marshal.WriteIntPtr(message, SteamNetworkingMessage.DataOffset, data);
        Marshal.WriteInt32(message, SteamNetworkingMessage.SizeOffset, size);
        Marshal.WriteInt32(message, SteamNetworkingMessage.IdentityPeerOffset + SteamNetworkingIdentity.TypeOffset, (int)identityType);
        Marshal.WriteInt32(message, SteamNetworkingMessage.IdentityPeerOffset + SteamNetworkingIdentity.ByteCountOffset, 8);
        Marshal.WriteInt64(message, SteamNetworkingMessage.IdentityPeerOffset + SteamNetworkingIdentity.SteamId64Offset, unchecked((long)sender));
        _waiting.Enqueue(message);
        _messages.Add(message);
        return message;
    }

    /// <summary>Queues a null message pointer, which Steam should never return.</summary>
    public void AddNull() => _waiting.Enqueue(0);

    public static bool IsReleased(nint message) => Marshal.ReadInt64(message, UserDataOffset) == ReleasedMark;

    public bool AllReleased()
    {
        foreach (nint message in _messages)
        {
            if (!IsReleased(message))
            {
                return false;
            }
        }

        return true;
    }

    public int Receive(int channel, nint[] messages, int maxMessages)
    {
        ReceiveCalls++;
        int count = 0;
        int limit = ReportedCount is int reported ? Math.Min(reported, maxMessages) : maxMessages;
        while (count < limit && _waiting.Count > 0)
        {
            messages[count++] = _waiting.Dequeue();
        }

        return ReportedCount ?? count;
    }

    public void Release(nint message)
    {
        if (message == 0)
        {
            return;
        }

        if (IsReleased(message))
        {
            DoubleReleases++;
            return;
        }

        Marshal.WriteInt64(message, UserDataOffset, ReleasedMark);
        Released++;
    }

    public void Dispose()
    {
        foreach (nint block in _allocations)
        {
            Marshal.FreeHGlobal(block);
        }

        _allocations.Clear();
    }

    private nint Allocate(int size)
    {
        nint block = Marshal.AllocHGlobal(size);
        _allocations.Add(block);
        return block;
    }
}
