#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2Cpp;
using Il2CppFMOD.Studio;
using Il2CppFMODUnity;
using Il2CppSkaters;
using Il2CppState;
using Il2CppTape2Tape;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// The match values a friend's HUD shows and the game no longer computes in client mode,
/// by key. The widgets themselves keep running and read these fields, so writing the fields is enough:
/// <list type="bullet">
/// <item><c>clock</c>, <c>clock.up</c>: <c>TimeRemaining._remainingTime</c> and <c>increaseTime</c> (the scoreboard polls them);</item>
/// <item><c>period</c>: <c>GameManager.currentPeriod</c> (read by the period indicator when EndOfPeriod arrives);</item>
/// <item><c>score.home</c>, <c>score.visitor</c>: the teams' goal counts (read by the score widget on ScoreUpdated);</item>
/// <item><c>state</c>: <c>GameStateManager._currentGameState</c> (polled by many widgets; written without raising the change event);</item>
/// <item><c>possession</c>: the puck carrier (<c>Puck._skaterPossession</c>, read by the puck indicator and trails);</item>
/// <item><c>line.*</c>, <c>stamina.*</c>: the selected line and each forward's stamina (the stamina widget polls them);</item>
/// <item><c>penalty.*</c>: the penalty box's team, forward and time left (its widget is created when one starts);</item>
/// <item><c>offside</c>: the team whose blue line glows (<c>OffsideVfx._blueLineVfxTeam</c>; offside logic is held);</item>
/// <item><c>north.home</c>, <c>north.visitor</c>: the end each team defends (<c>Team.isNorth</c>), sent only as a whole pair
/// (one team at each end). The teams switch ends when an end-of-period screen closes, in logic client mode detaches, so a
/// friend's PC moves its teams with the game's own switch and dresses the goalies (<see cref="TeamEndsMirror"/>, not
/// written here);</item>
/// <item><c>replay.*</c>: the goal replay's three skip prompts (canvas alphas) and its flashes (counted on the host by
/// <see cref="ReplayFlashProbe"/>, replayed with the same colour and length);</item>
/// <item><c>fight.*</c>: each forward's brawl state and health (<c>Forward.isInFightMode</c>, <c>HP</c>), which the brawl
/// HUD's bars read;</item>
/// <item><c>brawl</c>: the forwards the brawl HUD shows bars for (<c>FightHpBarSystem._bars</c>), null when it's down. The
/// game starts that HUD from fight logic, which client mode holds, so a friend's PC starts it with the same forwards and
/// adds bars for any that join later;</item>
/// <item><c>fmod/&lt;name&gt;</c>: FMOD's global parameters, which game logic sets (slow-mo, seconds remaining, brawl, possession).
/// A friend's PC writes only parameters its own list has (<see cref="GlobalParameters"/>: never a player's own audio
/// settings), by id;</item>
/// <item><c>audio.slowmo</c>: whether the host's slow-motion audio filter (<c>SoundManager._slowmoSnapshotInstance</c>, an
/// FMOD snapshot game logic starts with the brawl finisher and a charged slapshot) is playing; a friend's PC follows it
/// (<see cref="SlowmoAudio"/>);</item>
/// <item><c>players</c>, <c>player.&lt;id&gt;.*</c>: each player's controlled skater and star colours, which the markers over
/// the skaters show (<see cref="PlayerProxies"/>);</item>
/// <item><c>hud.scoreboard</c>: whether the scoreboard (<c>ScoreboardCanvasController</c>) is up. Match-flow events show
/// and hide it (a faceoff starting, a goal, a period's end); a friend who starts mid-match never saw the one that showed
/// it, so the friend's PC shows or hides it to match, with the game's own short fade;</item>
/// <item><c>cover.period</c>: the opacity of the cover the host's game fades in as an end-of-period screen closes and out
/// once the faceoff is placed (<c>PeriodTransitionCover</c>, a static full-screen overlay). Only that screen's own close
/// fades it, which never runs on a friend's PC, so the friend's PC shows the game's own cover at the host's opacity
/// (<see cref="SetPeriodCover"/>): nothing is timed here, so it can't race the menu's close or the faceoff.</item>
/// </list>
/// </summary>
internal static class HudModel
{
    /// <summary>What the key of every mirrored FMOD global parameter starts with.</summary>
    public const string FmodPrefix = "fmod/";

    /// <summary>The key of the host's slow-motion audio flag.</summary>
    public const string SlowmoKey = "audio.slowmo";

    /// <summary>The key of the period cover's opacity (0 when it's down).</summary>
    public const string CoverKey = "cover.period";

    private static readonly SideKeys HomeKeys = new("home");
    private static readonly SideKeys VisitorKeys = new("visitor");
    private static GameManager? _game;
    private static float _lastSearch = float.MinValue;
    private static Il2CppUI.Scoreboard.ScoreboardCanvasController? _scoreboard;
    private static float _lastScoreboardSearch = float.MinValue;

    /// <summary>Samples that left the team ends out because both teams were at one end, a switch half done (<see cref="Read"/>; the host's recorder resets it).</summary>
    public static int EndsLeftOut { get; set; }

    /// <summary>
    /// Reads every value into <paramref name="values"/>. <paramref name="refOf"/> encodes object references.
    /// <paramref name="beforeEvent"/>: the host's read right before an event, of what a friend's event handlers may read
    /// (score, period, team ends, state, possession, the selected line, fights, the penalty box, offside, players),
    /// leaving out what only polling widgets or the value itself use (stamina, the goal replay's prompts and flash, the
    /// scoreboard's visibility, the period cover, the slow-motion flag; FMOD parameters come only with
    /// <paramref name="parameters"/>): they follow at the next regular sample, within 50 ms. Over a hundred interop calls
    /// fewer per event.
    /// </summary>
    public static void Read(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf, IReadOnlyList<FmodParameter> parameters, bool beforeEvent = false)
    {
        GameManager? game = GameManagerInstance();
        if (game != null)
        {
            TimeRemaining? clock = game.timeRemaining;
            if (clock != null)
            {
                values["clock"] = NetValue.FromFloat(clock._remainingTime);
                values["clock.up"] = NetValue.FromBool(clock.increaseTime);
            }

            values["period"] = NetValue.FromInt(game.currentPeriod);
            ReadTeam(values, HomeKeys, game._homeTeam, withStamina: !beforeEvent);
            ReadTeam(values, VisitorKeys, game._visitorTeam, withStamina: !beforeEvent);
            // Before "offside": the friend's PC moves its teams before it places the offside line (TeamEndsMirror). Only a
            // whole pair: each team switches from its own listener of the end-of-period close, and the second team's raises
            // PlayingForwardsChanged before its own switch, so the sample before that event has both teams at one end. Left out
            // of it, the friend keeps the last pair until the next sample, in the same raise, has the whole switch.
            if (game._homeTeam is Team home && home != null && game._visitorTeam is Team visitor && visitor != null)
            {
                bool homeNorth = home.isNorth;
                bool visitorNorth = visitor.isNorth;
                if (homeNorth != visitorNorth)
                {
                    values[TeamEnds.HomeKey] = NetValue.FromBool(homeNorth);
                    values[TeamEnds.VisitorKey] = NetValue.FromBool(visitorNorth);
                }
                else
                {
                    EndsLeftOut++;
                }
            }
            values["brawl"] = BrawlBars(refOf);
            if (game._gameStateManager is GameStateManager states)
            {
                values["state"] = NetValue.FromInt((int)states._currentGameState);
            }
        }

        Puck? puck = Puck.Instance;
        if (puck != null)
        {
            values["possession"] = puck._skaterPossession is Skater carrier && carrier != null ? refOf(carrier) : NetValue.Null;
        }

        OffsideVfx? offside = OffsideController._instance?._vfx;
        if (offside != null)
        {
            values["offside"] = offside._blueLineVfxTeam is Team glowing && glowing != null ? refOf(glowing) : NetValue.Null;
        }

        GoalReplay? replay = beforeEvent ? null : GoalReplay.Instance;
        if (replay != null)
        {
            values["replay.pass"] = NetValue.FromFloat(replay.pressXtoPassUICanvasGroup?.alpha ?? 0f);
            values["replay.skip"] = NetValue.FromFloat(replay.pressXtoSkipUICanvasGroup?.alpha ?? 0f);
            values["replay.retroskip"] = NetValue.FromFloat(replay.retroPressXtoSkipUICanvasGroup?.alpha ?? 0f);
            Color flash = ReplayFlashProbe.Color;
            values["replay.flash.color"] = NetValue.FromColor(flash.r, flash.g, flash.b, flash.a);
            values["replay.flash.duration"] = NetValue.FromFloat(ReplayFlashProbe.Duration);
            values["replay.flash"] = NetValue.FromInt(ReplayFlashProbe.Count);
        }

        if (!beforeEvent && Scoreboard() is { } board && board._canvasGroup != null)
        {
            values["hud.scoreboard"] = NetValue.FromBool(board._canvasGroup.alpha >= 0.5f);
        }

        if (!beforeEvent)
        {
            values[CoverKey] = NetValue.FromFloat(PeriodCoverAlpha());
        }

        PlayerProxies.Read(values, refOf);
        if (!beforeEvent && SoundManager.Instance is SoundManager sound && sound != null)
        {
            values[SlowmoKey] = NetValue.FromBool(sound._slowmoSnapshotInstance.isValid());
        }

        Team? penaltyTeam = PenaltyBoxSystem._team;
        bool penalty = penaltyTeam != null;
        values["penalty.team"] = penalty ? refOf(penaltyTeam!) : NetValue.Null;
        values["penalty.forward"] = penalty && PenaltyBoxSystem._forward is Forward boxed && boxed != null ? refOf(boxed) : NetValue.Null;
        values["penalty.remaining"] = NetValue.FromFloat(penalty ? PenaltyBoxSystem._remaining : 0f);

        if (parameters.Count > 0)
        {
            Il2CppFMOD.Studio.System studio = RuntimeManager.StudioSystem;
            foreach (FmodParameter parameter in parameters)
            {
                if (studio.getParameterByID(parameter.Id, out float value) == Il2CppFMOD.RESULT.OK)
                {
                    values[parameter.Key] = NetValue.FromFloat(value);
                }
            }
        }
    }

    /// <summary>
    /// Writes one value into the local match. Unknown keys are ignored (a newer host); a reference that doesn't resolve
    /// leaves the field as it is. An FMOD parameter is written only when <paramref name="fmodIds"/> (this PC's own list,
    /// by key) has it, by its id here: the network can't reach a player's audio settings or anything else by name.
    /// </summary>
    public static void Write(string key, NetValue value, Func<NetValue, Type, object?> resolve, PenaltyState penalty, IReadOnlyDictionary<string, PARAMETER_ID> fmodIds)
    {
        if (key.StartsWith(FmodPrefix, StringComparison.Ordinal))
        {
            if (value.Kind == NetValueKind.Single32 && fmodIds.TryGetValue(key, out PARAMETER_ID id))
            {
                RuntimeManager.StudioSystem.setParameterByID(id, (float)value.Number, false);
            }

            return;
        }

        if (key == SlowmoKey)
        {
            // Followed by the HUD channel's slow-motion audio, not written here.
            return;
        }

        if (key == CoverKey)
        {
            if (value.Kind == NetValueKind.Single32)
            {
                SetPeriodCover((float)value.Number);
            }

            return;
        }

        if (key.StartsWith("penalty.", StringComparison.Ordinal))
        {
            penalty.Set(key, value, resolve);
            return;
        }

        if (PlayerProxies.Write(key, value, resolve))
        {
            return;
        }

        GameManager? game = GameManagerInstance();
        if (game == null)
        {
            return;
        }

        switch (key)
        {
            case "offside":
                OffsideVfx? offside = OffsideController._instance?._vfx;
                if (offside != null)
                {
                    offside.UpdateBlueLine(value.Kind == NetValueKind.Null ? null : resolve(value, typeof(Team)) as Team);
                }

                return;
            case "hud.scoreboard" when value.Kind == NetValueKind.Flag:
                if (Scoreboard() is { } board && board._canvasGroup != null && (board._canvasGroup.alpha >= 0.5f) != (value.WholeValue != 0))
                {
                    if (value.WholeValue != 0)
                    {
                        board.Show(0.25f);
                    }
                    else
                    {
                        board.Hide(0.25f);
                    }
                }

                return;
            case "replay.pass" or "replay.skip" or "replay.retroskip" when value.Kind == NetValueKind.Single32:
                GoalReplay? replay = GoalReplay.Instance;
                CanvasGroup? prompt = key switch
                {
                    "replay.pass" => replay?.pressXtoPassUICanvasGroup,
                    "replay.skip" => replay?.pressXtoSkipUICanvasGroup,
                    _ => replay?.retroPressXtoSkipUICanvasGroup,
                };
                if (prompt != null)
                {
                    prompt.alpha = (float)value.Number;
                }

                return;
            case "clock" when value.Kind == NetValueKind.Single32 && game.timeRemaining != null:
                game.timeRemaining._remainingTime = (float)value.Number;
                return;
            case "clock.up" when value.Kind == NetValueKind.Flag && game.timeRemaining != null:
                game.timeRemaining.increaseTime = value.WholeValue != 0;
                return;
            case "period" when value.Kind == NetValueKind.Whole:
                game.currentPeriod = (int)value.WholeValue;
                return;
            case "state" when value.Kind == NetValueKind.Whole && game._gameStateManager is GameStateManager states:
                var state = (GameState)(int)value.WholeValue;
                if (states._currentGameState != state)
                {
                    states._currentGameState = state;
                    states.TimeInState = Time.time;
                }

                return;
            case "possession":
                Puck? puck = Puck.Instance;
                if (puck != null)
                {
                    puck._skaterPossession = value.Kind == NetValueKind.Null ? null : resolve(value, typeof(Skater)) as Skater;
                }

                return;
        }

        // "<kind>.<side>" or "<kind>.<side>.<index>", read in place (a split per value is garbage 20 times a second).
        int dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return;
        }

        ReadOnlySpan<char> kind = key.AsSpan(0, dot);
        ReadOnlySpan<char> rest = key.AsSpan(dot + 1);
        int second = rest.IndexOf('.');
        ReadOnlySpan<char> side = second < 0 ? rest : rest[..second];
        Team? team = side switch { "home" => game._homeTeam, "visitor" => game._visitorTeam, _ => null };
        int index = -1;
        bool indexed = second >= 0 && rest[(second + 1)..].IndexOf('.') < 0 &&
            int.TryParse(rest[(second + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out index);
        if (team == null)
        {
            return;
        }

        switch (kind)
        {
            case "fight" when indexed:
                Il2CppSystem.Collections.Generic.List<Forward>? fighters = team.AllForwards;
                if (fighters != null && index >= 0 && index < fighters.Count && fighters[index] is Forward who)
                {
                    who.isInFightMode = value.Kind != NetValueKind.Null;
                    if (value.Kind == NetValueKind.Single32)
                    {
                        who.HP = (float)value.Number;
                    }
                }

                return;
            case "score" when value.Kind == NetValueKind.Whole:
                TeamStats stats = team.teamStats;
                stats._goals = (int)value.WholeValue;
                team.teamStats = stats;
                return;
            case "line" when value.Kind == NetValueKind.Whole:
                team.selectedLineNumber = (int)value.WholeValue;
                return;
            case "stamina" when value.Kind == NetValueKind.Single32 && indexed:
                Il2CppSystem.Collections.Generic.List<Forward>? forwards = team.AllForwards;
                Il2CppSystem.Collections.Generic.Dictionary<Forward, float>? stamina = team.StaminaSystem?._stamina;
                if (forwards != null && stamina != null && index >= 0 && index < forwards.Count && forwards[index] is Forward forward && stamina.ContainsKey(forward))
                {
                    stamina[forward] = (float)value.Number;
                }

                return;
        }
    }

    /// <summary>
    /// The global FMOD parameters a stream mirrors: settable ones (not read-only or automatic) that game logic drives. Any
    /// that look like a player's own audio settings (volume, mute) are left alone: each PC keeps its own.
    /// </summary>
    public static List<FmodParameter> GlobalParameters()
    {
        var parameters = new List<FmodParameter>();
        Il2CppFMOD.Studio.System studio = RuntimeManager.StudioSystem;
        if (!studio.isValid() || studio.getParameterDescriptionList(out Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<PARAMETER_DESCRIPTION> list) != Il2CppFMOD.RESULT.OK || list == null)
        {
            return parameters;
        }

        foreach (PARAMETER_DESCRIPTION description in list)
        {
            if ((description.flags & (PARAMETER_FLAGS.READONLY | PARAMETER_FLAGS.AUTOMATIC)) != 0)
            {
                continue;
            }

            string? name = Marshal.PtrToStringUTF8(description.name.nativeUtf8Ptr);
            if (!string.IsNullOrEmpty(name) && name.Length + FmodPrefix.Length <= 96 && !IsPlayerSetting(name))
            {
                parameters.Add(new FmodParameter(name, description.id));
            }
        }

        return parameters;
    }

    /// <summary>
    /// The period cover's opacity on this PC (the host's read): 0 while the game hasn't made it in this scene (Unity's null:
    /// it goes with the match's scene) or its canvas is off, else its fade's alpha (0 to 1).
    /// </summary>
    public static float PeriodCoverAlpha()
    {
        CanvasGroup? group = PeriodTransitionCover._group;
        if (group == null)
        {
            return 0f;
        }

        float alpha = group.alpha;
        if (!(alpha > 0f))
        {
            return 0f;
        }

        Canvas? canvas = PeriodTransitionCover._canvas;
        return canvas != null && canvas.enabled ? Math.Min(alpha, 1f) : 0f;
    }

    /// <summary>
    /// Shows the period cover at <paramref name="alpha"/> (the host's, from the network: held to 0..1, not a number is 0),
    /// or takes it down at 0. The cover is the game's own, made as its fade-in makes it (<c>PeriodTransitionCover.Build</c>)
    /// when this scene has none yet; its fades and faceoff listener are never started here (the host's opacity is the fade).
    /// True when it was down and now shows.
    /// </summary>
    public static bool SetPeriodCover(float alpha)
    {
        alpha = float.IsFinite(alpha) ? Math.Clamp(alpha, 0f, 1f) : 0f;
        Canvas? canvas = PeriodTransitionCover._canvas;
        if (alpha <= 0f)
        {
            if (canvas != null)
            {
                CanvasGroup? down = PeriodTransitionCover._group;
                if (down != null)
                {
                    down.alpha = 0f;
                }

                canvas.enabled = false;
            }

            return false;
        }

        if (canvas == null)
        {
            PeriodTransitionCover.Build();
            canvas = PeriodTransitionCover._canvas;
        }

        CanvasGroup? group = PeriodTransitionCover._group;
        if (canvas == null || group == null)
        {
            return false;
        }

        bool shown = !canvas.enabled;
        group.alpha = alpha;
        canvas.enabled = true;
        return shown;
    }

    /// <summary>Takes the period cover down (a held or ended stream: no cover left over the last frame).</summary>
    public static void HidePeriodCover() => SetPeriodCover(0f);

    /// <summary>
    /// Redraws the widgets that only update when an event arrives (the score digits on ScoreUpdated, the period markers on
    /// EndOfPeriod) from the model as it is now. For jumps the stream's events don't explain: playback starting, and the
    /// local values coming back when it ends.
    /// </summary>
    public static void RefreshEventDrivenWidgets()
    {
        foreach (Il2CppTape2Tape.TeamScore score in UnityEngine.Object.FindObjectsOfType<Il2CppTape2Tape.TeamScore>())
        {
            score.UpdateInstant();
        }

        GameManager? game = GameManagerInstance();
        if (game != null)
        {
            foreach (PeriodIndicatorUI periods in UnityEngine.Object.FindObjectsOfType<PeriodIndicatorUI>())
            {
                periods.UpdatePeriodMarker(game.currentPeriod);
            }

            // The scoreboard's "2nd period" label: set at start-up and on EndOfPeriod, from GameManager.currentPeriod.
            foreach (ScoreboardTimeRemaining scoreboard in UnityEngine.Object.FindObjectsOfType<ScoreboardTimeRemaining>())
            {
                scoreboard.OnEndOfPeriod(new Il2CppEvents.EndOfPeriodArg(game.currentPeriod));
            }
        }
    }

    private static bool IsPlayerSetting(string name) =>
        name.Contains("volume", StringComparison.OrdinalIgnoreCase) || name.Contains("mute", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("setting", StringComparison.OrdinalIgnoreCase) || name.Contains("option", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One team's score, selected line, each forward's stamina (unless <paramref name="withStamina"/> is false), and each
    /// forward's brawl health while it fights (null otherwise; the brawl HUD's bars read both). The forwards are fetched once.
    /// </summary>
    private static void ReadTeam(Dictionary<string, NetValue> values, SideKeys keys, Team? team, bool withStamina)
    {
        if (team == null)
        {
            return;
        }

        values[keys.Score] = NetValue.FromInt(team.teamStats._goals);
        values[keys.Line] = NetValue.FromInt(team.selectedLineNumber);
        Il2CppSystem.Collections.Generic.List<Forward>? forwards = team.AllForwards;
        if (forwards == null)
        {
            return;
        }

        Il2CppSystem.Collections.Generic.Dictionary<Forward, float>? stamina = withStamina ? team.StaminaSystem?._stamina : null;
        for (int i = 0; i < forwards.Count && stamina != null; i++)
        {
            Forward forward = forwards[i];
            if (forward != null && stamina.TryGetValue(forward, out float value))
            {
                values[keys.Stamina(i)] = NetValue.FromFloat(value);
            }
        }

        for (int i = 0; i < forwards.Count; i++)
        {
            Forward forward = forwards[i];
            if (forward != null)
            {
                values[keys.Fight(i)] = forward.isInFightMode ? NetValue.FromFloat(forward.HP) : NetValue.Null;
            }
        }
    }

    /// <summary>The forwards the brawl HUD shows bars for, or null when it's down.</summary>
    private static NetValue BrawlBars(Func<UnityEngine.Object, NetValue> refOf)
    {
        if (!Il2CppFighting.FightHpBarSystem.IsBrawlActive)
        {
            return NetValue.Null;
        }

        return CompoundValues.RefsToNet(BarForwards().Cast<UnityEngine.Object>(), refOf);
    }

    /// <summary>The forwards that have a bar in this PC's brawl HUD now.</summary>
    private static List<Forward> BarForwards()
    {
        var forwards = new List<Forward>();
        Il2CppSystem.Collections.Generic.List<Il2CppFighting.FightHpBar>? bars = Il2CppFighting.FightHpBarSystem._bars;
        if (bars == null)
        {
            return forwards;
        }

        for (int i = 0; i < bars.Count; i++)
        {
            Il2CppFighting.FightHpBar bar = bars[i];
            if (bar != null && bar._forward is Forward forward && forward != null)
            {
                forwards.Add(forward);
            }
        }

        return forwards;
    }

    /// <summary>
    /// Shows the host's brawl HUD here: started with the same forwards (by side, as the game does) when it's down, bars
    /// added for forwards that joined, ended when the host's ended and this PC started it. Returns whether this PC's
    /// brawl HUD is up because of the stream.
    /// </summary>
    public static bool SyncBrawlHud(NetValue bars, Func<NetValue, Type, object?> resolve, bool startedHere)
    {
        List<Forward> wanted = bars.Kind == NetValueKind.Null ? new List<Forward>() : CompoundValues.RefsFromNet<Forward>(bars, resolve);
        bool active = Il2CppFighting.FightHpBarSystem.IsBrawlActive;
        if (wanted.Count == 0)
        {
            if (startedHere && active)
            {
                Il2CppFighting.FightHpBarSystem.EndBrawl();
            }

            return false;
        }

        if (!active)
        {
            var home = new Il2CppSystem.Collections.Generic.List<Forward>();
            var visitor = new Il2CppSystem.Collections.Generic.List<Forward>();
            var neutral = new Il2CppSystem.Collections.Generic.List<Forward>();
            foreach (Forward forward in wanted)
            {
                Team? team = forward._team;
                (team == null || forward.GetComponent<Il2CppSkaters.Referee>() != null ? neutral : team.isHomeTeam ? home : visitor).Add(forward);
            }

            Il2CppFighting.FightHpBarSystem.StartBrawl(
                home.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Forward>>(),
                visitor.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Forward>>(),
                neutral.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Forward>>());
            return true;
        }

        var shown = new HashSet<IntPtr>(BarForwards().Select(f => f.Pointer));
        var joined = new Il2CppSystem.Collections.Generic.List<Forward>();
        foreach (Forward forward in wanted.Where(f => !shown.Contains(f.Pointer)))
        {
            joined.Add(forward);
        }

        if (joined.Count > 0)
        {
            Il2CppFighting.FightHpBarSystem.AddBars(joined.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Forward>>());
        }

        return startedHere || active;
    }

    /// <summary>The match's scoreboard, looked up at most once a second while there's none.</summary>
    private static Il2CppUI.Scoreboard.ScoreboardCanvasController? Scoreboard()
    {
        if (_scoreboard != null)
        {
            return _scoreboard;
        }

        if (Time.realtimeSinceStartup - _lastScoreboardSearch < 1f)
        {
            return null;
        }

        _lastScoreboardSearch = Time.realtimeSinceStartup;
        Il2CppUI.Scoreboard.ScoreboardCanvasController? board = UnityEngine.Object.FindObjectOfType<Il2CppUI.Scoreboard.ScoreboardCanvasController>();
        _scoreboard = board != null ? board : null;
        return _scoreboard;
    }

    /// <summary>The local match's game state (<c>GameStateManager._currentGameState</c>, which a friend's PC mirrors), or null with no match.</summary>
    public static int? CurrentGameState() =>
        GameManagerInstance() is GameManager game && game._gameStateManager is GameStateManager states && states != null
            ? (int)states._currentGameState
            : null;

    /// <summary>The match's GameManager: looked up once and kept while it lives (at most one search a second when there's none).</summary>
    internal static GameManager? GameManagerInstance()
    {
        if (_game != null)
        {
            return _game;
        }

        if (Time.realtimeSinceStartup - _lastSearch < 1f)
        {
            return null;
        }

        _lastSearch = Time.realtimeSinceStartup;
        GameManager? game = UnityEngine.Object.FindObjectOfType<GameManager>();
        _game = game != null ? game : null;
        return _game;
    }

    /// <summary>One side's keys, made once (the host samples them 20 times a second).</summary>
    private sealed class SideKeys
    {
        private readonly string _side;
        private readonly List<string> _stamina = new();
        private readonly List<string> _fight = new();

        public SideKeys(string side)
        {
            _side = side;
            Score = $"score.{side}";
            Line = $"line.{side}";
        }

        public string Score { get; }

        public string Line { get; }

        public string Stamina(int index) => Indexed(_stamina, "stamina", index);

        public string Fight(int index) => Indexed(_fight, "fight", index);

        private string Indexed(List<string> keys, string kind, int index)
        {
            while (keys.Count <= index)
            {
                keys.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{kind}.{_side}.{keys.Count}"));
            }

            return keys[index];
        }
    }
}

/// <summary>One global FMOD parameter.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Id">Its id in the loaded banks.</param>
internal readonly record struct FmodParameter(string Name, PARAMETER_ID Id)
{
    /// <summary>Its HUD key, made once (the host samples every parameter 20 times a second).</summary>
    public string Key { get; } = HudModel.FmodPrefix + Name;
}

/// <summary>
/// The penalty box on a friend's PC. Its widget exists only while a penalty runs and is created by the game's own
/// <c>TryStartPenalty</c>, which doesn't run in client mode, so it's created here when the host's penalty starts.
/// </summary>
internal sealed class PenaltyState
{
    private Team? _team;
    private Forward? _forward;
    private bool _active;
    private bool _saved;
    private Team? _savedTeam;
    private Forward? _savedForward;
    private float _savedRemaining;

    /// <summary>Keeps this match's own penalty (playback is starting).</summary>
    public void Save()
    {
        _savedTeam = PenaltyBoxSystem._team;
        _savedForward = PenaltyBoxSystem._forward;
        _savedRemaining = PenaltyBoxSystem._remaining;
        _saved = true;
        _team = null;
        _forward = null;
        _active = false;
    }

    /// <summary>Applies one penalty value.</summary>
    public void Set(string key, NetValue value, Func<NetValue, Type, object?> resolve)
    {
        switch (key)
        {
            case "penalty.team":
                _team = value.Kind == NetValueKind.Null ? null : resolve(value, typeof(Team)) as Team;
                Apply();
                break;
            case "penalty.forward":
                _forward = value.Kind == NetValueKind.Null ? null : resolve(value, typeof(Forward)) as Forward;
                Apply();
                break;
            case "penalty.remaining" when value.Kind == NetValueKind.Single32:
                PenaltyBoxSystem._remaining = (float)value.Number;
                break;
        }
    }

    /// <summary>Ends a mirrored penalty and puts this match's own back, time left included (playback over).</summary>
    public void Restore()
    {
        if (_saved)
        {
            // Unity's null: a team or forward destroyed since isn't put back.
            PenaltyBoxSystem._team = _savedTeam != null ? _savedTeam : null;
            PenaltyBoxSystem._forward = _savedForward != null ? _savedForward : null;
            PenaltyBoxSystem._remaining = _savedRemaining;
        }
        else if (_active)
        {
            PenaltyBoxSystem._team = null;
            PenaltyBoxSystem._forward = null;
        }

        _saved = _active = false;
        _team = _savedTeam = null;
        _forward = _savedForward = null;
    }

    private void Apply()
    {
        if (_team != null && _forward != null && !_active)
        {
            PenaltyBoxSystem._team = _team;
            PenaltyBoxSystem._forward = _forward;
            Il2CppTape2Tape.Hockey.UI.PenaltyTimerWidget.TryCreate(_team, _forward);
            _active = true;
        }
        else if (_team == null && _active)
        {
            PenaltyBoxSystem._team = null;
            PenaltyBoxSystem._forward = null;
            _active = false;
        }
    }
}

/// <summary>
/// Host side: samples <see cref="HudModel"/> at most 20 times a second, and right before every event (what event handlers
/// read only) so a friend's widgets already show the new value when the event arrives, and writes what changed
/// (everything every 5 s). Both samples are timed (<see cref="Costs"/>).
/// </summary>
internal sealed class HudRecorder
{
    private const long MinIntervalMicros = 50_000;
    private const long FullEveryMicros = 5_000_000;
    private const long ParameterListEveryMicros = 5_000_000;
    private const long Never = long.MinValue;

    private readonly StreamRecorder _recorder;
    private readonly Func<UnityEngine.Object, NetValue> _refOf;
    private readonly Dictionary<string, NetValue> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetValue> _sent = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, NetValue>> _changes = new();
    private readonly CostMeter _sampleCost = new("HUD sample");
    private readonly CostMeter _beforeEventCost = new("HUD before an event");
    private List<FmodParameter> _parameters = new();
    private long _lastSample = Never;
    private long _lastFull = Never;
    private long _lastParameterList = Never;
    private int _failures;
    private int _endsSwitches;
    private bool _listedParameters;

    public HudRecorder(StreamRecorder recorder)
    {
        _recorder = recorder;
        Func<ReplayObject, ushort> netIdOf = recorder.NetIdOf;
        _refOf = value => ObjectRefs.ToNet(value, netIdOf, recorder.Paths);
    }

    /// <summary>HUD messages written.</summary>
    public int Written { get; private set; }

    /// <summary>What the regular sample and the before-event sample cost on the host's main thread.</summary>
    public IEnumerable<CostMeter> Costs => new[] { _sampleCost, _beforeEventCost };

    /// <summary>Starts with an empty baseline, so the first sample sends everything.</summary>
    public void Start()
    {
        _sampleCost.Clear();
        _beforeEventCost.Clear();
        _sent.Clear();
        _lastSample = _lastFull = _lastParameterList = Never;
        Written = 0;
        _failures = 0;
        _endsSwitches = 0;
        HudModel.EndsLeftOut = 0;
        _listedParameters = false;
        MatchPresentation.HostStart();
        ChainMirror.HostStart();
        PlayerProxies.HostMatchStarted();
    }

    /// <summary>Called after each snapshot: samples everything when a sample is due.</summary>
    public void OnSnapshot(long now)
    {
        if (Due(_lastSample, now, MinIntervalMicros))
        {
            Sample(now, withParameters: true);
        }
    }

    /// <summary>
    /// Called right before an event is written: samples what event handlers read now, so it goes first (a listener earlier
    /// in the same frame may have just changed one, as setting the score does right before ScoreUpdated). The rest, FMOD
    /// parameters included, waits for the regular sample (<see cref="HudModel.Read"/>).
    /// </summary>
    public void BeforeEvent(BusEvent busEvent) => Sample(_recorder.Now, withParameters: false);

    /// <summary>Logs a summary.</summary>
    public void Stop()
    {
        int leftOut = HudModel.EndsLeftOut;
        ModLog.Info($"HUD recorder: {Written} HUD messages, {_sent.Count} values tracked ({_parameters.Count} FMOD global parameters); " +
            $"the teams switched ends {_endsSwitches} time{(_endsSwitches == 1 ? string.Empty : "s")}" +
            $"{(leftOut > 0 ? $" ({leftOut} sample{(leftOut == 1 ? string.Empty : "s")} taken mid-switch, both teams at one end, left the ends out)" : string.Empty)}" +
            $"{(_failures > 0 ? $"; {_failures} samples failed" : string.Empty)}.");
        ChainMirror.HostStop();
    }

    /// <summary>The home team's end changed (the teams switched ends): said once, then counted.</summary>
    private void NoteEndsSwitch(NetValue homeNorth)
    {
        if (_endsSwitches++ == 0)
        {
            int period = HudModel.GameManagerInstance()?.currentPeriod ?? 0;
            ModLog.Info($"HUD recorder: the teams switched ends (period {period}: home now defends the {(homeNorth.WholeValue != 0 ? "north" : "south")} end); " +
                $"friends follow it from the HUD values {TeamEnds.HomeKey} and {TeamEnds.VisitorKey}. Further switches are counted.");
            ModLog.Info($"HUD recorder: after the switch, {TeamEndsMirror.Readout()} (the goalies' kits change at the next faceoff, Goaltender.OnResetMap); " +
                "a friend's \"Team ends: after the move\" line says the same.");
        }
    }

    /// <summary>True when <paramref name="interval"/> has passed since <paramref name="last"/>, or it never happened.</summary>
    private static bool Due(long last, long now, long interval) => last == Never || now - last >= interval;

    private void Sample(long now, bool withParameters)
    {
        long start = CostMeter.Start();
        try
        {
            if (withParameters)
            {
                _lastSample = now;
                if (Due(_lastParameterList, now, ParameterListEveryMicros))
                {
                    _lastParameterList = now;
                    _parameters = HudModel.GlobalParameters();
                    if (!_listedParameters && _parameters.Count > 0)
                    {
                        _listedParameters = true;
                        ModLog.Info($"HUD recorder: mirroring {_parameters.Count} FMOD global parameters: {string.Join(", ", _parameters.Select(p => p.Name))}.");
                    }
                }
            }

            _current.Clear();
            HudModel.Read(_current, _refOf, withParameters ? _parameters : Array.Empty<FmodParameter>(), beforeEvent: !withParameters);
            if (withParameters)
            {
                // The intro, the win banner, the golfer outro and the golfer boss's forms (no event handler reads them).
                MatchPresentation.ReadHost(_current, _refOf);

                // The balls' chains, which are in no stream (Streaming/ChainMirror).
                ChainMirror.ReadHost(_current, _refOf);
            }

            // Everything every 5 s, and at once when a friend has just started getting the stream.
            bool full = withParameters && (Due(_lastFull, now, FullEveryMicros) | _recorder.TakeFullStateRequest());
            if (full)
            {
                _lastFull = now;
            }

            _changes.Clear();
            foreach (KeyValuePair<string, NetValue> pair in _current)
            {
                bool known = _sent.TryGetValue(pair.Key, out NetValue previous);
                if (full || !known || previous != pair.Value)
                {
                    if (known && previous != pair.Value && pair.Key == TeamEnds.HomeKey)
                    {
                        NoteEndsSwitch(pair.Value);
                    }

                    _changes.Add(pair);
                    _sent[pair.Key] = pair.Value;
                }
            }

            if (_changes.Count == 0)
            {
                return;
            }

            _recorder.WriteNewObjects();
            for (int i = 0; i < _changes.Count; i += HudStateMessage.MaxEntries)
            {
                List<KeyValuePair<string, NetValue>> part = _changes.GetRange(i, Math.Min(HudStateMessage.MaxEntries, _changes.Count - i));
                _recorder.Write(MessageType.HudState, Channel.Events, w => HudStateMessage.Write(w, part));
                Written++;
            }
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"HUD recorder: sample failed: {ModLog.SafeErrorText(ex)}");
            }
        }
        finally
        {
            (withParameters ? _sampleCost : _beforeEventCost).AddSince(start);
        }
    }
}

/// <summary>
/// Friend side: writes the host's HUD values into the local match, and puts the local values back when playback ends
/// (in the same-match test the match carries on afterwards).
/// </summary>
internal sealed class HudChannel : IPresentationChannel
{
    /// <summary>Distinct keys remembered (and traced on first sight): a host sends about a hundred; the rest are only counted.</summary>
    private const int MaxKeys = 512;

    private static readonly MessageType[] HandledTypes = { MessageType.HudState };

    private readonly SlowmoAudio _slowmo;
    private readonly Func<NetValue, Type, object?> _resolve;
    private readonly List<KeyValuePair<string, NetValue>> _values = new();
    private readonly Dictionary<string, NetValue> _saved = new(StringComparer.Ordinal);
    private readonly RefCache _refCache = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly PenaltyState _penalty = new();
    private readonly ClockCues _cues = new();
    private readonly TeamEndsMirror _ends = new();
    private readonly ScoreboardRedraw _redraw = new();
    private readonly Dictionary<string, PARAMETER_ID> _fmodIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _fmodWritten = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fmodLocal = new(StringComparer.Ordinal);
    private long _flashCount = -1;
    private float _flashDuration;
    private Color _flashColor;
    private int _flashes;
    private int _covers;
    private IReadOnlyDictionary<ushort, ReplayObject> _objects = new Dictionary<ushort, ReplayObject>();
    private bool _begun;
    private bool _brawlHud;
    private int _brawls;
    private int _messages;
    private int _undecodable;
    private int _failures;
    private int _untrackedKeys;

    /// <summary>Writes the host's values; <paramref name="slowmo"/> follows the host's slow-motion audio flag.</summary>
    public HudChannel(SlowmoAudio slowmo)
    {
        _slowmo = slowmo;
        _resolve = (value, type) => ObjectRefs.Resolve(value, type, _objects, _refCache);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Handles => HandledTypes;

    /// <inheritdoc />
    public void Begin(IStreamSource source, IReadOnlyDictionary<ushort, ReplayObject> objects)
    {
        // Set first: an End after a Begin that threw partway puts back what was saved by then.
        _begun = true;
        _objects = objects;
        _redraw.Ask();
        _messages = _undecodable = _failures = _brawls = _untrackedKeys = 0;
        _brawlHud = false;
        _cues.Reset();
        _ends.Begin();
        _flashCount = -1;
        _flashes = 0;
        _covers = 0;
        _keys.Clear();
        _refCache.Clear();
        _saved.Clear();
        _fmodIds.Clear();
        _fmodWritten.Clear();
        _fmodLocal.Clear();
        Online.HostMatchWitness.Begin();
        List<FmodParameter> parameters = HudModel.GlobalParameters();
        foreach (FmodParameter parameter in parameters)
        {
            // By whole key: the network's keys are looked up as they come, with no substring per value.
            _fmodIds[parameter.Key] = parameter.Id;
        }

        HudModel.Read(_saved, value => ObjectRefs.ToNet(value, NetIdOfLocal), parameters);
        _penalty.Save();
        PlayerProxies.Begin();
        MatchPresentation.Begin(_resolve);
        ChainMirror.Begin(objects);
    }

    /// <inheritdoc />
    public void Deliver(in TimedMessage message)
    {
        var reader = new BitReader(message.Payload);
        _values.Clear();
        if (!HudStateMessage.TryRead(ref reader, _values))
        {
            _undecodable++;
            return;
        }

        _messages++;
        foreach (KeyValuePair<string, NetValue> pair in _values)
        {
            _redraw.Note(pair.Key);
            if (_keys.Count >= MaxKeys && !_keys.Contains(pair.Key))
            {
                _untrackedKeys++;
            }
            else if (_keys.Add(pair.Key))
            {
                // The host's text, for the log: a key is written only when it's shaped like one (HostText), a value with a
                // skater's name in it as that name's shape; both where they're written, never where they're looked up.
                ModLog.Trace($"HUD channel: first {HostText.KeyForLog(pair.Key)} = {SafeText.Clean(LogNames.ReferenceText(pair.Value.ToString()), 80)}");
            }

            try
            {
                if (pair.Key.StartsWith("replay.flash", StringComparison.Ordinal))
                {
                    Flash(pair.Key, pair.Value);
                }
                else if (pair.Key == "brawl")
                {
                    // Handled once the whole message is in (SyncBrawl).
                }
                else if (_ends.Take(pair.Key, pair.Value))
                {
                    // Both teams' ends: followed once the whole message is in (SyncEnds).
                }
                else if (pair.Key == HudModel.SlowmoKey)
                {
                    _slowmo.FromHost(pair.Value.Kind == NetValueKind.Flag && pair.Value.WholeValue != 0);
                }
                else if (MatchPresentation.Take(pair.Key, pair.Value))
                {
                    // The intro, the win banner, the golfer outro and the golfer boss: followed once the whole message is in.
                }
                else if (ChainMirror.Take(pair.Key, pair.Value))
                {
                    // The balls' chains: followed once the whole message is in.
                }
                else if (pair.Key == HudModel.CoverKey)
                {
                    if (pair.Value.Kind == NetValueKind.Single32 && HudModel.SetPeriodCover((float)pair.Value.Number) && _covers++ == 0)
                    {
                        ModLog.Info("HUD channel: the host's period cover shows here (the game's own cover, at the host's opacity); further ones are counted.");
                    }
                }
                else
                {
                    if (pair.Key.StartsWith(HudModel.FmodPrefix, StringComparison.Ordinal))
                    {
                        NoteLocalFmodChange(pair.Key, pair.Value);
                    }

                    Online.HostMatchWitness.NoteHud(pair.Key, pair.Value);
                    HudModel.Write(pair.Key, pair.Value, _resolve, _penalty, _fmodIds);
                }
            }
            catch (Exception ex)
            {
                if (_failures++ < 5)
                {
                    ModLog.Warn($"HUD channel: couldn't set {HostText.KeyForLog(pair.Key)}: {ModLog.SafeErrorText(ex)}");
                }
            }
        }

        SyncEnds(message.TimeMicros);
        MatchPresentation.AfterMessage();
        ChainMirror.AfterMessage();
        if (_redraw.TakeAfterMessage())
        {
            // The scoreboard jumps to where the stream is: its digits and period markers only draw on the events that move them.
            Refresh("start");
        }

        // The players' markers: redrawn once their players and colours are in, then checked (Streaming/PlayerProxies).
        PlayerProxies.AfterHudMessage(HudModel.CurrentGameState());

        float? clock = null;
        bool countingUp = false;
        foreach (KeyValuePair<string, NetValue> pair in _values)
        {
            if (pair.Key == "brawl")
            {
                // After the fighters' health and fight flags, which come earlier in the same message.
                SyncBrawl(pair.Value);
            }

            if (pair.Key == "clock" && pair.Value.Kind == NetValueKind.Single32)
            {
                clock = (float)pair.Value.Number;
            }

            countingUp |= pair.Key == "clock.up" && pair.Value.WholeValue != 0;
        }

        if (clock is float remaining)
        {
            _cues.Update(remaining, countingUp);
        }
    }

    /// <summary>The goal replay's flash: colour and length arrive first, then the count; a new count is a new flash.</summary>
    private void Flash(string key, NetValue value)
    {
        switch (key)
        {
            case "replay.flash.color" when value.Kind == NetValueKind.Colour:
                _flashColor = new Color(value.X, value.Y, value.Z, value.W);
                return;
            case "replay.flash.duration" when value.Kind == NetValueKind.Single32:
                _flashDuration = (float)value.Number;
                return;
            case "replay.flash" when value.Kind == NetValueKind.Whole:
                bool fresh = _flashCount >= 0 && value.WholeValue != _flashCount;
                _flashCount = value.WholeValue;
                if (fresh && GoalReplay.Instance is GoalReplay replay && replay != null)
                {
                    replay.StartFlash(_flashDuration, _flashColor);
                    _flashes++;
                }

                return;
        }
    }

    /// <summary>After a message the host sent at <paramref name="hostMicros"/>: this PC's teams to the host's ends, if they're elsewhere (<see cref="TeamEndsMirror"/>).</summary>
    private void SyncEnds(long hostMicros)
    {
        try
        {
            _ends.Sync(hostMicros);
        }
        catch (Exception ex)
        {
            if (_failures++ < 5)
            {
                ModLog.Warn($"HUD channel: couldn't follow the host's team ends: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    private void SyncBrawl(NetValue bars)
    {
        try
        {
            bool before = _brawlHud;
            _brawlHud = HudModel.SyncBrawlHud(bars, _resolve, _brawlHud);
            _brawls += !before && _brawlHud ? 1 : 0;
        }
        catch (Exception ex)
        {
            if (_failures++ < 5)
            {
                ModLog.Warn($"HUD channel: couldn't show the brawl HUD: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>
    /// The stream starts again for this PC (a rejoin): what the host's game did meanwhile comes as values only (the full set it
    /// sends for a friend who starts getting the stream), with none of the events that would have drawn it.
    /// </summary>
    public void StreamRestarted() => _redraw.Ask();

    /// <inheritdoc />
    public void End(bool matchLoaded)
    {
        if (!_begun)
        {
            return;
        }

        _begun = false;
        _redraw.Reset();
        MatchPresentation.End(matchLoaded);
        ChainMirror.End(matchLoaded);
        // The penalty statics are put back either way: once the match has gone, its objects are destroyed and come back as
        // none rather than stay pointing at it.
        _penalty.Restore();
        int restored = 0;
        if (matchLoaded)
        {
            // The teams' own ends first (with the game's own switch), so the offside line put back below glows at the right
            // blue line.
            try
            {
                _ends.Restore(_saved);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"HUD channel: couldn't put this PC's team ends back: {ModLog.SafeErrorText(ex)}");
            }

            // Penalty values were put back above; the players' stand-ins exist only while playing; the ends just now.
            foreach (KeyValuePair<string, NetValue> pair in _saved.Where(p => !p.Key.StartsWith("penalty.", StringComparison.Ordinal) &&
                !p.Key.StartsWith("player", StringComparison.Ordinal) && p.Key != TeamEnds.HomeKey && p.Key != TeamEnds.VisitorKey))
            {
                try
                {
                    HudModel.Write(pair.Key, pair.Value, _resolve, _penalty, _fmodIds);
                    restored++;
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"HUD channel: couldn't put {pair.Key} back: {ModLog.SafeErrorText(ex)}");
                }
            }

            SyncBrawl(_saved.GetValueOrDefault("brawl"));
        }

        PlayerProxies.End();
        if (matchLoaded)
        {
            Refresh("end");
        }

        if (_fmodLocal.Count > 0)
        {
            // Diagnostics for audio: something on this PC set a mirrored parameter between two of the stream's values.
            ModLog.Info("HUD channel: FMOD parameters changed on this PC between the stream's values: " +
                string.Join(", ", _fmodLocal.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key[HudModel.FmodPrefix.Length..]} ×{p.Value}")) + ".");
        }

        int prefix = HudModel.FmodPrefix.Length;
        // Only the parameters this PC's own list has are named (their names are the game's); the rest are the host's text: counted.
        string fmod = SafeText.Clean(string.Join(", ", _keys.Where(_fmodIds.ContainsKey).Select(k => k[prefix..]).OrderBy(k => k, StringComparer.Ordinal)), 2000);
        int notHere = _keys.Count(k => k.StartsWith(HudModel.FmodPrefix, StringComparison.Ordinal) && !_fmodIds.ContainsKey(k));
        ModLog.Info($"HUD channel: {_messages} HUD messages applied ({_keys.Count} values{(_untrackedKeys > 0 ? $", {_untrackedKeys} more beyond the first {MaxKeys} keys" : string.Empty)}; " +
            $"FMOD global parameters: {(fmod.Length > 0 ? fmod : "none")}{(notHere > 0 ? $"; {notHere} more not written, not in this PC's list" : string.Empty)})" +
            $"{(_failures > 0 ? $", {_failures} writes failed" : string.Empty)}" +
            $"{(_undecodable > 0 ? $", {_undecodable} undecodable" : string.Empty)}" +
            $"{(_refCache.Deferred > 0 ? $", {_refCache.Deferred} reference lookups held back by the lookup budget" : string.Empty)}; " +
            $"{(matchLoaded ? $"{restored} local values put back" : "the match had gone, so its values and the FMOD parameters weren't put back")}. Brawl HUD shown {_brawls} times; " +
            $"clock sounds: {_cues.LastMinutes} last-minute, {_cues.Beeps} countdown beeps; replay flashes: {_flashes}; period covers: {_covers}; " +
            $"{_ends.Describe()}.");
        _refCache.Clear();
        _saved.Clear();
    }

    /// <inheritdoc />
    public void Finish()
    {
    }

    /// <summary>Before writing a mirrored FMOD parameter (by key): was it changed on this PC since the stream's last value?</summary>
    private void NoteLocalFmodChange(string key, NetValue value)
    {
        if (value.Kind != NetValueKind.Single32 || !_fmodIds.TryGetValue(key, out PARAMETER_ID id))
        {
            return;
        }

        if (_fmodWritten.TryGetValue(key, out float written) &&
            RuntimeManager.StudioSystem.getParameterByID(id, out float current) == Il2CppFMOD.RESULT.OK && Math.Abs(current - written) > 0.001f)
        {
            _fmodLocal[key] = _fmodLocal.GetValueOrDefault(key) + 1;
        }

        _fmodWritten[key] = (float)value.Number;
    }

    private static void Refresh(string when)
    {
        try
        {
            HudModel.RefreshEventDrivenWidgets();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"HUD channel: couldn't redraw the scoreboard at the {when}: {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>The local values are saved by the same keys: a replay object's network id is its id in the playing stream.</summary>
    private ushort NetIdOfLocal(ReplayObject replayObject)
    {
        foreach (KeyValuePair<ushort, ReplayObject> pair in _objects)
        {
            if (pair.Value.Pointer == replayObject.Pointer)
            {
                return pair.Key;
            }
        }

        return 0;
    }
}

/// <summary>
/// The clock's own sounds, which <c>TimeRemaining.Update</c> plays directly (not through events) and client mode holds:
/// the last-minute sound once when the clock passes 60 s, and a beep at each whole second from 10 down to 1 (the game
/// rounds the time left up), never while the clock counts up. Played from the mirrored clock through SoundManager's own
/// <c>PlayLastMinute</c> and <c>PlayLastSecondsBeep</c>.
/// </summary>
internal sealed class ClockCues
{
    private const float LastMinute = 60f;
    private const int FirstBeep = 10;

    private bool _seen;
    private bool _lastMinuteDone;
    private int _nextBeep = FirstBeep;

    /// <summary>Last-minute sounds played.</summary>
    public int LastMinutes { get; private set; }

    /// <summary>Countdown beeps played.</summary>
    public int Beeps { get; private set; }

    /// <summary>Forgets the clock (playback starting).</summary>
    public void Reset()
    {
        _seen = false;
        _lastMinuteDone = false;
        _nextBeep = FirstBeep;
        LastMinutes = Beeps = 0;
    }

    /// <summary>The mirrored clock read <paramref name="remaining"/> seconds.</summary>
    public void Update(float remaining, bool countingUp)
    {
        int rounded = (int)Math.Ceiling(remaining);
        if (!_seen)
        {
            // Joining mid-countdown: nothing for what already passed.
            _seen = true;
            _lastMinuteDone = remaining <= LastMinute;
            _nextBeep = remaining <= FirstBeep ? rounded - 1 : FirstBeep;
            return;
        }

        if (remaining > LastMinute + 1f)
        {
            _lastMinuteDone = false; // a new period
        }

        if (remaining > FirstBeep + 1f)
        {
            _nextBeep = FirstBeep;
        }

        if (countingUp)
        {
            return;
        }

        SoundManager? sound = SoundManager.Instance;
        if (sound == null)
        {
            return;
        }

        if (!_lastMinuteDone && remaining <= LastMinute)
        {
            _lastMinuteDone = true;
            sound.PlayLastMinute();
            LastMinutes++;
        }

        if (remaining <= FirstBeep && rounded >= 1 && _nextBeep >= rounded)
        {
            sound.PlayLastSecondsBeep();
            Beeps++;
            _nextBeep = rounded - 1;
        }
    }
}

/// <summary>
/// Host side: counts the goal replay's flashes (<c>GoalReplay.StartFlash</c>, a DOTween on an overlay, started by the
/// replay's own camera cuts) so a friend's PC can play the same ones. The patch only records; the game's flash runs as
/// usual.
/// </summary>
internal static class ReplayFlashProbe
{
    private static bool _installed;

    /// <summary>Flashes started since the game began.</summary>
    public static long Count { get; private set; }

    /// <summary>The last flash's length in seconds.</summary>
    public static float Duration { get; private set; }

    /// <summary>The last flash's colour.</summary>
    public static Color Color { get; private set; }

    /// <summary>Patches <c>GoalReplay.StartFlash</c> (once).</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return true;
        }

        System.Reflection.MethodInfo? target = HarmonyLib.AccessTools.Method(typeof(GoalReplay), nameof(GoalReplay.StartFlash));
        if (target == null)
        {
            ModLog.Warn("Replay flash probe: GoalReplay.StartFlash not found; goal-replay flashes won't be mirrored.");
            return false;
        }

        harmony.Patch(target, prefix: new HarmonyLib.HarmonyMethod(typeof(ReplayFlashProbe).GetMethod(nameof(StartFlashPrefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
        _installed = true;
        return true;
    }

    private static void StartFlashPrefix(float duration, Color color)
    {
        // Inside the game's call: record only, never throw, never skip the original.
        try
        {
            Duration = duration;
            Color = color;
            Count++;
        }
        catch (Exception)
        {
            // Nothing to do: a missed flash is cosmetic.
        }
    }
}
#endif
