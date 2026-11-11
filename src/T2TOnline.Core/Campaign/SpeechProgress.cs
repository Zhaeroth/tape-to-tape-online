using System;

namespace T2TOnline.Core.Campaign;

/// <summary>Where a line of speech is on the host's screen. Values are part of the protocol: only add.</summary>
public enum SpeechStage : byte
{
    /// <summary>The line started (it types out, then waits for a press).</summary>
    Started = 0,

    /// <summary>A press showed the line in full while it was still typing.</summary>
    Shown = 1,

    /// <summary>A press advanced past the line (it's ending; the speaker's next line, if any, comes next).</summary>
    Advanced = 2,
}

/// <summary>
/// One speaker's progress through its speech on the host's screen (the speech mirror): the line it's on, counted
/// from 1 since its speech began, and how far the host's presses have taken that line. It travels as a menu value of the
/// menu the speaker is in (group <see cref="Group"/>, item = the speaker's slot, value = <see cref="Value"/>), so the board
/// gives the newest to a friend who joins; a friend's game replays it on its own bubble with the game's own skip.
/// <code>
/// value = key × 4 + stage    key 1..255, stage 0..2; value 0: the speaker's speech began, no line yet
/// </code>
/// Every other value (1 to 3, a stage of 3, more than <see cref="MaxValue"/>) reads as nothing (<see cref="TryRead"/>).
/// Immutable; <c>default</c> is <see cref="None"/>.
/// </summary>
public readonly record struct SpeechProgress
{
    /// <summary>The menu value group the progress travels in.</summary>
    public const string Group = "speech";

    /// <summary>The most lines a speaker's speech counts (a speech with more stays at this one).</summary>
    public const int MaxKey = 255;

    /// <summary>Speakers per menu (slots 0 to 7).</summary>
    public const int MaxSpeakers = 8;

    /// <summary>The largest value (<see cref="MaxKey"/>, advanced): within a menu value's 0 to 1023.</summary>
    public const int MaxValue = (MaxKey * 4) + (int)SpeechStage.Advanced;

    private SpeechProgress(int key, SpeechStage stage)
    {
        Key = key;
        Stage = stage;
    }

    /// <summary>A speech began and no line has started yet (value 0).</summary>
    public static SpeechProgress None => default;

    /// <summary>The line's number in the speaker's speech, from 1 (0: <see cref="None"/>).</summary>
    public int Key { get; }

    /// <summary>How far the host's presses have taken the line.</summary>
    public SpeechStage Stage { get; }

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Key == 0;

    /// <summary>The menu value: <see cref="Key"/> × 4 + <see cref="Stage"/> (0 for <see cref="None"/>).</summary>
    public int Value => (Key * 4) + (int)Stage;

    /// <summary>Line <paramref name="key"/> (1 to <see cref="MaxKey"/>) at <paramref name="stage"/>. Throws for anything out of range.</summary>
    public static SpeechProgress Line(int key, SpeechStage stage)
    {
        if (key is < 1 or > MaxKey)
        {
            throw new ArgumentOutOfRangeException(nameof(key), $"A line's key is 1 to {MaxKey}.");
        }

        if (stage is < SpeechStage.Started or > SpeechStage.Advanced)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), "No such stage.");
        }

        return new SpeechProgress(key, stage);
    }

    /// <summary>Reads a menu value. False (and <see cref="None"/>) for any value that isn't one. Never throws.</summary>
    public static bool TryRead(int value, out SpeechProgress progress)
    {
        progress = None;
        if (value == 0)
        {
            return true;
        }

        int key = value / 4;
        int stage = value % 4;
        if (value < 0 || value > MaxValue || key < 1 || stage > (int)SpeechStage.Advanced)
        {
            return false;
        }

        progress = new SpeechProgress(key, (SpeechStage)stage);
        return true;
    }

    /// <summary>
    /// This progress taken to line <paramref name="key"/> at <paramref name="stage"/>, never back: a later line replaces it,
    /// the same line only moves forward, an earlier one changes nothing (two presses in one frame, or one skip reaching two
    /// bubbles, can't count twice). <paramref name="key"/> is clamped to 1..<see cref="MaxKey"/>.
    /// </summary>
    public SpeechProgress Raised(int key, SpeechStage stage)
    {
        int line = Math.Clamp(key, 1, MaxKey);
        if (line < Key || (line == Key && stage <= Stage))
        {
            return this;
        }

        return new SpeechProgress(line, stage);
    }

    /// <summary>For logs: "no line yet", "line 3 started", "line 3 shown in full", "line 3 advanced".</summary>
    public override string ToString() => IsNone ? "no line yet" : Stage switch
    {
        SpeechStage.Shown => $"line {Key} shown in full",
        SpeechStage.Advanced => $"line {Key} advanced",
        _ => $"line {Key} started",
    };
}
