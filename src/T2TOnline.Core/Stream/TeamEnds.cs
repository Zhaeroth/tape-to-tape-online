using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// Which end of the rink each team defends: the game's <c>Team.isNorth</c>. The teams change
/// ends when an end-of-period screen closes (<c>GameplayEvents.EndOfPeriodCloseUI</c> → <c>Team.OnEndOfPeriodFinished</c>
/// → <c>SetSide</c> → <c>SwitchSide</c>), in a shootout's rounds, and the first period's ends come from each PC's own
/// camera setting. A goalie stays at its end of the rink; what changes is which team it plays for, so its kit, and the
/// blue line the offside line glows at. The host sends its ends as two HUD flags (<see cref="HomeKey"/>,
/// <see cref="VisitorKey"/>: true for north).
/// </summary>
/// <param name="HomeNorth">Whether the home team defends the north end.</param>
/// <param name="VisitorNorth">Whether the visiting team defends the north end.</param>
public readonly record struct TeamEnds(bool HomeNorth, bool VisitorNorth)
{
    /// <summary>The HUD key of the home team's end (a flag, true for north).</summary>
    public const string HomeKey = "north.home";

    /// <summary>The HUD key of the visiting team's end (a flag, true for north).</summary>
    public const string VisitorKey = "north.visitor";

    /// <summary>One team at each end, as the game always has them.</summary>
    public bool OnePerEnd => HomeNorth != VisitorNorth;

    /// <inheritdoc />
    public override string ToString() => $"home {End(HomeNorth)}, visitor {End(VisitorNorth)}";

    private static string End(bool north) => north ? "north" : "south";
}

/// <summary>Why a friend's PC moves its teams to other ends (<see cref="EndsStep"/>).</summary>
public enum EndsChange
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>
    /// The host's first values differ from this PC's: a friend joining after the teams switched ends, or the host's camera
    /// setting differs from this PC's.
    /// </summary>
    Start,

    /// <summary>The host's teams switched ends during the match (a period's end, a shootout round).</summary>
    Switch,

    /// <summary>This PC's teams left the host's ends with no change from the host.</summary>
    Drift,

    /// <summary>
    /// The host's values have kept both teams at the same end for <see cref="TeamEndsFollower.SettleMicros"/> or longer: not
    /// followed (a real error: the game has one team at each end once a switch is done).
    /// </summary>
    Refused,

    /// <summary>Playback is over and the match is still loaded: this PC's own ends are put back.</summary>
    Restore,

    /// <summary>
    /// The host's values put both teams at the same end for a moment: a switch half done. The game switches each team from its
    /// own listener of the end-of-period close (<c>Team.OnEndOfPeriodFinished</c> → <c>SetSide</c> → <c>SwitchSide</c>), and the
    /// second team's listener raises <c>GameplayEvents.PlayingForwardsChanged</c> (<c>ApplyLineForFaceoff</c> →
    /// <c>ChangeLine</c> → <c>UpdatePlayingForwards</c>) before its own switch, so a value sent for that event has the first
    /// team moved and the second not yet. Expected: counted, nothing moved, and refused only if it lasts.
    /// </summary>
    Between,
}

/// <summary>What a friend's PC does now: which of its teams change ends, and why.</summary>
/// <param name="Change">Why (<see cref="EndsChange.None"/>: nothing to do).</param>
/// <param name="Target">The ends the teams go to (the host's, or this PC's own when put back).</param>
/// <param name="MoveHome">Whether the home team changes ends.</param>
/// <param name="MoveVisitor">Whether the visiting team changes ends.</param>
public readonly record struct EndsStep(EndsChange Change, TeamEnds Target, bool MoveHome, bool MoveVisitor)
{
    /// <summary>Nothing to do.</summary>
    public static EndsStep Nothing => default;

    /// <summary>Whether any team changes ends.</summary>
    public bool Moves => MoveHome || MoveVisitor;

    /// <summary>
    /// What takes <paramref name="local"/> to <paramref name="target"/>, for <paramref name="change"/>: the teams whose end
    /// differs. Nothing when none does, or when <paramref name="target"/> has both teams at the same end (never followed).
    /// </summary>
    public static EndsStep Toward(EndsChange change, TeamEnds target, TeamEnds local)
    {
        if (!target.OnePerEnd)
        {
            return Nothing;
        }

        bool home = local.HomeNorth != target.HomeNorth;
        bool visitor = local.VisitorNorth != target.VisitorNorth;
        return home || visitor ? new EndsStep(change, target, home, visitor) : Nothing;
    }
}

/// <summary>
/// Friend side: follows the host's team ends from its HUD values (<see cref="TeamEnds"/>). Feed it every HUD value
/// (<see cref="Take"/>), then, after each message, ask what to do with this PC's ends as they are (<see cref="Next"/>): the
/// first time both of the host's values are known is the start, a later change of the host's values a switch, and this
/// PC's teams found elsewhere with no change from the host a drift. The host's values are untrusted: a value that isn't a
/// flag is ignored. Both teams at the same end is a switch half done (<see cref="EndsChange.Between"/>: counted once per
/// such pair, nothing moved); a pair that still has them there <see cref="SettleMicros"/> later is refused (counted once).
/// Not thread-safe.
/// </summary>
public sealed class TeamEndsFollower
{
    /// <summary>
    /// How long, in the host's stream time, its values may keep both teams at one end before that's refused as an error.
    /// A switch is whole again within the one event raise that started it, and the host sends every value at least every
    /// 5 s, so a pair still at one end a second later is stuck there.
    /// </summary>
    public const long SettleMicros = 1_000_000;

    private bool? _home;
    private bool? _visitor;
    private TeamEnds? _followed;
    private TeamEnds? _between;
    private long _betweenSince;
    private TeamEnds? _refused;

    /// <summary>Starts that moved this PC's teams.</summary>
    public int Starts { get; private set; }

    /// <summary>Switches by the host that moved this PC's teams.</summary>
    public int Switches { get; private set; }

    /// <summary>Times this PC's teams were moved back to the host's ends with no change from the host.</summary>
    public int Drifts { get; private set; }

    /// <summary>Times the host's values put both teams at the same end for a moment, a switch half done (a pair that stays counts once).</summary>
    public int Betweens { get; private set; }

    /// <summary>Times the host's values kept both teams at the same end past <see cref="SettleMicros"/> (not followed; a pair that stays counts once).</summary>
    public int Refusals { get; private set; }

    /// <summary>The host's ends as last followed, or null before both values were known.</summary>
    public TeamEnds? Followed => _followed;

    /// <summary>Forgets the host's values and the counts (playback starting).</summary>
    public void Reset()
    {
        _home = _visitor = null;
        _followed = _between = _refused = null;
        _betweenSince = 0;
        Starts = Switches = Drifts = Betweens = Refusals = 0;
    }

    /// <summary>
    /// Takes one HUD value. True when <paramref name="key"/> is one of the two ends' keys (whatever the value: the caller
    /// writes it nowhere else); a value that isn't a flag is ignored.
    /// </summary>
    public bool Take(string key, NetValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool home = string.Equals(key, TeamEnds.HomeKey, StringComparison.Ordinal);
        if (!home && !string.Equals(key, TeamEnds.VisitorKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Kind == NetValueKind.Flag)
        {
            bool north = value.WholeValue != 0;
            if (home)
            {
                _home = north;
            }
            else
            {
                _visitor = north;
            }
        }

        return true;
    }

    /// <summary>
    /// What to do so this PC's teams, at <paramref name="local"/> now, defend the host's ends, after a message the host sent at
    /// <paramref name="hostMicros"/> (its stream time): nothing until both of the host's values are known. A pair with both
    /// teams at the same end moves nothing: <see cref="EndsChange.Between"/> the first time, <see cref="EndsChange.Refused"/>
    /// once the same pair is still there <see cref="SettleMicros"/> later, else nothing. Otherwise the teams to move, if any,
    /// and why.
    /// </summary>
    public EndsStep Next(TeamEnds local, long hostMicros)
    {
        if (_home is not bool homeNorth || _visitor is not bool visitorNorth)
        {
            return EndsStep.Nothing;
        }

        var host = new TeamEnds(homeNorth, visitorNorth);
        if (!host.OnePerEnd)
        {
            if (_refused == host)
            {
                return EndsStep.Nothing;
            }

            if (_between != host)
            {
                _between = host;
                _betweenSince = hostMicros;
                Betweens++;
                return new EndsStep(EndsChange.Between, host, false, false);
            }

            if (!Lasted(_betweenSince, hostMicros))
            {
                return EndsStep.Nothing;
            }

            _refused = host;
            Refusals++;
            return new EndsStep(EndsChange.Refused, host, false, false);
        }

        EndsChange change = _followed is not TeamEnds followed ? EndsChange.Start : followed != host ? EndsChange.Switch : EndsChange.Drift;
        _followed = host;
        _between = _refused = null;
        EndsStep step = EndsStep.Toward(change, host, local);
        switch (step.Change)
        {
            case EndsChange.Start:
                Starts++;
                break;
            case EndsChange.Switch:
                Switches++;
                break;
            case EndsChange.Drift:
                Drifts++;
                break;
        }

        return step;
    }

    /// <summary>True when <see cref="SettleMicros"/> or more passed from <paramref name="since"/> to <paramref name="now"/> (the host's times: any values, never an overflow).</summary>
    private static bool Lasted(long since, long now) => now >= since && unchecked((ulong)(now - since)) >= SettleMicros;
}
