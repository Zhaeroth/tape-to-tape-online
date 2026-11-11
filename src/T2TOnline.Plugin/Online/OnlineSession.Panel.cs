#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// What the developer's panel (F1, Online/ModPanel.cs) reads about the session, and the friends' games it lists to join (the
// panel calls HostGame, JoinGame and FindGames in OnlineSession.cs); apart from what the session does (OnlineSession.cs)
// and its log and status lines (OnlineSession.Text.cs).
internal sealed partial class OnlineSession
{
    private IReadOnlyList<JoinableGame> _games = Array.Empty<JoinableGame>();
    private readonly Dictionary<ulong, (VirtualController Pad, InputBindingSet? Controls, IReadOnlyList<string> Names)> _boundActions = new();

    /// <summary>True while the net overlay (F9) is on.</summary>
    public bool NetOverlayVisible => _net.Visible;

    /// <summary>
    /// Friends' games from the last <see cref="FindGames"/> or while a browser is open (<see cref="OpenBrowser"/>), in the
    /// order they appeared: a new list whenever an entry changes (details arrive within a second or two), the same one in
    /// between. Empty before the first look. OnlineSession.Browser.cs keeps it.
    /// </summary>
    public IReadOnlyList<JoinableGame> JoinableGames => _games;

    /// <summary>Host: the seats of friends who dropped and are kept for them (D22), by Steam ID and name, for the panel.</summary>
    public IReadOnlyList<(ulong SteamId, string Name)> ParkedSeats =>
        _parked.Select(pair => (pair.Key, pair.Value.Pad.DisplayName)).Where(seat => seat.Key != _self).ToList();

    /// <summary>
    /// Host: frees a dropped friend's seat now, so a newcomer can take it: their parked virtual pad and game
    /// player go at the next update, or once the match in play is over (removing a player mid-match pauses it for the
    /// controller screen), as when the two minutes run out. False when <paramref name="steamId"/> has no parked seat.
    /// </summary>
    public bool FreeSeat(ulong steamId)
    {
        if (steamId == _self || !_parked.TryGetValue(steamId, out (RemotePad Pad, TimeSpan Until) parked))
        {
            return false;
        }

        _parked[steamId] = (parked.Pad, _clock.Elapsed);
        ModLog.Info($"Online: freeing {parked.Pad.Tag}'s seat (the host's choice); their player goes " +
            $"{(MatchSetup.CurrentMatch(out _).IsNone ? "now" : "when this match is over")}.");
        return true;
    }

    /// <summary>The session as the panel shows it, made when asked: what this PC is doing, and who's in it with their ping.</summary>
    public SessionSummary Summarize()
    {
        var players = new List<SessionPlayer>();
        SessionPhase phase;
        string hostName = string.Empty;
        int friends = 0;
        if (!T2TOnlinePlugin.OnlineAvailable && !InLobby)
        {
            phase = SessionPhase.Unavailable;
        }
        else if (_broken)
        {
            phase = SessionPhase.Broken;
        }
        else if (!_started || _self == 0)
        {
            phase = _started ? SessionPhase.NoSteam : SessionPhase.WaitingForSteam;
        }
        else if (_host != null)
        {
            phase = _solo ? SessionPhase.OnePcTest : SessionPhase.Hosting;
            friends = _pads.Count;
            players.Add(new SessionPlayer("You", "host", -1, string.Empty, IsYou: true));
            foreach (RemoteFriend friend in _host.Friends)
            {
                if (!_pads.TryGetValue(friend.SteamId, out RemotePad? pad))
                {
                    continue;
                }

                string note = friend.Link.IsLagging ? "lagging" : friend.PadConnected ? string.Empty : AwayLabel(friend.AwayReason);
                if (_standIn.IsStandingIn(pad.Pad.RewiredPlayerId))
                {
                    note = note.Length == 0 ? "the AI plays for them" : $"{note}, the AI plays for them";
                }

                players.Add(new SessionPlayer(pad.DisplayName, pad.GamePlayer >= 0 ? $"P{pad.GamePlayer + 1}" : "not joined yet", Millis(friend.Link.LastRoundTripMicros), note, IsYou: false));
            }

            foreach ((RemotePad pad, _) in _parked.Values)
            {
                players.Add(new SessionPlayer(pad.DisplayName, "dropped", -1, "keeping their player for now", IsYou: false));
            }
        }
        else if (_friend != null)
        {
            phase = _everJoined && _friend.State == FriendSessionState.Connecting ? SessionPhase.Rejoining
                : _friend.State == FriendSessionState.Joined ? SessionPhase.Joined
                : SessionPhase.Joining;
            hostName = _hostName;
            players.Add(new SessionPlayer(_hostName, "host", Millis(_friend.Link.LastRoundTripMicros), _friend.Link.IsLagging ? "silent" : string.Empty, IsYou: false));
            players.Add(new SessionPlayer("You", _friend.State != FriendSessionState.Joined ? "joining" : DeviceWithHost ? PadLine().Trim() : "in their lobby", -1, string.Empty, IsYou: true));
        }
        else if (_link.Creating)
        {
            phase = SessionPhase.Creating;
        }
        else if (_link.Joining)
        {
            phase = SessionPhase.Joining;
            hostName = _link.JoiningName;
        }
        else
        {
            phase = InLobby ? SessionPhase.Joining : SessionPhase.Idle;
        }

        return new SessionSummary(phase, hostName, players, InLobby, friends, PlayOn: _friend != null && DeviceWithHost);
    }

    /// <summary>A round trip in whole milliseconds (-1: none measured).</summary>
    private static long Millis(long micros) => micros < 0 ? -1 : micros / 1000;

    /// <summary>
    /// What this PC plays with in a session it joined (the one-PC test's friend side too): a pad or the keyboard and mouse;
    /// null outside one, or while it's still choosing. For the panel's Controller tab. Read only.
    /// </summary>
    public InputKind? PlaysWith => _friend == null ? null : _localKeyboard != null ? InputKind.KeyboardMouse : _localPad != null ? InputKind.Gamepad : null;

    /// <summary>
    /// Host: each friend's controller in the session (the one-PC test's too; not a dropped friend's, which stands still):
    /// the game actions it gave the game so far (<see cref="VirtualController.ActionsSeen"/>) and the ones its maps bind,
    /// for the panel's Controller tab. A new list each time; empty outside hosting. Read only: nothing here changes a
    /// controller.
    /// </summary>
    public IReadOnlyList<FriendController> FriendControllers()
    {
        if (_host == null || _pads.Count == 0)
        {
            _boundActions.Clear();
            return Array.Empty<FriendController>();
        }

        var friends = new List<FriendController>(_pads.Count);
        foreach ((ulong steamId, RemotePad pad) in _pads)
        {
            VirtualController controller = pad.Pad;
            (IReadOnlyList<string> given, int of) = controller.ActionsSeen();
            string kind = controller.IsKeyboard ? "keyboard and mouse"
                : $"pad read as {VirtualPad.FamilyName(controller.DeviceType)}{(controller.Controls != null ? ", their own maps" : string.Empty)}";
            friends.Add(new FriendController(steamId, steamId == _self ? $"{pad.DisplayName} (the one-PC test)" : pad.DisplayName, kind, given, BoundActions(steamId, controller), of));
        }

        foreach (ulong gone in _boundActions.Keys.Where(id => !_pads.ContainsKey(id)).ToList())
        {
            _boundActions.Remove(gone);
        }

        return friends;
    }

    /// <summary>
    /// The game's actions a friend's controller's maps bind, by name in the game's order: their own bindings, or for a pad
    /// on the game's defaults the gamepad template maps its virtual pad was bound from. Made again only when its controls change.
    /// </summary>
    private IReadOnlyList<string> BoundActions(ulong steamId, VirtualController controller)
    {
        InputBindingSet? controls = controller.Controls;
        if (_boundActions.TryGetValue(steamId, out (VirtualController Pad, InputBindingSet? Controls, IReadOnlyList<string> Names) known) &&
            ReferenceEquals(known.Pad, controller) && ReferenceEquals(known.Controls, controls))
        {
            return known.Names;
        }

        IEnumerable<InputBinding> bindings = controls != null ? controls.Bindings
            : controller is VirtualPad ? RewiredBindings.PadDefaults(VirtualPadDefinition.MapCategoryIds, out _)
            : Array.Empty<InputBinding>();
        var bound = new HashSet<int>(bindings.Select(b => (int)b.ActionId));
        IReadOnlyList<string> names = RewiredBindings.ActionIds().Where(bound.Contains).Select(RewiredBindings.ActionName).ToList();
        _boundActions[steamId] = (controller, controls, names);
        return names;
    }
}

/// <summary>A friend's controller on the host, for the panel's Controller tab (<see cref="OnlineSession.FriendControllers"/>).</summary>
/// <param name="SteamId">The friend's Steam ID.</param>
/// <param name="Name">Their name, as the status box shows it.</param>
/// <param name="Kind">What it is here ("pad read as Xbox, their own maps", "keyboard and mouse").</param>
/// <param name="Given">The game's actions it gave the game so far this session, by name in the game's order.</param>
/// <param name="Bound">The game's actions its maps bind, by name in the game's order.</param>
/// <param name="GameActions">How many actions the game has (those a virtual controller can give).</param>
internal sealed record FriendController(ulong SteamId, string Name, string Kind, IReadOnlyList<string> Given, IReadOnlyList<string> Bound, int GameActions);

/// <summary>What this PC's online session is doing, for the mod's panel.</summary>
internal enum SessionPhase
{
    /// <summary>Online is off this session (an untested game build, a failed check, or crash reporting that may be live).</summary>
    Unavailable,

    /// <summary>Online is off after an error; a restart brings it back.</summary>
    Broken,

    /// <summary>The game hasn't started Steam yet.</summary>
    WaitingForSteam,

    /// <summary>Steam isn't available.</summary>
    NoSteam,

    /// <summary>Ready, not in a session.</summary>
    Idle,

    /// <summary>Creating a lobby to host.</summary>
    Creating,

    /// <summary>Joining a friend's lobby or session.</summary>
    Joining,

    /// <summary>Hosting.</summary>
    Hosting,

    /// <summary>Hosting, with the one-PC test running (Ctrl+F6).</summary>
    OnePcTest,

    /// <summary>In a friend's session: in their lobby (the device and game stay this PC's own), or, once they start (<see cref="SessionSummary.PlayOn"/>), this PC's pad plays on their game.</summary>
    Joined,

    /// <summary>The host went quiet; rejoining by itself.</summary>
    Rejoining,
}

/// <summary>The session for the mod's panel: its phase, the host's name (as a friend), the players, whether a lobby is in use, (hosting) how many friends have a pad here, and (as a friend) whether the device is on the host's game yet.</summary>
internal sealed record SessionSummary(SessionPhase Phase, string HostName, IReadOnlyList<SessionPlayer> Players, bool InLobby, int Friends, bool PlayOn = false);

/// <summary>One player in the session: their name, seat or state, ping in ms (-1: none measured), a note, and whether it's this PC's player.</summary>
internal sealed record SessionPlayer(string Name, string Seat, long PingMs, string Note, bool IsYou);

/// <summary>
/// A friend's game the panel or the native browser can offer to join (<see cref="OnlineSession.JoinableGames"/>; join it with
/// <see cref="OnlineSession.JoinGame"/>). Text is cleaned for display. What the host did not say stays absent (Unknown, null,
/// -1, 0): nothing is guessed.
/// </summary>
/// <param name="LobbyId">Its Steam lobby.</param>
/// <param name="HostName">Who hosts it (the friends seen in it until its details arrive, or when the lobby names no host).</param>
/// <param name="ModVersion">Its T2T Online version, empty until its details arrive.</param>
/// <param name="Players">How many play in it (the host's count; before that, the friends seen in it).</param>
/// <param name="Note">"checking..." until its details arrive, then empty, or why it can't be joined.</param>
/// <param name="Joinable">False only when its details say it can't be joined here (another T2T Online version or game update, not a T2T Online game).</param>
/// <param name="Checked">True once its details arrived, or Steam didn't send them in time.</param>
/// <param name="HostId">The host's Steam ID once the lobby's data names a host who is a friend in it; 0 before that.</param>
/// <param name="Mode">The game mode it is set for.</param>
/// <param name="State">Whether it is in its lobby or playing (it can be joined either way).</param>
/// <param name="Save">The co-op save it is set to continue, or null.</param>
/// <param name="PingMs">An estimate of the round trip to the host in ms (Steam's relay data and the host's ping location: an upper bound, not a measurement), or -1.</param>
/// <param name="Compat">Whether the host's T2T Online and game build match this PC's, and which side is behind if not (<see cref="ListedCompat.Unchecked"/> until the details arrive).</param>
/// <param name="Invited">True when Steam delivered an invite to this game and it hasn't expired.</param>
/// <param name="Answered">True when the details really arrived (<see cref="Checked"/> is also true when Steam never sent them).</param>
internal sealed record JoinableGame(
    ulong LobbyId,
    string HostName,
    string ModVersion,
    int Players,
    string Note,
    bool Joinable,
    bool Checked,
    ulong HostId = 0,
    ListedMode Mode = ListedMode.Unknown,
    ListedState State = ListedState.Unknown,
    ListedSave? Save = null,
    int PingMs = -1,
    ListedCompat Compat = ListedCompat.Unchecked,
    bool Invited = false,
    bool Answered = false);
#endif
