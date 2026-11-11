#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppData;
using Il2CppEvents;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppMichsky.LSS;
using Il2CppScriptableStates;
using Il2CppSkaters;
using Il2CppState;
using Il2CppState.EndConditions;
using Il2CppTape2Tape.Customization;
using Il2CppTape2Tape.Customization.UI;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Replay;
using T2TOnline.Plugin.Streaming;
using UnityEngine.SceneManagement;

namespace T2TOnline.Plugin.Match;

/// <summary>One loaded match, for as long as it stays loaded (a rematch or a new match is a new token).</summary>
/// <param name="SceneHandle">The match scene's handle.</param>
/// <param name="Serial">Counts loaded matches this session; 0 is no match.</param>
internal readonly record struct MatchToken(int SceneHandle, int Serial)
{
    /// <summary>True for "no match".</summary>
    public bool IsNone => Serial == 0;
}

/// <summary>
/// An exhibition ("Play Now") match's setup as named values, read on the host and loaded on a friend's PC
/// (D30):
/// <list type="bullet">
/// <item>mode, difficulty, rules, kit colours, both teams by asset id, every forward by asset id (<c>OriginalId</c>) in
/// roster order and in each line's slots, the active line, the goalies, and the referee;</item>
/// <item>random teams ("HRD"/"VRD"): each slot's forward from the random pools with its body skins, rebuilt on the friend
/// in place of the game's own roll (<c>GameLoader.InitializeRandomTeams</c> prefix);</item>
/// <item>the referee is a pure function of the two teams, and also forced (<c>TeamSelection.GetRandomReferee</c>
/// prefix).</item>
/// </list>
/// The host's setup is read the moment the game has fixed it (a postfix on <c>InitializeRandomTeams</c>, early in the
/// match's load), so friends start loading while the host still is. A loaded match is announced by
/// <c>GameplayEvents.AllSkatersInitialized</c>. Loading goes through the game's own loading screen, as Play Now does.
/// Campaign matches (M4) add their node, rules and opponent to the same setup: MatchSetup.Campaign.cs.
/// </summary>
internal static partial class MatchSetup
{
    private const string Home = "home";
    private const string Visitor = "visitor";
    private const int Slots = 5;
    private const int GameTypeQuickGame = 0;
    private const int GameModeNormal = 0;
    private const string MenuScene = "start_screen";
    private const string MatchScene = "standard_game";

    // The game's own ranges for a setup's numbers (Oct 29 build), so a bad or hostile setup is refused before it changes
    // anything: the game has period labels up to the fourth (its tournament offers 1 to 3, of 2 to 5 minutes), a team
    // has five places, at most two lines (TeamData.SetActiveLine clamps to 1-2; below 1 its CurrentLine throws), and an
    // exhibition team five forwards (the cap leaves room for a bench).
    private const int MaxPeriods = 4;
    private const float MaxPeriodMinutes = 20f;
    private const int MaxLines = 2;
    private const int MaxRoster = 32;
    private const int MaxEndConditions = 16;

    private static readonly SkaterType[] LineSlots = { (SkaterType)0, (SkaterType)1, (SkaterType)2, (SkaterType)3, (SkaterType)4 };

    private static Forced? _forced;
    private static Il2CppSystem.Action? _listener;
    private static double _nextListenerCheck;
    private static int _serial;
    private static int _forcedSerial;
    private static Scene _matchScene;
    private static double _initializedAt;
    private static bool _installed;

    /// <summary>Match loads seen so far (while hosting, the relay sends the setup of each new one).</summary>
    public static int Captures { get; private set; }

    /// <summary>
    /// The setup read at the last match load, or null when that match can't be streamed or this PC wasn't hosting then
    /// (<see cref="LastCaptureProblem"/>).
    /// </summary>
    public static List<KeyValuePair<string, NetValue>>? LastCapture { get; private set; }

    /// <summary>Why the last loaded match can't be streamed, or empty.</summary>
    public static string LastCaptureProblem { get; private set; } = string.Empty;

    /// <summary>The custom content (teams, skaters, goalies, logos) the last loaded match's setup names, for friends (<see cref="CustomContent"/>).</summary>
    public static IReadOnlyList<CustomItem> LastCaptureContent { get; private set; } = Array.Empty<CustomItem>();

    /// <summary>
    /// The game type of the last match loaded (<c>TeamSelection.gameType</c>: 1 is the campaign's,
    /// <c>CampaignMatchValues.CampaignGameType</c>), or -1 when unknown; read even when its setup can't be streamed (run
    /// sync tells a player's own campaign match from an exhibition with it).
    /// </summary>
    public static int LoadedGameType { get; private set; } = -1;

    /// <summary>How many matches have loaded so far (the serial of the newest; a match loading now will be the next).</summary>
    public static int LoadedSerial => _serial;

    /// <summary>Seconds since the newest match loaded (infinity before the first).</summary>
    public static double SecondsSinceLoaded => _serial == 0 ? double.PositiveInfinity : FrameClock.Seconds - _initializedAt;

    /// <summary>
    /// True while a forced load waits for its match (or first: a campaign match for the host's run, an exhibition for the
    /// host's custom content).
    /// </summary>
    public static bool Loading => _forced != null || _campaignLoad != null || _contentLoad != null;

    /// <summary>
    /// True while the game's loading screen brings in a host's match this PC asked for (a forced load is armed and its match
    /// isn't in yet). A load still waiting for the host's run or custom content hasn't started one: cancelled, it brings
    /// nothing in.
    /// </summary>
    public static bool LoadingHostScene => _forced != null;

    /// <summary>
    /// True while this PC hosts a session (the match relay sets it): only then is a loading match's setup, with the custom
    /// content it names, read for friends. With no session a match load only counts and notes its game type.
    /// </summary>
    public static bool ReadForFriends { get; set; }

    /// <summary>
    /// True while the match in play here is one a forced load brought in (a host's match on a friend's PC, or F2's): it
    /// stays the host's after the session that loaded it ends, until its scene unloads.
    /// </summary>
    public static bool HostMatchHere => _forcedSerial != 0 && CurrentMatch(out _).Serial == _forcedSerial;

    /// <summary>True while the game's loading screen is starting a scene load: it ignores another one meanwhile.</summary>
    public static bool SceneLoadStarting => UnityEngine.Object.FindObjectOfType<LoadingScreenManager>() is { isStartingLoad: true };

    /// <summary>
    /// What an exhibition's load must wait for now (said for the log), or null when it can be tried: the loading screen
    /// starting a scene (<c>isStartingLoad</c> stays set from <c>LoadScene</c> until its manager goes with the old scene, and
    /// any other load is ignored meanwhile), or this game not yet at the main menu or a match.
    /// </summary>
    public static string? ExhibitionLoadWait() =>
        SceneLoadStarting ? "a scene load is under way"
        : SceneManager.GetActiveScene().name is MenuScene or MatchScene ? null
        : "this game isn't at its main menu yet";

    /// <summary>Disarms a forced load that won't happen (it failed, timed out, or was cancelled), so the next match is the game's own.</summary>
    public static void CancelLoad()
    {
        if (_forced != null)
        {
            _forced = null;
            ModLog.Info("Match setup: the pending load was cancelled.");
        }

        CancelCampaignLoad();
        CancelContentLoad();
    }

    /// <summary>Patches the match loader (random teams, referee). False, logged, when it can't.</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            MethodInfo random = AccessTools.Method(typeof(GameLoader), nameof(GameLoader.InitializeRandomTeams));
            MethodInfo referee = AccessTools.Method(typeof(TeamSelection), nameof(TeamSelection.GetRandomReferee));
            harmony.Patch(random, prefix: new HarmonyMethod(typeof(MatchSetup), nameof(RandomTeamsPrefix)), postfix: new HarmonyMethod(typeof(MatchSetup), nameof(RandomTeamsPostfix)));
            harmony.Patch(referee, prefix: new HarmonyMethod(typeof(MatchSetup), nameof(RefereePrefix)));
            InstallFallbackPlayer(harmony);
            _installed = true;
            ModLog.Info("Match setup: on (match loads are read for friends, and a friend's game can load the host's match).");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Match setup: couldn't patch the match loader; online matches won't work this session", ex);
            return false;
        }
    }

    /// <summary>
    /// Call once per frame: steps a campaign match's load that waits for the host's run, and keeps a listener on the
    /// "match loaded" event. A persistent listener should survive the game's wipe of match events when a match is torn
    /// down, but that's checked once a second, not assumed.
    /// </summary>
    public static void Update()
    {
        CustomContent.Update();
        UpdateContentLoad();
        UpdateCampaignLoad();
        double now = FrameClock.Seconds;
        if (now < _nextListenerCheck)
        {
            return;
        }

        _nextListenerCheck = now + 1;
        try
        {
            var loaded = GameplayEvents.AllSkatersInitialized;
            if (loaded == null)
            {
                return;
            }

            Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = loaded.m_Listeners;
            if (_listener != null && listeners != null)
            {
                for (int i = 0; i < listeners.Count; i++)
                {
                    if (listeners[i]?.Pointer == _listener.Pointer)
                    {
                        return;
                    }
                }
            }

            bool again = _listener != null;
            if (_listener == null)
            {
                Il2CppSystem.Action created = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnAllSkatersInitialized))
                    ?? throw new InvalidOperationException("the listener couldn't be made");
                EventChannel.ModListeners.Add(created.Pointer);
                _listener = created;
            }

            loaded.RegisterPersistentDelegate(_listener, "T2TOnline");
            if (again)
            {
                ModLog.Info("Match setup: the match-loaded listener was gone (the game wiped its events); listening again.");
            }
        }
        catch (Exception ex)
        {
            _nextListenerCheck = now + 30;
            ModLog.Error("Match setup: couldn't listen for loaded matches", ex);
        }
    }

    /// <summary>
    /// The match in play, or none: loaded (its skaters initialized) and its scene still loaded. <paramref name="scene"/>
    /// is the game's replay scene for it once the game's recorder has captured from it (null before: the recorder starts
    /// at the first faceoff).
    /// </summary>
    public static MatchToken CurrentMatch(out ReplayScene? scene)
    {
        scene = null;
        if (_serial == 0 || !_matchScene.isLoaded)
        {
            return default;
        }

        ReplayScene? last = SnapshotProbe.LastScene;
        if (last != null && SnapshotProbe.SecondsSinceLastSnapshot < FrameClock.Seconds - _initializedAt)
        {
            scene = last;
        }

        return new MatchToken(_matchScene.handle, _serial);
    }

    /// <summary>Reads the setup of the match being played now. False, with why, if there's none or it can't be streamed.</summary>
    public static bool TryCapture(out List<KeyValuePair<string, NetValue>> setup, out string problem) => TryCapture(out setup, out _, out problem);

    /// <summary>
    /// Reads the setup of the match being played now, and the custom content it names (<paramref name="content"/>: what
    /// friends need sent). False, with why, if there's none or it can't be streamed.
    /// </summary>
    public static bool TryCapture(out List<KeyValuePair<string, NetValue>> setup, out List<CustomItem> content, out string problem)
    {
        setup = new List<KeyValuePair<string, NetValue>>();
        content = new List<CustomItem>();
        try
        {
            TeamSelection? teams = Settings<TeamSelection>();
            InGameData? data = Settings<InGameData>();
            if (teams == null || data == null)
            {
                problem = $"the game's {(teams == null ? "team selection" : "match rules")} isn't available";
                return false;
            }

            return Capture(teams, data, setup, content, out problem);
        }
        catch (Exception ex)
        {
            problem = $"reading it failed ({ModLog.SafeErrorText(ex)})";
            return false;
        }
    }

    /// <summary>
    /// Loads the match <paramref name="setup"/> describes, as Play Now would (the game's loading screen, then
    /// <c>standard_game</c>). False, with why, if this PC can't (a team or skater it doesn't have, a mode not supported, a
    /// value outside the game's range, or the game isn't at its main menu or in an exhibition); then the game's match
    /// settings are as they were. From the moment the load starts, this PC's save writes are refused until the loaded
    /// match's scene unloads (<see cref="SaveGuard"/>). A campaign match may first wait for the host's run, an exhibition for
    /// the host's custom content; if that fails, <see cref="TakeLoadProblem"/> says why.
    /// </summary>
    public static bool TryLoad(IReadOnlyList<KeyValuePair<string, NetValue>> setup, out string problem)
    {
        _loadProblem = null;

        // A newer setup replaces one still waiting for custom content.
        CancelContentLoad();
        if (!_installed)
        {
            problem = "the match loader isn't patched (see the log at start-up)";
            return false;
        }

        try
        {
            return Load(new Values(setup), out problem);
        }
        catch (Exception ex)
        {
            _forced = null;
            problem = $"setting it up failed ({ModLog.SafeErrorText(ex)})";
            ModLog.Error("Match setup: loading a match failed", ex);
            return false;
        }
    }

    /// <summary>Leaves the match for the main menu the way the game's own "quit" after a match does.</summary>
    public static void ReturnToMenu()
    {
        GameLoader? loader = UnityEngine.Object.FindObjectOfType<GameLoader>();
        IGameModeControllerWrapper.Quit(loader);
    }

    /// <summary>
    /// A short description for logs: teams, rules, referee. A custom team's name, and a campaign run's team's (a run may be
    /// on a custom team), is player-written: only its shape (D127). On a friend the setup is the host's: a random team's
    /// nickname (the game's <c>HRD</c> or <c>VRD</c>) and the campaign node are written only when shaped like an id (D146).
    /// </summary>
    public static string Describe(IReadOnlyList<KeyValuePair<string, NetValue>> setup) => Describe(new Values(setup));

    private static string Describe(Values values)
    {
        string Team(string side) => values.Bool($"{side}.random") ? $"random team ({HostText.KeyForLog(values.Text($"{side}.nickname"))})"
            : side == Home && values.Int("mode.gameType") == GameTypeCampaign ? $"the run's team ({LogNames.TextShape(values.Text($"{side}.name"))})"
            : TeamLogText.Of(values.Text($"{side}.team"), values.Text($"{side}.name"), LogNames.TextShape);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Team(Home)} vs {Team(Visitor)}, {values.Int("rules.periods")} periods of {values.Float("rules.periodLength"):0.#} min, difficulty {values.Int("mode.difficulty")}") +
            DescribeCampaign(values);
    }

    // ------------------------------------------------------------------ capture

    private static bool Capture(TeamSelection teams, InGameData data, List<KeyValuePair<string, NetValue>> setup, List<CustomItem> content, out string problem)
    {
        int gameType = (int)teams.gameType;
        int gameMode = (int)teams.gameMode;
        bool campaign = gameType == GameTypeCampaign && gameMode == GameModeNormal && !teams.onboard;
        if (!campaign && (gameType != GameTypeQuickGame || gameMode != GameModeNormal || teams.onboard))
        {
            problem = $"only exhibition (Play Now) and campaign matches can be played online yet; this one is game type {teams.gameType}, mode {teams.gameMode}{(teams.onboard ? ", tutorial" : string.Empty)}";
            return false;
        }

        if (teams.homeTeam == null || teams.visitorTeam == null)
        {
            problem = "a team is missing";
            return false;
        }

        void Add(string key, NetValue value) => setup.Add(new KeyValuePair<string, NetValue>(key, value));
        Add("mode.gameType", NetValue.FromInt(gameType));
        Add("mode.gameMode", NetValue.FromInt(gameMode));
        Add("mode.difficulty", NetValue.FromInt(teams.difficultyLevel != null ? teams.difficultyLevel.index : -1));
        Add("mode.powerupFree", NetValue.FromBool(teams.powerupFree));
        Add("mode.manualGoalie", NetValue.FromBool(teams.isManualGoalie));
        Add("mode.injuries", NetValue.FromBool(teams.areInjuriesEnabled));
        Add("mode.skaterHome", NetValue.FromInt(teams.skaterHome));
        Add("mode.skaterVisitor", NetValue.FromInt(teams.skaterVisitor));
        Add("colors.homeOverride", NetValue.FromBool(teams.isHomeColorOverride));
        Add("colors.homeAlternate", NetValue.FromBool(teams.isHomeColorAlternate));
        Add("colors.visitorOverride", NetValue.FromBool(teams.isVisitorColorOverride));
        Add("colors.visitorAlternate", NetValue.FromBool(teams.isVisitorColorAlternate));
        TimerRuleStateData timer = data.timerRule;
        Add("rules.periods", NetValue.FromInt(timer.numOfPeriod));
        Add("rules.periodLength", NetValue.FromFloat(timer.periodLength));
        Add("rules.overtime", NetValue.FromBool(timer.hasOvertime));
        Add("rules.normalTime", NetValue.FromBool(timer.isNormalTime));
        var conditions = new List<int>();
        Il2CppSystem.Collections.Generic.List<EndOfGameConditionType>? list = data.endOfGameConditionData?.endOfGameConditions;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            conditions.Add((int)list[i]);
        }

        Add("rules.endConditions", NetValue.FromString(string.Join(",", conditions.Select(c => c.ToString(CultureInfo.InvariantCulture)))));
        Add("rules.tieBreaker", NetValue.FromInt((int)data.tieBreaker));
        Add("rules.tournament", NetValue.FromBool(data.isTournamentMatch));
        Add("referee", NetValue.FromString(teams.GetRandomReferee()?.OriginalId ?? string.Empty));
        CaptureSide(teams.homeTeam, Home, Add, goalieById: campaign);
        CaptureSide(teams.visitorTeam, Visitor, Add);
        if (campaign && !CaptureCampaign(teams, data, Add, out problem))
        {
            return false;
        }

        // An exhibition's custom teams, skaters, goalies and logos exist only on the PC that made them: named here, sent to
        // friends by the relay. A campaign's teams are the run's and the game's; its run team's custom logo and look are
        // named too, so a friend's load waits for them.
        var refs = new List<CustomRef>();
        if (campaign)
        {
            CustomContent.CollectRun(content, refs);
        }
        else if (!CustomContent.Collect(teams.homeTeam, teams.visitorTeam, content, refs, out problem))
        {
            return false;
        }

        CustomContentValues.Add(Add, refs);

        if (setup.Count > Core.Protocol.MatchSetupMessage.MaxEntries - 8)
        {
            problem = $"its setup is too large to send ({setup.Count} values)";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// One side's team, roster, lines and goalie by asset id. <paramref name="goalieById"/> names the goalie by its own id
    /// instead: a campaign run's file keeps a goalie's id but not its asset id, so that's what a friend's copy of the run
    /// can match.
    /// </summary>
    private static void CaptureSide(TeamData team, string side, Action<string, NetValue> add, bool goalieById = false)
    {
        string nickname = team.nickname ?? string.Empty;
        bool random = nickname is "HRD" or "VRD";
        add($"{side}.team", NetValue.FromString(team.id ?? string.Empty));
        add($"{side}.name", NetValue.FromString(team.teamName ?? string.Empty));
        add($"{side}.nickname", NetValue.FromString(nickname));
        add($"{side}.random", NetValue.FromBool(random));
        add($"{side}.goalie", NetValue.FromString(team.goalie == null ? string.Empty : (goalieById ? team.goalie.id : team.goalie.OriginalId) ?? string.Empty));
        add($"{side}.activeLine", NetValue.FromInt(team.activeLineNumber));
        for (int i = 0; i < Slots; i++)
        {
            ForwardData? forward = team.GetForward(i);
            add($"{side}.slot.{i}", NetValue.FromString(forward != null ? forward.OriginalId ?? string.Empty : string.Empty));
            if (random && forward != null)
            {
                add($"{side}.skin.{i}", NetValue.FromString(forward.bodySkin ?? string.Empty));
                add($"{side}.awaySkin.{i}", NetValue.FromString(forward.bodyAwaySkin ?? string.Empty));
            }
        }

        if (random)
        {
            return;
        }

        Il2CppSystem.Collections.Generic.List<ForwardData> forwards = team.GetForwards(true);
        add($"{side}.forwards", NetValue.FromInt(forwards.Count));
        for (int i = 0; i < forwards.Count; i++)
        {
            ForwardData? forward = forwards[i];
            add($"{side}.forward.{i}", NetValue.FromString(forward != null ? forward.OriginalId ?? string.Empty : string.Empty));
        }

        Il2CppSystem.Collections.Generic.List<Lineup>? lines = team.lines;
        int lineCount = lines?.Count ?? 0;
        add($"{side}.lines", NetValue.FromInt(lineCount));
        for (int k = 0; k < lineCount; k++)
        {
            Lineup line = lines![k];
            foreach (SkaterType slot in LineSlots)
            {
                string id = line?.GetSlotId(slot) ?? string.Empty;
                ForwardData? forward = id.Length > 0 ? team.GetForwardById(id) : null;
                add($"{side}.line.{k}.{(int)slot}", NetValue.FromString(forward != null ? forward.OriginalId ?? string.Empty : string.Empty));
            }
        }
    }

    // ------------------------------------------------------------------ load

    private static bool Load(Values values, out string problem)
    {
        if (values.Int("mode.gameType") == GameTypeCampaign && values.Int("mode.gameMode") == GameModeNormal)
        {
            return LoadCampaign(values, out problem);
        }

        if (values.Int("mode.gameType") != GameTypeQuickGame || values.Int("mode.gameMode") != GameModeNormal)
        {
            problem = "only exhibition and campaign matches can be loaded";
            return false;
        }

        // A friend's exhibition loads behind the loading transition its controller screen's exit showed here, as on the host (no
        // LoadReady of its own): a load that can't go ahead takes that one down at once (MatchRelay's failure, LoadGivenUp).
        Campaign.Menus.FriendTransitions.LoadGoesAhead(Campaign.Menus.FriendTransitions.HostMatch);

        if ((CheckRules(values) ?? CheckSide(values, Home) ?? CheckSide(values, Visitor)) is string wrong)
        {
            // Short: the host's status box shows it too.
            problem = $"bad setup: {wrong}";
            return false;
        }

        // The host's custom content first: the match loads once every piece it names is here (it comes on another channel).
        if (!CustomContentValues.TryRead(values.Setup, out List<CustomRef> refs, out string badRefs))
        {
            problem = $"bad setup: {badRefs}";
            return false;
        }

        CustomContent.BeginLoad(refs);
        if (CustomContent.Waiting())
        {
            CancelContentLoad();
            _contentLoad = new ContentLoad(values, new Core.Stream.ContentWait(FrameClock.Seconds));
            ModLog.Info($"Match setup: the match waits for the host's custom content ({SafeText.Clean(CustomContent.WaitingFor(), 300)}); it loads as soon as that's here.");
            problem = string.Empty;
            return true;
        }

        TeamSelection? teams = Settings<TeamSelection>();
        InGameData? data = Settings<InGameData>();
        DifficultySettings? difficulties = Settings<DifficultySettings>();
        ITeamRepository? teamRepository = Repository<ITeamRepository>(p => new ITeamRepository(p));
        IForwardRepository? forwardRepository = Repository<IForwardRepository>(p => new IForwardRepository(p));
        IGoalieRepository? goalieRepository = Repository<IGoalieRepository>(p => new IGoalieRepository(p));
        LoadingScreenManager? loading = UnityEngine.Object.FindObjectOfType<LoadingScreenManager>();
        var missing = new List<string>();
        if (teams == null) missing.Add("team selection");
        if (data == null) missing.Add("match rules");
        if (difficulties == null) missing.Add("difficulty settings");
        if (teamRepository == null) missing.Add("team list");
        if (forwardRepository == null) missing.Add("skater list");
        if (goalieRepository == null) missing.Add("goalie list");
        if (loading == null) missing.Add("loading screen");
        if (missing.Count > 0)
        {
            problem = $"the game's {string.Join(", ", missing)} {(missing.Count == 1 ? "isn't" : "aren't")} available here (is the game at its menus?)";
            return false;
        }

        // Only from the main menu or an exhibition (Play Now, or the host's last match): anywhere else (a campaign's map or
        // match, the tutorial, the end of a run) loading would throw away what's in progress. At the main menu a campaign
        // run in memory is only the copy the title screen read from disk (TitleScreen.RefreshCampaignData), so it's safe.
        string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
        bool inExhibition = sceneName == MatchScene && (int)teams!.gameType == GameTypeQuickGame && !teams.onboard;
        if (sceneName != MenuScene && !inExhibition)
        {
            problem = sceneName == MatchScene
                ? $"this game is in a {(teams!.onboard ? "tutorial" : "campaign")} match; go to the main menu and the host's next match loads"
                : $"this game isn't at its main menu ({sceneName}); go to the main menu and the host's next match loads";
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

        // From here the game's match settings change: if the load doesn't go ahead, they're put back as they were.
        SetupSnapshot before = SetupSnapshot.Take(teams!, data!);
        try
        {
            if (!Apply(values, teams!, data!, difficulties!, teamRepository!, forwardRepository!, goalieRepository!, out problem))
            {
                before.Restore(problem);
                return false;
            }

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

    /// <summary>Arms the forced load (<see cref="RandomTeamsPrefix"/>, <see cref="RefereePrefix"/>), holds this PC's saves and loads the match scene.</summary>
    private static void StartLoad(Values values, LoadingScreenManager loading)
    {
        _forced = new Forced(values, values.Text("referee"));
        bool held = SaveGuard.HoldForMatch(Describe(values));
        try
        {
            loading.LoadScene(MatchScene, false);
        }
        catch
        {
            if (!held)
            {
                SaveGuard.ReleaseMatch("the load didn't start");
            }

            throw;
        }
    }

    /// <summary>Changes the game's match settings to the host's. False, with why, if a team, skater or goalie isn't here.</summary>
    private static bool Apply(Values values, TeamSelection teams, InGameData data, DifficultySettings difficulties, ITeamRepository teamRepository,
        IForwardRepository forwardRepository, IGoalieRepository goalieRepository, out string problem)
    {
        foreach ((string side, bool isHome) in new[] { (Home, true), (Visitor, false) })
        {
            if (!SetTeam(values, side, isHome, teams, teamRepository, forwardRepository, goalieRepository, out problem))
            {
                return false;
            }
        }

        ApplyRules(values, teams, data, difficulties, GameTypeQuickGame);
        problem = string.Empty;
        return true;
    }

    /// <summary>Everything in the team selection and the match rules that isn't a team: colours, mode, difficulty, players, timer and end conditions.</summary>
    private static void ApplyRules(Values values, TeamSelection teams, InGameData data, DifficultySettings difficulties, int gameType)
    {
        teams.isHomeColorOverride = values.Bool("colors.homeOverride");
        teams.isHomeColorAlternate = values.Bool("colors.homeAlternate");
        teams.isVisitorColorOverride = values.Bool("colors.visitorOverride");
        teams.isVisitorColorAlternate = values.Bool("colors.visitorAlternate");
        teams.onboard = false;
        teams.skaterHome = values.Int("mode.skaterHome");
        teams.skaterVisitor = values.Int("mode.skaterVisitor");
        teams.gameType = (GameType)gameType;
        teams.gameMode = (GameMode)GameModeNormal;
        int difficulty = values.Int("mode.difficulty");
        DifficultyLevel? level = new[] { difficulties.easy, difficulties.medium, difficulties.hard, difficulties.nightmare }.FirstOrDefault(d => d != null && d.index == difficulty);
        if (level != null)
        {
            teams.difficultyLevel = level;
        }
        else
        {
            ModLog.Warn($"Match setup: no difficulty with index {difficulty} here; keeping this PC's.");
        }

        teams.powerupFree = values.Bool("mode.powerupFree");
        teams.isManualGoalie = values.Bool("mode.manualGoalie");
        teams.areInjuriesEnabled = values.Bool("mode.injuries");
        // Nobody plays on this PC: the host's players play through their pads on the host (the game's fallback player for a
        // campaign match with none is skipped here: MatchSetup.FallbackPlayer.cs).
        teams.homeTeamPlayers?.Clear();
        teams.visitorTeamPlayers?.Clear();

        TimerRuleStateData timer = data.timerRule;
        timer.numOfPeriod = values.Int("rules.periods");
        timer.periodLength = values.Float("rules.periodLength");
        timer.hasOvertime = values.Bool("rules.overtime");
        timer.isNormalTime = values.Bool("rules.normalTime");
        Il2CppSystem.Collections.Generic.List<EndOfGameConditionType>? conditions = data.endOfGameConditionData?.endOfGameConditions;
        if (conditions != null)
        {
            conditions.Clear();
            foreach (string part in values.Text("rules.endConditions").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int condition))
                {
                    conditions.Add((EndOfGameConditionType)condition);
                }
            }
        }

        data.tieBreaker = (TieBreakerType)values.Int("rules.tieBreaker");
        data.isTournamentMatch = values.Bool("rules.tournament");
    }

    /// <summary>
    /// The rules' numbers against the game's ranges, before anything changes: a period count, period length or skater
    /// count the game can't handle would break or never end the match. Null when they're fine, else what's wrong (short:
    /// the host sees it).
    /// </summary>
    private static string? CheckRules(Values values, float maxMinutes = MaxPeriodMinutes)
    {
        int periods = values.Int("rules.periods");
        if (periods is < 1 or > MaxPeriods)
        {
            return $"rules.periods is {periods} (1 to {MaxPeriods})";
        }

        float minutes = values.Float("rules.periodLength");
        if (!float.IsFinite(minutes) || minutes <= 0 || minutes > maxMinutes)
        {
            return $"rules.periodLength is {minutes.ToString("0.##", CultureInfo.InvariantCulture)} (up to {maxMinutes:0} minutes)";
        }

        foreach (string key in new[] { "mode.skaterHome", "mode.skaterVisitor" })
        {
            int skaters = values.Int(key);
            if (skaters is < 1 or > Slots)
            {
                return $"{key} is {skaters} (1 to {Slots})";
            }
        }

        int tieBreaker = values.Int("rules.tieBreaker");
        if (!Enum.IsDefined((TieBreakerType)tieBreaker))
        {
            return $"rules.tieBreaker is {tieBreaker}";
        }

        string[] conditions = values.Text("rules.endConditions").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (conditions.Length > MaxEndConditions)
        {
            return $"rules.endConditions has {conditions.Length} entries";
        }

        foreach (string part in conditions)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int condition) || !Enum.IsDefined((EndOfGameConditionType)condition))
            {
                return $"rules.endConditions has {(!HostText.IsKey(part) ? HostText.KeyForLog(part) : part.Length > 12 ? part[..12] + "..." : part)}";
            }
        }

        return null;
    }

    /// <summary>
    /// One side's counts and ids, before anything changes: a roster count outside the game's range would make this PC
    /// loop and allocate for it, an empty id would put a null into the game's lists, and an active line below 1 makes the
    /// game's <c>TeamData.CurrentLine</c> throw. Null when they're fine, else what's wrong (short: the host sees it).
    /// </summary>
    private static string? CheckSide(Values values, string side)
    {
        if (values.Bool($"{side}.random"))
        {
            for (int i = 0; i < Slots; i++)
            {
                if (values.Text($"{side}.slot.{i}").Length == 0)
                {
                    return $"{side}.slot.{i} is empty";
                }
            }

            return null;
        }

        if (values.Text($"{side}.team").Length == 0)
        {
            return $"{side}.team is empty";
        }

        int forwards = values.Int($"{side}.forwards");
        if (forwards is < Slots or > MaxRoster)
        {
            return $"{side}.forwards is {forwards} ({Slots} to {MaxRoster})";
        }

        for (int i = 0; i < forwards; i++)
        {
            if (values.Text($"{side}.forward.{i}").Length == 0)
            {
                return $"{side}.forward.{i} is empty";
            }
        }

        int lines = values.Int($"{side}.lines");
        if (lines is < 0 or > MaxLines)
        {
            return $"{side}.lines is {lines} (0 to {MaxLines})";
        }

        // The game keeps it in 1-2 (SetActiveLine) and reads line (active - 1), clamped to the lines there are but not below
        // the first; a team without lines ignores it.
        int active = values.Int($"{side}.activeLine");
        int lowest = lines > 0 ? 1 : 0;
        if (active < lowest || active > MaxLines)
        {
            return $"{side}.activeLine is {active} ({lowest} to {MaxLines})";
        }

        return null;
    }

    /// <summary>
    /// The host's active line for <paramref name="side"/>, whose team here has <paramref name="lines"/> lines (checked as the
    /// host's): held to the last line when it's beyond them, with why in <paramref name="held"/>. <c>TeamData.CurrentLine</c>
    /// clamps it the same way, so the playing places don't change, but not every reader of the field does.
    /// </summary>
    private static int ActiveLine(Values values, string side, int lines, out string? held)
    {
        int active = values.Int($"{side}.activeLine");
        held = lines > 0 && active > lines ? $"{side} active line {active} held to {lines}, its last line" : null;
        return held != null ? lines : active;
    }

    /// <summary>One side: the team (a copy of its asset, as Play Now makes), its roster, lines, active line and goalie as the host has them.</summary>
    private static bool SetTeam(Values values, string side, bool isHome, TeamSelection teams, ITeamRepository teamRepository, IForwardRepository forwardRepository,
        IGoalieRepository goalieRepository, out string problem)
    {
        string teamId = values.Text($"{side}.team");
        bool random = values.Bool($"{side}.random");
        string customProblem = string.Empty;
        TeamData? asset = random ? FirstBuiltInTeam(teamRepository) : CustomContent.Team(teamId, out customProblem) ?? FindTeam(teamRepository, teamId);
        if (asset == null)
        {
            problem = random ? "no built-in team found to stand in for a random team"
                : customProblem.Length > 0 ? customProblem : $"the {side} team ({HostId(teamId)}) isn't on this PC (a custom team the host didn't send?)";
            return false;
        }

        teams.SetQuickMatchTeam(asset, isHome);
        if (random)
        {
            // Built when the match loads, from the game's random pools (RandomTeamsPrefix).
            problem = string.Empty;
            return true;
        }

        TeamData team = isHome ? teams.homeTeam : teams.visitorTeam;
        var byOriginal = new Dictionary<string, ForwardData>(StringComparer.Ordinal);
        Il2CppSystem.Collections.Generic.List<ForwardData> current = team.GetForwards(true);
        for (int i = 0; i < current.Count; i++)
        {
            ForwardData? forward = current[i];
            if (forward != null && forward.OriginalId is string original && original.Length > 0)
            {
                byOriginal.TryAdd(original, forward);
            }
        }

        // The roster in the host's order (its count and ids were checked in CheckSide); a forward this team copy doesn't
        // have comes from the skater list, as a copy with a fresh id (as the game's own team copy makes them).
        int count = values.Int($"{side}.forwards");
        var roster = new List<ForwardData>();
        for (int i = 0; i < count; i++)
        {
            string id = values.Text($"{side}.forward.{i}");
            if (!byOriginal.TryGetValue(id, out ForwardData? forward))
            {
                // The host's own custom skater (a copy of what it sent), else the game's.
                forward = CustomContent.NewForward(id) ?? forwardRepository.GetForwardData(id, true);
                if (forward == null)
                {
                    problem = $"the {side} team's skater {HostId(id)} isn't on this PC (a custom skater the host didn't send?)";
                    return false;
                }

                forward.GenerateID();
                forward.OriginalId = id;
                byOriginal[id] = forward;
            }

            roster.Add(forward);
        }

        Il2CppSystem.Collections.Generic.List<ForwardData> forwards = team.forwards;
        forwards.Clear();
        foreach (ForwardData forward in roster)
        {
            forwards.Add(forward);
        }

        int lines = values.Int($"{side}.lines");
        Il2CppSystem.Collections.Generic.List<Lineup>? teamLines = team.lines;
        if ((teamLines?.Count ?? 0) != lines)
        {
            problem = $"the {side} team has {teamLines?.Count ?? 0} lines here and {lines} on the host";
            return false;
        }

        for (int k = 0; k < lines; k++)
        {
            Lineup line = teamLines![k];
            foreach (SkaterType slot in LineSlots)
            {
                string id = values.Text($"{side}.line.{k}.{(int)slot}");
                line.SetSlotId(slot, id.Length > 0 && byOriginal.TryGetValue(id, out ForwardData? forward) ? forward.id : string.Empty);
            }
        }

        team.activeLineNumber = ActiveLine(values, side, lines, out string? held);
        if (held != null)
        {
            ModLog.Warn($"Match setup: the host's {held}.");
        }

        string goalieId = values.Text($"{side}.goalie");
        if (goalieId.Length > 0 && (team.goalie == null || team.goalie.OriginalId != goalieId))
        {
            GoaltenderData? goalie = CustomContent.GoalieFor(goalieId) ?? FindGoalie(goalieRepository, goalieId);
            if (goalie == null)
            {
                problem = $"the {side} team's goalie {HostId(goalieId)} isn't on this PC (a custom goalie the host didn't send?)";
                return false;
            }

            team.goalie = goalie;
        }

        // The five playing slots must come out exactly as the host's.
        var wrong = new List<string>();
        for (int i = 0; i < Slots; i++)
        {
            ForwardData? forward = team.GetForward(i);
            string want = values.Text($"{side}.slot.{i}");
            string have = forward != null ? forward.OriginalId ?? string.Empty : string.Empty;
            if (want != have)
            {
                wrong.Add($"slot {i}: {HostId(have)} instead of {HostId(want)}");
            }
        }

        problem = wrong.Count == 0 ? string.Empty : $"the {side} lineup didn't come out as the host's ({string.Join("; ", wrong)})";
        return wrong.Count == 0;
    }

    /// <summary>The game's team <paramref name="id"/>, campaign-exclusive ones too, or null.</summary>
    internal static TeamData? FindTeam(ITeamRepository repository, string id)
    {
        TeamData? team = repository.GetTeam(id);
        if (team != null)
        {
            return team;
        }

        // Campaign-exclusive teams aren't in GetTeam.
        Il2CppSystem.Collections.Generic.List<TeamData> builtIn = repository.GetBuiltInTeams(true, true, true);
        for (int i = 0; i < builtIn.Count; i++)
        {
            if (builtIn[i] != null && builtIn[i].id == id)
            {
                return builtIn[i];
            }
        }

        return null;
    }

    private static TeamData? FirstBuiltInTeam(ITeamRepository repository)
    {
        Il2CppSystem.Collections.Generic.List<TeamData> builtIn = repository.GetBuiltInTeams(false, false, true);
        return builtIn.Count > 0 ? builtIn[0] : null;
    }

    private static GoaltenderData? FindGoalie(IGoalieRepository repository, string id)
    {
        Il2CppSystem.Collections.Generic.List<GoaltenderData> goalies = repository.LoadAllGoalies();
        for (int i = 0; i < goalies.Count; i++)
        {
            GoaltenderData? goalie = goalies[i];
            if (goalie != null && (goalie.OriginalId == id || goalie.id == id))
            {
                return goalie;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ the game's hooks

    /// <summary>
    /// While a forced load is armed, random sides are built from the host's picks (the game's roll is unseedable)
    /// instead of rolled; real teams were already set. Nothing throws into the game: on a failure the game rolls.
    /// </summary>
    private static bool RandomTeamsPrefix(GameLoader __instance)
    {
        Forced? forced = _forced;
        if (forced == null)
        {
            return true;
        }

        try
        {
            TeamSelection teams = __instance.teamSelectionState;
            RandomTeamData pools = __instance.randomTeamData;
            if (forced.Values.Bool($"{Home}.random"))
            {
                teams.homeTeam = BuildRandom(pools, pools.homeClone, forced.Values, Home);
            }

            if (forced.Values.Bool($"{Visitor}.random"))
            {
                teams.visitorTeam = BuildRandom(pools, pools.visitorClone, forced.Values, Visitor);
            }

            return false;
        }
        catch (Exception ex)
        {
            ModLog.Error("Match setup: building the host's random team failed; the game rolls its own (the match will differ)", ex);
            return true;
        }
    }

    /// <summary>
    /// The match's setup is final here, before anything is spawned: read it for friends while hosting (it reads this PC's
    /// custom teams, skaters, goalies and logos from disk, so not otherwise: a session started later reads the match in play
    /// afresh, <see cref="TryCapture(out List{KeyValuePair{string, NetValue}}, out List{CustomItem}, out string)"/>).
    /// </summary>
    private static void RandomTeamsPostfix(GameLoader __instance)
    {
        LoadedGameType = -1;
        try
        {
            RememberAtLoad();
            LoadedGameType = __instance.teamSelectionState is { } selection ? (int)selection.gameType : -1;
            if (!ReadForFriends)
            {
                LastCapture = null;
                LastCaptureContent = Array.Empty<CustomItem>();
                LastCaptureProblem = "this PC isn't hosting";
                Captures++;
                return;
            }

            var setup = new List<KeyValuePair<string, NetValue>>();
            var content = new List<CustomItem>();
            if (Capture(__instance.teamSelectionState, __instance.inGameState, setup, content, out string problem))
            {
                LastCapture = setup;
                LastCaptureContent = content;
                LastCaptureProblem = string.Empty;
            }
            else
            {
                LastCapture = null;
                LastCaptureContent = Array.Empty<CustomItem>();
                LastCaptureProblem = problem;
            }

            Captures++;
        }
        catch (Exception ex)
        {
            LastCapture = null;
            LastCaptureContent = Array.Empty<CustomItem>();
            LastCaptureProblem = $"reading it failed ({ModLog.SafeErrorText(ex)})";
            Captures++;
            ModLog.Error("Match setup: reading the loading match's setup failed", ex);
        }
    }

    /// <summary>While a forced load is armed, the host's referee (the game's choice is a function of the teams; this is insurance).</summary>
    private static bool RefereePrefix(TeamSelection __instance, ref ForwardData __result)
    {
        string? id = _forced?.RefereeId;
        if (string.IsNullOrEmpty(id))
        {
            return true;
        }

        try
        {
            foreach (ForwardData? referee in new[] { __instance.gedeonReferee, __instance.oldReferee, __instance.boringReferee })
            {
                if (referee != null && (referee.OriginalId == id || referee.id == id))
                {
                    __result = referee;
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Match setup: couldn't force the referee ({ModLog.SafeErrorText(ex)}); the game picks.");
        }

        return true;
    }

    private static void OnAllSkatersInitialized()
    {
        try
        {
            _serial++;
            _matchScene = SceneManager.GetActiveScene();
            _initializedAt = FrameClock.Seconds;
            bool forced = _forced != null;
            _forced = null;
            _forcedSerial = forced ? _serial : 0;
            ModLog.Info($"Match setup: match {_serial} loaded (scene \"{_matchScene.name}\" {_matchScene.handle}){(forced ? ", the one we were asked to load" : string.Empty)}.");
            SaveGuard.MatchLoaded(_matchScene);
        }
        catch (Exception ex)
        {
            ModLog.Error("Match setup: handling a loaded match failed", ex);
        }
    }

    private static TeamData BuildRandom(RandomTeamData pools, TeamData placeholder, Values values, string side)
    {
        TeamData team = UnityEngine.Object.Instantiate(placeholder).Cast<TeamData>();
        for (int i = 0; i < Slots; i++)
        {
            string id = values.Text($"{side}.slot.{i}");
            ForwardData source = FindIn(pools.elites, id) ?? FindIn(pools.vanilla, id)
                ?? throw new InvalidOperationException($"the random pools have no skater {HostId(id)}");
            ForwardData forward = UnityEngine.Object.Instantiate(source).Cast<ForwardData>();
            forward.bodySkin = values.Text($"{side}.skin.{i}");
            forward.bodyAwaySkin = values.Text($"{side}.awaySkin.{i}");
            if (i == 4)
            {
                team.rightDefensemen = forward;
            }
            else
            {
                team.SetForward(i, forward);
            }
        }

        return team;
    }

    private static ForwardData? FindIn(Il2CppSystem.Collections.Generic.List<ForwardData>? pool, string id)
    {
        for (int i = 0; pool != null && i < pool.Count; i++)
        {
            ForwardData? forward = pool[i];
            if (forward != null && (forward.OriginalId == id || forward.id == id))
            {
                return forward;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ the game's services

    /// <summary>A service the game binds in its project injector, by type (as the developers' soak runner gets them).</summary>
    private static T? FromProject<T>()
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<T>());
        return found?.TryCast<T>();
    }

    /// <summary>
    /// A settings asset (one ScriptableObject of its type): from the injector, else the loaded asset itself (that the
    /// injector keys it by its type is inferred, not proven, for the team selection).
    /// </summary>
    private static T? Settings<T>()
        where T : UnityEngine.Object
    {
        T? found = FromProject<T>();
        if (found != null)
        {
            return found;
        }

        foreach (UnityEngine.Object asset in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            T? typed = asset?.TryCast<T>();
            if (typed != null)
            {
                ModLog.Info($"Match setup: {typeof(T).Name} isn't in the game's injector; using the loaded asset.");
                return typed;
            }
        }

        return null;
    }

    /// <summary>An interface-typed service: the injector holds the implementation, wrapped here as the interface.</summary>
    private static T? Repository<T>(Func<IntPtr, T> wrap)
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<T>());
        return found == null ? null : wrap(found.Pointer);
    }

    /// <summary>A forced load: the host's setup, until the match has loaded.</summary>
    private sealed record Forced(Values Values, string RefereeId);

    /// <summary>
    /// The game's match settings as a load found them (everything <see cref="Apply"/> changes in the team selection and
    /// the match rules), so a load that doesn't go ahead puts them back. The team copies a failed load made are left to
    /// the game, which destroys copies that are no longer current at the next scene change.
    /// </summary>
    private sealed class SetupSnapshot
    {
        private readonly List<(string What, Action Undo)> _undo = new();

        public static SetupSnapshot Take(TeamSelection teams, InGameData data)
        {
            var snapshot = new SetupSnapshot();
            TeamData home = teams.homeTeam;
            TeamData visitor = teams.visitorTeam;
            snapshot._undo.Add(("teams", () =>
            {
                teams.homeTeam = home;
                teams.visitorTeam = visitor;
            }));
            bool homeOverride = teams.isHomeColorOverride;
            bool homeAlternate = teams.isHomeColorAlternate;
            bool visitorOverride = teams.isVisitorColorOverride;
            bool visitorAlternate = teams.isVisitorColorAlternate;
            snapshot._undo.Add(("colours", () =>
            {
                teams.isHomeColorOverride = homeOverride;
                teams.isHomeColorAlternate = homeAlternate;
                teams.isVisitorColorOverride = visitorOverride;
                teams.isVisitorColorAlternate = visitorAlternate;
            }));
            bool onboard = teams.onboard;
            int skaterHome = teams.skaterHome;
            int skaterVisitor = teams.skaterVisitor;
            GameType gameType = teams.gameType;
            GameMode gameMode = teams.gameMode;
            DifficultyLevel difficulty = teams.difficultyLevel;
            bool powerupFree = teams.powerupFree;
            bool manualGoalie = teams.isManualGoalie;
            bool injuries = teams.areInjuriesEnabled;
            snapshot._undo.Add(("mode", () =>
            {
                teams.onboard = onboard;
                teams.skaterHome = skaterHome;
                teams.skaterVisitor = skaterVisitor;
                teams.gameType = gameType;
                teams.gameMode = gameMode;
                teams.difficultyLevel = difficulty;
                teams.powerupFree = powerupFree;
                teams.isManualGoalie = manualGoalie;
                teams.areInjuriesEnabled = injuries;
            }));
            snapshot.AddList("home players", teams.homeTeamPlayers);
            snapshot.AddList("visitor players", teams.visitorTeamPlayers);
            TimerRuleStateData timer = data.timerRule;
            int periods = timer.numOfPeriod;
            float periodLength = timer.periodLength;
            bool overtime = timer.hasOvertime;
            bool normalTime = timer.isNormalTime;
            snapshot._undo.Add(("timer", () =>
            {
                timer.numOfPeriod = periods;
                timer.periodLength = periodLength;
                timer.hasOvertime = overtime;
                timer.isNormalTime = normalTime;
            }));
            snapshot.AddList("end conditions", data.endOfGameConditionData?.endOfGameConditions);
            TieBreakerType tieBreaker = data.tieBreaker;
            bool tournament = data.isTournamentMatch;
            snapshot._undo.Add(("rules", () =>
            {
                data.tieBreaker = tieBreaker;
                data.isTournamentMatch = tournament;
            }));
            return snapshot;
        }

        /// <summary>One more thing to put back (a campaign match's node and rules).</summary>
        public void Add(string what, Action undo) => _undo.Add((what, undo));

        /// <summary>Puts everything back (each part on its own, so one failure doesn't skip the rest) and logs it.</summary>
        public void Restore(string why)
        {
            var failed = new List<string>();
            foreach ((string what, Action undo) in _undo)
            {
                try
                {
                    undo();
                }
                catch (Exception ex)
                {
                    failed.Add($"{what} ({ex.GetType().Name})");
                }
            }

            if (failed.Count == 0)
            {
                ModLog.Info($"Match setup: the load didn't go ahead ({why}); this game's match settings are back as they were.");
            }
            else
            {
                ModLog.Warn($"Match setup: the load didn't go ahead ({why}); putting this game's match settings back failed for: {string.Join(", ", failed)}.");
            }
        }

        private void AddList<T>(string what, Il2CppSystem.Collections.Generic.List<T>? list)
        {
            if (list == null)
            {
                return;
            }

            var items = new List<T>();
            for (int i = 0; i < list.Count; i++)
            {
                items.Add(list[i]);
            }

            _undo.Add((what, () =>
            {
                list.Clear();
                foreach (T item in items)
                {
                    list.Add(item);
                }
            }));
        }
    }

    /// <summary>A setup's values by key, with defaults for anything missing.</summary>
    private sealed class Values
    {
        private readonly Dictionary<string, NetValue> _values = new(StringComparer.Ordinal);

        public Values(IReadOnlyList<KeyValuePair<string, NetValue>> setup)
        {
            Setup = setup;
            foreach ((string key, NetValue value) in setup)
            {
                _values[key] = value;
            }
        }

        /// <summary>The setup's values as they came.</summary>
        public IReadOnlyList<KeyValuePair<string, NetValue>> Setup { get; }

        public string Text(string key) => _values.TryGetValue(key, out NetValue value) && value.Kind == NetValueKind.Text ? value.Text ?? string.Empty : string.Empty;

        public int Int(string key) => _values.TryGetValue(key, out NetValue value) && value.Kind == NetValueKind.Whole ? (int)value.WholeValue : 0;

        public bool Bool(string key) => _values.TryGetValue(key, out NetValue value) && value.Kind == NetValueKind.Flag && value.WholeValue != 0;

        public float Float(string key) => _values.TryGetValue(key, out NetValue value) && value.Kind == NetValueKind.Single32 ? (float)value.Number : 0f;
    }
}

/// <summary>Quits a match through the game mode controller, as the end-of-match "quit" does.</summary>
internal static class IGameModeControllerWrapper
{
    /// <summary>Calls <c>Quit</c> on the loader's game mode controller (raises OnGameExit, loads the start screen).</summary>
    public static void Quit(GameLoader? loader)
    {
        if (loader == null)
        {
            ModLog.Warn("Match setup: no match to quit.");
            return;
        }

        Il2CppState.GameModeControllers.IGameModeController? controller = loader._gameModeController;
        if (controller == null)
        {
            ModLog.Warn("Match setup: the match has no game mode controller to quit through.");
            return;
        }

        controller.Quit();
    }
}
#endif
