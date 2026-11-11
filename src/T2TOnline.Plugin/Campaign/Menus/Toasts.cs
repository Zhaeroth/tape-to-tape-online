#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2CppTape2Tape.Hockey.UI;
using MelonLoader;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The run's toasts on every friend's screen (v1): the relic triggers, talents
/// gained, power-ups upgraded or lost, stat bonuses, injuries, line changes, mercy and the rest of the 30 places that queue
/// one with <c>NotificationQueue.Queue</c>. Their makers listen to game events client mode detaches, so a
/// friend's game made none of them.
/// <list type="bullet">
/// <item>Host: every toast its game queues is counted by type (logged when its scene changes: how many a match makes). With
/// friends in, each goes out as a <see cref="NotificationCopy"/> (<see cref="ToastCopies"/>; <see cref="MessageType.Notification"/>,
/// channel 1), at most <see cref="ToastThrottle.PerSecond"/> a second, read before the game queues it (a prefix: the pop its
/// queueing may start goes after it); each pop of the queue (<c>PopQueue</c>, a postfix) and each clear
/// (<c>Clear</c>) goes too. Each player's own toasts aren't sent: unlocks (type Unlock), bench quests (timing
/// BenchQuest) and the benchwarmers' at a run's end (timing TitleScreen, <see cref="ToastOwner"/>) are each profile's own, and
/// each friend's game makes its own.</item>
/// <item>Friend: each toast is made again with this PC's objects and queued with the game's own <c>Queue</c>; the host's
/// pops and clears are done with the game's own <c>PopQueue</c> and <c>Clear</c>, so the game's own display and sound show
/// them, in this PC's language where they were made from a term, an object or a recipe (a text the game formats from a term and
/// arguments: an injury's, a line change's; ToastCopies.Formats.cs). While it holds the host's run, this PC's
/// game queues no run toasts of its own (the ones it would make from the host's streamed events, an injury's say, would
/// come twice): only its own unlocks, bench quests and benchwarmers' at a run's end. A toast this PC's game can't make yet
/// but may soon (the host's game queues some as its match loads, about the match's home team, before this PC's copy has
/// started loading) waits for it (<see cref="ToastHold"/>, at most <see cref="ToastHold.HoldSeconds"/>), and a pop that
/// would show it waits behind it; one that can't be made here at all is dropped, logged once per type.</item>
/// </list>
/// Every entry is guarded; after <see cref="MaxErrors"/> failures in a session the friend's side stops (its own toasts are
/// queued again) until the next session. All three patches are on unique, non-generic methods; the toast data is a
/// 0x58-byte struct the native code passes by address, which the interop's detour reads correctly (not a STRUCT ARG).
/// </summary>
internal sealed class Toasts : ICampaignModule
{
    private const int MaxErrors = 5;
    private const int MaxErrorLogs = 3;
    private const int MaxMalformedLogs = 3;

    // A friend's limits on what a host sends (network input is untrusted): well over the host's own 8 a second, for a link
    // catching up after a stall; and the game's queue never grows past this many waiting toasts.
    private const int FriendPerSecond = 16;
    private const int FriendBurst = 64;
    private const int MaxWaitingHere = 128;

    private static readonly MessageType[] Types = { MessageType.Notification };
    private static bool _installed;

    private readonly ToastThrottle _throttle = new();
    private readonly ToastThrottle _friendThrottle = new(FriendPerSecond, FriendBurst);
    private readonly ToastTally _inScene = new();
    private readonly ToastTally _sent = new();
    private readonly ToastTally _skipped = new();
    private readonly ToastTally _unread = new();
    private readonly ToastTally _shown = new();
    private readonly ToastTally _dropped = new();
    private readonly ToastTally _refused = new();
    private readonly ToastTally _waited = new();
    private readonly ToastHold _hold = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private CampaignRelay? _relay;
    private string _scene = string.Empty;
    private string _waitProblem = string.Empty;
    private int _replaying;
    private int _errors;
    private int _malformed;
    private int _pops;
    private int _clears;
    private int _popFrame = -1;
    private int _popTiming;
    private bool _off;

    /// <summary>Made once by <see cref="CampaignModules"/>.</summary>
    public Toasts()
    {
        Instance = this;
    }

    /// <summary>The module of the running game, or null before it's made.</summary>
    public static Toasts? Instance { get; private set; }

    /// <inheritdoc />
    public string Name => "Toasts";

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Owns => Types;

    private bool Hosting => _relay?.Hosting == true;

    private bool Following => _relay?.Following == true && !_off;

    /// <summary>The host shares its run with friends now (some in, its co-op slot's run: D48).</summary>
    private bool Sharing => _relay is { Hosting: true } relay && relay.Friends.Count > 0 && RunSync.Instance?.HostOnCoopSlot == true;

    /// <summary>Installs the three patches (they stay for the game's life, D38, and do nothing outside a session).</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(failed);
        Patch(harmony, failed, "Queue", prefix: nameof(QueuePrefix));
        Patch(harmony, failed, "PopQueue", postfix: nameof(PopPostfix));
        Patch(harmony, failed, "Clear", postfix: nameof(ClearPostfix));
        if (!_installed)
        {
            _installed = true;
            MelonEvents.OnSceneWasLoaded.Subscribe(SceneLoaded);
        }
    }

    /// <inheritdoc />
    public void StartHosting(CampaignRelay relay)
    {
        _relay = relay;
        _off = false;
        _errors = 0;
        _throttle.Reset();
        _sent.Clear();
        _skipped.Clear();
        _unread.Clear();
        _inScene.Clear();
        _logged.Clear();
        _pops = 0;
        _clears = 0;
        _scene = WorldMap.SceneName;
    }

    /// <inheritdoc />
    public void StopHosting(string reason)
    {
        FlushScene("hosting stopped");
        if (_sent.Total + _skipped.Total + _unread.Total + _throttle.Dropped > 0)
        {
            ModLog.Info($"Toasts: stopped hosting ({reason}); sent friends {_sent.Total} toast(s) ({_sent}), {_pops} pop(s) and {_clears} clear(s); " +
                $"kept back {_skipped.Total} of each player's own ({_skipped}); {_unread.Total} couldn't be read ({_unread}); {_throttle.Dropped} over {ToastThrottle.PerSecond} a second.");
        }

        _relay = null;
    }

    /// <inheritdoc />
    public void StartFollowing(CampaignRelay relay)
    {
        _relay = relay;
        _off = false;
        _errors = 0;
        _malformed = 0;
        _friendThrottle.Reset();
        _shown.Clear();
        _dropped.Clear();
        _refused.Clear();
        _waited.Clear();
        _ = _hold.Clear();
        _logged.Clear();
        ToastCopies.Forget();
    }

    /// <inheritdoc />
    public void StopFollowing(string reason, bool gameClosing)
    {
        for (int waiting = _hold.Clear(); waiting > 0; waiting--)
        {
            _ = _dropped.Add("still waiting at the end");
        }

        if (_shown.Total + _dropped.Total + _refused.Total > 0)
        {
            ModLog.Info($"Toasts: stopped following ({reason}); showed {_shown.Total} of {LogNames.HostTag()}'s toast(s) here ({_shown}), {_waited.Total} of them after waiting for this PC's game ({_waited}); " +
                string.Create(CultureInfo.InvariantCulture, $"dropped {_dropped.Total} ({_dropped}); held back {_refused.Total} of this game's own run toasts ({_refused}); sprite index {ToastCopies.SpriteIndexMs:0.0} ms, ") +
                $"{ToastCopies.MissingSpriteCount} image(s) not loaded here.");
        }

        _relay = null;
        ToastCopies.Forget();
    }

    /// <inheritdoc />
    public bool Receive(MessageType type, ref BitReader reader)
    {
        if (_relay?.Following != true || type != MessageType.Notification)
        {
            return false;
        }

        if (!NotificationMessage.TryRead(ref reader, out NotificationEvent? read) || read == null)
        {
            if (++_malformed <= MaxMalformedLogs)
            {
                ModLog.Warn($"Toasts: a malformed toast message from {LogNames.HostTag()} was dropped{(_malformed == MaxMalformedLogs ? " (further ones are only counted)" : string.Empty)}.");
            }

            return false;
        }

        if (!_off)
        {
            Run("showing the host's toast", () => Apply(read));
        }

        return true;
    }

    /// <summary>A scene loaded on this PC: a host's count of the scene it left is logged.</summary>
    private static void SceneLoaded(int index, string name)
    {
        try
        {
            Instance?.FlushScene($"\"{SafeText.Clean(name, 40)}\" loaded");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Toasts: the scene count couldn't be logged ({ex.GetType().Name}).");
        }
    }

    /// <summary>Host: the toasts its game queued in the scene it's leaving, by type (the log line says how many a match makes).</summary>
    private void FlushScene(string why)
    {
        if (Hosting && _inScene.Total > 0)
        {
            ModLog.Info($"Toasts: the host's game queued {_inScene.Total} toast(s) in \"{_scene}\" ({_inScene}){(Sharing ? string.Empty : "; no friend got them (none in, or the host's solo run)")} ({why}).");
        }

        _inScene.Clear();
        _scene = WorldMap.SceneName;
    }

    /// <summary>A toast is being queued on this PC (prefix). False refuses it (a following friend's own run toast).</summary>
    private bool Queueing(NotificationData data)
    {
        // The mod's own notice (Online/OwnToasts): neither copied to friends nor held back as a run toast.
        if (OwnToasts.Active)
        {
            return true;
        }

        int type = (int)data.Type;
        int timing = (int)data.Timing;
        if (Hosting)
        {
            Host(data, type, timing);
            return true;
        }

        // Only in the host's campaign (this PC holds the host's run): an exhibition's toasts are this game's own, as before.
        if (!Following || _replaying > 0 || OwnToast(type, timing) || RunSync.Instance?.HoldingHostRun != true)
        {
            return true;
        }

        string kind = TypeName(type);
        if (_refused.Add(kind))
        {
            ModLog.Info($"Toasts: this game's own {kind} toast was held back (its title: {LogNames.TextShape(data.Title)}): {LogNames.HostTag()}'s game sends its run toasts " +
                "(further ones of this kind are only counted).");
        }

        return false;
    }

    /// <summary>Host: counts a toast and, with friends in, sends it (unless it's each player's own, over the cap, or unreadable).</summary>
    private void Host(NotificationData data, int type, int timing)
    {
        string kind = TypeName(type);
        _ = _inScene.Add(kind);
        if (!Sharing)
        {
            return;
        }

        if (OwnToast(type, timing))
        {
            _ = _skipped.Add(kind);
            return;
        }

        if (!_throttle.TryTake(FrameClock.NowMicros))
        {
            if (_logged.Add("cap:" + kind))
            {
                ModLog.Warn($"Toasts: the host's game queues {kind} toasts faster than {ToastThrottle.PerSecond} a second; friends get the first of each second (the rest are counted).");
            }

            return;
        }

        NotificationCopy? copy = ToastCopies.Read(data, out string problem, out IReadOnlyList<string> kept);
        if (copy == null)
        {
            if (_unread.Add(kind))
            {
                ModLog.Warn($"Toasts: the host's {kind} toast (its title: {LogNames.TextShape(data.Title)}) can't go to friends ({problem}); further ones of this kind are only counted.");
            }

            return;
        }

        // The relay's payload can't refuse (what's written goes out, and a friend drops one too big as malformed): looked at first.
        NotificationEvent queued = NotificationEvent.Queued(copy);
        if (!NotificationMessage.Write(new BitWriter(1024), queued))
        {
            if (_unread.Add(kind))
            {
                ModLog.Warn($"Toasts: the host's {kind} toast (its title: {LogNames.TextShape(data.Title)}) can't go to friends (over the message's {NotificationMessage.MaxBytes / 1024} KB); further ones of this kind are only counted.");
            }

            return;
        }

        int friends = _relay!.Broadcast(MessageType.Notification, Channel.Events, w => NotificationMessage.Write(w, queued));
        if (_sent.Add(kind))
        {
            ModLog.Info($"Toasts: sent the host's {kind} toast to {friends} friend(s): {ShownTextLog.Toast(copy, LogNames.TextShape)}; a friend's game shows it with its own objects " +
                "(further ones of this kind are counted).");
        }

        // Words no recipe makes stay the host's language on every friend's screen: said once per kind of toast and place.
        foreach (string note in kept)
        {
            int cut = note.IndexOf(" (", StringComparison.Ordinal);
            if (_logged.Add($"kept:{kind}:{(cut > 0 ? note[..cut] : note)}"))
            {
                ModLog.Info($"Toasts: the host's {kind} toast's {note} has no recipe; friends see it in the host's language (said once per kind and place).");
            }
        }
    }

    /// <summary>Host: the queue showed the toasts of <paramref name="timing"/>: friends' queues do the same (once a frame per timing).</summary>
    private void Popped(int timing, bool keepGoing)
    {
        if (!Sharing || (_popFrame == UnityEngine.Time.frameCount && _popTiming == timing))
        {
            return;
        }

        _popFrame = UnityEngine.Time.frameCount;
        _popTiming = timing;
        NotificationEvent popped = NotificationEvent.Popped(timing, keepGoing);
        if (_relay!.Broadcast(MessageType.Notification, Channel.Events, w => NotificationMessage.Write(w, popped)) > 0)
        {
            _pops++;
        }
    }

    /// <summary>Host: the queue was cleared: friends' queues are too.</summary>
    private void Cleared()
    {
        if (Sharing && _relay!.Broadcast(MessageType.Notification, Channel.Events, w => NotificationMessage.Write(w, NotificationEvent.Cleared())) > 0)
        {
            _clears++;
        }
    }

    /// <summary>
    /// Friend: the host's queue did <paramref name="read"/>: this PC's does the same, the game's own way. What waits
    /// (<see cref="ToastHold"/>) goes first when it's due another try; a toast this PC's game can't make yet but may soon (the
    /// host's game queued it as its match loaded, before this PC's copy started loading) waits too, and a pop or a toast that
    /// shares a timing with something waiting waits behind it.
    /// </summary>
    private void Apply(NotificationEvent read)
    {
        double now = FrameClock.Seconds;

        // Only at the hold's own pace: a try can look the scene over (no match here: a search for its game each time), and a
        // host's messages come as fast as it sends them. What shares a timing with something held waits behind it anyway.
        if (_hold.Due(now))
        {
            DrainHeld(now);
        }

        switch (read.Op)
        {
            case NotificationOp.Queue:
                if (_hold.Blocks(read))
                {
                    Wait(read, now, "behind an earlier one of its timing");
                }
                else if (TryShow(read.Copy!, first: true, waited: 0) == ToastAttempt.Later)
                {
                    Wait(read, now, _waitProblem);
                }

                break;
            case NotificationOp.Pop:
                if (!_hold.Blocks(read) || !_hold.Hold(read, now))
                {
                    _ = NotificationQueue.Instance?.PopQueue((ENotificationTiming)read.Timing, read.KeepGoing);
                }

                break;
            case NotificationOp.Clear:
                int cleared = _hold.Clear();
                for (int i = 0; i < cleared; i++)
                {
                    _ = _dropped.Add("cleared while waiting");
                }

                NotificationQueue.Instance?.Clear();
                break;
        }
    }

    /// <summary>Friend, every frame: the toasts that wait are tried again, at most every <see cref="ToastHold.RetrySeconds"/>. Nothing on a frame with none.</summary>
    public void Update()
    {
        if (_hold.Count == 0 || !Following)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (_hold.Due(now))
        {
            Run("showing a toast that waited", () => DrainHeld(now));
        }
    }

    /// <summary>Friend: <paramref name="read"/> waits for this PC's game (<paramref name="why"/>); dropped when too many wait already.</summary>
    private void Wait(NotificationEvent read, double now, string why)
    {
        string type = TypeName(read.Copy!.Type);
        if (!_hold.Hold(read, now))
        {
            if (_dropped.Add(type))
            {
                ModLog.Warn($"Toasts: {LogNames.HostTag()}'s {type} toast can't be made here yet ({why}) and {ToastHold.MaxHeld} wait already; dropped (further ones of this kind are only counted).");
            }

            return;
        }

        if (_logged.Add($"wait:{type}"))
        {
            ModLog.Info($"Toasts: {LogNames.HostTag()}'s {type} toast waits here until this PC's game can make it ({why}); queued then, or dropped after {ToastHold.HoldSeconds:0} s " +
                "(further ones of this kind are counted).");
        }
    }

    /// <summary>Friend: what waits, tried in the host's order.</summary>
    private void DrainHeld(double now) => _hold.Drain(
        now,
        (e, waited) => TryShow(e.Copy!, first: false, waited),
        e => _ = NotificationQueue.Instance?.PopQueue((ENotificationTiming)e.Timing, e.KeepGoing),
        (e, waited) =>
        {
            string type = TypeName(e.Copy!.Type);
            if (_dropped.Add(type))
            {
                ModLog.Warn($"Toasts: {LogNames.HostTag()}'s {type} toast can't be made here ({_waitProblem}, still after {waited:0} s); dropped (further ones of this kind are only counted).");
            }
        });

    /// <summary>
    /// Friend: the host's toast made with this PC's objects and queued the game's own way. <see cref="ToastAttempt.Later"/>
    /// (why kept for the log) when it can't be made only for something this PC's game may have soon (this scene's
    /// toast queue, the match's teams); dropped (logged once per type) when it can't be made at all. <paramref name="first"/>:
    /// its first try (the flood cap counts it then only); <paramref name="waited"/>: how long it has waited here.
    /// </summary>
    private ToastAttempt TryShow(NotificationCopy copy, bool first, double waited)
    {
        string type = TypeName(copy.Type);
        NotificationQueue? queue = NotificationQueue.Instance;
        if (queue == null)
        {
            _waitProblem = "this scene has no toast queue";
            return ToastAttempt.Later;
        }

        if ((first && !_friendThrottle.TryTake(FrameClock.NowMicros)) || (queue.m_Queue?.Count ?? 0) >= MaxWaitingHere)
        {
            _ = _dropped.Add(type);
            if (_logged.Add("flood"))
            {
                ModLog.Warn($"Toasts: {LogNames.HostTag()} sends toasts faster than this PC takes them ({FriendPerSecond} a second, {MaxWaitingHere} waiting at most); the rest are dropped and counted.");
            }

            return ToastAttempt.Failed;
        }

        NotificationData? data = ToastCopies.Build(copy, out string problem, out string how, out IReadOnlyList<string> fellBack, out bool later);
        if (data == null)
        {
            if (later)
            {
                _waitProblem = problem;
                return ToastAttempt.Later;
            }

            if (_dropped.Add(type))
            {
                ModLog.Warn($"Toasts: {LogNames.HostTag()}'s {type} toast can't be made here ({problem}); dropped (further ones of this kind are only counted).");
            }

            return ToastAttempt.Failed;
        }

        _replaying++;
        try
        {
            queue.Queue(data);
        }
        finally
        {
            _replaying--;
        }

        if (_shown.Add(type))
        {
            ModLog.Info($"Toasts: {LogNames.HostTag()}'s {type} toast is queued here as on the host's ({ShownTextLog.Toast(copy, LogNames.TextShape)}); its title from {how}, shown when {LogNames.HostTag()}'s game shows it " +
                "(further ones of this kind are counted).");
        }

        if (!first && _waited.Add(type))
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Toasts: {LogNames.HostTag()}'s {type} toast that waited is queued here after {waited:0.0} s, once this PC's game could make it (further ones of this kind are counted)."));
        }

        if (fellBack.Count > 0 && _logged.Add($"fellback:{type}"))
        {
            ModLog.Warn($"Toasts: {LogNames.HostTag()}'s {type} toast's {fellBack[0]} made nothing in this PC's language (a term or name this game lacks); the host's text is shown " +
                "(said once per kind of toast).");
        }

        return ToastAttempt.Shown;
    }

    /// <summary>Each player's own toasts (never sent; a friend's own are never held back).</summary>
    private static bool OwnToast(int type, int timing) => ToastOwner.IsEachPlayersOwn(type, timing);

    /// <summary>The game's name of a toast type ("Generic", "Injury"), or its number.</summary>
    private static string TypeName(int type) => Enum.IsDefined(typeof(ENotificationType), type) ? ((ENotificationType)type).ToString() : $"type {type}";

    /// <summary>Runs one friend entry; a failure is logged (the first few) and after <see cref="MaxErrors"/> the friend's side stops.</summary>
    private void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Failed(what, ex);
        }
    }

    private void Failed(string what, Exception ex)
    {
        _errors++;
        if (_errors <= MaxErrorLogs)
        {
            ModLog.Error($"Toasts: failed while {what}{(_errors == MaxErrorLogs ? " (further failures are only counted)" : string.Empty)}", ex);
        }

        if (_errors >= MaxErrors && !_off)
        {
            _off = true;
            ModLog.Error($"Toasts: {MaxErrors} failures; the host's toasts aren't sent or shown until the next online session (this game's own toasts show again).");
        }
    }

    private static void Patch(HarmonyLib.Harmony harmony, List<string> failed, string method, string? prefix = null, string? postfix = null)
    {
        try
        {
            MethodInfo? target = AccessTools.DeclaredMethod(typeof(NotificationQueue), method);
            if (target == null)
            {
                failed.Add($"toasts: NotificationQueue.{method} not found");
                return;
            }

            harmony.Patch(
                target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Toasts).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Toasts).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception ex)
        {
            failed.Add($"toasts: NotificationQueue.{method}: {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>A toast is queued: the host reads and sends it; a following friend holds back its own run toasts.</summary>
    private static bool QueuePrefix(NotificationData data)
    {
        Toasts? toasts = Instance;
        if (toasts == null || toasts._off || toasts._relay == null || data == null || NativeLobby.Widgets.LobbyToast.Queuing)
        {
            return true; // (the lobby's own toasts are each PC's)
        }

        try
        {
            return toasts.Queueing(data);
        }
        catch (Exception ex)
        {
            toasts.Failed("reading a toast", ex);
            return true;
        }
    }

    /// <summary>The queue showed the toasts of a timing: the host sends it.</summary>
    private static void PopPostfix(ENotificationTiming timing, bool keepGoing)
    {
        Toasts? toasts = Instance;
        if (toasts == null || toasts._off || !toasts.Hosting)
        {
            return;
        }

        try
        {
            toasts.Popped((int)timing, keepGoing);
        }
        catch (Exception ex)
        {
            toasts.Failed("sending a toast pop", ex);
        }
    }

    /// <summary>The queue was cleared: the host sends it.</summary>
    private static void ClearPostfix()
    {
        Toasts? toasts = Instance;
        if (toasts == null || toasts._off || !toasts.Hosting)
        {
            return;
        }

        try
        {
            toasts.Cleared();
        }
        catch (Exception ex)
        {
            toasts.Failed("sending a toast clear", ex);
        }
    }
}
#endif
