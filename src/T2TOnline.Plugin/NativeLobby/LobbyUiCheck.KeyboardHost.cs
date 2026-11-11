#if HAS_INTEROP
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// The campaign tours' keyboard host step (Core's CampaignTour.KeyboardHostStep, after cp_first, in the plan only with
// kbhost=on): with the host on the keyboard alone, the game asks at a friend's every menu press whether they may take the menus, and HostDecides answers no (the game
// would answer yes, since a keyboard player never goes on its controlling list).
//
// Where the game asks. Only where one player has the menus: the map with nothing over it, its nodes, the one-player menus.
// A menu whose prefab lets any player drive it (T2TMenu._canBeControlledByAnyPlayer: the map's own menu, the pause menu,
// the lineup, Settings, the glossary, the unlocks) turns the game's any-player control on while it is up (T2TMenu.Show):
// UIInputSource.IsControlledBy then answers yes for every player before it asks anything, the controlling list is emptied,
// and SingleControllingPlayer reads nobody with more than one player tracked. There the rule is never asked and a friend's
// press moves the cursor, on a pad host and a keyboard host alike, as on a couch. So the friend's press is made on the map
// itself, never in its menu.
//
// How it is safe. A tour's host is its pad, which the game does list, so the step sets the pad aside for a moment
// (HostDecides.TourPadSetAside): the rule then reads the host's own player as the keyboard alone, as on a keyboard host's
// PC, while the game's list still holds the pad. In that state the game's own ask answers a friend no as well, without side
// effects (UIInputSource.SetControllerIfNone returns false at once when its controlling list holds anyone), so the rule's
// refusal changes nothing the steps after this one could meet: the one-PC friend's d-pad on the map is refused either way.
// The press is made only in that state (Core's CampaignTour.KeyboardHostHeld: hosting, the map bare, any-player control
// off, the pad the one controlling player; read again at the press itself), and only once the map's cursor has stood
// still, so a move of the map's own isn't taken for the friend's. A refusal with nothing pressed fails the step: the game
// then asks about the friend by itself, and the refusals under the press no longer show that the press reached the ask.
//
// What it does. Start by the pad opens the map's menu; the step writes who has the menus there (any player) and closes it
// by B. The pad's Start is itself an ask (the map bar's entry takes it from the controlling player only, and with nobody on
// the list the game lists the pad at that press), so the pad is the player the menu saves as it shows and the one the game
// hands the menus back to as it closes (T2TMenu.ReleaseInputOwnership). Then, on the map, the pad set aside: the rule's
// refusals counted for a moment with nothing pressed (the asks the game makes by itself), then the friend's d-pad down,
// the rule's refusals and the map's cursor read before and after.
// The step never marks the run broken and takes no picture (flags.tsv keeps its rows); a menu it leaves open is the one
// cp_continue opens itself (its MapMenu takes an open map menu as its own).
internal static partial class LobbyUiCheck
{
    private const double KeyboardHostPressSeconds = 0.2;
    private const double KeyboardHostSettleSeconds = 0.6;

    /// <summary>The map's cursor must have stood still this long first: longer than the count with nothing pressed and the press with its settle, which follow.</summary>
    private const double KeyboardHostStillSeconds = 2.0;

    /// <summary>The longest wait for it: the map's arrival after a match (its sound, the beaten node, the next node taken) is a few seconds.</summary>
    private const double KeyboardHostStillWaitSeconds = 15;

    private static IEnumerable<Wait> StepCampaignKeyboardHost()
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
        yield return Soft(() => { Watch(); return MapFree() && MapStartHeldNow() == null; }, MapInputSeconds, "the map, nothing over it, its menu input ready");
        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 10, "the friend Here");
        if (!MapFree() || FriendSeat().Stage != SeatStage.Here || session.TourFriendGamePlayer < 0 || _walk == null)
        {
            Problem($"a keyboard host: not tried: the map {(MapFree() ? "free" : "not free")}, the friend {FriendSeat().Stage} (game player {session.TourFriendGamePlayer}), the tour's pad {(_walk == null ? "gone" : "there")}; {MapInputLine()}");
            yield break;
        }

        StepNote(CampaignTour.KeyboardHostControlLine("the map before its menu", AnyPlayerControl(), Controlling()));
        WorldMapMenu? menu = null;
        if (MayPress("Start on the map", MapFree))
        {
            foreach (Wait wait in Tap(PadButtons.Start))
            {
                yield return wait;
            }

            yield return Soft(() => (menu = Top()?.TryCast<WorldMapMenu>()) != null, 3, "the map's menu");
        }

        if (menu == null || menu.WasCollected)
        {
            Problem($"a keyboard host: not tried: Start by the pad opened no map menu ({MapInputLine()})");
            yield break;
        }

        yield return Seconds(0.8); // its intro: a press in it is ignored meanwhile
        WorldMapMenu open = menu;
        bool MenuUp() => !open.WasCollected && Top()?.Pointer == open.Pointer;
        StepNote(CampaignTour.KeyboardHostControlLine("the map's menu up", AnyPlayerControl(), Controlling()));

        // The map's menu closed by B, as ScreenShowsTeam closes it; then the map's own input ready, as cp_continue finds it after cp_first.
        if (MenuUp() && MayPress("B on the map's menu", MenuUp))
        {
            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => { Watch(); return !MenuUp(); }, 3, "the map's menu closed");
        }

        if (MenuUp())
        {
            StepNote("a keyboard host: the map's menu is still up after B; cp_continue's own map menu press takes it from here");
        }

        double since = FrameClock.Seconds;
        yield return Soft(() => { Watch(); return MapStartHeldNow() == null; }, MapInputSeconds, "the map's own input ready again");
        StepNote(string.Create(CultureInfo.InvariantCulture, $"a keyboard host: the map's menu input {(MapStartHeldNow() is { } held ? $"still not ready ({held})" : "ready")} {FrameClock.Seconds - since:0.0} s after"));

        foreach (Wait wait in FriendPressesForAKeyboardHost(session))
        {
            yield return wait;
        }
    }

    /// <summary>
    /// On the map with nothing over it: its cursor still, the pad set aside, the rule's refusals counted with nothing pressed,
    /// then the one-PC friend's d-pad down; the rule's refusals and the map's cursor read before and after. Nothing is
    /// pressed unless the game's own ask would refuse the friend too (<see cref="CampaignTour.KeyboardHostHeld"/>). The
    /// pad and the friend's press are put back in a <c>finally</c>: at the step's end, a throw, or a stop's dispose.
    /// </summary>
    private static IEnumerable<Wait> FriendPressesForAKeyboardHost(OnlineSession session)
    {
        // The map's cursor still: the map's own arrival moves it (the next node taken after a match), and the menu's close puts it back on a node.
        long seen = CursorPointer();
        double seenAt = FrameClock.Seconds;
        bool Still()
        {
            long now = CursorPointer();
            if (now != seen)
            {
                seen = now;
                seenAt = FrameClock.Seconds;
            }

            return FrameClock.Seconds - seenAt >= KeyboardHostStillSeconds;
        }

        yield return Soft(() => { Watch(); return MapFree() && Still(); }, KeyboardHostStillWaitSeconds, "the map's cursor still, nothing over the map");

        UIInputSource? source = MenuGame.Module()?.InputSource;
        bool hosting = CampaignRelay.Current is { Hosting: true };
        StepNote(CampaignTour.KeyboardHostControlLine("the map after its menu", AnyPlayerControl(), Controlling()));
        string? heldBy = source == null ? "the game's UI input isn't there"
            : !Still() ? string.Create(CultureInfo.InvariantCulture, $"the map's cursor didn't stand still for {KeyboardHostStillSeconds:0.0} s (now on {Cursor()})")
            : CampaignTour.KeyboardHostHeld(hosting, MapFree(), AnyPlayerControl(), IsPadControlling());
        if (source == null || heldBy != null)
        {
            // Outside that one state the friend's press could be taken by the host's game: not made then.
            Problem($"a keyboard host: not tried: {heldBy} (hosting {hosting}, the map {(MapFree() ? "bare" : "not bare")}, any-player control {(AnyPlayerControl() ? "on" : "off")}, the UI's controlling player {Controlling()})");
            yield break;
        }

        string cursorBefore = Cursor();
        long cursorAt = CursorPointer();
        bool hostOnKeyboard;
        string host;
        int refusedIdle = 0;
        int refused;
        bool stayed;
        string? heldAtPress = null;
        HostDecides.TourPadSetAside = true;
        try
        {
            hostOnKeyboard = HostDecides.HostOnKeyboardNow(source, out host);
            int refusedBefore = HostDecides.Refusals;
            if (hostOnKeyboard)
            {
                // Nothing pressed, for as long as the press and its settle: the asks about the friend's player that come by themselves.
                yield return Seconds(KeyboardHostPressSeconds + KeyboardHostSettleSeconds);
                refusedIdle = HostDecides.Refusals - refusedBefore;
                cursorBefore = Cursor();
                cursorAt = CursorPointer();
                refusedBefore = HostDecides.Refusals;

                // The same state again at the press itself: a screen that came up meanwhile would take the friend's d-pad.
                heldAtPress = CampaignTour.KeyboardHostHeld(CampaignRelay.Current is { Hosting: true }, MapFree(), AnyPlayerControl(), IsPadControlling());
                if (heldAtPress == null)
                {
                    session.TourFriendPress(PadButtons.DPadDown);
                    yield return Seconds(KeyboardHostPressSeconds);
                    session.TourFriendPress(PadButtons.None);
                    yield return Seconds(KeyboardHostSettleSeconds);
                }
            }

            refused = HostDecides.Refusals - refusedBefore;
            stayed = CursorPointer() == cursorAt;
        }
        finally
        {
            session.TourFriendPress(PadButtons.None);
            HostDecides.TourPadSetAside = false;
        }

        if (heldAtPress != null)
        {
            Problem($"a keyboard host: not pressed: {heldAtPress} at the press (any-player control {(AnyPlayerControl() ? "on" : "off")}, the UI's controlling player {Controlling()})");
            yield break;
        }

        StepNote(CampaignTour.KeyboardHostLine(host, refused, refusedIdle, stayed, cursorBefore, Cursor()));
        if (CampaignTour.KeyboardHostProblem(hostOnKeyboard, refused, refusedIdle, stayed) is { } problem)
        {
            Problem(problem);
        }
    }

    /// <summary>The map loaded with nothing over it, no prompt of the mod's up: where the pad's Start opens the map's menu.</summary>
    private static bool MapFree() => WorldMap.MapLoaded && !TourHolds.ModPromptOpen && Top() == null;

    /// <summary>True while the game takes every player's menu press (a menu any player drives is up): the UI input source's own flag, read.</summary>
    private static bool AnyPlayerControl() => MenuGame.Module()?.InputSource is { m_CanAnyPlayerControl: true };
}
#endif
