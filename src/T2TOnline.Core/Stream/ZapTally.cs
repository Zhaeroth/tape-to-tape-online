namespace T2TOnline.Core.Stream;

/// <summary>
/// A friend's zap lines for one match (its skaters following the host's zaps). Every zap and revert applied is written as
/// it happens and counted for the match's closing line. A zap into a skater this PC doesn't have changes nothing here and
/// is the host's word alone, so only the first <see cref="NotFollowedSaid"/> of a match are written; the rest are counted
/// and the closing line says how many, so a host sending many unknown forms can't fill the log. Game-independent.
/// </summary>
public sealed class ZapTally
{
    /// <summary>The zaps into a skater this PC doesn't have that are written in one match.</summary>
    public const int NotFollowedSaid = 3;

    /// <summary>The host's zaps this PC's skaters took this match.</summary>
    public int Zapped { get; private set; }

    /// <summary>The host's reverts this PC's skaters took this match.</summary>
    public int Reverted { get; private set; }

    /// <summary>The host's zaps into a skater this PC doesn't have, this match.</summary>
    public int NotFollowed { get; private set; }

    /// <summary>The zaps into a skater this PC doesn't have that were counted and not written.</summary>
    public int NotFollowedUnsaid => NotFollowed > NotFollowedSaid ? NotFollowed - NotFollowedSaid : 0;

    /// <summary>A new match: every count back to zero.</summary>
    public void Begin()
    {
        Zapped = 0;
        Reverted = 0;
        NotFollowed = 0;
    }

    /// <summary>A zap of the host's taken here.</summary>
    public void AddZapped() => Zapped++;

    /// <summary>A revert of the host's taken here.</summary>
    public void AddReverted() => Reverted++;

    /// <summary>A zap into a skater this PC doesn't have: true when its line is written (one of the match's first <see cref="NotFollowedSaid"/>).</summary>
    public bool AddNotFollowed() => ++NotFollowed <= NotFollowedSaid;

    /// <summary>
    /// The match's closing line, or null with nothing to say: "Zaps: 2 of the host's zap(s) and 1 revert(s) applied to this
    /// PC's skaters.", and after it the zaps into a skater this PC doesn't have that weren't written, when there were any.
    /// </summary>
    public string? EndLine()
    {
        int unsaid = NotFollowedUnsaid;
        if (Zapped + Reverted == 0 && unsaid == 0)
        {
            return null;
        }

        string left = unsaid > 0
            ? $"; {unsaid} more zap(s) into a skater this PC doesn't have weren't followed (only a match's first {NotFollowedSaid} are written)"
            : string.Empty;
        return $"Zaps: {Zapped} of the host's zap(s) and {Reverted} revert(s) applied to this PC's skaters{left}.";
    }
}
