using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>The native flow's screens, as these rules know them.</summary>
public enum LobbyScreen
{
    /// <summary>The title's Online group: Host Lobby, Join Lobby.</summary>
    Online,

    /// <summary>The friends browser, with rows or its empty state.</summary>
    JoinList,

    /// <summary>The wait after a row's pressed: the host's name, dots and a Cancel.</summary>
    Joining,

    /// <summary>The lobby, for both roles (the countdown runs over it).</summary>
    Lobby,

    /// <summary>The host's friend list: a state of the lobby's shell.</summary>
    InvitePanel,

    /// <summary>A yes/no prompt: the game's own.</summary>
    Prompt,

    /// <summary>A failure's prompt: the game's own.</summary>
    Failure,
}

/// <summary>What focus can be on.</summary>
public enum LobbyFocusKind
{
    /// <summary>Leave focus where it is.</summary>
    Unchanged,

    /// <summary>The title's Online row.</summary>
    OnlineRow,

    /// <summary>Online's Host Lobby.</summary>
    HostLobbyRow,

    /// <summary>Online's Join Lobby.</summary>
    JoinLobbyRow,

    /// <summary>A Join list row (<see cref="LobbyFocus.Index"/>).</summary>
    JoinRow,

    /// <summary>The Join list's empty state: its Host Lobby.</summary>
    EmptyStateHost,

    /// <summary>The joining screen's Cancel.</summary>
    JoinCancel,

    /// <summary>A friend list row (<see cref="LobbyFocus.Index"/>).</summary>
    FriendRow,

    /// <summary>The friend list's Back, with nobody to invite and no overlay: never a dead end.</summary>
    PanelBack,

    /// <summary>The seat the friend list was opened from: where it goes back to.</summary>
    PreviousSeat,

    /// <summary>The lobby's Game Mode selector.</summary>
    ModeRow,

    /// <summary>The lobby's Co-op Save selector (Campaign only).</summary>
    SaveRow,

    /// <summary>The lobby's Invite button.</summary>
    InviteRow,

    /// <summary>The lobby's Start.</summary>
    StartRow,

    /// <summary>A seat's card (<see cref="LobbyFocus.Index"/>).</summary>
    Seat,

    /// <summary>A prompt's safe answer: it keeps things as they are.</summary>
    SafeAnswer,

    /// <summary>A failure's first button.</summary>
    FirstButton,
}

/// <summary>A place for focus: a kind, and for rows and seats their place.</summary>
/// <param name="Kind">What.</param>
/// <param name="Index">Which row or seat.</param>
public readonly record struct LobbyFocus(LobbyFocusKind Kind, int Index = 0);

/// <summary>A pad's four directions, as the navigation gives them.</summary>
public enum LobbyDirection
{
    /// <summary>Up.</summary>
    Up,

    /// <summary>Down.</summary>
    Down,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Right.</summary>
    Right,
}

/// <summary>What a screen needs to choose its first focus, or its prompts.</summary>
public readonly record struct LobbyScreenFacts
{
    /// <summary>The lobby's state; null elsewhere.</summary>
    public LobbyState? State { get; init; }

    /// <summary>Online: a friend is hosting (<see cref="BrowserList.HostingLine"/> isn't null).</summary>
    public bool AnyoneHosting { get; init; }

    /// <summary>The Join list: rows it can focus.</summary>
    public int JoinRows { get; init; }

    /// <summary>The Join list: the default row (<see cref="BrowserList.FocusIndex"/>), from a fresh list.</summary>
    public int FirstJoinRow { get; init; }

    /// <summary>The Join list: the focused row can be joined now. False: A explains.</summary>
    public bool FocusedRowJoinable { get; init; }

    /// <summary>The Join list: the focused row's details are still on their way: there's nothing to join yet, and nothing to explain.</summary>
    public bool FocusedRowChecking { get; init; }

    /// <summary>The friend list: its rows.</summary>
    public int FriendRows { get; init; }

    /// <summary>The friend list: the focused row can be invited now.</summary>
    public bool FocusedFriendInvitable { get; init; }

    /// <summary>Steam's overlay can open now (checked at the press too: it can be false for its first seconds).</summary>
    public bool OverlayOn { get; init; }
}

/// <summary>A physical button by its role: the prompts draw the real glyph from the game's action, so remaps and devices follow.</summary>
public enum LobbyButton
{
    /// <summary>A: <c>UI_UIConfirm</c>.</summary>
    Confirm,

    /// <summary>B: <c>UI_UIBack</c>.</summary>
    Back,

    /// <summary>X: <c>UI_UISpecial</c>.</summary>
    Special,

    /// <summary>Y: <c>UI_UISpecial2</c>.</summary>
    Special2,

    /// <summary>LB: <c>UI_UITabLeft</c>.</summary>
    TabLeft,

    /// <summary>RB: <c>UI_UITabRight</c>.</summary>
    TabRight,

    /// <summary>Start: <c>UI_UIStart</c>.</summary>
    Start,

    /// <summary>LT: <c>UI_UILeftTrigger</c>.</summary>
    LeftTrigger,

    /// <summary>RT: <c>UI_UIRightTrigger</c>.</summary>
    RightTrigger,
}

/// <summary>What a press asks for: one for each of <c>ILobbyActions</c> and the screens' own moves.</summary>
public enum LobbyAction
{
    /// <summary>Open what's focused.</summary>
    Open,

    /// <summary>Go back (<see cref="LobbyRules.Back"/> says how).</summary>
    Back,

    /// <summary>Join the focused row.</summary>
    Join,

    /// <summary>Explain the focused greyed row.</summary>
    Explain,

    /// <summary>Look again.</summary>
    Refresh,

    /// <summary>Host a lobby.</summary>
    HostLobby,

    /// <summary>Cancel the join being waited for.</summary>
    CancelJoin,

    /// <summary>Open the friend list.</summary>
    OpenInvitePanel,

    /// <summary>Invite the focused friend.</summary>
    Invite,

    /// <summary>Open Steam's invite window.</summary>
    InviteOverlay,

    /// <summary>Open the focused card's Steam profile.</summary>
    OpenProfile,

    /// <summary>Ask to remove the focused card's friend.</summary>
    Remove,

    /// <summary>Start: asks first if someone isn't here.</summary>
    Start,

    /// <summary>Cancel the countdown, or the wait.</summary>
    CancelStart,

    /// <summary>The other mode, leftward.</summary>
    ModeLeft,

    /// <summary>The other mode, rightward.</summary>
    ModeRight,

    /// <summary>Start a new run on a slot that has one (the game's own prompt confirms).</summary>
    NewRun,

    /// <summary>Flip our own ready mark.</summary>
    ToggleReady,

    /// <summary>Our own game's Settings.</summary>
    OpenSettings,

    /// <summary>A prompt's answer.</summary>
    Answer,
}

/// <summary>One prompt in the bottom bar. A prompt that isn't shown is a thing that can't be done now: nothing is ever greyed.</summary>
/// <param name="Button">Which button.</param>
/// <param name="Term">Its word: a term of the mod's or the game's.</param>
/// <param name="Action">What it does.</param>
public readonly record struct LobbyPrompt(LobbyButton Button, string Term, LobbyAction Action);

/// <summary>What B does.</summary>
public enum LobbyBackAction
{
    /// <summary>Nothing: no prompt is shown for it.</summary>
    Nothing,

    /// <summary>Close this screen.</summary>
    CloseScreen,

    /// <summary>Stop the join being waited for.</summary>
    CancelJoin,

    /// <summary>Cancel the countdown or the wait: the one thing B does to a Start.</summary>
    CancelStart,

    /// <summary>Close or leave the lobby, past the question if there is one: <see cref="LobbyEvent.LeavePressed"/>.</summary>
    LeaveLobby,

    /// <summary>Take the prompt's safe answer.</summary>
    SafeAnswer,

    /// <summary>Take the failure's last button.</summary>
    LastButton,
}

/// <summary>Where B goes and whether it asks.</summary>
/// <param name="Action">What it does.</param>
/// <param name="Question">The question first, or null: yes/no prompts, no hold-to-leave (XAG 115).</param>
/// <param name="OtherAnswerTerm">The question's other answer. Its safe answer, focused first, is always <see cref="LobbyWords.Stay"/>.</param>
/// <param name="Landing">The screen it lands on.</param>
/// <param name="Focus">What has focus there.</param>
public sealed record LobbyBackRule(LobbyBackAction Action, LobbyLine? Question, string OtherAnswerTerm, LobbyScreen Landing, LobbyFocus Focus)
{
    /// <summary>True when it asks first.</summary>
    public bool Asks => Question != null;
}

/// <summary>A lobby's seats on screen: four vertical splits, or for more seats a grid of 5 x 2.</summary>
/// <param name="Columns">Columns.</param>
/// <param name="Rows">Rows.</param>
/// <param name="CardWidth">A card's width on the 1080-high canvas.</param>
/// <param name="CardHeight">A card's height.</param>
/// <param name="AvatarSize">The avatar's side.</param>
/// <param name="NameSize">The name's largest text size (it auto-sizes down to 18).</param>
/// <param name="BannerHeight">The ready banner's height.</param>
/// <param name="ShowsDevice">True when there's room for the device icon on the card.</param>
public readonly record struct SeatLayout(int Columns, int Rows, int CardWidth, int CardHeight, int AvatarSize, int NameSize, int BannerHeight, bool ShowsDevice);

/// <summary>Which of the game's own start paths a Start runs. The session's flow (the plugin's <c>OnlineSession.Lobby.cs</c>) calls it, and the
/// tutorial's path if the game would take that.</summary>
public enum LobbyStartKind
{
    /// <summary><c>title.LaunchPlayNow()</c>.</summary>
    QuickGame,

    /// <summary><c>title.ContinueRun()</c> (a failure falls to New Run in the game itself).</summary>
    ContinueRun,

    /// <summary><c>title.NewRun()</c>: on a used slot the game's own prompt confirms.</summary>
    NewRun,
}

/// <summary>
/// The rules the tables set, as code: where focus starts, where B goes and whether it asks, which prompts show for each role
/// and focus, the seats' layout, and how focus moves. Each is a pure function of plain state, so the screens hold no rule of
/// their own, and each row of the tables has a test.
/// </summary>
public static class LobbyRules
{
    /// <summary>The floor for anything that must be read, in text size on the 1080-high canvas (XAG 101's console floor, Deck's readability).</summary>
    public const int TextFloor = 26;

    /// <summary>The floor for secondary labels in all caps.</summary>
    public const int TextFloorCaps = 20;

    /// <summary>Where focus starts on <paramref name="screen"/>. A friend arriving never moves it (XAG 114): this is only for a screen's first show.</summary>
    public static LobbyFocus DefaultFocus(LobbyScreen screen, in LobbyScreenFacts facts) => screen switch
    {
        LobbyScreen.Online => new LobbyFocus(facts.AnyoneHosting ? LobbyFocusKind.JoinLobbyRow : LobbyFocusKind.HostLobbyRow),
        LobbyScreen.JoinList => facts.JoinRows > 0 ? new LobbyFocus(LobbyFocusKind.JoinRow, Math.Clamp(facts.FirstJoinRow, 0, facts.JoinRows - 1)) : new LobbyFocus(LobbyFocusKind.EmptyStateHost),
        LobbyScreen.Joining => new LobbyFocus(LobbyFocusKind.JoinCancel),
        LobbyScreen.InvitePanel => facts.FriendRows > 0 ? new LobbyFocus(LobbyFocusKind.FriendRow) : new LobbyFocus(LobbyFocusKind.PanelBack),
        LobbyScreen.Lobby => LobbyFocusOf(facts.State ?? LobbyState.None),
        LobbyScreen.Prompt => new LobbyFocus(LobbyFocusKind.SafeAnswer),
        LobbyScreen.Failure => new LobbyFocus(LobbyFocusKind.FirstButton),
        _ => new LobbyFocus(LobbyFocusKind.Unchanged),
    };

    /// <summary>Where B goes on <paramref name="screen"/>, and whether it asks. On <see cref="LobbyScreen.Lobby"/> it depends on the state: the countdown, who's here, the role.</summary>
    public static LobbyBackRule Back(LobbyScreen screen, in LobbyScreenFacts facts)
    {
        LobbyState state = facts.State ?? LobbyState.None;
        switch (screen)
        {
            case LobbyScreen.Online:
                return Plain(LobbyBackAction.CloseScreen, LobbyScreen.Online, LobbyFocusKind.OnlineRow);
            case LobbyScreen.JoinList:
                return Plain(LobbyBackAction.CloseScreen, LobbyScreen.Online, LobbyFocusKind.JoinLobbyRow);
            case LobbyScreen.InvitePanel:
                return Plain(LobbyBackAction.CloseScreen, LobbyScreen.Lobby, LobbyFocusKind.PreviousSeat);
            case LobbyScreen.Joining:
                return Plain(LobbyBackAction.CancelJoin, LobbyScreen.JoinList, LobbyFocusKind.JoinRow);
            case LobbyScreen.Prompt:
                return Plain(LobbyBackAction.SafeAnswer, LobbyScreen.Prompt, LobbyFocusKind.Unchanged);
            case LobbyScreen.Failure:
                return Plain(LobbyBackAction.LastButton, LobbyScreen.Failure, LobbyFocusKind.Unchanged);
            case LobbyScreen.Lobby:
                return LobbyBack(state);
            default:
                return Plain(LobbyBackAction.Nothing, screen, LobbyFocusKind.Unchanged);
        }
    }

    /// <summary>The bottom bar on <paramref name="screen"/> with focus on <paramref name="focus"/>: what can be done now, and nothing else.</summary>
    public static IReadOnlyList<LobbyPrompt> Prompts(LobbyScreen screen, in LobbyFocus focus, in LobbyScreenFacts facts)
    {
        var prompts = new List<LobbyPrompt>(6);
        switch (screen)
        {
            case LobbyScreen.Online:
                prompts.Add(new LobbyPrompt(LobbyButton.Confirm, LobbyTerms.Select, LobbyAction.Open));
                prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back));
                break;
            case LobbyScreen.JoinList:
                if (focus.Kind == LobbyFocusKind.EmptyStateHost)
                {
                    prompts.Add(new LobbyPrompt(LobbyButton.Confirm, LobbyWords.HostLobby.Key, LobbyAction.HostLobby));
                }
                else if (focus.Kind == LobbyFocusKind.JoinRow && !facts.FocusedRowChecking)
                {
                    prompts.Add(facts.FocusedRowJoinable
                        ? new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Join.Key, LobbyAction.Join)
                        : new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Why.Key, LobbyAction.Explain));
                }

                prompts.Add(new LobbyPrompt(LobbyButton.Special, LobbyWords.Refresh.Key, LobbyAction.Refresh));
                prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back));
                break;
            case LobbyScreen.Joining:
                prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Cancel, LobbyAction.CancelJoin));
                break;
            case LobbyScreen.InvitePanel:
                if (focus.Kind == LobbyFocusKind.FriendRow && facts.FocusedFriendInvitable)
                {
                    prompts.Add(new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Invite.Key, LobbyAction.Invite));
                }

                if (facts.OverlayOn)
                {
                    prompts.Add(new LobbyPrompt(LobbyButton.Special, LobbyWords.SteamOverlay.Key, LobbyAction.InviteOverlay));
                }

                prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back));
                break;
            case LobbyScreen.Lobby:
                AddLobbyPrompts(prompts, focus, facts);
                break;
            case LobbyScreen.Prompt:
            case LobbyScreen.Failure:
                prompts.Add(new LobbyPrompt(LobbyButton.Confirm, LobbyTerms.Select, LobbyAction.Answer));
                break;
        }

        return prompts;
    }

    /// <summary>True when up and down wrap on <paramref name="screen"/>: linear lists do, the lobby's seats-and-options layout doesn't (XAG 112).</summary>
    public static bool Wraps(LobbyScreen screen) => screen is LobbyScreen.Online or LobbyScreen.JoinList or LobbyScreen.InvitePanel;

    /// <summary>A linear list's next row, <paramref name="by"/> away: wrapping where <see cref="Wraps"/> says so, stopping at the ends otherwise. -1 for an empty list.</summary>
    public static int MoveInList(LobbyScreen screen, int index, int count, int by)
    {
        if (count <= 0)
        {
            return -1;
        }

        long next = (long)Math.Clamp(index, 0, count - 1) + by;
        return Wraps(screen) ? (int)(((next % count) + count) % count) : (int)Math.Clamp(next, 0L, count - 1L);
    }

    /// <summary>A lobby's layout: four vertical splits, or for more seats the 5 x 2 grid, with no device icon on the smaller cards.</summary>
    public static SeatLayout LayoutFor(int capacity) => capacity <= LobbyState.TunedSeats
        ? new SeatLayout(LobbyState.TunedSeats, 1, 277, 700, 184, 32, 57, ShowsDevice: true)
        : new SeatLayout(5, 2, 222, 391, 96, 24, 46, ShowsDevice: false);

    /// <summary>True when focus can land on a seat: a host's on every one (an empty one invites), a friend's only on someone's card.</summary>
    public static bool CanFocusSeat(LobbyState state, int index)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (index < 0 || index >= state.Seats.Count)
        {
            return false;
        }

        return state.Role == LobbyRole.Host || state.Seats[index].Stage is not (SeatStage.Open or SeatStage.Invited);
    }

    /// <summary>
    /// Where focus goes from <paramref name="from"/> in a direction, on the lobby's screen: seats by the layout, the host's column of rows
    /// to their left, no wrapping, and what isn't there skipped. Stays put at an edge.
    /// </summary>
    public static LobbyFocus Move(in LobbyFocus from, LobbyDirection direction, LobbyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return from.Kind switch
        {
            LobbyFocusKind.Seat => MoveFromSeat(from, direction, state),
            LobbyFocusKind.ModeRow or LobbyFocusKind.SaveRow or LobbyFocusKind.InviteRow or LobbyFocusKind.StartRow when state.Role == LobbyRole.Host => MoveInColumn(from, direction, state),
            _ => from,
        };
    }

    /// <summary>
    /// Focus put right after the lobby changed: a row that went (Save, in Quick Game; Invite, with no empty seat), or a friend's card
    /// that emptied. A focus still good stays: this is what keeps arrivals from moving the cursor.
    /// </summary>
    public static LobbyFocus Repair(in LobbyFocus focus, LobbyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        switch (focus.Kind)
        {
            case LobbyFocusKind.Seat when CanFocusSeat(state, focus.Index):
                return focus;
            case LobbyFocusKind.ModeRow or LobbyFocusKind.StartRow when state.Role == LobbyRole.Host:
                return focus;
            case LobbyFocusKind.SaveRow when state.Role == LobbyRole.Host:
                return state.Mode == LobbyMode.Campaign ? focus : new LobbyFocus(LobbyFocusKind.ModeRow);
            case LobbyFocusKind.InviteRow when state.Role == LobbyRole.Host:
                return HasRoomToInvite(state) ? focus : new LobbyFocus(LobbyFocusKind.StartRow);
            default:
                return LobbyFocusOf(state);
        }
    }

    /// <summary>What Start runs: Quick Game, or on a campaign save Continue where it holds a run and New Run where it doesn't. <paramref name="newRun"/>: the host's second prompt on a used slot.</summary>
    public static LobbyStartKind StartKindOf(LobbyMode mode, in LobbySaveRow save, bool newRun = false) =>
        mode == LobbyMode.QuickGame ? LobbyStartKind.QuickGame : save.HasRun && !newRun ? LobbyStartKind.ContinueRun : LobbyStartKind.NewRun;

    /// <summary>Start's word for what it runs: the game's own.</summary>
    public static string StartTerm(LobbyStartKind kind) => kind switch
    {
        LobbyStartKind.ContinueRun => LobbyTerms.ContinueRun,
        LobbyStartKind.NewRun => LobbyTerms.NewRun,
        _ => LobbyTerms.Start,
    };

    /// <summary>The mode's word: the game's own ("Quick Game", never "Quick Play").</summary>
    public static string ModeTerm(LobbyMode mode) => mode == LobbyMode.QuickGame ? LobbyTerms.QuickGame : LobbyTerms.Campaign;

    /// <summary>The other mode, as left or right both give: only two are listed.</summary>
    public static LobbyMode NextMode(LobbyMode mode) => mode == LobbyMode.QuickGame ? LobbyMode.Campaign : LobbyMode.QuickGame;

    /// <summary>
    /// The mode a press asks for: the selector's own choice (<see cref="LobbyButton.Confirm"/> with <see cref="LobbyAction.ModeLeft"/>
    /// for the first, <see cref="LobbyAction.ModeRight"/> for the second), or for L and R (a key, a bumper, a click on either glyph)
    /// the other mode from <paramref name="current"/>. <paramref name="current"/> is the session's mode at the press, not a screen's
    /// last look at it: two presses in one redraw then go there and back.
    /// </summary>
    public static LobbyMode ModeAfter(LobbyButton button, LobbyAction action, LobbyMode current) =>
        button == LobbyButton.Confirm ? (action == LobbyAction.ModeLeft ? LobbyMode.Campaign : LobbyMode.QuickGame) : NextMode(current);

    /// <summary>Start's one prompt: who isn't here, by name for one and by count for more; null when everyone's here and it needn't ask.</summary>
    public static LobbyLine? NotHereLine(LobbyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        switch (state.NotHere.Count)
        {
            case 0:
                return null;
            case 1:
                LobbySeat seat = state.NotHere[0];
                return new LobbyLine(seat.Stage switch
                {
                    SeatStage.Joining => LobbyWords.NotHereJoining,
                    SeatStage.Reconnecting => LobbyWords.NotHereReconnecting,
                    _ => LobbyWords.NotHereLoading,
                }, seat.Name);
            default:
                return new LobbyLine(LobbyWords.NotHereMany, LobbyWords.Number(state.NotHere.Count));
        }
    }

    /// <summary>The number the countdown shows: 3, 2, 1. 0 outside it.</summary>
    public static int CountdownNumber(int countdownMs) => countdownMs <= 0 ? 0 : (countdownMs + 999) / 1000;

    /// <summary>
    /// The lobby's one status line, for whoever's looking: reconnecting, the countdown, Starting, the wait; on a friend, what the host is
    /// busy with. Null with nothing to say. <paramref name="hostName"/> is cleaned, and the board's is used without one.
    /// </summary>
    public static LobbyLine? StatusLine(LobbyState state, string? hostName = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        string host = hostName ?? state.HostName;
        if (state.Reconnecting)
        {
            return new LobbyLine(LobbyWords.ReconnectingTo, host);
        }

        switch (state.Phase)
        {
            case LobbyPhase.Counting:
                return new LobbyLine(LobbyWords.StartingIn, LobbyWords.Number(CountdownNumber(state.CountdownMs)));
            case LobbyPhase.Starting or LobbyPhase.Playing:
                return new LobbyLine(LobbyWords.GameStarting);
        }

        if (state.WaitingForEveryone)
        {
            return new LobbyLine(LobbyWords.WaitingEveryone);
        }

        return state.Role == LobbyRole.Friend ? LobbyMarks.HostStatus(state.Activity, host) : null;
    }

    /// <summary>
    /// The players band's one line, beside its count: the invite list's note while it's open; nothing under the countdown, which says it
    /// over everything; else the status line, then the last game's ribbon (<paramref name="news"/> true: drawn in the game's gold), and a
    /// host alone with an open seat is told the lobby is waiting for players. Null with nothing to say.
    /// </summary>
    public static LobbyLine? BandLine(LobbyState state, bool panelOpen, out bool news)
    {
        ArgumentNullException.ThrowIfNull(state);

        news = false;
        if (panelOpen)
        {
            return new LobbyLine(LobbyWords.FriendsNeedMod);
        }

        if (state.Phase is LobbyPhase.Counting or LobbyPhase.Starting or LobbyPhase.Playing)
        {
            return null;
        }

        if (StatusLine(state) is { } status)
        {
            return status;
        }

        if (LobbyMarks.Ribbon(state.Result) is { } ribbon)
        {
            news = true;
            return ribbon;
        }

        return state.Role == LobbyRole.Host && state.Players <= 1 && HasRoomToInvite(state) ? new LobbyLine(LobbyWords.WaitingPlayers) : null;
    }

    private static LobbyFocus LobbyFocusOf(LobbyState state)
    {
        if (state.Role == LobbyRole.Friend)
        {
            LobbySeat? own = state.SelfSeat;
            return own != null ? new LobbyFocus(LobbyFocusKind.Seat, own.Index) : FirstCard(state);
        }

        if (state.BackFromGame)
        {
            return new LobbyFocus(LobbyFocusKind.StartRow);
        }

        // Alone: the likeliest next step, and a double-tap from Host Lobby opens a list rather than starting a game.
        return state.Players <= 1 && FirstEmptySeat(state) is >= 0 and var empty ? new LobbyFocus(LobbyFocusKind.Seat, empty) : new LobbyFocus(LobbyFocusKind.StartRow);
    }

    private static LobbyFocus FirstCard(LobbyState state)
    {
        for (int i = 0; i < state.Seats.Count; i++)
        {
            if (CanFocusSeat(state, i))
            {
                return new LobbyFocus(LobbyFocusKind.Seat, i);
            }
        }

        return new LobbyFocus(LobbyFocusKind.Unchanged);
    }

    private static int FirstEmptySeat(LobbyState state)
    {
        for (int i = 0; i < state.Seats.Count; i++)
        {
            if (state.Seats[i].IsEmpty)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when another invite could still bring someone: a seat is empty, or held only for someone invited. Their mark holds a seat for two minutes, and an invite nobody answers must not
    /// stop the host asking someone else: whoever arrives first sits, and an invited seat is nobody's yet.
    /// </summary>
    public static bool HasRoomToInvite(LobbyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (LobbySeat seat in state.Seats)
        {
            if (seat.Stage is SeatStage.Open or SeatStage.Invited)
            {
                return true;
            }
        }

        return false;
    }

    private static LobbyBackRule LobbyBack(LobbyState state)
    {
        if (state.InputLocked)
        {
            // The half second after a return: B is what people arrive mashing.
            return Plain(LobbyBackAction.Nothing, LobbyScreen.Lobby, LobbyFocusKind.Unchanged);
        }

        if (state.Reconnecting)
        {
            // The wait for a lost host has a Back, and nothing to ask.
            return Plain(LobbyBackAction.LeaveLobby, LobbyScreen.JoinList, LobbyFocusKind.JoinRow);
        }

        if (state.Role == LobbyRole.Host && state.WaitingForEveryone)
        {
            return Plain(LobbyBackAction.CancelStart, LobbyScreen.Lobby, LobbyFocusKind.StartRow);
        }

        switch (state.Phase)
        {
            case LobbyPhase.Counting:
                return state.Role == LobbyRole.Host ? Plain(LobbyBackAction.CancelStart, LobbyScreen.Lobby, LobbyFocusKind.StartRow) : Plain(LobbyBackAction.Nothing, LobbyScreen.Lobby, LobbyFocusKind.Unchanged);
            case LobbyPhase.Starting or LobbyPhase.Playing or LobbyPhase.Returning:
                return Plain(LobbyBackAction.Nothing, LobbyScreen.Lobby, LobbyFocusKind.Unchanged);
        }

        if (state.Role == LobbyRole.Friend)
        {
            // B is what you mash on result screens: it asks.
            string host = state.HostSeat?.Name ?? string.Empty;
            return new LobbyBackRule(LobbyBackAction.LeaveLobby, new LobbyLine(LobbyWords.AskLeave, host), LobbyWords.Leave.Key, LobbyScreen.JoinList, new LobbyFocus(LobbyFocusKind.JoinRow));
        }

        LobbyLine? question = null;
        int friends = state.Players - 1;
        if (friends == 1)
        {
            foreach (LobbySeat seat in state.Seats)
            {
                if (!seat.IsHost && seat.Stage is not (SeatStage.Open or SeatStage.Invited))
                {
                    question = new LobbyLine(LobbyWords.AskCloseOne, seat.Name);
                }
            }
        }
        else if (friends > 1)
        {
            question = new LobbyLine(LobbyWords.AskCloseMany);
        }

        return new LobbyBackRule(LobbyBackAction.LeaveLobby, question, LobbyTerms.Close, LobbyScreen.Online, new LobbyFocus(LobbyFocusKind.HostLobbyRow));
    }

    private static LobbyBackRule Plain(LobbyBackAction action, LobbyScreen landing, LobbyFocusKind focus) =>
        new(action, null, string.Empty, landing, new LobbyFocus(focus));

    private static void AddLobbyPrompts(List<LobbyPrompt> prompts, in LobbyFocus focus, in LobbyScreenFacts facts)
    {
        LobbyState state = facts.State ?? LobbyState.None;
        if (state.InputLocked)
        {
            return;
        }

        LobbyBackRule back = LobbyBack(state);
        if (state.Phase != LobbyPhase.Open || state.Reconnecting || (state.Role == LobbyRole.Host && state.WaitingForEveryone))
        {
            // The countdown, the barrier, the wait or a lost link: B and nothing else, if B does anything.
            if (back.Action == LobbyBackAction.CancelStart)
            {
                prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Cancel, LobbyAction.CancelStart));
            }
            else if (back.Action == LobbyBackAction.LeaveLobby)
            {
                prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back));
            }

            return;
        }

        if (state.Role == LobbyRole.Host)
        {
            AddHostPrompts(prompts, focus, facts, state);
        }
        else
        {
            AddFriendPrompts(prompts, focus, facts, state);
        }
    }

    private static void AddHostPrompts(List<LobbyPrompt> prompts, in LobbyFocus focus, in LobbyScreenFacts facts, LobbyState state)
    {
        LobbySeat? seat = focus.Kind == LobbyFocusKind.Seat && (uint)focus.Index < (uint)state.Seats.Count ? state.Seats[focus.Index] : null;
        bool roomToInvite = HasRoomToInvite(state);
        if (seat is { Stage: SeatStage.Open or SeatStage.Invited } || focus.Kind == LobbyFocusKind.InviteRow)
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Invite.Key, LobbyAction.OpenInvitePanel));
        }
        else if (focus.Kind == LobbyFocusKind.StartRow && state.StartAllowed)
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Confirm, StartTerm(StartKindOf(state.Mode, state.Save)), LobbyAction.Start));
        }

        if (seat is { IsEmpty: false, Stage: not SeatStage.Invited })
        {
            if (facts.OverlayOn)
            {
                prompts.Add(new LobbyPrompt(LobbyButton.Special, LobbyWords.SteamProfile.Key, LobbyAction.OpenProfile));
            }

            if (!seat.IsHost)
            {
                prompts.Add(new LobbyPrompt(LobbyButton.LeftTrigger, LobbyWords.Remove.Key, LobbyAction.Remove));
            }
        }
        else if (focus.Kind == LobbyFocusKind.SaveRow && state.Save.HasRun)
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Special, LobbyTerms.NewRun, LobbyAction.NewRun));
        }

        if (roomToInvite)
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Special2, LobbyWords.Invite.Key, LobbyAction.OpenInvitePanel));
        }

        prompts.Add(new LobbyPrompt(LobbyButton.TabLeft, LobbyTerms.GameMode, LobbyAction.ModeLeft));
        prompts.Add(new LobbyPrompt(LobbyButton.TabRight, LobbyTerms.GameMode, LobbyAction.ModeRight));
        if (state.StartAllowed)
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Start, StartTerm(StartKindOf(state.Mode, state.Save)), LobbyAction.Start));
        }

        prompts.Add(new LobbyPrompt(LobbyButton.RightTrigger, LobbyTerms.Settings, LobbyAction.OpenSettings));
        prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyWords.CloseLobby.Key, LobbyAction.Back));
    }

    private static void AddFriendPrompts(List<LobbyPrompt> prompts, in LobbyFocus focus, in LobbyScreenFacts facts, LobbyState state)
    {
        LobbySeat? seat = focus.Kind == LobbyFocusKind.Seat && (uint)focus.Index < (uint)state.Seats.Count ? state.Seats[focus.Index] : null;
        if (seat is { IsSelf: true })
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Confirm, seat.Ready ? LobbyWords.NotReady.Key : LobbyTerms.Ready, LobbyAction.ToggleReady));
        }

        if (seat is { IsEmpty: false, Stage: not SeatStage.Invited } && facts.OverlayOn)
        {
            prompts.Add(new LobbyPrompt(LobbyButton.Special, LobbyWords.SteamProfile.Key, LobbyAction.OpenProfile));
        }

        prompts.Add(new LobbyPrompt(LobbyButton.RightTrigger, LobbyTerms.Settings, LobbyAction.OpenSettings));
        prompts.Add(new LobbyPrompt(LobbyButton.Back, LobbyWords.LeaveLobby.Key, LobbyAction.Back));
    }

    private static LobbyFocus MoveFromSeat(in LobbyFocus from, LobbyDirection direction, LobbyState state)
    {
        SeatLayout layout = LayoutFor(state.Seats.Count);
        int columns = Math.Max(layout.Columns, 1);
        int row = from.Index / columns;
        int column = from.Index % columns;
        (int dRow, int dColumn) = direction switch
        {
            LobbyDirection.Up => (-1, 0),
            LobbyDirection.Down => (1, 0),
            LobbyDirection.Left => (0, -1),
            _ => (0, 1),
        };
        for (int r = row + dRow, c = column + dColumn; r >= 0 && c >= 0 && c < columns && (r * columns) + c < state.Seats.Count; r += dRow, c += dColumn)
        {
            int next = (r * columns) + c;
            if (CanFocusSeat(state, next))
            {
                return new LobbyFocus(LobbyFocusKind.Seat, next);
            }
        }

        // Off the seats' left edge: a host's rows are there, and Start is the likeliest next press.
        return direction == LobbyDirection.Left && state.Role == LobbyRole.Host ? new LobbyFocus(LobbyFocusKind.StartRow) : from;
    }

    private static LobbyFocus MoveInColumn(in LobbyFocus from, LobbyDirection direction, LobbyState state)
    {
        if (direction == LobbyDirection.Left)
        {
            return from;
        }

        if (direction == LobbyDirection.Right)
        {
            // Into the seats: an empty seat if there is one, as that's what a host goes there for.
            int empty = FirstEmptySeat(state);
            return new LobbyFocus(LobbyFocusKind.Seat, empty >= 0 ? empty : 0);
        }

        Span<LobbyFocusKind> column = stackalloc LobbyFocusKind[4];
        int count = 0;
        column[count++] = LobbyFocusKind.ModeRow;
        if (state.Mode == LobbyMode.Campaign)
        {
            column[count++] = LobbyFocusKind.SaveRow;
        }

        if (HasRoomToInvite(state))
        {
            column[count++] = LobbyFocusKind.InviteRow;
        }

        column[count++] = LobbyFocusKind.StartRow;
        int at = 0;
        while (at < count && column[at] != from.Kind)
        {
            at++;
        }

        if (at == count)
        {
            return from;
        }

        int next = direction == LobbyDirection.Up ? at - 1 : at + 1;
        return next < 0 || next >= count ? from : new LobbyFocus(column[next]);
    }
}
