#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the controller screen as a real view (v1, D69 amended):
/// <list type="bullet">
/// <item>The campaign's screen (the map's menu, the pause menu, a host pad's disconnect) opens here with the game's own
/// <c>ShowMenuAsync&lt;ControllerAssignmentMenu&gt;</c> and the game's own metadata for the campaign mode, once this PC holds
/// the host's run (its team panel reads it) and, from a match, shows the host's match. The game builds the same icons on
/// every PC; the patches cut them off this PC's input (MenuPatches.Controllers.cs).</item>
/// <item>The host's state goes into the icons with the game's own setters, in the order <see cref="ControllerScreenPlan"/>
/// gives: at the first frame (nothing slides), at the re-apply frames, and whenever the host's data changes (they slide as
/// on the host's screen); then the screen's own ready check. The host's feedback (a refused move's bump, the arrows, the
/// ready pulse) plays here with the same call on the same elements.</item>
/// <item>Play Now's screen and an exhibition's from its pause menu open the same way, with the metadata the game opens them with;
/// Play Now's two teams and rules page follow the host's too (FriendMenus.Exhibition.cs). The tutorial's, a shootout's and a
/// tournament's stay text, and so does any screen that can't be shown (logged with why).</item>
/// </list>
/// The screen closes with the host's, with the base close: its exits, which move skater control and save its teams and rules,
/// are never reached here.
/// </summary>
internal sealed partial class FriendMenus
{
    private const int ControllerLinesPerMinute = 30;
    private const string ExhibitionControllers = "it's for a mode this version doesn't stream (a shootout's or a tournament's controller screen)";

    private readonly LineBudget _controllerLines = new("Menus (this PC's controller screen)", ControllerLinesPerMinute);

    // ShowsScreen as a delegate, made once (it runs every frame a screen is shown for the host).
    private Func<IntPtr, bool>? _showsScreen;

    /// <summary>Why the host's controller screen can't be shown for real here ("" when it can, or for another menu).</summary>
    private string ControllerBlocker(MenuState state)
    {
        if (state.Type != ControllerViews.Type)
        {
            return string.Empty;
        }

        if (!ControllerScreenState.TryRead(state.Data, out ControllerScreenState? screen, out string problem) || screen == null)
        {
            return problem;
        }

        if (screen.Kind == ControllerScreenKind.NotStreamed)
        {
            return ExhibitionControllers;
        }

        if (screen.Kind == ControllerScreenKind.Tutorial)
        {
            return "it's the tutorial's controller screen (its speech waits for a press here, and a tutorial's match isn't streamed)";
        }

        if (!MenuPatches.ControllersReady)
        {
            return "this game's controller screen couldn't be patched to show it safely";
        }

        if (state.Scene == WorldMap.MatchScene && !_matches.PlayingHostMatch && !_matches.HoldingEndedMatch)
        {
            return NoHostMatch;
        }

        // Its campaign layout shows the run's team (InitCampaignSingleSide reads CampaignState.runData.teamData). An exhibition's
        // shows teams every PC has, or the host's custom ones as they come; its side selection, the match's two.
        return screen.Kind != ControllerScreenKind.Campaign || RunSync.Instance?.HoldingHostRun == true ? string.Empty : "the host's run isn't loaded here";
    }

    /// <summary>The controller screen's data for <paramref name="view"/>: the game's own metadata for the host's screen's mode, with the host's lineup lock (or null with why).</summary>
    private static ControllerAssignmentMenu.Metadata? PrepareControllers(View view, MenuData data, out string problem)
    {
        if (!ControllerScreenState.TryRead(data, out ControllerScreenState? screen, out problem) || screen == null)
        {
            return null;
        }

        view.Controllers = new ControllerView();
        return screen.Kind != ControllerScreenKind.Campaign ? ExhibitionScreen.Request(screen) : new ControllerAssignmentMenu.Metadata(false, ControllerAssignmentMenu.EMode.CampaignSingleSide, false,
            isLineupLocked: screen.LineupLocked, isOnboarding: false, isOnboardingFromPause: false);
    }

    /// <summary>
    /// The controller screen <paramref name="view"/> asked for got its data (before its icons are made): from now on its icons
    /// are the host's, and as its Show makes them they already take the host's newest flags and colours (the patches read
    /// them from <see cref="ControllerViews"/>).
    /// </summary>
    private void BindControllers(View view, AbstractMenu menu)
    {
        if (view.Type != ControllerViews.Type || menu.TryCast<ControllerAssignmentMenu>() is not { } controllers)
        {
            return;
        }

        ControllerViews.Mirror(controllers);
        view.Controllers = new ControllerView();
        if (_mirror.TryGet(view.Serial, out MirroredMenu mirrored) && ControllerScreenState.TryRead(mirrored.State.Data, out ControllerScreenState? screen, out _) && screen != null)
        {
            ControllerViews.Want(controllers, screen);
        }
    }

    /// <summary>
    /// A real controller screen's frames: the host's state goes in at its first frame (once its icons are made), at the
    /// re-apply frames (its icons' own start refreshes their glyphs) and whenever the host's data changed.
    /// </summary>
    private void ControllerTick(View view, MirroredMenu menu, ControllerAssignmentMenu open)
    {
        ControllerGlyphViews.Follow(open, view.Controllers?.State, menu.State.Data, _self, view.Frame); // the other players' own button names
        ControllerView controllers = view.Controllers ??= new ControllerView();
        bool due = view.Frame == 1 || Array.IndexOf(ReselectFrames, view.Frame) >= 0 || !ReferenceEquals(controllers.AppliedOf, menu.State);
        if (due)
        {
            ApplyControllers(view, menu, open, controllers);
        }

        if (view.Mode == Mode.Real)
        {
            ExhibitionTick(view, open, controllers, due);
        }
    }

    /// <summary>
    /// The host's controller screen into this PC's: each icon's control flags, side, lock, ready, disabled state, colour and
    /// glyphs with the game's own calls (<see cref="ControllerViews.Run"/>), then the screen's own ready check. Data that
    /// can't be read shows the screen as text from here.
    /// </summary>
    private void ApplyControllers(View view, MirroredMenu menu, ControllerAssignmentMenu open, ControllerView controllers)
    {
        if (!ReferenceEquals(controllers.StateOf, menu.State))
        {
            if (!ControllerScreenState.TryRead(menu.State.Data, out ControllerScreenState? read, out string problem) || read == null)
            {
                ModLog.Warn($"Menus: {view.LogId}: {problem}; showing it as text from here.");
                CloseReal(view);
                ToPanel(view, problem);
                return;
            }

            controllers.State = read;
            controllers.StateOf = menu.State;
        }

        ControllerScreenState? screen = controllers.State;
        if (screen == null || open.IsHidden || open.m_Players == null || open.m_Players.Count == 0)
        {
            return; // its icons are made (or, a copy the game kept, made again) in its Show, still to come
        }

        ControllerViews.Want(open, screen);
        List<(ControllerAssignmentPlayer Icon, ControllerIconShown Shown)> icons = ControllerViews.Icons(open);
        bool first = !controllers.Applied;
        ControllerPlan plan = ControllerScreenPlan.For(icons.Select(i => i.Shown).ToList(), screen, _self, first);
        controllers.AppliedOf = menu.State;
        if (plan.Waiting)
        {
            return; // the host's first read, before its icons were made: its next comes within a frame or two
        }

        var bySlot = new Dictionary<int, ControllerAssignmentPlayer>();
        foreach ((ControllerAssignmentPlayer icon, ControllerIconShown shown) in icons)
        {
            bySlot.TryAdd(shown.Slot, icon);
        }

        foreach (ControllerStep step in plan.Steps)
        {
            if (!bySlot.TryGetValue(step.Slot, out ControllerAssignmentPlayer? icon) ||
                !Try(view, $"giving P{step.Slot + 1}'s icon the host's {step.Kind.ToString().ToLowerInvariant()}", () => ControllerViews.Run(open, icon, step)))
            {
                if (view.Mode != Mode.Real)
                {
                    return; // it kept failing and shows as text now
                }
            }
        }

        if (plan.RefreshReady && !Try(view, "running the screen's own ready check", open.CheckReadyState) && view.Mode != Mode.Real)
        {
            return;
        }

        controllers.Applied = true;
        if (plan.Missing.Count > 0 && !controllers.MissedLogged)
        {
            controllers.MissedLogged = true;
            Missed(view, $"the host's player(s) {string.Join(", ", plan.Missing.Select(s => $"P{s + 1}"))} (this PC's screen has {icons.Count} icon(s))");
        }

        if (first)
        {
            int playing = screen.Icons.Count(i => !i.Off && i.Side != ControllerScreenState.CentreSide);
            int ready = screen.Icons.Count(i => !i.Off && i.Ready);
            Log($"Menus: {view.LogId} shows {LogNames.HostTag()}'s players here: {screen.Icons.Count(i => !i.Off)} icon(s), {playing} playing, {ready} ready " +
                $"({plan.Steps.Count} change(s) from this PC's own); this PC's own input is off on it.");
            return;
        }

        if (plan.Steps.Any(s => s.Kind is not (ControllerStepKind.Colour or ControllerStepKind.Glyphs)) && _controllerLines.TrySpend())
        {
            bool campaign = screen.Mode == ControllerScreenState.CampaignMode;
            ModLog.Info($"Menus: {view.LogId} follows {LogNames.HostTag()}'s screen here: {ControllerScreenPlan.Describe(plan, campaign)}" +
                $"{(screen.BothReady ? "; everyone's ready" : string.Empty)}.");
        }

        if (plan.RefreshReady && open.AreBothTeamsReady != screen.BothReady && !controllers.ReadyMismatchLogged)
        {
            controllers.ReadyMismatchLogged = true;
            ModLog.Warn($"Menus: {view.LogId}: this PC's screen says {(open.AreBothTeamsReady ? "everyone's ready" : "not everyone's ready")}, " +
                $"{LogNames.HostTag()}'s says {(screen.BothReady ? "everyone's ready" : "not everyone's ready")} (logged once).");
        }
    }

    /// <summary>
    /// A host's step on its controller screen's feedback (<see cref="ControllerFeedback"/>): played on this PC's real screen with
    /// the game's own call, live only (never from the re-apply frames, nor before the screen is up a few frames). False when
    /// <paramref name="target"/> isn't a feedback step.
    /// </summary>
    private bool ControllerFeedbackStep(View view, MenuTarget target, int value)
    {
        if (target.Kind != MenuTargetKind.Item || target.Group != ControllerFeedback.Group)
        {
            return false;
        }

        if (view.Menu?.TryCast<ControllerAssignmentMenu>() is not { } open || view.Frame < ReselectFrames[0] ||
            !ControllerFeedback.TryDecode(target.Index, value, out ControllerFeedbackStep step))
        {
            return true;
        }

        string problem = string.Empty;
        bool played = false;
        if (Try(view, "playing the host's feedback", () => played = ControllerViews.PlayFeedback(open, step, out problem)) && !played)
        {
            Missed(view, $"the host's feedback on {ControllerViews.TargetName(step.Target)} ({problem})");
        }
        else if (played && view.Controllers is { FeedbackLogged: false } controllers)
        {
            controllers.FeedbackLogged = true;
            Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s screen played feedback {(EControllerAssignmentFeedback)step.Type}{(step.Negative ? " (refused)" : string.Empty)} " +
                $"on {ControllerViews.TargetName(step.Target)}; played here too with the game's own call (further ones aren't logged).");
        }

        return true;
    }

    /// <summary>Every frame: the screens no view shows any more are forgotten (their icons are this PC's own again at their next use).</summary>
    private void ControllersTick()
    {
        if (ControllerViews.MirrorCount > 0)
        {
            ControllerViews.Prune(_showsScreen ??= ShowsScreen);
        }
    }

    /// <summary>True while a view shows the menu at <paramref name="pointer"/>, or it opened late and waits to close (no allocation: run every frame).</summary>
    private bool ShowsScreen(IntPtr pointer)
    {
        foreach (View view in _views.Values)
        {
            if (view.Menu is { } open && open.Pointer == pointer)
            {
                return true;
            }
        }

        foreach (Late late in _late)
        {
            if (late.Menu.Pointer == pointer)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A view's state of the host's controller screen here.</summary>
    private sealed class ControllerView
    {
        /// <summary>The host's state as last read.</summary>
        public ControllerScreenState? State { get; set; }

        /// <summary>The host's menu state <see cref="State"/> was read from.</summary>
        public MenuState? StateOf { get; set; }

        /// <summary>The host's menu state last put into the screen here.</summary>
        public MenuState? AppliedOf { get; set; }

        /// <summary>The host's state went in once (later changes slide).</summary>
        public bool Applied { get; set; }

        public bool MissedLogged { get; set; }

        public bool ReadyMismatchLogged { get; set; }

        public bool FeedbackLogged { get; set; }

        /// <summary>The host's home team's panel here.</summary>
        public TeamSideView Home { get; } = new();

        /// <summary>The host's visitor team's panel here.</summary>
        public TeamSideView Visitor { get; } = new();

        /// <summary>The host's list of custom content last put to use here.</summary>
        public MenuAttachment? ContentOf { get; set; }

        /// <summary>The rules page is up here (the host's cursor on it isn't a miss until then).</summary>
        public bool RulesUp { get; set; }

        /// <summary>The rules are compared every frame until this frame of the view.</summary>
        public int RulesSettleUntil { get; set; }

        public bool RulesWaitLogged { get; set; }
    }
}
#endif
