using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>One stretch without window focus: how long, how many frames ran meanwhile, and the longest of them.</summary>
/// <param name="Seconds">How long the window was unfocused.</param>
/// <param name="Frames">Frames the game ran meanwhile.</param>
/// <param name="LongestFrameMs">The longest of those frames, in milliseconds.</param>
public readonly record struct FocusSpell(double Seconds, long Frames, double LongestFrameMs)
{
    /// <summary>Frames a second meanwhile (0 for a spell too short to tell).</summary>
    public double FramesPerSecond => Seconds > 0.001 ? Frames / Seconds : 0;
}

/// <summary>
/// The game window's focus while online: a session keeps the game running without focus (D23), but
/// a host's Alt-Tab can still cost its friends' input (the pad line's dropped ticks rise by tens after each,
/// from frames late beyond the catch-up limit), and nothing else in the log ties the two. This keeps each stretch without focus
/// (its length, its frames and the longest one) and the session's totals, including the focus pauses the guard skipped.
/// Pure logic: the caller passes the time and each frame's length. Not thread-safe.
/// </summary>
public sealed class FocusSpells
{
    private double _lostAt = double.NaN;
    private long _frames;
    private double _longestMs;

    /// <summary>True while the window is unfocused (between <see cref="Lost"/> and <see cref="Regained"/>).</summary>
    public bool Unfocused => !double.IsNaN(_lostAt);

    /// <summary>Stretches without focus that ended this session.</summary>
    public int Count { get; private set; }

    /// <summary>Their seconds in all.</summary>
    public double TotalSeconds { get; private set; }

    /// <summary>The longest frame of any of them, in milliseconds.</summary>
    public double LongestFrameMs { get; private set; }

    /// <summary>Focus pauses the game asked for and the guard skipped this session.</summary>
    public long PausesSkipped { get; private set; }

    /// <summary>The window lost focus at <paramref name="nowSeconds"/> (a second call while unfocused changes nothing).</summary>
    public void Lost(double nowSeconds)
    {
        if (Unfocused)
        {
            return;
        }

        _lostAt = nowSeconds;
        _frames = 0;
        _longestMs = 0;
    }

    /// <summary>A frame of <paramref name="frameSeconds"/> ran (counted only while unfocused).</summary>
    public void Frame(double frameSeconds)
    {
        if (!Unfocused)
        {
            return;
        }

        _frames++;
        double ms = double.IsFinite(frameSeconds) ? Math.Max(0, frameSeconds * 1000) : 0;
        _longestMs = Math.Max(_longestMs, ms);
    }

    /// <summary>The window has focus again at <paramref name="nowSeconds"/>: the stretch that ended, or null when none was on.</summary>
    public FocusSpell? Regained(double nowSeconds)
    {
        if (!Unfocused)
        {
            return null;
        }

        var spell = new FocusSpell(Math.Max(0, nowSeconds - _lostAt), _frames, _longestMs);
        _lostAt = double.NaN;
        Count++;
        TotalSeconds += spell.Seconds;
        LongestFrameMs = Math.Max(LongestFrameMs, spell.LongestFrameMs);
        return spell;
    }

    /// <summary>The game asked to pause on a focus change and the guard skipped it. True for the first this session.</summary>
    public bool PauseSkipped() => ++PausesSkipped == 1;

    /// <summary>Forgets everything (a new session).</summary>
    public void Clear()
    {
        _lostAt = double.NaN;
        _frames = 0;
        _longestMs = 0;
        Count = 0;
        TotalSeconds = LongestFrameMs = 0;
        PausesSkipped = 0;
    }
}
