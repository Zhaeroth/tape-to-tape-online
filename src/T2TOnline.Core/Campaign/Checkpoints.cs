using System;
using System.Collections.Generic;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// A friend's newest <see cref="RunCheckpoint"/> from the host. Checkpoints arrive in order on the reliable channel, so
/// an older one only turns up after a rejoin; it's ignored. One store per session: the host numbers its checkpoints per
/// session.
/// </summary>
public sealed class CheckpointStore
{
    /// <summary>The newest checkpoint, or null before the first.</summary>
    public RunCheckpoint? Latest { get; private set; }

    /// <summary>Checkpoints taken.</summary>
    public int Accepted { get; private set; }

    /// <summary>Checkpoints ignored as older than (or the same as) the newest.</summary>
    public int Ignored { get; private set; }

    /// <summary>Takes <paramref name="checkpoint"/> if it's newer than <see cref="Latest"/>. True when taken.</summary>
    public bool Offer(RunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (Latest != null && !SequenceMath.IsNewer(checkpoint.Sequence, Latest.Sequence))
        {
            Ignored++;
            return false;
        }

        Latest = checkpoint;
        Accepted++;
        return true;
    }

    /// <summary>True once the store holds checkpoint <paramref name="sequence"/> or a later one. Sequence 0 means none is needed.</summary>
    public bool Has(uint sequence) => sequence == 0 || (Latest != null && !SequenceMath.IsNewer(sequence, Latest.Sequence));
}

/// <summary>
/// The host's side: numbers each new run checkpoint (an unchanged run gets no new number) and tracks which one each
/// friend was sent, so a friend who joins or rejoins gets the newest straight away and nobody gets one twice.
/// </summary>
public sealed class CheckpointPublisher
{
    private readonly Dictionary<ulong, uint> _sentTo = new();
    private uint _sequence;

    /// <summary>The newest checkpoint, or null before the first.</summary>
    public RunCheckpoint? Latest { get; private set; }

    /// <summary>Checkpoints the host's game saved that were the same as the one before (not numbered or sent again).</summary>
    public int Unchanged { get; private set; }

    /// <summary>
    /// The host's game saved its run: numbers it as the next checkpoint unless it's the same as the newest. Returns the
    /// checkpoint when it's new, null when unchanged.
    /// </summary>
    public RunCheckpoint? Publish(IReadOnlyDictionary<RunCheckpointPart, byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (Latest != null && RunCheckpoint.ComputeHash(parts) == Latest.Hash && parts.Count == Latest.Parts.Count)
        {
            Unchanged++;
            return null;
        }

        _sequence++;
        if (_sequence == 0)
        {
            _sequence = 1; // 0 means "no checkpoint" in a host view
        }

        Latest = new RunCheckpoint(_sequence, parts);
        return Latest;
    }

    /// <summary>The checkpoint <paramref name="friend"/> still needs (the newest, if they weren't sent it), or null.</summary>
    public RunCheckpoint? PendingFor(ulong friend) =>
        Latest != null && (!_sentTo.TryGetValue(friend, out uint sent) || SequenceMath.IsNewer(Latest.Sequence, sent)) ? Latest : null;

    /// <summary><paramref name="friend"/> was sent checkpoint <paramref name="sequence"/>.</summary>
    public void MarkSent(ulong friend, uint sequence) => _sentTo[friend] = sequence;

    /// <summary><paramref name="friend"/> left or rejoined: they get the newest checkpoint again.</summary>
    public void Forget(ulong friend) => _sentTo.Remove(friend);
}
