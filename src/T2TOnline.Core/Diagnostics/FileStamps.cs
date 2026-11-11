using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Diagnostics;

/// <summary>One file's size and last write time, by its path relative to the folder it was read from.</summary>
/// <param name="Path">The path relative to the folder (compared without case, as Windows does).</param>
/// <param name="Length">The size in bytes.</param>
/// <param name="LastWriteUtc">The last write time, UTC.</param>
public readonly record struct FileStamp(string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// Which files changed between two looks at a folder: new, rewritten (another size or write time) or gone. The plugin
/// uses it to confirm, every session, that Unity's own analytics never wrote its local files.
/// </summary>
public static class FileStamps
{
    /// <summary>
    /// The files that differ between <paramref name="before"/> and <paramref name="after"/>, each as "path (new)",
    /// "path (rewritten)" or "path (gone)", sorted by path; empty when nothing changed.
    /// </summary>
    public static IReadOnlyList<string> Changed(IEnumerable<FileStamp> before, IEnumerable<FileStamp> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (FileStamp stamp in before)
        {
            old.TryAdd(stamp.Path, stamp);
        }

        var changes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileStamp stamp in after)
        {
            if (!seen.Add(stamp.Path))
            {
                continue;
            }

            if (!old.TryGetValue(stamp.Path, out FileStamp was))
            {
                changes[stamp.Path] = $"{stamp.Path} (new)";
            }
            else if (was.Length != stamp.Length || was.LastWriteUtc != stamp.LastWriteUtc)
            {
                changes[stamp.Path] = $"{stamp.Path} (rewritten)";
            }
        }

        foreach (string path in old.Keys)
        {
            if (!seen.Contains(path))
            {
                changes[path] = $"{path} (gone)";
            }
        }

        return new List<string>(changes.Values);
    }

    /// <summary>The newest write time among <paramref name="stamps"/>, or null when there are none.</summary>
    public static DateTime? Newest(IEnumerable<FileStamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        DateTime? newest = null;
        foreach (FileStamp stamp in stamps)
        {
            if (newest == null || stamp.LastWriteUtc > newest)
            {
                newest = stamp.LastWriteUtc;
            }
        }

        return newest;
    }
}
