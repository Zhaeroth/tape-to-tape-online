#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTape2Tape;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using Goaltender = Il2CppGoaltender.Goaltender;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// The teams' ends on a friend's PC. On the host, the end-of-period screen's close
/// (<c>GameplayEvents.EndOfPeriodCloseUI</c>) reaches <c>Team.OnEndOfPeriodFinished</c> → <c>SetSide</c> →
/// <c>SwitchSide(north)</c> (then, for a reversed-net team, <c>ApplyReversedNetLayout(north)</c>), which sets
/// <c>Team.isNorth</c>, gives the team the goalie at its new end
/// (<c>FindSideGoalie</c>: the goalies stay at their ends of the rink), makes that goalie the team's
/// (<c>Goaltender.Initialize</c> with the team's goalie) and refreshes its UI; the goalie is dressed in its new team's kit
/// at the next faceoff (<c>Goaltender.OnResetMap</c> → <c>UpdateSkin</c>). Both listeners are logic, which client mode
/// detaches here, so a friend's goalies kept the first period's kits and the offside line glowed at the first period's
/// blue line (<c>OffsideVfx.GetOffensiveBlueLine</c> reads <c>Team.isNorth</c>).
/// <para>
/// The host sends each team's end as a HUD flag (<see cref="TeamEnds"/>); when this PC's differ, each team is moved with
/// the game's own <c>Team.SwitchSide</c>, a reversed-net team's rink layout with the game's own
/// <c>Team.ApplyReversedNetLayout</c> (what <c>SetSide</c> runs after the switch since the Nov 6 build), and its goalie
/// dressed with the game's own <c>Goaltender.UpdateSkin</c> (the
/// period cover is up then, as on the host), and an offside line already glowing is placed again. That covers every
/// switch (each period, overtime, a shootout's rounds), a friend joining after one (the first values), and a host whose
/// camera setting (Alternating, Always up, Always down) differs from this PC's. When playback ends with the match still
/// loaded (a recording, the F4 test), this PC's own ends are put back.
/// </para>
/// Logs one INFO line per kind of move, then counts (<see cref="Describe"/>). Never throws.
/// </summary>
internal sealed class TeamEndsMirror
{
    private const int MaxWarnings = 3;

    private readonly TeamEndsFollower _follower = new();
    private readonly HashSet<EndsChange> _told = new();
    private bool _pending;
    private int _restores;
    private int _failures;
    private int _missed;
    private int _reversedNetMoves;

    /// <summary>Forgets the host's ends and the counts (playback starting).</summary>
    public void Begin()
    {
        _follower.Reset();
        _told.Clear();
        _pending = false;
        _restores = _failures = _missed = _reversedNetMoves = 0;
    }

    /// <summary>
    /// Takes one HUD value: true when it's one of the ends' keys (the caller writes it nowhere else). What it means is
    /// decided once the whole message is in (<see cref="Sync"/>).
    /// </summary>
    public bool Take(string key, NetValue value)
    {
        if (!_follower.Take(key, value))
        {
            return false;
        }

        _pending = true;
        return true;
    }

    /// <summary>
    /// After a HUD message the host sent at <paramref name="hostMicros"/> (its stream time): moves this PC's teams to the host's
    /// ends if they're elsewhere.
    /// </summary>
    public void Sync(long hostMicros)
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        if (!TryTeams(out Team home, out Team visitor, out int period))
        {
            return;
        }

        TeamEnds here = Ends(home, visitor);
        EndsStep step = _follower.Next(here, hostMicros);
        if (step.Change == EndsChange.Between)
        {
            // A switch half done on the host (one team's listener has run, the other's not yet): the whole pair follows in the
            // same raise. Counted in the closing line.
            return;
        }

        if (step.Change == EndsChange.Refused)
        {
            if (_told.Add(EndsChange.Refused))
            {
                ModLog.Warn($"Team ends: the host's values have kept both teams at the same end ({step.Target}) for {TeamEndsFollower.SettleMicros / 1_000_000} s " +
                    "or more; not followed (a switch puts one team at each end within the same moment). Further ones are counted.");
            }

            return;
        }

        if (step.Moves)
        {
            Apply(step, home, visitor, here, period);
        }
    }

    /// <summary>
    /// Playback is over and the match is still loaded: puts back this PC's own ends, as they were when playback started
    /// (<paramref name="saved"/>: the HUD values read then).
    /// </summary>
    public void Restore(IReadOnlyDictionary<string, NetValue> saved)
    {
        if (!saved.TryGetValue(TeamEnds.HomeKey, out NetValue home) || home.Kind != NetValueKind.Flag ||
            !saved.TryGetValue(TeamEnds.VisitorKey, out NetValue visitor) || visitor.Kind != NetValueKind.Flag ||
            !TryTeams(out Team homeTeam, out Team visitorTeam, out int period))
        {
            return;
        }

        TeamEnds here = Ends(homeTeam, visitorTeam);
        EndsStep step = EndsStep.Toward(EndsChange.Restore, new TeamEnds(home.WholeValue != 0, visitor.WholeValue != 0), here);
        if (step.Moves)
        {
            _restores++;
            Apply(step, homeTeam, visitorTeam, here, period);
        }
    }

    /// <summary>What was followed this playback, for the HUD channel's closing line.</summary>
    public string Describe()
    {
        TeamEndsFollower f = _follower;
        if (f.Starts + f.Switches + f.Drifts + f.Betweens + f.Refusals + _restores + _failures + _missed == 0)
        {
            return f.Followed is TeamEnds ends ? $"team ends: as the host's throughout ({ends} last)" : "team ends: none from the host";
        }

        var parts = new List<string>();
        parts.Add($"{f.Starts} at the start");
        parts.Add($"{f.Switches} switch{(f.Switches == 1 ? string.Empty : "es")}");
        if (f.Drifts > 0)
        {
            parts.Add($"{f.Drifts} moved back");
        }

        if (f.Betweens > 0)
        {
            parts.Add($"{f.Betweens} seen half done (both teams at one end for a moment, as the game's own switch passes through)");
        }

        if (f.Refusals > 0)
        {
            parts.Add($"{f.Refusals} refused (both teams kept at one end)");
        }

        if (_restores > 0)
        {
            parts.Add($"{_restores} put back at the end");
        }

        if (_missed > 0)
        {
            parts.Add($"{_missed} not taken (the teams stayed elsewhere)");
        }

        if (_reversedNetMoves > 0)
        {
            parts.Add($"the reversed net moved with its team {_reversedNetMoves} time{(_reversedNetMoves == 1 ? string.Empty : "s")}");
        }

        if (_failures > 0)
        {
            parts.Add($"{_failures} game calls failed");
        }

        return $"team ends followed ({(f.Followed is TeamEnds last ? $"host's last: {last}" : "none from the host")}): {string.Join(", ", parts)}";
    }

    private void Apply(EndsStep step, Team home, Team visitor, TeamEnds before, int period)
    {
        if (step.MoveHome)
        {
            Move(home, step.Target.HomeNorth, "home");
        }

        if (step.MoveVisitor)
        {
            Move(visitor, step.Target.VisitorNorth, "visiting");
        }

        PlaceOffsideAgain();
        TeamEnds after = Ends(home, visitor);
        if (after != step.Target)
        {
            _missed++;
            if (_missed <= MaxWarnings)
            {
                ModLog.Warn($"Team ends: after moving ({step.Change}) the teams here are at {after}, not {step.Target}.");
            }
        }

        if (_told.Add(step.Change))
        {
            ModLog.Info(Explain(step, before, period));
            ModLog.Info($"Team ends: after the move, {Readout()}.");
        }
    }

    /// <summary>
    /// Who's at each end now and where the offside line glows, the same words on the host and a friend:
    /// "the north end's goalie "g-1" plays for the home team, the south end's "g-2" for the visiting team; offside
    /// line glowing for the home team". Never throws.
    /// </summary>
    internal static string Readout()
    {
        try
        {
            if (!TryTeams(out Team home, out Team visitor, out _))
            {
                return "the teams can't be read (no match)";
            }

            if (home.isNorth == visitor.isNorth)
            {
                return $"both teams are at the {(home.isNorth ? "north" : "south")} end";
            }

            Team north = home.isNorth ? home : visitor;
            Team south = home.isNorth ? visitor : home;
            Team? glowing = OffsideController._instance?._vfx?._blueLineVfxTeam;
            string offside = glowing != null ? $"glowing for the {Side(glowing)} team" : "not up";
            return $"the north end's goalie {GoalieName(north)} plays for the {Side(north)} team, the south end's {GoalieName(south)} for the {Side(south)} team; " +
                $"offside line {offside}{ReversedNet(home, visitor)}";
        }
        catch (Exception ex)
        {
            return $"the ends couldn't be read ({ModLog.SafeErrorText(ex)})";
        }
    }

    private static string Side(Team team) => team.isHomeTeam ? "home" : "visiting";

    /// <summary>
    /// For a match with a reversed-net team (Calaveras's): "; the reversed net's slab at the north end (the home team, the
    /// reversed-net one, is at the north end)", from the rink's own record of where it put the slab
    /// (<c>Rink._reversedNetSlabNorth</c>); empty for any other match. Read only for the readout's few lines.
    /// </summary>
    private static string ReversedNet(Team home, Team visitor)
    {
        try
        {
            Team? reversed = home.IsReversedNetTeam() ? home : visitor.IsReversedNetTeam() ? visitor : null;
            if (reversed == null)
            {
                return string.Empty;
            }

            Rink? rink = UnityEngine.Object.FindObjectOfType<Rink>();
            if (rink == null)
            {
                return $"; the {Side(reversed)} team plays with the reversed net, and the rink can't be read";
            }

            return $"; the reversed net's slab at the {(rink._reversedNetSlabNorth ? "north" : "south")} end (the {Side(reversed)} team, the reversed-net one, is at the " +
                $"{(reversed.isNorth ? "north" : "south")} end)";
        }
        catch (Exception ex)
        {
            return $"; the reversed net couldn't be read ({ModLog.SafeErrorText(ex)})";
        }
    }

    private static string GoalieName(Team team)
    {
        Goaltender? goalie = team.goaltender;
        if (goalie == null)
        {
            return "(none)";
        }

        string? id = goalie._goaltenderData_k__BackingField?.id;
        return string.IsNullOrEmpty(id) ? "(no data)" : $"\"{Core.Diagnostics.SafeText.Clean(id, 48)}\"";
    }

    /// <summary>The first line of each kind of move: what happened and what this PC did.</summary>
    private static string Explain(EndsStep step, TeamEnds before, int period)
    {
        string how = "moved with the game's own Team.SwitchSide (each team takes the goalie at its new end, as on the host) and " +
            "both goalies dressed with the game's own Goaltender.UpdateSkin, so each end's goalie wears its team's kit and the " +
            "offside line glows at the right blue line";
        return step.Change switch
        {
            EndsChange.Switch => $"Team ends: the host's teams switched ends (period {period}: {step.Target}; here they were {before}). This PC's " +
                $"teams {how}; client mode detaches the game's own switch here (Team.OnEndOfPeriodFinished, Goaltender.OnResetMap). " +
                "Further switches are counted.",
            EndsChange.Start => $"Team ends: the host's teams are at other ends than this PC's when its values start (host: {step.Target}; " +
                $"here: {before}; period {period}): a friend joining after the teams switched ends, or the host's camera setting " +
                $"(Alternating, Always up, Always down) differs from this PC's. This PC's teams {how}. Further ones are counted.",
            EndsChange.Drift => $"Team ends: this PC's teams had left the host's ends with no switch from the host (here: {before}; host: " +
                $"{step.Target}); {how}. Further ones are counted.",
            EndsChange.Restore => $"Team ends: playback is over and the match is still loaded here, so this PC's own ends are put back " +
                $"({step.Target}; following the host's, they were {before}), with the same game calls. Further ones are counted.",
            _ => $"Team ends: {step.Change} to {step.Target} (here: {before}).",
        };
    }

    /// <summary>
    /// One team to the <paramref name="north"/> end as the game's own <c>Team.SetSide</c> moves it (its switch, then, for a
    /// reversed-net team, the rink's layout for that end), then its goalie dressed.
    /// </summary>
    private void Move(Team team, bool north, string side)
    {
        bool switched = false;
        try
        {
            team.SwitchSide(north);
            switched = true;
        }
        catch (Exception ex)
        {
            Fail($"moving the {side} team to the {(north ? "north" : "south")} end (Team.SwitchSide)", ex);
        }

        if (switched)
        {
            MoveReversedNet(team, north, side);
        }

        // Even after a failed switch: whatever goalie the team has now is dressed for the team it plays for.
        try
        {
            Goaltender? goalie = team.goaltender;
            if (goalie != null)
            {
                goalie.UpdateSkin();
            }
        }
        catch (Exception ex)
        {
            Fail($"dressing the {side} team's goalie (Goaltender.UpdateSkin)", ex);
        }
    }

    /// <summary>
    /// The rink's reversed-net layout follows a reversed-net team (Calaveras's) to its new end, as the game's own
    /// <c>Team.SetSide</c> does after its switch (Nov 6 build): <c>Team.ApplyReversedNetLayout</c> moves both nets, the slab,
    /// its ad bands and colliders (<c>Rink.ApplyReversedNetLayout</c>) and turns each end's goalie to face its net
    /// (<c>Goaltender.SyncFacingSetToNet</c>). Logged once per playback, then counted. The game's own test,
    /// <c>Team.IsReversedNetTeam</c>, is false for every team while its debug switch <c>TeamData.ReversedNetDebugOn</c> is off,
    /// and nothing in the Nov 6 build sets it: there no team moves its net, on the host or here, and nothing is logged.
    /// </summary>
    private void MoveReversedNet(Team team, bool north, string side)
    {
        try
        {
            if (!team.IsReversedNetTeam())
            {
                return;
            }

            team.ApplyReversedNetLayout(north);
            if (_reversedNetMoves++ == 0)
            {
                ModLog.Info($"Team ends: the {side} team plays with the reversed net (Calaveras): its layout moved to the {(north ? "north" : "south")} end with the game's own " +
                    "Team.ApplyReversedNetLayout, as Team.SetSide does on the host after its switch (the nets, the slab and each goalie's facing). Further ones are counted.");
            }
        }
        catch (Exception ex)
        {
            Fail($"moving the {side} team's reversed net to the {(north ? "north" : "south")} end (Team.ApplyReversedNetLayout)", ex);
        }
    }

    /// <summary>
    /// An offside line already glowing is placed again: it glows at the offensive blue line of its team's end, which
    /// <c>UpdateBlueLine</c> reads only when the team changes.
    /// </summary>
    private void PlaceOffsideAgain()
    {
        try
        {
            OffsideVfx? offside = OffsideController._instance?._vfx;
            if (offside != null && offside._blueLineVfxTeam is Team glowing && glowing != null)
            {
                offside.UpdateBlueLine(null);
                offside.UpdateBlueLine(glowing);
            }
        }
        catch (Exception ex)
        {
            Fail("placing the offside line again (OffsideVfx.UpdateBlueLine)", ex);
        }
    }

    private void Fail(string what, Exception ex)
    {
        _failures++;
        if (_failures <= MaxWarnings)
        {
            ModLog.Warn($"Team ends: {what} failed: {ModLog.SafeErrorText(ex)}");
        }
    }

    private static TeamEnds Ends(Team home, Team visitor) => new(home.isNorth, visitor.isNorth);

    private static bool TryTeams(out Team home, out Team visitor, out int period)
    {
        home = visitor = null!;
        period = 0;
        GameManager? game = HudModel.GameManagerInstance();
        if (game == null || game._homeTeam is not Team homeTeam || homeTeam == null || game._visitorTeam is not Team visitorTeam || visitorTeam == null)
        {
            return false;
        }

        home = homeTeam;
        visitor = visitorTeam;
        period = game.currentPeriod;
        return true;
    }
}
#endif
