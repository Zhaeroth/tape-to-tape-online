#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppFMOD.Studio;
using Il2CppFMODUnity;
using Il2CppRogue;
using Il2CppRogue.Powerups.Repository;
using Il2CppRogue.Relics.Repository;
using Il2CppRogue.Rewards;
using Il2CppRogue.Stickers;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the run-start menus:
/// <list type="bullet">
/// <item>The old squad menu (the superstar pick, the old school draft) as a real view: once the host's run start data is
/// here (its <see cref="MenuAttachmentKind.RunPreview"/> attachment), this PC's game loads the host's new run as a preview
/// (<see cref="RunSync.ApplyPreview"/>; the host's first save replaces it), builds the host's offers (skaters with the game's
/// own deserializer, each read back as the host's; rewards from this game's lists), and opens the menu with the preview's
/// team and <c>applyChoice = false</c>. Its rolls return the host's offers (<see cref="MenuPatches"/>), the host's picks run
/// its own click handlers in order (its reveal skipped first when it's behind), and at the host's exit step the menu runs
/// its own exit (it applies nothing; its animations, final sound, hide and transition play as on the host's screen). As
/// text meanwhile, and with why when anything fails.</item>
/// <item>The lineup with benchwarmers opens the same way, with the host's benchwarmers (FriendMenus.Bench.cs); its text panel
/// (its roster as lines, <see cref="RunStartCapture.Bench"/>) is the fallback.</item>
/// <item>While the host's run-start menus are up, this PC plays the run-start music (the host's game does), and while the
/// squad screen is shown for real, the title's background is hidden as the host's game hides it.</item>
/// </list>
/// After the host's first real checkpoint, the log says whether its run has the superstar the host picked.
/// </summary>
internal sealed partial class FriendMenus
{
    private const double RunStartDataWaitSeconds = 8;
    private const double PreviewWaitSeconds = 15;
    private const double StepGiveUpSeconds = 4;
    private const double MusicGraceSeconds = 1.5;
    private const int StepRetryFrames = 20;
    private const string RunStartNotYet = "the host's run start data is still on its way";
    private const string PreviewNotYet = "your game is loading the host's new run";
    private const string LocationParameter = "location";

    private static readonly string[] StepOrder = { RunStartCapture.PowerUpStep, RunStartCapture.SuperStarStep, RunStartCapture.RelicStep, RunStartCapture.ExitStep };

    private readonly HashSet<string> _dressed = new(StringComparer.Ordinal);
    private readonly List<OldSquadPlan> _unshown = new(); // plans built here whose offers no menu took yet (released at the latest when following stops)
    private OldSquadPlan? _armed;
    private EventInstance _music;
    private bool _musicPlaying;
    private double _runStartSeen;
    private bool _backgroundHidden;
    private double _backgroundRetryAt;
    private ExpectedPick? _expectedPick;

    /// <summary>
    /// Whether a run-start view (an old squad menu, the lineup with benchwarmers) can open now. When it can't, it shows as text
    /// (with why, or waiting for the host's data or the preview's load: <see cref="RunStartWaitTick"/> opens it once that's
    /// done) and this is false.
    /// </summary>
    private bool RunStartReady(View view, MirroredMenu menu)
    {
        RunStartView state = view.RunStart ??= new RunStartView();
        string hostProblem = menu.State.Data.Text(RunStartCapture.ProblemKey);
        bool lineup = view.Type == RunStartCapture.Benchwarmer;
        string blocker = lineup && !MenuPatches.BenchReady ? "this game's lineup menu couldn't be patched to show it safely"
            : !lineup && !MenuPatches.RunStartReady ? "this game's old squad menu couldn't be patched to show it safely"
            : lineup && !BenchViews.HostSendsView(menu.State.Data) ? "the host's T2T Online sends no lineup data for it (an older version)"
            : hostProblem.Length > 0 ? hostProblem
            : RunSync.Instance == null ? "this PC's run sync isn't running"
            : string.Empty;
        if (blocker.Length > 0)
        {
            state.Stop();
            ToPanel(view, blocker);
            Log($"Menus: showing {view.LogId} as text, not as the menu: {blocker}.");
            return false;
        }

        MenuAttachment? attachment = _attachments.Get(view.Serial, MenuAttachmentKind.RunPreview);
        if (attachment == null)
        {
            // The host's data follows the menu's open (its rolls come after it shows): it opens once that's in (Attached),
            // as text meanwhile, for a few seconds at most.
            if (state.WaitDataUntil == 0)
            {
                state.WaitDataUntil = FrameClock.Seconds + RunStartDataWaitSeconds;
                Log($"Menus: {view.LogId} waits for {LogNames.HostTag()}'s run start data; as text if that takes over a second.");
            }

            Wait(view, RunStartNotYet);
            return false;
        }

        state.WaitDataUntil = 0;
        if (!attachment.TryGetRunPreview(out RunPreviewSet? set) || set == null)
        {
            state.Stop();
            ToPanel(view, "the host's run start data can't be read here");
            ModLog.Warn($"Menus: {view.LogId}: {LogNames.HostTag()}'s run start data can't be read here; showing it as text.");
            return false;
        }

        state.Set = set;
        RunSync runs = RunSync.Instance!;
        if (runs.HeldPreviewHash == set.RunHash && runs.AppliedCheckpoint == 0)
        {
            return true;
        }

        if (!state.PreviewStarted)
        {
            state.PreviewStarted = true;
            state.WaitPreviewUntil = FrameClock.Seconds + PreviewWaitSeconds;
            Log(string.Create(CultureInfo.InvariantCulture, $"Menus: {view.LogId}: loading {LogNames.HostTag()}'s new run here ({set.RunFile.Length / 1024.0:0.0} KB, its run-start preview) before the menu opens; as text if that takes over a second."));
            runs.ApplyPreview(set.RunFile, set.RunHash, result => state.PreviewResult = result);
        }

        Wait(view, PreviewNotYet);
        return false;
    }

    /// <summary>An old squad view waiting as text, every frame: the host's data, or the preview's load, came (it opens) or didn't (it stays text, with why).</summary>
    private void RunStartWaitTick(View view, MirroredMenu menu)
    {
        RunStartView state = view.RunStart!;
        double now = FrameClock.Seconds;
        string hostProblem = menu.State.Data.Text(RunStartCapture.ProblemKey);
        if (state.WaitDataUntil > 0 && hostProblem.Length > 0)
        {
            // The host's game couldn't send it (its data says why, read again after the open).
            state.Stop();
            ToPanel(view, hostProblem);
            ModLog.Warn($"Menus: {view.LogId}: {hostProblem}; showing it as text.");
            return;
        }

        if (state.WaitDataUntil > 0 && now > state.WaitDataUntil)
        {
            state.Stop();
            ToPanel(view, "the host's run start data didn't come");
            ModLog.Warn($"Menus: {view.LogId} waited {RunStartDataWaitSeconds:0} s for {LogNames.HostTag()}'s run start data, which didn't come; showing it as text.");
            return;
        }

        if (state.PreviewResult is { } result)
        {
            state.PreviewResult = null;
            state.WaitPreviewUntil = 0;
            if (result.Ok && state.Set != null && RunSync.Instance?.HeldPreviewHash == state.Set.RunHash)
            {
                TryReal(view, menu);
                return;
            }

            string why = result.Ok ? "the host's run moved on while it loaded" : result.Reason;
            state.Stop();
            ToPanel(view, $"your game couldn't load the host's new run ({why})");
            ModLog.Warn($"Menus: {view.LogId}: this PC couldn't load {LogNames.HostTag()}'s new run ({why}); showing it as text.");
            return;
        }

        if (state.WaitPreviewUntil > 0 && now > state.WaitPreviewUntil)
        {
            state.Stop();
            ToPanel(view, "your game didn't load the host's new run in time");
            ModLog.Warn($"Menus: {view.LogId}: this PC didn't load {LogNames.HostTag()}'s new run within {PreviewWaitSeconds:0} s; showing it as text.");
        }
    }

    /// <summary>
    /// The old squad menu's data for <paramref name="view"/>: the preview's team, the host's mode and side,
    /// <c>applyChoice = false</c>; and its plan, the host's offers built here (armed when the menu binds). Null, with why,
    /// when an offer can't be built as the host's (or the game's own code throws while building one): what was built so far
    /// is destroyed then.
    /// </summary>
    private OldSquadMenu.Metadata? PrepareOldSquad(View view, out string problem)
    {
        DisarmRunStart(view);
        RunPreviewSet? set = view.RunStart?.Set;
        TeamData? team = set == null ? null : MenuRebuild.Team(out problem);
        if (set == null || team == null)
        {
            problem = set == null ? "the host's run start data isn't here" : $"the host's new run has no team here";
            return null;
        }

        if (set.Menu != RunStartMenu.OldSquad)
        {
            problem = $"the host's run start data is for another menu ({set.Menu})";
            return null;
        }

        if (!Enum.IsDefined(typeof(OldSquadMenu.Mode), set.Mode))
        {
            problem = $"the host's old squad menu has a mode ({set.Mode}) this game doesn't know";
            return null;
        }

        // Every skater and reward made here is this PC's own (noted by the builder): destroyed if it never reaches the menu.
        var builder = new SkaterModels.Builder();
        Il2CppList.List<ForwardData>? superStars = null, random = null;
        Il2CppList.List<PowerUpReward>? powerUps = null;
        Il2CppList.List<RelicReward>? relics = null;
        OldSquadMenu.Metadata? request = null;
        try
        {
            superStars = Skaters(builder, set.SuperStars, "superstar", out problem);
            random = superStars == null ? null : Skaters(builder, set.RandomSkaters, "random skater", out problem);
            powerUps = random == null ? null : PowerUps(builder, set.PowerUps, out problem);
            relics = powerUps == null ? null : Relics(builder, set.Relics, out problem);
            request = relics == null ? null : new OldSquadMenu.Metadata(team, set.IsTeamHome, false, (OldSquadMenu.Mode)set.Mode);
        }
        catch (Exception ex)
        {
            // The game's own code threw (its deserializer, one of its lists): the menu shows as text, with why.
            problem = $"an error while building the host's offers ({ModLog.SafeErrorText(ex)})";
            request = null;
        }

        if (request == null)
        {
            DestroyOffers(builder.Made, "they can't be shown here");
            return null;
        }

        var plan = new OldSquadPlan(set, superStars!, random!, powerUps!, relics!) { Made = builder.Made };
        view.RunStart!.Plan = _armed = plan;
        _unshown.Add(plan);
        problem = string.Empty;
        return request;
    }

    /// <summary>
    /// The view's menu got its data here: its rolls are the host's from now on (the plan is armed for this menu); a lineup's
    /// takes the host's benchwarmers when it loads them.
    /// </summary>
    private void BindRunStart(View view, AbstractMenu menu)
    {
        if (view.RunStart?.Plan is { } plan)
        {
            plan.Menu = menu.Pointer;
            _armed = plan;
        }

        if (view.RunStart?.Bench is { } bench)
        {
            bench.Menu = menu.Pointer;
        }
    }

    /// <summary>A view's plan is no longer wanted (closed, opened again, gave up opening): released.</summary>
    private void DisarmRunStart(View view)
    {
        if (view.RunStart?.Bench is { } bench)
        {
            view.RunStart.Bench = null;
            ReleaseBench(bench);
        }

        if (view.RunStart?.Plan is not { } plan)
        {
            return;
        }

        if (ReferenceEquals(plan, _armed))
        {
            _armed = null;
        }

        view.RunStart.Plan = null;
        ReleaseRunStart(plan);
    }

    /// <summary>
    /// A plan that won't be armed again: the skaters and rewards it made here are destroyed when no menu took its offers
    /// (nothing of the game's holds them then). Once a menu's roll took them, that menu holds them, as the host's game holds
    /// its own offers (the ones not picked go with the next unload of unused assets): they're kept. Never throws.
    /// </summary>
    private void ReleaseRunStart(OldSquadPlan plan)
    {
        _unshown.Remove(plan);
        if (plan.Used || plan.Made.Count == 0)
        {
            return;
        }

        DestroyOffers(plan.Made, "they weren't shown");
        plan.Made = Array.Empty<UnityEngine.Object>();
    }

    /// <summary>Following stopped or started again: every plan no menu took goes, with what it made (its view is gone).</summary>
    private void ReleaseUnshown()
    {
        foreach (OldSquadPlan plan in _unshown.ToList())
        {
            ReleaseRunStart(plan);
        }

        _unshown.Clear();
        foreach (BenchPlan plan in _benchUnshown.ToList())
        {
            ReleaseBench(plan);
        }

        _benchUnshown.Clear();
    }

    /// <summary>Destroys the skaters and rewards made here for the host's offers that won't be shown, and says so. Never throws.</summary>
    private static void DestroyOffers(IReadOnlyList<UnityEngine.Object> made, string why)
    {
        int destroyed = SkaterModels.Destroy(made, "the host's old squad offers");
        if (destroyed > 0)
        {
            ModLog.Info($"Menus: {RunStartCapture.OldSquad}: destroyed the {destroyed} object(s) made here for the host's offers ({why}).");
        }
    }

    /// <summary>
    /// Friend: an old squad menu rolls its skaters. The view's menu gets the host's (built here, every field read back as the
    /// host's; logged once per opening); any other gets none (only a late copy could be one: it's closed once it's up).
    /// </summary>
    internal Il2CppList.List<ForwardData>? OldSquadSkaters(OldSquadMenu menu, RunStartCapture.Offer offer)
    {
        OldSquadPlan? plan = PlanFor(menu);
        if (plan == null)
        {
            ReportRoll(menu);
            return null;
        }

        if (offer == RunStartCapture.Offer.SuperStars)
        {
            RunPreviewSet set = plan.Set;
            string draft = set.Mode == RunPreviewSet.FullSequence
                ? $", {set.PowerUps.Count} power-up(s), {set.Relics.Count} relic(s) and {set.RandomSkaters.Count} random skater(s)"
                : string.Empty;
            Log($"Menus: {MenuGame.MenuId(menu)}: {LogNames.HostTag()}'s {set.SuperStars.Count} superstar(s){draft} are in this PC's menu (its own rolls skipped; " +
                $"the skaters built with the game's deserializer, every field read back as the host's).");
            return plan.SuperStars;
        }

        return plan.RandomSkaters;
    }

    /// <summary>Friend: the old school draft rolls its power-ups: the host's for the view's menu, none for any other.</summary>
    internal Il2CppList.List<PowerUpReward>? OldSquadPowerUps(OldSquadMenu menu) => PlanFor(menu)?.PowerUps;

    /// <summary>Friend: the old school draft rolls its relics: the host's for the view's menu, none for any other.</summary>
    internal Il2CppList.List<RelicReward>? OldSquadRelics(OldSquadMenu menu) => PlanFor(menu)?.Relics;

    /// <summary>The armed plan when it's <paramref name="menu"/>'s, marked as taken by it (the menu holds what it made from here); else null.</summary>
    private OldSquadPlan? PlanFor(OldSquadMenu menu)
    {
        if (_armed is not { } plan || plan.Menu != menu.Pointer)
        {
            return null;
        }

        plan.Used = true;
        _unshown.Remove(plan);
        return plan;
    }

    /// <summary>
    /// Friend: an old squad menu's exit (<c>ApplyAndExit</c>). True (the game's own exit runs) for the view's menu at the
    /// host's exit step (armed by <see cref="PlayStep"/>): with <c>applyChoice = false</c> it applies nothing (its apply
    /// returns at once, and is refused here besides), and it plays the shrink and out animations, the final
    /// sound, its hide and its transition as on the host's screen. Otherwise refused, as a fallback: the view's menu plays
    /// the host's pick (the others hidden, the final sound) and closes when the host's does.
    /// </summary>
    internal bool OldSquadExit(OldSquadMenu menu)
    {
        if (menu.m_IsExiting)
        {
            return false;
        }

        View? view = _views.Values.FirstOrDefault(v => v.Menu is { } open && open.Pointer == menu.Pointer);
        if (view?.OwnExit == OwnExitState.Armed)
        {
            view.OwnExit = OwnExitState.Running;
            FriendTransitions.OwnExit(view.LogId);
            if (view.RunStart is { PickPlaying: true } playing)
            {
                playing.ExitPending = true; // inside the pick's own handler (the superstar pick): said after the pick's line
            }
            else
            {
                LogConfirmed(view);
            }

            return true;
        }

        menu.m_IsExiting = true; // takes no more picks, as the game's own exit
        if (view == null)
        {
            return false;
        }

        try
        {
            FriendUi.Apply(() =>
            {
                menu.HideUnselectedSkaters();
                menu.m_SFX?.PlaySelectedFinal();
            });
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {view.LogId}: the host's final pick couldn't be played here ({ModLog.SafeErrorText(ex)}); it closes with the host's.");
        }

        if (view.RunStart is { PickPlaying: true } state)
        {
            // Inside the pick's own handler (the superstar pick's exit runs inside its click): said after the pick's line.
            state.ExitPending = true;
            return false;
        }

        LogConfirmed(view);
        return false;
    }

    /// <summary>The host's old squad menu applied its picks and is leaving: said once its last pick has been said.</summary>
    private void LogConfirmed(View view) =>
        Log(view.OwnExit == OwnExitState.None
            ? $"Menus: {LogNames.HostTag()} confirmed the picks in {view.LogId}; this PC's menu plays the last one and closes with the host's (nothing is applied here)."
            : $"Menus: {LogNames.HostTag()} confirmed the picks in {view.LogId}; this PC's menu runs its own exit, as the host's game does " +
                "(nothing is applied here: its applyChoice is off, so its apply returns at once, and the apply is refused besides).");

    /// <summary>The host's exit step is due on the view's menu: the menu's own exit may run once (<see cref="OldSquadExit"/>).</summary>
    private static void ArmOwnExit(View view)
    {
        if (view.OwnExit == OwnExitState.None)
        {
            view.OwnExit = OwnExitState.Armed;
        }
    }

    /// <summary>An armed own exit the game didn't start (the pick's handler didn't exit): disarmed.</summary>
    private static void DisarmOwnExit(View view)
    {
        if (view.OwnExit == OwnExitState.Armed)
        {
            view.OwnExit = OwnExitState.None;
        }
    }

    /// <summary>
    /// A step value of the host's old squad menu (a pick): kept, and played in order once this PC's menu is ready for it
    /// (<see cref="OldSquadTick"/>). The host's superstar is noted for the check at its first real checkpoint.
    /// </summary>
    private bool OldSquadStep(View view, MenuTarget target, int index)
    {
        if (Array.IndexOf(StepOrder, target.Group) < 0 || view.RunStart is not { } state)
        {
            return false;
        }

        if (!state.Done.Contains(target.Group))
        {
            state.Steps[target.Group] = index;
        }

        if (target.Group == RunStartCapture.SuperStarStep && state.Set is { } set && index < set.SuperStars.Count)
        {
            FreeAgentData picked = set.SuperStars[index].Model;
            _expectedPick = new ExpectedPick(picked.Id, $"#{picked.Number}"); // by number and id: a host sends the name
        }

        return false;
    }

    /// <summary>
    /// A real old squad view's frames: the host's picks, in order, each through the menu's own click handler once its cards are
    /// up (the menu's reveal skipped first when it's behind the host's); one that can't be played in a few seconds is left
    /// (logged), the host's close closes the menu anyway.
    /// </summary>
    private void OldSquadTick(View view, OldSquadMenu open)
    {
        if (view.RunStart is not { } state)
        {
            return;
        }

        // The menu makes each step's cards after its own rolls and reveal (frames after it opened): the host's cursor is
        // applied again once a new set of cards is up, and a miss isn't said before (SelectionPending).
        (IntPtr, IntPtr, IntPtr) cards = CardsKey(open);
        state.CardsUp = cards != (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (state.CardsUp && cards != state.CardsKey)
        {
            state.CardsKey = cards;
            view.ReselectFrame = view.Frame + 2;
        }

        if (state.Steps.Count == state.Done.Count)
        {
            return;
        }

        foreach (string step in StepOrder)
        {
            if (state.Done.Contains(step) || !state.Steps.TryGetValue(step, out int index))
            {
                continue;
            }

            if (!PlayStep(view, state, open, step, index))
            {
                return; // the next ones wait for it
            }
        }
    }

    /// <summary>One of the host's picks on this PC's menu. True when it's done (or given up); false while it waits for the menu.</summary>
    private bool PlayStep(View view, RunStartView state, OldSquadMenu open, string step, int index)
    {
        double now = FrameClock.Seconds;
        if (state.StepName != step)
        {
            state.StepName = step;
            state.StepSince = now;
            state.Skipped = false;
        }

        if (step == RunStartCapture.ExitStep || open.m_IsExiting)
        {
            if (!open.m_IsExiting)
            {
                // The menu's own exit, as the host's game ran it (OldSquadExit lets it run once it's armed).
                ArmOwnExit(view);
                Try(view, "playing the host's exit", open.ApplyAndExit);
                DisarmOwnExit(view);
            }

            state.Done.Add(step);
            return true;
        }

        if (Taken(open, step) == index)
        {
            state.Done.Add(step);
            return true;
        }

        AbstractNavigable? card = Card(open, step, index);
        if (card != null && !open.m_IsWaitSkip && !open.m_HasMenuOverSelf)
        {
            Action<AbstractNavigable> click = step == RunStartCapture.PowerUpStep ? open.OnClickPowerUp : step == RunStartCapture.SuperStarStep ? open.OnClickSuperStar : open.OnClickRelic;
            bool played;
            state.PickPlaying = true;

            // The host's exit came with this pick (the superstar pick's exit runs inside its click): armed, so
            // the game's own exit runs inside the pick here too.
            if (state.Steps.ContainsKey(RunStartCapture.ExitStep))
            {
                ArmOwnExit(view);
            }

            try
            {
                played = Try(view, "playing the host's pick", () => click(card)) && Taken(open, step) == index;
            }
            finally
            {
                state.PickPlaying = false;
                DisarmOwnExit(view);
            }

            if (played)
            {
                state.Done.Add(step);
                view.ReselectFrame = view.Frame + 3; // the next step's cards select their own default: the host's selection again
                Log($"Menus: {LogNames.HostTag()} picked {Words(step)} {index + 1} in {view.LogId}; the pick plays here (nothing is picked on this PC).");
            }

            if (state.ExitPending)
            {
                // The menu's exit ran inside the pick's handler (the superstar pick): the host's confirm, after its pick.
                state.ExitPending = false;
                LogConfirmed(view);
            }

            if (played)
            {
                return true;
            }
        }
        else if (card != null && open.m_IsWaitSkip && !state.Skipped)
        {
            // This PC's reveal is still playing (the host's is over: it picked): skipped once, as its own skip key does.
            state.Skipped = true;
            Try(view, "skipping the reveal to the host's step", () => Skip(open, step));
        }

        if (now - state.StepSince > StepGiveUpSeconds)
        {
            state.Done.Add(step);
            Missed(view, $"the host's {Words(step)} {index + 1} (its cards weren't up here within {StepGiveUpSeconds:0} s)");
            return true;
        }

        return false;
    }

    /// <summary>
    /// The old squad menu's cards up now (the first shown card of each step's list, once its reveal is over), as a key that
    /// changes with each new set of cards (their native objects; none for a list with no card shown); all none while none
    /// are up. Read every frame the menu is up: no string is made.
    /// </summary>
    private static (IntPtr SkaterCard, IntPtr PowerUpCard, IntPtr RelicCard) CardsKey(OldSquadMenu open)
    {
        if (open.m_IsWaitSkip)
        {
            return default;
        }

        return (Shown(open.m_SkaterChoices?.Count > 0 ? open.m_SkaterChoices[0]?.Navigable : null),
            Shown(open.m_PowerUpChoices?.Count > 0 ? open.m_PowerUpChoices[0]?.Navigable : null),
            Shown(open.m_RelicChoices?.Count > 0 ? open.m_RelicChoices[0]?.Navigable : null));

        static IntPtr Shown(AbstractNavigable? card) => card != null && card.gameObject.activeInHierarchy ? card.Pointer : IntPtr.Zero;
    }

    /// <summary>The menu's own choice for a step (-1: none yet).</summary>
    private static int Taken(OldSquadMenu open, string step) => step switch
    {
        RunStartCapture.PowerUpStep => open.m_CurrentPowerUpChoice,
        RunStartCapture.SuperStarStep => open.m_CurrentSkaterChoice,
        RunStartCapture.RelicStep => open.m_CurrentRelicChoice,
        _ => -1,
    };

    /// <summary>The card a step's pick clicks, once the menu's cards for that step are up (null before).</summary>
    private static AbstractNavigable? Card(OldSquadMenu open, string step, int index)
    {
        switch (step)
        {
            case RunStartCapture.PowerUpStep:
                Il2CppList.List<OldSquadMenu.PowerUpChoiceData>? powerUps = open.m_PowerUpChoices;
                return powerUps != null && index < powerUps.Count ? Under(powerUps[index]?.Navigable, open.m_RewardsParent) : null;
            case RunStartCapture.SuperStarStep:
                Il2CppList.List<OldSquadMenu.SkaterChoiceData>? skaters = open.m_SkaterChoices;
                return skaters != null && index < skaters.Count ? Under(skaters[index]?.Navigable, open.m_SuperStarParent) : null;
            case RunStartCapture.RelicStep:
                Il2CppList.List<OldSquadMenu.RelicChoiceData>? relics = open.m_RelicChoices;
                return relics != null && index < relics.Count ? Under(relics[index]?.Navigable, open.m_RelicsParent) : null;
            default:
                return null;
        }

        // A card of this step's own list (the menu fills the lists again for each step).
        static AbstractNavigable? Under(AbstractNavigable? card, NavigableParent? parent) =>
            card != null && parent != null && card.transform.IsChildOf(parent.transform) ? card : null;
    }

    /// <summary>The menu's own skip of the reveal before a step's cards (its argument isn't read).</summary>
    private static void Skip(OldSquadMenu open, string step)
    {
        switch (step)
        {
            case RunStartCapture.PowerUpStep:
                open.SkipPowerUpAnimation(null!);
                break;
            case RunStartCapture.SuperStarStep:
                open.SkipSuperStarAnimation(null!);
                break;
            case RunStartCapture.RelicStep:
                open.SkipRelicAnimation(null!);
                break;
        }
    }

    private static string Words(string step) => step switch
    {
        RunStartCapture.PowerUpStep => "power-up",
        RunStartCapture.SuperStarStep => "superstar",
        RunStartCapture.RelicStep => "relic",
        _ => "exit",
    };

    /// <summary>An old squad menu rolled that isn't the view's: said once (it gets nothing; a late copy is closed once it's up).</summary>
    private void ReportRoll(OldSquadMenu menu)
    {
        if (_notified.Add("old squad roll"))
        {
            ModLog.Info($"Menus: an old squad menu's own rolls were skipped on this PC ({MenuGame.MenuId(menu)}: only the host's game rolls them; its offers come with the host's run start data).");
        }
    }

    private static Il2CppList.List<ForwardData>? Skaters(SkaterModels.Builder builder, IReadOnlyList<RunStartSkater> skaters, string what, out string problem)
    {
        var built = new Il2CppList.List<ForwardData>();
        for (int i = 0; i < skaters.Count; i++)
        {
            ForwardData? forward = builder.Build(skaters[i], out problem);
            if (forward == null)
            {
                problem = $"its {what} {i + 1} ({skaters[i]}) can't be built here: {problem}";
                return null;
            }

            built.Add(forward);
        }

        problem = string.Empty;
        return built;
    }

    /// <summary>
    /// The host's power-up rewards from this game's lists at the host's level, with its stickers. The lists clone what they
    /// give (<c>isClone</c>): every copy is noted by <paramref name="builder"/>, and one at the wrong level is destroyed.
    /// </summary>
    private static Il2CppList.List<PowerUpReward>? PowerUps(SkaterModels.Builder builder, IReadOnlyList<RunStartReward> rewards, out string problem)
    {
        ITalentRepository? talents = MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
        IAbilityRepository? abilities = MenuGame.Repository<IAbilityRepository>(p => new IAbilityRepository(p));
        var built = new Il2CppList.List<PowerUpReward>();
        foreach (RunStartReward reward in rewards)
        {
            PowerUp? power = null;
            if (reward.Kind == RunStartRewardKind.Talent && talents != null)
            {
                Talent? talent = builder.Keep(talents.GetTalent(reward.Id, true));
                if (talent != null && talent.level != reward.Level)
                {
                    Talent first = talent;
                    talent = builder.Keep(talents.GetTalent(talent.GetIl2CppType(), reward.Level, true));
                    builder.Drop(first);
                }

                power = talent;
            }
            else if (reward.Kind == RunStartRewardKind.Ability && abilities != null)
            {
                Ability? ability = builder.Keep(abilities.GetAbility(reward.Id, true));
                if (ability != null && ability.level != reward.Level)
                {
                    Ability first = ability;
                    ability = builder.Keep(abilities.GetAbility(ability.GetIl2CppType(), reward.Level, true));
                    builder.Drop(first);
                }

                power = ability;
            }

            if (power == null || power.level != reward.Level || power.id != reward.Id)
            {
                problem = $"its power-up {reward} isn't in this game";
                return null;
            }

            var made = new PowerUpReward(power);
            if (!AddStickers(made, reward, out problem))
            {
                return null;
            }

            built.Add(made);
        }

        problem = string.Empty;
        return built;
    }

    /// <summary>The host's relic rewards from this game's list at the host's level, with its stickers (each copy noted by <paramref name="builder"/>, as above).</summary>
    private static Il2CppList.List<RelicReward>? Relics(SkaterModels.Builder builder, IReadOnlyList<RunStartReward> rewards, out string problem)
    {
        IRelicRepository? repository = MenuGame.Repository<IRelicRepository>(p => new IRelicRepository(p));
        var built = new Il2CppList.List<RelicReward>();
        foreach (RunStartReward reward in rewards)
        {
            Relic? relic = builder.Keep(repository?.GetRelic(reward.Id, true));
            if (relic != null && relic.level != reward.Level)
            {
                Relic first = relic;
                relic = builder.Keep(repository!.GetRelic(relic.GetIl2CppType(), reward.Level, true));
                builder.Drop(first);
            }

            if (relic == null || relic.level != reward.Level || relic.id != reward.Id)
            {
                problem = $"its relic {reward} isn't in this game";
                return null;
            }

            var made = new RelicReward(relic);
            if (!AddStickers(made, reward, out problem))
            {
                return null;
            }

            built.Add(made);
        }

        problem = string.Empty;
        return built;
    }

    /// <summary>The host's stickers on a rebuilt reward (each a stat this game knows), read back.</summary>
    private static bool AddStickers(Reward made, RunStartReward reward, out string problem)
    {
        problem = string.Empty;
        if (reward.Stickers.Count == 0)
        {
            return true;
        }

        made.stickers ??= new Il2CppList.List<Sticker>();
        foreach (RunStartSticker sticker in reward.Stickers)
        {
            if (!Enum.IsDefined(typeof(Sticker.StatType), sticker.Stat))
            {
                problem = $"its {reward} has a sticker stat ({sticker.Stat}) this game doesn't know";
                return false;
            }

            made.stickers.Add(new Sticker((Sticker.StatType)sticker.Stat, sticker.Value));
        }

        if (made.stickers.Count != reward.Stickers.Count)
        {
            problem = $"its {reward}'s stickers didn't read back as written";
            return false;
        }

        return true;
    }

    // ---- Run-start music and the title's background ----

    /// <summary>
    /// A host menu opened: a run-start menu (the old squads, the lineup) starts this PC's run-start music, as the host's game
    /// plays it. The squad screen means the host left its run start (backed out of the lineup): a superstar it picked there
    /// isn't checked against its next run.
    /// </summary>
    private void RunStartOpened(View view)
    {
        if (view.Type == MenuPayloads.ChooseMeta)
        {
            _expectedPick = null;
            _expectedLineup = null;
        }

        if (view.Type is RunStartCapture.OldSquad or RunStartCapture.Benchwarmer)
        {
            _runStartSeen = FrameClock.Seconds;
            StartMusic();
        }
    }

    /// <summary>
    /// Every frame while following: the run-start music stops once the host has no run-start menu open (a moment's grace: one
    /// follows another), when the squad screen comes back, or when this PC leaves its title; the title's background is
    /// hidden while the squad screen or Play Now's controller screen is shown for real here (the host's title hides it around
    /// both: its Play Now's first menu, and again when it comes back).
    /// </summary>
    private void RunStartTick()
    {
        if (_musicPlaying)
        {
            bool open = false;
            bool squads = false;
            foreach (View view in _views.Values)
            {
                open |= view.Type is RunStartCapture.OldSquad or RunStartCapture.Benchwarmer;
                squads |= view.Type == MenuPayloads.ChooseMeta;
            }

            if (open)
            {
                _runStartSeen = FrameClock.Seconds;
            }
            else if (squads || !CampaignGame.TitleLoaded() || FrameClock.Seconds - _runStartSeen > MusicGraceSeconds)
            {
                StopMusic(squads ? "the host is back on the squad screen" : "the host's run-start menus are done");
            }
        }

        bool squadUp = false;
        foreach (View view in _views.Values)
        {
            squadUp |= (view.Type == MenuPayloads.ChooseMeta || view.Controllers?.State?.Kind == ControllerScreenKind.PlayNow) && view.Mode == Mode.Real && view.Menu != null;
        }

        if (squadUp != _backgroundHidden && FrameClock.Seconds >= _backgroundRetryAt)
        {
            Background(hidden: squadUp);
        }
    }

    /// <summary>Following started: the self-check of the host's superstar pick listens for the host's runs.</summary>
    private void RunStartStarted()
    {
        RunSync.HostRunLoaded -= HostRunLoaded;
        RunSync.HostRunLoaded += HostRunLoaded;
        _expectedPick = null;
        _expectedLineup = null;
        _armed = null;
        ReleaseUnshown(); // an earlier session's, if its stop didn't release them
        _benchSkaters.Clear();
        _dressed.Clear();
        _backgroundRetryAt = 0;
    }

    /// <summary>
    /// Following stopped: the music stops and the title's background is back, and every plan whose menu never took its
    /// offers goes with what it made (no game calls when it's closing).
    /// </summary>
    private void RunStartStopped(bool gameClosing)
    {
        RunSync.HostRunLoaded -= HostRunLoaded;
        _expectedPick = null;
        _expectedLineup = null;
        _armed = null;
        if (gameClosing)
        {
            _unshown.Clear();
            _benchUnshown.Clear();
            _benchSkaters.Clear();
            _musicPlaying = false;
            _backgroundHidden = false;
            return;
        }

        ReleaseUnshown();
        _benchSkaters.Clear();
        StopMusic("the session ended");
        if (_backgroundHidden)
        {
            Background(hidden: false);
        }

        _backgroundHidden = false;
        _backgroundRetryAt = 0;
    }

    /// <summary>
    /// A run of the host's loaded here: the first real one after the host's superstar pick should have that superstar, and
    /// after its lineup the skaters this PC's lineup showed last (<see cref="CheckLineup"/>).
    /// </summary>
    private void HostRunLoaded(uint checkpoint)
    {
        CheckLineup(checkpoint);
        CheckShownLineup(checkpoint);
        if (checkpoint == 0 || _expectedPick is not { } pick)
        {
            return;
        }

        _expectedPick = null;
        TeamData? team = CampaignGame.State()?.runData?.teamData;
        bool has = team?.GetForwardById(pick.Id) != null;
        if (has)
        {
            ModLog.Info($"Run start: the host's run {checkpoint} has the superstar the host picked ({pick.Name}, {HostText.KeyForLog(pick.Id)}).");
        }
        else
        {
            ModLog.Warn($"Run start: the host's run {checkpoint} doesn't have the superstar the host picked ({pick.Name}, {HostText.KeyForLog(pick.Id)}): this PC showed another pick than the host's game made.");
        }
    }

    private void StartMusic()
    {
        if (_musicPlaying)
        {
            return;
        }

        TitleScreen? title = CampaignGame.Title();
        if (title == null)
        {
            Log("Menus: the host's run-start menus are up, but this PC isn't on its title screen: no run-start music here.");
            return;
        }

        try
        {
            RuntimeManager.StudioSystem.setParameterByNameWithLabel(LocationParameter, "blademaster", false);
            _music = RuntimeManager.CreateInstance(title.m_BlademasterMusicEvent);
            _music.start();
            _musicPlaying = true;
            Log("Menus: the host's run-start menus are up: this PC plays the run-start music, as the host's game does.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: the run-start music couldn't be played here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private void StopMusic(string why)
    {
        if (!_musicPlaying)
        {
            return;
        }

        _musicPlaying = false;
        try
        {
            _music.stop(Il2CppFMOD.Studio.STOP_MODE.ALLOWFADEOUT);
            _music.release();
            if (CampaignGame.TitleLoaded())
            {
                RuntimeManager.StudioSystem.setParameterByNameWithLabel(LocationParameter, "menu", false);
            }

            Log($"Menus: the run-start music stopped here ({why}).");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: the run-start music couldn't be stopped cleanly here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// Hides or shows the title's background objects with the title's own switch (only on this PC's title screen, not while its
    /// own run start runs). When it can't now, it's tried again a second later (the title is looked for no more often).
    /// </summary>
    private void Background(bool hidden)
    {
        TitleScreen? title = CampaignGame.Title();
        if (title == null || title.m_IsStartingRun)
        {
            // Gone (nothing to put back), or the title's own run start has it now.
            _backgroundHidden = hidden && title != null && _backgroundHidden;
            _backgroundRetryAt = FrameClock.Seconds + 1;
            return;
        }

        try
        {
            FriendUi.Apply(() => title.DeactivateObjectsInBackground(!hidden));
            _backgroundHidden = hidden;
            Log(hidden ? "Menus: the title's background is hidden behind the host's menu, as on the host's screen." : "Menus: the title's background is shown again.");
        }
        catch (Exception ex)
        {
            _backgroundRetryAt = double.MaxValue; // not again this session
            ModLog.Warn($"Menus: the title's background couldn't be {(hidden ? "hidden" : "shown again")} here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// A squad screen selector this profile has fewer choices on than the host's (its own unlocks): the host's choice is shown
    /// over its label (cosmetic; the friend's menu never commits). Said once per selector and view.
    /// </summary>
    private void DressList(View view, CycleList list, MenuTarget element)
    {
        if (!_mirror.TryGet(view.Serial, out MirroredMenu menu))
        {
            return;
        }

        string path = MenuPayloads.PathText(element);
        MenuItem? row = menu.State.Data.Group(MenuPayloads.Choice).FirstOrDefault(r => r.Text("path") == path);
        string text = row == null ? string.Empty : ItemOf(row);
        Il2CppTMPro.TMP_Text? label = list.m_ItemText;
        if (text.Length == 0 || label == null)
        {
            return;
        }

        if (label.text != text)
        {
            Try(view, "showing the host's choice", () => label.text = text);
        }

        if (_dressed.Add($"{view.Serial}:{path}"))
        {
            bool here = row!.Text(MenuPayloads.CycleItemTermKey).Length > 0 && MenuGame.Localized(row.Text(MenuPayloads.CycleItemTermKey)).Length > 0;
            string shown = here ? $"\"{SafeText.Clean(text, 40)}\", in this PC's language" : $"{LogNames.TextShape(text)}, as the host's text"; // a host's text by its shape
            Log($"Menus: {view.LogId}: the host's {element} ({shown}) is shown here (this profile hasn't unlocked it).");
        }
    }

    /// <summary>
    /// A list selector's item as the host's shows it: its term in this PC's language when the game has it here, else the
    /// host's text of it; an older host's row (no item) gives the row's label.
    /// </summary>
    private static string ItemOf(MenuItem row)
    {
        string local = MenuGame.Plain(MenuGame.Localized(row.Text(MenuPayloads.CycleItemTermKey)), 150);
        if (local.Length > 0)
        {
            return local;
        }

        string item = row.Text(MenuPayloads.CycleItemKey);
        return item.Length > 0 ? item : TextOf(row);
    }

    /// <summary>What a view of a run-start menu (an old squad menu, the lineup) holds besides the panel's own state.</summary>
    private sealed class RunStartView
    {
        /// <summary>Waiting (until then) for the host's run start data; 0: not waiting.</summary>
        public double WaitDataUntil { get; set; }

        /// <summary>The preview's load was asked for (once per view).</summary>
        public bool PreviewStarted { get; set; }

        /// <summary>Waiting (until then) for the preview's load; 0: not waiting.</summary>
        public double WaitPreviewUntil { get; set; }

        /// <summary>The preview load's answer, taken on the next frame (never acted on inside run sync's callback).</summary>
        public FollowResult? PreviewResult { get; set; }

        /// <summary>True while it waits for the host's data or the preview's load.</summary>
        public bool Waiting => WaitDataUntil > 0 || WaitPreviewUntil > 0 || PreviewResult != null;

        /// <summary>The host's run start data.</summary>
        public RunPreviewSet? Set { get; set; }

        /// <summary>The offers built here, while the menu is asked for or up.</summary>
        public OldSquadPlan? Plan { get; set; }

        /// <summary>The lineup's benchwarmers built here, while the menu is asked for or up.</summary>
        public BenchPlan? Bench { get; set; }

        /// <summary>The lineup's newest host data seen (what its close leaves for the check at the host's next checkpoint).</summary>
        public MenuData? LastData { get; set; }

        /// <summary>The lineup's looks in a row with a swap its menu refused.</summary>
        public int Refusals { get; set; }

        /// <summary>The lineup has followed the host's cards once (said once).</summary>
        public bool FollowedOnce { get; set; }

        /// <summary>The lineup's cards weren't the host's in time (said once).</summary>
        public bool ReadyMissed { get; set; }

        /// <summary>The old squad menu's cards up now (the native objects of its first shown cards; all zero for none).</summary>
        public (IntPtr SkaterCard, IntPtr PowerUpCard, IntPtr RelicCard) CardsKey { get; set; }

        /// <summary>True while the old squad menu has a step's cards up (the host's cursor can land on them).</summary>
        public bool CardsUp { get; set; }

        /// <summary>The old squad menu's pick handler is running a host's pick here (its exit, inside it, is said after it).</summary>
        public bool PickPlaying { get; set; }

        /// <summary>The exit ran inside the pick being played: its line follows the pick's.</summary>
        public bool ExitPending { get; set; }

        /// <summary>The host's picks (by step), as they came.</summary>
        public Dictionary<string, int> Steps { get; } = new(StringComparer.Ordinal);

        /// <summary>The picks played here (or given up on).</summary>
        public HashSet<string> Done { get; } = new(StringComparer.Ordinal);

        /// <summary>The step being played, since when, and whether its reveal was skipped.</summary>
        public string StepName { get; set; } = string.Empty;

        public double StepSince { get; set; }

        public bool Skipped { get; set; }

        /// <summary>Stops waiting (it's text from here, with why).</summary>
        public void Stop()
        {
            WaitDataUntil = 0;
            WaitPreviewUntil = 0;
            PreviewResult = null;
        }
    }

    /// <summary>The host's offers built here for one old squad menu, and the menu they're for (set when it binds).</summary>
    private sealed class OldSquadPlan
    {
        public OldSquadPlan(RunPreviewSet set, Il2CppList.List<ForwardData> superStars, Il2CppList.List<ForwardData> randomSkaters,
            Il2CppList.List<PowerUpReward> powerUps, Il2CppList.List<RelicReward> relics)
        {
            Set = set;
            SuperStars = superStars;
            RandomSkaters = randomSkaters;
            PowerUps = powerUps;
            Relics = relics;
        }

        public RunPreviewSet Set { get; }

        public Il2CppList.List<ForwardData> SuperStars { get; }

        public Il2CppList.List<ForwardData> RandomSkaters { get; }

        public Il2CppList.List<PowerUpReward> PowerUps { get; }

        public Il2CppList.List<RelicReward> Relics { get; }

        public IntPtr Menu { get; set; }

        /// <summary>The skaters and rewards' copies made here for it (every one this PC's own).</summary>
        public IReadOnlyList<UnityEngine.Object> Made { get; set; } = Array.Empty<UnityEngine.Object>();

        /// <summary>True once its menu's roll took its offers: the menu holds what it made from then on.</summary>
        public bool Used { get; set; }
    }

    /// <summary>The superstar the host picked, for the self-check at its first real checkpoint.</summary>
    private readonly record struct ExpectedPick(string Id, string Name);
}
#endif
