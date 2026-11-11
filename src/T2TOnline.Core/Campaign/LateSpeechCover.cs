namespace T2TOnline.Core.Campaign;

/// <summary>What a speech joined late does with its bubbles this frame (<see cref="LateSpeechCover.Next"/>).</summary>
public enum LateSpeechStep
{
    /// <summary>Nothing changes.</summary>
    None,

    /// <summary>This PC's bubbles hide: its lines up to the host's run unseen.</summary>
    Hide,

    /// <summary>This PC's bubbles show again (<see cref="LateSpeechCover.Ended"/> says why).</summary>
    Show,
}

/// <summary>Why a late speech's bubbles showed again.</summary>
public enum LateSpeechEnd
{
    /// <summary>They're still hidden, or never were.</summary>
    None,

    /// <summary>A bubble up here is on a line the host hasn't gone past.</summary>
    Arrived,

    /// <summary>This PC's speech is over (what follows it is up here).</summary>
    Finished,

    /// <summary>Hidden for <see cref="LateSpeechCover.MaxHiddenSeconds"/>.</summary>
    TimedOut,
}

/// <summary>
/// The Canada curse's dialogue on a friend that came to it late. The menu builds each line in code,
/// one after the other (<c>CanadaCurseMenu.&lt;PlayDialogue&gt;d__44</c>: Pucklin's and Hatfreeze's line 1, then their line 2), so a
/// late start can't begin at the host's line as an event NPC's can; its lines run from the first, and the speech mirror presses
/// each it's past, one press a frame. So until this PC is on the host's line its bubbles are hidden (the speech canvas, and the
/// bubbles' sounds): it arrives at the host's line without the flash. Only when the host is past the first line before this
/// PC's first line starts; a speech followed from its start is left alone. Hidden for <see cref="MaxHiddenSeconds"/> at most;
/// the glue shows them whatever happens when the menu closes or following stops. Pure logic. Not thread-safe.
/// </summary>
public sealed class LateSpeechCover
{
    /// <summary>The longest the bubbles stay hidden: the characters' slide in and a few lines' fades, with room.</summary>
    public const double MaxHiddenSeconds = 6;

    private double _since;

    /// <summary>True while the bubbles are hidden.</summary>
    public bool Hidden { get; private set; }

    /// <summary>True once it has nothing more to do (they showed again, or there was no need).</summary>
    public bool Over { get; private set; }

    /// <summary>Why they showed again.</summary>
    public LateSpeechEnd Ended { get; private set; }

    /// <summary>How long they were hidden, once <see cref="Over"/>.</summary>
    public double HiddenFor { get; private set; }

    /// <summary>
    /// True when a two-speaker speech that goes first, second, first, second is past its first line on the host: the first
    /// speaker advanced past it or started another, or the second has started.
    /// </summary>
    public static bool PastFirstLine(SpeechProgress first, SpeechProgress second) =>
        first.Key > 1 || (first.Key == 1 && first.Stage == SpeechStage.Advanced) || !second.IsNone;

    /// <summary>
    /// True when a speaker's bubble up here (<paramref name="own"/>: its line here; <paramref name="phase"/>: its bubble) is on a
    /// line <paramref name="host"/> hasn't gone past: the line the host is on, or one just ahead of it.
    /// </summary>
    public static bool OnHostsLine(int own, SpeechPhase phase, SpeechProgress host) =>
        own > 0 && phase != SpeechPhase.None && (host.IsNone || own > host.Key || (own == host.Key && host.Stage != SpeechStage.Advanced));

    /// <summary>
    /// One frame at <paramref name="now"/>: <paramref name="hostPastFirst"/> (<see cref="PastFirstLine"/>),
    /// <paramref name="linesHere"/> (a line has started here), <paramref name="arrived"/> (a speaker is
    /// <see cref="OnHostsLine"/>), <paramref name="finished"/> (this PC's speech is over).
    /// </summary>
    public LateSpeechStep Next(bool hostPastFirst, bool linesHere, bool arrived, bool finished, double now)
    {
        if (Over)
        {
            return LateSpeechStep.None;
        }

        if (!Hidden)
        {
            if (linesHere || finished)
            {
                // Its first line came before the host went past it: followed press for press, nothing flashes.
                Over = true;
                return LateSpeechStep.None;
            }

            if (!hostPastFirst)
            {
                return LateSpeechStep.None;
            }

            Hidden = true;
            _since = now;
            return LateSpeechStep.Hide;
        }

        LateSpeechEnd end = arrived ? LateSpeechEnd.Arrived
            : finished ? LateSpeechEnd.Finished
            : now - _since >= MaxHiddenSeconds ? LateSpeechEnd.TimedOut
            : LateSpeechEnd.None;
        if (end == LateSpeechEnd.None)
        {
            return LateSpeechStep.None;
        }

        Ended = end;
        HiddenFor = now - _since;
        Hidden = false;
        Over = true;
        return LateSpeechStep.Show;
    }
}
