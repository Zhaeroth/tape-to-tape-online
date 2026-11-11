#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// Shared progress (D42): profile progress counts for every player, from their own profile, at their own
/// thresholds, as if each were playing it on their own account.
/// <list type="bullet">
/// <item>Host (<see cref="ProgressHost"/>): what the host's game credits in the campaign (stat operations, unlock
/// checks, the squad record, comics, the run history entry, bench quest steps outside matches) is recorded in order
/// and sent to every friend in numbered batches at safe points (<see cref="ProgressPatches"/>).</item>
/// <item>Friend (<see cref="ProgressFriend"/>): each batch is applied once, at a safe point (never while the host's
/// match plays here), to the friend's own stats and profile through the game's own calls; the friend's own game then
/// runs the same unlock checks on its own profile at its own thresholds, raises its own toasts and achievements, and
/// saves its own profile and stats. The friend's own game credits nothing by itself meanwhile.</item>
/// <item>Bench quests count on the friend's own quest system: the event channel delivers the host's in-match events to
/// it (<see cref="Streaming.EventChannel.AlsoDeliverTo"/>), and the steps outside matches come in the batches.</item>
/// <item>Only the shared co-op run is shared: a host still playing its solo run (hosting started mid-run) sends
/// nothing.</item>
/// <item>A session that ends early: the host seals the run's end as soon as its game records it, and when hosting stops
/// (F7, leaving, the game closing) whatever it recorded since is sealed and sent to every friend still there before the
/// session closes. On a friend everything that arrived applies as the session ends (the relay stops this module before
/// run sync puts the friend's own run back, D47), so a run's end the host left on its run-end screen still counts. What
/// never arrived comes again if the friend rejoins the same hosting session, and is lost if that hosting session is over
/// (<see cref="ProgressFriend"/>).</item>
/// <item>The host's per-run counters are kept per run file (<see cref="ProgressRunScopes"/>), so its co-op slot and its
/// solo run don't share "this run" and "this act" values.</item>
/// </list>
/// With the ShareProgress setting off, a host records and sends nothing (what its game credits meanwhile is never sent;
/// friends are told), and a friend applies nothing: what arrives is kept, up to <see cref="ProgressInbox.MaxHeld"/>
/// batches, and applies if the setting is turned back on during the same session (it comes again on a rejoin of the
/// same hosting session); the friend's own game still credits nothing by itself, and the host's in-match events don't
/// reach its bench quests. Every method catches its own errors: the relay never has to switch this module off, so a
/// friend's own values are always put back.
/// </summary>
internal sealed class ProgressModule : ICampaignModule
{
    /// <summary>The friend's own quest system, by the event channel's owner name.</summary>
    private const string QuestSystemClass = "Rogue.BenchQuests.BenchQuestSystem";

    /// <summary>Errors of one kind logged in full; after that they're counted (an error every frame mustn't flood the log).</summary>
    private const int ErrorsLogged = 3;

    private static readonly MessageType[] OwnedTypes = { MessageType.ProgressEvent, MessageType.ProgressStatus, MessageType.ProgressRunCopy };
    private static readonly MessageType[] OwnedFromFriends = { MessageType.ProgressResend, MessageType.ProgressRunRequest };
    private static readonly HashSet<string> HeldBackLogged = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Errors = new(StringComparer.Ordinal);
    private readonly CampaignContext _context;
    private readonly Action _updateSides;
    private ProgressHost? _host;
    private ProgressFriend? _friend;
    private int _aliveFrame = int.MinValue / 2;
    private long _heldBack;

    /// <summary>Made once by <see cref="CampaignModules"/>.</summary>
    public ProgressModule(CampaignContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _updateSides = UpdateSides;
        Instance = this;
        Streaming.EventChannel.AlsoDeliverTo = owner => owner == QuestSystemClass && Instance is { DeliversQuestEvents: true };
        Streaming.EventChannel.OwnRaising = () => ProgressFriend.ApplyingNow is { Applying: true };
    }

    /// <summary>The module of the running game, or null before it's made.</summary>
    public static ProgressModule? Instance { get; private set; }

    /// <summary>Host: true while what this PC's game credits is recorded for friends (hosting, sharing on).</summary>
    public static bool Recording => Instance is { _host: not null, Alive: true } && ModSettings.ShareProgress;

    /// <summary>
    /// Friend: true while this PC's own game must not credit progress by itself (following, and not applying), including
    /// while a session that ended waits for a scene load to land (the relay doesn't update the modules then, but the host's
    /// run is still held here).
    /// </summary>
    public static bool HoldingBack => Instance is { _friend: { Applying: false } } module && (module.Alive || CampaignRelay.Current is { Following: true });

    /// <summary>The host side, while hosting (else null).</summary>
    public static ProgressHost? Host => Instance is { Alive: true } module ? module._host : null;

    /// <summary>The friend side, while following (else null).</summary>
    public static ProgressFriend? Friend => Instance is { Alive: true } module ? module._friend : null;

    /// <summary>
    /// Friend: true while shared progress that needs the host's run held here is still waiting (for client mode or the
    /// host's match to end here). Run sync should put this PC's own run back only once this is false (or after a few
    /// seconds), or the squad record, unlock checks and comics of the run's end are skipped.
    /// </summary>
    public static bool RunBoundProgressWaiting
    {
        get
        {
            try
            {
                return Friend?.RunBoundWaiting == true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public string Name => "Progress";

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Owns => OwnedTypes;

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> OwnsFromFriends => OwnedFromFriends;

    /// <summary>
    /// Friend: the shared runs this PC's game held, kept for the game's life (not just a session), so a run's end that
    /// applies after the run was replaced (the next run's checkpoint, the session's end, a rejoin) still has its run.
    /// </summary>
    internal ProgressKeptRuns KeptRuns { get; } = new();

    /// <summary>
    /// Friend: what a hosting session has added to each stat here (D47). Kept here, not per play period: a lobby's hosting session
    /// spans many (<c>ProgressCredit</c> starts again by itself with another session).
    /// </summary>
    internal ProgressCredit Credit { get; } = new();

    /// <summary>Friend: what a hosting session has added to each entity here, as <see cref="Credit"/>.</summary>
    internal EntityCredit EntityCredit { get; } = new();

    /// <summary>
    /// Friend: the host's in-match events reach this PC's own bench quest system (sharing on). Its handlers check the
    /// quest's own conditions on this PC's profile and the shared run, so a quest the host finished still counts here.
    /// </summary>
    private bool DeliversQuestEvents => _friend != null && Alive && ModSettings.ShareProgress;

    /// <summary>
    /// The relay calls <see cref="Update"/> every frame of a session: a module it switched off (or a session that broke)
    /// stops holding back the game's own credit within two frames.
    /// </summary>
    private bool Alive => UnityEngine.Time.frameCount - _aliveFrame <= 2;

    /// <summary>Friend: counts something this PC's own game tried to credit and was held back (logged once per kind).</summary>
    public static void NoteHeldBack(string what)
    {
        if (Instance == null)
        {
            return;
        }

        Instance._heldBack++;
        if (HeldBackLogged.Add(what))
        {
            ModLog.Info($"Progress: held back this PC's own {what} (while following a host, progress counts here only from what the host's game credits; logged once).");
        }
    }

    /// <summary>Friend: an unlock this PC's own game tried to mark while following (logged once per id).</summary>
    public static void NoteHeldBackUnlock(string id)
    {
        if (Instance == null)
        {
            return;
        }

        Instance._heldBack++;
        if (HeldBackLogged.Add($"unlock {id}"))
        {
            ModLog.Warn($"Progress: held back marking {id} as unlocked here: while following, unlocks come only from shared progress checked at this PC's own thresholds.");
        }
    }

    /// <inheritdoc />
    public void StartHosting(CampaignRelay relay) => Guard("starting to host", () =>
    {
        _aliveFrame = UnityEngine.Time.frameCount;
        _host = new ProgressHost(relay, _context.Matches);
    });

    /// <inheritdoc />
    public void StopHosting(string reason) => Guard("stopping hosting", () =>
    {
        _host?.Stop(reason);
        _host = null;
    });

    /// <inheritdoc />
    public void FriendJoined(ulong friend) => Guard("adding a friend", () => _host?.FriendJoined(friend));

    /// <inheritdoc />
    public void FriendLeft(ulong friend) => Guard("removing a friend", () => _host?.FriendLeft(friend));

    /// <inheritdoc />
    public void StartFollowing(CampaignRelay relay) => Guard("starting to follow", () =>
    {
        _aliveFrame = UnityEngine.Time.frameCount;
        _heldBack = 0;
        HeldBackLogged.Clear();
        _friend = new ProgressFriend(relay, _context, KeptRuns, Credit, EntityCredit);
        _friend.Start();
    });

    /// <inheritdoc />
    public void StopFollowing(string reason, bool gameClosing)
    {
        ProgressFriend? friend = _friend;
        if (friend != null && !gameClosing)
        {
            // The relay stops the modules in reverse (D42): this PC still holds the host's run here, and the host's match
            // has stopped playing, so what arrived but waited (e.g. the run's end when the host left on its run-end
            // screen) applies now rather than being lost with the session. The friend side stays the module's until then.
            Guard("applying what waits as the session ends", friend.Flush);
        }

        // Always, whatever the flush did: this PC's own quest counts go back and the ledger is saved.
        _friend = null;
        Guard("stopping following", () => friend?.Stop(reason, _heldBack, gameClosing));
    }

    /// <inheritdoc />
    public bool Receive(MessageType type, ref BitReader reader)
    {
        try
        {
            return type switch
            {
                MessageType.ProgressEvent => _friend != null && _friend.Receive(ref reader),
                MessageType.ProgressStatus => _friend != null && _friend.ReceiveStatus(ref reader),
                MessageType.ProgressRunCopy => _friend != null && _friend.ReceiveRunCopy(ref reader),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            ModLog.Error("Progress: a message from the host couldn't be read", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public bool ReceiveFromFriend(ulong friend, MessageType type, ref BitReader reader)
    {
        try
        {
            return _host != null && type switch
            {
                MessageType.ProgressResend => _host.ReceiveResend(friend, ref reader),
                MessageType.ProgressRunRequest => _host.ReceiveRunRequest(friend, ref reader),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            ModLog.Error("Progress: a friend's request couldn't be read", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void Update()
    {
        _aliveFrame = UnityEngine.Time.frameCount;
        Guard("updating", _updateSides);
    }

    private void UpdateSides()
    {
        _host?.Update();
        _friend?.Update();
    }

    /// <inheritdoc />
    public IEnumerable<string> Status()
    {
        try
        {
            return _friend?.Status() ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Runs <paramref name="action"/>; an error is logged (the first few of each kind in full, then only counted).</summary>
    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            int count = Errors.TryGetValue(what, out int seen) ? seen + 1 : 1;
            Errors[what] = count;
            if (count <= ErrorsLogged)
            {
                ModLog.Error($"Progress: failed while {what}{(count == ErrorsLogged ? $" ({ErrorsLogged} times now; more of these are counted, not logged)" : string.Empty)}", ex);
            }
            else if (count % 1000 == 0)
            {
                ModLog.Warn($"Progress: failed while {what} {count} times so far (the last: {ModLog.SafeErrorText(ex)}).");
            }
        }
    }
}
#endif
