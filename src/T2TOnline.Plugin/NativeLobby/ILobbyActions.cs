using System.Collections.Generic;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// What the native screens ask of the online session. The screens are a function of <see cref="Snapshot"/>: they keep no truth
/// of their own, and every call here is safe in any state (it does nothing, and logs, when it makes no sense then). The session
/// implements it (<c>OnlineSession.Lobby.cs</c>); the check tour feeds the same screens from a fake one.
/// </summary>
internal interface ILobbyActions
{
    /// <summary>What the lobby's screen draws now. <see cref="LobbyState.None"/> with no session.</summary>
    LobbyState Snapshot();

    /// <summary>True while a session (hosting, or in a host's lobby) is up, whatever its phase.</summary>
    bool InSession { get; }

    /// <summary>This PC's game is on its way home to its title after a session ended under it (out of the host's match, or off the host's map): a prompt now would go with the scene.</summary>
    bool LeavingForTitle { get; }

    /// <summary>Why Online can't be used now (<see cref="FailureMessage"/> to show instead), or null when it can.</summary>
    FailureMessage? WhyNotOnline();

    /// <summary>Host Lobby: creates the Steam lobby and the session (the lobby's screen opens by itself through <see cref="ILobbyScreens"/>).</summary>
    void HostLobby();

    /// <summary>Joins the friend's lobby.</summary>
    void JoinLobby(ulong lobby);

    /// <summary>Cancels a join still on its way (the Joining screen's Back).</summary>
    void CancelJoin();

    /// <summary>Leaves the host's lobby, or closes our own. The screen has already asked when the tables say it must.</summary>
    void LeaveOrClose();

    /// <summary>Host: the game mode.</summary>
    void SetMode(LobbyMode mode);

    /// <summary>Host: the co-op slot, 1 to 3 (<c>CoopSlots.TrySelect</c>). False when it can't change now.</summary>
    bool SetSlot(int slot);

    /// <summary>The three co-op slots as the save selector shows them.</summary>
    IReadOnlyList<LobbySaveRow> Slots();

    /// <summary>Host: Start. <paramref name="newRun"/>: New Run on a used slot (the game's own prompt confirms). With someone not here it waits, or with <paramref name="anyway"/> doesn't.</summary>
    void Start(bool newRun, bool anyway);

    /// <summary>Host: the wait for someone not here, or the countdown, is cancelled. (Wait at the not-here prompt is <see cref="Start"/> without <c>anyway</c>.)</summary>
    void CancelStart();

    /// <summary>Friend or host: our own optional ready mark.</summary>
    void ToggleReady();

    /// <summary>Host: what the host is doing, for friends' status line.</summary>
    void NoteActivity(HostActivity activity);

    /// <summary>Host: a real Steam invite through the native list. True when Steam took it (never that it arrived).</summary>
    bool Invite(ulong friend);

    /// <summary>Host: Steam's own invite window. False with the overlay off.</summary>
    bool OpenInviteOverlay();

    /// <summary>A player's Steam profile, in Steam's overlay. False with the overlay off.</summary>
    bool OpenProfile(ulong steamId);

    /// <summary>Host: removes a player or a kept seat; the screen has asked.</summary>
    void Remove(ulong steamId);

    /// <summary>This PC's own Settings, from the lobby (its own game: nothing is sent).</summary>
    void OpenOwnSettings();

    /// <summary>Tells the session the browser is on screen (it polls friends' lobbies only while it is).</summary>
    void SetBrowserOpen(bool open);

    /// <summary>The browser's newest listing (a same list object until something changes).</summary>
    IReadOnlyList<LobbyListing> Listings();

    /// <summary>Asks for a fresh look right now (Refresh on the browser).</summary>
    void RefreshListings();

    /// <summary>Online friends for the invite panel, newest read.</summary>
    IReadOnlyList<PickerFriend> Friends();

    /// <summary>A play-test count (<see cref="LobbyMeter"/>): a press on a screen, a repaired focus, a prompt's answer.</summary>
    LobbyMeter Meter { get; }

    /// <summary>The Online row was pressed: the count's clock starts, on the session's own clock, which reads it at the end.</summary>
    void NoteOnlineOpened();
}

/// <summary>What the online session asks of the native screens: the effects of <see cref="LobbyFlow"/> that are screens. Every call is safe when the screens are off: the session goes on the same.</summary>
internal interface ILobbyScreens
{
    /// <summary>Open the lobby's screen, or reopen it, when the title allows (the screens wait for an idle title themselves).</summary>
    void ShowLobby();

    /// <summary>Close the lobby's screen now.</summary>
    void HideLobby();

    /// <summary>Starting: the shell stays under the game's flow (<c>LobbyShell.BeginHandOver</c>) and closes when the title leaves.</summary>
    void HandOverForStart(string what);

    /// <summary>Land on the Join list, on <paramref name="lobby"/>'s row if it's there.</summary>
    void ShowJoinList(ulong lobby);

    /// <summary>A friend following a game that was already running: the Join list goes, if it's up or wanted (nothing else is touched).</summary>
    void CloseJoinList();

    /// <summary>
    /// The friend's Try again after a failed match load, their session just ended: the Join list joins <paramref name="lobby"/> again
    /// once it's up and lists that lobby as joinable, as a press of its row would (its Joining, its Cancel).
    /// </summary>
    void JoinLobbyAgain(ulong lobby, string hostName);

    /// <summary>Back on the Online group, on Host Lobby.</summary>
    void ShowOnlineGroup();

    /// <summary>A failure's message: a toast, or a one-button or two-button prompt, then its landing.</summary>
    void ShowFailure(FailureMessage message);

    /// <summary>A short toast in this PC's language.</summary>
    void ShowToast(LobbyLine line);

    /// <summary>
    /// A notice (the router, <c>Online/OnlineSession.Notices.cs</c>) as a toast, with when the game may show it: <see cref="ToastTiming.Now"/> outside a game; in play
    /// <see cref="ToastTiming.NextFaceoff"/> or <see cref="ToastTiming.NextStop"/> (the game's <c>NextFaceoff</c> or <c>All</c>), so it doesn't cover play, or <see cref="ToastTiming.Now"/> for what no stop can bring. False: no toast could
    /// be made now (no queue in this scene), and the session shows it in the status box instead. The default shows <see cref="ShowToast"/> and ignores the timing: the screens
    /// override it to queue with the mapped timing. The router wraps the call in <c>OwnToasts.Begin()</c>, so <c>Toasts</c> doesn't copy the toast to friends.
    /// </summary>
    bool ShowNotice(LobbyLine line, ToastTiming timing)
    {
        ShowToast(line);
        return true;
    }

    /// <summary>The link dropped: the Reconnecting state on the lobby's screen, with a Back. False: it came back.</summary>
    void ShowReconnecting(bool reconnecting);
}

