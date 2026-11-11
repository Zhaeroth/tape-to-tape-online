#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppRewired;
using MelonLoader;
using MelonLoader.Utils;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;
using UnityEngine;
using KeyCode = UnityEngine.KeyCode;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>How the last bug report went, for the screens that show it (the problem report's prompts; the developer's F1 panel's status line).</summary>
internal enum ReportState
{
    /// <summary>None made this session.</summary>
    None,

    /// <summary>One is being made.</summary>
    Working,

    /// <summary>The last one is ready.</summary>
    Ready,

    /// <summary>The last one couldn't be made.</summary>
    Failed,
}

/// <summary>When Explorer opens with a report once it's made (never over a match: Explorer takes the window's focus).</summary>
internal enum ReportExplorer
{
    /// <summary>Never: whoever asked shows the report its own way (the game's prompt, D140).</summary>
    Never,

    /// <summary>When the developer's F1 panel is still open as it's ready.</summary>
    PanelOpen,

    /// <summary>When no match is on as it's ready (Insert held, where the native screens aren't used).</summary>
    OutOfMatch,
}

/// <summary>
/// Bug reports (D127): one zip, safe to post in public, written to <c>UserData\T2TOnline\reports\</c> (the newest
/// <see cref="BugReportFiles.Kept"/> kept). A player makes it from Settings > Online > Problem Report or the Online menu's Report a Problem (the game's own
/// prompts, <c>ReportFlow</c>), from the offer at the title after an online game that went wrong (<c>ReportOfferWatch</c>), or by holding Insert where the native
/// screens aren't used; the prompt's Show File opens Explorer with it selected. The developer's F1 > Help > Make a bug report makes one
/// too, and opens Explorer with it selected when the F1 panel is still open as it's ready (else the Help tab offers Show in Explorer: never Explorer over a match).
/// A mark (the Insert key, or the developer's F1 > Help > Mark this moment) writes <c>MARK n (in a match | in a menu | at the title)</c>
/// with the UTC time and shows the game's own toast. Works whether online is on or off: the plugin calls
/// <see cref="Update"/> and <see cref="OnGUI"/> before the runtime or the offline shell.
/// </summary>
/// <remarks>
/// The report is read, cleaned (<see cref="BugReportBuilder"/>) and zipped on a background thread at below-normal priority,
/// touching nothing of the game's: what only the game's thread may read (Unity's log folder, the settings, the other mods)
/// is gathered before it starts, and its outcome is logged from the game's thread in the next <see cref="Update"/>. Only
/// files <see cref="ReportAllowList"/> allows are read. Nothing here throws into the game: each step is guarded and logged.
/// </remarks>
internal static class BugReports
{
    /// <summary>The mark key's name, for the panel and the log.</summary>
    public const string MarkKeyName = "Insert";

    /// <summary>What a toast's title says (a name: the same in every language).</summary>
    private const string ToastTitle = "T2T Online";

    /// <summary>How long the corner note stands in for a toast that couldn't be shown.</summary>
    private const long NoteMs = 5000;

    /// <summary>
    /// The mark key: no default map of the game binds it (nor any F-key), no key of the mod's or Steam's is it, and a keyboard
    /// friend's game forwards only the keys its maps bind.
    /// </summary>
    private const KeyCode MarkKey = KeyCode.Insert;

    /// <summary>How long the mark key is held to make a report where the native screens aren't used (D140).</summary>
    private const long HoldMs = 2000;

    private static readonly BugMarks Marks = new();
    private static readonly object Gate = new();
    private static bool _configured;
    private static string _gameBuild = string.Empty;
    private static string? _toastOff;
    private static bool _holdMakes;
    private static long _heldSince = -1;
    private static bool _running;
    private static bool _keyOff;
    private static volatile Outcome? _done;
    private static volatile string _status = string.Empty;
    private static volatile string _problem = string.Empty;
    private static volatile ReportState _state;
    private static IReadOnlyList<string> _note = Array.Empty<string>();
    private static long _noteUntil;

    // The last report made (its full path stays in memory: never logged or shown), for Show in Explorer.
    private static string? _lastPath;

    /// <summary>The panel's status line for the last report ("" before any).</summary>
    public static string Status => _status;

    /// <summary>How the last report went.</summary>
    public static ReportState State => _state;

    /// <summary>Why the last report couldn't be started or made, in a few words ("" when the last one was made, or before any).</summary>
    public static string Problem => _problem;

    /// <summary>True while a report is being made.</summary>
    public static bool Busy
    {
        get
        {
            lock (Gate)
            {
                return _running;
            }
        }
    }

    /// <summary>How many reports have finished (made or failed) this session: the check tour waits on it.</summary>
    public static int Finished { get; private set; }

    /// <summary>The last finished report's zip name (empty when it failed), and its one-line summary.</summary>
    public static (string Name, string Summary) Last { get; private set; } = (string.Empty, string.Empty);

    /// <summary>
    /// Once, after the start-up checks: <paramref name="gameBuild"/> for the manifest; <paramref name="nativeScreens"/> true on
    /// a tested game build whose hooks all resolved (the mark's toast is the game's own only there, and the game's Settings and Online menu make
    /// reports; elsewhere holding the mark key 2 s makes one, D140).
    /// </summary>
    public static void Configure(string? gameBuild, bool nativeScreens)
    {
        _gameBuild = gameBuild ?? string.Empty;
        _toastOff = nativeScreens ? null : "this game build isn't one T2T Online has tested, so its toasts aren't used";
        _holdMakes = !nativeScreens;
        _configured = true;
        ModLog.Info($"Bug reports: {MarkKeyName} marks a moment (a MARK line here); " +
            (nativeScreens ? $"{Core.OnlineSettings.PlayersBuild.ProblemReport} (or the title's {Core.OnlineSettings.PlayersBuild.ReportAProblem})" : $"holding {MarkKeyName} {HoldMs / 1000} s") +
            $" zips the newest logs, cleaned, into UserData/T2TOnline/{BugReportFiles.FolderName}.");
        if (_holdMakes)
        {
            ModLog.Info($"Bug reports: holding {MarkKeyName} for {HoldMs / 1000} s makes one (the native screens aren't used on this game build); Explorer then shows it, out of a match.");
        }
    }

    /// <summary>
    /// The Settings menu's Online tab is off for this game (its patch or its graft failed; <paramref name="why"/> says which): its Problem Report row
    /// isn't there, so from now on holding the mark key 2 s makes a report too, as where the native screens aren't used (D140). Said once.
    /// </summary>
    public static void HoldMakesToo(string why)
    {
        if (_holdMakes)
        {
            return;
        }

        _holdMakes = true;
        ModLog.Info($"Bug reports: holding {MarkKeyName} for {HoldMs / 1000} s makes one from now on ({why}); Explorer then shows it, out of a match.");
    }

    /// <summary>Every frame, from the plugin: a finished report's outcome is logged, and the mark key is read. Never throws.</summary>
    public static void Update()
    {
        if (!_configured)
        {
            return;
        }

        if (_done is { } outcome)
        {
            _done = null;
            Finish(outcome);
        }

        if (_keyOff)
        {
            return;
        }

        try
        {
            // Rewired's keyboard, as the mod's other keys: off while the developer's F1 panel holds it, and only with the window focused.
            if (ReInput.isReady && ReInput.controllers.Keyboard.GetKeyDown(MarkKey) && Application.isFocused)
            {
                Mark($"the {MarkKeyName} key");
                _heldSince = _holdMakes ? Environment.TickCount64 : -1;
            }
            else if (_heldSince >= 0)
            {
                Hold();
            }
        }
        catch (Exception ex)
        {
            _keyOff = true;
            ModLog.Warn($"Bug reports: the {MarkKeyName} key can't be read ({ModLog.SafeErrorText(ex)}); " +
                (ModSettings.DevTools ? "F1, Help, Mark this moment still marks." : "a moment can't be marked this session (the logs still hold every line)."));
        }
    }

    /// <summary>
    /// The mark key still down after its press (only where the native screens aren't used): held <see cref="HoldMs"/> with the window focused, a
    /// report is made, once a press; let go (or the window left) first, nothing more. Read only while it's down: no call a frame otherwise.
    /// </summary>
    private static void Hold()
    {
        if (!ReInput.isReady || !ReInput.controllers.Keyboard.GetKey(MarkKey) || !Application.isFocused)
        {
            _heldSince = -1;
            return;
        }

        if (Environment.TickCount64 - _heldSince < HoldMs)
        {
            return;
        }

        _heldSince = -1;
        bool inMatch = Place() == MarkPlace.Match;
        ModLog.Info($"Bug report: {MarkKeyName} held {HoldMs / 1000} s: making one (the native screens aren't used on this game build).");
        if (!Make(ReportSources.InsertHeld, inMatch ? ReportExplorer.Never : ReportExplorer.OutOfMatch))
        {
            ModLog.Info($"Bug report: {MarkKeyName} held, but {(Busy ? "one is being made already" : "it couldn't start (" + (_problem.Length > 0 ? _problem : "not ready") + ")")}.");
        }
    }

    /// <summary>From the plugin's OnGUI: the corner note that stands in for a mark's toast the game couldn't show. Never throws.</summary>
    public static void OnGUI()
    {
        if (_note.Count > 0 && Environment.TickCount64 < _noteUntil)
        {
            StatusOverlay.Draw(_note, right: true);
        }
    }

    /// <summary>
    /// Marks this moment: the log's <c>MARK n</c> line and the game's toast (or the corner note). Returns the mark's number,
    /// or 0 when it failed (logged). <paramref name="how"/> says what set it ("the Insert key").
    /// </summary>
    public static int Mark(string how)
    {
        try
        {
            string line = Marks.Next(Place(), DateTime.UtcNow, how, out int number);
            ModLog.Info(line);
            var toast = new LobbyLine(LobbyWords.ToastMomentMarked, LobbyWords.Number(number));
            string? why = ShowToast(toast);
            if (why == null)
            {
                ModLog.Info("Bug reports: the mark's toast is on screen (the game's own).");
            }
            else
            {
                _note = new[] { toast.Text(0) };
                _noteUntil = Environment.TickCount64 + NoteMs;
                ModLog.Info($"Bug reports: the mark's toast couldn't be shown ({why}); a note in the corner says it for {NoteMs / 1000} s.");
            }

            return number;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug reports: marking failed ({ModLog.SafeErrorText(ex)}).");
            return 0;
        }
    }

    /// <summary>
    /// Starts a report on a background thread; false when one is already being made or it couldn't start (logged, and
    /// <see cref="Status"/> says why). <paramref name="why"/> says who asked; <paramref name="openExplorer"/> opens Explorer
    /// with the zip selected once it's made, if the developer's F1 panel is still open then (<see cref="ModPanel.AnyOpen"/>). Never throws.
    /// </summary>
    public static bool Make(string why, bool openExplorer) => Make(why, openExplorer ? ReportExplorer.PanelOpen : ReportExplorer.Never);

    /// <summary>
    /// Starts a report on a background thread, Explorer opening with it as <paramref name="explorer"/> says; false when one is already being made
    /// or it couldn't start (logged, and <see cref="Problem"/> says why). <paramref name="why"/> says who asked (the manifest names it). Never throws.
    /// </summary>
    public static bool Make(string why, ReportExplorer explorer)
    {
        if (!_configured)
        {
            _problem = "bug reports aren't set up yet";
            return false;
        }

        lock (Gate)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
        }

        try
        {
            Job job = Gather(why, explorer);
            _state = ReportState.Working;
            _problem = string.Empty;
            _status = "Making a bug report...";
            ModLog.Info($"Bug report: making one ({why}); the newest {BugReportFiles.SessionLogs} session logs, MelonLoader's newest {BugReportFiles.MelonLogs} and " +
                "Unity's 2 are read, cleaned and zipped in the background.");
            var thread = new Thread(() => Run(job))
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
                Name = "T2T Online bug report",
            };
            thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                _running = false;
            }

            _state = ReportState.Failed;
            _problem = ModLog.SafeErrorText(ex);
            _status = $"The bug report couldn't be started: {_problem}";
            ModLog.Warn($"Bug report: couldn't be started ({ModLog.SafeErrorText(ex)}).");
            return false;
        }
    }

    /// <summary>
    /// The developer's F1 panel's Show in Explorer, or the report prompt's Show File (<paramref name="asker"/> names which for the log): Explorer opens with
    /// the last report made selected. Null when it did, else why not (for the panel's line; logged either way). Never throws.
    /// </summary>
    public static string? ShowLastInExplorer(string asker = "the F1 panel's Show in Explorer")
    {
        string? path = _lastPath;
        string report = Last.Name;
        if (_state != ReportState.Ready || path == null || report.Length == 0)
        {
            return "no bug report is ready";
        }

        try
        {
            if (!File.Exists(path))
            {
                ModLog.Warn($"Bug report: {report} isn't where it was made any more; nothing to show.");
                return $"{report} isn't there any more";
            }
        }
        catch (Exception ex)
        {
            return ModLog.SafeErrorText(ex);
        }

        if (OpenExplorer(path) is { } failure)
        {
            ModLog.Warn($"Bug report: Explorer couldn't be opened ({failure}).");
            return failure;
        }

        ModLog.Info($"Bug report: Explorer shows {report} ({asker}).");
        return null;
    }

    /// <summary>Explorer with <paramref name="path"/> selected; null when it started, else the error's kind. Never throws.</summary>
    private static string? OpenExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>Where the game is now, for a mark: the active scene.</summary>
    private static MarkPlace Place()
    {
        string scene = WorldMap.SceneName;
        return scene == WorldMap.MatchScene ? MarkPlace.Match : scene == WorldMap.TitleScene ? MarkPlace.Title : MarkPlace.Menu;
    }

    /// <summary>The game's own toast for a mark, at once; null when it's on screen, else why not.</summary>
    private static string? ShowToast(LobbyLine line)
    {
        if (_toastOff != null)
        {
            return _toastOff;
        }

        try
        {
            // The mod's own toast: a host doesn't copy it to friends, and a following friend's game doesn't hold it back.
            using (OwnToasts.Begin())
            {
                return LobbyToast.Show(line, ToastTiming.Now, ToastTitle) ? null : "the game has no toast queue here (a loading screen?)";
            }
        }
        catch (Exception ex)
        {
            return $"the game's toast failed: {ModLog.SafeErrorText(ex)}";
        }
    }

    /// <summary>What the report needs from the game's thread, read before the background thread starts.</summary>
    private static Job Gather(string why, ReportExplorer explorer)
    {
        // This PC's own Steam name, known to the cleaner before the logs are read: a report before any lobby cleans it too. Never throws.
        OwnName.Learn("a report");
        string settings;
        try
        {
            settings = ModSettings.NonDefaultSummary();
        }
        catch (Exception ex)
        {
            settings = $"couldn't be listed ({ex.GetType().Name})";
        }

        var mods = new List<string>();
        try
        {
            mods.AddRange(MelonBase.RegisteredMelons.Where(m => m is not T2TOnlinePlugin)
                .Select(m => $"{SafeText.Clean(m.Info.Name, 48)} {SafeText.Clean(m.Info.Version, 24)}"));
        }
        catch (Exception ex)
        {
            mods.Add($"(couldn't be listed: {ex.GetType().Name})");
        }

        string melonLoader;
        try
        {
            Version? version = typeof(MelonBase).Assembly.GetName().Version;
            melonLoader = version == null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception ex)
        {
            melonLoader = $"unknown ({ex.GetType().Name})";
        }

        // Unity's log: where the engine says it writes (its folder beside the saves unless the game was started with -logFile).
        string unityFolder = string.Empty;
        try
        {
            string path = Application.consoleLogPath;
            unityFolder = string.IsNullOrEmpty(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch (Exception)
        {
            // Read below from the save folder instead.
        }

        if (unityFolder.Length == 0)
        {
            try
            {
                unityFolder = Application.persistentDataPath ?? string.Empty;
            }
            catch (Exception)
            {
                // No Unity log this time: the manifest says so.
            }
        }

        string sessionLogs = ModLog.FilePath is { } file && Path.GetDirectoryName(file) is { Length: > 0 } folder
            ? folder
            : Path.Combine(CampaignFiles.ModFolder, "logs");

        // A check tour's report goes in the tour's own folder, and nothing prunes the player's reports meanwhile (D143).
        ReportDestination destination = BugReportFiles.Destination(Path.Combine(CampaignFiles.ModFolder, BugReportFiles.FolderName), NativeLobby.TourHolds.On,
            NativeLobby.TourHolds.Folder);
        return new Job(
            why,
            explorer,
            new BugReportFacts
            {
                Why = why,
                Release = ModVersion.Release,
                Commit = ModVersion.Commit,
                Protocol = ProtocolInfo.Version,
                GameBuild = _gameBuild,
                MelonLoader = melonLoader,
                Os = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                Settings = settings,
                OtherMods = mods,
                ThisLaunchLog = ModLog.FilePath is { } own ? Path.GetFileName(own) : string.Empty,
            },
            MelonEnvironment.GameRootDirectory,
            sessionLogs,
            unityFolder,
            destination);
    }

    /// <summary>The background thread: the report written, the old ones pruned. Logs nothing itself (Explorer is the game's thread's, in <see cref="Finish"/>).</summary>
    private static void Run(Job job)
    {
        Outcome outcome;
        string? part = null;
        try
        {
            Directory.CreateDirectory(job.Reports);
            var notes = new List<string>();
            foreach (string stale in Directory.GetFiles(job.Reports, "*" + ModTemporaries.PartSuffix))
            {
                TryDelete(stale);
            }

            DateTime now = DateTime.UtcNow;
            string name = BugReportFiles.ReportName(now);
            while (File.Exists(Path.Combine(job.Reports, name)))
            {
                now = now.AddSeconds(1);
                name = BugReportFiles.ReportName(now);
            }

            string path = Path.Combine(job.Reports, name);
            string melonLogs = Path.Combine(job.GameRoot, "MelonLoader", "Logs");
            var allow = new ReportAllowList(job.SessionLogs, melonLogs, job.UnityFolder);
            var notFound = new List<string>();
            var refused = new List<string>();
            List<ReportSource> sessions = Sources(allow, ReportFileKind.SessionLog, job.SessionLogs, "session-*.log", BugReportFiles.SessionLogsNewestFirst, 3 * BugReportFiles.SessionLogs, notFound, refused, "the session logs folder");
            List<ReportSource> melons = Sources(allow, ReportFileKind.MelonLog, melonLogs, "*.log", BugReportFiles.MelonLogsNewestFirst, BugReportFiles.MelonLogs, notFound, refused, "MelonLoader's Logs folder");
            var unity = new List<ReportSource>();
            foreach (string unityName in new[] { "Player.log", "Player-prev.log" })
            {
                string unityPath = job.UnityFolder.Length == 0 ? string.Empty : Path.Combine(job.UnityFolder, unityName);
                if (unityPath.Length == 0 || !File.Exists(unityPath))
                {
                    notFound.Add($"Unity's {unityName}");
                }
                else if (allow.Source(ReportFileKind.UnityLog, unityPath, OpenShared) is { } source)
                {
                    unity.Add(source);
                }
                else
                {
                    refused.Add($"Unity's {unityName}");
                }
            }

            IReadOnlyList<string> loaders = Array.Empty<string>();
            try
            {
                loaders = BugReportFiles.OtherLoaders(Directory.GetFileSystemEntries(job.GameRoot).Select(Path.GetFileName).OfType<string>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add("the game folder couldn't be listed for other mod loaders");
            }

            BugReportFacts facts = new()
            {
                MadeUtc = now,
                Why = job.Facts.Why,
                Release = job.Facts.Release,
                Commit = job.Facts.Commit,
                Protocol = job.Facts.Protocol,
                GameBuild = job.Facts.GameBuild,
                MelonLoader = job.Facts.MelonLoader,
                Os = job.Facts.Os,
                Runtime = job.Facts.Runtime,
                Settings = job.Facts.Settings,
                OtherMods = job.Facts.OtherMods,
                OtherLoaders = loaders,
                NotFound = notFound,
                Refused = refused,
                ThisLaunchLog = job.Facts.ThisLaunchLog,
            };

            part = path + ModTemporaries.PartSuffix;
            BugReportResult result;
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                result = BugReportBuilder.Write(stream, facts, sessions, melons, unity, TimeZoneInfo.Local, ModLog.Scrub, LogNames.TextShape);
            }

            File.Move(part, path);
            part = null;
            long size = new FileInfo(path).Length;

            // The player's folder only, and never while a check tour holds (one may have begun since this report did).
            int removed = 0;
            if (job.Destination.Prunes && !NativeLobby.TourHolds.On)
            {
                foreach (string old in BugReportFiles.ReportsToDelete(Directory.GetFiles(job.Reports, "*.zip").Select(Path.GetFileName).OfType<string>()))
                {
                    removed += TryDelete(Path.Combine(job.Reports, old)) ? 1 : 0;
                }
            }

            outcome = new Outcome(true, name, path, size, result, removed, job.Explorer, notes, string.Empty, job.Destination.ForTour);
        }
        catch (Exception ex)
        {
            if (part != null)
            {
                TryDelete(part);
            }

            outcome = new Outcome(false, string.Empty, string.Empty, 0, null, 0, job.Explorer, new List<string>(), SafeError.Text(ex, ModLog.Scrub), job.Destination.ForTour);
        }

        _done = outcome;
        lock (Gate)
        {
            _running = false;
        }
    }

    /// <summary>
    /// The allowed files of <paramref name="kind"/> in <paramref name="folder"/>, newest first, at most <paramref name="most"/>;
    /// a missing folder is noted in <paramref name="notFound"/>, files the allow-list refused in <paramref name="refusedFiles"/>.
    /// </summary>
    private static List<ReportSource> Sources(ReportAllowList allow, ReportFileKind kind, string folder, string pattern,
        Func<IEnumerable<ReportCandidate>, IReadOnlyList<ReportCandidate>> order, int most, List<string> notFound, List<string> refusedFiles, string label)
    {
        var sources = new List<ReportSource>();
        if (!Directory.Exists(folder))
        {
            notFound.Add(label);
            return sources;
        }

        var candidates = new List<ReportCandidate>();
        foreach (string path in Directory.GetFiles(folder, pattern))
        {
            try
            {
                candidates.Add(new ReportCandidate(Path.GetFileName(path), File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone meanwhile: not a candidate.
            }
        }

        int refused = 0;
        foreach (ReportCandidate candidate in order(candidates))
        {
            if (sources.Count >= most)
            {
                break;
            }

            if (allow.Source(kind, Path.Combine(folder, candidate.Name), OpenShared) is { } source)
            {
                sources.Add(source);
            }
            else
            {
                refused++;
            }
        }

        if (refused > 0)
        {
            // Said, so a report with no session log isn't a mystery (a log folder somewhere the allow-list refuses: a game folder under a "progress" folder, say).
            refusedFiles.Add(string.Create(CultureInfo.InvariantCulture, $"{refused} file(s) in {label}"));
        }

        return sources;
    }

    /// <summary>Opens a file to read while others may be writing or replacing it.</summary>
    private static Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The game's thread: a finished report's lines, the panel's status, the tour's answer.</summary>
    private static void Finish(Outcome outcome)
    {
        try
        {
            if (!outcome.Made || outcome.Result is not { } result)
            {
                _state = ReportState.Failed;
                _problem = outcome.Error;
                _status = $"The bug report couldn't be made: {outcome.Error}";
                Last = (string.Empty, outcome.Error);
                ModLog.Warn($"Bug report: couldn't be made ({outcome.Error}).");
                return;
            }

            LogScrubCounts c = result.Cleaned;
            string summary = string.Create(CultureInfo.InvariantCulture,
                $"{outcome.Bytes / 1024.0:0} KB, {result.Files.Count} files; cleaned: {c.SteamIds} Steam IDs, {c.Lobbies} lobby IDs, {c.Names} names, {c.Folders} folders, " +
                $"{c.Addresses} addresses, {c.Offsets} time zone offsets, {result.MelonTimes} MelonLoader times to UTC, {result.GraphicsLines} graphics lines");
            // Explorer only while the panel the report was asked from is still open (closed, the player is back in the game, maybe a match, and
            // Explorer would take the window's focus; the Help tab offers Show in Explorer instead), or for a held Insert while no match is on.
            _lastPath = outcome.Path;
            bool shown = false;
            string where;
            if (outcome.Explorer == ReportExplorer.Never)
            {
                where = "Explorer not opened";
            }
            else if (outcome.Explorer == ReportExplorer.PanelOpen && !ModPanel.AnyOpen)
            {
                where = "Explorer not opened (the F1 panel was closed by then; its Help tab has Show in Explorer)";
            }
            else if (outcome.Explorer == ReportExplorer.OutOfMatch && Place() == MarkPlace.Match)
            {
                where = "Explorer not opened (a match is on by then: it would take the game's focus)";
            }
            else if (OpenExplorer(outcome.Path) is { } failure)
            {
                where = $"Explorer couldn't be opened ({failure})";
            }
            else
            {
                shown = true;
                where = outcome.Explorer == ReportExplorer.PanelOpen ? "Explorer shows it (the F1 panel was still open)" : "Explorer shows it (no match was on)";
            }

            // A check tour's goes in the tour's own folder (its label, as the tour's other files are named), the player's reports left as they were.
            string madeAt = outcome.ForTour ? DevOutput.Label(outcome.Path) : $"{BugReportFiles.FolderName}/{outcome.Name}";
            ModLog.Info($"Bug report: made UserData/T2TOnline/{madeAt} ({summary}); {where}" +
                (outcome.ForTour ? "; the check tour's own folder, the player's reports untouched." : "."));
            foreach (ReportLeftOut left in result.LeftOut)
            {
                ModLog.Info($"Bug report: left out {left.Name}: {left.Why}.");
            }

            foreach (string note in outcome.Notes)
            {
                ModLog.Info($"Bug report: {note}.");
            }

            if (outcome.Removed > 0)
            {
                ModLog.Info($"Bug report: removed {outcome.Removed} older report(s) (the newest {BugReportFiles.Kept} are kept).");
            }

            _state = ReportState.Ready;
            _problem = string.Empty;
            _status = $"Report ready: {outcome.Name} in the game folder's UserData\\T2TOnline\\{(outcome.ForTour ? Path.GetDirectoryName(madeAt)?.Replace('/', '\\') : BugReportFiles.FolderName)}" +
                (shown ? " (Explorer shows it)" : string.Empty) +
                ". Drag it into your bug report; it holds no Steam names, Steam IDs, folder names or local times.";
            Last = (outcome.Name, summary);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: its outcome couldn't be logged ({ModLog.SafeErrorText(ex)}).");
        }
        finally
        {
            Finished++;
        }
    }

    /// <summary>What the background thread needs.</summary>
    private sealed record Job(string Why, ReportExplorer Explorer, BugReportFacts Facts, string GameRoot, string SessionLogs, string UnityFolder, ReportDestination Destination)
    {
        /// <summary>The folder the zip goes in.</summary>
        public string Reports => Destination.Folder;
    }

    /// <summary>What the background thread did.</summary>
    private sealed record Outcome(bool Made, string Name, string Path, long Bytes, BugReportResult? Result, int Removed, ReportExplorer Explorer,
        List<string> Notes, string Error, bool ForTour);
}
#endif
