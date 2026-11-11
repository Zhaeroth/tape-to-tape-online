using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Steam;

/// <summary>
/// The Steam lobby data keys a T2T Online host publishes, which friends read (without joining) to choose a game. Only the
/// lobby's owner can set them, and Steam batches the changes, so the host changes them rarely.
/// </summary>
public static class LobbyDataKeys
{
    /// <summary>The host's mod version (<c>0.9.2-beta</c>); absent for a lobby that is not a T2T Online one.</summary>
    public const string Mod = "t2t.mod";

    /// <summary>The wire protocol number: both sides need the same one.</summary>
    public const string Protocol = "t2t.protocol";

    /// <summary>The game build both sides run (hex): both need the same one.</summary>
    public const string Build = "t2t.build";

    /// <summary>
    /// The host's Steam ID: friends say hello to it, not to the lobby's owner, which Steam hands to a friend when the host
    /// falls out of the lobby with friends in it.
    /// </summary>
    public const string Host = "t2t.host";

    /// <summary>How many play in the session (the host and the friends with a seat, kept seats included).</summary>
    public const string Players = "t2t.players";

    /// <summary><see cref="StateLobby"/> or <see cref="StatePlay"/>: whether the session is in its lobby or playing.</summary>
    public const string State = "t2t.state";

    /// <summary><see cref="ModeCampaign"/> or <see cref="ModeQuick"/>: the game mode the lobby is set for.</summary>
    public const string Mode = "t2t.mode";

    /// <summary>The co-op save the lobby is set to continue: <c>squad,act,floor</c>, or empty (see <see cref="ListedSave"/>).</summary>
    public const string Save = "t2t.save";

    /// <summary>
    /// The key for the log tags of the host's hosting session (D127): 32 lowercase hex digits, the same across a re-made
    /// lobby. Every PC in the lobby names players in its log by tags under it, so the logs match. Never logged.
    /// </summary>
    public const string LogTag = "t2t.logtag";

    /// <summary>The host's Steam ping location string (<see cref="SteamPing.TryGetLocalLocation"/>): friends estimate their ping from it.</summary>
    public const string PingLocation = "t2t.pingloc";

    /// <summary>A <see cref="State"/> value: in the lobby, before a game starts.</summary>
    public const string StateLobby = "lobby";

    /// <summary>A <see cref="State"/> value: a game is on.</summary>
    public const string StatePlay = "play";

    /// <summary>A <see cref="Mode"/> value: the co-op campaign.</summary>
    public const string ModeCampaign = "campaign";

    /// <summary>A <see cref="Mode"/> value: a Quick Game (an exhibition).</summary>
    public const string ModeQuick = "quick";
}

/// <summary>The game mode a listed lobby is set for (<see cref="LobbyDataKeys.Mode"/>).</summary>
public enum ListedMode
{
    /// <summary>Not said, or something this version doesn't know.</summary>
    Unknown = 0,

    /// <summary>The co-op campaign.</summary>
    Campaign = 1,

    /// <summary>A Quick Game.</summary>
    Quick = 2,
}

/// <summary>Whether a listed lobby is still in its lobby or playing (<see cref="LobbyDataKeys.State"/>).</summary>
public enum ListedState
{
    /// <summary>Not said, or something this version doesn't know.</summary>
    Unknown = 0,

    /// <summary>In the lobby: no game has started.</summary>
    Lobby = 1,

    /// <summary>A game is on (it can still be joined).</summary>
    Playing = 2,
}

/// <summary>
/// The co-op save a lobby is set to continue, as the host publishes it (<see cref="LobbyDataKeys.Save"/>, <c>squad,act,floor</c>).
/// Read from another PC, so every part is checked: a squad of 1 to <see cref="MaxSquadLength"/> printable characters without a
/// comma, an act of 0 to <see cref="MaxAct"/>, a floor of 0 to <see cref="MaxFloor"/>.
/// </summary>
/// <param name="Squad">The squad, as the host's game names it.</param>
/// <param name="Act">The run's act.</param>
/// <param name="Floor">The run's floor.</param>
public sealed record ListedSave(string Squad, int Act, int Floor)
{
    /// <summary>The longest squad text kept.</summary>
    public const int MaxSquadLength = 48;

    /// <summary>The highest act accepted.</summary>
    public const int MaxAct = 99;

    /// <summary>The highest floor accepted.</summary>
    public const int MaxFloor = 999;

    /// <summary>
    /// The save in <paramref name="text"/> (<c>squad,act,floor</c>). False for empty text (no save), the wrong number of parts,
    /// a squad that is empty, too long or has control characters, or a number out of range.
    /// </summary>
    public static bool TryParse(string? text, out ListedSave save)
    {
        save = null!;
        if (string.IsNullOrEmpty(text) || text.Length > MaxSquadLength + 16)
        {
            return false;
        }

        string[] parts = text.Split(',');
        if (parts.Length != 3 || !IsSquad(parts[0]) ||
            !TryNumber(parts[1], MaxAct, out int act) || !TryNumber(parts[2], MaxFloor, out int floor))
        {
            return false;
        }

        save = new ListedSave(parts[0], act, floor);
        return true;
    }

    /// <summary>
    /// <paramref name="squad"/> made fit for the save text: control characters and commas dropped, leading and trailing spaces
    /// trimmed, cut to <see cref="MaxSquadLength"/>. Empty when nothing is left.
    /// </summary>
    public static string CleanSquad(string? squad)
    {
        if (string.IsNullOrEmpty(squad))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(Math.Min(squad.Length, MaxSquadLength));
        foreach (char c in squad)
        {
            if (builder.Length >= MaxSquadLength)
            {
                break;
            }

            if (c != ',' && !char.IsControl(c) && !char.IsSurrogate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The text published for this save (<c>squad,act,floor</c>), or empty when the squad cleans to nothing.</summary>
    public string ToText()
    {
        string squad = CleanSquad(Squad);
        return squad.Length == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{squad},{Math.Clamp(Act, 0, MaxAct)},{Math.Clamp(Floor, 0, MaxFloor)}");
    }

    private static bool IsSquad(string squad)
    {
        if (squad.Length is 0 or > MaxSquadLength || squad.Trim().Length != squad.Length)
        {
            return false;
        }

        foreach (char c in squad)
        {
            if (char.IsControl(c) || char.IsSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryNumber(string text, int max, out int value)
    {
        value = 0;
        return text.Length is >= 1 and <= 4
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value <= max;
    }
}

/// <summary>Whether a listed lobby's host runs a T2T Online this PC can play with, and if not why (read from its lobby data).</summary>
public enum ListedCompat
{
    /// <summary>Not known yet: the lobby's details haven't arrived.</summary>
    Unchecked = 0,

    /// <summary>The same protocol and game build (another release of the same protocol is fine).</summary>
    Compatible = 1,

    /// <summary>The lobby has no T2T Online data: another mod, or something else.</summary>
    NotT2TOnline = 2,

    /// <summary>The host's protocol is older than ours: the host needs the newer zip.</summary>
    TheirsIsOlder = 3,

    /// <summary>The host's protocol is newer than ours: this PC needs the newer zip.</summary>
    YoursIsOlder = 4,

    /// <summary>The host's protocol isn't a number this version understands.</summary>
    UnknownVersion = 5,

    /// <summary>The same protocol but another game update: both need the same one.</summary>
    OtherGameBuild = 6,
}

/// <summary>Which lobby data a PC can read of a friend's lobby, and the rules for judging it.</summary>
public static class ListedLobby
{
    /// <summary>
    /// Whether the host of a lobby with these <paramref name="mod"/>, <paramref name="protocol"/> and <paramref name="build"/>
    /// values can play with a PC on <paramref name="ourProtocol"/> and <paramref name="ourBuild"/>. Which side is behind is
    /// said by comparing the protocol numbers; game builds are hashes and can only differ.
    /// </summary>
    public static ListedCompat CheckCompat(string? mod, string? protocol, string? build, int ourProtocol, string ourBuild)
    {
        if (string.IsNullOrEmpty(mod))
        {
            return ListedCompat.NotT2TOnline;
        }

        if (string.IsNullOrEmpty(protocol) || protocol.Length > 6 ||
            !int.TryParse(protocol, NumberStyles.None, CultureInfo.InvariantCulture, out int theirs))
        {
            return ListedCompat.UnknownVersion;
        }

        if (theirs != ourProtocol)
        {
            return theirs < ourProtocol ? ListedCompat.TheirsIsOlder : ListedCompat.YoursIsOlder;
        }

        return string.Equals(build, ourBuild, StringComparison.OrdinalIgnoreCase) ? ListedCompat.Compatible : ListedCompat.OtherGameBuild;
    }

    /// <summary>The mode in a <see cref="LobbyDataKeys.Mode"/> value (<see cref="ListedMode.Unknown"/> for anything else).</summary>
    public static ListedMode ParseMode(string? value) => value switch
    {
        LobbyDataKeys.ModeCampaign => ListedMode.Campaign,
        LobbyDataKeys.ModeQuick => ListedMode.Quick,
        _ => ListedMode.Unknown,
    };

    /// <summary>The state in a <see cref="LobbyDataKeys.State"/> value (<see cref="ListedState.Unknown"/> for anything else).</summary>
    public static ListedState ParseState(string? value) => value switch
    {
        LobbyDataKeys.StateLobby => ListedState.Lobby,
        LobbyDataKeys.StatePlay => ListedState.Playing,
        _ => ListedState.Unknown,
    };

    /// <summary>The text published for a mode, or empty for <see cref="ListedMode.Unknown"/>.</summary>
    public static string ModeText(ListedMode mode) => mode switch
    {
        ListedMode.Campaign => LobbyDataKeys.ModeCampaign,
        ListedMode.Quick => LobbyDataKeys.ModeQuick,
        _ => string.Empty,
    };

    /// <summary>The text published for a state, or empty for <see cref="ListedState.Unknown"/>.</summary>
    public static string StateText(ListedState state) => state switch
    {
        ListedState.Lobby => LobbyDataKeys.StateLobby,
        ListedState.Playing => LobbyDataKeys.StatePlay,
        _ => string.Empty,
    };

    /// <summary>The player count in a <see cref="LobbyDataKeys.Players"/> value, 1 to 99, or 0 when it is absent or not that.</summary>
    public static int ParsePlayers(string? value) =>
        value is { Length: >= 1 and <= 2 } && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count is >= 1 and <= 99
            ? count
            : 0;

    /// <summary>A Steam ID in a <see cref="LobbyDataKeys.Host"/> value, or 0 when it is absent or not a number.</summary>
    public static ulong ParseHost(string? value) =>
        value is { Length: >= 1 and <= 20 } && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;
}

/// <summary>Whether Steam has answered for a lobby the browser lists (<see cref="SteamLobbyListing.Answer"/>).</summary>
public enum ListingAnswer
{
    /// <summary>The details were asked for and haven't been read (yet).</summary>
    Checking,

    /// <summary>The details arrived and the lobby's host is a friend who is in it.</summary>
    Answered,

    /// <summary>Steam sent nothing in time (the lobby may have closed, or Steam is slow): joining can still be tried.</summary>
    NoAnswer,
}

/// <summary>
/// A friend's lobby as the browser knows it, from the friends list (who is in it) and, once Steam has answered, its lobby data.
/// Strings are as other PCs and Steam gave them, cut to a length but not cleaned: clean them before showing them. A new
/// instance each time anything changes, the same one in between. Absent values stay absent (<c>Unknown</c>, 0, null, -1);
/// none is guessed.
/// </summary>
public sealed class SteamLobbyListing
{
    /// <summary>Lists lobby <paramref name="lobbyId"/>, the <paramref name="sequence"/>th to be listed.</summary>
    public SteamLobbyListing(ulong lobbyId, int sequence)
    {
        LobbyId = lobbyId;
        Sequence = sequence;
    }

    /// <summary>The lobby.</summary>
    public ulong LobbyId { get; }

    /// <summary>Order of first being listed (a lobby that appears is listed after those already there).</summary>
    public int Sequence { get; }

    /// <summary>The host's Steam ID once the lobby's data names a host who is a friend in it; 0 before that, and for a lobby that names none.</summary>
    public ulong HostId { get; init; }

    /// <summary>The host's persona name once <see cref="HostId"/> is known; otherwise empty (see <see cref="FriendNames"/>).</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>The names of the friends the friends list shows in the lobby (up to <see cref="SteamLobbyBrowser.MaxFriendNames"/>).</summary>
    public IReadOnlyList<string> FriendNames { get; init; } = Array.Empty<string>();

    /// <summary>How many friends the friends list shows in the lobby.</summary>
    public int FriendsSeen { get; init; }

    /// <summary>Whether Steam has answered.</summary>
    public ListingAnswer Answer { get; init; }

    /// <summary>The host's mod version, or empty.</summary>
    public string ModVersion { get; init; } = string.Empty;

    /// <summary>The host's protocol number as text, or empty.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>The host's game build as text, or empty.</summary>
    public string Build { get; init; } = string.Empty;

    /// <summary>How many play (the host's count), or 0 when it isn't given.</summary>
    public int Players { get; init; }

    /// <summary>The mode the lobby is set for.</summary>
    public ListedMode Mode { get; init; }

    /// <summary>Whether the lobby is in its lobby or playing.</summary>
    public ListedState State { get; init; }

    /// <summary>The co-op save the lobby is set to continue, or null when it names none (or its text was not a save).</summary>
    public ListedSave? Save { get; init; }

    /// <summary>
    /// An estimate of the round trip to the host in ms, from Steam's relay data and the host's ping location: an upper bound
    /// for a direct route, not a measurement. -1 when there is none (no location, Steam can't estimate yet).
    /// </summary>
    public int PingMs { get; init; } = -1;

    internal bool SameAs(SteamLobbyListing other) =>
        LobbyId == other.LobbyId && HostId == other.HostId && HostName == other.HostName && FriendsSeen == other.FriendsSeen
        && Answer == other.Answer && ModVersion == other.ModVersion && Protocol == other.Protocol && Build == other.Build
        && Players == other.Players && Mode == other.Mode && State == other.State && Equals(Save, other.Save) && PingMs == other.PingMs
        && FriendNames.Count == other.FriendNames.Count && FriendNamesEqual(other);

    private bool FriendNamesEqual(SteamLobbyListing other)
    {
        for (int i = 0; i < FriendNames.Count; i++)
        {
            if (!string.Equals(FriendNames[i], other.FriendNames[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
