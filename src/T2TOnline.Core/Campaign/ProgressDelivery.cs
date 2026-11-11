using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's progress window: entries come in as the host's game credits them; at a safe point (a stats save, a run
/// change, or a quiet moment outside a live match) the window is sealed into a batch. Game-independent. Not thread-safe
/// (one game thread).
/// </summary>
public sealed class ProgressBatcher
{
    /// <summary>A window with entries, quiet this long and outside a live match, is sealed.</summary>
    public const long IdleMicros = 2_000_000;

    private readonly Queue<SealedProgress> _sealed = new();
    private readonly Dictionary<int, double> _credit = new();
    private List<ProgressEntry> _window = new();
    private string _run = string.Empty;
    private long _lastAddMicros;

    /// <summary>Entries waiting in the window.</summary>
    public int Pending => _window.Count;

    /// <summary>Entries refused for being out of limits (counted, never sent).</summary>
    public int Refused { get; private set; }

    /// <summary>Unlock checks dropped for repeating the one just before with nothing in between (they'd find nothing new).</summary>
    public int Repeats { get; private set; }

    /// <summary>
    /// Adds what the host's game just credited, on run <paramref name="run"/> (empty: none). A different run seals the
    /// window first (a batch belongs to one run); a full window too, one whose stat would get more than
    /// <see cref="ProgressLimits.MaxStatCreditPerBatch"/>, one that holds a run's end already when another comes, and one
    /// with <see cref="ProgressLimits.MaxMarksPerBatch"/> profile marks. A run history entry is sealed straight after it (it's the big
    /// one). An unlock check that repeats the one right before it (same timing, nothing earned, no stat change between) is
    /// dropped: the game runs one per in-match event, and a friend's game would only find nothing twice. False, counted,
    /// for an entry outside <see cref="ProgressLimits"/>.
    /// </summary>
    public bool Add(ProgressEntry entry, string run, long nowMicros)
    {
        run ??= string.Empty;
        if (!ProgressLimits.IsValid(entry) || (run.Length > 0 && !ProgressLimits.IsPlausibleId(run)))
        {
            Refused++;
            return false;
        }

        if (entry is UnlockPassEntry { Earned.Count: 0 } pass && _window.Count > 0 && _window[^1] is UnlockPassEntry { Earned.Count: 0 } last &&
            last.Timing == pass.Timing && last.Queue == pass.Queue && last.NotificationTiming == pass.NotificationTiming &&
            string.Equals(run, _run, StringComparison.Ordinal))
        {
            Repeats++;
            _lastAddMicros = nowMicros;
            return true;
        }

        if (_window.Count > 0 && !string.Equals(run, _run, StringComparison.Ordinal))
        {
            Seal("the run changed");
        }

        if (_window.Count >= ProgressBatchMessage.MaxEntries)
        {
            Seal("the batch was full");
        }

        (int stat, double credit) = entry is StatEntry statEntry ? (statEntry.Stat, ProgressLimits.CreditOf(statEntry)) : (-1, 0d);
        if (credit > 0 && _credit.GetValueOrDefault(stat) + credit > ProgressLimits.MaxStatCreditPerBatch)
        {
            Seal("a stat's credit filled the batch");
        }

        // A batch holds one run's end and so many profile marks (ProgressLimits.IsWithinBatchLimits).
        if ((entry is RunEndingEntry && _window.Exists(e => e is RunEndingEntry)) ||
            (entry is ProfileMarkEntry && _window.Count(e => e is ProfileMarkEntry) >= ProgressLimits.MaxMarksPerBatch))
        {
            Seal(entry is RunEndingEntry ? "another run's end" : "the profile marks filled the batch");
        }

        _run = run;
        _window.Add(entry);
        if (credit > 0)
        {
            _credit[stat] = _credit.GetValueOrDefault(stat) + credit;
        }

        _lastAddMicros = nowMicros;
        if (entry is RunHistoryRecord)
        {
            Seal("the run history was saved");
        }

        return true;
    }

    /// <summary>A safe point: whatever is in the window becomes a batch (<paramref name="reason"/> goes in the log).</summary>
    public void Seal(string reason)
    {
        if (_window.Count == 0)
        {
            return;
        }

        _sealed.Enqueue(new SealedProgress(_run, reason, _window));
        _window = new List<ProgressEntry>();
        _credit.Clear();
    }

    /// <summary>
    /// The next sealed batch, oldest first. When none is sealed, a window that's been quiet for <see cref="IdleMicros"/>
    /// is sealed, unless a host match is <paramref name="live"/> (in-match credit waits for the end-of-match stats save).
    /// </summary>
    public bool TryTake(long nowMicros, bool live, out SealedProgress batch)
    {
        if (_sealed.Count == 0 && _window.Count > 0 && !live && nowMicros - _lastAddMicros >= IdleMicros)
        {
            Seal("a quiet moment");
        }

        return _sealed.TryDequeue(out batch);
    }

    /// <summary>Drops everything waiting (hosting stopped).</summary>
    public void Clear()
    {
        _sealed.Clear();
        _window = new List<ProgressEntry>();
        _credit.Clear();
        _run = string.Empty;
    }
}

/// <summary>A sealed window: the run its entries belong to, why it was sealed, and the entries.</summary>
/// <param name="Run">The host's run id (empty: none).</param>
/// <param name="Reason">Why it was sealed, for the log.</param>
/// <param name="Entries">The entries, in order.</param>
public readonly record struct SealedProgress(string Run, string Reason, IReadOnlyList<ProgressEntry> Entries);

/// <summary>
/// The host's progress outbox for one hosting session. Every batch is kept (up to <see cref="MaxStoredBytes"/>), and
/// each friend gets every batch made since they first joined, in order: a friend who drops and comes back gets what
/// they missed (the session's batches from their first join again; their own ledger skips what they applied), as on the
/// couch a player who steps away is still on the team. A friend who first joins mid-session doesn't get what was earned
/// before. A friend that asks for batches again (<see cref="Resend"/>) gets those it may have, ahead of anything new, at
/// most every <see cref="ResendGapMicros"/>; and each friend gets the host's <see cref="ProgressStatus"/> when it joins,
/// when sharing is turned on or off, after a resend, and a while after the newest batch changed (<see cref="TryStatus"/>).
/// Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class ProgressOutbox
{
    /// <summary>Most bytes kept; the oldest batches go first beyond it (a run makes tens of KB).</summary>
    public const long MaxStoredBytes = 8L * 1024 * 1024;

    /// <summary>A friend's requests to send batches again are taken at most this often (each can make the host send a lot).</summary>
    public const long ResendGapMicros = 2_000_000;

    /// <summary>Most ranges of batches waiting to be sent again to one friend (overlapping ones merge).</summary>
    public const int MaxResendRanges = 8;

    /// <summary>A friend gets the host's status this long after its newest batch changed (it shows the last ones of a stretch missing).</summary>
    public const long StatusEveryMicros = 10_000_000;

    private readonly List<(uint Sequence, byte[] Body)> _batches = new();
    private readonly Dictionary<ulong, FriendState> _friends = new();
    private long _storedBytes;

    /// <summary>The outbox of hosting session <paramref name="session"/>.</summary>
    public ProgressOutbox(ulong session) => Session = session;

    /// <summary>The hosting session's id (random per session).</summary>
    public ulong Session { get; }

    /// <summary>The newest batch's number (0: none yet).</summary>
    public uint LastSequence { get; private set; }

    /// <summary>The number the next batch gets.</summary>
    public uint NextSequence => LastSequence + 1;

    /// <summary>Batches dropped for space (friends who rejoin after that don't get them).</summary>
    public int Dropped { get; private set; }

    /// <summary>True when some friend (here or away) would get a new batch: else there's no one to keep it for.</summary>
    public bool AnyoneEntitled => _friends.Count > 0;

    /// <summary>Friends here now.</summary>
    public IEnumerable<ulong> Present => _friends.Where(f => f.Value.Present).Select(f => f.Key);

    /// <summary>True when a friend here now hasn't been sent every batch kept for them (else <see cref="TryNext"/> finds nothing).</summary>
    public bool AnyToSend
    {
        get
        {
            if (_batches.Count == 0)
            {
                return false;
            }

            foreach (FriendState state in _friends.Values)
            {
                if (state.Present && (Math.Max(state.Next, _batches[0].Sequence) <= LastSequence || state.Resends.Count > 0))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Requests to send batches again that were taken (each sends what the friend may have of its range).</summary>
    public int Resends { get; private set; }

    /// <summary>Requests to send batches again that were too soon after the last, or named nothing kept for that friend.</summary>
    public int ResendsRefused { get; private set; }

    /// <summary>
    /// The first batch the outbox still keeps for <paramref name="friend"/> (earlier ones were made before they first joined,
    /// or dropped for space); the next batch's number for a friend it doesn't know.
    /// </summary>
    public uint EntitledFrom(ulong friend)
    {
        uint oldest = _batches.Count > 0 ? _batches[0].Sequence : NextSequence;
        return _friends.TryGetValue(friend, out FriendState? state) ? Math.Max(state.First, oldest) : NextSequence;
    }

    /// <summary>
    /// <paramref name="friend"/> asked for batches <paramref name="from"/> to <paramref name="to"/> again (a gap on their side).
    /// What of it they may have, the outbox still keeps and was already sent to them goes to them again ahead of anything
    /// new (<paramref name="first"/> to <paramref name="last"/>); later ones come anyway. False, nothing queued, when they
    /// aren't here, asked again within <see cref="ResendGapMicros"/>, or the range holds nothing to send again.
    /// </summary>
    public bool Resend(ulong friend, uint from, uint to, long nowMicros, out uint first, out uint last)
    {
        first = last = 0;
        if (!_friends.TryGetValue(friend, out FriendState? state) || !state.Present)
        {
            ResendsRefused++;
            return false;
        }

        if (state.ResendAt != long.MinValue && nowMicros - state.ResendAt < ResendGapMicros)
        {
            ResendsRefused++;
            return false;
        }

        uint low = Math.Max(Math.Max(from, 1u), EntitledFrom(friend));
        uint high = Math.Min(Math.Min(to, LastSequence), state.Next - 1);
        if (low > high)
        {
            ResendsRefused++;
            return false;
        }

        state.ResendAt = nowMicros;
        AddRange(state.Resends, low, high);
        state.LastStatus = null; // the friend learns what's kept for it with the next status
        Resends++;
        (first, last) = (low, high);
        return true;
    }

    /// <summary>
    /// The status <paramref name="friend"/> should get now (true), or false: when they join, when the host's sharing setting
    /// (<paramref name="sharing"/>) differs from what they were last told, after a resend, and
    /// <see cref="StatusEveryMicros"/> after the newest batch (or what's kept for them) changed since their last status (by
    /// then the batches have gone; a friend missing the last of them learns it here). <paramref name="immediately"/> (the
    /// hosting session is closing, after its last batches): a changed status is due at once. <see cref="MarkStatusSent"/>
    /// once it went.
    /// </summary>
    public bool TryStatus(ulong friend, bool sharing, long nowMicros, out ProgressStatus status, bool immediately = false)
    {
        status = default;
        if (!_friends.TryGetValue(friend, out FriendState? state) || !state.Present)
        {
            return false;
        }

        var now = new ProgressStatus(Session, sharing, EntitledFrom(friend), LastSequence);
        if (state.LastStatus is ProgressStatus last && last.Sharing == sharing)
        {
            if (last == now)
            {
                state.ChangedAt = long.MinValue;
                return false;
            }

            if (state.ChangedAt == long.MinValue)
            {
                state.ChangedAt = nowMicros; // the batches go first: the status follows a while after
            }

            if (!immediately && nowMicros - state.ChangedAt < StatusEveryMicros)
            {
                return false;
            }
        }

        status = now;
        return true;
    }

    /// <summary><paramref name="friend"/> was sent <paramref name="status"/>.</summary>
    public void MarkStatusSent(ulong friend, ProgressStatus status)
    {
        if (_friends.TryGetValue(friend, out FriendState? state))
        {
            state.LastStatus = status;
            state.ChangedAt = long.MinValue;
        }
    }

    /// <summary>
    /// Adds batches <paramref name="from"/> to <paramref name="to"/> to a friend's ranges to send again, merging overlaps;
    /// beyond <see cref="MaxResendRanges"/> the latest are left out (the friend asks for them again).
    /// </summary>
    private static void AddRange(List<(uint From, uint To)> ranges, uint from, uint to)
    {
        ranges.Add((from, to));
        ranges.Sort((a, b) => a.From.CompareTo(b.From));
        for (int i = 1; i < ranges.Count; i++)
        {
            if ((ulong)ranges[i].From <= (ulong)ranges[i - 1].To + 1)
            {
                ranges[i - 1] = (ranges[i - 1].From, Math.Max(ranges[i - 1].To, ranges[i].To));
                ranges.RemoveAt(i);
                i--;
            }
        }

        while (ranges.Count > MaxResendRanges)
        {
            ranges.RemoveAt(ranges.Count - 1);
        }
    }

    /// <summary>Keeps batch <paramref name="sequence"/> (it must be <see cref="NextSequence"/>) for every entitled friend.</summary>
    public void Add(uint sequence, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (sequence != NextSequence)
        {
            throw new ArgumentException($"Batch {sequence} out of order (next is {NextSequence}).", nameof(sequence));
        }

        LastSequence = sequence;
        _batches.Add((sequence, body));
        _storedBytes += body.Length;
        while (_storedBytes > MaxStoredBytes && _batches.Count > 1)
        {
            _storedBytes -= _batches[0].Body.Length;
            _batches.RemoveAt(0);
            Dropped++;
        }
    }

    /// <summary>
    /// A friend joined: one new to the session gets batches from the next one on; one coming back gets every batch since
    /// they first joined again (their ledger skips what they applied).
    /// </summary>
    public void FriendJoined(ulong friend)
    {
        if (_friends.TryGetValue(friend, out FriendState? state))
        {
            state.Present = true;
            state.Next = state.First;
            state.Resends.Clear(); // everything since their first join comes again anyway
            state.LastStatus = null;
            return;
        }

        _friends[friend] = new FriendState(NextSequence) { Present = true };
    }

    /// <summary>A friend left: nothing goes to them until they're back, and what's made meanwhile is kept for them.</summary>
    public void FriendLeft(ulong friend)
    {
        if (_friends.TryGetValue(friend, out FriendState? state))
        {
            state.Present = false;
        }
    }

    /// <summary>
    /// The next batch for <paramref name="friend"/> (here now), with the first batch the outbox still keeps for them
    /// (<paramref name="entitledFrom"/>: earlier ones aren't coming). False when they have everything.
    /// </summary>
    public bool TryNext(ulong friend, out uint sequence, out uint entitledFrom, out byte[] body)
    {
        sequence = entitledFrom = 0;
        body = Array.Empty<byte>();
        if (!_friends.TryGetValue(friend, out FriendState? state) || !state.Present || _batches.Count == 0)
        {
            return false;
        }

        uint oldest = _batches[0].Sequence;
        entitledFrom = Math.Max(state.First, oldest);

        // Batches asked for again go first (those dropped for space since are skipped).
        while (state.Resends.Count > 0)
        {
            (uint from, uint to) = state.Resends[0];
            from = Math.Max(from, entitledFrom);
            if (from > to)
            {
                state.Resends.RemoveAt(0);
                continue;
            }

            state.Resends[0] = (from, to);
            (sequence, body) = _batches[(int)(from - oldest)];
            return true;
        }

        uint next = Math.Max(state.Next, oldest);
        if (next > LastSequence)
        {
            return false;
        }

        (sequence, body) = _batches[(int)(next - oldest)];
        return true;
    }

    /// <summary><paramref name="friend"/> was sent batch <paramref name="sequence"/>: the next one is theirs next.</summary>
    public void MarkSent(ulong friend, uint sequence)
    {
        if (!_friends.TryGetValue(friend, out FriendState? state))
        {
            return;
        }

        if (state.Resends.Count > 0 && state.Resends[0].From == sequence)
        {
            (uint from, uint to) = state.Resends[0];
            if (from >= to)
            {
                state.Resends.RemoveAt(0);
            }
            else
            {
                state.Resends[0] = (from + 1, to);
            }

            return;
        }

        if (sequence >= state.Next)
        {
            state.Next = sequence + 1;
        }
    }

    private sealed class FriendState
    {
        public FriendState(uint first)
        {
            First = first;
            Next = first;
        }

        public uint First { get; }

        public uint Next { get; set; }

        public bool Present { get; set; }

        /// <summary>Ranges of batches asked for again, in order (each already sent once).</summary>
        public List<(uint From, uint To)> Resends { get; } = new();

        /// <summary>When the last request to send again was taken.</summary>
        public long ResendAt { get; set; } = long.MinValue;

        /// <summary>The last status sent (null: none yet, or one is due).</summary>
        public ProgressStatus? LastStatus { get; set; }

        /// <summary>When the status was first seen to differ from the last one sent (MinValue: it doesn't).</summary>
        public long ChangedAt { get; set; } = long.MinValue;
    }
}

/// <summary>How a friend's inbox took a batch.</summary>
public enum ProgressOffer
{
    /// <summary>Kept, to be applied in order.</summary>
    Accepted,

    /// <summary>Applied before (or already waiting): ignored.</summary>
    Duplicate,

    /// <summary>
    /// Too many waiting: not kept, and remembered: once there's room it's asked for again (<see cref="ProgressInbox.TryRequest"/>).
    /// A gap nothing fills is given up on and remembered as missing, so it still applies if a resend or a rejoin of the same
    /// hosting session (which sends every batch again) brings it.
    /// </summary>
    Refused,

    /// <summary>
    /// For another hosting session than the one this PC's connection was welcomed into: not kept, counted
    /// (<see cref="ProgressInbox.WrongSession"/>), and the inbox stays on its session. A host's progress counts only for the
    /// session a friend joined; a new one needs a new welcome (D135).
    /// </summary>
    WrongSession,
}

/// <summary>
/// A friend's progress inbox: batches are applied once each, in the host's order. What was applied is kept in the
/// <see cref="ProgressLedger"/> (saved on the friend's PC), so a batch the host sends again after a rejoin, a resend
/// request, or after this game restarted, is never applied twice. Missing batches (a gap in the numbers, batches refused
/// while too many waited, the newest ones the host's <see cref="ProgressStatus"/> names, batches given up on earlier) are
/// asked for again (<see cref="TryRequest"/>). A gap that nothing fills within <see cref="GapGiveUpMicros"/> is
/// skipped (logged), so one lost batch can't hold up the rest; the ledger remembers the skipped batches as missing, so a
/// resend or a rejoin of the same hosting session (the host sends every batch since the friend's first join again) still
/// applies them, late. Batches and status count only for the hosting session this PC's connection was welcomed into
/// (the expected session every <see cref="Offer"/> and <see cref="Status"/> is given): a message naming any other is
/// refused and counted, so a host can't start its session's limits over by naming a new session in each message (D135).
/// Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class ProgressInbox
{
    /// <summary>Most batches waiting (batches older than every one waiting are still taken, up to twice this: they go first).</summary>
    public const int MaxHeld = 256;

    /// <summary>A missing batch is given up on after this long.</summary>
    public const long GapGiveUpMicros = 30_000_000;

    /// <summary>Missing batches are asked for this long after they're first seen missing (a rejoin's resend may be on its way).</summary>
    public const long AskAfterMicros = 1_000_000;

    /// <summary>The same missing batches are asked for again after this long (the host takes a request every 2 s).</summary>
    public const long AskAgainMicros = 5_000_000;

    /// <summary>The same missing batches are asked for at most this many times (then the gap is given up on, or a rejoin brings them).</summary>
    public const int MaxAsks = 3;

    private readonly ProgressLedger _ledger;
    private readonly SortedDictionary<uint, ProgressBatch> _held = new();
    private long _gapSince = -1;
    private uint _refusedFrom;
    private uint _refusedTo;
    private uint _askFrom;
    private int _asks;
    private long _askAt = long.MinValue;
    private long _missingSince = long.MinValue;

    /// <summary>An inbox that remembers what it applied in <paramref name="ledger"/>.</summary>
    public ProgressInbox(ProgressLedger ledger) => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>The host's session the inbox is on (0: none yet).</summary>
    public ulong Session { get; private set; }

    /// <summary>Every batch of <see cref="Session"/> up to this one is applied (or was never meant for this friend).</summary>
    public uint AppliedThrough { get; private set; }

    /// <summary>Batches waiting.</summary>
    public int Held => _held.Count;

    /// <summary>Batches received again and ignored.</summary>
    public int Duplicates { get; private set; }

    /// <summary>Batches given up on (missing for <see cref="GapGiveUpMicros"/>); never past the largest whole number.</summary>
    public int Skipped { get; private set; }

    /// <summary>Batches given up on earlier that came after all (a rejoin sent them again) and were taken, late.</summary>
    public int Late { get; private set; }

    /// <summary>
    /// Batches the host said this PC isn't entitled to (made before it first joined the hosting session, or dropped from
    /// the host's outbox for space): never coming, so never waited for.
    /// </summary>
    public long NotEntitled { get; private set; }

    /// <summary>The host's newest status for <see cref="Session"/> (null: none came: an older host, or none yet).</summary>
    public ProgressStatus? HostStatus { get; private set; }

    /// <summary>Requests to send missing batches again that <see cref="TryRequest"/> made.</summary>
    public int Requests { get; private set; }

    /// <summary>
    /// Batches and status refused for naming another hosting session than the expected one (<see cref="ProgressOffer.WrongSession"/>).
    /// </summary>
    public long WrongSession { get; private set; }

    /// <summary>
    /// Takes a batch the host sent (see <see cref="ProgressMessage"/>), if it's for <paramref name="expected"/>: the hosting
    /// session of this PC's current welcome (0 while there is none: everything is refused).
    /// </summary>
    public ProgressOffer Offer(ProgressMessage message, ulong expected)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (expected == 0 || message.Session != expected)
        {
            WrongSession++;
            return ProgressOffer.WrongSession;
        }

        SwitchTo(message.Session);
        Entitle(message.EntitledFrom);
        uint sequence = message.Batch.Sequence;
        if (_held.ContainsKey(sequence) || (sequence <= AppliedThrough && !_ledger.IsMissing(Session, sequence)))
        {
            Duplicates++;
            return ProgressOffer.Duplicate;
        }

        // Full, a batch is still taken when it's older than every one waiting (it's the one they wait for), up to twice full.
        bool first = _held.Count == 0 || sequence < _held.First().Key;
        if (_held.Count >= MaxHeld && (!first || _held.Count >= 2 * MaxHeld))
        {
            _refusedFrom = _refusedFrom == 0 ? sequence : Math.Min(_refusedFrom, sequence);
            _refusedTo = Math.Max(_refusedTo, sequence);
            return ProgressOffer.Refused;
        }

        _held[sequence] = message.Batch;
        return ProgressOffer.Accepted;
    }

    /// <summary>
    /// The host's status (<see cref="ProgressStatus"/>): what it shares, the first batch it keeps for this PC (earlier ones
    /// aren't coming, as with a batch's own) and its newest batch (later missing ones are asked for). False, counted in
    /// <see cref="WrongSession"/> and ignored, when it isn't for <paramref name="expected"/> (as <see cref="Offer"/>).
    /// </summary>
    public bool Status(ProgressStatus status, ulong expected)
    {
        if (expected == 0 || status.Session != expected)
        {
            WrongSession++;
            return false;
        }

        SwitchTo(status.Session);
        Entitle(status.EntitledFrom);
        HostStatus = status;
        return true;
    }

    /// <summary>
    /// The first batches missing now, to ask the host for again (true), or false. Missing: a gap below a batch waiting, the
    /// batches refused while too many waited, the newest ones the host's status names, or (when nothing else is) batches
    /// of this session given up on earlier that the host may still keep. They're asked for <see cref="AskAfterMicros"/>
    /// after they're first seen missing, again every <see cref="AskAgainMicros"/>, at most <see cref="MaxAsks"/> times.
    /// <paramref name="to"/> is <see cref="ProgressResendMessage.ToNewest"/> when the end isn't known.
    /// </summary>
    public bool TryRequest(long nowMicros, out uint from, out uint to)
    {
        if (!TryFirstMissing(out from, out to))
        {
            _askFrom = 0;
            _asks = 0;
            _missingSince = long.MinValue;
            return false;
        }

        if (from != _askFrom)
        {
            _askFrom = from;
            _asks = 0;
            _missingSince = nowMicros;
        }

        bool due = _asks == 0 ? nowMicros - _missingSince >= AskAfterMicros : _asks < MaxAsks && nowMicros - _askAt >= AskAgainMicros;
        if (!due)
        {
            return false;
        }

        _asks++;
        _askAt = nowMicros;
        Requests++;
        return true;
    }

    /// <summary>The first range of batches missing now (see <see cref="TryRequest"/>), or false.</summary>
    private bool TryFirstMissing(out uint from, out uint to)
    {
        from = to = 0;
        if (Session == 0)
        {
            return false;
        }

        if (_refusedTo != 0 && _refusedTo <= AppliedThrough)
        {
            _refusedFrom = _refusedTo = 0; // they came after all
        }

        // The newest batch this PC knows of: one waiting, one it refused, or the host's newest.
        uint known = Math.Max(AppliedThrough, Math.Max(_refusedTo, HostStatus?.Newest ?? 0));
        if (_held.Count > 0)
        {
            known = Math.Max(known, _held.Keys.Last());
        }

        uint next = AppliedThrough + 1;
        while (next <= known && next != 0 && _held.ContainsKey(next))
        {
            next++;
        }

        if (next != 0 && next <= known)
        {
            from = next;
            uint after = 0;
            foreach (uint key in _held.Keys)
            {
                if (key > next)
                {
                    after = key;
                    break;
                }
            }

            to = after != 0 ? after - 1 : known;
            return true;
        }

        // Nothing missing ahead: batches given up on earlier, which the host may still keep.
        return _ledger.TryFirstMissing(Session, HostStatus?.EntitledFrom ?? 1, out from, out to);
    }

    /// <summary>Another hosting session (the host started hosting again): its own numbering, from the ledger.</summary>
    private void SwitchTo(ulong session)
    {
        if (session == Session)
        {
            return;
        }

        Session = session;
        AppliedThrough = _ledger.AppliedThrough(session);
        _held.Clear();
        _gapSince = -1;
        HostStatus = null;
        _refusedFrom = _refusedTo = 0;
        _askFrom = 0;
        _asks = 0;
        _missingSince = long.MinValue;
    }

    /// <summary>The batches before <paramref name="entitledFrom"/> were made before this PC joined (or dropped for space): none of them are coming.</summary>
    private void Entitle(uint entitledFrom)
    {
        if (entitledFrom <= (ulong)AppliedThrough + 1)
        {
            return;
        }

        uint before = AppliedThrough;
        NotEntitled += entitledFrom - 1 - AppliedThrough;
        AppliedThrough = entitledFrom - 1;
        _ledger.SetApplied(Session, AppliedThrough);
        foreach (uint old in _held.Keys.Where(k => k > before && k <= AppliedThrough).ToList())
        {
            _held.Remove(old);
        }
    }

    /// <summary>
    /// The next batch to apply, in order, or false. A gap (the next batch missing while later ones wait) is waited out
    /// for <see cref="GapGiveUpMicros"/>, then skipped: <paramref name="skipped"/> says how many were given up (the ledger
    /// keeps them as missing). A batch given up on that comes after all is next at once. <paramref name="giveUpOnGap"/>: the
    /// session is ending, so nothing would fill a gap now: it's skipped at once, and what came after it applies.
    /// </summary>
    public bool TryPeek(long nowMicros, out ProgressBatch? batch, out uint skipped, bool giveUpOnGap = false)
    {
        batch = null;
        skipped = 0;
        if (_held.Count == 0)
        {
            _gapSince = -1;
            return false;
        }

        KeyValuePair<uint, ProgressBatch> first = _held.First();
        if (first.Key > (ulong)AppliedThrough + 1)
        {
            if (_gapSince < 0)
            {
                _gapSince = nowMicros;
            }

            if (!giveUpOnGap && nowMicros - _gapSince < GapGiveUpMicros)
            {
                return false;
            }

            skipped = first.Key - AppliedThrough - 1;
            Skipped = (int)Math.Min(int.MaxValue, Skipped + (long)skipped); // a host may number a batch billions ahead
            _ledger.AddMissing(Session, AppliedThrough + 1, first.Key - 1);
            AppliedThrough = first.Key - 1;
            _ledger.SetApplied(Session, AppliedThrough);
        }

        _gapSince = -1;
        batch = first.Value;
        return true;
    }

    /// <summary>
    /// True while a gap is being waited out: <paramref name="from"/> is the first batch missing. A session can ask the host
    /// to send again from there (a rejoin does: the host sends every batch since this PC's first join).
    /// </summary>
    public bool Waiting(out uint from)
    {
        from = AppliedThrough + 1;
        return _held.Count > 0 && _held.First().Key > (ulong)AppliedThrough + 1;
    }

    /// <summary>The batch from <see cref="TryPeek"/> was applied (or given up on): it's done, and the ledger says so.</summary>
    public void Done(ProgressBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_held.Remove(batch.Sequence))
        {
            return;
        }

        if (batch.Sequence <= AppliedThrough)
        {
            // A batch given up on earlier, applied late: it's no longer missing.
            _ledger.RemoveMissing(Session, batch.Sequence);
            Late++;
            return;
        }

        AppliedThrough = batch.Sequence;
        _ledger.SetApplied(Session, AppliedThrough);
    }

    /// <summary>True when a waiting batch matches <paramref name="predicate"/>.</summary>
    public bool AnyWaiting(Func<ProgressBatch, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return _held.Values.Any(predicate);
    }

    /// <summary>Drops what's waiting (the session ended). The ledger keeps what was applied, and what never came.</summary>
    public void Clear()
    {
        _held.Clear();
        _gapSince = -1;
        _refusedFrom = _refusedTo = 0;
        _askFrom = 0;
        _asks = 0;
        _missingSince = long.MinValue;
    }
}

/// <summary>
/// Dated copies of a friend's <see cref="ProgressLedger"/>, taken as each following session starts and ends (the only
/// times it changes are in between), so that a restore of older saves can bring the ledger back to the same moment: the
/// ledger says which batches the profile and stats already hold, and one newer than the restored saves would make a rejoin
/// of a hosting session still running skip what the restore took away. Names say when (UTC) and which end of a session.
/// Game-independent.
/// </summary>
public static class ProgressLedgerHistory
{
    /// <summary>Copies kept (the newest, and always the oldest); backups keep 10 and a session makes two.</summary>
    public const int Kept = 24;

    /// <summary>A session-start copy taken this long after a backup still counts as of the backup (it's taken right after it).</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(30);

    private const string Prefix = "ledger-";
    private const string StartSuffix = "Z-start.txt";
    private const string EndSuffix = "Z-end.txt";
    private const string TimeFormat = "yyyyMMdd'T'HHmmssfff";

    /// <summary>A copy's file name: taken at <paramref name="utc"/>, as a session <paramref name="start"/>s or ends.</summary>
    public static string Name(DateTime utc, bool start) =>
        $"{Prefix}{utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture)}{(start ? StartSuffix : EndSuffix)}";

    /// <summary>When a copy named <paramref name="name"/> was taken, and at which end of a session; false for any other name.</summary>
    public static bool TryParse(string? name, out DateTime utc, out bool start)
    {
        utc = default;
        start = false;
        if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        start = name.EndsWith(StartSuffix, StringComparison.Ordinal);
        if (!start && !name.EndsWith(EndSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        string time = name[Prefix.Length..^(start ? StartSuffix.Length : EndSuffix.Length)];
        return DateTime.TryParseExact(time, TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    /// <summary>
    /// The copy that holds the ledger as it was when a backup was taken at <paramref name="backupUtc"/>: the first
    /// session-start copy taken with it or just after it (<see cref="StartTolerance"/>: the session the backup was taken for
    /// copies the ledger right after its backup, so that copy is the ledger the backup's saves go with); with none, the
    /// newest taken before it. The start copy comes first because a copy before the backup can be older than the saves in
    /// it: a session the game stopped in the middle of leaves no end copy, and its batches, in the next backup's saves,
    /// would apply a second time under the ledger from its start. Null when there's none (the ledger of that moment isn't
    /// known).
    /// </summary>
    public static string? Pick(IEnumerable<string> names, DateTime backupUtc)
    {
        ArgumentNullException.ThrowIfNull(names);
        backupUtc = backupUtc.ToUniversalTime();
        var copies = new List<(string Name, DateTime Utc, bool Start)>();
        foreach (string name in names)
        {
            if (TryParse(name, out DateTime utc, out bool start))
            {
                copies.Add((name, utc, start));
            }
        }

        string? own = copies.Where(c => c.Start && c.Utc >= backupUtc && c.Utc - backupUtc <= StartTolerance).OrderBy(c => c.Utc).Select(c => c.Name).FirstOrDefault();
        return own ?? copies.Where(c => c.Utc <= backupUtc).OrderByDescending(c => c.Utc).Select(c => c.Name).FirstOrDefault();
    }

    /// <summary>The copies to delete: all but the newest <see cref="Kept"/> and the oldest.</summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var copies = new List<(string Name, DateTime Utc)>();
        foreach (string name in names)
        {
            if (TryParse(name, out DateTime utc, out _))
            {
                copies.Add((name, utc));
            }
        }

        if (copies.Count <= Kept + 1)
        {
            return Array.Empty<string>();
        }

        List<(string Name, DateTime Utc)> oldestFirst = copies.OrderBy(c => c.Utc).ToList();
        return oldestFirst.Skip(1).Take(oldestFirst.Count - 1 - Kept).Select(c => c.Name).ToList();
    }
}

/// <summary>
/// What one hosting session has added to (and taken off) each stat on a friend's profile: a batch is capped on its own
/// (<see cref="ProgressLimits.MaxStatCreditPerBatch"/>), and this caps the session
/// (<see cref="ProgressLimits.MaxStatCreditPerSession"/> added, <see cref="ProgressLimits.MaxStatDecrementPerSession"/>
/// taken off), so batch after batch can't pump a stat or wear it down without limit. With a ledger and a host key, the
/// operation must also fit what this PC's own play with that host in the last 24 hours allows (D135): added credit within
/// the host's rolling budget for the stat (<see cref="ProgressLedger.TrySpend"/>), so hosting again (a new session)
/// doesn't start the limits over; a record no larger than all that play could hold of the stat
/// (<see cref="ProgressLedger.RecordBound"/>); a record set lower, and the win streak's reset, only after a match played
/// here (<see cref="ProgressLedger.PlayedMatchWithin"/>). A record raised to another stat's own value is this profile's
/// own and passes. Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class ProgressCredit
{
    private readonly Dictionary<int, double> _given = new();
    private readonly Dictionary<int, double> _taken = new();
    private ulong _session;

    /// <summary>Stat operations refused for going over the session's cap.</summary>
    public int Refused { get; private set; }

    /// <summary>
    /// Stat operations refused for going past what this PC's play with the host allows (the host's 24-hour budget,
    /// <see cref="ProgressLedger.TrySpend"/>, or a record's bound).
    /// </summary>
    public int OverBudget { get; private set; }

    /// <summary>
    /// True (and counted) when <paramref name="entry"/> fits in what hosting session <paramref name="session"/> may still
    /// add to (or take off) its stat; false (counted as refused) when it would go over. Another session starts from
    /// nothing.
    /// </summary>
    public bool TryGive(ulong session, StatEntry entry) => TryGive(session, entry, null, string.Empty, default);

    /// <summary>
    /// As <see cref="TryGive(ulong, StatEntry)"/>, and also within what this PC's play with host <paramref name="host"/>
    /// allows in <paramref name="ledger"/> at <paramref name="utc"/> (see the class; counted there only when it fits both;
    /// false, counted in <see cref="OverBudget"/>, when it doesn't). No ledger: the session's cap only.
    /// </summary>
    public bool TryGive(ulong session, StatEntry entry, ProgressLedger? ledger, string host, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(host);
        if (session != _session)
        {
            _session = session;
            _given.Clear();
            _taken.Clear();
        }

        double credit = ProgressLimits.CreditOf(entry);
        if (credit == 0)
        {
            if (ledger == null || StatOps.IsFromStat(entry.Op) || (!StatOps.IsRecord(entry.Op) && !StatOps.IsReset(entry.Op)))
            {
                return true;
            }

            if (!FitsPlay(entry, ledger, host, utc))
            {
                OverBudget++;
                return false;
            }

            return true;
        }

        Dictionary<int, double> counts = credit < 0 ? _taken : _given;
        double total = counts.GetValueOrDefault(entry.Stat) + Math.Abs(credit);
        if (total > (credit < 0 ? ProgressLimits.MaxStatDecrementPerSession : ProgressLimits.MaxStatCreditPerSession))
        {
            Refused++;
            return false;
        }

        if (ledger != null && !ledger.TrySpend(host, entry.Stat, credit, utc))
        {
            OverBudget++;
            return false;
        }

        counts[entry.Stat] = total;
        return true;
    }

    /// <summary>A record or a reset (they add nothing) against this PC's play with the host.</summary>
    private static bool FitsPlay(StatEntry entry, ProgressLedger ledger, string host, DateTime utc)
    {
        if (StatOps.IsReset(entry.Op) || entry.Op is StatOp.TrySetSmaller or StatOp.TrySetSmallerCampaign or StatOp.TrySetSmallerPlayNow)
        {
            return ledger.PlayedMatchWithin(host, utc);
        }

        double value = entry.Value.Kind == StatValueKind.Real ? entry.Value.RealValue : entry.Value.WholeValue;
        return value <= ledger.RecordBound(host, entry.Stat, utc);
    }
}

/// <summary>
/// What a friend's PC applied of each host session's progress (and which batches before that it gave up on, so they still
/// apply if they come again), each shared run's bench quest counts (the per-run counts belong to the run, and a co-op run
/// can go on in a later session), which once-per-run parts of a shared run's end (its squad record, its run history
/// entry) it applied, the new unlocks shared progress gave this profile in each shared run, for that run's new-unlocks
/// screen (the game keeps a run's new unlocks in the run, which on a friend's PC is the host's: a co-op run that goes on
/// over several sessions or game starts shows them all at its end, as the game shows its own run's), and each host's
/// rolling 24-hour budget per stat (<see cref="TrySpend"/>, D135) with the play this PC saw with that host in the same
/// hours (<see cref="NotePlay"/>), which bounds it (<see cref="StatBounds"/>), by a key this PC makes from the host's ID
/// that names nobody. Saved as a small text file by the game glue; parsing is tolerant (a bad line is skipped, and a version that
/// doesn't know a kind of line skips it). Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class ProgressLedger
{
    /// <summary>Hosts whose budgets are kept: beyond it, the host used longest ago goes.</summary>
    public const int MaxBudgetHosts = 64;

    /// <summary>The budgets' window, in hours: what a host gave in the last this many (each hour a bucket) counts.</summary>
    public const int BudgetHours = 24;

    /// <summary>Sessions remembered (newest kept).</summary>
    public const int MaxSessions = 32;

    /// <summary>Shared runs remembered (newest kept).</summary>
    public const int MaxRuns = 32;

    /// <summary>Once-per-run parts remembered (newest kept): up to four a run (squad, history, ending, unlocks).</summary>
    public const int MaxOnce = 96;

    /// <summary>Ranges of missing batches remembered (newest kept).</summary>
    public const int MaxMissing = 64;

    /// <summary>The bench quest counts kept per run.</summary>
    public const int RunCounters = 5;

    /// <summary>New unlocks remembered for the runs' new-unlocks screens, in all (oldest dropped first).</summary>
    public const int MaxUnlocks = 256;

    /// <summary>The once-per-run part marking that a run's new-unlocks screen had its turn (<see cref="UnlocksShown"/>).</summary>
    public const string OnceUnlocksShown = "unlocks";

    // What separates the words of a line, the CR of a CR LF line ending included.
    private static readonly char[] Blanks = { ' ', '\t', '\r' };

    private readonly List<(ulong Session, uint Through)> _sessions = new();
    private readonly List<(string Run, int[] Counters)> _runs = new();
    private readonly List<(string Part, string Run)> _once = new();
    private readonly List<(ulong Session, uint From, uint To)> _missing = new();
    private readonly List<(string Run, string Id)> _unlocks = new();
    private readonly List<HostBudget> _budgets = new();
    private long _prunedAt = long.MinValue;

    /// <summary>True when something changed since the last <see cref="Format"/>.</summary>
    public bool Changed { get; private set; }

    /// <summary>The newest batch applied from <paramref name="session"/> (0: none).</summary>
    public uint AppliedThrough(ulong session)
    {
        foreach ((ulong id, uint through) in _sessions)
        {
            if (id == session)
            {
                return through;
            }
        }

        return 0;
    }

    /// <summary>Every batch of <paramref name="session"/> up to <paramref name="through"/> is applied.</summary>
    public void SetApplied(ulong session, uint through)
    {
        int at = _sessions.FindIndex(s => s.Session == session);
        if (at >= 0)
        {
            if (_sessions[at].Through == through && at == _sessions.Count - 1)
            {
                return;
            }

            _sessions.RemoveAt(at);
        }

        _sessions.Add((session, through));
        if (_sessions.Count > MaxSessions)
        {
            ulong forgotten = _sessions[0].Session;
            _sessions.RemoveAt(0);
            _missing.RemoveAll(m => m.Session == forgotten);
        }

        Changed = true;
    }

    /// <summary>True when batch <paramref name="sequence"/> of <paramref name="session"/> was given up on and hasn't come since.</summary>
    public bool IsMissing(ulong session, uint sequence) => _missing.Exists(m => m.Session == session && sequence >= m.From && sequence <= m.To);

    /// <summary>How many batches of <paramref name="session"/> were given up on and haven't come since.</summary>
    public long MissingCount(ulong session) => _missing.Where(m => m.Session == session).Sum(m => (long)m.To - m.From + 1);

    /// <summary>
    /// The lowest range of batches of <paramref name="session"/> given up on and not come since, from
    /// <paramref name="atLeast"/> on (earlier ones aren't kept for this PC any more), or false.
    /// </summary>
    public bool TryFirstMissing(ulong session, uint atLeast, out uint from, out uint to)
    {
        from = to = 0;

        // Nearly always none, and asked every frame of a friend's session: the search (and its closure) is apart from this.
        return _missing.Count > 0 && TryFindMissing(session, atLeast, out from, out to);
    }

    private bool TryFindMissing(ulong session, uint atLeast, out uint from, out uint to)
    {
        from = to = 0;
        foreach (var range in _missing.Where(m => m.Session == session).OrderBy(m => m.From))
        {
            if (range.To >= atLeast)
            {
                (from, to) = (Math.Max(range.From, atLeast), range.To);
                return true;
            }
        }

        return false;
    }

    /// <summary>Batches <paramref name="from"/> to <paramref name="to"/> of <paramref name="session"/> were given up on: if they come again, they apply.</summary>
    public void AddMissing(ulong session, uint from, uint to)
    {
        if (from == 0 || to < from)
        {
            return;
        }

        _missing.Add((session, from, to));
        if (_missing.Count > MaxMissing)
        {
            _missing.RemoveAt(0);
        }

        Changed = true;
    }

    /// <summary>
    /// Batch <paramref name="sequence"/> of <paramref name="session"/>, given up on before, came and was applied. One out of
    /// the middle of a range leaves two; beyond <see cref="MaxMissing"/> the oldest range goes, as in <see cref="AddMissing"/>
    /// (its batches no longer apply if they come: a host that numbers a batch far ahead and then fills the gap one batch in
    /// two can't grow the ledger).
    /// </summary>
    public void RemoveMissing(ulong session, uint sequence)
    {
        int at = _missing.FindIndex(m => m.Session == session && sequence >= m.From && sequence <= m.To);
        if (at < 0)
        {
            return;
        }

        (ulong id, uint from, uint to) = _missing[at];
        _missing.RemoveAt(at);
        if (sequence < to)
        {
            _missing.Insert(at, (id, sequence + 1, to));
        }

        if (sequence > from)
        {
            _missing.Insert(at, (id, from, sequence - 1));
        }

        while (_missing.Count > MaxMissing)
        {
            _missing.RemoveAt(0);
        }

        Changed = true;
    }

    /// <summary>Shared run <paramref name="run"/>'s bench quest counts, or false when none are kept.</summary>
    public bool TryGetRun(string run, out int[] counters)
    {
        foreach ((string id, int[] kept) in _runs)
        {
            if (string.Equals(id, run, StringComparison.Ordinal))
            {
                counters = (int[])kept.Clone();
                return true;
            }
        }

        counters = Array.Empty<int>();
        return false;
    }

    /// <summary>Keeps shared run <paramref name="run"/>'s bench quest counts (<see cref="RunCounters"/> of them).</summary>
    public void SetRun(string run, IReadOnlyList<int> counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        if (!ProgressLimits.IsPlausibleId(run) || counters.Count != RunCounters)
        {
            throw new ArgumentException("A run id and its counts.", nameof(counters));
        }

        _runs.RemoveAll(r => string.Equals(r.Run, run, StringComparison.Ordinal));
        _runs.Add((run, counters.ToArray()));
        if (_runs.Count > MaxRuns)
        {
            _runs.RemoveAt(0);
        }

        Changed = true;
    }

    /// <summary>
    /// Marks <paramref name="part"/> (an id such as "squad" or "history": what a run's end gives once) of shared run
    /// <paramref name="run"/> as applied. False when it already was: whichever batch or session brings it again, it isn't
    /// applied twice.
    /// </summary>
    public bool TryMarkOnce(string part, string run)
    {
        if (!ProgressLimits.IsPlausibleId(part) || !ProgressLimits.IsPlausibleId(run))
        {
            throw new ArgumentException("A part and a run id.", nameof(run));
        }

        if (_once.Exists(o => string.Equals(o.Part, part, StringComparison.Ordinal) && string.Equals(o.Run, run, StringComparison.Ordinal)))
        {
            return false;
        }

        _once.Add((part, run));
        if (_once.Count > MaxOnce)
        {
            _once.RemoveAt(0);
        }

        Changed = true;
        return true;
    }

    /// <summary>
    /// Shared progress gave this profile new unlock <paramref name="id"/> in shared run <paramref name="run"/>: it's kept for
    /// that run's new-unlocks screen. False when it isn't (already kept, the run's screen already had its turn, or an id that
    /// isn't plausible); at most <see cref="MaxUnlocks"/> in all, the oldest dropped first.
    /// </summary>
    public bool NoteUnlock(string? run, string? id)
    {
        if (!ProgressLimits.IsPlausibleId(run) || !ProgressLimits.IsPlausibleId(id) || ScreenHadItsTurn(run!) ||
            _unlocks.Exists(u => string.Equals(u.Run, run, StringComparison.Ordinal) && string.Equals(u.Id, id, StringComparison.Ordinal)))
        {
            return false;
        }

        _unlocks.Add((run!, id!));
        if (_unlocks.Count > MaxUnlocks)
        {
            _unlocks.RemoveAt(0);
        }

        Changed = true;
        return true;
    }

    /// <summary>The new unlocks kept for shared run <paramref name="run"/>'s new-unlocks screen, in the order they came.</summary>
    public IReadOnlyList<string> UnlocksOf(string? run)
    {
        if (string.IsNullOrEmpty(run) || _unlocks.Count == 0)
        {
            return Array.Empty<string>();
        }

        return _unlocks.Where(u => string.Equals(u.Run, run, StringComparison.Ordinal)).Select(u => u.Id).ToArray();
    }

    /// <summary>
    /// Shared run <paramref name="run"/>'s new-unlocks screen had its turn (the host's closed): its kept unlocks go, and later
    /// ones of that run (its last progress applying after the screen) aren't kept. Returns how many went.
    /// </summary>
    public int UnlocksShown(string? run)
    {
        if (!ProgressLimits.IsPlausibleId(run))
        {
            return 0;
        }

        int forgotten = _unlocks.RemoveAll(u => string.Equals(u.Run, run, StringComparison.Ordinal));
        bool marked = TryMarkOnce(OnceUnlocksShown, run!);
        Changed |= forgotten > 0 || marked;
        return forgotten;
    }

    /// <summary>Hosts whose budgets are kept now.</summary>
    public int BudgetHostCount => _budgets.Count;

    /// <summary>
    /// What host <paramref name="host"/> (its key) added to and took off <paramref name="stat"/> here in the
    /// <see cref="BudgetHours"/> up to <paramref name="utc"/>.
    /// </summary>
    public (double Added, double Taken) BudgetUsed(string host, int stat, DateTime utc)
    {
        HostBudget? budget = FindBudget(host);
        return budget == null ? (0, 0) : budget.Used(stat, HourOf(utc));
    }

    /// <summary>
    /// This PC played <paramref name="play"/> with host <paramref name="host"/> (its key) at <paramref name="utc"/>: counted in
    /// that hour, so what the host may credit here grows with it (<see cref="StatBounds"/>). Nothing for no play or a value
    /// that isn't plausible.
    /// </summary>
    public void NotePlay(string host, DateTime utc, PlayTally play)
    {
        CheckHost(host);
        if (play.IsEmpty || !play.IsPlausible())
        {
            return;
        }

        long now = HourOf(utc);
        Prune(now);
        HostBudget budget = FindBudget(host) ?? AddBudget(host);
        budget.AddPlay(now, play);
        DropOldestBudgets();
        Changed = true;
    }

    /// <summary>The play this PC saw with host <paramref name="host"/> in the <see cref="BudgetHours"/> up to <paramref name="utc"/>.</summary>
    public PlayTally PlayWithin(string host, DateTime utc)
    {
        HostBudget? budget = FindBudget(host);
        return budget == null ? PlayTally.None : budget.PlayWithin(HourOf(utc));
    }

    /// <summary>
    /// The host with key <paramref name="host"/> gives <paramref name="credit"/> to <paramref name="stat"/> at
    /// <paramref name="utc"/> (negative: takes some off): true, and counted, when that keeps what it added in the last
    /// <see cref="BudgetHours"/> within what this PC's play with it allows (<see cref="StatBounds.CreditBound"/>, never past
    /// <see cref="ProgressLimits.MaxStatCreditPerHostDay"/>) and what it took off within what it added and, once a match of
    /// its played here, a little (<see cref="StatBounds.DecrementBound"/>); false, nothing counted, when it wouldn't. Buckets more than
    /// <see cref="BudgetHours"/> from <paramref name="utc"/> either way are dropped (a clock set back can't hold a budget
    /// forever), and beyond <see cref="MaxBudgetHosts"/> the host used longest ago goes.
    /// </summary>
    public bool TrySpend(string host, int stat, double credit, DateTime utc)
    {
        CheckHost(host);
        if (stat is < 0 or > ProgressLimits.MaxStat)
        {
            return credit == 0;
        }

        return TrySpendKey(host, stat, credit, utc, play => StatBounds.CreditBound(stat, play));
    }

    /// <summary>
    /// The most a record of <paramref name="stat"/> (its value from one match or one run) may be from host
    /// <paramref name="host"/> at <paramref name="utc"/>: what all of this PC's play with it in the window could hold of that
    /// stat (<see cref="StatBounds.CreditBound"/>).
    /// </summary>
    public double RecordBound(string host, int stat, DateTime utc)
    {
        CheckHost(host);
        return StatBounds.CreditBound(stat, PlayWithin(host, utc));
    }

    /// <summary>
    /// True when this PC played at least one of host <paramref name="host"/>'s matches in the window up to
    /// <paramref name="utc"/>: what a record set lower (a fastest goal) and the win streak's reset (a match lost) need.
    /// </summary>
    public bool PlayedMatchWithin(string host, DateTime utc)
    {
        CheckHost(host);
        return PlayWithin(host, utc).Matches > 0;
    }

    /// <summary>
    /// The most one entity's field <paramref name="field"/> (index <paramref name="fieldIndex"/> of
    /// <paramref name="group"/>'s schema) may take from host <paramref name="host"/> at <paramref name="utc"/>
    /// (<see cref="StatBounds.EntityBound"/>), and whether <paramref name="credit"/> more to that field over all the group's
    /// entities still fits what this PC's play with it allows (<see cref="StatBounds.EntityGroupBound"/>). Nothing counted.
    /// </summary>
    public bool EntityFits(string host, EntityStatGroup group, int fieldIndex, string field, double credit, DateTime utc, out double entityBound)
    {
        CheckHost(host);
        PlayTally play = PlayWithin(host, utc);
        entityBound = StatBounds.EntityBound(group, field, play);
        if (credit <= 0)
        {
            return true;
        }

        double added = FindBudget(host)?.Used(EntityKey(group, fieldIndex), HourOf(utc)).Added ?? 0;
        return double.IsFinite(credit) && added + credit <= StatBounds.EntityGroupBound(group, field, play);
    }

    /// <summary>
    /// Counts <paramref name="credit"/> to field <paramref name="fieldIndex"/> of <paramref name="group"/> over all its
    /// entities, for host <paramref name="host"/> at <paramref name="utc"/> (after <see cref="EntityFits"/> said it fits).
    /// </summary>
    public void SpendEntity(string host, EntityStatGroup group, int fieldIndex, double credit, DateTime utc)
    {
        CheckHost(host);
        if (credit <= 0 || !double.IsFinite(credit) || fieldIndex is < 0 or >= EntityFieldSlots)
        {
            return;
        }

        long now = HourOf(utc);
        Prune(now);
        (FindBudget(host) ?? AddBudget(host)).Add(now, EntityKey(group, fieldIndex), credit);
        DropOldestBudgets();
        Changed = true;
    }

    // The budgets of the entity stats sit beside the stats' own, under keys no stat has.
    private const int EntityKeyBase = 8192;
    private const int EntityFieldSlots = 64;

    private static int EntityKey(EntityStatGroup group, int fieldIndex) => EntityKeyBase + ((int)group * EntityFieldSlots) + fieldIndex;

    private static bool IsBudgetKey(int key) =>
        key is >= 0 and <= ProgressLimits.MaxStat || key is >= EntityKeyBase and < EntityKeyBase + (((int)EntityStatGroup.Relic + 1) * EntityFieldSlots);

    private static void CheckHost(string host)
    {
        if (!ProgressLimits.IsPlausibleId(host))
        {
            throw new ArgumentException("A host key.", nameof(host));
        }
    }

    private bool TrySpendKey(string host, int key, double credit, DateTime utc, Func<PlayTally, double> bound)
    {
        if (credit == 0)
        {
            return true;
        }

        if (!double.IsFinite(credit))
        {
            return false;
        }

        long now = HourOf(utc);
        Prune(now);
        HostBudget? budget = FindBudget(host);
        (double added, double taken) = budget?.Used(key, now) ?? (0, 0);
        PlayTally play = budget?.PlayWithin(now) ?? PlayTally.None;
        if (credit > 0 ? added + credit > bound(play) : taken - credit > StatBounds.DecrementBound(added, play))
        {
            return false;
        }

        (budget ?? AddBudget(host)).Add(now, key, credit);
        DropOldestBudgets();
        Changed = true;
        return true;
    }

    private void Prune(long now)
    {
        if (now == _prunedAt)
        {
            return;
        }

        // Once an hour is enough: a batch brings many operations in the same hour.
        _prunedAt = now;
        foreach (HostBudget kept in _budgets)
        {
            Changed |= kept.Prune(now);
        }

        Changed |= _budgets.RemoveAll(b => b.IsEmpty) > 0;
    }

    private HostBudget AddBudget(string host)
    {
        var budget = new HostBudget(host);
        _budgets.Add(budget);
        return budget;
    }

    private static long HourOf(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).Ticks / TimeSpan.TicksPerHour;

    private HostBudget? FindBudget(string host)
    {
        foreach (HostBudget budget in _budgets)
        {
            if (string.Equals(budget.Host, host, StringComparison.Ordinal))
            {
                return budget;
            }
        }

        return null;
    }

    private void DropOldestBudgets()
    {
        while (_budgets.Count > MaxBudgetHosts)
        {
            HostBudget oldest = _budgets[0];
            foreach (HostBudget budget in _budgets)
            {
                if (budget.Newest < oldest.Newest)
                {
                    oldest = budget;
                }
            }

            _budgets.Remove(oldest);
        }
    }

    private bool ScreenHadItsTurn(string run) =>
        _once.Exists(o => string.Equals(o.Part, OnceUnlocksShown, StringComparison.Ordinal) && string.Equals(o.Run, run, StringComparison.Ordinal));

    /// <summary>The ledger as text (and <see cref="Changed"/> is cleared).</summary>
    public string Format()
    {
        var text = new StringBuilder();
        text.Append("# T2T Online: shared progress this PC applied (each host session's newest batch, and the batches before it that never " +
            "came), shared runs' bench quest counts, the parts of a shared run's end applied once, and this profile's new unlocks kept for " +
            "a shared run's new-unlocks screen; per host, by a key that names nobody, what it credited here and the play this PC saw with it in " +
            "the last day, which bounds that credit.\n");
        foreach ((ulong session, uint through) in _sessions)
        {
            text.Append(CultureInfo.InvariantCulture, $"session {session:x16} {through}\n");
        }

        foreach ((ulong session, uint from, uint to) in _missing)
        {
            text.Append(CultureInfo.InvariantCulture, $"missing {session:x16} {from} {to}\n");
        }

        foreach ((string run, int[] counters) in _runs)
        {
            text.Append(CultureInfo.InvariantCulture, $"run {run} {string.Join(' ', counters.Select(c => c.ToString(CultureInfo.InvariantCulture)))}\n");
        }

        foreach ((string part, string run) in _once)
        {
            text.Append(CultureInfo.InvariantCulture, $"once {part} {run}\n");
        }

        foreach ((string run, string id) in _unlocks)
        {
            text.Append(CultureInfo.InvariantCulture, $"unlock {run} {id}\n");
        }

        foreach (HostBudget budget in _budgets)
        {
            budget.Format(text);
        }

        Changed = false;
        return text.ToString();
    }

    /// <summary>
    /// A ledger from <see cref="Format"/>'s text; lines that don't read are skipped. Lines may end in CR LF and words be
    /// apart by tabs (an editor may have saved it so): a line that no longer reads would forget what was applied. A
    /// <c>#</c> that starts a word starts a comment (the first line is one); one inside an id is the id's (an id is any
    /// printable ASCII, and a line cut there would be read as another run's). Never throws.
    /// </summary>
    public static ProgressLedger Parse(string? text)
    {
        var ledger = new ProgressLedger();
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string[] parts = raw.Split(Blanks, StringSplitOptions.RemoveEmptyEntries);
            int comment = Array.FindIndex(parts, word => word[0] == '#');
            if (comment >= 0)
            {
                parts = parts[..comment];
            }

            if (parts.Length == 3 && parts[0] == "session" &&
                ulong.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong session) &&
                uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint through))
            {
                ledger.SetApplied(session, through);
            }
            else if (parts.Length == 4 && parts[0] == "missing" &&
                ulong.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong gapSession) &&
                uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint from) &&
                uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint to))
            {
                ledger.AddMissing(gapSession, from, to);
            }
            else if (parts.Length == 2 + RunCounters && parts[0] == "run" && ProgressLimits.IsPlausibleId(parts[1]))
            {
                int[] counters = new int[RunCounters];
                bool ok = true;
                for (int i = 0; i < RunCounters && ok; i++)
                {
                    ok = int.TryParse(parts[2 + i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out counters[i]);
                }

                if (ok)
                {
                    ledger.SetRun(parts[1], counters);
                }
            }
            else if (parts.Length == 3 && parts[0] == "once" && ProgressLimits.IsPlausibleId(parts[1]) && ProgressLimits.IsPlausibleId(parts[2]))
            {
                ledger.TryMarkOnce(parts[1], parts[2]);
            }
            else if (parts.Length == 3 && parts[0] == "unlock")
            {
                ledger.NoteUnlock(parts[1], parts[2]);
            }
            else if (parts.Length >= 4 && parts[0] == "budget" && ProgressLimits.IsPlausibleId(parts[1]) &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long hour))
            {
                ledger.ReadBudget(parts[1], hour, parts.AsSpan(3));
            }
            else if (parts.Length == 7 && parts[0] == "play" && ProgressLimits.IsPlausibleId(parts[1]) &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long playHour) &&
                double.TryParse(parts[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double matchSeconds) &&
                int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int matches) &&
                double.TryParse(parts[5], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double runSeconds) &&
                int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out int runs))
            {
                var play = new PlayTally(matchSeconds, matches, runSeconds, runs);
                if (play.IsPlausible() && !play.IsEmpty)
                {
                    (ledger.FindBudget(parts[1]) ?? ledger.AddBudget(parts[1])).AddPlay(playHour, play);
                }
            }
        }

        foreach (HostBudget budget in ledger._budgets)
        {
            budget.KeepNewestHours();
        }

        ledger.DropOldestBudgets();
        ledger.Changed = false;
        return ledger;
    }

    /// <summary>One budget line's buckets (<c>stat:added:taken</c> each); a bucket that doesn't read is skipped.</summary>
    private void ReadBudget(string host, long hour, ReadOnlySpan<string> items)
    {
        HostBudget? budget = FindBudget(host);
        foreach (string item in items)
        {
            string[] fields = item.Split(':');
            if (fields.Length == 3 &&
                int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int stat) && IsBudgetKey(stat) &&
                HostBudget.TryReadAmount(fields[1], out double added) && HostBudget.TryReadAmount(fields[2], out double taken))
            {
                if (budget == null)
                {
                    budget = new HostBudget(host);
                    _budgets.Add(budget);
                }

                budget.Add(hour, stat, added);
                budget.Add(hour, stat, -taken);
            }
        }
    }

    /// <summary>
    /// One host's rolling budget: per hour (hours since 0001-01-01 UTC), what it added to and took off each stat here (and
    /// each entity stat's field over all its entities, under keys no stat has), and the play this PC saw with it.
    /// </summary>
    private sealed class HostBudget
    {
        // Far more than any budget holds: a stored amount past it is damage.
        private const double MaxAmount = int.MaxValue;

        private readonly SortedDictionary<long, Dictionary<int, (double Added, double Taken)>> _hours = new();
        private readonly SortedDictionary<long, PlayTally> _play = new();

        public HostBudget(string host) => Host = host;

        /// <summary>The host's key.</summary>
        public string Host { get; }

        /// <summary>True when no bucket is left.</summary>
        public bool IsEmpty => _hours.Count == 0 && _play.Count == 0;

        /// <summary>The newest bucket's hour (MinValue: none).</summary>
        public long Newest => Math.Max(_hours.Count == 0 ? long.MinValue : _hours.Keys.Last(), _play.Count == 0 ? long.MinValue : _play.Keys.Last());

        /// <summary>The play in the window around hour <paramref name="now"/>.</summary>
        public PlayTally PlayWithin(long now)
        {
            PlayTally total = PlayTally.None;
            foreach ((long hour, PlayTally play) in _play)
            {
                if (InWindow(hour, now))
                {
                    total += play;
                }
            }

            return total;
        }

        /// <summary>Counts <paramref name="play"/> in hour <paramref name="hour"/> (each field kept within its plausible largest).</summary>
        public void AddPlay(long hour, PlayTally play)
        {
            PlayTally sum = _play.GetValueOrDefault(hour) + play;
            _play[hour] = new PlayTally(Math.Min(sum.MatchSeconds, PlayTally.MaxValue), (int)Math.Min(sum.Matches, PlayTally.MaxValue),
                Math.Min(sum.RunSeconds, PlayTally.MaxValue), (int)Math.Min(sum.Runs, PlayTally.MaxValue));
        }

        public static bool TryReadAmount(string text, out double amount) =>
            double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) && double.IsFinite(amount) && amount <= MaxAmount;

        /// <summary>What was added to and taken off <paramref name="stat"/> in the window around hour <paramref name="now"/>.</summary>
        public (double Added, double Taken) Used(int stat, long now)
        {
            double added = 0, taken = 0;
            foreach ((long hour, Dictionary<int, (double Added, double Taken)> stats) in _hours)
            {
                if (InWindow(hour, now) && stats.TryGetValue(stat, out var used))
                {
                    added += used.Added;
                    taken += used.Taken;
                }
            }

            return (added, taken);
        }

        /// <summary>Counts <paramref name="credit"/> (negative: taken off) to <paramref name="stat"/> in hour <paramref name="hour"/>.</summary>
        public void Add(long hour, int stat, double credit)
        {
            if (credit == 0)
            {
                return;
            }

            if (!_hours.TryGetValue(hour, out Dictionary<int, (double Added, double Taken)>? stats))
            {
                stats = new Dictionary<int, (double Added, double Taken)>();
                _hours[hour] = stats;
            }

            (double added, double taken) = stats.GetValueOrDefault(stat);
            stats[stat] = credit > 0 ? (added + credit, taken) : (added, taken - credit);
        }

        /// <summary>Drops the buckets outside the window around hour <paramref name="now"/>. True when any went.</summary>
        public bool Prune(long now)
        {
            List<long>? gone = null;
            foreach (long hour in _hours.Keys)
            {
                if (!InWindow(hour, now))
                {
                    (gone ??= new List<long>()).Add(hour);
                }
            }

            gone?.ForEach(hour => _hours.Remove(hour));
            List<long>? played = null;
            foreach (long hour in _play.Keys)
            {
                if (!InWindow(hour, now))
                {
                    (played ??= new List<long>()).Add(hour);
                }
            }

            played?.ForEach(hour => _play.Remove(hour));
            return gone != null || played != null;
        }

        /// <summary>A damaged or edited file can't make a budget hold more than the window's worth of buckets.</summary>
        public void KeepNewestHours()
        {
            while (_hours.Count > 2 * BudgetHours)
            {
                _hours.Remove(_hours.Keys.First());
            }

            while (_play.Count > 2 * BudgetHours)
            {
                _play.Remove(_play.Keys.First());
            }
        }

        public void Format(StringBuilder text)
        {
            foreach ((long hour, Dictionary<int, (double Added, double Taken)> stats) in _hours)
            {
                text.Append(CultureInfo.InvariantCulture, $"budget {Host} {hour}");
                foreach ((int stat, (double added, double taken)) in stats.OrderBy(s => s.Key))
                {
                    text.Append(CultureInfo.InvariantCulture, $" {stat}:{added:0.###}:{taken:0.###}");
                }

                text.Append('\n');
            }

            foreach ((long hour, PlayTally play) in _play)
            {
                text.Append(CultureInfo.InvariantCulture, $"play {Host} {hour} {play.MatchSeconds:0.#} {play.Matches} {play.RunSeconds:0.#} {play.Runs}\n");
            }
        }

        private static bool InWindow(long hour, long now) => Math.Abs(hour - now) < BudgetHours;
    }
}
