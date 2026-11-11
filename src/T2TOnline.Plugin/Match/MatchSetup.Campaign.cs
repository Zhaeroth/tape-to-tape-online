#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2CppData;
using Il2CppInterop.Runtime;
using Il2CppMichsky.LSS;
using Il2CppScriptableStates;
using Il2CppSkaters;
using Il2CppSTS.Map;
using Il2CppState;
using Il2CppState.EndConditions;
using Il2CppTape2Tape.Customization;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// Campaign matches (M4, D40). The host's game launches the match from a map node (<c>MatchMapNode.LaunchMatch</c>: substitutes, a
/// Challenge's stat rolls, the rules, the current node); a friend's game never does. Its setup carries, on top of an exhibition's values:
/// <list type="bullet">
/// <item>the node (its point, type and blueprint), the act and floor, the run (its id, squad, difficulty, game rule) and
/// the run checkpoint a friend needs first;</item>
/// <item>the rules <c>LaunchMatch</c> sets beyond an exhibition's: the goal target (<c>MaxGoal.goalToScore</c>, as the
/// match loads), the objective and the restrictions;</item>
/// <item>the home team is the run's own (a friend's copy of it comes from the host's run checkpoint, D41), so only its
/// lineup is sent, by asset id (the run file keeps each skater's asset id, so the stream's keys match);</item>
/// <item>the opponent as launched: its team by asset id, every forward by asset id in roster order with its stats and
/// jersey number (after the substitutions and a Challenge's rolls), its lines, goalie and relics.</item>
/// </list>
/// A friend's game first loads the host's run if it doesn't hold it yet (over a few frames; <see cref="RunSync"/>), then
/// sets the team selection (home: the run's team; visitor: a copy of the opponent rebuilt from the host's lineup), the
/// rules, the goal target and the campaign's current node, and loads <c>standard_game</c> as an exhibition does. Every
/// value set and every difference from the host's setup goes in the log.
/// </summary>
internal static partial class MatchSetup
{
    /// <summary>The setup key for the campaign map node, "x,y".</summary>
    internal const string CampaignNodeKey = "campaign.node";

    /// <summary>The setup key for the act (1-3).</summary>
    internal const string CampaignActKey = "campaign.act";

    /// <summary>The setup key for the run's id (<c>RunData.runID</c>).</summary>
    internal const string CampaignRunIdKey = "campaign.runId";

    private const int GameTypeCampaign = 1;
    private const string MapScene = "world_map";

    // A campaign's own ranges (Oct 29 build): a Challenge is one period of 999 minutes (sudden death); a map is a few
    // nodes wide and some twenty floors high, over three acts; a goal target is a handful of goals; a forward's stats are
    // clamped by the game well under a thousand (shot power 200, speed 250, checking 500, accuracy 100).
    private const float MaxCampaignPeriodMinutes = 1000f;
    private const int MaxMapCoordinate = 255;
    private const int MaxAct = 9;
    private const int MaxGoalTarget = 99;
    private const int MaxStat = 1000;
    private const int MaxRelics = 24;
    private const int MaxRestrictions = 16;

    // How long a campaign match waits here for the host's run before it gives up (the relay's own load timeout is longer).
    private const double RunWaitSeconds = 60;

    private static readonly NodeType[] MatchNodeTypes = { NodeType.Challenge, NodeType.Elite, NodeType.Boss };

    private static CampaignLoad? _campaignLoad;
    private static string? _loadProblem;
    private static int _goalAtLoad = -1;

    /// <summary>True while a campaign match's load waits for the host's run.</summary>
    public static bool WaitingForRun => _campaignLoad is { ForRun: true };

    /// <summary>
    /// A load that <see cref="TryLoad"/> accepted and that failed later (a campaign match whose host run couldn't be
    /// loaded here): true once, with why.
    /// </summary>
    public static bool TakeLoadProblem(out string problem)
    {
        problem = _loadProblem ?? string.Empty;
        _loadProblem = null;
        return problem.Length > 0;
    }

    /// <summary>
    /// Host: why friends can't load the campaign match <paramref name="setup"/> describes (the campaign isn't shared, D46, or
    /// it names no run checkpoint: <see cref="PublishSavedRun"/> couldn't send one), or null when they can or it isn't a
    /// campaign match. Such a match isn't sent to friends at all: they'd wait for a run that doesn't come, and the host's
    /// match would wait for them (D31).
    /// </summary>
    public static string? CampaignNotShared(IReadOnlyList<KeyValuePair<string, NetValue>> setup) =>
        CampaignMatchValues.WhyNotShared(setup, RunSync.Instance?.HostOnCoopSlot == true);

    /// <summary>", campaign node 3,4 (Elite), act 1" for a campaign setup's description; empty for any other.</summary>
    private static string DescribeCampaign(Values values) =>
        values.Int("mode.gameType") != GameTypeCampaign ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $", campaign node {HostText.KeyForLog(values.Text(CampaignNodeKey))} ({NodeTypeName(values.Int("campaign.nodeType"))}), act {values.Int(CampaignActKey)}");

    /// <summary>A node type's name ("Elite"), or its number when the game has no such type.</summary>
    private static string NodeTypeName(int nodeType) => Enum.IsDefined((NodeType)nodeType) ? ((NodeType)nodeType).ToString() : nodeType.ToString(CultureInfo.InvariantCulture);

    /// <summary>Values that change once the match is under way: read as the match loads (before the match's own set-up recomputes them).</summary>
    private static void RememberAtLoad() => _goalAtLoad = MaxGoal.goalToScore;

    // ------------------------------------------------------------------ capture (host)

    /// <summary>The campaign's part of a match's setup (the common part and both sides are already in).</summary>
    private static bool CaptureCampaign(TeamSelection teams, InGameData data, Action<string, NetValue> add, out string problem)
    {
        CampaignState? campaign = Settings<CampaignState>();
        RunData? run = campaign?.runData;
        NodeBlueprint? node = campaign?.currentNode;
        if (campaign == null || run == null || node == null)
        {
            problem = campaign == null ? "the game's campaign state isn't available" : run == null ? "the campaign holds no run" : "the campaign has no current node";
            return false;
        }

        // The node LaunchMatch made current (CampaignState.SetCurrentNode writes its point into the run).
        Vector2 point = run.currentNodePoint;
        add(CampaignNodeKey, NetValue.FromString(CampaignMatchValues.FormatPoint((int)Math.Round(point.x), (int)Math.Round(point.y))));
        add("campaign.nodeType", NetValue.FromInt((int)node.nodeType));
        add("campaign.blueprint", NetValue.FromString(node.name ?? string.Empty));
        add(CampaignActKey, NetValue.FromInt(run.CurrentAct));
        add("campaign.floor", NetValue.FromInt(run.currentFloor));
        add(CampaignRunIdKey, NetValue.FromString(run.runID ?? string.Empty));
        add("campaign.squad", NetValue.FromString(run.squadId ?? string.Empty));
        add("campaign.difficulty", NetValue.FromInt(run.difficultyLevel));
        add("campaign.gameRule", NetValue.FromInt((int)run.gameRule));

        // The run checkpoint a friend needs first: the host's newest, or the run as last saved when none has gone out yet.
        // Read back on a friend's PC, the one it applied.
        CampaignRelay? relay = CampaignRelay.Current;
        uint checkpoint = relay is { Hosting: true } ? relay.LatestRun?.Sequence ?? PublishSavedRun(relay, campaign) : RunSync.Instance?.AppliedCheckpoint ?? 0;
        add(CampaignMatchValues.RunCheckpointKey, NetValue.FromInt(checkpoint));

        add("rules.goalToScore", NetValue.FromInt(_goalAtLoad >= 0 ? _goalAtLoad : MaxGoal.goalToScore));
        add("rules.objective", NetValue.FromInt(data.gameObjectiveData != null ? (int)data.gameObjectiveData.gameObjective : 0));
        var restrictions = new List<int>();
        Il2CppSystem.Collections.Generic.List<GameRestriction>? list = data.gameRestrictionData?.gameRestrictions;
        for (int i = 0; list != null && i < list.Count && i < MaxRestrictions; i++)
        {
            restrictions.Add((int)list[i]);
        }

        add("rules.restrictions", NetValue.FromString(CampaignMatchValues.FormatList(restrictions)));

        // The opponent as the host's game launched it: each roster forward's stats and number (after the substitutions
        // and, in a Challenge, the stat rolls), in the order of visitor.forward.N; and its relics.
        TeamData visitor = teams.visitorTeam;
        Il2CppSystem.Collections.Generic.List<ForwardData> forwards = visitor.GetForwards(true);
        for (int i = 0; i < forwards.Count; i++)
        {
            ForwardData? forward = forwards[i];
            add($"{Visitor}.stats.{i}", NetValue.FromString(forward == null ? string.Empty
                : CampaignMatchValues.FormatList(new[] { forward.shotPower, forward.speed, forward.checking, forward.shotAccuracy, forward.number })));
        }

        Il2CppSystem.Collections.Generic.List<Il2CppRogue.Relic>? relics = visitor.relics;
        int relicCount = Math.Min(relics?.Count ?? 0, MaxRelics);
        add($"{Visitor}.relics", NetValue.FromInt(relicCount));
        for (int i = 0; i < relicCount; i++)
        {
            Il2CppRogue.Relic? relic = relics![i];
            add($"{Visitor}.relic.{i}", NetValue.FromString(relic != null ? relic.id ?? string.Empty : string.Empty));
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Host: a shared campaign's run as last saved (the co-op slot's file; a match launches without saving, so it holds
    /// this match's team) goes to friends as a checkpoint, when none has yet in this session: hosting started with a match
    /// preview open (the map's own save then comes only after the match) or during the match. Returns its sequence, or 0
    /// (not shared, or the file can't be read): <see cref="CampaignNotShared"/> then keeps the match from friends.
    /// </summary>
    private static uint PublishSavedRun(CampaignRelay relay, CampaignState campaign)
    {
        string path = CampaignGame.FullPath(CampaignGame.RunFileName(campaign));
        if (RunSync.Instance?.HostOnCoopSlot != true || !CampaignFiles.IsModPath(path) || !File.Exists(path))
        {
            return 0;
        }

        try
        {
            RunCheckpoint? published = relay.PublishRun(CampaignFiles.ReadShared(path));
            uint sequence = published?.Sequence ?? relay.LatestRun?.Sequence ?? 0;
            ModLog.Info($"Match setup: no run checkpoint had gone to friends in this session yet (hosting started with a preview open, or during this match); " +
                $"the run as last saved ({Path.GetFileName(path)}) goes now as checkpoint {sequence}, so they can load this match.");
            return sequence;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Match setup: couldn't read the run file for friends ({ModLog.SafeErrorText(ex)}); this campaign match isn't sent to them.");
            return 0;
        }
    }

    // ------------------------------------------------------------------ load (friend)

    /// <summary>
    /// A campaign match: checked, then loaded now if this PC holds the host's run the match needs and isn't loading a
    /// scene, else as soon as it does (<see cref="UpdateCampaignLoad"/>). True when the load started or waits.
    /// </summary>
    private static bool LoadCampaign(Values values, out string problem)
    {
        if ((CheckRules(values, MaxCampaignPeriodMinutes) ?? CheckCampaign(values) ?? CheckCampaignSide(values, Home) ?? CheckCampaignSide(values, Visitor)) is string wrong)
        {
            problem = $"bad setup: {wrong}";
            return false;
        }

        // The home team is the host's run, which only following the host's campaign brings to this PC (so F2 can't
        // rehearse a campaign match either).
        if (CampaignRelay.Current is not { Following: true })
        {
            problem = "a campaign match loads only while following the host's campaign";
            return false;
        }

        // Say now if this PC can't load a campaign match at all, rather than after the run.
        if (!CanLoadCampaignHere(null, out problem))
        {
            return false;
        }

        // The host's custom content the match names (its run team's logo and look): waited for after the run.
        if (!CustomContentValues.TryRead(values.Setup, out List<CustomRef> refs, out string badRefs))
        {
            problem = $"bad setup: {badRefs}";
            return false;
        }

        CustomContent.BeginLoad(refs);
        CancelCampaignLoad();

        // The host's checkpoint number travels as a whole number; its low 32 bits are the sequence.
        var load = new CampaignLoad(values, unchecked((uint)values.Int(CampaignMatchValues.RunCheckpointKey)), FrameClock.Seconds);
        switch (StepCampaignLoad(load, out problem))
        {
            case CampaignStep.Loaded:
                return true;
            case CampaignStep.Failed:
                return false;
            default:
                _campaignLoad = load;
                ModLog.Info($"Match setup: the campaign match waits ({load.Waiting}); it loads as soon as it can.");
                problem = string.Empty;
                return true;
        }
    }

    /// <summary>A campaign match waiting for the host's run (or for a scene load to finish): one step a frame; never throws.</summary>
    private static void UpdateCampaignLoad()
    {
        CampaignLoad? load = _campaignLoad;
        if (load == null)
        {
            return;
        }

        try
        {
            CampaignStep step = StepCampaignLoad(load, out string problem);
            if (step != CampaignStep.Waiting)
            {
                _campaignLoad = null;
            }

            if (step == CampaignStep.Failed)
            {
                FailCampaignLoad(problem);
            }
        }
        catch (Exception ex)
        {
            _campaignLoad = null;
            ModLog.Error("Match setup: loading a campaign match failed", ex);
            FailCampaignLoad($"setting it up failed ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// One step of a campaign match's load: the host's run first (the run sync loads it over a few frames), then, once no
    /// scene load is under way (this PC following the host to the map, say), the match.
    /// </summary>
    private static CampaignStep StepCampaignLoad(CampaignLoad load, out string problem)
    {
        problem = string.Empty;
        if (CampaignRelay.Current is not { Following: true } relay)
        {
            problem = "this PC no longer follows the host's campaign";
            return CampaignStep.Failed;
        }

        RunSync? runSync = RunSync.Instance;
        if (runSync == null)
        {
            problem = "the run sync isn't running";
            return CampaignStep.Failed;
        }

        if (load.Result is FollowResult result)
        {
            // The run sync answered (at once or frames later).
            load.Result = null;
            load.Applying = false;
            if (!result.Ok)
            {
                problem = $"the host's run couldn't be loaded here ({result.Reason})";
                return CampaignStep.Failed;
            }

            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Match setup: {RunName(runSync.AppliedCheckpoint)} is loaded here, {FrameClock.Seconds - load.Since:0.0} s after the match's setup arrived."));
            if (!CampaignMatchValues.HoldsCheckpoint(runSync.AppliedCheckpoint, load.Needed))
            {
                problem = $"this PC holds {RunName(runSync.AppliedCheckpoint)} and the match needs {RunName(load.Needed)}";
                return CampaignStep.Failed;
            }
        }

        // The host's custom content still arriving keeps the wait going past the run's limit (ContentWait's own limits), and
        // what's left after it (a scene load, the loading transition) gets the limit afresh.
        if (FrameClock.Seconds - load.LimitFrom > RunWaitSeconds && load.Content == null)
        {
            problem = $"the match waited {RunWaitSeconds:0} s here ({load.Waiting})";
            return CampaignStep.Failed;
        }

        load.ForRun = true;
        if (load.Applying || runSync.Busy)
        {
            load.WaitForRun(load.Needed, held: null);
            return CampaignStep.Waiting;
        }

        if (!CampaignMatchValues.HoldsCheckpoint(runSync.AppliedCheckpoint, load.Needed))
        {
            RunCheckpoint? latest = relay.LatestRun;
            if (latest == null || !CampaignMatchValues.HoldsCheckpoint(latest.Sequence, load.Needed))
            {
                load.WaitForRun(load.Needed, runSync.AppliedCheckpoint);
                return CampaignStep.Waiting;
            }

            load.Applying = true;
            load.WaitForRun(latest.Sequence, held: null);
            ModLog.Info($"Match setup: loading {RunName(latest.Sequence)} for the campaign match (this PC held {RunName(runSync.AppliedCheckpoint)}).");
            runSync.Apply(latest, answer => load.Result = answer);
            return CampaignStep.Waiting;
        }

        load.ForRun = false;
        if (!load.ContentGivenUp && (load.Content != null || CustomContent.Waiting()))
        {
            // The host's run is here; its run team's logo and look should be too, so the first faceoff shows them.
            load.Content ??= new ContentWait(load.Since);
            switch (StepContent(load.Content, out string contentProblem))
            {
                case ContentStep.Waiting:
                    load.WaitFor("the host's custom content is arriving");
                    return CampaignStep.Waiting;
                case ContentStep.Failed:
                    // Only the run team's looks, unlike an exhibition's custom teams: never a reason to sit a campaign match
                    // out (they're worn when they arrive, CustomContent.WearOnRun, RunLook).
                    load.ContentGivenUp = true;
                    ModLog.Warn($"Match setup: {SafeText.Clean(contentProblem, 240)}; the campaign match loads without them (the run team's logo and look show when they arrive).");
                    break;
            }

            load.Content = null;
            load.LimitFrom = FrameClock.Seconds;
        }

        LoadingScreenManager? loading = UnityEngine.Object.FindObjectOfType<LoadingScreenManager>();
        if (loading == null || loading.isStartingLoad)
        {
            load.WaitFor("a scene is loading");
            return CampaignStep.Waiting;
        }

        if (!CanLoadCampaignHere(null, out string where))
        {
            // Somewhere it can't load from now (between scenes, say): it may move on; if it doesn't, the wait says why.
            load.WaitFor(where);
            return CampaignStep.Waiting;
        }

        // The game's loading transition first, as the host's game shows it before loading its match (a frame or two).
        if (!Campaign.Menus.FriendTransitions.LoadReady(Campaign.Menus.FriendTransitions.HostMatch, out string showing))
        {
            load.WaitFor(showing);
            return CampaignStep.Waiting;
        }

        return LoadCampaignNow(load.Values, out problem) ? CampaignStep.Loaded : CampaignStep.Failed;
    }

    /// <summary>Drops a campaign match's load that's waiting (a newer setup, or the load was cancelled).</summary>
    private static void CancelCampaignLoad()
    {
        if (_campaignLoad != null)
        {
            _campaignLoad = null;
            ModLog.Info("Match setup: the campaign match's load (it was waiting) was cancelled.");
        }
    }

    /// <summary>A waiting campaign match can't load after all: why goes in the log and to the match relay (<see cref="TakeLoadProblem"/>).</summary>
    private static void FailCampaignLoad(string problem)
    {
        _loadProblem = SafeText.Clean(problem, 240);
        ModLog.Warn($"Match setup: the campaign match can't load here: {_loadProblem}.");
    }

    /// <summary>"the host's run 5" for the log (0: none).</summary>
    private static string RunName(uint checkpoint) => checkpoint == 0 ? "no run checkpoint of the host's" : $"the host's run {checkpoint}";

    /// <summary>
    /// Loads a campaign match now (this PC holds the host's run): the team selection, the rules, the goal target and the
    /// campaign's current node, then <c>standard_game</c>. False, with why, when it can't; then the game's settings are as
    /// they were.
    /// </summary>
    private static bool LoadCampaignNow(Values values, out string problem)
    {
        TeamSelection? teams = Settings<TeamSelection>();
        InGameData? data = Settings<InGameData>();
        DifficultySettings? difficulties = Settings<DifficultySettings>();
        CampaignState? campaign = Settings<CampaignState>();
        ITeamRepository? teamRepository = Repository<ITeamRepository>(p => new ITeamRepository(p));
        IForwardRepository? forwardRepository = Repository<IForwardRepository>(p => new IForwardRepository(p));
        IGoalieRepository? goalieRepository = Repository<IGoalieRepository>(p => new IGoalieRepository(p));
        LoadingScreenManager? loading = UnityEngine.Object.FindObjectOfType<LoadingScreenManager>();
        var missing = new List<string>();
        if (teams == null) missing.Add("team selection");
        if (data == null) missing.Add("match rules");
        if (difficulties == null) missing.Add("difficulty settings");
        if (campaign == null) missing.Add("campaign state");
        if (teamRepository == null) missing.Add("team list");
        if (forwardRepository == null) missing.Add("skater list");
        if (goalieRepository == null) missing.Add("goalie list");
        if (loading == null) missing.Add("loading screen");
        if (missing.Count > 0)
        {
            problem = $"the game's {string.Join(", ", missing)} {(missing.Count == 1 ? "isn't" : "aren't")} available here";
            return false;
        }

        if (!CanLoadCampaignHere(teams, out problem))
        {
            return false;
        }

        if (loading!.isStartingLoad)
        {
            problem = "the game is already loading a scene";
            return false;
        }

        if (!SaveGuard.Install(out string guardProblem))
        {
            problem = $"this PC's saves can't be protected during the match ({guardProblem})";
            return false;
        }

        RunData? run = campaign!.runData;
        TeamData? home = run?.teamData;
        if (run == null || home == null)
        {
            problem = "this PC holds no run (the host's didn't load?)";
            return false;
        }

        string runId = values.Text(CampaignRunIdKey);
        if (run.runID != runId)
        {
            problem = $"this PC holds another run than the host's match ({Quote(run.runID)}; the host's {Quote(runId)})";
            return false;
        }

        // Differences from the host's setup found on the way (fixed, or harmless), for the log.
        var notes = new List<string>();
        CampaignNode node = FindCampaignNode(values, notes);

        // From here the game's match settings change: if the load doesn't go ahead, they're put back as they were.
        SetupSnapshot before = SetupSnapshot.Take(teams!, data!);
        AddCampaignUndo(before, data!, campaign, run);
        try
        {
            if (!SetCampaignHome(values, home, notes, out problem) ||
                !SetCampaignVisitor(values, teams!, node.MapNode, teamRepository!, forwardRepository!, goalieRepository!, notes, out problem))
            {
                if (notes.Count > 0)
                {
                    // What was set or found on the way, so the log says how far the home and visitor sides came.
                    ModLog.Info(SafeText.Clean($"Match setup: set or found differently from the host's before the load stopped: {string.Join("; ", notes)}.", 1200));
                }

                before.Restore(problem);
                return false;
            }

            // As LaunchMatch: the run's own team, not a copy.
            teams!.homeTeam = home;
            ApplyRules(values, teams, data!, difficulties!, GameTypeCampaign);
            ApplyCampaignRules(values, data!);
            SetCampaignNode(campaign, run, node, values.Int("campaign.floor"), notes);
            LogCampaignLoad(values, teams, run, notes);
            StartLoad(values, loading);
        }
        catch (Exception ex)
        {
            _forced = null;
            before.Restore($"{ModLog.SafeErrorText(ex)}");
            throw;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Where a campaign match may load from: the main menu, the world map, or a match that's the host's (an exhibition, or a
    /// campaign match while this PC holds the host's run). Anywhere else (the tutorial, a campaign match of this PC's
    /// own) loading would throw away what's in progress. <paramref name="teams"/> is looked up when null and needed.
    /// </summary>
    private static bool CanLoadCampaignHere(TeamSelection? teams, out string problem)
    {
        problem = string.Empty;
        string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
        if (sceneName is MenuScene or MapScene)
        {
            return true;
        }

        if (sceneName == MatchScene)
        {
            teams ??= Settings<TeamSelection>();
        }

        int gameType = teams != null ? (int)teams.gameType : -1;
        bool hostsMatch = sceneName == MatchScene && teams != null && !teams.onboard &&
            (gameType == GameTypeQuickGame || (gameType == GameTypeCampaign && RunSync.Instance is { HoldingHostRun: true }));
        if (hostsMatch)
        {
            return true;
        }

        problem = sceneName == MatchScene
            ? $"this game is in a {(teams is { onboard: true } ? "tutorial" : "campaign")} match of its own; go to the main menu and the host's next match loads"
            : $"this game is somewhere a campaign match can't load from ({sceneName}); go to the main menu";
        return false;
    }

    /// <summary>The campaign's own values against the game's ranges, before anything changes. Null when they're fine, else what's wrong (short: the host sees it).</summary>
    private static string? CheckCampaign(Values values)
    {
        if (!CampaignMatchValues.TryParsePoint(values.Text(CampaignNodeKey), MaxMapCoordinate, out _, out _))
        {
            return $"{CampaignNodeKey} is {Quote(values.Text(CampaignNodeKey))}";
        }

        int nodeType = values.Int("campaign.nodeType");
        if (!MatchNodeTypes.Contains((NodeType)nodeType))
        {
            return $"campaign.nodeType is {nodeType} (a match node: Challenge, Elite or Boss)";
        }

        int act = values.Int(CampaignActKey);
        if (act is < 1 or > MaxAct)
        {
            return $"{CampaignActKey} is {act} (1 to {MaxAct})";
        }

        int floor = values.Int("campaign.floor");
        if (floor is < 0 or > MaxMapCoordinate)
        {
            return $"campaign.floor is {floor} (0 to {MaxMapCoordinate})";
        }

        if (values.Text(CampaignRunIdKey).Length == 0)
        {
            return $"{CampaignRunIdKey} is empty";
        }

        int goal = values.Int("rules.goalToScore");
        if (goal is < 0 or > MaxGoalTarget)
        {
            return $"rules.goalToScore is {goal} (0 to {MaxGoalTarget})";
        }

        int objective = values.Int("rules.objective");
        if (!Enum.IsDefined((GameObjectiveType)objective))
        {
            return $"rules.objective is {objective}";
        }

        if (!CampaignMatchValues.TryParseList(values.Text("rules.restrictions"), MaxRestrictions, 0, int.MaxValue, out int[] restrictions) ||
            restrictions.Any(r => !Enum.IsDefined((GameRestriction)r)))
        {
            return $"rules.restrictions is {Quote(values.Text("rules.restrictions"))}";
        }

        return null;
    }

    /// <summary>
    /// One side's counts and ids, before anything changes (as <see cref="CheckSide"/>, but a campaign roster may hold
    /// empty places: a bench seat nobody fills). Null when they're fine, else what's wrong (short: the host sees it).
    /// </summary>
    private static string? CheckCampaignSide(Values values, string side)
    {
        if (values.Bool($"{side}.random"))
        {
            return $"{side} is a random team (campaign teams never are)";
        }

        // The home team is the run's own (this PC's copy comes from the host's checkpoint), so only the visitor's id is used.
        if (side == Visitor && values.Text($"{side}.team").Length == 0)
        {
            return $"{side}.team is empty";
        }

        int forwards = values.Int($"{side}.forwards");
        if (forwards is < 0 or > MaxRoster)
        {
            return $"{side}.forwards is {forwards} (0 to {MaxRoster})";
        }

        int lines = values.Int($"{side}.lines");
        if (lines is < 0 or > MaxLines)
        {
            return $"{side}.lines is {lines} (0 to {MaxLines})";
        }

        int active = values.Int($"{side}.activeLine");
        int lowest = lines > 0 ? 1 : 0;
        if (active < lowest || active > MaxLines)
        {
            return $"{side}.activeLine is {active} ({lowest} to {MaxLines})";
        }

        if (side != Visitor)
        {
            return null;
        }

        for (int i = 0; i < forwards; i++)
        {
            if (values.Text($"{side}.forward.{i}").Length > 0 && !CampaignMatchValues.TryParseForward(values.Text($"{side}.stats.{i}"), MaxStat, out _, out _))
            {
                return $"{side}.stats.{i} is {Quote(values.Text($"{side}.stats.{i}"))}";
            }
        }

        int relics = values.Int($"{side}.relics");
        return relics is < 0 or > MaxRelics ? $"{side}.relics is {relics} (0 to {MaxRelics})" : null;
    }

    /// <summary>
    /// The home side: this PC's copy of the host's run team (from the host's checkpoint), with its roster order, lines and
    /// active line as the host has them (a lineup change the host's game hasn't saved yet). False when a skater in the host's
    /// lineup isn't in the copy (an older checkpoint): the stream finds each skater by its asset id.
    /// </summary>
    private static bool SetCampaignHome(Values values, TeamData home, List<string> notes, out string problem)
    {
        OrderHomeRoster(values, home, notes);

        // Each forward's own id by its asset id. The run file keeps both, so they're the host's.
        var idByOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        Il2CppSystem.Collections.Generic.List<ForwardData> current = home.GetForwards(true);
        for (int i = 0; i < current.Count; i++)
        {
            ForwardData? forward = current[i];
            if (forward != null && forward.OriginalId is { Length: > 0 } original && forward.id is { Length: > 0 } id)
            {
                idByOriginal.TryAdd(original, id);
            }
        }

        var notHere = new List<string>();
        for (int i = 0; i < values.Int($"{Home}.forwards"); i++)
        {
            string id = values.Text($"{Home}.forward.{i}");
            if (id.Length > 0 && !idByOriginal.ContainsKey(id))
            {
                notHere.Add(id);
            }
        }

        if (notHere.Count > 0)
        {
            notes.Add($"home roster: {string.Join(", ", notHere.Take(6).Select(HostId))} not in this PC's copy of the run (players off the ice there won't be found)");
        }

        int lines = values.Int($"{Home}.lines");
        Il2CppSystem.Collections.Generic.List<Lineup>? teamLines = home.lines;
        if ((teamLines?.Count ?? 0) != lines)
        {
            problem = $"the run's team has {teamLines?.Count ?? 0} lines here and {lines} on the host (an older run?)";
            return false;
        }

        for (int k = 0; k < lines; k++)
        {
            Lineup line = teamLines![k];
            string[] wanted = LineSlots.Select(slot => values.Text($"{Home}.line.{k}.{(int)slot}")).ToArray();
            if (!CampaignMatchValues.TryMapSlots(wanted, idByOriginal, out string[] ids, out List<string> missingIds))
            {
                problem = $"the host's skater {HostId(missingIds[0])} isn't in this PC's copy of the run (an older run?)";
                return false;
            }

            for (int s = 0; s < LineSlots.Length; s++)
            {
                if ((line.GetSlotId(LineSlots[s]) ?? string.Empty) != ids[s])
                {
                    line.SetSlotId(LineSlots[s], ids[s]);
                    notes.Add($"home line {k + 1} slot {(int)LineSlots[s]} set as the host's ({HostId(wanted[s])})");
                }
            }
        }

        int active = ActiveLine(values, Home, lines, out string? held);
        if (held != null)
        {
            notes.Add(held);
        }

        if (home.activeLineNumber != active)
        {
            notes.Add($"home active line {home.activeLineNumber} set to {active}{(held == null ? " (the host's)" : string.Empty)}");
            home.activeLineNumber = active;
        }

        string goalie = values.Text($"{Home}.goalie");
        if (goalie != (home.goalie != null ? home.goalie.id ?? string.Empty : string.Empty))
        {
            notes.Add("home goalie isn't the host's (an older run?)");
        }

        return SlotsAsHost(values, Home, home, out problem);
    }

    /// <summary>
    /// The run team's roster in the host's order, when this PC's copy holds the same skaters in another order. A run team has
    /// no lines, so its roster's order is its lineup (<c>TeamData.GetForward</c> reads the roster when a team has no line): the
    /// lineup menu's leave (<c>TeamRosterData.ApplyChanges</c>) writes the new order into the team in memory, and the run file
    /// gets it only at the game's next save (the map's lineup saves nothing), so the host's checkpoint may still hold the old
    /// order. Each place is set with the game's own <c>TeamData.SetForwardAtIndex</c>, as that leave sets the bench; the
    /// skaters are this PC's own (their stats and powers are the checkpoint's). A copy with other skaters (an older run) is
    /// left as it is, and the home check says what differs. Not undone when the load doesn't go ahead: the order is the host's
    /// live team's, and this PC's copy of the run is never saved.
    /// </summary>
    private static void OrderHomeRoster(Values values, TeamData home, List<string> notes)
    {
        Il2CppSystem.Collections.Generic.List<ForwardData>? live = home.GetForwards(true);
        int count = values.Int($"{Home}.forwards");
        if (live == null || count <= 0)
        {
            return;
        }

        // Read whole before any place changes: GetForwards(true) returns the team's own list.
        var here = new ForwardData?[live.Count];
        var hereIds = new string[live.Count];
        for (int i = 0; i < here.Length; i++)
        {
            here[i] = live[i];
            hereIds[i] = here[i]?.OriginalId ?? string.Empty;
            if (here[i] != null && hereIds[i].Length == 0)
            {
                // A skater the setup can't name (it sends asset ids; "" is an empty seat there): no order can be told.
                notes.Add("home roster: a skater in this PC's copy of the run has no asset id; left in its own order");
                return;
            }
        }

        var hostIds = new string[count];
        for (int i = 0; i < count; i++)
        {
            hostIds[i] = values.Text($"{Home}.forward.{i}");
        }

        if (!CampaignMatchValues.TryOrderRoster(hostIds, hereIds, MaxRoster, out int[] from))
        {
            notes.Add($"home roster: this PC's copy of the run ({here.Length} place(s)) doesn't hold the host's {count} as the same skaters; left in its own order");
            return;
        }

        int moved = 0;
        for (int i = 0; i < from.Length; i++)
        {
            if (from[i] != i)
            {
                home.SetForwardAtIndex(i, here[from[i]]!);
                moved++;
            }
        }

        if (moved > 0)
        {
            notes.Add($"home roster: {moved} place(s) put in the host's order (a lineup change its game hadn't saved to the run yet)");
        }
    }

    /// <summary>
    /// The visitor side: a copy of the opponent (the game's own team copy, so nothing here changes the map node's team or
    /// an asset), its roster rebuilt from the host's (each forward by asset id, with the host's stats and number), its
    /// lines, active line and goalie as the host's. False, with why, when a team, skater or goalie isn't on this PC.
    /// </summary>
    /// <remarks>
    /// The host's game plays the map node's own opponent (<c>MatchMapNode.LaunchMatch</c> sets it as the visitor, no copy),
    /// so its forwards keep their own ids: a boss team's are the skater assets themselves (<c>BossMapNode.SetBoss</c> copies
    /// only the team). The game's team copy (<c>TeamSelection.SetQuickMatchTeam</c> → <c>TeamData.Clone</c>) gives every
    /// forward a fresh id, and game code that finds a forward by its id then misses this PC's: the golfer boss's defense
    /// pairs (<c>GolferDefenseRotationSystem.RosterContains</c> compares ids) all came out "not in roster", so this PC's
    /// golfers had no defense rotation. Each copy gets its source forward's id back, as the host's has it; a forward this
    /// PC adds (a substitute, a Challenge's pick) keeps the fresh id the host's copy has too.
    /// </remarks>
    private static bool SetCampaignVisitor(Values values, TeamSelection teams, MapNode? mapNode, ITeamRepository teamRepository, IForwardRepository forwardRepository,
        IGoalieRepository goalieRepository, List<string> notes, out string problem)
    {
        // The opponent this PC's own map node made from the run's map (as the host's game made it, before its
        // substitutions), else the team asset.
        string teamId = values.Text($"{Visitor}.team");
        MatchMapNode? matchNode = mapNode?.TryCast<MatchMapNode>();
        TeamData? source = matchNode?.opponent is TeamData opponent && opponent.id == teamId ? opponent : null;
        string from = "this PC's map node";
        if (source == null)
        {
            source = FindTeam(teamRepository, teamId) ?? FindLoaded<TeamData>(t => t.id == teamId);
            from = "the team asset";
        }

        if (source == null)
        {
            problem = $"the visitor team ({HostId(teamId)}) isn't on this PC";
            return false;
        }

        // Each forward's own id on the map node's team, by asset id: the copy below gives every forward a fresh one.
        var sourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        Il2CppSystem.Collections.Generic.List<ForwardData> sourceForwards = source.GetForwards(true);
        for (int i = 0; i < sourceForwards.Count; i++)
        {
            ForwardData? forward = sourceForwards[i];
            if (forward != null && forward.OriginalId is { Length: > 0 } original && forward.id is { Length: > 0 } id)
            {
                sourceIds.TryAdd(original, id);
            }
        }

        teams.SetQuickMatchTeam(source, false);
        TeamData team = teams.visitorTeam;
        ChallengeMapNode? challenge = matchNode?.TryCast<ChallengeMapNode>();
        var byOriginal = new Dictionary<string, ForwardData>(StringComparer.Ordinal);
        Il2CppSystem.Collections.Generic.List<ForwardData> current = team.GetForwards(true);
        int idsBack = 0;
        for (int i = 0; i < current.Count; i++)
        {
            ForwardData? forward = current[i];
            if (forward != null && forward.OriginalId is { Length: > 0 } original && byOriginal.TryAdd(original, forward) &&
                sourceIds.TryGetValue(original, out string? ownId) && forward.id != ownId)
            {
                // Before the lines are set below: they name each forward by this id.
                forward.id = ownId;
                idsBack++;
            }
        }

        // The roster in the host's order, empty places kept (their counts and stats were checked in CheckCampaignSide).
        int count = values.Int($"{Visitor}.forwards");
        var roster = new List<ForwardData?>();
        int added = 0;
        for (int i = 0; i < count; i++)
        {
            string id = values.Text($"{Visitor}.forward.{i}");
            if (id.Length == 0)
            {
                roster.Add(null);
                continue;
            }

            if (!byOriginal.TryGetValue(id, out ForwardData? forward))
            {
                forward = NewForward(id, challenge, forwardRepository);
                if (forward == null)
                {
                    problem = $"the visitor's skater {HostId(id)} isn't on this PC";
                    return false;
                }

                byOriginal[id] = forward;
                added++;
            }

            if (!CampaignMatchValues.TryParseForward(values.Text($"{Visitor}.stats.{i}"), MaxStat, out int[] stats, out bool clamped))
            {
                problem = $"{Visitor}.stats.{i} is {Quote(values.Text($"{Visitor}.stats.{i}"))}";
                return false;
            }

            if (clamped)
            {
                notes.Add($"the visitor's skater {i}'s stats {Quote(values.Text($"{Visitor}.stats.{i}"))} were outside 0 to {MaxStat}; clamped");
            }

            if (stats[4] is < 0 or > MaxStat)
            {
                // The case the Act 1 boss fix is for (a prisoner's jersey number past the stats' limit): taken as it is.
                notes.Add($"the visitor's skater {i}'s jersey number {stats[4]} is past 0 to {MaxStat} (taken as it is)");
            }

            // The host's values as they are (the game's setters would apply a talent's stat lock again).
            forward.shotPower = stats[0];
            forward.speed = stats[1];
            forward.checking = stats[2];
            forward.shotAccuracy = stats[3];
            forward.number = stats[4];
            roster.Add(forward);
        }

        Il2CppSystem.Collections.Generic.List<ForwardData> forwards = team.forwards;
        forwards.Clear();
        foreach (ForwardData? forward in roster)
        {
            // An empty place stays empty (null), as in the host's roster.
            forwards.Add(forward!);
        }

        int lines = values.Int($"{Visitor}.lines");
        Il2CppSystem.Collections.Generic.List<Lineup>? teamLines = team.lines;
        if ((teamLines?.Count ?? 0) != lines)
        {
            problem = $"the visitor team has {teamLines?.Count ?? 0} lines here and {lines} on the host";
            return false;
        }

        for (int k = 0; k < lines; k++)
        {
            Lineup line = teamLines![k];
            foreach (SkaterType slot in LineSlots)
            {
                string id = values.Text($"{Visitor}.line.{k}.{(int)slot}");
                line.SetSlotId(slot, id.Length > 0 && byOriginal.TryGetValue(id, out ForwardData? forward) ? forward.id : string.Empty);
            }
        }

        team.activeLineNumber = ActiveLine(values, Visitor, lines, out string? held);
        if (held != null)
        {
            notes.Add(held);
        }

        string goalieId = values.Text($"{Visitor}.goalie");
        if (goalieId.Length > 0 && (team.goalie == null || team.goalie.OriginalId != goalieId))
        {
            GoaltenderData? goalie = FindIn(challenge?.challengeElligibleGoalers, goalieId) ?? FindGoalie(goalieRepository, goalieId)
                ?? FindLoaded<GoaltenderData>(g => g.OriginalId == goalieId || g.id == goalieId);
            if (goalie == null)
            {
                problem = $"the visitor's goalie {HostId(goalieId)} isn't on this PC";
                return false;
            }

            team.goalie = goalie;
        }

        NoteRelics(values, team, notes);
        notes.Add($"visitor: a copy of {from} ({HostId(teamId)}), {roster.Count(f => f != null)} forwards with the host's stats, {added} of them not on that team, " +
            $"{idsBack} with their own ids back from the copy's fresh ones (as the host's game has them)");
        return SlotsAsHost(values, Visitor, team, out problem);
    }

    /// <summary>A copy of the forward with asset id <paramref name="id"/> (a Challenge's pick, a substitute): from the map node's pool, the skater list, or any loaded skater.</summary>
    private static ForwardData? NewForward(string id, ChallengeMapNode? challenge, IForwardRepository forwardRepository)
    {
        ForwardData? source = FindIn(challenge?.challengeElligiblePlayers, id);
        ForwardData? copy = source != null ? UnityEngine.Object.Instantiate(source).Cast<ForwardData>() : forwardRepository.GetForwardData(id, true);
        if (copy == null && FindLoaded<ForwardData>(f => f.OriginalId == id) is ForwardData loaded)
        {
            copy = UnityEngine.Object.Instantiate(loaded).Cast<ForwardData>();
        }

        if (copy == null)
        {
            return null;
        }

        // A fresh id of its own, as the game's own copies get, and the asset id the stream knows it by.
        copy.GenerateID();
        copy.OriginalId = id;
        return copy;
    }

    /// <summary>The five playing places must come out as the host's: the stream finds each skater by its asset id.</summary>
    private static bool SlotsAsHost(Values values, string side, TeamData team, out string problem)
    {
        var wrong = new List<string>();
        for (int i = 0; i < Slots; i++)
        {
            string want = values.Text($"{side}.slot.{i}");
            ForwardData? forward = team.GetForward(i);
            string have = forward != null ? forward.OriginalId ?? string.Empty : string.Empty;
            if (want.Length > 0 && want != have)
            {
                wrong.Add($"slot {i}: {HostId(have)} instead of {HostId(want)}");
            }
        }

        problem = wrong.Count == 0 ? string.Empty : $"the {side} lineup didn't come out as the host's ({string.Join("; ", wrong)})";
        return wrong.Count == 0;
    }

    /// <summary>The visitor's relics against the host's, for the log (they come with the team; a difference isn't fixed).</summary>
    private static void NoteRelics(Values values, TeamData team, List<string> notes)
    {
        var host = new List<string>();
        for (int i = 0; i < values.Int($"{Visitor}.relics"); i++)
        {
            host.Add(values.Text($"{Visitor}.relic.{i}"));
        }

        var here = new List<string>();
        Il2CppSystem.Collections.Generic.List<Il2CppRogue.Relic>? relics = team.relics;
        for (int i = 0; relics != null && i < relics.Count && i < MaxRelics; i++)
        {
            here.Add(relics[i] != null ? relics[i].id ?? string.Empty : string.Empty);
        }

        List<string> onlyHost = host.Except(here, StringComparer.Ordinal).ToList();
        List<string> onlyHere = here.Except(host, StringComparer.Ordinal).ToList();
        if (onlyHost.Count + onlyHere.Count > 0)
        {
            notes.Add($"visitor relics differ (host only: {string.Join(", ", onlyHost.Select(HostId))}; here only: {string.Join(", ", onlyHere.Select(HostId))})");
        }
    }

    /// <summary>The rules LaunchMatch sets beyond an exhibition's: the objective, the restrictions and the goal target.</summary>
    private static void ApplyCampaignRules(Values values, InGameData data)
    {
        if (data.gameObjectiveData != null)
        {
            data.gameObjectiveData.gameObjective = (GameObjectiveType)values.Int("rules.objective");
        }

        if (data.gameRestrictionData != null)
        {
            Il2CppSystem.Collections.Generic.List<GameRestriction> restrictions = data.gameRestrictionData.gameRestrictions ?? new Il2CppSystem.Collections.Generic.List<GameRestriction>();
            data.gameRestrictionData.gameRestrictions = restrictions;
            restrictions.Clear();
            CampaignMatchValues.TryParseList(values.Text("rules.restrictions"), MaxRestrictions, 0, int.MaxValue, out int[] list);
            foreach (int restriction in list)
            {
                restrictions.Add((GameRestriction)restriction);
            }
        }

        // As the host's match loaded: the match's own set-up then works out its target from the node, as the host's did.
        MaxGoal.goalToScore = values.Int("rules.goalToScore");
    }

    /// <summary>
    /// The campaign's current node, which the match reads (its end conditions, its goal target): as the host's
    /// LaunchMatch set it (<c>CampaignState.SetCurrentNode</c>) when this PC's world map has the node, else its blueprint
    /// and point directly. And the run's floor, as the host's <c>MapPlayerTracker.SendPlayerToNode</c> set it before its
    /// LaunchMatch (that call is held back on a friend: without this every campaign match on a friend read "differs from
    /// the host's: campaign.floor N vs N-1"). The setup's floor was checked (0 to 255); a friend's run is
    /// never saved, and the next checkpoint brings the host's run anyway.
    /// </summary>
    private static void SetCampaignNode(CampaignState campaign, RunData run, CampaignNode node, int floor, List<string> notes)
    {
        if (run.currentFloor != floor)
        {
            notes.Add($"floor {floor} (as the host's SendPlayerToNode set it; this PC's run had {run.currentFloor})");
            run.currentFloor = floor;
        }

        if (node.MapNode != null)
        {
            campaign.SetCurrentNode(node.MapNode);
            notes.Add($"current node: the world map's node at {node.X},{node.Y} (as the host's LaunchMatch set it)");
        }
        else if (node.Blueprint != null)
        {
            campaign.currentNode = node.Blueprint;
            var point = new Vector2(node.X, node.Y);
            run.currentNodePoint = point;
            run.lastNodePoint = point;
            notes.Add($"current node: the world map isn't loaded here, so its blueprint ({node.Blueprint.name}) and point {node.X},{node.Y} were set directly");
        }
        else
        {
            notes.Add($"current node: no blueprint for a {NodeTypeName(node.Type)} node here, so this PC's is left as it was");
        }
    }

    /// <summary>The map node at the host's point when this PC's world map is loaded, and the node type's blueprint.</summary>
    private static CampaignNode FindCampaignNode(Values values, List<string> notes)
    {
        CampaignMatchValues.TryParsePoint(values.Text(CampaignNodeKey), MaxMapCoordinate, out int x, out int y);
        int nodeType = values.Int("campaign.nodeType");
        MapNode? mapNode = null;
        foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapNode>(), FindObjectsSortMode.None))
        {
            MapNode? candidate = found?.TryCast<MapNode>();
            Point? point = candidate?.Node?.point;
            if (point != null && point.x == x && point.y == y)
            {
                mapNode = candidate;
                break;
            }
        }

        NodeBlueprint? blueprint = mapNode?.Blueprint;
        if (mapNode != null && (blueprint == null || (int)blueprint.nodeType != nodeType))
        {
            notes.Add($"the map node at {x},{y} here isn't a {NodeTypeName(nodeType)} node; not used");
            mapNode = null;
            blueprint = null;
        }

        blueprint ??= FindBlueprint(values.Text("campaign.blueprint"), nodeType);
        return new CampaignNode(mapNode, blueprint, x, y, nodeType);
    }

    /// <summary>The node type's blueprint, by the host's name if there's one of that name.</summary>
    private static NodeBlueprint? FindBlueprint(string name, int nodeType)
    {
        var candidates = new List<NodeBlueprint>();
        Il2CppSystem.Collections.Generic.List<NodeBlueprint>? listed = FromProject<NodeBlueprintRepository>()?.nodeBlueprints;
        for (int i = 0; listed != null && i < listed.Count; i++)
        {
            if (listed[i] != null)
            {
                candidates.Add(listed[i]);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (UnityEngine.Object found in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<NodeBlueprint>()))
            {
                if (found?.TryCast<NodeBlueprint>() is NodeBlueprint blueprint)
                {
                    candidates.Add(blueprint);
                }
            }
        }

        List<NodeBlueprint> ofType = candidates.Where(b => (int)b.nodeType == nodeType).ToList();
        return ofType.FirstOrDefault(b => b.name == name) ?? ofType.FirstOrDefault();
    }

    /// <summary>A loaded object of type <typeparamref name="T"/> that <paramref name="match"/> picks (a team or skater that's in memory but in no list).</summary>
    private static T? FindLoaded<T>(Func<T, bool> match)
        where T : UnityEngine.Object
    {
        foreach (UnityEngine.Object found in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            T? typed = found?.TryCast<T>();
            if (typed != null && match(typed))
            {
                return typed;
            }
        }

        return null;
    }

    /// <summary>The goalie with asset id (or own id) <paramref name="id"/> in <paramref name="pool"/> (a Challenge node's), or null.</summary>
    private static GoaltenderData? FindIn(Il2CppSystem.Collections.Generic.List<GoaltenderData>? pool, string id)
    {
        for (int i = 0; pool != null && i < pool.Count; i++)
        {
            GoaltenderData? goalie = pool[i];
            if (goalie != null && (goalie.OriginalId == id || goalie.id == id))
            {
                return goalie;
            }
        }

        return null;
    }

    /// <summary>What a campaign load changes beyond an exhibition's, so a load that doesn't go ahead puts it back.</summary>
    private static void AddCampaignUndo(SetupSnapshot snapshot, InGameData data, CampaignState campaign, RunData run)
    {
        GameObjectiveData? objectiveData = data.gameObjectiveData;
        GameObjectiveType objective = objectiveData != null ? objectiveData.gameObjective : default;
        GameRestrictionData? restrictionData = data.gameRestrictionData;
        Il2CppSystem.Collections.Generic.List<GameRestriction>? restrictionList = restrictionData?.gameRestrictions;
        var restrictions = new List<GameRestriction>();
        for (int i = 0; restrictionList != null && i < restrictionList.Count; i++)
        {
            restrictions.Add(restrictionList[i]);
        }

        int goal = MaxGoal.goalToScore;
        NodeBlueprint currentNode = campaign.currentNode;
        Vector2 nodePoint = run.currentNodePoint;
        Vector2 lastPoint = run.lastNodePoint;
        snapshot.Add("campaign rules", () =>
        {
            if (objectiveData != null)
            {
                objectiveData.gameObjective = objective;
            }

            if (restrictionData != null)
            {
                restrictionData.gameRestrictions = restrictionList;
                restrictionList?.Clear();
                foreach (GameRestriction restriction in restrictions)
                {
                    restrictionList?.Add(restriction);
                }
            }

            MaxGoal.goalToScore = goal;
        });
        snapshot.Add("current node", () =>
        {
            campaign.currentNode = currentNode;
            run.currentNodePoint = nodePoint;
            run.lastNodePoint = lastPoint;
        });
    }

    /// <summary>Everything a campaign load set, and every difference from the host's setup, in the log.</summary>
    private static void LogCampaignLoad(Values values, TeamSelection teams, RunData run, List<string> notes)
    {
        string Playing(TeamData team) => string.Join(" ", Enumerable.Range(0, Slots).Select(i => team.GetForward(i)?.OriginalId is string id ? HostId(id) : "-"));
        string line = string.Create(CultureInfo.InvariantCulture,
            $"Match setup: loading the host's campaign match: node {HostId(values.Text(CampaignNodeKey))} ({NodeTypeName(values.Int("campaign.nodeType"))}, " +
            $"blueprint {HostId(values.Text("campaign.blueprint"))}), act {values.Int(CampaignActKey)} floor {values.Int("campaign.floor")}; run {HostId(run.runID)} (squad {HostId(run.squadId)}, " +
            $"difficulty {run.difficultyLevel}, rule {run.gameRule}); home {HostId(teams.homeTeam.id)} (the run's team, {LogNames.TextShape(teams.homeTeam.teamName)}): {Playing(teams.homeTeam)}, line {teams.homeTeam.activeLineNumber}; " +
            $"visitor {HostId(teams.visitorTeam.id)}: {Playing(teams.visitorTeam)}, line {teams.visitorTeam.activeLineNumber}, goalie {HostId(teams.visitorTeam.goalie?.OriginalId)}; " +
            $"{values.Int("rules.periods")} period(s) of {values.Float("rules.periodLength"):0.#} min, overtime {values.Bool("rules.overtime")}, end conditions [{HostId(values.Text("rules.endConditions"))}], " +
            $"objective {values.Int("rules.objective")}, restrictions [{HostId(values.Text("rules.restrictions"))}], goal target {values.Int("rules.goalToScore")}; " +
            $"{values.Int("mode.skaterHome")} v {values.Int("mode.skaterVisitor")} skaters.");
        ModLog.Info(SafeText.Clean(line, 1200));
        if (notes.Count > 0)
        {
            ModLog.Info(SafeText.Clean($"Match setup: set or found differently from the host's: {string.Join("; ", notes)}.", 1200));
        }
    }

    /// <summary>
    /// A value from the host, short, for a reason it may be shown in: quoted and cut as it is when it has an identifier's shape
    /// (every honest host's does), else as its length only (D146: a modified host chooses every byte).
    /// </summary>
    private static string Quote(string? text) => text == null ? "nothing" : text.Length == 0 ? "\"\""
        : !HostText.IsKey(text) ? $"\"{HostText.KeyForLog(text)}\"" : text.Length > 12 ? $"\"{text[..12]}...\"" : $"\"{text}\"";

    /// <summary>
    /// An id, key or name a host's setup or run file gave, for a log line or a reason: as it is when it has an identifier's
    /// shape (every honest host's does: GUIDs, asset ids, blueprint names, points), "" for none, else its length only (D146).
    /// </summary>
    private static string HostId(string? id) => string.IsNullOrEmpty(id) ? string.Empty : HostText.KeyForLog(id);

    /// <summary>The campaign map node a load found: this PC's own (when its world map is loaded), its blueprint, and the host's point and type.</summary>
    private sealed record CampaignNode(MapNode? MapNode, NodeBlueprint? Blueprint, int X, int Y, int Type);

    /// <summary>Where a campaign match's load stands after a step.</summary>
    private enum CampaignStep
    {
        /// <summary>Still waiting (for the host's run, or a scene load).</summary>
        Waiting,

        /// <summary>The match is loading.</summary>
        Loaded,

        /// <summary>It can't load here.</summary>
        Failed,
    }

    /// <summary>A campaign match waiting for the host's run, or for a scene load to finish, before it loads.</summary>
    private sealed class CampaignLoad
    {
        private string _what = string.Empty;
        private uint? _run;
        private uint? _held;

        /// <summary>A load of the host's setup <paramref name="values"/>, needing run checkpoint <paramref name="needed"/>, from <paramref name="since"/>.</summary>
        public CampaignLoad(Values values, uint needed, double since)
        {
            Values = values;
            Needed = needed;
            Since = since;
            LimitFrom = since;
        }

        /// <summary>The host's setup.</summary>
        public Values Values { get; }

        /// <summary>The run checkpoint the match needs (0: any of the host's).</summary>
        public uint Needed { get; }

        /// <summary>When the wait started (<see cref="FrameClock.Seconds"/>).</summary>
        public double Since { get; }

        /// <summary>What the run's limit counts from: when the wait started, or when the wait for the host's custom content ended.</summary>
        public double LimitFrom { get; set; }

        /// <summary>True while the run sync loads the host's run for it.</summary>
        public bool Applying { get; set; }

        /// <summary>The run sync's answer, acted on in the next step.</summary>
        public FollowResult? Result { get; set; }

        /// <summary>What it waits for now, for the log (worded only when read: the load asks again every frame).</summary>
        public string Waiting => _run is uint run
            ? _held is uint held ? $"{RunName(run)} hasn't arrived; this PC holds {RunName(held)}" : $"{RunName(run)} is loading"
            : _what;

        /// <summary>True while what it waits for is the host's run (not a scene load).</summary>
        public bool ForRun { get; set; }

        /// <summary>The wait for the host's custom content, while the run is here and that isn't (else null).</summary>
        public ContentWait? Content { get; set; }

        /// <summary>True once that wait ended without it: the match loads without the run team's looks.</summary>
        public bool ContentGivenUp { get; set; }

        /// <summary>It waits for <paramref name="what"/> (<see cref="Waiting"/> says it as it is).</summary>
        public void WaitFor(string what)
        {
            _what = what;
            _run = null;
            _held = null;
        }

        /// <summary>
        /// It waits for the host's run checkpoint <paramref name="run"/>: loading here, or not here yet while this PC holds
        /// <paramref name="held"/> (null: it's loading).
        /// </summary>
        public void WaitForRun(uint run, uint? held)
        {
            _run = run;
            _held = held;
        }
    }
}
#endif
