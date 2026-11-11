using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Counts the errors a log writes, so a check can ask how many came since it began (<see cref="Mark"/>,
/// <see cref="SinceMark"/>) and which came first. Every error counts, written in full or only counted by the log's repeat
/// limit. Thread-safe and cheap: a lock around a few fields, taken only when an error is written or a check asks.
/// </summary>
public sealed class ErrorTally
{
    /// <summary>The longest first line kept.</summary>
    public const int MaxLineLength = 160;

    private readonly object _gate = new();
    private long _count;
    private long _mark;
    private bool _marked;
    private string? _firstSinceMark;

    /// <summary>Errors counted since the tally began.</summary>
    public long Total
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Errors counted since the last <see cref="Mark"/> (since the tally began when there was none).</summary>
    public long SinceMark
    {
        get
        {
            lock (_gate)
            {
                return _count - _mark;
            }
        }
    }

    /// <summary>The first error's line since the last <see cref="Mark"/>, cut to <see cref="MaxLineLength"/>, or null for none (or no mark).</summary>
    public string? FirstSinceMark
    {
        get
        {
            lock (_gate)
            {
                return _firstSinceMark;
            }
        }
    }

    /// <summary>Counts one error, whose line is <paramref name="line"/>.</summary>
    public void Note(string? line)
    {
        lock (_gate)
        {
            _count++;
            if (_marked && _firstSinceMark == null)
            {
                string text = line ?? string.Empty;
                _firstSinceMark = text.Length <= MaxLineLength ? text : string.Concat(text.AsSpan(0, MaxLineLength - 3), "...");
            }
        }
    }

    /// <summary>From now on <see cref="SinceMark"/> counts from zero and <see cref="FirstSinceMark"/> waits for the next error.</summary>
    public void Mark()
    {
        lock (_gate)
        {
            _mark = _count;
            _marked = true;
            _firstSinceMark = null;
        }
    }

    /// <summary>
    /// What's wrong with a check that saw <paramref name="errors"/> errors after it had staged <paramref name="staged"/> of
    /// them itself: null when no more came than it staged, else one sentence.
    /// </summary>
    public static string? Verdict(long errors, int staged) =>
        errors <= Math.Max(staged, 0) ? null
        : staged > 0 ? $"{errors} ERROR line(s) since the tour started, {staged} of them staged by its steps"
        : $"{errors} ERROR line(s) since the tour started, none staged by its steps";
}
