#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using Il2CppAI.Brawl;
using Il2CppFighting;
using Il2CppMovements.MovementStates;
using Il2CppSkaters;
using Il2CppState;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Runtime;
using T2TOnline.Plugin.Streaming;
using Player = Il2CppTape2Tape.Player;

namespace T2TOnline.Plugin.NativeLobby;

// The campaign tours' brawl offers (brawl=on; Core's CampaignTour.BrawlStep reports them after the nodes). A brawl offer
// comes from the game's own offer code (BrawlChallengeSystem.TryOfferChallenge), asked by F10's own call
// (ModRuntime.OfferBrawl): a tour can't press F10 (a human's key press stops it). The game opens an offer at once (no delay)
// for 5 s (its OfferWindowSeconds), or declines it (live play only, not near a period's end, one brawl a match): a decline is
// a note. The friend accepts as a couch player does, with LB+RB on their own pad (their stream, TourFriendPress); the host
// relays a fresh combo to the game's own AcceptOffer (BrawlComboRelay). Whether a press took an offer is read from the
// relay's own counts, before the press and after it: the game's flag for an accepted offer can't tell, because the fight
// starts on the game's next update and its start clears the flag again (EnterFightMode cancels the offer). Two parts:
// - fresh: in the friend's first match outside the drop match, once their skater is in live play: an offer, their LB+RB,
//   taken; then the brawl it begins is waited out before the match is ended (with brawlend=game it is left on through the
//   forced end instead, for the mod's guard: LobbyUiCheck.BrawlEnd.cs).
// - held: in the drop match, LB+RB held from the cut (nothing goes out while their link is cut) through the hand-back, with an
//   offer kept open meanwhile (a new one each time the last ran out): on the hand-back the session holds their controller
//   neutral until then and counts what's down as no fresh press (Core's BrawlComboWatch), so no combo may reach the relay
//   while it's held; then let go and pressed again, the offer must be taken, and that brawl is waited out too.
// A fight is this match's while its game state is the fight, an accepted offer waits to begin, or the game's fight
// matchmaker is still on after a fight that began in this match (CampaignTour.BrawlUnderWay). The game ends a fight only in
// its own end sequence, once a whole side is down; a match ended with the fight still on leaves the matchmaker and the brawl
// HUD's flag set, and then no player can take a skater in the matches after (Player.TakeControlOfSkater asks the
// matchmaker). So a fight still on at its limit is the step's failure, said once with every fighter's state, and before the
// match is ended by force the calls the game's end sequence makes for that shared state are made here.
// Every press is let go in a finally; nothing here runs without brawl=on.
internal static partial class LobbyUiCheck
{
    private const double BrawlOfferWaitSeconds = 1;
    private const int BrawlFreshCalls = 4;

    // The held part's next call: half a second after an offer ran out, two after a decline (each call writes F10's line).
    private const double BrawlReofferSeconds = 0.5;
    private const double BrawlAfterDeclineSeconds = 2;

    // How long a press may take to reach the host's relay as a combo (their stream, the playout, an input update).
    private const double BrawlComboSeconds = 2;

    private static readonly CampaignBrawlTally BrawlTally = new();
    private static double _brawlLastCall = double.NegativeInfinity;
    private static bool _brawlLastDeclined;

    // The relay's counts at the drop's cut: the held part compares them with the counts when the hold's time is up.
    private static BrawlRelayCounts _brawlAtCut;

    // This match's fight: begun (its game state seen as the fight), and still on past its limit (said once a match).
    private static bool _brawlBegun;
    private static bool _brawlOverran;

    private static bool Brawling => _cpExtras.HasFlag(CampaignTourExtras.Brawl);

    private static void ResetBrawl()
    {
        BrawlTally.Reset();
        _brawlLastCall = double.NegativeInfinity;
        _brawlLastDeclined = false;
        _brawlAtCut = default;
        BrawlNewMatch();
        ResetBrawlEnd(); // brawlend=game (LobbyUiCheck.BrawlEnd.cs)
    }

    /// <summary>A match starts: no fight of it has begun (what an earlier match left on isn't this one's).</summary>
    private static void BrawlNewMatch()
    {
        _brawlBegun = false;
        _brawlOverran = false;
    }

    /// <summary>cp_brawl: the two parts' outcomes, read. It presses nothing (the offers were made in the matches).</summary>
    private static IEnumerable<Wait> StepCampaignBrawl()
    {
        if (!_cpReady)
        {
            Problem("skipped: cp_host didn't set the tour's run up");
            yield break;
        }

        StepNote(CampaignTour.BrawlLine(BrawlTally));
        if (CampaignTour.BrawlProblem(BrawlTally) is { } problem)
        {
            Problem(problem);
        }

        if (BrawlEndingByGame)
        {
            BrawlEndLateRead();
            StepNote(CampaignTour.BrawlEndLine(BrawlEndTally));
            if (CampaignTour.BrawlEndProblem(BrawlEndTally) is { } ended)
            {
                Problem(ended);
            }
        }
    }

    /// <summary>True while the game holds an open offer. Never throws.</summary>
    private static bool OfferOpen()
    {
        try
        {
            return BrawlChallengeSystem.IsOfferActive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True while this match's game state is the fight (never left over from an earlier match). Never throws.</summary>
    private static bool FightState()
    {
        try
        {
            return HudModel.CurrentGameState() == (int)GameState.Fight;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True while an accepted offer waits for its fight to begin. Never throws.</summary>
    private static bool PendingStart()
    {
        try
        {
            return BrawlChallengeSystem.HasPendingAcceptedStart;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True while the game's fight matchmaker is on: set by a fight's start, cleared only by its end sequence. Never throws.</summary>
    private static bool MatchmakerOn()
    {
        try
        {
            return BrawlMatchmakerSystem.IsActive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True while a fight of this match is on or an accepted one waits to begin.</summary>
    private static bool BrawlUnderWay() => CampaignTour.BrawlUnderWay(FightState(), PendingStart(), _brawlBegun, MatchmakerOn());

    /// <summary>F10's own call, counted; true when the game opened an offer.</summary>
    private static bool CallOffer()
    {
        BrawlTally.Calls++;
        _brawlLastCall = FrameClock.Seconds;
        bool offered;
        try
        {
            offered = ModRuntime.OfferBrawl();
        }
        catch (Exception ex)
        {
            StepNote($"campaign: {CampaignTour.BrawlKey}=on: the key's call threw ({ModLog.SafeErrorText(ex)})");
            offered = false;
        }

        if (!offered)
        {
            BrawlTally.Declined++;
        }

        _brawlLastDeclined = !offered;
        return offered;
    }

    /// <summary>
    /// The friend's LB+RB for a moment on an open offer, then what it came to by the relay's counts: taken only when the relay
    /// accepted an offer for them (<see cref="CampaignTour.BrawlPressOutcome"/>). The press is let go by <see cref="FriendTap"/>.
    /// </summary>
    private static IEnumerable<Wait> BrawlPress(OnlineSession session, Action<CampaignBrawlOutcome, string> done)
    {
        BrawlRelayCounts before = BrawlComboRelay.Counts;
        foreach (Wait wait in FriendTap(session, BrawlCombo.PadCombo))
        {
            yield return wait;
        }

        yield return Soft(() => { Watch(); return BrawlComboRelay.Counts.Combos > before.Combos; }, BrawlComboSeconds, "the friend's combo at the host's relay");
        BrawlRelayCounts after = BrawlComboRelay.Counts;
        CampaignBrawlOutcome outcome = CampaignTour.BrawlPressOutcome(before, after);
        string how = outcome switch
        {
            CampaignBrawlOutcome.Taken => "taken",
            CampaignBrawlOutcome.Declined => "its offer had run out when the press reached the host (the game's own gate; a note)",
            _ when after.Combos == before.Combos => "not taken (no combo of theirs reached the host's relay)",
            _ => "not taken (the game's gate refused the combo; the relay's \"Brawl:\" line says why)",
        };
        done(outcome, how);
    }

    /// <summary>
    /// The fresh part, in match <paramref name="number"/> (outside the drop match): once the friend's skater is in live play, an
    /// offer, the friend's LB+RB on it, taken; the brawl it begins waited out. Only once a tour.
    /// </summary>
    private static IEnumerable<Wait> BrawlFresh(int number)
    {
        if (!Brawling || BrawlTally.Fresh != CampaignBrawlOutcome.NotTried || !_cpFriendSeated)
        {
            yield break;
        }

        OnlineSession session = Sd();
        yield return Soft(() => { Watch(); return FriendSkaterInPlay(); }, 20, "the friend's skater in live play");
        if (!FriendSkaterInPlay())
        {
            StepNote($"campaign: {CampaignTour.BrawlKey}=on: match {number}: the friend's skater isn't in live play: no offer here");
            yield break;
        }

        BrawlTally.FreshMatch = number;
        bool open = false;
        for (int call = 0; call < BrawlFreshCalls && !open && !Stopping; call++)
        {
            if (call > 0)
            {
                yield return Soft(() => { Watch(); return false; }, 2, "a moment before the next call");
            }

            if (!InPlay())
            {
                yield return Soft(() => { Watch(); return InPlay(); }, 5, "live play");
            }

            if (CallOffer())
            {
                yield return Soft(() => { Watch(); return OfferOpen(); }, BrawlOfferWaitSeconds, "the brawl offer open");
            }

            open = OfferOpen();
        }

        if (!open)
        {
            BrawlTally.Fresh = CampaignBrawlOutcome.Declined;
            StepNote($"campaign: {CampaignTour.BrawlKey}=on: match {number}: the game declined {BrawlTally.Declined} call(s): no offer to take (a note)");
            yield break;
        }

        StepNote($"campaign: {CampaignTour.BrawlKey}=on: match {number}: an offer is open; the friend's LB+RB");
        foreach (Wait wait in Snap("brawl_offer", 0))
        {
            yield return wait;
        }

        string how = "no open offer to press on (it ran out before the press)";
        BrawlTally.Fresh = CampaignBrawlOutcome.Declined;
        if (MayPress("the friend's LB+RB on the open offer", () => InPlay() && OfferOpen()))
        {
            foreach (Wait wait in BrawlPress(session, (outcome, text) => (BrawlTally.Fresh, how) = (outcome, text)))
            {
                yield return wait;
            }
        }

        StepNote($"campaign: {CampaignTour.BrawlKey}=on: match {number}: the friend's fresh LB+RB: {how}");
        foreach (Wait wait in BrawlEndingByGame ? BrawlLeaveOn(number) : BrawlWaitOut()) // brawlend=game: left on (LobbyUiCheck.BrawlEnd.cs)
        {
            yield return wait;
        }
    }

    /// <summary>
    /// This match's fight, waited out: its start (an accepted offer's fight begins on the game's own update, in live play),
    /// then its end. A fight still on at the limit is the step's failure, said once a match with every fighter's state.
    /// </summary>
    private static IEnumerable<Wait> BrawlWaitOut()
    {
        if (_brawlOverran || !BrawlUnderWay())
        {
            yield break;
        }

        if (!_brawlBegun)
        {
            yield return Soft(() => { Watch(); return FightState() || !PendingStart(); }, CampaignTour.BrawlStartSeconds, "the brawl to begin");
            if (!FightState())
            {
                StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: {CampaignTour.BrawlKey}=on: the accepted offer's brawl {(PendingStart()
                    ? $"hadn't begun within {CampaignTour.BrawlStartSeconds:0} s (the game begins it at its own moment)"
                    : "didn't begin (the game took the accepted offer back: a goal, a period's or the match's end)")}"));
                yield break;
            }

            _brawlBegun = true;
            BrawlTally.Brawls++;
        }

        double started = FrameClock.Seconds;
        yield return Soft(() => { Watch(); return !BrawlUnderWay(); }, CampaignTour.BrawlEndSeconds, "the brawl's end");
        if (Stopping)
        {
            yield break; // the tour is stopping: the wait was cut short, the limit didn't pass
        }

        if (BrawlUnderWay())
        {
            _brawlOverran = true;
            Fail(string.Create(CultureInfo.InvariantCulture, $"campaign: {CampaignTour.BrawlKey}=on: the brawl was still on after {CampaignTour.BrawlEndSeconds:0} s; the match is ended all the same"));
            StepNote(BrawlFightersLine());
        }
        else
        {
            StepNote(string.Create(CultureInfo.InvariantCulture, $"campaign: {CampaignTour.BrawlKey}=on: a brawl began and ended in {FrameClock.Seconds - started:0.0} s (its \"Brawl:\" line follows the session's look)"));
        }
    }

    /// <summary>
    /// Before a match is ended by force: a fight of it still on is waited out; one that passed its limit has its shared state
    /// ended first (<see cref="BrawlEndSharedState"/>), so no fight runs into the forced end and on into the matches after.
    /// </summary>
    private static IEnumerable<Wait> BrawlBeforeForcedEnd()
    {
        if (!Brawling || BrawlLeftOnAtForcedEnd()) // brawlend=game: this match's fight is left on (LobbyUiCheck.BrawlEnd.cs)
        {
            yield break;
        }

        if (CampaignTour.BrawlBeforeForcedEnd(BrawlUnderWay(), _brawlOverran) == CampaignFightEnd.WaitOut)
        {
            foreach (Wait wait in BrawlWaitOut())
            {
                yield return wait;
            }
        }

        if (CampaignTour.BrawlBeforeForcedEnd(BrawlUnderWay(), _brawlOverran) == CampaignFightEnd.EndSharedState)
        {
            BrawlEndSharedState();
        }
    }

    /// <summary>
    /// The fight passed its limit and the match is about to be ended by force. What the game's end sequence does for the state a
    /// fight shares with later matches is done here, in its order: every fighter out of fight mode (Forward.ExitFightMode), the
    /// fight matchmaker ended, the brawl HUD ended. The rest of a fight's state is the match's own and goes with it. Each of the
    /// three is tried on its own: a fighter that can't be taken out of fight mode, or a list that can't be read, doesn't keep
    /// the matchmaker from being ended, and the matchmaker is what refuses every player a skater in the matches after. The
    /// line says what the game's own flags read afterwards. Never throws.
    /// </summary>
    private static void BrawlEndSharedState()
    {
        int fighters = 0;
        string? problem = null;
        try
        {
            foreach (Forward fighter in HudFighters())
            {
                try
                {
                    if (fighter.isInFightMode)
                    {
                        fighter.ExitFightMode();
                        fighters++;
                    }
                }
                catch (Exception ex)
                {
                    problem ??= $"a fighter couldn't be taken out of fight mode ({ModLog.SafeErrorText(ex)})";
                }
            }
        }
        catch (Exception ex)
        {
            problem ??= $"the fighters couldn't be read ({ModLog.SafeErrorText(ex)})";
        }

        try
        {
            BrawlMatchmakerSystem.EndBrawl();
        }
        catch (Exception ex)
        {
            problem ??= $"the matchmaker's end threw ({ModLog.SafeErrorText(ex)})";
        }

        try
        {
            FightHpBarSystem.EndBrawl();
        }
        catch (Exception ex)
        {
            problem ??= $"the brawl HUD's end threw ({ModLog.SafeErrorText(ex)})";
        }

        StepNote(CampaignTour.BrawlSharedStateLine(fighters, MatchmakerOn(), BrawlHudUp(), problem));
    }

    private static bool BrawlHudUp()
    {
        try
        {
            return FightHpBarSystem.IsBrawlActive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The forwards with a bar in the brawl HUD: both sides' rosters as the game's end test walks them, and the neutral ones.</summary>
    private static List<Forward> HudFighters()
    {
        var fighters = new List<Forward>();
        Il2CppSystem.Collections.Generic.List<FightHpBar>? bars = FightHpBarSystem._bars;
        for (int i = 0; bars != null && i < bars.Count; i++)
        {
            FightHpBar bar = bars[i];
            if (bar != null && bar._forward is Forward forward && forward != null)
            {
                fighters.Add(forward);
            }
        }

        return fighters;
    }

    /// <summary>
    /// The fight as it stands, for the line under a fight's failure: the game's state and its fight flags, then every fighter
    /// with a bar: its side, its movement state (a knocked-out one held down, or getting up), fight mode, health, who plays it
    /// (a game player's number, or the AI) and where it stands. The game ends a fight when every fighter of one side is
    /// knocked out and held down: the line shows which one isn't. No name in it. Never throws.
    /// </summary>
    private static string BrawlFightersLine()
    {
        var line = new StringBuilder($"campaign: {CampaignTour.BrawlKey}=on: the fight at its limit: ");
        try
        {
            line.Append(CultureInfo.InvariantCulture, $"game state {HudModel.CurrentGameState()?.ToString(CultureInfo.InvariantCulture) ?? "none"} (the fight is {(int)GameState.Fight}), ")
                .Append(CultureInfo.InvariantCulture, $"matchmaker {(MatchmakerOn() ? "on" : "off")}, brawl HUD {(BrawlHudUp() ? "up" : "down")}, an accepted start {(PendingStart() ? "waiting" : "not waiting")}; fighters: ");
            PlayersManager? players = PlayersManager.Instance;
            List<Forward> fighters = HudFighters();
            for (int i = 0; i < fighters.Count; i++)
            {
                Forward fighter = fighters[i];
                Il2CppTape2Tape.Team? team = fighter._team;
                MovementState? state = fighter.movementManager?.MovementStateManager?._currentMovementState;
                string stateName = state == null ? "no state" : state.GetIl2CppType().Name;
                if (state?.TryCast<KnockoutState>() is { } knockout)
                {
                    stateName += knockout._skipBrawlHold ? " (getting up)" : " (held down)";
                }

                Player? player = null;
                bool played = players != null && players.IsSkaterControlled(fighter, out player) && player != null;
                UnityEngine.Vector3 at = fighter.transform.position;
                line.Append(i == 0 ? string.Empty : "; ")
                    .Append(CultureInfo.InvariantCulture, $"{i + 1} {(team == null ? "no side" : team.isHomeTeam ? "home" : "visitor")}: {stateName}, ")
                    .Append(CultureInfo.InvariantCulture, $"{(fighter.isInFightMode ? "in" : "not in")} fight mode, health {fighter.HP:0.#}, ")
                    .Append(CultureInfo.InvariantCulture, $"{(played ? $"game player {player!._PlayerId_k__BackingField}" : "the AI")}")
                    .Append(fighter.forceAIControlled ? ", AI forced" : string.Empty)
                    .Append(fighter.canBeSelected ? string.Empty : ", not selectable")
                    .Append(CultureInfo.InvariantCulture, $", at {at.x:0.#},{at.z:0.#}");
            }

            if (fighters.Count == 0)
            {
                line.Append("none with a bar");
            }
        }
        catch (Exception ex)
        {
            line.Append(CultureInfo.InvariantCulture, $" (unread from here: {ModLog.SafeErrorText(ex)})");
        }

        return line.ToString();
    }

    /// <summary>The held part's start, at the drop's cut: the friend holds LB+RB (nothing goes out while their link is cut).</summary>
    private static bool BrawlHoldBegin(OnlineSession session)
    {
        if (!Brawling || BrawlTally.Held != CampaignBrawlOutcome.NotTried)
        {
            return false;
        }

        _brawlAtCut = BrawlComboRelay.Counts;
        session.TourFriendPress(BrawlCombo.PadCombo);
        StepNote($"campaign: {CampaignTour.BrawlKey}=on: the drop match: the friend holds LB+RB from the cut; an offer is kept open until the hand-back");
        return true;
    }

    /// <summary>
    /// Each frame of the drop's wait while the held part runs: an offer kept open while the AI plays their skater (a new call
    /// once the last ran out, at most every half second; after a decline every two seconds, so a match the game won't offer
    /// in writes a few lines, not one each half second), and the hand-back's frame noted with whether an offer was open on it.
    /// </summary>
    private static void BrawlKeepOffered(OnlineSession session, ref bool standInSeen, ref bool handedBack)
    {
        int standIns = session.TourStandIns;
        if (standIns > 0)
        {
            standInSeen = true;
            double gap = _brawlLastDeclined ? BrawlAfterDeclineSeconds : BrawlReofferSeconds;
            if (!OfferOpen() && FrameClock.Seconds - _brawlLastCall > gap && InPlay())
            {
                CallOffer();
            }
        }
        else if (standInSeen && !handedBack)
        {
            handedBack = true;
            BrawlTally.OfferOpenAtHandBack = OfferOpen();
        }
    }

    /// <summary>
    /// After the hand-back, LB+RB still held: for <see cref="CampaignTour.BrawlHeldSeconds"/> on an open offer no combo of
    /// theirs may reach the relay (counted from the cut); then let go and pressed again, the offer must be taken. The press is
    /// let go in a finally.
    /// </summary>
    private static IEnumerable<Wait> BrawlAfterHandBack(OnlineSession session)
    {
        try
        {
            if (!OfferOpen() && InPlay())
            {
                CallOffer();
                yield return Soft(() => { Watch(); return OfferOpen(); }, BrawlOfferWaitSeconds, "the brawl offer open");
            }

            // With no offer open the rule still shows as no combo at the relay; with one open, as an offer not taken.
            bool offerOpen = OfferOpen();
            yield return Soft(() => { Watch(); return BrawlComboRelay.Counts.Combos > _brawlAtCut.Combos; }, CampaignTour.BrawlHeldSeconds, "the held LB+RB (no combo of it may count)");
            BrawlTally.Held = CampaignTour.BrawlHeldOutcome(_brawlAtCut, BrawlComboRelay.Counts, offerOpen);
            StepNote(BrawlTally.Held == CampaignBrawlOutcome.Declined
                ? $"campaign: {CampaignTour.BrawlKey}=on: the drop match: no offer open after the hand-back (the game declined); LB+RB held through it: no combo of it counted (a note: the rule isn't shown on an offer)"
                : $"campaign: {CampaignTour.BrawlKey}=on: the drop match: LB+RB held through the hand-back {(BrawlTally.OfferOpenAtHandBack ? "with an offer open on its frame" : "(no offer open on its frame)")}: {CampaignTour.HeldText(BrawlTally.Held)}");
        }
        finally
        {
            session.TourFriendPress(PadButtons.None);
        }

        if (BrawlTally.Held is CampaignBrawlOutcome.TakenWhileHeld or CampaignBrawlOutcome.FiredWhileHeld)
        {
            // The combo counted while held: whatever it began is waited out like any fight of this match.
            foreach (Wait wait in BrawlWaitOut())
            {
                yield return wait;
            }

            yield break;
        }

        // Let go, then pressed again on an open offer: a fresh combo, which the game takes.
        yield return Seconds(0.3);
        if (!OfferOpen() && InPlay())
        {
            CallOffer();
            yield return Soft(() => { Watch(); return OfferOpen(); }, BrawlOfferWaitSeconds, "the brawl offer open");
        }

        if (!MayPress("the friend's LB+RB again, after letting go", () => InPlay() && OfferOpen()))
        {
            StepNote($"campaign: {CampaignTour.BrawlKey}=on: the drop match: no offer open for the fresh press (the game declined): not shown (a note)");
            yield break;
        }

        string how = string.Empty;
        foreach (Wait wait in BrawlPress(session, (outcome, text) => (BrawlTally.AfterLetGo, how) = (outcome, text)))
        {
            yield return wait;
        }

        StepNote($"campaign: {CampaignTour.BrawlKey}=on: the drop match: LB+RB let go and pressed again: {how}");
        foreach (Wait wait in BrawlWaitOut())
        {
            yield return wait;
        }
    }
}
#endif
