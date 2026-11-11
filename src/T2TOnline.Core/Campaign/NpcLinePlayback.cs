namespace T2TOnline.Core.Campaign;

/// <summary>What a friend's event screen does with the host's NPC line now.</summary>
public enum NpcLineAction
{
    /// <summary>Nothing to do.</summary>
    None = 0,

    /// <summary>The line waits: this PC's event screen isn't at its NPC's lines yet.</summary>
    Wait = 1,

    /// <summary>Play the line now (it replaces any line still playing here).</summary>
    Play = 2,

    /// <summary>End the line playing here: the host's intro (its NPC's lines and the cards coming in) has ended.</summary>
    Cut = 3,

    /// <summary>Leave the line out: the host's intro had already ended when it could have played (its line was over).</summary>
    DropIntroOver = 4,

    /// <summary>Leave the line out: this PC's event screen didn't get to its NPC's lines within <see cref="NpcLinePlayback.WaitSeconds"/>.</summary>
    DropTooLate = 5,
}

/// <summary>One decision: what to do, and with which line (its number in the host's menu).</summary>
/// <param name="Action">What to do.</param>
/// <param name="Number">The line's number (0 for <see cref="NpcLineAction.None"/>).</param>
public readonly record struct NpcLineStep(NpcLineAction Action, int Number);

/// <summary>
/// When a friend's event screen plays the host's NPC lines. The host's game
/// speaks its NPC's line inside the event screen's intro, after the NPC has come in and before the cards do; a friend's
/// game plays the same line (<see cref="NpcLineData"/>) once its own screen gets there: its intro at the NPC's lines, or
/// its intro over while the host's still plays. A line is played at most once, a newer one replaces it, and the host's
/// intro ending ends it here too (a skip on the host's screen ends the host's line; a line that finished there has also
/// finished here, give or take the link). A line that comes after the host's intro ended, or that waits longer than
/// <see cref="WaitSeconds"/>, is left out. One per event screen opening; pure logic, the game glue does the playing.
/// </summary>
public sealed class NpcLinePlayback
{
    /// <summary>How long a line waits for this PC's event screen to get to its NPC's lines.</summary>
    public const double WaitSeconds = 10;

    /// <summary>The host's intro state as its menu values say it (<c>EventCapture.IntroOver</c>).</summary>
    public const int HostIntroOver = 2;

    private bool _cut;

    /// <summary>The newest line number played or left out here (0: none yet).</summary>
    public int Handled { get; private set; }

    /// <summary>The line playing here (0: none, or it was ended).</summary>
    public int Playing { get; private set; }

    /// <summary>
    /// What to do now. <paramref name="newest"/>: the host's newest line number here (0: none), which came at
    /// <paramref name="arrivedAt"/> (seconds); <paramref name="ready"/>: this PC's event screen is at its NPC's lines or past
    /// its intro; <paramref name="hostIntro"/>: the host's intro state (<see cref="HostIntroOver"/> once over; anything else,
    /// unknown included, counts as not over); <paramref name="now"/>: the time.
    /// </summary>
    public NpcLineStep Step(int newest, double arrivedAt, bool ready, int hostIntro, double now)
    {
        bool hostOver = hostIntro == HostIntroOver;
        if (Playing != 0 && !_cut && hostOver)
        {
            _cut = true;
            return new NpcLineStep(NpcLineAction.Cut, Playing);
        }

        if (newest <= Handled)
        {
            return new NpcLineStep(NpcLineAction.None, 0);
        }

        if (hostOver)
        {
            Handled = newest;
            return new NpcLineStep(NpcLineAction.DropIntroOver, newest);
        }

        if (now - arrivedAt > WaitSeconds)
        {
            Handled = newest;
            return new NpcLineStep(NpcLineAction.DropTooLate, newest);
        }

        if (!ready)
        {
            return new NpcLineStep(NpcLineAction.Wait, newest);
        }

        Handled = newest;
        Playing = newest;
        _cut = false;
        return new NpcLineStep(NpcLineAction.Play, newest);
    }

    /// <summary>The line playing here ended for another reason (the screen closed, playing it failed): nothing plays now.</summary>
    public void Stopped()
    {
        Playing = 0;
        _cut = false;
    }
}
