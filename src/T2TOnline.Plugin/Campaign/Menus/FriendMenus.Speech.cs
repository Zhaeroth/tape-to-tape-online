#if HAS_INTEROP
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the v1's event work: the speech mirror's per-frame step
/// and its speakers' binds and closes (<see cref="SpeechMirror"/>), the event cards that wait for the host's
/// (<see cref="HoldCards"/>), and the event screens a map trip's run load waits for (<see cref="HoldsRunLoad"/>).
/// </summary>
internal sealed partial class FriendMenus
{
    private static readonly MenuTarget CardsTarget = MenuTarget.AtItem(EventCapture.StateGroup, EventCapture.Cards);

    // The event views whose cards wait for the host's (by serial), since when; the event screens this PC closed, hiding.
    private readonly Dictionary<uint, double> _cardsHeld = new();
    private readonly List<Closing> _closingEvents = new();
    private bool _cardsDriving;

    /// <summary>
    /// Friend: true while the host's newest run should wait before going in, with which screen (<paramref name="what"/>):
    /// a real event screen is up here, or one this PC closed is still hiding (for at most
    /// <see cref="RunLoadHold.ClosingSeconds"/>). The host's game saves the run during an event's exit, before its menu
    /// hides, so the run and map come a moment before the close (<see cref="Scenes.MapTrip"/>).
    /// </summary>
    public bool HoldsRunLoad(out string what)
    {
        foreach (View view in _views.Values)
        {
            if (view.Type == MenuPayloads.EventChoice && view.Mode == Mode.Real && view.Menu is { } open && !open.IsHidden)
            {
                what = view.LogId;
                return true;
            }
        }

        double now = FrameClock.Seconds;
        for (int i = _closingEvents.Count - 1; i >= 0; i--)
        {
            Closing closing = _closingEvents[i];
            if (closing.Menu == null || closing.Menu.IsHidden || now - closing.Since > RunLoadHold.ClosingSeconds)
            {
                _closingEvents.RemoveAt(i);
                continue;
            }

            what = closing.Id;
            return true;
        }

        what = string.Empty;
        return false;
    }

    /// <summary>
    /// Friend: the game shows or puts away <paramref name="menu"/>'s cards (<c>EventChoiceMenu.SetChoicesShown</c>). True
    /// (the call is refused) for this PC's real event view's own intro reveal while the host's cards aren't in
    /// (<see cref="CardsGate.Holds"/>): its intro has no NPC lines of its own to wait for, the host's play beside it. Held
    /// cards come in with the host's (<see cref="ReleaseCards"/>).
    /// </summary>
    public bool HoldCards(EventChoiceMenu menu, bool shown, bool instant)
    {
        if (_cardsDriving || !shown || instant || !MenuPatches.CardsReady)
        {
            return false;
        }

        View? view = null;
        foreach (View each in _views.Values)
        {
            if (each.Type == MenuPayloads.EventChoice && each.Mode == Mode.Real && each.Menu is { } open && open.Pointer == menu.Pointer)
            {
                view = each;
                break;
            }
        }

        if (view == null || !_mirror.TryGet(view.Serial, out MirroredMenu mirrored) ||
            !CardsGate.Holds(shown, instant, menu.m_IsPlayingIntro, _cardsDriving, HostCards(mirrored), HostIntro(mirrored)))
        {
            return false;
        }

        if (!_cardsHeld.ContainsKey(view.Serial))
        {
            _cardsHeld[view.Serial] = FrameClock.Seconds;
            Log($"Menus: {view.LogId}: the cards wait for {LogNames.HostTag()}'s (the NPC is still speaking there).");
        }

        return true;
    }

    /// <summary>The host's cards state (<see cref="EventCapture.Cards"/>: 1 in, 0 not), or -1 when it hasn't said.</summary>
    private static int HostCards(MirroredMenu menu) => menu.Values.TryGetValue(CardsTarget, out int cards) ? cards : -1;

    /// <summary>True while <paramref name="view"/>'s cards wait for the host's (its intro isn't over here until they're in).</summary>
    private bool CardsHeld(View view) => _cardsHeld.ContainsKey(view.Serial);

    /// <summary>
    /// An event view's held cards come in once the host's are, or its intro is over, or it said nothing more, or
    /// <see cref="CardsGate.MaxHoldSeconds"/> passed: the menu's own reveal, made by the mod (the same tween).
    /// </summary>
    private void ReleaseCards(View view, MirroredMenu menu, EventChoiceMenu open)
    {
        if (!_cardsHeld.TryGetValue(view.Serial, out double since))
        {
            return;
        }

        double held = FrameClock.Seconds - since;
        CardsRelease why = CardsGate.Release(HostCards(menu), HostIntro(menu), held);
        if (why == CardsRelease.Hold)
        {
            return;
        }

        _cardsHeld.Remove(view.Serial);
        _cardsDriving = true;
        bool shown;
        try
        {
            shown = Try(view, "showing the cards as the host's came in", () => open.SetChoicesShown(true, false));
        }
        finally
        {
            _cardsDriving = false;
        }

        string after = why switch
        {
            CardsRelease.HostCards => $"as on {LogNames.HostTag()}'s screen",
            CardsRelease.HostIntroOver => $"{LogNames.HostTag()}'s intro is over",
            CardsRelease.HostSilent => $"{LogNames.HostTag()}'s screen no longer says",
            _ => $"{LogNames.HostTag()}'s didn't come within {CardsGate.MaxHoldSeconds:0} s",
        };
        if (shown)
        {
            Log(string.Create(CultureInfo.InvariantCulture, $"Menus: {view.LogId}: the cards are in here ({after}; held {held:0.0} s)."));
        }
    }

    /// <summary>
    /// The host's event started its exit: as its <c>ExitAsync</c> does, this PC's
    /// cards go (<c>SetChoicesShown</c>, unless a pick or the procedure screen already took them) and its NPC says
    /// goodbye with the game's own <c>PlayBye</c>. Nothing here waits for it: the copy closes with the host's close,
    /// which comes after the host's own goodbye. Once a bind. A failure is the old look: the NPC stands still, then goes with
    /// the close.
    /// </summary>
    private void NpcGoodbye(View view, EventChoiceMenu open)
    {
        if (view.GoodbyeSaid)
        {
            return;
        }

        view.GoodbyeSaid = true;

        // While the real procedure screen still holds the event screen, its close brings back everything it hid, the cards among
        // them (RestoreHost): a call now would do nothing that lasts, and they go after that close, once (NurseCardsAfterClose).
        bool screenHolds = view.NurseReal is { Fallen: false, CardsAway: false } nurse && (nurse.Screen is not null || nurse.Closing is not null);
        if (view.IntroOver && !view.Swapping && !view.CardsHidden && !screenHolds && Try(view, "putting the cards away at the event's exit", () => open.SetChoicesShown(false, false)))
        {
            view.CardsHidden = true;
        }

        bool played = false;
        if (Try(view, "playing the NPC's goodbye", () =>
        {
            if (open.GetCurrentNpc() is { } npc && npc != null)
            {
                _ = npc.PlayBye();
                played = true;
            }
        }) && played)
        {
            Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s event exits; this PC's NPC says goodbye with it (the game's own), and the screen closes with {LogNames.HostTag()}'s.");
        }
    }

    /// <summary>
    /// Every frame while following: the speech mirror's step (each speaker follows the host's; closes' guards), and the
    /// curses of a closed Canada curse menu destroyed once it has hidden.
    /// </summary>
    private void SpeechTick()
    {
        SpeechMirror.FriendTick(_mirror);
        CanadaViews.ReleaseTick();
    }

    /// <summary>A view's real menu is bound: its own speakers follow the host's (a Canada curse menu's two).</summary>
    private static void BindSpeech(View view, AbstractMenu menu) => SpeechMirror.FriendBound(view.Serial, menu);

    /// <summary>
    /// A view's real menu is closing here: its speakers' bubbles are cleared (and guarded a moment), its held cards
    /// forgotten, and an event screen noted as hiding (a map trip's run load waits for it).
    /// </summary>
    private void SpeechClosing(View view, AbstractMenu? open)
    {
        SpeechMirror.FriendClosed(view.Serial);
        _cardsHeld.Remove(view.Serial);
        if (open != null && view.Mode == Mode.Real && view.Type == MenuPayloads.EventChoice && !open.IsHidden)
        {
            _closingEvents.RemoveAll(c => c.Menu == null || c.Menu.Pointer == open.Pointer);
            _closingEvents.Add(new Closing(open, view.LogId, FrameClock.Seconds));
        }
    }

    /// <summary>Following started or stopped: the speakers, held cards and closing screens are forgotten.</summary>
    private void SpeechReset(bool starting, bool gameClosing)
    {
        if (starting)
        {
            SpeechMirror.FriendStart();
        }
        else
        {
            SpeechMirror.ClearFriend(gameClosing);
        }

        _cardsHeld.Clear();
        _closingEvents.Clear();
        _cardsDriving = false;
        NodeRewardsMirror.ClearFriend();
        CanadaViews.ClearReleases();
    }

    /// <summary>An event screen this PC closed, until it has hidden.</summary>
    private readonly record struct Closing(AbstractMenu Menu, string Id, double Since);
}
#endif
