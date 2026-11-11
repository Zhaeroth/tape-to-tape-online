#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Il2Cpp;
using Il2CppTape2Tape;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// The host's side of shared progress: what its game credits in the campaign (recorded by <see cref="ProgressPatches"/>)
/// is batched at safe points (<see cref="ProgressBatcher"/>: a stats save, a run change, the end of a match's campaign
/// update and its unlock checks, a quiet moment outside a match being played) and sent to every friend in order
/// (<see cref="ProgressOutbox"/>; channel 1, reliable and ordered with the host views). A friend who drops and comes back
/// gets every batch since they first joined again (their game skips what it applied): progress needs no acknowledgement
/// to be complete. When hosting stops, whatever was recorded since the last batch is sealed and sent to every friend
/// still there before the session closes (a host who stops or quits on its run-end screen would otherwise keep the run's
/// end to itself). Only the shared run counts: credit outside the campaign (an exhibition) isn't shared, except the
/// run's end when the host abandons the co-op run from its title screen, and neither is a solo run the host was still
/// playing when it started hosting (its run file isn't the co-op slot, <see cref="RunSync.HostOnCoopSlot"/>). One log
/// line per batch says what went to whom. Sends to a friend pause while the session's transport still holds earlier
/// messages for them (<see cref="CampaignRelay.QueuedBytes"/>), so a rejoin's resend never fills its queue. The newest
/// checkpoint of each of the newest 3 shared runs is kept for the hosting session (<see cref="RunCopyStore"/>): a
/// friend whose game restarted after holding one asks for it to apply the run's end on.
/// </summary>
internal sealed class ProgressHost
{
    private const int SendsPerFriendPerFrame = 16;
    private const int KeptDescriptions = 256;

    /// <summary>Longest the title screen's abandon of the co-op run counts as campaign credit, if its run reset never comes.</summary>
    private const long TitleEndMicros = 30_000_000;

    private readonly CampaignRelay _relay;
    private readonly MatchRelay _matches;
    private readonly ProgressBatcher _batcher = new();
    private readonly ProgressOutbox _outbox;
    private readonly RunCopyStore _copies = new();
    private uint _copySeen;
    private readonly Dictionary<uint, (string Reason, string What)> _described = new();
    private readonly Dictionary<ulong, uint> _highestSent = new();
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private int _stateFrame = -1;
    private bool _inCampaign;
    private bool _shared;
    private bool _matchOver;
    private long _titleEndUntil = long.MinValue;
    private string _run = string.Empty;
    private int _batches;
    private long _entries;

    // What the session credited to each stat's value (its adding operations, by stat id and bucket: the game counts most
    // campaign credit twice, once in the lifetime value and once in the campaign value), for the stop's detail line.
    private readonly SortedDictionary<(int Stat, StatBucket Bucket), double> _credited = new();
    private long _outsideCampaign;
    private long _notShared;
    private int _unsent;
    private int _paused;
    private bool _sharingSaid;
    private long _nextListenCheck;
    private long _nextStatusCheck;
    private readonly LineBudget _resendLines = new("Progress: requests to send batches again", 10);

    /// <summary>Hosting started: a new session (its own batch numbers) over <paramref name="relay"/>.</summary>
    public ProgressHost(CampaignRelay relay, MatchRelay matches)
    {
        _relay = relay;
        _matches = matches;
        // The hosting session's own id (in every friend's welcome), so one id names it in every log; a new one if not known.
        _outbox = new ProgressOutbox(relay.HostingSession != 0 ? relay.HostingSession : BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));
        _sharingSaid = ModSettings.ShareProgress;
        ModLog.Info($"Progress: what this game credits in the campaign is shared with friends, each counting it on their own profile (session {_outbox.Session:x16})" +
            $"{(_sharingSaid ? "." : "; ShareProgress is off, so nothing is sent (friends are told).")}");
    }

    /// <summary>Records what the host's game just credited (from a patch, inside the game's own call: nothing is sent here).</summary>
    public void Record(ProgressEntry entry)
    {
        RefreshState();
        if (!_shared)
        {
            // Hosting started during the host's solo run: that run, its end and its history are the host's alone.
            _notShared++;
            Note("solo", "Progress: this game is playing a run that isn't the shared co-op run (hosting started during your solo run), so " +
                "what it credits isn't sent to friends; the co-op slot becomes the run file at the title screen.");
            return;
        }

        // Stat operations (entity stats and record holders too) and unlock checks happen in exhibitions too; the rest only
        // comes from the campaign's own code (the end-of-match steps, the run's end), some of it behind the run end's
        // loading screen, so it isn't filtered. The co-op run abandoned from the title screen ends there too (its concede
        // and fail stats, which the game counts as lifetime and Play Now values at the title).
        if (!_inCampaign && FrameClock.NowMicros > _titleEndUntil && entry is StatEntry or UnlockPassEntry or StatTextEntry or EntityStatEntry)
        {
            _outsideCampaign++;
            Note("outside", "Progress: credit outside the campaign (an exhibition, the menus) isn't shared; only the campaign counts on friends' profiles.");
            return;
        }

        if (_batcher.Add(entry, _run, FrameClock.NowMicros))
        {
            _entries++;
            if (entry is StatEntry stat && ProgressLimits.CreditOf(stat) is double credit && credit != 0)
            {
                (int, StatBucket) value = (stat.Stat, StatOps.BucketOf(stat.Op));
                _credited[value] = _credited.GetValueOrDefault(value) + credit;
            }
        }
        else
        {
            Note($"refused {entry.GetType().Name}", $"Progress: something this game credited is outside the protocol's limits and isn't shared: {ForLog(entry)}.");
        }
    }

    /// <summary>
    /// <paramref name="entry"/> for a log line: its text that a player may have written (a custom team's name, a custom
    /// skater's as a run's best scorer) as its shape only (D127).
    /// </summary>
    private static ProgressEntry ForLog(ProgressEntry entry) => entry switch
    {
        StatTextEntry text => text with { Text = LogNames.TextShape(text.Text) },
        SquadEntry squad => squad with { BestScorer = LogNames.TextShape(squad.BestScorer) },
        _ => entry,
    };

    /// <summary>A safe point: what's recorded so far becomes a batch (sent at the next update).</summary>
    public void Seal(string reason) => _batcher.Seal(reason);

    /// <summary>
    /// The host's match is over (its end-of-match campaign steps run): what's recorded now is sealed, and until the match's
    /// scene unloads a quiet moment seals too. The run-end menus can hold the match's scene for as long as the host likes,
    /// and the run's end mustn't wait there for a stats save that only comes after them.
    /// </summary>
    public void MatchOver(string reason)
    {
        _matchOver = true;
        _batcher.Seal(reason);
    }

    /// <summary>
    /// The title screen is ending the co-op run (abandoned, or replaced by a new one): its stat credit counts as the
    /// campaign's until the run's reset (<see cref="TitleEndDone"/>), or <see cref="TitleEndMicros"/> at most.
    /// </summary>
    public void TitleEndStarted()
    {
        RefreshState();
        if (_shared)
        {
            _titleEndUntil = FrameClock.NowMicros + TitleEndMicros;
            ModLog.Info("Progress: the co-op run is ending at the title screen; its run's end (concede and fail stats, run history) is shared.");
        }
    }

    /// <summary>The title screen's end of the co-op run reset the run: what follows isn't the campaign's.</summary>
    public void TitleEndDone()
    {
        if (_titleEndUntil != long.MinValue)
        {
            _titleEndUntil = long.MinValue;
            _batcher.Seal("the run ended at the title screen");
        }
    }

    /// <summary>A stat operation whose value couldn't be read (logged once per operation).</summary>
    public void NoteUnreadable(StatOp op) => Note($"unreadable {op}", $"Progress: a {op} stat operation's value wasn't a number this mod reads; it isn't shared.");

    /// <summary>Logs <paramref name="line"/> once per hosting session for <paramref name="key"/> (from a patch: nothing more is done).</summary>
    public void NoteOnce(string key, string line) => Note(key, line);

    /// <summary>A friend joined or came back: they get the host's status, then every batch since they first joined.</summary>
    public void FriendJoined(ulong friend) => _outbox.FriendJoined(friend);

    /// <summary>
    /// A friend asked for batches again (a gap on their side): what of it they may have, the outbox still keeps and
    /// they were already sent goes to them again ahead of anything new, at most every
    /// <see cref="ProgressOutbox.ResendGapMicros"/>, and their status follows. Logged. False when the request is malformed.
    /// </summary>
    public bool ReceiveResend(ulong friend, ref BitReader reader)
    {
        if (!ProgressResendMessage.TryRead(ref reader, out ProgressResend request))
        {
            return false;
        }

        string tag = LogNames.Tag(friend);
        if (request.Session != _outbox.Session)
        {
            Note($"resend session {tag}", $"Progress: {tag} asked for batches of another hosting session again ({request.Session:x16}); ignored.");
            return true;
        }

        string range = request.To == ProgressResendMessage.ToNewest ? $"{request.From} on" : $"{request.From} to {request.To}";
        if (_outbox.Resend(friend, request.From, request.To, FrameClock.NowMicros, out uint first, out uint last))
        {
            ModLog.Info($"Progress: {tag} asked for batch{(request.To == request.From ? $" {range}" : $"es {range}")} again (missing on their side); " +
                $"sending {(first == last ? $"batch {first}" : $"batches {first} to {last}")} again.");
        }
        else if (_resendLines.TrySpend())
        {
            ModLog.Info($"Progress: {tag} asked for batches {range} again; nothing sent (asked again within " +
                $"{ProgressOutbox.ResendGapMicros / 1_000_000} s, or none of them is kept for them and already sent: the others come anyway).");
        }

        return true;
    }

    /// <summary>
    /// A friend asked for a copy of one of the shared runs (they held it before their game restarted): its newest
    /// checkpoint goes to them (channel 2) if it's one of the newest 3 kept, else "not kept"; at most one answer per friend and
    /// run every <see cref="RunCopyStore.AnswerGapMicros"/>. Logged. False when the request is malformed.
    /// </summary>
    public bool ReceiveRunRequest(ulong friend, ref BitReader reader)
    {
        if (!ProgressRunRequestMessage.TryRead(ref reader, out ProgressRunRequest request))
        {
            return false;
        }

        string tag = LogNames.Tag(friend);
        if (request.Session != _outbox.Session)
        {
            Note($"copy session {tag}", $"Progress: {tag} asked for a copy of a run of another hosting session ({request.Session:x16}); ignored.");
            return true;
        }

        if (!_copies.TryAnswer(friend, request.Run, FrameClock.NowMicros))
        {
            return true; // answered within the last few seconds: the answer is on its way
        }

        bool kept = _copies.TryGet(request.Run, out RunCheckpoint? checkpoint);
        var payload = new BitWriter(kept ? 64 * 1024 : 128);
        if (!ProgressRunCopyMessage.Write(payload, new ProgressRunCopy(_outbox.Session, request.Run, kept ? RunCopyStatus.Copy : RunCopyStatus.NotKept, checkpoint)))
        {
            // Never seen: a checkpoint this big was never sent to friends either (RunCheckpointMessage's limit), so none held it.
            ModLog.Warn($"Progress: {tag} asked for a copy of the shared run {HostText.KeyForLog(request.Run)}; its checkpoint {checkpoint!.Sequence} is too big for one message, so they're told it isn't kept.");
            kept = false;
            payload.Reset();
            ProgressRunCopyMessage.Write(payload, new ProgressRunCopy(_outbox.Session, request.Run, RunCopyStatus.NotKept, null));
        }

        bool sent = _relay.SendTo(friend, MessageType.ProgressRunCopy, Channel.Bulk, w => w.WriteBytes(payload.AsSpan()));
        ModLog.Info(kept
            ? $"Progress: {tag} asked for a copy of the shared run {HostText.KeyForLog(request.Run)} (they no longer hold it); {(sent ? "sent" : "couldn't send")} checkpoint {checkpoint!.Sequence} " +
              string.Create(CultureInfo.InvariantCulture, $"({checkpoint.RawBytes / 1024.0:0.0} KB).")
            : $"Progress: {tag} asked for a copy of the shared run {HostText.KeyForLog(request.Run)} (they no longer hold it); it isn't kept here (only the newest " +
              $"{RunCopyStore.MaxRuns} shared runs of this hosting session are){(sent ? "; told them" : "; couldn't tell them")}.");
        return true;
    }

    /// <summary>A newly published run checkpoint is kept by its run (the newest of each of the newest 3 shared runs).</summary>
    private void KeepRunCopy()
    {
        RunCheckpoint? latest = _relay.LatestRun;
        if (latest == null || latest.Sequence == _copySeen)
        {
            return;
        }

        _copySeen = latest.Sequence;
        byte[]? run = latest.Get(RunCheckpointPart.Run);
        if (run == null || !RunFileSummary.TryRead(run, out RunFileSummary? summary) || !ProgressLimits.IsPlausibleId(summary!.RunId))
        {
            Note("copy unread", $"Progress: run checkpoint {latest.Sequence}'s run couldn't be read, so it isn't kept for friends who restart.");
            return;
        }

        _copies.Keep(summary.RunId, latest);
    }

    /// <summary>A friend left: what's made meanwhile is kept for them.</summary>
    public void FriendLeft(ulong friend) => _outbox.FriendLeft(friend);

    /// <summary>Every frame: sealed batches are numbered and kept, and each friend here gets what they haven't had.</summary>
    public void Update()
    {
        // "Live" is a match being played: once its end-of-match steps run, the run's end isn't kept waiting for the scene
        // to unload (the host can sit on its run-end menus, or stop hosting there).
        if (!_matches.HostStreaming)
        {
            _matchOver = false;
        }

        long now = FrameClock.NowMicros; // read once: this runs every frame of a hosted session
        while (_batcher.TryTake(now, live: _matches.HostStreaming && !_matchOver, out SealedProgress sealedBatch))
        {
            Publish(sealedBatch);
        }

        SendAll(SendsPerFriendPerFrame);
        KeepRunCopy();
        if (now >= _nextStatusCheck)
        {
            _nextStatusCheck = now + 250_000;
            SendStatuses(closing: false);
        }

        if (now >= _nextListenCheck)
        {
            // Entity stat updates are heard on the game's stat repository (again if the game made a new one).
            _nextListenCheck = now + 1_000_000;
            ProgressEntityStats.Listen(ProgressFriend.FromProject<Il2CppStats.PersistentStats.StatManager>()?.StatRepository);
        }
    }

    /// <summary>
    /// Each friend here gets the host's status when it's due (<see cref="ProgressOutbox.TryStatus"/>: on joining, when
    /// ShareProgress changes, after a resend, a while after new batches), after the batches sent so far and only once the
    /// session's transport holds nothing more for them (it would name batches still on their way).
    /// </summary>
    private void SendStatuses(bool closing)
    {
        bool sharing = ModSettings.ShareProgress;
        if (sharing != _sharingSaid)
        {
            _sharingSaid = sharing;
            ModLog.Info($"Progress: ShareProgress is {(sharing ? "on" : "off")} on this PC; friends are told{(sharing ? string.Empty : " (they apply nothing more from this session until it's back on)")}.");
        }

        foreach (ulong friend in _outbox.Present.ToList())
        {
            if ((!closing && _relay.QueuedBytes(friend) > 0) || !_outbox.TryStatus(friend, sharing, FrameClock.NowMicros, out ProgressStatus status, immediately: closing))
            {
                continue;
            }

            if (_relay.SendTo(friend, MessageType.ProgressStatus, Channel.Events, w => ProgressStatusMessage.Write(w, status)))
            {
                _outbox.MarkStatusSent(friend, status);
            }
        }
    }

    /// <summary>
    /// Hosting stopped: whatever was recorded since the last batch is sealed and sent, with everything a friend still here
    /// hasn't had, before the session closes (it's called before the link to friends ends). Nothing waits for the link
    /// here: the session's transport queues what Steam can't take yet behind what it already holds, in order, and sends it
    /// as the session closes. What the link refuses outright is lost with the hosting session (friends' games apply what
    /// came as their session ends).
    /// </summary>
    public void Stop(string reason)
    {
        _batcher.Seal("hosting stopped");
        while (_batcher.TryTake(long.MaxValue, live: false, out SealedProgress sealedBatch))
        {
            Publish(sealedBatch);
        }

        SendAll(int.MaxValue, closing: true);
        SendStatuses(closing: true); // each friend learns the newest batch: one that doesn't arrive is named on their side
        ProgressEntityStats.Unlisten();
        _copies.Clear();
        var unsent = new List<string>();
        foreach (ulong friend in _outbox.Present.ToList())
        {
            int left = 0;
            while (_outbox.TryNext(friend, out uint sequence, out _, out _))
            {
                _outbox.MarkSent(friend, sequence); // only counted: the session is closing
                left++;
            }

            if (left > 0)
            {
                unsent.Add($"{left} to {LogNames.Tag(friend)}");
            }
        }

        ModLog.Info($"Progress: hosting stopped ({reason}). {_batches} batches made ({_entries} entries)" +
            $"{(unsent.Count > 0 ? $"; NOT sent before the session closed (the link refused them): {string.Join(", ", unsent)}" : string.Empty)}" +
            $"{(_unsent > 0 ? $"; {_unsent} made with no friend to send them to" : string.Empty)}" +
            $"{(_outsideCampaign > 0 ? $"; {_outsideCampaign} credits outside the campaign not shared" : string.Empty)}" +
            $"{(_notShared > 0 ? $"; {_notShared} credits of a run that isn't the shared co-op run not shared" : string.Empty)}" +
            $"{(_batcher.Refused > 0 ? $"; {_batcher.Refused} refused for limits" : string.Empty)}" +
            $"{(_batcher.Repeats > 0 ? $"; {_batcher.Repeats} repeated unlock checks left out" : string.Empty)}" +
            $"{(_outbox.Dropped > 0 ? $"; {_outbox.Dropped} dropped for space" : string.Empty)}" +
            $"{(_outbox.Resends + _outbox.ResendsRefused > 0 ? $"; friends asked for batches again {_outbox.Resends + _outbox.ResendsRefused} times ({_outbox.Resends} answered)" : string.Empty)}" +
            $"{(_paused > 0 ? $"; sends paused {_paused} times while the link still held earlier messages" : string.Empty)}" +
            $"{(ProgressPatches.Failures > 0 ? $"; {ProgressPatches.Failures} failures in the progress patches (the first 5 logged)" : string.Empty)}.");
        if (_credited.Count > 0)
        {
            // For the session file: what each stat's value was credited, each bucket on its own as a friend's "Progress detail"
            // lines show them (and as the stats file keeps them).
            ModLog.Trace("Progress detail (this hosting session's credit per stat and bucket): " +
                string.Join(", ", _credited.Select(c => $"{(Il2CppStats.PersistentStats.StatType)c.Key.Stat}.{c.Key.Bucket} {(c.Value > 0 ? "+" : string.Empty)}{c.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}")) + ".");
            _credited.Clear();
        }

        _batcher.Clear();
    }

    /// <summary>
    /// Each friend here gets what they haven't had (at most <paramref name="perFriend"/> batches each), logged once per
    /// batch. While <paramref name="closing"/>, sends don't pause for what the transport still holds.
    /// </summary>
    private void SendAll(int perFriend, bool closing = false)
    {
        if (!_outbox.AnyToSend)
        {
            return;
        }

        var fresh = new SortedDictionary<uint, List<string>>();
        foreach (ulong friend in _outbox.Present.ToList())
        {
            Send(friend, perFriend, fresh, closing);
        }

        foreach ((uint sequence, List<string> names) in fresh)
        {
            (string reason, string what) = _described.TryGetValue(sequence, out var described) ? described : ("?", "?");
            ModLog.Info($"Progress: sent batch {sequence} ({reason}): {what} to {string.Join(", ", names)}.");
        }
    }

    private void Publish(SealedProgress sealedBatch)
    {
        if (!ModSettings.ShareProgress)
        {
            Note("off", "Progress: ShareProgress is off on this PC: the campaign's progress isn't sent to friends.");
            return;
        }

        if (!_outbox.AnyoneEntitled)
        {
            _unsent++;
            return;
        }

        uint checkpoint = _relay.LatestRun?.Sequence ?? 0;
        var batch = new ProgressBatch(_outbox.NextSequence, checkpoint, sealedBatch.Run, sealedBatch.Entries);
        byte[]? body = ProgressBatchMessage.EncodeBody(batch);
        if (body == null)
        {
            ModLog.Warn($"Progress: a batch ({batch.Describe()}) is too big for one message and isn't sent.");
            return;
        }

        _outbox.Add(batch.Sequence, body);
        _batches++;
        _described[batch.Sequence] = ($"{sealedBatch.Reason}{(checkpoint != 0 ? $", run checkpoint {checkpoint}" : string.Empty)}", batch.Describe());
        if (_described.Count > KeptDescriptions)
        {
            _described.Remove(_described.Keys.Min());
        }
    }

    private void Send(ulong friend, int most, SortedDictionary<uint, List<string>> fresh, bool closing)
    {
        uint againFirst = 0, againLast = 0;
        for (int i = 0; i < most && _outbox.TryNext(friend, out uint sequence, out uint entitledFrom, out byte[] body); i++)
        {
            // The session's transport keeps a reliable message Steam refused and sends it again, and drops a friend whose
            // queue grows too big: while it still holds anything for them, the rest waits for the next frame (unless the
            // session is closing: there's no next frame, and the transport sends its queue, in order, as it closes).
            if (!closing && _relay.QueuedBytes(friend) > 0)
            {
                _paused++;
                break;
            }

            ulong session = _outbox.Session;
            if (!_relay.SendTo(friend, MessageType.ProgressEvent, Channel.Events, w => ProgressBatchMessage.Write(w, session, entitledFrom, body)))
            {
                break; // refused (or not in the link yet): again next frame
            }

            _outbox.MarkSent(friend, sequence);
            if (sequence > _highestSent.GetValueOrDefault(friend))
            {
                _highestSent[friend] = sequence;
                (fresh.TryGetValue(sequence, out List<string>? names) ? names : fresh[sequence] = new List<string>()).Add(LogNames.Tag(friend));
            }
            else
            {
                againFirst = againFirst == 0 ? sequence : againFirst;
                againLast = sequence;
            }
        }

        if (againFirst != 0)
        {
            ModLog.Info($"Progress: sent {LogNames.Tag(friend)} batch{(againFirst == againLast ? $" {againFirst}" : $"es {againFirst} to {againLast}")} again " +
                "(they're back, or asked for them; their game skips what it already applied).");
        }
    }

    /// <summary>Once a frame: is the host's game in the campaign, on the shared co-op run, and which run.</summary>
    private void RefreshState()
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == _stateFrame)
        {
            return;
        }

        _stateFrame = frame;
        _shared = RunSync.Instance?.HostOnCoopSlot == true;
        try
        {
            _inCampaign = T2TUtils.IsInRogueMode;
            string? run = ProfileData.Instance?.campaignState?.runData?.runID;
            _run = ProgressLimits.IsPlausibleId(run) ? run! : string.Empty;
        }
        catch (Exception ex)
        {
            // Early or late in the game's life its menus may not be there to ask: nothing is in a campaign then.
            _inCampaign = false;
            _run = string.Empty;
            Note("state", $"Progress: couldn't tell whether the game is in the campaign ({ex.GetType().Name}); credit then isn't shared.");
        }
    }

    private void Note(string key, string line)
    {
        if (_noted.Add(key))
        {
            ModLog.Info(line);
        }
    }

}
#endif
