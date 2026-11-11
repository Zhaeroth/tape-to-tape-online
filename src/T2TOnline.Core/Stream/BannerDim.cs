using System;

namespace T2TOnline.Core.Stream;

/// <summary>Why a friend's dim for the host's win banner came off (<see cref="BannerDimFollow.Watch"/>).</summary>
public enum BannerDimEnd
{
    /// <summary>It stays.</summary>
    None,

    /// <summary>The host's dim is over, and no menu of this PC took the backdrop over in the moment after.</summary>
    HostEnded,

    /// <summary>Another owner holds the backdrop here (the in-match menu's blurred still): this dim isn't needed any more.</summary>
    MenuTookOver,

    /// <summary>No HUD message for <see cref="BannerDimFollow.SilenceSeconds"/>: a drop, or the host's stream ended.</summary>
    HostSilent,

    /// <summary>Up for <see cref="BannerDimFollow.MaxSeconds"/>, whatever the host says.</summary>
    TimedOut,
}

/// <summary>
/// Friend side: the arena's 20% dim behind the host's end-of-match win banner. The host's
/// end-of-match step dims with the game's <c>SceneBackdrop</c> half a second before its banner, and turns the dim into a
/// blurred still when the banner is over. A friend's game dims too (under an owner of the mod's) while the host's HUD says so;
/// its in-match menu, which opens right after, owns the blurred still itself, so this dim goes as soon as anything else holds
/// the backdrop, a moment after the host's dim ended, after <see cref="SilenceSeconds"/> with no HUD message and after
/// <see cref="MaxSeconds"/> in any case. Pure logic; the glue dims and takes down, and calls <see cref="Watch"/> every frame
/// while <see cref="Up"/>. Times are seconds on one clock. Not thread-safe.
/// </summary>
public sealed class BannerDimFollow
{
    /// <summary>How long after the host's dim ended this PC's waits for its in-match menu to take the backdrop over.</summary>
    public const double AfterHostSeconds = 1;

    /// <summary>How long the host's HUD may stay silent: longer than the period of its full set of values (5 s).</summary>
    public const double SilenceSeconds = 6;

    /// <summary>The longest a dim stays up: the scoreboard's 0.5 s before the banner and a banner of a few seconds, with room.</summary>
    public const double MaxSeconds = 20;

    private bool _hostDim;
    private double _since;
    private double _lastHeard;
    private double _hostEndedAt = double.NaN;

    /// <summary>True while this PC's dim is up (it's the glue's to take down when <see cref="Watch"/> says so).</summary>
    public bool Up { get; private set; }

    /// <summary>A new playback: forgets everything (a dim still up is the caller's to take down first).</summary>
    public void Reset()
    {
        _hostDim = false;
        _hostEndedAt = double.NaN;
        Up = false;
    }

    /// <summary>
    /// One HUD message at <paramref name="now"/>, with the host's dim as last heard (null: never heard). True when this PC's
    /// dim goes up now: the host's has just started (a friend joining in the middle of it starts there too; one that went for
    /// lack of news doesn't come back until the host's dims again).
    /// </summary>
    public bool Message(bool? hostDim, double now)
    {
        _lastHeard = now;
        bool dim = hostDim == true;
        bool rose = dim && !_hostDim;
        _hostDim = dim;
        if (Up)
        {
            _hostEndedAt = dim ? double.NaN : double.IsNaN(_hostEndedAt) ? now : _hostEndedAt;
            return false;
        }

        if (!rose)
        {
            return false;
        }

        Up = true;
        _since = now;
        _hostEndedAt = double.NaN;
        return true;
    }

    /// <summary>
    /// A look at <paramref name="now"/> while it's up (<paramref name="otherOwner"/>: something else holds the backdrop here too).
    /// Anything but <see cref="BannerDimEnd.None"/> means it's over: the caller takes it down.
    /// </summary>
    public BannerDimEnd Watch(bool otherOwner, double now)
    {
        if (!Up)
        {
            return BannerDimEnd.None;
        }

        BannerDimEnd end = otherOwner ? BannerDimEnd.MenuTookOver
            : !double.IsNaN(_hostEndedAt) && now - _hostEndedAt >= AfterHostSeconds ? BannerDimEnd.HostEnded
            : now - _lastHeard >= SilenceSeconds ? BannerDimEnd.HostSilent
            : now - _since >= MaxSeconds ? BannerDimEnd.TimedOut
            : BannerDimEnd.None;
        Up = end == BannerDimEnd.None;
        return end;
    }

    /// <summary>It came down some other way (playback ended, its scene went, the game's call failed).</summary>
    public void TakenDown() => Up = false;

    /// <summary>The words for the log.</summary>
    public static string Words(BannerDimEnd end) => end switch
    {
        BannerDimEnd.HostEnded => "the host's dim is over",
        BannerDimEnd.MenuTookOver => "the in-match menu's blurred backdrop took over",
        BannerDimEnd.HostSilent => FormattableString.Invariant($"no HUD values from the host for {SilenceSeconds:0} s"),
        BannerDimEnd.TimedOut => FormattableString.Invariant($"it was up for {MaxSeconds:0} s"),
        _ => "it stays",
    };
}
