using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader.Utils;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>
/// The developer tools' output (D128), all under <c>UserData\T2TOnline\dev\</c>: stream recordings (<c>streams\</c>, F3 and
/// <c>RecordOnlineMatches</c>), snapshot captures (<c>captures\</c>, Ctrl+F9 and <c>SnapshotMeasure</c>) and the check tours
/// (<c>ui-check\</c>, whose screenshots can show the Steam friends list), with a <c>DO-NOT-SHARE.txt</c>. Each folder keeps
/// its newest few (<see cref="DevFileRetention"/>), pruned at start-up and after each new file. Nothing here is created on a
/// PC whose developer tools never wrote anything. Logs name files by label (<c>dev/captures/...</c>), never by path. No
/// method throws.
/// </summary>
internal static class DevOutput
{
    private const string StreamsName = "streams";
    private const string CapturesName = "captures";
    private const string ToursName = "ui-check";

    /// <summary>The mod's folder in UserData.</summary>
    public static string ModFolder => Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline");

    /// <summary><c>UserData\T2TOnline\dev</c>.</summary>
    public static string Root => Path.Combine(ModFolder, "dev");

    /// <summary>Where stream recordings go.</summary>
    public static string StreamsFolder => Path.Combine(Root, StreamsName);

    /// <summary>Where snapshot captures go.</summary>
    public static string CapturesFolder => Path.Combine(Root, CapturesName);

    /// <summary>Where the check tours' <c>run.txt</c> is read and their folders go.</summary>
    public static string ToursFolder => Path.Combine(Root, ToursName);

    /// <summary>
    /// <paramref name="path"/> as logs name it: relative to <c>UserData\T2TOnline</c> with forward slashes
    /// (<c>dev/captures/snapshots-20261105T100000Z.t2tsnap</c>), or just its file name when it's elsewhere.
    /// </summary>
    public static string Label(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        try
        {
            string root = Path.GetFullPath(ModFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full[root.Length..].Replace(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return "(a path that can't be read)";
        }
    }

    /// <summary>
    /// At start-up, before any developer tool writes: the folders 0.10.x and earlier kept directly in
    /// <c>UserData\T2TOnline</c> move under <c>dev\</c> (once: whatever is left, in use say, moves at the next start), each
    /// folder is pruned to its limits, and the notice is written when <c>dev\</c> exists.
    /// </summary>
    public static void StartUp()
    {
        MoveOldFolders();
        PruneCaptures(CapturesFolder, newest: null);
        PruneStreams(StreamsFolder, newest: null);
        PruneTours(newest: null);
        EnsureNotice();
    }

    /// <summary>After a new capture (<paramref name="newest"/>, always kept): the newest <see cref="DevFileRetention.KeptCaptures"/> stay in <paramref name="folder"/>.</summary>
    public static void PruneCaptures(string folder, string? newest) =>
        PruneFiles(folder, "*.t2tsnap", DevFileRetention.KeptCaptures, long.MaxValue, newest, ".names.txt");

    /// <summary>
    /// After a new recording (<paramref name="newest"/>, always kept): the newest <see cref="DevFileRetention.KeptStreams"/>,
    /// at most <see cref="DevFileRetention.MaxStreamBytes"/> in all, stay in <paramref name="folder"/>.
    /// </summary>
    public static void PruneStreams(string folder, string? newest) =>
        PruneFiles(folder, "*.t2tstream", DevFileRetention.KeptStreams, DevFileRetention.MaxStreamBytes, newest);

    /// <summary>After a new check tour's folder (<paramref name="newest"/>, always kept): the newest <see cref="DevFileRetention.KeptTours"/> tours stay.</summary>
    public static void PruneTours(string? newest)
    {
        string folder = ToursFolder;
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            var tours = new List<FileStamp>();
            foreach (DirectoryInfo tour in new DirectoryInfo(folder).GetDirectories())
            {
                if (DevFileRetention.TryParseTour(tour.Name, out DateTime started))
                {
                    tours.Add(new FileStamp(tour.Name, 0, started));
                }
            }

            IReadOnlyList<string> expired = DevFileRetention.Expired(tours, DevFileRetention.KeptTours, long.MaxValue, newest == null ? null : Path.GetFileName(newest));
            Remove(folder, expired, $"the newest {DevFileRetention.KeptTours} tours", name => Directory.Delete(Path.Combine(folder, name), recursive: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Developer output: couldn't tidy {Label(folder)} ({ex.GetType().Name}); tried again at the next start.");
        }
    }

    /// <summary>Writes <c>dev\DO-NOT-SHARE.txt</c> when <c>dev\</c> exists and the file is missing or different.</summary>
    public static void EnsureNotice()
    {
        string path = Path.Combine(Root, DataFolderReadme.DevNoticeFileName);
        try
        {
            if (!Directory.Exists(Root) || (File.Exists(path) && !DataFolderReadme.NeedsWrite(File.ReadAllText(path), DataFolderReadme.DevNoticeText)))
            {
                return;
            }

            string temporary = path + ModTemporaries.PartSuffix;
            File.WriteAllText(temporary, DataFolderReadme.DevNoticeText);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Developer output: couldn't write {Label(path)} ({ex.GetType().Name}).");
        }
    }

    private static void PruneFiles(string folder, string pattern, int keep, long maxBytes, string? newest, params string[] companions)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            List<FileStamp> files = new DirectoryInfo(folder).GetFiles(pattern).Select(f => new FileStamp(f.Name, f.Length, f.LastWriteTimeUtc)).ToList();
            IReadOnlyList<string> expired = DevFileRetention.Expired(files, keep, maxBytes, newest == null ? null : Path.GetFileName(newest));
            string limit = maxBytes == long.MaxValue ? $"the newest {keep}" : $"the newest {keep}, at most {maxBytes / (1024 * 1024)} MB";
            Remove(folder, expired, limit, name =>
            {
                string path = Path.Combine(folder, name);
                File.Delete(path);
                foreach (string extension in companions)
                {
                    string companion = Path.ChangeExtension(path, extension);
                    if (File.Exists(companion))
                    {
                        File.Delete(companion);
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Developer output: couldn't tidy {Label(folder)} ({ex.GetType().Name}); tried again at the next start.");
        }
    }

    /// <summary>Removes each of <paramref name="expired"/> by <paramref name="delete"/>; one line for what went, one for what couldn't.</summary>
    private static void Remove(string folder, IReadOnlyList<string> expired, string limit, Action<string> delete)
    {
        if (expired.Count == 0)
        {
            return;
        }

        var removed = new List<string>();
        string? failure = null;
        foreach (string name in expired)
        {
            try
            {
                delete(name);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure ??= ex.GetType().Name;
            }
        }

        if (removed.Count > 0)
        {
            ModLog.Info($"Developer output: {Label(folder)} keeps {limit}; removed {removed.Count} older: {DevFileRetention.NameList(removed)}.");
        }

        if (failure != null)
        {
            ModLog.Warn($"Developer output: {expired.Count - removed.Count} old item(s) in {Label(folder)} couldn't be removed ({failure}: in use?); tried again at the next start.");
        }
    }

    /// <summary>The one-time move of 0.10.x's <c>streams\</c>, <c>captures\</c> and <c>ui-check\</c> under <c>dev\</c>.</summary>
    private static void MoveOldFolders()
    {
        var movedFolders = new List<string>();
        int moved = 0;
        int left = 0;
        string? failure = null;
        foreach (string name in new[] { StreamsName, CapturesName, ToursName })
        {
            string old = Path.Combine(ModFolder, name);
            try
            {
                if (!Directory.Exists(old))
                {
                    continue;
                }

                string target = Path.Combine(Root, name);
                Directory.CreateDirectory(target);
                int movedBefore = moved;
                foreach (string entry in Directory.GetFileSystemEntries(old))
                {
                    string destination = Path.Combine(target, Path.GetFileName(entry));
                    try
                    {
                        if (File.Exists(destination) || Directory.Exists(destination))
                        {
                            left++; // a copy is in dev\ already: nothing is overwritten
                            failure ??= "already in dev/";
                        }
                        else if (Directory.Exists(entry))
                        {
                            Directory.Move(entry, destination);
                            moved++;
                        }
                        else
                        {
                            File.Move(entry, destination);
                            moved++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        left++;
                        failure ??= ex.GetType().Name;
                    }
                }

                if (Directory.GetFileSystemEntries(old).Length == 0)
                {
                    Directory.Delete(old);
                }

                if (moved > movedBefore)
                {
                    movedFolders.Add(name + "/");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failure ??= ex.GetType().Name;
                left++;
            }
        }

        if (movedFolders.Count > 0)
        {
            ModLog.Info($"Developer output: moved {string.Join(", ", movedFolders)} under dev/ ({moved} files and folders).");
        }

        if (left > 0)
        {
            ModLog.Warn($"Developer output: {left} item(s) of the old streams/, captures/ or ui-check/ stayed where they were ({failure}); moving them is tried again at the next start.");
        }
    }
}
