using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Diagnostics;

/// <summary>A file a bug report may read, as a folder listing gives it.</summary>
/// <param name="Name">The file's name.</param>
/// <param name="WrittenUtc">When it was last written (UTC).</param>
public sealed record ReportCandidate(string Name, DateTime WrittenUtc);

/// <summary>Where a report is written (<see cref="BugReportFiles.Destination"/>).</summary>
/// <param name="Folder">The folder the zip goes in.</param>
/// <param name="Prunes">True when the folder's older reports are deleted down to the newest <see cref="BugReportFiles.Kept"/> once it's made.</param>
/// <param name="ForTour">True when it's a check tour's own folder.</param>
public readonly record struct ReportDestination(string Folder, bool Prunes, bool ForTour);

/// <summary>
/// Which files a bug report reads and keeps (D127, D128): the report's own name, the newest session logs and MelonLoader
/// launch logs (newest first; the builder takes the first that qualify), the reports kept, and other mod loaders' files
/// at the game root. Only these: a report never reads saves, backups, co-op slots, settings, developer output or crash
/// dumps.
/// </summary>
public static class BugReportFiles
{
    /// <summary>The folder under <c>UserData\T2TOnline</c> that holds the reports.</summary>
    public const string FolderName = "reports";

    /// <summary>Reports kept, the newest first; older ones are deleted when a new one is made.</summary>
    public const int Kept = 5;

    /// <summary>Session logs in a report.</summary>
    public const int SessionLogs = 3;

    /// <summary>MelonLoader launch logs in a report (the running launch's is the newest).</summary>
    public const int MelonLogs = 2;

    /// <summary>The most of one file a report keeps (its start and end: <see cref="ReportExcerpts"/>).</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024;

    /// <summary>The most text a report holds in all: files past it are left out (and listed).</summary>
    public const long MaxReportBytes = 16L * 1024 * 1024;

    /// <summary>A file isn't worth adding with less room than this left (it's listed as left out instead).</summary>
    public const long MinFileBytes = 64L * 1024;

    private const string Prefix = "T2TOnline-report-";
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    // DLLs a mod loader or overlay puts beside a game's exe to be loaded with it (MelonLoader's own is version.dll).
    private static readonly string[] ProxyDlls =
    {
        "winhttp.dll", "winmm.dll", "dinput8.dll", "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll", "dsound.dll",
        "ddraw.dll", "wininet.dll", "iphlpapi.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll", "msacm32.dll", "dbghelp.dll",
    };

    // Other loaders' own files and folders at a game's root.
    private static readonly string[] LoaderFiles = { "BepInEx", "doorstop_config.ini", ".doorstop_version", "dotnet", "ReShade.ini" };

    /// <summary>
    /// Where a report goes (D127, D143): the player's reports folder (<paramref name="playerReports"/>), pruned to the newest <see cref="Kept"/>; or,
    /// while a check tour holds the player's data (<paramref name="tourHolds"/>), the tour's own folder's <see cref="FolderName"/> subfolder
    /// (<paramref name="tourFolder"/>), pruned with the tour. During a tour nothing prunes the player's folder, so a tour never deletes a report of
    /// the player's; a tour with no folder of its own (it has always one) writes to the player's folder without pruning it.
    /// </summary>
    public static ReportDestination Destination(string playerReports, bool tourHolds, string? tourFolder)
    {
        ArgumentNullException.ThrowIfNull(playerReports);
        if (!tourHolds)
        {
            return new ReportDestination(playerReports, Prunes: true, ForTour: false);
        }

        return string.IsNullOrEmpty(tourFolder)
            ? new ReportDestination(playerReports, Prunes: false, ForTour: false)
            : new ReportDestination(System.IO.Path.Combine(tourFolder, FolderName), Prunes: false, ForTour: true);
    }

    /// <summary>The name of a report made at <paramref name="utc"/>: <c>T2TOnline-report-20261105T052200Z.zip</c>.</summary>
    public static string ReportName(DateTime utc) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{utc:yyyyMMdd'T'HHmmss'Z'}.zip");

    /// <summary>True for a report's name (<see cref="ReportName"/>).</summary>
    public static bool IsReportName(string? name) => StampOfReport(name) != null;

    /// <summary>
    /// The reports to delete once a new one is made, so the newest <see cref="Kept"/> stay (the new one among
    /// <paramref name="names"/>); names that aren't reports' are never touched.
    /// </summary>
    public static IReadOnlyList<string> ReportsToDelete(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Where(IsReportName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => StampOfReport(n)).ThenByDescending(n => n, StringComparer.Ordinal)
            .Skip(Kept).ToList();
    }

    /// <summary>The session logs to try, newest first (by the UTC time in their names, else their write time).</summary>
    public static IReadOnlyList<ReportCandidate> SessionLogsNewestFirst(IEnumerable<ReportCandidate> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Where(f => f != null && f.Name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => LogRetention.StampOf(new SessionLogFile(f.Name, 0, f.WrittenUtc)))
            .ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// MelonLoader's launch logs to try, newest first: by their last write (UTC: the running launch's is being written), then
    /// by the local start time in their names.
    /// </summary>
    public static IReadOnlyList<ReportCandidate> MelonLogsNewestFirst(IEnumerable<ReportCandidate> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Where(f => f != null && f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.WrittenUtc)
            .ThenByDescending(f => MelonLogTimes.TryParseName(f.Name, out DateTime start) ? start : DateTime.MinValue)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Other mod loaders' (and overlay injectors') files among a game root's entries: a <c>BepInEx</c> folder, Doorstop's
    /// files, a second proxy DLL. MelonLoader's own (<c>version.dll</c>, <c>MelonLoader</c>) aren't listed. Sorted.
    /// </summary>
    public static IReadOnlyList<string> OtherLoaders(IEnumerable<string> rootEntries)
    {
        ArgumentNullException.ThrowIfNull(rootEntries);
        var known = new HashSet<string>(ProxyDlls.Concat(LoaderFiles), StringComparer.OrdinalIgnoreCase);
        return rootEntries.Where(e => !string.IsNullOrEmpty(e) && known.Contains(e))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DateTime? StampOfReport(string? name)
    {
        if (name == null || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.Length != Prefix.Length + 16 + 4)
        {
            return null;
        }

        return DateTime.TryParseExact(name.AsSpan(Prefix.Length, 16), StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime stamp) ? stamp : null;
    }
}
