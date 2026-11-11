#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

// The tour's core, shared by every tour: what run.txt asks for (Core's UiCheckRequest: the tour, its time limit, its
// arguments), the ERROR count since the tour began (ModLog.ErrorCount) with the errors its steps stage, the layout flags per
// screen for diffing two runs (flags.tsv), the files hashed at the start and compared at the end (Core's UiCheckFiles), and
// the coroutine pump a step drives a game coroutine with (Core's CoroutinePump, TourCoroutine).
internal static partial class LobbyUiCheck
{
    private const string FlagsFile = "flags.tsv";

    private static readonly List<KeyValuePair<string, IReadOnlyList<UiCheckHit>>> ScreenFlags = new();
    private static readonly Dictionary<string, string> HashesAtStart = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> BackupsAtStart = new();
    private static string? _tourLine;
    private static UiCheckRequest? _request;
    private static int _stagedErrors;

    /// <summary>
    /// True once a press or the time limit has turned the tour to restoring (or it has ended): a coroutine the tour drives
    /// stops at its next frame, and a game routine given a "should stop" question (the campaign walker's) is told to stop.
    /// </summary>
    public static bool Stopping => _phase != Phase.Running;

    /// <summary>What run.txt asked for, read when the tour started; null before.</summary>
    public static UiCheckRequest? Request => _request;

    /// <summary>
    /// A step stages <paramref name="count"/> ERROR line(s) on purpose (a failure path it checks): the tour fails only on
    /// more ERROR lines than its steps staged. Said in the summary with <paramref name="why"/>.
    /// </summary>
    public static void StageErrors(int count, string why)
    {
        if (count <= 0)
        {
            return;
        }

        _stagedErrors += count;
        StepNote($"staged: {count} ERROR line(s) expected ({why})");
    }

    /// <summary>The tour run.txt (or Ctrl+F12) asks for, or null with the refusal said in the log and the summary.</summary>
    private static UiCheckRequest? ReadRequest()
    {
        if (UiCheckRequest.TryParse(_tourLine, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem))
        {
            return request;
        }

        ModLog.Warn($"Lobby check: {RunFile} asks for \"{SafeText.Clean(_tourLine, 48)}\": refused: {problem}; nothing was run.");
        return null;
    }

    /// <summary>At the tour's start: its name, limit and arguments; the ERROR mark; the pump's own check; the files hashed.</summary>
    private static void BeginTourCore(UiCheckRequest request)
    {
        _stagedErrors = 0;
        ScreenFlags.Clear();
        ModLog.ErrorCount.Mark();
        string tour = string.Create(CultureInfo.InvariantCulture, $"tour {request.Tour.Name} (limit {request.Tour.Limit.TotalMinutes:0.#} min; arguments {request.ArgumentsText()})");
        Line($"Lobby check: {tour}.");
        ModLog.Info($"Lobby check: {tour}.");
        string pump = TourCoroutine.SelfCheck();
        Line($"Lobby check: {pump}");
        ModLog.Info($"Lobby check: {pump}");
        HashGuardedFiles();
    }

    /// <summary>At the tour's end, before its last line: the ERROR count, and a failure when more came than were staged.</summary>
    private static void EndTourErrors()
    {
        long errors = ModLog.ErrorCount.SinceMark;
        string count = string.Create(CultureInfo.InvariantCulture, $"ERROR lines since the tour started: {errors}{(_stagedErrors > 0 ? $" ({_stagedErrors} staged)" : string.Empty)}");
        Line($"Lobby check: {count}");
        ModLog.Info($"Lobby check: {count}");
        if (ErrorTally.Verdict(errors, _stagedErrors) is { } problem)
        {
            _failed++;
            string first = ModLog.ErrorCount.FirstSinceMark is { } line ? $"; the first: {line}" : string.Empty;
            Line($"FAIL errors: {problem}{first}");
            ModLog.Warn($"Lobby check: the tour fails: {problem}.");
        }
    }

    /// <summary>
    /// At the tour's end, beside the ERROR count: a failure when the holds turned an invite or a join away (TourHolds): a tour
    /// never invites or joins anyone, so one asked for means a step pressed what it must not, or someone reached the tour
    /// through Steam. The log's own lines say which.
    /// </summary>
    private static void EndTourHolds()
    {
        if (TourHolds.ReachProblem is { } problem)
        {
            _failed++;
            Line($"FAIL holds: {problem}");
            ModLog.Warn($"Lobby check: the tour fails: {problem}.");
        }
    }

    /// <summary>A screen's flags, for <c>flags.tsv</c>, under an id two runs of the same tour share (no capture counter).</summary>
    private static void NoteScreenFlags(string name, List<UiCheckHit> hits) =>
        ScreenFlags.Add(new KeyValuePair<string, IReadOnlyList<UiCheckHit>>(
            string.Create(CultureInfo.InvariantCulture, $"{_step?.Id ?? "x"}_{name}_{Il2CppI2.Loc.LocalizationManager.CurrentLanguageCode}_{Screen.width}x{Screen.height}"), hits));

    /// <summary>Writes <c>flags.tsv</c> beside <c>summary.txt</c>.</summary>
    private static void FlushFlags()
    {
        try
        {
            if (_folder.Length > 0)
            {
                File.WriteAllText(Path.Combine(_folder, FlagsFile), UiCheckRules.FlagsTable(ScreenFlags));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Lobby check: {FlagsFile} couldn't be written ({ModLog.SafeErrorText(ex)}).");
        }
    }

    // ---- The coroutine pump ----

    /// <summary>
    /// Drives <paramref name="routine"/>, a game coroutine, once a frame as Unity would (<see cref="CoroutinePump"/>), until
    /// it ends, fails, stops (a press or the limit: <see cref="Stopping"/>) or runs past <paramref name="timeoutSeconds"/>.
    /// <paramref name="started"/> gets the pump before its first frame (to hand the coroutine's failure callback to
    /// <see cref="CoroutinePump.Fail"/>). A failure or a timeout is the step's problem; the line says how far it got.
    /// </summary>
    private static IEnumerable<Wait> Pump(string what, Il2CppSystem.Collections.IEnumerator routine, double timeoutSeconds, Action<CoroutinePump>? started = null)
    {
        var pump = new CoroutinePump(new TourCoroutine(routine, what), () => Stopping);
        started?.Invoke(pump);
        yield return Until(() => pump.Tick(Time.timeAsDouble, Time.realtimeSinceStartupAsDouble) != CoroutinePumpState.Running, timeoutSeconds, what);
        string how = string.Create(CultureInfo.InvariantCulture, $"{pump.Moves} stretches in {pump.Ticks} frames, {pump.Deepest} deep{(pump.Unknowns.Count > 0 ? $"; unknown yields taken as a frame: {string.Join(", ", pump.Unknowns)}" : string.Empty)}");
        switch (pump.State)
        {
            case CoroutinePumpState.Done:
                StepNote($"{what}: done after {how}");
                break;
            case CoroutinePumpState.Stopped:
                StepNote($"{what}: stopped (the tour is restoring) after {how}");
                break;
            case CoroutinePumpState.Failed:
                Problem($"{what}: {pump.Failure}; {how}");
                break;
            default:
                Problem(string.Create(CultureInfo.InvariantCulture, $"{what}: still running after {timeoutSeconds:0} s, in {pump.Top}; {how}"));
                break;
        }
    }

    // ---- Files ----

    /// <summary>
    /// Every file the tour guards, as label to path: the game's saves and Easy Save's backup copies of them (<c>*.es3</c>
    /// and <c>*.es3.bac</c> of its save folder, <see cref="UiCheckFiles.HashesSave"/>), the game's saves outside Easy Save
    /// (the files of <c>BracketData</c> and <c>TeamDataModels</c>, <see cref="UiCheckFiles.HashesSaveIn"/>), every file of
    /// the mod's data folder but its backups and read-me (<see cref="UiCheckFiles"/>), and the mod's <c>settings.cfg</c>.
    /// </summary>
    private static Dictionary<string, string> GuardedFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string saves = Application.persistentDataPath;
        foreach (string file in Directory.Exists(saves) ? Directory.GetFiles(saves) : Array.Empty<string>())
        {
            string name = Path.GetFileName(file);
            if (UiCheckFiles.HashesSave(name))
            {
                files[$@"saves\{name}"] = file;
            }
        }

        // The game's saves outside Easy Save (the tournament mode's files, each custom team, skater and goalie as JSON): the
        // save guard holds Easy Save's writes only, so the compare is what shows a tour that wrote one of these.
        foreach (string folder in UiCheckFiles.SaveFolders)
        {
            string path = Path.Combine(saves, folder);
            foreach (string file in Directory.Exists(path) ? Directory.GetFiles(path) : Array.Empty<string>())
            {
                string name = Path.GetFileName(file);
                if (UiCheckFiles.HashesSaveIn(folder, name))
                {
                    files[$@"saves\{folder}\{name}"] = file;
                }
            }
        }

        string data = CampaignFiles.DataFolder;
        if (Directory.Exists(data))
        {
            foreach (string file in Directory.GetFiles(data))
            {
                AddDataFile(files, data, file);
            }

            foreach (string folder in Directory.GetDirectories(data))
            {
                if (UiCheckFiles.HashesFolder(Path.GetFileName(folder)))
                {
                    foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        AddDataFile(files, data, file);
                    }
                }
            }
        }

        string settings = Path.Combine(CampaignFiles.ModFolder, SettingsFile.FileName);
        if (File.Exists(settings))
        {
            files[SettingsFile.Label] = settings;
        }

        return files;
    }

    private static void AddDataFile(Dictionary<string, string> files, string data, string file)
    {
        string relative = Path.GetRelativePath(data, file);
        if (UiCheckFiles.HashesFile(relative))
        {
            files[$@"{CampaignFiles.DataFolderLabel}\{relative}"] = file;
        }
    }

    /// <summary>
    /// True when the summary may print <paramref name="label"/>'s hash: the game's Easy Save files and the co-op slots, never
    /// the mod's own records. The files of the game's other save folders (a line per custom team would be many) are compared
    /// and named only when one changed.
    /// </summary>
    private static bool PrintsHash(string label) =>
        (label.StartsWith(@"saves\", StringComparison.OrdinalIgnoreCase) && label.IndexOf('\\', @"saves\".Length) < 0)
        ||(label.StartsWith(CampaignFiles.DataFolderLabel + @"\", StringComparison.OrdinalIgnoreCase) && UiCheckFiles.PrintsHash(label[(CampaignFiles.DataFolderLabel.Length + 1)..]));

    /// <summary>The names of the backups' folders (never their contents: a tour takes and restores none).</summary>
    private static List<string> BackupNames()
    {
        string backups = CampaignFiles.BackupsFolder;
        var names = new List<string>();
        foreach (string folder in Directory.Exists(backups) ? Directory.GetDirectories(backups) : Array.Empty<string>())
        {
            names.Add(Path.GetFileName(folder));
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static Dictionary<string, string> HashAll()
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> file in GuardedFiles())
        {
            hashes[file.Key] = Hash(file.Value);
        }

        return hashes;
    }

    private static void HashGuardedFiles()
    {
        HashesAtStart.Clear();
        BackupsAtStart.Clear();
        try
        {
            foreach (KeyValuePair<string, string> hash in HashAll())
            {
                HashesAtStart[hash.Key] = hash.Value;
            }

            BackupsAtStart.AddRange(BackupNames());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Line($"FILES NOT ALL HASHED at the start: {ModLog.SafeErrorText(ex)}.");
        }

        int printed = 0;
        foreach (KeyValuePair<string, string> file in HashesAtStart)
        {
            if (PrintsHash(file.Key))
            {
                printed++;
                Line($"File at the start: {file.Key} {file.Value}");
            }
        }

        Line(string.Create(CultureInfo.InvariantCulture, $"Files hashed besides: {HashesAtStart.Count - printed} (the mod's records and settings: hashes not printed); backups: {BackupsAtStart.Count} folder(s)."));
    }

    private static void CompareFiles()
    {
        Dictionary<string, string> now;
        List<string> backups;
        try
        {
            now = HashAll();
            backups = BackupNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Line($"FILES NOT COMPARED at the end: {ModLog.SafeErrorText(ex)}.");
            ModLog.Warn($"Lobby check: the files couldn't be compared at the end ({ModLog.SafeErrorText(ex)}).");
            return;
        }

        List<UiCheckFileChange> changed = UiCheckFiles.Compare(HashesAtStart, now);
        List<string> backupChanges = UiCheckFiles.BackupChanges(BackupsAtStart, backups); // named as a log names a backup: no local time, no folder name a player gave
        int same = now.Count - changed.FindAll(c => c.Kind != UiCheckFileChangeKind.Gone).Count;
        if (changed.Count == 0 && backupChanges.Count == 0)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"Files: all {same} hashed files are as they were at the start; the backups' {backups.Count} folder names too."));
            return;
        }

        if (changed.Count > 0)
        {
            string list = string.Join(", ", changed);
            Line($"FILES CHANGED during the tour: {list} ({same} as they were).");
            ModLog.Warn($"Lobby check: files changed during the tour: {list}.");
        }

        if (backupChanges.Count > 0)
        {
            string list = string.Join(", ", backupChanges);
            Line($"BACKUPS CHANGED during the tour: {list}.");
            ModLog.Warn($"Lobby check: the backups' folders changed during the tour: {list}.");
        }
    }

    private static string Hash(string file)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }
}
#endif
