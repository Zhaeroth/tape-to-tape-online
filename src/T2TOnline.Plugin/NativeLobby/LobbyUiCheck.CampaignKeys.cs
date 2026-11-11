#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// The campaign tours' opt-in keys (Core's CampaignTourExtras): what they share, the late joiner (late=on) and the controller
// screen's choice (sides=on). The events (LobbyUiCheck.Events.cs) and the brawl offers (LobbyUiCheck.Brawl.cs) have files of
// their own. Without a key, nothing here runs: the plan has none of its steps and every branch of the shared steps that reads
// _cpExtras keeps today's path.
//
// late=on: cp_host seats nobody, so the run's start seats the host alone; cp_late, on the map after the Continue, seats the
// one-PC friend (TourSeatFriend, as at the lobby) and presses their A (their pad's join button: the session relays it to the
// game's own AssignNextPlayer, as a real friend's late press is); the session queues them and puts them on the run's team's
// list on the map (OnlineSession.TeamSeats.cs). The next match, the drop match, must show their skater (PlayMatch). Anything
// the game opens over the map meanwhile fails the step, and the tour stops after it with no press on it.
//
// sides=on: cp_sides opens the map menu's Controllers as cp_nodes' read does, then plays the screen as a couch does: the
// friend's own left to the middle (their B first if they're locked in: an unlocked player's B backs the whole screen out, so
// it is pressed only on a locked one), the host's A to ready and A again to save (the screen's own save: ApplyCurrentSelection,
// then its exit); the game's memory of sides and the team's list are read. Then the friend goes back on the team the same way
// (their right and A). A friend not back on the team stops the tour: the drop match after the step needs them.
internal static partial class LobbyUiCheck
{
    private const double FriendPressSeconds = 0.25;
    private const double ControllerAnswerSeconds = 1.5;

    private static CampaignTourExtras _cpExtras;
    private static bool _cpFriendSeated;
    private static bool _cpLateMatchSaid;

    private static bool LateJoiner => _cpExtras.HasFlag(CampaignTourExtras.LateJoiner);

    private static void AddCampaignKeySteps()
    {
        Steps[CampaignTour.EventsStep] = StepCampaignEvents; // LobbyUiCheck.Events.cs
        Steps[CampaignTour.LateJoinerStep] = StepCampaignLateJoiner;
        Steps[CampaignTour.SidesStep] = StepCampaignSides;
        Steps[CampaignTour.BrawlStep] = StepCampaignBrawl; // LobbyUiCheck.Brawl.cs
    }

    /// <summary>The keys' state for a new run: which keys the plan holds, nothing tried.</summary>
    private static void ResetCampaignKeys()
    {
        _cpExtras = CampaignTour.ExtrasOfPlan(_tour) ?? CampaignTourExtras.None;
        _cpFriendSeated = _cpLateMatchSaid = false;
        ResetBrawl();
    }

    /// <summary>
    /// The running step can't leave the game where the next step starts (<paramref name="why"/>): its problem, and the tour
    /// stops after it (Core's <see cref="TourGate.StopAfter"/>, said in the summary's <c>STOPPED:</c> line); the caller presses
    /// nothing more.
    /// </summary>
    private static void StopAfterThisStep(string why)
    {
        Problem(why);
        Gate.StopAfter(_step?.Id ?? "?", why);
    }

    /// <summary>A menu over the map now, by its definition's name (never a player's words), or null: a transition counts as none.</summary>
    private static string? MenuOverMap()
    {
        if (TourHolds.ModPromptOpen)
        {
            return "a prompt of the mod's";
        }

        AbstractMenu? top = Top();
        if (top == null)
        {
            return null;
        }

        string id = MenuGame.MenuId(top);
        return TourStarts.IsTransition(id) ? null : MenuGame.Name(id, "a menu");
    }

    /// <summary>
    /// Whether <paramref name="gamePlayer"/> is on the team's list the campaign's matches seat from (each match's
    /// <c>InitializePlayers</c>); <paramref name="count"/> is the list's live players, or -1 when it can't be read. Never throws.
    /// </summary>
    private static bool OnTeamList(int gamePlayer, out int count)
    {
        count = -1;
        try
        {
            Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>? list = TeamSeats.TeamList(null)?.homeTeamPlayers;
            if (list == null)
            {
                return false;
            }

            bool on = false;
            count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is { } player && !player.WasCollected)
                {
                    count++;
                    on |= player._PlayerId_k__BackingField == gamePlayer;
                }
            }

            return on;
        }
        catch (Exception)
        {
            count = -1;
            return false; // read for the step's lines and checks only
        }
    }

    /// <summary>The side the game remembers for <paramref name="gamePlayer"/> (the game's numbers), or -1 when none is or it can't be read.</summary>
    private static int RememberedSide(int gamePlayer)
    {
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, ControllerAssignmentPlayer.ESide>? sides = Il2CppTape2Tape.ControllerAssignmentState.s_ControllerSide;
            return sides != null && sides.TryGetValue(gamePlayer, out ControllerAssignmentPlayer.ESide side) ? (int)side : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>The one-PC friend's press of <paramref name="buttons"/> for a moment, over Steam as their pad's stream; always let go.</summary>
    private static IEnumerable<Wait> FriendTap(OnlineSession session, PadButtons buttons)
    {
        session.TourFriendPress(buttons);
        try
        {
            yield return Seconds(FriendPressSeconds);
        }
        finally
        {
            session.TourFriendPress(PadButtons.None);
        }

        yield return Frames(2);
    }

    // ---- late=on ----

    private static IEnumerable<Wait> StepCampaignLateJoiner()
    {
        if (!CampaignGoesOn())
        {
            yield break;
        }

        if (_cpRunEnded)
        {
            Problem("the run has ended: no map to join on");
            yield break;
        }

        OnlineSession session = Sd();
        yield return Soft(() => { Watch(); return MapFree(); }, MapInputSeconds, "the map, nothing over it");
        if (!MapFree() || _walk == null)
        {
            Problem($"{CampaignTour.LateJoinerKey}=on: not tried: the map {(MapFree() ? "free" : $"not free ({MenuOverMap() ?? "nothing over it"})")}, the tour's pad {(_walk == null ? "gone" : "there")}");
            yield break;
        }

        double started = FrameClock.Seconds;
        Check("the friend arrives on the run's map", () => session.TourSeatFriend(holdHello: false), "a friend session on this game");
        yield return Soft(() => { Watch(); return FriendSeat().Stage == SeatStage.Here; }, 15, "the friend Here");
        bool here = FriendSeat().Stage == SeatStage.Here;
        double? hereSeconds = here ? FrameClock.Seconds - started : null;
        StepNote(SeatLine(FriendSeat()));

        // Their join button, as a friend who comes in during a run presses it: only on the free map, the place it is for.
        int gamePlayer = session.TourFriendGamePlayer;
        double? joinSeconds = null;
        if (here && gamePlayer < 0 && MayPress("the friend's A, their join button", MapFree))
        {
            double pressed = FrameClock.Seconds;
            foreach (Wait wait in FriendTap(session, PadButtons.A))
            {
                yield return wait;
            }

            yield return Soft(() => { Watch(); return session.TourFriendGamePlayer >= 0; }, CampaignTour.LateJoinSeconds, "the friend a game player");
            gamePlayer = session.TourFriendGamePlayer;
            joinSeconds = gamePlayer >= 0 ? FrameClock.Seconds - pressed : null;
        }

        // The session puts a late joiner on the team's list on the map, a frame or so after their join.
        int player = gamePlayer;
        yield return Soft(() => { Watch(); return player >= 0 && OnTeamList(player, out _); }, 5, "the friend on the run's team's list");
        yield return Seconds(1.5); // what the game would open over the map, given its moment
        int count = -1;
        bool onList = gamePlayer >= 0 && OnTeamList(gamePlayer, out count);
        string? over = MenuOverMap();
        string remembered = gamePlayer >= 0 ? RememberedText(gamePlayer, out _) : "nothing (no game player)";
        StepNote(CampaignTour.LateJoinerLine(hereSeconds, gamePlayer, joinSeconds, onList, remembered, over == null ? "nothing over the map" : $"{over} over the map"));
        StepNote(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"campaign: {CampaignTour.LateJoinerKey}=on: the team's list holds {count} player(s); {session.TourFriendLine()}"));
        foreach (Wait wait in Snap("late_joiner_map", SettleFrames))
        {
            yield return wait;
        }

        _cpFriendSeated = here && gamePlayer >= 0;
        if (CampaignTour.LateJoinerProblem(here, gamePlayer, onList, over) is { } problem)
        {
            if (over != null)
            {
                StopAfterThisStep(problem);
            }
            else
            {
                Problem(problem);
            }
        }
    }

    /// <summary>The drop match, with late=on: the late joiner's first match, which must show their skater (the wait is the drop's own).</summary>
    private static void NoteLateMatch(int number, double? seconds)
    {
        if (!LateJoiner || _cpLateMatchSaid)
        {
            return;
        }

        _cpLateMatchSaid = true;
        StepNote(CampaignTour.LateMatchLine(number, seconds, FriendPlayerLine()));
        Check($"{CampaignTour.LateJoinerKey}=on: the late joiner on the team in their first match", () => seconds != null, "their game player holding a skater in live play, with no visit to the controller screen");
    }

    // ---- sides=on ----

    private static IEnumerable<Wait> StepCampaignSides()
    {
        if (!CampaignGoesOn())
        {
            yield break;
        }

        if (_cpRunEnded)
        {
            Problem("the run has ended: no map to try it on");
            yield break;
        }

        OnlineSession session = Sd();
        int friend = session.TourFriendGamePlayer;
        if (!_cpFriendSeated || friend < 0 || _walk == null || _walk.GamePlayer < 0)
        {
            Problem($"{CampaignTour.SidesKey}=on: not tried: the friend {(friend < 0 ? "has no game player" : $"is game player {friend}")}, the tour's pad {(_walk == null ? "gone" : "there")}");
            yield break;
        }

        StepNote($"campaign: {CampaignTour.SidesKey}=on: before: the game remembers the friend {RememberedText(friend, out _)}; on the team's list {OnTeamList(friend, out _)}");
        foreach (Wait wait in SidesPass(session, friend, CampaignTour.SideCenter))
        {
            yield return wait;
        }

        // Back on the team whatever the first pass came to: a pass that saved nothing leaves them there already, and is said.
        bool needsBack = RememberedSide(friend) != CampaignTour.SideRight || !OnTeamList(friend, out _);
        if (_cpBroken == null && needsBack)
        {
            foreach (Wait wait in SidesPass(session, friend, CampaignTour.SideRight))
            {
                yield return wait;
            }
        }
        else if (!needsBack)
        {
            StepNote($"campaign: {CampaignTour.SidesKey}=on: the friend is on the team already (the first pass saved nothing): no second pass");
        }

        bool onTeam = OnTeamList(friend, out int count);
        bool back = RememberedSide(friend) == CampaignTour.SideRight && onTeam;
        StepNote($"campaign: {CampaignTour.SidesKey}=on: after: the game remembers the friend {RememberedText(friend, out _)}; the team's list {count} player(s), the friend {(back ? "on it" : "not on it")}");
        if (!back)
        {
            StopAfterThisStep($"{CampaignTour.SidesKey}=on: the friend isn't back on the run's team: the drop match after this step would play without them");
            yield break;
        }

        // The map's menu left by B (as cp_nodes' read leaves it), then its own input ready again, as cp_nodes finds it.
        yield return Soft(() => { Watch(); return Top()?.TryCast<WorldMapMenu>() != null || Top() == null; }, 3, "the map's menu or the map");
        if (Top()?.TryCast<WorldMapMenu>() is { } menu && MayPress("B on the map's menu", () => !menu.WasCollected && Top()?.Pointer == menu.Pointer))
        {
            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => { Watch(); return menu.WasCollected || Top()?.Pointer != menu.Pointer; }, 3, "the map's menu closed");
        }

        yield return Soft(() => { Watch(); return MapStartHeldNow() == null; }, MapInputSeconds, "the map's own input ready again");
    }

    /// <summary>
    /// One pass: the controller screen from the map's menu; the friend's own presses to <paramref name="wanted"/> (and A on the
    /// team, where only ready players play); the host's A to ready and A to save; the game's memory and list read and checked.
    /// A screen it couldn't save is left with nothing applied (its own two steps, as cp_nodes' read leaves it).
    /// </summary>
    private static IEnumerable<Wait> SidesPass(OnlineSession session, int friend, int wanted)
    {
        string? brokenBefore = _cpBroken;
        foreach (Wait wait in MapMenu("Controllers", menu => menu.m_ControllerAssignmentButton))
        {
            yield return wait;
        }

        if (!ReferenceEquals(_cpBroken, brokenBefore))
        {
            _cpBroken = brokenBefore; // no map menu: this pass fails; the run itself goes on
            Fail($"campaign: {CampaignTour.SidesKey}=on: no map menu to reach Controllers from");
            yield break;
        }

        ControllerAssignmentMenu? screen = null;
        yield return Soft(() => { Watch(); return (screen = Top()?.TryCast<ControllerAssignmentMenu>()) != null; }, 6, "the game's controller screen");
        if (screen == null || screen.WasCollected)
        {
            Fail($"campaign: {CampaignTour.SidesKey}=on: the map menu's Controllers opened no controller screen");
            yield break;
        }

        yield return Seconds(1.2); // its intro, and the remembered sides the icons take a few frames in
        ControllerAssignmentMenu open = screen;
        bool ScreenUp() => !open.WasCollected && Top()?.Pointer == open.Pointer;
        int host = _walk!.GamePlayer;
        StepNote($"campaign: {CampaignTour.SidesKey}=on: the controller screen: {SeatText(open, host, "the host")}; {SeatText(open, friend, "the friend")}; {ControllerScreenLine()}");

        // The friend: unlocked first if they're ready (B is the screen's way back only for a player who isn't).
        if (SeatOf(open, friend, out _, out bool friendReady) && friendReady && MayPress("the friend's B (to unready)", ScreenUp))
        {
            foreach (Wait wait in FriendTap(session, PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => !SeatOf(open, friend, out _, out bool ready) || !ready, ControllerAnswerSeconds, "the friend unready");
        }

        if (SeatOf(open, friend, out int side, out _) && side != wanted && MayPress($"the friend's {(wanted < side ? "left" : "right")}", ScreenUp))
        {
            foreach (Wait wait in FriendTap(session, wanted < side ? PadButtons.DPadLeft : PadButtons.DPadRight))
            {
                yield return wait;
            }

            yield return Soft(() => SeatOf(open, friend, out int now, out _) && now == wanted, ControllerAnswerSeconds, "the friend's card to move");
        }

        bool moved = SeatOf(open, friend, out int sideNow, out _) && sideNow == wanted;
        if (moved && wanted == CampaignTour.SideRight && SeatOf(open, friend, out _, out bool readyNow) && !readyNow && MayPress("the friend's A (ready)", ScreenUp))
        {
            foreach (Wait wait in FriendTap(session, PadButtons.A))
            {
                yield return wait;
            }

            yield return Soft(() => SeatOf(open, friend, out _, out bool ready) && ready, ControllerAnswerSeconds, "the friend ready");
        }

        // The host: A to ready (when not), then A to save once both teams are ready (the screen's own rule).
        bool saved = false;
        for (int press = 0; moved && press < 3 && ScreenUp(); press++)
        {
            bool hostReady = SeatOf(open, host, out _, out bool ready) && ready;
            if (hostReady && !open.AreBothTeamsReady)
            {
                yield return Soft(() => open.WasCollected || open.AreBothTeamsReady, ControllerAnswerSeconds, "both teams ready");
            }

            if (!MayPress(hostReady ? "the host's A (save)" : "the host's A (ready)", ScreenUp))
            {
                break;
            }

            foreach (Wait wait in Tap(PadButtons.A))
            {
                yield return wait;
            }

            yield return Soft(() => !ScreenUp(), hostReady ? 3 : 0.8, hostReady ? "the screen saved and gone" : "the host ready");
            saved = !ScreenUp();
        }

        if (!saved && ScreenUp())
        {
            StepNote($"campaign: {CampaignTour.SidesKey}=on: not saved ({ControllerScreenLine()}); the screen left with nothing applied (its own two steps)");
            open.SetResult(false);
            open.FinishExitMenu();
            yield return Soft(() => !ScreenUp(), 3, "the controller screen gone");
        }

        yield return Seconds(0.5);
        int remembered = RememberedSide(friend);
        bool onList = OnTeamList(friend, out int count);
        StepNote(CampaignTour.SidesLine(friend, wanted, moved, saved, RememberedText(friend, out _), count, onList));
        if (wanted == CampaignTour.SideCenter)
        {
            foreach (Wait wait in Snap("sides_saved", 0))
            {
                yield return wait;
            }
        }

        if (CampaignTour.SidesProblem(wanted, moved, saved, remembered, onList) is { } problem)
        {
            Fail(problem);
        }
    }
}
#endif
