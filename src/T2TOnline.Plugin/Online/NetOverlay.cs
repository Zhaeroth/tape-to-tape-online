#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The net overlay (F9): a box in the top-right corner with the session's live numbers,
/// per friend on the host (ping, loss, pad rate and playout, the stream out, how late each friend sees the match) and
/// for the link to the host on a friend's PC. Sampled twice a second from running totals (<see cref="RunningTotals"/>,
/// tested in Core), so it never disturbs the log's own report windows. <see cref="OnlineSession"/> fills it.
/// </summary>
internal sealed class NetOverlay
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);
    private readonly RunningTotals _totals = new();
    private List<string> _lines = new();

    /// <summary>True while the overlay is on.</summary>
    public bool Visible { get; private set; }

    /// <summary>The lines to draw (empty while hidden): a new list each sample, so the overlay's drawing can keep its layout in between.</summary>
    public IReadOnlyList<string> Lines => Visible ? _lines : Array.Empty<string>();

    /// <summary>F9: on or off.</summary>
    public void Toggle()
    {
        Visible = !Visible;
        _totals.Reset();
        _lines = new List<string>();
        ModLog.Info($"Net overlay {(Visible ? "on" : "off")} (F9).");
    }

    /// <summary>Off, if it's on: online is off after an error, so there's nothing left to measure.</summary>
    public void Hide()
    {
        if (!Visible)
        {
            return;
        }

        Visible = false;
        _totals.Reset();
        _lines = new List<string>();
        ModLog.Info("Net overlay off (online is off after an error).");
    }

    /// <summary>True when the lines should be rebuilt now.</summary>
    public bool Due(TimeSpan now) => Visible && (_totals.SampledAt == TimeSpan.MinValue || now - _totals.SampledAt >= Every);

    /// <summary>Starts a new sample at <paramref name="now"/>.</summary>
    public void Begin(TimeSpan now)
    {
        _totals.Begin(now);
        _lines = new List<string>();
    }

    /// <summary>Adds a line.</summary>
    public void Line(string text) => _lines.Add(text);

    /// <summary>The per-second rate of a running total since the last sample (0 on the first, or after a reset).</summary>
    public double PerSecond(string key, long total) => _totals.PerSecond(key, total);

    /// <summary>How much a running total grew since the last sample (0 on the first, or after a reset).</summary>
    public long Delta(string key, long total) => _totals.Delta(key, total);

    /// <summary>Loss over the last sample, in percent, from running expected and received counts.</summary>
    public double LossPercent(string key, long expected, long received) => _totals.LossPercent(key, expected, received);

    /// <summary>Milliseconds for display ("49" or "-").</summary>
    public static string Ms(long micros) => RunningTotals.Ms(micros);
}
#endif
