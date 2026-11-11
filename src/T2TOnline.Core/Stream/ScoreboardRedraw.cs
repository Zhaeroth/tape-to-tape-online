using System;

namespace T2TOnline.Core.Stream;

/// <summary>
/// Friend side: when to redraw the widgets that only draw on an event (the score digits on <c>ScoreUpdated</c>, the period
/// markers and label on <c>EndOfPeriod</c>). The HUD channel writes the host's values into the model; a jump no event of the
/// stream explains needs a redraw, or the digits stay as they were until the next goal: playback's start (a friend joining
/// mid-match) and a rejoin (the events of the time away never came). A redraw is due from then until the score and the period
/// have all come, so it reads the host's values: a message carries only what changed, and the one before the full set (a value
/// just before an event, while the host's next regular sample is still due) may have neither. Pure logic. Not thread-safe.
/// </summary>
public sealed class ScoreboardRedraw
{
    /// <summary>The HUD key of the period.</summary>
    public const string PeriodKey = "period";

    /// <summary>The HUD key of the home team's score.</summary>
    public const string HomeScoreKey = "score.home";

    /// <summary>The HUD key of the visiting team's score.</summary>
    public const string VisitorScoreKey = "score.visitor";

    private const int All = 7;

    private bool _due;
    private int _seen;

    /// <summary>True while a redraw waits for the host's values.</summary>
    public bool Due => _due;

    /// <summary>A redraw is due once the host's score and period have come: playback started, or the stream started again for a rejoin.</summary>
    public void Ask()
    {
        _due = true;
        _seen = 0;
    }

    /// <summary>Forgets it (playback over).</summary>
    public void Reset()
    {
        _due = false;
        _seen = 0;
    }

    /// <summary>One HUD value of a message.</summary>
    public void Note(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_due)
        {
            return;
        }

        _seen |= string.Equals(key, PeriodKey, StringComparison.Ordinal) ? 1
            : string.Equals(key, HomeScoreKey, StringComparison.Ordinal) ? 2
            : string.Equals(key, VisitorScoreKey, StringComparison.Ordinal) ? 4
            : 0;
    }

    /// <summary>After a message has been written: true once, when the redraw is due and everything it reads has come.</summary>
    public bool TakeAfterMessage()
    {
        if (!_due || _seen != All)
        {
            return false;
        }

        _due = false;
        return true;
    }
}
