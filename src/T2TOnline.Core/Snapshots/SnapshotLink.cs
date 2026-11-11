using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Snapshots;

/// <summary>
/// The host's side of one friend's snapshot stream: which snapshot to delta
/// against. A delta is only useful against a snapshot the friend has, so each friend acknowledges the newest one it
/// decoded (<see cref="Protocol.SnapshotAckMessage"/>) and the host deltas against that. A lost packet then costs nothing
/// but a slightly larger next delta; there's no periodic keyframe to wait for. Every snapshot sent decodes if it arrives.
/// </summary>
public sealed class SnapshotPeer
{
    /// <summary>A delta against a base older than this many snapshots is replaced by a keyframe (it wouldn't be smaller).</summary>
    public const int MaxBaseAge = 120;

    /// <summary>
    /// While the friend has no usable base (nothing acknowledged yet, nothing recent, or it asked for a keyframe),
    /// keyframes go at most this many snapshots apart (about 100 ms at 60/s) and the snapshots between are held back:
    /// the friend couldn't decode a delta, and a keyframe every frame into a link that's down is ~1 Mbps of nothing.
    /// </summary>
    public const int KeyframeSpacing = 6;

    private uint _lastKeyframe;
    private uint _newestSent;

    /// <summary>The newest snapshot the friend says it decoded, 0 before the first.</summary>
    public uint Acked { get; private set; }

    /// <summary>True when the friend asked for a keyframe (it lost the base of what it received).</summary>
    public bool WantsKeyframe { get; private set; } = true;

    /// <summary>Snapshots sent as keyframes.</summary>
    public long Keyframes { get; private set; }

    /// <summary>Snapshots sent as deltas.</summary>
    public long Deltas { get; private set; }

    /// <summary>Snapshots held back, waiting for the friend to acknowledge a keyframe.</summary>
    public long Held { get; private set; }

    /// <summary>Acknowledgements ignored for naming a snapshot never sent to this friend (a stale or corrupt one).</summary>
    public long BogusAcks { get; private set; }

    /// <summary>
    /// An acknowledgement from the friend. One older than already known (they arrive out of order) can ask for a keyframe
    /// but can't cancel a request.
    /// </summary>
    public void OnAck(uint newest, bool needKeyframe)
    {
        if (newest > _newestSent)
        {
            // Never sent to them (another match's, or corrupt): believing it would pin the base out of reach.
            BogusAcks++;
            WantsKeyframe |= needKeyframe;
            return;
        }

        if (newest >= Acked)
        {
            Acked = newest;
            WantsKeyframe = needKeyframe;
        }
        else
        {
            WantsKeyframe |= needKeyframe;
        }
    }

    /// <summary>
    /// What to send this friend for <paramref name="frame"/>: a delta against the acknowledged snapshot
    /// (<paramref name="baseFrame"/>) while the host still holds it and it isn't too old, unless the friend asked for a
    /// keyframe; otherwise a keyframe, or nothing when one went out less than <see cref="KeyframeSpacing"/> snapshots ago.
    /// </summary>
    public SnapshotPlan Plan(SnapshotFrame frame, SnapshotHistory history, out SnapshotFrame? baseFrame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(history);
        if (!WantsKeyframe && Acked != 0 && frame.Sequence > Acked && frame.Sequence - Acked <= MaxBaseAge &&
            history.TryGet(Acked, out SnapshotFrame? acked))
        {
            baseFrame = acked;
            Deltas++;
            _newestSent = Math.Max(_newestSent, frame.Sequence);
            return SnapshotPlan.Delta;
        }

        baseFrame = null;
        if (_lastKeyframe != 0 && frame.Sequence > _lastKeyframe && frame.Sequence - _lastKeyframe < KeyframeSpacing)
        {
            Held++;
            return SnapshotPlan.Hold;
        }

        _lastKeyframe = frame.Sequence;
        _newestSent = Math.Max(_newestSent, frame.Sequence);
        Keyframes++;
        return SnapshotPlan.Keyframe;
    }

    /// <summary>Forgets the friend's state (a new match, or the friend rejoined).</summary>
    public void Reset()
    {
        Acked = 0;
        WantsKeyframe = true;
        _lastKeyframe = 0;
        _newestSent = 0;
    }
}

/// <summary>What the host sends one friend for one snapshot (<see cref="SnapshotPeer.Plan"/>).</summary>
public enum SnapshotPlan
{
    /// <summary>Nothing yet: a keyframe went out moments ago and the friend hasn't acknowledged it.</summary>
    Hold,

    /// <summary>The whole snapshot.</summary>
    Keyframe,

    /// <summary>What changed since the snapshot the friend acknowledged.</summary>
    Delta,
}

/// <summary>
/// A friend's side of the snapshot stream: decodes snapshots against the ones it already has, and says what to
/// acknowledge. Snapshots arrive unreliable and possibly out of order; one older than the newest decoded is still
/// decoded (the playout timeline can use it) but never acknowledged.
/// </summary>
public sealed class SnapshotReceiver
{
    /// <summary>Fewest microseconds between two acknowledgements (30 a second at most).</summary>
    public const long AckInterval = 33_000;

    private readonly SnapshotHistory _history;
    private long _lastAck = long.MinValue;
    private uint _lastAcked;
    private bool _ackedNeed;

    /// <summary>Creates a receiver remembering the newest <paramref name="capacity"/> decoded snapshots as delta bases.</summary>
    public SnapshotReceiver(int capacity = 128) => _history = new SnapshotHistory(capacity);

    /// <summary>The newest snapshot decoded, 0 before the first.</summary>
    public uint Newest { get; private set; }

    /// <summary>True after a snapshot whose base was missing, until a keyframe arrives.</summary>
    public bool NeedsKeyframe { get; private set; }

    /// <summary>Snapshots decoded.</summary>
    public long Decoded { get; private set; }

    /// <summary>Snapshots that couldn't be decoded because their base was missing.</summary>
    public long MissingBase { get; private set; }

    /// <summary>Snapshots that were malformed.</summary>
    public long Malformed { get; private set; }

    /// <summary>
    /// Decodes one snapshot message body (after its header). False when it can't be (malformed, or its base is missing:
    /// then a keyframe is asked for).
    /// </summary>
    public bool TryReceive(ref BitReader reader, out SnapshotFrame frame)
    {
        if (!SnapshotCodec.TryRead(ref reader, _history, out frame, out bool missingBase))
        {
            if (missingBase)
            {
                MissingBase++;
                NeedsKeyframe = true;
            }
            else
            {
                Malformed++;
            }

            return false;
        }

        _history.Add(frame);
        Decoded++;
        if (frame.Sequence > Newest)
        {
            Newest = frame.Sequence;
            NeedsKeyframe = false;
        }

        return true;
    }

    /// <summary>
    /// True when an acknowledgement is due at <paramref name="now"/> (microseconds): something new to acknowledge, or a
    /// keyframe request to make, at most every <see cref="AckInterval"/>.
    /// </summary>
    public bool AckDue(long now)
    {
        bool changed = Newest != _lastAcked || NeedsKeyframe != _ackedNeed;
        if (!changed && !NeedsKeyframe)
        {
            return false;
        }

        if (_lastAck != long.MinValue && now - _lastAck < AckInterval)
        {
            return false;
        }

        _lastAck = now;
        _lastAcked = Newest;
        _ackedNeed = NeedsKeyframe;
        return true;
    }

    /// <summary>Forgets everything (a new match).</summary>
    public void Reset()
    {
        _history.Clear();
        Newest = _lastAcked = 0;
        NeedsKeyframe = _ackedNeed = false;
        _lastAck = long.MinValue;
        Decoded = MissingBase = Malformed = 0;
    }
}
