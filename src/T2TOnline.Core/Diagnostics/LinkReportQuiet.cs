using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The rate rule for a friend's own 5 s link line (its link to the host), as <see cref="QuietReports"/> is for the host's lines
/// about its friends: written every 5 s whatever changed, the line is about a sixth of a friend's log lines and a third
/// of its bytes over a long session, most of them a lobby's <c>0 pad packets/s</c>. A line is written when the link's kind changed
/// (the session's state, the route), the round trip's p95 rose by more than <see cref="QuietReports.RoundTripRiseMs"/>, Steam
/// refused a send, at least every <see cref="QuietReports.QuietEverySeconds"/>, and always at the session's end; otherwise it's
/// left out, and the next line written says how many were. The first is always written. The route is the caller's to read at
/// every report all the same (D133's guard reads it there). Not thread-safe.
/// </summary>
public sealed class LinkReportQuiet
{
    private readonly QuietReports _quiet = new();
    private string _kind = string.Empty;

    /// <summary>
    /// Whether this report is written. <paramref name="kind"/> is what the line says the link is (its state and route, never a
    /// measurement); <paramref name="final"/> the session's last report, always written (the next session's first is too).
    /// When written, <paramref name="leftOut"/> says how many reports before it were left out.
    /// </summary>
    /// <param name="kind">The link's state and route as text ("Joined, session Connected, relayed").</param>
    /// <param name="roundTripP95Ms">This window's round-trip 95th percentile, in milliseconds (0 when none was measured).</param>
    /// <param name="sendsRefused">Sends Steam refused so far this session.</param>
    /// <param name="nowSeconds">The time now, on one clock for the session.</param>
    /// <param name="final">The session's last report.</param>
    /// <param name="leftOut">Reports left out since the last one written (when this one is).</param>
    public bool ShouldWrite(string kind, double roundTripP95Ms, long sendsRefused, double nowSeconds, bool final, out int leftOut)
    {
        int before = _quiet.LeftOut;
        bool fresh = final || !string.Equals(kind, _kind, StringComparison.Ordinal);
        if (fresh)
        {
            _kind = final ? string.Empty : kind ?? string.Empty;
            _quiet.Clear();
        }

        bool write = _quiet.ShouldWrite(new InputReportCounters(0, roundTripP95Ms, 0, 0, 0, 0, 0, 0, sendsRefused), nowSeconds, out leftOut);
        if (fresh)
        {
            leftOut += before;
        }

        return write;
    }
}
