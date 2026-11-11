using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// How many of each named thing, for a line at the end of a session ("RelicRewardMenu 3, EventChoiceMenu 5"). The names may
/// come from another PC, so each is cleaned and cut, and only so many are kept apart: the rest count together, so a long
/// session or a crafted peer can't grow it or the line. Not thread-safe.
/// </summary>
public sealed class NamedCounts
{
    /// <summary>Most names kept apart; the rest count as "others".</summary>
    public const int MaxNames = 24;

    /// <summary>Longest name kept.</summary>
    public const int MaxNameLength = 120;

    private readonly SortedDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _others;

    /// <summary>How many were counted, under any name.</summary>
    public int Total { get; private set; }

    /// <summary>Counts one more of <paramref name="name"/>.</summary>
    public void Add(string? name)
    {
        Total++;
        string clean = SafeText.Clean(name, MaxNameLength);
        if (_counts.TryGetValue(clean, out int count))
        {
            _counts[clean] = count + 1;
        }
        else if (_counts.Count < MaxNames)
        {
            _counts[clean] = 1;
        }
        else
        {
            _others++;
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _counts.Clear();
        _others = 0;
        Total = 0;
    }

    /// <summary>"A 2, B 1", in name order, with "3 others" last when some didn't fit; "" when none were counted.</summary>
    public override string ToString()
    {
        IEnumerable<string> parts = _counts.Select(c => $"{c.Key} {c.Value}");
        return string.Join(", ", _others > 0 ? parts.Append($"{_others} others") : parts);
    }
}

/// <summary>
/// One line for a count that comes in bursts (the batches a rejoin sends again): once the running total has stopped growing
/// for a while, how much it grew. No line per item, and nothing allocated while it's quiet. Not thread-safe.
/// </summary>
public sealed class QuietBurst
{
    private readonly long _quietMicros;
    private long _reported;
    private long _seen;
    private long _grewAt;

    /// <summary>A burst is over once the total hasn't grown for <paramref name="quietMicros"/>.</summary>
    public QuietBurst(long quietMicros) => _quietMicros = quietMicros;

    /// <summary>
    /// The running <paramref name="total"/> now. True once, with how much it grew in the burst (<paramref name="count"/>),
    /// when the burst is over. A total that goes down (a new counter) starts again.
    /// </summary>
    public bool TryReport(long total, long nowMicros, out long count)
    {
        count = 0;
        if (total < _seen)
        {
            _seen = total;
            _reported = total;
            return false;
        }

        if (total > _seen)
        {
            _seen = total;
            _grewAt = nowMicros;
            return false;
        }

        if (_seen == _reported || nowMicros - _grewAt < _quietMicros)
        {
            return false;
        }

        count = _seen - _reported;
        _reported = _seen;
        return true;
    }
}
