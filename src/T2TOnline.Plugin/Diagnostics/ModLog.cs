using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>
/// Writes to the MelonLoader console and to our own per-session log file under UserData/T2TOnline/logs,
/// so a bug report is one file. Never throws: logging must not be able to break the game.
/// </summary>
/// <remarks>
/// <para>
/// Every line passes through <see cref="Scrub"/> before either sink (D127), so neither the session file nor MelonLoader's
/// own log holds a Steam name or ID, a lobby ID, a folder path, an IP address or a time zone: players and lobbies appear
/// as tags (<see cref="LogTags"/>), folders as labels. The first line of each file is the header
/// (<see cref="LogHeader"/>): the build, the protocol, the log format and the UTC date; the date is written again when a
/// session goes past midnight UTC.
/// </para>
/// <para>
/// Bounded, so a flood (a failure repeating every frame) can't fill the disk or stall the game on console writes: each
/// error or warning message is written in full only its first <see cref="RepeatsInFull"/> times, then counted, with a
/// line saying how many at most once a minute while it keeps coming (and at the end of the session); a session file
/// stops at <see cref="LogRetention.MaxFileBytes"/> with a line saying so, after which only errors still reach the
/// console, within the same budget. Old session files go at start-up by <see cref="LogRetention"/> (D128), and files
/// from builds before log format 2 (which held Steam names and IDs) all go at the first start of format 2.
/// </para>
/// </remarks>
internal static class ModLog
{
    /// <summary>How many times one error or warning message is written in full before it's only counted.</summary>
    private const int RepeatsInFull = 5;

    /// <summary>The shortest time between two "N more" lines of one message.</summary>
    private const long RepeatSummaryEveryMs = 60_000;

    /// <summary>Messages counted one by one, per level; any beyond share one count.</summary>
    private const int RepeatKeys = 256;

    private static readonly object Gate = new();
    private static readonly RepeatLimiter Errors = new(RepeatsInFull, RepeatSummaryEveryMs, RepeatKeys);
    private static readonly RepeatLimiter Warnings = new(RepeatsInFull, RepeatSummaryEveryMs, RepeatKeys);
    private static MelonLogger.Instance? _melon;
    private static StreamWriter? _file;
    private static long _fileBytes;
    private static long _droppedLines;
    private static DateTime _fileDay;

    // Set once the session file hit its limit: from then on only errors are logged (to the console).
    private static volatile bool _capped;

    /// <summary>This launch's own tags: outside a lobby, and in a lobby whose host published no key.</summary>
    public static LogTags LaunchTags { get; } = LogTags.ForLaunch();

    /// <summary>Every <see cref="Error"/> counted, written in full or only counted: the check tour reads how many came since it began.</summary>
    public static ErrorTally ErrorCount { get; } = new();

    /// <summary>The backstop every line passes through; the session sets its tags and registers names.</summary>
    public static LogScrub Scrub { get; } = new(LaunchTags);

    /// <summary>Path of this session's log file, once opened. Never logged: <see cref="FileName"/> is.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>This session's log file's name (<c>session-20261105T100000Z.log</c>), or "(none)".</summary>
    public static string FileName => FilePath == null ? "(none)" : Path.GetFileName(FilePath);

    /// <summary>
    /// Starts logging to the console instance and a new session file in <paramref name="logDirectory"/>, after the old
    /// files' clean-up; the first line is the header. <paramref name="gameRoot"/> and the user's folders become labels.
    /// </summary>
    public static void Open(MelonLogger.Instance melon, string logDirectory, string gameRoot)
    {
        _melon = melon;
        string userProfile = KnownFolders(gameRoot);
        int oldFormat = 0;
        List<KeyValuePair<SessionLogFile, LogRemoval>> pruned = new();
        try
        {
            Directory.CreateDirectory(logDirectory);
            oldFormat = DeleteOldFormats(logDirectory);
            pruned = Prune(logDirectory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            FilePath = Path.Combine(logDirectory, $"session-{stamp}.log");
            _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToConsole("WARN", $"Could not open the T2T Online log file: {SafeError.Text(ex, Scrub)}");
        }

        DateTime now = DateTime.UtcNow;
        _fileDay = now.Date;
        Info(LogHeader.Line(ModVersion.Release, ModVersion.Commit, ProtocolInfo.Version, now));
        Info(GameFolderShape.Describe(gameRoot, userProfile, DriveKind(gameRoot)));
        if (oldFormat > 0)
        {
            Info($"Session logs: deleted {oldFormat} written by T2T Online builds before log format {LogHeader.Format} " +
                "(they named players by their Steam names and IDs; from this build on, players are tags).");
        }

        if (pruned.Count > 0)
        {
            Info(LogRetention.Describe(pruned));
        }
    }

    /// <summary>
    /// The settings line of the header: <paramref name="summary"/> says which settings aren't at their defaults (names
    /// and values only). Written once, after the settings load.
    /// </summary>
    public static void WriteSettings(Func<string>? summary)
    {
        string text;
        try
        {
            text = summary?.Invoke() ?? string.Empty;
        }
        catch (Exception ex)
        {
            text = $"(couldn't be listed: {SafeError.Text(ex, Scrub)})";
        }

        Info(summary == null ? "Settings: this build doesn't list them."
            : text.Length == 0 ? "Settings: all at their defaults."
            : $"Settings not at their defaults: {text}");
    }

    /// <summary>Folders <see cref="Scrub"/> replaces with labels, from a part that knows more of them (the game's save folder).</summary>
    public static void AddFolders(IEnumerable<KeyValuePair<string, string>> folders)
    {
        try
        {
            Scrub.AddFolders(folders);
        }
        catch (Exception ex)
        {
            Warn($"Log: folders to label couldn't be added ({ex.GetType().Name}).");
        }
    }

    /// <summary>Registers a Steam name, so the backstop replaces it with its player's tag (<see cref="LogScrub.RegisterName"/>). Never throws.</summary>
    public static void RegisterName(ulong steamId, string? name)
    {
        try
        {
            Scrub.RegisterName(steamId, name);
        }
        catch (Exception)
        {
            // A name that can't be registered is only a weaker backstop: nothing to stop for.
        }
    }

    /// <summary>An exception as one safe line: type and message, folders as labels (<see cref="SafeError"/>).</summary>
    public static string SafeErrorText(Exception? exception) => SafeError.Text(exception, Scrub);

    /// <summary>Informational message.</summary>
    public static void Info(string message)
    {
        if (_capped)
        {
            CountDropped();
            return;
        }

        string line = Scrub.Scrub(message);
        ToConsole("INFO", line);
        WriteFile("INFO", line);
    }

    /// <summary>
    /// A line from the Steam library (<c>T2TOnline.Steam</c>), which writes what other players' Steam data says: every
    /// control character, line breaks included, is neutralised first, so it can't forge a line.
    /// </summary>
    public static void SteamInfo(string message) => Info(LogScrub.NeutraliseControls(message ?? string.Empty, keepLineBreaks: false));

    /// <summary>A warning from the Steam library, cleaned as <see cref="SteamInfo"/> is.</summary>
    public static void SteamWarn(string message) => Warn(LogScrub.NeutraliseControls(message ?? string.Empty, keepLineBreaks: false));

    /// <summary>
    /// A breadcrumb for the session file only (not the console): written before a step that could take the game down
    /// natively, so the file's last line names it. The file flushes every line.
    /// </summary>
    public static void Trace(string message) => WriteFile("TRACE", Scrub.Scrub(message));

    /// <summary>
    /// Something unexpected that the mod handled. The same <paramref name="message"/> is written in full only its first
    /// few times; after that it's counted, and a line says how many at most once a minute (see the remarks).
    /// </summary>
    public static void Warn(string message)
    {
        if (_capped)
        {
            CountDropped();
            return;
        }

        string line = Scrub.Scrub(message);
        if (Limit(Warnings, line, out int counted, out bool lastInFull) is not { } decision)
        {
            return;
        }

        string text = decision == RepeatDecision.Write
            ? line + (lastInFull ? " (this warning is only counted from now on: a line about once a minute says how many more)" : string.Empty)
            : $"{line}: {counted} more time{(counted == 1 ? string.Empty : "s")} since the last line about it.";
        ToConsole("WARN", text);
        WriteFile("WARN", text);
    }

    /// <summary>
    /// A failure, with the exception if there is one. The same <paramref name="message"/> is written in full only its
    /// first few times; after that it's counted, and a line says how many at most once a minute (see the remarks).
    /// </summary>
    public static void Error(string message, Exception? exception = null)
    {
        string line = Scrub.Scrub(message ?? string.Empty);
        ErrorCount.Note(line);
        if (Limit(Errors, line, out int counted, out bool lastInFull) is not { } decision)
        {
            return;
        }

        string text = decision == RepeatDecision.Write
            ? (exception == null ? line : $"{line}: {Scrub.Scrub(exception.ToString())}") +
              (lastInFull ? " (this error is only counted from now on: a line about once a minute says how many more)" : string.Empty)
            : $"{line}: {counted} more time{(counted == 1 ? string.Empty : "s")} since the last line about it" +
              (exception == null ? "." : $" (the newest: {SafeErrorText(exception)}).");
        ToConsole("ERROR", text);
        WriteFile("ERROR", text);
    }

    /// <summary>Flushes and closes the session file, first saying how many errors and warnings were only counted.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            foreach ((RepeatLimiter limiter, string level) in new[] { (Errors, "ERROR"), (Warnings, "WARN") })
            {
                foreach (KeyValuePair<string, int> pending in limiter.Drain())
                {
                    string line = $"{pending.Key}: {pending.Value} more time{(pending.Value == 1 ? string.Empty : "s")} before the session ended (counted, not written one by one).";
                    ToConsole(level, line);
                    WriteFile(level, line);
                }
            }

            if (_capped && _droppedLines > 0)
            {
                ToConsole("WARN", $"T2T Online: {_droppedLines} log lines were dropped after the session log reached its size limit.");
            }

            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>What <paramref name="limiter"/> says about one more <paramref name="line"/>; null when it's only counted.</summary>
    private static RepeatDecision? Limit(RepeatLimiter limiter, string line, out int counted, out bool lastInFull)
    {
        lock (Gate)
        {
            RepeatDecision decision = limiter.Next(line, Environment.TickCount64, out counted);
            lastInFull = decision == RepeatDecision.Write && limiter.Remaining(line) == 0;
            return decision == RepeatDecision.Count ? null : decision;
        }
    }

    private static void WriteFile(string level, string message)
    {
        lock (Gate)
        {
            if (_file == null)
            {
                return;
            }

            try
            {
                DateTime now = DateTime.UtcNow;
                if (now.Date != _fileDay)
                {
                    _fileDay = now.Date;
                    _file.WriteLine($"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} INFO  {LogHeader.DateLine(now)}");
                }

                string line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level,-5} {message}";
                long bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_fileBytes + bytes > LogRetention.MaxFileBytes)
                {
                    _capped = true;
                    _file.WriteLine($"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} WARN  " +
                        $"Session log cut at {LogRetention.MaxFileBytes / (1024 * 1024)} MB: nothing more is written to this file, and only errors still go to the MelonLoader console (each written in full a few times, then counted).");
                    _file.Dispose();
                    _file = null;
                    ToConsole("WARN", $"T2T Online: the session log reached {LogRetention.MaxFileBytes / (1024 * 1024)} MB and was cut; from now on only errors are logged, to this console.");
                    return;
                }

                _file.WriteLine(line);
                _fileBytes += bytes;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _file = null;
            }
        }
    }

    /// <summary>
    /// <paramref name="text"/> to MelonLoader's console and its log at <paramref name="level"/> (<c>INFO</c>, <c>WARN</c>,
    /// <c>ERROR</c>). MelonLoader then runs every other mod's log callback (its <c>MelonLogger</c> events) without a guard, so
    /// whatever one of those throws stops here: the line still goes to the session file, and nothing reaches the caller,
    /// which is often a game callback.
    /// </summary>
    private static void ToConsole(string level, string text)
    {
        MelonLogger.Instance? melon = _melon;
        if (melon == null)
        {
            return;
        }

        try
        {
            switch (level)
            {
                case "ERROR":
                    melon.Error(text);
                    break;
                case "WARN":
                    melon.Warning(text);
                    break;
                default:
                    melon.Msg(text);
                    break;
            }
        }
        catch (Exception)
        {
            // Another mod's log callback failed: the session file has the line all the same.
        }
    }

    private static void CountDropped()
    {
        lock (Gate)
        {
            _droppedLines++;
        }
    }

    /// <summary>
    /// Gives <see cref="Scrub"/> the folders known before the game's own code runs (no interop here: this runs before
    /// MelonLoader's generator): the game folder, its Steam library, Steam's own folder, the mod's data folder, local
    /// application data and the user's profile. Returns the profile folder ("" when unknown).
    /// </summary>
    private static string KnownFolders(string gameRoot)
    {
        var folders = new List<KeyValuePair<string, string>>();
        string profile = string.Empty;
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            folders.Add(new(gameRoot, "<game folder>"));
            folders.Add(new(SteamLibraryOf(gameRoot), "<Steam library>"));
            folders.Add(new(SteamFolder(), "<Steam>"));
            folders.Add(new(string.IsNullOrEmpty(local) ? string.Empty : Path.Combine(local, "T2T Online"), @"%LOCALAPPDATA%\T2T Online"));
            folders.Add(new(local, "%LOCALAPPDATA%"));
            folders.Add(new(profile, "%USERPROFILE%"));
            Scrub.AddFolders(folders);
        }
        catch (Exception)
        {
            // Any user profile still loses its user name (LogScrub's catch-all for X:\Users\<name>).
        }

        return profile;
    }

    /// <summary>The Steam library a game folder is in (the folder above <c>steamapps</c>), or "" when it isn't in one.</summary>
    private static string SteamLibraryOf(string gameRoot)
    {
        string path = (gameRoot ?? string.Empty).Replace('/', '\\');
        int at = path.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? path[..at] : string.Empty;
    }

    /// <summary>Steam's own folder, as Steam records it for this Windows user; "" when it can't be read.</summary>
    private static string SteamFolder()
    {
        try
        {
            if (OperatingSystem.IsWindows() &&
                Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string path)
            {
                return path.Replace('/', '\\');
            }
        }
        catch (Exception)
        {
            // Not there, or not readable: the library and the profile still cover the usual places.
        }

        return string.Empty;
    }

    /// <summary>The kind of drive the game is on ("fixed", "removable", "network"), or null when it can't be told.</summary>
    private static string? DriveKind(string gameRoot)
    {
        try
        {
            if (Path.GetPathRoot(gameRoot) is { Length: > 0 } root)
            {
                return new DriveInfo(root).DriveType switch
                {
                    DriveType.Fixed => "fixed",
                    DriveType.Removable => "removable",
                    DriveType.Network => "network",
                    DriveType.CDRom => "optical",
                    DriveType.Ram => "RAM",
                    _ => null,
                };
            }
        }
        catch (Exception)
        {
            // A share or an odd root: the description says the rest.
        }

        return null;
    }

    /// <summary>
    /// Deletes every session file whose first line isn't a log format 2 (or later) header: written by a build that logged
    /// Steam names and IDs. After the first start of format 2 there are none. Returns how many went.
    /// </summary>
    private static int DeleteOldFormats(string logDirectory)
    {
        int deleted = 0;
        foreach (string file in Directory.GetFiles(logDirectory, LogRetention.Pattern))
        {
            try
            {
                string? first;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    first = reader.ReadLine();
                }

                if (LogHeader.FormatOf(first) < LogHeader.Format)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in another program, say: tried again next start.
            }
        }

        return deleted;
    }

    /// <summary>Deletes what <see cref="LogRetention"/> says goes, leaving room for this session's file. Returns what went.</summary>
    private static List<KeyValuePair<SessionLogFile, LogRemoval>> Prune(string logDirectory)
    {
        var files = new List<SessionLogFile>();
        var paths = new Dictionary<SessionLogFile, string>();
        foreach (string path in Directory.GetFiles(logDirectory, LogRetention.Pattern))
        {
            try
            {
                var info = new FileInfo(path);
                var file = new SessionLogFile(info.Name, info.Length, info.LastWriteTimeUtc);
                files.Add(file);
                paths[file] = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone meanwhile, or unreadable: left alone.
            }
        }

        var deleted = new List<KeyValuePair<SessionLogFile, LogRemoval>>();
        foreach (KeyValuePair<SessionLogFile, LogRemoval> old in LogRetention.ToDelete(files, DateTime.UtcNow))
        {
            try
            {
                File.Delete(paths[old.Key]);
                deleted.Add(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in another program, say: try again next session.
            }
        }

        return deleted;
    }
}
