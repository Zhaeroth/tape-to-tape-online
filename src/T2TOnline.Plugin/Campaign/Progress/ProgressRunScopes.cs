#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using Il2CppRogue.BenchQuests;
using Il2CppStats.PersistentStats;
using MelonLoader;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// A host's per-run counters, kept per run file (the solo run and each co-op slot, D46). The game keeps every stat's "this
/// run" and "this act" value and the bench quests' per-run counts in the stats and profile files, not in the run, and
/// resets them only when a run ends or an act starts: vanilla never has two runs alive, a host with a co-op slot does. So
/// when the game's run file switches, the outgoing run's counters go into <c>%LOCALAPPDATA%\T2T Online\progress\run-scopes.txt</c>
/// (<see cref="RunScopeStore"/>) and the incoming run's come back (zero for a run never kept), and the stats and profile
/// are saved at once so the files on disk always hold the counters the store calls live. Without it the co-op run's
/// "most ... in a run" records (and what friends get of them) and its run history would count the solo run's goals,
/// injuries and knockouts, and each run's start or end would wipe the other's.
/// <list type="bullet">
/// <item>Run sync calls <see cref="Switching"/> where it switches the run file (the co-op slot at the title, the solo run
/// back).</item>
/// <item>A check about once a second (<see cref="Settle"/>) finds a run file that changed without it, and above all a game
/// that closed on a co-op slot: it starts again on the solo run file with the slot's counters in its files, and the
/// store says whose they are.</item>
/// <item>Backups keep the store with the live run file's counters from memory (<see cref="SnapshotText"/>), and a restore
/// sets the restored run files' counters from it (<see cref="Restored"/>, <see cref="RunScopeRestore"/>): after a saves
/// restore the store's <c>reload</c> puts the planned counters into memory once the restarted game has loaded its files.</item>
/// </list>
/// Never on a friend's PC while it follows a host (its run file is the host's run there; shared progress puts this PC's
/// own run values back itself). Nothing here throws into the game.
/// </summary>
internal static class ProgressRunScopes
{
    private const double SettleSeconds = 1;
    private static RunScopeStore? _store;
    private static double _nextSettle;
    private static bool _installed;
    private static bool _waitLogged;
    private static int _failures;

    /// <summary>Where the store lives.</summary>
    public static string StorePath => Path.Combine(ProgressLedgerFile.Folder, "run-scopes.txt");

    /// <summary>
    /// The game's run file is switching from <paramref name="fromRunFile"/> to <paramref name="toRunFile"/> (names or paths,
    /// as the run repositories hold them): the counters in memory are put aside for the run they belong to and the
    /// incoming run's come back. Call it right after the switch, at the title screen, with no run being played. A friend's
    /// switch to the host's run is ignored. Never throws.
    /// </summary>
    public static void Switching(string? fromRunFile, string? toRunFile)
    {
        try
        {
            string fromFile = Name(fromRunFile), to = Name(toRunFile);
            if (string.Equals(fromFile, to, StringComparison.OrdinalIgnoreCase) || IsHostRun(fromFile) || IsHostRun(to) || CampaignRelay.Current is { Following: true })
            {
                return;
            }

            RunScopeStore store = Store();
            if (store.Live == null)
            {
                // The first switch ever: the counters in memory are the outgoing run's. Noted first, so a swap that has to
                // wait (the stats not loaded yet) is finished by the next check.
                store.SetLive(fromFile);
                SaveStore(store);
            }
            else if (!string.Equals(store.Live, fromFile, StringComparison.OrdinalIgnoreCase))
            {
                ModLog.Info($"Progress: the per-run counters in this game are {store.Live}'s (not {fromFile}'s, the run file before this switch); they're kept as {store.Live}'s.");
            }

            Swap(store, to, "the run file switched");
        }
        catch (Exception ex)
        {
            Failed("switching run files", ex);
        }
    }

    /// <summary>
    /// For a backup: the store as text with the live run file's counters as they are in memory now
    /// (<see cref="RunScopeRestore.Snapshot"/>), or null when nothing is known (no store and the counters can't be read).
    /// The counters aren't read while this PC follows a host or holds the host's run, a restored backup waits for the
    /// restart, a restore's reload is pending, or the profile or stats aren't loaded. Nor without
    /// <paramref name="fromGame"/>: at the mod's start-up, before the game's first scene, nothing of the game is loaded, and
    /// asking for its stats would make the game's project context (its getter makes one when there's none). Then the store
    /// without the live run file's entry, since the save files being backed up hold that run file's counters themselves and
    /// the store's entry is older; with a reload pending, the store as it is (<see cref="RunScopeRestore.Snapshot"/>).
    /// Never throws.
    /// </summary>
    internal static string? SnapshotText(bool fromGame = true)
    {
        try
        {
            RunScopeStore store = Store();
            string live = store.Live ?? RunSync.SoloRunFile;
            RunScope? capture = null;
            ProfileData? profile = fromGame ? ProfileData.Instance : null;
            StatRepository? stats = fromGame ? ProgressFriend.FromProject<StatManager>()?.StatRepository : null;
            if (!store.Reload && !SaveBackups.RestartNeeded && CampaignRelay.Current is not { Following: true } && RunSync.Instance?.HoldingHostRun != true &&
                profile != null && profile.IsLoaded && stats != null && stats.IsLoaded)
            {
                capture = Capture(stats, profile.benchQuestData);
            }

            if (capture == null && store.Count == 0)
            {
                return null;
            }

            return RunScopeRestore.Snapshot(store, live, capture).Format();
        }
        catch (Exception ex)
        {
            Failed("reading the per-run counters for a backup", ex);
            return null;
        }
    }

    /// <summary>A copy of the store as it is (an empty one when it can't be read). Never throws.</summary>
    internal static RunScopeStore StoreCopy()
    {
        try
        {
            return Store().Clone();
        }
        catch (Exception ex)
        {
            Failed("copying the per-run counters kept per run file", ex);
            return new RunScopeStore();
        }
    }

    /// <summary>
    /// A restore's plan (<see cref="RunScopeRestore.Plan"/>) becomes the store, saved at once, and each decision is logged
    /// (<paramref name="why"/>: which restore). Run at the title outside a session (a restore), or at start-up (a restore
    /// the game stopped in the middle of). Never throws.
    /// </summary>
    internal static void Restored(RunScopePlan plan, string why)
    {
        try
        {
            _store = plan.Store;
            SaveStore(_store, force: true); // saved even when nothing changed since its copy was made
            foreach (string note in plan.Notes)
            {
                ModLog.Info($"Progress: {note}");
            }

            ModLog.Info($"Progress: the per-run counters kept per run file follow {why} ({_store.Count} run file(s) kept, {_store.Live ?? "none"} live" +
                $"{(_store.Reload ? ", its counters go into memory before anything else" : string.Empty)}).");
        }
        catch (Exception ex)
        {
            Failed("keeping the per-run counters a restore set", ex);
        }
    }

    /// <summary>
    /// A restore yielded to Steam Cloud (another device saved since, <see cref="CloudPushDecision"/>): the game loads Steam's
    /// copies, whose counters are that device's, so a pending reload of the restore's counters is dropped. Never throws.
    /// </summary>
    internal static void CloudYielded(string backup)
    {
        try
        {
            RunScopeStore store = Store();
            if (!store.Reload)
            {
                return;
            }

            store.Reload = false;
            SaveStore(store);
            ModLog.Info($"Progress: the restore of backups/{SaveBackupNames.ForLog(backup)} yielded to Steam Cloud, so the per-run counters in memory stay the ones the game loaded from it ({store.Live ?? RunSync.SoloRunFile}'s).");
        }
        catch (Exception ex)
        {
            Failed("dropping a restore's reload", ex);
        }
    }

    /// <summary>
    /// At start-up, online on or off: on a game build the mod may patch (<paramref name="patchable"/>: a tested build whose
    /// hooks all resolved) the once-a-second check starts, so the counters follow the run file on an online-off launch too:
    /// a restore's reload goes in and a game closed on a co-op slot gets the solo run's counters back, as at an online
    /// start. It needs nothing of a session: no patch, no Steam, only the game's run storage, its stats and profile, and the
    /// store (C3: it writes this PC's own stats and profile files, never a run file). On any other build nothing writes the
    /// game's stats (D12), and the log says what waits for a start that can. Never throws.
    /// </summary>
    internal static void Start(bool patchable)
    {
        try
        {
            if (patchable)
            {
                if (!_installed)
                {
                    Install();
                    ModLog.Info("Progress: the per-run counters follow the game's run file on this launch, online or not (checked once a second).");
                }

                return;
            }

            if (RunScopeRestore.WaitingNote(Store(), RunSync.SoloRunFile) is { } waits)
            {
                ModLog.Info($"Progress: the per-run counters aren't followed on this launch (T2T Online can't patch this game version): {waits}.");
            }
        }
        catch (Exception ex)
        {
            Failed("starting the per-run counters' check", ex);
        }
    }

    /// <summary>Starts the once-a-second check (with the other progress patches, and at start-up, <see cref="Start"/>).</summary>
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        MelonEvents.OnUpdate.Subscribe(Tick);
        _installed = true;
    }

    private static void Tick()
    {
        double now = FrameClock.Seconds;
        if (now < _nextSettle)
        {
            return;
        }

        _nextSettle = now + SettleSeconds;
        try
        {
            Settle();
        }
        catch (Exception ex)
        {
            Failed("checking the run file", ex);
        }
    }

    /// <summary>
    /// A restore's reload first (the store's live counters into memory); then, when the game's run file isn't the one whose
    /// counters are in memory (the store's live one), switches them.
    /// </summary>
    private static void Settle()
    {
        if (CampaignRelay.Current is { Following: true } || RunSync.Instance?.HoldingHostRun == true)
        {
            return; // this PC holds a host's run (it isn't this PC's own run file)
        }

        if (Store().Reload)
        {
            ReloadLive(Store());
            return;
        }

        if (CampaignGame.State() is not { } state || CampaignGame.RunFileName(state) is not { } file)
        {
            return; // the game's run storage can't say
        }

        string current = Name(file);
        if (IsHostRun(current))
        {
            return;
        }

        RunScopeStore store = Store();
        if (store.Live == null)
        {
            if (string.Equals(current, RunSync.SoloRunFile, StringComparison.OrdinalIgnoreCase))
            {
                return; // never switched, and on the solo run: nothing to keep apart (no file is written)
            }

            // Never switched before, and a co-op slot is the run file: the counters in memory are the solo run's (the game
            // starts on it), switched without a word to this class.
            store.SetLive(RunSync.SoloRunFile);
            SaveStore(store);
        }

        if (!string.Equals(current, store.Live, StringComparison.OrdinalIgnoreCase))
        {
            Swap(store, current, $"the game's run file is {current} and the counters in memory are {store.Live}'s (the game closed on that run file, or it switched without a word)");
        }
    }

    /// <summary>
    /// After a restore (<see cref="RunScopeStore.Reload"/>): once the game has loaded its files and no restart waits, the
    /// store's counters for its live run file go into memory (when memory holds others), and the stats and profile are
    /// saved, so the files hold them too.
    /// </summary>
    private static void ReloadLive(RunScopeStore store)
    {
        string liveFile = store.Live ?? RunSync.SoloRunFile;
        ProfileData? profile = ProfileData.Instance;
        StatManager? manager = ProgressFriend.FromProject<StatManager>();
        StatRepository? stats = manager?.StatRepository;
        string? wait = SaveBackups.RestartNeeded ? "the game has restarted after the restore"
            : profile == null || !profile.IsLoaded ? "the profile is loaded"
            : stats == null || !stats.IsLoaded ? "the stats are loaded"
            : SaveBackups.CloudUndecided ? "Steam Cloud's copies of the restored files are checked (the restore may yield to another device's newer saves)"
            : null;
        if (wait != null)
        {
            if (!_waitLogged)
            {
                _waitLogged = true;
                ModLog.Info($"Progress: the restore's per-run counters for {liveFile} go into memory once {wait}.");
            }

            return;
        }

        _waitLogged = false;
        bool known = store.TryGet(liveFile, out RunScope planned);
        RunScope now = Capture(stats!, profile!.benchQuestData);
        store.Reload = false;
        if (now.SameAs(planned))
        {
            SaveStore(store);
            ModLog.Info($"Progress: the per-run counters in memory are already {liveFile}'s as the restore left them ({RunScopeRestore.Describe(planned)}).");
            return;
        }

        int set;
        try
        {
            set = Write(stats!, profile.benchQuestData, planned);
        }
        catch
        {
            // Half written: what was in memory goes back, and the reload is tried again at the next check.
            Write(stats!, profile.benchQuestData, now);
            store.Reload = true;
            throw;
        }

        SaveStore(store);
        string saved = "saved";
        try
        {
            // The counters were set on the stats themselves, which marks nothing for the Nov 6 build's save: marked here.
            StatsFileWrites.MarkListChanged(stats, "saving the restore's per-run counters");
            manager!.Save();
            profile.Save();
        }
        catch (Exception ex)
        {
            saved = $"NOT saved ({ModLog.SafeErrorText(ex)}; the game's next save writes them)";
        }

        ModLog.Info($"Progress: the per-run counters in memory are now {liveFile}'s as the restore left them ({(known ? RunScopeRestore.Describe(planned) : "none kept: the game's starting values")}; " +
            $"they were {RunScopeRestore.Describe(now)}; {set} stats set); stats and profile {saved}.");
    }

    /// <summary>The counters in memory (the store's live run file's) go into the store, and <paramref name="to"/>'s come out.</summary>
    private static void Swap(RunScopeStore store, string to, string why)
    {
        string liveFile = store.Live!;
        if (string.Equals(liveFile, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProfileData? profile = ProfileData.Instance;
        StatManager? manager = ProgressFriend.FromProject<StatManager>();
        StatRepository? stats = manager?.StatRepository;
        string? wait = SaveBackups.RestartNeeded ? "a restored backup waits for the game to restart"
            : profile == null || !profile.IsLoaded ? "the profile isn't loaded yet"
            : stats == null || !stats.IsLoaded ? "the stats aren't loaded yet"
            : null;
        if (wait != null)
        {
            if (!_waitLogged)
            {
                _waitLogged = true;
                ModLog.Info($"Progress: {liveFile}'s and {to}'s per-run counters are switched once {wait}.");
            }

            return;
        }

        _waitLogged = false;
        RunScope outgoing = Capture(stats!, profile!.benchQuestData);
        if (store.Reload && store.TryGet(liveFile, out RunScope planned))
        {
            // A restore's reload still pending: memory holds the restored files' counters, which belong to nobody; the store's
            // own for the live run file are the ones to put aside.
            outgoing = planned;
            ModLog.Info($"Progress: the restore's per-run counters for {liveFile} hadn't gone into memory yet; they're put aside as the restore left them.");
        }

        store.Reload = false;
        bool kept = store.TryGet(to, out RunScope incoming);
        store.Set(liveFile, outgoing);
        int set;
        try
        {
            set = Write(stats!, profile.benchQuestData, incoming);
        }
        catch
        {
            // Half written: the outgoing run's go back, so the next try starts from them again.
            Write(stats!, profile.benchQuestData, outgoing);
            SaveStore(store);
            throw;
        }

        store.SetLive(to);
        SaveStore(store);

        // The files must hold what the store calls live: a game closed before its next save would otherwise start with the
        // outgoing run's counters under the incoming run's name.
        string saved = "saved";
        try
        {
            // The counters were set on the stats themselves, which marks nothing for the Nov 6 build's save: marked here.
            StatsFileWrites.MarkListChanged(stats, "saving the switched per-run counters");
            manager!.Save();
            profile.Save();
        }
        catch (Exception ex)
        {
            saved = $"NOT saved ({ModLog.SafeErrorText(ex)}; the game's next save writes them)";
        }

        ModLog.Info($"Progress: per-run counters follow the run file ({why}): {liveFile}'s put aside ({outgoing.Stats.Count} stats with a value this run or act, " +
            $"bench quest counts {string.Join("/", outgoing.Quests)}), {to}'s back ({(kept ? $"{incoming.Stats.Count} stats, bench quest counts {string.Join("/", incoming.Quests)}" : "none kept: a new run's, the game's starting values")}; " +
            $"{set} stats set); stats and profile {saved}.");
    }

    private static RunScope Capture(StatRepository stats, BenchQuestProfileData? quests)
    {
        var values = new Dictionary<int, ScopeValues>();
        Il2CppSystem.Collections.Generic.Dictionary<StatType, IStat>? all = stats.Stats;
        if (all != null)
        {
            Il2CppSystem.Collections.Generic.Dictionary<StatType, IStat>.Enumerator items = all.GetEnumerator();
            while (items.MoveNext())
            {
                IStat? stat = items.Current.Value;
                if (stat == null)
                {
                    continue;
                }

                StatValue run = Value(stat.GetRunValue()), act = Value(stat.GetActValue());
                if (run.Kind == StatValueKind.None || (IsZero(run) && IsZero(act)))
                {
                    continue; // a text stat, or nothing this run
                }

                values[(int)items.Current.Key] = new ScopeValues(run, act);
            }
        }

        return new RunScope(quests != null ? ProgressFriend.ReadQuest(quests) : new int[RunScopeStore.QuestCounters], values);
    }

    /// <summary>
    /// Sets every stat's run and act values and the quest counts; returns how many stats were set. A stat
    /// <paramref name="scope"/> has none for starts from the game's own starting values (<c>ResetRun</c>, <c>ResetAct</c>):
    /// zero for a count, but "none yet" (3599 s) for a fastest time, where a zero would be a record no goal can beat.
    /// </summary>
    private static int Write(StatRepository stats, BenchQuestProfileData? quests, RunScope scope)
    {
        int set = 0;
        Il2CppSystem.Collections.Generic.Dictionary<StatType, IStat>? all = stats.Stats;
        if (all != null)
        {
            Il2CppSystem.Collections.Generic.Dictionary<StatType, IStat>.Enumerator items = all.GetEnumerator();
            while (items.MoveNext())
            {
                IStat? stat = items.Current.Value;
                StatValueKind kind = stat == null ? StatValueKind.None : Value(stat.GetRunValue()).Kind;
                if (kind == StatValueKind.None)
                {
                    continue; // a text stat: nothing to count
                }

                if (scope.Stats.TryGetValue((int)items.Current.Key, out ScopeValues kept))
                {
                    stat!.SetRunValue(ProgressFriend.Box(As(kept.Run, kind)));
                    stat.SetActValue(ProgressFriend.Box(As(kept.Act, kind)));
                }
                else
                {
                    stat!.ResetRun();
                    stat.ResetAct();
                }

                set++;
            }
        }

        if (quests != null)
        {
            ProgressFriend.WriteQuest(quests, scope.Quests);
        }

        return set;
    }

    private static StatValue Value(Il2CppSystem.Object? boxed)
    {
        double number = ProgressFriend.Number(boxed);
        if (double.IsNaN(number))
        {
            return StatValue.None;
        }

        return boxed!.ObjectClass == Il2CppInterop.Runtime.Il2CppClassPointerStore<float>.NativeClassPtr ? StatValue.Real((float)number) : StatValue.Whole((int)number);
    }

    private static bool IsZero(StatValue value) => value.Kind == StatValueKind.Real ? value.RealValue == 0f : value.WholeValue == 0;

    private static StatValue As(StatValue value, StatValueKind kind) => kind == StatValueKind.Real
        ? StatValue.Real(value.Kind == StatValueKind.Real ? value.RealValue : value.WholeValue)
        : StatValue.Whole(value.Kind == StatValueKind.Real ? (int)value.RealValue : value.WholeValue);

    /// <summary>A run file's name as the store keeps it: the file name alone (the solo run's when there's none).</summary>
    private static string Name(string? file)
    {
        string name = Path.GetFileName(string.IsNullOrEmpty(file) ? RunSync.SoloRunFile : file);
        return RunScopeStore.IsRunFile(name) ? name : RunSync.SoloRunFile;
    }

    private static bool IsHostRun(string name) => string.Equals(name, Path.GetFileName(CampaignFiles.HostRunPath), StringComparison.OrdinalIgnoreCase);

    private static RunScopeStore Store()
    {
        if (_store != null)
        {
            return _store;
        }

        try
        {
            _store = File.Exists(StorePath) ? RunScopeStore.Parse(File.ReadAllText(StorePath)) : new RunScopeStore();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Progress: the per-run counters kept per run file couldn't be read ({ModLog.SafeErrorText(ex)}); starting again from the game's own.");
            _store = new RunScopeStore();
        }

        return _store;
    }

    private static void SaveStore(RunScopeStore store, bool force = false)
    {
        if (!store.Changed && !force)
        {
            return;
        }

        if (NativeLobby.TourHolds.HoldsFile("Progress", "the per-run counters kept per run file"))
        {
            // The self-driving check tour hosts for real under its save guard: what it does to the counters stays in memory, and the game's files say nothing of it.
            return;
        }

        try
        {
            Directory.CreateDirectory(ProgressLedgerFile.Folder);
            string temporary = StorePath + CampaignFiles.PartSuffix;
            File.WriteAllText(temporary, store.Format());
            File.Move(temporary, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Failed("saving the per-run counters kept per run file", ex);
        }
    }

    private static void Failed(string what, Exception ex)
    {
        if (_failures++ < 3)
        {
            ModLog.Error($"Progress: failed {what}", ex);
        }
    }
}
#endif
