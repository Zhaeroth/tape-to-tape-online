using System;
using System.Collections.Generic;

namespace T2TOnline.Steam;

/// <summary>
/// What a host tells friends about its lobby before they join: whether it is in its lobby or playing, the game mode, and the
/// co-op save it is set to continue. The session sets it as it changes; <see cref="SteamLobbyAdvertiser"/> publishes it as
/// lobby data (<see cref="LobbyDataKeys"/>).
/// </summary>
/// <param name="State">In the lobby, or a game is on.</param>
/// <param name="Mode">The game mode.</param>
/// <param name="Save">The co-op save a campaign continues, or null when there is none (a new run, a Quick Game).</param>
public sealed record LobbyAdvert(ListedState State, ListedMode Mode, ListedSave? Save)
{
    /// <summary>What a new lobby says until the session sets better: in the lobby, Campaign, no save.</summary>
    public static LobbyAdvert Default { get; } = new(ListedState.Lobby, ListedMode.Campaign, null);
}

/// <summary>
/// Publishes a lobby's <see cref="LobbyAdvert"/> and the host's ping location as lobby data, and only what changed: Steam
/// sends every change to everyone who looks, and asks for updates to be infrequent. Only the lobby's owner can set lobby data,
/// so a refusal is logged once per value and tried again every <see cref="RetryInterval"/> (we may have lost ownership).
/// A new lobby starts from nothing. The ping location needs Steam to have measured the relays, which takes a while after the
/// game starts: it is tried every <see cref="LocationRetry"/> until there is one, then refreshed every
/// <see cref="LocationRefresh"/>. An empty save is not published until there has been a save to take away. Main thread only;
/// never throws.
/// </summary>
public sealed class SteamLobbyAdvertiser
{
    /// <summary>Between tries at a value Steam refused.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>Between tries at reading the local ping location until Steam has one.</summary>
    public static readonly TimeSpan LocationRetry = TimeSpan.FromSeconds(5);

    /// <summary>Between refreshes of the published ping location once it is out.</summary>
    public static readonly TimeSpan LocationRefresh = TimeSpan.FromMinutes(5);

    private readonly Func<ulong, string, string, bool> _set;
    private readonly LocationReader _location;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Dictionary<string, string> _sent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedLogged = new(StringComparer.Ordinal);
    private ulong _lobby;
    private LobbyAdvert? _advert;
    private TimeSpan _retryAt;
    private TimeSpan _locationAt;
    private bool _advertRefused;

    /// <summary>Reads a local ping location: false until there is one.</summary>
    internal delegate bool LocationReader(out string location);

    /// <summary>Publishes through Steam (<see cref="SteamLobbies.SetLobbyData"/>); <paramref name="info"/> and <paramref name="warn"/> get its log lines.</summary>
    public SteamLobbyAdvertiser(Action<string> info, Action<string> warn)
        : this(SteamLobbies.SetLobbyData, ReadLocalLocation, info, warn)
    {
    }

    internal SteamLobbyAdvertiser(Func<ulong, string, string, bool> set, LocationReader location, Action<string> info, Action<string> warn)
    {
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>The lobby whose data was last published to, or 0.</summary>
    public ulong Lobby => _lobby;

    /// <summary>The ping location published, or empty when none is out yet.</summary>
    public string PublishedLocation => _sent.TryGetValue(LobbyDataKeys.PingLocation, out string? location) ? location : string.Empty;

    /// <summary>Forgets what was published (the lobby ended, or a new one is made).</summary>
    public void Reset()
    {
        _lobby = 0;
        _advert = null;
        _sent.Clear();
        _refusedLogged.Clear();
        _retryAt = TimeSpan.Zero;
        _locationAt = TimeSpan.Zero;
        _advertRefused = false;
    }

    /// <summary>
    /// Makes lobby <paramref name="lobbyId"/>'s data say <paramref name="advert"/> and the host's ping location. Cheap when
    /// nothing changed, so it may be called every frame. Returns how many values were sent to Steam this time.
    /// </summary>
    public int Publish(ulong lobbyId, LobbyAdvert advert, TimeSpan now)
    {
        if (lobbyId == 0 || advert is null)
        {
            return 0;
        }

        if (lobbyId != _lobby)
        {
            Reset();
            _lobby = lobbyId;
        }

        int sent = 0;
        if ((!ReferenceEquals(advert, _advert) && !advert.Equals(_advert)) || (_advertRefused && now >= _retryAt))
        {
            _advert = advert;
            bool refused = false;
            sent += Send(LobbyDataKeys.State, ListedLobby.StateText(advert.State), ref refused);
            sent += Send(LobbyDataKeys.Mode, ListedLobby.ModeText(advert.Mode), ref refused);
            sent += Send(LobbyDataKeys.Save, advert.Save?.ToText() ?? string.Empty, ref refused);
            _advertRefused = refused;
            if (refused)
            {
                _retryAt = now + RetryInterval;
            }

            if (sent > 0)
            {
                _info($"Online: lobby {SteamLogTags.Lobby(_lobby)}'s data for friends' browsers: state {Show(ListedLobby.StateText(advert.State))}, mode {Show(ListedLobby.ModeText(advert.Mode))}, " +
                    $"save {Show(advert.Save?.ToText() ?? string.Empty)}.");
            }
        }

        if (now >= _locationAt)
        {
            sent += PublishLocation(now);
        }

        return sent;
    }

    private static string Show(string value) => value.Length == 0 ? "(none)" : $"\"{value}\"";

    private static bool ReadLocalLocation(out string location) => SteamPing.TryGetLocalLocation(out location, out _);

    private int PublishLocation(TimeSpan now)
    {
        if (!_location(out string location) || location.Length == 0 || location.Length >= SteamPing.MaxLocationLength)
        {
            _locationAt = now + LocationRetry;
            return 0;
        }

        bool first = !_sent.ContainsKey(LobbyDataKeys.PingLocation);
        bool refused = false;
        int sent = Send(LobbyDataKeys.PingLocation, location, ref refused);
        _locationAt = now + (refused ? RetryInterval : LocationRefresh);
        if (sent > 0 && first)
        {
            _info($"Online: lobby {SteamLogTags.Lobby(_lobby)}'s ping location is published ({location.Length} characters); friends' browsers estimate their ping to you from it.");
        }

        return sent;
    }

    /// <summary>
    /// Sets one key if its value changed; 1 when it was sent. A value Steam refuses isn't kept, so the next try sends it again
    /// (<paramref name="refused"/> is set).
    /// </summary>
    private int Send(string key, string value, ref bool refused)
    {
        bool had = _sent.TryGetValue(key, out string? last);
        if (had ? string.Equals(last, value, StringComparison.Ordinal) : value.Length == 0)
        {
            return 0;
        }

        if (_set(_lobby, key, value))
        {
            _sent[key] = value;
            return 1;
        }

        refused = true;
        if (_refusedLogged.Add(key))
        {
            _warn($"Online: Steam refused lobby data {key} for lobby {SteamLogTags.Lobby(_lobby)} (we may not own it any more); friends' browsers won't show it until it is accepted.");
        }

        return 0;
    }
}
