#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Plugin.Online.Panel;

/// <summary>
/// What the mod's panel (<see cref="ModPanel"/>) needs from the online session: what it shows, and what its Online tab's
/// buttons do. <see cref="OnlinePanelSession"/> is the live session; <see cref="OfflinePanelSession"/> stands in while
/// T2T Online is off (the panel then still shows the co-op slots, backups, settings and help).
/// </summary>
internal interface IPanelSession
{
    /// <summary>True while the net overlay (F9) is on.</summary>
    bool NetOverlayVisible { get; }

    /// <summary>True when the Online tab can find friends' games and join or host (false while T2T Online is off).</summary>
    bool ChoosesGames { get; }

    /// <summary>Friends' games from the last <see cref="FindGames"/>: a new list whenever one changes, the same one in between.</summary>
    IReadOnlyList<JoinableGame> JoinableGames { get; }

    /// <summary>Hosting: the seats kept for friends who dropped (D22), by Steam ID and name; a new list each time.</summary>
    IReadOnlyList<(ulong SteamId, string Name)> ParkedSeats { get; }

    /// <summary>The session as the panel shows it.</summary>
    SessionSummary Summarize();

    /// <summary>Hosting: frees a dropped friend's seat now (their player goes now, or once the match in play is over). False when it isn't kept.</summary>
    bool FreeSeat(ulong steamId);

    /// <summary>The net overlay on or off, as F9.</summary>
    void ToggleNetOverlay();

    /// <summary>Looks for friends' games now; <see cref="JoinableGames"/> fills in as Steam answers.</summary>
    void FindGames();

    /// <summary>What F7 does: joins a friend's game or hosts when none is open; leaves when in a session.</summary>
    void Toggle();

    /// <summary>Joins the friend's game in lobby <paramref name="lobbyId"/>, one of <see cref="JoinableGames"/>.</summary>
    void JoinGame(ulong lobbyId);

    /// <summary>Hosts a game for friends to join, as Shift+F7.</summary>
    void HostGame();

    /// <summary>True when <see cref="InviteFriends"/> can open Steam's invite window (hosting, in the lobby).</summary>
    bool CanInvite { get; }

    /// <summary>Opens Steam's invite window for the hosted lobby: whether it opened, and a line saying so or why not.</summary>
    (bool Opened, string Message) InviteFriends();

    /// <summary>In a session this PC joined: what it plays with there (a pad, or the keyboard and mouse); null otherwise.</summary>
    InputKind? PlaysWith { get; }

    /// <summary>Hosting: each friend's controller, with the game's actions it gave so far and those its maps bind; a new list each time.</summary>
    IReadOnlyList<FriendController> FriendControllers();

    /// <summary>In a friend's game: whether your own game's remapper and settings can be opened now, and whether one is open.</summary>
    OwnMenuView OwnMenu { get; }

    /// <summary>In a friend's game: opens your own game's remapper (<paramref name="remapper"/>) or its whole Settings menu; a line saying what happened.</summary>
    string OpenOwnMenu(bool remapper);

    /// <summary>Closes your own game's remapper or settings opened with <see cref="OpenOwnMenu"/>; a line saying what happened.</summary>
    string CloseOwnMenu();
}

/// <summary>The live session for the panel; each action is guarded by the session like its key.</summary>
internal sealed class OnlinePanelSession : IPanelSession
{
    private readonly OnlineSession _session;
    private readonly Action _toggle;
    private readonly Action _host;
    private readonly Action<ulong> _join;

    /// <summary>Wraps <paramref name="session"/> for the panel.</summary>
    /// <param name="session">The online session.</param>
    /// <param name="toggle">What F7 does (the runtime's: the F8 loopback gives the pad up first).</param>
    /// <param name="host">What Shift+F7 does (the same, then <see cref="OnlineSession.HostGame"/>).</param>
    /// <param name="join">Joins a friend's game by its lobby (the same, then <see cref="OnlineSession.JoinGame"/>).</param>
    public OnlinePanelSession(OnlineSession session, Action toggle, Action host, Action<ulong> join)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _join = join ?? throw new ArgumentNullException(nameof(join));
    }

    /// <inheritdoc />
    public bool NetOverlayVisible => _session.NetOverlayVisible;

    /// <inheritdoc />
    public bool ChoosesGames => true;

    /// <inheritdoc />
    public IReadOnlyList<JoinableGame> JoinableGames => _session.JoinableGames;

    /// <inheritdoc />
    public IReadOnlyList<(ulong SteamId, string Name)> ParkedSeats => _session.ParkedSeats;

    /// <inheritdoc />
    public SessionSummary Summarize() => _session.Summarize();

    /// <inheritdoc />
    public bool FreeSeat(ulong steamId) => _session.FreeSeat(steamId);

    /// <inheritdoc />
    public void ToggleNetOverlay() => _session.ToggleNetOverlay();

    /// <inheritdoc />
    public void FindGames() => _session.FindGames();

    /// <inheritdoc />
    public void Toggle() => _toggle();

    /// <inheritdoc />
    public void JoinGame(ulong lobbyId) => _join(lobbyId);

    /// <inheritdoc />
    public void HostGame() => _host();

    /// <inheritdoc />
    public bool CanInvite => _session.CanInvite;

    /// <inheritdoc />
    public (bool Opened, string Message) InviteFriends() => _session.InviteFriends();

    /// <inheritdoc />
    public InputKind? PlaysWith => _session.PlaysWith;

    /// <inheritdoc />
    public IReadOnlyList<FriendController> FriendControllers() => _session.FriendControllers();

    /// <inheritdoc />
    public OwnMenuView OwnMenu => _session.OwnMenu;

    /// <inheritdoc />
    public string OpenOwnMenu(bool remapper) => _session.OpenOwnMenu(remapper);

    /// <inheritdoc />
    public string CloseOwnMenu() => _session.CloseOwnMenu();
}

/// <summary>
/// Stands in for the session while T2T Online is off (<see cref="Runtime.OfflineShell"/>): the panel shows why, and its
/// session buttons are off.
/// </summary>
internal sealed class OfflinePanelSession : IPanelSession
{
    private readonly bool _afterError;

    /// <summary>Creates the stand-in.</summary>
    /// <param name="afterError">True when T2T Online stopped after an error (else it never started this session).</param>
    public OfflinePanelSession(bool afterError) => _afterError = afterError;

    /// <inheritdoc />
    public bool NetOverlayVisible => false;

    /// <inheritdoc />
    public bool ChoosesGames => false;

    /// <inheritdoc />
    public IReadOnlyList<JoinableGame> JoinableGames => Array.Empty<JoinableGame>();

    /// <inheritdoc />
    public IReadOnlyList<(ulong SteamId, string Name)> ParkedSeats => Array.Empty<(ulong, string)>();

    /// <inheritdoc />
    public SessionSummary Summarize() =>
        new(_afterError ? SessionPhase.Broken : SessionPhase.Unavailable, string.Empty, Array.Empty<SessionPlayer>(), InLobby: false, Friends: 0);

    /// <inheritdoc />
    public bool FreeSeat(ulong steamId) => false;

    /// <inheritdoc />
    public void ToggleNetOverlay()
    {
    }

    /// <inheritdoc />
    public void FindGames()
    {
    }

    /// <inheritdoc />
    public void Toggle()
    {
    }

    /// <inheritdoc />
    public void JoinGame(ulong lobbyId)
    {
    }

    /// <inheritdoc />
    public void HostGame()
    {
    }

    /// <inheritdoc />
    public bool CanInvite => false;

    /// <inheritdoc />
    public (bool Opened, string Message) InviteFriends() => (false, "Online is off this session, so there's no game to invite friends to.");

    /// <inheritdoc />
    public InputKind? PlaysWith => null;

    /// <inheritdoc />
    public IReadOnlyList<FriendController> FriendControllers() => Array.Empty<FriendController>();

    /// <inheritdoc />
    public OwnMenuView OwnMenu => OwnMenuView.None;

    /// <inheritdoc />
    public string OpenOwnMenu(bool remapper) => "Online is off this session: your game's settings are in its own menus.";

    /// <inheritdoc />
    public string CloseOwnMenu() => string.Empty;
}
#endif
