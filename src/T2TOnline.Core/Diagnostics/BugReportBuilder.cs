using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace T2TOnline.Core.Diagnostics;

/// <summary>What kind of log a bug report source is: each is cleaned its own way.</summary>
public enum ReportFileKind
{
    /// <summary>A session log of the mod's (<c>session-&lt;UTC&gt;.log</c>): taken only with a log format 2 header.</summary>
    SessionLog,

    /// <summary>A MelonLoader launch log (<c>yy-M-d_H-m-s.log</c>, local times): taken only when it holds a format 2 header.</summary>
    MelonLog,

    /// <summary>Unity's own log (<c>Player.log</c>, <c>Player-prev.log</c>).</summary>
    UnityLog,
}

/// <summary>A file a bug report may take, and how to open it (read with others still writing it).</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Its file name.</param>
/// <param name="Open">Opens it for reading; may throw (the file is then listed as left out).</param>
public sealed record ReportSource(ReportFileKind Kind, string Name, Func<System.IO.Stream> Open);

/// <summary>A file in a bug report.</summary>
/// <param name="Name">Its name in the zip.</param>
/// <param name="Bytes">Its size in the zip (uncompressed).</param>
/// <param name="Sha256">The SHA-256 of those bytes, 64 lowercase hex digits.</param>
/// <param name="LeftOutBytes">Bytes of the original's middle left out by the size cap (0: whole).</param>
/// <param name="What">What it is, in a few words.</param>
public sealed record ReportFileRow(string Name, long Bytes, string Sha256, long LeftOutBytes, string What);

/// <summary>A file a bug report didn't take, and why.</summary>
/// <param name="Name">Its name (as it would be in the zip when known).</param>
/// <param name="Why">Why, in a few words.</param>
public sealed record ReportLeftOut(string Name, string Why);

/// <summary>Facts about the build and the PC for a bug report's manifest, gathered by the plugin.</summary>
public sealed class BugReportFacts
{
    /// <summary>When the report is made (UTC).</summary>
    public DateTime MadeUtc { get; init; }

    /// <summary>What asked for it ("Settings > Online").</summary>
    public string Why { get; init; } = string.Empty;

    /// <summary>The mod's release (<c>0.11.0-beta</c>).</summary>
    public string Release { get; init; } = string.Empty;

    /// <summary>The mod's commit, or "".</summary>
    public string Commit { get; init; } = string.Empty;

    /// <summary>The wire protocol's version.</summary>
    public int Protocol { get; init; }

    /// <summary>The game build's label, or "" when unknown.</summary>
    public string GameBuild { get; init; } = string.Empty;

    /// <summary>MelonLoader's version.</summary>
    public string MelonLoader { get; init; } = string.Empty;

    /// <summary>The operating system's name and version.</summary>
    public string Os { get; init; } = string.Empty;

    /// <summary>The .NET runtime's name and version.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>The settings not at their defaults (names and values only), or "none".</summary>
    public string Settings { get; init; } = string.Empty;

    /// <summary>The other MelonLoader mods and plugins, each "name version".</summary>
    public IReadOnlyList<string> OtherMods { get; init; } = Array.Empty<string>();

    /// <summary>Other mod loaders' files at the game root (<see cref="BugReportFiles.OtherLoaders"/>).</summary>
    public IReadOnlyList<string> OtherLoaders { get; init; } = Array.Empty<string>();

    /// <summary>Files looked for and not there ("MelonLoader's Logs folder").</summary>
    public IReadOnlyList<string> NotFound { get; init; } = Array.Empty<string>();

    /// <summary>Files <see cref="ReportAllowList"/> refused where a log was looked for ("2 file(s) in the session logs folder").</summary>
    public IReadOnlyList<string> Refused { get; init; } = Array.Empty<string>();

    /// <summary>
    /// This launch's session log's file name (<c>session-20261105T050000Z.log</c>), or "" when this launch has none (its file
    /// couldn't be made): only that file is called this launch's in the manifest.
    /// </summary>
    public string ThisLaunchLog { get; init; } = string.Empty;
}

/// <summary>What went into a bug report: its files, what was left out and what was cleaned.</summary>
public sealed class BugReportResult
{
    /// <summary>The files in it, in the zip's order (the manifest's own row is not one).</summary>
    public List<ReportFileRow> Files { get; } = new();

    /// <summary>The files left out, and why.</summary>
    public List<ReportLeftOut> LeftOut { get; } = new();

    /// <summary>What the scrubber replaced, in every file.</summary>
    public LogScrubCounts Cleaned { get; } = new();

    /// <summary>MelonLoader time stamps converted from local time to UTC.</summary>
    public int MelonTimes { get; set; }

    /// <summary>Unity's graphics card lines reduced.</summary>
    public int GraphicsLines { get; set; }

    /// <summary>Names in the game's own lines (a skater's, a team's, a logo's: player-written in a custom team) reduced to their shape (<see cref="GameLogText"/>).</summary>
    public int GameNames { get; set; }

    /// <summary>This PC's tags per lobby, from the session logs' <c>Log tags: this PC is ...</c> lines ("file: this PC is ...").</summary>
    public List<string> Tags { get; } = new();

    /// <summary>The marks in the session logs ("file hh:mm:ss UTC: MARK n (in a match)").</summary>
    public List<string> Marks { get; } = new();

    /// <summary>The text in the report's files, in bytes.</summary>
    public long TextBytes { get; set; }

    /// <summary>The manifest's size in bytes.</summary>
    public long ManifestBytes { get; set; }
}

/// <summary>
/// Writes a bug report (D127): one zip, safe to post in public, with the newest <see cref="BugReportFiles.SessionLogs"/>
/// session logs that have a log format 2 header, MelonLoader's newest <see cref="BugReportFiles.MelonLogs"/> launch logs
/// (each left out unless it holds a format 2 header: older builds wrote Steam names into it, which no scrubber can know),
/// their times converted to UTC and named by UTC, Unity's two logs with the graphics card reduced to its maker, every line
/// through <see cref="LogScrub"/> and the game's own lines' names through <see cref="GameLogText"/>, and <c>report.txt</c>
/// (<see cref="BugReportManifest"/>) last. Files are taken in a
/// priority order (this launch's first) under <see cref="BugReportFiles.MaxFileBytes"/> each and
/// <see cref="BugReportFiles.MaxReportBytes"/> in all. Each entry is dated the report's UTC time: a zip stores a wall time
/// with no zone, so the player's local time never goes in. A file that can't be read is listed as left out; a failure to
/// write the zip throws.
/// </summary>
public static class BugReportBuilder
{
    /// <summary>The manifest's name in the zip.</summary>
    public const string ManifestName = "report.txt";

    private static readonly Regex TagsLine = new(@"this PC is (P#[0-9a-f?]{6,8}) in lobby (L#[0-9a-f]{6,8}), host (P#[0-9a-f?]{6,8})", RegexOptions.CultureInvariant);
    private static readonly Regex MarkLine = new(@"^(\d\d:\d\d:\d\d)\.\d{3} INFO +MARK (\d{1,6}) \((in a match|in a menu|at the title)\)", RegexOptions.CultureInvariant);

    /// <summary>The most tag and mark lines the manifest lists (a session that sets hundreds is cut).</summary>
    private const int MaxListed = 40;

    /// <summary>
    /// Writes the report's zip to <paramref name="output"/> (left open). <paramref name="sessionLogs"/> and
    /// <paramref name="melonLogs"/> newest first; <paramref name="unityLogs"/> this launch's first. <paramref name="textShape"/>
    /// writes player-written text's shape (the mod's per-launch <see cref="PlayerTextShape"/>, so a name reads the same as in
    /// this launch's session log; a new key when null).
    /// </summary>
    public static BugReportResult Write(System.IO.Stream output, BugReportFacts facts, IReadOnlyList<ReportSource> sessionLogs, IReadOnlyList<ReportSource> melonLogs,
        IReadOnlyList<ReportSource> unityLogs, TimeZoneInfo zone, LogScrub scrub, Func<string?, string>? textShape = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(sessionLogs);
        ArgumentNullException.ThrowIfNull(melonLogs);
        ArgumentNullException.ThrowIfNull(unityLogs);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(scrub);

        var result = new BugReportResult();
        DateTimeOffset stamp = new(DateTime.SpecifyKind(facts.MadeUtc, DateTimeKind.Unspecified), TimeSpan.Zero);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var state = new State(zip, stamp, zone, scrub, textShape ?? PlayerTextShape.ForLaunch().Describe, result, facts.ThisLaunchLog ?? string.Empty);
            int session = 0;
            int sessionsTaken = 0;
            int melon = 0;

            // Priority: this launch's three logs, then the previous launch's, then the third session log.
            TakeSession(state, sessionLogs, ref session, ref sessionsTaken);
            TakeMelon(state, melonLogs, ref melon);
            TakeUnity(state, unityLogs, 0);
            TakeSession(state, sessionLogs, ref session, ref sessionsTaken);
            TakeMelon(state, melonLogs, ref melon);
            TakeUnity(state, unityLogs, 1);
            TakeSession(state, sessionLogs, ref session, ref sessionsTaken);

            // The manifest's facts are names and numbers, through the scrubber all the same (a backstop, as for every line).
            string manifest = scrub.Scrub(BugReportManifest.Text(facts, result));
            byte[] bytes = new UTF8Encoding(false).GetBytes(manifest);
            result.ManifestBytes = bytes.Length;
            WriteEntry(state, ManifestName, bytes);
        }

        return result;
    }

    private static void TakeSession(State state, IReadOnlyList<ReportSource> sources, ref int next, ref int taken)
    {
        while (next < sources.Count && taken < BugReportFiles.SessionLogs)
        {
            ReportSource source = sources[next++];
            bool thisLaunch = state.ThisLaunchLog.Length > 0 && string.Equals(source.Name, state.ThisLaunchLog, StringComparison.OrdinalIgnoreCase);
            if (Take(state, source, source.Name, thisLaunch ? "this launch's session log" : "an earlier launch's session log"))
            {
                taken++;
                return;
            }
        }
    }

    private static void TakeMelon(State state, IReadOnlyList<ReportSource> sources, ref int next)
    {
        if (next >= sources.Count || next >= BugReportFiles.MelonLogs)
        {
            return;
        }

        ReportSource source = sources[next];
        bool current = next == 0;
        next++;
        if (!MelonLogTimes.TryParseName(source.Name, out DateTime localStart))
        {
            state.Result.LeftOut.Add(new ReportLeftOut("a MelonLoader log", "its name isn't MelonLoader's, so its local times couldn't be converted to UTC"));
            return;
        }

        MelonLogTimes clock;
        try
        {
            clock = new MelonLogTimes(state.Zone, localStart);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidTimeZoneException)
        {
            state.Result.LeftOut.Add(new ReportLeftOut("a MelonLoader log", "its start time couldn't be converted to UTC"));
            return;
        }

        string name = MelonLogTimes.UtcName(clock.StartUtc);
        string what = string.Create(CultureInfo.InvariantCulture, $"MelonLoader's log of {(current ? "this launch" : "the launch before")}, times in UTC");
        _ = Take(state, source, name, what, clock);
    }

    private static void TakeUnity(State state, IReadOnlyList<ReportSource> sources, int index)
    {
        if (index < sources.Count)
        {
            ReportSource source = sources[index];
            _ = Take(state, source, source.Name, index == 0 ? "Unity's log of this launch" : "Unity's log of the launch before");
        }
    }

    /// <summary>Reads, cleans and adds one file; false when it was left out (said in the result).</summary>
    private static bool Take(State state, ReportSource source, string name, string what, MelonLogTimes? clock = null)
    {
        BugReportResult result = state.Result;
        long room = BugReportFiles.MaxReportBytes - result.TextBytes;
        if (room < BugReportFiles.MinFileBytes)
        {
            result.LeftOut.Add(new ReportLeftOut(name, "the report reached its size limit"));
            return false;
        }

        ReportExcerpt excerpt;
        try
        {
            using System.IO.Stream stream = source.Open();
            excerpt = ReportExcerpts.Read(stream, Math.Min(BugReportFiles.MaxFileBytes, room));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
        {
            result.LeftOut.Add(new ReportLeftOut(name, ex is FileNotFoundException or DirectoryNotFoundException ? "not found"
                : $"couldn't be read ({SafeError.Text(ex, state.Scrub)})"));
            return false;
        }

        string[] lines = excerpt.Text.Split('\n');
        if (source.Kind == ReportFileKind.SessionLog && LogHeader.FormatOf(lines[0]) < LogHeader.Format)
        {
            result.LeftOut.Add(new ReportLeftOut(name, $"written before log format {LogHeader.Format} (older builds wrote Steam names and IDs into their logs)"));
            return false;
        }

        if (source.Kind == ReportFileKind.MelonLog && !HoldsHeader(lines))
        {
            result.LeftOut.Add(new ReportLeftOut(name, $"written before log format {LogHeader.Format} (T2T Online didn't start in it, or an older build wrote Steam names into it)"));
            return false;
        }

        var cleaned = new StringBuilder(excerpt.Text.Length + 256);
        var counts = new LogScrubCounts();
        int times = 0;
        int graphics = 0;
        int names = 0;
        var tags = new List<string>();
        var marks = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool cr = line.Length > 0 && line[^1] == '\r';
            if (cr)
            {
                line = line[..^1];
            }

            if (clock != null)
            {
                int before = clock.Converted;
                line = clock.Convert(line);
                times += clock.Converted - before;
            }

            line = state.Scrub.Scrub(line, counts);
            line = GameLogText.Reduce(line, state.TextShape, out int gameNames);
            names += gameNames;
            if (source.Kind == ReportFileKind.UnityLog)
            {
                line = UnityLogText.Reduce(line, out bool reduced);
                graphics += reduced ? 1 : 0;
            }
            else if (source.Kind == ReportFileKind.SessionLog)
            {
                Notice(line, name, tags, marks);
            }

            cleaned.Append(line);
            if (cr)
            {
                cleaned.Append('\r');
            }

            if (i < lines.Length - 1)
            {
                cleaned.Append('\n');
            }
        }

        byte[] bytes = new UTF8Encoding(false).GetBytes(cleaned.ToString());
        string sha = WriteEntry(state, name, bytes);
        result.Files.Add(new ReportFileRow(name, bytes.Length, sha, excerpt.LeftOutBytes, what));
        result.TextBytes += bytes.Length;
        Add(result.Cleaned, counts);
        result.MelonTimes += times;
        result.GraphicsLines += graphics;
        result.GameNames += names;
        AddListed(result.Tags, tags);
        AddListed(result.Marks, marks);
        return true;
    }

    /// <summary>True when a MelonLoader log holds a log format 2 (or later) header of T2T Online's.</summary>
    private static bool HoldsHeader(string[] lines)
    {
        foreach (string line in lines)
        {
            if (line.Contains("T2T Online ", StringComparison.Ordinal) && LogHeader.FormatOf(line) >= LogHeader.Format)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A session log's line that says this PC's tags or a mark, for the manifest.</summary>
    private static void Notice(string line, string file, List<string> tags, List<string> marks)
    {
        if (line.Contains("this PC is P#", StringComparison.Ordinal) && TagsLine.Match(line) is { Success: true } tag)
        {
            string entry = $"{file}: this PC is {tag.Groups[1].Value} in lobby {tag.Groups[2].Value}, host {tag.Groups[3].Value}";
            if (!tags.Contains(entry))
            {
                tags.Add(entry);
            }
        }
        else if (line.Contains(BugMarks.Prefix, StringComparison.Ordinal) && MarkLine.Match(line) is { Success: true } mark)
        {
            marks.Add($"{file} {mark.Groups[1].Value} UTC: MARK {mark.Groups[2].Value} ({mark.Groups[3].Value})");
        }
    }

    private static void AddListed(List<string> into, List<string> found)
    {
        foreach (string item in found)
        {
            if (into.Count >= MaxListed)
            {
                return;
            }

            if (!into.Contains(item))
            {
                into.Add(item);
            }
        }
    }

    private static void Add(LogScrubCounts total, LogScrubCounts counts)
    {
        total.SteamIds += counts.SteamIds;
        total.Lobbies += counts.Lobbies;
        total.Names += counts.Names;
        total.Folders += counts.Folders;
        total.Addresses += counts.Addresses;
        total.Offsets += counts.Offsets;
        total.Controls += counts.Controls;
        total.Keys += counts.Keys;
        total.Texts += counts.Texts;
    }

    /// <summary>Adds an entry holding <paramref name="bytes"/>; returns their SHA-256. Throws when the zip can't be written.</summary>
    private static string WriteEntry(State state, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = state.Zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = state.Stamp;
        using (System.IO.Stream stream = entry.Open())
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record State(ZipArchive Zip, DateTimeOffset Stamp, TimeZoneInfo Zone, LogScrub Scrub, Func<string?, string> TextShape, BugReportResult Result,
        string ThisLaunchLog);
}
