using System;

namespace T2TOnline.Core.Timing;

/// <summary>
/// Estimates the offset between this PC's clock and the host's from ping exchanges, NTP style.
/// Keeps the last <see cref="WindowSize"/> samples, discards those with unusually long round trips (their one-way
/// split is the least reliable), and reports the median of the rest. All times are in microseconds.
/// </summary>
public sealed class ClockSync
{
    /// <summary>A round trip longer than this (a minute) is a broken sample, not a slow link.</summary>
    public const long MaxRoundTripMicros = 60_000_000;

    /// <summary>Samples kept.</summary>
    public const int WindowSize = 16;

    private readonly long[] _offsets = new long[WindowSize];
    private readonly long[] _roundTrips = new long[WindowSize];
    private readonly long[] _scratch = new long[WindowSize];
    private int _count;
    private int _next;

    /// <summary>True once at least one valid sample has been added.</summary>
    public bool HasEstimate => _count > 0;

    /// <summary>Host clock minus local clock.</summary>
    public long OffsetMicros { get; private set; }

    /// <summary>Median round-trip time.</summary>
    public long RoundTripMicros { get; private set; }

    /// <summary>Estimated one-way delay: half the median round trip.</summary>
    public long OneWayDelayMicros => RoundTripMicros / 2;

    /// <summary>Adds one ping exchange.</summary>
    /// <param name="localSendMicros">Local time the ping left.</param>
    /// <param name="remoteMicros">Host time stamped in the reply.</param>
    /// <param name="localReceiveMicros">Local time the reply arrived.</param>
    /// <returns>False if the sample was rejected (a negative round trip, one over <see cref="MaxRoundTripMicros"/>, or a negative send time).</returns>
    public bool AddSample(long localSendMicros, long remoteMicros, long localReceiveMicros)
    {
        long roundTrip = localReceiveMicros - localSendMicros;
        if (roundTrip < 0 || roundTrip > MaxRoundTripMicros || localSendMicros < 0)
        {
            return false;
        }

        _offsets[_next] = remoteMicros - (localSendMicros + (roundTrip / 2));
        _roundTrips[_next] = roundTrip;
        _next = (_next + 1) % WindowSize;
        _count = Math.Min(_count + 1, WindowSize);
        Recompute();
        return true;
    }

    /// <summary>Converts a local time to host time.</summary>
    public long ToHost(long localMicros) => localMicros + OffsetMicros;

    /// <summary>Converts a host time to local time.</summary>
    public long ToLocal(long hostMicros) => hostMicros - OffsetMicros;

    /// <summary>Forgets all samples, for example after a reconnect.</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        OffsetMicros = 0;
        RoundTripMicros = 0;
    }

    private void Recompute()
    {
        Span<long> scratch = _scratch.AsSpan(0, _count);
        _roundTrips.AsSpan(0, _count).CopyTo(scratch);
        RoundTripMicros = Median(scratch);

        // Spike rejection: keep samples within 1.5x the median round trip (plus 1 ms of slack for tiny pings).
        long limit = (RoundTripMicros * 3 / 2) + 1000;
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_roundTrips[i] <= limit)
            {
                scratch[kept++] = _offsets[i];
            }
        }

        if (kept > 0)
        {
            OffsetMicros = Median(scratch[..kept]);
        }
    }

    private static long Median(Span<long> values)
    {
        values.Sort();
        int mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : values[mid - 1] + ((values[mid] - values[mid - 1]) / 2);
    }
}
