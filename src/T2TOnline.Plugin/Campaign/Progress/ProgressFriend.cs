#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppCysharp.Threading.Tasks;
using Il2CppData;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue;
using Il2CppRogue.BenchQuests;
using Il2CppRogue.BenchSnapshots;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppState;
using Il2CppState.GolferOutro;
using Il2CppState.RunHistory;
using Il2CppStats.PersistentStats;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppUnlocks;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// A friend's side of shared progress (D42): the host's batches are applied once each, in order
/// (<see cref="ProgressInbox"/>, remembered in a ledger file so a resent batch is never applied twice), to this PC's own
/// stats and profile through the game's own calls, at its own thresholds:
/// <list type="bullet">
/// <item>Stat operations on this PC's own stat objects (the same method with the same value; a record the host already
/// holds still counts here). This PC's "this run" and "this act" values belong to its own run and are put back after
/// each operation. What the game sets outright is applied by its meaning: the win streak
/// set back to zero, the largest streak raised to this profile's own streak, a record's holder named only where this
/// profile's own record rose; the "W-L" records and percentages are worked out again from this profile's own counters
/// (<see cref="DerivedStats"/>); per-entity stats are combined into this profile's own (<see cref="ProgressEntityStats"/>).</item>
/// <item>The host's unlock checks, run on this PC's own unlock system (not the stored ones: this profile's own
/// thresholds decide), with every result marked unlocked at once; the unlock toasts and achievements are this game's
/// own. The three live-match unlocks answer what the host's check earned.</item>
/// <item>Squad wins and losses (with the host's run time and the run's best scorer, so the squad's "most goals in a run"
/// is the shared run's), the run history entry (only for a run this PC held), the run's end for the benchwarmers (the
/// game's own <c>BenchSnapshotService.HandleRunEnding</c> on this PC's copy of the run: its own roll, its own toasts),
/// and the bench quest steps outside matches, through the game's own methods. A run's squad record, bench snapshots and
/// history entry count once per run (the ledger), whatever brings them again. The golfer's comic strips aren't part of
/// this: each friend's game plays the golfer outro itself, with its own strips and cups (<c>Match/GolferOutro</c>, D42), and the
/// marks it makes reach disk with this PC's next allowed profile save.</item>
/// <item>What the host's game marked on its profile while the shared run played, marked here too: the Nurse procedures its
/// Nurse offered (<c>NurseProcedures.MarkOffered</c>, so this player's own Nurse doesn't offer them again) and the event
/// NPC lines everyone heard (<c>SeenDialogues.MarkSeen</c>, D62).</item>
/// <item>Then the batch is marked applied (the ledger first: a game that stops in between misses it rather than counting
/// it twice). Every batch that can apply in a frame does, and this PC's own profile and stats are then saved once,
/// through the save guard (the run file never); each batch's log line says whether the files were really written.</item>
/// </list>
/// The order of that work is Core's (<see cref="ProgressApplier"/>, tested without the game); this class is its
/// game side (<see cref="IProgressTarget"/>). When a batch applies, and where its run-bound parts go, is decided apart
/// from the game too (<see cref="ProgressApplyPolicy"/>): only at a safe point (never while the host's match plays, loads
/// or is held on its last frame here, nor while the host's run loads, nor before this game's profile and stats have
/// loaded). The run's last match is the one exception: once the host's run is over and its run-end screens are up here
/// (<see cref="HostRunEnded"/>), that match plays no more, and what it credited applies while its scene stays loaded, so
/// this player's new-unlocks screen shows it. Its run-bound parts (unlock checks, squad record, quest steps, comics, the
/// run's end for the benchwarmers) apply on the host's run held here, or else on this PC's kept copy of that
/// run (<see cref="ProgressKeptRuns"/>: a run's end applies whatever came first, the next run's checkpoint, the
/// session's end, a rejoin), put in place just for the batch with that run's own bench quest counts; a batch for the run
/// the host is on now that this PC doesn't hold yet waits up to <see cref="ProgressApplyPolicy.RunWaitMicros"/> for it;
/// otherwise they're skipped and logged. While they apply, the game's own checks are told it's in the campaign (the title
/// screen, where a run's end applies, isn't). One log line per batch says what changed here. A batch that fails as a
/// whole (an error outside its entries) still counts as applied, logged once, so it can't hold up the rest; the
/// difficulty, the run held and this PC's own quest counts are put back whatever happens. With ShareProgress off,
/// batches wait (they apply if it's turned back on during the session, and come again on a rejoin of the same hosting
/// session), never marked applied.
/// <para>
/// Missing batches (a gap in the numbers, refused while too many waited, the newest the host's status names, given up on
/// earlier) are asked for again (<c>MessageType.ProgressResend</c>), and the host's status says whether it
/// shares progress at all (said once in the log and the status box when it's off, and when it's back on).
/// </para>
/// <para>
/// When a session ends: every batch that arrived and can apply does so first (<see cref="Flush"/>: the host's match and
/// run loads no longer hold it back; the relay stops this module before run sync puts this PC's own run back).
/// Batches that still couldn't apply (this game's saves not loaded, or restored and waiting for a restart), or never
/// arrived, come again if this PC rejoins the same hosting session (the ledger skips what it applied, and remembers what
/// it gave up on so that still applies, on the kept copy of its run if this game hasn't restarted), and are lost if that
/// hosting session ended (the host stopped hosting or closed its game, which sends everything it has first). Each case is
/// logged.
/// </para>
/// </summary>
internal sealed class ProgressFriend : IProgressTarget
{
    private const long HistoryTimeoutMicros = 30_000_000;
    private const long WaitShownAfterMicros = 10_000_000;
    private const long RunCheckMicros = 250_000;
    private const int ShownStats = 16;
    private const string StatsFile = "statsV2.es3";
    private const long CameAgainQuietMicros = 2_000_000;

    // The ledger's temporary file before 0.11.0 (now CampaignFiles.PartSuffix, the one the start-up sweep knows).
    private const string OldPartSuffix = ".tmp";
    private static readonly string[] QuestNames = { "Boring Dude", "Brewster", "Kovalski", "Mark Bench", "Cassoulet", "Kirby", "Stumple", "Sally" };
    private static readonly IntPtr Scratch = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);

    // The facts Core's ProgressApplyPolicy.BlockOf asks for only when they matter (made once: asked every frame a batch waits).
    private static readonly Func<bool> RunLoading = () => RunSync.Instance?.Busy == true;
    private static readonly Func<bool> SavesNotLoaded = () => ProfileData.Instance is not { IsLoaded: true } || Stats()?.StatRepository is not { IsLoaded: true };
    private static readonly Func<bool> RestartPending = () => SaveBackups.RestartNeeded;
    private static PendingHistory? _savingAfterSession;
    private static bool _watchingSaves;

    private readonly CampaignRelay _relay;
    private readonly MatchRelay _matches;
    private readonly string _ledgerPath;
    private readonly ProgressLedger _ledger;
    private readonly ProgressInbox _inbox;
    private readonly ProgressApplier _applier;
    private readonly RunCopyRequests _copies;
    private readonly RunCopyLoader _loader = new();
    private readonly HashSet<string> _liveEarned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private readonly ProgressCredit _credit;
    private readonly EntityCredit _entityCredit;
    private readonly int _creditRefusedBefore;
    private readonly int _overBudgetBefore;
    private readonly int _entityRefusedBefore;

    // The ledger's key for the host followed (D135: its daily limits hold across its hosting sessions); names nobody.
    private readonly string _hostKey;

    // The play this PC sees with the host (D135), noted in the ledger every few seconds and right before a batch applies:
    // what the host may credit here is bounded by it (StatBounds).
    private const long PlayNoteMicros = 10_000_000;
    private const long MaxPlayStepMicros = 1_000_000;
    private const int MaxRunsNoted = 64;
    private readonly HashSet<string> _runsNoted = new(StringComparer.Ordinal);
    private readonly int _entityOverBefore;
    private long _playClock = -1;
    private long _playMatchMicros;
    private long _playRunMicros;
    private int _playMatches;
    private int _playRuns;
    private readonly MatchPlayCounter _matchPlay = new();
    private uint _checkpointSeen;
    private long _nextPlayNote;
    private readonly ProgressRecordWatch _records = new();
    private readonly ProgressKeptRuns _kept;
    private readonly ProgressRunEnds _runEnds = new();
    private readonly QuietBurst _cameAgain = new(CameAgainQuietMicros);
    private readonly NoticeSink _notify;
    private readonly NoticeAboutSink _notifyAbout;
    private readonly string _hostTag;
    private string? _ownSquad;
    private bool _haveOwnSquad;
    private int[]? _ownQuest;
    private string _sharedRun = string.Empty;
    private string _knownHostRun = string.Empty;
    private bool[] _questsDone = Array.Empty<bool>();
    private PendingHistory? _history;
    private AchievementManager? _achievements;
    private long _nextRunCheck;
    private int _overflows;
    private int _derived;
    private long _notEntitledLogged;
    private bool? _hostSharing;
    private int _joinsSeen;
    private bool _linkUp = true;
    private bool _hostRunOver;
    private int _granted;
    private UnlockAchievementBridge? _bridge;
    private readonly ComicMarkWatch _comic = new();

    // While a batch applies (IProgressTarget): the kept run put in place, the difficulty to put back, the campaign flag
    // this class set, and the game's services looked up once for the batch's entries.
    private IDisposable? _swapped;
    private DifficultyLevel? _restoreDifficulty;
    private bool _forcedCampaign;
    private StatManager? _manager;
    private StatRepository? _stats;
    private UnlockSystem? _unlocks;
    private BenchQuestSystem? _quests;

    /// <summary>
    /// Following a host's session over <paramref name="relay"/>; <paramref name="kept"/> holds the shared runs this game held;
    /// <paramref name="credit"/> and <paramref name="entityCredit"/> what a hosting session gave here (a lobby's spans many play periods).
    /// </summary>
    public ProgressFriend(CampaignRelay relay, CampaignContext context, ProgressKeptRuns kept, ProgressCredit credit, EntityCredit entityCredit)
    {
        _relay = relay;
        _matches = context.Matches;
        _notify = context.Notify;
        _notifyAbout = context.NotifyAbout;
        _kept = kept;
        _credit = credit;
        _entityCredit = entityCredit;
        _creditRefusedBefore = credit.Refused;
        _overBudgetBefore = credit.OverBudget;
        _entityRefusedBefore = entityCredit.Refused;
        _entityOverBefore = entityCredit.OverBudget;
        _hostTag = relay.FollowedHost != 0 ? LogNames.Tag(relay.FollowedHost) : "the host"; // the applier's log lines (D127)
        _hostKey = HostKeyFile.KeyFor(relay.FollowedHost);
        _ledgerPath = ProgressLedgerFile.LivePath;
        _ledger = LoadLedger(ProgressLedgerFile.ReadPath);
        _inbox = new ProgressInbox(_ledger);
        _applier = new ProgressApplier(this, _inbox, _ledger, _hostTag);
        _copies = new RunCopyRequests(run => _ledger.TryGetRun(run, out _)); // held before: the ledger keeps its quest counts
        _joinsSeen = relay.LinkJoins;
    }

    /// <summary>
    /// The friend side applying a batch right now, or null: the live-match unlocks answer from it, whatever the module's
    /// state (a session's end applies what waits after the module let go of it).
    /// </summary>
    public static ProgressFriend? ApplyingNow { get; private set; }

    /// <summary>True while a batch is being applied (the game's own credit is let through only then).</summary>
    public bool Applying { get; private set; }

    /// <summary>
    /// True while a batch waits that needs the host's run held here (unlock checks, the squad record, quest steps):
    /// putting this PC's own run back now would make them miss it. Not with ShareProgress off: they wait for that setting,
    /// and holding this game back (the way home, the 10 s of its wait, every time) brings them no nearer.
    /// </summary>
    public bool RunBoundWaiting => ModSettings.ShareProgress && _inbox.AnyWaiting(ProgressApplyPolicy.HoldsRun);

    /// <summary>How many of the host's batches wait here and will apply if given a moment (the new-unlocks menu waits for them); none with ShareProgress off.</summary>
    public int Waiting => ModSettings.ShareProgress ? _inbox.Held : 0;

    /// <summary>While applying the host's unlock check: whether it earned live-match unlock <paramref name="id"/> on the host.</summary>
    public bool LiveEarned(string id) => _liveEarned.Contains(id);

    /// <summary>
    /// This profile's new unlocks of shared run <paramref name="run"/>, kept in the ledger for that run's new-unlocks screen
    /// (from every session of the run, and across this game's restarts).
    /// </summary>
    public IReadOnlyList<string> RunUnlocks(string? run) => _ledger.UnlocksOf(run);

    /// <summary>
    /// Shared run <paramref name="run"/>'s new-unlocks screen had its turn (the host's closed): the unlocks kept for it go, and
    /// the ledger is saved. Returns how many went.
    /// </summary>
    public int UnlockScreenShown(string? run)
    {
        int forgotten = _ledger.UnlocksShown(run);
        SaveLedger();
        return forgotten;
    }

    /// <summary>
    /// The host's run is over and its run-end screen came up here (the run-end menu at the run's end, or its new-unlocks
    /// menu). While its last match's scene stays loaded here (client mode on), that match no longer holds shared progress
    /// back (<see cref="ApplyFacts.HostRunOver"/>): what it credited applies now, before this player's new-unlocks screen,
    /// rather than once the host has left for its title. Said once per run's end; over when client mode ends.
    /// </summary>
    public void HostRunEnded()
    {
        if (_hostRunOver || !ClientMode.Active)
        {
            return;
        }

        _hostRunOver = true;
        int waiting = _inbox.Held;
        ModLog.Info($"Progress: {LogNames.HostTag()}'s run is over and its run-end screens are up while its last match stays loaded here; " +
            (!ModSettings.ShareProgress ? $"ShareProgress is off on this PC, so what that match credited still waits ({waiting} batch(es))."
            : waiting > 0 ? $"the {waiting} batch(es) that waited for that match apply now, before this player's new-unlocks screen."
            : "what that match credited applies as it comes, before this player's new-unlocks screen."));
    }

    /// <summary>
    /// Session start: this PC's own last squad is noted (a shared save mustn't change it), and the ledger as it is now is
    /// kept beside the backup the session just took (a restore of that backup brings it back).
    /// </summary>
    public void Start()
    {
        ProgressLedgerFile.Snapshot(start: true);
        NoteOwnProfile(ProfileData.Instance);
        ModLog.Info($"Progress: following {LogNames.HostTag()}. Shared progress is {(ModSettings.ShareProgress ? "on: what you earn together counts on this PC's own profile, at its own thresholds" : "off (ShareProgress): what the host sends waits here, and applies only if you turn it on during this session")}; " +
            "this PC's own game credits nothing by itself meanwhile.");
    }

    /// <summary>A progress message from the host. False when it's malformed.</summary>
    public bool Receive(ref BitReader reader)
    {
        if (!ProgressBatchMessage.TryRead(ref reader, out ProgressMessage? message))
        {
            return false;
        }

        if (message!.Batch.Run.Length > 0)
        {
            _knownHostRun = message.Batch.Run;
        }

        ulong sessionBefore = _inbox.Session;
        ProgressOffer offer = _inbox.Offer(message, _relay.FollowedSession);
        if (offer == ProgressOffer.WrongSession)
        {
            NoteWrongSession();
            return true;
        }

        NoteHostingSession(sessionBefore);
        if (offer == ProgressOffer.Refused)
        {
            Note("refused", ModSettings.ShareProgress
                ? $"Progress: more than {ProgressInbox.MaxHeld} of {LogNames.HostTag()}'s batches are waiting; later ones aren't kept now and still apply if a rejoin of this hosting session brings them."
                : $"Progress: {ProgressInbox.MaxHeld} of {LogNames.HostTag()}'s batches wait while ShareProgress is off; later ones aren't kept now and still apply if a rejoin of this hosting session brings them.");
        }
        else if (offer == ProgressOffer.Accepted && message.Batch.Sequence <= _inbox.AppliedThrough)
        {
            ModLog.Info($"Progress: {LogNames.HostTag()}'s batch {message.Batch.Sequence}, given up on earlier, came after all; it applies now.");
        }

        NoteNotEntitled();
        return true;
    }

    /// <summary>
    /// The host's progress status: whether it shares progress at all (said once in the log and the status box when
    /// it's off, and again when it's back on), what it keeps for this PC and its newest batch (missing ones are asked for).
    /// False when it's malformed.
    /// </summary>
    public bool ReceiveStatus(ref BitReader reader)
    {
        if (!ProgressStatusMessage.TryRead(ref reader, out ProgressStatus status))
        {
            return false;
        }

        ulong sessionBefore = _inbox.Session;
        if (!_inbox.Status(status, _relay.FollowedSession))
        {
            NoteWrongSession();
            return true;
        }

        NoteHostingSession(sessionBefore);
        NoteNotEntitled();
        if (_hostSharing != status.Sharing)
        {
            if (!status.Sharing)
            {
                ModLog.Warn($"Progress: {LogNames.HostTag()} has ShareProgress off: what you earn together isn't sent to this PC (nothing counts on your profile " +
                    "from this session until they turn it on).");
                _notifyAbout(_relay.FollowedHost, NoticeId.SharingOff, SafeText.Clean(_relay.HostName, 32));
            }
            else if (_hostSharing == false)
            {
                ModLog.Info($"Progress: {LogNames.HostTag()} turned ShareProgress back on: what you earn together counts on this PC's profile again.");
                _notifyAbout(_relay.FollowedHost, NoticeId.SharingOn, SafeText.Clean(_relay.HostName, 32));
            }

            _hostSharing = status.Sharing;
        }

        return true;
    }

    /// <summary>
    /// A progress message for another hosting session than the one this PC was welcomed into: refused (D135), said once per
    /// following, counted in the stop line. An honest host's are always its welcome's.
    /// </summary>
    private void NoteWrongSession() =>
        Note("wrong session", "Progress: refused a message from the host for another hosting session than the one this PC was welcomed into " +
            "(only that session's progress counts here; logged once, counted when following stops).");

    /// <summary>A rejoin into another hosting session (the host started hosting again): what its copies' store holds is its own.</summary>
    private void NoteHostingSession(ulong before)
    {
        if (before != 0 && before != _inbox.Session)
        {
            _copies.Forget();
        }
    }

    private void NoteNotEntitled()
    {
        if (_inbox.NotEntitled > _notEntitledLogged)
        {
            ModLog.Info($"Progress: {_inbox.NotEntitled - _notEntitledLogged} earlier batch(es) of {LogNames.HostTag()}'s hosting session don't come here " +
                "(made before you first joined it, or dropped from the host's outbox for space); nothing waits for them.");
            _notEntitledLogged = _inbox.NotEntitled;
        }
    }

    /// <summary>Asks the host for the first batches missing here, when <see cref="ProgressInbox.TryRequest"/> says it's time.</summary>
    private void AskForMissing(long now)
    {
        if (!_inbox.TryRequest(now, out uint from, out uint to))
        {
            return;
        }

        ulong session = _inbox.Session;
        bool sent = _relay.SendToHost(T2TOnline.Core.Protocol.MessageType.ProgressResend, T2TOnline.Core.Protocol.Channel.Events, w => ProgressResendMessage.Write(w, new ProgressResend(session, from, to)));
        string range = to == ProgressResendMessage.ToNewest ? $"{from} on" : from == to ? $"{from}" : $"{from} to {to}";
        ModLog.Info($"Progress: batch(es) {range} of {LogNames.HostTag()}'s hosting session are missing here; {(sent ? "asked the host to send them again" : "couldn't ask the host now (tried again later)")} " +
            "(the ledger keeps any from applying twice).");
    }

    /// <summary>
    /// Every frame: the shared run's quest counts, the run history save in flight, and the batches that can apply now (at
    /// most <see cref="ProgressApplyPolicy.MaxPerFrame"/>), saved together; then the first batches missing here are asked
    /// for again when it's time. Never throws.
    /// </summary>
    public void Update()
    {
        long now = FrameClock.NowMicros;
        TrackPlay(now);
        if (_hostRunOver && !ClientMode.Active)
        {
            _hostRunOver = false; // the run's last match unloaded here: nothing is held back for it now
        }

        _loader.Update();
        TrackLink(now);
        if (!_comic.Known)
        {
            NoteOwnProfile(ProfileData.Instance); // the profile loaded after following began
        }

        _applier.ApplyWaiting(now, ProgressApplyPolicy.MaxPerFrame);
        if (_cameAgain.TryReport(_inbox.Duplicates, now, out long cameAgain))
        {
            ModLog.Info($"Progress: {cameAgain} of {LogNames.HostTag()}'s batches came again (a rejoin, or a request for missing ones) and were already applied here; skipped.");
        }

        try
        {
            AskForMissing(now);
            AskForCopies(now);
        }
        catch (Exception ex)
        {
            Note("ask", $"Progress: asking {LogNames.HostTag()} for missing batches or a run's copy failed ({ModLog.SafeErrorText(ex)}); a rejoin still brings them.");
        }
    }

    /// <summary>
    /// The host's answer to a request for a copy of one of its runs: a copy that's expected, of this hosting session,
    /// whose run file is the run asked for and passes the run file check, is loaded (<see cref="RunCopyLoader"/>) and kept
    /// (<see cref="ProgressKeptRuns"/>); "not kept", or a copy that can't be had, lets the batch waiting for it apply without
    /// its run-bound parts. False when the message is malformed.
    /// </summary>
    public bool ReceiveRunCopy(ref BitReader reader)
    {
        if (!ProgressRunCopyMessage.TryRead(ref reader, out ProgressRunCopy? copy))
        {
            return false;
        }

        string run = copy!.Run;
        if (copy.Session != _inbox.Session || !_copies.Expecting(run))
        {
            Note("copy unasked", $"Progress: a copy of {LogNames.HostTag()}'s run {HostText.KeyForLog(run)} came that nothing here waits for (an answer to a request sent again, or of another hosting session); ignored (logged once).");
            return true;
        }

        if (copy.Status == RunCopyStatus.NotKept)
        {
            _copies.Refused(run, "the host doesn't keep it any more: only the newest 3 shared runs of its hosting session are");
            ModLog.Info($"Progress: {LogNames.HostTag()} doesn't keep its run {HostText.KeyForLog(run)} any more (only the newest 3 shared runs of its hosting session are); " +
                $"the batch waiting for it applies without its {ProgressApplyPolicy.RunBoundParts}.");
            return true;
        }

        if (_loader.Busy)
        {
            // Another copy is loading (two runs' ends waiting at once): this one is asked for again shortly.
            ModLog.Info($"Progress: a copy of {LogNames.HostTag()}'s run {HostText.KeyForLog(run)} came while another is loading; it's asked for again shortly.");
            return true;
        }

        RunCheckpoint checkpoint = copy.Checkpoint!;
        byte[] bytes = checkpoint.Get(RunCheckpointPart.Run)!;
        string? problem = !RunFileTypes.TryCheck(bytes, RunFileTypes.RunFile, out string unsafeRun) ? $"its run file was refused here ({RunFileTypes.ProblemForLog(unsafeRun)})" // a type it names is the host's text: by its shape
            : !RunFileSummary.TryRead(bytes, out RunFileSummary? summary) ? "its run file doesn't read as a run"
            : summary!.RunId != run ? $"its run file holds run {HostText.KeyForLog(summary.RunId)}, not {HostText.KeyForLog(run)}"
            : !ProgressLimits.IsPlausibleId(summary.SquadId) ? "its run file names no squad"
            : _loader.Start(run, summary.SquadId, checkpoint, (data, why) => CopyLoaded(run, data, why));
        if (problem != null)
        {
            _copies.Failed(run, problem);
            ModLog.Warn($"Progress: couldn't load a copy of {LogNames.HostTag()}'s run {HostText.KeyForLog(run)}: {problem}; the batch waiting for it applies without its {ProgressApplyPolicy.RunBoundParts}.");
            return true;
        }

        if (_copies.StateOf(run) == RunCopyState.Asked)
        {
            _copies.Answered(run); // still loading (one that finished inside the call answered already)
        }

        return true;
    }

    /// <summary>A copy's load finished: kept (its batch applies on it), or failed (it applies without its run-bound parts).</summary>
    private void CopyLoaded(string run, RunData? data, string problem)
    {
        if (data == null)
        {
            _copies.Failed(run, problem);
            ModLog.Warn($"Progress: couldn't load a copy of {LogNames.HostTag()}'s run {HostText.KeyForLog(run)}: {problem}; the batch waiting for it applies without its {ProgressApplyPolicy.RunBoundParts}.");
            return;
        }

        _kept.Keep(run, data);
        _copies.Loaded(run);
    }

    /// <summary>
    /// Every frame: the link to the host told to the copy requests, so nothing is asked, and no wait runs out, while
    /// nobody could hear it; and a rejoin (the host dropped this friend meanwhile, so what was asked never reached it) asks
    /// for what is still awaited again, from the start. Said while a copy is awaited, once per outage.
    /// </summary>
    private void TrackLink(long now)
    {
        bool up = _relay.LinkUp;
        int joins = _relay.LinkJoins;
        if (joins != _joinsSeen)
        {
            _joinsSeen = joins;
            int again = _copies.LinkRestarted();
            if (again > 0)
            {
                ModLog.Info($"Progress: this PC is back in {LogNames.HostTag()}'s session; the {again} copy request(s) still unanswered start over (the earlier ones may never have reached it; they aren't counted).");
            }
        }

        if (up != _linkUp && _copies.Awaiting > 0)
        {
            ModLog.Info(up
                ? $"Progress: the link to {LogNames.HostTag()} is up again; the wait for a copy of its run goes on from where it stopped."
                : $"Progress: the link to {LogNames.HostTag()} is down; the wait for a copy of its run is paused (nothing is asked meanwhile, and the 30 s don't run).");
        }

        _linkUp = up;
        _copies.Tick(now, up);
    }

    /// <summary>
    /// Every frame: the play this PC sees with the host (D135): real time while the host's match plays here and while the
    /// host's run is held here (a step of at most a second: a stalled or sleeping PC adds no more), the host's matches with
    /// enough of each played here (<see cref="StatBounds.SecondsPerMatch"/>), and each new run of the host's held here. Noted
    /// in the ledger every few seconds (and right before a batch applies, and as following stops). Managed values only.
    /// </summary>
    private void TrackPlay(long now)
    {
        long step = _playClock < 0 ? 0 : Math.Clamp(now - _playClock, 0, MaxPlayStepMicros);
        _playClock = now;
        if (_matches.PlayingHostMatch)
        {
            _playMatchMicros += step;

            // A match counts once enough of it played here (StatBounds.SecondsPerMatch), over a rejoin's playback too.
            if (_matchPlay.Played(_matches.PlayedHostSerial, step / 1e6))
            {
                _playMatches++;
            }
        }

        uint checkpoint = _relay.AppliedRun;
        if (checkpoint > 0)
        {
            _playRunMicros += step;
        }

        if (checkpoint != _checkpointSeen)
        {
            _checkpointSeen = checkpoint;
            string run = _relay.View?.Text(Details.Run) ?? string.Empty;
            if (checkpoint > 0 && ProgressLimits.IsPlausibleId(run) && _runsNoted.Count < MaxRunsNoted && _runsNoted.Add(run))
            {
                _playRuns++;
            }
        }

        if (now >= _nextPlayNote)
        {
            _nextPlayNote = now + PlayNoteMicros;
            NotePlay();
        }
    }

    /// <summary>The play counted since the last note goes in the ledger, in this hour's bucket for the host.</summary>
    private void NotePlay()
    {
        var play = new PlayTally(_playMatchMicros / 1e6, _playMatches, _playRunMicros / 1e6, _playRuns);
        if (play.IsEmpty)
        {
            return;
        }

        _ledger.NotePlay(_hostKey, DateTime.UtcNow, play);
        _playMatchMicros = _playRunMicros = 0;
        _playMatches = _playRuns = 0;
    }

    /// <summary>Asks the host for the copies batches wait for (<see cref="RunCopyRequests.TryAsk"/>), and says when one is given up on.</summary>
    private void AskForCopies(long now)
    {
        while (true)
        {
            bool ask = _copies.TryAsk(now, out string run, out int attempt, out string gaveUp);
            if (gaveUp.Length > 0)
            {
                ModLog.Warn($"Progress: {LogNames.HostTag()} didn't answer {RunCopyRequests.MaxAsks} requests for a copy of its run {gaveUp}; the batch waiting for it applies " +
                    $"without its {ProgressApplyPolicy.RunBoundParts}.");
            }

            if (!ask)
            {
                return;
            }

            ulong session = _inbox.Session;
            bool sent = _relay.SendToHost(T2TOnline.Core.Protocol.MessageType.ProgressRunRequest, T2TOnline.Core.Protocol.Channel.Events,
                w => ProgressRunRequestMessage.Write(w, new ProgressRunRequest(session, run)));
            ModLog.Info($"Progress: {(sent ? "asked" : "couldn't ask")} {LogNames.HostTag()} for a copy of its run {HostText.KeyForLog(run)}" +
                $"{(attempt > 1 ? $" (again: {attempt} of {RunCopyRequests.MaxAsks})" : string.Empty)}.");
        }
    }

    /// <summary>
    /// The session is ending: applies the batches still waiting that can apply now (each as <see cref="Update"/> would,
    /// at most <see cref="ProgressApplyPolicy.MaxAtEnd"/>, saved once), so progress that arrived isn't lost with the session:
    /// the host's match and run loads no longer hold them back, and a run's end goes on its run (held, or kept). Stops at
    /// the first one that has to wait (this game's saves not loaded or waiting for a restart, a second run history entry
    /// while the first still saves). Never throws.
    /// </summary>
    public void Flush()
    {
        int before = _inbox.Held;
        if (before == 0)
        {
            return;
        }

        // Nothing it waits for can come now: a batch for a run this PC is about to load is applied without its run-bound
        // parts rather than kept waiting.
        _applier.Flush(FrameClock.NowMicros);
        ModLog.Info($"Progress: the session is ending; {before - _inbox.Held} of {before} waiting batch(es) from {LogNames.HostTag()} applied before it ended" +
            $"{(_inbox.Held > 0 ? $" ({_inbox.Held} still wait: {(_applier.WaitWhy.Length > 0 ? _applier.WaitWhy : "a save in progress")})" : string.Empty)}.");
    }

    /// <summary>Lines for the status box: only a batch that has waited a while.</summary>
    public IEnumerable<string> Status()
    {
        if (_applier.WaitSince >= 0 && FrameClock.NowMicros - _applier.WaitSince > WaitShownAfterMicros && _inbox.Held > 0)
        {
            yield return $"shared progress waits: {_applier.WaitWhy}";
        }
    }

    /// <summary>The session ended: this PC's own run-bound values go back, and the ledger is saved. Never throws.</summary>
    public void Stop(string reason, long heldBack, bool gameClosing = false)
    {
        try
        {
            if (!gameClosing)
            {
                LeaveHistorySaving();
            }

            PollHistory(FrameClock.NowMicros, force: true);
        }
        catch (Exception ex)
        {
            ModLog.Error("Progress: couldn't finish the shared run's history entry", ex);
        }

        try
        {
            if (_ownQuest != null && ProfileData.Instance?.benchQuestData is BenchQuestProfileData quests)
            {
                if (_sharedRun.Length > 0)
                {
                    _ledger.SetRun(_sharedRun, ReadQuest(quests));
                }

                WriteQuest(quests, _ownQuest);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Progress: couldn't put this PC's own bench quest counts back", ex);
        }

        SaveComicMarks(gameClosing);
        PlayTally play = PlayTally.None;
        try
        {
            NotePlay();
            play = _ledger.PlayWithin(_hostKey, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            ModLog.Error("Progress: couldn't note this PC's play with the host", ex);
        }

        SaveLedger();
        ProgressLedgerFile.Snapshot(start: false);
        ModLog.Info($"Progress: this PC's play with the host in the last 24 hours, which bounds what the host may credit here: {play}.");
        int waiting = _inbox.Held;
        bool runBound = false;
        try
        {
            runBound = waiting > 0 && _inbox.AnyWaiting(ProgressApplyPolicy.HoldsRun);
        }
        catch (Exception)
        {
            // only for the log line
        }

        long missing = _ledger.MissingCount(_inbox.Session);
        int cameAgain = _inbox.Duplicates;
        _inbox.Clear();
        _loader.Stop();
        ModLog.Info($"Progress: stopped following {LogNames.HostTag()} ({reason}). {_applier.Applied} batches applied here" +
            $"{(_applier.KeptApplied > 0 ? $" ({_applier.KeptApplied} on this PC's kept copy of a run it no longer held)" : string.Empty)}" +
            $"{(waiting > 0 ? $"; {waiting} not applied yet{(ModSettings.ShareProgress ? string.Empty : " (ShareProgress is off)")}: they come again if you rejoin this hosting session" : string.Empty)}" +
            $"{(runBound ? $" (their {ProgressApplyPolicy.RunBoundParts} then on this PC's kept copy of their run, unless this game restarted)" : string.Empty)}" +
            $"{(_inbox.Requests > 0 ? $"; asked the host for missing batches {_inbox.Requests} times" : string.Empty)}" +
            $"{(_inbox.Skipped > 0 ? $"; {_inbox.Skipped} never came in time" : string.Empty)}" +
            $"{(_inbox.Late > 0 ? $" ({_inbox.Late} came later and applied)" : string.Empty)}" +
            $"{(missing > 0 ? $"; {missing} still missing (they apply if a rejoin of this hosting session brings them)" : string.Empty)}" +
            $"{(_hostSharing == false ? $"; {LogNames.HostTag()} had ShareProgress off" : string.Empty)}" +
            $"{(_derived > 0 ? $"; {_derived} records and percentages worked out again from this profile's own counts" : string.Empty)}" +
            $"{(_granted > 0 ? $"; {_granted} unlock achievement(s) handed to the game directly at a run's end" : string.Empty)}" +
            $"{(_applier.FailedBatches > 0 ? $"; {_applier.FailedBatches} failed as a whole (see the errors above)" : string.Empty)}" +
            $"{(_credit.Refused - _creditRefusedBefore + _overflows > 0 ? $"; {_credit.Refused - _creditRefusedBefore + _overflows} stat operations refused for exceeding a stat's limits" : string.Empty)}" +
            $"{(_credit.OverBudget - _overBudgetBefore > 0 ? $"; {_credit.OverBudget - _overBudgetBefore} stat operations refused past what this PC's play with the host explains" : string.Empty)}" +
            $"{(_inbox.WrongSession > 0 ? $"; {_inbox.WrongSession} message(s) for another hosting session refused" : string.Empty)}" +
            $"{(_entityCredit.Refused - _entityRefusedBefore > 0 ? $"; {_entityCredit.Refused - _entityRefusedBefore} entity stat updates refused for exceeding this session's limits" +
                $"{(_entityCredit.OverBudget - _entityOverBefore > 0 ? $" ({_entityCredit.OverBudget - _entityOverBefore} past what this PC's play with the host explains)" : string.Empty)}" : string.Empty)}" +
            $"{(heldBack > 0 ? $"; this PC's own game was held back {heldBack} times" : string.Empty)}" +
            $"{(_applier.Applied + cameAgain > 0 ? _runEnds.Part(cameAgain) : string.Empty)}.");
    }

    // ------------------------------------------------------------------ the applier's game side (IProgressTarget)

    /// <inheritdoc />
    string IProgressTarget.LiveSharedRun => _sharedRun;

    /// <inheritdoc />
    bool IProgressTarget.HoldsRun => ProfileData.Instance?.campaignState?.runData != null;

    /// <inheritdoc />
    bool IProgressTarget.HistorySaving => _history != null;

    /// <inheritdoc />
    ApplyFacts IProgressTarget.Facts(bool ending)
    {
        // Applied and saved before this game's own stats or profile have loaded, the save would write defaults (plus the
        // batch) over them; and while a restored backup waits for a restart nothing would be saved, yet marked applied. The
        // order of the questions is Core's (the game calls only when they can matter); with sharing off nothing applies
        // whatever else holds, so nothing is asked.
        bool sharing = ModSettings.ShareProgress;
        ApplyBlock block = sharing
            ? ProgressApplyPolicy.BlockOf(ClientMode.Active, _matches.FriendBusy, ending, _hostRunOver, RunLoading, SavesNotLoaded, RestartPending)
            : ApplyBlock.None;

        // A batch that waits on those alone (through the host's end-of-match screens, or a whole session with sharing off)
        // asks every frame: the runs, which the policy reads only for a batch it lets through, are left unread meanwhile.
        bool runsUnread = ProgressFactsNeed.WaitsWhateverTheRuns(sharing, block, ending, _hostRunOver);
        string held = runsUnread ? string.Empty : ProfileData.Instance?.campaignState?.runData?.runID ?? string.Empty;
        string view = runsUnread ? string.Empty : _relay.View?.Text(Details.Run) ?? string.Empty;
        return new ApplyFacts(sharing, block, ending, held, view, _kept.Ids, _copies, _relay.LinkUp, _hostRunOver);
    }

    /// <inheritdoc />
    void IProgressTarget.BeforeBatches(long nowMicros)
    {
        PollHistory(nowMicros, force: false);
        if (nowMicros >= _nextRunCheck)
        {
            // A few times a second sees the held run change (a checkpoint loads long before the match it's for); it's
            // looked at again right before a batch applies.
            _nextRunCheck = nowMicros + RunCheckMicros;
            TrackSharedRun();
        }
    }

    /// <inheritdoc />
    void IProgressTarget.BeforeBatch()
    {
        TrackSharedRun();
        NotePlay(); // the play up to now bounds what this batch may credit
    }

    /// <inheritdoc />
    void IProgressTarget.WantRunCopy(ProgressBatch batch)
    {
        if (_copies.Want(batch.Run))
        {
            ModLog.Info($"Progress: batch {batch.Sequence} of {LogNames.HostTag()} is for the host's run {HostText.KeyForLog(batch.Run)}, which this PC held before its game restarted; " +
                $"asking the host for a copy of it (its {ProgressApplyPolicy.RunBoundParts} apply on that).");
        }
    }

    /// <inheritdoc />
    IProgressLog IProgressTarget.StartLog(ProgressBatch batch) => new ApplyLog(_hostTag, batch);

    /// <inheritdoc />
    bool IProgressTarget.PutKeptRun(string run)
    {
        CampaignState? campaign = ProfileData.Instance?.campaignState;
        _swapped = campaign == null ? null : _kept.Swap(campaign, run);
        return _swapped != null;
    }

    /// <inheritdoc />
    void IProgressTarget.PutHeldRunBack()
    {
        IDisposable? swapped = _swapped;
        _swapped = null;
        swapped?.Dispose();
    }

    /// <inheritdoc />
    bool IProgressTarget.Keeps(string run) => _kept.Has(run);

    /// <inheritdoc />
    int[]? IProgressTarget.ReadQuestCounts() => ProfileData.Instance?.benchQuestData is BenchQuestProfileData quests ? ReadQuest(quests) : null;

    /// <inheritdoc />
    void IProgressTarget.WriteQuestCounts(IReadOnlyList<int> counts)
    {
        if (ProfileData.Instance?.benchQuestData is BenchQuestProfileData quests)
        {
            WriteQuest(quests, counts);
        }
    }

    /// <inheritdoc />
    ApplyAbilities IProgressTarget.Abilities()
    {
        StatManager? manager = Stats();
        return new ApplyAbilities(manager?.StatRepository != null, Unlocks(manager) != null, FromProject<BenchQuestSystem>() != null, true);
    }

    /// <inheritdoc />
    StatsGate IProgressTarget.OpenStatsGate()
    {
        // The unlock checks need this game to track stats right now, as the host's did: its difficulty is set to the run's
        // for them (a friend's difficulty manager may still hold its own), and a match of another mode must be gone.
        int difficulty = ProfileData.Instance!.campaignState!.runData!.difficultyLevel;
        _restoreDifficulty = AlignDifficulty(difficulty, out bool aligned);
        bool tracking = Stats()?.StatRepository?.ShouldSkipStatTracking() == false;
        string why = DifficultyManager._instance?.currentDifficulty?.index == 0 ? "the difficulty here is training" : "a match that doesn't count is loaded here";
        return new StatsGate(tracking, why, aligned ? difficulty : -1);
    }

    /// <inheritdoc />
    void IProgressTarget.CloseStatsGate()
    {
        DifficultyLevel? previous = _restoreDifficulty;
        _restoreDifficulty = null;
        PutBack(previous);
    }

    /// <inheritdoc />
    void IProgressTarget.StartApplying(bool inRun)
    {
        _manager = Stats();
        _stats = _manager?.StatRepository;
        _unlocks = Unlocks(_manager);
        _quests = FromProject<BenchQuestSystem>();
        Applying = true;
        ApplyingNow = this;
        _records.Clear();

        // The host's game credited all of this in its campaign. Some of the game's own steps first ask whether this game is
        // in the campaign (the bench quests at the run's win), and the title screen, where a run's end applies, isn't: while
        // the host's run is held here (or its copy), they're told it is.
        if (inRun && !Il2CppTape2Tape.T2TUtils.s_ForceRogueMode)
        {
            Il2CppTape2Tape.T2TUtils.s_ForceRogueMode = true;
            _forcedCampaign = true;
        }
    }

    /// <inheritdoc />
    void IProgressTarget.StopApplying()
    {
        Applying = false;
        ApplyingNow = null;
        _liveEarned.Clear();
        _manager = null;
        _stats = null;
        _unlocks = null;
        _quests = null;
        if (_forcedCampaign)
        {
            _forcedCampaign = false;
            Il2CppTape2Tape.T2TUtils.s_ForceRogueMode = false;
        }
    }

    /// <inheritdoc />
    void IProgressTarget.Apply(ProgressEntry entry, IProgressLog log)
    {
        var line = (ApplyLog)log;
        switch (entry)
        {
            case StatEntry stat:
                ApplyStat(_stats!, stat, line);
                break;
            case StatTextEntry text:
                ApplyText(_stats!, text, line);
                break;
            case EntityStatEntry entity:
                ApplyEntity(_stats!, entity, line);
                break;
            case UnlockPassEntry pass:
                ApplyCheck(_unlocks!, pass, line);
                break;
            case ComicsEntry:
                ApplyComics(line);
                break;
            case QuestEntry quest:
                ApplyQuest(_quests!, quest, line);
                break;
        }
    }

    /// <inheritdoc />
    string? IProgressTarget.SquadMismatch(SquadEntry squad)
    {
        RunData run = ProfileData.Instance!.campaignState!.runData!;
        return run.squadId != squad.Squad || run.difficultyLevel != squad.Difficulty
            ? $"this PC's run is {HostText.KeyForLog(run.squadId)} on difficulty {run.difficultyLevel}"
            : null;
    }

    /// <inheritdoc />
    void IProgressTarget.RecordSquad(SquadEntry squad, IProgressLog log)
    {
        RecordSquad(ProfileData.Instance!, squad, (ApplyLog)log);
        _runEnds.Squad(squad.Squad, squad.Outcome == SquadOutcome.Won, squad.Difficulty);
    }

    /// <inheritdoc />
    void IProgressTarget.EndRun(IProgressLog log) => EndRun(ProfileData.Instance!, (ApplyLog)log);

    /// <inheritdoc />
    string? IProgressTarget.HistoryUnavailable() =>
        FromProject<IRunHistoryRepository>()?.TryCast<RunHistoryRepository>() == null ? "the run history entry not saved (this game's run history isn't there)" : null;

    /// <inheritdoc />
    void IProgressTarget.SaveHistory(RunHistoryRecord history, long nowMicros, IProgressLog log)
    {
        RunHistoryData data = history.Entry;

        // An entry the game's history page can't show (it throws there, at every later launch) never reaches this PC's files.
        if (RunHistorySafety.Refusal(data) is string unsafeEntry)
        {
            log.Note(unsafeEntry);
            return;
        }

        RunHistoryRepository repository = FromProject<IRunHistoryRepository>()!.TryCast<RunHistoryRepository>()!;
        RunHistoryEntryModel entry = RunHistoryMapping.ToGame(data, out int matchesLeftOut);

        // The page makes a date of the entry's time, and lists runs newest first: the host's own time, when it is one.
        entry.timestampUtc = RunHistorySafety.Timestamp(data.TimestampUtc, DateTime.UtcNow, out bool restamped);

        // The repository saves on its own time (it waits for its lock and the cloud): its files stay allowed until it's done.
        IDisposable allowance = SaveGuard.AllowWrites("the shared run's history entry", HistoryFiles());
        try
        {
            UniTask task = repository.SaveEntry(HistoryLogos.ForEntry(entry));
            _history = new PendingHistory(task, allowance, nowMicros, data, ((ApplyLog)log).Sequence);
        }
        catch
        {
            allowance.Dispose();
            throw;
        }

        log.Part("run history entry saving");
        if (restamped)
        {
            log.Note("the run history entry's time from the host wasn't a date, or was more than a day ahead of this PC's clock: kept as ended now");
        }

        if (matchesLeftOut > 0)
        {
            log.Note(string.Create(CultureInfo.InvariantCulture,
                $"{matchesLeftOut} of the run history entry's {data.Matches.Count} match(es) left out: {RunHistoryMapping.MatchesLeftOutWhy}"));
        }
    }

    /// <inheritdoc />
    void IProgressTarget.Mark(ProfileMarkEntry mark, IProgressLog log) => Mark(ProfileData.Instance!, mark, (ApplyLog)log);

    /// <inheritdoc />
    void IProgressTarget.WorkOutDerived(IProgressLog log)
    {
        if (_stats != null)
        {
            WorkOutDerived(_stats, (ApplyLog)log);
        }
    }

    /// <inheritdoc />
    void IProgressTarget.LedgerChanged() => SaveLedger();

    /// <inheritdoc />
    string IProgressTarget.SaveAfter(IReadOnlyList<IProgressLog> applied)
    {
        // Completed bench quests and this game's own collection achievements are checked, the shared run's quest counts kept,
        // then the profile and stats saved once.
        var last = (ApplyLog)applied[^1];
        ProfileData? profile = ProfileData.Instance;
        if (profile != null)
        {
            NoteQuests(profile, last);
            KeepSharedQuestCounts(profile);
        }

        CheckAchievements(last);
        return profile != null ? Save(profile, Stats()) : "NOT saved (this game's profile is gone)";
    }

    /// <inheritdoc />
    void IProgressTarget.Report(IProgressLog log, string saved)
    {
        var line = (ApplyLog)log;
        ModLog.Info(line.Line(saved));
        if (line.Detail() is { Length: > 0 } detail)
        {
            // Every value the batch changed, for the session file only: the line above shows the first few.
            ModLog.Trace(detail);
        }

        foreach (string unlocked in line.UnlockedNames)
        {
            _runEnds.Unlocked(unlocked);
        }

        // An entry that failed is only a note in the line above: said as a warning (the first few batches), and counted.
        if (line.FailedEntries > 0 && _runEnds.Failed(line.FailedEntries))
        {
            ModLog.Warn($"Progress: {line.FailedEntries} entr{(line.FailedEntries == 1 ? "y" : "ies")} of {LogNames.HostTag()}'s batch {line.Sequence} failed here ({line.FailedWords}); the rest applied.");
        }
    }

    /// <inheritdoc />
    void IProgressTarget.Log(ProgressLogLevel level, string line, Exception? exception)
    {
        switch (level)
        {
            case ProgressLogLevel.Trace:
                ModLog.Trace(line);
                break;
            case ProgressLogLevel.Info:
                ModLog.Info(line);
                break;
            case ProgressLogLevel.Warn:
                ModLog.Warn(line);
                break;
            default:
                ModLog.Error(line, exception);
                break;
        }
    }

    private void ApplyStat(StatRepository stats, StatEntry entry, ApplyLog log)
    {
        // Only a stat this game has (its own StatType): the game's own lookup logs every unknown one.
        IStat? stat = Enum.IsDefined((StatType)entry.Stat) ? stats.GetStat((StatType)entry.Stat) : null;
        if (stat == null)
        {
            log.UnknownStat();
            return;
        }

        StatBucket bucket = StatOps.BucketOf(entry.Op);
        Il2CppSystem.Object? runValue = stat.GetRunValue(), actValue = stat.GetActValue();
        double before = Number(Read(stat, bucket));
        double lifetimeBefore = bucket == StatBucket.Lifetime ? before : Number(stat.GetValue());
        IntStat? whole = stat.TryCast<IntStat>();
        FloatStat? real = whole == null ? stat.TryCast<FloatStat>() : null;
        StatValue value = entry.Value;
        bool fromStat = StatOps.IsFromStat(entry.Op);
        if (StatOps.IsReset(entry.Op) || fromStat ? whole == null
            : StatOps.HasValue(entry.Op) && (whole != null ? value.Kind != StatValueKind.Whole : real == null || value.Kind != StatValueKind.Real))
        {
            log.Mismatched();
            return;
        }

        // The game adds without an overflow check (a whole-number value past its largest would wrap negative), and what the
        // host's game takes back (a goal against it cancelled) never takes this profile's stat below zero.
        StatVerdict verdict = ProgressApplyPolicy.Check(entry, before, whole != null);
        if (verdict != StatVerdict.Apply)
        {
            _overflows += verdict == StatVerdict.Overflow ? 1 : 0;
            log.Refused();
            return;
        }

        // A hosting session can add only so much to a stat, batch after batch (D47), and a host only what this PC's own play
        // with it in the last 24 hours explains, over all its hosting sessions (D135).
        int overBudget = _credit.OverBudget;
        DateTime utc = DateTime.UtcNow;
        if (!_credit.TryGive(_inbox.Session, entry, _ledger, _hostKey, utc))
        {
            if (_credit.OverBudget != overBudget && !_noted.Contains("budget"))
            {
                PlayTally play = _ledger.PlayWithin(_hostKey, utc);
                Note("budget", $"Progress: a stat operation from the host went past what this PC's play with that host explains ({(StatType)entry.Stat}: " +
                    $"{StatBounds.LimitWords(entry, play, _ledger.BudgetUsed(_hostKey, entry.Stat, utc).Added)} in 24 hours, from {play}); it and any more past " +
                    "such a bound are refused (logged once, counted when following stops).");
            }

            log.Refused();
            return;
        }

        if (fromStat)
        {
            // The record from another stat's value: this profile's own (the game reads the stat's lifetime value).
            IStat? source = stats.GetStat((StatType)value.WholeValue);
            double own = source == null ? double.NaN : Number(source.GetValue());
            if (double.IsNaN(own))
            {
                log.Mismatched();
                return;
            }

            value = StatValue.Whole((int)own);
        }

        switch (entry.Op)
        {
            case StatOp.ResetCampaign:
                // The game's SetStat(stat, 0): the lifetime value and the campaign's (this run's and act's go back below).
                whole!.SetValue(0);
                whole.SetCampaignValue(0, true);
                break;
            case StatOp.ResetPlayNow:
                whole!.SetValue(0);
                whole.SetPlayNowValue(0);
                break;
            case StatOp.Increment:
                stat.Increment();
                break;
            case StatOp.IncrementCampaign:
                stat.IncrementCampaign();
                break;
            case StatOp.IncrementPlayNow:
                stat.IncrementPlayNow();
                break;
            case StatOp.IncrementBy:
                stat.IncrementBy(Box(value));
                break;
            case StatOp.IncrementCampaignBy:
                stat.IncrementCampaignBy(Box(value));
                break;
            case StatOp.IncrementPlayNowBy:
                stat.IncrementPlayNowBy(Box(value));
                break;
            default:
                SetRecord(entry.Op, whole, real, value);
                break;
        }

        // The campaign operations move "this run" and "this act" too: those are this PC's own run's, so they go back.
        if (runValue != null)
        {
            stat.SetRunValue(runValue);
        }

        if (actValue != null)
        {
            stat.SetActValue(actValue);
        }

        double after = Number(Read(stat, bucket));
        if (StatOps.IsReset(entry.Op))
        {
            log.Stat(entry.Stat, StatBucket.Lifetime, lifetimeBefore, Number(stat.GetValue()), false);
        }

        // A record holder's name follows this profile's own lifetime record (the game names the holder only when it rises).
        _records.Applied(entry, bucket == StatBucket.Lifetime && !double.IsNaN(before) && after > before);
        log.Changed(entry);
        log.Stat(entry.Stat, bucket, before, after, StatOps.IsRecord(entry.Op));
    }

    /// <summary>
    /// A record's holder by name (the game sets the text outright when the lifetime record rises): set only when this
    /// profile's own lifetime record rose with the batch's last operation on it, as the game would here.
    /// </summary>
    private void ApplyText(StatRepository stats, StatTextEntry entry, ApplyLog log)
    {
        if (!_records.Holds(entry))
        {
            return; // this profile's record didn't rise: its holder stays its own
        }

        StringStat? text = Enum.IsDefined((StatType)entry.Stat) ? stats.GetStat((StatType)entry.Stat)?.TryCast<StringStat>() : null;
        if (text == null)
        {
            log.Mismatched();
            return;
        }

        text.SetValue(entry.Text);
        if (entry.Campaign)
        {
            text.SetCampaignValue(entry.Text, true);
        }
        else
        {
            text.SetPlayNowValue(entry.Text);
        }

        log.Part($"{(StatType)entry.Stat} ({LogNames.TextShape(entry.Text)})"); // a team name, maybe a custom one: player-written (D127)
    }

    /// <summary>A per-entity stat update, combined into this profile's own (or added): <see cref="ProgressEntityStats.Apply"/>.</summary>
    private void ApplyEntity(StatRepository stats, EntityStatEntry entry, ApplyLog log)
    {
        // A hosting session can add only so much to an entity, and touch only so many (D47); a host only what this PC's own
        // play with it in the last 24 hours explains (D135).
        int overBudget = _entityCredit.OverBudget;
        if (!_entityCredit.TryGive(_inbox.Session, entry, _ledger, _hostKey, DateTime.UtcNow))
        {
            if (_entityCredit.OverBudget != overBudget)
            {
                Note("entity budget", $"Progress: an entity stat update from the host went past what this PC's play with that host explains ({entry.Group}); it and any " +
                    "more past such a bound are refused (logged once, counted when following stops).");
            }

            log.Refused();
            return;
        }

        EntityApplied applied = ProgressEntityStats.Apply(stats, entry);
        log.Entity(entry.Group, applied);
        if (applied.Key == EntityKeyWrite.Failed)
        {
            Note("entity key", $"Progress: an entity's key in this PC's stats file couldn't be written after filing {LogNames.HostTag()}'s update ({entry.Group}: " +
                $"{applied.KeyProblem ?? "no reason given"}); the update is in this game's memory. Logged once; each batch's line counts them.");
        }
    }

    /// <summary>
    /// The derived stats whose counters the batch changed here (<see cref="DerivedStats"/>: a "W-L" record, four
    /// percentages) are worked out again from this profile's own counters, as the game does at the end of a match.
    /// </summary>
    private void WorkOutDerived(StatRepository stats, ApplyLog log)
    {
        foreach ((DerivedStat derived, StatBucket bucket) in DerivedStats.Affected(log.ChangedStats))
        {
            try
            {
                IStat? first = stats.GetStat((StatType)derived.First), second = stats.GetStat((StatType)derived.Second);
                IStat? result = stats.GetStat((StatType)derived.Stat);
                double a = first == null ? double.NaN : Number(Read(first, bucket));
                double b = second == null ? double.NaN : Number(Read(second, bucket));
                if (result == null || double.IsNaN(a) || double.IsNaN(b))
                {
                    continue;
                }

                string shown;
                if (derived.Kind == DerivedKind.WonLost)
                {
                    StringStat? text = result.TryCast<StringStat>();
                    if (text == null)
                    {
                        continue;
                    }

                    shown = DerivedStats.Text(derived, (int)a, (int)b);
                    switch (bucket)
                    {
                        case StatBucket.Campaign:
                            text.SetCampaignValue(shown, true);
                            break;
                        case StatBucket.PlayNow:
                            text.SetPlayNowValue(shown);
                            break;
                        default:
                            text.SetValue(shown);
                            break;
                    }
                }
                else
                {
                    FloatStat? real = result.TryCast<FloatStat>();
                    if (real == null)
                    {
                        continue;
                    }

                    float fraction = DerivedStats.Fraction(derived, (int)a, (int)b);
                    shown = fraction.ToString("0.###", CultureInfo.InvariantCulture);
                    switch (bucket)
                    {
                        case StatBucket.Campaign:
                            real.SetCampaignValue(fraction, true);
                            break;
                        case StatBucket.PlayNow:
                            real.SetPlayNowValue(fraction);
                            break;
                        default:
                            real.SetValue(fraction);
                            break;
                    }
                }

                _derived++;
                log.Derived($"{(StatType)derived.Stat}.{bucket} {shown}");
            }
            catch (Exception ex)
            {
                log.Note($"{(StatType)derived.Stat} not worked out again ({ex.GetType().Name})");
            }
        }
    }

    private static Il2CppSystem.Object Read(IStat stat, StatBucket bucket) => bucket switch
    {
        StatBucket.Campaign => stat.GetCampaignValue(),
        StatBucket.PlayNow => stat.GetPlayNowValue(),
        _ => stat.GetValue(),
    };

    private static void SetRecord(StatOp op, IntStat? whole, FloatStat? real, StatValue value)
    {
        if (whole != null)
        {
            int v = value.WholeValue;
            _ = op switch
            {
                StatOp.TrySetLarger or StatOp.TrySetLargerFromStat => whole.TrySetLargerValue(v),
                StatOp.TrySetLargerCampaign or StatOp.TrySetLargerCampaignFromStat => whole.TrySetLargerCampaignValue(v),
                StatOp.TrySetLargerPlayNow or StatOp.TrySetLargerPlayNowFromStat => whole.TrySetLargerPlayNowValue(v),
                StatOp.TrySetSmaller => whole.TrySetSmallerValue(v),
                StatOp.TrySetSmallerCampaign => whole.TrySetSmallerCampaignValue(v),
                StatOp.TrySetSmallerPlayNow => whole.TrySetSmallerPlayNowValue(v),
                _ => false,
            };
            return;
        }

        if (real != null)
        {
            float v = value.RealValue;
            _ = op switch
            {
                StatOp.TrySetLarger => real.TrySetLargerValue(v),
                StatOp.TrySetLargerCampaign => real.TrySetLargerCampaignValue(v),
                StatOp.TrySetLargerPlayNow => real.TrySetLargerPlayNowValue(v),
                StatOp.TrySetSmaller => real.TrySetSmallerValue(v),
                StatOp.TrySetSmallerCampaign => real.TrySetSmallerCampaignValue(v),
                StatOp.TrySetSmallerPlayNow => real.TrySetSmallerPlayNowValue(v),
                _ => false,
            };
        }
    }

    private void ApplyCheck(UnlockSystem unlocks, UnlockPassEntry pass, ApplyLog log)
    {
        if (pass.Timing > (int)UnlockTiming.SquadUnlock)
        {
            log.Note($"an unlock check of a timing this game doesn't have ({pass.Timing}) skipped");
            return;
        }

        _liveEarned.Clear();
        foreach (string id in pass.Earned)
        {
            if (unlocks.HasUnlockData(id))
            {
                _liveEarned.Add(id);
            }
            else
            {
                log.Note($"the host earned {HostText.KeyForLog(id)}, which this game doesn't have");
            }
        }

        // Not the stored ones: the run's stored unlocks here are the host's. This profile's own thresholds decide; the
        // toasts show at once (the host's in-match timings wait for a faceoff that never comes here).
        Il2CppSystem.Collections.Generic.List<IUnlockData>? found = unlocks.CheckForNewUnlocks((UnlockTiming)pass.Timing, false, true, ENotificationTiming.AfterCheckingUnlocks);
        string? run = ProfileData.Instance?.campaignState?.runData?.runID; // the batch's run: held, or this PC's kept copy put in place
        bool detached = EventChannel.LogicDetached;
        int granted = 0;
        for (int i = 0; found != null && i < found.Count; i++)
        {
            IUnlockData unlock = found[i];
            string id = unlock.RewardID;
            UnlockSystem.MarkUnlocked(id);
            log.Unlocked(ProgressPatches.Describe(unlock));

            // Shown on this player's own new-unlocks screen at the host's run end, from whichever session of the run.
            Menus.UnlockViews.Noted(id, _ledger.NoteUnlock(run, id));
            if (detached && GrantHere(unlock, log))
            {
                granted++;
            }
        }

        if (granted > 0)
        {
            _granted += granted;
            log.Part($"{granted} unlock(s) handed to the game's unlock achievements directly (the host's last match is still loaded here, " +
                "with the game's own listeners held off its events)");
        }

        _liveEarned.Clear();
    }

    /// <summary>
    /// While client mode holds the game's own listeners off the event buses (D24: the host's last match stays loaded here at
    /// the run's end), the unlock registry's achievement bridge doesn't hear the check's <c>RogueEvents.UnlockEarned</c>:
    /// its own handler (<c>UnlockAchievementBridge.OnUnlockEarned</c>, which grants the unlock's Steam achievement through
    /// the game's own call) is called for it, as the event would have. False when it couldn't be (noted on the batch's line).
    /// </summary>
    private bool GrantHere(IUnlockData unlock, ApplyLog log)
    {
        try
        {
            if (_bridge == null || _bridge.WasCollected)
            {
                _bridge = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<UnlockAchievementBridge>(), UnityEngine.FindObjectsSortMode.None)
                    .FirstOrDefault()?.TryCast<UnlockAchievementBridge>();
            }

            if (_bridge == null)
            {
                log.Note("an unlock's achievement not granted here: the game's unlock achievement bridge isn't there");
                return false;
            }

            _bridge.OnUnlockEarned(unlock);
            return true;
        }
        catch (Exception ex)
        {
            log.Note($"an unlock's achievement not granted here ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// The shared run's result on this profile's squad record, through the game's own <c>RecordSquadWin</c> or
    /// <c>RecordSquadLoss</c> (the applier checked the run and made its once-per-run mark), with the host's run time and the
    /// run's best scorer in place for the call (<see cref="WithBestScorer"/>).
    /// </summary>
    private static void RecordSquad(ProfileData profile, SquadEntry squad, ApplyLog log)
    {
        CampaignState campaign = profile.campaignState!;
        SquadTeamData before = profile.GetSquadTeamData(squad.Squad);
        bool known = before != null && !string.IsNullOrEmpty(before.Id);
        int bestBefore = known ? before!.MaxGoalsInOneRun : 0;
        WithBestScorer(campaign, squad, () =>
        {
            if (squad.Outcome == SquadOutcome.Won)
            {
                WithRunTime(campaign, squad.RunSeconds, profile.RecordSquadWin);
            }
            else
            {
                profile.RecordSquadLoss();
            }
        });

        SquadTeamData after = profile.GetSquadTeamData(squad.Squad);
        log.Part(squad.Outcome == SquadOutcome.Won
            ? $"squad win {HostText.KeyForLog(squad.Squad)} on difficulty {squad.Difficulty} (wins {(known ? before!.Wins : 0)} → {after?.Wins}, best difficulty {after?.HighestCompletedDifficulty})"
            : $"squad loss {HostText.KeyForLog(squad.Squad)} on difficulty {squad.Difficulty} (losses {(known ? before!.Losses : 0)} → {after?.Losses})");
        if (after != null && after.MaxGoalsInOneRun != bestBefore)
        {
            log.Part($"{HostText.KeyForLog(squad.Squad)}'s most goals in a run {after.MaxGoalsInOneRun} by a skater ({LogNames.TextShape(after.MaxGoalsScorer)}; was {bestBefore})"); // a custom team's skater's name is player-written (D127)
        }
        else if (squad.BestScorer.Length > 0)
        {
            log.Note($"{HostText.KeyForLog(squad.Squad)}'s most goals in a run stays {bestBefore} (the shared run's best, a skater ({LogNames.TextShape(squad.BestScorer)}) with {squad.BestGoals}, isn't more)");
        }

        if (!known)
        {
            log.Note($"{HostText.KeyForLog(squad.Squad)} wasn't unlocked on this profile; the shared run's result counts all the same");
        }
    }

    /// <summary>
    /// Runs <paramref name="record"/> with the run's scorers here reading the host's best scorer only (or none): the game
    /// records the squad's "most goals in a run" from them (<c>UpdateMaxGoalsIfAppropriate</c>, on a win and on a loss), and
    /// the run stats held here are a checkpoint behind, or another run's. The list goes back whatever happens; a run stats
    /// state made for the call (none was held) is let go again.
    /// </summary>
    private static void WithBestScorer(CampaignState campaign, SquadEntry squad, Action record)
    {
        RunStatsState? state = campaign.runStatsState;
        bool made = state == null;
        if (made)
        {
            state = UnityEngine.ScriptableObject.CreateInstance(Il2CppType.Of<RunStatsState>()).Cast<RunStatsState>();
            campaign.runStatsState = state;
        }

        Il2CppSystem.Collections.Generic.List<ScorersStats>? held = state!.runScorersStats;
        var best = new Il2CppSystem.Collections.Generic.List<ScorersStats>();
        if (squad.BestScorer.Length > 0)
        {
            best.Add(new ScorersStats { displayName = squad.BestScorer, goals = squad.BestGoals });
        }

        try
        {
            state.runScorersStats = best;
            record();
        }
        finally
        {
            state.runScorersStats = held;
            if (made)
            {
                campaign.runStatsState = null;
                UnityEngine.Object.Destroy(state);
            }
        }
    }

    /// <summary>
    /// The shared run's end for its benchwarmers: the game's own <c>BenchSnapshotService.HandleRunEnding</c> on the run
    /// held here (this PC's copy of the host's: held, or kept and put in place for the batch), so a random share of each
    /// benchwarmer's powerups, and hot and cold, go on this profile's own snapshots, rolled here, with their toasts on this
    /// screen (its patch lets only the applier through while following). The service ends the run its own campaign state
    /// holds: when that isn't the profile's, the run is put in it for the call. Its own profile save is refused by the save
    /// guard; the applier's save after the batch writes the snapshots.
    /// </summary>
    private static void EndRun(ProfileData profile, ApplyLog log)
    {
        CampaignState campaign = profile.campaignState!;
        RunData run = campaign.runData!;
        BenchSnapshotService? service = campaign._benchSnapshotService ?? FromProject<BenchSnapshotService>();
        CampaignState? its = service?._campaignState;
        if (service == null || its == null)
        {
            log.Note("bench snapshots skipped: this game's bench snapshot service isn't there");
            return;
        }

        bool other = its.Pointer != campaign.Pointer;
        RunData? itsRun = its.runData;
        if (other)
        {
            its.runData = run;
        }

        (int Benchwarmers, int Powerups, int Hot, int Cold) before = Snapshots(profile);
        try
        {
            service.HandleRunEnding();
        }
        finally
        {
            if (other)
            {
                its.runData = itsRun;
            }
        }

        (int benchwarmers, int powerups, int hot, int cold) = Snapshots(profile);
        log.Part($"bench snapshots at the run's end, rolled here: {benchwarmers} benchwarmers' kept ({powerups} powerups, {hot} hot, {cold} cold; " +
            $"were {before.Benchwarmers}, {before.Powerups}, {before.Hot}, {before.Cold}){(other ? "; the snapshot service's own campaign state held the run for it" : string.Empty)}");
    }

    /// <summary>This profile's bench snapshots, counted: benchwarmers, powerups kept, hot, cold.</summary>
    private static (int Benchwarmers, int Powerups, int Hot, int Cold) Snapshots(ProfileData profile)
    {
        Il2CppSystem.Collections.Generic.Dictionary<string, BenchSnapshotData>? snapshots = profile.benchSnapshots;
        int count = 0, powerups = 0, hot = 0, cold = 0;
        if (snapshots == null)
        {
            return (0, 0, 0, 0);
        }

        Il2CppSystem.Collections.Generic.Dictionary<string, BenchSnapshotData>.Enumerator items = snapshots.GetEnumerator();
        while (items.MoveNext())
        {
            BenchSnapshotData? data = items.Current.Value;
            if (data == null)
            {
                continue;
            }

            count++;
            powerups += data.powerupIds?.Count ?? 0;
            hot += data.hot ? 1 : 0;
            cold += data.cold ? 1 : 0;
        }

        return (count, powerups, hot, cold);
    }

    /// <summary>
    /// A mark the host's game made on its profile, made on this profile through the game's own calls: a Nurse
    /// procedure offered (<c>NurseProcedures.MarkOffered</c>, which takes only this game's procedures, once), an event NPC's
    /// line seen (<c>SeenDialogues.MarkSeen</c>; its patch lets the applier through while following). Saved with the batch.
    /// </summary>
    private static void Mark(ProfileData profile, ProfileMarkEntry mark, ApplyLog log)
    {
        if (mark.Mark == ProfileMark.NurseOffered)
        {
            var procedure = (NurseProcedureState.Procedure)mark.Procedure;
            Il2CppSystem.Collections.Generic.List<int>? offered = profile.offeredNurseProcedures;
            if (offered == null)
            {
                log.Note($"Nurse procedure {procedure} not marked offered: this profile has no list of them");
                return;
            }

            log.Part(NurseProcedures.MarkOffered(offered, procedure)
                ? $"Nurse procedure {procedure} marked offered (this profile's own Nurse won't offer it again)"
                : $"Nurse procedure {procedure} already offered on this profile (or not one of this game's)");
            return;
        }

        SeenDialogues? seen = profile.seenDialogues;
        if (seen == null)
        {
            log.Note($"dialogue {HostText.KeyForLog(mark.Dialogue)} not marked seen: this profile has no seen dialogues");
            return;
        }

        bool already = seen.IsSeen(mark.Dialogue);
        seen.MarkSeen(mark.Dialogue);
        log.Part(already ? $"dialogue {HostText.KeyForLog(mark.Dialogue)} already seen on this profile"
            : $"dialogue {HostText.KeyForLog(mark.Dialogue)} marked seen (heard from the host's NPC; this profile's own won't say it again)");
    }

    /// <summary>
    /// Runs <paramref name="record"/> with the run timer here reading the host's run time: the squad's fastest win is the
    /// shared run's time, not however long this PC's own timer happened to run.
    /// </summary>
    private static void WithRunTime(CampaignState campaign, float hostSeconds, Action record)
    {
        RunTimer? timer = campaign._runTimer;
        if (timer == null)
        {
            record();
            return;
        }

        bool running = timer._isRunning;
        float stopped = timer._stoppedElapsed;
        try
        {
            timer._isRunning = false;
            timer._stoppedElapsed = 0f;
            float banked = campaign.GetTotalRunTime();
            timer._stoppedElapsed = Math.Max(0f, hostSeconds - banked);
            record();
        }
        finally
        {
            timer._isRunning = running;
            timer._stoppedElapsed = stopped;
        }
    }

    /// <summary>
    /// An entry saying the host's game showed the golfer's comic. This build's hosts send none (the game's
    /// <c>RunComicSequence</c> inlines the <c>MarkSeen</c> that recorded it, so its patch never fired, and it was removed); the
    /// kind stays on the wire. This only says which strips of this profile are waiting to be revealed.
    /// </summary>
    private static void ApplyComics(ApplyLog log)
    {
        int count = 3; // the game's StripCount (Oct 29 build), if it can't say
        try
        {
            count = Math.Clamp(GolferComicProgress.StripCount, 1, 31);
        }
        catch (Exception)
        {
            // a constant the interop may not read: the build's own value stands
        }

        var states = new Il2CppStructArray<GolferComicStripState>(count);
        GolferComicProgress.GetStates(states, GolferOutroSettings.Load());
        var unseen = Enumerable.Range(0, count).Where(i => states[i] == GolferComicStripState.NewlyUnlocked).Select(i => i + 1).ToList();
        if (unseen.Count > 0)
        {
            log.Part($"comic strip{(unseen.Count == 1 ? string.Empty : "s")} {string.Join(", ", unseen)} unlocked on this profile and not seen yet " +
                "(revealed at your own next golfer outro)");
        }
    }

    private void PollHistory(long now, bool force)
    {
        if (_history == null)
        {
            return;
        }

        UniTaskStatus status = StatusOf(_history);
        bool timedOut = now - _history.StartedMicros > HistoryTimeoutMicros;
        if (status == UniTaskStatus.Pending && !timedOut && !force)
        {
            return;
        }

        _history.Allowance.Dispose();
        if (status == UniTaskStatus.Succeeded)
        {
            RunHistoryData run = _history.Entry;
            _runEnds.HistorySaved();
            ModLog.Info($"Progress: the shared run {HostText.KeyForLog(run.RunId)} ({run.Outcome}; squad {HostText.KeyForLog(run.SquadId)}, difficulty {run.DifficultyLevel}) is in this PC's run history (batch {_history.Batch}).");
        }
        else
        {
            ModLog.Warn($"Progress: the shared run's history entry {(status == UniTaskStatus.Pending ? (force ? "was still saving when the session ended" : "took too long to save") : $"didn't save ({status})")}; " +
                "its files aren't let through any more.");
        }

        _history = null;
    }

    /// <summary>
    /// The session ends with the run history entry still saving (the game's save goes on over frames: <c>RunHistoryRepository.SaveEntry</c>
    /// yields first). Its files stay let through until it's done: the guard may hold on until this game's title screen
    /// (<see cref="RunSync.StopFollowing"/>), and refused, the entry the ledger calls saved would never reach the disk.
    /// </summary>
    private void LeaveHistorySaving()
    {
        PendingHistory? history = _history;
        if (history == null || StatusOf(history) != UniTaskStatus.Pending)
        {
            return;
        }

        _history = null;
        if (!_watchingSaves)
        {
            _watchingSaves = true;
            MelonLoader.MelonEvents.OnUpdate.Subscribe(WatchSaveAfterSession);
        }

        _savingAfterSession?.Allowance.Dispose();
        _savingAfterSession = history;
        ModLog.Info($"Progress: the shared run {HostText.KeyForLog(history.Entry.RunId)}'s history entry is still saving as the session ends; its files stay let through until it's done.");
    }

    /// <summary>Every frame once a session left a history save running: its allowance goes when the save is done, or at its limit. Never throws.</summary>
    private static void WatchSaveAfterSession()
    {
        PendingHistory? history = _savingAfterSession;
        if (history == null)
        {
            return;
        }

        try
        {
            UniTaskStatus status = StatusOf(history);
            if (status == UniTaskStatus.Pending && FrameClock.NowMicros - history.StartedMicros <= HistoryTimeoutMicros)
            {
                return;
            }

            _savingAfterSession = null;
            history.Allowance.Dispose();
            if (status == UniTaskStatus.Succeeded)
            {
                ModLog.Info($"Progress: the shared run {HostText.KeyForLog(history.Entry.RunId)} is in this PC's run history (batch {history.Batch}; saved just after the session ended).");
            }
            else
            {
                ModLog.Warn($"Progress: the shared run's history entry {(status == UniTaskStatus.Pending ? "took too long to save" : $"didn't save ({status})")} after the session ended; its files aren't let through any more.");
            }
        }
        catch (Exception ex)
        {
            _savingAfterSession = null;
            ModLog.Warn($"Progress: watching the shared run's history entry after the session failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static UniTaskStatus StatusOf(PendingHistory history)
    {
        try
        {
            return history.Task.Status;
        }
        catch (Exception)
        {
            return UniTaskStatus.Faulted;
        }
    }

    private static void ApplyQuest(BenchQuestSystem quests, QuestEntry quest, ApplyLog log)
    {
        switch (quest.Step)
        {
            case QuestStep.MatchEnded:
                // The Mark Bench check reads the match's lineup (GameManager's team), long gone here when a batch applies
                // (it threw on every match). What it decides is the shared run's own flag
                // (RunData.benchQuestData.markBenchQuestValid), which comes with the host's checkpoint: the host checks it
                // for every player (ProgressPatches.CheckMarkBenchForEveryone), and this profile's own quest reads it when
                // the run is won (OnRunWon).
                break;
            case QuestStep.RunWon:
                quests.OnRunWon();
                log.Part("bench quests: run won");
                break;
            case QuestStep.RewardAssigned:
                quests.OnRewardAssigned(null);
                break;
            case QuestStep.NewAct:
                quests.OnNewActCreated(quest.Act);
                break;
            case QuestStep.RunFailed:
                quests.OnRunFailed();
                log.Part("bench quests: run over");
                break;
        }
    }

    /// <summary>Bench quests this profile finished since the last batch (in a match, from the mirrored events, or here).</summary>
    private void NoteQuests(ProfileData profile, ApplyLog log)
    {
        bool[] now = QuestsDone(profile.benchQuestData);
        for (int i = 0; i < now.Length && i < _questsDone.Length; i++)
        {
            if (now[i] && !_questsDone[i])
            {
                log.Part($"bench quest {QuestNames[i]} completed");
            }
        }

        _questsDone = now;
    }

    /// <summary>
    /// This game's own collection achievements (lifetime hat tricks, every squad won, the bench quests, every free agent,
    /// every reward), checked on this profile now rather than at its next unlock or start-up.
    /// </summary>
    private void CheckAchievements(ApplyLog log)
    {
        try
        {
            if (_achievements == null || _achievements.WasCollected)
            {
                _achievements = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<AchievementManager>(), UnityEngine.FindObjectsSortMode.None)
                    .FirstOrDefault()?.TryCast<AchievementManager>();
            }

            _achievements?.CheckRepurposedAchievements();
        }
        catch (Exception ex)
        {
            log.Note($"achievements not checked ({ex.GetType().Name})");
        }
    }

    // ------------------------------------------------------------------ the golfer comic's marks

    /// <summary>
    /// This PC's own last squad and finished quests, and the comic's marks on disk, are what its profile holds now, once it has
    /// loaded (following began, or it loaded since): a profile still loading holds the class's defaults, and a save would
    /// write its last squad back as none.
    /// </summary>
    private void NoteOwnProfile(ProfileData? profile)
    {
        if (profile is { IsLoaded: true })
        {
            _ownSquad = profile.lastSelectedSquad;
            _haveOwnSquad = true;
            _questsDone = QuestsDone(profile.benchQuestData);
            _comic.OnDisk(profile.golferComicStripsSeen);
        }
    }

    /// <summary>
    /// The session ends: the golfer comic's strips this profile marked seen while following (in memory only: the game's inline
    /// save of them is refused by the save guard) are written once, through the guard's allowance for this PC's own profile
    /// (<see cref="Save"/> with the profile alone: this player's squad and bench quest counts put back first, as every
    /// allowed save does, and the stats left alone). Shared progress's own saves write them whenever one comes after the outro;
    /// this covers a session with none: ShareProgress off, or one that ended mid-outro with nothing left to apply. Marks are the
    /// player's own viewing, not the host's credit, so ShareProgress doesn't decide it. Not while the game is closing (its own
    /// quit save follows), while the profile hasn't loaded, or while a restore waits for the game's restart. Never throws.
    /// </summary>
    private void SaveComicMarks(bool gameClosing)
    {
        try
        {
            ProfileData? profile = ProfileData.Instance;
            if (profile == null)
            {
                return;
            }

            bool savable = !profile._loadFaulted && !profile.isCheating && !SaveBackups.RestartNeeded;
            ComicMarkVerdict verdict = _comic.Judge(profile.golferComicStripsSeen, profile.IsLoaded, savable);
            if (verdict.NewStrips.Count == 0)
            {
                return;
            }

            string strips = string.Join(", ", verdict.NewStrips);
            string setting = ModSettings.ShareProgress ? "on" : "off";
            if (!verdict.Owed || gameClosing)
            {
                ModLog.Info($"Progress: the golfer comic's strip(s) {strips} are marked seen in this game's memory and not on disk; not written as the session ends " +
                    $"({(gameClosing ? "the game is closing, with no game calls now" : verdict.Why)}); this game's next own save that gets through the save guard writes them" +
                    $"{(gameClosing ? " (its quit save, if the guard has let go by then; else the comic's reveal shows again next time)" : string.Empty)}.");
                return;
            }

            string result = Save(profile, null, profileOnly: true, why: "the golfer comic's marks");
            if (result == "saved the profile")
            {
                ModLog.Info($"Progress: the golfer comic's strip(s) {strips}, marked seen here while following, were written to this PC's profile as the session ended " +
                    $"(the game's own save of them is refused while following; shared progress is {setting}: it decides what {LogNames.HostTag()}'s play credits, not what this player saw).");
            }
            else
            {
                ModLog.Warn($"Progress: the golfer comic's strip(s) {strips} are marked seen here but weren't written as the session ended ({result}); this game's next own save writes them.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Progress: couldn't write the golfer comic's marks as the session ended", ex);
        }
    }

    // ------------------------------------------------------------------ saving

    /// <summary>
    /// Saves this PC's own profile and stats (the save guard lets those two files through, never the run file). The last
    /// squad and the bench quests' per-run counts in the file stay this PC's own (noted at the session's start, even when
    /// there's none): the host's squad, which loading the host's run puts in memory, and the shared run's counts are live
    /// in memory only. What this PC's own screens marked while following (rewards discovered, dialogues seen, nurse
    /// procedures offered: "new" badges and first-time lines) is saved as it is: this player saw
    /// them, as on the couch. The stats list is marked changed first, so the game's save writes it whole
    /// (<see cref="StatsFileWrites"/>). Returns what happened, for the log: whether each file really changed on disk (the save
    /// guard or the game can refuse a write and still report success).
    /// </summary>
    private string Save(ProfileData profile, StatManager? manager, bool profileOnly = false, string why = "shared progress")
    {
        bool profileSkipped = profile._loadFaulted || profile.isCheating;
        string profileFile = Path.GetFileName(profile.fileName ?? string.Empty);
        profileFile = profileFile.Length > 0 ? profileFile : "profileV2.es3";
        BenchQuestProfileData? quests = profile.benchQuestData;
        string? squad = profile.lastSelectedSquad;
        int[]? sharedQuest = _ownQuest != null && quests != null ? ReadQuest(quests) : null;
        string? failed = null;
        bool statsWritten, profileWritten;
        using (SaveAllowance allowance = profileOnly ? SaveGuard.Allow(why, profileFile) : SaveGuard.Allow(why, profileFile, StatsFile))
        {
            try
            {
                if (_haveOwnSquad)
                {
                    profile.lastSelectedSquad = _ownSquad;
                }

                if (sharedQuest != null)
                {
                    WriteQuest(quests!, _ownQuest!);
                }

                if (!profileOnly)
                {
                    // The Nov 6 build saves only what its own calls marked changed, and shared progress marks nothing: the
                    // stats list is marked, so this save writes it whole (with every entity key filed into the file's cache).
                    StatsFileWrites.MarkListChanged(manager?.StatRepository, $"saving {why}");
                    manager?.Save();
                }

                profile.Save();
            }
            catch (Exception ex)
            {
                failed = $"{ModLog.SafeErrorText(ex)}";
            }
            finally
            {
                profile.lastSelectedSquad = squad;
                if (sharedQuest != null)
                {
                    WriteQuest(quests!, sharedQuest);
                }
            }

            // The game saves both synchronously: a file unchanged now wasn't written (refused on its way, or failed).
            statsWritten = profileOnly || allowance.Written(StatsFile);
            profileWritten = allowance.Written(profileFile);
        }

        if (profileWritten)
        {
            _comic.OnDisk(profile.golferComicStripsSeen); // this profile's marks are on disk with it
        }

        if (profileOnly)
        {
            return profileWritten ? "saved the profile" : profileSkipped ? "not saved (the game won't save a profile whose last load failed, or with cheats on)"
                : $"NOT written to disk ({failed ?? "the write was refused or failed on its way"})";
        }

        if (statsWritten && (profileWritten || profileSkipped))
        {
            return profileSkipped ? "saved stats only (the game won't save a profile whose last load failed, or with cheats on)" : "saved profile and stats";
        }

        var unwritten = new List<string>();
        if (!statsWritten)
        {
            unwritten.Add("stats");
        }

        if (!profileWritten && !profileSkipped)
        {
            unwritten.Add("profile");
        }

        string line = $"{string.Join(" and ", unwritten)} NOT written to disk ({(failed ?? "the write was refused or failed on its way")}; the progress is in this " +
            "game's memory and goes to disk with its next save that gets through)";
        Note($"unwritten {string.Join("+", unwritten)}", $"Progress: this PC's {string.Join(" and ", unwritten)} weren't written after applying {LogNames.HostTag()}'s progress " +
            $"({(failed ?? "the write was refused or failed on its way")}); see the save guard's lines. Logged once; each batch's line says it again.");
        return line;
    }

    // ------------------------------------------------------------------ the shared run's bench quest counts

    /// <summary>
    /// The bench quests' per-run counts belong to a run. When this game holds a shared run, this PC's own run's counts go
    /// aside and the shared run's come in (zero, or kept from an earlier session of the same co-op run); the quest system
    /// counts the mirrored in-match events against them.
    /// </summary>
    private void TrackSharedRun()
    {
        try
        {
            TrackSharedRunCore();
        }
        catch (Exception ex)
        {
            // Logged once: batches still apply (their quest steps count on whatever counts are live).
            Note("shared run", $"Progress: following the shared run's bench quest counts failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private void TrackSharedRunCore()
    {
        ProfileData? profile = ProfileData.Instance;
        RunData? runData = profile?.campaignState?.runData;
        string? held = runData?.runID;
        string hostRun = _relay.View?.Text(Details.Run) is { Length: > 0 } viewed ? viewed : _knownHostRun;
        bool hostsRun = !string.IsNullOrEmpty(held) && ProgressLimits.IsPlausibleId(held) && (RunSync.Instance?.HoldingHostRun == true || held == hostRun);
        if (hostsRun)
        {
            // Kept (the newest load of it) for a run's end that applies after this run is replaced.
            _applier.Held(held!);
            _kept.Keep(held!, runData!);
        }

        if (!ModSettings.ShareProgress || profile?.benchQuestData is not BenchQuestProfileData quests || !hostsRun || held == _sharedRun)
        {
            return;
        }

        if (_ownQuest == null)
        {
            _ownQuest = ReadQuest(quests);
        }
        else if (_sharedRun.Length > 0)
        {
            _ledger.SetRun(_sharedRun, ReadQuest(quests));
        }

        bool kept = _ledger.TryGetRun(held!, out int[] counts);
        WriteQuest(quests, kept ? counts : new int[ProgressLedger.RunCounters]);
        _sharedRun = held!;
        SaveLedger();
        ModLog.Info($"Progress: the shared run's bench quest counts are live here{(kept ? " (kept from an earlier session of this run)" : string.Empty)}; " +
            "this PC's own run's are kept aside until the session ends.");
    }

    /// <summary>The shared run's bench quest counts go in the ledger at every batch too, so a game that stops mid-session finds them again.</summary>
    private void KeepSharedQuestCounts(ProfileData profile)
    {
        try
        {
            if (_ownQuest != null && _sharedRun.Length > 0 && profile.benchQuestData is BenchQuestProfileData quests)
            {
                _ledger.SetRun(_sharedRun, ReadQuest(quests));
                SaveLedger();
            }
        }
        catch (Exception ex)
        {
            Note("quest counts", $"Progress: the shared run's bench quest counts couldn't be kept in the ledger ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>The bench quests' five per-run counts, in the ledger's order.</summary>
    internal static int[] ReadQuest(BenchQuestProfileData quests) => new[]
    {
        quests.brewsterAbilityUsesThisRun, quests.kovalskiGoalsThisRun, quests.cassouletKnockoutsThisRun,
        quests.kirbyHighestCritPercentThisRun, quests.sallyEnragedGoalsThisRun,
    };

    /// <summary>Sets the bench quests' five per-run counts (the ledger's order).</summary>
    internal static void WriteQuest(BenchQuestProfileData quests, IReadOnlyList<int> counts)
    {
        quests.brewsterAbilityUsesThisRun = counts[0];
        quests.kovalskiGoalsThisRun = counts[1];
        quests.cassouletKnockoutsThisRun = counts[2];
        quests.kirbyHighestCritPercentThisRun = counts[3];
        quests.sallyEnragedGoalsThisRun = counts[4];
    }

    private static bool[] QuestsDone(BenchQuestProfileData? quests) => quests == null ? Array.Empty<bool>() : new[]
    {
        quests.isBoringDudeCompleted, quests.isBrewsterCompleted, quests.isKovalskiCompleted, quests.isMarkBenchCompleted,
        quests.isCassouletCompleted, quests.isKirbyCompleted, quests.isStumpleCompleted, quests.isSallyCompleted,
    };

    // ------------------------------------------------------------------ the game's services and values

    private static StatManager? Stats() => FromProject<StatManager>();

    private static UnlockSystem? Unlocks(StatManager? manager) => FromProject<UnlockSystem>() ?? manager?._unlockSystem;

    /// <summary>A service the game binds in its project injector, by type.</summary>
    internal static T? FromProject<T>()
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<T>());
        return found?.TryCast<T>();
    }

    /// <summary>Sets the difficulty manager to <paramref name="index"/> when it holds another; returns the one to put back (or null).</summary>
    private static DifficultyLevel? AlignDifficulty(int index, out bool aligned)
    {
        aligned = false;
        DifficultyManager? manager = DifficultyManager._instance;
        DifficultyLevel? current = manager?.currentDifficulty;
        if (manager == null || current?.index == index)
        {
            return null;
        }

        DifficultyLevel? wanted = DifficultyManager.GetDifficulty(index);
        if (wanted == null)
        {
            return null;
        }

        manager.currentDifficulty = wanted;
        aligned = true;
        return current;
    }

    private static void PutBack(DifficultyLevel? previous)
    {
        if (previous != null && DifficultyManager._instance is DifficultyManager manager)
        {
            manager.currentDifficulty = previous;
        }
    }

    /// <summary>The value boxed as the game's stat methods take it (an Int32 or a Single: their unbox is exact).</summary>
    internal static Il2CppSystem.Object Box(StatValue value)
    {
        bool real = value.Kind == StatValueKind.Real;
        IntPtr klass = real ? Il2CppClassPointerStore<float>.NativeClassPtr : Il2CppClassPointerStore<int>.NativeClassPtr;
        if (klass == IntPtr.Zero)
        {
            throw new InvalidOperationException("the runtime's number class isn't known yet");
        }

        System.Runtime.InteropServices.Marshal.WriteInt32(Scratch, real ? BitConverter.SingleToInt32Bits(value.RealValue) : value.WholeValue);
        return new Il2CppSystem.Object(IL2CPP.il2cpp_value_box(klass, Scratch));
    }

    /// <summary>A boxed Int32 or Single as a number; NaN for anything else (null, a text stat's value).</summary>
    internal static double Number(Il2CppSystem.Object? boxed)
    {
        if (boxed == null)
        {
            return double.NaN;
        }

        IntPtr klass = boxed.ObjectClass;
        return klass == Il2CppClassPointerStore<int>.NativeClassPtr ? boxed.Unbox<int>()
            : klass == Il2CppClassPointerStore<float>.NativeClassPtr ? boxed.Unbox<float>()
            : double.NaN;
    }

    /// <summary>
    /// The files the game's run history save may write: its index and every slot it has (the save picks one itself, and a
    /// slot left out would be refused without a word while the index named the run), by the game's own names.
    /// </summary>
    private static string[] HistoryFiles()
    {
        try
        {
            return RunHistoryFileNames.All(RunHistoryFiles.Index, RunHistoryFiles.MaxRuns, RunHistoryFiles.Slot);
        }
        catch (Exception)
        {
            // The game's own names and slots (State.RunHistory.RunHistoryFiles, Nov 6 build) if it can't say.
            return RunHistoryFileNames.All(null, RunHistoryFileNames.Slots, null);
        }
    }

    /// <summary>
    /// The ledger at <paramref name="path"/>. With none there, the temporary copy a save left when it stopped between writing
    /// it and moving it in (<see cref="CampaignFiles.PartSuffix"/>, or <see cref="OldPartSuffix"/> from 0.10.0 and before) is
    /// read instead: it's newer than nothing, and an empty ledger would apply every resent batch again.
    /// </summary>
    private static ProgressLedger LoadLedger(string path)
    {
        try
        {
            string? from = File.Exists(path) ? path
                : File.Exists(path + CampaignFiles.PartSuffix) ? path + CampaignFiles.PartSuffix
                : File.Exists(path + OldPartSuffix) ? path + OldPartSuffix
                : null;
            if (from != null && from != path)
            {
                ModLog.Warn($"Progress: the ledger of applied progress was missing, but a save of it that didn't finish is there ({Path.GetFileName(from)}); it's read instead.");
            }

            return from != null ? ProgressLedger.Parse(File.ReadAllText(from)) : new ProgressLedger();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Progress: the ledger of applied progress couldn't be read ({CampaignFiles.ErrorText(ex)}); starting a new one.");
            return new ProgressLedger();
        }
    }

    private void SaveLedger()
    {
        if (!_ledger.Changed || NativeLobby.TourHolds.HoldsFile("Progress", "the ledger of applied progress"))
        {
            return; // a check tour's batches stay in memory: the player's ledger says nothing of them
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
            string temporary = _ledgerPath + CampaignFiles.PartSuffix;
            File.WriteAllText(temporary, _ledger.Format());
            File.Move(temporary, _ledgerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Note("ledger", $"Progress: the ledger of applied progress couldn't be saved ({CampaignFiles.ErrorText(ex)}); a batch resent after a restart could apply twice.");
            return;
        }

        try
        {
            if (File.Exists(_ledgerPath + OldPartSuffix))
            {
                File.Delete(_ledgerPath + OldPartSuffix); // left by 0.10.0 or before: the ledger just saved holds all it could
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saved all the same; the start-up sweep removes it next time.
        }
    }

    private void Note(string key, string line)
    {
        if (_noted.Add(key))
        {
            ModLog.Warn(line);
        }
    }

    /// <summary>The run history save in flight: its task, the save guard allowance it holds, when it started, and the entry with the batch that brought it.</summary>
    private sealed record PendingHistory(UniTask Task, IDisposable Allowance, long StartedMicros, RunHistoryData Entry, uint Batch);

    /// <summary>What one batch changed here, for its log line.</summary>
    private sealed class ApplyLog : IProgressLog
    {
        private readonly string _host;
        private readonly ProgressBatch _batch;
        private readonly List<int> _order = new();
        private readonly Dictionary<(int Stat, StatBucket Bucket), (double Before, double After, bool Record)> _stats = new();
        private readonly List<string> _unlocked = new();
        private readonly List<string> _parts = new();
        private readonly List<string> _notes = new();
        private readonly List<string> _derived = new();
        private readonly HashSet<(int Stat, StatBucket Bucket)> _changed = new();
        private readonly Dictionary<EntityStatGroup, (int Updated, int Added)> _entities = new();
        private readonly SortedSet<string> _failedKinds = new(StringComparer.Ordinal);
        private string _firstFailure = string.Empty;
        private string _firstKeyProblem = string.Empty;
        private int _keysWritten;
        private int _keysUnwritten;
        private int _unknown;
        private int _mismatched;
        private int _refused;

        public ApplyLog(string host, ProgressBatch batch)
        {
            _host = host;
            _batch = batch;
        }

        /// <summary>The batch's number.</summary>
        public uint Sequence => _batch.Sequence;

        /// <summary>What the batch unlocked here.</summary>
        public IReadOnlyList<string> UnlockedNames => _unlocked;

        /// <summary>The batch's entries that failed here.</summary>
        public int FailedEntries { get; private set; }

        /// <summary>Those entries in words: their kinds, then the first failure.</summary>
        public string FailedWords => $"{string.Join(", ", _failedKinds)}; first: {_firstFailure}";

        /// <summary>One operation's effect on one of a stat's values (each value is followed on its own).</summary>
        public void Stat(int stat, StatBucket bucket, double before, double after, bool record)
        {
            if (!_order.Contains(stat))
            {
                _order.Add(stat);
            }

            _stats[(stat, bucket)] = _stats.TryGetValue((stat, bucket), out var seen) ? (seen.Before, after, seen.Record || record) : (before, after, record);
        }

        /// <summary>The values its stat operations changed here (for the derived stats).</summary>
        public IReadOnlyCollection<(int Stat, StatBucket Bucket)> ChangedStats => _changed;

        /// <summary>A stat operation applied: the values it changes.</summary>
        public void Changed(StatEntry entry)
        {
            foreach ((int Stat, StatBucket Bucket) change in ProgressApplyPolicy.Changes(entry))
            {
                _changed.Add(change);
            }
        }

        /// <summary>A derived stat worked out again ("GamesWonLost.Campaign 9W-1L").</summary>
        public void Derived(string text) => _derived.Add(text);

        /// <summary>
        /// An entity's stats updated here (<see cref="EntityApplied.Existed"/>: combined into its own; else added), and what
        /// became of its key in the stats file.
        /// </summary>
        public void Entity(EntityStatGroup group, EntityApplied applied)
        {
            (int updated, int added) = _entities.GetValueOrDefault(group);
            _entities[group] = applied.Existed ? (updated + 1, added) : (updated, added + 1);
            if (applied.Key == EntityKeyWrite.Written)
            {
                _keysWritten++;
            }
            else if (applied.Key == EntityKeyWrite.Failed && _keysUnwritten++ == 0)
            {
                _firstKeyProblem = applied.KeyProblem ?? "no reason given";
            }
        }

        public void UnknownStat() => _unknown++;

        public void Mismatched() => _mismatched++;

        public void Unlocked(string id) => _unlocked.Add(SafeText.Clean(id));

        public void Part(string text) => _parts.Add(text);

        public void Note(string text) => _notes.Add(text);

        /// <summary>A stat operation refused: it would wrap the value past its largest, or go over the session's cap or what this PC's play with the host explains for the stat.</summary>
        public void Refused() => _refused++;

        public void Failed(ProgressEntry entry, Exception exception)
        {
            string kind = entry.GetType().Name.Replace("Entry", string.Empty, StringComparison.Ordinal);
            _notes.Add($"{kind} failed ({ModLog.SafeErrorText(exception)})");
            _failedKinds.Add(kind);
            if (FailedEntries++ == 0)
            {
                _firstFailure = $"{ModLog.SafeErrorText(exception)}";
            }
        }

        /// <summary>The batch's line; <paramref name="saved"/> says what the save after it did.</summary>
        public string Line(string saved)
        {
            var parts = new List<string>();
            int kept = 0, shown = 0;
            foreach (int stat in _order)
            {
                // The campaign value is what every unlock threshold reads; a stat the batch only touched elsewhere shows that.
                if (!_stats.TryGetValue((stat, StatBucket.Campaign), out var shownValue) && !_stats.TryGetValue((stat, StatBucket.Lifetime), out shownValue))
                {
                    shownValue = _stats[(stat, StatBucket.PlayNow)];
                }

                (double before, double after, bool record) = shownValue;
                string name = ((StatType)stat).ToString();
                if (before.Equals(after))
                {
                    kept += record ? 1 : 0;
                    continue;
                }

                if (shown++ < ShownStats)
                {
                    parts.Add(record ? $"{name} record {Show(after)} (was {Show(before)})" : $"{name} {(after > before ? "+" : string.Empty)}{Show(after - before)} (now {Show(after)})");
                }
            }

            if (shown > ShownStats)
            {
                parts.Add($"{shown - ShownStats} more stats changed");
            }

            if (kept > 0)
            {
                parts.Add($"{kept} record{(kept == 1 ? string.Empty : "s")} kept (yours already higher)");
            }

            if (_unknown + _mismatched > 0)
            {
                parts.Add($"{_unknown + _mismatched} stat operations this game can't apply");
            }

            if (_refused > 0)
            {
                parts.Add($"{_refused} stat operation{(_refused == 1 ? string.Empty : "s")} refused (past a stat's largest value, this session's cap for it, or what this PC's play with the host explains)");
            }

            if (_unlocked.Count > 0)
            {
                parts.Add($"unlocked {string.Join(", ", _unlocked)}");
            }

            if (_entities.Count > 0)
            {
                parts.Add("entity stats: " + string.Join(", ", _entities.Select(e =>
                    $"{e.Key} {e.Value.Updated + e.Value.Added}{(e.Value.Added > 0 ? $" ({e.Value.Added} new here)" : string.Empty)}")));
            }

            // Each entity's key in the stats file (Nov 6 build), written for the save below to put on disk.
            if (_keysWritten > 0)
            {
                parts.Add($"{_keysWritten} entity key{(_keysWritten == 1 ? string.Empty : "s")} written");
            }

            if (_keysUnwritten > 0)
            {
                parts.Add($"{_keysUnwritten} entity key{(_keysUnwritten == 1 ? string.Empty : "s")} NOT written (first: {_firstKeyProblem}; " +
                    "the update is in this game's memory; the file keeps that entity as it was until this game's own play changes it again)");
            }

            if (_derived.Count > 0)
            {
                parts.Add($"worked out again from this profile's own counts: {string.Join(", ", _derived)}");
            }

            parts.AddRange(_parts);
            parts.AddRange(_notes);
            parts.Add(saved);
            string head = $"Progress from {_host} (batch {_batch.Sequence}, {_batch.Entries.Count} entries)";
            return parts.Count == 1 && _stats.Count == 0 ? $"{head}: nothing changed here, {parts[0]}." : $"{head}: {string.Join(", ", parts)}.";
        }

        /// <summary>
        /// Every value the batch changed, each bucket on its own (lifetime, campaign, play-now: before → after), or empty when
        /// none did; <see cref="Line"/> shows only the first few, and this lets a log be checked against the host's stats.
        /// </summary>
        public string Detail()
        {
            var parts = new List<string>();
            foreach (int stat in _order)
            {
                foreach (StatBucket bucket in new[] { StatBucket.Lifetime, StatBucket.Campaign, StatBucket.PlayNow })
                {
                    if (_stats.TryGetValue((stat, bucket), out var value) && !value.Before.Equals(value.After))
                    {
                        parts.Add($"{(StatType)stat}.{bucket} {Show(value.Before)}→{Show(value.After)}");
                    }
                }
            }

            return parts.Count == 0 ? string.Empty : $"Progress detail (batch {_batch.Sequence}): {string.Join(", ", parts)}.";
        }

        private static string Show(double value) => value.ToString(value % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);
    }
}
#endif
