#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Il2Cpp;
using Il2CppPlatformService;
using Il2CppPort;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// Backups of the player's save files and co-op slots (D46: saves are never overwritten unless the user
/// purposely does so). Before every online session (host or friend) every <c>*.es3</c> in the game's save
/// folder, and every co-op slot (<see cref="CoopSlots"/>), is copied to
/// <c>%LOCALAPPDATA%\T2T Online\backups\&lt;yyyyMMddTHHmmssZ&gt;-&lt;host|friend&gt;\</c> (UTC; names from before 0.11.0 hold local time, D129; the slots in its <c>coop</c>
/// folder; outside the game folder, so a reinstall or the mod's uninstall doesn't take them; 0.6.0's in
/// <c>UserData\T2TOnline\backups</c> are moved there) with a manifest (why, and each file's SHA-256), unless the newest
/// backup already holds exactly those files; the newest <see cref="Kept"/> are kept, and the first one ever taken always
/// (the saves from before the first session). When the session ends the same files are hashed again and every file is
/// logged as unchanged or changed: the save check. A change to a friend's run file or co-op slot, or to a host's solo
/// run while a co-op slot was the game's run file, is a warning, also on screen.
/// <para>
/// A backup can be restored from the mod's panel, at the title screen outside a session, whole or not at all: the saves,
/// any of its co-op slots, or both (<see cref="RestoreParts"/>). After the saves the game must restart
/// (<see cref="RestartNeeded"/>), and no session starts until it has (<see cref="BlocksSessions"/>); a co-op slot alone needs
/// no restart. A restore the game stopped in the middle of is finished, or undone, at the next start that can (what a start
/// that couldn't wrote since is backed up first). Restored
/// files the game keeps in Steam Cloud are sent there, and those Steam didn't take are listed until each is
/// (<see cref="CloudPushList"/>): Steam's older copy would otherwise come back at the game's next load of it.
/// </para>
/// </summary>
internal static class SaveBackups
{
    /// <summary>Backups kept besides the first one ever taken; older ones are deleted when a new one is taken (never the one being restored, nor an unfinished restore's two).</summary>
    public const int Kept = 10;

    /// <summary>
    /// The first backup of each of this many days is kept besides those: a backup is taken per Start, so a night of games can
    /// fill <see cref="Kept"/> by itself, and what came before that night should still be there.
    /// </summary>
    public const int KeptDays = 7;

    /// <summary>What to tell the player after a restore of the saves.</summary>
    public const string RestartNotice = "Saves restored: restart the game now. Until you do, nothing is saved.";

    private static readonly List<NoticeId> StartUpNotices = new();

    /// <summary>What a restore's confirmation should say besides "Your current saves are backed up first.".</summary>
    public const string RestoreConfirmNote = "The game must restart afterwards; nothing is saved until it does.";

    /// <summary>What a restore's confirmation says when only co-op slots are chosen.</summary>
    public const string SlotsOnlyNote = "Co-op slots are restored at once; the game doesn't need to restart for them.";

    /// <summary>Where the backups are, as the panel and logs name it (without the player's user name).</summary>
    public const string FolderLabel = CampaignFiles.DataFolderLabel + @"\backups";

    /// <summary>The file in the backups folder that names the first backup ever taken, which is never pruned.</summary>
    private const string FirstMarkerFile = "first-backup.txt";

    private const string PartialSuffix = ".partial";
    private const long MaxFileBytes = 16L * 1024 * 1024;

    // The restored save files Steam Cloud must still get (read at start-up; a load of one, on a game thread, may ask).
    private static readonly object CloudGate = new();
    private static readonly object DecisionGate = new();
    private static readonly HashSet<string> CloudWarned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KeptNoted = new(StringComparer.OrdinalIgnoreCase);

    // Each co-op slot copy's run, by the copy's hash, as the Backups screen read it this game session (SlotRuns).
    private static readonly Dictionary<string, BackupSlotRun> SlotRunsRead = new(StringComparer.OrdinalIgnoreCase);

    // The restored files this start found: looked at once more at the title screen, after the game's loads.
    private static RestoreCheck? _restoredAtStart;
    private static CloudPushList? _cloud;
    private static int _cloudCount;
    private static bool _cloudTitleDone;

    // Each listed file's verdict against Steam's copy, made once a game session before the first push.
    private static Dictionary<string, CloudPushChoice>? _cloudChoices;
    private static bool _cloudDecided;

    private static SaveBackupManifest? _session;
    private static string _sessionName = string.Empty;
    private static bool _moved;
    private static bool _startedUp;

    // Set at the mod's start-up (StartUpAtLaunch): what a restore found unfinished may use on this game build.
    private static bool _saveGuardUsable;
    private static bool _cloudLoadsHeld;

    /// <summary>
    /// True once a backup of the saves was restored: the game still holds the old profile, stats and settings in memory and
    /// would save them over the restored files, so every save write is refused (<see cref="SaveGuard.HoldUntilRestart"/>)
    /// until the game restarts and loads the restored files. No further restore, and no online session, until then.
    /// </summary>
    public static bool RestartNeeded { get; private set; }

    /// <summary>
    /// The newest thing about the backups the player should see in the panel's Backups tab, or "": a session's backup that failed, the
    /// last save check's warning, a restore the game stopped in the middle of.
    /// </summary>
    public static string Notice { get; private set; } = string.Empty;

    /// <summary>The last save check's result in one line ("" before the first session ends), for the panel's Backups tab.</summary>
    public static string LastCheck { get; private set; } = string.Empty;

    /// <summary>
    /// True after a restore that wrote some files but couldn't finish or put them back: its journal stays, and the next start
    /// of the game finishes it (the panel says so rather than "nothing was changed").
    /// </summary>
    public static bool RestoreUnfinished { get; private set; }

    /// <summary>True while restored files wait to be in Steam Cloud (a field read: the game's cloud loads ask).</summary>
    public static bool CloudPending => Volatile.Read(ref _cloudCount) > 0;

    /// <summary>True while <see cref="PushRestoredAtTitle"/> has something to do this game session (a field read: run sync asks every frame).</summary>
    public static bool PushDue => (!_cloudTitleDone && CloudPending) || _restoredAtStart != null;

    /// <summary>
    /// True while restored files wait to be in Steam Cloud and Steam's copies of them haven't been checked yet this game
    /// session: until then whether the restore stands or yields to another device's saves isn't known.
    /// </summary>
    public static bool CloudUndecided => CloudPending && !Volatile.Read(ref _cloudDecided);

    /// <summary>
    /// True when no online session may start now, with why (for the status box): a restored backup waits for the game to
    /// restart, and until then nothing of a session would be saved (a friend's shared progress, a host's co-op run); or a
    /// restore is unfinished, and the next start finishes it (a session would play on half-restored files). Cheap; for
    /// OnlineSession before it hosts or joins.
    /// </summary>
    public static bool BlocksSessions(out string why)
    {
        why = RestartNeeded ? "Saves were restored from a backup: restart the game first. Until then nothing would be saved, so no session starts."
            : RestoreUnfinished ? "A restore of your saves is unfinished: restart the game first (its start finishes the restore), so no session starts."
            : string.Empty;
        return why.Length > 0;
    }

    /// <summary>
    /// At the mod's start-up, whatever online's state (<see cref="SavesAtLaunch"/>, before the game has read a save: the mod
    /// starts before the game's <c>Bootstrapper</c>): <see cref="StartUp"/>, where a restore the game stopped in the middle of
    /// is put right without a restart. <paramref name="saveGuardUsable"/>: the save guard may be installed on this game build
    /// (needed only when the game has read its saves already). <paramref name="cloudLoadsHeld"/>: the game's loads from Steam
    /// Cloud go through <see cref="RunSync"/>'s guard, which keeps restored files over Steam's older copies; without it a restore
    /// of cloud-saved files is undone or left for a later start (<see cref="RestoreRecovery.Decide(RestoreJournal, SaveBackupManifest?, bool, SaveBackupManifest?, bool, bool)"/>).
    /// Once per game session; never throws.
    /// </summary>
    public static void StartUpAtLaunch(bool saveGuardUsable, bool cloudLoadsHeld)
    {
        _saveGuardUsable = saveGuardUsable;
        _cloudLoadsHeld = cloudLoadsHeld;
        StartUpCore(atLaunch: true);
    }

    /// <summary>
    /// Before the first use (and at start-up, <see cref="StartUpAtLaunch"/>): backups still in the game folder move out of
    /// it, the list of restored files Steam Cloud must still get is read, and a restore the game
    /// stopped in the middle of is finished or undone, and its unfinished copies removed. Once per game session;
    /// never throws.
    /// </summary>
    public static void StartUp() => StartUpCore(atLaunch: false);

    /// <summary>
    /// The backups, newest first: by the moment each was taken (UTC), then by name. Not by the local time a row shows: in the
    /// hour a clock change repeats, the later of two backups reads earlier.
    /// </summary>
    public static IReadOnlyList<BackupInfo> List()
    {
        StartUp();
        var backups = new List<(DateTime TakenAtUtc, BackupInfo Info)>();
        try
        {
            if (!Directory.Exists(CampaignFiles.BackupsFolder))
            {
                return Array.Empty<BackupInfo>();
            }

            foreach (string folder in Directory.GetDirectories(CampaignFiles.BackupsFolder))
            {
                string name = Path.GetFileName(folder);
                if (!SaveBackupNames.TryParse(name, out DateTime takenAt, out _))
                {
                    continue;
                }

                backups.Add(ReadManifest(folder) is { } manifest
                    ? (manifest.TakenAtUtc, new BackupInfo(name, manifest.TakenAtUtc.ToLocalTime(), manifest.Reason, manifest.Files.Count, manifest.CoopSlots.Select(s => s.Slot).ToList()))
                    : (takenAt, new BackupInfo(name, takenAt.ToLocalTime(), "(its manifest is missing or damaged)", 0, Array.Empty<int>())));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't list them ({CampaignFiles.ErrorText(ex)}).");
        }

        return backups.OrderByDescending(b => b.TakenAtUtc).ThenByDescending(b => b.Info.Name, StringComparer.OrdinalIgnoreCase).Select(b => b.Info).ToList();
    }

    /// <summary>
    /// What backup <paramref name="name"/> holds, for a restore's confirmation: how many save files, and each co-op slot's
    /// run (squad, act and floor, read from the copy). Null when there's no such backup or its manifest can't be read.
    /// Reads up to three run files: call it from an action, not every frame.
    /// </summary>
    public static BackupContents? Describe(string name)
    {
        StartUp();
        if (string.IsNullOrEmpty(name) || !SaveBackupManifest.IsPlainFileName(name) || !SaveBackupNames.TryParse(name, out _, out _))
        {
            return null;
        }

        string folder = Path.Combine(CampaignFiles.BackupsFolder, name);
        SaveBackupManifest? manifest = Directory.Exists(folder) ? ReadManifest(folder) : null;
        if (manifest == null)
        {
            return null;
        }

        var slots = new List<(int Slot, string Run)>();
        foreach (CoopSlotHash slot in manifest.CoopSlots.Where(s => s.Slot <= CoopSlots.Count))
        {
            string run = "a run";
            try
            {
                string path = Path.Combine(folder, SaveBackupManifest.CoopFolder, slot.FileName);
                if (slot.Bytes <= RunCheckpoint.MaxPartBytes && File.Exists(path) && RunFileSummary.TryRead(CampaignFiles.ReadShared(path), out RunFileSummary? summary) &&
                    summary != null && summary.SquadId.Length > 0)
                {
                    run = summary.Failed ? "a lost run" : $"{CampaignGame.SquadName(summary.SquadId)} · act {summary.Act}, floor {summary.Floor}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Backups: couldn't read co-op slot {slot.Slot} in backups/{LogName(name)} ({CampaignFiles.ErrorText(ex)}).");
            }

            slots.Add((slot.Slot, run));
        }

        return new BackupContents(name, manifest.Files.Count, slots);
    }

    /// <summary>Takes a backup now (<paramref name="reason"/> is kept with it). False, with why, when it couldn't.</summary>
    public static bool TryTake(string reason, out string problem) => TryTake(reason, out _, out problem);

    /// <summary>
    /// Takes a backup now (<paramref name="reason"/> is kept with it). <paramref name="taken"/> names it, and says whether it's
    /// an existing one: when the saves and co-op slots are the same as in the newest backup, that one stays their backup and
    /// nothing new is written (the panel says so rather than "Backup taken."). False, with why, when it couldn't. Never while
    /// a check tour runs: a backup taken then would push one of the player's own out of the retention.
    /// </summary>
    public static bool TryTake(string reason, out BackupTaken taken, out string problem)
    {
        if (NativeLobby.TourHolds.On)
        {
            taken = new BackupTaken(string.Empty, false);
            problem = "a check tour runs, and it takes no backups";
            ModLog.Info("Backups: a backup by hand refused: the check tour runs.");
            return false;
        }

        StartUp();
        SaveBackupManifest? manifest = Take("manual", string.IsNullOrWhiteSpace(reason) ? "taken from the panel" : reason, out string name, out bool reused, out problem);
        taken = new BackupTaken(name, reused);
        return manifest != null && problem.Length == 0;
    }

    /// <summary>
    /// Copies <paramref name="parts"/> of a backup back: its save files into the game's save folder, its co-op slots into the
    /// mod's. Only at the title screen (nothing open on it), outside a session, only parts the backup holds and only
    /// a backup whose every file matches its manifest; a co-op slot not while it's the game's run file. The current files
    /// and slots are backed up first ("before restoring ..."); every file is written beside its target before any is moved
    /// into place, and a move that fails puts back the ones already moved, so the restore happens whole or not at all (a
    /// journal beside the backups says what was under way, so the next start finishes it should the game stop in the
    /// middle). After the save files the shared-progress ledger goes back to the backup's time, each restored file the game
    /// keeps in Steam Cloud is sent there through the game's own platform save (one Steam doesn't take stays listed until a
    /// later start sends it, <see cref="CloudPushList"/>), and every save write is refused until the game restarts. A co-op slot alone
    /// needs none of that. False, with why, when nothing was restored (or, rarely, when a failed restore couldn't be put back;
    /// the reason says so). Never while a check tour runs (refused first, before anything is read).
    /// </summary>
    public static bool TryRestore(string name, RestoreParts parts, out string problem) => TryRestore(name, parts, RestoreAsker.Panel, out problem);

    /// <summary>
    /// <see cref="TryRestore(string, RestoreParts, out string)"/> for <paramref name="asker"/>. The Backups screen is opened over the game's Settings
    /// menu, which is on the title's list while it's open: the title counts as idle with only Settings and the mod's screens over it
    /// (<see cref="BackupRestoreTitle"/>). After the saves the screen tells the player to restart on its own prompt (Later or Quit), so the
    /// restart notice isn't sent from here then; the screen sends it should its prompt not come.
    /// </summary>
    public static bool TryRestore(string name, RestoreParts parts, RestoreAsker asker, out string problem)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (NativeLobby.TourHolds.On)
        {
            problem = "a check tour runs, and it restores nothing";
            ModLog.Info("Backups: a restore refused: the check tour runs.");
            return false;
        }

        StartUp();

        // Every condition first: nothing is written unless all of them hold.
        if (RestartNeeded)
        {
            problem = "a restore is waiting for the game to restart; restart it first";
            return false;
        }

        if (RestoreUnfinished)
        {
            problem = "the last restore is unfinished, and the next start of the game finishes it; restart it first";
            return false;
        }

        if (CampaignRelay.Current != null)
        {
            problem = "only outside an online session; leave it first";
            return false;
        }

        if (!TitleAllowsRestore(asker))
        {
            problem = asker == RestoreAsker.BackupsScreen ? "only at the title screen, with nothing but Settings open on it" : "only at the title screen, with nothing open on it";
            return false;
        }

        if (string.IsNullOrEmpty(name) || !SaveBackupManifest.IsPlainFileName(name) || !SaveBackupNames.TryParse(name, out _, out _))
        {
            problem = "there's no backup by that name";
            return false;
        }

        string folder = Path.Combine(CampaignFiles.BackupsFolder, name);
        SaveBackupManifest? manifest = Directory.Exists(folder) ? ReadManifest(folder) : null;
        if (manifest == null)
        {
            problem = Directory.Exists(folder) ? "its manifest is missing or damaged" : "there's no backup by that name";
            return false;
        }

        // Exactly what was asked for: the screen's rows each restore one part (BackupRow.Parts).
        if (asker == RestoreAsker.BackupsScreen && parts.Saves && parts.Slots.Count > 0)
        {
            problem = "the Backups screen restores the saves or one co-op slot, not both at once";
            return false;
        }

        if (!parts.TryCheck(manifest, out problem))
        {
            return false;
        }

        foreach (int slot in parts.Slots)
        {
            if (slot > CoopSlots.Count)
            {
                problem = $"this version has co-op slots 1 to {CoopSlots.Count} only";
                return false;
            }

            // A slot that only waits for the title to be idle to stop being the run file (hosting stopped under the menu this
            // was asked from) is let go now; one still in use isn't.
            if (RunSync.Instance is { } sync && !sync.FreeSlotForRestore(slot))
            {
                problem = $"co-op slot {slot} is the game's run file now (it switches back to your solo run at the title screen outside a session; try again in a moment)";
                return false;
            }
        }

        if (!Verify(folder, manifest, out Dictionary<string, byte[]> files, out Dictionary<int, byte[]> slots, out problem))
        {
            return false;
        }

        string saves;
        try
        {
            saves = UnityEngine.Application.persistentDataPath;
        }
        catch (Exception ex)
        {
            problem = $"the game's save folder isn't known ({CampaignFiles.ErrorText(ex)})";
            return false;
        }

        if (parts.Saves && !SaveGuard.Install(out string guardProblem))
        {
            problem = $"the save guard isn't available ({guardProblem}), so the game would save over the restored files";
            return false;
        }

        // The current files and slots, so the restore itself can be undone (and a failed one put back). Pruning never
        // removes the backup being restored.
        Dictionary<string, byte[]> current;
        Dictionary<int, byte[]> currentSlots;
        try
        {
            current = ReadSaveFiles(saves);
            currentSlots = ReadCoopSlots();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = $"your current saves couldn't be read first ({CampaignFiles.ErrorText(ex)}); nothing was restored";
            return false;
        }

        // What's put in place, each with what was there (null: nothing). One that exists but couldn't be backed up first
        // (too big, or it appeared meanwhile) couldn't be put back: then nothing is restored.
        var targets = new List<Target>();
        if (parts.Saves)
        {
            foreach (SaveFileHash file in manifest.Files)
            {
                string path = Path.Combine(saves, file.Name);
                if (File.Exists(path) && !current.ContainsKey(file.Name))
                {
                    problem = $"your current {file.Name} couldn't be backed up first, so it isn't replaced; nothing was restored";
                    return false;
                }

                targets.Add(new Target(file.Name, path, files[file.Name], current.GetValueOrDefault(file.Name)));
            }
        }

        foreach (int slot in parts.Slots)
        {
            string path = CampaignFiles.SlotPath(slot);
            if (File.Exists(path) && new FileInfo(path).Length > 0 && !currentSlots.ContainsKey(slot))
            {
                problem = $"your current co-op slot {slot} couldn't be backed up first, so it isn't replaced; nothing was restored";
                return false;
            }

            targets.Add(new Target($"co-op slot {slot}", path, slots[slot], currentSlots.GetValueOrDefault(slot)));
        }

        // The "before" reason names the restored backup exactly when the saves go back (an older Steam Cloud list's upgrade looks
        // for it, FromOlderList). The per-run counters as they are now go with it (the restore's plan starts from them; a restart
        // finishing it, too).
        string reason = parts.BeforeReason(name);
        string? countersNow = Progress.ProgressRunScopes.SnapshotText();
        Store(current, currentSlots, countersNow, "restore", reason, protect: name, out string before, out _, out string takeProblem);
        if (takeProblem.Length > 0)
        {
            problem = $"your current saves couldn't be backed up first ({takeProblem}); nothing was restored";
            return false;
        }

        // Steam Cloud's copies as they are now, for a later start that finds a restored file still waiting for Steam
        // Cloud: only a copy the restore replaced may be overwritten then.
        List<SaveFileHash> restoredFiles = parts.Saves ? manifest.Files.ToList() : new List<SaveFileHash>();
        Dictionary<string, SteamCopy> steamAtRestore = ReadSteamCopies(restoredFiles);
        RunScopeStore? backupCounters = ReadCounters(folder, manifest, name);

        var journal = new RestoreJournal(name, before, parts.Saves ? manifest.Files.Select(f => f.Name) : Array.Empty<string>(), parts.Slots);
        if (!Replace(targets, journal, parts, out problem))
        {
            return false;
        }

        // What follows the moves, then the journal goes: a game stopping before that finishes it at its next start.
        AfterMoves(new AfterRestore(name, manifest.TakenAtUtc, restoredFiles, parts.Slots, HoldsSoloRun(manifest),
            countersNow != null ? RunScopeStore.Parse(countersNow) : null, backupCounters,
            DateTime.UtcNow, current.ToDictionary(f => f.Key, f => (string?)SaveFileHash.HashOf(f.Value), StringComparer.OrdinalIgnoreCase), steamAtRestore));
        DeleteJournal();

        // Steam Cloud gets each restored file it keeps now (Steam's copy would come back at the next load); a local-only file
        // (config.es3) isn't sent. One Steam took is in its storage on this PC and goes up as the game closes: it leaves the
        // list, so no later start sends it again over what Steam Cloud holds by then (another PC's newer save, or the copy the
        // player picked in Steam's own conflict dialog). One it didn't take stays listed: the next start sends it before the
        // game loads it (a game stopping before this line has them all listed).
        var notPushed = new List<string>();
        var pushed = new List<string>();
        foreach (SaveFileHash file in restoredFiles.Where(f => SaveBackupNames.IsCloudSaved(f.Name)))
        {
            (Push(file.Name) ? pushed : notPushed).Add(file.Name);
        }

        Unlist(pushed);

        bool held = !parts.Saves || SaveGuard.HoldUntilRestart($"backup {LogName(name)} restored");
        RestartNeeded |= parts.Saves;
        string restored = string.Join(", ", targets.Select(t => t.Label));
        List<string> left = parts.Saves
            ? current.Keys.Where(f => !manifest.Files.Any(m => string.Equals(m.Name, f, StringComparison.OrdinalIgnoreCase))).ToList()
            : new List<string>();
        ModLog.Info($"Backups: restored {parts.Describe()} from backups/{LogName(name)} ({LogReason(manifest.Reason)}): {restored}; what was there before is in backups/{LogName(before)}" +
            (left.Count > 0 ? $"; left as they were (not in the backup): {string.Join(", ", left)}." : "."));
        CheckRestored(name, restoredFiles, parts.Slots.Select(manifest.CoopSlot).OfType<CoopSlotHash>().ToList(), parts.Saves);
        if (restoredFiles.Count > 0)
        {
            int cloud = restoredFiles.Count(f => SaveBackupNames.IsCloudSaved(f.Name));
            ModLog.Info(notPushed.Count == 0
                ? $"Backups: the {cloud} restored file(s) the game keeps in Steam Cloud were sent there (Steam uploads them as the game closes), so Steam's older copy can't come back."
                : $"Backups: Steam Cloud didn't take {string.Join(", ", notPushed)} now (Steam not running, cloud saves off for this game, no room, or save writes refused here); " +
                  $"they stay listed, and the next start sends them before the game loads them, keeping the restored files{(pushed.Count > 0 ? $" ({pushed.Count} other(s) were sent)" : string.Empty)}.");
        }

        if (!held)
        {
            ModLog.Warn("Backups: the save guard couldn't hold, so the game may save over the restored files before it restarts: restart it right away.");
        }

        if (parts.Saves && asker == RestoreAsker.BackupsScreen)
        {
            ModLog.Info("Backups: the Backups screen says the restart itself (no notice).");
        }
        else
        {
            RunSync.Instance?.Notify(parts.Saves ? NoticeId.RestoredRestart : NoticeId.RestoredSlots, $"Restored {parts.Describe()} from the backup.");
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>The title is there and quiet enough for a restore asked by <paramref name="asker"/> (<see cref="BackupRestoreTitle"/>).</summary>
    private static bool TitleAllowsRestore(RestoreAsker asker)
    {
        if (asker == RestoreAsker.Panel)
        {
            return CampaignGame.IdleTitle() != null;
        }

        Il2CppTape2Tape.Hockey.UI.TitleScreen? title = CampaignGame.Title();
        if (title == null)
        {
            return false;
        }

        var over = new List<string>();
        Il2CppSystem.Collections.Generic.List<string>? menus = title.m_MenusOverSelf;
        for (int i = 0; menus != null && i < menus.Count; i++)
        {
            over.Add(menus[i] ?? string.Empty);
        }

        bool busy = title.m_IsStartingRun || title.m_IsExiting || title.m_IsClickingButton || title.m_IsRefreshingCampaignState;
        return BackupRestoreTitle.Allows(asker, busy, over, NativeLobby.LobbyShell.IsShellId);
    }

    /// <summary>
    /// What each co-op slot copy in backup <paramref name="backup"/> holds, for the Backups screen's Co-op Runs: its hash (the manifest's) and its run (squad, act,
    /// floor, lost), read from the copy. A copy's run is read once a game session: copies are known by their hash, and a slot unchanged across backups is one
    /// file. Empty when the manifest can't be read. Never throws.
    /// </summary>
    public static IReadOnlyList<BackupSlotRun> SlotRuns(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var runs = new List<BackupSlotRun>();
        try
        {
            if (backup.Slots.Count == 0 || !SaveBackupManifest.IsPlainFileName(backup.Name) || !SaveBackupNames.TryParse(backup.Name, out _, out _))
            {
                return runs;
            }

            string folder = Path.Combine(CampaignFiles.BackupsFolder, backup.Name);
            SaveBackupManifest? manifest = Directory.Exists(folder) ? ReadManifest(folder) : null;
            foreach (CoopSlotHash slot in manifest?.CoopSlots ?? Array.Empty<CoopSlotHash>())
            {
                if (slot.Slot < 1 || slot.Slot > CoopSlots.Count)
                {
                    continue;
                }

                if (!SlotRunsRead.TryGetValue(slot.Sha256, out BackupSlotRun? known))
                {
                    known = ReadSlotRun(folder, slot);
                    if (known.Kind != BackupRunKind.Unreadable || slot.Bytes > RunCheckpoint.MaxPartBytes)
                    {
                        SlotRunsRead[slot.Sha256] = known;
                    }
                }

                runs.Add(known with { Slot = slot.Slot, Sha256 = slot.Sha256 });
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: couldn't read the co-op slots of backups/{LogName(backup.Name)} ({ModLog.SafeErrorText(ex)}).");
        }

        return runs;
    }

    private static BackupSlotRun ReadSlotRun(string folder, CoopSlotHash slot)
    {
        var unreadable = new BackupSlotRun(slot.Slot, slot.Sha256, BackupRunKind.Unreadable, string.Empty, 0, 0);
        try
        {
            string path = Path.Combine(folder, SaveBackupManifest.CoopFolder, slot.FileName);
            if (slot.Bytes > RunCheckpoint.MaxPartBytes || !File.Exists(path) ||
                !RunFileSummary.TryRead(CampaignFiles.ReadShared(path), out RunFileSummary? summary) || summary == null || summary.SquadId.Length == 0)
            {
                return unreadable;
            }

            return new BackupSlotRun(slot.Slot, slot.Sha256, summary.Failed ? BackupRunKind.Lost : BackupRunKind.Run, summary.SquadId, summary.Act, summary.Floor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't read co-op slot {slot.Slot} in a backup ({CampaignFiles.ErrorText(ex)}).");
            return unreadable;
        }
    }

    /// <summary>
    /// Puts each of <paramref name="targets"/> in place (a save file, or a co-op slot): each is written beside its target
    /// first, then the journal, then all are moved into place, then the journal again ("all moved"). When one can't be
    /// written nothing changes; when one can't be moved, those already moved get what was there back (or go, if nothing
    /// was). False, with why; then, if the files couldn't be put back, the journal stays (the next start finishes the
    /// restore) and, for save files, every save write is refused until the game restarts.
    /// </summary>
    private static bool Replace(List<Target> targets, RestoreJournal journal, RestoreParts parts, out string problem)
    {
        var staged = new List<Target>();
        foreach (Target target in targets)
        {
            try
            {
                staged.Add(target);
                string? folder = Path.GetDirectoryName(target.Path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                WriteToDisk(target.Path + CampaignFiles.PartSuffix, target.Bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteParts(staged);
                string error = CampaignFiles.ErrorText(ex);
                ModLog.Error($"Backups: restoring backups/{LogName(journal.Backup)}: couldn't write {target.Label} ({error}); nothing was restored.");
                problem = $"{target.Label} couldn't be written ({error}); nothing was restored";
                return false;
            }
        }

        // From the first move until the restore is whole (or put back) and what follows it is done, a journal says what's
        // under way: a game that stops in between finishes it at its next start. Without one that couldn't happen, so no
        // file is moved.
        if (!WriteJournal(journal, out string journalError))
        {
            DeleteParts(staged);
            ModLog.Error($"Backups: restoring backups/{LogName(journal.Backup)}: couldn't write the restore's journal ({journalError}); nothing was restored.");
            problem = $"the restore's journal couldn't be written ({journalError}); nothing was restored";
            return false;
        }

        var moved = new List<Target>();
        foreach (Target target in staged)
        {
            try
            {
                File.Move(target.Path + CampaignFiles.PartSuffix, target.Path, overwrite: true);
                moved.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string error = CampaignFiles.ErrorText(ex);
                ModLog.Error($"Backups: restoring backups/{LogName(journal.Backup)}: couldn't put {target.Label} in place ({error}); putting back the {moved.Count} already restored.");
                bool undone = PutBack(moved);
                DeleteParts(staged);
                if (undone)
                {
                    DeleteJournal();
                    problem = $"{target.Label} couldn't be replaced ({error}); nothing was restored";
                    return false;
                }

                // The journal stays: the next start finishes the restore from the backup (or undoes it).
                RestoreUnfinished = true;
                if (parts.Saves)
                {
                    RestartNeeded = true;
                    SaveGuard.HoldUntilRestart($"backup {LogName(journal.Backup)} only partly restored");
                }

                problem = $"{target.Label} couldn't be replaced ({error}) and what was restored before it couldn't be put back, so it's only partly restored; " +
                    (parts.Saves ? "restart the game: the next start finishes the restore" : "the next start of the game finishes the restore");
                Notice = $"The last restore was only partly done: the next start of the game finishes it (from the backup \"{journal.Backup}\").";
                return false;
            }
        }

        WriteJournal(journal.AllMoved());
        problem = string.Empty;
        return true;
    }

    /// <summary>Writes a restore's staged file through to the disk: the rename that follows can't reach it before its bytes do, so a power cut leaves the old file or the new one, never one of zeros.</summary>
    private static void WriteToDisk(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Gives each of <paramref name="moved"/> what was there before (deleting one that wasn't there). True when all went back.</summary>
    private static bool PutBack(List<Target> moved)
    {
        bool all = true;
        foreach (Target target in moved)
        {
            try
            {
                if (target.Before != null)
                {
                    CampaignFiles.WriteAtomically(target.Path, target.Before);
                }
                else
                {
                    File.Delete(target.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                all = false;
                ModLog.Error($"Backups: couldn't put {target.Label} back as it was ({CampaignFiles.ErrorText(ex)}).");
            }
        }

        return all;
    }

    private static void DeleteParts(List<Target> staged)
    {
        foreach (Target target in staged)
        {
            try
            {
                File.Delete(target.Path + CampaignFiles.PartSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Backups: couldn't remove the unfinished copy of {target.Label} ({CampaignFiles.ErrorText(ex)}).");
            }
        }
    }

    /// <summary>
    /// An online session starts (<paramref name="role"/>: host or friend): the saves and co-op slots are backed up and hashed
    /// for the end-of-session save check. A backup that fails is said on screen too.
    /// </summary>
    public static void BeginSession(string role, string reason)
    {
        if (NativeLobby.TourHolds.On)
        {
            // The self-driving check tour hosts for real, under its save guard: a tour rotates none of the player's backups, and writes nothing here.
            ModLog.Info($"Backups: none taken ({reason}): the check tour runs, and its save guard holds every game save.");
            _session = null;
            _sessionName = string.Empty;
            return;
        }

        StartUp();
        if (RestartNeeded)
        {
            ModLog.Warn("Backups: a restored backup is waiting for the game to restart, so nothing is saved this session. Restart the game first.");
            RunSync.Instance?.Notify(NoticeId.RestoredRestart);
        }

        _session = Take(role, reason, out string name, out _, out string problem);
        _sessionName = name;
        if (_session == null)
        {
            ModLog.Warn($"Backups: your saves couldn't be backed up before the session ({problem}); the end-of-session save check is off.");
            Tell(NoticeId.BackupFailed, "Your saves couldn't be backed up before this session (see the log).");
        }
        else if (problem.Length > 0)
        {
            ModLog.Warn($"Backups: your saves couldn't be copied before the session ({problem}); they were hashed, so the end-of-session save check still runs.");
            Tell(NoticeId.BackupFailed, "Your saves couldn't be backed up before this session (see the log); the save check after it still runs.");
        }
    }

    /// <summary>
    /// The session ended: every save file and co-op slot is hashed again and logged, unchanged or changed, against the
    /// session's start (the save check). A friend's run file or co-op slot changing is a warning; so is a host's solo run
    /// changing away from <paramref name="soloRunSinceCoop"/>, its hash ("" for no file) when a co-op slot became the game's
    /// run file (null: no co-op slot was). A host's co-op slot changes with its co-op run. Warnings are said on screen too.
    /// </summary>
    public static void EndSession(bool friend, string? soloRunSinceCoop)
    {
        SaveBackupManifest? start = _session;
        string name = _sessionName;
        _session = null;
        _sessionName = string.Empty;
        if (start == null)
        {
            ModLog.Info("Save check: skipped (no hashes from the session's start).");
            return;
        }

        Dictionary<string, byte[]> now;
        Dictionary<int, byte[]> nowSlots;
        try
        {
            now = ReadSaveFiles(UnityEngine.Application.persistentDataPath);
            nowSlots = ReadCoopSlots();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Save check: the save files couldn't be read ({CampaignFiles.ErrorText(ex)}).");
            return;
        }

        List<SaveFileHash> after = now.Select(f => SaveFileHash.Of(f.Key, f.Value)).ToList();
        List<SaveFileHash> slotsAfter = nowSlots.Select(s => CoopSlotHash.Of(s.Key, s.Value).AsFile()).ToList();
        int unchanged = 0;
        var warned = new List<string>();
        string copyAt = name.Length > 0 ? $"backups/{LogName(name)}" : "the hashes taken at the start (the copy failed)";
        foreach (SaveFileDifference difference in SaveBackupManifest.Compare(start.Files, after))
        {
            if (difference.Change == SaveFileChange.Unchanged)
            {
                unchanged++;
                ModLog.Info($"Save check: {difference.Name} unchanged.");
                continue;
            }

            string what = Changed(difference);
            bool runFile = string.Equals(difference.Name, RunSync.SoloRunFile, StringComparison.OrdinalIgnoreCase);
            string nowHash = after.FirstOrDefault(f => string.Equals(f.Name, difference.Name, StringComparison.OrdinalIgnoreCase))?.Sha256 ?? string.Empty;
            if (runFile && friend)
            {
                warned.Add(difference.Name);
                ModLog.Warn($"Save check: {difference.Name} {what} during the session on this friend's PC: a friend's run file must never be written. Your run from before is in {copyAt}.");
            }
            else if (runFile && soloRunSinceCoop != null && !string.Equals(nowHash, soloRunSinceCoop, StringComparison.OrdinalIgnoreCase))
            {
                warned.Add(difference.Name);
                ModLog.Warn($"Save check: {difference.Name} (your solo run) {what} while a co-op slot was the game's run file: it should have been left alone. Your run from before is in {copyAt}.");
            }
            else
            {
                // A host's solo run may change: played while hosting before the title switched to the co-op slot (not shared).
                string note = !runFile || friend ? string.Empty
                    : soloRunSinceCoop != null ? " (your solo run, played before a co-op slot took over)"
                    : " (your solo run, played while hosting; it isn't shared)";
                ModLog.Info($"Save check: {difference.Name} {what}{note}.");
            }
        }

        // The co-op slots: a host's changes with its co-op run; a friend's session never writes one.
        foreach (SaveFileDifference difference in SaveBackupManifest.Compare(start.CoopSlots.Select(s => s.AsFile()), slotsAfter))
        {
            if (difference.Change == SaveFileChange.Unchanged)
            {
                unchanged++;
                ModLog.Info($"Save check: {difference.Name} (a co-op slot) unchanged.");
            }
            else if (friend)
            {
                warned.Add(difference.Name);
                ModLog.Warn($"Save check: {difference.Name} (a co-op slot) {Changed(difference)} during the session on this friend's PC: a friend's session never writes a co-op slot. It was in {copyAt}.");
            }
            else
            {
                ModLog.Info($"Save check: {difference.Name} {Changed(difference)} (your co-op run).");
            }
        }

        int total = after.Count + slotsAfter.Count;
        string summary = $"Save check ({(friend ? "friend" : "host")}, against {copyAt}): {start.Files.Count} files and {start.CoopSlots.Count} co-op slot(s) at the start, " +
            $"{after.Count} and {slotsAfter.Count} now, {unchanged} unchanged{(warned.Count > 0 ? $", {warned.Count} warning(s)" : string.Empty)}.";
        LastCheck = warned.Count > 0
            ? $"The last session changed {string.Join(", ", warned)}, which it shouldn't have; " +
              (name.Length > 0 ? $"your saves from before it are in the backup {LogName(name)}." : "no backup of them could be taken before it.")
            : $"The last session's save check found nothing wrong ({unchanged} of {total} files unchanged).";
        if (warned.Count > 0)
        {
            ModLog.Warn(summary);
            Tell(NoticeId.SaveCheck, $"The save check found {string.Join(", ", warned)} changed by the session (see the log); your saves from before are in {Core.OnlineSettings.PlayersBuild.SaveBackups}.");
        }
        else
        {
            ModLog.Info(summary);
        }
    }

    /// <summary>The solo run file's hash now ("" when there's none), or null when it can't be read.</summary>
    public static string? SoloRunHash()
    {
        try
        {
            string path = Path.Combine(UnityEngine.Application.persistentDataPath, RunSync.SoloRunFile);
            return File.Exists(path) ? SaveFileHash.HashOf(CampaignFiles.ReadShared(path)) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't hash your solo run ({CampaignFiles.ErrorText(ex)}).");
            return null;
        }
    }

    // ---- Steam Cloud: restored files stay restored ----

    /// <summary>
    /// The game is about to load <paramref name="file"/> through Steam Cloud (<c>SteamService.Load</c> writes Steam's copy
    /// over the local file). True when the load must keep the local file: <paramref name="file"/> is a restored file Steam
    /// Cloud must still get (<see cref="CloudPushList"/>), the local file is still the restored one, and Steam's copy is
    /// still the one the restore replaced (<see cref="CloudPushDecision"/>, decided once for the whole list); it's sent to
    /// Steam Cloud first (<paramref name="service"/>'s own <c>Save</c>), and leaves the list once there. A listed file that
    /// changed since (the game saved it, which also sent it), one Steam already holds, and every file of a restore that
    /// yields to another device's newer saves leave the list, and the load goes on. Never throws.
    /// </summary>
    public static bool KeepRestoredFile(SteamService service, string file)
    {
        try
        {
            CloudPushList? list;
            lock (CloudGate)
            {
                list = _cloud;
            }

            if (list?.Find(file) == null)
            {
                return false;
            }

            if (list.Judge(file, LocalHash(file)) != CloudPushState.Restored)
            {
                Drop(file, "it changed since it was restored (the game saved it, which also sends it to Steam Cloud)");
                return false;
            }

            if (SaveGuard.Refusing || RestartNeeded)
            {
                // A push now would be refused (and reported as done): the local file stays, and it's sent later.
                bool first;
                lock (CloudGate)
                {
                    first = KeptNoted.Add(file);
                }

                if (first)
                {
                    ModLog.Info($"Backups: the game loaded {file} while save writes are refused; the restored file is kept and sent to Steam Cloud at a later start.");
                }

                return true;
            }

            switch (Choice(file).Verdict)
            {
                case CloudPushVerdict.Yield:
                    return false; // off the list: the game loads Steam Cloud's copy
                case CloudPushVerdict.AlreadyThere:
                    Drop(file, "Steam Cloud already holds the restored copy, so the game's load writes the same bytes");
                    return false;
                case CloudPushVerdict.KeepLocal:
                    return true; // not sent over a copy that couldn't be read; checked again at the next start
            }

            bool pushed;
            try
            {
                pushed = service.Save(file);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Backups: sending the restored {file} to Steam Cloud failed ({CampaignFiles.ErrorText(ex)}).");
                pushed = false;
            }

            if (pushed)
            {
                Drop(file, $"sent to Steam Cloud as the game loaded it (restored from backups/{LogName(list.Entry(file)?.Backup ?? list.Backup)}; the load kept the restored file)");
            }
            else
            {
                NotSent(file, list.Entry(file)?.Backup ?? list.Backup, "the game keeps the restored file, and Steam's copy isn't loaded over it");
            }

            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: checking {file} against the restored files failed ({ex.GetType().Name}); the game loads it as usual.");
            return false;
        }
    }

    /// <summary>
    /// At the title screen, once per game session (Steam is up by then): the listed restored files the game didn't load on
    /// its way there go to Steam Cloud now (<c>StorageUtils.SaveToSteam</c>), as Steam's copies allow
    /// (<see cref="CloudPushDecision"/>). Not while save writes are refused (a refused push reports success). Never throws.
    /// </summary>
    public static void PushRestoredAtTitle()
    {
        ReadRestoredAtTitle();
        if (_cloudTitleDone || !CloudPending || SaveGuard.Refusing || RestartNeeded)
        {
            return;
        }

        _cloudTitleDone = true;
        try
        {
            CloudPushList? list;
            lock (CloudGate)
            {
                list = _cloud;
            }

            foreach (CloudPushEntry entry in list?.Entries ?? Array.Empty<CloudPushEntry>())
            {
                if (list!.Judge(entry.Name, LocalHash(entry.Name)) != CloudPushState.Restored)
                {
                    Drop(entry.Name, "it changed since it was restored (the game saved it, which also sends it to Steam Cloud)");
                    continue;
                }

                switch (Choice(entry.Name).Verdict)
                {
                    case CloudPushVerdict.Yield:
                        break; // off the list already
                    case CloudPushVerdict.AlreadyThere:
                        Drop(entry.Name, "Steam Cloud already holds the restored copy");
                        break;
                    case CloudPushVerdict.KeepLocal:
                        NotSent(entry.Name, entry.Backup, "it's kept as restored here, and not sent over a copy that couldn't be read");
                        break;
                    default:
                        if (Push(entry.Name))
                        {
                            Drop(entry.Name, $"sent to Steam Cloud at the title screen (restored from backups/{LogName(entry.Backup)})");
                        }
                        else
                        {
                            NotSent(entry.Name, entry.Backup, "it's kept as restored here");
                        }

                        break;
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: sending the restored files to Steam Cloud failed ({ex.GetType().Name}); tried again at the next start.");
        }
    }

    /// <summary>
    /// The verdict on listed file <paramref name="file"/> against Steam's copy. The first call of a game
    /// session decides every listed file at once (<see cref="DecideCloud"/>), so a restore yields whole before any of its
    /// files is sent; a file not decided then (listed later) is pushed, as a restore's own push is. Never throws.
    /// </summary>
    private static CloudPushChoice Choice(string file)
    {
        DecideCloud();
        lock (CloudGate)
        {
            return _cloudChoices != null && _cloudChoices.TryGetValue(file, out CloudPushChoice choice)
                ? choice
                : new CloudPushChoice(CloudPushVerdict.Push, "listed after this game session's check");
        }
    }

    /// <summary>
    /// Once per game session, before the first push: Steam Cloud's copy of every listed restored file still as restored is
    /// read, and each file decided (<see cref="CloudPushDecision.DecideAll"/>), each decision logged. A restore that yields
    /// (another device saved since) leaves the list whole: the game's loads take Steam Cloud's copies, the restored ones stay
    /// in their backup, and the player is told (status box and the panel's Backups tab). Never throws.
    /// </summary>
    private static void DecideCloud()
    {
        if (Volatile.Read(ref _cloudDecided))
        {
            return;
        }

        // A second load asking meanwhile waits for the decision: nothing is sent before every file is decided.
        lock (DecisionGate)
        {
            if (!_cloudDecided)
            {
                DecideCloudOnce();
                Volatile.Write(ref _cloudDecided, true);
            }
        }
    }

    private static void DecideCloudOnce()
    {
        CloudPushList? list;
        lock (CloudGate)
        {
            list = _cloud;
        }

        if (list == null)
        {
            return;
        }

        try
        {
            List<CloudPushEntry> restored = list.Entries.Where(e => list.Judge(e.Name, LocalHash(e.Name)) == CloudPushState.Restored).ToList();
            var steam = new Dictionary<string, (SteamCopy Copy, string Why)>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, CloudPushChoice> choices = CloudPushDecision.DecideAll(restored, name =>
            {
                SteamCopy copy = SteamCloudCopies.Read(name, out string why);
                steam[name] = (copy, why);
                return copy;
            });

            foreach (CloudPushEntry entry in restored)
            {
                CloudPushChoice choice = choices[entry.Name];
                (SteamCopy copy, string why) = steam.TryGetValue(entry.Name, out var read) ? read : (SteamCopy.Unavailable, string.Empty);
                string line = $"Backups: Steam Cloud check of the restored {entry.Name} (backups/{LogName(entry.Backup)}, restored " +
                    $"{(entry.RestoredAtUtc is DateTime at ? CloudPushDecision.Utc(at) : "at a time not known")}): Steam's copy {copy.Describe()}{(why.Length > 0 ? $" ({why})" : string.Empty)}; " +
                    $"{choice.Why}: {Verb(choice.Verdict)}.";
                if (choice.Verdict == CloudPushVerdict.Yield)
                {
                    ModLog.Warn(line);
                }
                else
                {
                    ModLog.Info(line);
                }
            }

            lock (CloudGate)
            {
                _cloudChoices = new Dictionary<string, CloudPushChoice>(choices, StringComparer.OrdinalIgnoreCase);
            }

            foreach (IGrouping<string, CloudPushEntry> restore in restored.Where(e => choices[e.Name].Verdict == CloudPushVerdict.Yield)
                .GroupBy(e => e.Backup, StringComparer.OrdinalIgnoreCase))
            {
                Yield(restore.Key, restore.ToList(), steam);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: checking Steam Cloud's copies of the restored files failed ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)}); they're sent as before.");
        }
    }

    /// <summary>A restore whose files another device saved since: it yields to Steam Cloud whole, and the player is told.</summary>
    private static void Yield(string backup, IReadOnlyList<CloudPushEntry> files, Dictionary<string, (SteamCopy Copy, string Why)> steam)
    {
        Unlist(files.Select(f => f.Name).ToList());
        CloudPushEntry first = files.FirstOrDefault(f => steam.TryGetValue(f.Name, out var read) && read.Copy.TimeUtc != null) ?? files[0];
        DateTime? saved = steam.TryGetValue(first.Name, out var newest) ? newest.Copy.TimeUtc : null;
        string when = $"{(saved is DateTime at ? $"saved {CloudPushDecision.Utc(at)}" : "saved")} on another device, after the restore" +
            $"{(first.RestoredAtUtc is DateTime restoredAt ? $" at {CloudPushDecision.Utc(restoredAt)}" : string.Empty)}";
        ModLog.Warn($"Backups: Steam Cloud has a newer {first.Name} than the one restored from backups/{LogName(backup)} ({when}); the game uses Steam Cloud's copies " +
            $"({string.Join(", ", files.Select(f => f.Name))}), and the restored ones stay in backups/{LogName(backup)} (restore again to use them).");
        Tell(NoticeId.CloudNewer, $"Steam Cloud has newer saves from another device than the ones you restored from \"{backup}\": the game uses Steam Cloud's. " +
            $"The restored saves stay in the backup ({Core.OnlineSettings.PlayersBuild.SaveBackups}) if you want them back.", backup);
        Progress.ProgressRunScopes.CloudYielded(backup);
    }

    private static string Verb(CloudPushVerdict verdict) => verdict switch
    {
        CloudPushVerdict.Push => "the restored file is sent",
        CloudPushVerdict.KeepLocal => "the restored file is kept here, not sent",
        CloudPushVerdict.AlreadyThere => "off the list",
        _ => "the restore yields to Steam Cloud",
    };

    /// <summary>
    /// Sends <paramref name="file"/> (a save file's name) to Steam Cloud through the game's own platform save. True when Steam
    /// took it. False, not sent, while the save guard refuses save writes: its refusal of <c>SteamService.Save</c> reports
    /// success, and a file taken for sent leaves the list. Never throws.
    /// </summary>
    private static bool Push(string file)
    {
        if (SaveGuard.Refusing)
        {
            return false;
        }

        try
        {
            return StorageUtils.SaveToSteam(file);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: sending {file} to Steam Cloud failed ({CampaignFiles.ErrorText(ex)}).");
            return false;
        }
    }

    /// <summary><paramref name="file"/>'s SHA-256 in the game's save folder, or null when it isn't there or can't be read.</summary>
    private static string? LocalHash(string file) => LocalEntry(file)?.Sha256;

    /// <summary><paramref name="file"/> in the game's save folder, its size and hash, or null when it isn't there or can't be read.</summary>
    private static SaveFileHash? LocalEntry(string file)
    {
        try
        {
            string path = Path.Combine(UnityEngine.Application.persistentDataPath, file);
            return File.Exists(path) ? SaveFileHash.Of(file, CampaignFiles.ReadShared(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Takes <paramref name="file"/> off the Steam Cloud list (<paramref name="why"/> for the log).</summary>
    private static void Drop(string file, string why)
    {
        bool empty;
        lock (CloudGate)
        {
            if (_cloud?.Find(file) == null)
            {
                return;
            }

            CloudPushList rest = _cloud.Without(file);
            _cloud = rest.IsEmpty ? null : rest;
            Volatile.Write(ref _cloudCount, _cloud?.Files.Count ?? 0);
            empty = _cloud == null;
        }

        SaveCloudList();
        ModLog.Info($"Backups: {file} is off the list of restored files Steam Cloud must get: {why}." +
            (empty ? " The list is empty now." : string.Empty));
    }

    /// <summary>Takes <paramref name="files"/> off the Steam Cloud list at once, without a line each (the restore's own line says what was sent).</summary>
    private static void Unlist(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        lock (CloudGate)
        {
            if (_cloud == null)
            {
                return;
            }

            CloudPushList rest = _cloud.Without(files);
            _cloud = rest.IsEmpty ? null : rest;
            Volatile.Write(ref _cloudCount, _cloud?.Files.Count ?? 0);
        }

        SaveCloudList();
    }

    /// <summary>A listed file Steam Cloud didn't take: a warning, once per file and game session.</summary>
    private static void NotSent(string file, string backup, string then)
    {
        bool first;
        lock (CloudGate)
        {
            first = CloudWarned.Add(file);
        }

        if (first)
        {
            ModLog.Warn($"Backups: Steam Cloud didn't take {file}, restored from backups/{LogName(backup)} (Steam not running, cloud saves off for this game, or no room); " +
                $"{then}. It's tried again at the next start.");
        }
    }

    /// <summary>The restored files (<paramref name="entries"/>) of backup <paramref name="backup"/> that the game keeps in Steam Cloud go on the list (with any still on it).</summary>
    private static void ListForCloud(string backup, IEnumerable<CloudPushEntry> entries)
    {
        List<CloudPushEntry> cloudSaved = entries.Where(e => SaveBackupNames.IsCloudSaved(e.Name)).ToList();
        if (cloudSaved.Count == 0)
        {
            return;
        }

        var fresh = new CloudPushList(backup, cloudSaved);
        lock (CloudGate)
        {
            foreach (CloudPushEntry entry in cloudSaved)
            {
                _cloudChoices?.Remove(entry.Name); // a verdict on an earlier restore's copy doesn't stand for this one
            }

            _cloud = _cloud?.Then(fresh) ?? fresh;
            Volatile.Write(ref _cloudCount, _cloud.Files.Count);
        }

        SaveCloudList();
    }

    /// <summary>At start-up: the list of restored files Steam Cloud must still get, from beside the backups.</summary>
    private static void ReadCloudList()
    {
        string path = Path.Combine(CampaignFiles.BackupsFolder, CloudPushList.FileName);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (!CloudPushList.TryParse(File.ReadAllText(path), out CloudPushList? list) || list == null)
            {
                ModLog.Warn($"Backups: the list of restored files Steam Cloud must get ({CloudPushList.FileName}) can't be read; it's removed (Steam Cloud handles those files as usual).");
                File.Delete(path);
                return;
            }

            if (list.IsEmpty)
            {
                File.Delete(path);
                return;
            }

            bool older = list.Entries.Any(e => e.RestoredAtUtc == null && e.BeforeSha256 == null);
            if (older)
            {
                list = FromOlderList(list);
            }

            lock (CloudGate)
            {
                _cloud = list;
                Volatile.Write(ref _cloudCount, list.Files.Count);
            }

            if (older)
            {
                SaveCloudList();
            }

            string files = $"{list.Files.Count} file(s) restored from backups/{LogName(list.Backup)} wait to be in Steam Cloud ({string.Join(", ", list.Files.Select(f => f.Name))})";
            if (_cloudLoadsHeld)
            {
                ModLog.Info($"Backups: {files}: each is sent there as the game first loads it, and that load keeps the restored file, so Steam's older copy can't come back.");
            }
            else
            {
                // Nothing holds the game's loads from Steam Cloud on this start (a game build the mod can't patch): each load writes Steam's copy.
                ModLog.Warn($"Backups: {files}, but the game's loads from Steam Cloud aren't held this time (T2T Online can't patch this game version), so its first load " +
                    "of each writes Steam Cloud's copy over the restored one; the restored files stay in that backup: restore it again at a start with T2T Online on.");

                // Said once per restore: a later start finds the files the game loaded here already Steam's, and says nothing. A save
                // folder that can't be read yet counts as still restored (said rather than missed).
                bool asRestored;
                try
                {
                    asRestored = list.AnyAsRestored(LocalHash);
                }
                catch (Exception ex)
                {
                    asRestored = true;
                    ModLog.Info($"Backups: the restored files couldn't be compared with the save folder yet ({ModLog.SafeErrorText(ex)}); the player is told anyway.");
                }

                if (asRestored)
                {
                    Tell(NoticeId.CloudNotHeld, $"Steam Cloud may put older copies back over the saves restored from the backup \"{list.Backup}\": T2T Online can't keep " +
                        "them on this start. That backup still has them: restore it again once online play is back.", list.Backup);
                }
                else
                {
                    ModLog.Info("Backups: none of those files is still as restored (Steam Cloud's copies or later saves are in place), so nothing more is said of them.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: the list of restored files Steam Cloud must get couldn't be read ({CampaignFiles.ErrorText(ex)}).");
        }
    }

    /// <summary>
    /// A list an older version wrote (0.7: each file's restored hash only): each restore's "before" backup (the newest named
    /// "before restoring &lt;backup&gt;", taken just before it) gives the files' hashes from before and the restore's time,
    /// so a later check can still tell the copy the restore replaced. A restore whose "before" backup is gone stays with
    /// nothing known (it then yields to whatever newer copy Steam Cloud holds rather than overwrite it).
    /// </summary>
    private static CloudPushList FromOlderList(CloudPushList list)
    {
        var befores = new Dictionary<string, SaveBackupManifest?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> backups;
        try
        {
            List<string> names = Directory.GetDirectories(CampaignFiles.BackupsFolder).Select(d => Path.GetFileName(d)).ToList();
            backups = SaveBackupNames.NewestFirst(names, Clock(names));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            backups = Array.Empty<string>();
        }

        foreach (string restored in list.Entries.Where(e => e.RestoredAtUtc == null && e.BeforeSha256 == null).Select(e => e.Backup).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string reason = $"before restoring {restored}";
            string? found = backups.FirstOrDefault(b => ReadManifest(Path.Combine(CampaignFiles.BackupsFolder, b)) is { } m && m.Reason == reason);
            SaveBackupManifest? before = found != null ? ReadManifest(Path.Combine(CampaignFiles.BackupsFolder, found)) : null;
            befores[restored] = before;
            ModLog.Info(before != null
                ? $"Backups: the list of restored files Steam Cloud must get comes from an older version; the restore of backups/{LogName(restored)} is taken as at {CloudPushDecision.Utc(before.TakenAtUtc)}, with the files from before it in backups/{LogName(found)}."
                : $"Backups: the list of restored files Steam Cloud must get comes from an older version, and no backup from just before the restore of backups/{LogName(restored)} is left; " +
                  "its files go to Steam Cloud only where it holds no copy (or the restored one): any other copy there is kept, the restored ones staying in the backup.");
        }

        return list.Map(e => e.RestoredAtUtc != null || e.BeforeSha256 != null || befores.GetValueOrDefault(e.Backup) is not { } before ? e
            : e with
            {
                RestoredAtUtc = before.TakenAtUtc,
                BeforeSha256 = before.Files.FirstOrDefault(f => string.Equals(f.Name, e.Name, StringComparison.OrdinalIgnoreCase))?.Sha256 ?? CloudPushEntry.NoFileBefore,
            });
    }

    /// <summary>Writes the Steam Cloud list (or removes it when it's empty).</summary>
    private static void SaveCloudList()
    {
        CloudPushList? list;
        lock (CloudGate)
        {
            list = _cloud;
        }

        string path = Path.Combine(CampaignFiles.BackupsFolder, CloudPushList.FileName);
        try
        {
            if (list == null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            CampaignFiles.WriteAtomically(path, Encoding.UTF8.GetBytes(list.ToText()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: the list of restored files Steam Cloud must get couldn't be saved ({CampaignFiles.ErrorText(ex)}); " +
                "a restored file Steam Cloud didn't take may be replaced by Steam's copy at the next start.");
        }
    }

    /// <summary>
    /// Tells the player with the notice <paramref name="id"/> (a toast or a prompt, in their language), logs <paramref name="text"/> in full and keeps it for the
    /// panel's Backups tab. <paramref name="backup"/>, a backup <paramref name="text"/> names, is logged as <see cref="LogName"/> names it (an older backup's
    /// folder name is a local time); the panel shows its folder name.
    /// </summary>
    private static void Tell(NoticeId id, string text, string? backup = null)
    {
        Notice = text;
        string logged = string.IsNullOrEmpty(backup) ? text : text.Replace(backup, LogName(backup), StringComparison.Ordinal);
        ModLog.Info($"Backups: the player is told ({id}): {logged}");
        if (RunSync.Instance is { } sync)
        {
            sync.Notify(id);
        }
        else
        {
            StartUpNotices.Add(id); // the start-up recovery, before the session exists: it asks for them (TakeStartUpNotices)
        }
    }

    /// <summary>What was told before anything could say it (the recovery of an unfinished restore, at the mod's start): the session says them once, when the title is up.</summary>
    public static IReadOnlyList<NoticeId> TakeStartUpNotices()
    {
        var taken = new List<NoticeId>(StartUpNotices);
        StartUpNotices.Clear();
        return taken;
    }

    /// <summary>
    /// What follows a restore's moves, before its journal goes: the shared-progress ledger back to the restored backup's time
    /// when save files went back, those the game keeps in Steam Cloud listed until each is there (with what a later
    /// start needs to tell whether Steam's copy is still the one the restore replaced), and the per-run counters kept
    /// per run file set for what was restored (<see cref="RunScopeRestore.Plan"/>). Finishing it again is safe.
    /// </summary>
    private static void AfterMoves(AfterRestore restore)
    {
        if (restore.RestoredFiles.Count > 0)
        {
            if (restore.TakenAtUtc is DateTime takenAtUtc)
            {
                Progress.ProgressLedgerFile.Restored(restore.Backup, takenAtUtc);
            }

            ListForCloud(restore.Backup, restore.RestoredFiles.Select(f => new CloudPushEntry(f, restore.Backup, restore.RestoredAtUtc,
                restore.BeforeHashes == null ? null : restore.BeforeHashes.TryGetValue(f.Name, out string? was) ? was : CloudPushEntry.NoFileBefore,
                restore.SteamAtRestore.TryGetValue(f.Name, out SteamCopy steam) ? steam : SteamCopy.Unavailable)));
        }

        if (restore.RestoredFiles.Count == 0 && restore.Slots.Count == 0)
        {
            return;
        }

        // The counters from just before the restore: those the "before" backup kept (null when that backup came from an older
        // version or couldn't be read: then the store as it is, which only lacks the live run file's newest counters).
        try
        {
            RunScopeStore before = restore.CountersBefore ?? Progress.ProgressRunScopes.StoreCopy();
            if (restore.BackupCounters == null && restore.Slots.Count > 0)
            {
                ModLog.Info($"Backups: backups/{LogName(restore.Backup)} keeps no per-run counters (taken before T2T Online kept them, or they can't be read), so the restored co-op slots' start from zero.");
            }

            RunScopePlan plan = RunScopeRestore.Plan(before, restore.BackupCounters, restore.RestoredFiles.Count > 0, restore.BackupHoldsSoloRun,
                restore.Slots.Select(SaveBackupManifest.CoopSlotFileName).ToList(), RunSync.SoloRunFile);
            Progress.ProgressRunScopes.Restored(plan, $"the restore of backups/{LogName(restore.Backup)}");
        }
        catch (Exception ex)
        {
            // The files are restored whatever happens here: the counters stay as they were (the old behaviour).
            ModLog.Error($"Backups: the per-run counters couldn't be set for the restore of backups/{LogName(restore.Backup)}; they stay as they were", ex);
        }
    }

    /// <summary>
    /// Reads every save file and co-op slot, hashes them, and copies them into a new backup folder (written as
    /// <c>.partial</c>, then renamed), unless the newest backup already holds exactly these (then that one is the backup:
    /// <paramref name="reused"/>). Returns the manifest when the files were read (the hashes are good even if the copy
    /// failed, then with <paramref name="problem"/> set), or null with why. Then only the newest <see cref="Kept"/>, the
    /// first ever and the session's stay.
    /// </summary>
    private static SaveBackupManifest? Take(string role, string reason, out string name, out bool reused, out string problem)
    {
        name = string.Empty;
        reused = false;
        Dictionary<string, byte[]> files;
        Dictionary<int, byte[]> slots;
        try
        {
            files = ReadSaveFiles(UnityEngine.Application.persistentDataPath);
            slots = ReadCoopSlots();
        }
        catch (Exception ex)
        {
            problem = $"the save files couldn't be read ({CampaignFiles.ErrorText(ex)})";
            return null;
        }

        return Store(files, slots, Progress.ProgressRunScopes.SnapshotText(), role, reason, protect: null, out name, out reused, out problem);
    }

    /// <summary>
    /// <see cref="Take"/> with the files and slots already read, and the per-run counters (<paramref name="counters"/>,
    /// <see cref="Progress.ProgressRunScopes.SnapshotText"/>; null: none known) kept with them; never prunes <paramref name="protect"/>.
    /// Given one (a restore's backup of the saves it replaces), the newest backup stands for this one only when it holds
    /// exactly these bytes (<see cref="SaveBackupManifest.StandsFor"/>).
    /// </summary>
    private static SaveBackupManifest Store(Dictionary<string, byte[]> files, Dictionary<int, byte[]> slots, string? counters, string role, string reason, string? protect,
        out string name, out bool reused, out string problem)
    {
        name = string.Empty;
        reused = false;
        byte[]? counterBytes = counters == null ? null : new UTF8Encoding(false).GetBytes(counters);
        var manifest = new SaveBackupManifest(role, reason, DateTime.UtcNow, files.Select(f => SaveFileHash.Of(f.Key, f.Value)), slots.Select(s => CoopSlotHash.Of(s.Key, s.Value)),
            counterBytes == null ? null : SaveFileHash.Of(RunScopeRestore.FileName, counterBytes));
        string partial = string.Empty;
        try
        {
            Directory.CreateDirectory(CampaignFiles.BackupsFolder);
            List<string> existing = Directory.GetDirectories(CampaignFiles.BackupsFolder).Select(d => Path.GetFileName(d)).ToList();
            foreach (string leftover in existing.Where(n => n.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Delete(Path.Combine(CampaignFiles.BackupsFolder, leftover), recursive: true); // an earlier copy that never finished
            }

            // The same saves and slots as the newest backup (a session started again with nothing played): that backup is
            // this one, so repeated sessions don't push older, different backups out. Only an intact one counts. The game
            // writes its play-time counter into the profile whenever it saves it, with or without play (a return to the
            // title is enough), so a profile that differs only in that number is the same too; the backup then holds the
            // counter a few seconds behind, which a restore of it would bring back. Not for a restore's backup of the saves
            // it replaces (protect): the recovery of a restore that didn't finish reads its hashes as the replaced files'.
            IReadOnlyList<string> backups = SaveBackupNames.NewestFirst(existing, Clock(existing));
            string? newest = backups.Count > 0 ? backups[0] : null;
            string newestFolder = Path.Combine(CampaignFiles.BackupsFolder, newest ?? string.Empty);
            if (newest != null && ReadManifest(newestFolder) is { } latest)
            {
                BackupStandIn standIn = latest.StandsFor(manifest, exact: protect != null);
                bool same = standIn == BackupStandIn.Same;
                if (standIn != BackupStandIn.None &&
                    Verify(newestFolder, latest, out Dictionary<string, byte[]> kept, out _, out _) &&
                    (latest.RunScopes == null || TryReadCounterBytes(newestFolder, latest, out _, out _)) &&
                    (same || (kept.TryGetValue(ProfilePlayTime.ProfileFile, out byte[]? then) && files.TryGetValue(ProfilePlayTime.ProfileFile, out byte[]? now) &&
                              ProfilePlayTime.SameApartFromPlayTime(then, now))) &&
                    (protect == null || FlushBackup(newestFolder, latest)))
                {
                    name = newest;
                    reused = true;
                    problem = string.Empty;
                    ModLog.Info($"Backups: your {Contents(files.Count, slots.Keys)}{(counters != null ? " and per-run counters" : string.Empty)} are the same as in " +
                        $"backups/{LogName(name)}{(same ? string.Empty : $" but for the game's play-time counter in {ProfilePlayTime.ProfileFile}")}, which stays their backup ({LogReason(reason)}).");
                    return manifest;
                }
            }

            name = SaveBackupNames.Make(DateTime.UtcNow, role, existing);
            partial = Path.Combine(CampaignFiles.BackupsFolder, name + PartialSuffix);
            WriteBackup(partial, manifest, files, slots, counterBytes, toDisk: protect != null);
            Directory.Move(partial, Path.Combine(CampaignFiles.BackupsFolder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = $"the backup couldn't be written ({CampaignFiles.ErrorText(ex)})";
            ModLog.Warn($"Backups: {problem}.");
            TryDelete(partial);
            name = string.Empty;
            return manifest;
        }

        problem = string.Empty;
        ModLog.Info($"Backups: {Contents(files.Count, slots.Keys)} ({Kb(manifest.TotalBytes)}) backed up to backups/{LogName(name)} ({LogReason(reason)}): " +
            $"{string.Join(", ", files.Keys.Concat(slots.Keys.Select(SaveBackupManifest.CoopSlotFileName)))}{CountersKept(counters)}.");
        Prune(name, protect);
        return manifest;
    }

    /// <summary>"; per-run counters kept with them (RunDataV2.es3 live, coop-1.es3)", or "" when there are none.</summary>
    private static string CountersKept(string? counters)
    {
        if (counters == null)
        {
            return string.Empty;
        }

        RunScopeStore store = RunScopeStore.Parse(counters);
        IEnumerable<string> files = store.Files.Select(f => string.Equals(f, store.Live, StringComparison.OrdinalIgnoreCase) ? $"{f} live" : f);
        return $"; per-run counters kept with them ({(store.Count == 0 ? "none counted yet" : string.Join(", ", files))})";
    }

    /// <summary>
    /// Writes a backup's files, co-op slots and per-run counters (<paramref name="counters"/>; null: none) (both in its
    /// <c>coop</c> folder) and manifest into <paramref name="folder"/>. <paramref name="toDisk"/>: each is written through
    /// to the disk (<see cref="WriteToDisk"/>), for a restore's backup of what it is about to replace (and a recovery's): the
    /// files it replaces are written through too, so a power cut right after must not leave them replaced and this copy of
    /// them still unwritten. A backup taken before a session isn't followed by anything that replaces a save, and stays as it was.
    /// </summary>
    private static void WriteBackup(string folder, SaveBackupManifest manifest, Dictionary<string, byte[]> files, Dictionary<int, byte[]> slots, byte[]? counters,
        bool toDisk = false)
    {
        void Write(string path, byte[] bytes)
        {
            if (toDisk)
            {
                WriteToDisk(path, bytes);
            }
            else
            {
                File.WriteAllBytes(path, bytes);
            }
        }

        Directory.CreateDirectory(folder);
        foreach (KeyValuePair<string, byte[]> file in files)
        {
            Write(Path.Combine(folder, file.Key), file.Value);
        }

        if (slots.Count > 0 || counters != null)
        {
            string coop = Path.Combine(folder, SaveBackupManifest.CoopFolder);
            Directory.CreateDirectory(coop);
            foreach (KeyValuePair<int, byte[]> slot in slots)
            {
                Write(Path.Combine(coop, SaveBackupManifest.CoopSlotFileName(slot.Key)), slot.Value);
            }

            if (counters != null)
            {
                Write(Path.Combine(coop, RunScopeRestore.FileName), counters);
            }
        }

        // The manifest last: the same bytes either way (UTF-8, no byte order mark).
        Write(Path.Combine(folder, SaveBackupManifest.FileName), new UTF8Encoding(false).GetBytes(manifest.ToJson()));
    }

    /// <summary>
    /// An existing backup about to stand for a restore's backup of what it replaces: every file its manifest names, its per-run
    /// counters and its manifest are flushed to the disk (it may have been written a moment ago, as any backup is, without that).
    /// False when one can't be (the caller then writes a new backup, through to the disk).
    /// </summary>
    private static bool FlushBackup(string folder, SaveBackupManifest manifest)
    {
        try
        {
            var paths = manifest.Files.Select(f => Path.Combine(folder, f.Name))
                .Concat(manifest.CoopSlots.Select(s => Path.Combine(folder, SaveBackupManifest.CoopFolder, s.FileName))).ToList();
            if (manifest.RunScopes != null)
            {
                paths.Add(Path.Combine(folder, SaveBackupManifest.CoopFolder, RunScopeRestore.FileName));
            }

            paths.Add(Path.Combine(folder, SaveBackupManifest.FileName));
            foreach (string path in paths)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Info($"Backups: backups/{LogName(Path.GetFileName(folder))} couldn't be flushed to the disk ({CampaignFiles.ErrorText(ex)}), so it doesn't stand for the saves about to be replaced: a new backup is written.");
            return false;
        }
    }

    /// <summary>
    /// A backup's per-run counters (<see cref="RunScopeRestore.FileName"/> in its <c>coop</c> folder), checked against its
    /// manifest. False, with why, when the manifest names none or the copy is missing, damaged or unreadable.
    /// </summary>
    private static bool TryReadCounterBytes(string folder, SaveBackupManifest manifest, out byte[] bytes, out string problem)
    {
        bytes = Array.Empty<byte>();
        if (manifest.RunScopes == null)
        {
            problem = "it keeps none";
            return false;
        }

        if (!TryReadCopy(Path.Combine(folder, SaveBackupManifest.CoopFolder, RunScopeRestore.FileName), "per-run counters", out bytes, out problem))
        {
            return false;
        }

        if (SaveFileHash.Of(RunScopeRestore.FileName, bytes) != manifest.RunScopes)
        {
            problem = "its per-run counters don't match its manifest (changed or damaged)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Backup <paramref name="name"/>'s per-run counters (null: it keeps none, or they can't be used: logged, and a restore
    /// then starts its co-op slots' counters from zero rather than failing).
    /// </summary>
    private static RunScopeStore? ReadCounters(string folder, SaveBackupManifest manifest, string name)
    {
        if (manifest.RunScopes == null)
        {
            return null;
        }

        if (!TryReadCounterBytes(folder, manifest, out byte[] bytes, out string problem))
        {
            ModLog.Warn($"Backups: backups/{LogName(name)}'s per-run counters can't be used ({problem}); they're taken as none.");
            return null;
        }

        return RunScopeStore.Parse(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>True when <paramref name="manifest"/> holds the solo run file (<see cref="RunSync.SoloRunFile"/>).</summary>
    private static bool HoldsSoloRun(SaveBackupManifest manifest) =>
        manifest.Files.Any(f => string.Equals(f.Name, RunSync.SoloRunFile, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Steam Cloud's copy of each of <paramref name="files"/> the game keeps there, now (before a restore replaces them), by
    /// name; the line in the log says what Steam held.
    /// </summary>
    private static Dictionary<string, SteamCopy> ReadSteamCopies(IEnumerable<SaveFileHash> files)
    {
        var copies = new Dictionary<string, SteamCopy>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        string unknown = string.Empty;
        foreach (SaveFileHash file in files.Where(f => SaveBackupNames.IsCloudSaved(f.Name)))
        {
            SteamCopy copy = SteamCloudCopies.Read(file.Name, out string why);
            copies[file.Name] = copy;
            if (copy.State == SteamCopyState.Unavailable)
            {
                unknown = why;
                continue;
            }

            words.Add($"{file.Name} {copy.Describe()}{(why.Length > 0 ? $" ({why})" : string.Empty)}");
        }

        if (copies.Count > 0)
        {
            ModLog.Info(words.Count > 0
                ? $"Backups: Steam Cloud's copies before the restore: {string.Join(", ", words)}."
                : $"Backups: Steam Cloud's copies before the restore can't be read ({unknown}); a later start can only go by the restore's time.");
        }

        return copies;
    }

    /// <summary>"6 save files", "6 save files and co-op slots 1 and 2".</summary>
    private static string Contents(int files, IEnumerable<int> slots)
    {
        List<int> all = slots.ToList();
        string saves = files == 1 ? "1 save file" : $"{files} save files";
        return all.Count == 0 ? saves : $"{saves} and {new RestoreParts(false, all).Describe()}";
    }

    /// <summary>
    /// Keeps the newest <see cref="Kept"/> backups and the first of each of the newest <see cref="KeptDays"/> days, and removes
    /// the others, except the first one ever taken, the one just taken (<paramref name="taken"/>), the session's,
    /// <paramref name="protect"/>, and while a restore's journal is on disk the backup it restores and the one taken before it
    /// (<see cref="BackupRetention"/>): whatever the clock said when they were named, they stay. A journal that is there but
    /// can't be opened removes nothing this time.
    /// </summary>
    private static void Prune(string taken, string? protect)
    {
        try
        {
            List<string> names = Directory.GetDirectories(CampaignFiles.BackupsFolder).Select(d => Path.GetFileName(d)).ToList();
            BackupClock clock = Clock(names);
            string? first = FirstBackup(names, clock);
            var removed = new List<string>();
            RestoreJournal? journal = JournalOnDisk();
            foreach (string old in BackupRetention.Expired(names, Kept, KeptDays, new[] { first, taken, _sessionName, protect }, journal, clock))
            {
                // Only the mod's own backups go (a folder with a manifest).
                string folder = Path.Combine(CampaignFiles.BackupsFolder, old);
                if (!File.Exists(Path.Combine(folder, SaveBackupManifest.FileName)))
                {
                    continue;
                }

                Directory.Delete(folder, recursive: true);
                removed.Add(old);
            }

            if (removed.Count > 0)
            {
                ModLog.Info($"Backups: kept the newest {Kept}, the first of each of the last {KeptDays} days and the first ever ({LogName(first)}); removed {removed.Count} others: {string.Join(", ", removed.Select(LogName))}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't remove old backups ({CampaignFiles.ErrorText(ex)}).");
        }
    }

    /// <summary>
    /// The first backup ever taken (the saves from before the first online session), as its marker file names it; when
    /// there's no marker yet (or its backup is gone), the oldest by name becomes it and is marked. Null when there are none.
    /// </summary>
    private static string? FirstBackup(IReadOnlyCollection<string> names, BackupClock clock)
    {
        string marker = Path.Combine(CampaignFiles.BackupsFolder, FirstMarkerFile);
        try
        {
            if (File.Exists(marker))
            {
                string named = File.ReadAllText(marker).Trim();
                if (names.Contains(named, StringComparer.OrdinalIgnoreCase))
                {
                    return named;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't read which backup is the first ({CampaignFiles.ErrorText(ex)}).");
        }

        string? oldest = SaveBackupNames.Oldest(names.Where(n => !n.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)), clock);
        if (oldest != null)
        {
            try
            {
                File.WriteAllText(marker, oldest, new UTF8Encoding(false));
                ModLog.Info($"Backups: backups/{LogName(oldest)} is marked as the first backup; it's always kept.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Backups: couldn't mark backups/{LogName(oldest)} as the first ({CampaignFiles.ErrorText(ex)}); it's still kept as the oldest.");
            }
        }

        return oldest;
    }

    /// <summary>
    /// Backups 0.6.0 kept in <c>UserData\T2TOnline\backups</c>, which a reinstall or the mod's uninstall deletes, move to
    /// <see cref="CampaignFiles.BackupsFolder"/>: each is copied, checked against its manifest, and only then removed from
    /// the game folder. One that can't be moved stays where it was (tried again at the next start).
    /// </summary>
    private static void MoveFromGameFolder()
    {
        string oldRoot = CampaignFiles.OldBackupsFolder;
        string newRoot = CampaignFiles.BackupsFolder;
        if (!Directory.Exists(oldRoot) || string.Equals(Path.GetFullPath(oldRoot).TrimEnd('\\'), Path.GetFullPath(newRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int moved = 0;
        var stayed = new List<string>();
        foreach (string folder in Directory.GetDirectories(oldRoot))
        {
            string name = Path.GetFileName(folder);
            if (name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(folder); // a copy that never finished
                continue;
            }

            SaveBackupManifest? manifest = SaveBackupNames.TryParse(name, out _, out _) ? ReadManifest(folder) : null;
            if (manifest == null || !Verify(folder, manifest, out Dictionary<string, byte[]> files, out Dictionary<int, byte[]> slots, out _))
            {
                stayed.Add(name); // not one of the mod's backups, or damaged: left alone
                continue;
            }

            string target = Path.Combine(newRoot, name);
            if (Directory.Exists(target))
            {
                if (ReadManifest(target) is { } there && there.HoldsSame(manifest.Files, manifest.CoopSlots) && Verify(target, there, out _, out _, out _))
                {
                    TryDelete(folder); // already moved; this copy is left over
                    moved++;
                }
                else
                {
                    stayed.Add(name);
                }

                continue;
            }

            string partial = target + PartialSuffix;
            try
            {
                WriteBackup(partial, manifest, files, slots, TryReadCounterBytes(folder, manifest, out byte[] counters, out _) ? counters : null);
                if (!Verify(partial, manifest, out _, out _, out string problem))
                {
                    throw new IOException(problem);
                }

                Directory.Move(partial, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                TryDelete(partial);
                stayed.Add(name);
                ModLog.Warn($"Backups: backups/{LogName(name)} couldn't be moved out of the game folder ({CampaignFiles.ErrorText(ex)}); it stays in UserData\\T2TOnline\\backups.");
                continue;
            }

            TryDelete(folder);
            moved++;
        }

        if (moved > 0)
        {
            ModLog.Info($"Backups: moved {moved} backup(s) from UserData\\T2TOnline\\backups to {FolderLabel}, where a reinstall can't delete them.");
        }

        if (stayed.Count > 0)
        {
            ModLog.Warn($"Backups: left in UserData\\T2TOnline\\backups (not moved): {string.Join(", ", stayed.Select(LogName))}.");
        }
        else
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(oldRoot).Any())
                {
                    Directory.Delete(oldRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Trace($"Backups: the old backups folder stays ({CampaignFiles.ErrorText(ex)}).");
            }
        }
    }

    // ---- Start-up ----

    private static void StartUpCore(bool atLaunch)
    {
        if (_startedUp)
        {
            return;
        }

        try
        {
            if (!_moved)
            {
                _moved = true; // once, even if it fails: the panel asks every few seconds
                MoveFromGameFolder();
                if (Directory.Exists(CampaignFiles.BackupsFolder))
                {
                    // Marked now, before a backup named by a wrong clock could pass for the oldest.
                    List<string> names = Directory.GetDirectories(CampaignFiles.BackupsFolder).Select(d => Path.GetFileName(d)).ToList();
                    FirstBackup(names, Clock(names));
                }

                ReadCloudList();
            }

            _startedUp = CheckInterruptedRestore(atLaunch);
        }
        catch (Exception ex)
        {
            _startedUp = true;
            ModLog.Error("Backups: the start-up checks failed", ex);
        }
    }

    /// <summary>
    /// A restore journal left from a game that stopped mid-restore: the restore is finished or undone (<see cref="Recover"/>).
    /// Unfinished copies in the save folder and the co-op folder (a restore's, or a write of the mod's) are removed first.
    /// False when the save folder isn't known yet (asked again later).
    /// </summary>
    private static bool CheckInterruptedRestore(bool atLaunch)
    {
        string saves;
        try
        {
            saves = UnityEngine.Application.persistentDataPath;
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrEmpty(saves) || !Directory.Exists(saves))
        {
            if (atLaunch)
            {
                ModLog.Info("Backups: the game's save folder isn't known yet at start-up; a restore left unfinished is looked for again when the backups are next read (Save Backups, or the next online game).");
            }

            return false;
        }

        try
        {
            RemoveParts(saves, "*.es3" + CampaignFiles.PartSuffix);
            RemoveParts(CampaignFiles.SlotsFolder, "coop-*.es3" + CampaignFiles.PartSuffix);
            string journalPath = Path.Combine(CampaignFiles.BackupsFolder, RestoreJournal.FileName);
            if (File.Exists(journalPath))
            {
                Recover(File.ReadAllText(journalPath), saves, atLaunch);
            }
            else if (atLaunch)
            {
                ModLog.Info("Backups: no restore was left unfinished (looked for at start-up, before the game reads its saves).");
            }

            ReadRestoredAtStart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't check for a restore that didn't finish ({CampaignFiles.ErrorText(ex)}).");
        }

        return true;
    }

    private static void RemoveParts(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string part in Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly))
        {
            File.Delete(part);
            ModLog.Info($"Backups: removed {Path.GetFileName(part)}, an unfinished copy from a restore or write that didn't finish.");
        }
    }

    /// <summary>
    /// A restore the game stopped in the middle of (<paramref name="text"/>, its journal) is never left half done:
    /// <see cref="RestoreRecovery.Decide(RestoreJournal, SaveBackupManifest?, bool, SaveBackupManifest?, bool, bool)"/> finishes it (every file in place already: what follows; else from the restored
    /// backup, whole), or undoes it from the backup taken just before it, and says which in the log and on screen. At the
    /// mod's start-up the game hasn't read a save yet, so nothing more is needed; found later (the save folder wasn't known
    /// at start-up), the game must restart once a save file was written, and that needs the save guard (a game build the mod
    /// may patch): without it the journal stays for the next start. Without Steam Cloud's loads held (a build the mod can't
    /// patch) a restore of cloud-saved files is undone, or waits for a start that holds them: finished now, the game's first
    /// load of each file would write Steam's copy from before the restore over it. A start that couldn't put it right (a mixed
    /// install, a build the mod can't patch) may have played on the half-restored saves: before any file is put in place, what
    /// was written since is backed up (<see cref="KeepThenPut"/>), and an all-moved restore whose files aren't as restored any
    /// more isn't finished over them (<see cref="RestoreOnDisk.AllRestored"/>).
    /// </summary>
    private static void Recover(string text, string saves, bool atLaunch)
    {
        if (!RestoreJournal.TryParse(text, out RestoreJournal? journal) || journal == null)
        {
            ModLog.Warn($"Backups: a restore journal was left from a restore that didn't finish, but it can't be read; check your saves ({Core.OnlineSettings.PlayersBuild.SaveBackups}).");
            Tell(NoticeId.RestoreMixed, $"A restore didn't finish last time, so your saves may be mixed: check them, and restore a backup if needed ({Core.OnlineSettings.PlayersBuild.SaveBackups}).");
            DeleteJournal();
            return;
        }

        string what = JournalParts(journal);
        bool gameRead = !atLaunch || GameReadSaves();
        if (gameRead && journal.Files.Count > 0 && !journal.Moved && (!_saveGuardUsable || !SaveGuard.Install(out _)))
        {
            ModLog.Warn($"Backups: a restore of backups/{LogName(journal.Backup)} didn't finish last time ({what}), and the save guard isn't available now (T2T Online can't patch " +
                "this game version, or start-up couldn't reach the save folder); it's finished or undone at the next start with T2T Online on. Until then your saves may be mixed.");
            Tell(NoticeId.RestoreWaits, "A restore didn't finish last time, so your saves may be mixed: it's put right at the next start of the game with T2T Online on.");
            RestoreUnfinished = true; // the journal stays for that start
            return;
        }

        string backupFolder = Path.Combine(CampaignFiles.BackupsFolder, journal.Backup);
        string beforeFolder = Path.Combine(CampaignFiles.BackupsFolder, journal.Before);
        SaveBackupManifest? backup = Directory.Exists(backupFolder) ? ReadManifest(backupFolder) : null;
        SaveBackupManifest? before = Directory.Exists(beforeFolder) ? ReadManifest(beforeFolder) : null;
        var backupFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var backupSlots = new Dictionary<int, byte[]>();
        var beforeFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var beforeSlots = new Dictionary<int, byte[]>();
        string backupProblem = backup == null ? "its manifest is missing or damaged" : string.Empty;
        string beforeProblem = before == null ? "its manifest is missing or damaged" : string.Empty;

        // Without Steam Cloud's loads held, the restored backup's files wouldn't stay: it isn't read, and the one from before is.
        bool unheld = !_cloudLoadsHeld && RestoreRecovery.NamesCloudSavedFiles(journal);
        bool backupWhole = !journal.Moved && !unheld && backup != null && Verify(backupFolder, backup, out backupFiles, out backupSlots, out backupProblem);
        bool beforeWhole = !journal.Moved && !backupWhole && before != null && Verify(beforeFolder, before, out beforeFiles, out beforeSlots, out beforeProblem);
        RecoveryAction action = RestoreRecovery.Decide(journal, backup, backupWhole, before, beforeWhole, _cloudLoadsHeld);
        ModLog.Warn($"Backups: a restore of backups/{LogName(journal.Backup)} didn't finish last time (the game stopped during it; {what}" +
            $"{(journal.Moved ? ", all of them already in place" : string.Empty)}): {(unheld ? UnheldPlan(action) : Plan(action))}.");
        switch (action)
        {
            case RecoveryAction.Finish:
            {
                // Every file was in place: only the ledger, Steam Cloud's list and the per-run counters may be missing. Only while the
                // files are still as restored: a start that couldn't finish it may have put Steam Cloud's copies back or played on them,
                // and the ledger back at the backup's time would then be older than the saves (a rejoin would apply progress twice).
                // Nothing is written either way; when the backup's manifest can't be read this can't be told, and it's finished as always.
                if (backup != null && ReadOnDisk(journal, backup, before, saves, out _, out _, out _) is { AllRestored: false } overtaken)
                {
                    ModLog.Warn(RecoveryKeep.OvertakenLine(overtaken));
                    DeleteJournal();
                    Tell(NoticeId.RestoreMixed, "A restore didn't finish last time, and your saves changed since, so it isn't finished over them; they may be mixed: check them, " +
                        $"and restore a backup if needed ({Core.OnlineSettings.PlayersBuild.SaveBackups}). The backup \"{journal.Backup}\" still holds the restored files.", journal.Backup);
                    return;
                }

                List<SaveFileHash> restored = journal.Files.Select(LocalEntry).OfType<SaveFileHash>().ToList();
                if (journal.Files.Count > 0 && backup == null)
                {
                    ModLog.Warn($"Backups: backups/{LogName(journal.Backup)}'s manifest can't be read, so the shared-progress ledger can't be put back to its time; a rejoin may miss shared progress the restore took away.");
                }

                AfterMoves(Recovered(journal, backup, before, restored));
                DeleteJournal();
                ModLog.Info($"Backups: the restore of backups/{LogName(journal.Backup)} is finished ({what} were already in place).");
                Tell(NoticeId.RestoreDone, $"A restore didn't finish last time; it's finished now: {what} are from the backup \"{journal.Backup}\".", journal.Backup);
                return;
            }

            case RecoveryAction.Complete:
            {
                List<string> failed = KeepThenPut(journal, action, backup, before, saves, atLaunch, () => PutInPlace(saves,
                    journal.Files.Select(f => (f, (byte[]?)backupFiles[f])),
                    journal.Slots.Select(s => (s, (byte[]?)backupSlots[s]))));
                bool restart = gameRead && journal.Files.Count > 0;
                if (failed.Count > 0)
                {
                    Unfinished(journal, failed, restart);
                    return;
                }

                AfterMoves(Recovered(journal, backup, before, journal.Files.Select(f => SaveFileHash.Of(f, backupFiles[f])).ToList()));
                DeleteJournal();
                HoldAfterRecovery(restart, $"backup {LogName(journal.Backup)}'s restore finished at start-up");
                ModLog.Info($"Backups: the restore of backups/{LogName(journal.Backup)} is finished: {what} are as in that backup.");
                Tell(restart ? NoticeId.RestoredRestart : NoticeId.RestoreDone, $"A restore didn't finish last time; it's finished now: {what} are from the backup \"{journal.Backup}\"." + (restart ? " Restart the game: until you do, nothing is saved." : string.Empty), journal.Backup);
                return;
            }

            case RecoveryAction.RollBack:
            {
                List<string> failed = KeepThenPut(journal, action, backup, before, saves, atLaunch, () => PutInPlace(saves,
                    RestoreRecovery.RollBackFiles(journal, before!).Select(x => (x.File, x.PutBack ? beforeFiles[x.File] : null)),
                    RestoreRecovery.RollBackSlots(journal, before!).Select(x => (x.Slot, x.PutBack ? beforeSlots[x.Slot] : null))));
                bool restart = gameRead && journal.Files.Count > 0;
                if (failed.Count > 0)
                {
                    Unfinished(journal, failed, restart);
                    return;
                }

                // The per-run counters as they were before it too (the plan runs only after every move, but a journal whose
                // "all moved" couldn't be written can come here after it ran).
                if (before!.RunScopes != null && ReadCounters(beforeFolder, before, journal.Before) is { } counters)
                {
                    Progress.ProgressRunScopes.Restored(new RunScopePlan(counters, Array.Empty<string>()), $"the undone restore of backups/{LogName(journal.Backup)} (as backups/{LogName(journal.Before)} kept them)");
                }

                DeleteJournal();
                HoldAfterRecovery(restart, $"backup {LogName(journal.Backup)}'s restore undone at start-up");
                string why = unheld ? "Steam Cloud's copies would have come back over it" : backupProblem;
                ModLog.Info($"Backups: the restore of backups/{LogName(journal.Backup)} is undone ({why}): {what} are as they were before it (backups/{LogName(journal.Before)}).");
                Tell(restart ? NoticeId.RestoredRestart : NoticeId.RestoreUndone, (unheld
                        ? $"A restore didn't finish last time, and T2T Online can't keep restored saves over Steam Cloud's copies on this game version, so it's undone: {what} are as they were before it. " +
                          "Restore that backup again once T2T Online is on for this game version."
                        : $"A restore didn't finish last time, and its backup can't be read now, so it's undone: {what} are as they were before it.") +
                    (restart ? " Restart the game: until you do, nothing is saved." : string.Empty));
                return;
            }

            case RecoveryAction.Wait:
                // Nothing written: the game's loads put Steam Cloud's copies (from before the restore) in place this time.
                ModLog.Warn($"Backups: the restore of backups/{LogName(journal.Backup)} waits for a start with T2T Online on for this game version " +
                    $"({(journal.Moved ? "all of it was in place, and what followed may have run" : $"the backup from before it can't be used: {beforeProblem}")}); until then the game loads " +
                    "Steam Cloud's copies of the cloud-saved files, from before the restore.");
                Tell(NoticeId.RestoreWaits, "A restore didn't finish last time: T2T Online can't put it right on this game version, so it's finished or undone at a start with T2T Online on. Until then your saves may be mixed.");
                RestoreUnfinished = true; // the journal stays for that start
                return;

            default:
                ModLog.Warn($"Backups: neither backups/{LogName(journal.Backup)} ({backupProblem}) nor backups/{LogName(journal.Before)} ({beforeProblem}) can be read, so the restore can't be " +
                    $"finished or undone: {what} may be mixed. Restore another backup ({Core.OnlineSettings.PlayersBuild.SaveBackups}).");
                Tell(NoticeId.RestoreMixed, $"A restore didn't finish last time and neither its backup nor the one from before it can be read, so your saves may be mixed: restore another backup ({Core.OnlineSettings.PlayersBuild.SaveBackups}).");
                DeleteJournal();
                return;
        }
    }

    /// <summary>
    /// What follows the moves of a restore finished at start-up: the restored backup's time and counters (when its manifest
    /// reads), the "before" backup's counters, file hashes and time (when its manifest reads; it was taken just before the
    /// restore), and nothing known of Steam Cloud then.
    /// </summary>
    private static AfterRestore Recovered(RestoreJournal journal, SaveBackupManifest? backup, SaveBackupManifest? before, IReadOnlyCollection<SaveFileHash> restored) =>
        new(journal.Backup, backup?.TakenAtUtc, restored, journal.Slots,
            journal.Files.Any(f => string.Equals(f, RunSync.SoloRunFile, StringComparison.OrdinalIgnoreCase)),
            before != null ? ReadCounters(Path.Combine(CampaignFiles.BackupsFolder, journal.Before), before, journal.Before) : null,
            backup != null ? ReadCounters(Path.Combine(CampaignFiles.BackupsFolder, journal.Backup), backup, journal.Backup) : null,
            before?.TakenAtUtc,
            before?.Files.ToDictionary(f => f.Name, f => (string?)f.Sha256, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SteamCopy>());

    private static string Plan(RecoveryAction action) => action switch
    {
        RecoveryAction.Finish => "finishing it",
        RecoveryAction.Complete => "finishing it from that backup",
        RecoveryAction.RollBack => "that backup can't be used, so it's undone from the backup taken just before it",
        _ => "neither backup can be used",
    };

    /// <summary>The plan without Steam Cloud's loads held (a game build the mod can't patch), for the log.</summary>
    private static string UnheldPlan(RecoveryAction action) => action switch
    {
        RecoveryAction.RollBack => "the game's loads from Steam Cloud aren't held this time (T2T Online can't patch this game version), so Steam Cloud's copies would come back over the restored files at the game's first load; " +
            "it's undone from the backup taken just before it, whose files are the ones Steam Cloud holds",
        _ => "the game's loads from Steam Cloud aren't held this time (T2T Online can't patch this game version), so Steam Cloud's copies would come back over restored files, and it can't be undone now; it waits",
    };

    /// <summary>
    /// A recovery's moves (<paramref name="putInPlace"/>, for <paramref name="action"/>), with what is on disk kept first when something wrote it
    /// since the restore stopped (<see cref="RestoreOnDisk"/>, <see cref="RecoveryKeep.Run"/>): a start that couldn't put the restore right may
    /// have played on the half-restored saves. One line says what was kept, and where, before the first move. A backup that can't be taken
    /// doesn't stop the moves. The names of those that couldn't be put in place.
    /// </summary>
    private static List<string> KeepThenPut(RestoreJournal journal, RecoveryAction action, SaveBackupManifest? backup, SaveBackupManifest? before, string saves,
        bool atLaunch, Func<List<string>> putInPlace)
    {
        RestoreOnDisk? onDisk = ReadOnDisk(journal, backup, before, saves, out Dictionary<string, byte[]> files, out Dictionary<int, byte[]> slots, out string unread);
        RecoveryWrites writes = RecoveryKeep.Run(onDisk, unread, () => KeepBeforeRecovery(journal, action, files, slots, atLaunch), kept =>
        {
            string line = RecoveryKeep.Line(journal, action, onDisk, kept);
            if (kept.Kind == RecoveryKeepKind.Failed)
            {
                ModLog.Warn(line);
            }
            else
            {
                ModLog.Info(line);
            }

            return putInPlace();
        });
        return writes.Failed.ToList();
    }

    /// <summary>
    /// The save files and co-op slots as they are now (<paramref name="files"/>, <paramref name="slots"/>: what a backup of them holds), and where
    /// each one <paramref name="journal"/> names stands against its two backups; null, with why, when the save folder can't be read.
    /// </summary>
    private static RestoreOnDisk? ReadOnDisk(RestoreJournal journal, SaveBackupManifest? backup, SaveBackupManifest? before, string saves,
        out Dictionary<string, byte[]> files, out Dictionary<int, byte[]> slots, out string unread)
    {
        try
        {
            files = ReadSaveFiles(saves);
            slots = ReadCoopSlots();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            slots = new Dictionary<int, byte[]>();
            unread = $"the save files couldn't be read: {CampaignFiles.ErrorText(ex)}";
            return null;
        }

        unread = string.Empty;
        return RestoreOnDisk.Read(journal, backup, before, files, slots, BackupCopy);
    }

    /// <summary>Backup <paramref name="name"/>'s copy of save file <paramref name="file"/>, or null when it can't be read (the caller checks it against the manifest).</summary>
    private static byte[]? BackupCopy(string name, string file) =>
        SaveBackupManifest.IsPlainFileName(name) && SaveBackupManifest.IsPlainFileName(file) &&
        TryReadCopy(Path.Combine(CampaignFiles.BackupsFolder, name, file), file, out byte[] bytes, out _) ? bytes : null;

    /// <summary>
    /// What is on disk now (<paramref name="files"/>, <paramref name="slots"/>) kept before a recovery puts <paramref name="journal"/>'s files in place,
    /// as a restore's own backup of what it replaces is (<see cref="Store"/>: role <c>restore</c>, the reason <see cref="RecoveryKeep.Reason"/>, the
    /// per-run counters, the backup whose files go in place never pruned), after the shared-progress ledger's dated copy (as before a backup by hand),
    /// so a restore of this backup brings the ledger of this moment. Not while a check tour runs (it takes no backups). Never throws.
    /// </summary>
    private static RecoveryKept KeepBeforeRecovery(RestoreJournal journal, RecoveryAction action, Dictionary<string, byte[]> files, Dictionary<int, byte[]> slots, bool atLaunch)
    {
        if (NativeLobby.TourHolds.On)
        {
            return RecoveryKept.Failure("a check tour runs, and it takes no backups");
        }

        Progress.ProgressLedgerFile.KeepCopy();

        // At the mod's start-up nothing of the game is up yet (and reading its counters would make its project context early): the store,
        // less the live run file's entry, whose counters the files kept here hold themselves.
        string? counters = Progress.ProgressRunScopes.SnapshotText(fromGame: !atLaunch);
        Store(files, slots, counters, "restore", RecoveryKeep.Reason(journal, action), protect: action == RecoveryAction.RollBack ? journal.Before : journal.Backup,
            out string name, out _, out string problem);
        return problem.Length > 0 || name.Length == 0 ? RecoveryKept.Failure(problem) : RecoveryKept.In(name);
    }

    /// <summary>A recovery that couldn't write everything: the journal stays (tried again at the next start), and what was written waits for a restart if the game had read its saves.</summary>
    private static void Unfinished(RestoreJournal journal, List<string> failed, bool restart)
    {
        RestoreUnfinished = true;
        HoldAfterRecovery(restart, $"backup {LogName(journal.Backup)}'s restore only partly put right");
        ModLog.Warn($"Backups: {string.Join(", ", failed)} couldn't be put in place, so the restore of backups/{LogName(journal.Backup)} is still unfinished; it's tried again at the next start.");
        Tell(NoticeId.RestoreWaits, "A restore didn't finish last time and couldn't be put right now (see the log); it's tried again at the next start of the game.");
    }

    /// <summary>After a recovery wrote save files the game had already read: every save write is refused until it restarts.</summary>
    private static void HoldAfterRecovery(bool restart, string why)
    {
        if (restart)
        {
            RestartNeeded = true;
            SaveGuard.HoldUntilRestart(why);
        }
    }

    /// <summary>True when the game may have read its saves already (its profile exists); in doubt, true. Never throws.</summary>
    private static bool GameReadSaves()
    {
        try
        {
            return ProfileData.Instance != null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>A journal's files and slots in words, for the log and the screen.</summary>
    private static string JournalParts(RestoreJournal journal)
    {
        var parts = journal.Files.ToList();
        parts.AddRange(journal.Slots.Select(s => $"co-op slot {s}"));
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>
    /// Writes each of <paramref name="files"/> into the save folder and each of <paramref name="slots"/> into its co-op slot,
    /// each whole or not at all (null bytes: the file is deleted). The names of those that couldn't be.
    /// </summary>
    private static List<string> PutInPlace(string saves, IEnumerable<(string File, byte[]? Bytes)> files, IEnumerable<(int Slot, byte[]? Bytes)> slots)
    {
        var failed = new List<string>();
        var all = files.Select(f => (Label: f.File, Path: Path.Combine(saves, f.File), f.Bytes))
            .Concat(slots.Select(s => (Label: $"co-op slot {s.Slot}", Path: CampaignFiles.SlotPath(s.Slot), s.Bytes)));
        foreach ((string label, string path, byte[]? bytes) in all)
        {
            try
            {
                if (bytes != null)
                {
                    PutFile(path, bytes);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(label);
                ModLog.Error($"Backups: couldn't put {label} in place ({CampaignFiles.ErrorText(ex)}).");
            }
        }

        return failed;
    }

    /// <summary>
    /// One file of a recovery put in place whole: written through to the disk beside <paramref name="path"/> first, then renamed
    /// over it, as a restore's own files are (<see cref="WriteToDisk"/>). The journal goes right after the last one, so a power
    /// cut must leave the old file or the new one, never one of zeros with no journal left to finish it. A failure removes the
    /// copy (or the next start does) and throws.
    /// </summary>
    private static void PutFile(string path, byte[] bytes)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string part = path + CampaignFiles.PartSuffix;
        try
        {
            WriteToDisk(part, bytes);
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Backups: couldn't remove {Path.GetFileName(part)} after a failed write ({CampaignFiles.ErrorText(cleanup)}); the next start removes it.");
            }

            throw;
        }
    }

    /// <summary>Writes the restore's journal; once every file is in place, a failure is only logged (the journal from before the moves still says what's under way, and finishing it again is safe).</summary>
    private static void WriteJournal(RestoreJournal journal)
    {
        if (!WriteJournal(journal, out string error))
        {
            ModLog.Warn($"Backups: couldn't update the restore's journal ({error}); the restore is whole, and a next start that finds the older journal only finishes it again.");
        }
    }

    /// <summary>Writes the restore's journal; false, with why, when it couldn't be.</summary>
    private static bool WriteJournal(RestoreJournal journal, out string error)
    {
        try
        {
            CampaignFiles.WriteAtomically(Path.Combine(CampaignFiles.BackupsFolder, RestoreJournal.FileName), Encoding.UTF8.GetBytes(journal.ToText()));
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = CampaignFiles.ErrorText(ex);
            return false;
        }
    }

    /// <summary>
    /// Right after a restore: the files in place read back against the backup's manifest. When the game must
    /// restart (<paramref name="keep"/>: your saves were restored, and nothing writes them until then), what's restored is kept for
    /// the next start's look. A slot restored alone isn't: playing on it changes it. Never throws.
    /// </summary>
    private static void CheckRestored(string name, IReadOnlyList<SaveFileHash> files, IReadOnlyList<CoopSlotHash> slots, bool keep)
    {
        try
        {
            var check = new RestoreCheck(name, files, slots);
            RestoreReading reading = check.Read(LocalHash, SlotHash);
            if (reading.Same)
            {
                ModLog.Info(reading.ReadBackLine());
            }
            else
            {
                ModLog.Warn(reading.ReadBackLine());
            }

            if (keep)
            {
                File.WriteAllText(Path.Combine(CampaignFiles.BackupsFolder, RestoreCheck.FileName), check.ToText(), new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: couldn't read the restored files back ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)}); the next start won't check them.");
        }
    }

    /// <summary>
    /// At the first start after your saves were restored, before the game has read them: are they still as restored? The list
    /// goes (it's for this one look); what it holds is looked at once more at the title screen (<see cref="ReadRestoredAtTitle"/>).
    /// Never throws.
    /// </summary>
    private static void ReadRestoredAtStart()
    {
        string path = Path.Combine(CampaignFiles.BackupsFolder, RestoreCheck.FileName);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            string text = File.ReadAllText(path);
            File.Delete(path);
            if (!RestoreCheck.TryParse(text, out RestoreCheck? check))
            {
                ModLog.Warn($"Backups: the list of files the last restore put in place ({RestoreCheck.FileName}) can't be read; they aren't checked at this start.");
                return;
            }

            RestoreReading reading = check!.Read(LocalHash, SlotHash);
            if (reading.Same)
            {
                ModLog.Info(reading.StartLine());
            }
            else
            {
                ModLog.Warn(reading.StartLine());
            }

            _restoredAtStart = check;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: couldn't check the files the last restore put in place ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)}).");
        }
    }

    /// <summary>
    /// At the title screen of that start, once the game has loaded its files (through Steam Cloud for those it keeps there): the
    /// restored files once more. A plain line: the game's own start-up may rewrite some, and a restore that yielded to another
    /// device's newer saves has its own lines. Never throws.
    /// </summary>
    private static void ReadRestoredAtTitle()
    {
        RestoreCheck? check = _restoredAtStart;
        _restoredAtStart = null;
        if (check == null)
        {
            return;
        }

        try
        {
            ModLog.Info(check.Read(LocalHash, SlotHash).TitleLine());
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: couldn't check the restored files at the title screen ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)}).");
        }
    }

    /// <summary>Co-op slot <paramref name="slot"/>'s hash now, or null when it isn't there or can't be read.</summary>
    private static string? SlotHash(int slot)
    {
        try
        {
            string path = CampaignFiles.SlotPath(slot);
            return File.Exists(path) ? SaveFileHash.HashOf(CampaignFiles.ReadShared(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The restore journal on disk, or null when there's none or it doesn't read as one. Throws when it's there but can't be opened.</summary>
    private static RestoreJournal? JournalOnDisk()
    {
        string path = Path.Combine(CampaignFiles.BackupsFolder, RestoreJournal.FileName);
        return File.Exists(path) && RestoreJournal.TryParse(File.ReadAllText(path), out RestoreJournal? journal) ? journal : null;
    }

    private static void DeleteJournal()
    {
        try
        {
            File.Delete(Path.Combine(CampaignFiles.BackupsFolder, RestoreJournal.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Backups: couldn't remove the restore's journal ({CampaignFiles.ErrorText(ex)}); the next start will finish the restore again.");
        }
    }

    /// <summary>Every save file (<c>*.es3</c>) directly in <paramref name="folder"/>, by name. Never the mod's own files.</summary>
    private static Dictionary<string, byte[]> ReadSaveFiles(string folder)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(folder, "*.es3", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            if (!SaveBackupNames.IsSaveFile(fileName) || CampaignFiles.IsModPath(path))
            {
                continue;
            }

            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    ModLog.Warn($"Backups: {fileName} is over {MaxFileBytes / (1024 * 1024)} MB and is left out.");
                    continue;
                }

                files[fileName] = CampaignFiles.ReadShared(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone between the listing and the read (the game replacing it): not a save file any more.
                ModLog.Info($"Backups: {fileName} went away while the saves were read ({ex.GetType().Name}); left out.");
            }
        }

        return new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every co-op slot with a run file (<c>coop-1.es3</c> to <c>coop-3.es3</c> in <see cref="CampaignFiles.SlotsFolder"/>;
    /// an empty file is no run), by slot. Not Easy Save's <c>.bac</c> copies: the game never reads them.
    /// </summary>
    private static Dictionary<int, byte[]> ReadCoopSlots()
    {
        var slots = new Dictionary<int, byte[]>();
        for (int slot = 1; slot <= CoopSlots.Count; slot++)
        {
            string path = CampaignFiles.SlotPath(slot);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    continue;
                }

                if (info.Length > MaxFileBytes)
                {
                    ModLog.Warn($"Backups: co-op slot {slot} is over {MaxFileBytes / (1024 * 1024)} MB and is left out.");
                    continue;
                }

                slots[slot] = CampaignFiles.ReadShared(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                ModLog.Info($"Backups: co-op slot {slot} went away while it was read ({ex.GetType().Name}); left out.");
            }
        }

        return slots;
    }

    /// <summary>
    /// How backup names are read here (D129): in this PC's time zone, with each old (local-time) name's own manifest time
    /// when it can be read, which is exact even when the zone changed since. Reads the old backups' manifests: call it when
    /// listing or pruning, not every frame.
    /// </summary>
    /// <summary>A backup as a log line names it: its UTC name; an old local-time name as the UTC time it stands for (D127, D129).</summary>
    private static string LogName(string? name) => SaveBackupNames.ForLog(name);

    /// <summary>A backup's reason for the log: the backups it names by UTC, an old build's host name left out (D127, D129).</summary>
    private static string LogReason(string? reason) => SaveBackupNames.ReasonForLog(reason);

    private static BackupClock Clock(IEnumerable<string> names)
    {
        var known = new List<KeyValuePair<string, DateTime>>();
        foreach (string name in names)
        {
            if (!SaveBackupNames.IsUtcName(name) && SaveBackupNames.TryParse(name, out _, out _) &&
                ReadManifest(Path.Combine(CampaignFiles.BackupsFolder, name)) is { } manifest)
            {
                known.Add(new KeyValuePair<string, DateTime>(name, manifest.TakenAtUtc));
            }
        }

        return new BackupClock(TimeZoneInfo.Local, known);
    }

    private static SaveBackupManifest? ReadManifest(string folder)
    {
        try
        {
            string path = Path.Combine(folder, SaveBackupManifest.FileName);
            return File.Exists(path) && SaveBackupManifest.TryParse(File.ReadAllText(path), out SaveBackupManifest? manifest) ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when every file and co-op slot the manifest names is in the backup with the size and hash it records.</summary>
    private static bool Verify(string folder, SaveBackupManifest manifest, out Dictionary<string, byte[]> files, out Dictionary<int, byte[]> slots, out string problem)
    {
        files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        slots = new Dictionary<int, byte[]>();
        foreach (SaveFileHash file in manifest.Files)
        {
            if (!SaveBackupNames.IsSaveFile(file.Name))
            {
                problem = $"it's incomplete ({file.Name} is missing)";
                return false;
            }

            if (!TryReadCopy(Path.Combine(folder, file.Name), file.Name, out byte[] bytes, out problem))
            {
                return false;
            }

            if (SaveFileHash.Of(file.Name, bytes) != file)
            {
                problem = $"its {file.Name} doesn't match its manifest (changed or damaged)";
                return false;
            }

            files[file.Name] = bytes;
        }

        foreach (CoopSlotHash slot in manifest.CoopSlots)
        {
            if (!TryReadCopy(Path.Combine(folder, SaveBackupManifest.CoopFolder, slot.FileName), $"co-op slot {slot.Slot}", out byte[] bytes, out problem))
            {
                return false;
            }

            if (CoopSlotHash.Of(slot.Slot, bytes) != slot)
            {
                problem = $"its co-op slot {slot.Slot} doesn't match its manifest (changed or damaged)";
                return false;
            }

            slots[slot.Slot] = bytes;
        }

        problem = string.Empty;
        return true;
    }

    private static bool TryReadCopy(string path, string label, out byte[] bytes, out string problem)
    {
        bytes = Array.Empty<byte>();
        if (!File.Exists(path))
        {
            problem = $"it's incomplete ({label} is missing)";
            return false;
        }

        try
        {
            bytes = CampaignFiles.ReadShared(path);
            problem = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"its {label} couldn't be read ({CampaignFiles.ErrorText(ex)})";
            return false;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (folder.Length > 0 && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // By its log name: a folder from before 0.11.0 is named in local time (an unfinished copy's suffix isn't part of it).
            string name = Path.GetFileName(folder);
            bool partial = name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase);
            ModLog.Warn($"Backups: couldn't remove {(partial ? "the unfinished copy of " : string.Empty)}backups/{LogName(partial ? name[..^PartialSuffix.Length] : name)} ({CampaignFiles.ErrorText(ex)}).");
        }
    }

    private static string Changed(SaveFileDifference difference) => difference.Change switch
    {
        SaveFileChange.Added => $"is new ({Kb(difference.BytesAfter)})",
        SaveFileChange.Removed => $"is gone (it was {Kb(difference.BytesBefore)})",
        _ => $"changed ({Kb(difference.BytesBefore)} to {Kb(difference.BytesAfter)})",
    };

    private static string Kb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB");

    /// <summary>One thing a restore puts in place: its name for logs, where it goes, its bytes, and what was there (null: nothing).</summary>
    private sealed record Target(string Label, string Path, byte[] Bytes, byte[]? Before);

    /// <summary>
    /// What follows a restore's moves needs (<see cref="AfterMoves"/>): the restored backup and when it was taken (null: its
    /// manifest can't be read, so the shared-progress ledger is left as it is), the save
    /// files and co-op slots restored, whether it holds the solo run, the per-run counters from before the restore and the
    /// backup's (null: none), when the restore happened, each file's hash before it (null: not known; a file not in it
    /// wasn't there), and Steam Cloud's copies at the restore.
    /// </summary>
    private sealed record AfterRestore(string Backup, DateTime? TakenAtUtc, IReadOnlyCollection<SaveFileHash> RestoredFiles, IReadOnlyList<int> Slots,
        bool BackupHoldsSoloRun, RunScopeStore? CountersBefore, RunScopeStore? BackupCounters, DateTime? RestoredAtUtc,
        IReadOnlyDictionary<string, string?>? BeforeHashes, IReadOnlyDictionary<string, SteamCopy> SteamAtRestore);
}

/// <summary>A backup taken from the panel: its folder name, and whether it's an existing backup that already held the same saves.</summary>
internal sealed record BackupTaken(string Name, bool Reused);

/// <summary>What a backup holds, for a restore's confirmation: its name, how many save files, and each co-op slot with its run in words.</summary>
internal sealed record BackupContents(string Name, int Files, IReadOnlyList<(int Slot, string Run)> Slots);
#endif
