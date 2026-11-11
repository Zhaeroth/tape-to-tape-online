#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2CppBishopGames.UI;
using Il2CppRogue.Rewards;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Stream;
using T2TOnline.Core.Timing;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the menu views (D43): each menu open on the host's screen is shown here, the real menu where it can
/// be rebuilt safely, the spectator panel otherwise.
/// <list type="bullet">
/// <item>Real views (the reward menus, the squad menu and the run-end menu): opened with the game's own
/// <c>UIModule.ShowMenuAsync&lt;T&gt;</c> (called with the concrete menu class; the generic is called, never patched) and
/// the data <see cref="MenuRebuild"/> makes, only while this PC's scene is the host's and, for rewards and the map's "Run
/// stats", while it holds the host's run (the squad menu: while this PC's saves are held, since it writes the profile;
/// the run's end: once the host's run summary is here, <see cref="RunEndView"/>). The host's selection, list selectors,
/// menu state, sort and rerolled offers are applied to it; clicks show as a button press. It closes with the base close
/// (<c>Hide_Internal</c>), so a menu's own close code (which raises the end-of-reward events) never runs here; the
/// transition the host's menu exit showed shows here too, in the game's order (<see cref="FriendTransitions"/>). If it can't
/// open (no run, another scene, an offer this game lacks, no run summary, the generic call failing or not opening in
/// time), the panel shows instead and says why; one that opens without the data asked for, or after the host closed it
/// (even after the session), is closed once it's up.</item>
/// <item>The in-match menu (pause, end of period, end of game) and the glossary are real views too, while this PC shows
/// the host's match (the in-match menu) or its scene (the glossary): the in-match menu with the host's numbers, its
/// tabs and its cursor (<see cref="GameMenuViews"/>), the glossary with its tab and entry (its rows aren't
/// highlighted). A glossary opens only once the host's has been up a moment (the host's title screen opens and closes
/// it at once), and the in-match menu under it is hidden meanwhile, as on the host's screen (as it is under every overlay
/// it opens, FriendMenus.Overlays.cs). The controller screen is a real view too, the campaign's (FriendMenus.Controllers.cs) and
/// Play Now's (FriendMenus.Exhibition.cs).</item>
/// <item>The event menu is a real view for the Coach, the trainings, Chaos, the Blademaster, Fan Number 1, the General
/// Manager and the Nurse (<see cref="EventViews"/>): it opens once the host's choices are here (an attachment after its
/// open), with the game's own choices built from them in place of the event's roll (Fan Number 1's previews and the General
/// Manager's free agents made here as the game makes them), and follows the host's intro, the NPC's lines (the same line
/// played here, in this PC's language, <see cref="EventSpeech"/>, each advancing with the host's presses,
/// <see cref="SpeechMirror"/>; the cards come once the host's are in), map peek, details, cursor, pick, pages and rerolls. When
/// the host's Nurse takes the screen over, the game's own procedure screen opens over it here and follows the host's steps
/// (FriendMenus.Nurse.cs), or its cards go and the host's procedure screen shows as text over it
/// (<see cref="NurseScreen"/>; as text over the panel too when the event itself is text).</item>
/// <item>Prompts (<see cref="PromptViews"/>, from the host's prompt data, in this PC's language when it came as terms), the
/// lineup (<see cref="LineupViews"/>, on this PC's copy of the team, following the host's swaps), the map's own menu, the
/// Canada curse (<see cref="CanadaViews"/>, its curses made here) and the new-unlocks menu (<see cref="UnlockViews"/>, with
/// this player's own new unlocks; a note when there are none) are real views too.</item>
/// <item>The old squad menu (the superstar pick, the old school draft) is a real view too, once this PC holds the host's new
/// run (its run-start preview) and has built the host's offers (FriendMenus.RunStart.cs); so is the lineup with
/// benchwarmers, with the host's benchwarmers on its cards, following the host's swaps and toggle
/// (FriendMenus.Bench.cs).</item>
/// <item>The panel (<see cref="MenuPanel"/>): every other menu, as the host's text, with the host's cursor (the controller
/// screen and the lineup with benchwarmers when they can't be shown for real: a line per player, with "you" on this
/// player's own and each player's colour; its lineup, bench and benchwarmers). The host's unlockables and run history are
/// real views too (FriendMenus.Title.cs: this PC's own menu with the host's unlock states, the host's history pages); a
/// note says why when one can't be shown. The host's settings show only a note: they're the host's own.</item>
/// </list>
/// Only the mod's own selection, value, step and click-feedback changes run inside <see cref="FriendUi.Apply"/>, past the
/// friend's input lock; each is guarded, and a view that keeps failing shows as text. Nothing here throws into the game:
/// <see cref="MenuViews"/> guards every entry.
/// </summary>
internal sealed partial class FriendMenus
{
    private const double OpenTimeoutSeconds = 6;
    private const double PendingPanelSeconds = 1;
    private const double SceneRetrySeconds = 0.5;
    private const double PressSeconds = 0.25;
    private const double OrphanSeconds = 30;
    private const double LateOpenSeconds = 10;
    private const int MaxOrphans = 8;
    private const int MaxPending = 3;
    private const int MaxTimeouts = 2;
    private const int MaxViewFailures = 3;
    private const int MaxLoggedMisses = 5;
    private const int MaxLoggedFailures = 5;
    private const int LinesPerMinute = 60;
    private const double RunWaitSeconds = 15;
    private const double SummaryWaitSeconds = 5;
    private const int DressEveryFrames = 10;
    private const int MaxDressLogs = 3;
    private const double GlossaryDelaySeconds = 0.3;
    private const string OtherScene = "your game is on another screen";
    private const string NoHostMatch = "your game isn't showing the host's match";
    private const string MatchFailedHere = "your game couldn't load the host's match";
    private const string RunNotYet = "your game is still loading the host's newest run";
    private const string SummaryNotYet = "the host's run summary is still on its way";
    private const string EventNotYet = "the host's event choices are still on their way";
    private const string PromptNotYet = "the host's prompt data is still on its way";
    private const double IntroWaitSeconds = 8;
    private const double NextSetWaitSeconds = 5;
    private const double SwapSeconds = 4;
    private const double UnlockWaitSeconds = 3;
    private const string WorldMapType = "WorldMapMenu";
    private const int LineupEveryFrames = 6;
    private const int LineupReadyFrames = 600;

    private static readonly HashSet<string> RealTypes = new(StringComparer.Ordinal)
    {
        MenuPayloads.RelicReward, MenuPayloads.PowerUpReward, MenuPayloads.ChooseMeta, MenuPayloads.RunEnd, MenuPayloads.Game, MenuPayloads.Glossary,
        MenuPayloads.EventChoice, MenuPayloads.PermanentUnlock, LineupViews.Type, WorldMapType, CanadaViews.Type, RunStartCapture.OldSquad,
        RunStartCapture.Benchwarmer, PromptViews.YesNo, PromptViews.Dismissable, PromptViews.LineupInvalid, PromptViews.BenchwarmerInvalid, PromptViews.BenchwarmerExplanation,
        UnlockablesView.Type,
        ControllerViews.Type,
        BrawlViews.Type,
    };

    private static readonly int[] ReselectFrames = { 3, 20 };

    // The new-unlocks menu's step, as the host's menu sends it (UnlockViews.HostStepped): made once, asked for every frame.
    private static readonly MenuTarget UnlockStep = MenuTarget.AtItem(UnlockViews.StepGroup, 0);

    private readonly MenuMirror _mirror;
    private readonly MatchRelay _matches;
    private readonly NoticeSink _notify;
    private readonly Dictionary<uint, View> _views = new();

    // The host's attachments, the newest of each kind for each open menu (dropped when it closes).
    private readonly MenuAttachmentStore _attachments = new();
    private readonly List<Orphan> _orphans = new();
    private readonly List<Late> _late = new();
    private readonly HashSet<string> _broken = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _timeouts = new(StringComparer.Ordinal);

    // The host's details holds followed here (DetailsHolds): the kinds of menu whose first one was logged, the count per
    // kind (logged when following stops), and the kinds where one didn't reach the game's details state (warned once).
    private readonly HashSet<string> _holdKinds = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _holdsFollowed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _holdMisses = new(StringComparer.Ordinal);

    // The host's details panels' detailed stats followed here (DetailsHolds.FollowDetailedStats): the kinds of menu whose
    // first switch was logged, and the switches per kind (logged when following stops).
    private readonly HashSet<string> _detailedKinds = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _detailedFollowed = new(StringComparer.Ordinal);
    private string _host = string.Empty;
    private ulong _self;
    private bool _panelDirty;
    private uint _pressedSerial;
    private int _pressedRow = -1;
    private double _pressedUntil;
    private int _misses;
    private int _failures;
    private readonly LineBudget _lines = new("Menus (this PC)", LinesPerMinute);

    /// <summary>
    /// A friend's side over <paramref name="mirror"/> (the host's open menus); <paramref name="matches"/> says whether this
    /// PC shows the host's match; <paramref name="notify"/> tells the player something (a notice).
    /// </summary>
    public FriendMenus(MenuMirror mirror, MatchRelay matches, NoticeSink notify)
    {
        _mirror = mirror;
        _matches = matches;
        _notify = notify;
    }

    private enum Mode
    {
        Panel,
        Pending,
        Real,
        Note,
    }

    /// <summary>A view's own exit (the old squad menu's, at the host's exit step).</summary>
    private enum OwnExitState
    {
        None,
        Armed,
        Running,
        Hidden,
    }

    /// <summary>
    /// True while a menu this PC asked to open for the host may still open after the host closed it (or the session ended),
    /// or one that did is waiting to be closed: the patches and <see cref="Sweep"/> keep watching until then.
    /// </summary>
    public bool Expecting => _orphans.Count > 0 || _late.Count > 0;

    /// <summary>True while any of the host's menus is followed here (real, a note, text or still opening): a friend's map isn't bare then.</summary>
    public bool AnyHostMenu => _views.Count > 0 || _orphans.Count > 0 || _late.Count > 0;

    /// <summary>True while this PC asked the game to open a menu of class <paramref name="type"/> for the host that hasn't got its data yet.</summary>
    public bool WaitingFor(string type) => _orphans.Any(o => o.Type == type) || _views.Values.Any(v => v.Request != null && v.Type == type);

    /// <summary>True while a host menu is still opening here (asked for, or waiting for the host's data or run): the screen isn't settled.</summary>
    public bool Opening => _views.Values.Any(v => v.Mode == Mode.Pending || v.RunStart is { Waiting: true } || v.WaitRunUntil > 0 || v.WaitEventUntil > 0 ||
        v.WaitPromptUntil > 0 || v.WaitSummaryUntil > 0 || v.WaitUnlocksUntil > 0 || v.WaitTitleUntil > 0);

    /// <summary>
    /// True once the host's game has shown its run's end this following (its run-end menu at the run's end, or its new-unlocks
    /// menu): the run is over, whatever this PC's game showed of it.
    /// </summary>
    public bool HostRunOver { get; private set; }

    /// <summary>Following <paramref name="host"/> started.</summary>
    public void Start(string host)
    {
        foreach (View view in _views.Values)
        {
            Abandon(view);
            CloseReal(view);
        }

        _views.Clear();
        _attachments.Clear();
        _broken.Clear(); // a kind of menu given up on last time (a slow first load, say) is tried again
        _timeouts.Clear();
        _notified.Clear();
        TallyStarted();
        EventSpeech.ClearFriend();
        GlossaryScrolls.Clear();
        TipViews.StopFriend(gameClosing: false);
        WorldMap.ClearStaleRewards();
        UnlockViews.Reset();
        ControllerViews.ClearFriend();
        OverlaysStopped(gameClosing: false);
        _host = SafeText.Clean(host, 32);
        HostRunOver = false;
        OwnUnlocks.Start();
        SpeechReset(starting: true, gameClosing: false);
        _self = T2TOnline.Steam.SteamRuntime.LocalSteamId; // the controller screen's "you"
        FriendTransitions.Start();
        RewardFade.Forget();
        SoundsStarted();
        RunStartStarted();
        _holdKinds.Clear();
        _holdsFollowed.Clear();
        _holdMisses.Clear();
        _detailedKinds.Clear();
        _detailedFollowed.Clear();
        _misses = 0;
        _failures = 0;
        _pressedRow = -1;
        _panelDirty = true;
    }

    /// <summary>
    /// Following stopped: every real view closes and the panel goes. A menu still opening is closed once it's up
    /// (<see cref="Sweep"/>). <paramref name="gameClosing"/>: no game calls, just forget.
    /// </summary>
    public void Stop(string reason, bool gameClosing)
    {
        // This player's own new-unlocks screen first: its device and lock go back while the session can still take them.
        OwnUnlocks.Stop(reason, gameClosing);
        int closed = 0;
        foreach (View view in _views.Values)
        {
            Abandon(view);
            if (!gameClosing && CloseReal(view))
            {
                closed++;
            }
        }

        if (closed > 0)
        {
            ModLog.Info($"Menus: closed {closed} of {LogNames.HostTag()}'s menus here ({reason}).");
        }

        if (_holdsFollowed.Count > 0)
        {
            ModLog.Info($"Menus: {LogNames.HostTag()}'s details holds were followed here {_holdsFollowed.Values.Sum()} time(s) " +
                $"({string.Join(", ", _holdsFollowed.Select(k => $"{k.Key} {k.Value}"))}).");
            _holdsFollowed.Clear();
        }

        if (_detailedFollowed.Count > 0)
        {
            ModLog.Info($"Menus: {LogNames.HostTag()}'s detailed stats (\"UI Left Trigger\" held) were followed here {_detailedFollowed.Values.Sum()} time(s) " +
                $"({string.Join(", ", _detailedFollowed.Select(k => $"{k.Key} {k.Value}"))}).");
            _detailedFollowed.Clear();
        }

        TallyStopped(reason);
        _views.Clear();
        _attachments.Clear();
        if (!gameClosing)
        {
            EventSpeech.ClearFriend();
        }

        SpeechReset(starting: false, gameClosing);
        TipViews.StopFriend(gameClosing);

        WorldMap.ClearStaleRewards();
        UnlockViews.Reset();
        ControllerViews.ClearFriend();
        OverlaysStopped(gameClosing);
        if (!gameClosing)
        {
            BrawlGone();
        }

        RunStartStopped(gameClosing);
        FriendTransitions.Stop(gameClosing);
        RewardFadeStopped(gameClosing);
        _pressedRow = -1;
        if (gameClosing)
        {
            _orphans.Clear();
            _late.Clear();
        }
        else
        {
            // Not following any more, no event takes the host's choices: a plan still armed (its menu never opened) goes,
            // with what it made here.
            EventViews.Disarm();
            UnlockablesView.Disarm();
        }

        MenuPanel.Clear();
    }

    /// <summary>The mirror took <paramref name="menuEvent"/> as <paramref name="change"/>: show it.</summary>
    public void Apply(MenuMirrorChange change, MenuEvent menuEvent)
    {
        _panelDirty = true;
        _mirror.TryGet(menuEvent.Serial, out MirroredMenu menu);
        if (TipViews.Mirror(change, menuEvent, menu, _mirror))
        {
            return; // the host's campaign tip: not a menu (v1)
        }

        _views.TryGetValue(menuEvent.Serial, out View? view);
        if (change is MenuMirrorChange.Closed or MenuMirrorChange.Synced)
        {
            // What went with a menu goes with it (a menu sent again on a rejoin keeps it: the host sends it again too).
            _attachments.RemoveWhere(serial => !_mirror.TryGet(serial, out _));
        }

        switch (change)
        {
            case MenuMirrorChange.Opened:
                if (view != null)
                {
                    Close(view, replaced: true);
                }

                Open(menu);
                break;
            case MenuMirrorChange.Refreshed when view != null && (view.Mode is Mode.Real or Mode.Pending) && view.OfferKey == OfferKey(menu.State.Data):
                ApplyAll(view, menu);
                break;
            case MenuMirrorChange.Refreshed:
                // The host sent a menu it still shows again (this PC rejoined): shown again from its newest data.
                if (view != null)
                {
                    Close(view, replaced: true);
                }

                Open(menu);
                break;
            case MenuMirrorChange.Updated when view != null:
                Updated(view, menu);
                break;
            case MenuMirrorChange.Closed when view != null:
                Close(view, menuEvent);
                break;
            case MenuMirrorChange.Selected when view?.Mode == Mode.Real:
                FollowNavModeOf(view, menu); // this PC's own mouse or pad may have moved it since
                ApplySelection(view, menu);
                break;
            case MenuMirrorChange.ValueSet when view != null && Sounded(view, menuEvent.Target, menuEvent.Index):
                break; // a sound the host's game played (a shortcut's press, its back sound): played here once, never again
            case MenuMirrorChange.ValueSet when view?.Mode == Mode.Real:
                if (!ControllerFeedbackStep(view, menuEvent.Target, menuEvent.Index))
                {
                    ApplyValue(view, menu, menuEvent.Target, menuEvent.Index);
                }

                break;
            case MenuMirrorChange.Clicked when view != null:
                Pressed(view, menu, menuEvent);
                break;
            case MenuMirrorChange.Synced:
                _syncedAt = FrameClock.Seconds; // what follows a join replays the host's menus: no sounds from it
                Synced();
                break;
        }
    }

    /// <summary>Every frame while following.</summary>
    public void Update()
    {
        double now = FrameClock.Seconds;
        TipViews.FriendTick(); // the host's campaign tip (v1)
        OwnUnlocks.Update(); // this player's own new-unlocks screen at a run's end the host's game shows none for
        foreach (View view in _views.Values)
        {
            if (!_mirror.TryGet(view.Serial, out MirroredMenu menu))
            {
                continue;
            }

            switch (view.Mode)
            {
                case Mode.Pending when view.OpenAt > 0:
                    if (now >= view.OpenAt && view.Type == ControllerViews.Type && now - view.RequestedAt < OpenTimeoutSeconds && ExhibitionScreen.PreparingTitleMenus())
                    {
                        view.OpenAt = now + SceneRetrySeconds; // this PC's own title warm-ups still use its copy
                    }
                    else if (now >= view.OpenAt)
                    {
                        // Still open on the host's screen after a moment (not one of its title screen's warm-ups): open it here.
                        view.OpenAt = 0;
                        TryReal(view, menu);
                    }

                    break;
                case Mode.Pending when now - view.RequestedAt > OpenTimeoutSeconds:
                    TimedOut(view);
                    break;
                case Mode.Pending when !view.PanelShown && now - view.RequestedAt > PendingPanelSeconds:
                    view.PanelShown = true; // still opening: the host's screen as text meanwhile
                    _panelDirty = true;
                    break;
                case Mode.Real when view.Menu == null && view.OwnExit == OwnExitState.Hidden:
                    break; // its own exit hid it (the old squad menu): it waits for the host's close
                case Mode.Real when view.Menu == null:
                    view.Menu = null;
                    ToPanel(view, "your game closed it");
                    Log($"Menus: {view.LogId} closed on this PC while still open on {LogNames.HostTag()}'s; showing it as text.");
                    break;
                case Mode.Real when view.Menu is { } drawn && GameMenuViews.TakeFailure(drawn, out string failure):
                    // The host's numbers couldn't be drawn into it (logged): the host's screen as text instead.
                    CloseReal(view);
                    ToPanel(view, failure);
                    break;
                case Mode.Real when view.Event is { Failure.Length: > 0 } broken:
                    // The host's choices couldn't be put into the event as it opened (logged): the host's screen as text.
                    ModLog.Warn($"Menus: {view.LogId} can't show {LogNames.HostTag()}'s choices here ({broken.Failure}); showing it as text.");
                    CloseReal(view);
                    ToPanel(view, broken.Failure);
                    break;
                case Mode.Real:
                    Tick(view, menu);
                    break;
                case Mode.Panel when view.RunStart is { Waiting: true }:
                    RunStartWaitTick(view, menu);
                    break;
                case Mode.Panel when view.WaitRunUntil > 0 && now >= view.RetryAt:
                    view.RetryAt = now + SceneRetrySeconds;
                    if (now > view.WaitRunUntil)
                    {
                        view.WaitRunUntil = 0;
                        ToPanel(view, "your game didn't get the host's run this menu was made from in time");
                        ModLog.Warn($"Menus: {view.LogId} needs {LogNames.HostTag()}'s run {NeededRun(menu.State)}, but this PC holds {RunSync.Instance?.AppliedCheckpoint ?? 0} " +
                            $"after {RunWaitSeconds:0} s; showing it as text.");
                    }
                    else
                    {
                        TryReal(view, menu);
                    }

                    break;
                case Mode.Panel when view.WaitEventUntil > 0 && now > view.WaitEventUntil:
                    // It opens as soon as the choices come (Attached); this is only the giving up.
                    view.WaitEventUntil = 0;
                    ToPanel(view, "the host's event choices didn't come");
                    ModLog.Warn($"Menus: {view.LogId} waited {SummaryWaitSeconds:0} s for {LogNames.HostTag()}'s event choices, which didn't come; showing it as text.");
                    break;
                case Mode.Panel when view.WaitPromptUntil > 0 && now > view.WaitPromptUntil:
                    // It opens as soon as the prompt's data comes (Attached); this is only the giving up.
                    view.WaitPromptUntil = 0;
                    ToPanel(view, "the host's prompt data didn't come");
                    ModLog.Warn($"Menus: {view.LogId} waited {SummaryWaitSeconds:0} s for {LogNames.HostTag()}'s prompt data, which didn't come; showing it as text.");
                    break;
                case Mode.Panel when view.WaitSummaryUntil > 0 && now > view.WaitSummaryUntil:
                    // It opens as soon as the summary comes (Attached); this is only the giving up.
                    view.WaitSummaryUntil = 0;
                    ToPanel(view, "the host's run summary didn't come");
                    ModLog.Warn($"Menus: {view.LogId} waited {SummaryWaitSeconds:0} s for {LogNames.HostTag()}'s run summary, which didn't come; showing it as text.");
                    break;
                case Mode.Panel when view.WaitTitleUntil > 0 && now > view.WaitTitleUntil:
                    // It opens as soon as the host's unlock states or history page come (Attached); this is only the giving up.
                    TitleWaitOver(view);
                    break;
                case Mode.Panel when view.WaitUnlocksUntil > 0 && now >= view.RetryAt:
                    view.RetryAt = now + SceneRetrySeconds;
                    OpenUnlocks(view, menu);
                    break;
                case Mode.Panel when view.WaitScene && now >= view.RetryAt:
                    view.RetryAt = now + SceneRetrySeconds;
                    if ((menu.State.Scene == MenuGame.SceneName() || RunEndHereAnyway(menu.State)) && Blocker(menu.State) != NoHostMatch)
                    {
                        TryReal(view, menu);
                    }

                    break;
            }
        }

        RewardFadeTick(now);
        SpeechTick();
        CoverUnderOverlays();
        QuietTick();
        ControllersTick();
        RunStartTick();
        Sweep();
        if (_pressedRow >= 0 && now >= _pressedUntil)
        {
            _pressedRow = -1;
            _panelDirty = true;
        }

        if (_panelDirty)
        {
            _panelDirty = false;
            RefreshPanel();
        }
    }

    /// <summary>
    /// The host sent what goes with one of its open menus (a run-end menu's run summary, an event menu's choices or its NPC's
    /// line, a prompt's data, an old squad menu's run start data): kept while that menu is open, the newest of each kind (<see cref="Attachment"/>), and a view
    /// waiting for it opens now.
    /// </summary>
    public void Attached(MenuAttachment attachment)
    {
        if (!_mirror.TryGet(attachment.Serial, out MirroredMenu menu))
        {
            Log($"Menus: {LogNames.HostTag()}'s {attachment} came for a menu that isn't open here (it closed meanwhile); dropped.");
            return;
        }

        _attachments.Put(attachment);
        EventChoiceSet? set = null;
        string about = attachment.Kind switch
        {
            MenuAttachmentKind.RunSummary when attachment.TryGetRunSummary(out RunHistoryData? summary) => $", run {HostText.KeyForLog(summary!.RunId)}",
            MenuAttachmentKind.EventChoices when attachment.TryGetEventChoices(out set) => $": {set}",
            MenuAttachmentKind.NpcLine when attachment.TryGetNpcLine(out NpcLineData? line) => $": {line}",
            MenuAttachmentKind.Prompt when attachment.TryGetPrompt(out PromptData? prompt) => $": {ShownTextLog.Prompt(prompt!, LogNames.TextShape)}",
            MenuAttachmentKind.RunPreview when attachment.TryGetRunPreview(out RunPreviewSet? preview) => $": {preview}",
            MenuAttachmentKind.UnlockStates when attachment.TryGetUnlockStates(out UnlockStatesData? states) => $": {states}",
            MenuAttachmentKind.HistoryPage when attachment.TryGetHistoryPage(out HistoryPageData? page) => $": {page}",
            MenuAttachmentKind.NodeRewards when attachment.TryGetNodeRewards(out NodeRewardsData? rewards) => $": {rewards}",
            MenuAttachmentKind.NurseRoster when attachment.TryGetNurseRoster(out NurseRosterData? roster) => $": {roster}",
            _ => string.Empty,
        };
        if (attachment.Kind != MenuAttachmentKind.NurseSteps)
        {
            // The Nurse's steps come with each press on the host's procedure screen: each is logged as it's replayed.
            Log($"Menus: got {LogNames.HostTag()}'s {attachment} ({HostText.KeyForLog(menu.State.Id)}{about}).");
        }

        if (attachment.Kind == MenuAttachmentKind.NodeRewards && attachment.TryGetNodeRewards(out NodeRewardsData? rerolled) && rerolled != null)
        {
            // The host's General Manager rerolled the map's node rewards: this PC's map shows them at once.
            NodeRewardsMirror.Arrived(rerolled);
        }

        if (attachment.Kind == MenuAttachmentKind.NpcLine && attachment.TryGetNpcLine(out NpcLineData? npcLine) && npcLine != null)
        {
            // Played once this PC's event screen is at its NPC's lines (EventTick).
            EventSpeech.Arrived(attachment.Serial, npcLine);
        }

        if (!_views.TryGetValue(attachment.Serial, out View? view))
        {
            return;
        }

        TitleAttached(view, menu, attachment);
        // The Nurse's procedure screen steps and the host's commit's outcome (FriendMenus.Nurse.cs).
        NurseAttached(view, attachment);

        if (attachment.Kind == MenuAttachmentKind.RunSummary && view.WaitSummaryUntil > 0 && view.Mode == Mode.Panel)
        {
            view.WaitSummaryUntil = 0;
            TryReal(view, menu);
        }
        else if ((attachment.Kind is MenuAttachmentKind.EventChoices or MenuAttachmentKind.NurseRoster) && view.WaitEventUntil > 0 && view.Mode == Mode.Panel)
        {
            view.WaitEventUntil = 0;
            TryReal(view, menu);
        }
        else if (attachment.Kind == MenuAttachmentKind.Prompt && view.WaitPromptUntil > 0 && view.Mode == Mode.Panel)
        {
            view.WaitPromptUntil = 0;
            TryReal(view, menu);
        }
        else if (attachment.Kind == MenuAttachmentKind.RunPreview && view.RunStart is { WaitDataUntil: > 0 } && view.Mode == Mode.Panel)
        {
            view.RunStart.WaitDataUntil = 0;
            TryReal(view, menu);
        }
        else if (set != null && view.Event != null && (view.Mode is Mode.Real or Mode.Pending) && set.Version > view.SetVersion)
        {
            // The host's next choices (a page or a reroll): shown once this PC's menu is ready for them (Tick).
            view.NextSet = set;
            view.NextSetSince = FrameClock.Seconds;
        }

        // Prompts are kept for the views that show them (they read them with Attachment).
    }

    /// <summary>The host's newest attachment of <paramref name="kind"/> for its open menu <paramref name="serial"/>, or null.</summary>
    public MenuAttachment? Attachment(uint serial, MenuAttachmentKind kind) => _attachments.Get(serial, kind);

    /// <summary>
    /// Closes the menus that opened here for a host menu that was already gone, once they're up (a menu can't be closed
    /// before it has shown); forgets requests too old to still open. Every frame, session or not.
    /// </summary>
    public void Sweep()
    {
        double now = FrameClock.Seconds;
        for (int i = _orphans.Count - 1; i >= 0; i--)
        {
            if (now - _orphans[i].Since > OrphanSeconds)
            {
                _orphans.RemoveAt(i);
            }
        }

        for (int i = _late.Count - 1; i >= 0; i--)
        {
            Late late = _late[i];
            if (late.Menu == null || late.Menu.IsHiding)
            {
                _late.RemoveAt(i); // gone, or closing already
            }
            else if (!late.Menu.IsHidden)
            {
                _late.RemoveAt(i);
                if (CloseMenu(late.Menu, late.Id))
                {
                    _tally.Late();
                    Log($"Menus: closed {HostText.KeyForLog(late.Id)} here: it opened after {LogNames.HostTag()} had closed it.");
                }
            }
            else if (now - late.Since > LateOpenSeconds)
            {
                _late.RemoveAt(i);
                ModLog.Warn($"Menus: {HostText.KeyForLog(late.Id)} opened here for a host menu that's gone but never finished opening within {LateOpenSeconds:0} s; left to the game.");
            }
        }
    }

    /// <summary>
    /// A menu on this PC got its data (its <c>InitData</c>): if it's one this PC asked to open, it's that view from now on;
    /// if the host's menu closed before it opened, it's closed once it's up.
    /// </summary>
    public void Bound(AbstractMenu menu, Il2CppSystem.Object? data)
    {
        if (data == null || OwnUnlocks.Bound(menu, data))
        {
            return;
        }

        int orphan = _orphans.FindIndex(o => o.Request.Pointer == data.Pointer);
        if (orphan >= 0)
        {
            if (_orphans[orphan].Type == MenuPayloads.Game)
            {
                // Drawn safely (no pause or period flow) until it's closed, without the host's numbers.
                GameMenuViews.Mark(menu, null);
            }

            _orphans.RemoveAt(orphan);
            CloseWhenUp(menu);
            return;
        }

        View? view = _views.Values.FirstOrDefault(v => v.Request != null && v.Request.Pointer == data.Pointer);
        if (view != null)
        {
            Bind(view, menu);
        }
    }

    /// <summary>True when <paramref name="menu"/> is one this PC shows for the host (or opened for one that's gone): its own actions are refused.</summary>
    public bool Shows(AbstractMenu menu) => IsOurs(menu);

    /// <summary>The menu <paramref name="view"/> asked to open is up with its data: it's that view from now on.</summary>
    private void Bind(View view, AbstractMenu menu)
    {
        double waited = FrameClock.Seconds - view.RequestedAt;
        if (view.Type == MenuPayloads.Game)
        {
            GameMenuViews.Mark(menu, view.Plan);
        }

        view.Menu = menu;
        view.Mode = Mode.Real;
        view.Request = null;
        BindRunStart(view, menu);
        BindControllers(view, menu);
        BindSpeech(view, menu);
        view.Why = string.Empty;
        view.Frame = 0;
        view.ReselectFrame = -1;
        view.Failures = 0;
        view.BoundAt = FrameClock.Seconds;
        view.IntroSeen = false;
        view.IntroOver = false;
        view.IntroSkipped = false;
        view.Swapping = false;
        view.Overlay = false;
        view.CardsHidden = false;
        view.GoodbyeSaid = false;
        _panelDirty = true;
        Log($"Menus: showing {view.LogId} as on {LogNames.HostTag()}'s screen (opened in {waited * 1000:0} ms).");
        TallyBound(view);

        // The host's game takes its on or back transition down as its next menu opens (TitleScreen.StartNewRun: not on a friend).
        FriendTransitions.NextScreen();
    }

    /// <summary>
    /// A menu on this PC is showing (<c>T2TMenu.Show</c>, after its data): a real-view class this PC asked for that didn't
    /// get the data asked for (another instance, or one opened without it) can't show the host's offers, so it's closed
    /// once it's up; the view keeps waiting for its own and shows as text if that doesn't come. The game's own menus are
    /// left alone: once this PC no longer <paramref name="following"/>, a class with data of its own that didn't bind by
    /// that data (<see cref="Bound"/>) is one (a request still waiting then is one that never opened).
    /// </summary>
    public void Showing(AbstractMenu menu, bool following)
    {
        if (IsOurs(menu))
        {
            return;
        }

        string type = MenuGame.TypeName(menu);
        if (BindsAtShow(type) && _views.Values.FirstOrDefault(v => v.Request != null && v.Type == type) is { } waiting)
        {
            // It takes no data of its own (no InitData): the copy this PC asked for is the one that shows.
            Bind(waiting, menu);
            return;
        }

        if (!RealTypes.Contains(type) || type is MenuPayloads.Game or MenuPayloads.Glossary)
        {
            // The in-match menu and the glossary always get the data asked for (the game's own copy shown again is
            // given it too); one shown without is this game's own, and stays.
            return;
        }

        if (!following && !BindsAtShow(type))
        {
            // After the session (a timed-out request's orphan waits up to 30 s), a late copy of the host's menu has bound by
            // its data already: this one is this player's own (their squad screen, title warm-up, run history...).
            return;
        }

        int orphan = _orphans.FindIndex(o => o.Type == type);
        bool asked = orphan >= 0 || _views.Values.Any(v => v.Request != null && v.Type == type);
        if (!asked)
        {
            return;
        }

        if (orphan >= 0)
        {
            _orphans.RemoveAt(orphan);
        }

        ModLog.Warn($"Menus: this PC's game opened a {type} without the data asked for (another copy of it); closing it once it's up.");
        CloseWhenUp(menu);
    }

    /// <summary>
    /// A menu on this PC is closing: if it's a view this PC didn't close, the game closed it on its own. A squad menu's
    /// "don't commit" switch is put back then too, as when this PC closes it (<see cref="CloseMenu"/>).
    /// </summary>
    public void Hiding(AbstractMenu menu)
    {
        if (OwnUnlocks.Hiding(menu))
        {
            return; // this player's own new-unlocks screen, closing itself
        }

        foreach (View view in _views.Values)
        {
            if (view.Menu is { } open && open.Pointer == menu.Pointer)
            {
                if (view.OwnExit == OwnExitState.Running)
                {
                    // The old squad menu's own exit, run here at the host's exit step: its hide (then its transition) is
                    // the game's, as on the host's screen; the view waits for the host's close.
                    view.OwnExit = OwnExitState.Hidden;
                    view.Menu = null;
                    Log($"Menus: {view.LogId} is hiding here with its own exit; its transition follows, as on {LogNames.HostTag()}'s screen.");
                    continue;
                }

                if (open.TryCast<ChooseMetaMenu>() is { } squads)
                {
                    squads.m_DoNotSetChoice = false;
                }

                if (view.Type == BrawlViews.Type)
                {
                    BrawlClosedHere(view);
                    continue;
                }

                view.Menu = null;
                ToPanel(view, "your game closed it");
                Log($"Menus: this PC's game closed {view.LogId} while it's open on {LogNames.HostTag()}'s; showing it as text.");
            }
        }
    }

    private void Open(MirroredMenu menu)
    {
        // A copy still behind its fade to black closes first: it can't share the screen with the next of its kind.
        FriendTransitions.FlushTimedCloses();
        var view = new View(menu.State.Serial, menu.State.Id, menu.State.Type);
        _views[view.Serial] = view;
        _tally.Opened(view.Serial, view.Type);
        RunStartOpened(view);
        if (view.Type == MenuPayloads.EventChoice)
        {
            EventViews.HostEventOpened(menu.State.Data.Text(EventCapture.ClassKey));
        }

        if (view.Type == MenuPayloads.PermanentUnlock || (view.Type == MenuPayloads.RunEnd && RunEndView.AtRunsEnd(menu.State.Data)))
        {
            // The host's run is over: the run's last match, whose scene its run-end screens keep loaded here, no longer holds
            // shared progress back, so this player's new-unlocks screen shows what it unlocked here.
            Progress.ProgressModule.Friend?.HostRunEnded();
            UnlockViews.RunEnded(unlocksScreen: view.Type == MenuPayloads.PermanentUnlock);
            HostRunOver = true; // a question about a match that couldn't load here has no answer now (Online/OnlineSession.FollowQuestion.cs)
            if (view.Type == MenuPayloads.PermanentUnlock)
            {
                OwnUnlocks.HostScreenOpened();
            }
            else
            {
                OwnUnlocks.RunEnded(view.Serial);
            }
        }

        if (view.Type == MenuPayloads.PermanentUnlock)
        {
            OpenUnlocks(view, menu);
            return;
        }

        if (OpenSettingsNote(view))
        {
            return;
        }

        if (!RealTypes.Contains(view.Type))
        {
            _tally.Text(view.Serial, "this version has no real view of it");
            Log($"Menus: showing {view.LogId} as on {LogNames.HostTag()}'s screen (as text).");
            return;
        }

        if (view.Type == MenuPayloads.Glossary)
        {
            // It opens here only once it has stayed open a moment (nothing shows meanwhile). The host's title warm-ups, which
            // open and close theirs at once, no longer come here (ExhibitionScreen.IsWarmUp): this is the backstop.
            view.Mode = Mode.Pending;
            view.RequestedAt = FrameClock.Seconds;
            view.OpenAt = FrameClock.Seconds + GlossaryDelaySeconds;
            return;
        }

        if (view.Type == ControllerViews.Type && ExhibitionScreen.PreparingTitleMenus())
        {
            // This PC's own title warm-ups open and close its copy under a black cover: asked for once they're over.
            view.Mode = Mode.Pending;
            view.RequestedAt = FrameClock.Seconds;
            view.OpenAt = FrameClock.Seconds + SceneRetrySeconds;
            Log($"Menus: {view.LogId} waits for this PC's own title warm-ups to end before it opens.");
            return;
        }

        TryReal(view, menu);
    }

    /// <summary>
    /// The host's new-unlocks menu: this player's own new unlocks of the session, for real (D42), once the shared progress
    /// already here has applied (a few seconds at most, as text meanwhile); a note when there are none.
    /// </summary>
    private void OpenUnlocks(View view, MirroredMenu menu)
    {
        double now = FrameClock.Seconds;
        int waiting = Progress.ProgressModule.Friend?.Waiting ?? 0;
        if (waiting > 0 && (view.WaitUnlocksUntil == 0 || now < view.WaitUnlocksUntil))
        {
            if (view.WaitUnlocksUntil == 0)
            {
                view.WaitUnlocksUntil = now + UnlockWaitSeconds;
                view.RetryAt = now + SceneRetrySeconds;
                ToPanel(view, "your game is still applying the progress you shared");
                Log($"Menus: {view.LogId} waits for {waiting} shared progress batch(es) here to apply first (at most {UnlockWaitSeconds:0} s); as text meanwhile.");
            }

            return;
        }

        if (view.WaitUnlocksUntil > 0 && waiting > 0)
        {
            Log($"Menus: {view.LogId}: {waiting} shared progress batch(es) still wait here after {UnlockWaitSeconds:0} s; this player's new unlocks show without them (they still toast).");
        }

        view.WaitUnlocksUntil = 0;
        if (UnlockViews.OwnCount == 0)
        {
            NoteUnlocks(view, "you unlocked nothing new this run");
            view.NoteWord = LobbyWords.NoteNoNewUnlocks;
            return;
        }

        TryReal(view, menu);
    }

    /// <summary>The host's new-unlocks menu as a note: the host is looking at theirs, and why this player's own aren't shown.</summary>
    private void NoteUnlocks(View view, string why)
    {
        view.Mode = Mode.Note;
        view.Note = $"is looking at their new unlocks; {why}.";
        view.NoteWord = null;
        _tally.Note(view.Serial);
        _panelDirty = true;
        Log($"Menus: {LogNames.HostTag()} is looking at their new unlocks ({view.LogId}); this player's own aren't shown: {why}.");
    }

    /// <summary>The new-unlocks menu's data with this player's own unlocks (a note instead when there are none to show).</summary>
    private PermanentUnlockMenu.Metadata? PrepareUnlocks(View view, MenuData data, out string problem)
    {
        PermanentUnlockMenu.Metadata? request = UnlockViews.Request(data, out problem, out bool none);
        if (none)
        {
            NoteUnlocks(view, $"none of yours shows on this screen ({UnlockViews.OwnText()})");
            return null;
        }

        if (request != null)
        {
            Log($"Menus: {view.LogId} shows {UnlockViews.Describe(data, request)} here (this PC's own progress, at its own thresholds).");
        }

        return request;
    }

    /// <summary>The lineup menu's data on this PC's own copy of the host's team (or null with why).</summary>
    private LineupMenu.Metadata? PrepareLineup(View view, MenuData data, out string problem)
    {
        LineupMenu.Metadata? request = LineupViews.Request(data, out problem, out string about);
        if (request != null)
        {
            Log($"Menus: {view.LogId} opens here on this PC's copy of {about}.");
        }

        return request;
    }

    /// <summary>
    /// A lineup's cards as the host's: its swaps (the menu's own, on its temporary roster) and its toggle. Cards that can't
    /// match the host's (another team) show it as text.
    /// </summary>
    private void FollowLineup(View view, MirroredMenu menu, LineupMenu lineup)
    {
        string done = string.Empty;
        string problem = string.Empty;
        bool ready = false;
        if (!Try(view, "following the host's lineup", () => done = LineupViews.Follow(lineup, menu.State.Data, out problem, out ready)))
        {
            return;
        }

        view.LineupData = menu.State.Data;
        view.LineupChanges += done.Length > 0 ? 1 : 0;

        if (problem.Length > 0)
        {
            ModLog.Warn($"Menus: {view.LogId} can't follow {LogNames.HostTag()}'s lineup here ({problem}); showing it as text.");
            CloseReal(view);
            ToPanel(view, problem);
            return;
        }

        if (!ready && view.Frame == LineupReadyFrames)
        {
            Missed(view, "the host's lineup cards (this PC's menu has a different number of them)");
        }

        if (done.Length > 0)
        {
            Log($"Menus: {view.LogId} follows {LogNames.HostTag()}'s lineup here: {done}.");
        }
    }

    /// <summary>The Canada curse menu's data: the host's curses made here, this PC's Canada team (or null with why).</summary>
    private CanadaCurseMenu.Metadata? PrepareCanada(View view, MenuData data, out string problem)
    {
        CanadaCurseMenu.Metadata? request = CanadaViews.Request(data, out problem, out string about);
        if (request != null)
        {
            view.CardsUp = false;
            Log($"Menus: {view.LogId} opens here as on {LogNames.HostTag()}'s screen: {about}.");
        }

        return request;
    }

    /// <summary>The host's menus closed while this PC was away (a rejoin): their views close too.</summary>
    private void Synced()
    {
        List<View> stale = _views.Values.Where(v => !_mirror.TryGet(v.Serial, out _)).ToList();
        foreach (View view in stale)
        {
            Close(view);
        }

        if (stale.Count > 0)
        {
            Log($"Menus: {stale.Count} of {LogNames.HostTag()}'s menus closed while this PC was away; closed here too.");
        }
    }

    /// <summary>Opens the real menu for <paramref name="view"/> when it can; otherwise the panel shows it, with why.</summary>
    private void TryReal(View view, MirroredMenu menu)
    {
        view.WaitScene = false;
        view.KeptOff = false;
        string blocker = Blocker(menu.State);
        if (blocker == RunNotYet)
        {
            // The host's menu was made from a newer run than this game holds (it's still loading, or was left for the next
            // move): it opens once that run is in, as text meanwhile (after a quiet second, FriendMenus.Overlays.cs).
            Wait(view, blocker);
            if (view.WaitRunUntil == 0)
            {
                view.WaitRunUntil = FrameClock.Seconds + RunWaitSeconds;
                view.RetryAt = FrameClock.Seconds + SceneRetrySeconds;
                Log($"Menus: {view.LogId} waits for {LogNames.HostTag()}'s run {NeededRun(menu.State)} (this PC holds {RunSync.Instance?.AppliedCheckpoint ?? 0}); as text if that takes over a second.");
                BringRunIn(NeededRun(menu.State));
            }

            return;
        }

        view.WaitRunUntil = 0;
        if (blocker == SummaryNotYet)
        {
            // The run's end is shown from the host's run summary, which follows the menu's open: it opens once that's in
            // (Attached), as text meanwhile (after a quiet second), for a few seconds at most.
            Wait(view, blocker);
            if (view.WaitSummaryUntil == 0)
            {
                view.WaitSummaryUntil = FrameClock.Seconds + SummaryWaitSeconds;
                Log($"Menus: {view.LogId} waits for {LogNames.HostTag()}'s run summary; as text if that takes over a second.");
            }

            return;
        }

        view.WaitSummaryUntil = 0;
        if (blocker == EventNotYet)
        {
            // The host's choices follow the menu's open: it opens once they're in (Attached), as text meanwhile (after a
            // quiet second), for a few seconds at most.
            Wait(view, blocker);
            if (view.WaitEventUntil == 0)
            {
                view.WaitEventUntil = FrameClock.Seconds + SummaryWaitSeconds;
                Log($"Menus: {view.LogId} waits for {LogNames.HostTag()}'s event choices; as text if that takes over a second.");
            }

            return;
        }

        view.WaitEventUntil = 0;
        if (blocker == PromptNotYet)
        {
            // The host's prompt data follows the menu's open: it opens once that's in (Attached), as text meanwhile (after
            // a quiet second).
            Wait(view, blocker);
            if (view.WaitPromptUntil == 0)
            {
                view.WaitPromptUntil = FrameClock.Seconds + SummaryWaitSeconds;
                Log($"Menus: {view.LogId} waits for {LogNames.HostTag()}'s prompt data; as text if that takes over a second.");
            }

            return;
        }

        view.WaitPromptUntil = 0;
        if (TitleWait(view, blocker))
        {
            return;
        }

        if (blocker.Length > 0)
        {
            if (_matches.FriendMatchFailed && (blocker == NoHostMatch || (blocker == OtherScene && MatchFailedMenus.KeptOff(view.Type))))
            {
                // The match's own menus while this game couldn't load that match: nothing of them here, not even text. This PC's
                // game asks whether to try again or leave (Online/OnlineSession.FollowQuestion.cs), and a panel per menu would cover
                // that and pile up behind it. Tried again as for a scene on its way. The run's menus (its rewards, its end) aren't
                // these: shown for real where this game can (RunEndHereAnyway), else as text.
                KeepOff(view, MatchFailedHere);
                view.WaitScene = true;
                Log($"Menus: not showing {view.LogId} here: {MatchFailedHere} (host in \"{HostText.KeyForLog(menu.State.Scene)}\", this PC in \"{MenuGame.SceneName()}\"); " +
                    "its question to try again or leave stays clear.");
                return;
            }

            if (blocker is ExhibitionControllers or NoHostMatch)
            {
                // Shows as text after a quiet second, as a wait for data does.
                Wait(view, blocker);
            }
            else
            {
                ToPanel(view, blocker);
            }

            // Tried again: this PC's scene, or its copy of the host's match, may only be on its way (a late joiner, a rejoin, a slow load).
            view.WaitScene = blocker is OtherScene or NoHostMatch;
            Log($"Menus: {(view.Type == BrawlViews.Type ? "not showing" : "showing")} {view.LogId} {(view.Type == BrawlViews.Type ? "here" : "as text")}, not as the menu: {blocker} " +
                $"(host in \"{HostText.KeyForLog(menu.State.Scene)}\", this PC in \"{MenuGame.SceneName()}\").");
            return;
        }

        if ((view.Type is RunStartCapture.OldSquad or RunStartCapture.Benchwarmer) && !RunStartReady(view, menu))
        {
            return;
        }

        string problem = string.Empty;
        view.Plan = view.Type == MenuPayloads.Game ? GameMenuViews.Prepare(menu.State.Data, out problem) : null;
        Il2CppSystem.Object? request = view.Type switch
        {
            MenuPayloads.Game => view.Plan?.Request,
            MenuPayloads.Glossary => new Il2CppSystem.Object(), // its InitData takes no data; a fresh object binds it to this view
            MenuPayloads.EventChoice => PrepareEvent(view, out problem),
            MenuPayloads.PermanentUnlock => PrepareUnlocks(view, menu.State.Data, out problem),
            LineupViews.Type => PrepareLineup(view, menu.State.Data, out problem),
            CanadaViews.Type => PrepareCanada(view, menu.State.Data, out problem),
            ControllerViews.Type => PrepareControllers(view, menu.State.Data, out problem),
            WorldMapType => new Il2CppSystem.Object(), // it takes no data (no InitData of its own): bound at its Show
            BrawlViews.Type => PrepareBrawl(view, menu.State.Data, out problem), // likewise, with this PC's offer held open
            RunStartCapture.OldSquad => PrepareOldSquad(view, out problem),
            RunStartCapture.Benchwarmer => PrepareBench(view, out problem),
            UnlockablesView.Type => PrepareUnlockables(view, out problem),
            _ when PromptViews.IsPrompt(view.Type) => PromptViews.Request(view.Type, _attachments.Get(view.Serial, MenuAttachmentKind.Prompt), out problem),
            _ => MenuRebuild.Metadata(view.Type, menu.State.Data, RunEndAttachment(view, menu.State), out problem),
        };
        UIModule? ui = request == null ? null : MenuGame.Module();
        if (request == null && view.Mode == Mode.Note)
        {
            return; // nothing of this PC's own to show (a note says so)
        }

        if (request == null || ui == null)
        {
            EventViews.Disarm(view.Event);
            view.Event = null;
            DisarmRunStart(view);
            DisarmTitle(view);
            string why = request == null ? problem : "the game's UI isn't ready";
            ToPanel(view, why);
            ModLog.Warn($"Menus: couldn't rebuild {view.LogId} from {LogNames.HostTag()}'s data ({why}); showing it as text.");
            return;
        }

        view.Mode = Mode.Pending;
        view.Request = request;
        view.RequestedAt = FrameClock.Seconds;
        view.PanelShown = false;
        view.OfferKey = OfferKey(menu.State.Data);
        view.FadeOnClose = FadesOnClose(view.Type, menu.State.Data);
        if (view.Event != null)
        {
            // Its event's next Initialize takes the host's choices instead of rolling.
            EventViews.Arm(view.Event);
        }

        // The unlockables' rows take the host's states as its InitData converts them.
        ArmTitle(view);

        try
        {
            // Not inside FriendUi.Apply: opening clicks nothing, and the friend's lock should hold while the game opens it.
            Show(ui, view.Type, view.Id, request);
        }
        catch (Exception ex)
        {
            EventViews.Disarm(view.Event);
            view.Event = null;
            DisarmRunStart(view);
            DisarmTitle(view);
            // The generic call itself failed (Il2CppInterop couldn't make or call the method for this class): don't try
            // it again this game (Start: the next one tries).
            _broken.Add(view.Type);
            view.Request = null;
            ToPanel(view, "your game couldn't open it");
            ModLog.Error($"Menus: UIModule.ShowMenuAsync<{HostText.KeyForLog(menu.State.Type)}>(\"{view.LogId}\") failed; {view.LogId} shows as text for the rest of this game", ex);
            if (_notified.Add(view.LogId))
            {
                _notify(NoticeId.MenuAsText, $"couldn't open the host's {Titles.Of(menu.State)} here; showing it as text"); // a log-only notice: its text is the log's (no name, D127)
            }
        }
    }

    /// <summary>
    /// True for the host's run-end screens this PC shows for real on its own screen though the host's is in the match's scene:
    /// only while the host's match couldn't load here, where this game shows that screen itself (<see cref="MatchFailedMenus.ShowsHere"/>).
    /// </summary>
    private bool RunEndHereAnyway(MenuState state)
    {
        if (!_matches.FriendMatchFailed || state.Type is not (MenuPayloads.RunEnd or MenuPayloads.PermanentUnlock))
        {
            return false;
        }

        string here = MenuGame.SceneName();
        return MatchFailedMenus.ShowsHere(state.Type, state.Type == MenuPayloads.RunEnd && RunEndView.AtRunsEnd(state.Data),
            onMap: here == WorldMap.MapScene && WorldMap.MapLoaded, atTitle: here == WorldMap.TitleScene);
    }

    /// <summary>Why the real menu can't be shown now ("" when it can).</summary>
    private string Blocker(MenuState state)
    {
        if (_broken.Contains(state.Type))
        {
            return "your game couldn't open it earlier";
        }

        if (state.Scene.Length > 0 && state.Scene != MenuGame.SceneName() && !RunEndHereAnyway(state))
        {
            return OtherScene;
        }

        if (state.Type == MenuPayloads.RunEnd)
        {
            switch (RunEndView.Of(state.Data))
            {
                case RunEndView.Variant.Text:
                    return state.Data.Flag(RunEndView.RandomData) ? "the host's game shows made-up data" : "the host's game couldn't make its run summary";
                case RunEndView.Variant.Summary when !_attachments.Has(state.Serial, MenuAttachmentKind.RunSummary):
                    return SummaryNotYet;
            }
        }

        string title = TitleBlocker(state);
        if (title.Length > 0)
        {
            return title;
        }

        if (state.Type == MenuPayloads.Game)
        {
            if (!MenuPatches.GameMenuReady)
            {
                return "this game's in-match menu couldn't be patched to show it safely";
            }

            if (!_matches.PlayingHostMatch && !_matches.HoldingEndedMatch)
            {
                return NoHostMatch;
            }

            if (GameMenuViews.OwnMenuUp())
            {
                return "your game has its own pause menu up";
            }
        }

        if (state.Type == LineupViews.Type)
        {
            if (!MenuPatches.LineupReady)
            {
                return "this game's lineup menu couldn't be patched to show it safely";
            }

            if (LineupViews.OnMatchTeam(state.Data) && !_matches.PlayingHostMatch && !_matches.HoldingEndedMatch)
            {
                return NoHostMatch;
            }
        }

        if (state.Type == CanadaViews.Type)
        {
            if (!MenuPatches.CanadaReady)
            {
                return "this game's Canada curse menu couldn't be patched to show it safely";
            }

            if (!_matches.PlayingHostMatch && !_matches.HoldingEndedMatch)
            {
                return NoHostMatch;
            }
        }

        if (state.Type == BrawlViews.Type && !_matches.PlayingHostMatch)
        {
            return NoHostMatch;
        }

        if (state.Type == WorldMapType && !MenuPatches.WorldMapReady)
        {
            return "this game's map menu couldn't be patched to show it safely";
        }

        string controllers = ControllerBlocker(state);
        if (controllers.Length > 0)
        {
            return controllers;
        }

        if (state.Type == MenuPayloads.PermanentUnlock && !MenuPatches.UnlocksReady)
        {
            return "this game's new-unlocks menu couldn't be patched to show it safely";
        }

        if (state.Type == MenuPayloads.Glossary && !MenuPatches.GlossaryReady)
        {
            return "this game's glossary couldn't be patched to show it";
        }

        if (state.Type == MenuPayloads.EventChoice)
        {
            if (!MenuPatches.EventMenuReady)
            {
                return "this game's event menu couldn't be patched to show it safely";
            }

            if (!state.Data.Flag(EventCapture.SetKey))
            {
                string why = state.Data.Text(EventCapture.TextKey);
                return why.Length > 0 ? why : "the host's game didn't send its choices";
            }

            if (state.Data.Text(EventCapture.ClassKey) == "CoachEvent" && EventViews.TutorialActive())
            {
                return "the campaign tutorial is running on this PC (its Coach speech would wait for a press)";
            }

            if (state.Data.Text(EventCapture.ClassKey) == EventChoiceSet.TakeoverClass && !MenuPatches.NurseReady)
            {
                return "this game's Nurse event couldn't be patched to show it safely";
            }
        }

        if (PromptViews.IsPrompt(state.Type))
        {
            if (!MenuPatches.PromptReady)
            {
                return "this game's prompts couldn't be patched to show them safely";
            }

            if (PromptViews.NeedsData(state.Type) && !_attachments.Has(state.Serial, MenuAttachmentKind.Prompt))
            {
                return PromptNotYet;
            }
        }

        if (NeedsHostRun(state) && RunSync.Instance?.HoldingHostRun != true)
        {
            return "the host's run isn't loaded here";
        }

        if (NeedsHostRun(state) && NeededRun(state) is uint needed and not 0 &&
            !CampaignMatchValues.HoldsCheckpoint(RunSync.Instance?.AppliedCheckpoint ?? 0, needed))
        {
            return RunNotYet;
        }

        if (state.Type == MenuPayloads.EventChoice && (!_attachments.Has(state.Serial, MenuAttachmentKind.EventChoices) || NurseRosterPending(state)))
        {
            return EventNotYet;
        }

        if (state.Type == MenuPayloads.ChooseMeta && !SaveGuard.Refusing)
        {
            // Opening it saves the profile (new squad unlocks): only while this PC's saves are held for the session.
            return "your saves aren't held here, and opening it would save your profile";
        }

        if (_views.Values.Count(v => v.Mode == Mode.Pending) >= MaxPending)
        {
            return "too many menus are opening here at once";
        }

        return string.Empty;
    }

    /// <summary>
    /// True for a real view built from this PC's copy of the host's run: the reward menus (their skaters and team), the event
    /// menu (its skaters, and the run's relics, talents and abilities) and the map's "Run stats" (the run's team and stats).
    /// The squad menu reads no run; the run's end comes whole in the host's run summary.
    /// </summary>
    private static bool NeedsHostRun(MenuState state) =>
        state.Type is MenuPayloads.RelicReward or MenuPayloads.PowerUpReward or MenuPayloads.EventChoice ||
        (state.Type == LineupViews.Type && LineupViews.OnRunTeam(state.Data)) ||
        (state.Type == MenuPayloads.RunEnd && RunEndView.Of(state.Data) == RunEndView.Variant.DuringRun);

    /// <summary>The run checkpoint the host's menu was made from (0: not said, an older host, or a menu that reads no run).</summary>
    private static uint NeededRun(MenuState state) => (uint)Math.Clamp(state.Data.Whole(MenuPayloads.RunCheckpoint, 0), 0, uint.MaxValue);

    /// <summary>
    /// A reward menu needs run <paramref name="needed"/>, which this PC got but left for the next move on the map (a save the
    /// host made without moving): it goes in now, before the menu opens (no real menu is up here for it to go stale under).
    /// A run already loading here answers by itself.
    /// </summary>
    private void BringRunIn(uint needed)
    {
        RunSync? runs = RunSync.Instance;
        RunCheckpoint? latest = CampaignRelay.Current?.LatestRun;
        if (runs == null || latest == null || SequenceMath.IsNewer(needed, latest.Sequence) || _views.Values.Any(v => v.Mode == Mode.Real))
        {
            return;
        }

        Log($"Menus: bringing {LogNames.HostTag()}'s run {latest.Sequence} in for the menu that needs it.");
        runs.Apply(latest, result =>
        {
            if (!result.Ok)
            {
                ModLog.Warn($"Menus: couldn't bring {LogNames.HostTag()}'s run {latest.Sequence} in for a menu ({result.Reason}); it shows as text.");
            }
        });
    }

    /// <summary>The game's own open of menu <paramref name="id"/>, with its concrete class (the generic is called, never patched; D14, D43).</summary>
    private static void Show(UIModule ui, string type, string id, Il2CppSystem.Object request)
    {
        switch (type)
        {
            case MenuPayloads.RelicReward:
                _ = ui.ShowMenuAsync<RelicRewardMenu>(id, request);
                break;
            case MenuPayloads.PowerUpReward:
                _ = ui.ShowMenuAsync<SkaterPowerupRewardMenu>(id, request);
                break;
            case MenuPayloads.ChooseMeta:
                _ = ui.ShowMenuAsync<ChooseMetaMenu>(id, request);
                break;
            case MenuPayloads.RunEnd:
                _ = ui.ShowMenuAsync<RunEndMenu>(id, request);
                break;
            case MenuPayloads.Game:
                _ = ui.ShowMenuAsync<GameMenu>(id, request);
                break;
            case MenuPayloads.Glossary:
                _ = ui.ShowMenuAsync<GlossaryMenu>(id, request);
                break;
            case MenuPayloads.EventChoice:
                _ = ui.ShowMenuAsync<EventChoiceMenu>(id, request);
                break;
            case MenuPayloads.PermanentUnlock:
                _ = ui.ShowMenuAsync<PermanentUnlockMenu>(id, request);
                break;
            case LineupViews.Type:
                _ = ui.ShowMenuAsync<LineupMenu>(id, request);
                break;
            case WorldMapType:
                _ = ui.ShowMenuAsync<WorldMapMenu>(id, request);
                break;
            case CanadaViews.Type:
                _ = ui.ShowMenuAsync<CanadaCurseMenu>(id, request);
                break;
            case ControllerViews.Type:
                _ = ui.ShowMenuAsync<ControllerAssignmentMenu>(id, request);
                break;
            case BrawlViews.Type:
                _ = ui.ShowMenuAsync<BrawlFightMenu>(id, request);
                break;
            case RunStartCapture.OldSquad:
                _ = ui.ShowMenuAsync<OldSquadMenu>(id, request);
                break;
            case RunStartCapture.Benchwarmer:
                _ = ui.ShowMenuAsync<BenchwarmerMenu>(id, request);
                break;
            case UnlockablesView.Type:
                _ = ui.ShowMenuAsync<UnlockableMenu>(id, request);
                break;
            default:
                _ = PromptViews.Show(ui, type, id, request);
                break;
        }
    }

    /// <summary>
    /// The event menu's data for <paramref name="view"/>: the game's own choices built from the host's newest set (it's
    /// armed once the menu is asked for), or null with why.
    /// </summary>
    private EventChoiceMenu.Metadata? PrepareEvent(View view, out string problem)
    {
        EventViews.Disarm(view.Event);
        view.Event = null;
        view.NextSet = null;
        if (_attachments.Get(view.Serial, MenuAttachmentKind.EventChoices) is not { } attachment || !attachment.TryGetEventChoices(out EventChoiceSet? set) || set == null)
        {
            problem = "the host's event choices aren't here";
            return null;
        }

        EventPlan? plan = EventViews.Build(set, open: true, out problem);
        if (plan == null)
        {
            return null;
        }

        view.Event = plan;
        view.SetVersion = set.Version;
        view.PickedVersion = -1;
        NurseRebuild(view, plan);
        return plan.Request;
    }

    /// <summary>A real view didn't open in time: the panel shows it (taken over if it still opens); a class that keeps not opening isn't tried again.</summary>
    private void TimedOut(View view)
    {
        int count = _timeouts[view.Type] = _timeouts.GetValueOrDefault(view.Type) + 1;
        ToPanel(view, "it didn't open in time");
        if (count >= MaxTimeouts && _broken.Add(view.Type))
        {
            ModLog.Warn($"Menus: {view.LogId} didn't open within {OpenTimeoutSeconds:0} s here, {count} times now; this kind of menu shows as text for the rest of this game " +
                "(one that still opens is taken over).");
            return;
        }

        ModLog.Warn($"Menus: {view.LogId} didn't open within {OpenTimeoutSeconds:0} s; showing {LogNames.HostTag()}'s screen as text (it's taken over if it still opens).");
    }

    /// <summary>
    /// The host's menu's data changed: new offers (a reroll) go into the open menu; anything else only changes the panel.
    /// A menu still opening keeps the offers it was asked with: it's opened again with the new ones once it's up.
    /// </summary>
    private void Updated(View view, MirroredMenu menu)
    {
        if (view.Type == BrawlViews.Type)
        {
            BrawlUpdated(view, menu);
            return;
        }

        if (view.RunStart != null && view.Type == RunStartCapture.Benchwarmer)
        {
            // Its cards: followed at its next look; the newest is what its close leaves for the check of the host's run.
            view.RunStart.LastData = menu.State.Data;
        }

        if (view.Type == MenuPayloads.Glossary)
        {
            // Its data changes as the host's list scrolls (v1): its entry, row and list's place again.
            if (view.Mode == Mode.Real)
            {
                ApplySelection(view, menu);
            }

            return;
        }

        if (view.Type == MenuPayloads.EventChoice)
        {
            // Its choices come as attachments; its data changes only the panel, unless the host's next choices couldn't be
            // sent (then it's text from here, with why).
            if ((view.Mode is Mode.Real or Mode.Pending) && !menu.State.Data.Flag(EventCapture.SetKey))
            {
                string why = menu.State.Data.Text(EventCapture.TextKey);
                why = why.Length > 0 ? why : "the host's game stopped sending its choices";
                ModLog.Warn($"Menus: {view.LogId}: {why}; showing it as text from here.");
                Abandon(view);
                CloseReal(view);
                ToPanel(view, why);
            }

            return;
        }

        if (view.Type == MenuPayloads.ChooseMeta && view.Mode == Mode.Real && view.Menu is { } squads && view.Frame >= ReselectFrames[^1])
        {
            // Its selectors' items were read again: a choice this profile lacks shows the host's newest (DressList).
            foreach (KeyValuePair<MenuTarget, int> value in menu.Values.Where(v => v.Key.Kind == MenuTargetKind.Path && v.Value <= MenuHold.MaxListIndex).ToList())
            {
                ApplyList(view, squads, value.Key, value.Value);
            }
        }

        string key = OfferKey(menu.State.Data);
        if (view.Mode != Mode.Real || view.Menu == null || key == view.OfferKey)
        {
            return;
        }

        view.OfferKey = key;
        string problem = string.Empty;
        bool done = false;
        try
        {
            done = Reroll(view.Menu, menu.State.Data, out problem);
        }
        catch (Exception ex)
        {
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        if (done)
        {
            view.Frame = 0; // the host's selection again once the new cards are up
            Log($"Menus: {LogNames.HostTag()} rerolled {view.LogId}; the new offers are shown here.");
            return;
        }

        // Couldn't put the new offers into the open menu: open it again with them.
        ModLog.Warn($"Menus: couldn't put {LogNames.HostTag()}'s rerolled offers into {view.LogId} ({problem}); opening it again.");
        CloseReal(view);
        TryReal(view, menu);
    }

    /// <summary>Puts rerolled offers into the open menu as its own reroll does, minus the roll.</summary>
    private static bool Reroll(AbstractMenu open, MenuData data, out string problem)
    {
        problem = string.Empty;
        if (open.TryCast<RelicRewardMenu>() is { } relics)
        {
            Il2CppList.List<RelicReward>? rewards = MenuRebuild.RelicRewards(data, out problem);
            if (rewards == null)
            {
                return false;
            }

            int rerolls = MenuRebuild.Rerolls(data);
            relics.m_RelicRewards = new Il2CppList.IReadOnlyList<RelicReward>(rewards.Pointer);
            relics.m_RerollCount = rerolls;
            relics.m_CurrentRelicChoice = -1;
            relics.m_RerollContainer?.SetActive(rerolls > 0);
            _ = relics.InstantiateRelics();
            return true;
        }

        if (open.TryCast<SkaterPowerupRewardMenu>() is { } powerUps)
        {
            Il2CppData.TeamData? team = MenuRebuild.Team(out problem);
            if (team == null || !MenuRebuild.TryPowerUpLists(data, team, out MenuRebuild.Offers offers, out problem))
            {
                return false;
            }

            int rerolls = MenuRebuild.Rerolls(data);
            powerUps.m_PowerUpRewards = offers.PowerUps == null ? null! : new Il2CppList.IReadOnlyList<PowerUpReward>(offers.PowerUps.Pointer);
            powerUps.m_BenchRewards = offers.Bench == null ? null! : new Il2CppList.IReadOnlyList<BenchPowerReward>(offers.Bench.Pointer);
            powerUps.m_Skaters = offers.Skaters == null ? null! : new Il2CppList.IReadOnlyList<Il2CppData.SkaterData>(offers.Skaters.Pointer);
            powerUps.m_RerollCount = rerolls;
            powerUps.m_RerollContainer?.SetActive(rerolls > 0);
            _ = powerUps.InstantiatePowerUps();
            powerUps.InstantiateSkaters();
            powerUps.SetToState(SkaterPowerupRewardMenu.EState.SelectPowerUp, false);
            powerUps.m_CurrentPowerUpChoice = -1;
            return true;
        }

        problem = "this menu has no offers to reroll";
        return false;
    }

    /// <summary>
    /// A real view's frames: the host's state, values and selection again a few frames after it opened (its own Show
    /// selects a default), and the selection again after a step change (the menu selects its own default a frame later).
    /// </summary>
    private void Tick(View view, MirroredMenu menu)
    {
        view.Frame++;
        if (view.HoldReadout is { } held && FrameClock.Seconds >= view.HoldReadoutAt)
        {
            view.HoldReadout = null;
            ReadOutHold(view, held);
        }

        if (view.Type == MenuPayloads.EventChoice && view.MenuAs<EventChoiceMenu>() is { } eventMenu)
        {
            EventTick(view, menu, eventMenu);
            if (view.Mode != Mode.Real)
            {
                return;
            }
        }

        if (view.Frame == ReselectFrames[0] && OfferKey(menu.State.Data) != view.OfferKey)
        {
            // The host rerolled while this PC's copy was still opening: open it again with the new offers (putting them
            // into a menu still running its own opening could leave both sets of cards up).
            Log($"Menus: {LogNames.HostTag()} rerolled {view.LogId} while it was opening here; opening it again with the new offers.");
            CloseReal(view);
            TryReal(view, menu);
            return;
        }

        if (view.Type == RunStartCapture.OldSquad && view.MenuAs<OldSquadMenu>() is { } oldSquad)
        {
            OldSquadTick(view, oldSquad);
        }

        if (view.Type == BrawlViews.Type)
        {
            BrawlTick(view, menu);
        }

        if (view.Type == RunStartCapture.Benchwarmer && view.MenuAs<BenchwarmerMenu>() is { } bench)
        {
            BenchTick(view, menu, bench);
            if (view.Mode != Mode.Real)
            {
                return;
            }
        }

        if (view.Type == ControllerViews.Type && view.MenuAs<ControllerAssignmentMenu>() is { } controllers)
        {
            ControllerTick(view, menu, controllers);
            if (view.Mode != Mode.Real)
            {
                return;
            }
        }

        if (Array.IndexOf(ReselectFrames, view.Frame) >= 0)
        {
            ApplyAll(view, menu);
        }
        else if (view.Frame == view.ReselectFrame)
        {
            ApplySelection(view, menu);
        }

        if (view.StepFrame >= 0 && (view.Frame == view.StepFrame + 2 || view.Frame == view.StepFrame + StepRetryFrames) && view.Menu != null)
        {
            // A step change builds the next step's selectors over a few frames (the squad menu's difficulty and mode): the
            // host's values again once they're up.
            foreach (KeyValuePair<MenuTarget, int> value in menu.Values.Where(v => v.Key.Kind == MenuTargetKind.Path && MenuHold.IsListIndex(v.Value)).ToList())
            {
                ApplyList(view, view.Menu, value.Key, value.Value);
            }
        }

        if (view.Type == MenuPayloads.PermanentUnlock && view.MenuAs<PermanentUnlockMenu>() is { } unlocks &&
            menu.Values.TryGetValue(UnlockStep, out int hostUnlock) && unlocks.m_CurrentUnlockIndex < hostUnlock)
        {
            // Only while this copy is behind the host's (Follow checks the rest): no closure made a frame once it's caught up.
            string step = string.Empty;
            bool stepped = false;
            if (Try(view, "moving to this player's next unlock", () => stepped = UnlockViews.Follow(unlocks, hostUnlock, out step)) && stepped)
            {
                Log($"Menus: {LogNames.HostTag()} moved on in {view.LogId}; this player's next unlock shows here ({step}).");
            }
        }

        TitleTick(view, menu);
        if (view.Type == CanadaViews.Type && !view.CardsUp && view.MenuAs<CanadaCurseMenu>() is { } curse && CanadaViews.CardsUp(curse))
        {
            // Its cards come after its own dialogue: the host's highlight lands on them from now on.
            view.CardsUp = true;
            view.ReselectFrame = view.Frame + 2;
            Log($"Menus: {view.LogId}: the curse cards are up here; the host's highlight follows.");
        }

        if (view.Type == LineupViews.Type && view.Frame % LineupEveryFrames == 0 && view.MenuAs<LineupMenu>() is { } lineup)
        {
            FollowLineup(view, menu, lineup);
            if (view.Mode != Mode.Real)
            {
                return;
            }
        }

        if (view.Frame % LineupEveryFrames == 1 && HoldsReady(view) && view.Menu is { } panelsMenu && menu.State.Data.Whole(DetailedStatsMode.Key, -1) is 0 or 1)
        {
            FollowDetailedStats(view, panelsMenu, menu.State.Data.Whole(DetailedStatsMode.Key, -1));
            if (view.Mode != Mode.Real)
            {
                return;
            }
        }

        if (view.Type == MenuPayloads.RunEnd && view.Frame % DressEveryFrames == 3 && view.MenuAs<RunEndMenu>() is { } runEnd)
        {
            // Its own opening fills it over a few frames (after awaits): checked now and then while it's open, the host's
            // sort too (it waits for the rows).
            ApplyValue(view, menu, MenuTarget.AtItem(HostMenus.Sort, 0), 0);
            string changed = string.Empty;
            if (view.Mode == Mode.Real && Try(view, "showing the run-end screen as the host's", () => changed = RunEndView.Dress(runEnd, menu.State.Data)) &&
                changed.Length > 0 && view.DressLogs < MaxDressLogs)
            {
                view.DressLogs++;
                Log($"Menus: {view.LogId}: {changed}.");
            }
        }

        if (view.Pressed != null && view.Frame >= view.ReleaseFrame)
        {
            AbstractButton button = view.Pressed;
            view.Pressed = null;
            if (button != null)
            {
                bool selected = MenuGame.Module()?.NavigationController?.CurrentNavigable?.Pointer == button.Pointer;
                Try(view, "releasing a pressed button", () => button.SetState(selected ? AbstractButton.EButtonState.Highlight : AbstractButton.EButtonState.Normal, true));
            }
        }
    }

    /// <summary>
    /// An event menu's frames here: its intro (ended as soon as the host's has; the host's map peek, details and selection
    /// once it's over and the cards are up), its new cards after a page or reroll, and the host's next choices once the menu
    /// is ready for them (not in its intro, nor in another swap). A menu that isn't ready in time is opened again with them.
    /// </summary>
    private void EventTick(View view, MirroredMenu menu, EventChoiceMenu open)
    {
        double now = FrameClock.Seconds;
        ReleaseCards(view, menu, open);
        bool playing = open.m_IsPlayingIntro;
        if (playing)
        {
            if (!view.IntroSeen)
            {
                view.IntroSeen = true;
                if (HostIntro(menu) == EventCapture.IntroOver)
                {
                    SkipIntro(view, open);
                }
            }

            view.IntroOver = false; // in its intro (one that started after the wait below is waited for again)
        }
        else if (!view.IntroOver && !CardsHeld(view) && (view.IntroSeen || now - view.BoundAt > IntroWaitSeconds))
        {
            // Its cards are up (not waiting for the host's) and its own default is selected: the host's map peek, details and
            // selection now.
            view.IntroOver = true;
            ApplyAll(view, menu);
        }

        // The host's NPC line: played once this screen is at its NPC's lines (from the host's line, when it's further on),
        // following the host's presses, ended with the host's intro.
        EventSpeech.Tick(view.Serial, view.LogId, open, view.BoundAt, view.IntroOver, HostIntro(menu), SpeechMirror.HostProgress(menu, EventSpeech.NpcSlot));

        // The Nurse's procedure screen: opened here for real once the host's has taken its screen over, then its steps.
        NurseTick(view, menu, open);

        if (view.Swapping && ((!open.m_IsSwappingChoices && now - view.SwapStarted > 0.1) || now - view.SwapStarted > SwapSeconds))
        {
            view.Swapping = false;
            ApplyAll(view, menu);
        }

        if (view.NextSet is not { } next || view.Swapping)
        {
            return;
        }

        if (view.IntroOver && !open.m_IsSwappingChoices)
        {
            ApplyNextSet(view, open, next);
        }
        else if (playing && HostIntro(menu) == EventCapture.IntroOver)
        {
            SkipIntro(view, open); // the host's next choices mean its intro is over
        }
        else if (now - view.NextSetSince > NextSetWaitSeconds)
        {
            Log($"Menus: {view.LogId} wasn't ready for {LogNames.HostTag()}'s next choices within {NextSetWaitSeconds:0} s; opening it again with them.");
            view.NextSet = null;
            CloseReal(view);
            TryReal(view, menu);
        }
    }

    /// <summary>The host's intro state (<see cref="EventCapture.IntroNotYet"/>, playing, over), or -1 when it hasn't said.</summary>
    private static int HostIntro(MirroredMenu menu) =>
        menu.Values.TryGetValue(MenuTarget.AtItem(EventCapture.StateGroup, EventCapture.Intro), out int state) ? state : -1;

    /// <summary>Ends this PC's event intro (the host's has ended): the game's own skip, once per opening.</summary>
    private void SkipIntro(View view, EventChoiceMenu open)
    {
        if (view.IntroSkipped)
        {
            return;
        }

        view.IntroSkipped = true;
        if (Try(view, "ending the intro as the host's ended", open.SkipIntro))
        {
            Log($"Menus: {view.LogId}: the intro ended here as on {LogNames.HostTag()}'s screen.");
        }
    }

    /// <summary>
    /// An event menu's state as the host's: the intro ended once the host's has; once the cards are up, the map peek (the
    /// menu's own <c>SetVisibility</c>), the details (its flag and its own refresh) and a takeover (the host's Nurse
    /// procedure screen: the cards go with the menu's own <c>SetChoicesShown</c>, and the panel shows that screen as text
    /// over the menu, <see cref="RefreshPanel"/>). True when the peek changed (the menu selects its own default a frame
    /// later: the host's selection is applied again).
    /// </summary>
    private bool ApplyEventState(View view, EventChoiceMenu open, int item, int value)
    {
        switch (item)
        {
            case EventCapture.Takeover:
                bool takeover = value != 0;
                if (NurseTakeover(view, takeover))
                {
                    return false; // the real procedure screen here follows the host's (FriendMenus.Nurse.cs)
                }

                if (view.Overlay != takeover)
                {
                    view.Overlay = takeover;
                    _panelDirty = true;
                    Log(takeover
                        ? $"Menus: {LogNames.HostTag()}'s Nurse took {view.LogId} over; its procedure screen shows as text over the event screen here."
                        : $"Menus: {LogNames.HostTag()}'s Nurse procedure screen closed; {view.LogId} goes with {LogNames.HostTag()}'s close.");
                }

                // The end of a takeover doesn't bring them back: it always exits the host's event, whose exit takes its own cards
                // away (verified in the game's code: CloseHostAsync, ExitAsync), and the close comes here after
                // its NPC's goodbye.
                if (takeover && view.IntroOver && !view.Swapping && !view.CardsHidden &&
                    Try(view, "putting the cards away under the procedure screen", () => open.SetChoicesShown(false, false)))
                {
                    view.CardsHidden = true;
                }

                return false;
            case EventCapture.Intro:
                if (value == EventCapture.IntroOver && open.m_IsPlayingIntro)
                {
                    SkipIntro(view, open);
                }

                return false;
            case EventCapture.Exit:
                if (value != 0)
                {
                    NpcGoodbye(view, open);
                }

                return false;
            case EventCapture.Peek when view.IntroOver && !view.Swapping:
                bool visible = value != 0;
                // As the game's own toggle does: the screen's visibility, then the map's peek event (camera back, map scrolling).
                return open.IsVisible != visible && Try(view, visible ? "showing the event again" : "putting the event aside for the map", () =>
                {
                    open.SetVisibility(visible);
                    Scenes.NodeEntryMirror.Peeked(!visible);
                });
            case EventCapture.Details when view.IntroOver && !view.Swapping:
                bool show = value != 0;
                if (open.m_IsShowingDetails != show)
                {
                    Try(view, "showing the details as the host does", () =>
                    {
                        open.m_IsShowingDetails = show;
                        open.RefreshShowDetailsOnItems();
                    });
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The host clicked in its event menu: a card's pick plays here with the menu's own pick animation (once per set of
    /// choices; nothing is picked). False when the click wasn't on a card (a button press is shown instead).
    /// </summary>
    private bool Pick(View view, EventChoiceMenu open, MenuTarget target)
    {
        AbstractNavigable? navigable = MenuTargets.Find(open.transform, target);
        EventChoiceItem? item = navigable == null ? null : navigable.GetComponentInParent<EventChoiceItem>();
        if (item == null)
        {
            return false;
        }

        if (view.PickedVersion == view.SetVersion || !view.IntroOver || view.Swapping)
        {
            return true;
        }

        view.PickedVersion = view.SetVersion;
        if (Try(view, "showing the host's pick", () => _ = open.AnimatePick(item)))
        {
            Log($"Menus: {LogNames.HostTag()} picked {PickWords(view, open, item)} in {view.LogId}; the pick plays here (nothing is picked on this PC).");
        }

        return true;
    }

    /// <summary>
    /// The host's next choices (a page or a reroll) into the open event menu: built here, written into its event, its counts
    /// set, then the menu's own page or reroll step (which makes the new cards from them; nothing rolls). One that can't be
    /// built or written shows the event as text.
    /// </summary>
    private void ApplyNextSet(View view, EventChoiceMenu open, EventChoiceSet set)
    {
        view.NextSet = null;
        EventNode? node = open.SourceNode;
        EventPlan? plan = EventViews.Build(set, open: false, out string problem);
        if (plan != null && (node == null || plan.Node.Pointer != node.Pointer))
        {
            problem = "they're for another event than the one shown here";
            EventViews.Release(plan); // never written: what it made goes
            plan = null;
        }

        if (plan != null)
        {
            NurseRebuild(view, plan); // a Nurse's reroll rolled a new roster on the host
        }

        bool reroll = set.Step == EventSetStep.Reroll;
        if (plan == null || !Try(view, reroll ? "rerolling as the host did" : "moving to the host's next choices", () =>
        {
            if (!EventViews.Write(node!, plan, out string written))
            {
                throw new InvalidOperationException(written);
            }

            open.m_ChoicesMade = set.ChoicesMade;
            open.m_RerollCount = set.RerollCount;
            open.RefreshRerollShortcut();
            if (reroll)
            {
                _ = open.RerollChoicesInPlace();
            }
            else
            {
                _ = open.GoToNextSetOfChoices();
            }
        }))
        {
            string why = plan == null ? problem : "your game couldn't show the host's next choices";
            ModLog.Warn($"Menus: couldn't show {LogNames.HostTag()}'s next choices in {view.LogId} ({why}); showing it as text.");
            CloseReal(view);
            ToPanel(view, why);
            return;
        }

        view.Event = plan;
        view.SetVersion = set.Version;
        view.Swapping = true;
        view.SwapStarted = FrameClock.Seconds;
        Log($"Menus: {LogNames.HostTag()} {(reroll ? "rerolled" : "moved on to the next choices of")} {view.LogId}; shown here ({set}).");
    }

    /// <summary>The host's menu state, list selectors and selection, in that order.</summary>
    private void ApplyAll(View view, MirroredMenu menu)
    {
        if (view.Type == MenuPayloads.Glossary)
        {
            // The glossary's tab and entry as the host's menu was read, unless a newer step says otherwise.
            foreach (string group in new[] { HostMenus.Tab, HostMenus.Entry })
            {
                string key = group == HostMenus.Tab ? GameMenuCapture.GlossaryTab : GameMenuCapture.GlossaryEntry;
                if (!menu.Values.Keys.Any(t => t.Kind == MenuTargetKind.Item && t.Group == group) && menu.State.Data.Whole(key, -1) is >= 0 and <= MenuTarget.MaxIndex and long read)
                {
                    ApplyValue(view, menu, MenuTarget.AtItem(group, 0), (int)read);
                }
            }
        }

        foreach (KeyValuePair<MenuTarget, int> value in menu.Values.OrderBy(v => Order(v.Key)).ToList())
        {
            ApplyValue(view, menu, value.Key, value.Value);
        }

        ApplySelection(view, menu);
    }

    /// <summary>State values first (a power-up pick before the state that shows its skaters; a sort; a tab before its entry), then list selectors.</summary>
    private static int Order(MenuTarget target) => target.Group switch
    {
        HostMenus.PowerUpChoice => 0,
        HostMenus.State or HostMenus.Sort or HostMenus.Tab or UnlockablesView.TabGroup => 1,
        HostMenus.Entry or UnlockablesView.RowGroup => 3,
        _ => 2,
    };

    private void ApplySelection(View view, MirroredMenu menu)
    {
        AbstractMenu? open = view.Menu;
        if (view.Type == MenuPayloads.Glossary && view.Mode == Mode.Real && open?.TryCast<GlossaryMenu>() is { } glossary)
        {
            // Its rows are a recycled list: the same place can hold another entry here, and selecting it would show that
            // entry. The row of the host's entry is highlighted instead (its own rebuild selects its first row a frame
            // after a tab change: this runs again two frames on, and at the view's re-apply frames).
            int entry = GlossaryEntry(menu);
            if (entry >= 0)
            {
                ApplyGlossary(view, glossary, menu, HostMenus.Entry, entry);
            }

            return;
        }

        if (view.Type == UnlockablesView.Type)
        {
            // Its rows are a recycled list too, sorted in this PC's language: the host's row is highlighted by unlock, never
            // by path (also two frames after a tab change, and at the view's re-apply frames).
            FollowUnlockRow(view, menu, open?.TryCast<UnlockableMenu>());
            return;
        }

        if (open == null || view.Mode != Mode.Real || menu.Selection == null || menu.Selection.Kind != MenuTargetKind.Path)
        {
            return;
        }

        if (view.Type == CanadaViews.Type && !view.CardsUp)
        {
            return; // its cards come after its dialogue (Tick applies the host's highlight then)
        }

        if (view.Type == MenuPayloads.EventChoice && (!view.IntroOver || view.Swapping))
        {
            // Its cards are up (and its own default selected) only once its intro is over, or its new cards are in: the
            // host's selection is applied then.
            return;
        }

        AbstractNavigable? navigable = MenuTargets.Find(open.transform, menu.Selection);
        NavigationController? navigation = MenuGame.Module()?.NavigationController;
        if (navigable == null || navigation == null)
        {
            if (view.Frame >= ReselectFrames[^1] && !SelectionPending(view))
            {
                Missed(view, $"the host's selection {menu.Selection}");
            }

            return;
        }

        if (navigation.CurrentNavigable?.Pointer != navigable.Pointer)
        {
            Try(view, "selecting as the host did", () => navigation.SetCurrentNavigable(navigable, true, true));
        }
    }

    /// <summary>
    /// True while a run-start view's cards aren't up yet: the old squad menu makes each step's cards after its rolls and
    /// reveal, the lineup its benchwarmers' in its own load. The host's cursor on one is applied once they are (a miss
    /// before then isn't one).
    /// </summary>
    private static bool SelectionPending(View view) => view.Type switch
    {
        RunStartCapture.OldSquad => view.RunStart?.CardsUp != true,
        RunStartCapture.Benchwarmer => view.RunStart?.Bench?.Applied != true,
        ControllerViews.Type => view.Controllers?.RulesUp != true,
        _ => false,
    };

    private void ApplyValue(View view, MirroredMenu menu, MenuTarget target, int index)
    {
        AbstractMenu? open = view.Menu;
        if (open == null || view.Mode != Mode.Real || IsSound(target, index))
        {
            return; // a sound (a press, a back) plays once, for the host's live event (Sounded)
        }

        if (MenuSounds.TryReadNavMode(target, index, out int mode))
        {
            FollowNavMode(view, mode);
            return;
        }

        if (target.Kind == MenuTargetKind.Path && MenuHold.TryRead(index, out bool held))
        {
            // The host holds (or let go of) a shortcut: the same shortcut's own hold event here, once this copy is ready.
            view.Holds.Want(target, held);
            ApplyHolds(view, open);
            return;
        }

        if (target.Kind != MenuTargetKind.Item)
        {
            ApplyList(view, open, target, index);
            return;
        }

        if (ApplyState(view, menu, open, target, index) && view.Mode == Mode.Real)
        {
            // A step change resets the menu's own list selectors (the squad menu picks this PC's default difficulty) and
            // selects its own default a frame later: the host's values again now, its selection two frames on.
            foreach (KeyValuePair<MenuTarget, int> value in menu.Values.Where(v => v.Key.Kind == MenuTargetKind.Path && MenuHold.IsListIndex(v.Value)).ToList())
            {
                ApplyList(view, open, value.Key, value.Value);
            }

            view.ReselectFrame = view.Frame + 2;
            view.StepFrame = view.Frame;
        }
    }

    /// <summary>
    /// The host's holds and releases due on this copy (<see cref="DetailsHolds"/>), once it's ready for them: up a few frames
    /// (its shortcuts register their handlers as it shows), an event menu's intro over and no swap under way (its details
    /// handlers ignore a hold before). Each fires the shortcut's own hold event; the first in each kind of menu is logged
    /// with what it shows, a hold that didn't reach the game's details state is warned about once per kind, and the rest are
    /// counted (logged when following stops). A shortcut this copy lacks is skipped (logged as a miss) until it's made again.
    /// </summary>
    private void ApplyHolds(View view, AbstractMenu open)
    {
        if (!HoldsReady(view))
        {
            return;
        }

        foreach (HoldStep step in view.Holds.Due())
        {
            UIInputShortcutBase? shortcut = DetailsHolds.Find(open.transform, step.Target);
            if (shortcut == null)
            {
                view.Holds.Skip(step.Target);
                Missed(view, $"the host's held input {step.Target}");
                continue;
            }

            if (!Try(view, step.Held ? "holding the details input as the host does" : "letting go of the details input as the host did", () => DetailsHolds.Fire(shortcut, step.Held)))
            {
                view.Holds.Skip(step.Target);
                if (view.Mode != Mode.Real)
                {
                    return; // it kept failing and shows as text now
                }

                continue;
            }

            view.Holds.Applied(step.Target, step.Held);
            if (step.Held)
            {
                _holdsFollowed[view.LogId] = _holdsFollowed.TryGetValue(view.LogId, out int count) ? count + 1 : 1;
                if (_holdKinds.Add(view.LogId))
                {
                    string shows = DetailsHolds.Shows(open, shortcut);
                    // Straight to the log, not through the menus' line budget: one per menu kind, and a busy minute mustn't drop it.
                    ModLog.Info($"Menus: {view.LogId}: {LogNames.HostTag()} holds {step.Target}; held here too with the game's own hold event" +
                        $" ({(shows.Length > 0 ? $"it shows {shows}" : "no details panel listens to it: its sound only")}). Further holds are counted.");
                    view.HoldReadout = step.Target;
                    view.HoldReadoutAt = FrameClock.Seconds + DetailsHolds.ReadoutSeconds;
                }
            }

            string unfollowed = DetailsHolds.Unfollowed(open, shortcut, step.Held);
            if (unfollowed.Length > 0 && _holdMisses.Add(view.LogId))
            {
                ModLog.Warn($"Menus: {view.LogId}: {LogNames.HostTag()}'s {(step.Held ? "hold" : "release")} of {step.Target} didn't reach {unfollowed} here " +
                    "(their own handler didn't run); this PC may show less than the host's screen while it's held.");
            }
        }
    }

    /// <summary>
    /// The host's details panels' detailed stats on this copy's own panels (<see cref="DetailsHolds.FollowDetailedStats"/>):
    /// the first switch in each kind of menu is logged, the rest counted (logged when following stops).
    /// </summary>
    private void FollowDetailedStats(View view, AbstractMenu open, long hostValue)
    {
        int switched = 0;
        if (!Try(view, "showing the host's detailed stats", () => switched = DetailsHolds.FollowDetailedStats(open, hostValue)) || switched == 0)
        {
            return;
        }

        _detailedFollowed[view.LogId] = _detailedFollowed.TryGetValue(view.LogId, out int count) ? count + 1 : 1;
        if (_detailedKinds.Add(view.LogId))
        {
            // Straight to the log, not through the menus' line budget: one per menu kind.
            ModLog.Info($"Menus: {view.LogId}: {LogNames.HostTag()}'s skater details show their {(hostValue == 1 ? "detailed" : "plain")} stats; {switched} panel(s) here switched with " +
                "the game's own switch. Further switches are counted.");
        }
    }

    /// <summary>
    /// The first followed hold in this kind of menu: what this PC's copy shows a moment later, in the words the host's log
    /// uses for its own screen (<see cref="DetailsHolds.Readout"/>), to compare. Never throws.
    /// </summary>
    private static void ReadOutHold(View view, MenuTarget element)
    {
        try
        {
            if (view.Menu is not { } open || DetailsHolds.Find(open.transform, element) is not { } shortcut)
            {
                return;
            }

            string state = view.Holds.HeldNow().Contains(element) ? "still held" : "let go by then";
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Menus: {view.LogId} here {DetailsHolds.ReadoutSeconds:0.0} s into {LogNames.HostTag()}'s first hold of {element} ({state}): {DetailsHolds.Readout(open, shortcut)}."));
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {view.LogId}: couldn't read out the held details here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>True when the copy here is ready for the host's holds (see <see cref="ApplyHolds"/>).</summary>
    private static bool HoldsReady(View view) =>
        view.Mode == Mode.Real && view.Menu != null && view.Frame >= ReselectFrames[0] &&
        (view.Type != MenuPayloads.EventChoice || (view.IntroOver && !view.Swapping));

    /// <summary>
    /// Before this copy closes: every shortcut still held on it goes up with its own release event (a menu the game keeps
    /// and shows again would otherwise keep the host's old hold, its keyword boxes up with nothing held), and the copy's
    /// hold state is forgotten (the host's holds stay wanted for a copy made again). Never throws.
    /// </summary>
    private static void ReleaseHolds(View view, AbstractMenu? open)
    {
        IReadOnlyList<MenuTarget> held = view.Holds.HeldNow();
        view.Holds.Reset();
        if (open == null || view.Mode != Mode.Real)
        {
            return;
        }

        foreach (MenuTarget element in held)
        {
            try
            {
                if (DetailsHolds.Find(open.transform, element) is { } shortcut)
                {
                    FriendUi.Apply(() => DetailsHolds.Fire(shortcut, false));
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Menus: {view.LogId}: couldn't let go of the held input {element} before closing it ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    private void ApplyList(View view, AbstractMenu open, MenuTarget target, int index)
    {
        CycleList? list = MenuTargets.Find(open.transform, target)?.TryCast<CycleList>();
        int count = list?.m_Items?.Cast<Il2CppSystem.Collections.ICollection>().Count ?? 0;
        if (list == null || index >= count)
        {
            // Only once its selectors have had time to come up (they're built over a few frames after an open or a step).
            bool settled = view.Frame >= ReselectFrames[^1] && (view.StepFrame < 0 || view.Frame >= view.StepFrame + StepRetryFrames);
            if (list != null && settled)
            {
                // This profile's own view: a squad it hasn't unlocked has fewer (or no) difficulties or modes to pick here.
                // The host's choice is shown over the selector's label.
                DressList(view, list, target);
            }
            else if (settled && view.Type != ControllerViews.Type) // (its rules page loads in the background: FollowRules sets them)
            {
                Missed(view, $"the host's list selector {target} = {index} ({count} items here)");
            }

            return;
        }

        if (list.m_CurIndex != index)
        {
            // The squad menu's selectors only redraw their descriptions on their event; elsewhere the event is left out.
            bool withEvent = view.Type == MenuPayloads.ChooseMeta;
            Try(view, "setting a list selector as the host did", () =>
            {
                list.SelectIndex(index, withEvent);
                list.RefreshCurrentItem();
            });
        }
    }

    /// <summary>
    /// A menu's state: the power-up menu's step (and its picked power-up), the squad menu's step, the run-end menu's sort.
    /// True when the step or the sort changed (the menu's elements moved: the selection is applied again).
    /// </summary>
    private bool ApplyState(View view, MirroredMenu menu, AbstractMenu open, MenuTarget target, int index)
    {
        if (view.Type == RunStartCapture.OldSquad)
        {
            return OldSquadStep(view, target, index);
        }

        if (open.TryCast<EventChoiceMenu>() is { } eventMenu)
        {
            return target.Group == EventCapture.StateGroup && ApplyEventState(view, eventMenu, target.Index, index);
        }

        if (open.TryCast<UnlockableMenu>() is { } unlockables)
        {
            return ApplyUnlockables(view, menu, unlockables, target, index);
        }

        if (open.TryCast<RunEndMenu>() is { } runEnd)
        {
            if (target.Group != HostMenus.Sort)
            {
                return false;
            }

            // Both halves of the host's sort, the one not sent yet as this menu has it.
            int sort = menu.Values.TryGetValue(MenuTarget.AtItem(HostMenus.Sort, 0), out int by) ? by : (int)runEnd.m_CurSortType;
            int order = menu.Values.TryGetValue(MenuTarget.AtItem(HostMenus.Sort, 1), out int way) ? way : (int)runEnd.m_CurSortOrder;
            return RunEndView.SortDiffers(runEnd, sort, order) && Try(view, "sorting as the host did", () => RunEndView.Sort(runEnd, sort, order));
        }

        if (open.TryCast<SkaterPowerupRewardMenu>() is { } powerUps)
        {
            if (target.Group == HostMenus.PowerUpChoice)
            {
                int choices = powerUps.m_PowerUpChoices?.Count ?? 0;
                if (index < choices)
                {
                    powerUps.m_CurrentPowerUpChoice = index;
                }
            }
            else if (target.Group == HostMenus.State && Enum.IsDefined(typeof(SkaterPowerupRewardMenu.EState), index))
            {
                var state = (SkaterPowerupRewardMenu.EState)index;
                if (powerUps.State != state)
                {
                    return Try(view, $"moving to the {state} step", () => powerUps.SetToState(state, target.Index == 1));
                }
            }

            return false;
        }

        if (open.TryCast<ChooseMetaMenu>() is { } squads && target.Group == HostMenus.State)
        {
            // The picking steps, and the exit's animation (SquadExitAnimation); the last exit step is the menu's own hide,
            // never run here: the host's close closes it.
            var state = (ChooseMetaMenu.EState)index;
            if ((state is ChooseMetaMenu.EState.SelectMeta or ChooseMetaMenu.EState.SelectDifficulty) && squads.m_CurrentState != state)
            {
                return Try(view, $"moving to the {state} step", () => squads.SetState(state, true));
            }

            if (state == ChooseMetaMenu.EState.ExitAnimation && squads.m_CurrentState != state)
            {
                SquadExitAnimation(view, squads);
            }
            else if (state == ChooseMetaMenu.EState.Exiting && squads.m_CurrentState != state)
            {
                SquadExiting(view, squads);
            }
        }

        if (open.TryCast<GameMenu>() is { } game && target.Group == HostMenus.Tab)
        {
            // The tabs are made in the same order from the same flags on both PCs: the host's index is this menu's.
            int tabs = game.m_CurrentTabs?.Count ?? 0;
            if (index < tabs && !TabOpen(game, index))
            {
                return Try(view, $"opening tab {index}", () => game.SelectTab_Internal(index));
            }

            if (index >= tabs)
            {
                Missed(view, $"the host's tab {index} ({tabs} tabs here)");
            }
        }

        if (open.TryCast<GlossaryMenu>() is { } glossary)
        {
            return ApplyGlossary(view, glossary, menu: _mirror.TryGet(view.Serial, out MirroredMenu mirrored) ? mirrored : null, target.Group, index);
        }

        return false;
    }

    /// <summary>True when the in-match menu's tab <paramref name="index"/> is the open one.</summary>
    private static bool TabOpen(GameMenu game, int index)
    {
        GameMenu.TabData? tab = game.m_CurrentTabs[index];
        return tab?.m_RootObject != null && tab.m_RootObject.activeSelf;
    }

    /// <summary>
    /// The host's glossary tab or entry, when this PC's glossary has the same tabs and entries (checked against the host's
    /// counts and the entries' ids; once logged when it hasn't); an entry with its row highlighted (by entry, never by
    /// place: <see cref="GameMenuViews.GlossaryRow"/>). Display only: nothing it does is saved. True when the tab changed
    /// (its rows were made again: the selection is applied again).
    /// </summary>
    private bool ApplyGlossary(View view, GlossaryMenu glossary, MirroredMenu? menu, string group, int index)
    {
        MenuData? data = menu?.State.Data;
        if (group == HostMenus.Tab)
        {
            int tabs = glossary.m_Tabs?.Count ?? 0;
            if (data != null && data.Whole(GameMenuCapture.GlossaryTabs, -1) != tabs)
            {
                Missed(view, $"the host's glossary tabs ({data.Whole(GameMenuCapture.GlossaryTabs, -1)} there, {tabs} here)");
                return false;
            }

            return index < tabs && glossary.m_CurTabIndex != index && Try(view, $"opening glossary tab {index}", () => glossary.SelectTab(index));
        }

        if (group == HostMenus.Entry)
        {
            if (data != null && data.Whole(GameMenuCapture.GlossaryEntries, -1) != GameMenuCapture.EntriesCheck(glossary))
            {
                Missed(view, "the host's glossary entries (this game's differ)");
                return false;
            }

            List<GlossaryEntrySO> entries = GameMenuCapture.Entries(glossary);
            if (index >= entries.Count)
            {
                return false;
            }

            GlossaryEntrySO entry = entries[index];
            if (glossary.m_ShownEntry?.Pointer != entry.Pointer)
            {
                Try(view, "showing the host's glossary entry", () => glossary.ShowEntry(entry));
            }

            NavigationController? navigation = MenuGame.Module()?.NavigationController;
            if (navigation != null && GameMenuViews.GlossaryRow(glossary, entry) is { } row && navigation.CurrentNavigable?.Pointer != row.Pointer)
            {
                Try(view, "highlighting the host's glossary row", () => navigation.SetCurrentNavigable(row, true, true));
            }

            if (data != null)
            {
                // Its list scrolled so the row sits where the host's sits (v1).
                Try(view, "scrolling the glossary's list as the host's", () => GlossaryScrolls.Follow(glossary, entry, data));
            }
        }

        return false;
    }

    /// <summary>The host's glossary entry (its newest step, else as its menu was read; -1 for none).</summary>
    private static int GlossaryEntry(MirroredMenu menu) =>
        menu.Values.TryGetValue(MenuTarget.AtItem(HostMenus.Entry, 0), out int stepped) ? stepped
        : menu.State.Data.Whole(GameMenuCapture.GlossaryEntry, -1) is >= 0 and <= MenuTarget.MaxIndex and long read ? (int)read
        : -1;

    /// <summary>
    /// A click on the host's screen: a button press on a real view, with the element's own click sound for the host's result
    /// and effect; a flash on the panel. Never the click itself.
    /// </summary>
    private void Pressed(View view, MirroredMenu menu, MenuEvent click)
    {
        MenuTarget target = click.Target;
        ClickSound(view, target, click);
        SayCursePick(view, menu, target);
        if (view.Mode == Mode.Real && view.Menu?.TryCast<EventChoiceMenu>() is { } eventMenu && Pick(view, eventMenu, target))
        {
            return;
        }

        if (view.Mode == Mode.Real && view.Menu != null)
        {
            AbstractButton? button = MenuTargets.Find(view.Menu.transform, target)?.TryCast<AbstractButton>();
            if (button != null && Try(view, "showing the host's click", () => button.SetState(AbstractButton.EButtonState.Down, true)))
            {
                view.Pressed = button;
                view.ReleaseFrame = view.Frame + 8;
            }

            return;
        }

        int row = RowOf(menu, target);
        if (row >= 0)
        {
            _pressedSerial = menu.State.Serial;
            _pressedRow = row;
            _pressedUntil = FrameClock.Seconds + PressSeconds;
            _panelDirty = true;
        }
    }

    /// <summary>
    /// One of the mod's own changes to a real view (a selection, a value, a step, a click shown), past the friend's input
    /// lock. A failure is logged (the first few) and never stops the views; a view that keeps failing shows as text.
    /// </summary>
    private bool Try(View view, string what, Action action)
    {
        try
        {
            FriendUi.Apply(action);
            return true;
        }
        catch (Exception ex)
        {
            _failures++;
            view.Failures++;
            if (_failures <= MaxLoggedFailures)
            {
                ModLog.Warn($"Menus: {view.LogId}: failed while {what} ({ModLog.SafeErrorText(ex)})" +
                    $"{(_failures == MaxLoggedFailures ? "; further failures are only counted" : string.Empty)}.");
            }

            if (view.Failures >= MaxViewFailures && view.Mode == Mode.Real)
            {
                ModLog.Warn($"Menus: {view.LogId} can't follow {LogNames.HostTag()}'s menu here ({view.Failures} failures); showing it as text instead.");
                CloseReal(view);
                ToPanel(view, "your game couldn't follow it");
            }

            return false;
        }
    }

    /// <summary>
    /// The view goes (the host's menu closed, or was replaced). <paramref name="closed"/>: the host's close, with the
    /// transition its own exit showed, shown here in the game's order (<see cref="FriendTransitions"/>) when this PC shows
    /// the real menu; null for any other way out. <paramref name="replaced"/>: the host's menu is still open (it was sent
    /// again, a rejoin): only this PC's view of it goes, to be made again.
    /// </summary>
    private void Close(View view, MenuEvent? closed = null, bool replaced = false)
    {
        TallyClosing(view);
        if (view.Type == MenuPayloads.PermanentUnlock && !replaced)
        {
            // The host's look at its new unlocks is over: this player's own of the run are behind it too (the ledger's as well).
            string own = UnlockViews.OwnText();
            int fromLedger = UnlockViews.ScreenClosed();
            if (own != "none" || fromLedger > 0)
            {
                Log($"Menus: {LogNames.HostTag()}'s {view.LogId} closed; this player's new unlocks of the run ({own}) are cleared" +
                    $"{(fromLedger > 0 ? $", with the {fromLedger} kept for its screen in this PC's progress ledger" : string.Empty)}.");
            }
        }

        _views.Remove(view.Serial);
        Abandon(view);
        if (closed?.Reason == MenuCloseReason.Back)
        {
            BackSound(view);
        }

        ExhibitionStarts(view, closed);
        bool faded = closed != null && view.Mode == Mode.Real && view.Menu != null && FadeBeforeClose(view);
        MenuTransition transition = closed?.Transition ?? MenuTransition.None;
        if (view.Type == MenuPayloads.RunEnd && closed != null)
        {
            // The host's run-end menu exits with the loading transition when no new-unlocks menu follows on its screen; this
            // player's own may (OwnUnlocks), and then the copy exits with the game's progress transition before it.
            transition = OwnUnlocks.RunEndClosing(view.Serial, transition, view.Menu);
        }

        if (view.OwnExit != OwnExitState.None)
        {
            // Its own exit plays here: the transition is the one its own hide shows.
            if (view.OwnExit == OwnExitState.Hidden)
            {
                string what = view.RunStart?.Set?.Mode == RunPreviewSet.SuperStarOnly ? "the superstar pick" : "the old school draft";
                Log($"Menus: {view.LogId} closed here with its own exit ({what}; {MenuExits.Name(transition)} on {LogNames.HostTag()}'s screen), as on {LogNames.HostTag()}'s screen.");
            }

            transition = MenuTransition.None;
        }

        if (transition != MenuTransition.None && view.Mode == Mode.Real && view.Menu is { } open && !open.IsHidden && !open.IsHiding)
        {
            CloseReal(view, transition);
        }
        else
        {
            CloseReal(view, afterFade: faded);
        }

        DisarmTitle(view);
        if (view.Type == BrawlViews.Type)
        {
            BrawlGone();
        }

        EventSpeech.Closed(view.Serial);
        BenchClosed(view);
        DisarmRunStart(view);
        view.Event = null;
        view.NextSet = null;
    }

    /// <summary>A view's open request is no longer wanted: if it still opens, its menu is closed once it's up.</summary>
    private void Abandon(View view)
    {
        // An event menu still opening keeps its arming: if it opens after all, it shows the host's choices until it's
        // closed (once up), rather than whatever its event last held.
        if (view.Request == null)
        {
            return;
        }

        _orphans.Add(new Orphan(view.Request, view.Type, FrameClock.Seconds));
        view.Request = null;
        if (_orphans.Count > MaxOrphans)
        {
            _orphans.RemoveAt(0);
        }
    }

    private void CloseWhenUp(AbstractMenu menu)
    {
        if (!_late.Any(l => l.Menu.Pointer == menu.Pointer))
        {
            _late.Add(new Late(menu, MenuGame.MenuId(menu), FrameClock.Seconds));
        }
    }

    /// <summary>True for a real-view class with no <c>InitData</c> of its own: this PC's copy is bound at its <c>Show</c>.</summary>
    private static bool BindsAtShow(string type) => PromptViews.BindsAtShow(type) || type is WorldMapType or BrawlViews.Type;

    private bool IsOurs(AbstractMenu menu) =>
        _views.Values.Any(v => v.Menu is { } open && open.Pointer == menu.Pointer) || _late.Any(l => l.Menu.Pointer == menu.Pointer);

    /// <summary>
    /// Closes a real view's menu with the base close. True when there was one to close. With <paramref name="transition"/>,
    /// the host's menu exit showed it: it shows here too, before the close (the menu's exit shows it first) or once the
    /// copy has hidden (<see cref="FriendTransitions"/>). With <paramref name="afterFade"/>, the game's fade to black has just
    /// started here: the close waits for it, as the menu's own exit does. A menu running its own exit (the old squad menu) is
    /// left to it (and closed with the base close if it never gets there).
    /// </summary>
    private bool CloseReal(View view, MenuTransition transition = MenuTransition.None, bool afterFade = false)
    {
        AbstractMenu? open = view.Menu;
        SpeechClosing(view, open);
        ReleaseHolds(view, open);
        view.Menu = null;
        view.Pressed = null;
        if (view.Type == MenuPayloads.EventChoice)
        {
            // Its NPC's line, if one plays, ends with it (one played stays played if the menu opens again).
            EventSpeech.Stop(view.Serial);

            // The Nurse's procedure screen opened here closes, and a roster rebuilt here that no screen took is cleaned up.
            NurseClosed(view);
        }

        if (open == null || view.Mode != Mode.Real)
        {
            return false;
        }

        if (view.OwnExit == OwnExitState.Running)
        {
            // Its own exit is still in its animations: closing it now would cancel them (its waits share the reveal's
            // cancel). It hides itself, then shows its transition.
            Log($"Menus: {view.LogId} closes itself here with its own exit (still playing it).");
            string exiting = view.LogId;
            FriendTransitions.WatchOwnExit(open, exiting, () => CloseMenu(open, exiting));
            return true;
        }

        if (afterFade)
        {
            string fading = view.LogId;
            FriendTransitions.CloseAfter(RewardFadeRules.FadeSeconds, fading, () => CloseMenu(open, fading));
            return true;
        }

        if (transition != MenuTransition.None && MenuExits.TransitionFirst(view.Type))
        {
            string id = view.LogId;
            FriendTransitions.BeforeClose(transition, id, () => CloseMenu(open, id));
            return true;
        }

        if (CloseMenu(open, view.LogId))
        {
            if (transition != MenuTransition.None)
            {
                FriendTransitions.AfterClose(transition, view.LogId, open);
            }
            else
            {
                Log($"Menus: closed {view.LogId} here, as on {LogNames.HostTag()}'s screen.");
            }
        }

        return true;
    }

    /// <summary>
    /// The base close (<c>Hide_Internal</c>), so the menu's own close code (end-of-reward events, the squad menu's exit)
    /// never runs here; what that code would stop (reveals, sounds) is stopped first. False when it failed (logged).
    /// </summary>
    private static bool CloseMenu(AbstractMenu open, string id)
    {
        try
        {
            if (open.TryCast<ControllerAssignmentMenu>() is { } controllers)
            {
                // The game keeps Play Now's for the title: its rules page goes first.
                ExhibitionScreen.BeforeClose(controllers);
            }
            else if (open.TryCast<RelicRewardMenu>() is { } relics)
            {
                relics.CancelReveal();
            }
            else if (open.TryCast<SkaterPowerupRewardMenu>() is { } powerUps)
            {
                powerUps.CancelReveal();
            }
            else if (open.TryCast<OldSquadMenu>() is { } oldSquad)
            {
                // Its own close would show the transition to the host's next screen; the base close only hides it.
                oldSquad.CancelReveal();
            }
            else if (open.TryCast<EventChoiceMenu>() is { } eventMenu)
            {
                // Its own close is what the host's game does at the event's end (after its exit, refused here): the intro
                // cancelled, faces released, its fade and exit sound; it raises only the map's sub-menu events the host's
                // map gets too. The base close is the fallback.
                if (!open.IsHidden && !open.IsHiding)
                {
                    try
                    {
                        _ = eventMenu.Hide();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        ModLog.Warn($"Menus: the event menu's own close failed on this PC ({ModLog.SafeErrorText(ex)}); closing it with the base close.");
                    }
                }
            }
            else if (open.TryCast<WorldMapMenu>() is { } mapMenu && !open.IsHidden && !open.IsHiding)
            {
                // Its own close, as the host's game closes it: it raises the map's sub-menu closed event (this PC's map
                // got its opened event at the Show). The base close is the fallback.
                try
                {
                    mapMenu.BackOut();
                    return true;
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"Menus: the map menu's own close failed on this PC ({ModLog.SafeErrorText(ex)}); closing it with the base close.");
                }
            }
            else if (open.TryCast<BrawlFightMenu>() is { } brawl && !open.IsHidden && !open.IsHiding)
            {
                // Its own close, as the host's game closes it (its animator back on, then the base hide with its exit); it
                // touches nothing else. The base close is the fallback.
                try
                {
                    _ = brawl.Hide();
                    return true;
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"Menus: the brawl menu's own close failed on this PC ({ModLog.SafeErrorText(ex)}); closing it with the base close.");
                }
            }
            else if (open.TryCast<CanadaCurseMenu>() is { } curse)
            {
                // Its own exit (refused here) puts its speech canvas back and hides its blurred backdrop.
                CanadaViews.BeforeClose(curse);
            }
            else if (open.TryCast<PermanentUnlockMenu>() is { } unlocks)
            {
                // Its own close stops these (its exit transition, when the host's showed one, is shown before this close:
                // FriendTransitions).
                UnlockViews.BeforeClose(unlocks);
            }
            else if (open.TryCast<ChooseMetaMenu>() is { } squads)
            {
                // Its own close (skipped) stops its sounds and its audio snapshot. Its "don't commit" switch is only ever
                // set by a data object the game itself never passes: put back, so a copy the game keeps and shows again
                // later (this PC's own new run) commits as it should.
                squads.StopZamboniSound();
                squads.StopGauntletSound();
                squads.StopAudioSnapshot(Il2CppFMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                squads.m_DoNotSetChoice = false;
            }

            if (!open.IsHidden && !open.IsHiding)
            {
                _ = open.Hide_Internal(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"Menus: couldn't close {id} on this PC", ex);
            return false;
        }
    }

    /// <summary>One line for the log, up to <see cref="LinesPerMinute"/> a minute (a long run opens hundreds of menus, a busy screen many a minute).</summary>
    private void Log(string line)
    {
        if (_lines.TrySpend())
        {
            ModLog.Info(line);
        }
    }

    private void ToPanel(View view, string why)
    {
        if (NoteInstead(view, why))
        {
            return; // the title's unlockables and run history: a note says why
        }

        _tally.Text(view.Serial, why);
        view.Mode = Mode.Panel;
        view.Why = why;
        view.KeptOff = false;
        _panelDirty = true;
    }

    /// <summary>A view shown as nothing at all, on purpose (<paramref name="why"/>): no real menu and no text panel; tried again as a waiting one is.</summary>
    private void KeepOff(View view, string why)
    {
        _tally.Hidden(view.Serial, why);
        view.Mode = Mode.Panel;
        view.Why = why;
        view.KeptOff = true;
        _panelDirty = true;
    }

    private void Missed(View view, string what)
    {
        _misses++;
        if (_misses <= MaxLoggedMisses)
        {
            ModLog.Warn($"Menus: {view.LogId}: couldn't find {what} in this PC's menu{(_misses == MaxLoggedMisses ? " (further misses aren't logged)" : string.Empty)}.");
        }
    }

    /// <summary>
    /// The panel shows the newest host menu when it's one shown as text, or a real one still opening after a moment (a real
    /// view on top needs no panel), or the host's Nurse procedure screen when it has taken the host's event screen over
    /// (over the real menu, or in place of the event's text). Its rows are worked out once per version of the host's data.
    /// </summary>
    private void RefreshPanel()
    {
        MirroredMenu? top = _mirror.Open.Count > 0 ? _mirror.Open[^1] : null;
        if (top == null || !_views.TryGetValue(top.State.Serial, out View? view))
        {
            MenuPanel.Clear();
            return;
        }

        bool takenOver = view.Type == MenuPayloads.EventChoice && view.Mode != Mode.Note &&
            top.Values.TryGetValue(MenuTarget.AtItem(EventCapture.StateGroup, EventCapture.Takeover), out int takeover) && takeover != 0;
        if (takenOver)
        {
            if (NurseRealUp(view))
            {
                // The procedure screen is up here for real (or opening): no text over it.
                MenuPanel.Clear();
                return;
            }

            ShowNurse(view, top);
            return;
        }

        if (view.Mode == Mode.Real || (view.Mode == Mode.Pending && !view.PanelShown) || Quiet(view) || view.Type == BrawlViews.Type || (view.Mode == Mode.Panel && view.KeptOff))
        {
            // The brawl menu is never text: a panel over the match would hide the play (BrawlViews). One kept off shows nothing (KeepOff).
            MenuPanel.Clear();
            return;
        }

        string title = Titles.Of(top.State);
        if (view.Mode == Mode.Note)
        {
            if (NoteAsToast(view))
            {
                // A normal path's note is the game's own toast (FriendMenus.NoteToasts.cs); the text panel is its fallback.
                MenuPanel.Clear();
                return;
            }

            string noteLine = view.NoteWord?.Text(NativeLobby.LobbyLocalization.Language(), HostName) ?? $"{(_host.Length > 0 ? _host : "The host")} {view.Note}";
            MenuPanel.Show(new PanelContent(title, _host, noteLine, Array.Empty<PanelRow>(), Array.Empty<string>(), -1, -1));
            return;
        }

        if (!ReferenceEquals(view.RowsOf, top.State) || view.RowsNurse)
        {
            view.RowsOf = top.State;
            view.RowsNurse = false;
            view.Choices = top.State.Data.Group(MenuPayloads.Choice).Select(row => new PanelRow(TextOf(row), Titles.Words(row.Text("kind")), row.Flag("off"))).ToList();

            // A controller screen's teams and players first, each player with its colour; then the other lines.
            var lines = new List<string>();
            var colours = new List<UnityEngine.Color?>();
            foreach (string text in top.State.Data.Group(MenuPayloads.Teams).Select(TextOf).Where(t => t.Length > 0))
            {
                lines.Add(text);
                colours.Add(null);
            }

            foreach (MenuItem row in top.State.Data.Group(MenuPayloads.Player))
            {
                string text = PlayerText(row);
                if (text.Length > 0)
                {
                    lines.Add(text);
                    colours.Add(row.TryGet("color", out _) ? MenuPayloads.FromRgba(row.Whole("color", -1)) : null);
                }
            }

            foreach (string text in top.State.Data.Group(MenuPayloads.Line).Select(TextOf).Where(t => t.Length > 0))
            {
                lines.Add(text);
                colours.Add(null);
            }

            view.Lines = lines;
            view.LineColors = colours.Any(c => c != null) ? colours : null;
        }

        int selected = top.Selection == null ? -1 : RowOf(top, top.Selection);
        int pressed = _pressedSerial == top.State.Serial ? _pressedRow : -1;
        string note = view.Mode == Mode.Pending ? "Opening it here…" : view.Why.Length > 0 ? $"Shown as text: {view.Why}." : string.Empty;
        MenuPanel.Show(new PanelContent(title, _host, note, view.Choices, view.Lines, selected, pressed) { LineColors = view.LineColors });
    }

    /// <summary>
    /// The host's Nurse procedure screen as text (it has taken the host's event screen over): over the real event menu, or
    /// after why the event itself is text; titled with the procedure in this PC's language, with the host's highlighted row.
    /// </summary>
    private void ShowNurse(View view, MirroredMenu top)
    {
        if (!ReferenceEquals(view.RowsOf, top.State) || !view.RowsNurse)
        {
            view.RowsOf = top.State;
            view.RowsNurse = true;
            view.Nurse = NurseScreenText.From(top.State.Data);
            view.NurseTitle = NurseScreen.Title(view.Nurse?.Procedure ?? -1);
            view.Choices = view.Nurse == null ? Array.Empty<PanelRow>() : view.Nurse.Rows.Select(row => new PanelRow(row.Text, string.Empty, false)).ToList();
            view.Lines = view.Nurse?.Lines ?? Array.Empty<string>();
            SayNurseText(view);
        }

        // Over the real menu (or one still opening), the note says why this part is text; over the event's own text, why
        // that is text comes first.
        bool over = view.Mode is Mode.Real or Mode.Pending || view.Why.Length == 0;
        string why = NurseTextWhy(view);
        string about = view.Nurse != null ? view.Nurse.Note(over ? why : string.Empty)
            : $"{(_host.Length > 0 ? _host : "The host")}'s Nurse procedure screen is opening…";
        string note = over ? about : $"Shown as text: {view.Why}. {about}";
        MenuPanel.Show(new PanelContent(view.NurseTitle, _host, note, view.Choices, view.Lines, view.Nurse?.SelectedRow ?? -1, -1));
    }

    /// <summary>
    /// A controller screen player's line as this PC says it: "you" for this player's own pad on the host, the friend's name
    /// for another's, the host's PC for the host's own players.
    /// </summary>
    private string PlayerText(MenuItem row)
    {
        if (row.Flag(ControllerScreenRow.OffKey))
        {
            return string.Empty; // a disabled icon: not on the host's screen
        }

        long slot = row.Whole("slot", -1);
        if (slot is < 0 or > 99)
        {
            return row.Text("text");
        }

        ulong steam = unchecked((ulong)row.Whole("steam"));
        string name = MenuGame.Plain(row.Text("name"), 32);
        string who = steam != 0 && steam == _self ? "you" : name.Length > 0 ? name : $"{(_host.Length > 0 ? _host : "the host")}'s PC";

        // An older host sends no device: the keyboard flag says as much as it knew.
        string device = MenuGame.Plain(row.Text("device"), 16);
        if (device.Length == 0 && row.Flag("keyboard"))
        {
            device = "keyboard";
        }

        return MenuPayloads.PlayerLine((int)slot, who, MenuGame.Plain(row.Text("side"), 24), MenuGame.Plain(row.Text("position"), 40), MenuGame.Plain(row.Text("skater"), 40),
            device, row.Flag("ai"), row.Flag("ready"));
    }

    /// <summary>A row's text: its term in this PC's language when it has one the game can translate here, else the host's text.</summary>
    private static string TextOf(MenuItem row)
    {
        string local = MenuGame.Plain(MenuGame.Localized(row.Text("term")), 150);
        return local.Length > 0 ? local : row.Text("text");
    }

    /// <summary>The panel row a path target is on, or -1.</summary>
    private static int RowOf(MirroredMenu menu, MenuTarget target)
    {
        if (target.Kind != MenuTargetKind.Path)
        {
            return -1;
        }

        string path = MenuPayloads.PathText(target);
        int index = 0;
        foreach (MenuItem row in menu.State.Data.Group(MenuPayloads.Choice))
        {
            if (row.Text("path") == path)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>The offers in a menu's data (a reroll changes them; a new line of text on the host's screen doesn't).</summary>
    private static string OfferKey(MenuData data)
    {
        var key = new StringBuilder();
        foreach (MenuItem row in data.Items)
        {
            if (row.Group is MenuPayloads.Relic or MenuPayloads.PowerUp or MenuPayloads.Bench or MenuPayloads.Skater)
            {
                key.Append(row.Group).Append(':').Append(row.Text("id")).Append('/').Append(row.Whole("level")).Append('/').Append(row.Text("stickers")).Append(';');
            }
        }

        return key.ToString();
    }

    /// <summary>A menu this PC asked to open for a host menu that's gone: if it still opens, it's closed once it's up.</summary>
    private readonly record struct Orphan(Il2CppSystem.Object Request, string Type, double Since);

    /// <summary>A menu that opened here for a host menu that's gone, waiting to be up so it can be closed.</summary>
    private readonly record struct Late(AbstractMenu Menu, string Id, double Since);

    /// <summary>One host menu as this PC shows it.</summary>
    private sealed partial class View
    {
        // The last cast of Menu (MenuAs): the wrapper it was made from, and the cast.
        private AbstractMenu? _castOf;
        private Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? _cast;

        public View(uint serial, string id, string type)
        {
            Serial = serial;
            Id = id;
            LogId = HostText.KeyForLog(id);
            Type = type;
        }

        public uint Serial { get; }

        /// <summary>The host's id of the menu, which this PC opens its own by.</summary>
        public string Id { get; }

        /// <summary>The id for a log line (a host chooses it: <see cref="HostText.KeyForLog(string?)"/>).</summary>
        public string LogId { get; }

        public string Type { get; }

        public Mode Mode { get; set; }

        public AbstractMenu? Menu { get; set; }

        /// <summary>The old squad menu's own exit, run here at the host's exit step: armed, running, or its menu hidden.</summary>
        public OwnExitState OwnExit { get; set; }

        /// <summary>A reward menu whose own close on the host faded to black first (its data's flag): the fade runs before its close here.</summary>
        public bool FadeOnClose { get; set; }

        public Il2CppSystem.Object? Request { get; set; }

        /// <summary>The in-match menu's plan (the host's numbers matched to this PC's match), while it's asked for or up.</summary>
        public GameMenuPlan? Plan { get; set; }

        /// <summary>An old squad menu's waits, the host's data and offers built here, and the host's picks.</summary>
        public RunStartView? RunStart { get; set; }

        /// <summary>The frame of the menu's last step change (-1: none): its selectors are set again a few frames on.</summary>
        public int StepFrame { get; set; } = -1;

        /// <summary>The host's held shortcuts in this menu, and which of them are held on the copy here (<see cref="DetailsHolds"/>).</summary>
        public HoldFollower Holds { get; } = new();

        /// <summary>The first held shortcut of this kind of menu, its screen read out for the log at <see cref="HoldReadoutAt"/>; null: none due.</summary>
        public MenuTarget? HoldReadout { get; set; }

        /// <summary>When <see cref="HoldReadout"/> is read out.</summary>
        public double HoldReadoutAt { get; set; }

        /// <summary>When a glossary waiting out the host's warm-ups opens (0: not waiting).</summary>
        public double OpenAt { get; set; }

        /// <summary>An event menu's choices as built here (from its newest set), while it's asked for or up.</summary>
        public EventPlan? Event { get; set; }

        /// <summary>Waiting (until then) for the host's event choices that go with its event menu; 0: not waiting.</summary>
        public double WaitEventUntil { get; set; }

        /// <summary>The version of the host's event choices the menu here shows.</summary>
        public int SetVersion { get; set; }

        /// <summary>The host's next event choices (a page or reroll), waiting for this PC's menu to be ready for them.</summary>
        public EventChoiceSet? NextSet { get; set; }

        /// <summary>When <see cref="NextSet"/> came.</summary>
        public double NextSetSince { get; set; }

        /// <summary>The set version whose pick was shown here (-1: none), so one pick plays once.</summary>
        public int PickedVersion { get; set; } = -1;

        /// <summary>When the menu here got its data.</summary>
        public double BoundAt { get; set; }

        /// <summary>The event menu's intro has been seen playing here.</summary>
        public bool IntroSeen { get; set; }

        /// <summary>The event menu's intro is over here (its cards are up).</summary>
        public bool IntroOver { get; set; }

        /// <summary>The event menu's intro was ended here as the host's ended.</summary>
        public bool IntroSkipped { get; set; }

        /// <summary>The event menu is putting the host's next choices in place here (until its swap ends).</summary>
        public bool Swapping { get; set; }

        /// <summary>The host's event screen is taken over (the Nurse's procedure screen): it shows as text over the real menu here.</summary>
        public bool Overlay { get; set; }

        /// <summary>The event menu's cards were put away here under the host's procedure screen.</summary>
        public bool CardsHidden { get; set; }

        /// <summary>The event menu's NPC said its goodbye here (the host's event exits).</summary>
        public bool GoodbyeSaid { get; set; }

        /// <summary>The panel rows worked out last are the host's procedure screen's (not the menu's own text).</summary>
        public bool RowsNurse { get; set; }

        /// <summary>The host's procedure screen as last read (with <see cref="RowsNurse"/>), or null.</summary>
        public NurseScreenText? Nurse { get; set; }

        /// <summary>The Nurse's procedure screen as a real view here (v1, FriendMenus.Nurse.cs), or null.</summary>
        public NurseView? NurseReal { get; set; }

        /// <summary>The procedure's name in this PC's language, for the panel's title (with <see cref="RowsNurse"/>).</summary>
        public string NurseTitle { get; set; } = string.Empty;

        /// <summary>When the swap started.</summary>
        public double SwapStarted { get; set; }

        public double RequestedAt { get; set; }

        public bool PanelShown { get; set; }

        public string Why { get; set; } = string.Empty;

        public bool WaitScene { get; set; }

        /// <summary>Kept off this PC's screen on purpose, no text panel either (<see cref="KeepOff"/>).</summary>
        public bool KeptOff { get; set; }

        /// <summary>Waiting (until then) for the run the host's menu was made from; 0: not waiting.</summary>
        public double WaitRunUntil { get; set; }

        /// <summary>Waiting (until then) for the host's run summary that goes with its run-end menu; 0: not waiting.</summary>
        public double WaitSummaryUntil { get; set; }

        /// <summary>Waiting (until then) for the host's prompt data that goes with its prompt; 0: not waiting.</summary>
        public double WaitPromptUntil { get; set; }

        /// <summary>Waiting (until then) for shared progress here to apply before the new-unlocks menu opens; 0: not waiting.</summary>
        public double WaitUnlocksUntil { get; set; }

        /// <summary>The Canada curse menu here has shown its cards.</summary>
        public bool CardsUp { get; set; }

        /// <summary>A controller screen's state here (FriendMenus.Controllers.cs), while it's asked for or up.</summary>
        public ControllerView? Controllers { get; set; }

        /// <summary>A wait for the host's data shows nothing until then (0: no wait yet; -1: its quiet second is over).</summary>
        public double QuietUntil { get; set; }

        /// <summary><see cref="Mode.Note"/>: what the host is doing, after its name ("is looking at their run history.").</summary>
        public string Note { get; set; } = string.Empty;

        /// <summary><see cref="Mode.Note"/>: the note's whole line in this player's language, when it has one (the two notes that are a normal path, not a failure); else <see cref="Note"/>, in English.</summary>
        public LobbyWord? NoteWord { get; set; }

        /// <summary>How many times the run-end screen was put right and logged (the first few are).</summary>
        public int DressLogs { get; set; }

        public double RetryAt { get; set; }

        public string OfferKey { get; set; } = string.Empty;

        public int Frame { get; set; }

        public int ReselectFrame { get; set; } = -1;

        public int Failures { get; set; }

        public AbstractButton? Pressed { get; set; }

        public int ReleaseFrame { get; set; }

        public MenuState? RowsOf { get; set; }

        public IReadOnlyList<PanelRow> Choices { get; set; } = Array.Empty<PanelRow>();

        public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

        /// <summary>A colour for each line (a controller screen's players), or null for none.</summary>
        public IReadOnlyList<UnityEngine.Color?>? LineColors { get; set; }

        /// <summary>
        /// <see cref="Menu"/> as <typeparamref name="T"/>, or null (no menu, or not one). The cast is kept for the next call
        /// while the menu is the same: the interop makes a new wrapper, with a GC handle of its own, at every cast, and a real
        /// view's frames ask every frame.
        /// </summary>
        public T? MenuAs<T>()
            where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            AbstractMenu? menu = Menu;
            if (menu is null)
            {
                return null;
            }

            if (ReferenceEquals(_castOf, menu) && _cast is T kept)
            {
                return kept;
            }

            T? cast = menu.TryCast<T>();
            _castOf = cast is null ? null : menu;
            _cast = cast;
            return cast;
        }
    }
}

/// <summary>The spectator panel's names for the host's menus, in words.</summary>
internal static class Titles
{
    /// <summary>A menu's name for the panel and the status box.</summary>
    public static string Of(MenuState state) => state.Type switch
    {
        "EventChoiceMenu" => "Event",
        "CanadaCurseMenu" => "Canada curse",
        RunStartCapture.OldSquad => state.Data.Whole(RunStartCapture.ModeKey, -1) switch
        {
            RunPreviewSet.SuperStarOnly => "Superstar pick",
            RunPreviewSet.FullSequence => "Old school draft",
            _ => "Old squad",
        },
        "BenchwarmerMenu" => "Benchwarmers",
        "ControllerAssignmentMenu" => "Controllers",
        "WorldMapMenu" => "Map menu",
        "LineupMenu" => "Lineup",
        MenuPayloads.Game => state.Data.Text("context") switch
        {
            "EndOfPeriod" => "End of period",
            "EndOfGame" => "End of game",
            _ => "Paused",
        },
        MenuPayloads.RunEnd => RunEndView.Of(state.Data) switch
        {
            RunEndView.Variant.DuringRun => "Run stats",
            RunEndView.Variant.History => "Run history",
            _ => "Run summary",
        },
        MenuPayloads.PermanentUnlock => "New unlocks",
        MenuPayloads.RelicReward => "Relic reward",
        MenuPayloads.PowerUpReward => "Power-up reward",
        MenuPayloads.ChooseMeta => "Choose a squad",
        "CustomizableYesNoPrompt" => "Question",
        "CustomizableDismissablePrompt" => "Notice",
        "GameSettingsMenuV2" or "ShootoutGameSettingsMenuV2" => "Match settings",
        "UnlockableMenu" => "Unlocks",
        _ => Words(state.Type.EndsWith("Menu", StringComparison.Ordinal) && state.Type.Length > 4 ? state.Type[..^4] : state.Type),
    };

    /// <summary>A code name in words ("NurseHealAll": "Nurse heal all").</summary>
    public static string Words(string name)
    {
        var words = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                words.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(c == '_' ? ' ' : c);
            }
        }

        return words.ToString();
    }
}
#endif
