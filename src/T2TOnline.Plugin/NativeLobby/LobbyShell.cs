#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Il2CppBishopGames.Catalogs;
using Il2CppBishopGames.UI;
using Il2CppDG.Tweening;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>Which of the screens a shell is.</summary>
internal enum ShellKind
{
    /// <summary>Join Lobby's friends browser. Back closes it, natively.</summary>
    Browser,

    /// <summary>The lobby. Back asks first (<see cref="ShellOptions.BackAsks"/>).</summary>
    Lobby,

    /// <summary>
    /// The Backups screen, over the game's Settings menu: opened by the UI module as the game opens Credits over Settings, not by the title, so the title's
    /// menu list, fade and background are never touched; a failure undoes only the clone and turns off only this kind.
    /// </summary>
    Backups,
}

/// <summary>Why a shell closed.</summary>
internal enum ShellClosed
{
    /// <summary>Asked to (<see cref="LobbyShell.Close"/>) or Back: the title's back, focus returned by the game.</summary>
    Closed,

    /// <summary>The title is leaving for a game: nothing of the title is touched.</summary>
    TitleLeaving,

    /// <summary>It failed and was undone (<see cref="LobbyShell.OffReason"/> says why).</summary>
    Failed,
}

/// <summary>How to open a shell.</summary>
internal sealed record ShellOptions
{
    /// <summary>The header's term.</summary>
    public string HeaderTerm { get; init; } = LobbyLocalization.Online;

    /// <summary>
    /// Every tracked player drives it until it closes. Off on a host: friends' virtual pads are tracked players, and would drive the host's
    /// cursor. On for a friend and in the browser: only this PC's own devices exist, and whichever pad they pick up works. Null: by the kind.
    /// </summary>
    public bool? AnyPlayer { get; init; }

    /// <summary>Back doesn't close it: it's taken off the back stack, and the bar's Back raises <see cref="LobbyShell.BackPressed"/>. Null: by the kind.</summary>
    public bool? BackAsks { get; init; }
}

/// <summary>The shell's named parts (the clips bind them by name: resize by anchors and size, not position; replace only what's inside them).</summary>
internal sealed record ShellParts
{
    /// <summary>The menu's root.</summary>
    public RectTransform Root { get; init; } = null!;

    /// <summary>The full-screen background.</summary>
    public RectTransform Background { get; init; } = null!;

    /// <summary><c>ScaledContainer</c>.</summary>
    public RectTransform Scaled { get; init; } = null!;

    /// <summary><c>ScaledContainer/Header</c>.</summary>
    public RectTransform Header { get; init; } = null!;

    /// <summary>The header's label.</summary>
    public TMP_Text Title { get; init; } = null!;

    /// <summary><c>ScaledContainer/Tabs</c>.</summary>
    public RectTransform Tabs { get; init; } = null!;

    /// <summary><c>ScaledContainer/Content</c>.</summary>
    public RectTransform Content { get; init; } = null!;

    /// <summary><c>ScaledContainer/Content/Scroll View</c>.</summary>
    public RectTransform ScrollView { get; init; } = null!;

    /// <summary><c>Scroll View/Viewport/Content</c>: where rows go.</summary>
    public RectTransform ScrollContent { get; init; } = null!;

    /// <summary><c>ScaledContainer/Content/Details</c>.</summary>
    public RectTransform Details { get; init; } = null!;

    /// <summary>The details' text.</summary>
    public TMP_Text Description { get; init; } = null!;

    /// <summary><c>ScaledContainer/BottomBar</c>.</summary>
    public RectTransform BottomBar { get; init; } = null!;

    /// <summary>The bar's Back shortcut.</summary>
    public UIInputShortcutBase BackShortcut { get; init; } = null!;

    /// <summary>The bar's other shortcut with its label, off until it's used (its hold time comes as 0.4 s).</summary>
    public RectTransform ExtraShortcut { get; init; } = null!;
}

/// <summary>
/// The native screens' shell: a runtime <c>T2TMenuDefinition</c> in the game's catalogue, and a clone of
/// <c>GlossaryMenu</c>'s prefab with its logic scripts destroyed and a real, concrete <c>DismissablePromptMenu</c> added, preinstantiated for the title's own
/// <c>InstantiateMenuQuick</c>. So everything is the game's: the fade to black, the title hidden behind, <c>m_MenusOverSelf</c>, the Animator's In and Out, the back
/// and input stacks, focus given back. No injected type, no patch.
/// <para>
/// The top danger is an open that fails: nothing undoes <c>m_MenusOverSelf</c>, the fade or the hidden background, and the title is deaf behind black. So the
/// definition, prefab and clone are checked before the call, and a watchdog undoes it all if the menu isn't shown (5 s and 300 frames; 30 s on a PC too slow for that many); the native
/// screens are then off for this game. A second watchdog gives focus back to the default when nothing has been selected for 2 s.
/// </para>
/// <para>
/// <b>Starting a game (<see cref="BeginHandOver"/>):</b> the shell isn't closed for it. It stays open under the game's flow (every later menu takes a higher
/// rendering order, <c>AbstractMenu.SetRenderingOrder</c>), so the bare title can't flash; a back-out lands on the live lobby, not on a title to reopen
/// it over; and when the title really leaves (<c>m_IsExiting</c>, which every exit sets first) the shell is closed under the fade, which the game's
/// <c>RemoveAfterDestroy</c> sees and returns from at once, touching nothing.
/// </para>
/// </summary>
internal static class LobbyShell
{
    /// <summary>The browser's menu definition id.</summary>
    public const string BrowserId = "T2TOnlineBrowser";

    /// <summary>The lobby's menu definition id.</summary>
    public const string LobbyId = "T2TOnlineLobby";

    /// <summary>The Backups screen's menu definition id (the prefix keeps <see cref="IsShellId"/> and the menu views' skip true for it).</summary>
    public const string BackupsId = "T2TOnlineBackups";

    /// <summary>The prefab the shell is cloned from.</summary>
    public const string SourceDefinition = "Glossary";

    private const string IdPrefix = "T2TOnline";
    // A press and what it closed reach their handlers within this many frames.
    private const int PressFrames = 2;

    private const double ShowSeconds = 5;
    private const double CloseSeconds = 5;
    private const double WaitSeconds = 5;

    // What says an open or a close is really stuck: frames as well as seconds, as both wait on frames (an animator's state, the game's tasks). A long frame isn't a stuck menu: a title's first
    // frames or a scene load can take seconds on a slow PC, and the watchdog's answer turns the screens off for the game.
    private const int WatchdogFrames = 300;
    private const double WatchdogMostSeconds = 30;
    private const double FocusCheckSeconds = 0.25;
    private const double FocusLostSeconds = 2;
    private const int MaxFocusRepairs = 3;
    private const int FlowGoneFrames = 10;
    private const float LeavingAlpha = 0.98f;
    private const double LeavingSeconds = 1;
    private const int ReassertFrames = 30;

    /// <summary>What the shell keeps of the Glossary's prefab: what a plain clone keeps (its logic scripts go).</summary>
    private static readonly HashSet<string> ShellKeep = new(UiClone.Widgets, StringComparer.Ordinal);

    private static readonly Dictionary<string, T2TMenuDefinition> Definitions = new(StringComparer.Ordinal);

    private static State _state;
    private static ShellKind _kind;
    private static ShellOptions _options = new();
    private static string _id = string.Empty;
    private static DismissablePromptMenu? _menu;
    private static GameObject? _root;
    private static TitleScreen? _title;
    private static double _since;
    private static int _sinceFrame;
    private static long _openStamp;
    private static bool _shownSaid;
    private static bool _idle;
    private static int _shownFrame;
    private static bool _firstSelected;
    private static double _focusAt;
    private static double _focusLostSince = -1;
    private static int _focusRepairs;
    private static int _coveredFrame = -1000;
    private static IUIInputHandler? _inputHandler;
    private static bool _handingOver;
    private static bool _handOverSessionLed;
    private static string _handOverWhat = string.Empty;
    private static double _handOverSince;
    private static int _reassertUntil;
    private static int _flowGone;
    private static string? _offReason;
    private static string? _backupsOffReason;
    private static ShellClosed _closingFor = ShellClosed.Closed;
    private static double _leavingSince = -1;
    private static Il2CppSystem.Collections.Generic.List<AbstractMenu>? _menus;
    private static Il2CppSystem.Collections.Generic.IList<AbstractMenu>? _menusView;

    // Kept alive for the game: each shell's own events hold them.
    private static Il2CppSystem.Action<AbstractMenu>? _showingCompleted;
    private static Il2CppSystem.Action<UIInputShortcutBase>? _backTriggered;

    /// <summary>The shell's parts were made, asleep, just before it opens: fill them here.</summary>
    public static event Action<ShellKind, ShellParts>? Built;

    /// <summary>The shell is showing (its intro is starting).</summary>
    public static event Action<ShellKind, ShellParts>? Opened;

    /// <summary>The shell's intro is over.</summary>
    public static event Action<ShellKind>? Idle;

    /// <summary>
    /// The title's in a state the shell can open over: its script started, no warm-ups, not leaving, no start running, nothing else on its list. The screens wait for this after a
    /// title load (the wipe's still up then, so nothing bare shows). Null otherwise.
    /// </summary>
    public static TitleScreen? TitleAllowingOpen()
    {
        TitleScreen? title = CampaignGame.IdleTitle();
        return title != null && title.m_HasStarted && !ExhibitionScreen.PreparingTitleMenus() ? title : null;
    }

    /// <summary>The shell has gone.</summary>
    public static event Action<ShellKind, ShellClosed>? Closed;

    /// <summary>Every frame while it's open, after the kit's own checks.</summary>
    public static event Action<ShellKind>? Ticked;

    /// <summary>The bar's Back on a shell that asks first. With no listener, the shell closes.</summary>
    public static event Action<ShellKind>? BackPressed;

    /// <summary>A hand-over ended with the title still here (true: the game's flow was backed out of): the shell is on top again and live.</summary>
    public static event Action? HandOverBackedOut;

    /// <summary>The native screens can open: nothing has failed this game.</summary>
    public static bool Available => _offReason == null && UiAssets.Problem == null;

    /// <summary>Why the native screens are off this game, or null.</summary>
    public static string? OffReason => _offReason ?? UiAssets.Problem;

    /// <summary>A shell is asked for, opening, up or closing.</summary>
    public static bool IsOpen => _state != State.Closed;

    /// <summary>A shell is up: shown, and not closing.</summary>
    public static bool IsShown => _state == State.Shown;

    /// <summary>The title under the open shell; null with none, or when it's leaving (its fade's tween loads the next scene, and mustn't be touched).</summary>
    public static TitleScreen? TitleUnder => _title == null || _title.WasCollected || _title.m_IsExiting ? null : _title;

    /// <summary>The shell that's open is on its way out: what's wanted after it opens anew, even of its kind.</summary>
    public static bool IsClosing => _state == State.Closing;

    /// <summary>The shell's intro is over.</summary>
    public static bool IsIdle => _state == State.Shown && _idle;

    /// <summary>The kind of the shell that's open.</summary>
    public static ShellKind Kind => _kind;

    /// <summary>The open shell's id ("" when none).</summary>
    public static string Id => _state == State.Closed ? string.Empty : _id;

    /// <summary>The open shell's menu, or null.</summary>
    public static AbstractMenu? Menu => _menu == null || _menu.WasCollected ? null : _menu;

    /// <summary>The open shell's parts, or null.</summary>
    public static ShellParts? Parts { get; private set; }

    /// <summary>The game's flow is running under the shell (<see cref="BeginHandOver"/>): screens leave their buttons dead.</summary>
    public static bool HandingOver => _handingOver;

    /// <summary>
    /// The shell is up, its flow isn't running, and nothing has covered it these last frames (a prompt over it takes the input, verified each frame by the game's
    /// <c>CanProcessInput</c>): a mouse click on its bar counts, as a press would.
    /// </summary>
    public static bool TakesInput => _state == State.Shown && !_handingOver && FrameClock.Frame - _coveredFrame > PressFrames;

    /// <summary>
    /// The bar's Back was clicked (its shortcut already fired, with its sound): a shell that asks first heard it through its shortcut, as B; one that closes by the
    /// back stack (the Backups screen) closes here, as B's <c>DismissablePromptMenu.OnBackInput</c> would. Never throws.
    /// </summary>
    public static void BackClicked()
    {
        try
        {
            if (TakesInput && !BacksAsk())
            {
                BeginClose("Back, clicked");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the shell's clicked Back failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>True for the id of one of the shells: <c>CampaignGame.IsIdle</c> skips it, so the co-op slot can change in the lobby, and <c>MenuPayloads.Skipped</c> keeps it from friends.</summary>
    public static bool IsShellId(string? id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The menu on top of the screen, or null: the highest rendering order among those shown. <c>UIModule.GetTopMostMenu</c> won't do: it counts retained menus that are hidden
    /// (the plain controller screen, Glossary, Settings) (verified in the game's code). Not for every frame of every screen.
    /// </summary>
    public static AbstractMenu? TopShown()
    {
        UIModule? ui = MenuGame.Module();
        if (ui == null)
        {
            return null;
        }

        _menus ??= new Il2CppSystem.Collections.Generic.List<AbstractMenu>();
        _menusView ??= _menus.Cast<Il2CppSystem.Collections.Generic.IList<AbstractMenu>>();
        _menus.Clear();
        ui.GetAllMenus(_menusView);
        AbstractMenu? top = null;
        int order = int.MinValue;
        for (int i = 0; i < _menus.Count; i++)
        {
            AbstractMenu? menu = _menus[i];
            if (menu != null && !menu.WasCollected && !menu.IsHidden && menu.EffectiveRenderingOrder > order)
            {
                top = menu;
                order = menu.EffectiveRenderingOrder;
            }
        }

        return top;
    }

    /// <summary>
    /// Asks for a shell. It opens now if the prefab's held, else as soon as it is (up to 5 s; nothing on the title changes meanwhile). False with why when it can't.
    /// Never throws.
    /// </summary>
    public static bool RequestOpen(ShellKind kind, ShellOptions options, out string problem)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Available)
        {
            problem = $"the native screens are off for this game ({OffReason}).";
            return false;
        }

        if (kind == ShellKind.Backups && _backupsOffReason != null)
        {
            problem = $"the Backups screen is off for this game ({_backupsOffReason}).";
            return false;
        }

        if (_state != State.Closed)
        {
            problem = "a native screen is open already.";
            return false;
        }

        _kind = kind;
        _options = options;
        _id = kind switch
        {
            ShellKind.Lobby => LobbyId,
            ShellKind.Backups => BackupsId,
            _ => BrowserId,
        };
        _state = State.Waiting;
        _since = FrameClock.Seconds;
        _openStamp = Stopwatch.GetTimestamp();
        UiAssets.Request(SourceDefinition);
        problem = string.Empty;
        return true;
    }

    /// <summary>Closes the open shell through its own <c>Hide</c> (Out plays, the game gives focus back), or forgets a request. Never throws.</summary>
    public static void Close(string why)
    {
        try
        {
            switch (_state)
            {
                case State.Waiting:
                    _state = State.Closed;
                    ModLog.Info($"Lobby: the native screen wasn't opened after all ({why}).");
                    break;
                case State.Opening:
                case State.Shown:
                    BeginClose(why);
                    break;
            }
        }
        catch (Exception ex)
        {
            Undo($"its close threw ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// Start: the game's flow is about to run under the shell. Call just before <c>title.NewRun()</c>, <c>ContinueRun()</c>, <c>LaunchPlayNow()</c> or
    /// <c>StartCampaignTutorialFlow()</c> (called directly: <c>PerformButtonClickCallback</c> refuses while the shell's on <c>m_MenusOverSelf</c>). The shell stays
    /// under it: see the class's remarks. It closes by itself when the title leaves; on a back-out <see cref="HandOverBackedOut"/> fires. <paramref name="sessionLed"/>: no flow runs on this title
    /// (a friend's: the host's game runs, and its views come over the shell), so nothing here can tell when it's over: <see cref="EndHandOver"/> says, on the board's word.
    /// </summary>
    public static void BeginHandOver(string what, bool sessionLed = false)
    {
        // The session says it as it calls the game's start, and the flow's effect says it again.
        if (_state is not (State.Opening or State.Shown) || _handingOver)
        {
            return;
        }

        _handingOver = true;
        _handOverSessionLed = sessionLed;
        _handOverWhat = what;
        _handOverSince = FrameClock.Seconds;
        _flowGone = 0;
        ModLog.Info($"Lobby: {what}: the game's flow runs under the shell.");
    }

    /// <summary>A friend's hand-over is over: the host is back at the lobby and this game is home, with the title still here. The shell is live again; nothing if the hand-over isn't one of those.</summary>
    public static void EndHandOver()
    {
        if (_state != State.Shown || !_handingOver || !_handOverSessionLed)
        {
            return;
        }

        _handingOver = false;
        _firstSelected = false;
        _shownFrame = FrameClock.Frame;
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Lobby: {_handOverWhat}: over after {FrameClock.Seconds - _handOverSince:0.0} s; the host is back at the lobby."));
        Listener("back-out", () => HandOverBackedOut?.Invoke());
    }

    /// <summary>
    /// The selection belongs to the shell again, for the next frames: the title selects its own default as it finishes loading, and a friend's title moved a cursor behind the shell
    /// while it followed. For about half a second, a selection outside the shell goes back to the shell's first default, without the watchdog's wait.
    /// </summary>
    public static void ReassertFocus(string why)
    {
        if (_state is State.Opening or State.Shown)
        {
            _reassertUntil = FrameClock.Frame + ReassertFrames;
            ModLog.Info($"Lobby: the selection is held on the shell for a moment ({why}).");
        }
    }

    /// <summary>What the shell selects when nothing is selected (a mouse-to-pad switch, focus repair). The first is selected once on open.</summary>
    public static void SetDefaultNavigables(params AbstractNavigable[] navigables)
    {
        ArgumentNullException.ThrowIfNull(navigables);
        if (_menu == null || _menu.WasCollected)
        {
            return;
        }

        var array = new Il2CppReferenceArray<AbstractNavigable>(navigables.Length);
        for (int i = 0; i < navigables.Length; i++)
        {
            array[i] = navigables[i];
        }

        _menu.m_DefaultNavigables = array;
    }

    /// <summary>Every frame. Never throws: a failure undoes the shell and turns the native screens off.</summary>
    public static void Tick()
    {
        if (_state == State.Closed)
        {
            return;
        }

        try
        {
            TickOpen(FrameClock.Seconds);
        }
        catch (Exception ex)
        {
            Undo($"its tick failed ({ModLog.SafeErrorText(ex)})");
        }
    }

    private static void TickOpen(double now)
    {
        switch (_state)
        {
            case State.Waiting:
                TickWaiting(now);
                break;
            case State.Opening:
                TickOpening(now);
                break;
            case State.Shown:
                TickShown(now);
                if (_state == State.Shown && Ticked != null)
                {
                    Listener("tick", () => Ticked?.Invoke(_kind));
                }

                break;
            case State.Closing:
                TickClosing(now);
                break;
        }
    }

    private static void TickWaiting(double now)
    {
        if (UiAssets.Problem != null || now - _since > WaitSeconds)
        {
            _state = State.Closed;
            ModLog.Warn($"Lobby: the native screen can't open: {(UiAssets.Problem ?? $"its prefab wasn't there within {WaitSeconds:0} s")}.");
            Listener("Closed", () => Closed?.Invoke(_kind, ShellClosed.Failed));
            return;
        }

        if (UiAssets.Ready(SourceDefinition))
        {
            Open();
        }
    }

    /// <summary>Preflight (the title, the definition, the prefab, the clone) and the title's own open. Nothing's touched on the title until all of it holds.</summary>
    private static void Open()
    {
        if (_kind == ShellKind.Backups)
        {
            OpenOverMenu();
            return;
        }

        TitleScreen? title = CampaignGame.Title();
        string? refused = title == null ? "the title isn't up"
            : title.m_IsExiting ? "the title is leaving"
            : title.m_IsStartingRun ? "a run is starting"
            : title.m_MenusOverSelf == null ? "the title has no menu list"
            : title.m_MenusOverSelf.Contains(_id) ? "the title has it open already"
            : null;
        if (refused != null)
        {
            _state = State.Closed;
            ModLog.Warn($"Lobby: the native screen didn't open: {refused}.");
            return;
        }

        UIModule? ui = MenuGame.Module();
        GameObject? root = null;
        try
        {
            if (ui == null)
            {
                throw new InvalidOperationException("the game's UI module isn't there");
            }

            T2TMenuDefinition definition = EnsureDefinition(ui, _id);
            DismissablePromptMenu menu = Build(out root, out ShellParts parts);
            Built?.Invoke(_kind, parts);
            Il2CppSystem.Collections.Generic.Dictionary<MenuDefinition, GameObject> waiting = ui.m_PreinstantiatedMenus
                ?? throw new InvalidOperationException("the game has no preinstantiated menus");
            waiting[definition] = root;
            _menu = menu;
            _root = root;
            _title = title;
            Parts = parts;
            _shownSaid = false;
            _idle = false;
            _firstSelected = false;
            _focusLostSince = -1;
            _focusRepairs = 0;
            _handingOver = false;
            _leavingSince = -1;
            _since = FrameClock.Seconds;
            _sinceFrame = FrameClock.Frame;
            _state = State.Opening;
            _ = title!.InstantiateMenuQuick(new MenuDefinitionCatalogReference(_id), null!, null!, false, null!);
        }
        catch (Exception ex)
        {
            if (_state == State.Opening)
            {
                Undo($"the title's open threw ({ModLog.SafeErrorText(ex)})");
            }
            else
            {
                // Nothing on the title was touched: only the clone goes.
                UiClone.Destroy(root);
                _state = State.Closed;
                TurnOff($"its preflight failed ({ModLog.SafeErrorText(ex)})");
                Listener("Closed", () => Closed?.Invoke(_kind, ShellClosed.Failed));
            }
        }
    }

    /// <summary>
    /// The Backups screen's open: the UI module's own <c>ShowMenuAsync&lt;AbstractMenu&gt;(id, definition, null)</c> (generic, called only), as
    /// <c>SystemSettingsUI.OnCredits</c> opens Credits over Settings (verified in the game's code). The module takes the preinstantiated clone as it does for
    /// the title's opens (its <c>LoadMenuPrefab</c>); the title is only watched for leaving. A failure before the call destroys the clone and nothing else.
    /// </summary>
    private static void OpenOverMenu()
    {
        TitleScreen? title = CampaignGame.Title();
        string? refused = title == null ? "the title isn't up"
            : title.m_IsExiting ? "the title is leaving"
            : title.m_IsStartingRun ? "a run is starting"
            : null;
        if (refused != null)
        {
            _state = State.Closed;
            ModLog.Warn($"Lobby: {_id} didn't open: {refused}.");
            Listener("Closed", () => Closed?.Invoke(_kind, ShellClosed.Failed));
            return;
        }

        UIModule? ui = MenuGame.Module();
        GameObject? root = null;
        try
        {
            if (ui == null)
            {
                throw new InvalidOperationException("the game's UI module isn't there");
            }

            T2TMenuDefinition definition = EnsureDefinition(ui, _id);
            DismissablePromptMenu menu = Build(out root, out ShellParts parts);
            Built?.Invoke(_kind, parts);
            Il2CppSystem.Collections.Generic.Dictionary<MenuDefinition, GameObject> waiting = ui.m_PreinstantiatedMenus
                ?? throw new InvalidOperationException("the game has no preinstantiated menus");
            waiting[definition] = root;
            _menu = menu;
            _root = root;
            _title = title;
            Parts = parts;
            _shownSaid = false;
            _idle = false;
            _firstSelected = false;
            _focusLostSince = -1;
            _focusRepairs = 0;
            _handingOver = false;
            _leavingSince = -1;
            _since = FrameClock.Seconds;
            _sinceFrame = FrameClock.Frame;
            _state = State.Opening;
            _ = ui.ShowMenuAsync<AbstractMenu>(_id, definition, null!);
        }
        catch (Exception ex)
        {
            if (_state == State.Opening)
            {
                Undo($"the module's open threw ({ModLog.SafeErrorText(ex)})");
            }
            else
            {
                // Nothing of the game's was touched: only the clone goes, and only this kind is off.
                UiClone.Destroy(root);
                _state = State.Closed;
                _backupsOffReason ??= $"its preflight failed ({ModLog.SafeErrorText(ex)})";
                ModLog.Warn($"Lobby: {_id} is off for this game: {_backupsOffReason}.");
                Listener("Closed", () => Closed?.Invoke(_kind, ShellClosed.Failed));
            }
        }
    }

    /// <summary>The runtime definition in the game's catalogue (once a game): the catalogue's <c>Entries</c> rebuilds itself when its list grows (verified in the game's code).</summary>
    private static T2TMenuDefinition EnsureDefinition(UIModule ui, string id)
    {
        if (Definitions.TryGetValue(id, out T2TMenuDefinition? known) && known != null && !known.WasCollected && ui.GetMenuDefinitionFromUID(id) != null)
        {
            return known;
        }

        string guid = UiAssets.Guid(SourceDefinition) ?? throw new InvalidOperationException("the source prefab's address isn't known");
        T2TMenuDefinition definition = ScriptableObject.CreateInstance(Il2CppType.Of<T2TMenuDefinition>())?.TryCast<T2TMenuDefinition>()
            ?? throw new InvalidOperationException("the definition couldn't be made");
        definition.name = id;
        definition.hideFlags = HideFlags.HideAndDontSave;
        definition.m_PrefabRef = new AssetReferenceGameObject(guid);
        Catalog<MenuDefinition> catalogue = ui.m_Definitions ?? throw new InvalidOperationException("the game's menu catalogue isn't there");
        if (!catalogue.TryAddEntry(new Catalog<MenuDefinition>.Entry(id, definition)))
        {
            throw new InvalidOperationException("the game's menu catalogue refused the definition");
        }

        if (ui.GetMenuDefinitionFromUID(id) == null)
        {
            throw new InvalidOperationException("the game's menu catalogue doesn't find the definition it took");
        }

        Definitions[id] = definition;
        ModLog.Info($"Lobby: the menu definition {id} is in the game's catalogue.");
        return definition;
    }

    /// <summary>The clone, asleep: the Glossary's scripts gone, a real <c>DismissablePromptMenu</c> added, its parts found and emptied. Throws with what's wrong.</summary>
    private static DismissablePromptMenu Build(out GameObject root, out ShellParts parts)
    {
        GameObject prefab = UiAssets.Prefab(SourceDefinition) ?? throw new InvalidOperationException("the source prefab isn't held");
        root = UiClone.Make(prefab, ShellKeep, "the shell", out string problem) ?? throw new InvalidOperationException(problem);
        root.name = _id;
        DismissablePromptMenu menu = root.AddComponent(Il2CppType.Of<DismissablePromptMenu>())?.TryCast<DismissablePromptMenu>()
            ?? throw new InvalidOperationException("the menu script couldn't be added");
        menu.m_DismissButtons = new Il2CppReferenceArray<AbstractNavigable>(0);
        menu.m_DefaultNavigables = new Il2CppReferenceArray<AbstractNavigable>(0);
        menu._canBeControlledByAnyPlayer = _options.AnyPlayer ?? _kind == ShellKind.Browser;
        Transform top = root.transform;
        parts = new ShellParts
        {
            Root = Rect(top, string.Empty),
            Background = Rect(top, "BackgroundContainer/Background"),
            Scaled = Rect(top, "ScaledContainer"),
            Header = Rect(top, "ScaledContainer/Header"),
            Title = Text(top, "ScaledContainer/Header/Title"),
            Tabs = Rect(top, "ScaledContainer/Tabs"),
            Content = Rect(top, "ScaledContainer/Content"),
            ScrollView = Rect(top, "ScaledContainer/Content/Scroll View"),
            ScrollContent = Rect(top, "ScaledContainer/Content/Scroll View/Viewport/Content"),
            Details = Rect(top, "ScaledContainer/Content/Details"),
            Description = Text(top, "ScaledContainer/Content/Details/DescriptionBox/Description"),
            BottomBar = Rect(top, "ScaledContainer/BottomBar"),
            BackShortcut = top.Find("ScaledContainer/BottomBar/Btn-Back/UIInputShortcut_V2")?.GetComponent<UIInputShortcutBase>()
                ?? throw new InvalidOperationException("the bar has no Back shortcut"),
            ExtraShortcut = Rect(top, "ScaledContainer/BottomBar/InputShortcutWithLabel"),
        };

        // What only the Glossary's script filled goes; the parts keep their names, for the clips.
        UiClone.Destroy(top.Find("ScaledContainer/Content/Details/MediaContainer/SpriteView")?.gameObject);
        UiClone.Destroy(top.Find("ScaledContainer/Content/Details/MediaContainer/VideoView")?.gameObject);
        parts.Description.text = string.Empty;
        LobbyLocalization.Apply(parts.Title, _options.HeaderTerm);
        ListenTo(menu, parts);
        return menu;
    }

    private static RectTransform Rect(Transform top, string path)
    {
        Transform found = UiClone.Find(top, path) ?? throw new InvalidOperationException($"the shell has no {path}");
        return found.GetComponent<RectTransform>() ?? throw new InvalidOperationException($"{path} isn't a UI object");
    }

    private static TextMeshProUGUI Text(Transform top, string path)
    {
        Transform found = UiClone.Find(top, path) ?? throw new InvalidOperationException($"the shell has no {path}");
        return found.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException($"{path} has no text");
    }

    private static void ListenTo(DismissablePromptMenu menu, ShellParts parts)
    {
        _showingCompleted ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractMenu>>(new Action<AbstractMenu>(OnShowingCompleted))
            ?? throw new InvalidOperationException("the show listener couldn't be made");
        _backTriggered ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIInputShortcutBase>>(new Action<UIInputShortcutBase>(OnBackShortcut))
            ?? throw new InvalidOperationException("the Back listener couldn't be made");
        menu.OnShowingCompleted.RegisterPersistentDelegate(_showingCompleted, "T2TOnline");
        parts.BackShortcut.OnInputShortcutTriggered.Register(_backTriggered, "T2TOnline");
    }

    private static void OnShowingCompleted(AbstractMenu menu)
    {
        try
        {
            if (_menu != null && menu != null && menu.Pointer == _menu.Pointer)
            {
                _idle = true;
                ModLog.Info($"Lobby: {_id}'s intro is over ({ElapsedMs():0} ms after it was asked for).");
                Listener("Idle", () => Idle?.Invoke(_kind));
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the shell's show listener failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void OnBackShortcut(UIInputShortcutBase shortcut)
    {
        try
        {
            // A shell that doesn't ask closes by the back stack (DismissablePromptMenu.OnBackInput). The game's flow over a shell has the input: this can't fire then.
            if (_state != State.Shown || _handingOver || !BacksAsk())
            {
                return;
            }

            // One press, one thing. B on a menu over this shell closes that menu first, and the same press then reaches this shortcut in the same frame, that menu already off the input stack and
            // reading as hidden (Hide clears m_IsShown first; verified in the game's code) (seen with a pad: B on Settings closed the lobby; B on a question asked it
            // again). So what counts is the frames before.
            if (FrameClock.Frame - _coveredFrame <= PressFrames)
            {
                return;
            }

            if (BackPressed != null)
            {
                BackPressed.Invoke(_kind);
            }
            else
            {
                BeginClose("Back");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the shell's Back failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static bool BacksAsk() => _options.BackAsks ?? _kind == ShellKind.Lobby;

    private static void TickOpening(double now)
    {
        if (_menu == null || _menu.WasCollected || _root == null)
        {
            Undo("its menu went away while opening");
            return;
        }

        if (_menu.m_IsShown)
        {
            _state = State.Shown;
            _shownFrame = FrameClock.Frame;
            OnShown();
            return;
        }

        if (Overdue(now, ShowSeconds))
        {
            Undo($"it wasn't shown within {now - _since:0} s and {FrameClock.Frame - _sinceFrame} frames");
        }
    }

    private static bool Overdue(double now, double seconds) =>
        now - _since > WatchdogMostSeconds || (now - _since > seconds && FrameClock.Frame - _sinceFrame > WatchdogFrames);

    private static void OnShown()
    {
        if (_shownSaid)
        {
            return;
        }

        _shownSaid = true;

        // A title still loading selects its own default about now (its InitializeTitle, after a game): the selection is the shell's.
        _reassertUntil = FrameClock.Frame + ReassertFrames;

        // The game put it on the back stack as it showed. Without it, Back reaches the title's handler, refused while a menu's over the title.
        if (BacksAsk())
        {
            MenuGame.Module()?.NavigationController?.RemoveFromBackStack(new IBackHandler(_menu!.Pointer));
        }

        ModLog.Info($"Lobby: {_id} opened in {ElapsedMs():0} ms ({(_menu!._canBeControlledByAnyPlayer ? "any player" : "the controlling player")} drives it; Back {(BacksAsk() ? "asks" : "closes it")}).");
        // Each open: what the game has loaded differs after a match. The log has a line only when the answer changed.
        NativeArt.Report();

        Listener("Opened", () => Opened?.Invoke(_kind, Parts!));
    }

    private static void TickShown(double now)
    {
        if (_menu != null && !_menu.WasCollected && MenuGame.Module() is { } ui)
        {
            if (_inputHandler == null || _inputHandler.Pointer != _menu.Pointer)
            {
                _inputHandler = new IUIInputHandler(_menu.Pointer);
            }

            if (!ui.CanProcessInput(_inputHandler))
            {
                _coveredFrame = FrameClock.Frame;
            }
        }

        if (_menu == null || _menu.WasCollected)
        {
            // It went by itself: Back on a shell that closes by the back stack ends here too, after Out.
            Finish(ShellClosed.Closed, "it closed");
            return;
        }

        if (_menu.IsHiding)
        {
            _state = State.Closing;
            _since = now;
            _sinceFrame = FrameClock.Frame;
            return;
        }

        // The title's gone or going (nothing ever clears m_IsExiting; verified in the game's code: 7 writes, all to 1): closed with no title code left to run on it, once the
        // game's own fade has gone black, so its Out shows no title. A flow backed out of before had faded it clear under the shell; GoToWorldMap fades it
        // back, and its tween mustn't be touched: it's what loads the scene.
        if (_title == null || _title.WasCollected || _title.m_IsExiting)
        {
            CanvasGroup? fade = _title == null || _title.WasCollected ? null : _title.m_FadeToBlack;
            if (_leavingSince < 0)
            {
                _leavingSince = now;
            }

            if (fade != null && fade.alpha < LeavingAlpha && now - _leavingSince < LeavingSeconds)
            {
                return;
            }

            _leavingSince = -1;
            _closingFor = ShellClosed.TitleLeaving;
            BeginClose(_handingOver ? $"{_handOverWhat}: the title is leaving" : "the title is leaving");
            return;
        }

        if (_handingOver)
        {
            TickHandOver(now);
            return;
        }

        if (FrameClock.Frame < _reassertUntil && SelectionLost(MenuGame.Module()?.NavigationController) && FirstDefault() is { } again && TopShown()?.Pointer == _menu.Pointer)
        {
            AbstractNavigableUtils.SelectOnNextFrame(again, AbstractNavigable.ESelectFx.Default);
        }

        // Once, after DismissablePromptMenu's Start cleared the selection; not if a screen or the game's own EnsureValidSelection has made one here already.
        if (!_firstSelected && FrameClock.Frame - _shownFrame >= 2)
        {
            _firstSelected = true;
            AbstractNavigable? first = FirstDefault();
            if (first != null && SelectionLost(MenuGame.Module()?.NavigationController))
            {
                AbstractNavigableUtils.SelectOnNextFrame(first, AbstractNavigable.ESelectFx.Default);
            }
        }

        if (now >= _focusAt)
        {
            _focusAt = now + FocusCheckSeconds;
            CheckFocus(now);
        }
    }

    /// <summary>The flow is over when nothing of it is left on the title and the shell is on top again, for a few frames (a flow's steps have gaps).</summary>
    private static void TickHandOver(double now)
    {
        const double settleSeconds = 0.5;
        if (_handOverSessionLed)
        {
            return;
        }

        Il2CppSystem.Collections.Generic.List<string>? over = _title!.m_MenusOverSelf;
        bool running = _title.m_IsStartingRun || _title.m_IsClickingButton || now - _handOverSince < settleSeconds;
        for (int i = 0; !running && over != null && i < over.Count; i++)
        {
            running = !IsShellId(over[i]);
        }

        if (running)
        {
            _flowGone = 0;
            return;
        }

        AbstractMenu? top = TopShown();
        if (top != null && top.Pointer != _menu!.Pointer)
        {
            _flowGone = 0;
            return;
        }

        if (++_flowGone < FlowGoneFrames)
        {
            return;
        }

        _handingOver = false;
        _firstSelected = false;
        _shownFrame = FrameClock.Frame;
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Lobby: {_handOverWhat}: backed out after {now - _handOverSince:0.0} s; the shell is on top again."));
        Listener("back-out", () => HandOverBackedOut?.Invoke());
    }

    /// <summary>True when the cursor is on nothing, or on something outside the shell (a flow backed out of can leave it on a title row behind it).</summary>
    private static bool SelectionLost(NavigationController? navigation) => SelectionLost(navigation?.CurrentNavigable);

    private static bool SelectionLost(AbstractNavigable? current) => current == null || _root == null || !current.transform.IsChildOf(_root.transform);

    /// <summary>
    /// Focus watchdog: nothing selected here for 2 s with the shell on top: its default again, a few times an open at most. Not
    /// while a friend's UI is locked for following: the lock switches the game's navigation off, which drops the selection
    /// (<c>NavigationController.RefreshActiveState</c> → <c>ForceSelectionToNull</c>) and keeps it dropped, and nothing on the
    /// shell can be pressed then anyway.
    /// </summary>
    private static void CheckFocus(double now)
    {
        NavigationController? navigation = MenuGame.Module()?.NavigationController;
        if (navigation == null || !Application.isFocused || _focusRepairs >= MaxFocusRepairs)
        {
            return;
        }

        if (Campaign.Scenes.FriendUi.Locked)
        {
            _focusLostSince = -1;
            return;
        }

        AbstractNavigable? current = navigation.CurrentNavigable;
        if (!SelectionLost(current) || (current == null && navigation.CurrentNavigationMode == NavigationMode.Mouse))
        {
            // In the game's mouse mode the cursor is where the pointer is, and on nothing between rows, as in the game's own menus;
            // the first key or pad press after it selects again (the game's own mode change does). A cursor outside the shell is still repaired.
            _focusLostSince = -1;
            return;
        }

        // A prompt over the shell has the cursor, rightly: the wait starts again when it's gone.
        AbstractMenu? top = TopShown();
        if (top == null || top.Pointer != _menu!.Pointer)
        {
            _focusLostSince = -1;
            return;
        }

        if (_focusLostSince < 0)
        {
            _focusLostSince = now;
            return;
        }

        if (now - _focusLostSince < FocusLostSeconds)
        {
            return;
        }

        AbstractNavigable? first = FirstDefault();
        if (first == null)
        {
            return;
        }

        _focusRepairs++;
        _focusLostSince = -1;
        // Where the cursor was says what took it: nothing, or something of the title's behind the shell.
        string where = current == null || current.WasCollected ? "the cursor was on nothing" : $"the cursor was on \"{SafeText.Clean(UiObjectName.ForLog(current.gameObject.name), 48)}\", outside it";
        ModLog.Warn($"Lobby: nothing was selected on {_id} for {FocusLostSeconds:0} s ({where}); its default again ({UiObjectName.ForLog(first.gameObject.name)}; {_focusRepairs} of {MaxFocusRepairs} this open).");
        AbstractNavigableUtils.SelectOnNextFrame(first, AbstractNavigable.ESelectFx.Default);
    }

    private static AbstractNavigable? FirstDefault()
    {
        Il2CppReferenceArray<AbstractNavigable>? defaults = _menu?.m_DefaultNavigables;
        if (defaults == null)
        {
            return null;
        }

        for (int i = 0; i < defaults.Length; i++)
        {
            AbstractNavigable? navigable = defaults[i];
            if (navigable != null && !navigable.WasCollected && navigable.gameObject.activeInHierarchy)
            {
                return navigable;
            }
        }

        return null;
    }

    private static void BeginClose(string why)
    {
        if (_menu == null || _menu.WasCollected)
        {
            Finish(_closingFor, why);
            return;
        }

        _state = State.Closing;
        _since = FrameClock.Seconds;
        _sinceFrame = FrameClock.Frame;
        _handingOver = false;
        ModLog.Info($"Lobby: {_id} closing ({why}).");
        _ = _menu.Hide();
    }

    private static void TickClosing(double now)
    {
        if (_menu == null || _menu.WasCollected)
        {
            Finish(_closingFor, "it closed");
            return;
        }

        if (Overdue(now, CloseSeconds))
        {
            Undo($"it didn't close within {now - _since:0} s and {FrameClock.Frame - _sinceFrame} frames");
        }
    }

    private static void Finish(ShellClosed how, string why)
    {
        ShellKind kind = _kind;
        string id = _id;
        _state = State.Closed;
        _menu = null;
        _root = null;
        _title = null;
        Parts = null;
        _handingOver = false;
        _closingFor = ShellClosed.Closed;
        ModLog.Info($"Lobby: {id} has gone ({why}).");
        Listener("Closed", () => Closed?.Invoke(kind, how));
    }

    /// <summary>
    /// The top danger's cure. The title's open can't undo itself, so this does: the id off <c>m_MenusOverSelf</c>, the fade killed and cleared, the background back, the
    /// clone gone, a selection back on the title. The native screens stay off for this game; the session goes on, and Settings > Online still leaves or ends it. Each step is guarded, and the log names what it undid. A shell caught by the title
    /// leaving is only taken away: the title's fade and objects are on their way out (the fade cleared here would show the bare title), and the next title can have a shell.
    /// </summary>
    private static void Undo(string why)
    {
        var undone = new List<string>();
        string id = _id;
        bool heldTitle = _title != null && !_title.WasCollected;
        TitleScreen? title = heldTitle ? _title : CampaignGame.Title();
        bool leaving = _closingFor == ShellClosed.TitleLeaving || !heldTitle || title == null || title.m_IsExiting;
        bool overMenu = _kind == ShellKind.Backups;
        Step(undone, "the clone", () =>
        {
            UIModule? ui = MenuGame.Module();
            if (ui != null && Definitions.TryGetValue(id, out T2TMenuDefinition? definition) && ui.m_PreinstantiatedMenus?.ContainsKey(definition) == true)
            {
                ui.m_PreinstantiatedMenus.Remove(definition);
            }

            if (_root != null)
            {
                UiClone.Destroy(_root);
                return true;
            }

            return false;
        });
        if (title != null && !leaving && !overMenu)
        {
            Step(undone, "the title's menu list", () => title.m_MenusOverSelf != null && title.m_MenusOverSelf.Remove(id));
            Step(undone, "the fade", () =>
            {
                CanvasGroup? fade = title.m_FadeToBlack;
                if (fade == null)
                {
                    return false;
                }

                ShortcutExtensions.DOKill(fade, false);
                fade.alpha = 0;
                return true;
            });
            Step(undone, "the title's background", () =>
            {
                title.DeactivateObjectsInBackground(true);
                return true;
            });
            Step(undone, "the selection", () =>
            {
                MenuGame.Module()?.NavigationController?.EnsureValidSelection();
                return true;
            });
        }

        ShellKind kind = _kind;
        _state = State.Closed;
        _menu = null;
        _root = null;
        _title = null;
        Parts = null;
        _handingOver = false;
        _closingFor = ShellClosed.Closed;
        if (leaving)
        {
            ModLog.Warn($"Lobby: {id} was taken away as the title left: {why}.");
            Listener("Closed", () => Closed?.Invoke(kind, ShellClosed.TitleLeaving));
            return;
        }

        if (overMenu)
        {
            // Opened by the module over a menu: nothing of the title's was touched, so the lobby's screens stay on; only this kind goes off.
            _backupsOffReason ??= why;
            ModLog.Warn($"Lobby: {id} failed: {why}. Undone: {(undone.Count > 0 ? string.Join(", ", undone) : "nothing needed it")}; the Backups screen is off for this game.");
            Listener("Closed", () => Closed?.Invoke(kind, ShellClosed.Failed));
            return;
        }

        TurnOff(why);
        ModLog.Warn($"Lobby: {id} failed: {why}. Undone: {(undone.Count > 0 ? string.Join(", ", undone) : "nothing needed it")}. {Core.OnlineSettings.PlayersBuild.WithoutLobby(Startup.ModSettings.DevTools)}.");
        Listener("Closed", () => Closed?.Invoke(kind, ShellClosed.Failed));
    }

    /// <summary>A listener's failure is the listener's: logged, and the shell goes on.</summary>
    private static void Listener(string what, Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: a listener of the shell's {what} failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void Step(List<string> undone, string what, Func<bool> step)
    {
        try
        {
            if (step())
            {
                undone.Add(what);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: undoing {what} failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void TurnOff(string why)
    {
        _offReason ??= why;
        ModLog.Warn($"Lobby: the native screens are off for this game: {why}.");
    }

    private static double ElapsedMs() => (Stopwatch.GetTimestamp() - _openStamp) * 1000.0 / Stopwatch.Frequency;

    private enum State
    {
        Closed,
        Waiting,
        Opening,
        Shown,
        Closing,
    }
}
#endif
