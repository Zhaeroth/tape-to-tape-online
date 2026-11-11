using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>The steps a campaign tour's keys add, as flags (none by default: the tours run as they always have).</summary>
[Flags]
public enum CampaignTourExtras
{
    /// <summary>No key: the tour's own steps.</summary>
    None = 0,

    /// <summary><c>kbhost=on</c>: <see cref="CampaignTour.KeyboardHostStep"/> after <c>cp_first</c>.</summary>
    KeyboardHost = 1,

    /// <summary><c>events=on</c>: <see cref="CampaignTour.EventsStep"/> after <c>cp_first</c> (after the keyboard host step when both).</summary>
    Events = 2,

    /// <summary><c>late=on</c>: nobody seated at the lobby; <see cref="CampaignTour.LateJoinerStep"/> after <c>cp_continue</c>.</summary>
    LateJoiner = 4,

    /// <summary><c>sides=on</c>: <see cref="CampaignTour.SidesStep"/> after <c>cp_continue</c> (after the late joiner's step when both).</summary>
    Sides = 8,

    /// <summary><c>brawl=on</c>: brawl offers in two matches; <see cref="CampaignTour.BrawlStep"/> after <c>cp_nodes</c> reports them.</summary>
    Brawl = 16,
}

/// <summary>How an event the events step opened came to its end.</summary>
public enum CampaignEventEnd
{
    /// <summary>The developer key's call opened no event screen (the game has none of that kind loaded, or the map wasn't free).</summary>
    NotOpened,

    /// <summary>The pad drove it to its end: its screen gone, the map free again.</summary>
    Closed,

    /// <summary>The pad's picks changed nothing, and the screen's skip (its own way out with no pick) closed it.</summary>
    LeftByBack,

    /// <summary>The pad couldn't leave it: the tour stops.</summary>
    Stuck,

    /// <summary>A screen the step never means to press on came up (or a match began): the tour stops, nothing pressed there.</summary>
    OtherScreen,
}

/// <summary>What one brawl offer of the brawl key came to, for <see cref="CampaignTour.BrawlLine"/>.</summary>
public enum CampaignBrawlOutcome
{
    /// <summary>The part wasn't tried (no match with the friend's skater in play for it).</summary>
    NotTried,

    /// <summary>
    /// No open offer to press on: every call was declined by the game (its own gate: live play, not near a period's end, one brawl
    /// a match), or the offer it opened ran out before the press reached the host.
    /// </summary>
    Declined,

    /// <summary>An offer was open and the friend's fresh LB+RB took it: the host's relay accepted it for them.</summary>
    Taken,

    /// <summary>An offer was open and the friend's fresh LB+RB didn't take it.</summary>
    NotTaken,

    /// <summary>The friend's LB+RB, held through the hand-back, took an open offer: what the hold rule forbids.</summary>
    TakenWhileHeld,

    /// <summary>The friend's LB+RB, held through the hand-back, counted as a fresh combo with no offer open to take: the same fault.</summary>
    FiredWhileHeld,
}

/// <summary>The host's counts of friends' brawl combos, read by the check tour before a press and after it.</summary>
/// <param name="Combos">Every fresh combo of a friend's that reached the game's gate, taken or not.</param>
/// <param name="Accepts">Of those, the ones that accepted an open offer.</param>
/// <param name="NoOffer">Of those, the ones refused because no offer was open by then.</param>
public readonly record struct BrawlRelayCounts(int Combos, int Accepts, int NoOffer);

/// <summary>What the tour does about a fight before it ends a match by force.</summary>
public enum CampaignFightEnd
{
    /// <summary>No fight is on in this match.</summary>
    Nothing,

    /// <summary>A fight is on and its limit hasn't passed: it's waited out.</summary>
    WaitOut,

    /// <summary>A fight is still on past its limit (the step's failure): the state it shares with later matches is ended first.</summary>
    EndSharedState,
}

/// <summary>The brawl key's two parts, as the plugin saw them in the matches.</summary>
public sealed class CampaignBrawlTally
{
    /// <summary>The offer in the friend's first match outside the drop match: their fresh LB+RB.</summary>
    public CampaignBrawlOutcome Fresh { get; set; }

    /// <summary>The match of <see cref="Fresh"/> (0 when not tried).</summary>
    public int FreshMatch { get; set; }

    /// <summary>The drop match's offer: LB+RB held from the cut through the hand-back, then let go and pressed again.</summary>
    public CampaignBrawlOutcome Held { get; set; }

    /// <summary>True when an offer was open on the frame the friend's skater was theirs again (the hold rule's moment).</summary>
    public bool OfferOpenAtHandBack { get; set; }

    /// <summary>
    /// The held part's fresh press after letting go (<see cref="CampaignBrawlOutcome.Taken"/>, <see cref="CampaignBrawlOutcome.NotTaken"/>,
    /// or <see cref="CampaignBrawlOutcome.Declined"/> when the offer ran out before it reached the host; NotTried when it never came to it).
    /// </summary>
    public CampaignBrawlOutcome AfterLetGo { get; set; }

    /// <summary>How many times the developer key's call was made.</summary>
    public int Calls { get; set; }

    /// <summary>How many of those the game declined.</summary>
    public int Declined { get; set; }

    /// <summary>Brawls that began (the match's game state turned to its fight).</summary>
    public int Brawls { get; set; }

    /// <summary>Back to nothing tried.</summary>
    public void Reset()
    {
        Fresh = Held = AfterLetGo = CampaignBrawlOutcome.NotTried;
        FreshMatch = Calls = Declined = Brawls = 0;
        OfferOpenAtHandBack = false;
    }
}

/// <summary>The steps the campaign tours' keys add, their plans and their lines.</summary>
public static partial class CampaignTour
{
    /// <summary>The key that adds <see cref="EventsStep"/> (<c>events=on</c>).</summary>
    public const string EventsKey = "events";

    /// <summary>The key that seats the friend on the map after the Continue (<c>late=on</c>), in <see cref="LateJoinerStep"/>.</summary>
    public const string LateJoinerKey = "late";

    /// <summary>The key that adds <see cref="SidesStep"/> (<c>sides=on</c>).</summary>
    public const string SidesKey = "sides";

    /// <summary>The key that makes the brawl offers and adds <see cref="BrawlStep"/> (<c>brawl=on</c>).</summary>
    public const string BrawlKey = "brawl";

    /// <summary>
    /// The events step: on the map after the first match, the developer key's own call (<c>EventDevKey.Open</c>) for each kind
    /// it opens in turn, each event screen driven to its end by the tour's pad. Its picks change the tour's own run only.
    /// </summary>
    public const string EventsStep = "cp_events";

    /// <summary>The late joiner's step: on the map after the Continue, the one-PC friend seated, their A joins the game; the session puts them on the run's team.</summary>
    public const string LateJoinerStep = "cp_late";

    /// <summary>The sides step: on the map, the controller screen's choice saved by the pad (the friend to the middle), read, then put back the same way.</summary>
    public const string SidesStep = "cp_sides";

    /// <summary>The brawl step: after the nodes, the two offers' outcomes, read; it presses nothing (the offers are made in the matches).</summary>
    public const string BrawlStep = "cp_brawl";

    /// <summary>The kinds the developer's event key opens in turn (<c>EventDevKey</c>'s order), by the game's event class.</summary>
    public static IReadOnlyList<string> EventKinds => EventKindList;

    private static readonly string[] EventKindList =
    {
        "CoachEvent", "RegularPlayerTraining", "StatsTraining", "TeamTraining", "ChaosEvent3Choice", "BlademasterEvent", "GeneralManagerEvent", "FanNumber1Event", "NurseEvent",
    };

    /// <summary>How long one event may take, from its call to the map free again, before the step says the pad can't leave it.</summary>
    public const double EventSeconds = 90;

    /// <summary>Presses in a row that change nothing on an event's screen before the pad tries the screen's own way out (B).</summary>
    public const int EventNoEffectPresses = 6;

    /// <summary>How long the late joiner's A may take to make them a game player.</summary>
    public const double LateJoinSeconds = 10;

    /// <summary>How long a brawl may last before the match is ended all the same (the fight still on is the step's failure).</summary>
    public const double BrawlEndSeconds = 60;

    /// <summary>How long an accepted offer may wait for its brawl to begin (the game starts it at its own moment).</summary>
    public const double BrawlStartSeconds = 30;

    /// <summary>How long the friend's held LB+RB is kept on an open offer after the hand-back, which must not take it.</summary>
    public const double BrawlHeldSeconds = 1.5;

    /// <summary>The key of a step a key adds, or null for the tour's own steps.</summary>
    public static string? KeyOfStep(string? stepId) => stepId switch
    {
        KeyboardHostStep => KeyboardHostKey,
        EventsStep => EventsKey,
        LateJoinerStep => LateJoinerKey,
        SidesStep => SidesKey,
        BrawlStep => BrawlKey,
        _ => null,
    };

    /// <summary>The steps <paramref name="arguments"/> (a campaign tour's, as <c>run.txt</c> gave them) add.</summary>
    public static CampaignTourExtras ExtrasOf(IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        CampaignTourExtras extras = CampaignTourExtras.None;
        foreach (KeyValuePair<string, string> argument in arguments)
        {
            if (!string.Equals(argument.Value, "on", StringComparison.Ordinal))
            {
                continue;
            }

            extras |= argument.Key switch
            {
                KeyboardHostKey => CampaignTourExtras.KeyboardHost,
                EventsKey => CampaignTourExtras.Events,
                LateJoinerKey => CampaignTourExtras.LateJoiner,
                SidesKey => CampaignTourExtras.Sides,
                BrawlKey => CampaignTourExtras.Brawl,
                _ => CampaignTourExtras.None,
            };
        }

        return extras;
    }

    /// <summary>
    /// Why <paramref name="extras"/> can't run together, or null when they can. The keyboard host step needs the friend seated
    /// on the map right after the first match; <c>late=on</c> seats them only after the Continue.
    /// </summary>
    public static string? ExtrasProblem(CampaignTourExtras extras) =>
        (extras & (CampaignTourExtras.KeyboardHost | CampaignTourExtras.LateJoiner)) == (CampaignTourExtras.KeyboardHost | CampaignTourExtras.LateJoiner)
            ? $"{KeyboardHostKey}=on and {LateJoinerKey}=on can't both hold: the keyboard host step needs the friend seated on the map right after the first match, and {LateJoinerKey}=on seats them only after the Continue"
            : null;

    /// <summary>
    /// The campaign tours' check of a request's keys together (<see cref="UiCheckTour.KeysCheck"/>): <see cref="ExtrasProblem"/>,
    /// then <see cref="BrawlEndKeyProblem"/>.
    /// </summary>
    public static string? KeysCheck(IReadOnlyList<KeyValuePair<string, string>> arguments) => ExtrasProblem(ExtrasOf(arguments)) ?? BrawlEndKeyProblem(arguments);

    /// <summary>
    /// The plan of a campaign tour with <paramref name="extras"/>: <see cref="Steps"/> for none, <see cref="StepsWithKeyboardHost"/>
    /// for <c>kbhost=on</c> alone, else the same object for the same keys every time. A combination
    /// <see cref="ExtrasProblem"/> refuses has no plan (an argument exception: a request never holds one).
    /// </summary>
    public static IReadOnlyList<UiCheckStep> StepsWith(CampaignTourExtras extras)
    {
        int index = (int)extras;
        IReadOnlyList<UiCheckStep>[] plans = PlanTable.Plans;
        return index >= 0 && index < plans.Length && plans[index] is { } plan
            ? plan
            : throw new ArgumentException($"no campaign plan for {extras}: {ExtrasProblem(extras) ?? "not a combination of the keys"}", nameof(extras));
    }

    /// <summary>The keys whose plan <paramref name="steps"/> is (by reference), or null when it isn't a campaign tour's plan.</summary>
    public static CampaignTourExtras? ExtrasOfPlan(IReadOnlyList<UiCheckStep>? steps)
    {
        if (steps == null)
        {
            return null;
        }

        IReadOnlyList<UiCheckStep>[] plans = PlanTable.Plans;
        for (int i = 0; i < plans.Length; i++)
        {
            if (plans[i] != null && ReferenceEquals(plans[i], steps))
            {
                return (CampaignTourExtras)i;
            }
        }

        return null;
    }

    /// <summary>Every step any key adds, once each (for the start table's own check).</summary>
    public static IReadOnlyList<UiCheckStep> KeyedSteps => PlanTable.Plans[(int)AllAllowed()];

    // The largest combination ExtrasProblem allows: every key but the one late=on can't go with.
    private static CampaignTourExtras AllAllowed() => CampaignTourExtras.Events | CampaignTourExtras.LateJoiner | CampaignTourExtras.Sides | CampaignTourExtras.Brawl;

    /// <summary><see cref="Steps"/> with the steps <paramref name="extras"/> adds, each after the step it follows.</summary>
    private static UiCheckStep[] Build(CampaignTourExtras extras)
    {
        var list = new List<UiCheckStep>(Steps.Count + 5);
        foreach (UiCheckStep step in Steps)
        {
            list.Add(step);
            switch (step.Id)
            {
                case "cp_first":
                    if (extras.HasFlag(CampaignTourExtras.KeyboardHost))
                    {
                        list.Add(new UiCheckStep(KeyboardHostStep, "A keyboard host: the map's menu by the pad, read (any player drives that menu), and closed by B; then on the map, where one player has the menus, the pad set aside for a moment, so the host's own player is the keyboard alone; the friend's d-pad there refused by the host-decides rule, the map's cursor unmoved", Needs: UiCheckNeeds.PlayersSettings));
                    }

                    if (extras.HasFlag(CampaignTourExtras.Events))
                    {
                        list.Add(new UiCheckStep(EventsStep, "Every kind of event by the developer key's own call on the map, each driven to its end by the pad (A on a card, the d-pad to the next, a game prompt's Yes); the team's skaters counted before and after; a screen the pad can't leave stops the tour", Needs: UiCheckNeeds.PlayersSettings));
                    }

                    break;
                case "cp_continue":
                    if (extras.HasFlag(CampaignTourExtras.LateJoiner))
                    {
                        list.Add(new UiCheckStep(LateJoinerStep, "A late joiner: the one-PC friend seated on the map (nobody was at the lobby); their A joins the game; the session puts them on the run's team; nothing opens over the map; the next match shows their skater", Needs: UiCheckNeeds.PlayersSettings));
                    }

                    if (extras.HasFlag(CampaignTourExtras.Sides))
                    {
                        list.Add(new UiCheckStep(SidesStep, "A choice on the controller screen kept: Controllers from the map's menu; the friend's own left to the middle; the host's A to ready and save; the game's memory and the team's list read; the friend put back on the team the same way and read again", Needs: UiCheckNeeds.PlayersSettings));
                    }

                    break;
                case "cp_nodes":
                    if (extras.HasFlag(CampaignTourExtras.Brawl))
                    {
                        list.Add(new UiCheckStep(BrawlStep, "The brawl offers, read: one taken by the friend's fresh LB+RB; one with LB+RB held from the drop through the hand-back, not taken until let go and pressed again; a declined offer is a note", Needs: UiCheckNeeds.PlayersSettings));
                    }

                    break;
            }
        }

        return list.ToArray();
    }

    // Every plan, by its keys' flags: built once, on first use (after the tour's own steps exist); a refused combination has none.
    private static class PlanTable
    {
        public static readonly IReadOnlyList<UiCheckStep>[] Plans = Make();

        private static IReadOnlyList<UiCheckStep>[] Make()
        {
            var plans = new IReadOnlyList<UiCheckStep>[(int)(CampaignTourExtras.KeyboardHost | CampaignTourExtras.Events | CampaignTourExtras.LateJoiner | CampaignTourExtras.Sides | CampaignTourExtras.Brawl) + 1];
            for (int i = 0; i < plans.Length; i++)
            {
                var extras = (CampaignTourExtras)i;
                plans[i] = ExtrasProblem(extras) != null ? null!
                    : extras == CampaignTourExtras.None ? Steps
                    : extras == CampaignTourExtras.KeyboardHost ? StepsWithKeyboardHost
                    : Build(extras);
            }

            return plans;
        }
    }

    // ---- The late joiner ----

    /// <summary>
    /// The late joiner's line: <c>campaign: late=on: the friend seated on the map: Here after 3.1 s; game player 2 by their own
    /// A after 0.4 s; on the run's team's list; the game remembers them on the team (right); nothing over the map</c>.
    /// </summary>
    public static string LateJoinerLine(double? hereSeconds, int gamePlayer, double? joinSeconds, bool onTeamList, string remembered, string overMap) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign: {LateJoinerKey}=on: the friend seated on the map: {(hereSeconds is { } here ? string.Create(CultureInfo.InvariantCulture, $"Here after {here:0.0} s") : "never Here")}; " +
            $"{(gamePlayer >= 0 && joinSeconds is { } join ? string.Create(CultureInfo.InvariantCulture, $"game player {gamePlayer} by their own A after {join:0.0} s") : "no game player from their A")}; " +
            $"{(onTeamList ? "on the run's team's list" : "not on the run's team's list")}; the game remembers them {remembered}; {overMap}");

    /// <summary>
    /// What's wrong with the late joiner's seating, or null: Here, a game player by their A, on the team's list the next match
    /// seats from, and nothing the tour didn't ask for over the map (<paramref name="overMap"/>: null when nothing is).
    /// </summary>
    public static string? LateJoinerProblem(bool here, int gamePlayer, bool onTeamList, string? overMap)
    {
        if (overMap != null)
        {
            return $"{LateJoinerKey}=on: seating the friend on the map opened {overMap}, which the tour didn't ask for";
        }

        if (!here)
        {
            return $"{LateJoinerKey}=on: the friend never came to Here on the map";
        }

        if (gamePlayer < 0)
        {
            return $"{LateJoinerKey}=on: the friend's A made no game player (the game's [PlayerAssigner] lines say why)";
        }

        return onTeamList ? null : $"{LateJoinerKey}=on: the friend (game player {gamePlayer}) isn't on the run's team's list: the next match wouldn't seat them";
    }

    /// <summary>
    /// The late joiner's first match: <c>campaign: late=on: the late joiner's skater in live play 4.2 s after match 2 came into
    /// play (game player 2: on a team, a skater)</c>, or never seen (<paramref name="seconds"/> null) within
    /// <see cref="DropWaitSeconds"/>.
    /// </summary>
    public static string LateMatchLine(int match, double? seconds, string player) => seconds is { } s
        ? string.Create(CultureInfo.InvariantCulture, $"campaign: {LateJoinerKey}=on: the late joiner's skater in live play {s:0.0} s after match {match} came into play ({player})")
        : string.Create(CultureInfo.InvariantCulture, $"campaign: {LateJoinerKey}=on: the late joiner held no skater in live play in match {match} within {DropWaitSeconds:0} s ({player})");

    // ---- Events ----

    /// <summary>An event's class in the step's words: its kind's own name (<c>GeneralManagerEvent</c>), or <c>an event of N characters</c> for anything else.</summary>
    public static string EventClassText(string? eventClass) =>
        eventClass != null && Array.IndexOf(EventKindList, eventClass) >= 0 ? eventClass
        : string.Create(CultureInfo.InvariantCulture, $"an event of {eventClass?.Length ?? 0} characters");

    /// <summary>
    /// One event's line: <c>campaign: events=on: event 7 (GeneralManagerEvent): closed by the pad in 12.4 s; 9 presses, 1
    /// choice(s) made; the team's skaters 8 -&gt; 9</c>.
    /// </summary>
    public static string EventLine(int number, string? eventClass, CampaignEventEnd end, double seconds, int presses, int choices, int skatersBefore, int skatersAfter)
    {
        string how = end switch
        {
            CampaignEventEnd.NotOpened => "not opened (the key's own line says why)",
            CampaignEventEnd.Closed => "closed by the pad",
            CampaignEventEnd.LeftByBack => "left with no pick by the pad's skip (its picks changed nothing)",
            CampaignEventEnd.Stuck => "the pad couldn't leave it",
            _ => "a screen the step doesn't press on came up",
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"campaign: {EventsKey}=on: event {number} ({EventClassText(eventClass)}): {how} in {Math.Max(seconds, 0):0.0} s; {Math.Max(presses, 0)} press(es), {Math.Max(choices, 0)} choice(s) made; the team's skaters {SkatersText(skatersBefore)} -> {SkatersText(skatersAfter)}");
    }

    /// <summary>
    /// The events step's last line: <c>campaign: events=on: 9 called, 8 opened, 8 left by the pad (1 with no pick); the team's skaters
    /// 8 -&gt; 8: 1 signing(s), 1 loss(es) on the way</c>.
    /// </summary>
    public static string EventsLine(int called, int opened, int closed, int leftByBack, int skatersFirst, int skatersLast, int signings, int losses) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign: {EventsKey}=on: {called} called, {opened} opened, {closed} left by the pad ({leftByBack} with no pick); the team's skaters {SkatersText(skatersFirst)} -> {SkatersText(skatersLast)}: {signings} signing(s), {losses} loss(es) on the way");

    /// <summary>
    /// What's wrong with the events step, or null: every opened event left by the pad, and at least one change to the team (the
    /// step exists to make a signing or a loss certain: none means the picks it made never reached the team).
    /// </summary>
    public static string? EventsProblem(int opened, int closed, int signings, int losses)
    {
        if (opened == 0)
        {
            return $"{EventsKey}=on: the developer key's call opened no event screen";
        }

        if (closed < opened)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{EventsKey}=on: {opened - closed} of {opened} event screen(s) weren't left by the pad");
        }

        return signings + losses == 0 ? $"{EventsKey}=on: no event's pick changed the team (no signing, no loss)" : null;
    }

    private static string SkatersText(int count) => count < 0 ? "?" : count.ToString(CultureInfo.InvariantCulture);

    // ---- Sides ----

    /// <summary>
    /// One pass of the sides step: <c>campaign: sides=on: the friend (game player 2) to the middle (not playing) by their own
    /// press; saved by the host's A: the screen closed; the game remembers them on the middle (not playing); the team's list 1
    /// player(s), the friend not on it</c>.
    /// </summary>
    public static string SidesLine(int gamePlayer, int wantedSide, bool moved, bool saved, string remembered, int listCount, bool friendOnList) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign: {SidesKey}=on: the friend (game player {gamePlayer}) to {SideText(wantedSide)} {(moved ? "by their own press" : "not moved by their press")}; " +
            $"{(saved ? "saved by the host's A: the screen closed" : "not saved")}; the game remembers them {remembered}; " +
            $"the team's list {(listCount < 0 ? "unread" : listCount.ToString(CultureInfo.InvariantCulture) + " player(s)")}, the friend {(friendOnList ? "on it" : "not on it")}");

    /// <summary>
    /// What's wrong with one pass of the sides step, or null: the friend's press moved them to <paramref name="wantedSide"/>,
    /// the host's A saved it, and the game's memory and team list agree with it (on the list only on the team's side).
    /// </summary>
    public static string? SidesProblem(int wantedSide, bool moved, bool saved, int rememberedSide, bool friendOnList)
    {
        if (!moved)
        {
            return $"{SidesKey}=on: the friend's press didn't move them to {SideText(wantedSide)}";
        }

        if (!saved)
        {
            return $"{SidesKey}=on: the host's A didn't save the controller screen's choice";
        }

        if (rememberedSide != wantedSide)
        {
            return $"{SidesKey}=on: the game remembers the friend on {SideText(rememberedSide)}, not {SideText(wantedSide)}";
        }

        bool wantedOnList = wantedSide == SideRight;
        return friendOnList == wantedOnList ? null
            : $"{SidesKey}=on: the team's list {(friendOnList ? "holds" : "lacks")} the friend after saving them on {SideText(wantedSide)}";
    }

    // ---- Brawl ----

    /// <summary>
    /// What a friend's fresh LB+RB on an open offer came to, by the host's relay counts read before the press and after it.
    /// Taken only when the relay accepted an offer for them: the game's own flag for an accepted offer is cleared again the
    /// moment the brawl begins, so it can't tell. A combo the game's gate refused because the offer had run out by the time
    /// it reached the host is the game's own gate (a note); a press that never reached the gate as a combo, or one refused
    /// for another reason, is not taken.
    /// </summary>
    public static CampaignBrawlOutcome BrawlPressOutcome(BrawlRelayCounts before, BrawlRelayCounts after)
    {
        if (after.Accepts > before.Accepts)
        {
            return CampaignBrawlOutcome.Taken;
        }

        int combos = after.Combos - before.Combos;
        return combos > 0 && after.NoOffer - before.NoOffer == combos ? CampaignBrawlOutcome.Declined : CampaignBrawlOutcome.NotTaken;
    }

    /// <summary>
    /// What LB+RB held from the drop's cut through the hand-back came to, by the relay counts read at the cut and when the
    /// hold's time is up: an accepted offer or any combo at all while held breaks the hold rule; with neither, the rule held
    /// (shown on an open offer: <paramref name="offerOpen"/>, or only as no combo when the game opened none).
    /// </summary>
    public static CampaignBrawlOutcome BrawlHeldOutcome(BrawlRelayCounts before, BrawlRelayCounts after, bool offerOpen) =>
        after.Accepts > before.Accepts ? CampaignBrawlOutcome.TakenWhileHeld
        : after.Combos > before.Combos ? CampaignBrawlOutcome.FiredWhileHeld
        : offerOpen ? CampaignBrawlOutcome.NotTaken
        : CampaignBrawlOutcome.Declined;

    /// <summary>
    /// True while a fight is on in this match: its game state is the fight, an accepted offer waits to begin, or the game's
    /// fight matchmaker is still on after a fight that began in this match (its end sequence leaves the fight state before
    /// it is over). The matchmaker alone doesn't count: the game leaves it on when a match ends during a fight, so in a
    /// later match it says nothing about that match.
    /// </summary>
    public static bool BrawlUnderWay(bool fightState, bool pendingStart, bool begunThisMatch, bool matchmakerOn) =>
        fightState || pendingStart || (begunThisMatch && matchmakerOn);

    /// <summary>
    /// What the tour does before it ends a match by force: nothing with no fight on, the fight waited out, or, once its limit
    /// has passed with it still on (<paramref name="overran"/>), the state it shares with later matches ended first.
    /// </summary>
    public static CampaignFightEnd BrawlBeforeForcedEnd(bool underWay, bool overran) =>
        !underWay ? CampaignFightEnd.Nothing : overran ? CampaignFightEnd.EndSharedState : CampaignFightEnd.WaitOut;

    /// <summary>
    /// The line after the calls that end a fight's shared state before a forced end: how many fighters left fight mode, what
    /// the game's two flags read afterwards, and the first thing that failed (<paramref name="problem"/>, or null). Only the
    /// matchmaker left on harms the matches after (no player can take a skater while it is), so the line's end follows it:
    /// <c>campaign: brawl=on: before the forced end, the fight's shared state ended by the game's own calls (6 fighter(s) out
    /// of fight mode, its matchmaker off, its brawl HUD down): the matches after play as usual</c>.
    /// </summary>
    public static string BrawlSharedStateLine(int fighters, bool matchmakerOn, bool hudUp, string? problem)
    {
        bool clean = !matchmakerOn && !hudUp && string.IsNullOrEmpty(problem);
        string after = matchmakerOn ? "the matches after may start with no player on a skater"
            : clean ? "the matches after play as usual"
            : "the matchmaker is off, so the matches after have their players on skaters";
        return string.Create(CultureInfo.InvariantCulture,
            $"campaign: {BrawlKey}=on: before the forced end, {(clean ? "the fight's shared state ended by the game's own calls" : "the game's own calls didn't end all of the fight's shared state")} " +
            $"({fighters} fighter(s) out of fight mode, its matchmaker {(matchmakerOn ? "still on" : "off")}, its brawl HUD {(hudUp ? "still up" : "down")}" +
            $"{(string.IsNullOrEmpty(problem) ? string.Empty : "; " + problem)}): {after}");
    }

    /// <summary>
    /// The brawl step's line: <c>campaign: brawl=on: in match 1 the friend's fresh LB+RB: taken; in the drop match, LB+RB held
    /// through the hand-back with an offer open: not taken while held, taken once let go and pressed again; the key's call
    /// made 5 time(s), declined 1; brawls begun 2</c>.
    /// </summary>
    public static string BrawlLine(CampaignBrawlTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        string fresh = tally.Fresh switch
        {
            CampaignBrawlOutcome.NotTried => "the friend's fresh LB+RB: not tried (no match with their skater in play outside the drop match)",
            CampaignBrawlOutcome.Declined => string.Create(CultureInfo.InvariantCulture, $"in match {tally.FreshMatch} the friend's fresh LB+RB: no open offer to press on (the game declined each call, or its offer ran out first)"),
            _ => string.Create(CultureInfo.InvariantCulture, $"in match {tally.FreshMatch} the friend's fresh LB+RB: {OutcomeText(tally.Fresh)}"),
        };

        string held = tally.Held switch
        {
            CampaignBrawlOutcome.NotTried => "in the drop match: not tried",
            CampaignBrawlOutcome.Declined => "in the drop match: no offer open while LB+RB was held (the game declined each call)" +
                (tally.AfterLetGo == CampaignBrawlOutcome.NotTried ? string.Empty : ", once let go and pressed again " + OutcomeText(tally.AfterLetGo)),
            _ => $"in the drop match, LB+RB held through the hand-back {(tally.OfferOpenAtHandBack ? "with an offer open" : "(no offer open at the hand-back itself)")}: " +
                $"{HeldText(tally.Held)}, " +
                $"{(tally.AfterLetGo == CampaignBrawlOutcome.NotTried ? "no fresh press after it" : "once let go and pressed again " + OutcomeText(tally.AfterLetGo))}",
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"campaign: {BrawlKey}=on: {fresh}; {held}; the key's call made {tally.Calls} time(s), declined {tally.Declined}; brawls begun {tally.Brawls}");
    }

    /// <summary>
    /// What's wrong with the brawl offers, or null. Only an open offer the friend's fresh press didn't take, or a combo that
    /// counted while LB+RB was held through the hand-back (an offer taken, or a combo with none open), is a failure: a
    /// declined call is the game's own gate, a note.
    /// </summary>
    public static string? BrawlProblem(CampaignBrawlTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        if (tally.Held == CampaignBrawlOutcome.TakenWhileHeld)
        {
            return $"{BrawlKey}=on: the offer was taken while the friend held LB+RB through the hand-back (it must wait for a fresh press)";
        }

        if (tally.Held == CampaignBrawlOutcome.FiredWhileHeld)
        {
            return $"{BrawlKey}=on: LB+RB held through the hand-back counted as a fresh combo (it must wait to be let go and pressed again)";
        }

        if (tally.Fresh == CampaignBrawlOutcome.NotTaken)
        {
            return $"{BrawlKey}=on: an open offer wasn't taken by the friend's fresh LB+RB";
        }

        return tally.AfterLetGo == CampaignBrawlOutcome.NotTaken
            ? $"{BrawlKey}=on: after the hand-back, LB+RB let go and pressed again didn't take the open offer"
            : null;
    }

    /// <summary>A press's outcome in the step's words: <c>taken</c>, <c>not taken</c>, <c>declined by the game</c> (its offer ran out first).</summary>
    public static string OutcomeText(CampaignBrawlOutcome outcome) => outcome switch
    {
        CampaignBrawlOutcome.Taken => "taken",
        CampaignBrawlOutcome.NotTaken => "not taken",
        CampaignBrawlOutcome.TakenWhileHeld => "taken while held",
        CampaignBrawlOutcome.FiredWhileHeld => "counted as a fresh combo while held",
        CampaignBrawlOutcome.Declined => "declined by the game",
        _ => "not tried",
    };

    /// <summary>The held part's outcome in the step's words: <c>not taken while held</c>, or what broke the rule.</summary>
    public static string HeldText(CampaignBrawlOutcome held) => held switch
    {
        CampaignBrawlOutcome.TakenWhileHeld => "taken while held",
        CampaignBrawlOutcome.FiredWhileHeld => "counted as a fresh combo while held",
        _ => "not taken while held",
    };
}
