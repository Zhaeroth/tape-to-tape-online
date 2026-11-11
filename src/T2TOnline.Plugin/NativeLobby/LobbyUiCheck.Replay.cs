#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2Cpp;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Replay;
using T2TOnline.Plugin.Runtime;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

// The replay tour (run.txt's first line "replay"; Core's ReplayTour): a friend's whole match drawing on one PC, outside any
// session. A Play Now match started by the tour's pad through the game's controller screen; F3 records it, with a real goal
// at 20 s; F4 plays it back in place; the game's own quit; F2 loads the recording's match fresh and plays it to its end; F2
// once on a campaign recording if one is there. The keys' own methods are called (IStreamKeys, ModRuntime.StreamKeys.cs),
// never a key press. The recording goes into the tour's folder (TourStreamsFolder), so nothing outside it is written.
// The goal is the game's: the puck, while loose, is put in the visitor's net, where the net's VerifyGoal trigger checks it
// every physics step through GoalLineTrigger.VerifyGoalScored (verified in the game's code), and the match goes on.
internal static partial class LobbyUiCheck
{
    private const double MatchLoadSeconds = 60;
    private const double RecorderWaitSeconds = 30;
    private const double ClockWaitSeconds = 15;
    private const double TitleBackSeconds = 45;
    private const double RehearsalLoadSeconds = 100;
    private const double RehearsalTailSeconds = 30;
    private const int CampaignLooks = 12;

    private static readonly string[] MainRows = { "Campaign", "PlayNow", "Online", "Customize", "Compendium", "Glossary", "Settings", "Exit" };

    private static Dictionary<string, long>? _refusedBeforeMatch;
    private static string? _replayRecording;
    private static int _goalStateFailures;

    /// <summary>The developer keys F3, F4 and F2 as the replay tour calls them. Set once by the runtime.</summary>
    public static IStreamKeys? StreamKeys { get; set; }

    /// <summary>
    /// Where recordings go while any tour runs: its own folder (null otherwise: <c>dev\streams</c>). F3 records there and F2
    /// looks there (the replay tour), and so does the host's relay for every match a tour hosts (a campaign tour's
    /// <c>record=on</c>, or any tour's with the RecordOnlineMatches setting on): a recorder prunes the folder it records into,
    /// and a tour writes nothing outside its own.
    /// </summary>
    public static string? TourStreamsFolder => Running && _folder.Length > 0 ? _folder : null;

    /// <summary>Whether the replay tour is the one running: its steps share a match.</summary>
    private static bool Replaying => ReferenceEquals(_tour, ReplayTour.Steps);

    private static void AddReplaySteps()
    {
        Steps["rp_playnow"] = StepReplayPlayNow;
        Steps["rp_record"] = StepReplayRecord;
        Steps["rp_playback"] = StepReplayPlayback;
        Steps["rp_quit"] = StepReplayQuit;
        Steps["rp_rehearsal"] = StepReplayRehearsal;
        Steps["rp_campaign"] = StepReplayCampaign;
    }

    private static IStreamKeys Keys() => StreamKeys ?? throw new InvalidOperationException("the runtime gave the tour no developer keys");

    // ---- The steps ----

    private static IEnumerable<Wait> StepReplayPlayNow()
    {
        _ = Keys();
        _replayRecording = null;
        _goalStateFailures = 0;
        _refusedBeforeMatch = GuardCounts();
        foreach (Wait wait in MakePad())
        {
            yield return wait;
        }

        if (_walk == null)
        {
            yield break;
        }

        if (CampaignGame.Title() is { } title && (int)title.m_CurrentButtonGroup == 0)
        {
            // A real pad's first press joins it; the title takes a press from a pad that's a game player (as pw_start).
            foreach (Wait wait in Press("A, the pad's first press", PadButtons.A, () => _walk!.GamePlayer >= 0, "the pad a game player, by the game's join"))
            {
                yield return wait;
            }

            yield return Seconds(0.5);
            if (CampaignGame.Title() is { } still && (int)still.m_CurrentButtonGroup == 0)
            {
                foreach (Wait wait in Press("A, a second press", PadButtons.A, () => CampaignGame.Title() is { } t && (int)t.m_CurrentButtonGroup != 0, "off press-to-start"))
                {
                    yield return wait;
                }
            }
        }

        yield return Soft(OnMain, 8, "the main group");
        foreach (Wait wait in CursorOnMain("PlayNow"))
        {
            yield return wait;
        }

        // Main's Play Now row opens the Play group (TitleScreen.OnPlayClicked); the group's own Play Now (m_PlayNowButton) starts it.
        foreach (Wait wait in Press("A on Main's Play Now", PadButtons.A, PlayGroupUp, "the Play group", within: 4))
        {
            yield return wait;
        }

        for (int i = 0; i < 6 && PlayGroupUp() && !OnPlayNow() && MayPress("down to the Play group's Play Now", PlayGroupUp); i++)
        {
            string at = Cursor();
            foreach (Wait wait in Tap(i < 3 ? PadButtons.DPadUp : PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        if (_walkOk && !Check("the cursor on the Play group's Play Now", () => PlayGroupUp() && OnPlayNow(), "on Play Now"))
        {
            _walkOk = false;
        }

        foreach (Wait wait in Press("A on the Play group's Play Now", PadButtons.A, ControllerScreenUp, "Play Now's controller screen", within: 8))
        {
            yield return wait;
        }

        if (!ControllerScreenUp() && CampaignGame.Title() is { m_IsExiting: false } idle && PlaceNow(out _) is TourPlace.Main or TourPlace.Group)
        {
            // The step has failed (a pad alone must be enough; its FAIL line says where): the match is still started, the way
            // Close for Start does, so the rest of the tour runs. Only from the title's own groups, nothing over them.
            StepNote("the pad didn't reach Play Now's controller screen: the title's own LaunchPlayNow called, as Close for Start does (a failure's path)");
            idle.LaunchPlayNow();
            yield return Soft(ControllerScreenUp, 8, "Play Now's controller screen");
            Check("Play Now's controller screen", ControllerScreenUp, "the game's controller screen");
        }

        if (!ControllerScreenUp())
        {
            yield break;
        }

        yield return Seconds(1.2);
        StepNote($"the controller screen: {ControllerScreenLine()}");
        foreach (Wait wait in ForwardFromController())
        {
            yield return wait;
        }

        yield return Soft(InMatch, MatchLoadSeconds, "Play Now's match loaded");
        yield return Soft(() => InMatch() && SnapshotProbe.LastScene != null, RecorderWaitSeconds, "the game's recorder running");
        if (!InMatch())
        {
            Problem($"Play Now's match didn't load within {MatchLoadSeconds:0} s");
            yield break;
        }

        string setup = MatchSetup.TryCapture(out List<KeyValuePair<string, NetValue>> values, out string problem) ? MatchSetup.Describe(values) : $"its setup unread: {problem}";
        StepNote($"replay: Play Now's match in play ({setup}); the save guard has refused {Total(_refusedBeforeMatch)} writes before it");
    }

    private static IEnumerable<Wait> StepReplayRecord()
    {
        IStreamKeys keys = Keys();
        if (!InMatch())
        {
            Problem("no match in play to record");
            yield break;
        }

        yield return Soft(() => SnapshotProbe.LastScene != null && Time.timeScale > 0.5f, RecorderWaitSeconds, "the game's recorder and a running clock");
        keys.PressF3();
        StreamKeysState state = keys.State;
        if (!state.Recording || state.RecordingPath == null)
        {
            Problem("F3 didn't start recording (the log's Stream recorder line says why)");
            yield break;
        }

        string path = state.RecordingPath;
        _replayRecording = path;
        double started = FrameClock.Seconds;
        StepNote($"replay: F3 records to {DevOutput.Label(path)}");
        yield return Soft(() => !keys.State.Recording, ReplayTour.GoalAtSeconds, "the goal's moment");
        if (keys.State.Recording)
        {
            foreach (Wait wait in ForceGoal(started))
            {
                yield return wait;
            }
        }

        yield return Soft(() => !keys.State.Recording, Math.Max(0, ReplayTour.RecordSeconds - (FrameClock.Seconds - started)), "the recording's 40 s");
        if (!keys.State.Recording)
        {
            Problem(string.Create(CultureInfo.InvariantCulture, $"the recording stopped by itself after {FrameClock.Seconds - started:0.0} s (the log says why)"));
        }
        else
        {
            keys.PressF3();
        }

        if (keys.State.Recording)
        {
            Problem("F3 didn't stop the recording");
            yield break;
        }

        bool read = MatchRecording.TryLoad(path, out MatchRecording? recording, out string why);
        long bytes = FileBytes(path);
        int frames = recording?.Snapshots.Count ?? 0;
        double seconds = recording?.Seconds ?? 0;
        StepNote(ReplayTour.RecordedLine(frames, seconds, bytes, DevOutput.Label(path)));
        if (ReplayTour.RecordingProblem(read, frames, seconds) is { } wrong)
        {
            Problem(read ? wrong : $"{wrong} ({ModLog.Scrub.Scrub(why)})");
        }
    }

    private static IEnumerable<Wait> StepReplayPlayback()
    {
        IStreamKeys keys = Keys();
        if (!InMatch() || _replayRecording == null)
        {
            Problem(_replayRecording == null ? "nothing was recorded to play back" : "no match in play to play back onto");
            yield break;
        }

        // A goal's replay and slow motion hold the clock a while; F4 refuses a paused game.
        yield return Soft(() => Time.timeScale > 0.5f, ClockWaitSeconds, "the match's clock running");
        keys.PressF4();
        if (!keys.State.Playing)
        {
            Problem("F4 didn't start the playback (the log's Stream player line says why)");
            yield break;
        }

        double started = FrameClock.Seconds;
        yield return Soft(() => !keys.State.Playing, ReplayTour.PlaybackSeconds / 2, "the playback's first half");
        foreach (Wait wait in Snap("playback", 0))
        {
            yield return wait;
        }

        yield return Soft(() => !keys.State.Playing, Math.Max(0, ReplayTour.PlaybackSeconds - (FrameClock.Seconds - started)), "the playback's 20 s");
        double played = FrameClock.Seconds - started;
        bool byItself = !keys.State.Playing;
        if (!byItself)
        {
            keys.PressF4();
        }

        yield return Frames(2);
        StreamKeysState after = keys.State;
        StepNote(ReplayTour.PlayedLine(after.PlayedFrames, after.FailedStates, ModLog.Scrub.Scrub(after.StopReason)));
        if (after.Playing)
        {
            Problem("F4 didn't stop the playback");
        }
        else if (byItself)
        {
            Problem(string.Create(CultureInfo.InvariantCulture, $"the playback stopped by itself after {played:0.0} s ({ModLog.Scrub.Scrub(after.StopReason)})"));
        }

        if (ReplayTour.PlaybackProblem(after.PlayedFrames, after.FailedStates) is { } wrong)
        {
            Problem(wrong);
        }
    }

    private static IEnumerable<Wait> StepReplayQuit()
    {
        foreach (Wait wait in QuitToTitle("Play Now's match"))
        {
            yield return wait;
        }

        (IReadOnlyList<KeyValuePair<string, long>> grown, long total) = ReplayTour.RefusedSince(_refusedBeforeMatch ?? new Dictionary<string, long>(), GuardCounts());
        StepNote(ReplayTour.RefusedLine(grown, total));
        if (total == 0)
        {
            Problem("the save guard refused no write during the Play Now match: its writes weren't seen held");
        }
    }

    private static IEnumerable<Wait> StepReplayRehearsal()
    {
        IStreamKeys keys = Keys();
        if (_replayRecording == null)
        {
            Problem("nothing was recorded to rehearse");
            yield break;
        }

        yield return Soft(AtLoadableTitle, TitleBackSeconds, "the title, ready for a match load");
        string setup = PeekSetup(_replayRecording, out List<KeyValuePair<string, NetValue>> values) ? MatchSetup.Describe(values) : "a setup that doesn't read";
        keys.PressF2();
        if (!keys.State.RehearsalWaiting)
        {
            Problem($"F2 didn't start: {Outcome(keys.State)}");
            yield break;
        }

        StepNote("replay: F2 loads the recording's match fresh, as a friend's game would");
        yield return Soft(() => keys.State.Playing || !keys.State.RehearsalWaiting, RehearsalLoadSeconds, "the recording's match loaded and playing");
        StreamKeysState state = keys.State;
        StepNote($"replay: F2: {Outcome(state)}");
        if (state.RehearsalWaiting)
        {
            Problem($"F2's match didn't load within {RehearsalLoadSeconds:0} s");
            keys.PressF2();
        }
        else if (state.RehearsalOutcome != ModRuntime.RehearsalExact)
        {
            Problem($"F2: {Outcome(state)}");
        }

        if (keys.State.Playing)
        {
            double started = FrameClock.Seconds;
            yield return Soft(() => !keys.State.Playing, ReplayTour.GoalAtSeconds, "the rehearsal's first 20 s");
            foreach (Wait wait in Snap("rehearsal", 0))
            {
                yield return wait;
            }

            double length = ReplayTour.RecordSeconds + RehearsalTailSeconds;
            yield return Soft(() => !keys.State.Playing, Math.Max(0, length - (FrameClock.Seconds - started)), "the recording played to its end");
            if (keys.State.Playing)
            {
                Problem(string.Create(CultureInfo.InvariantCulture, $"still playing after {FrameClock.Seconds - started:0} s; stopped by F2"));
                keys.PressF2();
            }

            yield return Frames(2);
            state = keys.State;
            StepNote(ReplayTour.RehearsalLine(setup, state.PlayedFrames, state.FailedStates));
            if (ReplayTour.PlaybackProblem(state.PlayedFrames, state.FailedStates) is { } wrong)
            {
                Problem(wrong);
            }
        }
        else if (!state.RehearsalWaiting)
        {
            Problem("F2's playback didn't start (the log's Fresh-match rehearsal line says why)");
        }

        foreach (Wait wait in QuitToTitle("the rehearsal's match"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepReplayCampaign()
    {
        IStreamKeys keys = Keys();
        var files = new List<RecordingFile>();
        AddRecordings(files, DevOutput.StreamsFolder);
        foreach (string folder in Folders(DevOutput.ToursFolder))
        {
            if (!string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                AddRecordings(files, folder);
            }
        }

        RecordingFile? found = ReplayTour.NewestCampaign(files, IsCampaignRecording, CampaignLooks, out int looked);
        if (found is not { } campaign)
        {
            StepNote($"replay: no campaign recording to try F2 on ({looked} of {files.Count} recordings looked at, in dev/streams and the other tour folders)");
            yield break;
        }

        string label = DevOutput.Label(campaign.Path);
        yield return Soft(AtLoadableTitle, TitleBackSeconds, "the title, ready for a match load");
        keys.PressF2On(campaign.Path);
        StreamKeysState state = keys.State;
        if (!state.RehearsalWaiting && !state.Playing)
        {
            // MatchSetup.LoadCampaign refuses unless this game follows a host's campaign (verified in the code): expected.
            StepNote($"replay: F2 on a campaign recording ({label}): refused: {Outcome(state)}");
            yield break;
        }

        StepNote($"replay: F2 on a campaign recording ({label}): taken; waiting for what it comes to");
        yield return Soft(() => keys.State.Playing || !keys.State.RehearsalWaiting, RehearsalLoadSeconds, "the campaign recording's match");
        StepNote($"replay: F2 on a campaign recording ({label}): {Outcome(keys.State)}");
        if (keys.State.Playing || keys.State.RehearsalWaiting)
        {
            keys.PressF2();
        }

        yield return Frames(2);
        if (InMatch() || CampaignGame.Title() == null)
        {
            foreach (Wait wait in QuitToTitle("the campaign recording's match"))
            {
                yield return wait;
            }
        }
    }

    // ---- The goal ----

    /// <summary>
    /// A real goal, by the game's own scoring: within <see cref="ReplayTour.GoalWindowSeconds"/>, the first moment the puck
    /// is loose it is stopped and put in the visitor's net (its VerifyGoal trigger box); the game's trigger and
    /// <c>VerifyGoalScored</c> score it. Then up to <see cref="ReplayTour.GoalScoredSeconds"/> for the goals to rise.
    /// </summary>
    private static IEnumerable<Wait> ForceGoal(double recordStarted)
    {
        GameManager? game = HudModel.GameManagerInstance();
        if (game == null || game.WasCollected)
        {
            Problem("replay: no game manager for the goal");
            yield break;
        }

        int before = Goals(game, out int homeBefore);
        Il2CppTape2Tape.Puck? puck = null;
        yield return Soft(() => (puck = LoosePuck()) != null, ReplayTour.GoalWindowSeconds, "a loose puck");
        if (puck == null || puck.WasCollected || game.WasCollected)
        {
            Problem($"replay: the puck was never loose in {ReplayTour.GoalWindowSeconds:0} s (state {StateName()}); no goal forced");
            yield break;
        }

        double at = FrameClock.Seconds - recordStarted;
        if (!TryPutInNet(game, puck, out GoalLineTrigger? trigger, out string net))
        {
            Problem($"replay: no goal forced: {net}");
            yield break;
        }

        yield return Soft(() => !game.WasCollected && Goals(game, out _) > before, ReplayTour.GoalScoredSeconds, "the game's own goal");
        if (game.WasCollected || Goals(game, out int homeNow) <= before)
        {
            Problem(string.Create(CultureInfo.InvariantCulture,
                $"replay: no goal within {ReplayTour.GoalScoredSeconds:0} s of the puck in {net} (state {StateName()}, its trigger's in-goal flag {trigger != null && !trigger.WasCollected && trigger.isInGoal}, the puck at {PuckWhere()})"));
            yield break;
        }

        string scorer = homeNow > homeBefore ? "the home team" : "the visitors";
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"replay: a goal by {scorer} at {at:0.0} s into the recording: the puck into {net}, scored by the game's own VerifyGoal; score {Score(game)}"));
    }

    /// <summary>
    /// The puck stopped and put in a net (<see cref="TryNet"/>), to lie there from the next physics step. False, with why in
    /// <paramref name="what"/>, when there's no net or a call failed. Never throws.
    /// </summary>
    private static bool TryPutInNet(GameManager game, Il2CppTape2Tape.Puck puck, out GoalLineTrigger? trigger, out string what)
    {
        trigger = null;
        try
        {
            if (!TryNet(game, out Vector3 target, out trigger, out what))
            {
                return false;
            }

            puck.ResetVelocity();
            puck.SetPosition(target);
            return true;
        }
        catch (Exception ex)
        {
            what = $"putting the puck in the net failed ({ModLog.SafeErrorText(ex)})";
            return false;
        }
    }

    /// <summary>The puck when it is loose (a shot, a pass, nobody's) and unpossessed, else null. Never throws.</summary>
    private static Il2CppTape2Tape.Puck? LoosePuck()
    {
        try
        {
            if (HudModel.GameManagerInstance() == null || !ReplayTour.IsLoose((int)GameManager.GameState))
            {
                return null;
            }

            Il2CppTape2Tape.Puck? puck = Il2CppTape2Tape.Puck.Instance;
            return puck != null && !puck.WasCollected && !puck.IsPossessed() ? puck : null;
        }
        catch (Exception ex)
        {
            // Asked every frame of the window: said once.
            if (_goalStateFailures++ == 0)
            {
                StepNote($"replay: the puck's state couldn't be read ({ModLog.SafeErrorText(ex)})");
            }

            return null;
        }
    }

    /// <summary>
    /// The net to score in: the visitor's (a home goal), else the other; its VerifyGoal trigger box, else its goal line's.
    /// <paramref name="what"/> names it for the log, or says why there's none.
    /// </summary>
    private static bool TryNet(GameManager game, out Vector3 target, out GoalLineTrigger? trigger, out string what)
    {
        target = default;
        trigger = null;
        Il2CppTape2Tape.Team? visitor = game._visitorTeam;
        IntPtr visitorPointer = visitor == null ? IntPtr.Zero : visitor.Pointer;
        var boxes = new List<(Collider Box, GoalLineTrigger Trigger, bool Visitors, bool Inside)>();
        foreach (VerifyGoal check in UnityEngine.Object.FindObjectsOfType<VerifyGoal>())
        {
            GoalLineTrigger? line = check._goalLineTrigger;
            Collider? box = check.GetComponent<Collider>() ?? check.GetComponentInChildren<Collider>();
            if (line != null && box != null)
            {
                boxes.Add((box, line, OwnerIs(line, visitorPointer), true));
            }
        }

        foreach (GoalLineTrigger line in UnityEngine.Object.FindObjectsOfType<GoalLineTrigger>())
        {
            Collider? box = line.GetComponent<Collider>();
            if (box != null)
            {
                boxes.Add((box, line, OwnerIs(line, visitorPointer), false));
            }
        }

        if (boxes.Count == 0)
        {
            what = "no VerifyGoal or GoalLineTrigger with a collider in the scene";
            return false;
        }

        // The visitor's net first; a net's VerifyGoal box (inside it) before its goal line's.
        boxes.Sort((a, b) => Rank(a.Visitors, a.Inside).CompareTo(Rank(b.Visitors, b.Inside)));
        (Collider chosen, GoalLineTrigger owner, bool visitors, bool inside) = boxes[0];
        string kind = inside ? "VerifyGoal" : "GoalLineTrigger";
        Bounds bounds = chosen.bounds;
        Vector3 center = bounds.m_Center;
        Vector3 extents = bounds.m_Extents;
        float puckY = Il2CppTape2Tape.Puck.GetPosition().y;
        (float x, float y, float z) = ReplayTour.PuckTarget((center.x, center.y, center.z), (center.x - extents.x, center.y - extents.y, center.z - extents.z),
            (center.x + extents.x, center.y + extents.y, center.z + extents.z), puckY);
        target = new Vector3(x, y, z);
        trigger = owner;
        what = string.Create(CultureInfo.InvariantCulture,
            $"the {(visitors ? "visitor's" : "home team's")} net ({kind} box at {center.x:0.00},{center.y:0.00},{center.z:0.00}, {extents.x * 2:0.00} x {extents.y * 2:0.00} x {extents.z * 2:0.00} m; the puck to {x:0.00},{y:0.00},{z:0.00})");
        return true;
    }

    private static int Rank(bool visitors, bool inside) => (visitors ? 0 : 2) + (inside ? 0 : 1);

    private static bool OwnerIs(GoalLineTrigger line, IntPtr team) =>
        team != IntPtr.Zero && line.goal is { } goal && goal.GetTeam() is { } owner && owner.Pointer == team;

    /// <summary>Both teams' goals, and the home team's in <paramref name="home"/> (0 for a team that isn't there).</summary>
    private static int Goals(GameManager game, out int home)
    {
        home = TeamGoals(game._homeTeam);
        return home + TeamGoals(game._visitorTeam);
    }

    // TeamStats is a value type in the interop: the field is read from the copy the property gives.
    private static int TeamGoals(Il2CppTape2Tape.Team? team) => team == null ? 0 : team.teamStats._goals;

    private static string Score(GameManager game) =>
        string.Create(CultureInfo.InvariantCulture, $"{TeamGoals(game._homeTeam)}-{TeamGoals(game._visitorTeam)}");

    private static string PuckWhere()
    {
        try
        {
            Vector3 at = Il2CppTape2Tape.Puck.GetPosition();
            return string.Create(CultureInfo.InvariantCulture, $"{at.x:0.00},{at.y:0.00},{at.z:0.00}");
        }
        catch (Exception ex)
        {
            return $"unread: {ModLog.SafeErrorText(ex)}";
        }
    }

    private static string StateName()
    {
        try
        {
            return HudModel.GameManagerInstance() == null ? "no game manager" : GameManager.GameState.ToString();
        }
        catch (Exception ex)
        {
            return $"unread: {ModLog.SafeErrorText(ex)}";
        }
    }

    // ---- Shared stretches ----

    /// <summary>The cursor onto <paramref name="row"/> of Main by the pad's up and down (the rows' order as the game lists them); not there, the step's presses end.</summary>
    private static IEnumerable<Wait> CursorOnMain(string row)
    {
        int wanted = Array.IndexOf(MainRows, row);
        for (int i = 0; i < MainRows.Length && OnMain() && Cursor() != row; i++)
        {
            string at = Cursor();
            int now = Array.IndexOf(MainRows, at);
            foreach (Wait wait in Tap(now > wanted ? PadButtons.DPadUp : PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        // The press after it is A on that row: on any other of Main's rows it would be the player's own (Campaign, Exit).
        if (!Check($"the cursor on Main's {row}", () => OnMain() && Cursor() == row, $"on {row}"))
        {
            _walkOk = false;
        }
    }

    /// <summary>The match quit by the game's own quit (its mode controller's), and the title back.</summary>
    private static IEnumerable<Wait> QuitToTitle(string what)
    {
        if (!InMatch())
        {
            yield return Soft(() => InMatch() || CampaignGame.Title() != null, 30, "a match or the title");
        }

        if (InMatch())
        {
            GameLoader? loader = UnityEngine.Object.FindObjectOfType<GameLoader>();
            if (loader == null || loader._gameModeController == null)
            {
                Problem($"{what}: the game's mode controller wasn't found to quit through");
                yield break;
            }

            MatchSetup.ReturnToMenu();
            StepNote($"replay: {what} quit by the game's own quit");
        }

        yield return Soft(AtLoadableTitle, TitleBackSeconds, "the title back");
        Check($"{what}: back at the title", AtLoadableTitle, "the title, no match, no scene load under way");
    }

    /// <summary>
    /// Before anything over the title is put back: the stream tools stopped by their keys' methods and the replay tour's match
    /// quit by the game's own quit. Nothing when another tour runs. Never leaves the game in a match it could avoid.
    /// </summary>
    private static IEnumerable<Wait> LeaveReplay()
    {
        if (!Replaying || StreamKeys is not { } keys)
        {
            yield break;
        }

        StreamKeysState state = keys.State;
        if (state.Recording)
        {
            keys.PressF3();
        }

        // F2 cancels a load and stops a recording's playback, F4's or its own.
        if (keys.State.RehearsalWaiting || keys.State.Playing)
        {
            keys.PressF2();
        }

        yield return Frames(2);
        if (!InMatch() && CampaignGame.Title() == null)
        {
            yield return Soft(() => InMatch() || CampaignGame.Title() != null, 30, "a match or the title");
        }

        if (!InMatch())
        {
            yield break;
        }

        GameLoader? loader = UnityEngine.Object.FindObjectOfType<GameLoader>();
        if (loader == null || loader._gameModeController == null)
        {
            Line("NOT RESTORED: the replay tour's match couldn't be quit (no mode controller).");
            yield break;
        }

        MatchSetup.ReturnToMenu();
        yield return Soft(() => CampaignGame.Title() != null && !InMatch(), TitleBackSeconds, "the title back");
        Line(CampaignGame.Title() != null && !InMatch()
            ? "Restored: the replay tour's match quit by the game's own quit; the title back."
            : $"NOT RESTORED: still not at the title {TitleBackSeconds:0} s after the replay tour's match was quit.");
    }

    private static bool InMatch() => !MatchSetup.CurrentMatch(out _).IsNone && HudModel.GameManagerInstance() != null;

    /// <summary>At the title with nothing loading: where F2 can load a match (<see cref="MatchSetup.ExhibitionLoadWait"/>).</summary>
    private static bool AtLoadableTitle() => !InMatch() && CampaignGame.Title() is { } title && !title.m_IsExiting && MatchSetup.ExhibitionLoadWait() == null;

    private static bool ControllerScreenUp() => Top()?.TryCast<ControllerAssignmentMenu>() != null;

    /// <summary>The title's Play group (<c>TitleScreen.EButtonGroup.Play</c>, 3), settled, nothing over it.</summary>
    private static bool PlayGroupUp() => CampaignGame.Title() is { } title && (int)title.m_CurrentButtonGroup == 3 && !title._isTransitioningGroup && Top() == null;

    /// <summary>The cursor on the Play group's own Play Now (<c>TitleScreen.m_PlayNowButton</c>; Main's row is <c>m_PlayButton</c>).</summary>
    private static bool OnPlayNow() =>
        CampaignGame.Title()?.m_PlayNowButton is { } button && Navigation().CurrentNavigable is { } current && current.Pointer == button.Pointer;

    private static string Outcome(StreamKeysState state) => ModLog.Scrub.Scrub(state.RehearsalOutcome ?? "no outcome (the log says why)");

    /// <summary>The save guard's refusals so far, by "Method(files)".</summary>
    private static Dictionary<string, long> GuardCounts()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (KeyValuePair<(System.Reflection.MethodBase Method, string Files), long> refused in SaveGuard.RefusedByFilesSoFar())
        {
            string key = $"{refused.Key.Method.Name}({refused.Key.Files})";
            counts[key] = counts.TryGetValue(key, out long had) ? had + refused.Value : refused.Value;
        }

        return counts;
    }

    private static long Total(Dictionary<string, long>? counts)
    {
        long total = 0;
        foreach (long count in counts?.Values ?? (IEnumerable<long>)Array.Empty<long>())
        {
            total += count;
        }

        return total;
    }

    private static long FileBytes(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The match setup a recording starts with, read from its first records only. Never throws.</summary>
    private static bool PeekSetup(string path, out List<KeyValuePair<string, NetValue>> setup)
    {
        setup = new List<KeyValuePair<string, NetValue>>();
        try
        {
            return RecordingPeek.TryReadSetup(File.OpenRead(path), setup, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCampaignRecording(string path) => PeekSetup(path, out List<KeyValuePair<string, NetValue>> setup) && CampaignMatchValues.IsCampaign(setup);

    private static void AddRecordings(List<RecordingFile> files, string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(folder, "*.t2tstream"))
            {
                files.Add(new RecordingFile(file, File.GetLastWriteTimeUtc(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StepNote($"replay: {DevOutput.Label(folder)} couldn't be listed ({ex.GetType().Name})");
        }
    }

    private static string[] Folders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
#endif
