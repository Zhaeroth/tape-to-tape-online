#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// The evidence that Unity's own analytics never ran: it keeps its state in the game's save folder, under
/// <c>Unity\&lt;project id&gt;\Analytics\</c> (<c>values</c>, <c>config</c>, <c>ArchivedEvents\</c>) and
/// <c>Unity\ShaderVariantAnalytics\</c>, and writes them whenever it starts a session or queues an event. Versions
/// before the byte-typed switch-off (up to v0.5.3) still let them be written; with it they stay unwritten, even though
/// the log reads the engine's session tracker as "started" at Unity's start-up callbacks: the files are what
/// tells. <see cref="Note"/> reads them once T2T Online is up; <see cref="Recheck"/> (every 30 s) and <see cref="Check"/>
/// (as the game closes) compare. Only names relative to that folder are logged, never the Windows user name.
/// </summary>
internal static class AnalyticsFiles
{
    private const int MaxFiles = 500;

    private static string? _folder;
    private static List<FileStamp>? _start;
    private static bool _warned;

    /// <summary>Reads the analytics files and logs when they were last written. Never throws.</summary>
    public static void Note()
    {
        try
        {
            _folder = Path.Combine(UnityEngine.Application.persistentDataPath, "Unity");
            _start = Read(_folder);
            DateTime? newest = FileStamps.Newest(_start);
            if (newest == null)
            {
                ModLog.Info("Housekeeping check: Unity's analytics has no local files on this PC (it has never run here).");
                return;
            }

            DateTime gameStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            if (newest >= gameStarted)
            {
                ModLog.Warn($"Housekeeping: Unity's analytics wrote its local files after the game started, before T2T Online could check " +
                    string.Create(CultureInfo.InvariantCulture, $"({newest:yyyy-MM-dd HH:mm:ss} UTC): the engine's analytics ran at start-up."));
                return;
            }

            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Housekeeping check: Unity's analytics keeps {_start.Count} local file(s), last written {newest:yyyy-MM-dd HH:mm} UTC, ") +
                "before this game started; checked again every 30 s and as the game closes.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Housekeeping: Unity's analytics files couldn't be read ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Every 30 s: a WARN the first time the files changed since <see cref="Note"/>. Never throws.</summary>
    public static void Recheck()
    {
        if (_warned)
        {
            return;
        }

        IReadOnlyList<string>? changed = Changed();
        if (changed is { Count: > 0 })
        {
            _warned = true;
            ModLog.Warn($"Housekeeping: Unity's analytics wrote its local files during this session, so it ran: {string.Join(", ", changed)}.");
        }
    }

    /// <summary>As the game closes: says whether the files changed this session. Never throws.</summary>
    public static void Check()
    {
        IReadOnlyList<string>? changed = Changed();
        if (changed == null)
        {
            return;
        }

        if (changed.Count == 0)
        {
            ModLog.Info("Housekeeping check: Unity's analytics wrote nothing this session (its local files are unchanged).");
        }
        else
        {
            ModLog.Warn($"Housekeeping: Unity's analytics wrote its local files during this session, so it ran: {string.Join(", ", changed)}.");
        }
    }

    private static IReadOnlyList<string>? Changed()
    {
        if (_folder == null || _start == null)
        {
            return null;
        }

        try
        {
            return FileStamps.Changed(_start, Read(_folder));
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Housekeeping: Unity's analytics files couldn't be read again ({ModLog.SafeErrorText(ex)}).");
            return null;
        }
    }

    private static List<FileStamp> Read(string folder)
    {
        var stamps = new List<FileStamp>();
        if (!Directory.Exists(folder))
        {
            return stamps;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            stamps.Add(new FileStamp(Path.GetRelativePath(folder, file), info.Length, info.LastWriteTimeUtc));
            if (stamps.Count >= MaxFiles)
            {
                break;
            }
        }

        return stamps;
    }
}
#endif
