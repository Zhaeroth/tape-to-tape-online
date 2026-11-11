#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppBishopGames.UI;
using Il2CppI2.Loc;
using Il2CppRewired;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The self-running check tour of the native lobby: with no input from anyone it walks the title's
/// row and group, the shell and its widgets, other languages and window sizes, and writes a screenshot and a text dump per step into
/// <c>&lt;game&gt;\UserData\T2TOnline\dev\ui-check\&lt;stamp&gt;\</c> (the newest 5 tours kept), with a <c>summary.txt</c>. Runs when <c>DevTools</c> is on and <c>dev\ui-check\run.txt</c> exists (deleted when read, so it runs
/// once; the game then quits), or by Ctrl+F12 (DevTools; it doesn't quit then). A step's failure doesn't stop the next. Any press from a human stops it and restores everything,
/// without quitting, unless the game then holds something of the player's the holds kept off the disk (what the tour played, a match or a run still on,
/// something not put back): the game would save it once the holds let go, so it quits with them on instead (Core's <c>TourEnd</c>).
/// <para>
/// Safety is the point of its design. The tours do host, join, open Settings and start a new run (<c>selfdrive</c>, <c>padwalk</c>, <c>kbwalk</c>, <c>onepc</c>, <c>campaign</c>, <c>offline</c>,
/// <c>replay</c>), and do each only under the tour's holds (<see cref="TourHolds"/>), which keep a tour on a player's own PC from changing their profile, saves, co-op slots, settings or
/// Steam account. A tour refuses to start inside a session; the lobbies it hosts are private and let in only its own one-PC stand-in. Core's <c>TourStarts</c> says where every step
/// may start, and Core's <c>TourGate</c> runs it only there: a step that can't start is refused without a press, and the tour stops for good (the game then quits with the holds on) when
/// two in a row can't start, a step that must end at the title's Main doesn't, or the game is on a screen the tours never open (<c>docs/DEVELOPMENT.md</c>, "The check tours").
/// The holds exist because of what the game saves on its own, each verified in the game's code. At quit it saves <c>config.es3</c> with the window's size then and pushes it to Steam Cloud
/// (<c>SystemSettings.OnApplicationQuit</c>, <c>Save</c>, <c>StoreSettings</c>); the save guard (<see cref="SaveGuard.HoldForCheck"/>) is on from before the first step and through the
/// quit, so every game write is refused, the quit's included. A window change saves and pushes on any mode change (<c>SystemSettings.Update</c>, <c>OnScreenModeChanged</c>), so a tour keeps
/// the player's full-screen mode. A language change remembers nothing (<c>SetLanguageAndCode(..., false, true)</c>; <c>config.es3</c>'s language is the settings' own field). Opening
/// Settings saves too (its tabs' <c>OnDisable</c>), as does opening the squad screen of a new run (new unlocks and tips: <c>ChooseMetaMenu.SetupMetas</c>, <c>CampaignTips.ShowAsync</c>), and
/// leaving a screen by <c>TryBackOut</c> or <c>ExitMenu</c> writes the team selection: the holds refuse those writes, and a tour leaves Play Now's controller screen through
/// <c>SetResult</c> and <c>FinishExitMenu</c> instead. The save files are hashed at the start and compared at the end.
/// </para>
/// </summary>
internal static partial class LobbyUiCheck
{
    private const string RunFile = "run.txt";
    private const double MinFramesPerSecond = 15;
    private const double RunFileCheckSeconds = 1;
    private const double TitleWaitSeconds = 120;
    private const double AnyPressGraceSeconds = 1;
    private const double ShellSeconds = 8;
    private const double RestoreWindowSeconds = 3;
    private const int SettleFrames = 20;

    // Each step's iterator, by the id in Core's plan. To add a step: a row in UiCheckPlan, an iterator here.
    private static readonly Dictionary<string, Func<IEnumerable<Wait>>> Steps = new(StringComparer.Ordinal)
    {
        ["title"] = StepTitle,
        ["mainwalk"] = StepMainWalk,
        ["group"] = StepGroup,
        ["selection"] = StepSelection,
        ["back"] = StepBack,
        ["shell"] = StepShell,
        ["tenopens"] = StepTenOpens,
        ["widgets"] = StepWidgets,
        ["handover"] = StepHandOver,
        ["unload"] = StepUnload,
        ["languages"] = StepLanguages,
        ["sizes"] = StepSizes,
        ["scr_group"] = StepScrGroup,
        ["scr_browser"] = StepScrBrowser,
        ["scr_lobby"] = StepScrLobby,
        ["scr_langs"] = StepScrLangs,
        ["scr_sizes"] = StepScrSizes,
        ["finish"] = StepFinish,
    };

    private static readonly StringBuilder Summary = new();
    private static readonly List<string> SelectionLog = new();
    private static readonly List<UiCheckHit> AllHits = new();
    private static string _root = string.Empty;
    private static string _folder = string.Empty;
    private static bool _devTools;
    private static Func<bool> _inSession = () => false;
    private static double _fileCheckAt;
    private static Phase _phase;
    private static bool _quitWhenDone;
    private static double _armedAt;
    private static long _startedStamp;
    private static double _startedAt;
    private static IEnumerator<UiCheckStep>? _plan;
    private static UiCheckStep? _step;
    private static IEnumerator<Wait>? _body;
    private static Wait? _wait;
    private static long _stepStamp;
    private static string? _stepProblem;
    private static int _ok;
    private static int _failed;
    private static int _captures;
    private static string _language = string.Empty;
    private static string _languageCode = string.Empty;
    private static int _windowWidth;
    private static int _windowHeight;
    private static FullScreenMode _windowMode;
    private static bool _windowChanged;
    private static bool _languageChanged;
    private static bool _ranInBackground;
    private static bool _guardHeld;
    private static bool _backedOut;
    private static Il2CppTape2Tape.Hockey.UI.TitleScreen? _title;
    private static IReadOnlyList<UiCheckStep> _tour = UiCheckPlan.All;
    private static TimeSpan _limit = UiCheckPlan.DefaultLimit;

    /// <summary>True while the tour runs: the game's onboarding prompt is held off meanwhile (it would save the profile and wait for an answer).</summary>
    public static bool Running => _phase is Phase.Running or Phase.Restoring;

    /// <summary>Remembers whether the developer tools are on, and what says a session is on. Call once.</summary>
    public static void Configure(bool devTools, Func<bool> inSession)
    {
        _root = DevOutput.ToursFolder;
        _devTools = devTools;
        _inSession = inSession;
    }

    /// <summary>Ctrl+F12: the tour, now, without quitting at its end.</summary>
    public static void StartByKey()
    {
        // Its screenshots can show the Steam friends list: only ever with the developer tools on, whoever calls it.
        if (!_devTools)
        {
            ModLog.Info("Lobby check: Ctrl+F12 is a developer key and DevTools is off; nothing was done.");
            return;
        }

        if (_phase is Phase.Off or Phase.Done)
        {
            ModLog.Info("Lobby check: asked for by Ctrl+F12; it stops with any press and doesn't quit the game.");
            _tourLine = null; // the whole tour, whatever run.txt asked for last
            Arm(quit: false);
        }
    }

    /// <summary>Every frame. Cheap while it isn't armed (nothing with DevTools off; with it on, one file check a second until a tour is asked for).</summary>
    public static void Update()
    {
        try
        {
            switch (_phase)
            {
                case Phase.Off:
                    CheckFile();
                    break;
                case Phase.Armed:
                    TryStart();
                    break;
                case Phase.Running:
                case Phase.Restoring:
                    Run();
                    break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby check: the tour itself failed: {ModLog.SafeErrorText(ex)} (thrown in {ThrownIn(ex)}).");
            Line($"The tour itself failed ({ModLog.SafeErrorText(ex)}).");
            if (_phase == Phase.Running)
            {
                BeginRestore();
            }
            else if (_phase == Phase.Restoring)
            {
                Conclude();
            }
            else if (_guardHeld)
            {
                // Armed, the holds taken, and then it failed before its first step: the restore puts back what the start
                // changed and lets the holds go (or quits with them on), as after a press. Left as "not run" the save guard
                // would stay on for as long as the game runs.
                BeginRestore();
            }
            else
            {
                NotRun(); // armed, but it never began (its folder couldn't be made, say)
            }
        }
    }

    /// <summary>
    /// A tour that never began: nothing ran and nothing is held. One from <c>run.txt</c> still quits the game, as it promised,
    /// with the save guard held through the quit (the game's own saves at quit aren't this run's to make).
    /// </summary>
    private static void NotRun()
    {
        _phase = Phase.Done;
        if (_quitWhenDone)
        {
            SaveGuard.HoldForCheck(true, out _);
            ModLog.Info("Lobby check: quitting the game (the save guard stays on through the quit).");
            Application.Quit();
        }
    }

    private static void CheckFile()
    {
        if (!_devTools)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (now < _fileCheckAt)
        {
            return;
        }

        _fileCheckAt = now + RunFileCheckSeconds;
        string file = Path.Combine(_root, RunFile);
        if (File.Exists(file))
        {
            _tourLine = File.ReadLines(file).FirstOrDefault();
            File.Delete(file);
            ModLog.Info($"Lobby check: {RunFile} found and deleted (\"{SafeText.Clean(_tourLine, 48)}\"); the tour runs once, then the game quits.");
            Arm(quit: true);
        }
    }

    private static void Arm(bool quit)
    {
        AddSelfDriveSteps();
        AddPadWalkSteps();
        AddOnePcSteps();
        AddReplaySteps();
        AddSettingsWalkSteps();
        AddKeyWalkSteps();
        AddScreensNativeSteps();
        AddOfflineSteps();
        AddCampaignSteps();
        _quitWhenDone = quit;
        _armedAt = FrameClock.Seconds;
        _phase = Phase.Armed;
    }

    private static void TryStart()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen? title = CampaignGame.Title();
        if (title != null && title.m_HasStarted)
        {
            if (_inSession())
            {
                ModLog.Warn("Lobby check: not during an online session (it never hosts, joins or touches one).");
                NotRun();
                return;
            }

            Start(title);
            return;
        }

        if (FrameClock.Seconds - _armedAt > TitleWaitSeconds)
        {
            ModLog.Warn($"Lobby check: no title within {TitleWaitSeconds:0} s; not run.");
            NotRun();
        }
    }

    private static void Start(Il2CppTape2Tape.Hockey.UI.TitleScreen title)
    {
        // What run.txt asks for, or nothing at all (UiCheckRequest: an unknown tour, key or value is refused).
        if (ReadRequest() is not { } request)
        {
            NotRun();
            return;
        }

        _request = request;
        _tour = UiCheckPlan.StepsFor(request); // the steps a key adds only with that key (a campaign tour's by CampaignTour.StepsFor)
        _limit = request.Tour.Limit;
        string? planProblem = UiCheckPlan.Problem(_tour);
        string? missing = _tour.Select(s => s.Id).FirstOrDefault(id => !Steps.ContainsKey(id));
        if (planProblem != null || missing != null)
        {
            ModLog.Warn($"Lobby check: the plan is wrong ({planProblem ?? $"no step {missing}"}); not run.");
            NotRun();
            return;
        }

        _title = title;
        _folder = Path.Combine(_root, DateTime.UtcNow.ToString(DevFileRetention.TourStampFormat, CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_folder);
        DevOutput.PruneTours(_folder);
        DevOutput.EnsureNotice();
        _language = LocalizationManager.CurrentLanguage ?? string.Empty;
        _languageCode = LocalizationManager.CurrentLanguageCode ?? string.Empty;
        _windowWidth = Screen.width;
        _windowHeight = Screen.height;
        _windowMode = Screen.fullScreenMode;
        _ranInBackground = Application.runInBackground;
        Application.runInBackground = true;
        _startedAt = FrameClock.Seconds;
        _startedStamp = Stopwatch.GetTimestamp();
        Summary.Clear();
        AllHits.Clear();
        _ok = _failed = _captures = 0;
        _backedOut = false;
        Line(string.Create(CultureInfo.InvariantCulture, $"Lobby check: started {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC; language {_language} ({_languageCode}); window {_windowWidth}x{_windowHeight} {_windowMode}; {(_quitWhenDone ? "quits when done" : "stays open")}."));

        // Before anything else: the holds (the save guard first), or no tour. A tour that could write isn't run.
        if (!TourHolds.Hold(_folder, out string guardProblem))
        {
            Line($"FAILED: the holds can't be taken ({guardProblem}); nothing was run.");
            Flush();
            ModLog.Warn($"Lobby check: FAILED: the holds can't be taken ({guardProblem}); nothing was run.");
            Application.runInBackground = _ranInBackground;
            NotRun();
            return;
        }

        _guardHeld = true;
        BeginTourCore(request); // its name, limit and arguments; the ERROR mark; the files hashed (LobbyUiCheck.Tour.cs)
        BeginSafety(); // the start gate's count, no stop (LobbyUiCheck.Safety.cs)
        if (!OnRealSession)
        {
            AttachFake();
        }

        if (CampaignTour.IsCampaignPlan(_tour))
        {
            ModLog.Info($"Lobby check: {CampaignTour.PlanLine(_tour)}.");
        }
        else if (UiCheckPlan.PlanLine(_tour) is { } keyed)
        {
            Line($"Lobby check: {keyed}.");
            ModLog.Info($"Lobby check: {keyed}.");
        }

        _plan = _tour.GetEnumerator();
        _phase = Phase.Running;
        ModLog.Info($"Lobby check: running {_tour.Count} steps; writing to {DevOutput.Label(_folder)}. Any press stops it and restores everything.");
    }

    /// <summary>One frame of the tour: a press or the timeout turns it to restoring; else the current wait, then the current step's next stretch.</summary>
    private static void Run()
    {
        TickTourScreens();
        TickWalk();
        if (_phase == Phase.Running)
        {
            if (FrameClock.Seconds - _startedAt > AnyPressGraceSeconds && AnyPress())
            {
                Line("A press from a human: stopped; restoring; the game stays open unless it holds what the tour played (said at the end).");
                _quitWhenDone = false;
                BeginRestore();
            }
            else if (FrameClock.Seconds - _startedAt > _limit.TotalSeconds)
            {
                Line(string.Create(CultureInfo.InvariantCulture, $"Timed out after {_limit.TotalMinutes:0.#} minutes, the tour's limit; restoring."));
                BeginRestore();
            }
        }

        if (_wait != null && !_wait.Over())
        {
            return;
        }

        if (_wait is { Late: true, IsSoft: false })
        {
            StepNote($"waited in vain: {_wait.What}");
            _stepProblem ??= $"waited in vain: {_wait.What}";
        }

        _wait = null;
        Advance();
    }

    private static void Advance()
    {
        for (int guard = 0; guard < 64; guard++)
        {
            if (_body == null && !NextStep())
            {
                return;
            }

            bool more;
            try
            {
                more = _body!.MoveNext();
            }
            catch (Exception ex)
            {
                _stepProblem = ModLog.SafeErrorText(ex);
                ModLog.Warn($"Lobby check: step {_step?.Id} threw: {_stepProblem} (thrown in {ThrownIn(ex)}).");
                more = false;
            }

            if (more)
            {
                _wait = _body!.Current;
                return;
            }

            EndStep();

            // A stop the gate asked for (before or after the step): only the stop's restore runs now (LobbyUiCheck.Safety.cs).
            TakeStop();
        }
    }

    /// <summary>False when the tour has ended.</summary>
    private static bool NextStep()
    {
        if (_phase == Phase.Restoring)
        {
            Conclude();
            return false;
        }

        if (_plan == null || !_plan.MoveNext())
        {
            Conclude();
            return false;
        }

        _step = _plan.Current;
        _stepProblem = null;
        _walkOk = true; // a step's presses stop at its own failed expectations only (LobbyUiCheck.SettingsWalk.cs)
        _stepStamp = Stopwatch.GetTimestamp();
        ModLog.Info($"Lobby check: step {_step.Id}: {_step.What}.");
        _body = Guarded(_step, Steps[_step.Id]).GetEnumerator();
        return true;
    }

    /// <summary>A step, after whatever a failed step before it left open, only from where it declares it starts (LobbyUiCheck.Safety.cs).</summary>
    private static IEnumerable<Wait> Guarded(UiCheckStep step, Func<IEnumerable<Wait>> body)
    {
        // The real session's tours share a lobby across their steps, and its shell.
        foreach (Wait wait in OnRealSession ? Array.Empty<Wait>() : CloseLeftovers())
        {
            yield return wait;
        }

        foreach (Wait wait in Gated(step, body))
        {
            yield return wait;
        }
    }

    private static void EndStep()
    {
        _body?.Dispose();
        _body = null;
        if (_step == null)
        {
            return;
        }

        double ms = (Stopwatch.GetTimestamp() - _stepStamp) * 1000.0 / Stopwatch.Frequency;
        if (_stepProblem == null)
        {
            _ok++;
            Line($"OK   {_step.Id} ({ms:0} ms): {_step.What}");
            ModLog.Info($"Lobby check: step {_step.Id} OK ({ms:0} ms).");
        }
        else
        {
            _failed++;
            Line($"FAIL {_step.Id} ({ms:0} ms): {_step.What}: {_stepProblem}");
            ModLog.Warn($"Lobby check: step {_step.Id} failed: {_stepProblem}");
        }

        _step = null;
        Flush();
    }

    /// <summary>Stops the running step and runs only the restoring.</summary>
    private static void BeginRestore()
    {
        _body?.Dispose();
        _body = null;
        _wait = null;
        _step = null;
        _plan = null;
        _phase = Phase.Restoring;
        _body = Restore().GetEnumerator();
    }

    private static void Conclude()
    {
        DetachFake();
        double seconds = (Stopwatch.GetTimestamp() - _startedStamp) / (double)Stopwatch.Frequency;
        string flags = UiCheckRules.Counts(AllHits);
        EndTourErrors(); // more ERROR lines than its steps staged fail the tour (LobbyUiCheck.Tour.cs)
        EndTourHolds(); // so does an invite held or a join refused: a tour reaches nobody (LobbyUiCheck.Tour.cs)
        string holds = TourHolds.SummaryText; // what the holds kept from the player's data and Steam account

        // To stay open the holds must let go, and the game would then save what they kept in its memory: in that case it
        // quits with them on, as a tour from run.txt does (Core's TourEnd).
        if (!_quitWhenDone && _guardHeld && TourEnd.QuitReason(TourHolds.PlayerDataHeld, AtTitleNow(), !Summary.ToString().Contains("NOT RESTORED", StringComparison.Ordinal), _stopWhy) is { } quitWhy)
        {
            _quitWhenDone = true;
            Line($"Quits instead of staying open: {quitWhy}. Every hold stays on through the quit, so none of it reaches the disk or Steam.");
            ModLog.Info($"Lobby check: the game quits instead of staying open: {quitWhy}.");
        }

        Line($"Done in {seconds:0} s: {_ok} OK, {_failed} failed; {_captures} screens; flags: {flags}; {holds}.");
        CompareFiles();
        ReportGuard();
        Flush();
        ModLog.Info($"Lobby check: done in {seconds:0} s: {_ok} OK, {_failed} failed; {_captures} screens; flags: {flags}; {holds}. Read {DevOutput.Label(Path.Combine(_folder, "summary.txt"))}.");
        Application.runInBackground = _ranInBackground;
        _phase = Phase.Done;
        if (_quitWhenDone)
        {
            // The guard and the holds stay on: the game's own saves at quit are refused too, so the tour leaves every file as it found it.
            ModLog.Info("Lobby check: quitting the game (the save guard and the holds stay on through the quit).");
            Application.Quit();
        }
        else if (_guardHeld)
        {
            _guardHeld = false;
            TourHolds.Release(); // the settings held in memory go back, then the save guard lets go
        }
    }

    // ---- The steps ----

    private static IEnumerable<Wait> StepTitle()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        if ((int)title.m_CurrentButtonGroup == 0 && !title._isTransitioningGroup)
        {
            StepNote("pressed through press-to-start with the game's own PressedToStart");
            title.PressedToStart(null!);
        }

        yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 8, "the main group");
        yield return Until(() => OnlineGroup.IsIn || LobbyShell.OffReason != null, 3, "the Online group's install");
        if (!OnlineGroup.IsIn)
        {
            Problem($"the Online group isn't in{(ModSettingsOff() ? " (the NativeLobby setting is off)" : string.Empty)}{(LobbyShell.OffReason is { } off ? $" ({off})" : string.Empty)}");
        }

        UiAssets.Request(OnlineGroup.Sources);
        yield return Until(() => OnlineGroup.Sources.All(UiAssets.Ready) || UiAssets.Problem != null, UiAssets.TimeoutSeconds + 1, "the source prefabs");
        StepNote($"lobby words {(LobbyLocalization.Installed ? "in the game's source" : "as plain text")}; {UiClone.Stripped} scripts stripped so far");
        foreach (Wait wait in Snap("title", SettleFrames))
        {
            yield return wait;
        }

        StepNote(NativeArt.Report());
    }

    private static bool ModSettingsOff() => !Startup.ModSettings.NativeLobby;

    /// <summary>Every row of the main group in turn by the game's own move, past the end both ways: the order and the wrap are what a pad gets.</summary>
    private static IEnumerable<Wait> StepMainWalk()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        NavigationController navigation = Navigation();
        List<AbstractNavigable> rows = ActiveRows(title.m_MainButtonsContainer);
        if (rows.Count == 0)
        {
            Problem("the main group has no rows");
            yield break;
        }

        navigation.SetCurrentNavigable(rows[0], true, true);
        yield return Frames(6);
        var down = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            navigation.m_CurrentDirection = AbstractNavigable.EDirection.Down;
            navigation.MoveInDirection();
            yield return Frames(6);
            down.Add(ObjectName(navigation.CurrentNavigable));
        }

        var up = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            navigation.m_CurrentDirection = AbstractNavigable.EDirection.Up;
            navigation.MoveInDirection();
            yield return Frames(6);
            up.Add(ObjectName(navigation.CurrentNavigable));
        }

        string[] wantedDown = rows.Skip(1).Concat(rows.Take(1)).Select(r => r.gameObject.name).ToArray();
        string[] wantedUp = rows.AsEnumerable().Reverse().Select(r => r.gameObject.name).ToArray();
        StepNote($"{rows.Count} rows: {string.Join(", ", rows.Select(r => r.gameObject.name))}");
        StepNote($"down: {string.Join(", ", down)}");
        StepNote($"up: {string.Join(", ", up)}");
        if (!down.SequenceEqual(wantedDown))
        {
            Problem($"down went {string.Join(", ", down)}, not {string.Join(", ", wantedDown)}");
        }

        if (!up.SequenceEqual(wantedUp))
        {
            Problem($"up went {string.Join(", ", up)}, not {string.Join(", ", wantedUp)}");
        }

        if (OnlineGroup.Row != null)
        {
            navigation.SetCurrentNavigable(OnlineGroup.Row, true, true);
            yield return Frames(20);
            foreach (Wait wait in Snap("title_online_row", 0))
            {
                yield return wait;
            }
        }
    }

    private static IEnumerable<Wait> StepGroup()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        NavigationController navigation = Navigation();
        SelectionLog.Clear();
        if (!OnlineGroup.Enter(title))
        {
            Problem("the group couldn't be entered");
            yield break;
        }

        // Frame by frame, for the next step: what the cursor is on, each change once.
        string last = ObjectName(navigation.CurrentNavigable);
        SelectionLog.Add(last);
        for (int i = 0; i < 45; i++)
        {
            yield return Frames(1);
            string now = ObjectName(navigation.CurrentNavigable);
            if (now != last)
            {
                SelectionLog.Add(now);
                last = now;
            }
        }

        if ((int)title.m_CurrentButtonGroup != OnlineGroup.GroupValue)
        {
            Problem($"the title is on group {(int)title.m_CurrentButtonGroup}, not {OnlineGroup.GroupValue}");
        }

        foreach (Wait wait in Snap("group", SettleFrames))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepSelection()
    {
        StepNote($"the cursor went: {string.Join(" > ", SelectionLog)}");
        int arrivals = SelectionLog.Skip(1).Count(n => n == "HostLobby");
        int others = SelectionLog.Skip(1).Count(n => n is not ("HostLobby" or "nothing"));
        if (arrivals != 1 || others != 0)
        {
            Problem($"the first row was selected {arrivals} time(s), {others} other selection(s) on the way");
        }

        yield break;
    }

    private static IEnumerable<Wait> StepBack()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        NavigationController navigation = Navigation();
        navigation.OnBackInput();
        yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 3, "the main group after Back");
        yield return Frames(6);
        string on = ObjectName(navigation.CurrentNavigable);
        StepNote($"back on the main group, the cursor on {on}");
        if (OnlineGroup.Row != null && on != OnlineGroup.Row.gameObject.name)
        {
            Problem($"the cursor came back on {on}, not the Online row");
        }

        foreach (Wait wait in Snap("back_on_main", 0))
        {
            yield return wait;
        }
    }

    /// <summary>The shell by the real path (the row, the group, Confirm on Host Lobby), its intro in two pictures, Back asking on a lobby, closing on a browser.</summary>
    private static IEnumerable<Wait> StepShell()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        NavigationController navigation = Navigation();
        if (!OnlineGroup.Enter(title))
        {
            Problem("the group couldn't be entered");
            yield break;
        }

        yield return Until(() => !Title()._isTransitioningGroup && ObjectName(Navigation().CurrentNavigable) == "HostLobby", 3, "Host Lobby selected");
        yield return Seconds(0.6); // the group ignores A for its first half second, on purpose: a press in flight mustn't host
        Fake.SetState(LobbyState.None); // the fake starts in a session, and Host Lobby in a session is a second press of the first
        navigation.OnConfirm();
        yield return Until(() => LobbyShell.IsShown || !LobbyShell.Available, ShellSeconds, "the shell shown");
        if (!LobbyShell.IsShown)
        {
            Problem($"the shell didn't show ({LobbyShell.OffReason})");
            yield break;
        }

        yield return Seconds(0.25);
        foreach (Wait wait in Snap("shell_mid_intro", 0))
        {
            yield return wait;
        }

        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        foreach (Wait wait in Snap("shell_at_rest", SettleFrames))
        {
            yield return wait;
        }

        StepNote($"the title's menu list: {string.Join(", ", MenusOver())}; its group {(int)title.m_CurrentButtonGroup}; IsIdle {CampaignGame.IsIdle(title)}");
        if (!CampaignGame.IsIdle(title))
        {
            Problem("CampaignGame.IsIdle doesn't skip the shell");
        }

        // Back on a lobby: the title's handler refuses, and the shell stays.
        navigation.OnBackInput();
        yield return Frames(20);
        if (!LobbyShell.IsShown || (int)title.m_CurrentButtonGroup != OnlineGroup.GroupValue)
        {
            Problem("Back on the lobby did something");
        }

        LobbyShell.Close("the check tour");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        foreach (Wait wait in AfterClose("Host Lobby"))
        {
            yield return wait;
        }

        // Back on a browser: the game's own, through its back stack.
        foreach (Wait wait in OpenShell(ShellKind.Browser))
        {
            yield return wait;
        }

        if (LobbyShell.IsShown)
        {
            yield return Until(() => LobbyShell.IsIdle, 4, "the browser's intro over");
            navigation.OnBackInput();
            yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the browser closed by Back");
            foreach (Wait wait in AfterClose("Host Lobby"))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepTenOpens()
    {
        var times = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            long start = Stopwatch.GetTimestamp();
            foreach (Wait wait in OpenShell(i % 2 == 0 ? ShellKind.Browser : ShellKind.Lobby))
            {
                yield return wait;
            }

            if (!LobbyShell.IsShown)
            {
                yield break;
            }

            double shownMs = MsSince(start);
            yield return Until(() => LobbyShell.IsIdle, 4, $"open {i + 1}'s intro over");
            double idleMs = MsSince(start);
            long closing = Stopwatch.GetTimestamp();
            LobbyShell.Close("the check tour");
            yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, $"open {i + 1} closed");
            yield return Until(TitleIsBack, 3, $"the title back after open {i + 1}");
            times.Add(string.Create(CultureInfo.InvariantCulture, $"{i + 1}: shown {shownMs:0}, rest {idleMs:0}, closed {MsSince(closing):0} ms"));
        }

        StepNote(string.Join("; ", times));
    }

    /// <summary>A label, a button with a glyph and a prompt bar shortcut cloned into the shell, a real yes/no over it (hidden, not answered), a toast.</summary>
    private static IEnumerable<Wait> StepWidgets()
    {
        foreach (Wait wait in OpenShell(ShellKind.Lobby))
        {
            yield return wait;
        }

        ShellParts? parts = LobbyShell.Parts;
        if (!LobbyShell.IsShown || parts == null)
        {
            yield break;
        }

        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        GameObject? labelClone = UiClone.Make(parts.Title.gameObject, UiClone.Widgets, "a label", out string problem);
        if (labelClone == null)
        {
            Problem(problem);
        }
        else
        {
            UiClone.Place(labelClone, parts.Details);
            Il2CppTMPro.TMP_Text? label = labelClone.GetComponent<Il2CppTMPro.TMP_Text>();
            if (label != null)
            {
                LobbyLocalization.Apply(label, LobbyLocalization.Online);
            }
        }

        GameObject? source = UiAssets.Part("GameSettings", "ScaledContainer/BottomButtons/Button_Back");
        AbstractNavigable? button = null;
        if (source == null)
        {
            Problem("the glyph button's prefab isn't held");
        }
        else
        {
            GameObject? buttonClone = UiClone.Make(source, UiClone.Widgets, "a button", out problem);
            if (buttonClone == null)
            {
                Problem(problem);
            }
            else
            {
                UiClone.Place(buttonClone, parts.ScrollView); // the lobby hides its list's content
                RectTransform? place = buttonClone.GetComponent<RectTransform>();
                if (place != null)
                {
                    place.anchorMin = place.anchorMax = place.pivot = new Vector2(0, 1);
                    place.anchoredPosition = new Vector2(32, -28);
                    place.sizeDelta = new Vector2(320, 64);
                }

                button = buttonClone.GetComponent<AbstractNavigable>();
                Il2CppTMPro.TMP_Text? text = buttonClone.transform.Find("Container/Content/Text")?.GetComponent<Il2CppTMPro.TMP_Text>();
                if (text != null)
                {
                    LobbyLocalization.Apply(text, LobbyLocalization.JoinLobby);
                }

                // Its glyph shows another action than Back's, so its press can't close anything here.
                buttonClone.GetComponent<Il2CppTape2Tape.Customization.UI.UIInputShortcutBase>()?.RefreshInput(new Il2CppBishopGames.Catalogs.InputActionsCatalogReference("UI_UISpecial"));
            }
        }

        parts.ExtraShortcut.gameObject.SetActive(true);
        Il2CppTape2Tape.Customization.UI.UIInputShortcutBase? extra = parts.ExtraShortcut.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppTape2Tape.Customization.UI.UIInputShortcutBase>(), true)?.TryCast<Il2CppTape2Tape.Customization.UI.UIInputShortcutBase>();
        if (extra != null)
        {
            extra.m_RequiredHoldTime = 0;
        }

        Il2CppTMPro.TMP_Text? extraLabel = parts.ExtraShortcut.Find("Label")?.GetComponent<Il2CppTMPro.TMP_Text>();
        if (extraLabel != null)
        {
            LobbyLocalization.Apply(extraLabel, LobbyLocalization.Online);
        }

        if (button != null)
        {
            LobbyShell.SetDefaultNavigables(button);
            AbstractNavigableUtils.SelectOnNextFrame(button, AbstractNavigable.ESelectFx.Default);
        }

        UiClone.Rebuild(parts.Root);
        foreach (Wait wait in Snap("widgets", SettleFrames))
        {
            yield return wait;
        }

        string prompt = Il2CppBishopGames.Catalogs.MenuDefinitionCatalogReference.CustomizableYesNoPrompt.Name;
        var data = new Il2CppTape2Tape.Customization.UI.CustomizableYesNoPrompt.Data("T2T Online", "A check of the lobby's screens. Nothing is answered.", "Yes", "No", false, new Il2CppSystem.Nullable<Il2CppTape2Tape.Customization.UI.PromptTemplateMenu.PromptTemplateResult>(), false);
        _ = MenuGame.Module()!.ShowMenuAsync<Il2CppTape2Tape.Customization.UI.CustomizableYesNoPrompt>(prompt, data);
        AbstractMenu? shell = LobbyShell.Menu;
        yield return Until(() => Top() is { } top && top.Pointer != shell?.Pointer, 4, "the yes/no over the shell");
        yield return Seconds(0.8);
        foreach (Wait wait in Snap("widgets_prompt", 0))
        {
            yield return wait;
        }

        AbstractMenu? over = Top();
        if (over != null && over.Pointer != shell?.Pointer)
        {
            _ = over.Hide();
            yield return Until(() => Top()?.Pointer == shell?.Pointer, 4, "the yes/no gone");
        }

        yield return Frames(8);
        StepNote($"after the yes/no, the cursor is on {ObjectName(Navigation().CurrentNavigable)}");
        AbstractNavigable? cursorAfter = Navigation().CurrentNavigable;
        if (cursorAfter == null || !cursorAfter.transform.IsChildOf(parts.Root))
        {
            Problem("the yes/no didn't give the cursor back to the shell");
        }

        NotificationQueue? queue = NotificationQueue.Instance;
        if (queue == null)
        {
            Problem("the game's notification queue isn't there");
        }
        else
        {
            queue.QueueGenericNotification(ENotificationTiming.TitleScreen, "T2T Online", "A check of the lobby's screens.", new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(0), new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Sprite>(0), null!, true);
            yield return Seconds(1.5);
            foreach (Wait wait in Snap("widgets_toast", 0))
            {
                yield return wait;
            }
        }

        LobbyShell.Close("the check tour");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        yield return Until(TitleIsBack, 3, "the title back");
    }

    /// <summary>Close for Start, by Play Now's flow: its controller screen over the shell, the shell under it all the way, a back-out to a live shell, then a live title.</summary>
    private static IEnumerable<Wait> StepHandOver()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        foreach (Wait wait in OpenShell(ShellKind.Lobby))
        {
            yield return wait;
        }

        AbstractMenu? shell = LobbyShell.Menu;
        if (!LobbyShell.IsShown || shell == null)
        {
            yield break;
        }

        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        _backedOut = false;
        LobbyShell.HandOverBackedOut += OnBackedOut;
        try
        {
            LobbyShell.BeginHandOver("check tour, Play Now");
            title.LaunchPlayNow();
            yield return Until(() => Top()?.TryCast<ControllerAssignmentMenu>() != null, 8, "Play Now's controller screen over the shell");
            ControllerAssignmentMenu? controllers = Top()?.TryCast<ControllerAssignmentMenu>();
            if (controllers == null)
            {
                Problem("Play Now's controller screen didn't come");
            }
            else
            {
                yield return Seconds(1.2);
                foreach (Wait wait in Snap("handover_controller_screen", 0))
                {
                    yield return wait;
                }

                StepNote($"under the controller screen: shell shown {LobbyShell.IsShown}, handing over {LobbyShell.HandingOver}, title exiting {title.m_IsExiting}, its menu list {string.Join(", ", MenusOver())}");
                if (!LobbyShell.IsShown || title.m_IsExiting)
                {
                    Problem("the shell didn't stay under the game's flow");
                }

                // A back-out, without its write of the team selection (TryBackOut's and ExitMenu's).
                controllers.SetResult(true);
                controllers.FinishExitMenu();
            }

            yield return Until(() => _backedOut || !LobbyShell.IsOpen, 12, "the back-out seen");
            if (!_backedOut)
            {
                Problem("the shell didn't report the back-out");
            }
        }
        finally
        {
            LobbyShell.HandOverBackedOut -= OnBackedOut;
        }

        foreach (Wait wait in Snap("handover_back_on_shell", SettleFrames))
        {
            yield return wait;
        }

        StepNote(string.Create(CultureInfo.InvariantCulture, $"after the back-out: shell on top {Top()?.Pointer == LobbyShell.Menu?.Pointer}, title exiting {title.m_IsExiting}, starting {title.m_IsStartingRun}, its menu list {string.Join(", ", MenusOver())}, the fade {title.m_FadeToBlack?.alpha:0.##}"));
        if (LobbyShell.IsOpen)
        {
            LobbyShell.Close("the check tour");
            yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        }

        yield return Until(TitleIsBack, 4, "the title back");
        yield return Frames(8);
        Il2CppTape2Tape.Hockey.UI.TitleScreen after = Title();
        StepNote($"the title: exiting {after.m_IsExiting}, group {(int)after.m_CurrentButtonGroup}, cursor {ObjectName(Navigation().CurrentNavigable)}, IsIdle {CampaignGame.IsIdle(after)}");
        if (after.m_IsExiting || !CampaignGame.IsIdle(after))
        {
            Problem("the title isn't live after the back-out");
        }

        foreach (Wait wait in Snap("handover_title_live", 0))
        {
            yield return wait;
        }
    }

    private static void OnBackedOut() => _backedOut = true;

    /// <summary>The shell, the game's Glossary and Compendium over the title, the game's own prefab release, the shell again: its sprites, by screenshot and by count.</summary>
    private static IEnumerable<Wait> StepUnload()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        foreach (Wait wait in OpenShell(ShellKind.Browser))
        {
            yield return wait;
        }

        if (!LobbyShell.IsShown)
        {
            yield break;
        }

        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        UiDump.Result before = UiDump.Read(LobbyShell.Parts!.Root.gameObject, Array.Empty<IntPtr>());
        LobbyShell.Close("the check tour");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        yield return Until(TitleIsBack, 3, "the title back");
        title.LaunchGlossary();
        foreach (Wait wait in AwaitAndHide("Glossary", "unload_glossary"))
        {
            yield return wait;
        }

        title.LaunchCompendium();
        foreach (Wait wait in AwaitAndHide("Compendium", "unload_compendium"))
        {
            yield return wait;
        }

        int released = MenuGame.Module()!.ReleaseUnusedMenuPrefabs(false);
        StepNote($"the game's own prefab release let go of {released}");
        yield return Frames(10);
        foreach (Wait wait in OpenShell(ShellKind.Browser))
        {
            yield return wait;
        }

        if (!LobbyShell.IsShown)
        {
            yield break;
        }

        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        foreach (Wait wait in Snap("unload_shell_again", SettleFrames))
        {
            yield return wait;
        }

        UiDump.Result after = UiDump.Read(LobbyShell.Parts!.Root.gameObject, Array.Empty<IntPtr>());
        StepNote($"images without a sprite: {before.WithoutSprite} of {before.Images} before, {after.WithoutSprite} of {after.Images} after");
        if (after.WithoutSprite > before.WithoutSprite)
        {
            Problem("the shell lost sprites");
        }

        LobbyShell.Close("the check tour");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        yield return Until(TitleIsBack, 3, "the title back");
    }

    /// <summary>The title's rows with no menu open, then the shell opened once and the language changed under it; closed in the player's language (the game re-prepares its title menus when one closes in another).</summary>
    private static IEnumerable<Wait> StepLanguages()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        foreach (string wanted in UiCheckPlan.Languages)
        {
            (string Name, string Code)? language = FindLanguage(wanted);
            if (language == null)
            {
                Problem($"the game has no language {wanted}");
                continue;
            }

            foreach (Wait wait in SetLanguage(language.Value.Name, language.Value.Code))
            {
                yield return wait;
            }

            foreach (Wait wait in Snap($"title_{language.Value.Code}", SettleFrames))
            {
                yield return wait;
            }

            OnlineGroup.Enter(title);
            yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 3, "the Online group");
            foreach (Wait wait in Snap($"group_{language.Value.Code}", SettleFrames))
            {
                yield return wait;
            }

            Navigation().OnBackInput();
            yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 3, "the main group");
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
        foreach (Wait wait in OpenShell(ShellKind.Lobby))
        {
            yield return wait;
        }

        if (!LobbyShell.IsShown)
        {
            yield break;
        }

        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        foreach (string wanted in UiCheckPlan.Languages)
        {
            (string Name, string Code)? language = FindLanguage(wanted);
            if (language == null)
            {
                continue;
            }

            foreach (Wait wait in SetLanguage(language.Value.Name, language.Value.Code))
            {
                yield return wait;
            }

            foreach (Wait wait in Snap($"shell_{language.Value.Code}", SettleFrames))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
        LobbyShell.Close("the check tour");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        yield return Until(TitleIsBack, 3, "the title back");
        if (LocalizationManager.CurrentLanguage != _language)
        {
            Problem($"the language is {LocalizationManager.CurrentLanguage}, not {_language}");
        }
    }

    /// <summary>Each window size in the player's own full-screen mode (a mode change would make the game save and push its settings): the title's rows and the shell.</summary>
    private static IEnumerable<Wait> StepSizes()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        foreach ((int width, int height) in UiCheckPlan.SizesFor(Display.main.systemWidth, Display.main.systemHeight))
        {
            _windowChanged = true;
            Screen.SetResolution(width, height, _windowMode);
            yield return Until(() => Screen.width == width && Screen.height == height, RestoreWindowSeconds, $"the window at {width}x{height}");
            yield return Frames(10);
            StepNote($"asked for {width}x{height}, got {Screen.width}x{Screen.height} ({_windowMode})");
            foreach (Wait wait in Snap("title", SettleFrames))
            {
                yield return wait;
            }

            OnlineGroup.Enter(title);
            yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 3, "the Online group");
            foreach (Wait wait in Snap("group", SettleFrames))
            {
                yield return wait;
            }

            foreach (Wait wait in OpenShell(ShellKind.Lobby))
            {
                yield return wait;
            }

            if (LobbyShell.IsShown)
            {
                yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
                foreach (Wait wait in Snap("shell", SettleFrames))
                {
                    yield return wait;
                }

                LobbyShell.Close("the check tour");
                yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
                yield return Until(TitleIsBack, 3, "the title back");
            }

            Navigation().OnBackInput();
            yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 3, "the main group");
        }

        foreach (Wait wait in RestoreWindow())
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepFinish()
    {
        foreach (Wait wait in Restore())
        {
            yield return wait;
        }
    }

    // ---- Shared stretches ----

    /// <summary>Everything back: menus over the title, the shell, the language, the window. Runs at the end, after a press and after the timeout, from any state.</summary>
    private static IEnumerable<Wait> Restore()
    {
        // The replay tour's match first: nothing over the title is put back while a match is up (LobbyUiCheck.Replay.cs).
        foreach (Wait wait in LeaveReplay())
        {
            yield return wait;
        }

        // The campaign tour's run next: left by the map's menu, or the game quits with the holds on (LobbyUiCheck.Campaign.cs).
        foreach (Wait wait in LeaveCampaign())
        {
            yield return wait;
        }

        foreach (Wait wait in LeaveSession())
        {
            yield return wait;
        }

        foreach (Wait wait in CloseLeftovers())
        {
            yield return wait;
        }

        EndWalk();
        EndNativeChecks(); // the keyboard walk's keyboard and mouse mode, the screens' rows context (LobbyUiCheck.ScreensNative.cs)
        if (_languageChanged || LocalizationManager.CurrentLanguage != _language)
        {
            foreach (Wait wait in SetLanguage(_language, _languageCode))
            {
                yield return wait;
            }

            _languageChanged = false;
        }

        foreach (Wait wait in RestoreWindow())
        {
            yield return wait;
        }

        if (LocalizationManager.CurrentLanguage != _language)
        {
            Line($"NOT RESTORED: the language is {LocalizationManager.CurrentLanguage}, not {_language}.");
        }
        else
        {
            Line($"Restored: language {_language}; window {Screen.width}x{Screen.height} {Screen.fullScreenMode}.");
        }
    }

    /// <summary>
    /// Whatever the tours opened over the title goes, the game's menus first (a controller screen without its write), then the
    /// shell. Only at the title, and only the tours' own screens (Core's <see cref="TourStarts.IsTourScreen"/>): anything else
    /// is left as it is, and said.
    /// </summary>
    private static IEnumerable<Wait> CloseLeftovers()
    {
        if (!AtTitleNow())
        {
            if (Top() is { } away)
            {
                StepNote($"left as it is: {MenuGame.TypeName(away)} (not at the title: no leftover of the tour's is closed there)");
            }

            yield break;
        }

        for (int i = 0; i < 4; i++)
        {
            AbstractMenu? top = Top();
            if (top == null || top.Pointer == LobbyShell.Menu?.Pointer)
            {
                break;
            }

            string id = MenuGame.MenuId(top);
            if (TourStarts.IsTransition(id))
            {
                yield return Seconds(0.7); // the game's own transition: it goes by itself
                continue;
            }

            if (!TourStarts.IsTourScreen(id) && !LobbyShell.IsShellId(id))
            {
                StepNote($"left as it is: {MenuGame.TypeName(top)} (a screen the tours never open)");
                yield break;
            }

            StepNote($"closing {MenuGame.TypeName(top)}, left over");
            ControllerAssignmentMenu? controllers = top.TryCast<ControllerAssignmentMenu>();
            if (controllers != null)
            {
                controllers.SetResult(true);
                controllers.FinishExitMenu();
            }
            else
            {
                _ = top.Hide();
            }

            yield return Seconds(0.7);
        }

        if (LobbyShell.IsOpen)
        {
            LobbyShell.Close("the check tour");
            yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
            yield return Until(TitleIsBack, 3, "the title back");
        }
    }

    private static IEnumerable<Wait> RestoreWindow()
    {
        if (!_windowChanged && Screen.width == _windowWidth && Screen.height == _windowHeight)
        {
            yield break;
        }

        Screen.SetResolution(_windowWidth, _windowHeight, _windowMode);
        yield return Until(() => Screen.width == _windowWidth && Screen.height == _windowHeight, RestoreWindowSeconds, $"the window back at {_windowWidth}x{_windowHeight}");
        _windowChanged = false;
        if (Screen.width != _windowWidth || Screen.height != _windowHeight)
        {
            Line($"NOT RESTORED: the window is {Screen.width}x{Screen.height}, not {_windowWidth}x{_windowHeight}.");
        }
    }

    /// <summary>Back on the main group, by the game's own Back, from the Online group.</summary>
    private static IEnumerable<Wait> ToMain()
    {
        if ((int)Title().m_CurrentButtonGroup == 1)
        {
            yield break;
        }

        Navigation().OnBackInput();
        yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 3, "the main group");
    }

    private static IEnumerable<Wait> OpenShell(ShellKind kind)
    {
        if (!LobbyShell.RequestOpen(kind, new ShellOptions(), out string problem))
        {
            Problem(problem);
            yield break;
        }

        yield return Until(() => LobbyShell.IsShown || !LobbyShell.IsOpen, ShellSeconds, "the shell shown");
        if (!LobbyShell.IsShown)
        {
            Problem($"the shell didn't show{(LobbyShell.OffReason is { } off ? $" ({off})" : string.Empty)}");
        }
    }

    /// <summary>After a shell closes: the title's list empty, the fade cleared, the cursor back where it opened from.</summary>
    private static IEnumerable<Wait> AfterClose(string cursorWanted)
    {
        yield return Until(TitleIsBack, 3, "the title back");
        yield return Frames(8);
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        string on = ObjectName(Navigation().CurrentNavigable);
        StepNote(string.Create(CultureInfo.InvariantCulture, $"closed: its menu list {(title.m_MenusOverSelf?.Count ?? -1)} long, the fade {title.m_FadeToBlack?.alpha:0.##}, the cursor on {on}"));
        if (on != cursorWanted.Replace(" ", string.Empty, StringComparison.Ordinal))
        {
            Problem($"the game gave focus back to {on}, not {cursorWanted}");
        }
    }

    private static IEnumerable<Wait> AwaitAndHide(string what, string snap)
    {
        // The title's menu: shown over it, on top, not one of ours.
        yield return Until(() => Top() is { IsHidden: false } top && !LobbyShell.IsShellId(MenuGame.MenuId(top)) && Title().m_MenusOverSelf?.Count > 0, 8, $"the game's {what}");
        yield return Seconds(1);
        foreach (Wait wait in Snap(snap, 0))
        {
            yield return wait;
        }

        AbstractMenu? menu = Top();
        if (menu != null)
        {
            _ = menu.Hide();
        }

        yield return Until(TitleIsBack, 6, $"the title back after {what}");
    }

    private static IEnumerable<Wait> SetLanguage(string name, string code)
    {
        // As the game's SystemSettings.SetLanguage does: the fonts, then the language; here nothing is remembered (verified in the game's code).
        _languageChanged = true;
        Il2CppCysharp.Threading.Tasks.UniTask fonts = TmpFontRegistry.SetLanguageFonts(code);
        yield return Until(() => fonts.Status != Il2CppCysharp.Threading.Tasks.UniTaskStatus.Pending, 6, $"the fonts of {name}");
        LocalizationManager.SetLanguageAndCode(name, code, false, true);
        yield return Frames(4);
    }

    private static (string Name, string Code)? FindLanguage(string wanted)
    {
        Il2CppSystem.Collections.Generic.List<LanguageSourceData>? sources = LocalizationManager.Sources;
        Il2CppSystem.Collections.Generic.List<LanguageData>? languages = sources != null && sources.Count > 0 ? sources[0].mLanguages : null;
        for (int i = 0; languages != null && i < languages.Count; i++)
        {
            LanguageData language = languages[i];
            if (language.Name.StartsWith(wanted, StringComparison.Ordinal))
            {
                return (language.Name, language.Code);
            }
        }

        return null;
    }

    /// <summary>The text dump, then the screenshot, of what's on screen now (the top menu, else the title).</summary>
    private static IEnumerable<Wait> Snap(string name, int settleFrames)
    {
        if (settleFrames > 0)
        {
            yield return Frames(settleFrames);
        }

        Canvas.ForceUpdateCanvases();
        _captures++;
        string file = string.Create(CultureInfo.InvariantCulture, $"{_captures:000}_{_step?.Id ?? "x"}_{name}_{LocalizationManager.CurrentLanguageCode}_{Screen.width}x{Screen.height}");
        var text = new StringBuilder();
        var roots = new List<GameObject>();
        AbstractMenu? top = Top();
        if (top != null)
        {
            roots.Add(top.gameObject);
        }

        if (LobbyShell.Parts != null && LobbyShell.Menu != null && LobbyShell.Menu.Pointer != top?.Pointer)
        {
            roots.Add(LobbyShell.Parts.Root.gameObject);
        }

        // No menu up: the title, or in a match nothing to dump (the screenshot says).
        if (roots.Count == 0 && CampaignGame.Title() is { } title)
        {
            roots.Add(title.gameObject);
        }

        var defaults = new HashSet<IntPtr>();
        var hits = new List<UiCheckHit>();
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<AbstractNavigable>? shellDefaults = LobbyShell.Menu?.m_DefaultNavigables;
        for (int i = 0; shellDefaults != null && i < shellDefaults.Length; i++)
        {
            if (shellDefaults[i] != null)
            {
                defaults.Add(shellDefaults[i].Pointer);
            }
        }

        foreach (GameObject root in roots)
        {
            UiDump.Result read = UiDump.Read(root, defaults);
            text.Append(read.Text);
            hits.AddRange(read.Hits);
        }

        AllHits.AddRange(hits);
        NoteScreenFlags(name, hits);
        File.WriteAllText(Path.Combine(_folder, file + ".txt"), text.ToString());
        ScreenCapture.CaptureScreenshot(Path.Combine(_folder, file + ".png"));
        Line($"  screen {file}: flags {UiCheckRules.Counts(hits)}");
        yield return Frames(3);
    }

    private static bool TitleIsBack()
    {
        Il2CppTape2Tape.Hockey.UI.TitleScreen title = Title();
        return title.m_MenusOverSelf is { Count: 0 } && title.m_FadeToBlack != null && title.m_FadeToBlack.alpha < 0.02f;
    }

    private static List<string> MenusOver()
    {
        var names = new List<string>();
        Il2CppSystem.Collections.Generic.List<string>? over = Title().m_MenusOverSelf;
        for (int i = 0; over != null && i < over.Count; i++)
        {
            names.Add(over[i]);
        }

        return names;
    }

    private static List<AbstractNavigable> ActiveRows(NavigableParent parent)
    {
        var rows = new List<AbstractNavigable>();
        Transform top = parent.transform;
        for (int i = 0; i < top.childCount; i++)
        {
            GameObject child = top.GetChild(i).gameObject;
            AbstractNavigable? row = child.activeSelf ? child.GetComponent<AbstractNavigable>() : null;
            if (row != null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static Il2CppTape2Tape.Hockey.UI.TitleScreen Title()
    {
        if (_title == null || _title.WasCollected)
        {
            _title = CampaignGame.Title();
        }

        return _title ?? throw new InvalidOperationException("the title has gone");
    }

    private static NavigationController Navigation() =>
        MenuGame.Module()?.NavigationController ?? throw new InvalidOperationException("the game's navigation isn't there");

    private static AbstractMenu? Top() => LobbyShell.TopShown();

    // A row named after a Steam ID or a lobby ID is written as the row it is (the tour's notes go in the log).
    private static string ObjectName(AbstractNavigable? navigable) => navigable == null ? "nothing" : UiObjectName.ForLog(navigable.gameObject.name);

    private static double MsSince(long stamp) => (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Where <paramref name="ex"/> was thrown, as the method's type and name only (a step's iterator reads as its own
    /// <c>&lt;StepX&gt;d__N.MoveNext</c>): no file, no line, no message, so nothing a log line mustn't hold. Never throws.
    /// </summary>
    private static string ThrownIn(Exception ex)
    {
        try
        {
            System.Reflection.MethodBase? method = new StackTrace(ex, false).GetFrame(0)?.GetMethod();
            return method == null ? "a place the stack doesn't name" : $"{method.DeclaringType?.Name}.{method.Name}";
        }
        catch (Exception)
        {
            return "a place the stack doesn't name";
        }
    }

    /// <summary>The title screen's scene is loaded (no match, no world map); false when it can't be read (then the tour quits with its holds).</summary>
    private static bool AtTitleNow()
    {
        try
        {
            return CampaignGame.TitleLoaded();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AnyPress()
    {
        ReInput.ControllerHelper controllers = ReInput.controllers;
        if (controllers.Keyboard.GetAnyButtonDown() || controllers.Mouse.GetAnyButtonDown())
        {
            return true;
        }

        Il2CppSystem.Collections.Generic.IList<Joystick>? joysticks = controllers.Joysticks;
        int count = joysticks == null ? 0 : controllers.joystickCount;
        for (int i = 0; i < count; i++)
        {
            if (joysticks![i].GetAnyButtonDown())
            {
                return true;
            }
        }

        return false;
    }

    private static void Problem(string why)
    {
        StepNote($"PROBLEM: {why}");
        _stepProblem ??= why;
    }

    private static void StepNote(string note) => Line($"  {note}");

    private static void Line(string line)
    {
        Summary.Append(line).Append('\n');
        if (line.StartsWith("  ", StringComparison.Ordinal))
        {
            ModLog.Info($"Lobby check: {line.TrimStart()}");
        }
    }

    private static void Flush()
    {
        try
        {
            if (_folder.Length > 0)
            {
                File.WriteAllText(Path.Combine(_folder, "summary.txt"), Summary.ToString());
                FlushFlags();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Lobby check: summary.txt couldn't be written ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void ReportGuard()
    {
        var names = new List<string>();
        foreach (KeyValuePair<(System.Reflection.MethodBase Method, string Files), long> refused in SaveGuard.RefusedByFilesSoFar())
        {
            names.Add($"{refused.Key.Method.Name}({refused.Key.Files}) {refused.Value}x");
        }

        Line(names.Count == 0 ? "Save guard: nothing tried to write." : $"Save guard: refused {string.Join(", ", names)}.");
    }

    // ---- Waits ----

    private static Wait Frames(int count) => new() { UntilFrame = FrameClock.Frame + count, UntilTime = FrameClock.Seconds + (count / MinFramesPerSecond), What = $"{count} frames" }; // the same wait at 13 fps and at 160

    private static Wait Seconds(double seconds) => new() { UntilTime = FrameClock.Seconds + seconds, What = string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s") };

    private static Wait Until(Func<bool> condition, double timeout, string what) => new() { Condition = condition, UntilTime = FrameClock.Seconds + timeout, What = what };

    /// <summary>As <see cref="Until"/>, and running out isn't a problem by itself: what comes after says what was wanted.</summary>
    private static Wait Soft(Func<bool> condition, double timeout, string what) => new() { Condition = condition, UntilTime = FrameClock.Seconds + timeout, What = what, IsSoft = true };

    /// <summary>What a step waits for. A condition's timeout is said in the summary, and the step goes on.</summary>
    private sealed class Wait
    {
        public int UntilFrame { get; init; }

        public double UntilTime { get; init; }

        public Func<bool>? Condition { get; init; }

        public string What { get; init; } = string.Empty;

        public bool IsSoft { get; init; }

        public bool Late { get; private set; }

        public bool Over()
        {
            if (Condition != null)
            {
                bool met;
                try
                {
                    met = Condition();
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"Lobby check: a wait's condition threw ({ModLog.SafeErrorText(ex)}).");
                    met = false;
                }

                if (met)
                {
                    return true;
                }

                Late = FrameClock.Seconds >= UntilTime;
                return Late;
            }

            return FrameClock.Frame >= UntilFrame && FrameClock.Seconds >= UntilTime;
        }
    }

    private enum Phase
    {
        Off,
        Armed,
        Running,
        Restoring,
        Done,
    }
}
#endif
