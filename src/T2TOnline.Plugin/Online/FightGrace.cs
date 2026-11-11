#if HAS_INTEROP
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppFighting;
using Il2CppMovements.MovementStates;
using Il2CppSkaters;
using T2TOnline.Core.Hosting;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using Player = Il2CppTape2Tape.Player;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The fight grace window (D35). A friend sees an
/// AI's punch coming late (the stream's one-way delay plus their render delay) and their block reaches the host late
/// (the other way, plus the pad playout), so on the host their block would land after the punch.
/// <para>
/// The punch's hit is decided in one go at its apex (<c>PunchState.OnApexReached</c> → hit detection →
/// <c>Forward.OnPunched</c>, which checks the victim's <c>BlockState</c> inline), so the apex is what waits: when an AI's
/// punch reaches its apex with a remote friend's fighter in its reach (the move's own hit range and cone, or sphere)
/// who isn't blocking and could, the punch holds there (its animation track stopped) for up to that friend's window.
/// When every such friend blocks, the window runs out, or none of them could block any more, the apex happens and the
/// game resolves the hit itself: blocked, partly blocked or landed. <c>apexReached</c> makes sure it resolves once. A
/// punch that's no longer live (its fighter interrupted, the fight or match over) is let go without resolving here: its
/// animation runs again, so the game carries on with it as it would have without the wait. The verdict itself is
/// <see cref="GraceCheck"/> (Core, tested).
/// </para>
/// Only AI punches wait (a player on the host's PC would feel their own punch stall), only while friends are in the
/// session, and only in fights (the move has a block type). Waiting doesn't count while the host's game is paused.
/// Nothing here throws into the game: any failure lets the apex happen as normal.
/// <para>
/// Every AI punch's apex with a friend's fighter in reach is also told to the session (<see cref="Met"/>), the window on or
/// off: their block already up (the game blocks it at once and nothing waits), they couldn't block, or it waited; and how
/// each wait ended (<see cref="WaitEnded"/>). The session's brawl line counts them, so a block that needed no wait shows too.
/// </para>
/// </summary>
internal static class FightGrace
{
    /// <summary>The longest a punch waits (capped at 100 ms; everyone plays against the AI).</summary>
    public const long MaxWindowMicros = 100_000;

    /// <summary>Extra reach (metres) for a victim's movement while a punch waits.</summary>
    private const float ReachMargin = 0.75f;

    /// <summary>Reach (metres) of a sphere-cast move around its attacker beyond the move's radius (the hit point's offset).</summary>
    private const float SphereReach = 1.5f;

    /// <summary>Extra half-angle (degrees) on a cone move.</summary>
    private const float AngleMargin = 20f;

    /// <summary>Punches logged one by one per hosting session; the summary counts them all.</summary>
    private const int LoggedPerSession = 30;

    private static readonly Dictionary<nint, Hold> Holds = new();
    private static readonly List<(Hold Hold, GraceOutcome Outcome)> Due = new();
    private static nint _releasing;
    private static long _lastTick = long.MinValue;
    private static int _failures;
    private static int _logged;

    /// <summary>Host: true while friends are in the session (set by <see cref="OnlineSession"/>); punches wait only with the window's setting on too.</summary>
    public static Func<bool>? Hosting { get; set; }

    /// <summary>
    /// Host: an AI punch's apex met the fighter of the friend on this Rewired player (set by <see cref="OnlineSession"/>:
    /// the brawl's record). Called from the game's apex: must be quick and never throw.
    /// </summary>
    public static Action<int, ApexMeeting>? Met { get; set; }

    /// <summary>
    /// Host: a wait for the block of the friend on this Rewired player ended, how, how long it waited and how long ago its
    /// window ended, in microseconds (negative: before it ended) (set by <see cref="OnlineSession"/>). Must never throw.
    /// </summary>
    public static Action<int, GraceOutcome, long, long>? WaitEnded { get; set; }

    /// <summary>
    /// Host: the grace window for the friend on this Rewired player, in microseconds, or -1 when it isn't a remote
    /// friend's or they can't block right now (silent, or the AI plays for them) (set by <see cref="OnlineSession"/>).
    /// </summary>
    public static Func<int, long>? WindowFor { get; set; }

    /// <summary>Punches that waited for a friend.</summary>
    public static int Held { get; private set; }

    /// <summary>Of those, the ones the friend's block arrived for, the right way (high for a high punch, low for a low one).</summary>
    public static int Blocked { get; private set; }

    /// <summary>Of those, the ones a friend blocked the other way (the game dealt partial damage).</summary>
    public static int PartlyBlocked { get; private set; }

    /// <summary>Of those, the ones whose window ran out (the punch went on as normal).</summary>
    public static int TimedOut { get; private set; }

    /// <summary>Of those, the ones that went on early because the friend couldn't block any more.</summary>
    public static int Unblockable { get; private set; }

    /// <summary>Of those, the ones that went on early because hosting stopped, every friend left, or the window was switched off.</summary>
    public static int Released { get; private set; }

    /// <summary>Of those, the ones let go without resolving (the attacker interrupted, the fight or match over).</summary>
    public static int Interrupted { get; private set; }

    /// <summary>Counts every punch that starts or stops waiting: the session logs <see cref="Summary"/> when it changed.</summary>
    public static int Changes { get; private set; }

    private static bool Active => WindowFor != null && Hosting?.Invoke() == true;

    /// <summary>
    /// Patches <c>PunchState.OnExit</c>, then <c>OnApexReached</c> (a punch must never wait without the exit patch that
    /// lets it go). False, logged, when it can't.
    /// </summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            harmony.Patch(AccessTools.Method(typeof(PunchState), nameof(PunchState.OnExit)),
                prefix: new HarmonyMethod(typeof(FightGrace), nameof(ExitPrefix)));
        }
        catch (Exception ex)
        {
            ModLog.Error("Fight grace: couldn't patch PunchState.OnExit; friends' blocks get no grace window", ex);
            return false;
        }

        try
        {
            harmony.Patch(AccessTools.Method(typeof(PunchState), nameof(PunchState.OnApexReached)),
                prefix: new HarmonyMethod(typeof(FightGrace), nameof(ApexPrefix)));
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Fight grace: couldn't patch PunchState.OnApexReached; friends' blocks get no grace window", ex);
            return false;
        }
    }

    /// <summary>Call every frame (the plugin's update): lets waiting punches go when it's time.</summary>
    public static void Update()
    {
        long now = FrameClock.NowMicros;
        long elapsed = _lastTick == long.MinValue ? 0 : now - _lastTick;
        _lastTick = now;
        if (Holds.Count == 0)
        {
            return;
        }

        bool paused = Time.timeScale < 0.01f;
        Due.Clear();
        foreach (Hold hold in Holds.Values)
        {
            if (paused)
            {
                hold.Deadline += elapsed; // the host's pause menu: the window doesn't run
            }

            GraceOutcome outcome = Verdict(hold, now);
            if (outcome != GraceOutcome.Waiting)
            {
                Due.Add((hold, outcome));
            }
        }

        foreach ((Hold hold, GraceOutcome outcome) in Due)
        {
            // A punch resolved just before may have ended this one's state (its attacker hit): ExitPrefix took it then.
            if (Holds.Remove(hold.Key))
            {
                Finish(hold, outcome, resolveNow: true, now);
            }
        }
    }

    /// <summary>
    /// Lets every waiting punch go (hosting stopped): live ones resolve now, the rest are dropped. The next hosting session
    /// logs its own punches one by one again.
    /// </summary>
    public static void ReleaseAll(string why)
    {
        long now = FrameClock.NowMicros;
        var holds = new List<Hold>(Holds.Values);
        Holds.Clear();
        foreach (Hold hold in holds)
        {
            Finish(hold, GraceOutcome.SessionEnded, resolveNow: true, now, why);
        }

        _logged = 0;
    }

    /// <summary>A summary for the log, or empty if nothing waited. Its outcomes add up to the punches that waited.</summary>
    public static string Summary() => Held == 0 ? string.Empty :
        $"Fight grace: {Held} AI punches waited for a friend's block: {Blocked} blocked in time, {PartlyBlocked} blocked the other way (partial damage), " +
        $"{TimedOut} went on after the window, {Unblockable} went on early (the friend couldn't block), " +
        $"{Released} went on when the session ended or the window was switched off, {Interrupted} interrupted" +
        (Holds.Count > 0 ? $", {Holds.Count} still waiting." : ".");

    /// <summary>Whether a waiting punch should go now, and why (<see cref="GraceCheck"/>).</summary>
    private static GraceOutcome Verdict(Hold hold, long now)
    {
        try
        {
            bool live = Live(hold.Punch, hold.Attacker);
            var check = default(GraceCheck);
            if (live)
            {
                foreach (Forward victim in hold.Victims)
                {
                    if (victim == null)
                    {
                        // Gone: it can neither block nor be hit, so there's nothing to wait for (and it didn't block).
                        check.Victim(gone: true, blocking: false, blockingRightWay: false, canBlock: false);
                        continue;
                    }

                    BlockType? block = BlockingAs(victim);
                    check.Victim(gone: false, block != null, block == hold.BlockedBy, CanBlock(victim));
                }
            }

            return check.Outcome(live, now, hold.Deadline, ModSettings.FightGraceWindow, Active);
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Fight grace: checking a waiting punch failed, it goes on now: {ModLog.SafeErrorText(ex)}");
            }

            return GraceOutcome.TimedOut;
        }
    }

    /// <summary>The log's words for an outcome.</summary>
    private static string Describe(GraceOutcome outcome) => outcome switch
    {
        GraceOutcome.Blocked => "blocked",
        GraceOutcome.PartlyBlocked => "blocked the other way (partial damage)",
        GraceOutcome.TimedOut => "timed out",
        GraceOutcome.Unblockable => "the friend couldn't block any more",
        GraceOutcome.SwitchedOff => "the fight grace window was switched off",
        GraceOutcome.SessionEnded => "the session ended",
        _ => "no longer live, let go",
    };

    /// <summary>
    /// A wait is over: a live punch gets its apex now (the game resolves the hit) unless <paramref name="resolveNow"/>
    /// is false (the caller lets the apex run itself); a punch that's no longer live is let go without resolving. Its
    /// animation runs again either way while it's still its fighter's state: a fighter taken out of the fight with no state
    /// change (the brawl's end) would otherwise stay frozen mid-punch. <paramref name="why"/> replaces the outcome's words in
    /// the log.
    /// </summary>
    private static void Finish(Hold hold, GraceOutcome outcome, bool resolveNow, long now, string? why = null)
    {
        bool live = false;
        bool current = false;
        try
        {
            current = StillCurrent(hold.Punch, hold.Attacker);
            live = current && hold.Attacker.isInFightMode;
        }
        catch (Exception)
        {
            // Treated as gone.
        }

        if (!live)
        {
            outcome = GraceOutcome.Interrupted;
        }

        Changes++;
        switch (outcome)
        {
            case GraceOutcome.Blocked:
                Blocked++;
                break;
            case GraceOutcome.PartlyBlocked:
                PartlyBlocked++;
                break;
            case GraceOutcome.TimedOut:
                TimedOut++;
                break;
            case GraceOutcome.Unblockable:
                Unblockable++;
                break;
            case GraceOutcome.Interrupted:
                Interrupted++;
                break;
            default:
                Released++; // hosting stopped, every friend left, or the window was switched off
                break;
        }

        if (_logged++ < LoggedPerSession)
        {
            ModLog.Info($"Fight grace: an AI punch ({hold.Move}) at {hold.FriendNames} waited {(now - hold.Started) / 1000.0:0} ms of {hold.Window / 1000.0:0}: " +
                $"{(live ? why ?? Describe(outcome) : Describe(outcome))}.");
        }

        TellWaitEnded(hold, outcome, now);

        try
        {
            // A state that ended had its track put back already (ExitPrefix), and a fighter that's gone isn't ours to touch
            // (its pooled track entry may play something else by now). A fighter out of the fight plays its punch on as the
            // game would have, its apex then resolving through the punch's own fallback.
            if (current)
            {
                RestoreTrack(hold);
            }

            if (!live || !resolveNow || hold.Punch.apexReached)
            {
                return;
            }

            // The game's own apex, now: it sets the track to the recovery speed and resolves the hit.
            _releasing = hold.Key;
            hold.Punch.OnApexReached();
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Fight grace: couldn't let a punch go: {ModLog.SafeErrorText(ex)}");
            }
        }
        finally
        {
            _releasing = 0;
        }
    }

    /// <summary>The punch is still its attacker's current state, in a fight, with its fighter alive.</summary>
    private static bool Live(PunchState punch, Forward attacker) => StillCurrent(punch, attacker) && attacker.isInFightMode;

    /// <summary>The punch is still its attacker's current state, with its fighter alive (in a fight or not).</summary>
    private static bool StillCurrent(PunchState punch, Forward attacker) =>
        !punch.WasCollected && attacker != null &&
        attacker.movementManager?.MovementStateManager?._currentMovementState is { } current && current.Pointer == punch.Pointer;

    /// <summary>
    /// The punch's track runs again, if it's still the entry we stopped and still at our 0: track entries are pooled, so
    /// the same entry may play another animation by now, and any other speed was set by the game since.
    /// </summary>
    private static void RestoreTrack(Hold hold)
    {
        if (hold.Punch._track is { } track && track.Pointer == hold.TrackPointer && track.TimeScale == 0f)
        {
            track.TimeScale = hold.TrackSpeed;
        }
    }

    /// <summary>The punch's apex: wait if an AI's punch would hit a remote friend's fighter who isn't blocking yet.</summary>
    private static bool ApexPrefix(PunchState __instance)
    {
        try
        {
            nint key = __instance.Pointer;
            if (key == _releasing)
            {
                return true; // our own release
            }

            if (Holds.TryGetValue(key, out Hold? waiting))
            {
                // The punch's Update fallback asks every frame once its track passed the apex: check here too.
                GraceOutcome outcome = Verdict(waiting, FrameClock.NowMicros);
                if (outcome == GraceOutcome.Waiting)
                {
                    return false;
                }

                Holds.Remove(key);
                Finish(waiting, outcome, resolveNow: false, FrameClock.NowMicros);
                return true; // the apex runs now
            }

            if (!Active || __instance.apexReached || !TryStartHold(__instance, out Hold? hold))
            {
                return true;
            }

            Holds[key] = hold!;
            Held++;
            Changes++;
            return false;
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Fight grace: punch check failed, the punch goes on: {ModLog.SafeErrorText(ex)}");
            }

            return true;
        }
    }

    private static bool TryStartHold(PunchState punch, out Hold? hold)
    {
        hold = null;
        MoveConfig? move = punch.moveConfig;
        Forward? attacker = punch.forward;
        Il2CppSpine.TrackEntry? track = punch._track;
        if (move == null || move.blockedBy == BlockType.None || attacker == null || !attacker.isInFightMode || track == null)
        {
            return false;
        }

        PlayersManager? players = PlayersManager.Instance;
        if (players == null || players.IsSkaterControlled(attacker, out Player? _))
        {
            return false; // a player's punch: theirs to feel, never stalled
        }

        Il2CppSystem.Collections.Generic.List<Forward>? opponents = punch.PunchableOpponents();
        if (opponents == null)
        {
            return false;
        }

        var victims = new List<Forward>();
        var names = new List<string>();
        var rewiredIds = new List<int>();
        long window = 0;
        bool waits = ModSettings.FightGraceWindow;
        for (int i = 0; i < opponents.Count; i++)
        {
            Forward? victim = opponents[i];
            if (victim == null || !InReach(punch, move, attacker, victim) || !players.IsSkaterControlled(victim, out Player? player) || player == null)
            {
                continue;
            }

            int rewiredId = player.RewiredPlayer?.id ?? -1;
            long theirs = Math.Min(WindowFor!(rewiredId), MaxWindowMicros);
            if (theirs <= 0)
            {
                continue; // not a remote friend's, or they can't press anything now
            }

            // How the apex meets them, for the brawl's line: a block already up is the game's at once, and never waits.
            BlockType? block = BlockingAs(victim);
            ApexMeeting meeting = block != null ? (block == move.blockedBy ? ApexMeeting.BlockUp : ApexMeeting.BlockUpOtherWay)
                : !CanBlock(victim) ? ApexMeeting.CouldNotBlock
                : !waits ? ApexMeeting.WindowOff
                : ApexMeeting.Waited;
            Tell(rewiredId, meeting);
            if (meeting != ApexMeeting.Waited)
            {
                continue;
            }

            victims.Add(victim);
            names.Add($"game player {player._PlayerId_k__BackingField}");
            rewiredIds.Add(rewiredId);
            window = Math.Max(window, theirs);
        }

        if (victims.Count == 0)
        {
            return false;
        }

        // Hold: the windup pause must not restart the track, and the track stops at the apex.
        punch._windupPausePending = false;
        punch._windupPausing = false;
        float speed = track.TimeScale > 0 ? track.TimeScale : punch._executionSpeed > 0 ? punch._executionSpeed : 1f;
        track.TimeScale = 0f;
        long now = FrameClock.NowMicros;
        hold = new Hold(punch.Pointer, punch, attacker, victims, move.blockedBy, track.Pointer, speed, now, window, string.Join(" and ", names), MoveName(move), rewiredIds)
        {
            Deadline = now + window,
        };
        return true;
    }

    /// <summary>The move's name for the log ("Jab", "Uppercut"), or "a move" when it has none.</summary>
    private static string MoveName(MoveConfig move)
    {
        string? name = move.moveName;
        return string.IsNullOrEmpty(name) ? "a move" : name.Length > 40 ? name[..40] : name;
    }

    /// <summary>Tells the session how an apex met a friend (never throws into the game's apex).</summary>
    private static void Tell(int rewiredId, ApexMeeting meeting)
    {
        try
        {
            Met?.Invoke(rewiredId, meeting);
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Fight grace: noting a punch for the brawl's line failed: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>Tells the session how a wait ended, for each friend it waited for (never throws).</summary>
    private static void TellWaitEnded(Hold hold, GraceOutcome outcome, long now)
    {
        try
        {
            foreach (int rewiredId in hold.RewiredIds)
            {
                WaitEnded?.Invoke(rewiredId, outcome, now - hold.Started, now - hold.Deadline);
            }
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Fight grace: noting a wait for the brawl's line failed: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>
    /// The victim is where this move's own hit detection could reach it, with a margin for its movement while the punch
    /// waits: a cone move's hit range and angle, or a sphere move's radius around its hit point.
    /// </summary>
    private static bool InReach(PunchState punch, MoveConfig move, Forward attacker, Forward victim)
    {
        Vector3 toVictim = victim.transform.position - attacker.transform.position;
        toVictim.y = 0f;
        float distance = toVictim.magnitude;
        if (!move.useConeHitDetection)
        {
            return distance <= move.radius + SphereReach + ReachMargin;
        }

        if (distance > move.hitRange + ReachMargin)
        {
            return false;
        }

        Vector3 aim = punch._hitDirection;
        aim.y = 0f;
        return distance < 0.01f || aim.sqrMagnitude < 0.0001f || Vector3.Angle(aim, toVictim) <= (move.hitConeAngleDegrees * 0.5f) + AngleMargin;
    }

    /// <summary>The punch's state ends (interrupted, or done): a waiting punch stops waiting, its track as it was.</summary>
    private static void ExitPrefix(PunchState __instance)
    {
        try
        {
            if (Holds.Remove(__instance.Pointer, out Hold? hold))
            {
                Interrupted++;
                Changes++;
                RestoreTrack(hold);
                if (_logged++ < LoggedPerSession)
                {
                    ModLog.Info($"Fight grace: an AI punch ({hold.Move}) at {hold.FriendNames} was interrupted while waiting.");
                }

                TellWaitEnded(hold, GraceOutcome.Interrupted, FrameClock.NowMicros);
            }
        }
        catch (Exception)
        {
            // The game carries on.
        }
    }

    /// <summary>
    /// The block <paramref name="fighter"/> holds right now (high or low), or null when it isn't blocking. The game's hit
    /// check wants the type the punch is blocked by; the other one takes partial damage.
    /// </summary>
    private static BlockType? BlockingAs(Forward fighter)
    {
        MovementStateManager? states = fighter.movementManager?.MovementStateManager;
        MovementState? current = states?._currentMovementState;
        return current != null && states!._blockState is { } block && current.Pointer == block.Pointer ? block._type : null;
    }

    private static bool CanBlock(Forward fighter) =>
        fighter.isInFightMode && fighter.movementManager?.MovementStateManager?._currentMovementState is { } state && state.CanBlock();

    private sealed record Hold(nint Key, PunchState Punch, Forward Attacker, List<Forward> Victims, BlockType BlockedBy, nint TrackPointer, float TrackSpeed, long Started, long Window,
        string FriendNames, string Move, List<int> RewiredIds)
    {
        public long Deadline { get; set; }
    }
}
#endif
