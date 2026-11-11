using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>SteamNetConnectionRealTimeStatus_t</c> (steamnetworkingtypes.h, <c>#pragma pack(8)</c> on Windows): 120 bytes.
/// Twelve 4-byte fields at 0-44, <c>m_usecQueueTime</c> (int64) at 48, <c>m_usecMaxJitter</c> (int32) at 56,
/// <c>reserved[15]</c> at 60. Verified with MSVC x64 <c>offsetof</c> against the Steamworks SDK headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = SizeInBytes)]
public readonly struct SteamNetConnectionRealTimeStatus
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 120;

    private readonly SteamNetworkingConnectionState _state;
    private readonly int _ping;
    private readonly float _qualityLocal;
    private readonly float _qualityRemote;
    private readonly float _outPacketsPerSecond;
    private readonly float _outBytesPerSecond;
    private readonly float _inPacketsPerSecond;
    private readonly float _inBytesPerSecond;
    private readonly int _sendRateBytesPerSecond;
    private readonly int _pendingUnreliable;
    private readonly int _pendingReliable;
    private readonly int _sentUnackedReliable;
    private readonly long _queueTimeMicroseconds;
    private readonly int _maxJitterMicroseconds;

    /// <summary>High-level state (<c>m_eState</c>).</summary>
    public SteamNetworkingConnectionState State => _state;

    /// <summary>Current round-trip ping in milliseconds (<c>m_nPing</c>).</summary>
    public int Ping => _ping;

    /// <summary>Fraction of packets delivered end to end in order, measured locally, 0-1 (<c>m_flConnectionQualityLocal</c>).</summary>
    public float QualityLocal => _qualityLocal;

    /// <summary>The same as seen by the peer, 0-1 (<c>m_flConnectionQualityRemote</c>).</summary>
    public float QualityRemote => _qualityRemote;

    /// <summary>Recent outgoing packets per second (<c>m_flOutPacketsPerSec</c>).</summary>
    public float OutPacketsPerSecond => _outPacketsPerSecond;

    /// <summary>Recent outgoing bytes per second (<c>m_flOutBytesPerSec</c>).</summary>
    public float OutBytesPerSecond => _outBytesPerSecond;

    /// <summary>Recent incoming packets per second (<c>m_flInPacketsPerSec</c>).</summary>
    public float InPacketsPerSecond => _inPacketsPerSecond;

    /// <summary>Recent incoming bytes per second (<c>m_flInBytesPerSec</c>).</summary>
    public float InBytesPerSecond => _inBytesPerSecond;

    /// <summary>Estimated send capacity, bytes per second (<c>m_nSendRateBytesPerSecond</c>).</summary>
    public int SendRateBytesPerSecond => _sendRateBytesPerSecond;

    /// <summary>Unreliable bytes queued but not yet sent (<c>m_cbPendingUnreliable</c>).</summary>
    public int PendingUnreliable => _pendingUnreliable;

    /// <summary>Reliable bytes queued or awaiting retransmission (<c>m_cbPendingReliable</c>).</summary>
    public int PendingReliable => _pendingReliable;

    /// <summary>Reliable bytes on the wire awaiting acknowledgement (<c>m_cbSentUnackedReliable</c>).</summary>
    public int SentUnackedReliable => _sentUnackedReliable;

    /// <summary>Estimated wait before a newly queued message starts sending, microseconds (<c>m_usecQueueTime</c>).</summary>
    public long QueueTimeMicroseconds => _queueTimeMicroseconds;

    /// <summary>
    /// Highest jitter since the last query, microseconds; negative means no data (<c>m_usecMaxJitter</c>). Added in a
    /// recent SDK in what used to be reserved space, so an older Steam client leaves it 0.
    /// </summary>
    public int MaxJitterMicroseconds => _maxJitterMicroseconds;
}
