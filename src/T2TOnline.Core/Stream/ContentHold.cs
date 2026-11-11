using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Stream;

/// <summary>How one friend's custom content has moved during a match's load (<see cref="ContentHold"/>), for the barrier and the log.</summary>
public sealed class ContentMovement
{
    /// <summary>Content bytes still to go (the sender's and the transport's).</summary>
    public long PendingBytes { get; internal set; }

    /// <summary>The batch's content bytes.</summary>
    public long BatchBytes { get; internal set; }

    /// <summary>Bytes that went since the content started moving for this match.</summary>
    public long MovedBytes { get; internal set; }

    /// <summary>When content was first seen still to go for this match (null: none was).</summary>
    public TimeSpan? StartedAt { get; internal set; }

    /// <summary>When it last moved (or started).</summary>
    public TimeSpan LastMovedAt { get; internal set; }

    /// <summary>When all of it had gone (null: not yet, or none was seen going).</summary>
    public TimeSpan? DoneAt { get; internal set; }

    /// <summary>"2.1 of 4.0 MB, 240 KB/s" (the rate over the time it moved; up to <paramref name="now"/> while it still does).</summary>
    public string Describe(TimeSpan now)
    {
        long sent = Math.Max(0, BatchBytes - PendingBytes);
        TimeSpan end = DoneAt ?? now;
        double seconds = StartedAt is TimeSpan start ? (end - start).TotalSeconds : 0;
        string rate = seconds >= 0.5 ? string.Create(CultureInfo.InvariantCulture, $", {MovedBytes / 1024.0 / seconds:0} KB/s") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{Mb(sent)} of {Mb(Math.Max(BatchBytes, sent))} MB{rate}");
    }

    /// <summary>
    /// True when, at the rate it has moved so far, what's left would be gone by <paramref name="cap"/>; also while it has moved
    /// for less than <paramref name="sample"/>, too short to tell.
    /// </summary>
    internal bool CanFinishBy(TimeSpan cap, TimeSpan sample)
    {
        if (StartedAt is not TimeSpan start || MovedBytes <= 0 || LastMovedAt - start < sample)
        {
            return true;
        }

        double rate = MovedBytes / (LastMovedAt - start).TotalSeconds;
        return LastMovedAt.TotalSeconds + (PendingBytes / rate) <= cap.TotalSeconds;
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// How long the host's match waits for each friend while the custom content it needs still reaches them.
/// The content goes when the match starts loading, paced by each friend's link: two 4 MB logos at Steam's 256 KB/s take
/// over 30 s before a friend can even start loading. So a friend still loading holds the match
/// <see cref="BaseHold"/> after the stream starts, longer while their content keeps moving (it moved within
/// <see cref="Moving"/>), then <see cref="LoadAfterContent"/> after it has all gone, and never beyond <see cref="MaxHold"/>
/// after the stream started. Content that stops moving holds nothing more, nor does content too slow to be all gone by
/// <see cref="MaxHold"/> at its rate (the host and every other friend would wait the whole time for nothing): that friend
/// joins in progress. Times are any monotonic clock. Game-independent.
/// </summary>
public sealed class ContentHold
{
    /// <summary>The hold for a friend whose content isn't what keeps them (the barrier's own, D31).</summary>
    public static readonly TimeSpan BaseHold = TimeSpan.FromSeconds(45);

    /// <summary>Content that moved this recently still holds the match.</summary>
    public static readonly TimeSpan Moving = TimeSpan.FromSeconds(10);

    /// <summary>The time a friend gets to load once all their content has gone.</summary>
    public static readonly TimeSpan LoadAfterContent = TimeSpan.FromSeconds(30);

    /// <summary>The longest the match waits for anyone, from the stream's start.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(120);

    private readonly Dictionary<ulong, ContentMovement> _friends = new();

    /// <summary>When the stream started (the barrier with it), or null before.</summary>
    public TimeSpan? StreamStartedAt { get; private set; }

    /// <summary>A new match: nothing noted.</summary>
    public void Clear()
    {
        _friends.Clear();
        StreamStartedAt = null;
    }

    /// <summary>The stream (and the barrier) started at <paramref name="now"/>.</summary>
    public void StreamStarted(TimeSpan now) => StreamStartedAt = now;

    /// <summary>
    /// <paramref name="friend"/>'s content now: <paramref name="pendingBytes"/> still to go of a batch of
    /// <paramref name="batchBytes"/>. Call it often (a few times a second) from the match's setup on.
    /// </summary>
    public void Note(ulong friend, long pendingBytes, long batchBytes, TimeSpan now)
    {
        pendingBytes = Math.Max(0, pendingBytes);
        if (!_friends.TryGetValue(friend, out ContentMovement? movement))
        {
            movement = new ContentMovement();
            _friends[friend] = movement;
        }

        if (pendingBytes > 0)
        {
            if (movement.StartedAt == null || movement.DoneAt != null)
            {
                // Content to go (again): it counts from now.
                movement.StartedAt ??= now;
                movement.DoneAt = null;
                movement.LastMovedAt = now;
            }
            else if (pendingBytes < movement.PendingBytes)
            {
                movement.MovedBytes += movement.PendingBytes - pendingBytes;
                movement.LastMovedAt = now;
            }
        }
        else if (movement.PendingBytes > 0)
        {
            movement.MovedBytes += movement.PendingBytes;
            movement.LastMovedAt = now;
            movement.DoneAt = now;
        }

        movement.PendingBytes = pendingBytes;
        movement.BatchBytes = Math.Max(batchBytes, 0);
    }

    /// <summary>What's noted of <paramref name="friend"/>'s content, or null.</summary>
    public ContentMovement? Of(ulong friend) => _friends.TryGetValue(friend, out ContentMovement? movement) ? movement : null;

    /// <summary>True when <paramref name="friend"/>'s content has moved this match (so it's what keeps them).</summary>
    public bool Moved(ulong friend) => Of(friend)?.StartedAt != null;

    /// <summary>True when <paramref name="friend"/>, still loading, holds the match at <paramref name="now"/>.</summary>
    public bool Holds(ulong friend, TimeSpan now)
    {
        if (StreamStartedAt is not TimeSpan start)
        {
            return false;
        }

        if (now >= start + MaxHold)
        {
            return false;
        }

        TimeSpan baseEnd = start + BaseHold;
        if (Of(friend) is not { StartedAt: not null } movement)
        {
            return now < baseEnd;
        }

        if (movement.PendingBytes > 0)
        {
            return now < baseEnd || (now - movement.LastMovedAt < Moving && movement.CanFinishBy(start + MaxHold, Moving));
        }

        return now < (movement.DoneAt is TimeSpan done && done + LoadAfterContent > baseEnd ? done + LoadAfterContent : baseEnd);
    }

    /// <summary>True when <paramref name="friend"/> holds the match past <see cref="BaseHold"/> only because of their content.</summary>
    public bool Extended(ulong friend, TimeSpan now) =>
        StreamStartedAt is TimeSpan start && now >= start + BaseHold && Moved(friend) && Holds(friend, now);

    /// <summary>
    /// Why <paramref name="friend"/>, still loading, no longer holds the match at <paramref name="now"/>, worded to follow
    /// "friend N: " ("its custom content stopped moving 10 s ago at 2.1 of 4.0 MB, 240 KB/s").
    /// </summary>
    public string Ended(ulong friend, TimeSpan now)
    {
        TimeSpan start = StreamStartedAt ?? now;
        ContentMovement? movement = Of(friend);
        string content = movement?.StartedAt != null ? $" (their custom content: {movement.Describe(now)})" : string.Empty;
        if (now >= start + MaxHold)
        {
            return string.Create(CultureInfo.InvariantCulture, $"still loading {MaxHold.TotalSeconds:0} s after the stream started, the longest it waits{content}");
        }

        if (movement is { StartedAt: not null, PendingBytes: > 0 })
        {
            if (now - movement.LastMovedAt < Moving)
            {
                return string.Create(CultureInfo.InvariantCulture, $"their custom content is too slow to be all gone {MaxHold.TotalSeconds:0} s after the stream started ({movement.Describe(now)})");
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"their custom content stopped moving {(now - movement.LastMovedAt).TotalSeconds:0} s ago at {movement.Describe(now)}");
        }

        if (movement is { DoneAt: TimeSpan done } && done + LoadAfterContent >= start + BaseHold)
        {
            return string.Create(CultureInfo.InvariantCulture, $"still loading {LoadAfterContent.TotalSeconds:0} s after their custom content had all gone{content}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"still loading after {BaseHold.TotalSeconds:0} s{content}");
    }

    /// <summary>
    /// When <paramref name="friend"/>'s hold ends if nothing changes (for a countdown), or null while their content still
    /// moves (it holds as long as it does).
    /// </summary>
    public TimeSpan? Deadline(ulong friend, TimeSpan now)
    {
        if (StreamStartedAt is not TimeSpan start)
        {
            return null;
        }

        TimeSpan cap = start + MaxHold;
        TimeSpan baseEnd = start + BaseHold;
        ContentMovement? movement = Of(friend);
        if (movement is { StartedAt: not null, PendingBytes: > 0 })
        {
            if (!movement.CanFinishBy(cap, Moving))
            {
                return Min(cap, baseEnd);
            }

            return now - movement.LastMovedAt < Moving ? null : Min(cap, Max(baseEnd, movement.LastMovedAt + Moving));
        }

        TimeSpan end = movement is { DoneAt: TimeSpan done } ? Max(baseEnd, done + LoadAfterContent) : baseEnd;
        return Min(cap, end);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>
/// How long a friend's match load waits for the host's custom content: <see cref="BaseSeconds"/>
/// at least, and after that while pieces keep arriving (one within <see cref="StallSeconds"/>), up to
/// <see cref="MaxSeconds"/>. A link that's slow but moving no longer fails the match; one that stopped does, as before.
/// Seconds on any monotonic clock. Game-independent.
/// </summary>
public sealed class ContentWait
{
    /// <summary>The wait before a stall can end it (the 60 s it always had).</summary>
    public const double BaseSeconds = 60;

    /// <summary>No piece for this long, after <see cref="BaseSeconds"/>, ends the wait.</summary>
    public const double StallSeconds = 10;

    /// <summary>The longest wait.</summary>
    public const double MaxSeconds = 150;

    /// <summary>A wait that started at <paramref name="since"/>.</summary>
    public ContentWait(double since)
    {
        Since = since;
        LastArrivedAt = since;
    }

    /// <summary>When the wait started.</summary>
    public double Since { get; }

    /// <summary>When a piece last arrived (or the wait started).</summary>
    public double LastArrivedAt { get; private set; }

    /// <summary>Bytes of the awaited content here, as last noted.</summary>
    public long ReceivedBytes { get; private set; }

    /// <summary>The awaited content's size as known so far (an item's is known from its first piece).</summary>
    public long TotalBytes { get; private set; }

    /// <summary>True once the wait went past <see cref="BaseSeconds"/> (it's said once in the log).</summary>
    public bool Extended { get; private set; }

    /// <summary><paramref name="receivedBytes"/> of <paramref name="totalBytes"/> are here at <paramref name="now"/>.</summary>
    public void Note(long receivedBytes, long totalBytes, double now)
    {
        if (receivedBytes > ReceivedBytes)
        {
            LastArrivedAt = now;
        }

        ReceivedBytes = Math.Max(receivedBytes, 0);
        TotalBytes = Math.Max(totalBytes, ReceivedBytes);
    }

    /// <summary>True when the wait is over without the content (see the class).</summary>
    public bool Expired(double now)
    {
        double waited = now - Since;
        return waited > MaxSeconds || (waited > BaseSeconds && now - LastArrivedAt > StallSeconds);
    }

    /// <summary>True the first time the wait goes past <see cref="BaseSeconds"/> while it still holds (to say so once).</summary>
    public bool ExtendsNow(double now)
    {
        if (Extended || now - Since <= BaseSeconds || Expired(now))
        {
            return false;
        }

        Extended = true;
        return true;
    }

    /// <summary>"2.1 of 4.0 MB, 56 KB/s" (the size as known so far).</summary>
    public string Describe(double now)
    {
        double seconds = now - Since;
        string rate = seconds >= 0.5 ? string.Create(CultureInfo.InvariantCulture, $", {ReceivedBytes / 1024.0 / seconds:0} KB/s") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{ReceivedBytes / (1024.0 * 1024.0):0.0} of {TotalBytes / (1024.0 * 1024.0):0.0} MB{rate}");
    }

    /// <summary>Why the wait ended without the content, worded to follow "the host's custom content ".</summary>
    public string Why(double now) => now - Since > MaxSeconds
        ? string.Create(CultureInfo.InvariantCulture, $"didn't all arrive within {MaxSeconds:0} s ({Describe(now)})")
        : string.Create(CultureInfo.InvariantCulture, $"stopped arriving: nothing for {now - LastArrivedAt:0} s, {now - Since:0} s after the match's setup ({Describe(now)})");
}
