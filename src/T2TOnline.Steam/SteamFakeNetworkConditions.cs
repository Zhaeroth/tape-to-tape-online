namespace T2TOnline.Steam;

/// <summary>
/// Steam's simulated network conditions (the global <c>FakePacket*</c> config values), applied with
/// <see cref="SteamNetworking.ApplyFakeConditions"/>. They act at the UDP layer on everything this process sends or
/// receives through Steam networking, so set them on one PC to test against a real peer. The default value is a
/// perfect network (all zero), which also turns the simulation off. Percentages are 0-100. Applying clamps every value:
/// percentages to 0-100, times to 0-<see cref="MaxDelayMs"/>, and NaN to 0.
/// </summary>
public readonly record struct SteamFakeNetworkConditions
{
    /// <summary>The longest simulated delay applied, in ms (lag, jitter, reorder and duplicate times).</summary>
    public const int MaxDelayMs = 5000;

    /// <summary>Milliseconds added to every outgoing packet (<c>FakePacketLag_Send</c>).</summary>
    public int LagSendMs { get; init; }

    /// <summary>Milliseconds added to every incoming packet (<c>FakePacketLag_Recv</c>).</summary>
    public int LagRecvMs { get; init; }

    /// <summary>Percent of outgoing packets dropped (<c>FakePacketLoss_Send</c>).</summary>
    public float LossSendPercent { get; init; }

    /// <summary>Percent of incoming packets dropped (<c>FakePacketLoss_Recv</c>).</summary>
    public float LossRecvPercent { get; init; }

    /// <summary>Mean extra ms of exponential send jitter; 0 disables it (<c>FakePacketJitter_Send_Avg</c>).</summary>
    public float JitterSendAvgMs { get; init; }

    /// <summary>Cap on send jitter, ms (<c>FakePacketJitter_Send_Max</c>).</summary>
    public float JitterSendMaxMs { get; init; }

    /// <summary>Percent of sent packets that get jitter (<c>FakePacketJitter_Send_Pct</c>).</summary>
    public float JitterSendPercent { get; init; }

    /// <summary>Mean extra ms of exponential receive jitter; 0 disables it (<c>FakePacketJitter_Recv_Avg</c>).</summary>
    public float JitterRecvAvgMs { get; init; }

    /// <summary>Cap on receive jitter, ms (<c>FakePacketJitter_Recv_Max</c>).</summary>
    public float JitterRecvMaxMs { get; init; }

    /// <summary>Percent of received packets that get jitter (<c>FakePacketJitter_Recv_Pct</c>).</summary>
    public float JitterRecvPercent { get; init; }

    /// <summary>Percent of outgoing packets delayed so they arrive out of order (<c>FakePacketReorder_Send</c>).</summary>
    public float ReorderSendPercent { get; init; }

    /// <summary>Percent of incoming packets delayed so they arrive out of order (<c>FakePacketReorder_Recv</c>).</summary>
    public float ReorderRecvPercent { get; init; }

    /// <summary>Extra delay of a reordered packet, ms (<c>FakePacketReorder_Time</c>).</summary>
    public int ReorderTimeMs { get; init; }

    /// <summary>Percent of outgoing packets duplicated (<c>FakePacketDup_Send</c>).</summary>
    public float DuplicateSendPercent { get; init; }

    /// <summary>Percent of incoming packets duplicated (<c>FakePacketDup_Recv</c>).</summary>
    public float DuplicateRecvPercent { get; init; }

    /// <summary>Max delay of a duplicate, ms (<c>FakePacketDup_TimeMax</c>).</summary>
    public int DuplicateTimeMaxMs { get; init; }
}
