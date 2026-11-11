using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Diagnostics;

/// <summary>One session log in the logs folder, as <see cref="LogRetention"/> sees it.</summary>
/// <param name="Name">The file's name (<c>session-20261105T100000Z.log</c>).</param>
/// <param name="Bytes">Its size.</param>
/// <param name="WrittenUtc">When it was last written (UTC): its age when the name holds no time.</param>
public sealed record SessionLogFile(string Name, long Bytes, DateTime WrittenUtc);

/// <summary>Why <see cref="LogRetention"/> deletes a session log.</summary>
public enum LogRemoval
{
    /// <summary>Older than <see cref="LogRetention.MaxAge"/>.</summary>
    Age,

    /// <summary>Past <see cref="LogRetention.MaxFiles"/>.</summary>
    Count,

    /// <summary>Past <see cref="LogRetention.MaxTotalBytes"/>.</summary>
    Size,
}

/// <summary>
/// How long session logs are kept (D128): at most <see cref="MaxFiles"/> files of at most <see cref="MaxFileBytes"/> each,
/// <see cref="MaxTotalBytes"/> in all and <see cref="MaxAge"/> old, and the newest <see cref="AlwaysKept"/> whatever
/// their age or size. Applied at start-up before the new session's file is made, so the limits leave room for it (one
/// file, up to <see cref="MaxFileBytes"/>) and the newest <see cref="AlwaysKept"/> count it. The oldest go first.
/// </summary>
public static class LogRetention
{
    /// <summary>Session logs kept, this session's included.</summary>
    public const int MaxFiles = 20;

    /// <summary>A session log's size limit: the file is cut there.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>All session logs together, this session's included at its limit.</summary>
    public const long MaxTotalBytes = 100L * 1024 * 1024;

    /// <summary>Session logs kept whatever their age or size, this session's included.</summary>
    public const int AlwaysKept = 5;

    /// <summary>A session log older than this goes (unless it is one of the newest <see cref="AlwaysKept"/>).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>The session logs' file name pattern.</summary>
    public const string Pattern = "session-*.log";

    /// <summary>
    /// The logs of <paramref name="existing"/> to delete before a new session's starts at <paramref name="nowUtc"/>, and
    /// why, oldest last. Files are dated by the UTC time in their name (<see cref="StampOf"/>).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<SessionLogFile, LogRemoval>> ToDelete(IEnumerable<SessionLogFile> existing, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var deleted = new List<KeyValuePair<SessionLogFile, LogRemoval>>();
        int kept = 0;
        long bytes = 0;
        bool overBudget = false;
        foreach (SessionLogFile file in existing.Where(f => f != null)
                     .OrderByDescending(StampOf).ThenByDescending(f => f.Name, StringComparer.Ordinal))
        {
            long size = Math.Max(0, file.Bytes);
            if (kept < AlwaysKept - 1)
            {
                kept++;
                bytes += size;
                continue;
            }

            LogRemoval? why = nowUtc - StampOf(file) > MaxAge ? LogRemoval.Age
                : kept >= MaxFiles - 1 ? LogRemoval.Count
                : overBudget || bytes + size > MaxTotalBytes - MaxFileBytes ? LogRemoval.Size
                : null;
            if (why is { } removal)
            {
                overBudget |= removal == LogRemoval.Size;
                deleted.Add(new KeyValuePair<SessionLogFile, LogRemoval>(file, removal));
                continue;
            }

            kept++;
            bytes += size;
        }

        return deleted;
    }

    /// <summary>
    /// When a session log was started: the UTC time in its name (<c>session-yyyyMMddTHHmmssZ.log</c>), else when it was
    /// last written.
    /// </summary>
    public static DateTime StampOf(SessionLogFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        const string Prefix = "session-";
        const string Format = "yyyyMMdd'T'HHmmss'Z'";
        string name = file.Name ?? string.Empty;
        if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && name.Length >= Prefix.Length + 16 &&
            DateTime.TryParseExact(name.AsSpan(Prefix.Length, 16), Format, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime stamp))
        {
            return stamp;
        }

        return DateTime.SpecifyKind(file.WrittenUtc, DateTimeKind.Utc);
    }

    /// <summary>One line for the log about <paramref name="deleted"/>, or "" when it's empty.</summary>
    public static string Describe(IReadOnlyCollection<KeyValuePair<SessionLogFile, LogRemoval>> deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        if (deleted.Count == 0)
        {
            return string.Empty;
        }

        var why = new List<string>();
        foreach ((LogRemoval removal, string words) in new[]
                 {
                     (LogRemoval.Age, $"older than {MaxAge.TotalDays:0} days"),
                     (LogRemoval.Count, $"past {MaxFiles} files"),
                     (LogRemoval.Size, $"past {MaxTotalBytes / (1024 * 1024)} MB in all"),
                 })
        {
            int count = deleted.Count(d => d.Value == removal);
            if (count > 0)
            {
                why.Add($"{count} {words}");
            }
        }

        return $"Session logs: deleted the {deleted.Count} oldest ({string.Join(", ", why)}); the newest {AlwaysKept} are always kept.";
    }
}
