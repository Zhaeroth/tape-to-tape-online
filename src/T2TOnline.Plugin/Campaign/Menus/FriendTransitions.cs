#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the game's menu transitions: the "Woosh" a host menu's own exit showed, shown here when this PC's copy of the
/// menu closes, in the game's order (<see cref="MenuExits"/>): after the copy has hidden (<see cref="AfterClose"/>), or first, the
/// copy closing once the transition's show is complete (<see cref="BeforeClose"/>); the loading transition before each of this PC's
/// own campaign scene loads, as the game's loaders show it (<see cref="LoadReady"/>); and the transition the old squad menu's own exit
/// shows (<see cref="OwnExit"/>). Each is shown with the game's own call (<see cref="WooshGame.Show"/>).
/// <para>
/// What takes each down. The loading one: the same game code as on the host (the loading screen, the map's appear, the
/// run-end menu's show), or its own hide at once when the load it was shown for is given up (<see cref="LoadGivenUp"/>: a
/// friend's match that can't load here). The on and back ones: on the host, mostly the title's <c>StartNewRun</c> (WooshProgress, WooshRegress),
/// which never runs on a friend; here, the host's next
/// screen binding (<see cref="NextScreen"/>), or this PC's screen settling once the show is complete
/// (<see cref="StrayTransition.SettleSeconds"/>). A guard (<see cref="StrayTransition"/>) takes down one that's still up
/// after its limits, and every one when following stops, each with its own <c>Hide</c>: a transition asked for just before
/// the session ended goes as soon as it has shown. Only transitions the views showed are touched.
/// </para>
/// Game thread only; never throws (failures are logged and the close or load goes ahead).
/// </summary>
internal static class FriendTransitions
{
    /// <summary>What a friend's load of the host's match is called here (<see cref="LoadReady"/>, <see cref="LoadGoesAhead"/>, <see cref="LoadGivenUp"/>).</summary>
    public const string HostMatch = "the host's match";

    /// <summary>How long a closing copy is waited for before its transition shows anyway.</summary>
    private const double HiddenWaitSeconds = 2.5;

    /// <summary>How long a transition's show is waited for before the close or load goes ahead anyway.</summary>
    private const double ShownWaitSeconds = 2;

    /// <summary>A transition that starts showing this long after it was asked for is the one asked for.</summary>
    private const double AdoptSeconds = 3;

    /// <summary>The old squad menu's own exit shows its transition within this long (its animations' waits, then its hide).</summary>
    private const double OwnExitSeconds = 10;

    /// <summary>How often the guard looks at this PC's screen (it finds the loading screen: not every frame).</summary>
    private const double GuardSeconds = 0.25;

    private const int MaxListening = 16;
    private const int MaxStrayWarnings = 3;

    private static readonly List<Request> Requests = new();
    private static readonly List<Up> Ups = new();
    private static readonly HashSet<IntPtr> Listening = new();
    private static readonly Dictionary<IntPtr, double> CompletedAt = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly LineBudget Lines = new("Menus (transitions)", 30);
    private static Il2CppSystem.Action<AbstractMenu>? _completed;
    private static Request? _load;
    private static double _ownExitUntil = double.NegativeInfinity;
    private static string _ownExitAbout = string.Empty;
    private static double _askedUntil = double.NegativeInfinity;
    private static double _nextScreenAt = double.NegativeInfinity;
    private static double _guardAt;
    private static bool _stopped;
    private static int _strays;
    private static bool _listenFailed;

    /// <summary>True while anything here waits or is watched (the per-frame work and the show hook have something to do).</summary>
    public static bool Busy
    {
        get
        {
            if (Requests.Count > 0 || Ups.Count > 0 || _load != null)
            {
                return true;
            }

            double now = FrameClock.Seconds;
            return now < _ownExitUntil || now < _askedUntil;
        }
    }

    /// <summary>Following started: nothing of an earlier session is carried over (one its stop couldn't take down goes now).</summary>
    public static void Start()
    {
        foreach (Up up in Ups)
        {
            _ = TakeDown(up.Menu);
        }

        // (An own exit still running from the last session keeps its watch: its transition can still show.)
        Requests.Clear();
        Ups.Clear();
        _load = null;
        _askedUntil = double.NegativeInfinity;
        _nextScreenAt = double.NegativeInfinity;
        _stopped = false;
        _strays = 0;
    }

    /// <summary>
    /// A copy of a host menu that hides first was closed (the base close): <paramref name="kind"/> shows once it has hidden,
    /// as the menu's own exit shows it after its hide.
    /// </summary>
    public static void AfterClose(MenuTransition kind, string about, AbstractMenu copy) =>
        Requests.Add(new Request(kind, about, FrameClock.Seconds) { Copy = copy });

    /// <summary>
    /// A copy of a host menu whose exit shows its transition first is closing: <paramref name="kind"/> shows now, and
    /// <paramref name="close"/> (the copy's base close) runs once its show is complete, as the menu's own exit waits for it.
    /// </summary>
    public static void BeforeClose(MenuTransition kind, string about, Action close)
    {
        // Listed before it's asked for: a transition the game keeps can show within the call (Showing adopts it then).
        var request = new Request(kind, about, FrameClock.Seconds) { Close = close };
        Requests.Add(request);
        if (!Ask(request))
        {
            Requests.Remove(request);
            Run(close, about);
        }
    }

    /// <summary>
    /// A copy that closes after <paramref name="seconds"/> (a reward menu behind its fade to black, as its own exit waits for
    /// it). Run at once when following stops, or when the host's next menu opens (<see cref="FlushTimedCloses"/>).
    /// </summary>
    public static void CloseAfter(double seconds, string about, Action close) =>
        Requests.Add(new Request(MenuTransition.None, about, FrameClock.Seconds) { Close = close, Until = FrameClock.Seconds + seconds });

    /// <summary>The host's next menu is opening here: a copy still waiting out its fade closes now (the game can't show two of a kind).</summary>
    public static void FlushTimedCloses()
    {
        for (int i = Requests.Count - 1; i >= 0; i--)
        {
            Request request = Requests[i];
            if (request is { Until: > 0, Copy: null, Close: not null })
            {
                Requests.RemoveAt(i);
                Run(request.Close, request.About);
            }
        }
    }

    /// <summary>The old squad menu's own exit started here: the transition its hide shows soon is watched like the others.</summary>
    public static void OwnExit(string about)
    {
        _ownExitUntil = FrameClock.Seconds + OwnExitSeconds;
        _ownExitAbout = about;
    }

    /// <summary>
    /// The host closed a copy still running its own exit: it's left to it, but if it hasn't started hiding by the time its
    /// exit must have (its exit's task faulted, say: nothing would take it off this PC's screen), <paramref name="close"/>
    /// (the base close) runs.
    /// </summary>
    public static void WatchOwnExit(AbstractMenu copy, string about, Action close) =>
        Requests.Add(new Request(MenuTransition.None, about, FrameClock.Seconds) { Copy = copy, Close = close, Until = FrameClock.Seconds + OwnExitSeconds });

    /// <summary>
    /// The host's next screen is up here (a real view just bound): an on or back transition still up goes, as the host's game
    /// takes its own down when its next menu opens; one still to show doesn't show (<see cref="Step"/>).
    /// </summary>
    public static void NextScreen()
    {
        _nextScreenAt = FrameClock.Seconds;
        bool any = false;
        foreach (Up up in Ups)
        {
            if (up.Kind != MenuTransition.Loading)
            {
                up.NextScreen = true;
                any = true;
            }
        }

        if (any)
        {
            _guardAt = 0;
        }
    }

    /// <summary>
    /// A screen of this player's own comes before the load a loading transition here was shown for (<paramref name="why"/>;
    /// a friend's own new-unlocks screen, <see cref="OwnUnlocks"/>): every loading transition the views showed goes with its
    /// own close (which lets the UI's input through again); the load shows one again first (<see cref="LoadReady"/>). Returns
    /// how many went. Never throws.
    /// </summary>
    public static int TakeDownLoading(string why)
    {
        int down = 0;
        try
        {
            for (int i = Ups.Count - 1; i >= 0; i--)
            {
                if (Ups[i].Kind != MenuTransition.Loading)
                {
                    continue;
                }

                AbstractMenu menu = Ups[i].Menu;
                Ups.RemoveAt(i);
                down += TakeDown(menu) ? 1 : 0;
            }
        }
        catch (Exception ex)
        {
            Fail("taking a loading transition down", ex);
        }

        if (down > 0)
        {
            ModLog.Info($"Menus: took down {down} loading transition(s) here ({why}); the load shows one again before it starts.");
        }

        return down;
    }

    /// <summary>
    /// Before this PC's own campaign scene load (<paramref name="what"/>: "the world map"): true once the game's loading
    /// transition is up, as the game's own loaders wait for it before loading; false (with <paramref name="waiting"/>) while
    /// it's coming in: call again next frame. Goes ahead after <see cref="ShownWaitSeconds"/> whatever happens, or at once
    /// when it can't be shown (logged).
    /// </summary>
    public static bool LoadReady(string what, out string waiting)
    {
        waiting = string.Empty;
        try
        {
            double now = FrameClock.Seconds;
            if (_load is { GivenUp: true })
            {
                _load = null; // an earlier load's, given up: this load asks afresh
            }

            if (_load == null)
            {
                if (LoadingUp())
                {
                    // The host's menu exit already showed it here (a run's end, the lineup's confirm): this load goes ahead behind it,
                    // so this load given up takes it down too (LoadGivenUp).
                    Serve($"before loading {what}");
                    return true;
                }

                _load = new Request(MenuTransition.Loading, $"before loading {what}", now);
                if (!Ask(_load))
                {
                    _load = null;
                    return true;
                }
            }

            if (Completed(_load) || now - _load.Since > ShownWaitSeconds)
            {
                Request done = _load;
                _load = null;
                Log(Completed(done)
                    ? $"Menus: the {MenuExits.Name(done.Kind)} transition is up here {done.About}, as the host's game shows it before its loads."
                    : $"Menus: the {MenuExits.Name(done.Kind)} transition {done.About} didn't finish showing here within {ShownWaitSeconds:0} s; loading anyway.");
                return true;
            }

            waiting = "the loading transition is showing";
            return false;
        }
        catch (Exception ex)
        {
            _load = null;
            Fail("showing the loading transition", ex);
            return true;
        }
    }

    /// <summary>
    /// The load of <paramref name="what"/> (as <see cref="LoadReady"/> named it) won't happen here after all (a friend's match
    /// that can't load, <paramref name="why"/>): the loading transition shown for it goes now, with its own hide, which also
    /// releases the game's input block its show took (<c>WooshLoadingTransition.Hide</c> → <c>LoadingInputBlock.Unblock</c>).
    /// Nothing else would take it down: no load comes to, and the guard's limits leave the screen covered and the UI blocked
    /// for seconds. One asked for that hasn't shown yet goes as soon as it has, and one a host menu's exit showed that the load
    /// went ahead behind (<see cref="Serve"/>) goes too. Never throws.
    /// </summary>
    public static void LoadGivenUp(string what, string why)
    {
        try
        {
            string about = $"before loading {what}";
            int marked = 0;
            foreach (Up up in Ups)
            {
                if (up.Kind == MenuTransition.Loading && (up.About == about || up.Serves == about) && !up.GoNow)
                {
                    up.GoNow = true;
                    marked++;
                }
            }

            bool coming = false;
            if (_load is { } load && load.About == about)
            {
                coming = load is { Asked: true, Menu: null };
                load.GivenUp = true;
                if (!coming)
                {
                    _load = null;
                }
            }

            // One a host menu's exit asked for that the load went ahead behind (LoadGoesAhead), not shown yet: it goes once it has.
            foreach (Request request in Requests)
            {
                if (request is { Kind: MenuTransition.Loading, Menu: null } && request.Serves == about && !request.GivenUp)
                {
                    request.GivenUp = true;
                    coming = true;
                }
            }

            if (marked > 0)
            {
                _guardAt = 0; // the next frame's guard takes it down
            }

            if (marked > 0 || coming)
            {
                ModLog.Info($"Menus: the {MenuExits.Name(MenuTransition.Loading)} transition ({about}) goes {(marked > 0 ? "now" : "as soon as it has shown")}: {why}.");
            }
        }
        catch (Exception ex)
        {
            Fail("taking down a given-up load's transition", ex);
        }
    }

    /// <summary>The loading transitions up here are the ones the load <paramref name="about"/> goes ahead behind (<see cref="LoadReady"/> found one up already).</summary>
    private static void Serve(string about)
    {
        foreach (Up up in Ups)
        {
            if (up.Kind == MenuTransition.Loading)
            {
                up.Serves = about;
            }
        }
    }

    /// <summary>
    /// The load of <paramref name="what"/> goes ahead now without <see cref="LoadReady"/>: an exhibition's, whose loading
    /// transition is the one the controller screen's exit shows here, as on the host. The loading transitions up here, or asked
    /// for by a host menu's exit and still to show, are the ones it's behind, so the load given up (<see cref="LoadGivenUp"/>)
    /// takes them down too; one the game's own load takes down is left to it. Never throws.
    /// </summary>
    public static void LoadGoesAhead(string what)
    {
        try
        {
            string about = $"before loading {what}";
            Serve(about);
            foreach (Request request in Requests)
            {
                if (request is { Kind: MenuTransition.Loading, Menu: null })
                {
                    request.Serves = about;
                }
            }
        }
        catch (Exception ex)
        {
            Fail("noting the transition a load goes ahead behind", ex);
        }
    }

    /// <summary>
    /// A menu is showing on this PC (<c>T2TMenu.Show</c>, before its intro): a transition asked for here, or the one the old
    /// squad menu's own exit shows, is watched from now on (its show's completion, and the guard).
    /// </summary>
    public static void Showing(AbstractMenu menu)
    {
        try
        {
            if (!Busy || !WooshGame.IsWoosh(menu))
            {
                return;
            }

            double now = FrameClock.Seconds;
            MenuTransition kind = WooshGame.Kind(menu);
            string about = string.Empty;
            double askedAt = now;

            // Every request waiting for this kind takes it (one show answers them all: Ask asks once). One this build can't
            // name is still the one asked for: never left unwatched.
            bool goNow = false;
            string serves = string.Empty;
            foreach (Request request in Requests)
            {
                if (!Adopts(request, kind, menu, now))
                {
                    continue;
                }

                // A load that went ahead behind it (LoadGoesAhead), and was given up before it showed: it goes once it has.
                goNow |= request.GivenUp;
                serves = serves.Length > 0 ? serves : request.Serves;
                if (about.Length == 0)
                {
                    about = request.About;
                    askedAt = request.AskedAt;
                }
            }

            if (_load != null && Adopts(_load, kind, menu, now) && about.Length == 0)
            {
                about = _load.About;
                askedAt = _load.AskedAt;
                goNow |= _load.GivenUp; // its load was given up before it showed
            }

            if (about.Length == 0 && now < _ownExitUntil)
            {
                about = _ownExitAbout;
                askedAt = _ownExitUntil - OwnExitSeconds; // the host's next screen may already be up: its own exit took a while
                _ownExitUntil = double.NegativeInfinity;
            }

            if (about.Length == 0 && _stopped && now < _askedUntil)
            {
                // Asked for just before the session ended, and it has only shown now: it goes once it's up.
                about = "asked for before the session ended";
                _askedUntil = double.NegativeInfinity;
            }

            if (about.Length == 0)
            {
                return; // a transition this PC's own game showed outside the views: left to it
            }

            Listen(menu);
            IntPtr pointer = menu.Pointer;
            if (!Ups.Exists(u => u.Pointer == pointer))
            {
                Ups.Add(new Up(menu, pointer, kind, about, new StrayTransition(now, StrayTransition.SettleSeconds(kind)))
                {
                    NextScreen = kind != MenuTransition.Loading && _nextScreenAt >= askedAt,
                    GoNow = goNow,
                    Serves = serves,
                });
            }
        }
        catch (Exception ex)
        {
            Fail("noting a transition", ex);
        }
    }

    /// <summary>Every frame of the game (session or not): the waiting closes and shows, then the guard.</summary>
    public static void Tick()
    {
        if (!Busy)
        {
            return;
        }

        try
        {
            double now = FrameClock.Seconds;
            for (int i = Requests.Count - 1; i >= 0; i--)
            {
                if (Step(Requests[i], now))
                {
                    Requests.RemoveAt(i);
                }
            }

            if (_load != null && now - _load.Since > ShownWaitSeconds + AdoptSeconds)
            {
                // The load it was shown for didn't ask again (its trip gave way to another): the transition is the guard's.
                _load = null;
            }

            if (Ups.Count > 0 && now >= _guardAt)
            {
                _guardAt = now + GuardSeconds;
                Guard(now);
            }
        }
        catch (Exception ex)
        {
            // What was waiting on a close still closes, and what the views showed goes: nothing stays up on this PC's screen
            // because of this.
            RunWaitingCloses();
            Requests.Clear();
            foreach (Up up in Ups)
            {
                _ = TakeDown(up.Menu);
            }

            Ups.Clear();
            Fail("following the host's transitions", ex);
        }
    }

    /// <summary>
    /// Following stopped: closes still waiting for their transition run now, every transition the views showed is taken
    /// down (one asked for just before, or that the old squad menu's own exit shows later, goes as soon as it's up).
    /// <paramref name="gameClosing"/>: no game calls, just forget.
    /// </summary>
    public static void Stop(bool gameClosing)
    {
        if (gameClosing)
        {
            _load = null;
            Requests.Clear();
            Ups.Clear();
            _ownExitUntil = double.NegativeInfinity;
            _askedUntil = double.NegativeInfinity;
            return;
        }

        // A show asked for and not begun yet (the game loads a transition's prefab the first time) still comes.
        bool coming = _load is { Asked: true, Menu: null };
        foreach (Request request in Requests)
        {
            coming |= request is { Asked: true, Menu: null } && request.Kind != MenuTransition.None;
        }

        _load = null;
        _askedUntil = coming ? FrameClock.Seconds + AdoptSeconds : double.NegativeInfinity;
        RunWaitingCloses();
        Requests.Clear();
        int hidden = 0;
        foreach (Up up in Ups)
        {
            hidden += TakeDown(up.Menu) ? 1 : 0;
        }

        Ups.Clear();
        _stopped = true;
        if (hidden > 0)
        {
            ModLog.Info($"Menus: took down {hidden} transition(s) the host's screens had shown here (the session ended).");
        }
    }

    /// <summary>One waiting close or show. True when it's done.</summary>
    private static bool Step(Request request, double now)
    {
        if (request.Until > 0)
        {
            return StepTimed(request, now);
        }

        if (request.Close == null)
        {
            if (request.Asked)
            {
                // Shown: kept until its menu shows here (the guard takes it from there), or it never does.
                return request.Menu != null || now - request.AskedAt > AdoptSeconds;
            }

            if (request.Kind != MenuTransition.Loading && _nextScreenAt >= request.Since)
            {
                // The host's next screen is already up here: on the host the transition has come and gone by then.
                Log($"Menus: {request.About} closed here; its {MenuExits.Name(request.Kind)} transition is skipped: the host's next screen is already up.");
                return true;
            }

            // Hides first: the transition once the copy has hidden (or after a while).
            AbstractMenu? copy = request.Copy;
            // (IsHidden is true from the start of its hide; IsHiding until its outro is over.)
            bool hidden = copy == null || copy.WasCollected || (copy.IsHidden && !copy.IsHiding) || !copy.gameObject.activeInHierarchy;
            if (!hidden && now - request.Since <= HiddenWaitSeconds)
            {
                return false;
            }

            if (!Ask(request))
            {
                return true;
            }

            Log($"Menus: {request.About} closed here; its {MenuExits.Name(request.Kind)} transition shows{(hidden ? " now that it has hidden" : string.Create(CultureInfo.InvariantCulture, $" ({HiddenWaitSeconds:0.0} s on, it hadn't hidden)"))}, as on the host's screen.");
            return false;
        }

        // Shows first: the copy's close once the transition's show is complete (or after a while).
        bool shown = Completed(request);
        if (!shown && now - request.Since <= ShownWaitSeconds)
        {
            return false;
        }

        Run(request.Close, request.About);
        Log($"Menus: {request.About} closed here behind its {MenuExits.Name(request.Kind)} transition{(shown ? string.Empty : $" (its show didn't finish within {ShownWaitSeconds:0} s)")}, as on the host's screen.");
        return true;
    }

    /// <summary>A timed close (<see cref="CloseAfter"/>) or an own exit's watch (<see cref="WatchOwnExit"/>).</summary>
    private static bool StepTimed(Request request, double now)
    {
        AbstractMenu? copy = request.Copy;
        if (copy != null && (copy.WasCollected || copy.IsHidden || copy.IsHiding))
        {
            return true; // its own exit got there
        }

        if (now < request.Until)
        {
            return false;
        }

        if (request.Copy != null)
        {
            ModLog.Warn($"Menus: {request.About}'s own exit here hadn't hidden it {OwnExitSeconds:0} s on; closed with the base close.");
        }

        Run(request.Close!, request.About);
        return true;
    }

    /// <summary>Runs every close still waiting (it never throws: <see cref="Run"/>).</summary>
    private static void RunWaitingCloses()
    {
        foreach (Request request in Requests.ToArray())
        {
            if (request.Close != null)
            {
                Run(request.Close, request.About);
            }
        }
    }

    /// <summary>
    /// Shows the request's transition (once): one of that kind already up or coming answers it instead of a second show (the
    /// whoosh would play twice, and a second loading show takes a second input block, its first left to the game's watchdog).
    /// False when it can't (logged once per kind and why).
    /// </summary>
    private static bool Ask(Request request)
    {
        if (request.Asked)
        {
            return true;
        }

        double now = FrameClock.Seconds;
        request.Asked = true;
        request.AskedAt = now;
        foreach (Up up in Ups)
        {
            if (up.Kind == request.Kind && up.Menu != null && !up.Menu.WasCollected && !up.Menu.IsHidden && !up.Menu.IsHiding)
            {
                request.Menu = up.Menu;
                if (CompletedAt.TryGetValue(up.Pointer, out double completed) && completed >= up.Stray.ShownAt)
                {
                    request.AskedAt = completed; // its show is already complete
                }

                return true;
            }
        }

        if (Coming(request, now))
        {
            return true;
        }

        _askedUntil = now + AdoptSeconds;
        string problem;
        try
        {
            if (WooshGame.Show(request.Kind, out problem))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        if (Logged.Add($"show:{request.Kind}:{problem}"))
        {
            ModLog.Warn($"Menus: couldn't show the {MenuExits.Name(request.Kind)} transition here ({problem}); {request.About} goes without it.");
        }

        return false;
    }

    /// <summary>True when another request's show of <paramref name="request"/>'s kind is on its way.</summary>
    private static bool Coming(Request request, double now)
    {
        foreach (Request other in Requests)
        {
            if (!ReferenceEquals(other, request) && other is { Asked: true, Menu: null } && other.Kind == request.Kind && now - other.AskedAt <= AdoptSeconds)
            {
                return true;
            }
        }

        return _load != null && !ReferenceEquals(_load, request) && _load is { Asked: true, Menu: null } && _load.Kind == request.Kind && now - _load.AskedAt <= AdoptSeconds;
    }

    /// <summary>A transition shows: true when <paramref name="request"/> was waiting for it (it's the request's from now on).</summary>
    private static bool Adopts(Request request, MenuTransition kind, AbstractMenu menu, double now)
    {
        if (!request.Asked || request.Menu != null || request.Kind == MenuTransition.None || (kind != request.Kind && kind != MenuTransition.None) || now - request.AskedAt > AdoptSeconds)
        {
            return false;
        }

        request.Menu = menu;
        return true;
    }

    /// <summary>True once the request's transition has shown here and its show (intro) is complete.</summary>
    private static bool Completed(Request request) =>
        request.Menu != null && CompletedAt.TryGetValue(request.Menu.Pointer, out double at) && at >= request.AskedAt;

    /// <summary>True when a loading transition the views showed is up here, its show complete.</summary>
    private static bool LoadingUp()
    {
        foreach (Up up in Ups)
        {
            if (up.Kind == MenuTransition.Loading && up.Menu != null && !up.Menu.WasCollected && !up.Menu.IsHidden && !up.Menu.IsHiding &&
                CompletedAt.TryGetValue(up.Menu.Pointer, out double at) && at >= up.Stray.ShownAt)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The guard: a transition the game took down is let go (said once per kind); an on or back one goes once the host's next
    /// screen is up or this PC's screen has settled with its show complete (said once per kind: no game code does it here);
    /// any still up past its limits is a stray and is taken down (warned, the first few).
    /// </summary>
    private static void Guard(double now)
    {
        bool loading = WorldMap.Loading || _load is { GivenUp: false };
        bool settled = !loading && Settled();
        for (int i = Ups.Count - 1; i >= 0; i--)
        {
            Up up = Ups[i];
            AbstractMenu? menu = up.Menu;
            if (menu == null || menu.WasCollected || menu.IsHidden || menu.IsHiding)
            {
                Ups.RemoveAt(i);
                if (Logged.Add($"down:{up.Kind}:{up.About}"))
                {
                    Log(string.Create(CultureInfo.InvariantCulture, $"Menus: the {MenuExits.Name(up.Kind)} transition ({up.About}) was taken down here by the game's own code {now - up.Stray.ShownAt:0.0} s after it showed."));
                }

                continue;
            }

            bool complete = CompletedAt.TryGetValue(menu.Pointer, out double at) && at >= up.Stray.ShownAt;
            bool showOver = complete || now - up.Stray.ShownAt > ShownWaitSeconds;
            bool due = _stopped || up.NextScreen || up.GoNow ? showOver
                : up.Stray.Watch(now, loading, settled && (up.Kind == MenuTransition.Loading || showOver));
            if (!due)
            {
                continue;
            }

            Ups.RemoveAt(i);
            if (!TakeDown(menu) || _stopped)
            {
                continue;
            }

            if (up.GoNow)
            {
                // Said where it was given up (LoadGivenUp).
                continue;
            }

            if (up.Kind != MenuTransition.Loading && (up.NextScreen || up.Stray.SettledFor >= 0))
            {
                // The usual way here: the host's game takes its own down in code that doesn't run on a friend.
                if (Logged.Add($"went:{up.Kind}:{up.About}"))
                {
                    Log(string.Create(CultureInfo.InvariantCulture, $"Menus: the {MenuExits.Name(up.Kind)} transition ({up.About}) goes {now - up.Stray.ShownAt:0.0} s after it showed: ") +
                        $"{(up.NextScreen ? "the host's next screen is up here" : "this PC's screen is settled")}, as the host's game takes its own down.");
                }
            }
            else if (++_strays <= MaxStrayWarnings)
            {
                ModLog.Warn($"Menus: the {MenuExits.Name(up.Kind)} transition ({up.About}) was still up here " +
                    (up.Stray.SettledFor > 0 ? string.Create(CultureInfo.InvariantCulture, $"{up.Stray.SettledFor:0.0} s after this PC's screen settled") : $"{now - up.Stray.ShownAt:0} s after it showed") +
                    $"; taken down{(_strays == MaxStrayWarnings ? " (further ones are only counted)" : string.Empty)}.");
            }
        }
    }

    /// <summary>
    /// True when this PC's screen has settled: its game is on the host's newest screen, the map built when it's on the map,
    /// and no host menu is still opening here.
    /// </summary>
    private static bool Settled()
    {
        CampaignRelay? relay = CampaignRelay.Current;
        bool caughtUp = relay?.View == null || relay.Showing?.Epoch == relay.View.Epoch;
        bool scene = WorldMap.SceneName != WorldMap.MapScene || WorldMap.MapLoaded;
        bool opening = MenuViews.Instance?.Friend.Opening == true;
        return caughtUp && scene && !opening;
    }

    /// <summary>Hides a transition with its own close (a loading one releases its input block itself). False when it was gone or failed.</summary>
    private static bool TakeDown(AbstractMenu? menu)
    {
        try
        {
            if (menu == null || menu.WasCollected || menu.IsHidden || menu.IsHiding)
            {
                return false;
            }

            _ = menu.Hide();
            return true;
        }
        catch (Exception ex)
        {
            Fail("taking a transition down", ex);
            return false;
        }
    }

    /// <summary>
    /// Listens for the transition's show completing (<c>AbstractMenu.OnShowingCompleted</c>, raised once its intro is over):
    /// once per menu object (the game keeps its transitions). Without it, the waits run out instead (logged once).
    /// </summary>
    private static void Listen(AbstractMenu menu)
    {
        if (Listening.Contains(menu.Pointer) || _listenFailed)
        {
            return;
        }

        try
        {
            if (_completed == null)
            {
                _completed = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractMenu>>(new Action<AbstractMenu>(OnShowingCompleted))
                    ?? throw new InvalidOperationException("the listener couldn't be made");
                EventChannel.ModListeners.Add(_completed.Pointer);
            }

            if (Listening.Count >= MaxListening)
            {
                Listening.Clear();
                CompletedAt.Clear();
            }

            menu.OnShowingCompleted.RegisterPersistentDelegate(_completed, "T2TOnline");
            Listening.Add(menu.Pointer);
        }
        catch (Exception ex)
        {
            _listenFailed = true;
            ModLog.Warn($"Menus: can't listen for transitions finishing their show here ({ModLog.SafeErrorText(ex)}); " +
                $"closes and loads wait {ShownWaitSeconds:0} s for them instead.");
        }
    }

    private static void OnShowingCompleted(AbstractMenu menu)
    {
        try
        {
            if (menu != null)
            {
                CompletedAt[menu.Pointer] = FrameClock.Seconds;
            }
        }
        catch (Exception ex)
        {
            Fail("noting a transition's show", ex);
        }
    }

    private static void Run(Action close, string about)
    {
        try
        {
            close();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: couldn't close {about} here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void Log(string line)
    {
        if (Lines.TrySpend())
        {
            ModLog.Info(line);
        }
    }

    private static void Fail(string what, Exception ex)
    {
        if (Logged.Add($"fail:{what}"))
        {
            ModLog.Error($"Menus: failed while {what}", ex);
        }
    }

    /// <summary>A transition to show for a close or a load, a timed close, and what each waits for.</summary>
    private sealed class Request
    {
        public Request(MenuTransition kind, string about, double since)
        {
            Kind = kind;
            About = about;
            Since = since;
        }

        public MenuTransition Kind { get; }

        public string About { get; }

        public double Since { get; }

        /// <summary>The copy whose hide the transition waits for (it hides first), or the copy an own exit's watch watches; or null.</summary>
        public AbstractMenu? Copy { get; init; }

        /// <summary>The copy's close, run once the transition's show is complete (it shows first), or at <see cref="Until"/>; or null.</summary>
        public Action? Close { get; init; }

        /// <summary>A timed close's time (0: not one).</summary>
        public double Until { get; init; }

        public bool Asked { get; set; }

        public double AskedAt { get; set; }

        /// <summary>The transition's menu once it shows.</summary>
        public AbstractMenu? Menu { get; set; }

        /// <summary>The load it was asked for was given up before it showed (<see cref="LoadGivenUp"/>): it goes once it has.</summary>
        public bool GivenUp { get; set; }

        /// <summary>A load that went ahead behind it though a host menu's exit asked for it ("before loading ..."), or "" (<see cref="LoadGoesAhead"/>).</summary>
        public string Serves { get; set; } = string.Empty;
    }

    /// <summary>A transition the views showed, up on this PC's screen (its native object kept for lookups after it's destroyed).</summary>
    private sealed record Up(AbstractMenu Menu, IntPtr Pointer, MenuTransition Kind, string About, StrayTransition Stray)
    {
        /// <summary>The host's next screen is up here: an on or back transition goes once its own show is over.</summary>
        public bool NextScreen { get; set; }

        /// <summary>The load it was shown for was given up (<see cref="LoadGivenUp"/>): it goes once its own show is over.</summary>
        public bool GoNow { get; set; }

        /// <summary>The load that went ahead behind it though a host menu's exit showed it ("before loading ..."), or "" (<see cref="Serve"/>).</summary>
        public string Serves { get; set; } = string.Empty;
    }
}
#endif
