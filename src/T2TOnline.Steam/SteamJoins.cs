using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Steam;

/// <summary>Where a request to join a friend's game came from (Steam, not the mod's own keys or panel).</summary>
public enum SteamJoinSource
{
    /// <summary>
    /// A Steam invite accepted while the game runs, or "Join Game" on a friend who's in a lobby
    /// (<c>GameLobbyJoinRequested_t</c>).
    /// </summary>
    Invite,

    /// <summary>"Join Game" on a friend whose rich presence has a <c>connect</c> string (<c>GameRichPresenceJoinRequested_t</c>).</summary>
    JoinGame,

    /// <summary>The game was started by Steam for an invite or "Join Game": <c>+connect_lobby &lt;id&gt;</c> on its command line.</summary>
    Launch,
}

/// <summary>A request from Steam to join the lobby <paramref name="LobbyId"/>.</summary>
/// <param name="LobbyId">The lobby, checked to be one (<see cref="SteamJoinRequests.IsLobbyId"/>).</param>
/// <param name="FromSteamId">The friend it came through (who invited, or whose "Join Game" was used), or 0 when Steam doesn't say.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="At">When it arrived, on the caller's clock.</param>
public readonly record struct SteamJoinRequest(ulong LobbyId, ulong FromSteamId, SteamJoinSource Source, TimeSpan At);

/// <summary>What <see cref="SteamJoinInbox.Poll"/> found.</summary>
public enum SteamJoinPoll
{
    /// <summary>No request waits.</summary>
    None,

    /// <summary>A request waits for the game to be ready for it.</summary>
    Waiting,

    /// <summary>A request is ready to act on (and taken).</summary>
    Ready,

    /// <summary>A request waited too long for the game to be ready, and is dropped.</summary>
    Expired,
}

/// <summary>
/// Steam's ways of asking a game to join a lobby, as text: the <c>connect</c> rich presence this mod publishes
/// (<c>+connect_lobby &lt;id&gt;</c>, the form Steam itself uses on the command line for a lobby invite) and the command
/// line Steam starts the game with. Everything here comes from outside (a friend's rich presence, the command line), so
/// it's parsed strictly: only that form, only a Steam lobby's ID. Never throws.
/// </summary>
public static class SteamJoinRequests
{
    /// <summary>The command-line switch Steam starts a game with for a lobby invite, followed by the lobby's ID.</summary>
    public const string ConnectLobby = "+connect_lobby";

    /// <summary>The longest rich presence value Steam keeps (<c>k_cchMaxRichPresenceValueLength</c>): anything longer isn't Steam's.</summary>
    public const int MaxConnectLength = 256;

    /// <summary>Command-line arguments looked at, at most.</summary>
    public const int MaxArguments = 64;

    // A Steam ID: account (bits 0-31), instance (32-51), account type (52-55), universe (56-63). A matchmaking lobby is a
    // chat ID (type 8) in the public universe (1) whose instance has the lobby flag (k_EChatInstanceFlagLobby, 0x40000):
    // the lobbies the mod makes read 0x0186... (instance 0x60000, the lobby and matchmaking-lobby flags).
    private const ulong PublicUniverse = 1;
    private const ulong ChatAccountType = 8;
    private const ulong LobbyInstanceFlag = 0x40000;

    /// <summary>True when <paramref name="steamId"/> is a Steam matchmaking lobby's ID (not a user, a group or zero).</summary>
    public static bool IsLobbyId(ulong steamId) =>
        steamId >> 56 == PublicUniverse
        && ((steamId >> 52) & 0xF) == ChatAccountType
        && (((steamId >> 32) & 0xFFFFF) & LobbyInstanceFlag) != 0
        && (steamId & 0xFFFFFFFF) != 0;

    /// <summary>The <c>connect</c> rich presence for <paramref name="lobbyId"/>: <c>+connect_lobby &lt;id&gt;</c>.</summary>
    public static string ConnectString(ulong lobbyId) => string.Create(CultureInfo.InvariantCulture, $"{ConnectLobby} {lobbyId}");

    /// <summary>
    /// The lobby in a <c>connect</c> string (what "Join Game" hands over from a friend's rich presence): exactly
    /// <c>+connect_lobby &lt;id&gt;</c>, with any spacing around it. False for anything else, or an ID that isn't a lobby's.
    /// </summary>
    public static bool TryParseConnect(string? connect, out ulong lobbyId)
    {
        lobbyId = 0;
        if (string.IsNullOrEmpty(connect) || connect.Length > MaxConnectLength)
        {
            return false;
        }

        string[] parts = connect.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && string.Equals(parts[0], ConnectLobby, StringComparison.Ordinal) && TryParseLobby(parts[1], out lobbyId);
    }

    /// <summary>
    /// The lobby on the command line the game was started with (<paramref name="arguments"/>, the program first or not):
    /// <c>+connect_lobby</c> followed by the lobby's ID as the next argument, or both in one argument. The first valid one
    /// wins; only the first <see cref="MaxArguments"/> are looked at. False when there's none.
    /// </summary>
    public static bool TryFindInCommandLine(IReadOnlyList<string>? arguments, out ulong lobbyId)
    {
        lobbyId = 0;
        if (arguments == null)
        {
            return false;
        }

        int count = Math.Min(arguments.Count, MaxArguments);
        for (int i = 0; i < count; i++)
        {
            string? argument = arguments[i];
            if (argument == null)
            {
                continue;
            }

            if (string.Equals(argument, ConnectLobby, StringComparison.Ordinal))
            {
                if (i + 1 < count && TryParseLobby(arguments[i + 1], out lobbyId))
                {
                    return true;
                }
            }
            else if (argument.StartsWith(ConnectLobby, StringComparison.Ordinal) && TryParseConnect(argument, out lobbyId))
            {
                return true;
            }
        }

        lobbyId = 0;
        return false;
    }

    private static bool TryParseLobby(string? text, out ulong lobbyId)
    {
        lobbyId = 0;
        if (text == null || text.Length is 0 or > 20 ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || !IsLobbyId(id))
        {
            return false;
        }

        lobbyId = id;
        return true;
    }
}

/// <summary>
/// The request to join a friend's game that Steam made last, until the game can act on it: Steam's own callbacks arrive
/// inside the game's update, and a game Steam started for an invite has to finish starting first. One request waits at a
/// time, the newest (a player who accepts two invites wants the second). A request made while the game runs waits up to
/// <see cref="JoinWait"/> for the game to be free for it (its own run, exhibition or Settings: held, never refused); one from
/// the command line also waits for the game to finish starting, up to <see cref="LaunchWait"/>. Safe to offer from any
/// thread; polled on one.
/// </summary>
public sealed class SteamJoinInbox
{
    /// <summary>How long a request made while the game runs (an invite, "Join Game") waits for the game to be free for it.</summary>
    public static readonly TimeSpan JoinWait = TimeSpan.FromMinutes(3);

    /// <summary>How long a request from the command line waits for the game to finish starting.</summary>
    public static readonly TimeSpan LaunchWait = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private SteamJoinRequest? _waiting;

    /// <summary>
    /// Keeps <paramref name="request"/> as the one to act on, replacing any waiting (<paramref name="replaced"/>, else
    /// default). False, keeping nothing, when its lobby isn't a lobby's ID.
    /// </summary>
    public bool Offer(SteamJoinRequest request, out SteamJoinRequest replaced)
    {
        replaced = default;
        if (!SteamJoinRequests.IsLobbyId(request.LobbyId))
        {
            return false;
        }

        lock (_gate)
        {
            if (_waiting is { } waiting)
            {
                replaced = waiting;
            }

            _waiting = request;
            return true;
        }
    }

    /// <summary>
    /// The waiting request, if it can be acted on now: <paramref name="steamReady"/> (Steam is up and the session can
    /// start), and for one from the command line also <paramref name="gameStarted"/> (the game has finished starting).
    /// <see cref="SteamJoinPoll.Ready"/> and <see cref="SteamJoinPoll.Expired"/> take it (<paramref name="request"/> is
    /// it); <see cref="SteamJoinPoll.Waiting"/> leaves it waiting (<paramref name="request"/> is it too).
    /// </summary>
    public SteamJoinPoll Poll(TimeSpan now, bool steamReady, bool gameStarted, out SteamJoinRequest request)
    {
        lock (_gate)
        {
            if (_waiting is not { } waiting)
            {
                request = default;
                return SteamJoinPoll.None;
            }

            request = waiting;
            bool launch = waiting.Source == SteamJoinSource.Launch;
            if (steamReady && (gameStarted || !launch))
            {
                _waiting = null;
                return SteamJoinPoll.Ready;
            }

            if (now - waiting.At > (launch ? LaunchWait : JoinWait))
            {
                _waiting = null;
                return SteamJoinPoll.Expired;
            }

            return SteamJoinPoll.Waiting;
        }
    }

    /// <summary>Drops the waiting request, if any.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _waiting = null;
        }
    }
}
