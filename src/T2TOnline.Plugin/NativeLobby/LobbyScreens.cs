#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppDG.Tweening;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The native screens as the session and the title see them: <see cref="ILobbyScreens"/> for the session's effects, the Online group's clicks, and the shell's life. Which screen is wanted is all it
/// holds: each screen draws itself from <see cref="ILobbyActions"/>. One shell at a time: a change of screen closes the one that's up, waits for the title's own close to finish (so its reselect can't
/// land on the next screen) and opens the next. The title's own close fades from black as soon as the shell has gone, and it took the fade to 0.07 between two screens at 13 fps (seen on the check tour): so
/// the fade is held black until the next one has it. Every failure leaves a usable title: the session goes on the same.
/// </summary>
internal sealed class LobbyScreens : ILobbyScreens
{
    private const double LiveLineSeconds = 0.5;
    private const double TitleLookSeconds = 0.1;
    private const double FadeHoldSeconds = 3;
    private const double PromptLookSeconds = 0.1;
    private const double PromptHoldSeconds = 130; // past the longest a game can take to leave the host's scenes (SessionExit's 120 s)
    private const int MaxPendingFailures = 8;
    private const int SettleFrames = 3;

    // The game's own player colours, read from its PlayersManager: its static call has been seen to want a scene object, and this table can't fail.
    private static readonly int[] PlayerColours = { 0xE73842, 0x3454D1, 0x37C000, 0xFFF000, 0xB100D6, 0x00CCC0, 0xEC7900, 0x3A3A3A, 0xFF0064, 0x004C29, 0x513900 };

    private readonly BrowserList _hosts = new();
    private readonly JoinAgain _joinAgain = new();
    private BrowserScreen? _browser;
    private LobbyView? _lobby;
    private ILobbyActions? _actions;
    private ShellKind? _want;
    private ulong _landOn;
    private double _liveAt;
    private bool _hooked;
    private bool _browserPolling;
    private IReadOnlyList<LobbyListing>? _lastListings;
    private int _closedFrame = -1;
    private readonly Queue<FailureMessage> _pendingFailures = new();
    private double _promptLookAt;
    private double _promptsHeldSince = -1;
    private ulong _retryLobby;
    private string _retryHost = string.Empty;
    private OnlineChoice _lastChoice;
    private bool _landOnGroup;
    private double _titleLookAt;
    private CanvasGroup? _heldFade;
    private TitleScreen? _heldFadeTitle;
    private double _heldFadeUntil;
    private bool _settingSaid;
    private readonly Dictionary<int, bool> _drawable = new();
    private int _drawableLanguage = -1;
    private int _drawableFrame = -1;

    /// <summary>The one instance: the kit's seams are static.</summary>
    public static LobbyScreens Instance { get; } = new();

    /// <summary>Overrides the overlay's state: the tour's fake has no Steam window. Null asks Steam, at each use.</summary>
    public static Func<bool>? OverlayOverride { get; set; }

    /// <summary>Our versions, for the browser: set at start-up by the runtime.</summary>
    public static Func<BrowserIdentity> IdentitySource { get; set; } = () => new BrowserIdentity(ProtocolInfo.Version, string.Empty);

    /// <summary>The tour's fake has versions of its own.</summary>
    public static Func<BrowserIdentity>? IdentityOverride { get; set; }

    /// <summary>The session behind the screens, or the tour's fake. Screens are a function of it.</summary>
    public ILobbyActions? Actions => _actions;

    /// <summary>The Join list's view of who's hosting: the tour reads it too.</summary>
    public BrowserList Hosts => _hosts;

    /// <summary>The lobby's view, while it's up.</summary>
    public LobbyView? Lobby => _lobby;

    /// <summary>The browser's view, while it's up.</summary>
    public BrowserScreen? Browser => _browser;

    /// <summary>Steam's overlay can open now: checked at each use, as it's false for its first seconds.</summary>
    public static bool OverlayOn() => OverlayOverride != null ? OverlayOverride() : T2TOnline.Steam.SteamOverlay.IsEnabled == true;

    /// <summary>Our versions, to compare a listing's with.</summary>
    public static BrowserIdentity Identity() => (IdentityOverride ?? IdentitySource)();

    /// <summary>
    /// Whether this PC's fonts can draw a code point, for <see cref="DisplayName.Clean"/>: the label font's chain for this language, and the smileys the sprite asset has. What it can't is dropped: a box is
    /// no use in a name. Asked for every letter of every seat's name ten times a second, so each answer is kept (for this language, and once the prefab it asks is held: without one it says yes, for now).
    /// </summary>
    public static bool CanDraw(int codePoint)
    {
        // The language once a frame, not once a letter: each look reads the game's language name into a new string.
        LobbyScreens screens = Instance;
        int frame = FrameClock.Frame;
        if (frame != screens._drawableFrame)
        {
            screens._drawableFrame = frame;
            int language = LobbyLocalization.Language();
            if (language != screens._drawableLanguage)
            {
                screens._drawableLanguage = language;
                screens._drawable.Clear();
            }
        }

        if (screens._drawable.TryGetValue(codePoint, out bool known))
        {
            return known;
        }

        if (codePoint > char.MaxValue)
        {
            Il2CppTMPro.TMP_SpriteAsset? sprites = Il2CppTMPro.TMP_Settings.defaultSpriteAsset;
            bool smiley = sprites != null && sprites.GetSpriteIndexFromUnicode((uint)codePoint) >= 0;
            screens._drawable[codePoint] = smiley;
            return smiley;
        }

        Il2CppTMPro.TMP_Text? label = UiAssets.Part(LobbyUi.Glossary, "ScaledContainer/BottomBar/Btn-Back/Label")?.GetComponent<Il2CppTMPro.TMP_Text>();
        Il2CppTMPro.TMP_FontAsset? font = label == null ? null : label.font;
        if (font == null)
        {
            return true;
        }

        bool can = font.HasCharacter((char)codePoint, true, false) || HasFallback((char)codePoint);
        screens._drawable[codePoint] = can;
        return can;
    }

    private static bool HasFallback(char character)
    {
        Il2CppSystem.Collections.Generic.List<Il2CppTMPro.TMP_FontAsset>? fallbacks = Il2CppTMPro.TMP_Settings.fallbackFontAssets;
        for (int i = 0; fallbacks != null && i < fallbacks.Count; i++)
        {
            if (fallbacks[i] != null && fallbacks[i].HasCharacter(character, true, false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A player colour by its index.</summary>
    public static Color ColourOf(int index) => LobbyUi.Hex(PlayerColours[((index % PlayerColours.Length) + PlayerColours.Length) % PlayerColours.Length]);

    /// <summary>Connects the screens to the session (or the tour's fake). Safe to call again: the listeners go in once.</summary>
    public void Attach(ILobbyActions actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        if (_hooked)
        {
            return;
        }

        _hooked = true;
        OnlineGroup.Clicked += OnChoice;
        OnlineGroup.RowClicked += () => _actions?.NoteOnlineOpened();
        LobbyShell.Built += OnBuilt;
        LobbyShell.Opened += OnOpened;
        LobbyShell.Ticked += OnTicked;
        LobbyShell.Closed += OnClosed;
        LobbyShell.BackPressed += OnBack;
        LobbyShell.HandOverBackedOut += OnBackedOut;
    }

    // ---- ILobbyScreens ----

    /// <inheritdoc/>
    public void ShowLobby()
    {
        // A browser that's up shows its Joining until it has closed: it closes next, and its rows coming back for a moment are a flash and a cursor sound.
        Want(ShellKind.Lobby);
        LobbyShell.EndHandOver();
    }

    /// <inheritdoc/>
    public void HideLobby()
    {
        if (_want == ShellKind.Lobby)
        {
            _want = null;
        }

        // Only what was asked over the shell: a prompt the player asked for over their own Settings (a problem report's "saved as") isn't the lobby's, and its
        // asker would never hear it closed.
        if (LobbyPrompts.AskedOver(LobbyShell.Menu))
        {
            LobbyPrompts.CloseNow();
        }
        else if (LobbyPrompts.IsOpen)
        {
            ModLog.Info("Lobby: the lobby goes; the prompt up was asked over another menu, and stays.");
        }

        if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Lobby)
        {
            LobbyShell.Close("the session asked");
        }
    }

    /// <inheritdoc/>
    public void HandOverForStart(string what)
    {
        // A lobby still wanted (a friend's game coming home as the host starts again) would open under the host's game.
        if (_want == ShellKind.Lobby)
        {
            _want = null;
        }

        // No flow runs on a friend's title: only the board can say a Start is over (ShowLobby).
        LobbyShell.BeginHandOver(what, sessionLed: _actions?.Snapshot().Role == LobbyRole.Friend);
    }

    /// <inheritdoc/>
    public void ShowJoinList(ulong lobby)
    {
        _landOn = lobby;
        Want(ShellKind.Browser);
        if (lobby != 0)
        {
            _browser?.LandOn(lobby);
        }

        _browser?.JoinEnded();
    }

    /// <inheritdoc/>
    public void CloseJoinList()
    {
        // Its job is done: the friend follows the host's game from here, under a locked UI, and the title it sits on goes with
        // the host's next scene. Back on it when the session ends (ShowJoinList).
        if (_want == ShellKind.Browser)
        {
            _want = null;
        }

        _landOn = 0;
        if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Browser && !LobbyShell.IsClosing)
        {
            LobbyShell.Close("following the host's game");
        }
    }

    /// <inheritdoc/>
    public void JoinLobbyAgain(ulong lobby, string hostName)
    {
        if (lobby == 0)
        {
            return;
        }

        _joinAgain.Want(lobby, hostName, FrameClock.NowMicros);
        ShowJoinList(lobby);
        ModLog.Info("Lobby: the host's lobby is joined again from the Join list once this game is home and the list has it.");
    }

    /// <inheritdoc/>
    public void ShowOnlineGroup()
    {
        _want = null;
        if (LobbyShell.IsOpen)
        {
            _landOnGroup = true;
            LobbyShell.Close("back to the Online group");
        }

        OnlineGroup.SetDefaultSelection(OnlineChoice.Host);
    }

    /// <inheritdoc/>
    public void ShowFailure(FailureMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _browser?.JoinEnded();
        if (!message.IsToast && (LobbyPrompts.IsOpen || PromptsMustWait()))
        {
            Hold(message); // one at a time, and only where it can be read and answered
            return;
        }

        ModLog.Info($"Lobby: failure shown: {message.Reason} ({message.Buttons}, lands on {message.Landing}).");
        if (message.IsToast)
        {
            ShowToast(message.Line);
            return;
        }

        string title = LobbyLocalization.Say(TitleOf(message.Reason));
        string text = LobbyLocalization.Say(message.Line.ForRichText());
        if (message.Buttons == FailureButtons.TryAgainOrBack)
        {
            LobbyPrompts.AskRetry(title, text, LobbyLocalization.Say(LobbyWords.TryAgain.Key), LobbyLocalization.Say(LobbyTerms.Back), tryAgain =>
            {
                if (tryAgain)
                {
                    Retry(message);
                }
                else
                {
                    Land(message.Landing);
                }
            });
        }
        else
        {
            LobbyPrompts.Tell(title, text, LobbyLocalization.Say(LobbyTerms.Ok), () => Land(message.Landing));
        }
    }

    /// <inheritdoc/>
    public bool ShowNotice(LobbyLine line, ToastTiming timing) => LobbyToast.Show(line, timing);

    /// <inheritdoc/>
    public void ShowToast(LobbyLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        LobbyToast.Show(line);
    }

    /// <inheritdoc/>
    public void ShowReconnecting(bool reconnecting)
    {
        ModLog.Info($"Lobby: reconnecting {(reconnecting ? "shown" : "over")}{(LobbyShell.IsShown ? string.Empty : " (no lobby screen is up)")}.");
    }

    // ---- What the screens ask of each other ----

    /// <summary>The bar's Back on the browser: its shell closes, the game gives focus back to the row that opened it.</summary>
    public void CloseBrowser()
    {
        DropJoinAgain("the Join list was left");
        _want = null;
        _landOnGroup = true;
        OnlineGroup.SetDefaultSelection(OnlineChoice.Join);
        LobbyShell.Close("Back");
    }

    /// <summary>A row was pressed.</summary>
    public void JoinFromBrowser(ulong lobby, string hostName)
    {
        _retryLobby = lobby;
        _retryHost = hostName;
        _actions?.JoinLobby(lobby);

        // Refused before anything began, and said nowhere (the row went a moment ago, say): nothing else would end its Joining.
        if (_actions is { InSession: false } && !LobbyPrompts.IsOpen && _pendingFailures.Count == 0)
        {
            ShowFailure(JoinFailures.Message(LobbyFailure.Unknown, hostName));
        }
    }

    /// <summary>The empty state's Host Lobby.</summary>
    public void HostFromBrowser()
    {
        _actions?.Meter.Press();
        _actions?.HostLobby();
    }

    /// <summary>Leave, or close our own: the screen has asked when the table says it must. The session then says where to land; with none, a screen closes itself.</summary>
    public void LeaveOrClose()
    {
        _actions?.LeaveOrClose();
    }

    // ---- The frame ----

    /// <summary>Every frame, from the kit. Next to nothing while nothing's wanted.</summary>
    public void Tick()
    {
        if (_actions == null)
        {
            return;
        }

        // LobbyPrompts was ticked a moment ago in this frame, by the native screens' layer (the kit's first call).
        if (_pendingFailures.Count > 0 && !LobbyPrompts.IsOpen && FrameClock.Seconds >= _promptLookAt)
        {
            _promptLookAt = FrameClock.Seconds + PromptLookSeconds;
            if (!PromptsMustWait())
            {
                _promptsHeldSince = -1;
                ShowFailure(_pendingFailures.Dequeue());
            }
            else if (_promptsHeldSince < 0)
            {
                _promptsHeldSince = FrameClock.Seconds;
            }
        }

        TickGroup();
        TickWanted();
        if (_joinAgain.Lobby != 0)
        {
            TickJoinAgain();
        }

        if (_heldFade != null)
        {
            TickHeldFade();
        }

        if (_landOnGroup)
        {
            TickLanding();
        }
    }

    /// <summary>
    /// A prompt now would be lost, or nobody could answer it: a friend's UI is locked (following, or their game still on its way home from the host's map), their game is leaving the host's match (a quit takes
    /// seconds, 2.7 s measured on the check tour, and then) a scene load is starting (the game hides every menu first; verified in the game's code: <c>LoadSceneAfterCleanup</c>), it's between
    /// scenes, or the loading screen is still over the new one (a press mashed through it would dismiss what nobody has seen; seen on the check tour: a prompt up behind the wipe). It waits: nothing depends
    /// on it. A lock always ends (a session's end, <c>SessionExit</c>'s 120 s), and a prompt over one could never be dismissed; the rest can't wait for ever.
    /// </summary>
    private bool PromptsMustWait()
    {
        if (Campaign.Scenes.FriendUi.Locked)
        {
            return true;
        }

        if (_promptsHeldSince >= 0 && FrameClock.Seconds - _promptsHeldSince > PromptHoldSeconds)
        {
            return false;
        }

        if (_actions?.LeavingForTitle == true || Campaign.Menus.MenuGame.Module() == null || Il2CppMichsky.LSS.LoadingScreen.IsActive || Campaign.Scenes.WorldMap.Loading)
        {
            return true;
        }

        if (CampaignGame.TitleLoaded())
        {
            return CampaignGame.Title() is not { m_HasStarted: true };
        }

        return Campaign.Scenes.WorldMap.SceneName != Campaign.Scenes.WorldMap.MapScene && Match.MatchSetup.CurrentMatch(out _).IsNone;
    }

    /// <summary>A prompt for later, in the order they came. Saying the same thing twice is no use; nor is more than a screenful of them.</summary>
    private void Hold(FailureMessage message)
    {
        if (_pendingFailures.Count == 0)
        {
            _promptsHeldSince = LobbyPrompts.IsOpen ? -1 : FrameClock.Seconds;
        }

        foreach (FailureMessage held in _pendingFailures)
        {
            if (held.Reason == message.Reason && held.Line.Text(0) == message.Line.Text(0))
            {
                return;
            }
        }

        if (_pendingFailures.Count >= MaxPendingFailures)
        {
            // Its reason, not its words: they name the host by their Steam name, and whoever sent it has logged it by tag.
            ModLog.Warn($"Lobby: {MaxPendingFailures} prompts already wait; this one ({message.Reason}) is only in the log.");
            return;
        }

        ModLog.Info($"Lobby: a prompt waits for the one up, or for this game to be somewhere it can be read: {message.Reason}.");
        _pendingFailures.Enqueue(message);
    }

    private void Want(ShellKind kind)
    {
        _want = kind;
        _landOnGroup = false;
        if (ModSettings.NativeLobby && CampaignGame.TitleLoaded())
        {
            UiAssets.Request(OnlineGroup.Sources);
            UiAssets.RequestOptional(OnlineGroup.OptionalSources);
        }
    }

    /// <summary>
    /// Back on the Online group, as the Back table says, when the shell that closed wasn't opened from it (a lobby after a game opens over a new title's main group). The game's own group
    /// change, once the title's own close is over. Not from press-to-start: that's waiting for a press of its own.
    /// </summary>
    private void TickLanding()
    {
        if (LobbyShell.IsOpen || LobbyPrompts.IsOpen)
        {
            return;
        }

        TitleScreen? title = OnlineGroup.Title;
        if (title == null)
        {
            _landOnGroup = false;
            return;
        }

        if (title._isTransitioningGroup || title.m_MenusOverSelf is not { Count: 0 } || FrameClock.Frame - _closedFrame < SettleFrames)
        {
            return;
        }

        _landOnGroup = false;
        if ((int)title.m_CurrentButtonGroup == 1)
        {
            ModLog.Info("Lobby: back on the Online group.");

            // This title's main group never had the cursor (a lobby after a game opens over a new title): its Back would land on its first row, not on Online. The group goes back to the row it last had.
            if (OnlineGroup.Row != null && title.m_MainButtonsContainer?.TryCast<Il2CppBishopGames.UI.NavigableParentSelectPrevious>() is { } main)
            {
                main.m_LastHighlighted = OnlineGroup.Row;
            }

            OnlineGroup.Enter(title);
        }
    }

    private void TickWanted()
    {
        if (_want is not { } want)
        {
            return;
        }

        if (LobbyShell.IsOpen)
        {
            if (LobbyShell.Kind == want)
            {
                // Up already, or on its way up. On its way out is another matter: this one comes after it.
                if (!LobbyShell.IsClosing)
                {
                    _want = null;
                }
                else
                {
                    HoldFade();
                }
            }
            else
            {
                HoldFade();
                if (!LobbyPrompts.IsOpen && LobbyShell.IsShown)
                {
                    LobbyShell.Close("the next screen");
                }
            }

            return;
        }

        if (!ModSettings.NativeLobby)
        {
            // The setting turns off the row, the group and the shell: the session is the same without them.
            _want = null;
            if (!_settingSaid)
            {
                _settingSaid = true;
                ModLog.Info("Lobby: no lobby screen: the NativeLobby setting is off. The session goes on; F1 has the same options.");
            }

            return;
        }

        if (!LobbyShell.Available)
        {
            _want = null;
            ModLog.Warn($"Lobby: the screen can't open: {LobbyShell.OffReason}. The session goes on; " +
                (ModSettings.DevTools ? "F1 has the same options." : $"{Core.OnlineSettings.PlayersBuild.SettingsOnline} has Leave and End Online Game."));
            ShowFailure(JoinFailures.Message(LobbyFailure.ScreenFailed));
            return;
        }

        // What must be read comes first: its OK or Back says where to land, and it would only come up over the screen.
        if (LobbyPrompts.IsOpen || _pendingFailures.Count > 0)
        {
            return;
        }

        // Off the title nothing's asked for (the kit gives held prefabs back there, and a load a second would go round and round), and while a title loads it's looked for a few times a
        // second, not each frame: that's a search of the whole scene, for seconds, under the game's loading screen. Once it has started: every frame, as a swap of screens is racing its fade.
        double now = FrameClock.Seconds;
        if (now < _titleLookAt)
        {
            return;
        }

        if (!CampaignGame.TitleLoaded())
        {
            _titleLookAt = now + TitleLookSeconds;
            return;
        }

        foreach (string source in OnlineGroup.Sources)
        {
            if (!UiAssets.Ready(source))
            {
                UiAssets.Request(source);
                return;
            }
        }

        TitleScreen? title = CampaignGame.Title();
        if (title == null || !title.m_HasStarted)
        {
            _titleLookAt = now + TitleLookSeconds;
            return;
        }

        if (title.m_IsExiting || title.m_IsStartingRun || title._isTransitioningGroup || title.m_MenusOverSelf is not { Count: 0 } || Campaign.Menus.ExhibitionScreen.PreparingTitleMenus())
        {
            return;
        }

        // The title's own close has finished, and its reselect had its two frames; its fade is still near black.
        if (_closedFrame >= 0 && FrameClock.Frame - _closedFrame < SettleFrames)
        {
            return;
        }

        var options = new ShellOptions
        {
            HeaderTerm = want == ShellKind.Lobby ? LobbyWords.Lobby.Key : LobbyWords.FriendsLobbies.Key,
            AnyPlayer = want == ShellKind.Browser || _actions!.Snapshot().Role != LobbyRole.Host,
            BackAsks = true,
        };

        // Still wanted until the shell has taken it: a title that left idle this very frame refuses, and it's asked for again once the title is idle.
        if (!LobbyShell.RequestOpen(want, options, out string problem))
        {
            _want = null;
            ModLog.Warn($"Lobby: {problem}");
            ShowFailure(JoinFailures.Message(LobbyFailure.ScreenFailed));
        }
    }

    /// <summary>
    /// A join asked for again (<see cref="JoinLobbyAgain"/>): once the Join list is up and idle with nothing over it, and lists the
    /// lobby as one it can join again (joinable, or full by this player's kept seat: <see cref="JoinAgain.Fits"/>), its row is
    /// joined as a press would (its Joining shows, its Cancel works). Dropped when another session begins first; given up in time
    /// (<see cref="JoinAgain.WaitMicros"/>).
    /// </summary>
    private void TickJoinAgain()
    {
        bool listUp = _browser != null && LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Browser && !LobbyShell.HandingOver && !LobbyPrompts.IsOpen && _pendingFailures.Count == 0;
        bool joinable = false;
        bool full = false;
        if (listUp)
        {
            // Its own row, also when it reads full: the seat the host keeps for this player counts (JoinAgain.Fits).
            IReadOnlyList<BrowserRow> rows = _hosts.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                if (_joinAgain.Fits(rows[i]))
                {
                    joinable = true;
                    full = rows[i].Block == BrowserBlock.Full;
                }
            }
        }

        ulong lobby = _joinAgain.Lobby;
        string name = _joinAgain.HostName; // read before the step: a dropped wish forgets it
        JoinAgainStep step = _joinAgain.Step(FrameClock.NowMicros, _actions?.InSession == true, listUp, joinable);
        if (step == JoinAgainStep.Wait)
        {
            return;
        }

        // Drawn on the band and the details: cleaned as a seat's name is (the session's copy keeps what Steam gave, typed escapes included).
        string host = DisplayName.Clean(name, 1, LobbyLocalization.Language(), CanDraw);
        switch (step)
        {
            case JoinAgainStep.Join:
                ModLog.Info("Lobby: joining the host's lobby again from the Join list, as asked" +
                    $"{(full ? " (its row reads full, counting the seat the host keeps for this player while they're away)" : string.Empty)}.");
                _browser!.Joining(lobby, host);
                JoinFromBrowser(lobby, name);
                break;
            case JoinAgainStep.GaveUp:
                ModLog.Warn($"Lobby: the host's lobby wasn't on the Join list to join again within {JoinAgain.WaitMicros / 1_000_000} s; its row joins it when it's there.");
                break;
            case JoinAgainStep.Dropped:
                ModLog.Info("Lobby: joining the host's lobby again is dropped: another session began first.");
                break;
        }
    }

    private void DropJoinAgain(string why)
    {
        if (_joinAgain.Lobby != 0)
        {
            ModLog.Info($"Lobby: joining the host's lobby again is dropped: {why}.");
            _joinAgain.Drop();
        }
    }

    /// <summary>The shell that's up is going, and another comes after it: the title's fade stays black between them. Not on a title that's leaving: that fade's tween is what loads the next scene.</summary>
    private void HoldFade()
    {
        if (_heldFade == null && LobbyShell.TitleUnder is { } title)
        {
            _heldFadeTitle = title;
            _heldFade = title.m_FadeToBlack;
            _heldFadeUntil = FrameClock.Seconds + FadeHoldSeconds;
        }
    }

    /// <summary>The next shell has it, or nothing is coming after all (the fade goes clear, or the title would stay black), or it has waited too long, with no prompt to explain it.</summary>
    private void TickHeldFade()
    {
        CanvasGroup fade = _heldFade!;
        if (fade == null || fade.WasCollected || _heldFadeTitle == null || _heldFadeTitle.m_IsExiting || (LobbyShell.IsOpen && !LobbyShell.IsClosing && (_want == null || LobbyShell.Kind == _want)))
        {
            _heldFade = null;
            return;
        }

        // A failure's prompt can be up for as long as the player likes, and the Join list comes when it goes.
        if (LobbyPrompts.IsOpen || _pendingFailures.Count > 0)
        {
            _heldFadeUntil = FrameClock.Seconds + FadeHoldSeconds;
        }

        bool timedOut = FrameClock.Seconds > _heldFadeUntil;
        if (_want == null || timedOut)
        {
            if (timedOut)
            {
                ModLog.Warn($"Lobby: the next screen didn't come within {FadeHoldSeconds:0} s of the last; the title's fade goes clear.");
            }

            _heldFade = null;
            ShortcutExtensions.DOKill(fade, false);
            fade.alpha = 0;
            return;
        }

        ShortcutExtensions.DOKill(fade, false);
        fade.alpha = 1;
    }

    /// <summary>
    /// The Online group's live line and smart focus, from the browser's data: asked for only while the row or the group has the cursor's attention,
    /// and never in a session (the title has the cursor for a second between the lobby closing at Start and the scene leaving).
    /// </summary>
    private void TickGroup()
    {
        if (!OnlineGroup.IsIn || LobbyShell.IsOpen || _want != null || _actions?.InSession == true)
        {
            StopPolling();
            return;
        }

        double now = FrameClock.Seconds;
        if (now < _liveAt)
        {
            return;
        }

        _liveAt = now + LiveLineSeconds;
        TitleScreen? title = OnlineGroup.Title;
        if (title == null)
        {
            return;
        }

        bool inGroup = (int)title.m_CurrentButtonGroup == OnlineGroup.GroupValue;
        var current = Campaign.Menus.MenuGame.Module()?.NavigationController?.CurrentNavigable;
        bool onRow = current != null && OnlineGroup.Row != null && current.Pointer == OnlineGroup.Row.Pointer;
        if (!inGroup && !onRow)
        {
            StopPolling();
            return;
        }

        if (!_browserPolling)
        {
            _browserPolling = true;
            _actions!.SetBrowserOpen(true);
        }

        IReadOnlyList<LobbyListing> listings = _actions!.Listings();
        if (!ReferenceEquals(listings, _lastListings))
        {
            _lastListings = listings;
            _hosts.Update(listings, Identity(), FrameClock.NowMicros);
        }

        LobbyLine? line = _hosts.HostingLine();
        OnlineGroup.SetLiveLine(line == null ? null : LobbyLocalization.Say(line));
        OnlineGroup.SetDefaultSelection(line != null ? OnlineChoice.Join : OnlineChoice.Host);
    }

    private void StopPolling()
    {
        if (_browserPolling)
        {
            _browserPolling = false;
            _actions?.SetBrowserOpen(false);
        }
    }

    // ---- The kit's events ----

    private void OnChoice(OnlineChoice choice)
    {
        if (_actions == null)
        {
            return;
        }

        // A second press of A while the first is on its way (Steam is making the lobby; the shell isn't up): it's the same press to the player. With the screens off there's no lobby coming: said.
        if (_actions.InSession)
        {
            if (!LobbyShell.Available)
            {
                ShowFailure(JoinFailures.Message(LobbyFailure.ScreenFailed));
            }

            return;
        }

        DropJoinAgain("another choice was made");
        _lastChoice = choice;
        FailureMessage? why = _actions.WhyNotOnline();
        if (why != null)
        {
            ShowFailure(why);
            return;
        }

        // A session with no screen to show it would look like a press that did nothing.
        if (!LobbyShell.Available)
        {
            ModLog.Warn($"Lobby: the Online group was pressed with the screens off ({LobbyShell.OffReason}).");
            ShowFailure(JoinFailures.Message(LobbyFailure.ScreenFailed));
            return;
        }

        StopPolling();
        _actions.Meter.Press();
        if (choice == OnlineChoice.Host)
        {
            _actions.HostLobby();
        }
        else
        {
            Want(ShellKind.Browser);
        }
    }

    private void OnBuilt(ShellKind kind, ShellParts parts)
    {
        // The Backups screen is the Settings tab's, not the session's: none of these handlers is its.
        if (kind == ShellKind.Backups)
        {
            return;
        }

        ILobbyActions actions = _actions ?? throw new InvalidOperationException("no session behind the screens");
        if (kind == ShellKind.Browser)
        {
            _browser = new BrowserScreen(this, actions, _hosts);
            _browser.Build(parts);
            if (_landOn != 0)
            {
                _browser.LandOn(_landOn);
                _landOn = 0;
            }
        }
        else
        {
            _lobby = new LobbyView(this, actions);
            _lobby.Build(parts);
        }
    }

    private void OnOpened(ShellKind kind, ShellParts parts) => Guard(kind, "opening", () =>
    {
        if (kind == ShellKind.Backups)
        {
            return;
        }

        if (kind == ShellKind.Browser)
        {
            _browser?.Opened();
        }
        else
        {
            _lobby?.Opened();
        }
    });

    private void OnTicked(ShellKind kind)
    {
        if (kind == ShellKind.Backups)
        {
            return;
        }

        try
        {
            double now = FrameClock.Seconds;
            if (kind == ShellKind.Browser)
            {
                _browser?.Tick(now);
            }
            else
            {
                _lobby?.Tick(now);
            }
        }
        catch (Exception ex)
        {
            Failed(kind, "its tick", ex);
        }
    }

    /// <summary>A screen that threw is half drawn: it closes, with the toast that says to restart and make a problem report in Settings > Online. The session goes on the same.</summary>
    private void Guard(ShellKind kind, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Failed(kind, what, ex);
        }
    }

    private void Failed(ShellKind kind, string what, Exception ex)
    {
        ModLog.Warn($"Lobby: the {kind} screen failed at {what} ({ModLog.SafeErrorText(ex)}); it closes.");
        _want = null;
        if (LobbyShell.IsOpen)
        {
            LobbyShell.Close("the screen failed");
            ShowToast(new LobbyLine(LobbyWords.FailShell));
        }
    }

    private void OnClosed(ShellKind kind, ShellClosed how)
    {
        if (kind == ShellKind.Backups)
        {
            return;
        }

        BrowserScreen? browser = _browser;
        LobbyView? lobby = _lobby;
        _browser = null;
        _lobby = null;
        _closedFrame = FrameClock.Frame;
        Guard(kind, "closing", () =>
        {
            browser?.Closed();
            lobby?.Closed();
        });
        if (how == ShellClosed.Failed)
        {
            _want = null;
            if (_actions != null)
            {
                ShowFailure(JoinFailures.Message(LobbyFailure.ScreenFailed));
            }
        }
    }

    private void OnBack(ShellKind kind)
    {
        if (kind == ShellKind.Backups)
        {
            return;
        }

        if (kind == ShellKind.Browser)
        {
            _browser?.Back();
        }
        else
        {
            _lobby?.Back();
        }
    }

    private void OnBackedOut() => _lobby?.BackedOut();

    private void Land(FailureLanding landing)
    {
        switch (landing)
        {
            case FailureLanding.JoinListSameRow:
            case FailureLanding.JoinListNearestRow:
                if (!(LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Browser && !LobbyShell.IsClosing))
                {
                    ShowJoinList(landing == FailureLanding.JoinListSameRow ? _retryLobby : 0);
                }

                break;
            case FailureLanding.OnlineGroup:
                ShowOnlineGroup();
                break;
            case FailureLanding.Title:
                _want = null;
                if (LobbyShell.IsOpen)
                {
                    LobbyShell.Close("back to the title");
                }

                break;
        }
    }

    /// <summary>Try again: what failed, by what failed. A row's join again only with its browser still up; one from an invite has no row of ours to go by, and lands where its Back would.</summary>
    private void Retry(FailureMessage message)
    {
        if (_actions == null)
        {
            return;
        }

        switch (message.Reason)
        {
            case LobbyFailure.CreateFailed:
                _actions.HostLobby();
                break;
            case LobbyFailure.SteamOff or LobbyFailure.SteamWaiting:
                OnChoice(_lastChoice); // the press Steam kept from going through: it says so again if it still does
                break;
            default:
                if (_browser != null && _retryLobby != 0)
                {
                    _browser.Joining(_retryLobby, _retryHost);
                    JoinFromBrowser(_retryLobby, _retryHost); // the row may have gone meanwhile: that says so, and doesn't sit on Joining
                }
                else
                {
                    Land(message.Landing);
                }

                break;
        }
    }

    private static string TitleOf(LobbyFailure reason) => reason switch
    {
        LobbyFailure.OnlineOff or LobbyFailure.OnlineBroken or LobbyFailure.SteamOff or LobbyFailure.SteamWaiting or LobbyFailure.RestoredBackup or LobbyFailure.CreateFailed or LobbyFailure.ScreenFailed
            or LobbyFailure.RelayOnlyRefused => LobbyWords.Online.Key,
        LobbyFailure.Removed or LobbyFailure.HostClosed or LobbyFailure.HostLost or LobbyFailure.DirectLink => LobbyWords.Lobby.Key,
        LobbyFailure.Notice => LobbyWords.Online.Key,
        _ => LobbyWords.JoinFailed.Key,
    };
}
#endif
