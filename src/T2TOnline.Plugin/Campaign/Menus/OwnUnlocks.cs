#if HAS_INTEROP
using System;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Progress;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's own new-unlocks screen at the end of a host's run that the host's game shows none for (the decision and its
/// time limits: Core's <see cref="OwnUnlocksScreen"/>). The game shows its new-unlocks menu after a run's end only when that
/// run unlocked something it shows on the profile that played it (<c>UnlocksMenuStep</c> → <c>UnlockUIHelper.ShowPermanentUnlocks</c>
/// → <c>ShowPermanentUnlockMenu</c>, which skips an empty list), and its run-end menu's exit says which beforehand: the
/// loading transition when none follows, the progress one when it does (<c>RunEndMenu.Hide</c>, by
/// <c>HasVisiblePermanentUnlocks</c>). A friend whose own profile unlocked something in the run (shared progress at its own
/// thresholds, D42; <see cref="UnlockViews"/>) sees it as a couch player of that profile would: the copy of the host's
/// run-end menu exits with the progress transition, and this game's own <c>PermanentUnlockMenu</c> opens as the game opens
/// it (<c>UIModule.ShowMenuAsync</c>, its data from the game's own converter over this player's list), driven by the
/// friend's own pad, keyboard and mouse: their device is lent back to this game meanwhile (<see cref="LendDevice"/>, the
/// session's own lend) and the friend UI lock lets only this menu's input through. The friend's way home
/// (<c>TitleFollower</c>) waits for its close, then catches up with the host's current screen; the host's game never
/// waits. A run-end close this PC didn't see (a rejoin) decides as the way home begins instead. A session's end, the host
/// going on to its next game, its new-unlocks menu coming after all, or a screen that never shows ends it at once, its
/// device and lock given back. When the host's game shows its menu, the friend's copy shows this player's own (never a
/// second screen). Game thread only; never throws (a failure is logged, and the way home goes on).
/// </summary>
internal static class OwnUnlocks
{
    /// <summary>The game's new-unlocks menu's definition name (the id the game shows it by; the host's log names it as it opens).</summary>
    private const string MenuId = "PermanentUnlockMenu";

    /// <summary>How long a screen given up before it showed is looked for, to close it should it show after all.</summary>
    private const double LateCloseSeconds = 10;

    /// <summary>The longest the screen waits for the copy of the host's run-end menu to finish closing.</summary>
    private const double CopyCloseSeconds = 3;

    /// <summary>The longest the way home waits for the host's run-end menu's close, whose exit decides, when it came first.</summary>
    private const long RunEndCloseWaitMicros = 5_000_000;

    /// <summary>The longest the screen holds the way home whatever happens (its own limits, and a minute more): the way home's watchdog's.</summary>
    private const double MaxHoldSeconds = (OwnUnlocksScreen.OpenTimeoutMicros + OwnUnlocksScreen.MaxOpenMicros) / 1_000_000.0 + 60;

    private static OwnUnlocksScreen _screen = new();
    private static PermanentUnlockMenu.Metadata? _request;
    private static PermanentUnlockMenu? _menu;
    private static AbstractMenu? _closingCopy;
    private static string _device = string.Empty;
    private static uint _runEndSerial;
    private static bool _decided;
    private static bool _pending;
    private static bool _lent;
    private static bool _seenUp;
    private static bool _sawHostHome;
    private static long _waitSince = -1;
    private static double _pendingSince;
    private static double _holdSince;
    private static int _frame = -1;
    private static PermanentUnlockMenu.Metadata? _lateRequest;
    private static double _lateUntil;
    private static int _errors;

    /// <summary>
    /// Lends the friend's device back to this game for the screen: true, with words for the log, when it's lent (or this game
    /// has it already); false, with why, when it can't be. Set by the online session.
    /// </summary>
    public static Func<(bool Lent, string Device)>? LendDevice { get; set; }

    /// <summary>The screen closed (why): the friend's device goes back to the session, if its play period still has it. Set by the online session.</summary>
    public static Action<string>? GiveDeviceBack { get; set; }

    /// <summary>True while this PC follows the host's game in a play period (the host not back at its lobby, the link up). Set by the online session.</summary>
    public static Func<bool>? HostPlaying { get; set; }

    /// <summary>True while the host is back at its lobby and this PC is on its way home. Set by the online session.</summary>
    public static Func<bool>? HostAtLobby { get; set; }

    /// <summary>True while the way home waits for this player's own new-unlocks screen (about to open, asked for, or up).</summary>
    public static bool Holding => _pending || _screen.Holding;

    /// <summary>
    /// True while the screen holds the way home within its limits (<see cref="MaxHoldSeconds"/>): the way home's own watchdog
    /// counts from its close, and takes over if it ever held longer.
    /// </summary>
    public static bool HoldsWayHome => Holding && FrameClock.Seconds - _holdSince < MaxHoldSeconds;

    /// <summary>Following started: nothing of an earlier session is carried over.</summary>
    public static void Start()
    {
        Forget();
    }

    /// <summary>The host's run-end menu at a run's end came up here (<paramref name="serial"/>, its copy's): that run's end has its own turn.</summary>
    public static void RunEnded(uint serial)
    {
        if (Holding || !_screen.NextRunEnd())
        {
            return;
        }

        _runEndSerial = serial;
        _decided = false;
        _waitSince = -1;
        _sawHostHome = false;
    }

    /// <summary>The host's new-unlocks menu came up here: one of this player's own still to come or up gives way (never two screens).</summary>
    public static void HostScreenOpened()
    {
        if (Holding)
        {
            Closed("the host's new-unlocks menu came after all; this player's own show on its copy", closeMenu: true);
        }

        _decided = true;
    }

    /// <summary>
    /// The copy of the host's run-end menu at a run's end (<paramref name="serial"/>) closes with the host's exit
    /// (<paramref name="hostExit"/>; <paramref name="copy"/>, the copy's menu): when that exit says no new-unlocks menu
    /// follows on the host's screen, this player's own is decided now, and when it shows, the copy exits with the game's
    /// progress transition before it, as the game's own run-end menu does. Returns the transition to show. Never throws.
    /// </summary>
    public static MenuTransition RunEndClosing(uint serial, MenuTransition hostExit, AbstractMenu? copy)
    {
        try
        {
            if (serial == 0 || serial != _runEndSerial)
            {
                return hostExit;
            }

            _runEndSerial = 0;
            if (OwnUnlocksScreen.HostShowsNone(hostExit) && Holding)
            {
                // This player's own screen came first (the close was late here): the copy goes under it, with no transition over it.
                return MenuTransition.None;
            }

            if (!OwnUnlocksScreen.HostShowsNone(hostExit) || _decided || _screen.State != OwnUnlocksState.Idle)
            {
                return hostExit;
            }

            long now = FrameClock.NowMicros;
            _waitSince = now;
            OwnUnlocksChoice choice = Decide(now);
            if (choice == OwnUnlocksChoice.GoHome)
            {
                return hostExit;
            }

            // Shown once the copy has closed (Update), or decided there once shared progress already here has applied.
            _pending = true;
            _pendingSince = FrameClock.Seconds;
            _holdSince = _pendingSince;
            _closingCopy = copy;
            return OwnUnlocksScreen.ExitBefore(hostExit, choice);
        }
        catch (Exception ex)
        {
            Fail("deciding on your new-unlocks screen", ex);
            return hostExit;
        }
    }

    /// <summary>
    /// The way home, each step before its load: true while it must wait for this player's own new-unlocks screen (about to
    /// open, asked for, or up). A run's end this PC didn't see close here (a rejoin after it) is decided now. Never throws.
    /// </summary>
    public static bool AtWayHome()
    {
        try
        {
            if (Holding)
            {
                // Stepped here too: the way home must never wait on a screen nothing moves on.
                Update();
                return Holding;
            }

            if (_decided)
            {
                return false;
            }

            long now = FrameClock.NowMicros;
            if (_waitSince < 0)
            {
                _waitSince = now;
            }

            if (_runEndSerial != 0 && now - _waitSince < RunEndCloseWaitMicros)
            {
                return true; // the host's run-end menu's close (its exit decides) is still on its way here
            }

            switch (Decide(now))
            {
                case OwnUnlocksChoice.Wait:
                    return true;
                case OwnUnlocksChoice.GoHome:
                    return false;
                default:
                    // The host's run-end menu's exit may have left its loading transition up here: this screen comes before that load.
                    FriendTransitions.TakeDownLoading("this player's own new-unlocks screen comes first");
                    return Open(now);
            }
        }
        catch (Exception ex)
        {
            Fail("deciding on your new-unlocks screen", ex);
            return false;
        }
    }

    /// <summary>
    /// Every frame of a session's following: a screen decided at the run-end menu's close opens once the copy has closed
    /// (or is decided once shared progress has applied); the open one's time limits, the host starting its next game
    /// meanwhile, and a menu gone without its close being seen; a screen given up before it showed, looked for a few
    /// seconds. A few comparisons when idle. Never throws.
    /// </summary>
    public static void Update()
    {
        if (_lateRequest != null && FrameClock.Seconds > _lateUntil)
        {
            _lateRequest = null;
        }

        if (!Holding || _frame == Time.frameCount)
        {
            return;
        }

        try
        {
            _frame = Time.frameCount;
            long now = FrameClock.NowMicros;
            _sawHostHome |= HostAtLobby?.Invoke() == true;
            if (_sawHostHome && HostPlaying?.Invoke() == true)
            {
                // The host started its next game before this one got home: it follows at once.
                Closed("the host started its next game", closeMenu: true);
                return;
            }

            if (_pending)
            {
                StepPending(now);
                return;
            }

            Tick(now);
        }
        catch (Exception ex)
        {
            Fail("following your new-unlocks screen", ex);
        }
    }

    /// <summary>A menu got its data: true when it's this player's own new-unlocks screen (it's up from now on).</summary>
    public static bool Bound(AbstractMenu menu, Il2CppSystem.Object data)
    {
        try
        {
            if (_lateRequest != null && data.Pointer == _lateRequest.Pointer)
            {
                // Given up before it showed: nothing would answer it, so it goes again at once.
                _lateRequest = null;
                ModLog.Info("Menus: this player's own new-unlocks screen showed after it had been given up; closed.");
                CloseMenu(menu.TryCast<PermanentUnlockMenu>());
                return true;
            }

            if (_request == null || data.Pointer != _request.Pointer)
            {
                return false;
            }

            long now = FrameClock.NowMicros;
            _menu = menu.TryCast<PermanentUnlockMenu>();
            if (_screen.Up(now))
            {
                // As the host's game takes the run-end menu's progress transition down when its next menu opens.
                FriendTransitions.NextScreen();
                ModLog.Info($"Menus: this player's own new-unlocks screen is up after {Seconds(_screen.HeldMicros(now))}; {_device}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Fail("binding your new-unlocks screen", ex);
            return false;
        }
    }

    /// <summary>A menu is closing: true when it's this player's own new-unlocks screen (its own close, past its last unlock).</summary>
    public static bool Hiding(AbstractMenu menu)
    {
        if (_menu == null || _menu.WasCollected || menu.Pointer != _menu.Pointer)
        {
            return false;
        }

        Closed("its own close", closeMenu: false);
        return true;
    }

    /// <summary>
    /// Ends the screen now (<paramref name="why"/>): closed with the menu's base close if it's up (closed when it shows, if it
    /// was only asked for), the device and lock given back, and the way home goes on. Nothing happens unless it holds the
    /// way home. Never throws.
    /// </summary>
    public static void Abort(string why)
    {
        if (Holding)
        {
            Closed(why, closeMenu: true);
        }
    }

    /// <summary>
    /// Following stopped (<paramref name="reason"/>): a screen still holding the way home ends (<paramref name="gameClosing"/>:
    /// no game calls, only forgotten), and everything is forgotten.
    /// </summary>
    public static void Stop(string reason, bool gameClosing)
    {
        if (Holding)
        {
            if (gameClosing)
            {
                ModLog.Info($"Menus: this player's own new-unlocks screen was up as the game closed ({reason}).");
            }
            else
            {
                Abort($"the session ended: {reason}");
            }
        }

        if (_screen.Shown > 0)
        {
            ModLog.Info($"Menus: this session showed this player's own new-unlocks screen {_screen.Shown} time(s), at run's ends {LogNames.HostTag()}'s game showed none for.");
        }

        Forget();
    }

    /// <summary>The decision, with what this PC knows now; a screen decided against is done, said when there's something to say.</summary>
    private static OwnUnlocksChoice Decide(long now)
    {
        bool ready = MenuPatches.UnlocksReady && MenuViews.Instance is { Following: true, Off: false } && MenuGame.Module() != null;
        var facts = new OwnUnlocksFacts(UnlockViews.RunEndedHere, UnlockViews.HostScreenCame, UnlockViews.OwnCount,
            ProgressModule.Friend?.Waiting ?? 0, now - _waitSince, ready);
        OwnUnlocksChoice choice = OwnUnlocksScreen.Decide(facts, out string why);
        if (choice == OwnUnlocksChoice.Wait)
        {
            return choice;
        }

        _decided = true;
        if (choice == OwnUnlocksChoice.GoHome)
        {
            _screen.Finish();
            if (why.Length > 0)
            {
                ModLog.Info($"Menus: {LogNames.HostTag()}'s game showed no new-unlocks menu at its run's end, and {why}: no new-unlocks screen here, as in the game " +
                    $"({UnlockViews.OwnText()}).");
            }
        }

        return choice;
    }

    /// <summary>Decided at the run-end menu's close: decided now if it waited for shared progress, then opened once the copy has closed.</summary>
    private static void StepPending(long now)
    {
        if (!_decided)
        {
            switch (Decide(now))
            {
                case OwnUnlocksChoice.Wait:
                    return;
                case OwnUnlocksChoice.GoHome:
                    _pending = false;
                    _closingCopy = null;
                    return;
                default:
                    // The copy exited with the host's loading transition (it waited): this screen comes before that load.
                    FriendTransitions.TakeDownLoading("this player's own new-unlocks screen comes first");
                    break;
            }
        }

        // As the game opens its new-unlocks menu once its run-end menu has hidden.
        AbstractMenu? copy = _closingCopy;
        bool copyUp = copy != null && !copy.WasCollected && !(copy.IsHidden && !copy.IsHiding) && copy.gameObject.activeInHierarchy;
        if (copyUp && FrameClock.Seconds - _pendingSince < CopyCloseSeconds)
        {
            return;
        }

        _pending = false;
        _closingCopy = null;
        Open(now);
    }

    /// <summary>Opens the screen: its data, the device lent, the lock lifted for it, then the game's own open. False when it can't (logged).</summary>
    private static bool Open(long now)
    {
        string own = UnlockViews.OwnText();
        PermanentUnlockMenu.Metadata? request = UnlockViews.OwnScreenRequest(out string problem, out bool none);
        UIModule? ui = MenuGame.Module();
        if (request == null || ui == null)
        {
            _screen.Finish();
            ModLog.Info(none
                ? $"Menus: {LogNames.HostTag()}'s game showed no new-unlocks menu at its run's end; this player's own new unlocks of the run ({own}) are all ones the game keeps off that menu: no screen here."
                : $"Menus: {LogNames.HostTag()}'s game showed no new-unlocks menu at its run's end; this player's own new unlocks of the run ({own}) can't show on this game's own " +
                  $"({(request == null ? problem : "the game's UI isn't ready")}); they're on this profile, each toasted when it came.");
            return false;
        }

        (bool lent, string device) = LendDevice?.Invoke() ?? (false, "this PC isn't in a session now");
        if (!lent)
        {
            _screen.Finish();
            ModLog.Warn($"Menus: this player's own new unlocks of the run ({own}) can't show on this game's own new-unlocks screen: {device}. They're on this profile, each toasted when it came.");
            return false;
        }

        _lent = true;
        _device = device;
        _seenUp = false;
        _holdSince = FrameClock.Seconds;
        _screen.TryStart(now);
        _request = request;
        ModLog.Info($"Menus: {LogNames.HostTag()}'s game shows no new-unlocks menu at its run's end (its own profile unlocked nothing new it shows); this player's own new unlocks " +
            $"of the run ({own}) show on this game's own new-unlocks screen, and the way home waits for it ({device}; {LogNames.HostTag()}'s game doesn't wait).");
        FriendUi.SuspendForOwnMenu("your new-unlocks screen", navigation: true, Owns, TakesBack, KeepsCursorFrom);
        try
        {
            // The game's own open, with the concrete menu class (the generic is called, never patched; D43).
            _ = ui.ShowMenuAsync<PermanentUnlockMenu>(MenuId, request);
        }
        catch (Exception ex)
        {
            Closed($"the game couldn't open it ({ModLog.SafeErrorText(ex)})", closeMenu: false);
            return false;
        }

        return true;
    }

    /// <summary>Its time limits, and a menu gone without its close being seen.</summary>
    private static void Tick(long now)
    {
        switch (_screen.Update(now))
        {
            case OwnUnlocksTick.OpenTimedOut:
                Closed($"it didn't show within {OwnUnlocksScreen.OpenTimeoutMicros / 1_000_000} s", closeMenu: true);
                return;
            case OwnUnlocksTick.OpenTooLong:
                Closed($"it was up {OwnUnlocksScreen.MaxOpenMicros / 60_000_000} minutes", closeMenu: true);
                return;
        }

        if (_screen.State != OwnUnlocksState.Open)
        {
            return;
        }

        // Bound at its data, before its show: gone only once it has been up.
        if (_menu == null || _menu.WasCollected || (_seenUp && _menu.IsHidden))
        {
            Closed("it's gone", closeMenu: false);
            return;
        }

        _seenUp |= !_menu.IsHidden;
    }

    /// <summary>
    /// The screen's end: its state done, the lock whole again, the device back, this player's new unlocks of the run behind
    /// it (as at the close of the host's menu), then the menu's base close when asked (<paramref name="closeMenu"/>) and it's
    /// still up, or a late close armed when it was only asked for. Each step on its own. Never throws.
    /// </summary>
    private static void Closed(string why, bool closeMenu)
    {
        long now = FrameClock.NowMicros;
        bool pending = _pending;
        bool wasUp = _screen.State == OwnUnlocksState.Open;
        long held = _screen.HeldMicros(now);
        _pending = false;
        _closingCopy = null;
        _decided = true;
        _screen.Finish();
        PermanentUnlockMenu? menu = _menu;
        PermanentUnlockMenu.Metadata? request = _request;
        _menu = null;
        _request = null;
        if (pending)
        {
            ModLog.Info($"Menus: this player's own new-unlocks screen, about to open, won't ({why}); the way home goes on.");
            return;
        }

        Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu(why));
        if (_lent)
        {
            _lent = false;
            Step("giving your device back to the session", () => GiveDeviceBack?.Invoke(why));
        }

        int ledger = 0;
        Step("clearing this player's new unlocks of the run", () => ledger = UnlockViews.OwnScreenClosed());
        if (closeMenu)
        {
            if (menu != null && !menu.WasCollected)
            {
                Step("closing your new-unlocks screen", () => CloseMenu(menu));
            }
            else if (!wasUp && request != null)
            {
                _lateRequest = request;
                _lateUntil = FrameClock.Seconds + LateCloseSeconds;
            }
        }

        ModLog.Info($"Menus: this player's own new-unlocks screen {(wasUp ? "closed" : "was given up")} after {Seconds(held)} ({why})" +
            $"{(ledger > 0 ? $"; the {ledger} kept for it in this PC's progress ledger are cleared" : string.Empty)}; the way home goes on, to {LogNames.HostTag()}'s current screen.");
    }

    /// <summary>The menu's base close, what its own close stops stopped first (as a friend's copy of the host's closes).</summary>
    private static void CloseMenu(PermanentUnlockMenu? menu)
    {
        if (menu == null || menu.WasCollected || menu.IsHidden || menu.IsHiding)
        {
            return;
        }

        UnlockViews.BeforeClose(menu);
        _ = menu.Hide_Internal(true);
    }

    /// <summary>True when <paramref name="target"/>'s click or shortcut is this screen's own, while it takes input.</summary>
    private static bool Owns(Component? target) =>
        target != null && _menu != null && !_menu.WasCollected && target.transform.IsChildOf(_menu.transform) && TakesInput();

    /// <summary>True while the screen takes input: a Back goes to it, as in the game.</summary>
    private static bool TakesBack() => _menu != null && !_menu.WasCollected && TakesInput();

    /// <summary>True when the friend's cursor stays on this screen rather than go to <paramref name="target"/> (the host's cursor mirrored under it).</summary>
    private static bool KeepsCursorFrom(Component target) =>
        _menu != null && !_menu.WasCollected && !target.transform.IsChildOf(_menu.transform) && TakesInput();

    /// <summary>True when the game's UI lets the screen take input now (nothing above it blocks).</summary>
    private static bool TakesInput()
    {
        UIModule? ui = MenuGame.Module();
        return ui != null && _menu != null && ui.CanProcessInput(_menu);
    }

    private static void Forget()
    {
        _screen = new OwnUnlocksScreen();
        _request = null;
        _menu = null;
        _closingCopy = null;
        _lent = false;
        _pending = false;
        _decided = false;
        _seenUp = false;
        _sawHostHome = false;
        _runEndSerial = 0;
        _waitSince = -1;
        _device = string.Empty;
    }

    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Fail(what, ex);
        }
    }

    private static void Fail(string what, Exception ex)
    {
        if (_errors++ < 3)
        {
            ModLog.Warn($"Menus: {what} failed ({ModLog.SafeErrorText(ex)}); the way home goes on.");
        }

        if (Holding)
        {
            // Nothing may hold the way home after a failure: the screen ends, its device and lock given back.
            Closed("an error", closeMenu: true);
        }
    }

    private static string Seconds(long micros) => string.Create(CultureInfo.InvariantCulture, $"{micros / 1_000_000.0:0.0} s");
}
#endif
