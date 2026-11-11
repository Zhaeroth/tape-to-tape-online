using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Lobby;

/// <summary>Which of the two campaign tours runs.</summary>
public enum CampaignTourKind
{
    /// <summary><c>campaign act1</c>: a run through act 1, conceded on the act 2 map.</summary>
    Act1,

    /// <summary><c>campaign full</c>: a run to its end.</summary>
    Full,
}

/// <summary>What a campaign tour's walk does next, after a node.</summary>
public enum CampaignWalkVerdict
{
    /// <summary>Another node.</summary>
    WalkOn,

    /// <summary>The tour's act is over (<c>act1</c>: the act 2 map): on to the concede.</summary>
    ActOver,

    /// <summary>The run has ended (won or over): on to the lobby's ribbon.</summary>
    RunEnded,

    /// <summary>The tour walked its most nodes: on to the concede.</summary>
    NodeCap,

    /// <summary>The tour's time limit is near: on to the concede while there's time to see the ribbon.</summary>
    TimeReserve,
}

/// <summary>A node's kind, from the game's <c>STS.Map.NodeType</c> name as the walker logs it.</summary>
public enum CampaignNodeKind
{
    /// <summary>Unknown: no type, or one the tour doesn't know.</summary>
    Unknown,

    /// <summary>A match: <c>Challenge</c>, <c>Elite</c> or <c>Boss</c>.</summary>
    Match,

    /// <summary>An event (every other type: the Nurse, the coach, a fan, training...).</summary>
    Event,
}

/// <summary>What a campaign tour's <c>run.txt</c> arguments ask for.</summary>
/// <param name="Seed">Seeds the walker's squad pick and route (<see cref="CampaignTour.DefaultSeed"/> when not given).</param>
/// <param name="Record">The run's last match is recorded by the host's relay (the setting held in memory for the tour).</param>
/// <param name="BrawlEndByGame">
/// <c>brawlend=game</c> (with <c>brawl=on</c>): the fresh offer's match is ended with its fight on, and its fight state left to
/// the mod's guard at the match's unload (<see cref="CampaignTour.BrawlEndKey"/>).
/// </param>
public readonly record struct CampaignTourArgs(int Seed, bool Record, bool BrawlEndByGame = false);

/// <summary>
/// The campaign tours (<c>run.txt</c>: <c>campaign act1</c> or <c>campaign full</c>, <c>seed=n</c>, <c>record=on</c>): a
/// co-op run hosted with the one-PC stand-in seated, in the tour's own run file, played by the game's own
/// <c>CampaignSoakWalker</c> coroutines pumped by the tour's frames, each match ended by the game's
/// <c>SoakMatchCheats.ForceHomeWin</c> after a dwell. Its steps, limits, keys and rules, and its lines; the plugin's
/// <c>LobbyUiCheck.Campaign.cs</c> runs them. The steps a key adds, and their lines, are in <c>CampaignTourExtras.cs</c>.
/// </summary>
public static partial class CampaignTour
{
    /// <summary>The act 1 tour's name in <c>run.txt</c>.</summary>
    public const string Act1Name = "campaign act1";

    /// <summary>The whole run's name in <c>run.txt</c>.</summary>
    public const string FullName = "campaign full";

    /// <summary>The seed when <c>run.txt</c> gives none: a fixed one, so two runs of a tour walk the same route.</summary>
    public const int DefaultSeed = 1;

    /// <summary>The largest seed taken.</summary>
    public const int MaxSeed = 999_999;

    /// <summary>The squad the walker picks: <c>"random"</c>, an unlocked squad picked with the seed (the game's own string).</summary>
    public const string RandomSquad = "random";

    /// <summary>How long a match is played before the game's ForceHomeWin ends it (real seconds, once it is in play).</summary>
    public const double DwellSeconds = 20;

    /// <summary>The visitors' lead that ends a dwell early (see <see cref="DwellOver"/>).</summary>
    public const int MaxVisitorLead = 2;

    /// <summary>The match in which the friend's link is cut, so the AI stand-in plays their skater (counted from 1).</summary>
    public const int DropMatch = 2;

    /// <summary>
    /// How long the drop match waits for the friend's game player to hold a skater in live play (the match intro over, the
    /// anthem skate-in done) before the drop is cut: the AI stand-in takes only a skater the friend holds.
    /// </summary>
    public const double DropWaitSeconds = 45;

    /// <summary>How long the drop match goes on once the friend is back and their skater is theirs again, before its forced win.</summary>
    public const double AfterDropSeconds = 3;

    /// <summary>
    /// What's wrong when the node walk ended after <paramref name="matchesWon"/> won matches, or null: the walk's step claims
    /// the friend's drop in match <see cref="DropMatch"/>, so a walk that ended before that match never showed the AI stand-in.
    /// </summary>
    public static string? DropNotPlayed(int matchesWon) => matchesWon < DropMatch
        ? string.Create(CultureInfo.InvariantCulture, $"the walk ended after {matchesWon} match(es) won: match {DropMatch}, with the friend's drop and the AI stand-in, was never played")
        : null;

    /// <summary>
    /// The drop's line: <c>campaign: the second match: a simulated drop; the AI stand-in played their skater 3.0 s after the
    /// cut; the friend back after 16.0 s (seat ...)</c>, or <c>the AI stand-in never played their skater</c> when it wasn't
    /// seen (<paramref name="standInSeconds"/> null).
    /// </summary>
    public static string DropLine(double? standInSeconds, double backSeconds, string seat) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign: the second match: a simulated drop; {(standInSeconds is { } seconds ? string.Create(CultureInfo.InvariantCulture, $"the AI stand-in played their skater {seconds:0.0} s after the cut") : "the AI stand-in never played their skater")}; the friend back after {backSeconds:0.0} s ({seat})");

    /// <summary>
    /// What holds the map bar's Start, or null when a press of it opens the map's menu. The bar's Menu entry is a
    /// <c>UIInputShortcut</c> with no input handler of its own or above it in the scene, so the game's UI module takes its press
    /// only while no input handler is on its stack at all (<c>UIModule.CanProcessInput</c> with no handler). The loading
    /// transition is one: it stays on the stack until its hide has completed, a second or so after the map is first seen, and a
    /// press before that is dropped. The entry also presses only for the players it registered (those driving the menus), and
    /// its handler opens nothing while the map ignores inputs.
    /// </summary>
    /// <param name="entryShown">The bar's Menu entry is on screen (its bar hides while a map screen is open).</param>
    /// <param name="mapIgnoresInputs">The map's own handler ignores inputs (its arrival, a screen opening or just closed).</param>
    /// <param name="navigationActive">The game's navigation is active (the entry asks it).</param>
    /// <param name="moduleTakesInput">The UI module takes the entry's input: nothing is on its input stack.</param>
    /// <param name="padRegistered">The entry answers the pad's player.</param>
    public static string? MapStartHeld(bool entryShown, bool mapIgnoresInputs, bool navigationActive, bool moduleTakesInput, bool padRegistered) =>
        !entryShown ? "the map bar's Menu entry isn't on screen"
        : mapIgnoresInputs ? "the map ignores inputs"
        : !navigationActive ? "the game's navigation is off"
        : !moduleTakesInput ? "an input handler is still on the UI's stack"
        : !padRegistered ? "the Menu entry doesn't answer the pad's player"
        : null;

    /// <summary>The controller screen's left side, as the game numbers it (<c>ControllerAssignmentPlayer.ESide</c>).</summary>
    public const int SideLeft = 0;

    /// <summary>The controller screen's middle: in a campaign a player there doesn't play.</summary>
    public const int SideCenter = 1;

    /// <summary>The controller screen's right side: the campaign's one team.</summary>
    public const int SideRight = 2;

    /// <summary>
    /// True when the game's controller screen, opened from the run's map with no press made, shows both the host's and the
    /// friend's game players on the campaign's team: the screen places each player by the side the game remembers, and a
    /// campaign's start seats the players remembered there (D145: the host remembers every lobby player there at the start).
    /// </summary>
    public static bool ScreenShowsTeam(bool hostSeen, int hostSide, bool friendSeen, int friendSide) =>
        hostSeen && friendSeen && hostSide == SideRight && friendSide == SideRight;

    /// <summary>
    /// The first match's line for the friend: <c>campaign: the first match: the friend's skater in live play 4.2 s after the
    /// match came into play (game player 2: on a team, a skater)</c>, or, never seen (<paramref name="seconds"/> null), that it
    /// wasn't within <see cref="DropWaitSeconds"/>.
    /// </summary>
    public static string FirstMatchLine(double? seconds, string player) => seconds is { } s
        ? string.Create(CultureInfo.InvariantCulture, $"campaign: the first match: the friend's skater in live play {s:0.0} s after the match came into play ({player})")
        : string.Create(CultureInfo.InvariantCulture, $"campaign: the first match: the friend's game player held no skater in live play within {DropWaitSeconds:0} s ({player}), with no visit to the controller screen");

    /// <summary>
    /// The step that sees a keyboard host's refusal of a friend's menu press (HostDecides in the plugin, Core's
    /// <see cref="Hosting.FriendControlAsk"/>): with the tour's pad set aside for a moment the host's own player is the
    /// keyboard alone, and the one-PC friend's d-pad on the map must be refused by the rule and move nothing. The press
    /// is made on the map with nothing over it, where one player has the menus and the game asks: a menu any player drives
    /// (the map's own menu is one) takes every player's press without the ask (<see cref="KeyboardHostHeld"/>). It runs
    /// after <c>cp_first</c> (the map back), only when the tour is asked for with <c>kbhost=on</c>
    /// (<see cref="StepsFor"/>), and leaves the map as <c>cp_continue</c> expects it.
    /// </summary>
    public const string KeyboardHostStep = "cp_kbhost";

    /// <summary>
    /// The keyboard host step's line: <c>campaign: a keyboard host: the host's own player keyboard (Rewired player 0, game player
    /// 0), the tour's pad set aside; the friend's d-pad on the map: refused 2 time(s) by the host-decides rule (0 in the
    /// same time before it, with nothing pressed); the cursor stayed on MapNode</c>. <paramref name="host"/> is who the
    /// rule took for the host's own player; <paramref name="refusedIdle"/> counts the asks about the friend's player that
    /// the game made by itself, so the reader sees whether the refusals came with the press.
    /// </summary>
    public static string KeyboardHostLine(string host, int refused, int refusedIdle, bool cursorStayed, string cursorBefore, string cursorAfter) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign: a keyboard host: the host's own player {host}, the tour's pad set aside; the friend's d-pad on the map: refused {Math.Max(refused, 0)} time(s) by the host-decides rule ({Math.Max(refusedIdle, 0)} in the same time before it, with nothing pressed); {(cursorStayed ? "the cursor stayed on " + cursorBefore : "the cursor moved from " + cursorBefore + " to " + cursorAfter)}");

    /// <summary>
    /// Who has the menus at one moment of the keyboard host step (<paramref name="where"/>: <c>the map before its menu</c>,
    /// <c>the map's menu up</c>, <c>the map after its menu</c>), from the game's own state: <c>campaign: a keyboard host: the map's menu up: the game's
    /// any-player control on, the UI's controlling player nobody (every player's press is taken here, as on a couch, with no
    /// ask: the rule has nothing to refuse)</c>, or <c>... the map after its menu: the game's any-player control off, the
    /// UI's controlling player Rewired player 1, game player 1 (the pad) (one player has the menus: the game asks before it
    /// takes another's press)</c>. With any-player control on the game names a controlling player only when it tracks one
    /// player in all, so <c>nobody</c> is what it says there with a friend seated.
    /// </summary>
    public static string KeyboardHostControlLine(string where, bool anyPlayerControl, string controlling) =>
        $"campaign: a keyboard host: {where}: the game's any-player control {(anyPlayerControl ? "on" : "off")}, the UI's controlling player {controlling} " +
        (anyPlayerControl
            ? "(every player's press is taken here, as on a couch, with no ask: the rule has nothing to refuse)"
            : "(one player has the menus: the game asks before it takes another's press)");

    /// <summary>
    /// What keeps the keyboard host step from making the friend's press, or null when it may be made. The press shows the
    /// rule, and can move nothing of the host's whatever the rule does, only in one state: this PC <paramref name="hosting"/>,
    /// the map with nothing over it (<paramref name="mapBare"/>), the game's any-player control off
    /// (<paramref name="anyPlayerControl"/>: a menu any player drives takes every player's press without the ask, so there the
    /// rule is never asked and a press would move the menu's cursor, as on a couch), and the tour's pad the one player on the
    /// game's controlling list (<paramref name="padControls"/>: the game's own ask then answers the friend no as well, without
    /// side effects; with nobody on the list it could answer yes).
    /// </summary>
    public static string? KeyboardHostHeld(bool hosting, bool mapBare, bool anyPlayerControl, bool padControls)
    {
        if (!hosting)
        {
            return "this PC isn't hosting";
        }

        if (!mapBare)
        {
            return "a screen is over the map";
        }

        if (anyPlayerControl)
        {
            return "the game's any-player control is on: every player's press is taken without the ask, so the rule isn't asked";
        }

        return padControls ? null : "the tour's pad isn't the UI's controlling player: with nobody on the game's list its own ask could say yes to the friend";
    }

    /// <summary>
    /// What's wrong with the keyboard host step, or null: the rule must have taken the host's own player for a keyboard one
    /// (<paramref name="hostOnKeyboard"/>: else it has nothing to refuse, and on a keyboard host's PC it wouldn't either),
    /// refused at least one ask of the friend's (<paramref name="refused"/>: else their press never reached the game's ask
    /// on the map), none in the same time before it with nothing pressed (<paramref name="refusedIdle"/>: asks the game
    /// makes by itself would be counted under the press too, so a refusal there wouldn't show that the press reached the
    /// ask), and the map's cursor stayed where it was (<paramref name="cursorStayed"/>: it stood still before the press, so a
    /// move under it is taken for the friend's: the host's map took their press).
    /// </summary>
    public static string? KeyboardHostProblem(bool hostOnKeyboard, int refused, int refusedIdle, bool cursorStayed)
    {
        if (!hostOnKeyboard)
        {
            return "a keyboard host: with the tour's pad set aside the rule found no keyboard player of the host's own among the game's tracked players: it has nothing to refuse";
        }

        if (!cursorStayed)
        {
            return "a keyboard host: the cursor of the host's map moved under the friend's d-pad";
        }

        if (refused <= 0)
        {
            return "a keyboard host: no ask of the friend's was refused: their d-pad never reached the game's ask on the map";
        }

        if (refusedIdle > 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"a keyboard host: the game asked about the friend's player {refusedIdle} time(s) with nothing pressed: the refusals under the d-pad can't be told from those, so they don't show that the press reached the ask");
        }

        return null;
    }

    /// <summary>A side in the step's words: <c>the team (right)</c>, <c>the middle (not playing)</c>, <c>the left</c>.</summary>
    public static string SideText(int side) => side switch
    {
        SideRight => "the team (right)",
        SideCenter => "the middle (not playing)",
        SideLeft => "the left",
        _ => string.Create(CultureInfo.InvariantCulture, $"side {side}"),
    };

    /// <summary>The most nodes <c>act1</c> walks (act 1 has about 8 floors).</summary>
    public const int MaxNodesAct1 = 16;

    /// <summary>The most nodes <c>full</c> walks (three acts).</summary>
    public const int MaxNodesFull = 48;

    /// <summary>The time a tour keeps for its end (the concede, the lobby's ribbon, the close): no node starts inside it.</summary>
    public static readonly TimeSpan EndReserve = TimeSpan.FromMinutes(2.5);

    /// <summary>The act 1 tour's time limit.</summary>
    public static readonly TimeSpan Act1Limit = TimeSpan.FromMinutes(15);

    /// <summary>The whole run's time limit.</summary>
    public static readonly TimeSpan FullLimit = TimeSpan.FromMinutes(40);

    /// <summary>The keys both tours take.</summary>
    public static IReadOnlyList<UiCheckKey> Keys { get; } = new[]
    {
        UiCheckKey.Number("seed", "seeds the walker's squad pick and route", 0, MaxSeed),
        UiCheckKey.Choice("record", "the run's last match recorded by the host's relay, for the replay tour's F2", "on", "off"),
        UiCheckKey.Choice(KeyboardHostKey, "adds the keyboard host step after the first match (off by default: the tours run as before)", "on", "off"),
        UiCheckKey.Choice(EventsKey, "adds the events step after the first match: every kind of event opened by the developer key's own call and driven by the pad (off by default)", "on", "off"),
        UiCheckKey.Choice(LateJoinerKey, "the friend is seated on the map after the Continue, not in the lobby: a late joiner, on the team from the next match (off by default)", "on", "off"),
        UiCheckKey.Choice(SidesKey, "adds the controller screen step before the drop's nodes: the friend sits out by their own press, saved, then put back (off by default)", "on", "off"),
        UiCheckKey.Choice(BrawlKey, "a brawl offer taken by the friend's LB+RB in a match, and one held through the drop's hand-back; the brawl step reports both (off by default)", "on", "off"),
        UiCheckKey.Choice(BrawlEndKey, "with brawl=on, game: the fresh offer's match is ended with its fight on and its fight state left to the mod's guard at the match's unload; tour (the default): the tour's own handling", BrawlEndByGame, BrawlEndByTour),
    };

    /// <summary>The key that adds <see cref="KeyboardHostStep"/> to a campaign tour (<c>kbhost=on</c>); without it the step isn't in the plan.</summary>
    public const string KeyboardHostKey = "kbhost";

    /// <summary>The steps, in order; both tours share them (<see cref="KindOf"/> tells them apart). All run in the player's own settings.</summary>
    public static IReadOnlyList<UiCheckStep> Steps { get; } = new[]
    {
        new UiCheckStep("cp_host", "The tour's own run file; Host Lobby by the pad; the one-PC friend seated; mode Campaign; the save row New Run", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("cp_newrun", "Start by the pad: the countdown, the friend's ack, the game's squad screen; the game's walker picks a squad and starts the run: the world map", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("cp_first", "Nodes by the game's walker until the first match is won (ForceHomeWin after a dwell) and the map is back, a higher run checkpoint", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("cp_continue", "Back to the title by the map's menu: the lobby's Run saved ribbon; Start by the pad continues the run to the map", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("cp_nodes", "Nodes to the tour's end (act1: the act 2 map; full: the run's end); in the second match the friend dropped and the AI stand-in on their skater", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("cp_end", "Concede by the map's menu and the game's prompt by the pad, or the run's end; the lobby's ribbon RunOver or RunWon, never 0-0; the slot New Run", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("cp_close", "The lobby closed by the pad: a live title, nothing held", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>
    /// <see cref="Steps"/> with <see cref="KeyboardHostStep"/> after <c>cp_first</c>: the plan of a campaign tour asked for with
    /// <c>kbhost=on</c> and no other key (<see cref="StepsFor"/>).
    /// </summary>
    public static IReadOnlyList<UiCheckStep> StepsWithKeyboardHost { get; } = Build(CampaignTourExtras.KeyboardHost);

    /// <summary>
    /// The steps <paramref name="request"/>'s tour runs: a campaign tour's own (<see cref="Steps"/>), with the steps its keys
    /// add (<see cref="StepsWith"/>: <see cref="StepsWithKeyboardHost"/> for <c>kbhost=on</c> alone); any other tour's as its
    /// plan has them. A campaign request always holds a combination <see cref="ExtrasProblem"/> allows: its parse refused any
    /// other (<see cref="KeysCheck"/>).
    /// </summary>
    public static IReadOnlyList<UiCheckStep> StepsFor(UiCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReferenceEquals(request.Tour.Steps, Steps) ? StepsWith(ExtrasOf(request.Arguments)) : request.Tour.Steps;
    }

    /// <summary>True when <paramref name="steps"/> is one of the campaign tours' plans (with or without the steps a key adds).</summary>
    public static bool IsCampaignPlan(IReadOnlyList<UiCheckStep> steps) => ExtrasOfPlan(steps) is not null;

    /// <summary>
    /// The plan's line at the tour's start: with no key, <c>campaign: the plan has 8 steps, without cp_kbhost (kbhost=on adds
    /// it)</c>; with keys, each step a key adds and the step it follows: <c>campaign: the plan has 10 steps, cp_kbhost after
    /// cp_first (kbhost=on), cp_events after cp_kbhost (events=on)</c>.
    /// </summary>
    public static string PlanLine(IReadOnlyList<UiCheckStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        CampaignTourExtras extras = ExtrasOfPlan(steps) ?? CampaignTourExtras.None;
        if (extras == CampaignTourExtras.None)
        {
            return string.Create(CultureInfo.InvariantCulture, $"campaign: the plan has {steps.Count} steps, without {KeyboardHostStep} ({KeyboardHostKey}=on adds it)");
        }

        var added = new List<string>();
        for (int i = 1; i < steps.Count; i++)
        {
            if (KeyOfStep(steps[i].Id) is { } key)
            {
                added.Add($"{steps[i].Id} after {steps[i - 1].Id} ({key}=on)");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"campaign: the plan has {steps.Count} steps, {string.Join(", ", added)}");
    }

    /// <summary>Which campaign tour <paramref name="tourName"/> is, or null when it is neither.</summary>
    public static CampaignTourKind? KindOf(string? tourName) =>
        string.Equals(tourName, Act1Name, StringComparison.Ordinal) ? CampaignTourKind.Act1
        : string.Equals(tourName, FullName, StringComparison.Ordinal) ? CampaignTourKind.Full
        : null;

    /// <summary>The arguments <paramref name="request"/> gives (the defaults for any it doesn't).</summary>
    public static CampaignTourArgs ArgsOf(UiCheckRequest? request)
    {
        int seed = DefaultSeed;
        if (request?.Value("seed") is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int given))
        {
            seed = Math.Clamp(given, 0, MaxSeed);
        }

        return new CampaignTourArgs(seed, string.Equals(request?.Value("record"), "on", StringComparison.Ordinal),
            string.Equals(request?.Value(BrawlEndKey), BrawlEndByGame, StringComparison.Ordinal));
    }

    /// <summary>
    /// The route seed for the walker's <c>WalkWorldMapToMatch</c> at node walk <paramref name="walk"/> (from 1) of a tour
    /// seeded <paramref name="seed"/>: never 0 (0 makes the walker take the first attainable node every time), the same for
    /// the same seed and walk, different from one walk to the next.
    /// </summary>
    public static int RouteSeed(int seed, int walk)
    {
        unchecked
        {
            int value = (int)(((uint)Math.Max(seed, 0) * 2654435761u) ^ ((uint)Math.Max(walk, 0) * 40503u)) & 0x7FFFFFFF;
            return value == 0 ? 1 : value;
        }
    }

    /// <summary>The kind of a node whose <c>NodeType</c> the walker named <paramref name="nodeType"/>.</summary>
    public static CampaignNodeKind NodeKindOf(string? nodeType) => nodeType switch
    {
        null or "" or "?" or "NULL" => CampaignNodeKind.Unknown,
        "Challenge" or "Elite" or "Boss" => CampaignNodeKind.Match,
        _ => CampaignNodeKind.Event,
    };

    /// <summary>True when <paramref name="nodeType"/> is a boss match, the last match of its act.</summary>
    public static bool IsBoss(string? nodeType) => string.Equals(nodeType, "Boss", StringComparison.Ordinal);

    /// <summary>The act whose boss ends the tour's play: <c>act1</c>'s act 1, the whole run's act 3.</summary>
    public static int LastAct(CampaignTourKind kind) => kind == CampaignTourKind.Act1 ? 1 : 3;

    /// <summary>True when the match about to start is the tour's last (its last act's boss): <c>record=on</c> records it.</summary>
    public static bool IsLastMatch(CampaignTourKind kind, int act, string? nodeType) => IsBoss(nodeType) && act == LastAct(kind);

    /// <summary>
    /// What the walk does after a node: the run's end first, then the tour's act, the node cap, and the time kept for the
    /// end (<paramref name="elapsedSeconds"/> since the tour began, of <paramref name="limit"/>).
    /// </summary>
    public static CampaignWalkVerdict Next(CampaignTourKind kind, int act, bool runEnded, int nodes, double elapsedSeconds, TimeSpan limit)
    {
        if (runEnded)
        {
            return CampaignWalkVerdict.RunEnded;
        }

        if (kind == CampaignTourKind.Act1 && act > 1)
        {
            return CampaignWalkVerdict.ActOver;
        }

        if (nodes >= (kind == CampaignTourKind.Act1 ? MaxNodesAct1 : MaxNodesFull))
        {
            return CampaignWalkVerdict.NodeCap;
        }

        return elapsedSeconds >= (limit - EndReserve).TotalSeconds ? CampaignWalkVerdict.TimeReserve : CampaignWalkVerdict.WalkOn;
    }

    /// <summary>The verdict in words, for the step's note.</summary>
    public static string Said(CampaignWalkVerdict verdict) => verdict switch
    {
        CampaignWalkVerdict.ActOver => "act 1 is over (the act 2 map)",
        CampaignWalkVerdict.RunEnded => "the run has ended",
        CampaignWalkVerdict.NodeCap => "the tour's most nodes are walked",
        CampaignWalkVerdict.TimeReserve => "the time kept for the tour's end has come",
        _ => "on to the next node",
    };

    /// <summary>
    /// True when a match's dwell is over: <see cref="DwellSeconds"/> in play, or sooner once the visitors lead by
    /// <see cref="MaxVisitorLead"/> (the tour's own and its friend's skaters stand idle, and a lost match could end the run).
    /// </summary>
    public static bool DwellOver(double inPlaySeconds, int homeGoals, int visitorGoals) =>
        inPlaySeconds >= DwellSeconds || visitorGoals - homeGoals >= MaxVisitorLead;

    /// <summary>True when the run checkpoint rose from <paramref name="before"/> (at the last map) to <paramref name="after"/>: every map load after a node saves the run.</summary>
    public static bool CheckpointRose(uint before, uint after) => after > before;

    /// <summary>
    /// What's wrong with the lobby's ribbon after the tour's end, or null: a run conceded or lost reads <c>RunOver</c>, a run
    /// won <c>RunWon</c>, each with its act (never 0, so never 0-0) and floor; <paramref name="won"/> says which the tour saw.
    /// </summary>
    public static string? RibbonProblem(LobbyResult ribbon, bool won)
    {
        LobbyResultKind wanted = won ? LobbyResultKind.RunWon : LobbyResultKind.RunOver;
        if (ribbon.Kind != wanted)
        {
            return $"the ribbon reads {ribbon.Kind}, not {wanted}";
        }

        // A run on the map is act 1 or later: act 0 is the blank run the game puts in place at a run's end.
        return ribbon.First == 0
            ? string.Create(CultureInfo.InvariantCulture, $"the ribbon's act and floor read {ribbon.First}-{ribbon.Second}")
            : null;
    }

    /// <summary>What's wrong with the ribbon after the map's menu left the run, or null: <c>RunSaved</c> with the run's act and floor.</summary>
    public static string? SavedRibbonProblem(LobbyResult ribbon, int act, int floor)
    {
        if (ribbon.Kind != LobbyResultKind.RunSaved)
        {
            return $"the ribbon reads {ribbon.Kind}, not RunSaved";
        }

        return ribbon.First != act || ribbon.Second != floor
            ? string.Create(CultureInfo.InvariantCulture, $"the ribbon reads {ribbon.First}-{ribbon.Second}, the run is at {act}-{floor}")
            : null;
    }

    /// <summary>A ribbon in the summary's words: <c>RunOver 2-1</c>.</summary>
    public static string RibbonText(LobbyResult ribbon) =>
        ribbon.Kind == LobbyResultKind.None ? "None" : string.Create(CultureInfo.InvariantCulture, $"{ribbon.Kind} {ribbon.First}-{ribbon.Second}");

    /// <summary>
    /// The tour's last line: <c>campaign: N nodes (M matches, E events, R rewards), the run over at 2-1; ribbon RunOver 2-1;
    /// checkpoints c; time-scale resets t; achievements held back h; allowances refused r; writes refused w; tour run file
    /// hash (size).</c>
    /// </summary>
    public static string SummaryLine(CampaignTourTally tally, string runState, int act, int floor, LobbyResult ribbon, uint checkpoints, int timeScaleResets,
        long achievementsHeld, long allowancesRefused, long writesRefused, string runFile)
    {
        ArgumentNullException.ThrowIfNull(tally);
        return string.Create(CultureInfo.InvariantCulture,
            $"campaign: {tally.Nodes} nodes ({tally.Matches} matches, {tally.Events} events, {tally.Rewards} rewards), the run {runState} at {act}-{floor}; ribbon {RibbonText(ribbon)}; " +
            $"checkpoints {checkpoints}; time-scale resets {timeScaleResets}; achievements held back {achievementsHeld}; allowances refused {allowancesRefused}; " +
            $"writes refused {writesRefused}; tour run file {runFile}.");
    }

    /// <summary>A file's hash and size for the summary: <c>0123456789abcdef (48.0 KB)</c>, or <c>missing</c>.</summary>
    public static string FileText(string? sha256Hex, long bytes) =>
        string.IsNullOrEmpty(sha256Hex) ? "missing"
        : string.Create(CultureInfo.InvariantCulture, $"{sha256Hex[..Math.Min(16, sha256Hex.Length)].ToLowerInvariant()} ({Math.Max(bytes, 0) / 1024.0:0.0} KB)");
}

/// <summary>
/// One line the game's walker logged, read: <c>[SoakTest:Campaign] run=1 act=1 phase=SelectingNode node=Chaos point=0,3
/// opponent=?</c>, or an event's <c>... node=Chaos menu=EventChoiceMenu choice=2</c>.
/// </summary>
/// <param name="Phase">Its phase (<c>SelectingNode</c>, <c>EventNode</c>, <c>SquadSelect</c>...), or "".</param>
/// <param name="Node">The node's type (<c>node=</c>), or "".</param>
/// <param name="Point">The node's point (<c>point=</c>), or "".</param>
/// <param name="Opponent">The opponent (<c>opponent=</c>, "?" for none), or "".</param>
/// <param name="Menu">The menu it acted in (<c>menu=</c>), or "".</param>
/// <param name="Choice">The choice it made there (<c>choice=</c>), or "".</param>
/// <param name="Timeout">A walker failure (<c>TIMEOUT</c>).</param>
public readonly record struct WalkerLine(string Phase, string Node, string Point, string Opponent, string Menu, string Choice, bool Timeout)
{
    /// <summary>The longest line read; the rest is ignored.</summary>
    public const int MaxLength = 600;

    /// <summary>True when this line selects a node (the walker's <c>SelectingNode</c> with a node).</summary>
    public bool SelectsNode => Phase == "SelectingNode" && Node.Length > 0;

    /// <summary>Reads <paramref name="line"/>. False when it isn't one of the walker's (no <c>phase=</c> and no <c>node=</c>). Never throws.</summary>
    public static bool TryRead(string? line, out WalkerLine read)
    {
        read = default;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        string text = line.Length > MaxLength ? line[..MaxLength] : line;
        string phase = Field(text, "phase=");
        string node = Field(text, "node=");
        if (phase.Length == 0 && node.Length == 0)
        {
            return false;
        }

        read = new WalkerLine(phase, node, Field(text, "point="), Field(text, "opponent="), Field(text, "menu="), Field(text, "choice="),
            text.Contains(" TIMEOUT ", StringComparison.Ordinal));
        return true;
    }

    /// <summary>
    /// The value after <paramref name="key"/> up to the next key (a space, then a word and <c>=</c>) or the line's end, its
    /// spaces kept (an opponent's name can have them: <c>opponent=Greasy Lettuce</c>); at most 48 characters, or "".
    /// </summary>
    private static string Field(string text, string key)
    {
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(key, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return string.Empty;
            }

            // A key starts a word: "point=" must not match inside another key.
            if (found == 0 || text[found - 1] == ' ')
            {
                int start = found + key.Length;
                int end = NextKey(text, start);
                string value = text[start..end].TrimEnd();
                return value.Length > 48 ? value[..48] : value;
            }

            at = found + key.Length;
        }
    }

    /// <summary>Where the next <c> key=</c> after <paramref name="from"/> begins (its space), or the text's length.</summary>
    private static int NextKey(string text, int from)
    {
        for (int space = text.IndexOf(' ', from); space >= 0; space = text.IndexOf(' ', space + 1))
        {
            int i = space + 1;
            while (i < text.Length && (IsKeyLetter(text[i]) || (i > space + 1 && (text[i] is (>= '0' and <= '9') or '_'))))
            {
                i++;
            }

            if (i > space + 1 && i < text.Length && text[i] == '=')
            {
                return space;
            }
        }

        return text.Length;
    }

    private static bool IsKeyLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}

/// <summary>A node the tour walked, as its line says it.</summary>
/// <param name="Number">Its number in the tour, from 1.</param>
/// <param name="Act">The run's act when it was selected.</param>
/// <param name="Floor">The run's floor when it was selected.</param>
/// <param name="Type">The node's type, as the walker named it.</param>
/// <param name="About">The opponent (a match) or the point (an event), as the walker named it.</param>
/// <param name="StartedAt">When it was selected, seconds on the tour's clock.</param>
public sealed record CampaignNode(int Number, int Act, int Floor, string Type, string About, double StartedAt)
{
    /// <summary>The choice the walker made in its event menu, or "".</summary>
    public string Choice { get; set; } = string.Empty;

    /// <summary>Reward menus seen while it was the node in play.</summary>
    public int Rewards { get; set; }

    /// <summary>A match was played from it (a match node, or an event that led to a match).</summary>
    public bool Matched { get; set; }
}

/// <summary>
/// The campaign tour's count of what it walked, and the line for each node as it closes: an event when the walker selects
/// the next node, a match when the tour has won it. Fed from the walker's own lines and the tour's steps. One thread.
/// </summary>
public sealed class CampaignTourTally
{
    private readonly List<string> _closed = new();

    /// <summary>The node in play, or null.</summary>
    public CampaignNode? Current { get; private set; }

    /// <summary>Nodes selected so far.</summary>
    public int Nodes { get; private set; }

    /// <summary>Matches won so far.</summary>
    public int Matches { get; private set; }

    /// <summary>Event nodes closed so far (an event that led to a match counts as a match).</summary>
    public int Events { get; private set; }

    /// <summary>Reward menus seen so far.</summary>
    public int Rewards { get; private set; }

    /// <summary>Every count back to 0 and no node in play.</summary>
    public void Reset()
    {
        Current = null;
        Nodes = Matches = Events = Rewards = 0;
        _closed.Clear();
    }

    /// <summary>
    /// A walker line: a node selected opens one (closing an event before it); an event's choice is kept. False for a line
    /// that changes nothing here.
    /// </summary>
    public bool Read(in WalkerLine line, int act, int floor, double now, uint checkpoints, long menusSent)
    {
        if (line.SelectsNode)
        {
            CloseEvent(now, checkpoints, menusSent);
            Nodes++;
            string about = CampaignTour.NodeKindOf(line.Node) == CampaignNodeKind.Match && line.Opponent.Length > 0 && line.Opponent != "?" ? line.Opponent
                : line.Point.Length > 0 ? $"at {line.Point}" : "?";
            Current = new CampaignNode(Nodes, act, floor, line.Node, about, now);
            return true;
        }

        if (Current != null && line.Menu == "EventChoiceMenu" && line.Choice.Length > 0)
        {
            Current.Choice = line.Choice;
            return true;
        }

        return false;
    }

    /// <summary>A reward menu showed while the node in play is current.</summary>
    public void RewardSeen()
    {
        Rewards++;
        if (Current != null)
        {
            Current.Rewards++;
        }
    }

    /// <summary>The node in play led to a match (the walker handed it to a match scene).</summary>
    public void MatchStarted()
    {
        if (Current != null)
        {
            Current.Matched = true;
        }
    }

    /// <summary>The node in play's match was won and the map is back (or the run ended): its line is closed.</summary>
    public void MatchWon(double now, uint checkpoints, long menusSent)
    {
        if (Current == null)
        {
            return;
        }

        Matches++;
        Current.Matched = true;
        Close(now, checkpoints, menusSent);
    }

    /// <summary>An event in play closes (the next node, or the walk's end).</summary>
    public void CloseEvent(double now, uint checkpoints, long menusSent)
    {
        if (Current == null || Current.Matched)
        {
            return;
        }

        Events++;
        Close(now, checkpoints, menusSent);
    }

    /// <summary>The lines closed since the last call, in order.</summary>
    public IReadOnlyList<string> TakeLines()
    {
        string[] lines = _closed.ToArray();
        _closed.Clear();
        return lines;
    }

    /// <summary>A node's line: <c>campaign: node 3 (act 1, floor 2): Elite Spartans; won (forced) in 61 s; checkpoints 4; menus sent 12.</c></summary>
    public static string Line(CampaignNode node, double now, uint checkpoints, long menusSent)
    {
        ArgumentNullException.ThrowIfNull(node);
        string outcome = node.Matched ? "won (forced)" : $"event {(node.Choice.Length > 0 ? node.Choice : "?")}";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"campaign: node {node.Number} (act {node.Act}, floor {node.Floor}): {node.Type} {node.About}; {outcome}");
        if (node.Rewards > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", rewards {node.Rewards}");
        }

        text.Append(CultureInfo.InvariantCulture, $" in {Math.Max(now - node.StartedAt, 0):0} s; checkpoints {checkpoints}; menus sent {menusSent}.");
        return text.ToString();
    }

    private void Close(double now, uint checkpoints, long menusSent)
    {
        _closed.Add(Line(Current!, now, checkpoints, menusSent));
        Current = null;
    }
}
