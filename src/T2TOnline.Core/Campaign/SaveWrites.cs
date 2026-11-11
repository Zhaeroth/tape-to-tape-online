using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The files Easy Save 3 touches when it saves one save file <c>X</c> (Oct 29 build): it writes <c>X.tmp</c> and commits it
/// in <c>ES3IO.CommitBackup</c> (deletes <c>X.tmp.bak</c>, copies <c>X</c> to <c>X.tmp.bak</c> without overwriting, deletes
/// <c>X</c>, moves <c>X.tmp</c> to <c>X</c>, deletes <c>X.tmp.bak</c>), and <c>ES3.CreateBackup</c> copies <c>X</c> to
/// <c>X.bac</c> (deleting the old one first). The save guard lets those through with an allowed file, and nothing else.
/// </summary>
public static class EasySaveCopies
{
    /// <summary>The suffixes of Easy Save's own copies of a save file, longest first.</summary>
    public static IReadOnlyList<string> Suffixes { get; } = new[] { ".tmp.bak", ".tmp", ".bac" };

    /// <summary>
    /// The save file a write naming <paramref name="fileName"/> is for: the name itself, or, for one of Easy Save's copies,
    /// the file it's a copy of (one suffix only: <c>X.bac.tmp</c> is nobody's copy). Names only, no folders; null gives "".
    /// </summary>
    public static string Original(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        foreach (string suffix in Suffixes)
        {
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^suffix.Length];
            }
        }

        return fileName;
    }

    /// <summary>True when a write naming <paramref name="fileName"/> is a write of <paramref name="saveFile"/> or one of its copies (ignoring case).</summary>
    public static bool IsWriteOf(string? fileName, string saveFile) =>
        !string.IsNullOrEmpty(saveFile) && string.Equals(Original(fileName), saveFile, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A file's size and last write (UTC), to tell whether a save wrote it.</summary>
/// <param name="Exists">The file is there.</param>
/// <param name="Length">Its size (0 when it isn't there).</param>
/// <param name="WrittenUtc">Its last write (default when it isn't there).</param>
public readonly record struct SaveStamp(bool Exists, long Length, DateTime WrittenUtc)
{
    /// <summary>The stamp of the file at <paramref name="path"/> now (not there: <c>default</c>). Throws only for a path that can't be a file's.</summary>
    public static SaveStamp Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new SaveStamp(true, info.Length, info.LastWriteTimeUtc) : default;
    }
}

/// <summary>Whether a run save reached the disk, by the run file's stamp (<see cref="RunSaveCheck.Judge"/>).</summary>
public enum SaveLanded
{
    /// <summary>The file changed since before the save: the save wrote it.</summary>
    Written,

    /// <summary>Not changed yet; look again.</summary>
    NotYet,

    /// <summary>
    /// Unchanged, but the file had been written just before the save started, closer than file times can tell apart: the
    /// save may have written the same size again. What's there is taken as the save's.
    /// </summary>
    Unclear,

    /// <summary>Unchanged (or not there) well after the save: it never reached the disk.</summary>
    NotWritten,
}

/// <summary>
/// Tells whether the game's run save reached the disk. The game's run save is synchronous, so a run
/// file whose size and last write didn't change across it wasn't written: a full disk, a file locked by another program, a
/// write refused. Only when the file had been written moments before the save can an unchanged stamp be the save's own
/// write of the same size, since file times are coarser than the clock.
/// </summary>
public static class RunSaveCheck
{
    /// <summary>How close to a save's start the file's last write must be for an unchanged stamp to be <see cref="SaveLanded.Unclear"/>.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Whether the save that started at <paramref name="saveStartedUtc"/>, with the file at <paramref name="before"/> then,
    /// reached the disk, the file being at <paramref name="now"/>. <paramref name="lastLook"/>: no further look will come.
    /// </summary>
    public static SaveLanded Judge(SaveStamp before, DateTime saveStartedUtc, SaveStamp now, bool lastLook)
    {
        if (now.Exists && now != before)
        {
            return SaveLanded.Written;
        }

        if (!lastLook)
        {
            return SaveLanded.NotYet;
        }

        return now.Exists && before.Exists && saveStartedUtc - before.WrittenUtc < ClockSlack ? SaveLanded.Unclear : SaveLanded.NotWritten;
    }
}

/// <summary>
/// File-error text for logs players send: known folders in it (the Windows user's profile, the game's save folder,
/// the game folder) become placeholders, so the log doesn't carry the player's user name.
/// </summary>
public static class SavePathText
{
    /// <summary>
    /// <paramref name="text"/> with each of <paramref name="folders"/> (a folder and the placeholder it becomes) replaced,
    /// ignoring case and the kind of slash; longer folders first, so a save folder inside the profile keeps its own name. A
    /// folder counts only as a whole name (<c>C:\Users\bob</c> isn't in <c>C:\Users\bobby</c>). Folders of three characters
    /// or fewer (a drive) are skipped. Null gives "".
    /// </summary>
    public static string Redact(string? text, IEnumerable<KeyValuePair<string, string>> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        foreach (KeyValuePair<string, string> folder in folders
            .Where(f => !string.IsNullOrWhiteSpace(f.Key))
            .Select(f => new KeyValuePair<string, string>(f.Key.TrimEnd('\\', '/'), f.Value ?? string.Empty))
            .Where(f => f.Key.Length > 3)
            .OrderByDescending(f => f.Key.Length))
        {
            text = ReplaceWhole(text, folder.Key.Replace('/', '\\'), folder.Value);
            text = ReplaceWhole(text, folder.Key.Replace('\\', '/'), folder.Value);
        }

        return text;
    }

    private static string ReplaceWhole(string text, string folder, string placeholder)
    {
        int at = text.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            int end = at + folder.Length;
            if (end == text.Length || IsBoundary(text[end]))
            {
                text = string.Concat(text.AsSpan(0, at), placeholder, text.AsSpan(end));
                end = at + placeholder.Length;
            }

            at = end < text.Length ? text.IndexOf(folder, end, StringComparison.OrdinalIgnoreCase) : -1;
        }

        return text;
    }

    private static bool IsBoundary(char c) => !char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or '~' or '$');
}

/// <summary>
/// How many errors are logged from each place: the first <see cref="PerSite"/> from each, so a repeating error in
/// one place doesn't hide a different one later; the rest are counted, to be summed up at the session's end. Thread-safe.
/// </summary>
public sealed class ErrorLogLimit
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _suppressed = new(StringComparer.Ordinal);

    /// <summary>A limit of <paramref name="perSite"/> logged errors per place.</summary>
    public ErrorLogLimit(int perSite)
    {
        if (perSite < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(perSite), perSite, "At least one error per place is logged.");
        }

        PerSite = perSite;
    }

    /// <summary>Errors logged from each place.</summary>
    public int PerSite { get; }

    /// <summary>True when an error from <paramref name="site"/> is logged; false (and counted) once it had its share.</summary>
    public bool Allow(string site)
    {
        ArgumentNullException.ThrowIfNull(site);
        lock (_gate)
        {
            int seen = _seen.GetValueOrDefault(site) + 1;
            _seen[site] = seen;
            if (seen <= PerSite)
            {
                return true;
            }

            _suppressed[site] = _suppressed.GetValueOrDefault(site) + 1;
            return false;
        }
    }

    /// <summary>The errors not logged since the last call, by place (in order of place), and forgets them. Each place keeps its spent share.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> TakeSuppressed()
    {
        lock (_gate)
        {
            var taken = _suppressed.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
            _suppressed.Clear();
            return taken;
        }
    }

    /// <summary>One line for the log about <paramref name="suppressed"/> (from <see cref="TakeSuppressed"/>), or "" when there are none.</summary>
    public static string Describe(IReadOnlyList<KeyValuePair<string, int>> suppressed)
    {
        ArgumentNullException.ThrowIfNull(suppressed);
        int total = suppressed.Sum(s => s.Value);
        return total == 0 ? string.Empty : $"{total} more error(s) weren't logged: {string.Join(", ", suppressed.Select(s => $"{s.Key} ({s.Value})"))}";
    }
}
