#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppSTS.Map;
using Il2CppState.EndOfGame;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// Campaign matches stream like exhibitions (D40).
/// <list type="bullet">
/// <item>Host: a campaign match's setup carries the campaign's part (<see cref="MatchSetup"/>), and the match relay sends
/// it as for any match. When one starts loading, the host's view moves to <see cref="Screens.Match"/>. A campaign match
/// friends can't load (the campaign isn't shared, D46, or its run couldn't be sent to them) is neither sent nor published
/// (<see cref="MatchSetup.CampaignNotShared"/>).</item>
/// <item>Friend: the match relay loads the host's match from its setup (after the host's run, D41) and plays it in client
/// mode. The "match" screen follower only waits for that; it never loads anything itself.</item>
/// <item>Friend, while following the host's campaign: the game's own campaign steps on a match's way are blocked as a
/// backstop. The three <c>LaunchMatch</c> (a friend's game never launches a
/// campaign match), a campaign match's end-of-match pipeline (<c>EndOfGameFlowController.OnEndOfGame</c>: rewards,
/// saving and the way back to the map are the host's; its screens are mirrored as views) and the Canada curse picks
/// (they roll; the friend's game gets an empty enumerator, the pick marked done and its load cover removed).</item>
/// </list>
/// The patches go in at start-up and stay (D38): outside a followed campaign (and a host's match still loaded here after
/// its session) they let every call through.
/// </summary>
internal sealed class CampaignMatch : ICampaignModule, IScreenFollowerSource
{
    // A detail of the "match" view of the module's own: each match's view differs from the last, even at the same node.
    private const string MatchDetail = "match";

    // How long the "match" screen waits for the relay to load the host's match (the setup is sent with the view).
    private const double GoTimeoutSeconds = 30;

    private readonly MatchRelay _matches;
    private readonly IScreenFollower[] _followers;
    private CampaignRelay? _relay;
    private int _seenCaptures;
    private bool _publishCurrent;

    /// <summary>Made once by <see cref="CampaignModules"/>.</summary>
    public CampaignMatch(CampaignContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _matches = context.Matches;
        _followers = new IScreenFollower[] { new MatchFollower(this) };
    }

    /// <inheritdoc />
    public string Name => "Campaign matches";

    /// <inheritdoc />
    public IEnumerable<IScreenFollower> Followers => _followers;

    /// <summary>True while this PC follows a host's campaign (the friend-side blocks act only then).</summary>
    private static bool Following => CampaignRelay.Current is { Following: true };

    /// <summary>
    /// True while this PC follows a host's campaign, or plays a match a host's setup loaded here (it stays the host's until
    /// its scene unloads, also after the session ended): the blocks on a match's own campaign steps act then.
    /// </summary>
    private static bool HostsMatchHere => Following || MatchSetup.HostMatchHere;

    /// <summary>This PC started hosting: the next campaign match to load (or the one in play) is published as a view.</summary>
    public void StartHosting(CampaignRelay relay)
    {
        _relay = relay;
        _seenCaptures = MatchSetup.Captures;
        _publishCurrent = true;
    }

    /// <summary>This PC stopped hosting.</summary>
    public void StopHosting(string reason) => _relay = null;

    /// <summary>This PC joined a host as a friend.</summary>
    public void StartFollowing(CampaignRelay relay) => _relay = relay;

    /// <summary>This PC left the host (a campaign load waiting for the host's run notices and stops).</summary>
    public void StopFollowing(string reason, bool gameClosing) => _relay = null;

    /// <summary>Every frame: the host publishes the "match" view when a campaign match starts loading.</summary>
    public void Update()
    {
        if (_relay is { Hosting: true } relay)
        {
            PublishMatchView(relay);
        }
    }

    /// <summary>Friend: a line while a campaign match waits for the host's run.</summary>
    public IEnumerable<string> Status() =>
        _relay is { Following: true } relay && MatchSetup.WaitingForRun ? new[] { $"getting {relay.HostName}'s run for the match..." } : Array.Empty<string>();

    /// <summary>
    /// Host: a campaign match started loading (the setup was read for friends): friends follow the host to the "match"
    /// screen with the run it needs. Also once after hosting starts, for a campaign match already in play.
    /// </summary>
    private void PublishMatchView(CampaignRelay relay)
    {
        List<KeyValuePair<string, NetValue>>? setup = null;
        if (MatchSetup.Captures != _seenCaptures)
        {
            _seenCaptures = MatchSetup.Captures;
            _publishCurrent = false;
            setup = MatchSetup.LastCapture;
        }
        else if (_publishCurrent)
        {
            _publishCurrent = false;
            if (!MatchSetup.CurrentMatch(out _).IsNone && MatchSetup.TryCapture(out List<KeyValuePair<string, NetValue>> current, out _))
            {
                setup = current;
            }
        }

        if (setup == null || !CampaignMatchValues.IsCampaign(setup) || MatchSetup.CampaignNotShared(setup) != null)
        {
            // Not a campaign match, or friends can't load it: the host's campaign isn't shared (a solo run: friends stay on
            // the title view, D46), or its run couldn't be sent to them. The match relay doesn't send it either (its log
            // says why).
            return;
        }

        var details = new List<KeyValuePair<string, NetValue>>
        {
            new(Details.Node, MatchSetupValues.Get(setup, MatchSetup.CampaignNodeKey)),
            new(Details.Act, MatchSetupValues.Get(setup, MatchSetup.CampaignActKey)),
            new(Details.Run, MatchSetupValues.Get(setup, MatchSetup.CampaignRunIdKey)),
            new(MatchDetail, NetValue.FromInt(MatchSetup.Captures)),
        };
        relay.PublishView(Screens.Match, details, needsRun: true);

        // What friends will load, to compare with their logs: the campaign's part of the setup.
        string Value(string key) => MatchSetupValues.Get(setup, key).ToString();
        string Lineup(string side) => string.Join(" ", Enumerable.Range(0, 5).Select(i => MatchSetupValues.Get(setup, $"{side}.slot.{i}").Text is { Length: > 0 } id ? id : "-"));
        ModLog.Info(SafeText.Clean($"Campaign matches: the host's campaign match is loading: node {Value(MatchSetup.CampaignNodeKey)} (type {Value("campaign.nodeType")}, " +
            $"blueprint {Value("campaign.blueprint")}), act {Value(MatchSetup.CampaignActKey)}, floor {Value("campaign.floor")}, run checkpoint {Value(CampaignMatchValues.RunCheckpointKey)}; " +
            $"home {Lineup("home")}; visitor {Value("visitor.team")}: {Lineup("visitor")}, goalie {Value("visitor.goalie")}, {Value("visitor.relics")} relic(s); " +
            $"goal target {Value("rules.goalToScore")}, objective {Value("rules.objective")}, restrictions [{Value("rules.restrictions")}], " +
            $"{Value("rules.periods")} period(s) of {Value("rules.periodLength")} min, end conditions [{Value("rules.endConditions")}].", 1200));
    }

    /// <summary>
    /// Friend: true while the match relay loads or plays the host's campaign match at <paramref name="view"/>'s node (the
    /// relay loads it from the setup; the view only says the host is there).
    /// </summary>
    private bool LoadingOrPlaying(HostView view)
    {
        IReadOnlyList<KeyValuePair<string, NetValue>>? setup = _matches.FromHost?.Setup;
        if (!_matches.FriendBusy || setup == null || !CampaignMatchValues.IsCampaign(setup))
        {
            return false;
        }

        string node = view.Text(Details.Node);
        return node.Length == 0 || MatchSetupValues.Get(setup, MatchSetup.CampaignNodeKey).Text == node;
    }

    // ---------------------------------------------------------------- friend-side blocks

    /// <summary>Installs the friend-side blocks. Adds a line to <paramref name="failed"/> for each that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(failed);
        int before = failed.Count;
        Patch(harmony, failed, typeof(EliteMapNode), nameof(EliteMapNode.LaunchMatch), nameof(LaunchMatchPrefix));
        Patch(harmony, failed, typeof(BossMapNode), nameof(BossMapNode.LaunchMatch), nameof(LaunchMatchPrefix));
        Patch(harmony, failed, typeof(ChallengeMapNode), nameof(ChallengeMapNode.LaunchMatch), nameof(LaunchMatchPrefix));
        Patch(harmony, failed, typeof(EndOfGameFlowController), nameof(EndOfGameFlowController.OnEndOfGame), nameof(EndOfGamePrefix));
        Patch(harmony, failed, typeof(CanadaCursePicker), nameof(CanadaCursePicker.Run), nameof(CanadaCursePrefix));
        Patch(harmony, failed, typeof(CanadaCursePicker), nameof(CanadaCursePicker.RunSecondPick), nameof(CanadaCursePrefix));
        Patch(harmony, failed, typeof(CanadaCursePicker), nameof(CanadaCursePicker.RunPick), nameof(CanadaCursePrefix));
        if (failed.Count == before)
        {
            ModLog.Info("Campaign matches: on (a friend following the host's campaign never launches a match, runs its end-of-match pipeline or rolls the Canada curse; " +
                "campaign matches are loaded from the host's setup).");
        }
    }

    /// <summary>One prefix on <paramref name="type"/>'s own <paramref name="method"/>; a failure goes in <paramref name="failed"/>.</summary>
    private static void Patch(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, string prefix)
    {
        try
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.Name, method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CampaignMatch), prefix));
        }
        catch (Exception ex)
        {
            failed.Add($"campaign matches: couldn't patch {type.Name}.{method} ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// A friend's game never launches a campaign match: it loads the host's from its setup. Launching would roll the
    /// opponent's substitutes and a Challenge's stats, and load a match of this PC's own. Should never be reached (the map
    /// and preview on a friend's PC don't take commands): logged loudly if it is.
    /// </summary>
    private static bool LaunchMatchPrefix(MapNode __instance)
    {
        if (!Following)
        {
            return true;
        }

        try
        {
            Point? point = __instance?.Node?.point;
            ModLog.Warn($"Campaign matches: BLOCKED {__instance?.GetIl2CppType().Name ?? "a map node"}.LaunchMatch at node " +
                $"{(point != null ? CampaignMatchValues.FormatPoint(point.x, point.y) : "?")} on this PC. A friend's game never launches a campaign match " +
                "(it loads the host's from its setup); something here tried to. Please send this log.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Campaign matches: BLOCKED a LaunchMatch on this PC (couldn't name it: {ex.GetType().Name}). Please send this log.");
        }

        return false;
    }

    /// <summary>
    /// A campaign match's end-of-match pipeline (stats, rewards, saving the run, the way back to the map or the run's end)
    /// runs on the host only; a friend's game follows the host's screens. An exhibition's is left alone. Also held for a
    /// host's match still loaded after the session ended: the match relay is leaving it, and its own logic runs again
    /// meanwhile (client mode is off).
    /// </summary>
    private static bool EndOfGamePrefix(EndOfGameFlowController __instance)
    {
        if (!HostsMatchHere)
        {
            return true;
        }

        try
        {
            if (__instance == null || __instance._teamSelection == null || (int)__instance._teamSelection.gameType != (int)CampaignMatchValues.CampaignGameType)
            {
                return true;
            }

            ModLog.Info("Campaign matches: the match ended here; its end-of-match pipeline (stats, rewards, saving, the way back to the map) is the host's, " +
                "and this PC follows the host's screens.");
            return false;
        }
        catch (Exception ex)
        {
            // Not knowing whether it's a campaign match, hold it: a friend's game running the campaign's pipeline is worse.
            ModLog.Warn($"Campaign matches: couldn't tell which match ended ({ex.GetType().Name}); its end-of-match pipeline is held on this PC.");
            return false;
        }
    }

    /// <summary>
    /// The Canada curse picks (a Team Canada match's, before it and at the second period) roll the curses offered and add
    /// one to the team: the host's game does that, and the result shows in its stream. A friend's game gets an empty
    /// enumerator, which the caller awaits like the real one, so nothing is offered, rolled or added here (the anthem's
    /// <c>Run</c> on a friend following the host's intro gets a wait for the host's pick instead, MatchIntro). What the game's
    /// own pick leaves behind is done as it would be: the pick is marked done for this match (<c>_doneForMatch</c>,
    /// <c>_secondPickDoneForMatch</c>; the period presentation skips itself while a pick is pending), and the cover the
    /// anthem put over the match for the pick (<c>CoverLoad</c>, which only the pick removes) comes off, else it would black
    /// out the friend's whole match. <c>Run</c> is the anthem's call (this build's intro always takes its skip sequence);
    /// <c>FaceoffWithReferee</c> builds both picks' coroutines inline, from listeners client mode detaches, so <c>RunPick</c>,
    /// which every pick goes through, is the backstop (by then the coroutine has marked the pick done itself).
    /// </summary>
    private static bool CanadaCursePrefix(MethodBase __originalMethod, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!HostsMatchHere)
        {
            return true;
        }

        bool waits = false;
        try
        {
            // The anthem's call waits for the host's pick to be over (its intro stays with the host's, MatchIntro); the
            // other picks, and a match whose intro isn't followed, get the empty enumerator.
            Il2CppSystem.Collections.IEnumerator? wait = null;
            if (__originalMethod.Name == nameof(CanadaCursePicker.Run))
            {
                try
                {
                    wait = MatchIntro.CanadaRun();
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"Campaign matches: couldn't make this PC's intro wait for the host's Canada curse pick ({ModLog.SafeErrorText(ex)}); it goes on at once.");
                }
            }

            waits = wait != null;
            __result = wait ?? new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        }
        catch (Exception ex)
        {
            // The anthem would throw on a missing enumerator: the game's own pick runs instead (its menu shows here).
            ModLog.Error("Campaign matches: couldn't skip the Canada curse pick on this PC; the game's own runs (it may differ from the host's)", ex);
            return true;
        }

        try
        {
            GameManager? game = UnityEngine.Object.FindObjectOfType<GameManager>();
            int match = game != null ? game.GetInstanceID() : 0;
            if (match != 0 && __originalMethod.Name == nameof(CanadaCursePicker.Run))
            {
                CanadaCursePicker._doneForMatch = match;
            }
            else if (match != 0 && __originalMethod.Name == nameof(CanadaCursePicker.RunSecondPick))
            {
                CanadaCursePicker._secondPickDoneForMatch = match;
            }

            if (!waits)
            {
                CanadaCursePicker.RemoveLoadCover();
            }

            ModLog.Info($"Campaign matches: the Canada curse pick ({__originalMethod.Name}) is the host's; skipped on this PC{(match == 0 ? " (no match found to mark it done for)" : string.Empty)}" +
                $"{(waits ? "; its load cover stays until the host's pick is over" : string.Empty)}.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Campaign matches: the Canada curse pick ({__originalMethod.Name}) is skipped on this PC, but marking it done or removing its cover failed " +
                $"({ModLog.SafeErrorText(ex)}); the screen may stay covered until the match ends.");
        }

        return false;
    }

    /// <summary>
    /// A friend's way to the host's "match" screen: the match relay loads the host's match from its setup, and this only
    /// waits until it does (answering at once when it already is).
    /// </summary>
    private sealed class MatchFollower : IScreenFollower
    {
        private readonly CampaignMatch _owner;
        private HostView? _view;
        private Action<FollowResult>? _done;
        private double _since;

        /// <summary>The follower of <paramref name="owner"/>'s "match" screen.</summary>
        public MatchFollower(CampaignMatch owner) => _owner = owner;

        /// <inheritdoc />
        public string Screen => Screens.Match;

        /// <inheritdoc />
        public void Go(HostView view, RunCheckpoint? run, Action<FollowResult> done)
        {
            _view = view;
            _done = done;
            _since = FrameClock.Seconds;
            Update();
        }

        /// <summary>A newer run while the match is on: not brought in (loading a run replaces the team the match plays with); the next screen applies it.</summary>
        public void UpdateRun(RunCheckpoint run, Action<FollowResult> done) => done(new FollowResult(true, run.Sequence, string.Empty, Applied: false));

        /// <inheritdoc />
        public void Update()
        {
            if (_view == null || _done == null)
            {
                return;
            }

            if (_owner.LoadingOrPlaying(_view))
            {
                uint applied = RunSync.Instance?.AppliedCheckpoint ?? 0;
                Answer(new FollowResult(true, applied, string.Empty, Applied: applied != 0));
            }
            else if (FrameClock.Seconds - _since > GoTimeoutSeconds)
            {
                Answer(new FollowResult(false, 0, FollowReasons.MatchNotLoading, Applied: false));
            }
        }

        /// <inheritdoc />
        public void Leave()
        {
            _view = null;
            _done = null;
        }

        /// <summary>Calls the relay back, once.</summary>
        private void Answer(FollowResult result)
        {
            Action<FollowResult> done = _done!;
            _view = null;
            _done = null;
            done(result);
        }
    }
}

/// <summary>Registers the campaign matches module and its patches.</summary>
internal static partial class CampaignModules
{
    static partial void AddMatches(List<ICampaignModule> modules, CampaignContext context) => modules.Add(new CampaignMatch(context));

    static partial void InstallMatches(HarmonyLib.Harmony harmony, List<string> failed)
    {
        CampaignMatch.Install(harmony, failed);
        MatchPresentationPatches.Install(harmony, failed);
    }
}
#endif
