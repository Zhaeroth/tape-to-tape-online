#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppBishopGames.UI;
using Il2CppCysharp.Threading.Tasks;
using Il2CppData;
using Il2CppMichsky.LSS;
using Il2CppPlatformService;
using Il2CppState;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using MelonLoader;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Timing;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// Run sync (D41, D46): the host's run goes to the friends' games, a friend's game loads it with the
/// game's own loader, a friend's own saves stay untouched for the whole session, and the host's co-op runs live in their
/// own save slot.
/// <list type="bullet">
/// <item>Host: the game's run file is the co-op slot (<see cref="CoopSlots"/>), switched at the title screen. After every
/// <c>CampaignState.SaveRunData</c> (the game's only run save, synchronous) the slot file's bytes go to the friends as the
/// next run checkpoint (<see cref="CampaignRelay.PublishRun"/>). A host who starts hosting mid-run keeps playing the solo
/// run, which isn't shared, until the title screen.</item>
/// <item>Friend: <see cref="Apply"/> writes the checkpoint to <c>UserData/T2TOnline/campaign/HostRun.es3</c>, points both
/// run repositories at it and runs the game's own <c>CampaignState.LoadRunData</c>. For the whole session the friend's
/// run saves are skipped at the source, Steam Cloud loads (which rewrite local files) are refused, the save guard holds
/// (Easy Save's backup copies included) and the run's end can't delete a run file. <see cref="RestoreOwnRun"/> puts the
/// friend's own run back. Until the game has its own run in memory again (back at its title screen, after any load of the
/// host's run still running has finished), its run saves stay skipped: the host's map or match can still be on screen
/// after a session ends, and a save from it would write a run that isn't the friend's over their run file.</item>
/// <item>Both: Steam Cloud never sees the mod's files (absolute paths), and the player's saves are backed up before every
/// session and checked by hash after it (<see cref="SaveBackups"/>).</item>
/// </list>
/// The patches go in at start-up and stay (D38); outside a session they let everything through. Nothing here throws into
/// the game: a patch that fails lets the game carry on, except on a friend, where it doesn't write. What may be saved when
/// is decided by <see cref="RunSaveSafety"/> (Core, tested); this class feeds it the game's events and acts on its answers.
/// </summary>
internal sealed class RunSync : ICampaignModule
{
    /// <summary>The game's own run file (run data and run stats, D41).</summary>
    public const string SoloRunFile = SaveGuard.RunFile;

    private const double LoadTimeoutSeconds = 30;
    private const double BusyGameWaitSeconds = 10;
    private const double TitleCheckSeconds = 0.25;
    private const double RecheckSeconds = 0.5;
    private const int Rechecks = 3;
    private const double OrphanWatchSeconds = 60;
    private const int LoggedSkips = 5;
    private const int LoggedErrors = 5;

    // After the host's lineup closes: the map's own close work (its camera, its relics, which save the run when they
    // changed) goes first, then the run's team is looked at.
    private const double LineupSettleSeconds = 0.5;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly ErrorLogLimit Errors = new(LoggedErrors);
    private static bool _installed;
    private static bool _cloudLoadGuarded;

    private readonly CampaignContext _context;

    // What the game's run file is, whether the run in memory is that file's, and so whether run saves go through.
    private readonly RunSaveSafety _safety = new();
    private LoadJob? _job;
    private LoadJob? _next;
    private bool _switchProblemLogged;
    private double _nextTitleCheck;
    private string? _soloRunSinceCoop;
    private bool _squadRemembered;
    private string? _ownLastSquad;
    private UniTask<bool>.Awaiter? _orphan;
    private double _orphanSince;
    private bool _orphanSlowLogged;
    private double _nextStaleCheck;
    private double _nextCloudCheck;

    // The title's New Run and Abandon Run while its campaign buttons aren't reloaded for the run file as it now is, and when
    // run sync last started that reload (seconds on the module's clock; negative: never).
    private readonly TitleRunButtonsHold _titleButtons = new();
    private double _titleReloadAt = -1;

    // Host: the run save in progress (its prefix let it through on the co-op slot), and the file before it.
    private bool _captureNext;
    private SaveStamp _beforeSave;
    private DateTime _saveStartedUtc;
    private double _recheckAt = double.MaxValue;
    private int _rechecksLeft;
    private bool _saveFailing;
    private bool _formatLogged;
    private string? _typeProblemLogged;
    private int _skippedSaves;
    private int _skippedStaleSaves;
    private int _refusedCloudLoads;
    private int _skippedCloudCalls;
    private int _skippedConcedes;
    private int _unchangedRunFile;

    // Host: the run team's print as the run was last saved in this session (null: not yet), and when to look at the team
    // after the host's lineup closed (LineupSave).
    private long? _savedTeamPrint;
    private double _lineupCheckAt = double.MaxValue;

    // Friend: the run this player's own file held as following started, to compare once it's back in memory.
    private RunFileSummary? _ownRunBefore;
    private bool _ownRunKnown;
    private string? _ownRunHash;
    private bool _ownRunWanted;

    // Both: the team of the last checkpoint published or loaded here, so a change of skaters can be named.
    private RunTeamRoster? _lastTeam;

    /// <summary>Made once by <see cref="CampaignModules"/>.</summary>
    public RunSync(CampaignContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Instance = this;
    }

    /// <summary>The module of the running game, or null before it's made.</summary>
    public static RunSync? Instance { get; private set; }

    /// <inheritdoc />
    public string Name => "Run sync";

    /// <summary>Friend: true while this PC's campaign state holds the host's run (its run repositories point at the mod's file).</summary>
    public bool HoldingHostRun => _safety.HoldingHostRun;

    /// <summary>What the game's run file is: 0 the solo run, 1-3 a co-op slot, -1 the host's run (a friend following).</summary>
    public int RunFileSlot => _safety.ActiveSlot;

    /// <summary>Friend: the newest host checkpoint loaded into the game (0: none, or a run-start preview, <see cref="HeldPreviewHash"/>).</summary>
    public uint AppliedCheckpoint { get; private set; }

    /// <summary>
    /// Friend: the hash of the run-start preview the game holds (<see cref="ApplyPreview"/>; 0: none). A preview isn't a
    /// numbered checkpoint: <see cref="AppliedCheckpoint"/> stays 0 while it's held, so every view that needs a real one
    /// loads it, and the first real checkpoint replaces it.
    /// </summary>
    public ulong HeldPreviewHash { get; private set; }

    /// <summary>
    /// Friend: the game has just loaded a run of the host's (the checkpoint's number, or 0 for a run-start preview), after the
    /// game's own loader finished and before anyone waiting is answered. Handlers run on the game thread, each guarded; they
    /// may change the run in memory (a look the run file doesn't keep), never load or save one.
    /// </summary>
    public static event Action<uint>? HostRunLoaded;

    /// <summary>Friend: a checkpoint is being loaded (the game's loader is asynchronous); <see cref="Apply"/> waits its turn.</summary>
    public bool Busy => _job != null || _next != null;

    /// <summary>
    /// Host: true while hosting with the chosen co-op slot as the game's run file, i.e. the campaign is shared. False while
    /// a host who started hosting mid-run still plays the solo run: then only the title view goes to friends.
    /// </summary>
    public bool HostOnCoopSlot => _safety.HostOnCoopSlot;

    /// <summary>
    /// Host: true while a run save has happened whose checkpoint isn't published yet (the file hadn't changed on disk, or
    /// couldn't be read, when the save returned; it's read again shortly), or whose write never reached the disk (until a
    /// later save does). A view that needs this save's run waits for it.
    /// </summary>
    public bool CheckpointPending { get; private set; }

    /// <summary>
    /// True once Steam Cloud's load and save are patched to leave the mod's files out (<see cref="CloudCall"/>). Without it, a
    /// run repository pointed at a mod file would hand that file to Steam Cloud under its absolute path: nothing writes one
    /// then (the run-start preview refuses).
    /// </summary>
    public static bool CloudGuarded { get; private set; }

    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <inheritdoc />
    public void StartHosting(CampaignRelay relay)
    {
        SaveBackups.BeginSession("host", "before hosting");
        _safety.StartHosting(CoopSlots.Selected);
        _saveFailing = false;
        _lastTeam = null;
        _savedTeamPrint = null;
        _lineupCheckAt = double.MaxValue;
        _ownRunWanted = false; // what comes back in memory from here on is the co-op slot's
        int slot = _safety.HostSlot;
        if (_safety.ActiveSlot == slot)
        {
            ModLog.Info($"Run sync: hosting on co-op slot {slot}, already the game's run file: the campaign is shared.");
            return;
        }

        if (TrySwitchAtTitle("hosting started"))
        {
            return;
        }

        if (CampaignGame.Title() != null)
        {
            // A run started from what's open now (a squad select, say) would still be the solo run: say so on screen.
            ModLog.Info($"Run sync: hosting: co-op slot {slot} becomes the game's run file once nothing is open on the title screen; " +
                "a run started before that is your solo run.");
            _context.Notify(NoticeId.CoopAtTitle);
            return;
        }

        ModLog.Info($"Run sync: hosting started away from the title screen: the game keeps saving your solo run, which isn't shared. " +
            $"Co-op slot {slot} becomes the game's run file at the title screen.");
        _context.Notify(NoticeId.CoopAtTitle);
    }

    /// <inheritdoc />
    public void StopHosting(string reason)
    {
        SaveBackups.EndSession(friend: false, _soloRunSinceCoop);
        _safety.StopHosting();
        CheckpointPending = false;
        _lineupCheckAt = double.MaxValue;
        _recheckAt = double.MaxValue;
        _saveFailing = false;
        LogSuppressedErrors();
        if (_safety.ActiveSlot <= 0 || TrySwitchAtTitle("hosting stopped"))
        {
            return;
        }

        ModLog.Info($"Run sync: hosting stopped ({reason}); the co-op run keeps saving to co-op slot {_safety.ActiveSlot} until the title screen, " +
            "where your solo run becomes the game's run file again.");
    }

    /// <inheritdoc />
    public void StartFollowing(CampaignRelay relay)
    {
        // The reason is kept in the backup's manifest, which outlives the session: it names no one (D129).
        SaveBackups.BeginSession("friend", "before following a host");
        NoteOwnRun();
        _lastTeam = null;
        AppliedCheckpoint = 0;
        HeldPreviewHash = 0;

        // Each play period's own first few lines: a lobby's session has many, and the log should show each one's guards at work.
        _skippedSaves = 0;
        _skippedConcedes = 0;
        _refusedCloudLoads = 0;
        OwnRunPlace ownRun = OwnRunWhere();
        _safety.StartFollowing(guardHolds: false); // run saves are skipped from here, whatever happens next
        if (ownRun != OwnRunPlace.None)
        {
            // A join is refused there (BlocksJoining); this session started anyway (a join that raced a Continue, say). What
            // it leaves of the run in memory (a match whose end is the host's) is never saved: held until the title is back.
            _safety.OwnRunLeftInPlay(Now);
        }

        _safety.GuardHeld(SaveGuard.HoldForCampaign($"following {LogNames.Tag(relay.FollowedHost)}")); // this session's hold takes over from the last one's
        if (!_safety.GuardHolds)
        {
            ModLog.Warn("Run sync: this PC's save guard couldn't be put on, so the host's run won't be loaded here: your own saves come first.");
            _context.Notify(NoticeId.GuardFailed);
            return;
        }

        ModLog.Info($"Run sync: following {LogNames.HostTag()}: until the host's game is over here this PC skips its run saves, refuses Steam Cloud loads, " +
            "refuses save writes and Easy Save backups, and never deletes a run file.");
        if (ownRun != OwnRunPlace.None)
        {
            // The game saves a run only at its save points (a node, a map load); what this player did since the last one was
            // only in memory, which the session replaces. Said, so it isn't a surprise next time they continue.
            ModLog.Warn($"Run sync: the session started during this player's own run ({(ownRun == OwnRunPlace.Map ? "on its world map" : "in a match of its own")}): " +
                "what that run did since its last save point isn't kept; its run file is left as it was, and run saves stay held until the title screen has reloaded it after the session.");
            _context.Notify(NoticeId.OwnRunReplaced);
        }
    }

    /// <inheritdoc />
    public void StopFollowing(string reason, bool gameClosing)
    {
        Restore($"the session ended: {reason}", refreshTitle: !gameClosing);
        SaveBackups.EndSession(friend: true, null);
        LogSuppressedErrors();
        bool hostCampaignOnScreen = _safety.StopFollowing(gameClosing);
        if (!gameClosing && !_safety.MayHoldAnotherRun)
        {
            SayOwnRunBack(); // no run of the host's was held: this player's stayed in memory
        }

        if (hostCampaignOnScreen)
        {
            // The host's map or match is still on screen, with nothing of this player's behind it: what it saves or deletes
            // (a world-map menu closing saves the run, quitting the map saves it) mustn't reach this PC's files.
            _safety.GuardKeptUntilTitle(SaveGuard.HoldForCampaign("the session ended on the host's campaign; until this game's title screen loads"));
            ModLog.Info("Run sync: this PC's run saves and save writes stay refused until its title screen loads (the host's campaign is still on screen).");
            return;
        }

        SaveGuard.HoldForCampaign(null);
    }

    /// <inheritdoc />
    public void Update()
    {
        // The work runs from the game's own update (Tick), so it carries on between sessions too.
    }

    /// <inheritdoc />
    public IEnumerable<string> Status()
    {
        if (_safety.Hosting && !HostOnCoopSlot)
        {
            yield return "solo run, not shared: go to the title screen for co-op";
        }

        if (_saveFailing)
        {
            yield return "your co-op run isn't saving (see the log)";
        }
    }

    /// <summary>
    /// Friend: loads <paramref name="checkpoint"/> into the game with the game's own loader (<c>CampaignState.LoadRunData</c>,
    /// with the run repositories pointed at the mod's file), over as many frames as it takes. <paramref name="done"/> is
    /// called once: Ok with the checkpoint's sequence when the game holds it, or why not. A checkpoint already applied (or an
    /// older one) answers Ok at once. One load runs at a time: a checkpoint arriving meanwhile waits, and a newer one replaces
    /// a waiting one, whose callers then get the newer one's answer.
    /// </summary>
    public void Apply(RunCheckpoint checkpoint, Action<FollowResult> done)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(done);
        if (!_safety.MayLoadHostRun)
        {
            Answer(done, new FollowResult(false, AppliedCheckpoint,
                _safety.Following ? FollowReasons.SaveGuardOff : FollowReasons.NotFollowing, Applied: false));
            return;
        }

        if (_job == null && HoldingHostRun && AppliedCheckpoint != 0 && !SequenceMath.IsNewer(checkpoint.Sequence, AppliedCheckpoint))
        {
            Answer(done, new FollowResult(true, AppliedCheckpoint, string.Empty));
            return;
        }

        if (_job != null)
        {
            if (!_job.Preview && !SequenceMath.IsNewer(checkpoint.Sequence, _job.Checkpoint.Sequence))
            {
                _job.Waiting.Add(done); // the one loading now (or a newer one) answers this too
            }
            else if (_next == null)
            {
                _next = new LoadJob(checkpoint, done);
                ModLog.Info($"Run sync: the host's run {checkpoint.Sequence} waits for {JobName(_job)} to finish loading.");
            }
            else
            {
                if (_next.Preview || SequenceMath.IsNewer(checkpoint.Sequence, _next.Checkpoint.Sequence))
                {
                    ModLog.Info($"Run sync: the host's run {checkpoint.Sequence} replaces {JobName(_next)}, which was waiting to load.");
                    _next.Checkpoint = checkpoint;
                    _next.Preview = false;
                }

                _next.Waiting.Add(done);
            }

            return;
        }

        _job = new LoadJob(checkpoint, done);
        Advance();
    }

    /// <summary>
    /// Friend: loads the host's run-start preview (<see cref="RunPreviewSet.RunFile"/>) the way a
    /// checkpoint is loaded (the mod's file, the game's own loader), but as no numbered checkpoint: afterwards
    /// <see cref="AppliedCheckpoint"/> is 0 and <see cref="HeldPreviewHash"/> is <paramref name="hash"/>.
    /// <paramref name="done"/> is called once: Ok when the game holds it, or why not. A preview already held answers Ok at
    /// once; one loading answers when it's in. It never pushes a numbered checkpoint aside: while one is loading or waiting
    /// to, the answer is that the host's run moved on.
    /// </summary>
    public void ApplyPreview(byte[] runFile, ulong hash, Action<FollowResult> done)
    {
        ArgumentNullException.ThrowIfNull(runFile);
        ArgumentNullException.ThrowIfNull(done);
        if (!_safety.MayLoadHostRun)
        {
            Answer(done, new FollowResult(false, AppliedCheckpoint,
                _safety.Following ? FollowReasons.SaveGuardOff : FollowReasons.NotFollowing, Applied: false));
            return;
        }

        if (_job == null && HoldingHostRun && AppliedCheckpoint == 0 && HeldPreviewHash == hash && hash != 0)
        {
            Answer(done, new FollowResult(true, 0, string.Empty));
            return;
        }

        if ((_job != null && !_job.Preview) || (_next != null && !_next.Preview))
        {
            uint loading = _next?.Preview == false ? _next.Checkpoint.Sequence : _job!.Checkpoint.Sequence;
            Answer(done, new FollowResult(false, AppliedCheckpoint, $"the host's run moved on (its run {loading} is loading here)", Applied: false));
            return;
        }

        RunCheckpoint preview;
        try
        {
            preview = new RunCheckpoint(0, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = runFile });
        }
        catch (ArgumentException ex)
        {
            Answer(done, new FollowResult(false, AppliedCheckpoint, $"the host's preview can't be held ({ModLog.SafeErrorText(ex)})", Applied: false));
            return;
        }

        if (preview.Hash != hash)
        {
            Answer(done, new FollowResult(false, AppliedCheckpoint, FollowReasons.PreviewNotItsRun, Applied: false));
            return;
        }

        if (_job != null)
        {
            // Another preview is loading: the same one answers this too; a newer one waits for it (and replaces an older
            // one that's waiting, whose callers then get the newer one's answer).
            if (_job.Checkpoint.Hash == hash)
            {
                _job.Waiting.Add(done);
            }
            else if (_next == null)
            {
                _next = new LoadJob(preview, done) { Preview = true };
            }
            else
            {
                _next.Checkpoint = preview;
                _next.Waiting.Add(done);
            }

            return;
        }

        _job = new LoadJob(preview, done) { Preview = true };
        Advance();
    }

    /// <summary>
    /// Friend: puts the friend's own run back (the repositories back on the game's own file, the campaign state reset, the
    /// profile's last squad restored, the mod's file deleted). Synchronous; safe to call when nothing is held.
    /// </summary>
    public void RestoreOwnRun(string why) => Restore(why, refreshTitle: true);

    /// <summary>
    /// True when the co-op slot may change now; else why not, worded to follow "Slot N wasn't selected: " (a co-op run may
    /// be in progress).
    /// </summary>
    public bool CanChangeCoopSlot(out string problem) => !_safety.CoopSlotLocked(CampaignGame.IdleTitle() != null, out problem);

    /// <summary>
    /// For a restore of co-op slot <paramref name="slot"/>: true when that slot isn't the game's run file, so its file may be
    /// replaced. A slot stays the run file after hosting stopped until the title is idle (<see cref="TrySwitchAtTitle"/>), and
    /// the title isn't while a menu is over it: a host who ends the session from the game's Settings menu and opens the
    /// Backups screen from there would be refused for as long as Settings stays open. So when only that switch is waited
    /// for (not hosting, not following, the title's scene loaded, no load of a host's run running), the run file is pointed
    /// back at the solo run here, as the title's own scene load does it (<see cref="OnSceneLoaded"/>): run saves are held
    /// from then, and the title reloads the solo run once it's idle. False when the slot can't be let go now.
    /// </summary>
    public bool FreeSlotForRestore(int slot)
    {
        if (_safety.ActiveSlot != slot)
        {
            return true;
        }

        if (_safety.WantedSlot != RunSaveSafety.SoloSlot || !_safety.MaySwitchRunFile(hostLoadRunning: _job?.Awaiter != null) || !CampaignGame.TitleLoaded())
        {
            return false;
        }

        return SwitchTo(RunSaveSafety.SoloSlot, $"co-op slot {slot} is being restored from a backup");
    }

    /// <summary>
    /// Host: the game's start can be called on <paramref name="title"/> now: it's idle, the wanted slot is the game's run file, and no refresh is running or wanted (else its
    /// <c>m_HasOngoingCampaign</c> is stale, and run saves are held: <see cref="RunSaveSafety.LoadBeforeSwitch"/>).
    /// </summary>
    public bool ReadyToStart(TitleScreen title) => CampaignGame.IsIdle(title) && _safety.WantedSlot == _safety.ActiveSlot && !_safety.RefreshWanted && _job == null;

    /// <summary>Host: a later Start: the save check of the game before it, and a backup again (New Run can delete a slot's run).</summary>
    public void BackupAgain(string why)
    {
        SaveBackups.EndSession(friend: false, _soloRunSinceCoop);
        SaveBackups.BeginSession("host", why);
    }

    /// <summary>The CoopSlot setting changed (<see cref="CoopSlots.TrySelect"/>): a host's game switches to it now if it can.</summary>
    public void CoopSlotChanged()
    {
        _safety.CoopSlotChosen(CoopSlots.Selected);
        if (_safety.Hosting)
        {
            TrySwitchAtTitle("the co-op slot changed");
        }
    }

    /// <summary>Tells the player something, through the notices' one router (<see cref="NoticeId"/>).</summary>
    public void Notify(NoticeId id, params string[] args) => _context.Notify(id, args);

    /// <summary>
    /// Host: the host's lineup menu closed. Its leave applied the roster to the run's team in memory only, and the world map
    /// saves the run only at its save points, so friends would get the change only with the run's next save: after the next
    /// match's start (a friend's match setup puts its copy in the host's order meanwhile), and a friend who joins would get
    /// the run from before it. A moment later the team is looked at (<see cref="LineupSave"/>): changed since the run was last
    /// saved, on the world map with nothing under way, the shared run is saved once with the game's own save, which sends it
    /// to friends as the next checkpoint.
    /// </summary>
    public void HostLineupClosed()
    {
        if (_safety.Hosting && HostOnCoopSlot)
        {
            _lineupCheckAt = Now + LineupSettleSeconds;
        }
    }

    /// <summary>Patches the game's run save, Steam's cloud calls and the run's end, once, and starts the update. Adds a line to <paramref name="failed"/> for each patch that couldn't go in.</summary>
    internal static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        if (_installed)
        {
            return;
        }

        // The co-op slots and backups moved out of the game folder, a restore the game stopped in the middle of put right and
        // a stopped session's files removed came first, on every launch (SavesAtLaunch, with the Steam Cloud load guard).
        int before = failed.Count;
        Patch(harmony, failed, typeof(CampaignState), nameof(CampaignState.SaveRunData), nameof(SaveRunDataPrefix), nameof(SaveRunDataPostfix));
        bool cloudLoad = InstallCloudLoadGuard(harmony, failed);
        bool cloudSave = Patch(harmony, failed, typeof(SteamService), nameof(SteamService.Save), nameof(CloudSavePrefix), null);
        CloudGuarded = cloudLoad && cloudSave;
        Patch(harmony, failed, typeof(SteamService), nameof(SteamService.DeleteFile), nameof(CloudDeletePrefix), null);
        Patch(harmony, failed, typeof(CampaignLoader), nameof(CampaignLoader.OnRunConcede), nameof(RunConcedePrefix), null);
        Patch(harmony, failed, typeof(TitleScreen), nameof(TitleScreen.OnNewRunClicked), nameof(NewRunClickPrefix), null);
        Patch(harmony, failed, typeof(TitleScreen), nameof(TitleScreen.OnAbandonRunClicked), nameof(AbandonRunClickPrefix), null);
        MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneLoaded);
        MelonEvents.OnUpdate.Subscribe(Tick);
        _installed = true;
        if (failed.Count == before)
        {
            ModLog.Info("Run sync: ready (the run save is captured on a host and skipped on a friend; Steam Cloud never sees the mod's files).");
        }
    }

    /// <summary>
    /// The game's loads from Steam Cloud go through <see cref="CloudLoadPrefix"/>, once: a restored file Steam Cloud must still
    /// get is kept and sent there (<see cref="SaveBackups.KeepRestoredFile"/>), and a friend's or a mod file's load is refused.
    /// It needs no session (<see cref="Instance"/> may be null), so it goes in at the mod's start-up on every launch of a game
    /// build the mod may patch, online or not (<see cref="SavesAtLaunch"/>), and <see cref="Install"/> finds it in. False, with a
    /// line in <paramref name="failed"/>, when it couldn't go in.
    /// </summary>
    internal static bool InstallCloudLoadGuard(HarmonyLib.Harmony harmony, List<string> failed)
    {
        if (!_cloudLoadGuarded)
        {
            _cloudLoadGuarded = Patch(harmony, failed, typeof(SteamService), nameof(SteamService.Load), nameof(CloudLoadPrefix), null);
        }

        return _cloudLoadGuarded;
    }

    /// <summary>
    /// At the mod's start-up, on every launch whatever online's state (<see cref="SavesAtLaunch"/>): the files that last a session at
    /// most, left by a game that stopped during one: a host's run, and a copy of it being loaded (a session's end deletes them; a
    /// crash doesn't). No session is on, so nothing reads them. Never throws.
    /// </summary>
    internal static void RemoveSessionLeftovers()
    {
        foreach (string path in new[] { CampaignFiles.HostRunPath, Progress.RunCopyLoader.FilePath })
        {
            try
            {
                if (CampaignFiles.DeleteWithCopies(path) > 0)
                {
                    ModLog.Info($"Run sync: removed {Path.GetFileName(path)}, left by a game that stopped during a session.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Run sync: couldn't remove {Path.GetFileName(path)}, left by a game that stopped during a session ({CampaignFiles.ErrorText(ex)}).");
            }
        }
    }

    /// <summary>Patches one method; false (with a line in <paramref name="failed"/>) when it couldn't go in.</summary>
    private static bool Patch(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, string? prefix, string? postfix)
    {
        string what = $"{type.Name}.{method}";
        try
        {
            MethodInfo? target = AccessTools.Method(type, method);
            if (target == null)
            {
                failed.Add($"run sync: {what} not found");
                return false;
            }

            // First, so a friend's skip comes before any other campaign part's patch and the checkpoint before their postfixes.
            harmony.Patch(
                target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(RunSync), prefix) { priority = Priority.First },
                postfix: postfix == null ? null : new HarmonyMethod(typeof(RunSync), postfix) { priority = Priority.First });
            return true;
        }
        catch (Exception ex)
        {
            failed.Add($"run sync: {what} couldn't be patched ({ModLog.SafeErrorText(ex)})");
            return false;
        }
    }

    // ---- Every frame and at scene loads ----

    private static void Tick()
    {
        RunSync? sync = Instance;
        if (sync == null)
        {
            return;
        }

        try
        {
            sync.Step();
        }
        catch (Exception ex)
        {
            Failed("its update", ex);
        }
    }

    private void Step()
    {
        if (_job != null)
        {
            Advance();
        }

        if (_orphan != null)
        {
            WatchOrphan();
        }

        if (Now >= _recheckAt)
        {
            _recheckAt = double.MaxValue;
            Recapture();
        }

        if ((_safety.WantedSlot != _safety.ActiveSlot || _safety.RefreshWanted) && Now >= _nextTitleCheck)
        {
            _nextTitleCheck = Now + TitleCheckSeconds;
            TrySwitchAtTitle(_safety.Hosting ? "hosting" : "at the title screen");
        }

        if (_safety.StaleRun != null && Now >= _nextStaleCheck)
        {
            _nextStaleCheck = Now + TitleCheckSeconds;
            WatchStaleRun();
        }

        if (Now >= _lineupCheckAt)
        {
            _lineupCheckAt = double.MaxValue;
            SaveChangedLineup();
        }

        if (SaveBackups.PushDue && Now >= _nextCloudCheck)
        {
            // Restored files the game didn't load on its way to the title go to Steam Cloud there (once a game session).
            _nextCloudCheck = Now + TitleCheckSeconds;
            if (CampaignGame.TitleLoaded())
            {
                SaveBackups.PushRestoredAtTitle();
            }
        }
    }

    private static void OnSceneLoaded(int buildIndex, string name)
    {
        try
        {
            if (Instance is not { } sync)
            {
                return;
            }

            if (name != CampaignGame.TitleScene)
            {
                // The game left its title screen (scenes load one at a time) for a run: on the way it loaded (Continue) or
                // made (New Run) the run of its run file, so that's the run in memory now, unless it may be from before a
                // switch. Decided here, before the new scene's first save (the world map saves as it starts).
                sync.Changed(sync._safety.OtherSceneLoaded(SafeText.Clean(name, 32), Now));
                return;
            }

            if (sync._safety.TitleSceneLoaded())
            {
                SaveGuard.HoldForCampaign(null);
            }

            // The title screen's own refresh (from its OnEnable, before this event) is already loading the run file as it was;
            // after a switch here, run sync refreshes it again once it's idle, and a run started before that is held
            // (RunSaveSafety.LoadBeforeSwitch): it may be the run from before the switch.
            int wanted = sync._safety.WantedSlot;
            if (wanted != sync._safety.ActiveSlot)
            {
                sync.SwitchTo(wanted, "the title screen loaded");
            }
        }
        catch (Exception ex)
        {
            Failed("a scene load", ex);
        }
    }

    // ---- The game's run file: the solo run, a co-op slot, or (friend) the host's run ----

    /// <summary>
    /// At the title screen with nothing open on it: switches the run file if it should change, and refreshes the title's
    /// campaign buttons. Not while a load of the host's run is running: the game keeps one load and makes a second caller
    /// wait on it, and two waiting on one load is an error in the game's task library.
    /// </summary>
    private bool TrySwitchAtTitle(string why)
    {
        TitleScreen? title = _safety.MaySwitchRunFile(hostLoadRunning: _job?.Awaiter != null) ? CampaignGame.IdleTitle() : null;
        if (title == null)
        {
            return false;
        }

        int wanted = _safety.WantedSlot;
        if (wanted != _safety.ActiveSlot && !SwitchTo(wanted, why))
        {
            return false;
        }

        _safety.TitleRefreshed();
        title.RefreshCampaignData();
        _titleReloadAt = Now; // its buttons are the run file's once this load has ended (TitleRunPress)
        int active = _safety.ActiveSlot;
        ModLog.Info($"Run sync: the title screen reloads {(active > 0 ? $"co-op slot {active}'s run" : active == 0 ? "your solo run" : "the host's run")} ({why}).");
        return true;
    }

    private bool SwitchTo(int slot, string why)
    {
        CampaignState? state = CampaignGame.State();
        string file = slot > 0 ? CampaignFiles.RunFileFor(slot) : SoloRunFile; // a campaign check tour's own file stands in for the slot
        string problem;
        if (state == null)
        {
            problem = "the game's campaign isn't there yet";
        }
        else
        {
            if (slot > 0)
            {
                Directory.CreateDirectory(CampaignFiles.SlotsFolder);
            }

            string? soloRun = slot > 0 && _safety.ActiveSlot <= 0 ? SaveBackups.SoloRunHash() : _soloRunSinceCoop;
            bool titleLoading = CampaignGame.TitleRefreshing();
            string? from = CampaignGame.RunFileName(state);
            if (CampaignGame.PointRunFilesAt(state, file, out problem))
            {
                _safety.Switched(slot, titleLoading, Now);

                // The game's per-run stat counters and bench-quest counts belong to the run file they came from.
                Progress.ProgressRunScopes.Switching(from, file);
                _soloRunSinceCoop = slot > 0 ? soloRun : null;
                _switchProblemLogged = false;
                if (slot <= 0)
                {
                    NativeLobby.TourHolds.SoloRunBack(); // a stopped campaign tour's run file, kept while its run was in play, goes
                }

                ModLog.Info(slot > 0
                    ? $"Run sync: the game's run file is co-op slot {slot} ({CampaignFiles.RunFileLabel(slot)}) ({why}); your solo run is left alone."
                    : $"Run sync: the game's run file is your solo run ({SoloRunFile}) again ({why}).");
                if (slot > 0 && _safety.Hosting)
                {
                    _context.Notify(NoticeId.CoopSlotSwitched, $"Co-op slot {slot} is your campaign while hosting; your solo run is left alone.");
                }

                return true;
            }
        }

        if (!_switchProblemLogged)
        {
            _switchProblemLogged = true;
            ModLog.Warn($"Run sync: couldn't make {(slot > 0 ? $"co-op slot {slot}" : "your solo run")} the game's run file yet ({problem}); trying again at the title screen.");
        }

        return false;
    }

    // ---- Whether the run in memory is the run file's (RunSaveSafety decides) ----

    /// <summary>
    /// While the run in memory may not be the run file's: once the title screen is up, after run sync's refresh of it has
    /// finished (and a moment for anything still running from the scene before), or as soon as a run starts from it (or
    /// the game has left it), the game has the run file's run in memory again (<see cref="RunSaveSafety.WatchStale"/>).
    /// </summary>
    private void WatchStaleRun()
    {
        bool queued = _job != null;
        if (!_safety.WatchesTitle(queued))
        {
            return;
        }

        bool loaded = CampaignGame.TitleLoaded();
        TitleScreen? title = loaded ? CampaignGame.Title() : null;
        var view = new TitleView(loaded, title != null, title is { m_IsStartingRun: true }, title != null && CampaignGame.IsIdle(title));
        Changed(_safety.WatchStale(view, queued, Now));
    }

    /// <summary>Logs a change to whether the run in memory is the run file's; one that keeps run saves held is also said on screen.</summary>
    private void Changed(StaleChange? change)
    {
        if (change is not { } stale)
        {
            return;
        }

        if (stale.NowCurrent)
        {
            ModLog.Info($"Run sync: this game's run saves go through again ({stale.How}; they were held because {stale.Why}).");
            if (_safety.RefreshWanted)
            {
                // The game left the title in the moment between the title becoming free and run sync's reload of it (at most
                // TitleCheckSeconds). A run continued then is loaded afresh from the run file as it is now (the game's Continue
                // starts its own load unless one is still running, which LoadBeforeSwitch holds), so nothing wrong is saved;
                // only the title's buttons were from before: said, so a log shows it if it ever happens.
                ModLog.Warn("Run sync: the game left its title screen before the title's campaign buttons were reloaded for the run file as it is now " +
                    "(the reload waits for a title with nothing open on it). A run continued from there was loaded from that run file; " +
                    "Continue or New Run may have been offered as for the run file from before.");
            }

            SayOwnRunBack();
            return;
        }

        ModLog.Warn($"Run sync: {stale.How}, but {stale.Why}: this game's run saves stay held until its title screen is back.");
        _context.Notify(NoticeId.RunNotSaved);
    }

    /// <summary>
    /// Friend, as following starts: the run this player's own file holds, said, and noted to compare once it's back in memory
    /// (the save check proves the file, this says what the title loaded). Read once a session. Never throws.
    /// </summary>
    private void NoteOwnRun()
    {
        _ownRunWanted = true;
        _ownRunBefore = null;
        _ownRunKnown = false;
        _ownRunHash = null;
        try
        {
            string path = Path.Combine(UnityEngine.Application.persistentDataPath, SoloRunFile);
            byte[]? bytes = File.Exists(path) ? CampaignFiles.ReadShared(path) : null;
            _ownRunHash = bytes == null ? string.Empty : SaveFileHash.HashOf(bytes);
            _ownRunKnown = bytes == null || RunFileSummary.TryRead(bytes, out _ownRunBefore);
            ModLog.Info(_ownRunKnown
                ? $"Run sync: your own run before following: {OwnRunCheck.Words(_ownRunBefore)}."
                : $"Run sync: your own run before following: {SoloRunFile} doesn't read as a run here, so it isn't compared afterwards.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Run sync: your own run before following couldn't be read ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)}), so it isn't compared afterwards.");
        }
    }

    /// <summary>
    /// The run this player's own file holds is in memory again (the title reloaded it): which it is, against the one noted as following
    /// started. A WARN when it's another run. Once per put-away of the host's run. Never throws.
    /// </summary>
    private void SayOwnRunBack()
    {
        if (!_ownRunWanted || _safety.HoldingHostRun)
        {
            return;
        }

        _ownRunWanted = false;
        try
        {
            RunData? run = CampaignGame.State()?.runData;
            RunFileSummary? now = run == null ? null : new RunFileSummary(run.squadId ?? string.Empty, run._currentAct, run.currentFloor, run.runID ?? string.Empty, false);
            string? hash = SaveBackups.SoloRunHash();
            bool? same = hash == null || _ownRunHash == null ? null : hash == _ownRunHash;
            string line = OwnRunCheck.Line(_ownRunBefore, now, _ownRunKnown, same, SoloRunFile);
            if (_ownRunKnown && OwnRunCheck.Compare(_ownRunBefore, now) == OwnRunVerdict.Other)
            {
                ModLog.Warn(line);
            }
            else
            {
                ModLog.Info(line);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Run sync: couldn't say which run is back in memory ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// A checkpoint's team against the last one's: a skater come or gone is named, the same on both PCs. A read
    /// of the run file's JSON once per checkpoint, as its type check is. Never throws.
    /// </summary>
    private void SayTeamChange(RunCheckpoint checkpoint)
    {
        try
        {
            byte[]? bytes = checkpoint.Get(RunCheckpointPart.Run);
            if (bytes == null || !RunTeamRoster.TryRead(bytes, out RunTeamRoster? team) || team == null)
            {
                return;
            }

            if (team.ChangesSince(_lastTeam) is { } changes)
            {
                ModLog.Info($"Run sync: the host's run {checkpoint.Sequence}'s team changed: {changes}.");
            }

            _lastTeam = team;
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Run sync: the host's run {checkpoint.Sequence}'s team wasn't read for the log ({ex.GetType().Name}).");
        }
    }

    // ---- Host: checkpoints ----

    private static bool SaveRunDataPrefix(CampaignState __instance)
    {
        RunSync? sync = Instance;
        try
        {
            if (sync == null)
            {
                return true;
            }

            sync._captureNext = false;
            string? block = sync._safety.RunSaveBlock;
            if (block != null)
            {
                // A friend skips many (every world-map load saves); a held save outside a session is rare and worth a line each.
                bool session = sync._safety.InFriendSession;
                int count = session ? ++sync._skippedSaves : ++sync._skippedStaleSaves;
                if (count <= LoggedSkips)
                {
                    string message = $"Run sync: skipped this PC's run save (CampaignState.SaveRunData, {count} so far): {block}.";
                    if (session)
                    {
                        ModLog.Info(message);
                    }
                    else
                    {
                        ModLog.Warn(message);
                    }
                }

                return false;
            }

            // Host on the co-op slot: this save's checkpoint is captured after it (a save skipped above has none).
            if (sync.HostOnCoopSlot)
            {
                sync._saveStartedUtc = DateTime.UtcNow;
                sync._beforeSave = SaveStamp.Of(CampaignGame.FullPath(CampaignGame.RunFileName(__instance)));
                sync._captureNext = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            Failed("checking a run save", ex);
            bool write = !MayHoldAnotherRun(); // in doubt, a run that may not be this file's isn't written
            if (sync != null && write)
            {
                // No stamp from before: the file is taken as written by this save, as long as it's there afterwards.
                sync._beforeSave = default;
                sync._saveStartedUtc = DateTime.UtcNow;
                sync._captureNext = sync.HostOnCoopSlot;
            }

            return write;
        }
    }

    /// <summary>For a patch that failed: true when the run in memory may not be the run file's (never throws).</summary>
    private static bool MayHoldAnotherRun() => Instance is { } sync && sync._safety.MayHoldAnotherRun;

    private static void SaveRunDataPostfix(CampaignState __instance)
    {
        try
        {
            // Postfixes run even when a prefix skipped the save: only a save that went through is captured.
            if (Instance is { _captureNext: true } sync)
            {
                sync._captureNext = false;
                sync.Capture(__instance);
                sync._savedTeamPrint = TeamPrint(__instance); // the team as this save wrote it (LineupSave)
            }
        }
        catch (Exception ex)
        {
            Failed("sending the host's run", ex);
        }
    }

    /// <summary>
    /// Host: the game just saved its run (synchronously, so the file is committed): its bytes become the next checkpoint. A
    /// file that hasn't changed is looked at again (<see cref="Recapture"/>); nothing from before this save goes out as its
    /// checkpoint meanwhile.
    /// </summary>
    private void Capture(CampaignState state)
    {
        if (!HostOnCoopSlot || _safety.Following || CampaignRelay.Current is not { Hosting: true } relay)
        {
            return;
        }

        string path = CampaignGame.FullPath(CampaignGame.RunFileName(state));
        SaveStamp after = SaveStamp.Of(path);
        if (RunSaveCheck.Judge(_beforeSave, _saveStartedUtc, after, lastLook: false) != SaveLanded.Written)
        {
            if (++_unchangedRunFile <= LoggedSkips)
            {
                ModLog.Info($"Run sync: the game saved its run, but {Path.GetFileName(path)} {(after.Exists ? "hasn't changed on disk" : "isn't there")}; " +
                    string.Create(CultureInfo.InvariantCulture, $"it's looked at again in {RecheckSeconds} s, and friends wait for it."));
            }

            Recheck();
            return;
        }

        byte[] bytes;
        try
        {
            bytes = CampaignFiles.ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Friends would wait for this run until the host's next save: it's read again shortly instead.
            ModLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Run sync: the game saved its run, but {Path.GetFileName(path)} couldn't be read ({CampaignFiles.ErrorText(ex)}); it's read again in {RecheckSeconds} s."));
            Recheck();
            return;
        }

        LogFormat(bytes);
        Publish(relay, bytes, "written by this save");
        CheckpointPending = false;
        _recheckAt = double.MaxValue;
        SavesAgain();
    }

    /// <summary>Host: this save's checkpoint is pending; the run file is read again in a moment (up to <see cref="Rechecks"/> times).</summary>
    private void Recheck()
    {
        CheckpointPending = true;
        _rechecksLeft = Rechecks;
        _recheckAt = Now + RecheckSeconds;
    }

    /// <summary>
    /// Host: a run save that hadn't reached the disk when it returned (or couldn't be read), looked at again, up to
    /// <see cref="Rechecks"/> times: once the file changed, it's this save's checkpoint. Still unchanged after the last look,
    /// the save never reached the disk (<see cref="RunSaveCheck"/>): nothing is published for it, the host is told, and a
    /// view needing this save's run keeps waiting until a later save reaches the disk.
    /// </summary>
    private void Recapture()
    {
        if (!HostOnCoopSlot || CampaignRelay.Current is not { Hosting: true } relay || CampaignGame.State() is not { } state)
        {
            CheckpointPending = false;
            return;
        }

        string path = CampaignGame.FullPath(CampaignGame.RunFileName(state));
        bool last = --_rechecksLeft <= 0;
        bool again = false;
        bool unsaved = false;
        try
        {
            SaveStamp now = SaveStamp.Of(path);
            switch (RunSaveCheck.Judge(_beforeSave, _saveStartedUtc, now, last))
            {
                case SaveLanded.NotYet:
                    again = true; // not written yet: still pending
                    return;
                case SaveLanded.NotWritten:
                    unsaved = true;
                    NotSaved(Path.GetFileName(path), now.Exists);
                    return;
                case SaveLanded.Unclear:
                    Publish(relay, CampaignFiles.ReadShared(path), "unchanged, but written just before this save, so taken as its run");
                    break;
                default:
                    Publish(relay, CampaignFiles.ReadShared(path), "read again after the save");
                    SavesAgain();
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            again = !last;
            ModLog.Warn(again
                ? string.Create(CultureInfo.InvariantCulture, $"Run sync: {Path.GetFileName(path)} still couldn't be read ({CampaignFiles.ErrorText(ex)}); trying again in {RecheckSeconds} s.")
                : $"Run sync: {Path.GetFileName(path)} couldn't be read after the save ({CampaignFiles.ErrorText(ex)}); friends get the run at the host's next save.");
        }
        finally
        {
            // Whatever happened (even an error), a view waiting on this checkpoint never waits for good, except for a save
            // that never reached the disk: what friends would get is the run from before it.
            if (again)
            {
                _recheckAt = Now + RecheckSeconds;
            }
            else if (!unsaved)
            {
                CheckpointPending = false;
            }
        }
    }

    /// <summary>Host: a run save never reached the disk (<paramref name="file"/> unchanged, or gone): said in the log, and on screen once until saves work again.</summary>
    private void NotSaved(string file, bool exists)
    {
        ModLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Run sync: the game's run save didn't reach the disk: {file} {(exists ? "hasn't changed" : "isn't there")} {Rechecks * RecheckSeconds:0.0} s after it ") +
            "(a full disk, the file locked by another program, or a write refused; an error the game logged is above). Friends keep the run from " +
            "before it and a view that needs it waits for the next save that reaches the disk; your co-op slot holds the run from before it too.");
        if (!_saveFailing)
        {
            _saveFailing = true;
            _context.Notify(NoticeId.CoopSaveFailed);
        }
    }

    /// <summary>Host: a run save reached the disk; if saves had been failing, that's said.</summary>
    private void SavesAgain()
    {
        if (_saveFailing)
        {
            _saveFailing = false;
            ModLog.Info("Run sync: the co-op run's saves reach the disk again.");
            _context.Notify(NoticeId.CoopSaveBack);
        }
    }

    private void Publish(CampaignRelay relay, byte[] bytes, string note)
    {
        uint before = relay.LatestRun?.Sequence ?? 0;
        RunCheckpoint? checkpoint = relay.PublishRun(bytes);
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Run sync: the host's run in co-op slot {_safety.ActiveSlot} ({bytes.Length / 1024.0:0.0} KB, {note}): ") +
            (checkpoint != null ? $"checkpoint {checkpoint.Sequence}." : $"the same as checkpoint {before}, so nothing new is sent."));
        if (checkpoint != null)
        {
            SayTeamChange(checkpoint);
        }

        if (checkpoint != null && !RunFileTypes.TryCheck(bytes, RunFileTypes.RunFile, out string problem) && problem != _typeProblemLogged)
        {
            // Friends' games run the same check and would refuse this run: said here at once, so a type the list misses
            // (a new game build, say) shows on the host's log too.
            _typeProblemLogged = problem;
            ModLog.Warn($"Run sync: friends' games will refuse checkpoint {checkpoint.Sequence}: {SafeText.Clean(problem, 200)} " +
                "(Core.Campaign.RunFileTypes.RunFile lists the types a run file may name).");
        }
    }

    private void LogFormat(byte[] bytes)
    {
        if (_formatLogged)
        {
            return;
        }

        _formatLogged = true;
        if (!RunFileSummary.LooksLikeJson(bytes))
        {
            ModLog.Warn($"Run sync: the run file isn't plain JSON (it starts 0x{(bytes.Length > 0 ? bytes[0] : 0):X2}); friends' games may not load it.");
            return;
        }

        ModLog.Info(RunFileSummary.TryRead(bytes, out RunFileSummary? run) && run != null
            ? $"Run sync: the run file is plain JSON, as expected (squad {HostText.KeyForLog(run.SquadId)}, act {run.Act}, floor {run.Floor})."
            : "Run sync: the run file is JSON, but its run couldn't be read (it shows as just \"a run\").");
    }

    /// <summary>
    /// Host, a moment after its lineup closed: the run's team against the team as the run was last saved, and the shared run
    /// saved once when it changed on a quiet world map (<see cref="LineupSave.Decide"/>). One line when it changed; nothing
    /// when it didn't. Never throws (Step's guard).
    /// </summary>
    private void SaveChangedLineup()
    {
        // The world map's own campaign state when it's up (the one its relic refresh saves), else the game's.
        bool onMap = Scenes.WorldMap.MapLoaded;
        CampaignState? state = (onMap ? Scenes.WorldMap.State : null) ?? CampaignGame.State();
        if (TeamPrint(state) is not long now)
        {
            return;
        }

        bool shared = _safety.Hosting && HostOnCoopSlot && CampaignRelay.Current is { Hosting: true };
        bool eventOpen = onMap && UnityEngine.Object.FindObjectOfType<EventChoiceMenu>() != null;
        bool busy = onMap && (Scenes.WorldMap.Loading || CheckpointPending || Scenes.PreviewMirror.Menu is { IsShown: true } ||
            WorldMapMenusHandler.s_Instance is { m_IsNpcPushHeld: true } || TutorialRuns());
        LineupSaveStep step = LineupSave.Decide(shared, onMap, eventOpen, busy, _savedTeamPrint, now);
        if (step is LineupSaveStep.Unchanged or LineupSaveStep.NotShared)
        {
            return;
        }

        string since = _savedTeamPrint == null ? "the run's team isn't one this session has seen saved" : "the host's lineup changed the run's team since the run was last saved";
        ModLog.Info($"Run sync: {since} (the game saves a lineup only at its next save point); {LineupSave.Words(step)}.");
        if (step != LineupSaveStep.Save || state == null)
        {
            return;
        }

        try
        {
            // The game's own run save, as its map makes after a menu closed with its relics changed: run sync's capture sends
            // it to friends, and the map's view follows the new checkpoint (SceneFollowing).
            state.SaveRunData();
        }
        catch (Exception ex)
        {
            ModLog.Error("Run sync: saving the host's run after its lineup changed failed (friends get the lineup with the game's next run save)", ex);
        }
    }

    /// <summary>
    /// True while the game's campaign tutorial runs (one of its steps waits for the lineup's close and goes on from there), or
    /// when that can't be told: no save of the mod's comes between its steps.
    /// </summary>
    private static bool TutorialRuns()
    {
        try
        {
            return Menus.EventViews.TutorialActive();
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Run sync: whether the campaign tutorial runs couldn't be told ({ex.GetType().Name}).");
            return true;
        }
    }

    /// <summary>The run team's print (its roster by id, in order), or null when there's no run team or it can't be read.</summary>
    private static long? TeamPrint(CampaignState? state)
    {
        try
        {
            TeamData? team = state?.runData?.teamData;
            return team == null ? null : LineupOrder.Print(Menus.LineupViews.RosterIds(team));
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Run sync: the run's team couldn't be read ({ex.GetType().Name}).");
            return null;
        }
    }

    // ---- Friend: loading the host's run ----

    private void Advance()
    {
        LoadJob? job = _job;
        if (job == null)
        {
            return;
        }

        try
        {
            if (job.Awaiter == null)
            {
                Begin(job);
            }

            if (_job == job && job.Awaiter != null)
            {
                Poll(job);
            }
        }
        catch (Exception ex)
        {
            if (job.Awaiter != null)
            {
                AppliedCheckpoint = 0; // a load that throws resets the game's run
                HeldPreviewHash = 0;
            }

            Failed("loading the host's run", ex);
            if (_job == job)
            {
                Finish(job, false, $"an error ({ex.GetType().Name}; see the log)");
            }
        }
    }

    private void Begin(LoadJob job)
    {
        CampaignState? state = CampaignGame.State();
        string? busy = state == null ? "the game's campaign isn't ready"
            : CampaignGame.TitleRefreshing() ? "the title screen is loading a run"
            : OrphanRunning() ? "an earlier load is still running"
            : null;
        if (busy != null)
        {
            if (job.WaitingSince < 0)
            {
                job.WaitingSince = Now;
                ModLog.Info($"Run sync: {JobName(job)} waits: {busy}.");
            }
            else if (Now - job.WaitingSince > BusyGameWaitSeconds)
            {
                Finish(job, false, busy);
            }

            return;
        }

        byte[]? bytes = job.Checkpoint.Get(RunCheckpointPart.Run);
        if (bytes == null || bytes.Length == 0)
        {
            Finish(job, false, "the checkpoint holds no run file");
            return;
        }

        // Easy Save creates whatever type a file names: only the types a run file holds get to the game's loader.
        if (!RunFileTypes.TryCheck(bytes, RunFileTypes.RunFile, out string unsafeRun))
        {
            string why = RunFileTypes.ProblemForLog(unsafeRun); // a type the host's file names: by its shape, never as written
            ModLog.Warn($"Run sync: refused {JobName(job)}: {why}. It never reaches this game's loader (a run file the game wrote only names the run's own types).");
            Finish(job, false, $"the host's run file was refused here ({why})");
            return;
        }

        try
        {
            CampaignFiles.WriteAtomically(CampaignFiles.HostRunPath, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Finish(job, false, $"couldn't write it to UserData/T2TOnline/campaign ({CampaignFiles.ErrorText(ex)})");
            return;
        }

        if (!HoldingHostRun && !_squadRemembered)
        {
            ProfileData? profile = ProfileData.Instance;
            _ownLastSquad = profile != null ? profile.lastSelectedSquad : null;
            _squadRemembered = profile != null;
            ModLog.Info($"Run sync: your last squad ({Show(_ownLastSquad)}) is noted; it's put back when the session ends (loading a run changes it).");
        }

        if (!CampaignGame.PointRunFilesAt(state!, CampaignFiles.HostRunPath, out string problem))
        {
            Finish(job, false, problem);
            return;
        }

        _safety.HostRunPointed();
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Run sync: loading {JobName(job)} ({bytes.Length / 1024.0:0.0} KB, {(RunFileSummary.LooksLikeJson(bytes) ? "JSON" : "not JSON")}) with the game's own loader..."));
        Load(job, state!);
    }

    private static void Load(LoadJob job, CampaignState state)
    {
        job.Awaiter = state.LoadRunData().GetAwaiter();
        job.Since = Now;
    }

    private void Poll(LoadJob job)
    {
        UniTask<bool>.Awaiter awaiter = job.Awaiter!;
        if (!awaiter.IsCompleted)
        {
            job.Yielded = true;
            if (Now - job.Since > LoadTimeoutSeconds)
            {
                Orphan(awaiter);
                AppliedCheckpoint = 0;
                HeldPreviewHash = 0;
                Finish(job, false, $"the game's loader didn't finish within {LoadTimeoutSeconds:0} s");
            }

            return;
        }

        bool loaded;
        try
        {
            loaded = awaiter.GetResult();
            if (job.Yielded)
            {
                // A load that finished on a later frame was awaited here, which uses up the game's memory of it: the game's
                // own next load (its title screen's) would ask it whether it's still running, and that throws.
                ForgetGameLoad();
            }
        }
        catch (Exception ex) when (!job.Retried && ex.Message.Contains("Token version is not matched", StringComparison.Ordinal) && CampaignGame.State() is { } state)
        {
            // The game keeps its last load (CampaignState._loadDataTask) and asks it whether it's still running; once that
            // load was awaited, asking throws. Forget it, so the game starts a fresh one.
            job.Retried = true;
            CampaignGame.ForgetLoad(state);
            ModLog.Info("Run sync: the game's memory of its last run load was used up; loading again.");
            Load(job, state);
            return;
        }

        if (!loaded)
        {
            AppliedCheckpoint = 0; // the game reset its run
            HeldPreviewHash = 0;
            Finish(job, false, "the game couldn't load it (its own warning is in the log above)");
            return;
        }

        AppliedCheckpoint = job.Preview ? 0 : job.Checkpoint.Sequence;
        HeldPreviewHash = job.Preview ? job.Checkpoint.Hash : 0;
        LogLoaded(job, Now - job.Since);
        Loaded(AppliedCheckpoint);
        Finish(job, true, string.Empty);
    }

    private void Finish(LoadJob job, bool ok, string reason)
    {
        if (_job == job)
        {
            _job = null;
        }

        LoadJob? next = _next;
        _next = null;
        if (!ok)
        {
            ModLog.Warn($"Run sync: couldn't load {JobName(job)}: {reason}.");
            if (next != null)
            {
                // A newer one is waiting: everyone waiting gets its answer instead.
                next.Waiting.InsertRange(0, job.Waiting);
                _job = next;
                Advance();
                return;
            }
        }

        _job = next; // before the answers, so an Apply from inside one queues behind it
        Answer(job, ok ? new FollowResult(true, job.Checkpoint.Sequence, string.Empty) : new FollowResult(false, AppliedCheckpoint, reason, Applied: false));
        if (next != null && _job == next)
        {
            Advance();
        }
    }

    private void LogLoaded(LoadJob job, double seconds)
    {
        RunCheckpoint checkpoint = job.Checkpoint;
        SayTeamChange(checkpoint);
        try
        {
            RunData? run = CampaignGame.State()?.runData;
            int team = run?.teamData?.forwards?.Count ?? 0;
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Run sync: {JobName(job)} is loaded here in {seconds:0.00} s: run {ShowHosts(run?.runID)}, squad {ShowHosts(run?.squadId)}, ") +
                $"act {run?._currentAct ?? 0}, floor {run?.currentFloor ?? 0}, a team of {team} skaters (hash {checkpoint.Hash:x16})" +
                (job.Preview ? "; it holds no numbered checkpoint, and the host's first save replaces it." : "."));
            Menus.UnlockViews.RunLoaded(run?.runID); // a run the host went back to: its new unlocks are its own again
        }
        catch (Exception ex)
        {
            ModLog.Info($"Run sync: {JobName(job)} is loaded here (its details couldn't be read: {ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>"the host's run 3", or "the host's new run (its run-start preview)", for the log.</summary>
    private static string JobName(LoadJob job) =>
        job.Preview ? "the host's new run (its run-start preview)" : $"the host's run {job.Checkpoint.Sequence}";

    /// <summary>Tells <see cref="HostRunLoaded"/>'s handlers, each guarded.</summary>
    private static void Loaded(uint checkpoint)
    {
        Action<uint>? handlers = HostRunLoaded;
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<uint>)handler)(checkpoint);
            }
            catch (Exception ex)
            {
                Failed("telling others a run of the host's loaded", ex);
            }
        }
    }

    private bool OrphanRunning()
    {
        WatchOrphan();
        return _orphan != null;
    }

    /// <summary>Clears the game's memory of its last run load (<see cref="CampaignGame.ForgetLoad"/>). Never throws: whatever the load did stands.</summary>
    private static void ForgetGameLoad()
    {
        try
        {
            if (CampaignGame.State() is { } state)
            {
                CampaignGame.ForgetLoad(state);
            }
        }
        catch (Exception ex)
        {
            Failed("clearing the game's memory of its last run load", ex);
        }
    }

    /// <summary>
    /// A load of the host's run is given up on (timed out, or the session ended) while it still runs. The game's memory of
    /// it is cleared, so the game's own next load starts afresh instead of waiting on this one (two waiting on one load is
    /// an error in the game's task library); until it finishes, run saves are skipped (<see cref="RunSaveSafety.RunSaveBlock"/>),
    /// since it ends by putting its run in the game's memory.
    /// </summary>
    private void Orphan(UniTask<bool>.Awaiter awaiter)
    {
        _orphan = awaiter;
        _safety.LoadOrphaned();
        _orphanSince = Now;
        _orphanSlowLogged = false;
        ForgetGameLoad();
    }

    /// <summary>
    /// A load given up on (<see cref="Orphan"/>): watched until it finishes, however long that takes (it may still put the
    /// host's run in the game's memory). Once it has, a friend whose session is over gets their own run back: on the title
    /// screen at once, elsewhere once the title screen is back.
    /// </summary>
    private void WatchOrphan()
    {
        UniTask<bool>.Awaiter? orphan = _orphan;
        if (orphan == null)
        {
            return;
        }

        bool finished;
        try
        {
            finished = orphan.IsCompleted;
        }
        catch (Exception)
        {
            finished = true;
        }

        if (!finished)
        {
            if (!_orphanSlowLogged && Now - _orphanSince >= OrphanWatchSeconds)
            {
                _orphanSlowLogged = true;
                ModLog.Warn($"Run sync: a load of the host's run given up on {OrphanWatchSeconds:0} s ago is still running; this game's run saves stay held until it finishes.");
            }

            return;
        }

        _orphan = null;
        CampaignState? state = CampaignGame.State();
        TitleScreen? title = _safety.InFriendSession || state == null ? null : CampaignGame.Title();
        bool quietTitle = title != null && !title.m_IsStartingRun && !CampaignGame.LeavingTitle();
        switch (_safety.OrphanFinished(state != null, quietTitle, Now))
        {
            case OrphanLanded.ResetAtTitle:
                state!.ResetAllData();
                ModLog.Info("Run sync: a load of the host's run finished after the session ended; your campaign state was reset again and the title screen reloads your run.");
                break;
            case OrphanLanded.HeldUntilTitle:
                ModLog.Warn("Run sync: a load of the host's run finished after the session ended, away from the title screen; this game's run saves are held until its title screen is back.");
                break;
        }
    }

    // ---- Friend: back to the friend's own run ----

    private void Restore(string why, bool refreshTitle)
    {
        LoadJob? job = _job;
        LoadJob? next = _next;
        _job = null;
        _next = null;
        if (job?.Awaiter != null)
        {
            bool running;
            try
            {
                running = !job.Awaiter.IsCompleted;
            }
            catch (Exception)
            {
                running = false; // already finished and used up
            }

            if (running)
            {
                Orphan(job.Awaiter);
            }
        }

        var stopped = new FollowResult(false, 0, $"the host's run was put away ({why})", Applied: false);
        Answer(job, stopped);
        Answer(next, stopped);
        AppliedCheckpoint = 0;
        HeldPreviewHash = 0;

        // Until the game has this player's run in memory again, a run save would write the host's (or an empty) run over
        // theirs: held until the title screen has reloaded it, and, unless the game is on its title screen with no scene
        // load under way, until the title screen has loaded first (a world map landing after this would start a new run
        // on the reset state and save it).
        bool held = _safety.HoldingHostRun || _safety.ActiveSlot == RunSaveSafety.HostRunSlot;
        if (!_safety.PutHostRunAway(atSettledTitle: held && CampaignGame.AtSettledTitle(), Now))
        {
            // Nothing was loaded, so the profile's last squad is as it was: forget the note, so a later session takes its own.
            _squadRemembered = false;
            _ownLastSquad = null;
            return;
        }

        var done = new List<string>();
        try
        {
            ProfileData? profile = ProfileData.Instance;
            if (_squadRemembered && profile != null)
            {
                profile.lastSelectedSquad = _ownLastSquad;
                done.Add($"your last squad ({Show(_ownLastSquad)}) is back");
            }

            CampaignState? state = CampaignGame.State();
            if (state == null)
            {
                ModLog.Warn("Run sync: the game's campaign isn't there, so its run file can't be pointed back yet; it's done at the title screen.");
            }
            else if (CampaignGame.PointRunFilesAt(state, SoloRunFile, out string problem))
            {
                // A title screen still loading the host's run would put it in memory after this: a run started before run
                // sync's own refresh of the title stays held (RunSaveSafety.LoadBeforeSwitch).
                _safety.PointedBackToSolo(titleLoading: CampaignGame.TitleRefreshing());
                done.Add($"the game's run file is {SoloRunFile} again");
                state.ResetAllData();
                done.Add("the campaign state is reset");
            }
            else
            {
                ModLog.Warn($"Run sync: couldn't point the game back at your run file ({problem}); it's tried again at the title screen.");
            }
        }
        catch (Exception ex)
        {
            Failed("putting your own run back", ex);
        }

        try
        {
            if (CampaignFiles.DeleteWithCopies(CampaignFiles.HostRunPath) > 0)
            {
                done.Add("HostRun.es3 is deleted");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Run sync: couldn't delete HostRun.es3 ({CampaignFiles.ErrorText(ex)}).");
        }

        _squadRemembered = false;
        _ownLastSquad = null;
        _ownRunWanted = true; // said once the title has it in memory (Changed)
        ModLog.Info($"Run sync: your own run is back ({why}): {(done.Count > 0 ? string.Join(", ", done) : "nothing could be put back yet")}; " +
            $"this game's run saves are held until {(_safety.StaleUntilTitle ? "its title screen loads and " : string.Empty)}the title screen has reloaded your run.");
        if (refreshTitle)
        {
            TrySwitchAtTitle("your own run is back"); // the put-away asked for the title's refresh
        }
    }

    // ---- Friend: save safety; both: Steam Cloud never sees the mod's files ----

    private static bool CloudLoadPrefix(SteamService __instance, string file, ref bool __result)
    {
        try
        {
            RunSync? sync = Instance;
            if (CampaignFiles.IsAbsolute(file))
            {
                __result = false;
                LogCloud(sync, $"loading {Path.GetFileName(file)}");
                return false;
            }

            // Following, a cloud load would rewrite this PC's file; after a restore (until the restart) it could put back the
            // cloud's older copy if pushing the restored file failed.
            if (sync != null && (sync._safety.Following || SaveBackups.RestartNeeded))
            {
                __result = false; // the game keeps its local file
                if (++sync._refusedCloudLoads <= LoggedSkips)
                {
                    ModLog.Info($"Run sync: refused a Steam Cloud load of {Path.GetFileName(file)} {(sync._safety.Following ? "while following" : "until the game restarts after a restore")} " +
                        $"({sync._refusedCloudLoads} so far): it would rewrite this PC's file.");
                }

                return false;
            }

            // A file a restore put back, which Steam Cloud may still hold an older copy of (a push that failed, or Steam's own
            // sync): it's sent to Steam Cloud now and the load keeps the restored file (SaveBackups' Steam Cloud list).
            if (SaveBackups.CloudPending && SaveBackups.KeepRestoredFile(__instance, file))
            {
                __result = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("checking a Steam Cloud load", ex);
            if (Instance is { } sync && sync._safety.Following)
            {
                __result = false;
                return false;
            }
        }

        return true;
    }

    private static bool CloudSavePrefix(string file, ref bool __result) => CloudCall(file, "saving", ref __result);

    private static bool CloudDeletePrefix(string fileName, ref bool __result) => CloudCall(fileName, "deleting", ref __result);

    /// <summary>
    /// The game hands Steam Cloud the file name it was given: for a mod file that's an absolute path, which would land in
    /// the cloud under a broken name (SteamService.Save reads persistentDataPath combined with the name, which is the
    /// absolute path itself, and writes it to remote storage under that name). The mod's files stay on this PC.
    /// </summary>
    private static bool CloudCall(string file, string what, ref bool __result)
    {
        try
        {
            if (CampaignFiles.IsAbsolute(file))
            {
                __result = false;
                LogCloud(Instance, $"{what} {Path.GetFileName(file)}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("checking a Steam Cloud call", ex);
        }

        return true;
    }

    private static void LogCloud(RunSync? sync, string what)
    {
        if (sync == null || ++sync._skippedCloudCalls <= LoggedSkips)
        {
            ModLog.Info($"Run sync: Steam Cloud left out of {what} (a mod file: the co-op slots and the host's run stay on this PC).");
        }
    }

    private static bool RunConcedePrefix()
    {
        try
        {
            if (Instance is { } sync && sync._safety.RunSaveBlock is { } block)
            {
                if (++sync._skippedConcedes <= LoggedSkips)
                {
                    ModLog.Info($"Run sync: skipped this PC's run-end handler (CampaignLoader.OnRunConcede): it deletes the run file, and {block}.");
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("checking the run's end", ex);
            return !MayHoldAnotherRun();
        }

        return true;
    }

    // ---- The title's New Run and Abandon Run before the title is reloaded for the run file as it now is ----

    private static bool NewRunClickPrefix(TitleScreen __instance, AbstractNavigable nav) => TitleRunPress(__instance, nav, "New Run");

    private static bool AbandonRunClickPrefix(TitleScreen __instance, AbstractNavigable nav) => TitleRunPress(__instance, nav, "Abandon Run");

    /// <summary>
    /// A press on the title's New Run or Abandon Run (<c>TitleScreen.OnNewRunClicked</c>, <c>OnAbandonRunClicked</c>, the
    /// buttons' own handlers). The title acts on what it last loaded: New Run asks before replacing a run only when its
    /// <c>m_HasOngoingCampaign</c> says there is one, and Abandon Run ends the run in memory and deletes the run file's.
    /// After the run file switched, both are the file's from before until run sync has reloaded the title, which waits
    /// for nothing to be open on it (<see cref="TrySwitchAtTitle"/>, a quarter of a second at most after that) and then
    /// for the title's load. A press in that moment is refused as the title refuses one during its own transitions (the
    /// button's click result "ignored", the handler not run), so it can't replace or delete a run the title never showed.
    /// <see cref="TitleRunButtonsHold"/> decides: never while following, and for <see cref="TitleRunButtonsHold.CapSeconds"/>
    /// at most. A host's presses are held the same way: the lobby's Start calls the game's start itself, never these
    /// handlers (and <see cref="ReadyToStart"/> has it wait for the reload), but a host with no lobby screen (the
    /// developer's NativeLobby off) starts runs from these buttons, right after the title began to reload for the co-op slot. The first press held in each wait is logged. Never throws.
    /// </summary>
    private static bool TitleRunPress(TitleScreen? title, AbstractNavigable? nav, string button)
    {
        try
        {
            if (Instance is not { } sync)
            {
                return true;
            }

            RunSaveSafety safety = sync._safety;
            double now = Now;

            // The title is asked only while a reload of run sync's can still be running: one field read, on a press.
            bool reloadRecent = sync._titleReloadAt >= 0 && now - sync._titleReloadAt < TitleRunButtonsHold.CapSeconds;
            var reload = new TitleReload(safety.Following, safety.RefreshWanted, safety.StaleSince, sync._titleReloadAt,
                reloadRecent && title != null && title.m_IsRefreshingCampaignState);
            TitlePress press = sync._titleButtons.Press(reload, now);
            string why = safety.StaleRun ?? "run sync's reload of the title is still loading";
            switch (press)
            {
                case TitlePress.HeldFirst:
                    ModLog.Info($"Run sync: {button} on the title does nothing for a moment: the title's campaign buttons aren't reloaded yet for the run file as it is now " +
                        $"({why}). The press is ignored, as one during the title's own transitions is; it works once the title has reloaded.");
                    break;
                case TitlePress.Held:
                    break;
                case TitlePress.PassedLateFirst:
                    ModLog.Warn($"Run sync: the title's campaign buttons still aren't reloaded for the run file as it is now {TitleRunButtonsHold.CapSeconds:0} s after a press was held " +
                        $"({why}); {button} goes to the game as it is, so the title may ask, or not ask, as for the run file from before.");
                    return true;
                default:
                    return true;
            }

            nav?.SetClickResult(AbstractNavigable.EClickResult.Ignored);
            return false;
        }
        catch (Exception ex)
        {
            Failed("checking a press on the title's campaign buttons", ex);
            return true;
        }
    }

    // ---- Helpers ----

    private static void Answer(LoadJob? job, FollowResult result)
    {
        if (job == null)
        {
            return;
        }

        foreach (Action<FollowResult> done in job.Waiting)
        {
            Answer(done, result);
        }
    }

    private static void Answer(Action<FollowResult> done, FollowResult result)
    {
        try
        {
            done(result);
        }
        catch (Exception ex)
        {
            Failed("answering a caller", ex);
        }
    }

    private static string Show(string? text) => string.IsNullOrEmpty(text) ? "none" : SafeText.Clean(text);

    /// <summary>An id from the host's run for a log line: as it is when it has an identifier's shape, else its length (<see cref="HostText.KeyForLog(string?)"/>).</summary>
    private static string ShowHosts(string? id) => string.IsNullOrEmpty(id) ? "none" : HostText.KeyForLog(id);

    /// <summary>
    /// True when a join must wait, with why (to show): this player's own campaign run is in play away from the title screen.
    /// A session would replace that run in memory, and what it did since its last save point would
    /// be gone; the way back to the title screen saves it (<see cref="RunSaveSafety.JoinRefusal"/>). For F7 and the panel's
    /// Join; cheap, never throws.
    /// </summary>
    public static bool BlocksJoining(out string why)
    {
        string? refusal = Instance is { } sync ? RunSaveSafety.JoinRefusal(sync.OwnRunWhere()) : null;
        why = refusal ?? string.Empty;
        return refusal != null;
    }

    /// <summary>
    /// Where this player's own campaign run is in play, if anywhere: its world map, or a campaign match of its own (not a
    /// host's match loaded here, not an exhibition), with the run in memory the run file's (not following, not holding the
    /// host's run, nothing held as stale) and one with an id. Never throws.
    /// </summary>
    private OwnRunPlace OwnRunWhere()
    {
        try
        {
            if (_safety.MayHoldAnotherRun || CampaignGame.TitleLoaded() || string.IsNullOrEmpty(CampaignGame.State()?.runData?.runID))
            {
                return OwnRunPlace.None;
            }

            if (Scenes.WorldMap.SceneName == Scenes.WorldMap.MapScene)
            {
                return OwnRunPlace.Map;
            }

            return T2TOnline.Plugin.Match.MatchSetup.CurrentMatch(out _).Serial != 0 && !T2TOnline.Plugin.Match.MatchSetup.HostMatchHere &&
                T2TOnline.Plugin.Match.MatchSetup.LoadedGameType == T2TOnline.Core.Stream.CampaignMatchValues.CampaignGameType ? OwnRunPlace.Match : OwnRunPlace.None;
        }
        catch (Exception ex)
        {
            Failed("checking for this player's own run", ex);
            return OwnRunPlace.None;
        }
    }

    /// <summary>The first <see cref="LoggedErrors"/> errors from each place are logged; the rest are counted and summed up here, at a session's end.</summary>
    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Run sync: failed while {what}", ex);
        }
    }

    private static void LogSuppressedErrors()
    {
        string line = ErrorLogLimit.Describe(Errors.TakeSuppressed());
        if (line.Length > 0)
        {
            ModLog.Warn($"Run sync: {line}.");
        }
    }

    /// <summary>One checkpoint being loaded (or waiting to be), and everyone waiting for it.</summary>
    private sealed class LoadJob
    {
        public LoadJob(RunCheckpoint checkpoint, Action<FollowResult> done)
        {
            Checkpoint = checkpoint;
            Waiting.Add(done);
        }

        public RunCheckpoint Checkpoint { get; set; }

        public List<Action<FollowResult>> Waiting { get; } = new();

        /// <summary>The game's load, once started.</summary>
        public UniTask<bool>.Awaiter? Awaiter { get; set; }

        /// <summary>When the load started (seconds on the module's clock).</summary>
        public double Since { get; set; }

        /// <summary>When it started waiting for the game to be free, or -1.</summary>
        public double WaitingSince { get; set; } = -1;

        /// <summary>The load was started again once, after the game's memory of its last load was used up.</summary>
        public bool Retried { get; set; }

        /// <summary>The load was still running on a frame after it started (it didn't finish inside the call).</summary>
        public bool Yielded { get; set; }

        /// <summary>A run-start preview (<see cref="ApplyPreview"/>; its checkpoint's number is 0), not a numbered checkpoint.</summary>
        public bool Preview { get; set; }
    }
}

/// <summary>
/// The game's campaign, as run sync, the co-op slots and backups need it: the campaign state, its run file names, the
/// title screen. Call on the game thread.
/// </summary>
internal static class CampaignGame
{
    /// <summary>The title screen's scene.</summary>
    public const string TitleScene = "start_screen";

    /// <summary>The game's campaign state, or null before the game made it.</summary>
    public static CampaignState? State()
    {
        CampaignState? state = CampaignUtil.CampaignState;
        if (state != null)
        {
            return state;
        }

        ProfileData? profile = ProfileData.Instance;
        return profile != null && profile.campaignState != null ? profile.campaignState : null;
    }

    /// <summary>The file the game's run repository reads and writes, or null when the repository isn't the one this version knows.</summary>
    public static string? RunFileName(CampaignState state) =>
        state._runDataRepository?.TryCast<RunDataRepository>()?._runDataRepositoryV2?._fileName;

    /// <summary>
    /// Points both run repositories (run data and run stats, one file) at <paramref name="file"/>: the game's own name, or an
    /// absolute path, which Easy Save keeps as it is. False, with why, when they aren't the ones this version knows.
    /// </summary>
    public static bool PointRunFilesAt(CampaignState state, string file, out string problem)
    {
        RunDataRepositoryV2? run = state._runDataRepository?.TryCast<RunDataRepository>()?._runDataRepositoryV2;
        RunStatsStateRepository? stats = state._runStatsStateRepository?.TryCast<RunStatsStateRepository>();
        if (run == null || stats == null)
        {
            problem = "the game's run storage isn't the kind this version knows";
            return false;
        }

        run._fileName = file;
        stats._fileName = file;
        if (run._fileName != file || stats._fileName != file)
        {
            problem = "the game's run file name didn't change";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>Where the game keeps <paramref name="fileName"/>: as it is when absolute, else in its save folder.</summary>
    public static string FullPath(string? fileName)
    {
        string name = string.IsNullOrEmpty(fileName) ? RunSync.SoloRunFile : fileName;
        return Path.IsPathRooted(name) ? name : Path.Combine(UnityEngine.Application.persistentDataPath, name);
    }

    /// <summary>True while the title screen's scene is loaded.</summary>
    public static bool TitleLoaded() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(TitleScene).isLoaded;

    /// <summary>
    /// True while the title screen's scene is loaded and its loading screen has started loading another scene (the world map
    /// for a host's view, say): the title is on its way out. Only the title scene's own loader counts, since it goes with
    /// the scene and its flag can't outlive the load.
    /// </summary>
    public static bool LeavingTitle()
    {
        if (!TitleLoaded())
        {
            return false;
        }

        foreach (LoadingScreenManager loader in UnityEngine.Object.FindObjectsOfType<LoadingScreenManager>())
        {
            if (loader != null && loader.isStartingLoad && loader.gameObject.scene.name == TitleScene)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True while the title screen is loaded and settled: not on its way out to another scene (<see cref="LeavingTitle"/>).</summary>
    public static bool AtSettledTitle() => TitleLoaded() && !LeavingTitle();

    /// <summary>The title screen, if it's up (looked for only while its scene is loaded).</summary>
    public static TitleScreen? Title()
    {
        if (!TitleLoaded())
        {
            return null;
        }

        TitleScreen? title = UnityEngine.Object.FindObjectOfType<TitleScreen>();
        return title != null && title.isActiveAndEnabled ? title : null;
    }

    /// <summary>The title screen when nothing is happening on it (no run starting or loading, no menu open over it, not leaving), else null.</summary>
    public static TitleScreen? IdleTitle()
    {
        TitleScreen? title = Title();
        return title != null && IsIdle(title) ? title : null;
    }

    /// <summary>True when nothing is happening on <paramref name="title"/>: no run starting or loading, no menu but the lobby's shell open over it, not leaving.</summary>
    public static bool IsIdle(TitleScreen title)
    {
        if (title.m_IsStartingRun || title.m_IsExiting || title.m_IsClickingButton || title.m_IsRefreshingCampaignState)
        {
            return false;
        }

        // The native lobby's shell is on the list as any title menu is, but the title is idle under it: the co-op slot can change in the lobby.
        Il2CppSystem.Collections.Generic.List<string>? menus = title.m_MenusOverSelf;
        for (int i = 0; menus != null && i < menus.Count; i++)
        {
            if (!NativeLobby.LobbyShell.IsShellId(menus[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True while the title screen loads the run for its campaign buttons (a load must not start meanwhile).</summary>
    public static bool TitleRefreshing() => Title() is { m_IsRefreshingCampaignState: true };

    /// <summary>
    /// Clears the game's memory of its last run load (<c>CampaignState._loadDataTask</c>), so its next <c>LoadRunData</c>
    /// starts a fresh load instead of asking the old one whether it's still running.
    /// </summary>
    /// <remarks>
    /// The interop's empty <c>Nullable</c> is a zeroed box of the element type, not of the <c>Nullable</c> (an
    /// Il2CppInterop limit with structs), so this setter copies the field's size from a smaller box: the flag byte it writes is
    /// the box's first, zero, which is all "no value" needs; the value bytes after it are never read while the flag is
    /// clear. So the interop's setter is used as it is, rather than a write of the field in place.
    /// </remarks>
    public static void ForgetLoad(CampaignState state) => state._loadDataTask = new Il2CppSystem.Nullable<UniTask<bool>>();

    /// <summary>A squad's name for the panel, from the game's squads, or its id.</summary>
    public static string SquadName(string id)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<RunSquadScriptableObject>? squads = State()?.squads;
            for (int i = 0; squads != null && i < squads.Count; i++)
            {
                RunSquadScriptableObject squad = squads[i];
                if (squad != null && squad.id == id)
                {
                    string name = squad.LocalizedSquadName;
                    return string.IsNullOrWhiteSpace(name) ? squad.squadName ?? id : name;
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Co-op slots: squad name for {HostText.KeyForLog(id)} not found ({ModLog.SafeErrorText(ex)})."); // a friend's listed save names its squad too
        }

        return SafeText.Clean(id);
    }
}

/// <summary>Registers the run sync module and its patches.</summary>
internal static partial class CampaignModules
{
    static partial void AddRunSync(List<ICampaignModule> modules, CampaignContext context) => modules.Add(new RunSync(context));

    static partial void InstallRunSync(HarmonyLib.Harmony harmony, List<string> failed) => RunSync.Install(harmony, failed);
}
#endif
