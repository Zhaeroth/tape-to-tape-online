#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Hooks;
using T2TOnline.Plugin.NativeLobby;
using UnityEngine.SceneManagement;

namespace T2TOnline.Plugin.Client;

/// <summary>
/// Refuses this PC's save writes (the [saves] rows of clientmode.txt) while its game plays a host's match, so a friend's
/// own saves are never touched by a match that isn't theirs. Several things hold it, independently:
/// <list type="bullet">
/// <item>a host's match loading here (<see cref="HoldForMatch"/>, from <c>MatchSetup.TryLoad</c>): from the moment the
/// load starts until that match's scene unloads (<see cref="MatchLoaded"/> names the scene; the guard follows MelonLoader's
/// scene events), which covers the load, the moments before playback starts and after it stops, the end-of-match grace,
/// the quit back to the menu, and a failed or dropped session that leaves the match loaded. If the game is back at its
/// main menu before the match loaded, the hold ends there;</item>
/// <item>client mode, while it's on;</item>
/// <item>a host's campaign followed here (<see cref="HoldForCampaign"/>, M4, D41): for the whole campaign session, since
/// the friend's game then holds the host's run;</item>
/// <item>saves restored from a backup (<see cref="HoldUntilRestart"/>, D46): until the game restarts, so what the game
/// still holds in memory can't overwrite the restored files;</item>
/// <item>the native lobby's check tour (<see cref="HoldForCheck"/>), developer only: while it runs, and through the quit it ends with.</item>
/// </list>
/// While it holds, <see cref="AllowWrites"/> lets named files through for a moment (a friend's own profile and stats
/// after shared progress is applied, D42), with the copies Easy Save makes while saving them; the run file never. While the
/// check tour holds it, no allowance lets anything through (each is refused and counted, <see cref="TourHolds"/>), and the
/// only writes that pass are the tour's own run file's (<see cref="TourHolds.RunFile"/>, by full path, Core's
/// <see cref="TourWrites"/>), never a Steam service call.
/// A refused write reports success, so the game carries on as if it had saved. Every refusal is counted, and the first
/// few are named in the log.
/// </summary>
/// <remarks>
/// The patches go in the first time the guard is needed and stay, as client mode's do (HarmonyX can't take an IL2CPP
/// detour out again; see <see cref="ClientMode"/>): when nothing holds the guard they let every call through at once.
/// A PC that never loads a host's match or enters client mode (a host) never carries them. Nothing here throws into the
/// game.
/// </remarks>
internal static class SaveGuard
{
    private const string MenuScene = "start_screen";
    private const int NamedRefusals = 5;

    // The game may save from a worker thread: the counts are kept under a lock.
    private static readonly object Gate = new();
    private static readonly Dictionary<(MethodBase Method, string Files), long> RefusedByFiles = new();
    private static readonly List<string> FirstRefused = new();
    private static HarmonyLib.Harmony? _harmony;

    // Null until the first Install; then empty, or why the guard can't be trusted (for good: a second call must not retry).
    private static string? _installProblem;
    private static MatchHold _match;
    private static Scene _matchScene;
    private static string _matchWhat = string.Empty;
    private static bool _clientMode;
    private static string? _campaign;
    private static string? _restart;
    private static bool _check;
    private static bool _tourPathSaid;

    // The game's Steam service (cloud saves, the run's deletion): never a tour's write, whatever file it names.
    private const string SteamServiceName = "SteamService";

    // Each file let through, with how many allowances hold it (they can overlap: a closing Settings menu's and an opening one's).
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.OrdinalIgnoreCase);
    private static long _refusedThisHold;
    private static long _refusedThisCampaign;

    // The first few errors from each place are logged (a repeating one doesn't hide another).
    private static readonly ErrorLogLimit Errors = new(3);

    private enum MatchHold
    {
        Off,
        Loading,
        Loaded,
    }

    /// <summary>The run file, which <see cref="AllowWrites"/> never lets through while the guard holds.</summary>
    public const string RunFile = "RunDataV2.es3";

    /// <summary>True while the campaign's hold is on (<see cref="HoldForCampaign"/>).</summary>
    public static bool CampaignHolds => _campaign != null;

    /// <summary>True while any hold refuses this PC's save writes.</summary>
    public static bool Refusing => _clientMode || _match != MatchHold.Off || _campaign != null || _restart != null || _check;

    /// <summary>
    /// A copy of the writes refused so far this session, by method and the files each named (Easy Save's copies under the
    /// file they're of, joined by ", "; empty for a write naming none): client mode's leak audit tells the guard's expected
    /// refusals (the game's settings and profile) from leaks.
    /// </summary>
    public static Dictionary<(MethodBase Method, string Files), long> RefusedByFilesSoFar()
    {
        lock (Gate)
        {
            return new Dictionary<(MethodBase Method, string Files), long>(RefusedByFiles);
        }
    }

    /// <summary>Patches every save write in clientmode.txt [saves], once. False, with why, if the guard can't be trusted.</summary>
    public static bool Install(out string problem)
    {
        if (_installProblem == null)
        {
            _installProblem = InstallCore();
        }

        problem = _installProblem;
        return problem.Length == 0;
    }

    /// <summary>
    /// A host's match is about to load here: save writes are refused until that match's scene unloads (or the game is
    /// back at its main menu without it having loaded). Call only after <see cref="Install"/> succeeded, right before the
    /// load starts. Returns true if the guard already held for a match (one loaded before is still here).
    /// </summary>
    public static bool HoldForMatch(string what)
    {
        bool already = _match != MatchHold.Off;
        _match = MatchHold.Loading;
        _matchWhat = what;
        if (!already)
        {
            lock (Gate)
            {
                _refusedThisHold = 0;
                FirstRefused.Clear();
            }
        }

        ModLog.Info($"Save guard: on{(already ? " (still)" : string.Empty)}. This PC's save writes are refused until the match being loaded ({what}) has left again.");
        return already;
    }

    /// <summary>The load <see cref="HoldForMatch"/> was for didn't start, and no match held the guard before it.</summary>
    public static void ReleaseMatch(string why)
    {
        if (_match != MatchHold.Off)
        {
            Release(why);
        }
    }

    /// <summary>Client mode switched on or off.</summary>
    public static void HoldForClientMode(bool on) => _clientMode = on;

    /// <summary>
    /// A host's campaign is followed here (<paramref name="what"/>), or no longer (null): save writes are refused for the
    /// whole stretch. Installs the guard first; false, with nothing held, if it can't be trusted.
    /// </summary>
    public static bool HoldForCampaign(string? what)
    {
        if (what != null && !Install(out string problem))
        {
            ModLog.Warn($"Save guard: can't hold for the campaign ({problem}).");
            return false;
        }

        if (what != null && _campaign == null)
        {
            lock (Gate)
            {
                _refusedThisCampaign = 0;
            }

            ModLog.Info($"Save guard: on for the campaign ({what}). This PC's save writes are refused until the host's game is over here.");
        }
        else if (what == null && _campaign != null)
        {
            long refused;
            lock (Gate)
            {
                refused = _refusedThisCampaign;
            }

            bool stillHeld = _clientMode || _match != MatchHold.Off || _restart != null || _check;
            ModLog.Info($"Save guard: the campaign hold ({_campaign}) is over; {refused} save writes were refused during it" +
                $"{(stillHeld ? string.Empty : "; saves are written again")}.");
            LogSuppressedErrors();
        }

        _campaign = what;
        return true;
    }

    /// <summary>Sums up the errors not logged since the last time (a hold's end).</summary>
    private static void LogSuppressedErrors()
    {
        string line = ErrorLogLimit.Describe(Errors.TakeSuppressed());
        if (line.Length > 0)
        {
            ModLog.Warn($"Save guard: {line}.");
        }
    }

    /// <summary>
    /// Saves were just restored from a backup (<paramref name="why"/>): every save write is refused until the game restarts,
    /// since the profile, stats and settings the game holds in memory are the ones from before the restore and would be
    /// saved over the restored files. Installs the guard first; false, with nothing held, if it can't be trusted.
    /// </summary>
    public static bool HoldUntilRestart(string why)
    {
        if (!Install(out string problem))
        {
            ModLog.Warn($"Save guard: can't hold until the game restarts ({problem}).");
            return false;
        }

        // This hold's first refusals are named, whatever was refused earlier this game session (a friend's campaign's, a
        // match's): the log then shows what the game tried to save over the restored files. Not under a match's hold, whose
        // end reports its own.
        if (_restart == null && _match == MatchHold.Off)
        {
            lock (Gate)
            {
                _refusedThisHold = 0;
                FirstRefused.Clear();
            }
        }

        _restart = why;
        ModLog.Info($"Save guard: on until the game restarts ({why}). This PC's save writes are refused until then.");
        return true;
    }

    /// <summary>
    /// The native lobby's check tour runs: every save write is refused while <paramref name="on"/>. Installs the guard first; false, with why and nothing held, if it can't
    /// be trusted.
    /// </summary>
    public static bool HoldForCheck(bool on, out string problem)
    {
        problem = string.Empty;
        if (on && !Install(out problem))
        {
            return false;
        }

        _check = on;
        _tourPathSaid = false;
        ModLog.Info(on ? "Save guard: on for the check tour: this PC's save writes are refused while it runs." : "Save guard: the check tour is over.");
        return true;
    }

    /// <summary>
    /// Lets the game write the files named <paramref name="fileNames"/> (file names, any folder) while the guard holds, until
    /// the result is disposed: for a friend's own profile and stats after shared progress is applied. With each file go the
    /// copies Easy Save makes while saving it (<see cref="EasySaveCopies"/>: <c>.tmp</c>, <c>.bac</c>, and the
    /// <c>.tmp.bak</c> its commit copies the file to and deletes again). A write naming any other file, or none, is still
    /// refused, and so is the run file (<see cref="RunFile"/>) whatever it's called with; nothing is let through while a
    /// restored backup waits for the game to restart (<see cref="HoldUntilRestart"/>). Allowances add up: a file stays let
    /// through until the last one holding it is disposed. Logged, the files by name but for numbered ones, which are said as a
    /// range and a count (<see cref="SaveFileListText"/>: a run history save may write any of the game's 500 slots).
    /// <see cref="Allow"/> does the same and also tells which files didn't reach the disk; this one looks at no file (its
    /// caller's save runs on the game's own time, and nobody asks afterwards).
    /// </summary>
    public static IDisposable AllowWrites(string why, params string[] fileNames) => AllowCore(why, fileNames, stamped: false);

    /// <summary>
    /// <see cref="AllowWrites"/>, whose result also tells which of the files didn't reach the disk while it was on
    /// (<see cref="SaveAllowance.NotWritten"/>): a caller that saves them synchronously checks right after, before it says
    /// they were saved.
    /// </summary>
    public static SaveAllowance Allow(string why, params string[] fileNames) => AllowCore(why, fileNames, stamped: true);

    /// <summary>
    /// The allowance of <see cref="AllowWrites"/> and <see cref="Allow"/>. <paramref name="stamped"/>: each file's size and
    /// last write are read now, for <see cref="SaveAllowance.NotWritten"/> (one look at the disk per file).
    /// </summary>
    private static SaveAllowance AllowCore(string why, string[] fileNames, bool stamped)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var added = new List<string>();
        var held = new List<string>();
        var refused = new List<string>();
        var stamps = new Dictionary<string, (string Path, SaveStamp Before)>(StringComparer.OrdinalIgnoreCase);
        string? tourRunFile = TourHolds.RunFile;
        lock (Gate)
        {
            foreach (string name in fileNames)
            {
                string file = Path.GetFileName(name ?? string.Empty);
                if (file.Length == 0 || string.Equals(file, RunFile, StringComparison.OrdinalIgnoreCase) || stamps.ContainsKey(file))
                {
                    continue;
                }

                // A check tour lets no allowance through but for its own run file (which passes by its path anyway): the
                // file is stamped, so the caller's check finds it unwritten, and never goes on the allowed list.
                if (_check && !TourWrites.Allows(tourRunFile, name))
                {
                    refused.Add(file);
                    stamps[file] = (name!, default);
                    continue;
                }

                held.Add(file);
                int holding = Allowed.GetValueOrDefault(file);
                Allowed[file] = holding + 1;
                if (holding == 0)
                {
                    added.Add(file);
                }

                stamps[file] = (name!, default);
            }
        }

        // Each file as it was, so the caller can tell afterwards whether its save reached the disk. Only for a caller that
        // asks: an allowance nobody asks about looks at no file and keeps no stamps.
        if (stamped)
        {
            foreach (string file in stamps.Keys.ToList())
            {
                string path = stamps[file].Path;
                stamps[file] = (path, StampOf(path));
            }
        }
        else
        {
            stamps.Clear();
        }

        if (refused.Count > 0)
        {
            TourHolds.AllowanceRefused($"{why} ({SaveFileListText.Describe(refused)})");
        }

        if (_restart != null && added.Count > 0)
        {
            ModLog.Warn($"Save guard: not letting {SaveFileListText.Describe(added)} through ({why}): a restored backup waits for the game to restart ({_restart}).");
        }
        else if (Refusing && added.Count > 0)
        {
            ModLog.Info($"Save guard: letting {SaveFileListText.Describe(added)} through ({why}).");
        }

        return new SaveAllowance(held, stamps);
    }

    private static string InstallCore()
    {
        IReadOnlyList<ClientModeRule> rules;
        try
        {
            rules = ClientModeList.LoadEmbedded();
        }
        catch (Exception ex)
        {
            return $"clientmode.txt didn't load: {ModLog.SafeErrorText(ex)}";
        }

        _harmony ??= new HarmonyLib.Harmony("t2tonline.saveguard");
        var failures = new List<string>();
        int patched = 0;
        foreach (ClientModeRule rule in rules.Where(r => r.Action == ClientModeAction.Save))
        {
            Type? type = Il2CppTypeLocator.Find(rule.Entry.TypeName);
            List<MethodInfo> methods = type == null
                ? new List<MethodInfo>()
                : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == rule.Entry.MemberName).ToList();
            if (methods.Count == 0)
            {
                failures.Add($"{rule.Entry} ({(type == null ? "type" : "method")} not found)");
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                string prefix = method.ReturnType == typeof(void) ? nameof(VoidPrefix)
                    : method.ReturnType == typeof(bool) ? nameof(BoolPrefix)
                    : string.Empty;
                if (prefix.Length == 0)
                {
                    failures.Add($"{rule.Entry} (returns {method.ReturnType.Name}: only void and bool writes can be refused)");
                    continue;
                }

                try
                {
                    _harmony.Patch(method, prefix: new HarmonyMethod(typeof(SaveGuard).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
                    patched++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{rule.Entry} ({ModLog.SafeErrorText(ex)})");
                }
            }
        }

        // Every write must be covered: one that isn't could overwrite this PC's saves with a match that isn't its own.
        if (failures.Count > 0)
        {
            ModLog.Warn($"Save guard: {failures.Count} save writes couldn't be patched: {string.Join("; ", failures)}.");
            return $"{failures.Count} of the game's save writes couldn't be guarded (see the log)";
        }

        MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneLoaded);
        MelonEvents.OnSceneWasUnloaded.Subscribe(OnSceneUnloaded);
        ModLog.Info($"Save guard: ready ({patched} save writes patched; they refuse only while a host's match or campaign is here, client mode is on, or a restore waits for a restart).");
        return string.Empty;
    }

    /// <summary>
    /// A match has loaded (its skaters are initialized): while the guard waits for the one it holds for, that's it, and
    /// the guard now holds until <paramref name="scene"/> unloads. By the scene itself, not its name: a rematch loads a
    /// new <c>standard_game</c> while the old one may still be unloading.
    /// </summary>
    public static void MatchLoaded(Scene scene)
    {
        if (_match != MatchHold.Loading)
        {
            return;
        }

        _matchScene = scene;
        _match = MatchHold.Loaded;
        ModLog.Info($"Save guard: the match loaded (scene {scene.handle}); save writes stay refused until it unloads.");
    }

    private static void OnSceneLoaded(int buildIndex, string name)
    {
        try
        {
            if (_match == MatchHold.Loading && name == MenuScene)
            {
                // Back at the main menu, and the match it held for never loaded.
                Release("the game went back to its main menu before the match loaded");
            }
            else if (_match == MatchHold.Loaded && !_matchScene.isLoaded)
            {
                Release("the match's scene unloaded");
            }
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private static void OnSceneUnloaded(int buildIndex, string name)
    {
        try
        {
            if (_match == MatchHold.Loaded && !_matchScene.isLoaded)
            {
                Release("the match's scene unloaded");
            }
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private static void Release(string why)
    {
        _match = MatchHold.Off;
        long refused;
        string first;
        lock (Gate)
        {
            refused = _refusedThisHold;
            first = string.Join(", ", FirstRefused);
        }

        // The match's hold is over; the guard itself may still hold for something else (named, so the log isn't misread).
        string still = _clientMode ? "; client mode still holds it"
            : _campaign != null ? $"; the campaign hold ({_campaign}) still refuses writes"
            : _restart != null ? $"; writes stay refused until the game restarts ({_restart})"
            : _check ? "; the check tour still holds it"
            : string.Empty;
        ModLog.Info(refused == 0
            ? $"Save guard: the match's hold is off ({why}); no save writes were refused while it held for {_matchWhat}{still}."
            : $"Save guard: the match's hold is off ({why}); {refused} save writes were refused while it held for {_matchWhat} (first: {first}){still}.");
        LogSuppressedErrors();
    }

    private static void Failed(Exception ex)
    {
        // Held on purpose: a guard that can't follow the scenes keeps refusing (until the game restarts) rather than
        // risk a friend's saves.
        if (Errors.Allow("following the match's scenes"))
        {
            ModLog.Error("Save guard: following the match's scenes failed; save writes stay refused for this session", ex);
        }
    }

    private static bool VoidPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!Refusing || Permitted(__originalMethod, __args))
        {
            return true;
        }

        Count(__originalMethod, __args);
        return false;
    }

    private static bool BoolPrefix(MethodBase __originalMethod, object[] __args, ref bool __result)
    {
        if (!Refusing || Permitted(__originalMethod, __args))
        {
            return true;
        }

        Count(__originalMethod, __args);
        __result = true;
        return false;
    }

    private static void Count(MethodBase method, object[] args)
    {
        try
        {
            bool named;
            bool nearAllowed = false;
            lock (Gate)
            {
                var byFiles = (method, OriginalFiles(args));
                RefusedByFiles[byFiles] = RefusedByFiles.GetValueOrDefault(byFiles) + 1;
                long thisHold = ++_refusedThisHold;
                long thisCampaign = _campaign != null ? ++_refusedThisCampaign : 0;
                if (_check)
                {
                    // What the game meant to write stays in its memory: the tour quits rather than let it be saved later.
                    TourHolds.WriteRefused(TourEnd.HoldsPlayerData(FileNames(args)));
                }

                named = (thisHold <= NamedRefusals && (_match != MatchHold.Off || _restart != null || _check)) || thisCampaign is > 0 and <= NamedRefusals;

                // A write refused while a file with the same start is allowed: Easy Save may name a copy this build doesn't
                // know, and then the allowed save fails. Named at once, whatever the count.
                if (_restart == null && Allowed.Count > 0)
                {
                    foreach (string? name in FileNames(args))
                    {
                        nearAllowed |= name != null && Allowed.Keys.Any(file => name.StartsWith(file, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            if (_check && !_tourPathSaid && FileNames(args).Any(name => EasySaveCopies.IsWriteOf(name, TourWrites.RunFileName)) &&
                method.DeclaringType?.Name != SteamServiceName)
            {
                // Its name, by another path (a bare name, another folder): Easy Save didn't name the tour's file as the tour set it.
                _tourPathSaid = true;
                ModLog.Warn($"Save guard: refused a write naming {TourWrites.RunFileName} by another path: only the tour's own run file passes.");
            }

            if (named || nearAllowed)
            {
                string call = $"{method.DeclaringType?.Name.Replace("Il2Cpp", string.Empty, StringComparison.Ordinal)}::{method.Name}({Files(args)})";
                lock (Gate)
                {
                    FirstRefused.Add(call);
                }

                if (nearAllowed && Errors.Allow("a write near an allowed file"))
                {
                    ModLog.Warn($"Save guard: refused {call} while a file of that name is let through: the allowed save may fail (Easy Save named a file the guard doesn't know).");
                }
                else if (named)
                {
                    ModLog.Info($"Save guard: refused {call}.");
                }
            }
        }
        catch (Exception ex)
        {
            if (Errors.Allow("counting a refused write"))
            {
                ModLog.Error("Save guard: counting a refused write failed (it was refused all the same)", ex);
            }
        }
    }

    /// <summary>
    /// True when every file the write names is allowed (<see cref="AllowWrites"/>) or is one of Easy Save's copies of an
    /// allowed file (<see cref="EasySaveCopies.Original"/>), and it names at least one. Never the run file, and never while a
    /// restored backup waits for the restart: what the game holds in memory is from before the restore. While the check tour
    /// holds the guard, the allowed list counts for nothing: only a write of the tour's own run file passes (every path it
    /// names that file or Easy Save's copy of it, <see cref="TourWrites.AllowsWrite"/>), and never a Steam service call.
    /// </summary>
    private static bool Permitted(MethodBase method, object[] args)
    {
        try
        {
            if (_restart != null)
            {
                return false;
            }

            if (_check)
            {
                return method.DeclaringType?.Name != SteamServiceName && TourWrites.AllowsWrite(TourHolds.RunFile, Paths(args));
            }

            lock (Gate)
            {
                if (Allowed.Count == 0)
                {
                    return false;
                }

                bool any = false;
                foreach (string? name in FileNames(args))
                {
                    if (name == null)
                    {
                        continue;
                    }

                    string file = EasySaveCopies.Original(name);
                    if (string.Equals(file, RunFile, StringComparison.OrdinalIgnoreCase) || !Allowed.ContainsKey(file))
                    {
                        return false;
                    }

                    any = true;
                }

                return any;
            }
        }
        catch (Exception)
        {
            return false; // when in doubt, refuse
        }
    }

    /// <summary>The file name each argument of a write names (a path, or Easy Save settings' path), or null for one that names none.</summary>
    private static IEnumerable<string?> FileNames(object[] args)
    {
        foreach (string? path in Paths(args))
        {
            yield return path == null ? null : Path.GetFileName(path);
        }
    }

    /// <summary>The path each argument of a write names, as given (a path, or Easy Save settings' path), or null for one that names none.</summary>
    private static IEnumerable<string?> Paths(object[] args)
    {
        foreach (object arg in args)
        {
            string? path = arg switch
            {
                string text => text,
                ES3Settings settings => settings.path,
                _ => null,
            };
            yield return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>The stamp of a file named as the game names it (a bare name is in its save folder), or "not there" when it can't be read.</summary>
    internal static SaveStamp StampOf(string name)
    {
        try
        {
            return SaveStamp.Of(Path.IsPathRooted(name) ? name : Path.Combine(UnityEngine.Application.persistentDataPath, name));
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>An allowance's files are held by one allowance less: each goes off the allowed list with the last one holding it.</summary>
    internal static void Disallow(List<string> files)
    {
        lock (Gate)
        {
            foreach (string file in files)
            {
                int holding = Allowed.GetValueOrDefault(file);
                if (holding <= 1)
                {
                    Allowed.Remove(file);
                }
                else
                {
                    Allowed[file] = holding - 1;
                }
            }
        }
    }

    /// <summary>The save files a write was for (Easy Save's copies under their original), distinct, joined by ", "; empty for none.</summary>
    private static string OriginalFiles(object[] args) =>
        string.Join(", ", FileNames(args).Where(name => !string.IsNullOrEmpty(name)).Select(EasySaveCopies.Original).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The file names a write was given (names only: full paths would put the player's folders in the log).</summary>
    private static string Files(object[] args) => string.Join(", ", FileNames(args).Where(name => !string.IsNullOrEmpty(name)));
}

/// <summary>
/// Files let through the save guard for a moment (<see cref="SaveGuard.AllowWrites"/>), until disposed; it also tells
/// which of them didn't reach the disk, by each file's size and last write from when the allowance began. The game saves a
/// profile and stats synchronously, so a file unchanged right after its save wasn't written (a refused or failed write).
/// </summary>
internal sealed class SaveAllowance : IDisposable
{
    private readonly IReadOnlyDictionary<string, (string Path, SaveStamp Before)> _stamps;
    private List<string>? _files;

    /// <summary>An allowance holding <paramref name="files"/> on the allowed list, with every file's stamp from before (<paramref name="stamps"/>).</summary>
    public SaveAllowance(List<string> files, IReadOnlyDictionary<string, (string Path, SaveStamp Before)> stamps)
    {
        _files = files;
        _stamps = stamps;
    }

    /// <summary>
    /// The allowed files (by name) whose size and last write haven't changed since the allowance began: their save didn't
    /// reach the disk. Empty when every one was written. Never throws.
    /// </summary>
    public IReadOnlyList<string> NotWritten()
    {
        var unwritten = new List<string>();
        foreach (KeyValuePair<string, (string Path, SaveStamp Before)> file in _stamps)
        {
            SaveStamp now = SaveGuard.StampOf(file.Value.Path);
            if (!now.Exists || now == file.Value.Before)
            {
                unwritten.Add(file.Key);
            }
        }

        return unwritten;
    }

    /// <summary>True when <paramref name="file"/> (a name, any folder) was allowed here and its save reached the disk.</summary>
    public bool Written(string file)
    {
        string name = Path.GetFileName(file ?? string.Empty);
        return _stamps.ContainsKey(name) && !NotWritten().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<string>? files = Interlocked.Exchange(ref _files, null);
        if (files != null)
        {
            SaveGuard.Disallow(files);
        }
    }
}
#endif
