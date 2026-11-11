using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What the developer's output under <c>UserData\T2TOnline\dev\</c> keeps (D128): stream recordings, snapshot captures and
/// the check tours' folders, each pruned at start-up and after each new one, so none grows without bound.
/// </summary>
public static class DevFileRetention
{
    /// <summary>Snapshot captures kept (<c>dev\captures\*.t2tsnap</c>, each with its names file).</summary>
    public const int KeptCaptures = 10;

    /// <summary>Stream recordings kept (<c>dev\streams\*.t2tstream</c>).</summary>
    public const int KeptStreams = 20;

    /// <summary>The most the kept stream recordings may take in all.</summary>
    public const long MaxStreamBytes = 250L * 1024 * 1024;

    /// <summary>Check tours kept (<c>dev\ui-check\&lt;stamp&gt;\</c>).</summary>
    public const int KeptTours = 5;

    /// <summary>A check tour's folder name: its start in UTC (<c>20261105T100000Z</c>).</summary>
    public const string TourStampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>
    /// Which of <paramref name="items"/> go: newest first (by last write, then by name), a run of the newest is kept while
    /// it holds at most <paramref name="keep"/> and at most <paramref name="maxBytes"/> in all; everything older goes. The
    /// newest, and <paramref name="protect"/> (the one being written, compared without case), are always kept and count
    /// toward both limits. Returned oldest last.
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<FileStamp> items, int keep, long maxBytes = long.MaxValue, string? protect = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (keep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "Keep at least one.");
        }

        List<FileStamp> newestFirst = items
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(i => i.LastWriteUtc)
            .ThenByDescending(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int protectedAt = protect == null ? -1 : newestFirst.FindIndex(i => string.Equals(i.Path, protect, StringComparison.OrdinalIgnoreCase));
        if (protectedAt > 0)
        {
            FileStamp kept = newestFirst[protectedAt];
            newestFirst.RemoveAt(protectedAt);
            newestFirst.Insert(0, kept);
        }

        long total = 0;
        int count = 0;
        var expired = new List<string>();
        foreach (FileStamp item in newestFirst)
        {
            long bytes = Math.Max(item.Length, 0);
            bool fits = count == 0 || (expired.Count == 0 && count < keep && bytes <= maxBytes - total);
            if (fits)
            {
                count++;
                total += bytes;
            }
            else
            {
                expired.Add(item.Path);
            }
        }

        return expired;
    }

    /// <summary>True for a check tour's folder name (<see cref="TourStampFormat"/>); only those are ever pruned.</summary>
    public static bool TryParseTour(string? name, out DateTime startedUtc)
    {
        startedUtc = default;
        return !string.IsNullOrEmpty(name) && name.Length == 16 &&
            DateTime.TryParseExact(name, TourStampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out startedUtc);
    }

    /// <summary>
    /// "a, b, c" for a log line: the first <paramref name="shown"/> names, then "and N more". Empty for none.
    /// </summary>
    public static string NameList(IReadOnlyCollection<string> names, int shown = 8)
    {
        ArgumentNullException.ThrowIfNull(names);
        string list = string.Join(", ", names.Take(Math.Max(shown, 1)));
        return names.Count > shown ? $"{list} and {names.Count - shown} more" : list;
    }
}
