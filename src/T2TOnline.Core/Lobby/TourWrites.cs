using System;
using System.Collections.Generic;
using System.IO;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// Which save writes a check tour lets through: only its own run file (<see cref="RunFileName"/> in the tour's folder,
/// where a campaign tour points the co-op run file instead of a co-op slot) and the copies Easy Save makes while saving it
/// (<see cref="EasySaveCopies"/>), each named by its full path. A bare name (a file of the game's save folder), a file in
/// any other folder, and a write naming no file at all are refused, as is everything while the tour has no run file.
/// </summary>
public static class TourWrites
{
    /// <summary>The tour's own run file's name, in the tour's folder.</summary>
    public const string RunFileName = "tour-run.es3";

    /// <summary>The tour's own run file in <paramref name="tourFolder"/>.</summary>
    public static string RunFileIn(string tourFolder)
    {
        ArgumentNullException.ThrowIfNull(tourFolder);
        if (tourFolder.Length == 0)
        {
            throw new ArgumentException("The tour's folder is needed.", nameof(tourFolder));
        }

        return Path.Combine(tourFolder, RunFileName);
    }

    /// <summary>
    /// True when a write naming <paramref name="path"/> is a write of <paramref name="tourRunFile"/> (a full path) or of one
    /// of Easy Save's copies of it: <paramref name="path"/> must be absolute and, with one copy suffix taken off, the same
    /// file (ignoring case and the kind of slash). False for anything else, a missing tour run file included. Never throws.
    /// </summary>
    public static bool Allows(string? tourRunFile, string? path)
    {
        if (string.IsNullOrEmpty(tourRunFile) || string.IsNullOrEmpty(path) || !Path.IsPathRooted(tourRunFile) || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(full) ?? string.Empty;
            string original = Path.Combine(folder, EasySaveCopies.Original(Path.GetFileName(full)));
            return string.Equals(original, Path.GetFullPath(tourRunFile), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false; // not a path a file can have: refused
        }
    }

    /// <summary>
    /// True when a write naming <paramref name="paths"/> (each argument's path; null for an argument that names none) may go
    /// through during a tour: it names at least one file, and every file it names is the tour's run file or a copy of it
    /// (<see cref="Allows"/>). Never throws.
    /// </summary>
    public static bool AllowsWrite(string? tourRunFile, IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        bool any = false;
        foreach (string? path in paths)
        {
            if (path == null)
            {
                continue;
            }

            if (!Allows(tourRunFile, path))
            {
                return false;
            }

            any = true;
        }

        return any;
    }
}
