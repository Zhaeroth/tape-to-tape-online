using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Host: the newest run checkpoint of each of the newest <see cref="MaxRuns"/> shared runs of this hosting
/// session, so a friend whose game restarted after holding one (its in-memory copy is gone) can ask for it and apply the
/// run's end on it (<see cref="ProgressRunCopyMessage"/>). Answers are paced: one per friend and run every
/// <see cref="AnswerGapMicros"/>. Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class RunCopyStore
{
    /// <summary>Shared runs kept (the one played now, the one before, and one more).</summary>
    public const int MaxRuns = 3;

    /// <summary>Least time between two answers to the same friend about the same run.</summary>
    public const long AnswerGapMicros = 10_000_000;

    /// <summary>Most (friend, run) answers remembered for the pacing.</summary>
    private const int MaxAnswersKept = 64;

    private readonly List<(string Run, RunCheckpoint Checkpoint)> _runs = new();
    private readonly Dictionary<(ulong Friend, string Run), long> _answered = new();

    /// <summary>The runs kept, oldest first.</summary>
    public IReadOnlyList<string> Runs => _runs.ConvertAll(r => r.Run);

    /// <summary>
    /// Keeps <paramref name="checkpoint"/> as run <paramref name="run"/>'s newest (that run becomes the newest kept; the
    /// oldest beyond <see cref="MaxRuns"/> goes). False for an implausible run id, or a checkpoint already kept.
    /// </summary>
    public bool Keep(string run, RunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!ProgressLimits.IsPlausibleId(run))
        {
            return false;
        }

        int at = _runs.FindIndex(r => string.Equals(r.Run, run, StringComparison.Ordinal));
        if (at >= 0)
        {
            if (_runs[at].Checkpoint.Hash == checkpoint.Hash && _runs[at].Checkpoint.Sequence == checkpoint.Sequence)
            {
                return false;
            }

            _runs.RemoveAt(at);
        }

        _runs.Add((run, checkpoint));
        if (_runs.Count > MaxRuns)
        {
            _runs.RemoveAt(0);
        }

        return true;
    }

    /// <summary>Run <paramref name="run"/>'s newest kept checkpoint, or false when it isn't kept.</summary>
    public bool TryGet(string run, out RunCheckpoint? checkpoint)
    {
        int at = _runs.FindIndex(r => string.Equals(r.Run, run, StringComparison.Ordinal));
        checkpoint = at >= 0 ? _runs[at].Checkpoint : null;
        return checkpoint != null;
    }

    /// <summary>
    /// True when <paramref name="friend"/> may be answered about <paramref name="run"/> now (and it's counted as answered): at
    /// most once every <see cref="AnswerGapMicros"/> for a run kept here, and once for all the runs that aren't. So only runs
    /// kept here are remembered: however many run ids a friend asks about, it can't wash a copy's pacing out of the memory
    /// (a copy is up to 500 KB, deflated here) or fill the log with "not kept" lines.
    /// </summary>
    public bool TryAnswer(ulong friend, string run, long nowMicros)
    {
        ArgumentNullException.ThrowIfNull(run);
        (ulong Friend, string Run) key = (friend, TryGet(run, out _) ? run : string.Empty);
        if (_answered.TryGetValue(key, out long at) && nowMicros - at < AnswerGapMicros)
        {
            return false;
        }

        if (_answered.Count >= MaxAnswersKept)
        {
            _answered.Clear(); // only the pacing is lost, for a moment
        }

        _answered[key] = nowMicros;
        return true;
    }

    /// <summary>Hosting stopped: nothing is kept.</summary>
    public void Clear()
    {
        _runs.Clear();
        _answered.Clear();
    }
}

/// <summary>Where a friend's copy of one of the host's runs stands (<see cref="RunCopyRequests"/>).</summary>
public enum RunCopyState
{
    /// <summary>Not wanted (yet).</summary>
    None,

    /// <summary>Wanted: a request goes to the host at the next update.</summary>
    Wanted,

    /// <summary>Asked for; no answer yet.</summary>
    Asked,

    /// <summary>The host sent it; the game is loading it.</summary>
    Loading,

    /// <summary>Loaded and kept (<c>ProgressKeptRuns</c>).</summary>
    Loaded,

    /// <summary>The host doesn't keep it.</summary>
    Refused,

    /// <summary>It couldn't be had here (refused here, the load failed, or the host never answered).</summary>
    Failed,
}

/// <summary>What the policy asks about copies of the host's runs (<see cref="ProgressApplyPolicy.Decide"/>).</summary>
public interface IRunCopies
{
    /// <summary>True when this PC held run <paramref name="run"/> before (its ledger keeps the run's bench quest counts).</summary>
    bool HeldBefore(string run);

    /// <summary>Where the copy of run <paramref name="run"/> stands.</summary>
    RunCopyState StateOf(string run);

    /// <summary>Why run <paramref name="run"/>'s copy was refused or failed (empty otherwise).</summary>
    string Why(string run);

    /// <summary>How many times the link to the host restarted (a rejoin) since requests began: a wait for a copy starts afresh with
    /// each.</summary>
    int Restarts { get; }
}

/// <summary>
/// A friend's requests for copies of the host's runs: a batch whose run-bound parts are for a run this PC
/// held before its game restarted (so it keeps no copy in memory) wants one; it's asked for at once, again after
/// <see cref="AskAgainMicros"/> while unanswered, at most <see cref="MaxAsks"/> times, then given up on. The host answers
/// with the copy or "not kept"; the copy is checked and loaded by the game glue, which says how that went. One per
/// following session. The link to the host is told to it (<see cref="Tick"/>): nothing is asked while it is down, an
/// unanswered request's clock stops with it, and a link that restarted (a rejoin, after the host dropped this friend: what
/// was asked meanwhile never reached it) asks again from the start. Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class RunCopyRequests : IRunCopies
{
    /// <summary>An unanswered request is sent again after this long.</summary>
    public const long AskAgainMicros = 10_000_000;

    /// <summary>Most requests for one run (then it's given up on).</summary>
    public const int MaxAsks = 3;

    /// <summary>Most runs followed at once (a host keeps 3).</summary>
    public const int MaxRuns = 8;

    private readonly Func<string, bool> _heldBefore;
    private readonly Dictionary<string, Entry> _runs = new(StringComparer.Ordinal);
    private bool _linkUp = true;
    private long _lastTick = -1;

    /// <summary>Requests whose "held before" answer is <paramref name="heldBefore"/> (the ledger's run counts).</summary>
    public RunCopyRequests(Func<string, bool> heldBefore) => _heldBefore = heldBefore ?? throw new ArgumentNullException(nameof(heldBefore));

    /// <summary>Requests sent.</summary>
    public int Asked { get; private set; }

    /// <inheritdoc />
    public int Restarts { get; private set; }

    /// <summary>How many copies are wanted or asked for and not answered yet.</summary>
    public int Awaiting => _runs.Values.Count(e => e.State is RunCopyState.Wanted or RunCopyState.Asked);

    /// <summary>True while the link to the host is up (<see cref="Tick"/>): requests go out only then.</summary>
    public bool LinkUp => _linkUp;

    /// <summary>
    /// Every frame, before <see cref="TryAsk"/>: whether the link to the host is up at <paramref name="nowMicros"/>.
    /// While it is down nothing is asked and the clock of each unanswered request stops (its next ask and its giving up are
    /// counted in link-up time, so a drop can't use up the three requests).
    /// </summary>
    public void Tick(long nowMicros, bool linkUp)
    {
        long step = _lastTick < 0 ? 0 : Math.Max(0, nowMicros - _lastTick);
        _lastTick = nowMicros;
        _linkUp = linkUp;
        if (linkUp || step == 0)
        {
            return;
        }

        foreach (Entry entry in _runs.Values)
        {
            if (entry.State == RunCopyState.Asked)
            {
                entry.AskedAt += step;
            }
        }
    }

    /// <summary>
    /// The link to the host restarted (this friend rejoined: the host dropped it before, so what was asked meanwhile never
    /// reached the host): a request still unanswered is as new, asked again at once and counted afresh, and a wait for a copy
    /// starts over (<see cref="Restarts"/>). A copy being loaded, kept, refused or failed stands. Returns how many requests
    /// start over.
    /// </summary>
    public int LinkRestarted()
    {
        Restarts++;
        int again = 0;
        foreach (Entry entry in _runs.Values)
        {
            if (entry.State == RunCopyState.Asked)
            {
                entry.State = RunCopyState.Wanted;
                again++;
            }

            if (entry.State == RunCopyState.Wanted)
            {
                entry.Asks = 0;
            }
        }

        return again;
    }

    /// <inheritdoc />
    public bool HeldBefore(string run) => ProgressLimits.IsPlausibleId(run) && _heldBefore(run);

    /// <inheritdoc />
    public RunCopyState StateOf(string run) => _runs.TryGetValue(run, out Entry? entry) ? entry.State : RunCopyState.None;

    /// <inheritdoc />
    public string Why(string run) => _runs.TryGetValue(run, out Entry? entry) ? entry.Why : string.Empty;

    /// <summary>A batch waits for run <paramref name="run"/>'s copy: it's asked for (once). True when it's newly wanted.</summary>
    public bool Want(string run)
    {
        if (!ProgressLimits.IsPlausibleId(run) || _runs.ContainsKey(run))
        {
            return false;
        }

        if (_runs.Count >= MaxRuns)
        {
            return false; // a host can't make this PC follow more; the batch's wait runs out and it applies without them
        }

        _runs[run] = new Entry();
        return true;
    }

    /// <summary>
    /// The next request to send now (<paramref name="run"/>, <paramref name="attempt"/> from 1), or false. A run asked
    /// <see cref="MaxAsks"/> times with no answer for <see cref="AskAgainMicros"/> after the last is given up on
    /// (<see cref="RunCopyState.Failed"/>): <paramref name="gaveUp"/> names it, and the call returns false (call again).
    /// </summary>
    public bool TryAsk(long nowMicros, out string run, out int attempt, out string gaveUp)
    {
        gaveUp = string.Empty;
        if (!_linkUp)
        {
            run = string.Empty;
            attempt = 0;
            return false; // nobody could hear it, and it would be counted
        }

        foreach ((string id, Entry entry) in _runs)
        {
            bool due = entry.State == RunCopyState.Wanted || (entry.State == RunCopyState.Asked && nowMicros - entry.AskedAt >= AskAgainMicros);
            if (!due)
            {
                continue;
            }

            if (entry.Asks >= MaxAsks)
            {
                entry.State = RunCopyState.Failed;
                entry.Why = $"the host didn't answer {MaxAsks} requests";
                gaveUp = id;
                break; // said now; the next due request goes at the next call
            }

            entry.State = RunCopyState.Asked;
            entry.Asks++;
            entry.AskedAt = nowMicros;
            Asked++;
            run = id;
            attempt = entry.Asks;
            return true;
        }

        run = string.Empty;
        attempt = 0;
        return false;
    }

    /// <summary>True when a copy of <paramref name="run"/> is expected (asked for, not yet answered): only then is one taken.</summary>
    public bool Expecting(string run) => StateOf(run) == RunCopyState.Asked;

    /// <summary>The host sent the copy of <paramref name="run"/>; it's loading.</summary>
    public void Answered(string run) => Set(run, RunCopyState.Loading, string.Empty);

    /// <summary>The copy of <paramref name="run"/> is loaded and kept.</summary>
    public void Loaded(string run) => Set(run, RunCopyState.Loaded, string.Empty);

    /// <summary>The host doesn't keep <paramref name="run"/> (<paramref name="why"/>).</summary>
    public void Refused(string run, string why) => Set(run, RunCopyState.Refused, why);

    /// <summary>The copy of <paramref name="run"/> couldn't be had here (<paramref name="why"/>).</summary>
    public void Failed(string run, string why) => Set(run, RunCopyState.Failed, why);

    /// <summary>
    /// This PC is now in another hosting session (a rejoin after the host started hosting again): what the last one's host
    /// answered, or didn't, says nothing of what this one's keeps.
    /// </summary>
    public void Forget() => _runs.Clear();

    private void Set(string run, RunCopyState state, string why)
    {
        if (_runs.TryGetValue(run, out Entry? entry))
        {
            entry.State = state;
            entry.Why = why ?? string.Empty;
        }
    }

    private sealed class Entry
    {
        public RunCopyState State { get; set; } = RunCopyState.Wanted;

        public int Asks { get; set; }

        public long AskedAt { get; set; }

        public string Why { get; set; } = string.Empty;
    }
}

/// <summary>A friend's request for a copy of the host's run <paramref name="Run"/> of hosting session <paramref name="Session"/>.</summary>
/// <param name="Session">The host's hosting session (the progress batches' session).</param>
/// <param name="Run">The run's id.</param>
public readonly record struct ProgressRunRequest(ulong Session, string Run);

/// <summary>
/// Friend to host (channel 1, <c>MessageType.ProgressRunRequest</c>, protocol 6): "send me your copy of run R": this PC held
/// it before its game restarted and has a run's end to apply on it (<see cref="RunCopyRequests"/>).
/// <code>u8 format (1) · u64 session · str run</code>
/// A bad message reads as false, never throws.
/// </summary>
public static class ProgressRunRequestMessage
{
    /// <summary>The layout's version (the first byte).</summary>
    public const byte Format = 1;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, ProgressRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Format);
        writer.WriteUInt64(request.Session);
        writer.WriteShortString(request.Run, ProgressLimits.MaxIdBytes);
    }

    /// <summary>Reads the payload. False for anything malformed, of another format, or an implausible run id. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ProgressRunRequest request)
    {
        request = default;
        byte format = reader.ReadByte();
        ulong session = reader.ReadUInt64();
        string run = reader.ReadShortString(ProgressLimits.MaxIdBytes);
        if (reader.IsOverrun || format != Format || !ProgressLimits.IsPlausibleId(run))
        {
            return false;
        }

        request = new ProgressRunRequest(session, run);
        return true;
    }
}

/// <summary>How the host answered a <see cref="ProgressRunRequest"/>. Values are part of the protocol: only add.</summary>
public enum RunCopyStatus : byte
{
    /// <summary>Not a valid answer.</summary>
    None = 0,

    /// <summary>Here is the copy (the run's newest checkpoint).</summary>
    Copy = 1,

    /// <summary>The host doesn't keep that run (not one of its newest <see cref="RunCopyStore.MaxRuns"/> shared runs of this hosting session).</summary>
    NotKept = 2,
}

/// <summary>The host's answer: run <paramref name="Run"/> of hosting session <paramref name="Session"/>, and its checkpoint or why not.</summary>
/// <param name="Session">The hosting session.</param>
/// <param name="Run">The run's id.</param>
/// <param name="Status">Copy or not kept.</param>
/// <param name="Checkpoint">The run's newest checkpoint (<see cref="RunCopyStatus.Copy"/> only).</param>
public sealed record ProgressRunCopy(ulong Session, string Run, RunCopyStatus Status, RunCheckpoint? Checkpoint);

/// <summary>
/// Host to friend (channel 2, <c>MessageType.ProgressRunCopy</c>, protocol 6): the answer to a
/// <see cref="ProgressRunRequestMessage"/>. A copy is the run's newest checkpoint as <see cref="RunCheckpointMessage"/> writes
/// it (parts deflated, sizes checked before anything is allocated, the hash checked), so the same checks apply.
/// <code>u8 format (1) · u64 session · str run · u8 status · [status 1: the checkpoint as RunCheckpointMessage]</code>
/// A bad message reads as false, never throws.
/// </summary>
public static class ProgressRunCopyMessage
{
    /// <summary>The layout's version (the first byte).</summary>
    public const byte Format = 1;

    /// <summary>Writes the payload (after the header). False when a copy wouldn't fit in a message (<see cref="RunCheckpointMessage.MaxMessageBytes"/>).</summary>
    public static bool Write(BitWriter writer, ProgressRunCopy copy)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(copy);
        writer.WriteByte(Format);
        writer.WriteUInt64(copy.Session);
        writer.WriteShortString(copy.Run, ProgressLimits.MaxIdBytes);
        writer.WriteByte((byte)copy.Status);
        if (copy.Status != RunCopyStatus.Copy)
        {
            return true;
        }

        return copy.Checkpoint != null && RunCheckpointMessage.Write(writer, copy.Checkpoint);
    }

    /// <summary>Reads the payload. False for anything malformed, of another format, or out of limits. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ProgressRunCopy? copy)
    {
        copy = null;
        byte format = reader.ReadByte();
        ulong session = reader.ReadUInt64();
        string run = reader.ReadShortString(ProgressLimits.MaxIdBytes);
        var status = (RunCopyStatus)reader.ReadByte();
        if (reader.IsOverrun || format != Format || !ProgressLimits.IsPlausibleId(run) || status is not (RunCopyStatus.Copy or RunCopyStatus.NotKept))
        {
            return false;
        }

        RunCheckpoint? checkpoint = null;
        if (status == RunCopyStatus.Copy && (!RunCheckpointMessage.TryRead(ref reader, out checkpoint) || checkpoint!.Get(RunCheckpointPart.Run) == null))
        {
            return false;
        }

        copy = new ProgressRunCopy(session, run, status, checkpoint);
        return true;
    }
}
