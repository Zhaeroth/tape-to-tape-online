using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Steam;

/// <summary>What this PC's Steam rich presence says about its session.</summary>
/// <param name="LobbyId">The session's Steam lobby: friends' "Join Game" joins it.</param>
/// <param name="Hosting">True on the host, false on a friend in the host's session.</param>
/// <param name="Players">How many play in the session now.</param>
/// <param name="MaxPlayers">How many can.</param>
public readonly record struct SteamPresenceState(ulong LobbyId, bool Hosting, int Players, int MaxPlayers);

/// <summary>
/// The rich presence keys this mod sets on Steam while in a session, so friends can join it from Steam's friends list
/// ("Join Game") and see what it is: <c>connect</c> (<see cref="SteamJoinRequests.ConnectString"/>, handed to the joining
/// friend's game), <c>status</c> (the text Steam shows in the friend's game info; <c>steam_display</c> would need a
/// localization token in the game's own Steam settings, which only its developers can add), and
/// <c>steam_player_group</c> with <c>steam_player_group_size</c> (Steam lists players of one session together). Only keys
/// that changed are sent, and they're deleted (set empty) when the session ends; keys the mod didn't set are never
/// touched. The game itself sets no rich presence. The status names nobody (D129): a friend's says it's in a friend's
/// game, as the host's says it hosts one, since everyone on that friend's Steam friends list sees it, strangers to the host
/// too.
/// </summary>
public sealed class SteamPresence
{
    /// <summary>The key "Join Game" hands to a joining friend's game.</summary>
    public const string ConnectKey = "connect";

    /// <summary>The key Steam shows as text in a friend's view of the game.</summary>
    public const string StatusKey = "status";

    /// <summary>The key Steam groups players of one session by.</summary>
    public const string GroupKey = "steam_player_group";

    /// <summary>The key with the group's size.</summary>
    public const string GroupSizeKey = "steam_player_group_size";

    private static readonly string[] Keys = { ConnectKey, StatusKey, GroupKey, GroupSizeKey };

    private readonly Func<string, string?, bool> _set;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private SteamPresenceState? _shown;

    /// <summary>Sets the keys on Steam (<see cref="SteamFriends.SetRichPresence"/>).</summary>
    public SteamPresence()
        : this(SteamFriends.SetRichPresence)
    {
    }

    /// <summary>Sets the keys through <paramref name="set"/> (key, value or null to delete; false when refused): for tests.</summary>
    internal SteamPresence(Func<string, string?, bool> set) => _set = set ?? throw new ArgumentNullException(nameof(set));

    /// <summary>Sets Steam refused (or couldn't be asked to make: Steam not available) so far.</summary>
    public int Refused { get; private set; }

    /// <summary>What the keys say now, or null when they're cleared.</summary>
    public SteamPresenceState? Shown => _shown;

    /// <summary>The status text for <paramref name="state"/>, as friends see it in Steam.</summary>
    public static string Status(SteamPresenceState state)
    {
        string players = string.Create(CultureInfo.InvariantCulture, $"{Math.Max(state.Players, 1)} of {Math.Max(state.MaxPlayers, state.Players)} players");
        return state.Hosting ? $"T2T Online: hosting a co-op game · {players}" : $"T2T Online: in a friend's co-op game · {players}";
    }

    /// <summary>
    /// Makes the keys say <paramref name="state"/> (null clears them). Does nothing when that's what they say already, so it
    /// can be called every frame. True when anything was sent to Steam.
    /// </summary>
    public bool Show(SteamPresenceState? state)
    {
        if (Nullable.Equals(state, _shown))
        {
            return false;
        }

        _shown = state;
        bool sent = false;
        foreach (string key in Keys)
        {
            string value = state is { } shown ? ValueOf(key, shown) : string.Empty;
            string had = _values.TryGetValue(key, out string? previous) ? previous : string.Empty;
            if (string.Equals(value, had, StringComparison.Ordinal))
            {
                continue;
            }

            sent = true;
            // Taken as set either way: a refusal isn't asked again every frame, only with the next change.
            _values[key] = value;
            if (!_set(key, value.Length == 0 ? null : value))
            {
                Refused++;
            }
        }

        return sent;
    }

    private static string ValueOf(string key, SteamPresenceState state) => key switch
    {
        ConnectKey => SteamJoinRequests.ConnectString(state.LobbyId),
        StatusKey => Status(state),
        GroupKey => state.LobbyId.ToString(CultureInfo.InvariantCulture),
        GroupSizeKey => Math.Max(state.Players, 1).ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };
}
