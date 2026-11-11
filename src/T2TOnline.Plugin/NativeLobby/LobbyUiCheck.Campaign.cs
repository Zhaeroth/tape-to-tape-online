#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Il2Cpp;
using Il2CppBishopGames.UI;
using Il2CppT2T.Debugging;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

// The campaign tours (run.txt's first line "campaign act1" or "campaign full"; Core's CampaignTour): a co-op run hosted by
// the tour's pad with the one-PC friend seated, in the tour's own run file (TourHolds), played by the game's own campaign
// walker (TourCampaignWalker), whose coroutines the tour's frames pump: FinishRunStart from the squad screen,
// WalkWorldMapToMatch for every node, WatchRunEnd after every match. Each match is ended by the game's
// SoakMatchCheats.ForceHomeWin after a dwell. The walker answers any prompt yes, so it is ticked only while no prompt of
// the mod's is up. What a player presses (Start, the map's menu, the concede prompt, the period stats screen, the Team
// Canada curse pick) the tour's pad presses; a game call stands in only when a press did nothing, and says so.
internal static partial class LobbyUiCheck
{
    private const double ModPromptSeconds = 15;
    private const double RewardFallbackSeconds = 12;
    private const double StatsScreenSeconds = 2;
    private const double CanadaTapSeconds = 1.2;
    private const int CanadaTapsBeforeClick = 12;
    private const int MaxWalkerLinesLogged = 400;
    private const double InPlaySeconds = 120;
    private const double MapSeconds = 90;
    private const double WalkSeconds = 420;
    private const double FinishRunStartSeconds = 200;
    private const double RunEndSeconds = 240;
    private const double WatchSeconds = 0.1;
    private const double TapSeconds = 0.1;
    private const double MapInputSeconds = 30;
    private const double RunEndAppearSeconds = 20;
    private const double RunEndWalkSeconds = 90;
    private const int RunEndNoEffectPresses = 2;

    private static readonly CampaignTourTally Tally = new();
    private static readonly HashSet<IntPtr> RewardMenusSeen = new();
    private static CampaignTourKind _cpKind;
    private static CampaignTourArgs _cpArgs;
    private static string? _cpBroken;
    private static bool _cpReady;
    private static bool _cpRunEnded;
    private static bool _cpWon;
    private static bool _cpWalking;
    private static int _cpWalks;
    private static int _cpWalkerLines;
    private static int _cpDroppedReported;
    private static int _cpTimeScaleResets;
    private static int _cpBarrierAtStart;
    private static int _cpPromptAnswers;
    private static int _cpRewardFallbacks;
    private static int _cpWatchFailures;
    private static uint _cpMapCheckpoint;
    private static int _cpAct;
    private static int _cpFloor;
    private static bool _cpRecordHeld;
    private static bool _cpMenuRowPressed;
    private static CoroutinePumpState _cpPumpState;
    private static double _cpNextWatch;
    private static double _cpNextNodeTypeRead;
    private static PromptTemplateMenu? _cpPrompt;
    private static string _cpPromptKind = string.Empty;
    private static double _cpPromptSince;
    private static PadButtons _cpTapHeld;
    private static double _cpTapUntil;
    private static IntPtr _cpStatsMenu;
    private static double _cpStatsSince;
    private static int _cpStatsPresses;
    private static IntPtr _cpCanadaMenu;
    private static double _cpCanadaNext;
    private static int _cpCanadaTaps;
    private static IntPtr _cpRewardMenu;
    private static double _cpRewardSince;
    private static CoroutinePump? _cpRewardPump;
    private static string? _cpRewardFailureSaid;
    private static string _cpRunFileSeen = "missing";

    /// <summary>Whether a campaign tour is the one running: its steps share a lobby and a run.</summary>
    private static bool Campaigning => CampaignTour.IsCampaignPlan(_tour);

    private static void AddCampaignSteps()
    {
        Steps["cp_host"] = StepCampaignHost;
        Steps["cp_newrun"] = StepCampaignNewRun;
        Steps["cp_first"] = () => StepCampaignNodes(firstMatch: true);
        Steps[CampaignTour.KeyboardHostStep] = StepCampaignKeyboardHost; // LobbyUiCheck.KeyboardHost.cs
        Steps["cp_continue"] = StepCampaignContinue;
        Steps["cp_nodes"] = () => StepCampaignNodes(firstMatch: false);
        Steps["cp_end"] = StepCampaignEnd;
        Steps["cp_close"] = StepCampaignClose;
        Steps["op_failload"] = StepOnePcFailLoad;
        AddCampaignKeySteps(); // the steps the opt-in keys add (LobbyUiCheck.CampaignKeys.cs)
    }

    // ---- The steps ----

    private static IEnumerable<Wait> StepCampaignHost()
    {
        ResetCampaign();
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: {(_cpKind == CampaignTourKind.Act1 ? "act 1, then a concede" : "the whole run")}; seed {_cpArgs.Seed}; the last match {(_cpArgs.Record ? "recorded" : "not recorded")}"));

        // Before anything hosts: the co-op run file is the tour's own, never one of the player's slots.
        if (!TourHolds.UseTourRunFile(out string problem))
        {
            Broken($"the tour's own run file was refused ({problem}); nothing is hosted");
            yield break;
        }

        foreach (Wait wait in StepWalkStart())
        {
            yield return wait;
        }

        if (_walk == null)
        {
            Broken("the tour's pad isn't there");
            yield break;
        }

        foreach (Wait wait in OnlineRowOnMain())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenGroup("A on Online", null))
        {
            yield return wait;
        }

        if (Cursor() != "HostLobby")
        {
            foreach (Wait wait in Press("up", PadButtons.DPadUp, () => Cursor() == "HostLobby", "the cursor on Host Lobby"))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in OpenLobby("A on Host Lobby"))
        {
            yield return wait;
        }

        OnlineSession session = Sd();
        if (Flow().Host != LobbyHostState.Open || TourHolds.RunFile == null)
        {
            Broken($"no lobby to play the run from (flow {Flow().Host}, the tour's run file {(TourHolds.RunFile == null ? "not set" : "set")})");
            yield break;
        }

        _cpBarrierAtStart = session.TourBarrierResets;
        if (LateJoiner)
        {
            StepNote($"campaign: {CampaignTour.LateJoinerKey}=on: nobody is seated at the lobby; the one-PC friend joins on the run's map after the Continue ({CampaignTour.LateJoinerStep})");
        }
        else
        {
            Check("the friend arrives", () => session.TourSeatFriend(holdHello: false), "a friend session on this game");
            yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 12, "the friend Here");
            StepNote(SeatLine(FriendSeat()));
            _cpFriendSeated = Check("the friend seated", () => FriendSeat().Stage == SeatStage.Here, "Here on the first free seat");
        }
        for (int i = 0; i < 3 && session.Actions.Snapshot().Mode != LobbyMode.Campaign; i++)
        {
            foreach (Wait wait in Press("LB", PadButtons.LeftBumper, () => session.Actions.Snapshot().Mode == LobbyMode.Campaign, "Campaign"))
            {
                yield return wait;
            }
        }

        yield return Soft(() => session.Actions.Snapshot().StartAllowed, 10, "Start allowed");
        LobbyState state = session.Actions.Snapshot();
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: the lobby: mode {state.Mode}, the save row {(state.Save.HasRun ? $"a run at {state.Save.Act}-{state.Save.Floor}" : "New Run")} (the tour's own run file stands for slot {state.Save.Slot}); Start {(state.StartAllowed ? "allowed" : "not allowed")}"));
        if (state.Mode != LobbyMode.Campaign || state.Save.HasRun || TourHolds.RunFile == null)
        {
            Broken(state.Save.HasRun ? "the save row shows a run: the tour's own run file isn't empty" : $"the lobby's mode is {state.Mode}, not Campaign");
            yield break;
        }

        foreach (Wait wait in Snap("campaign_lobby", SettleFrames))
        {
            yield return wait;
        }

        _cpReady = true;
    }

    private static IEnumerable<Wait> StepCampaignNewRun()
    {
        if (!CampaignGoesOn())
        {
            yield break;
        }

        // The walker set up at the lobby, before Start: a delegate that can't be made leaves nothing half-started.
        if (TourCampaignWalker.Configure(_cpArgs.Seed, () => Stopping) is { } problem)
        {
            Broken(problem);
            yield break;
        }

        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: the walker's ShouldStop, GlobalMatchCount and BeforeSelectNode set before its first call; RunIndex 1, SquadSeed {_cpArgs.Seed}; its delegates held for the game's life"));

        OnlineSession session = Sd();
        foreach (Wait wait in MoveTo(Row(LobbyFocusKind.StartRow)))
        {
            yield return wait;
        }

        yield return Soft(() => session.Actions.Snapshot().StartAllowed, 8, "Start allowed");
        foreach (Wait wait in Press("Start, New Run", PadButtons.Start, () => Flow().Host is LobbyHostState.Counting or LobbyHostState.Starting or LobbyHostState.GameFlow, "the countdown"))
        {
            yield return wait;
        }

        yield return Soft(() => Flow().Host is LobbyHostState.GameFlow or LobbyHostState.InGame, 12, "the friend's ack and the game's flow");
        StepNote($"the barrier: last ack {Flow().LastAckAfterMicros / 1000} ms after it began, awaiting {Flow().AwaitingAcks.Count}; {session.TourFriendLine()}");
        yield return Soft(() => Top()?.TryCast<ChooseMetaMenu>() != null, 25, "the game's squad screen");
        Check("New Run by the pad", () => Top()?.TryCast<ChooseMetaMenu>() != null && Flow().AwaitingAcks.Count == 0, "the game's squad screen, every friend acked");
        if (Top()?.TryCast<ChooseMetaMenu>() == null)
        {
            Broken("no squad screen after Start");
            yield break;
        }

        foreach (Wait wait in Snap("squad_screen", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in PumpWalker("FinishRunStart", () => CampaignSoakWalker.FinishRunStart(TourCampaignWalker.OnFailed, TourCampaignWalker.OnLog, CampaignTour.RandomSquad), FinishRunStartSeconds, null))
        {
            yield return wait;
        }

        if (_cpPumpState != CoroutinePumpState.Done)
        {
            Broken(_cpPumpState == CoroutinePumpState.Stopped ? "stopped" : "the walker didn't start the run");
            yield break;
        }

        yield return Soft(() => { Watch(); return WorldMap.MapLoaded; }, MapSeconds, "the world map");
        ReadRun(out int act, out int floor);
        Check("the run started", () => WorldMap.MapLoaded && act >= 1, "the world map of a run at act 1 or later");
        Check("the run file's first checkpoint", () => Checkpoint() >= 1, "run checkpoint 1 or later");
        if (!WorldMap.MapLoaded)
        {
            Broken("the world map didn't load");
            yield break;
        }

        ArmRewards();
        _cpMapCheckpoint = Checkpoint();
        _cpRunFileSeen = RunFileText();
        _cpAct = act;
        _cpFloor = floor;
        StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: the run is on the map at {act}-{floor}; checkpoint {_cpMapCheckpoint}; the tour's run file {RunFileText()}"));
        foreach (Wait wait in Snap("world_map", SettleFrames))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepCampaignNodes(bool firstMatch)
    {
        if (!CampaignGoesOn())
        {
            yield break;
        }

        if (!firstMatch)
        {
            foreach (Wait wait in ScreenShowsTeam())
            {
                yield return wait;
            }
        }

        int startMatches = Tally.Matches;
        while (!Stopping)
        {
            ReadRun(out int act, out _);
            if (firstMatch)
            {
                if (Tally.Matches > startMatches || _cpRunEnded)
                {
                    break;
                }

                if (Tally.Nodes >= CampaignTour.MaxNodesAct1)
                {
                    Problem(string.Create(CultureInfo.InvariantCulture, $"no match won in {Tally.Nodes} nodes"));
                    break;
                }
            }
            else
            {
                CampaignWalkVerdict verdict = CampaignTour.Next(_cpKind, act, _cpRunEnded, Tally.Nodes, FrameClock.Seconds - _startedAt, _limit);
                if (verdict != CampaignWalkVerdict.WalkOn)
                {
                    StepNote($"campaign: the walk ends: {CampaignTour.Said(verdict)}");
                    break;
                }
            }

            foreach (Wait wait in WalkOne())
            {
                yield return wait;
            }

            if (_cpBroken != null || _cpRunEnded)
            {
                break;
            }
        }

        // The step's claim includes the drop: a walk that ended (its time, its node cap) before the drop's match says so.
        if (!firstMatch && !Stopping && _cpBroken == null && CampaignTour.DropNotPlayed(Tally.Matches) is { } notPlayed)
        {
            Problem($"campaign: {notPlayed}");
        }

        Flush();
    }

    private static IEnumerable<Wait> StepCampaignContinue()
    {
        if (!CampaignGoesOn())
        {
            yield break;
        }

        if (_cpRunEnded)
        {
            Problem("the run has ended: nothing to continue");
            yield break;
        }

        OnlineSession session = Sd();
        ReadRun(out int act, out int floor);
        foreach (Wait wait in MapMenu("Back", menu => menu.m_BackButton))
        {
            yield return wait;
        }

        yield return Soft(() => { Watch(); return LobbyBack(); }, 60, "back at the lobby");
        yield return Seconds((LobbyTiming.InputLockMicros / 1_000_000.0) + WalkSettleSeconds);
        LobbyState state = session.Actions.Snapshot();
        StepNote($"campaign: back at the lobby from the map's menu: ribbon {CampaignTour.RibbonText(state.Result)}; the save row {(state.Save.HasRun ? $"a run at {state.Save.Act}-{state.Save.Floor}" : "New Run")}; {SeatLine(FriendSeat())}");
        Check("back at the lobby from the map's menu", LobbyBack, "the lobby open, its shell up");
        if (CampaignTour.SavedRibbonProblem(state.Result, act, floor) is { } ribbon)
        {
            Fail($"the ribbon after the map's menu: {ribbon}");
        }

        Check("the slot after the map's menu", () => session.Actions.Snapshot().Save.HasRun, "a run to continue");
        foreach (Wait wait in Snap("ribbon_run_saved", SettleFrames))
        {
            yield return wait;
        }

        if (!LobbyBack() || TourHolds.RunFile == null)
        {
            Broken("not back at the lobby with the tour's run file");
            yield break;
        }

        foreach (Wait wait in MoveTo(Row(LobbyFocusKind.StartRow)))
        {
            yield return wait;
        }

        yield return Soft(() => session.Actions.Snapshot().StartAllowed, 10, "Start allowed");
        foreach (Wait wait in Press("Start, Continue", PadButtons.Start, () => Flow().Host is LobbyHostState.Counting or LobbyHostState.Starting or LobbyHostState.GameFlow, "the countdown"))
        {
            yield return wait;
        }

        yield return Soft(() => { Watch(); return WorldMap.MapLoaded; }, MapSeconds, "the run continued on the world map");
        ReadRun(out int actAfter, out int floorAfter);
        Check("Continue by the pad", () => WorldMap.MapLoaded && actAfter == act && floorAfter == floor, string.Create(CultureInfo.InvariantCulture, $"the world map of the same run at {act}-{floor}"));
        if (!WorldMap.MapLoaded)
        {
            Broken("the run didn't continue to the map");
            yield break;
        }

        _cpMapCheckpoint = Checkpoint();
        StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: continued at {actAfter}-{floorAfter}; checkpoint {_cpMapCheckpoint}"));
    }

    private static IEnumerable<Wait> StepCampaignEnd()
    {
        OnlineSession session = Sd();
        if (!_cpReady)
        {
            Problem("skipped: cp_host didn't set the tour's run up");
            yield break;
        }

        if (!_cpRunEnded && WorldMap.MapLoaded)
        {
            // A walk that failed inside an event leaves its screen up: left by the pad first (LobbyUiCheck.Takeover.cs).
            if (Top()?.TryCast<EventChoiceMenu>() is { } eventScreen && !eventScreen.WasCollected)
            {
                foreach (Wait wait in LeaveEventScreen(eventScreen))
                {
                    yield return wait;
                }
            }

            // act1, or a whole run the time or a failure cut short: the game's concede, by the pad.
            foreach (Wait wait in Concede())
            {
                yield return wait;
            }
        }
        else if (!_cpRunEnded)
        {
            Problem($"not on the map to concede from (scene {WorldMap.SceneName}){(_cpBroken != null ? $": {_cpBroken}" : string.Empty)}");
            yield break;
        }
        else if (!LobbyBack())
        {
            foreach (Wait wait in RunEndScreens())
            {
                yield return wait;
            }
        }

        yield return Soft(() => { Watch(); return LobbyBack(); }, 60, "back at the lobby");
        yield return Seconds((LobbyTiming.InputLockMicros / 1_000_000.0) + WalkSettleSeconds);
        LobbyState state = session.Actions.Snapshot();
        Check("back at the lobby after the run", LobbyBack, "the lobby open, its shell up");
        if (CampaignTour.RibbonProblem(state.Result, _cpWon) is { } ribbon)
        {
            Fail($"the ribbon after the run: {ribbon}");
        }

        Check("the slot after the run", () => !session.Actions.Snapshot().Save.HasRun, "New Run");
        foreach (Wait wait in Snap("ribbon_run_end", SettleFrames))
        {
            yield return wait;
        }

        (long achievements, long allowances, long writes) = TourHolds.Counts;
        string runState = _cpWon ? "won" : _cpRunEnded || state.Result.Kind == LobbyResultKind.RunOver ? "over" : "stopped";
        int resets = _cpTimeScaleResets + Math.Max(session.TourBarrierResets - _cpBarrierAtStart, 0);
        StepNote(CampaignTour.SummaryLine(Tally, runState, _cpAct, _cpFloor, state.Result, Checkpoint(), resets, achievements, allowances, writes, _cpRunFileSeen));
        StepNote($"campaign: the tour's run file as last seen on the map: {_cpRunFileSeen}; now: {RunFileText()} (the game's run end deletes a run's file)");
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: time-scale resets: the walker's {_cpTimeScaleResets}, the loading barrier's {Math.Max(session.TourBarrierResets - _cpBarrierAtStart, 0)} (expected); prompts the walker answered {_cpPromptAnswers}; reward menus the tour drove {_cpRewardFallbacks}; event screens the tour drove {_cpEventScreens}; walker lines {_cpWalkerLines}{(TourCampaignWalker.Dropped > 0 ? $" ({TourCampaignWalker.Dropped} dropped)" : string.Empty)}"));
        TourCampaignWalker.Disarm();
    }

    /// <summary>onepc's close of the lobby by the pad, from the lobby only: anywhere else the restore decides (LeaveCampaign).</summary>
    private static IEnumerable<Wait> StepCampaignClose()
    {
        if (!_cpReady || !LobbyBack())
        {
            Problem($"skipped: not at the lobby (scene {WorldMap.SceneName}, flow {Flow().Host}){(_cpBroken != null ? $": {_cpBroken}" : string.Empty)}");
            return Array.Empty<Wait>();
        }

        return StepOnePcClose();
    }

    // ---- A node, a match ----

    private static IEnumerable<Wait> WalkOne()
    {
        yield return Soft(() => { Watch(); return WorldMap.MapLoaded; }, MapSeconds, "the world map");
        if (!WorldMap.MapLoaded)
        {
            Broken($"not on the world map to walk from (scene {WorldMap.SceneName})");
            yield break;
        }

        _cpWalks++;
        int route = CampaignTour.RouteSeed(_cpArgs.Seed, _cpWalks);
        _cpWalking = true;
        foreach (Wait wait in PumpWalker("WalkWorldMapToMatch", () => CampaignSoakWalker.WalkWorldMapToMatch(TourCampaignWalker.OnFailed, TourCampaignWalker.OnLog, route), WalkSeconds, null))
        {
            yield return wait;
        }

        _cpWalking = false;
        if (Stopping || _cpPumpState == CoroutinePumpState.Stopped)
        {
            yield break;
        }

        if (_cpPumpState != CoroutinePumpState.Done)
        {
            Broken("the walker failed on the map");
            yield break;
        }

        yield return Soft(() => { Watch(); return InMatchScene(); }, 60, "the match scene");
        if (!InMatchScene())
        {
            if (!Stopping)
            {
                Broken($"the walker ended with no match scene (scene {WorldMap.SceneName})");
            }

            yield break;
        }

        Tally.MatchStarted();
        foreach (Wait wait in PlayMatch())
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> PlayMatch()
    {
        OnlineSession session = Sd();
        int number = Tally.Matches + 1;
        string type = Tally.Current?.Type ?? TourCampaignWalker.LastNodeType();
        ReadRun(out int act, out _);
        yield return Soft(() => { Watch(); return InPlay(); }, InPlaySeconds, "the match in play");
        if (!InPlay())
        {
            Broken(string.Create(CultureInfo.InvariantCulture, $"match {number} never came into play (scene {WorldMap.SceneName}, state {StateName()}, time scale {Time.timeScale:0.##})"));
            yield break;
        }

        GameManager? game = HudModel.GameManagerInstance();
        StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: match {number} ({type}) in play; score {(game == null ? "?" : Score(game))}"));
        BrawlNewMatch(); // brawl=on: no fight of this match has begun (LobbyUiCheck.Brawl.cs)
        foreach (Wait wait in BrawlEndNextMatch(number)) // brawlend=game: every team player on a skater after a match left mid-fight (LobbyUiCheck.BrawlEnd.cs)
        {
            yield return wait;
        }

        double playStarted = FrameClock.Seconds;
        if (number == 1 && LateJoiner && !_cpFriendSeated)
        {
            StepNote($"campaign: {CampaignTour.LateJoinerKey}=on: the first match is the host's alone (the friend joins after the Continue)");
        }
        else if (number == 1)
        {
            // D145: the lobby's players are on the run's team from its first match, with no visit to the controller screen.
            double waitFrom = FrameClock.Seconds;
            yield return Soft(() => { Watch(); return FriendSkaterInPlay(); }, CampaignTour.DropWaitSeconds, "the friend's skater in live play");
            bool held = FriendSkaterInPlay();
            StepNote(CampaignTour.FirstMatchLine(held ? FrameClock.Seconds - waitFrom : null, FriendPlayerLine()));
            Check("the friend on the team in the first match", () => held, "the friend's game player on a team, holding a skater in live play");
            playStarted = FrameClock.Seconds;
        }

        if (number != CampaignTour.DropMatch && Brawling)
        {
            // brawl=on: the fresh offer, in the friend's first match outside the drop match (LobbyUiCheck.Brawl.cs).
            foreach (Wait wait in BrawlFresh(number))
            {
                yield return wait;
            }
        }

        if (number == CampaignTour.DropMatch)
        {
            double waitFrom = FrameClock.Seconds;
            yield return Soft(() => { Watch(); return FriendSkaterInPlay(); }, CampaignTour.DropWaitSeconds, "the friend's skater in live play");
            NoteLateMatch(number, FriendSkaterInPlay() ? FrameClock.Seconds - waitFrom : null); // late=on: the late joiner's first match
            if (!FriendSkaterInPlay())
            {
                Fail(string.Create(CultureInfo.InvariantCulture,
                    $"campaign: the second match: the friend's game player held no skater in live play within {CampaignTour.DropWaitSeconds:0} s ({FriendPlayerLine()}): no stand-in to see; no drop made"));
            }
            else
            {
                StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: the second match: the friend's skater in live play {FrameClock.Seconds - waitFrom:0.0} s after the match came into play ({FriendPlayerLine()})"));
                foreach (Wait wait in DropFriend())
                {
                    yield return wait;
                }

                // The forced win a little after the hand-back: the friend plays their skater again before the match ends.
                playStarted = FrameClock.Seconds - CampaignTour.DwellSeconds + CampaignTour.AfterDropSeconds;
            }
        }

        yield return Soft(() => { Watch(); return DwellDone(playStarted) || _brawlLeaveOn; }, CampaignTour.DwellSeconds + 1, "the dwell"); // brawlend=game: no dwell

        // brawl=on: a fight of this match still on is waited out; one past its limit has the state it shares with later
        // matches ended, so no fight runs into the forced end (LobbyUiCheck.Brawl.cs).
        foreach (Wait wait in BrawlBeforeForcedEnd())
        {
            yield return wait;
        }

        game = HudModel.GameManagerInstance();
        if (game == null || game.WasCollected)
        {
            Broken($"match {number}: no game manager to end the match with");
            yield break;
        }

        if (game.isGameEnded)
        {
            StepNote($"campaign: match {number} ended by itself, score {Score(game)}");
        }
        else
        {
            string before = Score(game);
            try
            {
                SoakMatchCheats.ForceHomeWin();
                TourCampaignWalker.Matches++;
            }
            catch (Exception ex)
            {
                Broken($"ForceHomeWin failed ({ModLog.SafeErrorText(ex)})");
                yield break;
            }

            StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: match {number} ended by the game's ForceHomeWin after {FrameClock.Seconds - playStarted:0} s in play (score {before}, then {(game.WasCollected ? "?" : Score(game))})"));
        }

        if (_cpRecordHeld && CampaignTour.IsLastMatch(_cpKind, act, type))
        {
            StepNote("campaign: record=on: this match was the run's last for the tour; the host's relay recorded it (its \"Also recorded to the check tour's folder\" line names the file)");
        }

        // As the game's soak runner does after every match: WatchRunEnd, until the map (or the title at the run's end).
        foreach (Wait wait in PumpWalker("WatchRunEnd", () => CampaignSoakWalker.WatchRunEnd(TourCampaignWalker.OnFailed, TourCampaignWalker.OnLog), RunEndSeconds,
            () => CampaignGame.TitleLoaded() || WorldMap.MapLoaded))
        {
            yield return wait;
        }

        if (Stopping)
        {
            yield break;
        }

        if (_cpPumpState == CoroutinePumpState.Failed)
        {
            Broken("the walker failed after a match");
            yield break;
        }

        yield return Soft(() => { Watch(); return WorldMap.MapLoaded || CampaignGame.TitleLoaded(); }, MapSeconds, "the map or the title");
        if (CampaignGame.TitleLoaded() && !WorldMap.MapLoaded)
        {
            _cpRunEnded = true;
            _cpWon = CampaignTour.IsBoss(type) && act >= CampaignTour.LastAct(CampaignTourKind.Full);
            Tally.MatchWon(FrameClock.Seconds, Checkpoint(), MenusSent());
            NodeLines();
            StepNote($"campaign: the run ended after match {number} ({type} at act {act}): the title is back");
            yield break;
        }

        if (!WorldMap.MapLoaded)
        {
            Broken($"match {number}: no map after it (scene {WorldMap.SceneName})");
            yield break;
        }

        foreach (Wait wait in BrawlEndAfterMatch(number)) // brawlend=game: the guard after a match left mid-fight (LobbyUiCheck.BrawlEnd.cs)
        {
            yield return wait;
        }

        // Each map load after a node saves the run, and run sync captures it from the tour's run file.
        uint before2 = _cpMapCheckpoint;
        yield return Soft(() => { Watch(); return Checkpoint() > before2; }, 10, "a higher run checkpoint");
        uint now = Checkpoint();
        Check(string.Create(CultureInfo.InvariantCulture, $"the run checkpoint after match {number}"), () => CampaignTour.CheckpointRose(before2, now), string.Create(CultureInfo.InvariantCulture, $"above {before2}"));
        _cpMapCheckpoint = now;
        _cpRunFileSeen = RunFileText();
        ReadRun(out _cpAct, out _cpFloor);
        if (_cpFriendSeated || !LateJoiner)
        {
            yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 10, "the friend Here");
            Check("the friend's seat", () => FriendSeat().Stage == SeatStage.Here && session.TourStandIns == 0, "Here, their skater theirs");
        }

        Tally.MatchWon(FrameClock.Seconds, now, MenusSent());
        NodeLines();
    }

    /// <summary>The second match: the friend's link cut for the session's 15 s drop; the AI on their skater; the friend back.</summary>
    private static IEnumerable<Wait> DropFriend()
    {
        OnlineSession session = Sd();
        if (!ModSettings.AiStandIn)
        {
            Fail("the AiStandIn setting is off: no stand-in to see in the drop");
            yield break;
        }

        double started = FrameClock.Seconds;
        session.SimulateDrop();
        StepNote("campaign: the second match: a simulated drop of the friend (their link cut, their seat kept)");
        bool brawl = BrawlHoldBegin(session); // brawl=on: LB+RB held from the cut through the hand-back (LobbyUiCheck.Brawl.cs)
        bool standInSeen = false, handedBack = false;
        try
        {
            yield return Soft(() => { Watch(); return session.TourStandIns > 0; }, 10, "the AI on the friend's skater");
            double? standIn = session.TourStandIns > 0 ? FrameClock.Seconds - started : null;
            Check("the AI stand-in in the drop", () => session.TourStandIns > 0, "the AI plays the friend's skater");
            yield return Soft(() =>
            {
                Watch();
                if (brawl)
                {
                    BrawlKeepOffered(session, ref standInSeen, ref handedBack);
                }

                return FriendSeat().Stage == SeatStage.Here && session.TourStandIns == 0 && FrameClock.Seconds - started > 16;
            }, 50, "the friend back");
            StepNote(CampaignTour.DropLine(standIn, FrameClock.Seconds - started, SeatLine(FriendSeat())));
            Check("the friend back from the drop", () => FriendSeat().Stage == SeatStage.Here && session.TourStandIns == 0, "Here again, their skater theirs");
            if (brawl)
            {
                foreach (Wait wait in BrawlAfterHandBack(session))
                {
                    yield return wait;
                }
            }
        }
        finally
        {
            if (brawl)
            {
                session.TourFriendPress(PadButtons.None);
            }
        }
    }

    /// <summary>The one-PC friend's game player in this match, or null.</summary>
    private static Il2CppTape2Tape.Player? FriendPlayer()
    {
        int id = Sd().TourFriendGamePlayer;
        Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>? players = PlayersManager.Instance?._players;
        for (int i = 0; id >= 0 && players != null && i < players.Count; i++)
        {
            Il2CppTape2Tape.Player? player = players[i];
            if (player != null && !player.WasCollected && player._PlayerId_k__BackingField == id)
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>
    /// The friend's game player holds a skater in live play: the match's clock running, the skater not in one of the game's own
    /// sequences (the anthem skate-in, a talent's: then the game owns its flags and the stand-in waits).
    /// </summary>
    private static bool FriendSkaterInPlay()
    {
        if (!InPlay() || FriendPlayer()?.controlledSkater is not { } skater || skater.WasCollected)
        {
            return false;
        }

        return skater.TryCast<Il2CppSkaters.Forward>() is not { overrideAI: true }
            && skater.gameObject.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<AnthemMovementSkaters>()) == null;
    }

    /// <summary>The friend's game player for the step's line: its number, team and skater (held or not; never a name).</summary>
    private static string FriendPlayerLine()
    {
        try
        {
            Il2CppTape2Tape.Player? player = FriendPlayer();
            return player == null
                ? $"game player {Sd().TourFriendGamePlayer}: not in this match's players"
                : $"game player {player._PlayerId_k__BackingField}: {(player.currentTeam == null ? "on no team" : "on a team")}, {(player.controlledSkater == null ? "no skater" : "a skater")}";
        }
        catch (Exception ex)
        {
            return $"unread ({ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>
    /// Before the nodes with the drop, a read of the game's own controller screen (D145): opened by the map menu's Controllers with
    /// the pad and pressed nowhere, it must show the host and the friend already on the run's team, as the campaign's start
    /// remembered them. The screen places each player on the map by the side the game remembers (in a campaign, the right
    /// side is the one team, the middle doesn't play), so this is the game reading what the start wrote. It is left with
    /// nothing applied (its own SetResult(false) and FinishExitMenu), and the map menu closed by B as a player closes it.
    /// </summary>
    private static IEnumerable<Wait> ScreenShowsTeam()
    {
        OnlineSession session = Sd();
        if (session.TourFriendGamePlayer < 0 || _walk == null || _walk.GamePlayer < 0)
        {
            Fail("campaign: the controller screen's read: no friend's game player or no tour pad");
            yield break;
        }

        int host = _walk.GamePlayer;
        int friend = session.TourFriendGamePlayer;

        // A map menu that didn't come fails this step, not the run: the nodes are walked all the same.
        string? brokenBefore = _cpBroken;
        foreach (Wait wait in MapMenu("Controllers", menu => menu.m_ControllerAssignmentButton))
        {
            yield return wait;
        }

        if (!ReferenceEquals(_cpBroken, brokenBefore))
        {
            _cpBroken = brokenBefore;
            yield break;
        }

        ControllerAssignmentMenu? screen = null;
        yield return Soft(() => { Watch(); return (screen = Top()?.TryCast<ControllerAssignmentMenu>()) != null; }, 6, "the game's controller screen");
        if (screen == null || screen.WasCollected)
        {
            Fail("campaign: the map menu's Controllers opened no controller screen: nothing to read");
            yield break;
        }

        yield return Seconds(1.2); // its intro, and the remembered sides the icons take a few frames in
        ControllerAssignmentMenu open = screen;
        bool ScreenUp() => !open.WasCollected && Top()?.Pointer == open.Pointer;
        bool campaignLayout = open.Mode == ControllerAssignmentMenu.EMode.CampaignSingleSide;
        bool hostSeen = SeatOf(open, host, out int hostSide, out _);
        bool friendSeen = SeatOf(open, friend, out int friendSide, out _);
        string seats = $"{SeatText(open, host, "the host")}; {SeatText(open, friend, "the friend")}; the game remembers the host {RememberedText(host, out _)}, the friend {RememberedText(friend, out _)}";
        foreach (Wait wait in Snap("controllers_team", 0))
        {
            yield return wait;
        }

        if (campaignLayout && CampaignTour.ScreenShowsTeam(hostSeen, hostSide, friendSeen, friendSide))
        {
            StepNote($"campaign: the controller screen from the map shows the host and the friend on the team (right) with no press: {seats}");
        }
        else
        {
            Fail($"campaign: the controller screen from the map ({(campaignLayout ? "its campaign layout" : $"mode {open.Mode}")}) doesn't show both on the team: {seats}: {ControllerScreenLine()}");
        }

        if (ScreenUp())
        {
            // Left without applying (the game's own back-out's two steps): nothing it shows is saved.
            open.SetResult(false);
            open.FinishExitMenu();
            yield return Soft(() => !ScreenUp(), 3, "the controller screen gone");
        }

        // The map's menu under it, closed by B as a player closes it.
        yield return Soft(() => { Watch(); return Top()?.TryCast<WorldMapMenu>() != null || Top() == null; }, 3, "the map's menu or the map");
        if (Top()?.TryCast<WorldMapMenu>() is { } menu && MayPress("B on the map's menu", () => !menu.WasCollected && Top()?.Pointer == menu.Pointer))
        {
            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => { Watch(); return menu.WasCollected || Top()?.Pointer != menu.Pointer; }, 3, "the map's menu closed");
        }
    }

    /// <summary>What the controller screen shows for <paramref name="gamePlayer"/>: its side (the game's numbers) and ready flag. False when it isn't a player there.</summary>
    private static bool SeatOf(ControllerAssignmentMenu menu, int gamePlayer, out int side, out bool ready)
    {
        side = CampaignTour.SideCenter;
        ready = false;
        try
        {
            Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? list = menu.WasCollected ? null : menu.m_Players;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                if (list[i] is { } p && !p.WasCollected && p.IsPlayerControlled && p.GamePlayerId == gamePlayer)
                {
                    side = (int)p.CurrentSide;
                    ready = p.IsLockedIn;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Not read: not a player there, as far as the tour can press.
        }

        return false;
    }

    /// <summary>A player's place on the controller screen for a line: <c>the host (game player 1) on the team (right), ready</c>.</summary>
    private static string SeatText(ControllerAssignmentMenu menu, int gamePlayer, string who) =>
        SeatOf(menu, gamePlayer, out int side, out bool ready)
            ? $"{who} (game player {gamePlayer}) on {CampaignTour.SideText(side)}{(ready ? ", ready" : string.Empty)}"
            : $"{who} (game player {gamePlayer}) not on the screen";

    /// <summary>
    /// The side the game remembers for <paramref name="gamePlayer"/> (what the controller screen last saved; a campaign's start
    /// seats by it), in the step's words. <paramref name="offTeam"/> is true only when it was read and isn't the team: a read
    /// that failed says so and decides nothing (the next match's players line is the proof either way). Never throws.
    /// </summary>
    private static string RememberedText(int gamePlayer, out bool offTeam)
    {
        offTeam = false;
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, ControllerAssignmentPlayer.ESide>? sides = Il2CppTape2Tape.ControllerAssignmentState.s_ControllerSide;
            if (sides != null && sides.TryGetValue(gamePlayer, out ControllerAssignmentPlayer.ESide remembered))
            {
                offTeam = (int)remembered != CampaignTour.SideRight;
                return $"on {CampaignTour.SideText((int)remembered)}";
            }

            offTeam = true;
            return "on no side";
        }
        catch (Exception ex)
        {
            return $"unread ({ModLog.SafeErrorText(ex)})";
        }
    }

    private static bool DwellDone(double playStarted)
    {
        GameManager? game = HudModel.GameManagerInstance();
        if (game == null || game.WasCollected || game.isGameEnded)
        {
            return true;
        }

        bool over = CampaignTour.DwellOver(FrameClock.Seconds - playStarted, TeamGoals(game._homeTeam), TeamGoals(game._visitorTeam));
        if (over && FrameClock.Seconds - playStarted < CampaignTour.DwellSeconds)
        {
            StepNote($"campaign: the dwell ends early: the visitors lead {Score(game)} (the tour's skaters stand idle)");
        }

        return over;
    }

    // ---- The map's menu and the concede, by the pad ----

    /// <summary>The map's menu by the pad's Start, the cursor down (or up) to <paramref name="row"/>, A. A game call stands in for a press that did nothing, and says so.</summary>
    private static IEnumerable<Wait> MapMenu(string row, Func<WorldMapMenu, AbstractButton?> pick)
    {
        _cpMenuRowPressed = false;
        yield return Soft(() => { Watch(); return WorldMap.MapLoaded && !TourHolds.ModPromptOpen && (Top() == null || Top()!.TryCast<WorldMapMenu>() != null); }, 30, "the map, nothing over it");

        // The map's own menu input, as a player's Start meets it. Start on the map is the bar's Menu entry and nothing else (the
        // game's MenusShow is in no scene of this build). The entry has no input handler of its own, so the UI module takes its
        // press only with nothing on its input stack, and the loading transition stays there until its hide has completed: a
        // press while the map is already seen under it is dropped, in the game as it ships. The entry's own answer is asked
        // (UIInputShortcutBase.CanProcessInputs), not a time: a real condition, not a sleep.
        double since = FrameClock.Seconds;
        yield return Soft(() => { Watch(); return MapStartHeldNow() == null; }, MapInputSeconds, "the map's own input ready for its menu");
        string? held = MapStartHeldNow();
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: the map's menu input {(held == null ? "ready" : $"still not ready ({held})")} after {FrameClock.Seconds - since:0.0} s ({MapInputLine()})"));
        WorldMapMenu? menu = Top()?.TryCast<WorldMapMenu>();
        if (menu == null && MayPress("Start on the map", () => WorldMap.MapLoaded && Top() == null))
        {
            foreach (Wait wait in Tap(PadButtons.Start))
            {
                yield return wait;
            }

            yield return Soft(() => (menu = Top()?.TryCast<WorldMapMenu>()) != null, 3, "the map's menu");
        }

        if (menu == null && WorldMap.MapLoaded && Top() == null && WorldMapMenusHandler.s_Instance is { } handler && !handler.WasCollected)
        {
            Fail($"Start by the pad opened no map menu ({MapInputLine()}); the map bar's own handler, WorldMapMenusHandler.OnOpenWorldMapMenu, stands in");
            handler.OnOpenWorldMapMenu(null!);
            yield return Soft(() => (menu = Top()?.TryCast<WorldMapMenu>()) != null, 4, "the map's menu");
        }

        if (menu == null || menu.WasCollected)
        {
            Broken($"no map menu to reach its {row} row");
            yield break;
        }

        yield return Seconds(0.8); // its intro: a press in it is ignored meanwhile
        AbstractButton? target = pick(menu);
        if (target == null)
        {
            Broken($"the map's menu has no {row} row");
            yield break;
        }

        WorldMapMenu open = menu;
        bool MenuUp() => !open.WasCollected && Top()?.Pointer == open.Pointer;
        foreach (Wait wait in CursorTo(target, row, PadButtons.DPadDown, PadButtons.DPadUp, MenuUp))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap($"map_menu_{row.ToLowerInvariant()}", 0))
        {
            yield return wait;
        }

        if (!MenuUp())
        {
            Broken($"the map's menu went before its {row} row was pressed");
        }
        else if (CursorPointer() == target.Pointer.ToInt64())
        {
            foreach (Wait wait in Tap(PadButtons.A))
            {
                yield return wait;
            }

            _cpMenuRowPressed = true;
        }
        else
        {
            Fail($"the pad's cursor never reached the map menu's {row} row; the row is clicked as the game's own click does");
            target.ForceClick();
            _cpMenuRowPressed = true;
        }
    }

    /// <summary>True when the map's own handler opens its menu at a call: the map loaded, its handler not ignoring inputs (its arrival over, nothing open over it).</summary>
    private static bool MapHandlerReady()
    {
        try
        {
            WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
            return handler != null && !handler.WasCollected && WorldMap.MapLoaded && !handler.IgnoreInputs;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The map bar's Menu entry (the game's own "[Start] Menu" shortcut), or null.</summary>
    private static UIInputShortcutBase? MapMenuEntry()
    {
        WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
        UIInputShortcutBase? entry = handler == null || handler.WasCollected ? null : handler.m_WorldMapMenuInput;
        return entry == null || entry.WasCollected ? null : entry;
    }

    /// <summary>True when the entry answers the tour pad's player: the game registers it only for the players driving the menus.</summary>
    private static bool EntryAnswersPad(UIInputShortcutBase entry)
    {
        Il2CppSystem.Collections.Generic.List<Il2CppRewired.Player>? players = entry.m_RegisteredPlayers;
        for (int i = 0; _walk != null && players != null && i < players.Count; i++)
        {
            if (players[i] is { } player && player.id == _walk.RewiredId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What holds the pad's Start on the map, or null when a press opens the map's menu (Core's <see cref="CampaignTour.MapStartHeld"/>,
    /// from the game's own answers: the entry's <c>CanProcessInputs</c> is the gate its press asks). Read only; never throws.
    /// </summary>
    private static string? MapStartHeldNow()
    {
        try
        {
            if (!WorldMap.MapLoaded)
            {
                return "the map isn't loaded";
            }

            UIInputShortcutBase? entry = MapMenuEntry();
            bool shown = entry != null && entry.isActiveAndEnabled;
            return CampaignTour.MapStartHeld(shown, !MapHandlerReady(), MenuGame.Module()?.NavigationController is { IsActive: true },
                shown && entry!.CanProcessInputs(), shown && EntryAnswersPad(entry!));
        }
        catch (Exception ex)
        {
            return $"unread ({ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>What the map's own menu input waits on, for the step's line (the game's flags, read; never written).</summary>
    private static string MapInputLine()
    {
        try
        {
            WorldMapMenusHandler? h = WorldMapMenusHandler.s_Instance;
            if (h == null || h.WasCollected)
            {
                return "no map handler";
            }

            UIInputShortcutBase? entry = MapMenuEntry();
            bool shown = entry != null && entry.isActiveAndEnabled;
            Il2CppSTS.Map.MapPlayerTracker? tracker = Il2CppSTS.Map.MapPlayerTracker.Instance;
            return $"the map bar's Menu entry: shown {shown}, takes input {(shown ? entry!.CanProcessInputs().ToString() : "?")}, answers the pad {(shown ? EntryAnswersPad(entry!).ToString() : "?")}; " +
                $"the map's handler: loaded {h.m_IsWorldMapLoaded}, visible {h.m_IsWorldMapVisible}, sub-menus {h.m_InstantiatingWorldMapSubmenus?.Count ?? -1}/{h.m_WorldMapSubMenus?.Count ?? -1}, " +
                $"opening {h.m_OpeningSubMenu}, closed recently {h.m_MenuClosedRecently}, tracker locked {(tracker == null ? "?" : tracker.isLocked.ToString())}; " +
                $"the navigation active {MenuGame.Module()?.NavigationController?.IsActive.ToString() ?? "?"}; the UI's input stack: {InputStackText()}";
        }
        catch (Exception ex)
        {
            return $"unread ({ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>The UI module's input handlers by class, top last (<c>empty</c>, or <c>1 (WooshLoadingTransition)</c>): what a handler-less shortcut waits out.</summary>
    private static string InputStackText()
    {
        Il2CppSystem.Collections.Generic.List<IUIInputHandler>? stack = MenuGame.Module()?.m_InputHandlersStack;
        if (stack == null)
        {
            return "?";
        }

        if (stack.Count == 0)
        {
            return "empty";
        }

        var names = new List<string>();
        for (int i = 0; i < stack.Count && i < 6; i++)
        {
            IUIInputHandler? handler = stack[i];
            names.Add(handler == null || handler.Pointer == IntPtr.Zero ? "gone" : SafeText.Clean(MenuGame.TypeName(new Il2CppSystem.Object(handler.Pointer)), 48));
        }

        return $"{stack.Count} ({string.Join(", ", names)})";
    }

    /// <summary>The cursor onto <paramref name="target"/> by the pad: <paramref name="first"/> while it moves, then <paramref name="second"/>; only while <paramref name="on"/> (its screen is the top one).</summary>
    private static IEnumerable<Wait> CursorTo(AbstractNavigable target, string what, PadButtons first, PadButtons second, Func<bool> on)
    {
        long wanted = target.Pointer.ToInt64();
        foreach (PadButtons button in new[] { first, second })
        {
            for (int i = 0; i < 10 && CursorPointer() != wanted && on(); i++)
            {
                long at = CursorPointer();
                foreach (Wait wait in Tap(button))
                {
                    yield return wait;
                }

                yield return Soft(() => CursorPointer() != at, 0.8, "the cursor to move");
                yield return Seconds(WalkSettleSeconds);
                if (CursorPointer() == at)
                {
                    break; // an end: the other way
                }
            }
        }

        StepNote($"campaign: the cursor {(CursorPointer() == wanted ? "on" : "not on")} {what} ({Cursor()})");
    }

    /// <summary>The map's menu's Concede, the game's prompt's Yes by the pad, then the walker's WatchRunEnd until the title.</summary>
    private static IEnumerable<Wait> Concede()
    {
        ReadRun(out _cpAct, out _cpFloor);
        _cpRunFileSeen = RunFileText();
        foreach (Wait wait in MapMenu("Concede", menu => menu.m_ConcedeButton))
        {
            yield return wait;
        }

        // Only the prompt Concede itself raises is answered: with no press of that row (the map's menu never came, or went), a
        // yes/no on top now is someone else's (MapMenu has said why).
        if (!_cpMenuRowPressed || (_cpBroken != null && !WorldMap.MapLoaded))
        {
            yield break;
        }

        CustomizableYesNoPrompt? prompt = null;
        yield return Soft(() => (prompt = Top()?.TryCast<CustomizableYesNoPrompt>()) != null, 5, "the game's concede prompt");
        if (prompt == null || prompt.WasCollected)
        {
            Problem("Concede opened no prompt");
            yield break;
        }

        yield return Seconds(0.9);
        StepNote($"campaign: the game's prompt \"{SafeText.Clean(prompt.m_TitleText?.text, 48)}\": the cursor on {Cursor()}");
        foreach (Wait wait in Snap("concede_prompt", 0))
        {
            yield return wait;
        }

        AbstractNavigable? yes = prompt.GetTargetNavigable(PromptTemplateMenu.PromptTemplateResult.Yes);
        if (yes == null)
        {
            Problem("the concede prompt has no Yes");
            yield break;
        }

        CustomizableYesNoPrompt asked = prompt;
        bool PromptUp() => !asked.WasCollected && Top()?.Pointer == asked.Pointer;
        foreach (Wait wait in CursorTo(yes, "the prompt's Yes", PadButtons.DPadDown, PadButtons.DPadRight, PromptUp))
        {
            yield return wait;
        }

        if (CursorPointer() != yes.Pointer.ToInt64())
        {
            foreach (Wait wait in CursorTo(yes, "the prompt's Yes", PadButtons.DPadUp, PadButtons.DPadLeft, PromptUp))
            {
                yield return wait;
            }
        }

        if (!PromptUp())
        {
            Problem("the concede prompt went before its Yes was pressed");
            yield break;
        }

        if (CursorPointer() == yes.Pointer.ToInt64())
        {
            foreach (Wait wait in Tap(PadButtons.A))
            {
                yield return wait;
            }
        }
        else
        {
            Fail("the pad's cursor never reached the prompt's Yes; it is clicked as the game's own click does");
            yes.ForceClick();
        }

        // The concede shows the run's summary on the map (RunEndMenu), then the new-unlocks screen if any, then the title: the
        // walker's WatchRunEnd returns at once on the map (its first check is the world map scene), so the host's way is walked here.
        foreach (Wait wait in RunEndScreens())
        {
            yield return wait;
        }

        _cpRunEnded = CampaignGame.TitleLoaded();
        StepNote($"campaign: conceded at {_cpAct}-{_cpFloor}; {(_cpRunEnded ? "the title is back" : $"still in {WorldMap.SceneName}")}");
    }

    /// <summary>
    /// The run's end as a host walks it: the run's summary (<c>RunEndMenu</c>), then the new-unlocks screen
    /// (<c>PermanentUnlockMenu</c>) if it comes, each left by the pad's press of its own on-screen shortcut (the button whose action
    /// the shortcut registered, held as long as it asks), until the title. Two presses that change nothing: the game's own soak
    /// runner's <c>DismissThroughShortcut</c> stands in, after a FAIL line. Presses only on those screens.
    /// </summary>
    private static IEnumerable<Wait> RunEndScreens()
    {
        double started = FrameClock.Seconds;
        yield return Soft(() => { Watch(); return RunEndScreen(out _, out _, out _) != null || CampaignGame.TitleLoaded(); }, RunEndAppearSeconds, "the run's summary or the title");
        IntPtr shown = IntPtr.Zero;
        int noEffect = 0;
        while (!Stopping && FrameClock.Seconds - started < RunEndWalkSeconds && !LobbyBack())
        {
            AbstractMenu? screen = RunEndScreen(out UIInputShortcutBase? shortcut, out string name, out string field);
            if (screen == null)
            {
                if (CampaignGame.TitleLoaded() && LobbyShell.IsOpen)
                {
                    break; // the lobby on its way: the step's own wait takes it
                }

                yield return Soft(() => { Watch(); return RunEndScreen(out _, out _, out _) != null || LobbyBack(); }, 6, "the next run-end screen or the lobby");
                if (RunEndScreen(out _, out _, out _) == null && !CampaignGame.TitleLoaded())
                {
                    break;
                }

                continue;
            }

            if (screen.Pointer != shown)
            {
                shown = screen.Pointer;
                noEffect = 0;
                yield return Seconds(1.2); // its intro: a press in it is ignored meanwhile
                StepNote($"campaign: {name} is up");
                foreach (Wait wait in Snap(field == "m_ExitButton" ? "run_end_summary" : "run_end_unlocks", 0))
                {
                    yield return wait;
                }

                continue;
            }

            int action = shortcut != null && !shortcut.WasCollected ? shortcut.m_RegisteredActionId : -1;
            PadButtons? button = TourShortcuts.ButtonFor(action);
            if (button == null || noEffect >= RunEndNoEffectPresses)
            {
                Fail(button == null
                    ? $"campaign: {name}: its {field} shortcut's action ({action}) has no pad button; the game's own soak runner's DismissThroughShortcut stands in"
                    : $"campaign: {name}: {RunEndNoEffectPresses} presses of its {field} changed nothing; the game's own soak runner's DismissThroughShortcut stands in");
                AbstractMenu standIn = screen;
                foreach (Wait wait in PumpWalker("DismissThroughShortcut", () => CampaignSoakWalker.DismissThroughShortcut(standIn, field, 10f), 15, () => standIn.WasCollected || Top()?.Pointer != standIn.Pointer))
                {
                    yield return wait;
                }

                shown = IntPtr.Zero;
                noEffect = 0;
                if (!standIn.WasCollected && Top()?.Pointer == standIn.Pointer)
                {
                    Problem($"campaign: {name} stayed up after its own dismissal too");
                    yield break;
                }

                continue;
            }

            AbstractMenu pressed = screen;
            if (!MayPress($"{button} on {name}", () => !pressed.WasCollected && Top()?.Pointer == pressed.Pointer))
            {
                yield break;
            }

            int index = UnlockIndex(pressed);
            double hold = TourShortcuts.HoldSeconds(shortcut!.m_RequiredHoldTime);
            _walk!.Hold(button.Value);
            yield return Seconds(hold);
            _walk.Let(button.Value);
            yield return Soft(() => { Watch(); return pressed.WasCollected || Top()?.Pointer != pressed.Pointer || UnlockIndex(pressed) != index; }, 3, $"{name} to answer");
            bool gone = pressed.WasCollected || Top()?.Pointer != pressed.Pointer;
            bool stepped = !gone && UnlockIndex(pressed) != index;
            noEffect = gone || stepped ? 0 : noEffect + 1;
            StepNote(string.Create(CultureInfo.InvariantCulture,
                $"campaign: {name}: {button} by the pad (its {field}, action {action}, held {hold:0.0} s): {(gone ? "closed" : stepped ? "the next unlock" : "still up")}"));
            if (gone)
            {
                shown = IntPtr.Zero;
            }
        }
    }

    /// <summary>The run's end screen on top (the run's summary or the new-unlocks screen) with its shortcut out, or null.</summary>
    private static AbstractMenu? RunEndScreen(out UIInputShortcutBase? shortcut, out string name, out string field)
    {
        AbstractMenu? top = Top();
        if (top?.TryCast<RunEndMenu>() is { } summary)
        {
            shortcut = summary.m_ExitButton;
            name = "the run's summary (RunEndMenu)";
            field = "m_ExitButton";
            return summary;
        }

        if (top?.TryCast<PermanentUnlockMenu>() is { } unlocks)
        {
            shortcut = unlocks.m_NextButton;
            name = "the new-unlocks screen (PermanentUnlockMenu)";
            field = "m_NextButton";
            return unlocks;
        }

        shortcut = null;
        name = field = string.Empty;
        return null;
    }

    /// <summary>The new-unlocks screen's unlock shown (its index), or -1 for any other screen.</summary>
    private static int UnlockIndex(AbstractMenu menu) =>
        !menu.WasCollected && menu.TryCast<PermanentUnlockMenu>() is { } unlocks ? unlocks.m_CurrentUnlockIndex : -1;

    // ---- The walker's pump ----

    /// <summary>
    /// A walker coroutine pumped by the tour's frames (the tour's coroutine pump, LobbyUiCheck.Tour.cs) while no prompt of the mod's is up: one the walker would
    /// answer yes. A mod prompt pauses it; one up for <see cref="ModPromptSeconds"/> fails the step with its title.
    /// <paramref name="doneWhen"/> ends it early as done (a state the coroutine has nothing more to do in). Its end goes in
    /// <see cref="_cpPumpState"/>; a failure or timeout is the step's problem.
    /// </summary>
    private static IEnumerable<Wait> PumpWalker(string what, Func<Il2CppSystem.Collections.IEnumerator> start, double timeoutSeconds, Func<bool>? doneWhen)
    {
        _cpPumpState = CoroutinePumpState.Failed;
        Il2CppSystem.Collections.IEnumerator? routine;
        try
        {
            routine = start();
        }
        catch (Exception ex)
        {
            Problem($"campaign: the walker's {what} couldn't start ({ModLog.SafeErrorText(ex)})");
            yield break;
        }

        if (routine == null)
        {
            Problem($"campaign: the walker's {what} gave no coroutine");
            yield break;
        }

        var pump = new CoroutinePump(new TourCoroutine(routine, what), () => Stopping);
        TourCampaignWalker.CurrentPump = pump;
        double promptSince = -1;
        string ended = string.Empty;
        bool countResets = what == "WalkWorldMapToMatch";
        bool takeovers = countResets; // the map walk: an event's takeover pauses it (LobbyUiCheck.Takeover.cs)
        EventChoiceMenu? takenOver = null;

        bool Step()
        {
            Watch();
            if (doneWhen != null && doneWhen())
            {
                ended = "the game is where it leads";
                return true;
            }

            // Before the walker's next step: under a takeover it would find no card and give up.
            if (takeovers && (takenOver = TakenOverEvent()) != null)
            {
                ended = "takeover";
                return true;
            }

            if (TourHolds.ModPromptOpen)
            {
                if (promptSince < 0)
                {
                    promptSince = FrameClock.Seconds;
                    StepNote($"campaign: the walker's {what} paused: a prompt of the mod's is up (\"{ModPromptTitle()}\"), which the walker would answer yes");
                }

                if (FrameClock.Seconds - promptSince > ModPromptSeconds)
                {
                    ended = "prompt";
                    return true;
                }

                return false;
            }

            promptSince = -1;
            WatchPromptBefore();
            float scaleBefore = countResets ? Time.timeScale : 1f;
            CoroutinePumpState state = pump.Tick(Time.timeAsDouble, Time.realtimeSinceStartupAsDouble);
            if (countResets && Math.Abs(scaleBefore - 1f) > 0.001f && Math.Abs(Time.timeScale - 1f) < 0.001f)
            {
                _cpTimeScaleResets++; // the walker's AssertTimeScale (only its map walk calls it): expected
            }

            WatchPromptAfter();
            return state != CoroutinePumpState.Running;
        }

        // The walker's time is the time it is stepped: a takeover's drive, with the walker paused, isn't counted in it.
        double budget = timeoutSeconds;
        while (true)
        {
            double stretchFrom = FrameClock.Seconds;
            yield return Soft(Step, budget, what);
            budget -= FrameClock.Seconds - stretchFrom;
            if (ended != "takeover" || takenOver == null || Stopping)
            {
                break;
            }

            ended = string.Empty;
            promptSince = -1;
            foreach (Wait wait in DriveTakeover(takenOver))
            {
                yield return wait;
            }

            if (!_cpTakeoverGoesOn || Stopping)
            {
                ended = "takeover";
                break;
            }
        }

        TourCampaignWalker.CurrentPump = null;
        DrainWalker();
        string how = string.Create(CultureInfo.InvariantCulture, $"{pump.Moves} stretches in {pump.Ticks} frames, {pump.Deepest} deep{(pump.Unknowns.Count > 0 ? $"; unknown yields taken as a frame: {string.Join(", ", pump.Unknowns)}" : string.Empty)}");
        if (ended == "takeover")
        {
            _cpPumpState = Stopping ? CoroutinePumpState.Stopped : CoroutinePumpState.Failed;
            if (!Stopping)
            {
                Problem($"campaign: the walker's {what} stopped at an event whose own screen took it over: the pad couldn't leave that screen ({_cpEventPresses} press(es)); {how}");
            }

            yield break;
        }

        if (ended == "prompt")
        {
            _cpPumpState = CoroutinePumpState.Failed;
            Problem($"campaign: a mod prompt \"{ModPromptTitle()}\" stayed open {ModPromptSeconds:0} s while the walker's {what} was paused; the walker never answers ours ({how})");
            yield break;
        }

        _cpPumpState = ended.Length > 0 && pump.State == CoroutinePumpState.Running ? CoroutinePumpState.Done : pump.State;
        switch (_cpPumpState)
        {
            case CoroutinePumpState.Done:
                StepNote($"campaign: the walker's {what}: done{(ended.Length > 0 ? $" ({ended})" : string.Empty)} after {how}");
                break;
            case CoroutinePumpState.Stopped:
                StepNote($"campaign: the walker's {what}: stopped (the tour is restoring) after {how}");
                break;
            case CoroutinePumpState.Failed:
                Problem($"campaign: the walker's {what} failed: {pump.Failure}; {how}");
                break;
            default:
                _cpPumpState = CoroutinePumpState.Failed;
                Problem(string.Create(CultureInfo.InvariantCulture, $"campaign: the walker's {what} still ran after {timeoutSeconds:0} s, in {pump.Top}; {how}"));
                break;
        }
    }

    // ---- What the tour watches while it waits (at most ten times a second, but the pad's release and a reward's pump) ----

    private static void Watch()
    {
        try
        {
            ReleaseTapIfDue();
            TickRewardFallback();
            if (FrameClock.Seconds < _cpNextWatch)
            {
                return;
            }

            _cpNextWatch = FrameClock.Seconds + WatchSeconds;
            DrainWalker();
            if (TourCampaignWalker.RewardFailure is { } rewardFailure && !ReferenceEquals(rewardFailure, _cpRewardFailureSaid))
            {
                _cpRewardFailureSaid = rewardFailure;
                Fail($"campaign: the walker's reward coroutine failed: {rewardFailure}");
            }

            AbstractMenu? top = Top();
            WatchRewards(top);
            WatchStatsScreen(top);
            WatchCanada(top);
            WatchRecording();
        }
        catch (Exception ex)
        {
            if (_cpWatchFailures++ == 0)
            {
                StepNote($"campaign: a watch failed ({ModLog.SafeErrorText(ex)}); the tour goes on (said once)");
            }
        }
    }

    private static void DrainWalker()
    {
        List<string> lines = TourCampaignWalker.TakeLines();
        foreach (string line in lines)
        {
            if (_cpWalkerLines++ < MaxWalkerLinesLogged)
            {
                StepNote($"campaign: walker: {SafeText.Clean(line, 200)}");
            }

            if (WalkerLine.TryRead(line, out WalkerLine read))
            {
                ReadRun(out int act, out int floor);
                Tally.Read(read, act, floor, FrameClock.Seconds, Checkpoint(), MenusSent());
            }
        }

        if (TourCampaignWalker.Dropped > _cpDroppedReported)
        {
            StepNote($"campaign: {TourCampaignWalker.Dropped - _cpDroppedReported} walker line(s) came faster than the tour read them; dropped");
            _cpDroppedReported = TourCampaignWalker.Dropped;
        }

        NodeLines();
    }

    private static void NodeLines()
    {
        foreach (string line in Tally.TakeLines())
        {
            StepNote(line);
        }
    }

    private static void WatchRewards(AbstractMenu? top)
    {
        SkaterPowerupRewardMenu? skater = top?.TryCast<SkaterPowerupRewardMenu>();
        RelicRewardMenu? relic = skater == null ? top?.TryCast<RelicRewardMenu>() : null;
        if (skater == null && relic == null)
        {
            _cpRewardMenu = IntPtr.Zero;
            return;
        }

        IntPtr menu = top!.Pointer;
        if (RewardMenusSeen.Add(menu))
        {
            Tally.RewardSeen();
            StepNote($"campaign: a reward menu ({(skater != null ? "SkaterPowerupRewardMenu" : "RelicRewardMenu")}){(TourCampaignWalker.Armed ? "; the walker's reward coroutine takes it" : "; the tour drives it")}");
        }

        if (_cpRewardMenu != menu)
        {
            _cpRewardMenu = menu;
            _cpRewardSince = FrameClock.Seconds;
        }

        double waited = TourCampaignWalker.Armed ? RewardFallbackSeconds : 1.5;
        if (_cpRewardPump != null || FrameClock.Seconds - _cpRewardSince < waited || TourCampaignWalker.RewardInFlight() || TourHolds.ModPromptOpen)
        {
            return;
        }

        Il2CppSystem.Collections.IEnumerator? routine = skater != null ? CampaignSoakWalker.DriveSkaterPowerupReward(skater) : CampaignSoakWalker.DriveRelicReward(relic!);
        if (routine == null)
        {
            return;
        }

        _cpRewardFallbacks++;
        _cpRewardPump = new CoroutinePump(new TourCoroutine(routine, skater != null ? "DriveSkaterPowerupReward" : "DriveRelicReward"), () => Stopping);
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: the reward menu stayed {FrameClock.Seconds - _cpRewardSince:0} s with no reward coroutine on it{(TourCampaignWalker.Armed ? " (the armed one didn't take it)" : string.Empty)}: the tour drives it with the walker's own {(skater != null ? "DriveSkaterPowerupReward" : "DriveRelicReward")}"));
    }

    private static void TickRewardFallback()
    {
        if (_cpRewardPump == null || TourHolds.ModPromptOpen)
        {
            return;
        }

        CoroutinePumpState state = _cpRewardPump.Tick(Time.timeAsDouble, Time.realtimeSinceStartupAsDouble);
        if (state == CoroutinePumpState.Running)
        {
            return;
        }

        if (state == CoroutinePumpState.Failed)
        {
            Fail($"campaign: the tour's drive of a reward menu failed: {_cpRewardPump.Failure}");
        }
        else
        {
            StepNote($"campaign: the tour's drive of a reward menu: {state} after {_cpRewardPump.Moves} stretches");
        }

        _cpRewardPump = null;
    }

    /// <summary>The game's menu over a match (the period stats screen): B by the pad after 2 s, as a player would; the game's own back handler if two Bs did nothing.</summary>
    private static void WatchStatsScreen(AbstractMenu? top)
    {
        GameMenu? menu = WorldMap.SceneName == WorldMap.MatchScene ? top?.TryCast<GameMenu>() : null;
        if (menu == null || menu.IsHidden)
        {
            _cpStatsMenu = IntPtr.Zero;
            return;
        }

        if (_cpStatsMenu != menu.Pointer)
        {
            _cpStatsMenu = menu.Pointer;
            _cpStatsSince = FrameClock.Seconds;
            _cpStatsPresses = 0;
            StepNote($"campaign: the game's {menu.CurrentContext} menu is up over the match");
            return;
        }

        if (FrameClock.Seconds - _cpStatsSince < StatsScreenSeconds * (_cpStatsPresses + 1))
        {
            return;
        }

        if (_cpStatsPresses < 2)
        {
            _cpStatsPresses++;
            TapNow(PadButtons.B);
            StepNote($"campaign: B by the pad on the game's {menu.CurrentContext} menu");
        }
        else if (_cpStatsPresses == 2)
        {
            _cpStatsPresses++;
            Fail($"campaign: two Bs by the pad left the game's {menu.CurrentContext} menu up; its own back handler closes it, as the soak runner does");
            menu.OnBackInput();
        }
    }

    /// <summary>The Team Canada curse pick: A by the pad as a player would (its speech, then the first card); the first card clicked if a dozen As did nothing.</summary>
    private static void WatchCanada(AbstractMenu? top)
    {
        CanadaCurseMenu? menu = top?.TryCast<CanadaCurseMenu>();
        if (menu == null)
        {
            _cpCanadaMenu = IntPtr.Zero;
            return;
        }

        if (_cpCanadaMenu != menu.Pointer)
        {
            _cpCanadaMenu = menu.Pointer;
            _cpCanadaTaps = 0;
            _cpCanadaNext = FrameClock.Seconds + 1.5;
            StepNote("campaign: the Team Canada curse pick is up: A by the pad, as a player picks");
            return;
        }

        if (FrameClock.Seconds < _cpCanadaNext)
        {
            return;
        }

        _cpCanadaNext = FrameClock.Seconds + CanadaTapSeconds;
        if (_cpCanadaTaps < CanadaTapsBeforeClick)
        {
            _cpCanadaTaps++;
            TapNow(PadButtons.A);
        }
        else if (_cpCanadaTaps == CanadaTapsBeforeClick)
        {
            _cpCanadaTaps++;
            Il2CppSystem.Collections.Generic.List<AbstractNavigable>? cards = menu.m_CardNavigables;
            Fail($"campaign: {CanadaTapsBeforeClick} As by the pad left the Team Canada curse pick up; its first card is clicked as the soak runner does");
            if (cards != null && cards.Count > 0 && cards[0] != null)
            {
                cards[0].ForceClick();
            }
        }
    }

    /// <summary>record=on: once the walker has picked the tour's last match (its last act's boss), the host's relay records it.</summary>
    private static void WatchRecording()
    {
        if (!_cpArgs.Record || _cpRecordHeld || !_cpWalking || FrameClock.Seconds < _cpNextNodeTypeRead)
        {
            return;
        }

        _cpNextNodeTypeRead = FrameClock.Seconds + 0.5;
        ReadRun(out int act, out _);
        string type = TourCampaignWalker.LastNodeType();
        if (!CampaignTour.IsLastMatch(_cpKind, act, type))
        {
            return;
        }

        _cpRecordHeld = true;
        StepNote(ModSettings.TryHoldForTour(nameof(ModSettings.RecordOnlineMatches), true, out string problem)
            ? $"campaign: record=on: the last match ({type}) is recorded by the host's relay (RecordOnlineMatches held in memory for the tour, never the player's setting)"
            : $"campaign: record=on: RecordOnlineMatches couldn't be held for the tour ({problem}); nothing recorded");
    }

    private static void WatchPromptBefore()
    {
        if (_cpPrompt != null)
        {
            return;
        }

        PromptTemplateMenu? prompt = Top()?.TryCast<PromptTemplateMenu>();
        if (prompt != null && !prompt.m_HasSentResult)
        {
            _cpPrompt = prompt;
            _cpPromptSince = FrameClock.Seconds;
            string title = prompt.TryCast<CustomizableYesNoPrompt>()?.m_TitleText?.text ?? string.Empty;
            _cpPromptKind = $"{MenuGame.TypeName(prompt)}{(title.Length > 0 ? $" \"{SafeText.Clean(title, 48)}\"" : string.Empty)}";
        }
    }

    private static void WatchPromptAfter()
    {
        if (_cpPrompt == null)
        {
            return;
        }

        if (_cpPrompt.WasCollected)
        {
            _cpPromptAnswers++;
            StepNote($"campaign: the walker answered {_cpPromptKind} (it closed before its answer was read)");
            _cpPrompt = null;
        }
        else if (_cpPrompt.m_HasSentResult)
        {
            _cpPromptAnswers++;
            StepNote($"campaign: the walker answered {_cpPromptKind} {_cpPrompt.m_Result}");
            _cpPrompt = null;
        }
        else if (FrameClock.Seconds - _cpPromptSince > 10)
        {
            _cpPrompt = null; // still up, not the walker's to answer: looked at again from the next frame
        }
    }

    private static string ModPromptTitle()
    {
        AbstractMenu? top = Top();
        string? title = top?.TryCast<CustomizableYesNoPrompt>()?.m_TitleText?.text ?? top?.TryCast<CustomizableDismissablePrompt>()?.m_Title?.text;
        return title == null ? "(on its way)" : SafeText.Clean(title, 48);
    }

    // ---- The pad from inside a watch ----

    private static void TapNow(PadButtons button)
    {
        if (_walk == null || _cpTapHeld != PadButtons.None)
        {
            return;
        }

        _walk.Hold(button);
        _cpTapHeld = button;
        _cpTapUntil = FrameClock.Seconds + TapSeconds;
    }

    private static void ReleaseTapIfDue()
    {
        if (_cpTapHeld != PadButtons.None && FrameClock.Seconds >= _cpTapUntil)
        {
            ReleaseTap();
        }
    }

    private static void ReleaseTap()
    {
        if (_cpTapHeld != PadButtons.None)
        {
            _walk?.Let(_cpTapHeld);
            _cpTapHeld = PadButtons.None;
        }
    }

    // ---- State ----

    private static void ResetCampaign()
    {
        _cpKind = CampaignTour.KindOf(_request?.Tour.Name) ?? CampaignTourKind.Act1;
        _cpArgs = CampaignTour.ArgsOf(_request);
        Tally.Reset();
        ResetCampaignKeys(); // the opt-in keys' state (LobbyUiCheck.CampaignKeys.cs): which the plan holds, nothing tried
        RewardMenusSeen.Clear();
        _cpBroken = null;
        _cpReady = _cpRunEnded = _cpWon = _cpWalking = _cpRecordHeld = _cpMenuRowPressed = false;
        _cpWalks = _cpWalkerLines = _cpDroppedReported = _cpTimeScaleResets = _cpPromptAnswers = _cpRewardFallbacks = _cpWatchFailures = _cpEventScreens = 0;
        _cpTakeoverGoesOn = false;
        _cpMapCheckpoint = 0;
        _cpAct = _cpFloor = 0;
        _cpPrompt = null;
        _cpTapHeld = PadButtons.None;
        _cpStatsMenu = _cpCanadaMenu = _cpRewardMenu = IntPtr.Zero;
        _cpRewardPump = null;
        _cpRewardFailureSaid = null;
        _cpRunFileSeen = "missing";
        _cpNextWatch = _cpNextNodeTypeRead = 0;
    }

    /// <summary>True when the run can go on: cp_host set it up and nothing broke it; else the step says why it's skipped.</summary>
    private static bool CampaignGoesOn()
    {
        if (!_cpReady)
        {
            Problem("skipped: cp_host didn't set the tour's run up");
            return false;
        }

        if (_cpBroken != null)
        {
            Problem($"skipped: {_cpBroken}");
            return false;
        }

        return true;
    }

    /// <summary>The run can't go on: the step's problem, and every later step but the end and the close is skipped.</summary>
    private static void Broken(string why)
    {
        Problem(why);
        _cpBroken ??= why;
    }

    private static void ArmRewards()
    {
        PlayersManager? host = PlayersManager.Instance;
        string? problem = host == null || host.WasCollected ? "no PlayersManager"
            : !host.isActiveAndEnabled ? "PlayersManager isn't active"
            : TourCampaignWalker.Arm(host);
        StepNote(problem == null
            ? "campaign: rewards armed on PlayersManager (the game's reward events start the walker's reward coroutine on it; it lives across scenes)"
            : $"campaign: no host for the walker's rewards ({problem}): each reward menu is driven by the tour");
    }

    private static bool InMatchScene() => WorldMap.SceneName == WorldMap.MatchScene && HudModel.GameManagerInstance() != null;

    /// <summary>The match is being played: its scene, a running clock, the puck in play (the game's states 1 to 6).</summary>
    private static bool InPlay()
    {
        if (!InMatchScene() || Time.timeScale < 0.5f)
        {
            return false;
        }

        int state = (int)GameManager.GameState;
        return state is >= 1 and <= 6;
    }

    private static bool LobbyBack() => CampaignGame.TitleLoaded() && Flow().Host == LobbyHostState.Open && LobbyShell.IsShown && LobbyShell.IsIdle;

    private static uint Checkpoint() => CampaignRelay.Current?.LatestRun?.Sequence ?? 0;

    private static long MenusSent() => MenuViews.Instance?.Sent ?? 0;

    /// <summary>The run's act and floor as the game holds them (false and 0 when there's no run).</summary>
    private static bool ReadRun(out int act, out int floor)
    {
        act = floor = 0;
        try
        {
            var run = CampaignGame.State()?.runData;
            if (run == null || string.IsNullOrEmpty(run.runID))
            {
                return false;
            }

            act = run._currentAct;
            floor = run.currentFloor;
            return true;
        }
        catch (Exception)
        {
            return false; // read for the tour's lines only
        }
    }

    /// <summary>The tour's run file's hash and size for the summary (its first 16 hex digits), or <c>missing</c>.</summary>
    private static string RunFileText()
    {
        try
        {
            string? path = TourHolds.RunFile;
            if (path == null || !File.Exists(path))
            {
                return "missing";
            }

            byte[] bytes = File.ReadAllBytes(path);
            return CampaignTour.FileText(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }

    // ---- Leaving (Restore) ----

    /// <summary>
    /// Before anything else is put back: the walker's rewards disarmed, the pad's press let go; a run on the map left by the
    /// map's menu to the title (the run saved in the tour's own file). Anywhere else away from the title (a match, a load),
    /// the tour quits the game with every hold on: a run left away from the title must never be played on with the save
    /// guard let go. Nothing when another tour runs.
    /// </summary>
    private static IEnumerable<Wait> LeaveCampaign()
    {
        if (!Campaigning)
        {
            yield break;
        }

        ReleaseTap();
        Session?.TourFriendPress(PadButtons.None); // a tour stopped inside one of the friend's presses lets it go too
        TourCampaignWalker.Disarm();
        TourCampaignWalker.CurrentPump = null;
        _cpRewardPump = null;
        if (CampaignGame.TitleLoaded())
        {
            yield break;
        }

        if (WorldMap.MapLoaded && TourHolds.RunFile != null)
        {
            // The tour's own run (its own run file): its map menu by the map bar's own handler, once the map takes it (a call,
            // not a press: the handler's own condition is all it needs, and the pad may be gone already).
            WorldMapMenu? menu = Top()?.TryCast<WorldMapMenu>();
            if (menu == null && Top() == null)
            {
                yield return Soft(MapHandlerReady, 10, "the map's own input ready for its menu");
                if (MapHandlerReady())
                {
                    WorldMapMenusHandler.s_Instance!.OnOpenWorldMapMenu(null!);
                }

                yield return Soft(() => (menu = Top()?.TryCast<WorldMapMenu>()) != null, 5, "the map's menu");
            }

            if (menu != null && !menu.WasCollected)
            {
                yield return Seconds(0.8);
                menu.BackToMainMenu();
                yield return Soft(CampaignGame.TitleLoaded, 45, "the title");
            }
        }

        if (CampaignGame.TitleLoaded())
        {
            Line("Restored: the campaign tour's run left by the map's menu (saved in the tour's own run file); the title back.");
            yield break;
        }

        _quitWhenDone = true;
        Line($"NOT RESTORED: the campaign tour stopped in {WorldMap.SceneName}, away from the title: it quits the game with every hold on (a run away from the title is never played on with the save guard let go).");
    }

    // ---- onepc: a failed load ----

    /// <summary>The one-PC friend reports it couldn't load the next match: the host's AI plays their skater at once; the game's quit; Here again.</summary>
    private static IEnumerable<Wait> StepOnePcFailLoad()
    {
        OnlineSession session = Sd();
        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 10, "the friend Here");
        session.TourFailNextLoad(true);
        try
        {
            foreach (Wait wait in MoveTo(Row(LobbyFocusKind.StartRow)))
            {
                yield return wait;
            }

            yield return Soft(() => session.Actions.Snapshot().StartAllowed, 8, "Start allowed");
            foreach (Wait wait in StartToController("Start", PadButtons.Start))
            {
                yield return wait;
            }

            // The friend onto a side and ready over Steam, as op_start's press: a player of the match, with a skater. Only on
            // the controller screen, the screen these presses are for (StartToController has said it if it never came).
            foreach (PadButtons press in new[] { PadButtons.DPadLeft, PadButtons.A })
            {
                if (Top()?.TryCast<ControllerAssignmentMenu>() == null)
                {
                    break;
                }

                session.TourFriendPress(press);
                yield return Seconds(0.2);
                session.TourFriendPress(PadButtons.None);
                yield return Seconds(0.5);
            }

            StepNote($"the controller screen with the friend: {ControllerScreenLine()}");
            foreach (Wait wait in ForwardFromController())
            {
                yield return wait;
            }

            yield return Soft(() => Flow().Host == LobbyHostState.InGame, 30, "the scene left the title");
            yield return Soft(() => session.TourFriendLoadFailed, 45, "the friend's failed load at the host");
            yield return Soft(() => session.TourStandIns > 0, 15, "the AI on the friend's skater");
            StepNote($"op_failload: the friend {(session.TourFriendLoadFailed ? "reported a failed load" : "reported no failed load")}; the AI plays {session.TourStandIns} friend skater(s) (blind); {session.TourFriendLine()}");
            Check("the failed load at the host", () => session.TourFriendLoadFailed, "their load failure held by the host's stream");
            Check("the AI at once", () => session.TourStandIns > 0, "the AI plays their skater");
            foreach (Wait wait in Snap("failload_match", 0))
            {
                yield return wait;
            }

            Il2CppTape2Tape.CampaignUtil.startNewRunImmediately = true;
            session.TourMarkMatchEnded();
            GameLoader? loader = UnityEngine.Object.FindObjectOfType<GameLoader>();
            if (loader?._gameModeController is { } controller)
            {
                controller.Quit();
            }
            else
            {
                Fail("the game's mode controller wasn't found to quit through");
            }

            yield return Soft(() => Flow().Host == LobbyHostState.Open && LobbyShell.IsIdle, 45, "back at the lobby");
            yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 10, "the friend Here");
            StepNote($"{SeatLine(FriendSeat())}; {session.TourFriendLine()}");
            Check("after the failed load", () => Flow().Host == LobbyHostState.Open && FriendSeat().Stage == SeatStage.Here && !session.TourFriendLoadFailed && session.TourStandIns == 0,
                "the lobby, the friend Here, the failure cleared, their skater theirs");
        }
        finally
        {
            session.TourFailNextLoad(false);
        }
    }
}
#endif
