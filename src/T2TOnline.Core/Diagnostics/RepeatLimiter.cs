using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Diagnostics;

/// <summary>What to do with one occurrence of a repeated message (<see cref="RepeatLimiter"/>).</summary>
public enum RepeatDecision
{
    /// <summary>Write it in full.</summary>
    Write,

    /// <summary>Count it, write nothing.</summary>
    Count,

    /// <summary>Write one line saying how many were counted since the last such line (this one included).</summary>
    Summarize,
}

/// <summary>
/// Keeps a message that repeats (a failure every frame) from flooding a log: per key, the first few are written in full,
/// the rest counted, with a summary line at most once per interval while they keep coming. Keys past a limit share one
/// budget, so messages that differ every time can't grow it without bound. Not thread-safe: the caller locks.
/// </summary>
public sealed class RepeatLimiter
{
    /// <summary>The key that every key past <see cref="MaxKeys"/> shares.</summary>
    public const string OverflowKey = "(other messages)";

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates a limiter.</summary>
    /// <param name="writtenPerKey">How many of each key are written in full.</param>
    /// <param name="summaryEveryMs">The shortest time between two summary lines of one key.</param>
    /// <param name="maxKeys">Keys tracked one by one; later ones share <see cref="OverflowKey"/>.</param>
    public RepeatLimiter(int writtenPerKey, long summaryEveryMs, int maxKeys)
    {
        if (writtenPerKey < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(writtenPerKey), "At least one must be written.");
        }

        if (summaryEveryMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(summaryEveryMs), "The interval can't be negative.");
        }

        if (maxKeys < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeys), "At least one key must be tracked.");
        }

        WrittenPerKey = writtenPerKey;
        SummaryEveryMs = summaryEveryMs;
        MaxKeys = maxKeys;
    }

    /// <summary>How many of each key are written in full.</summary>
    public int WrittenPerKey { get; }

    /// <summary>The shortest time between two summary lines of one key, in milliseconds.</summary>
    public long SummaryEveryMs { get; }

    /// <summary>Keys tracked one by one.</summary>
    public int MaxKeys { get; }

    /// <summary>
    /// Decides one occurrence of <paramref name="key"/> at <paramref name="nowMs"/> (any monotonic clock).
    /// <paramref name="counted"/> is how many the summary covers (for <see cref="RepeatDecision.Summarize"/>), else 0.
    /// </summary>
    public RepeatDecision Next(string key, long nowMs, out int counted)
    {
        ArgumentNullException.ThrowIfNull(key);
        counted = 0;
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            if (_entries.Count >= MaxKeys)
            {
                key = OverflowKey;
                _entries.TryGetValue(key, out entry);
            }

            if (entry == null)
            {
                entry = new Entry();
                _entries[key] = entry;
            }
        }

        if (entry.Written < WrittenPerKey)
        {
            entry.Written++;
            entry.LastLineMs = nowMs;
            return RepeatDecision.Write;
        }

        entry.Counted++;
        if (nowMs - entry.LastLineMs < SummaryEveryMs)
        {
            return RepeatDecision.Count;
        }

        counted = entry.Counted;
        entry.Counted = 0;
        entry.LastLineMs = nowMs;
        return RepeatDecision.Summarize;
    }

    /// <summary>How many more occurrences of <paramref name="key"/> would be written in full.</summary>
    public int Remaining(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_entries.TryGetValue(key, out Entry? entry) && _entries.Count >= MaxKeys)
        {
            _entries.TryGetValue(OverflowKey, out entry);
        }

        return entry == null ? WrittenPerKey : Math.Max(0, WrittenPerKey - entry.Written);
    }

    /// <summary>Every key with occurrences counted but not yet summarized, and how many; their counts start again.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> Drain()
    {
        var pending = new List<KeyValuePair<string, int>>();
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            if (pair.Value.Counted > 0)
            {
                pending.Add(new KeyValuePair<string, int>(pair.Key, pair.Value.Counted));
                pair.Value.Counted = 0;
            }
        }

        return pending;
    }

    private sealed class Entry
    {
        public int Written { get; set; }

        public int Counted { get; set; }

        public long LastLineMs { get; set; }
    }
}
