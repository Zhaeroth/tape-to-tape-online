using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The dated copy a friend's <see cref="ProgressLedger"/> is owed when it changed with none written after it
/// (<see cref="ProgressLedgerHistory"/>). The copies are taken as a following session starts and ends, and a restore of
/// saves picks the one from its backup's time; that holds only while every change of the ledger is followed by a copy.
/// Two aren't: a session the game stopped in the middle of saved the ledger after every batch and never reached its end
/// copy, and a restore replaces the ledger. A backup taken after either (one taken before another restore, a host's) would
/// restore with the copy from before the change: a ledger older than its saves, under which a rejoin applies again what
/// they hold, or one newer, under which it skips what they lack. So the ledger as it is gets a copy dated when it was last
/// written, which is when it became what it is. Game-independent.
/// </summary>
public static class ProgressLedgerCatchUp
{
    /// <summary>The newest dated copy among <paramref name="names"/> and when it was taken, or null when there's none.</summary>
    public static string? Newest(IEnumerable<string> names, out DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(names);
        string? newest = null;
        utc = default;
        foreach (string name in names)
        {
            if (ProgressLedgerHistory.TryParse(name, out DateTime taken, out _) && (newest == null || taken > utc))
            {
                newest = name;
                utc = taken;
            }
        }

        return newest;
    }

    /// <summary>
    /// The name of the copy the live ledger is owed, or null when it's owed none: there's no ledger
    /// (<paramref name="liveText"/> null), or the newest copy (<paramref name="newestText"/>; null: none) already holds it.
    /// The copy is an end copy (it never stands for a backup's own session), dated when the ledger was last written
    /// (<paramref name="liveWrittenUtc"/>, never later than <paramref name="utcNow"/>), and always newer than the newest
    /// copy there is (<paramref name="newestUtc"/>): it's the ledger from then on.
    /// </summary>
    public static string? CopyOwed(string? liveText, DateTime liveWrittenUtc, string? newestText, DateTime? newestUtc, DateTime utcNow)
    {
        if (liveText == null || (newestText != null && string.Equals(liveText, newestText, StringComparison.Ordinal)))
        {
            return null;
        }

        DateTime at = liveWrittenUtc.ToUniversalTime();
        DateTime now = utcNow.ToUniversalTime();
        if (at > now)
        {
            at = now;
        }

        // The copies' names tell times a millisecond apart: this one's is a whole millisecond, after the newest's.
        at = new DateTime(at.Ticks - (at.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        if (newestUtc is DateTime newest && at <= newest.ToUniversalTime() && newest.ToUniversalTime() < DateTime.MaxValue.AddMilliseconds(-1))
        {
            at = newest.ToUniversalTime().AddMilliseconds(1);
        }

        return ProgressLedgerHistory.Name(at, start: false);
    }
}
