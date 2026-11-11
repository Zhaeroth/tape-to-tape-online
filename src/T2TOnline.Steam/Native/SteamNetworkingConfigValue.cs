namespace T2TOnline.Steam.Native;

/// <summary>
/// The subset of <c>ESteamNetworkingConfigValue</c> (steamnetworkingtypes.h) this project sets or reads, with the
/// SDK's numbers. The data type and narrowest scope of each value are given as in the SDK comments; a connection value
/// set at global scope becomes the default for connections created afterwards.
/// </summary>
public enum SteamNetworkingConfigValue
{
    /// <summary><c>k_ESteamNetworkingConfig_Invalid</c>.</summary>
    Invalid = 0,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketLoss_Send</c>: [global float, 0-100] percent of outgoing packets dropped.</summary>
    FakePacketLossSend = 2,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketLoss_Recv</c>: [global float, 0-100] percent of incoming packets dropped.</summary>
    FakePacketLossRecv = 3,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketLag_Send</c>: [global int32] ms added to every outgoing packet.</summary>
    FakePacketLagSend = 4,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketLag_Recv</c>: [global int32] ms added to every incoming packet.</summary>
    FakePacketLagRecv = 5,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketReorder_Send</c>: [global float, 0-100] percent of outgoing packets delayed to reorder them.</summary>
    FakePacketReorderSend = 6,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketReorder_Recv</c>: [global float, 0-100] percent of incoming packets delayed to reorder them.</summary>
    FakePacketReorderRecv = 7,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketReorder_Time</c>: [global int32] extra ms applied to reordered packets.</summary>
    FakePacketReorderTime = 8,

    /// <summary><c>k_ESteamNetworkingConfig_SendBufferSize</c>: [connection int32] bytes queued before sends fail.</summary>
    SendBufferSize = 9,

    /// <summary><c>k_ESteamNetworkingConfig_SendRateMin</c>: [connection int32] minimum send rate, bytes per second.</summary>
    SendRateMin = 10,

    /// <summary><c>k_ESteamNetworkingConfig_SendRateMax</c>: [connection int32] maximum send rate, bytes per second.</summary>
    SendRateMax = 11,

    /// <summary><c>k_ESteamNetworkingConfig_NagleTime</c>: [connection int32] Nagle delay in microseconds.</summary>
    NagleTime = 12,

    /// <summary><c>k_ESteamNetworkingConfig_LogLevel_P2PRendezvous</c>: [connection int32] P2P rendezvous logging.</summary>
    LogLevelP2PRendezvous = 17,

    /// <summary><c>k_ESteamNetworkingConfig_TimeoutInitial</c>: [connection int32] ms to wait for a connection to be established.</summary>
    TimeoutInitial = 24,

    /// <summary><c>k_ESteamNetworkingConfig_TimeoutConnected</c>: [connection int32] ms of silence before a connection drops.</summary>
    TimeoutConnected = 25,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketDup_Send</c>: [global float, 0-100] percent of outgoing packets duplicated.</summary>
    FakePacketDupSend = 26,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketDup_Recv</c>: [global float, 0-100] percent of incoming packets duplicated.</summary>
    FakePacketDupRecv = 27,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketDup_TimeMax</c>: [global int32] max ms a duplicate is delayed.</summary>
    FakePacketDupTimeMax = 28,

    /// <summary><c>k_ESteamNetworkingConfig_MTU_PacketSize</c>: [connection int32] UDP packet size limit, bytes.</summary>
    MtuPacketSize = 32,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketJitter_Send_Avg</c>: [global float] mean ms of exponential send jitter (0 = off).</summary>
    FakePacketJitterSendAvg = 53,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketJitter_Send_Max</c>: [global float] max ms of send jitter.</summary>
    FakePacketJitterSendMax = 54,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketJitter_Send_Pct</c>: [global float, 0-100] percent of sent packets jittered.</summary>
    FakePacketJitterSendPct = 55,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketJitter_Recv_Avg</c>: [global float] mean ms of exponential receive jitter (0 = off).</summary>
    FakePacketJitterRecvAvg = 56,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketJitter_Recv_Max</c>: [global float] max ms of receive jitter.</summary>
    FakePacketJitterRecvMax = 57,

    /// <summary><c>k_ESteamNetworkingConfig_FakePacketJitter_Recv_Pct</c>: [global float, 0-100] percent of received packets jittered.</summary>
    FakePacketJitterRecvPct = 58,

    /// <summary><c>k_ESteamNetworkingConfig_P2P_STUN_ServerList</c>: [connection string] STUN servers for NAT piercing.</summary>
    P2PStunServerList = 103,

    /// <summary>
    /// <c>k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable</c>: [connection int32] which ICE candidate types to share
    /// with the peer, a bitmask of <see cref="SteamIceCandidates"/> values.
    /// </summary>
    P2PTransportIceEnable = 104,

    /// <summary><c>k_ESteamNetworkingConfig_P2P_Transport_ICE_Penalty</c>: [connection int32] ms added to ICE route scores.</summary>
    P2PTransportIcePenalty = 105,

    /// <summary><c>k_ESteamNetworkingConfig_P2P_Transport_SDR_Penalty</c>: [connection int32] ms added to relayed route scores.</summary>
    P2PTransportSdrPenalty = 106,
}
