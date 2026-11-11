#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppRogue.BenchPowers;
using Il2CppRogue.BenchPowers.Repository;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the lineup with benchwarmers (<c>BenchwarmerMenu</c>, the run start's "make your squad" screen) as a real
/// view (D68, v1.1):
/// <list type="bullet">
/// <item>It opens as the old squad menu does (FriendMenus.RunStart.cs): once the host's run start data is here (its run
/// preview, with the lineup's benchwarmers as the host's game built them) and this PC's game has loaded the host's run as a
/// preview, with the preview's team, the host's side, and nothing stored. The host's benchwarmers are built here with the
/// game's own deserializer and read back with its serializer (<see cref="SkaterModels"/>).</item>
/// <item>When its <c>LoadCurrentTeam</c> has made this profile's own pool, its benchwarmer cards take the host's, locked as
/// the host's (<see cref="BenchViews.ApplyPool"/>); the snapshot service answers the host's hot and cold for them, its random
/// fill never runs, and a reveal of a benchwarmer the host shows locked isn't played (MenuPatches.RunStart.cs).</item>
/// <item>It follows the host's cards with the menu's own swaps on its temporary roster, and the host's toggle and cursor; its
/// confirm and way back are refused; it closes with the host's (the base close: nothing is applied, nothing saved).</item>
/// </list>
/// After the host's first real checkpoint, the log says whether its run has the skaters this PC's lineup showed last.
/// </summary>
internal sealed partial class FriendMenus
{
    private const int BenchLoadFrames = 600;
    private const int BenchRefusalsAllowed = 20;
    private const int BenchLateReselectFrames = 20;

    // The host's benchwarmers built here, by native object, while a view of the host's lineup holds them (the snapshot
    // service's hot and cold, and the reveal guard, answer from them).
    private readonly Dictionary<IntPtr, RunStartBenchwarmer> _benchSkaters = new();
    private readonly List<BenchPlan> _benchUnshown = new();
    private string[]? _expectedLineup;

    /// <summary>
    /// The lineup menu's data for <paramref name="view"/>: the preview's team on the host's side, nothing stored (its roster is
    /// a clone the menu makes; only its confirm, refused here, would apply it); and its plan, the host's benchwarmers built
    /// here. Null, with why, when a benchwarmer can't be built as the host's (what was built so far is destroyed).
    /// </summary>
    private BenchwarmerMenu.Metadata? PrepareBench(View view, out string problem)
    {
        DisarmRunStart(view);
        RunPreviewSet? set = view.RunStart?.Set;
        TeamData? team = set == null ? null : MenuRebuild.Team(out problem);
        if (set == null || team == null)
        {
            problem = set == null ? "the host's run start data isn't here" : "the host's new run has no team here";
            return null;
        }

        if (set.Menu != RunStartMenu.Benchwarmer)
        {
            problem = $"the host's run start data is for another menu ({set.Menu})";
            return null;
        }

        var builder = new SkaterModels.Builder();
        var built = new List<ForwardData>(set.Pool.Count);
        problem = string.Empty;
        try
        {
            IBenchPowerRepository? powers = MenuGame.Repository<IBenchPowerRepository>(p => new IBenchPowerRepository(p));
            for (int i = 0; i < set.Pool.Count && problem.Length == 0; i++)
            {
                RunStartBenchwarmer host = set.Pool[i];
                ForwardData? forward = builder.Build(host.Skater, out string why);
                if (forward == null)
                {
                    problem = $"its benchwarmer {i + 1} (skater {HostText.KeyForLog(host.Skater?.Model?.Id)}) can't be built here: {why}";
                    continue;
                }

                built.Add(forward);
                if (host.BenchPowerId is { } id)
                {
                    // The run file's model keeps no bench power: the skater gets the game's own (shared, never changed),
                    // as a benchwarmer made from its asset has it.
                    BenchPower? power = powers?.GetBenchPower(id, false);
                    if (power == null || power.ID != id)
                    {
                        problem = $"its benchwarmer {i + 1}'s bench power \"{HostText.KeyForLog(id)}\" isn't in this game";
                        continue;
                    }

                    forward.benchPower = power;
                }
            }
        }
        catch (Exception ex)
        {
            // The game's own code threw (its deserializer, one of its lists): the menu shows as text, with why.
            problem = $"an error while building the host's benchwarmers ({ModLog.SafeErrorText(ex)})";
        }

        if (problem.Length > 0)
        {
            DestroyBench(builder.Made, "they can't be shown here");
            return null;
        }

        var plan = new BenchPlan(set, built) { Made = builder.Made };
        for (int i = 0; i < built.Count; i++)
        {
            _benchSkaters[built[i].Pointer] = set.Pool[i];
        }

        view.RunStart!.Bench = plan;
        _benchUnshown.Add(plan);
        return new BenchwarmerMenu.Metadata
        {
            Team = team,
            LockPlayers = false,
            IsHomeTeam = set.IsTeamHome,
            IsStoringLineup = false, // nothing is ever applied here
            LineupStore = null!,
        };
    }

    /// <summary>
    /// Friend: a lineup menu loaded this profile's own team and benchwarmers (inside its Show). The view's menu takes the host's
    /// benchwarmers on its cards; any other is left alone (only a late copy could be one: it's closed once it's up).
    /// </summary>
    internal void BenchLoaded(BenchwarmerMenu menu)
    {
        View? view = _views.Values.FirstOrDefault(v => v.RunStart?.Bench is { } bench && bench.Menu == menu.Pointer);
        if (view?.RunStart?.Bench is not { Applied: false } plan)
        {
            return;
        }

        plan.Used = true; // the menu's cards hold what it made from here on
        _benchUnshown.Remove(plan);
        string done = string.Empty;
        string problem = string.Empty;
        try
        {
            FriendUi.Apply(() => done = BenchViews.ApplyPool(menu, plan.Built, plan.Set.Pool, out problem));
        }
        catch (Exception ex)
        {
            problem = $"an error while showing the host's benchwarmers ({ModLog.SafeErrorText(ex)})";
        }

        if (problem.Length > 0)
        {
            plan.Failure = problem; // the view's next frame shows it as text, with why
            return;
        }

        plan.Applied = true;
        plan.AppliedFrame = view.Frame;
        view.ReselectFrame = view.Frame + 2; // its cards are up now: the host's cursor again (the menu selects its own default)
        int hot = plan.Set.Pool.Count(b => b.Hot), cold = plan.Set.Pool.Count(b => b.Cold);
        Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s {plan.Built.Count} benchwarmer(s) are in this PC's menu ({done}; {hot} hot, {cold} cold; this profile's own pool " +
            "replaced; the skaters built with the game's deserializer, every field read back as the host's).");
    }

    /// <summary>The host's benchwarmer that <paramref name="forward"/> is, when this PC built it for a lineup of the host's it shows; else null.</summary>
    internal RunStartBenchwarmer? BenchSkater(ForwardData forward) =>
        _benchSkaters.Count > 0 && _benchSkaters.TryGetValue(forward.Pointer, out RunStartBenchwarmer? host) ? host : null;

    /// <summary>True when <paramref name="card"/> is in a lineup this PC shows for the host and shows a benchwarmer the host's card shows locked.</summary>
    internal bool BenchRevealRefused(SkaterCard card)
    {
        if (_benchSkaters.Count == 0)
        {
            return false;
        }

        foreach (View view in _views.Values)
        {
            if (view.RunStart?.Bench != null && view.Menu is { } open && card.transform.IsChildOf(open.transform))
            {
                return card.m_CurrentSkater?.Skater?.TryCast<ForwardData>() is { } forward && BenchSkater(forward) is { Unlocked: false };
            }
        }

        return false;
    }

    /// <summary>
    /// A real lineup view's frames: the host's benchwarmers once its menu has loaded (or text when they couldn't be shown, or
    /// didn't load in time), then every few frames the host's cards and toggle (<see cref="BenchViews.Follow"/>). A swap its
    /// menu keeps refusing, or cards that can't match the host's, show it as text.
    /// </summary>
    private void BenchTick(View view, MirroredMenu menu, BenchwarmerMenu open)
    {
        if (view.RunStart is not { Bench: { } plan } state)
        {
            return;
        }

        state.LastData = menu.State.Data;
        if (plan.Failure.Length > 0 || (!plan.Applied && view.Frame >= BenchLoadFrames))
        {
            string why = plan.Failure.Length > 0 ? plan.Failure : "your game's lineup didn't load its benchwarmers in time";
            ModLog.Warn($"Menus: couldn't show {LogNames.HostTag()}'s benchwarmers in {view.LogId} ({why}); showing it as text.");
            CloseReal(view);
            ToPanel(view, why);
            return;
        }

        if (!plan.Applied)
        {
            return;
        }

        if (view.Frame == plan.AppliedFrame + BenchLateReselectFrames)
        {
            ApplySelection(view, menu); // after the menu's own late default select
        }

        if (view.Frame % LineupEveryFrames != 0)
        {
            return;
        }

        string done = string.Empty;
        string problem = string.Empty;
        bool ready = false, refused = false;
        if (!Try(view, "following the host's lineup", () => done = BenchViews.Follow(open, menu.State.Data, out problem, out ready, out refused)))
        {
            return;
        }

        if (problem.Length > 0)
        {
            ModLog.Warn($"Menus: {view.LogId} can't follow {LogNames.HostTag()}'s lineup here ({problem}); showing it as text.");
            CloseReal(view);
            ToPanel(view, problem);
            return;
        }

        state.Refusals = refused ? state.Refusals + 1 : 0;
        if (state.Refusals == 1)
        {
            Log($"Menus: {view.LogId}: this PC's menu refused a swap toward {LogNames.HostTag()}'s lineup (its own rules); it's worked out again at the next look.");
        }
        else if (state.Refusals >= BenchRefusalsAllowed)
        {
            ModLog.Warn($"Menus: {view.LogId}: this PC's menu kept refusing the swaps toward {LogNames.HostTag()}'s lineup ({state.Refusals} looks in a row); showing it as text.");
            CloseReal(view);
            ToPanel(view, "your game's lineup refused the host's swaps");
            return;
        }

        if (ready && !state.FollowedOnce)
        {
            state.FollowedOnce = true;
            Log($"Menus: {view.LogId} follows {LogNames.HostTag()}'s lineup here ({BenchViews.HostCounts(menu.State.Data)}).");
        }
        else if (!ready && view.Frame >= LineupReadyFrames && !state.ReadyMissed)
        {
            state.ReadyMissed = true;
            Missed(view, "the host's loaded lineup (its data hasn't said its benchwarmers are in)");
        }

        if (done.Length > 0)
        {
            Log($"Menus: {view.LogId} follows {LogNames.HostTag()}'s lineup here: {done}.");
        }
    }

    /// <summary>A lineup view closes: the skaters its last host data showed in the starters and bench are what the host's run should have next.</summary>
    private void BenchClosed(View view)
    {
        if (view.Type == RunStartCapture.Benchwarmer && view.RunStart is { Bench.Applied: true, LastData: { } data } && BenchViews.HostLineup(data) is { } lineup)
        {
            _expectedLineup = lineup;
        }
    }

    /// <summary>
    /// After the host's lineup closed, its first real checkpoint: the run's team should hold every skater this PC's lineup
    /// showed last in its starters and bench (the host's confirm applied them; nothing was applied here).
    /// </summary>
    private void CheckLineup(uint checkpoint)
    {
        if (checkpoint == 0 || _expectedLineup is not { } lineup)
        {
            return;
        }

        _expectedLineup = null;
        TeamData? team = CampaignGame.State()?.runData?.teamData;
        if (team == null)
        {
            return;
        }

        // The run team's roster holds its starters and its bench (a team with no lines reads its bench from it: GetBench is
        // a part of the same roster, so it isn't added again).
        List<string?> ids = LineupViews.RosterIds(team);
        int shown = lineup.Count(id => id.Length > 0);
        string? difference = BenchwarmerLineup.Difference(lineup, ids);
        if (BenchwarmerLineup.Missing(lineup, ids).Count == 0)
        {
            ModLog.Info($"Run start: the host's run {checkpoint} has the lineup {LogNames.HostTag()} confirmed (the {shown} skater(s) this PC's lineup showed last" +
                $"{(difference == null ? string.Empty : $"; {difference}")}).");
        }
        else
        {
            ModLog.Warn($"Run start: the host's run {checkpoint} doesn't have the lineup this PC's view showed last ({difference}): this PC showed another lineup than the host's game confirmed.");
        }
    }

    /// <summary>
    /// A lineup plan that won't be armed again: its skaters stop answering for the host's (hot, cold, reveals), and what it
    /// made here is destroyed when no menu took it (once a menu's cards hold them, they're kept, as the host's game keeps its
    /// own pool). Never throws.
    /// </summary>
    private void ReleaseBench(BenchPlan plan)
    {
        _benchUnshown.Remove(plan);
        foreach (ForwardData forward in plan.Built)
        {
            _benchSkaters.Remove(forward.Pointer);
        }

        if (plan.Used || plan.Made.Count == 0)
        {
            return;
        }

        DestroyBench(plan.Made, "they weren't shown");
        plan.Made = Array.Empty<UnityEngine.Object>();
    }

    /// <summary>Destroys the skaters made here for the host's benchwarmers that won't be shown, and says so. Never throws.</summary>
    private static void DestroyBench(IReadOnlyList<UnityEngine.Object> made, string why)
    {
        int destroyed = SkaterModels.Destroy(made, "the host's benchwarmers");
        if (destroyed > 0)
        {
            ModLog.Info($"Menus: {RunStartCapture.Benchwarmer}: destroyed the {destroyed} object(s) made here for the host's benchwarmers ({why}).");
        }
    }

    /// <summary>The host's benchwarmers built here for one lineup menu, and the menu they're for (set when it binds).</summary>
    private sealed class BenchPlan
    {
        public BenchPlan(RunPreviewSet set, IReadOnlyList<ForwardData> built)
        {
            Set = set;
            Built = built;
        }

        public RunPreviewSet Set { get; }

        /// <summary>The host's benchwarmers built here, in the host's order.</summary>
        public IReadOnlyList<ForwardData> Built { get; }

        public IntPtr Menu { get; set; }

        /// <summary>The objects made here for it (every one this PC's own).</summary>
        public IReadOnlyList<UnityEngine.Object> Made { get; set; } = Array.Empty<UnityEngine.Object>();

        /// <summary>True once its menu's load took its benchwarmers: the menu holds what it made from then on.</summary>
        public bool Used { get; set; }

        /// <summary>True once the menu's cards show the host's benchwarmers.</summary>
        public bool Applied { get; set; }

        /// <summary>The view's frame when they did.</summary>
        public int AppliedFrame { get; set; } = -1;

        /// <summary>Why the host's benchwarmers couldn't be shown on the menu's cards ("" when they could).</summary>
        public string Failure { get; set; } = string.Empty;
    }
}
#endif
