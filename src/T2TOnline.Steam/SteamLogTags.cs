using System;

namespace T2TOnline.Steam;

/// <summary>
/// How this library's log lines name players and lobbies: by the tags the plugin's log uses (D127: <c>P#a0c1d2</c>,
/// <c>L#7d20e1</c>), never by a Steam name or ID. The plugin sets both once, to its log's tags in use at the time of each
/// call; until then (and in tests) every player and lobby reads as unknown. Main thread only, like the rest of this library;
/// a throw is swallowed.
/// </summary>
public static class SteamLogTags
{
    /// <summary>What a player is called when there's no tagger (or it failed).</summary>
    public const string UnknownPlayer = "P#??????";

    /// <summary>What a lobby is called when there's no tagger (or it failed).</summary>
    public const string UnknownLobby = "L#??????";

    /// <summary>The tag of a player's Steam ID, as the plugin's log writes it. Optional; set once by the plugin.</summary>
    public static Func<ulong, string>? PlayerTag { get; set; }

    /// <summary>The tag of a lobby's Steam ID, as the plugin's log writes it. Optional; set once by the plugin.</summary>
    public static Func<ulong, string>? LobbyTag { get; set; }

    /// <summary>The log's name for the player <paramref name="steamId"/>.</summary>
    public static string Player(ulong steamId) => Call(PlayerTag, steamId, UnknownPlayer);

    /// <summary>The log's name for the lobby <paramref name="lobbyId"/>.</summary>
    public static string Lobby(ulong lobbyId) => Call(LobbyTag, lobbyId, UnknownLobby);

    private static string Call(Func<ulong, string>? tagger, ulong id, string unknown)
    {
        if (tagger == null)
        {
            return unknown;
        }

        try
        {
            return tagger(id) is { Length: > 0 } tag ? tag : unknown;
        }
        catch (Exception)
        {
            return unknown; // a log line never fails for its tag
        }
    }
}
