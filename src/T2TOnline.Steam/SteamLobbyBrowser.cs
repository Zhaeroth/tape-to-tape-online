using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// The data behind the friends browser: which friends' lobbies there are and what each says about its game, kept up to date
/// by polling (D20: no callbacks). While it is active (a browser is open, or <see cref="Refresh"/> was called less than
/// <see cref="Linger"/> ago) it rescans the friends list every <see cref="ScanInterval"/>, asks Steam for each listed lobby's
/// data every <see cref="RequestInterval"/> (a small request each) and reads the values every <see cref="ReadInterval"/>;
/// otherwise it does nothing at all. It never joins a lobby to look at it.
/// <para>
/// A lobby is listed only if a friend's own game info names it. Its data is then read for <c>t2t.host</c>: the host must be
/// one of the friends the list shows in that lobby, or the lobby is not listed (Steam hands a lobby to another member when its
/// host leaves, and the data the host set stays: a stale lobby no friend hosts any more). A lobby that names no host (an older
/// mod) is listed as it is; your own lobby is not listed. A lobby no friend's game info names for
/// <see cref="MissedScansToRemove"/> scans in a row goes.
/// </para>
/// <para>
/// <c>GetLobbyDataCount &gt; 0</c> is not proof of a fresh answer: Steam keeps the last copy it had. So a lobby whose data was
/// already there at the first read counts as answered only <see cref="AnswerWait"/> after a request Steam took; one whose data
/// appeared after being empty counts at once. Until then it is <see cref="ListingAnswer.Checking"/> (and
/// <see cref="ListingAnswer.NoAnswer"/> after <see cref="NoAnswerAfter"/>). After that the values are as old as the last
/// request's answer, at most <see cref="RequestInterval"/> plus a round trip.
/// </para>
/// Main thread only; never throws; logs changes (a lobby appearing, answering, going) through the callbacks it is given.
/// </summary>
public sealed class SteamLobbyBrowser
{
    /// <summary>How many lobbies are listed at most.</summary>
    public const int MaxLobbies = 16;

    /// <summary>How many friend names a listing keeps.</summary>
    public const int MaxFriendNames = 4;

    /// <summary>How many scans in a row must miss a lobby before it goes (a friend's game info can blink).</summary>
    public const int MissedScansToRemove = 2;

    /// <summary>Between scans of the friends list.</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    /// <summary>Between requests for one lobby's data.</summary>
    public static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(5);

    /// <summary>Between reads of the lobbies' values.</summary>
    public static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long after Steam took a request a copy of the data that was already there counts as the answer.</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a lobby may stay <see cref="ListingAnswer.Checking"/> before it is called <see cref="ListingAnswer.NoAnswer"/>.</summary>
    public static readonly TimeSpan NoAnswerAfter = TimeSpan.FromSeconds(5);

    /// <summary>Between estimates of the ping to one lobby's host.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    /// <summary>Between estimates while Steam doesn't know enough yet.</summary>
    public static readonly TimeSpan PingRetry = TimeSpan.FromSeconds(2);

    /// <summary>How long <see cref="Refresh"/> keeps the browser active.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(15);

    private const int ModCap = 32;
    private const int ProtocolCap = 8;
    private const int BuildCap = 32;

    private readonly ILobbyBrowserSource _source;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Dictionary<ulong, Tracked> _tracked = new();
    private IReadOnlyList<SteamLobbyListing> _listings = Array.Empty<SteamLobbyListing>();
    private TimeSpan _nextScan;
    private TimeSpan _nextRead;
    private TimeSpan _lingerUntil = TimeSpan.MinValue;
    private bool _active;
    private ulong _self;
    private int _sequence;
    private bool _changed;

    /// <summary>Reads the friends and lobbies from Steam; <paramref name="info"/> and <paramref name="warn"/> get its log lines.</summary>
    public SteamLobbyBrowser(Action<string> info, Action<string> warn)
        : this(NativeSource.Instance, info, warn)
    {
    }

    internal SteamLobbyBrowser(ILobbyBrowserSource source, Action<string> info, Action<string> warn)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>
    /// The listed lobbies, in the order they appeared. A new list whenever anything in it changes, the same one in between.
    /// Kept (as last read) while the browser is not active, so a join can still look a lobby up.
    /// </summary>
    public IReadOnlyList<SteamLobbyListing> Listings => _listings;

    /// <summary>Changes whenever <see cref="Listings"/> does.</summary>
    public int Version { get; private set; }

    /// <summary>True while it is scanning and reading (a browser is open, or a <see cref="Refresh"/> is lingering).</summary>
    public bool Active => _active;

    /// <summary>
    /// Looks now (the developer's F1 panel's Find games): rescans and asks for every lobby's data at the next <see cref="Poll"/>, and keeps
    /// the browser active for <see cref="Linger"/> even if no browser is open.
    /// </summary>
    public void Refresh(TimeSpan now)
    {
        _lingerUntil = now + Linger;
        _nextScan = now;
        _nextRead = now;
        foreach (Tracked tracked in _tracked.Values)
        {
            tracked.NextRequest = now;
        }
    }

    /// <summary>Ends a <see cref="Refresh"/>'s lingering now (the PC joined or hosted a game): the work stops unless a browser is open.</summary>
    public void Stop() => _lingerUntil = TimeSpan.MinValue;

    /// <summary>Forgets every lobby (a listing is empty again until the next scan).</summary>
    public void Clear()
    {
        Reset();
        Publish();
    }

    /// <summary>
    /// Call every frame. <paramref name="open"/> is whether a browser is open. Coming active starts from nothing (the old
    /// lobbies may be gone), scanning at once; going idle keeps the last listings but stops all work.
    /// </summary>
    public void Poll(TimeSpan now, bool open)
    {
        bool active = open || now < _lingerUntil;
        if (!active)
        {
            _active = false;
            return;
        }

        if (!_active)
        {
            _active = true;
            Reset();
            _nextScan = now;
            _nextRead = now;
            Publish();
        }

        if (now >= _nextScan)
        {
            _nextScan = now + ScanInterval;
            Scan(now);
        }

        foreach (Tracked tracked in _tracked.Values)
        {
            if (now >= tracked.NextRequest)
            {
                Request(tracked, now);
            }
        }

        if (now >= _nextRead)
        {
            _nextRead = now + ReadInterval;
            foreach (Tracked tracked in _tracked.Values)
            {
                Update(tracked, now);
            }
        }

        Publish();
    }

    private static string Cap(string value, int length) => value.Length <= length ? value : value[..length];

    private void Reset()
    {
        _changed |= _listings.Count > 0;
        _tracked.Clear();
    }

    private void Scan(TimeSpan now)
    {
        _self = _source.LocalSteamId;
        uint app = _source.AppId;
        IReadOnlyList<FriendLobby> found = app == 0 ? Array.Empty<FriendLobby>() : SteamFriendsLobbies.FindFriendLobbies(app, _source);
        var seen = new HashSet<ulong>();
        foreach (IGrouping<ulong, FriendLobby> group in found.GroupBy(f => f.LobbySteamId))
        {
            seen.Add(group.Key);
            if (!_tracked.TryGetValue(group.Key, out Tracked? tracked))
            {
                if (_tracked.Count >= MaxLobbies)
                {
                    continue;
                }

                tracked = new Tracked(group.Key, _sequence++, now);
                _tracked[group.Key] = tracked;
                _info($"Online (browser): lobby {SteamLogTags.Lobby(group.Key)} appeared: {group.Count()} friend(s) in it; asking Steam for its details.");
            }

            tracked.Missed = 0;
            tracked.Friends = group.ToList();
        }

        List<ulong>? gone = null;
        foreach (Tracked tracked in _tracked.Values)
        {
            if (!seen.Contains(tracked.Lobby) && ++tracked.Missed >= MissedScansToRemove)
            {
                (gone ??= new List<ulong>()).Add(tracked.Lobby);
            }
        }

        if (gone is not null)
        {
            foreach (ulong lobby in gone)
            {
                _tracked.Remove(lobby);
                _changed = true;
                _info($"Online (browser): lobby {SteamLogTags.Lobby(lobby)} is no longer listed: no friend's game info names it any more.");
            }
        }
    }

    private void Request(Tracked tracked, TimeSpan now)
    {
        tracked.NextRequest = now + RequestInterval;
        if (_source.RequestLobbyData(tracked.Lobby))
        {
            tracked.FirstAccepted ??= now;
        }
        else if (!tracked.RequestRefusedLogged)
        {
            tracked.RequestRefusedLogged = true;
            _warn($"Online (browser): Steam didn't take the request for lobby {SteamLogTags.Lobby(tracked.Lobby)}'s details (not connected to Steam's servers?); " +
                "it is listed as without details if nothing else arrives.");
        }
    }

    /// <summary>Reads one lobby's values into a listing, or none when it must not be listed.</summary>
    private void Update(Tracked tracked, TimeSpan now)
    {
        SteamLobbyListing? next = Build(tracked, now);
        SteamLobbyListing? current = tracked.Listing;
        if (next is null ? current is not null : current is null || !next.SameAs(current))
        {
            tracked.Listing = next;
            _changed = true;
        }
    }

    private SteamLobbyListing? Build(Tracked tracked, TimeSpan now)
    {
        ulong lobby = tracked.Lobby;
        int count = _source.GetLobbyDataCount(lobby);
        tracked.EmptyAtFirstRead ??= count == 0;
        if (!tracked.Fresh && count > 0 &&
            (tracked.EmptyAtFirstRead == true || (tracked.FirstAccepted is { } first && now - first >= AnswerWait)))
        {
            tracked.Fresh = true;
        }

        if (tracked.Fresh && count == 0)
        {
            // Steam let go of its copy while a friend's game info still names the lobby: what was read stays until it answers again.
            return tracked.Listing;
        }

        if (!tracked.Fresh)
        {
            ListingAnswer waiting = now - tracked.FirstSeen >= NoAnswerAfter ? ListingAnswer.NoAnswer : ListingAnswer.Checking;
            if (waiting == ListingAnswer.NoAnswer && !tracked.NoAnswerLogged)
            {
                tracked.NoAnswerLogged = true;
                _info($"Online (browser): Steam sent no details for lobby {SteamLogTags.Lobby(lobby)} within {NoAnswerAfter.TotalSeconds:0} s; it is listed without them " +
                    "(it may have closed; joining can still be tried).");
            }

            return Basic(tracked, waiting);
        }

        ulong host = ListedLobby.ParseHost(_source.GetLobbyData(lobby, LobbyDataKeys.Host));
        string hostName = string.Empty;
        if (host != 0)
        {
            if (host == _self)
            {
                return null;
            }

            FriendLobby hostEntry = tracked.Friends.FirstOrDefault(f => f.FriendSteamId == host);
            if (hostEntry.FriendSteamId == 0)
            {
                if (tracked.StaleHostLogged != host)
                {
                    tracked.StaleHostLogged = host;
                    _info($"Online (browser): not listing lobby {SteamLogTags.Lobby(lobby)}: its data names {SteamLogTags.Player(host)} as the host, but no friend's game info shows them in it " +
                        "(Steam hands a lobby to another player when its host leaves, or the host isn't your friend).");
                }

                return null;
            }

            hostName = hostEntry.PersonaName;
        }

        // A location that doesn't fit Steam's own limit is not one (cutting it would only make another that doesn't parse).
        string location = _source.GetLobbyData(lobby, LobbyDataKeys.PingLocation);
        if (location.Length >= SteamPing.MaxLocationLength)
        {
            location = string.Empty;
        }

        SteamLobbyListing listing = new(lobby, tracked.Sequence)
        {
            HostId = host,
            HostName = hostName,
            FriendNames = tracked.Friends.Select(f => f.PersonaName).Take(MaxFriendNames).ToArray(),
            FriendsSeen = tracked.Friends.Count,
            Answer = ListingAnswer.Answered,
            ModVersion = Cap(_source.GetLobbyData(lobby, LobbyDataKeys.Mod), ModCap),
            Protocol = Cap(_source.GetLobbyData(lobby, LobbyDataKeys.Protocol), ProtocolCap),
            Build = Cap(_source.GetLobbyData(lobby, LobbyDataKeys.Build), BuildCap),
            Players = ListedLobby.ParsePlayers(_source.GetLobbyData(lobby, LobbyDataKeys.Players)),
            Mode = ListedLobby.ParseMode(_source.GetLobbyData(lobby, LobbyDataKeys.Mode)),
            State = ListedLobby.ParseState(_source.GetLobbyData(lobby, LobbyDataKeys.State)),
            Save = ListedSave.TryParse(_source.GetLobbyData(lobby, LobbyDataKeys.Save), out ListedSave? save) ? save : null,
            PingMs = EstimatePing(tracked, location, now),
        };
        if (!tracked.AnsweredLogged)
        {
            tracked.AnsweredLogged = true;
            _info($"Online (browser): lobby {SteamLogTags.Lobby(lobby)} answered: host {(host == 0 ? "not named" : SteamLogTags.Player(host))}, T2T Online {ShowRemote(listing.ModVersion)}, " +
                $"protocol {ShowRemote(listing.Protocol)}, build {ShowRemote(listing.Build)}, {listing.Mode}, {listing.State}, {listing.Players} player(s)" +
                $"{(listing.Save is { } s ? $", save {ShowRemote(s.Squad)} act {s.Act} floor {s.Floor}" : string.Empty)}" +
                $"{(listing.PingMs >= 0 ? $", ping about {listing.PingMs} ms (an estimate)" : ", no ping estimate")}.");
        }

        return listing;
    }

    /// <summary>
    /// A value another PC put in its lobby's data, for the log: as it is when it has the shape this mod writes there (a
    /// version, a protocol number, a game build, a squad's id: letters, digits and <c>. - _ +</c>), otherwise only its length,
    /// so words someone wrote into their lobby's data never reach this PC's log (D127).
    /// </summary>
    internal static string ShowRemote(string value)
    {
        if (value.Length == 0)
        {
            return "(none)";
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '.' or '-' or '_' or '+'))
            {
                return $"({value.Length} character(s), not as T2T Online writes it)";
            }
        }

        return $"\"{value}\"";
    }

    private int EstimatePing(Tracked tracked, string location, TimeSpan now)
    {
        if (location.Length == 0)
        {
            tracked.PingLocation = string.Empty;
            tracked.PingMs = -1;
            return -1;
        }

        if (!string.Equals(location, tracked.PingLocation, StringComparison.Ordinal) || now >= tracked.NextPing)
        {
            int ms = _source.EstimatePing(location);
            tracked.PingLocation = location;
            tracked.PingMs = ms >= 0 ? ms : -1;
            tracked.NextPing = now + (ms == SteamPing.Unknown ? PingRetry : PingInterval);
        }

        return tracked.PingMs;
    }

    private static SteamLobbyListing Basic(Tracked tracked, ListingAnswer answer) => new(tracked.Lobby, tracked.Sequence)
    {
        FriendNames = tracked.Friends.Select(f => f.PersonaName).Take(MaxFriendNames).ToArray(),
        FriendsSeen = tracked.Friends.Count,
        Answer = answer,
    };

    /// <summary>Makes <see cref="Listings"/> a new list if anything changed since the last one.</summary>
    private void Publish()
    {
        if (!_changed)
        {
            return;
        }

        _changed = false;
        _listings = _tracked.Values.Where(t => t.Listing is not null).OrderBy(t => t.Sequence).Select(t => t.Listing!).ToArray();
        Version++;
    }

    private sealed class Tracked
    {
        public Tracked(ulong lobby, int sequence, TimeSpan now)
        {
            Lobby = lobby;
            Sequence = sequence;
            FirstSeen = now;
            NextRequest = now;
        }

        public ulong Lobby { get; }

        public int Sequence { get; }

        public TimeSpan FirstSeen { get; }

        public TimeSpan NextRequest { get; set; }

        public TimeSpan? FirstAccepted { get; set; }

        public bool? EmptyAtFirstRead { get; set; }

        public bool Fresh { get; set; }

        public int Missed { get; set; }

        public List<FriendLobby> Friends { get; set; } = new();

        public SteamLobbyListing? Listing { get; set; }

        public string PingLocation { get; set; } = string.Empty;

        public int PingMs { get; set; } = -1;

        public TimeSpan NextPing { get; set; }

        public bool RequestRefusedLogged { get; set; }

        public bool NoAnswerLogged { get; set; }

        public bool AnsweredLogged { get; set; }

        public ulong StaleHostLogged { get; set; }
    }

    private sealed class NativeSource : ILobbyBrowserSource
    {
        public static readonly NativeSource Instance = new();

        public ulong LocalSteamId => SteamRuntime.LocalSteamId;

        public uint AppId => SteamRuntime.AppId;

        public int GetFriendCount(SteamFriendFilter flags) => SteamFriends.GetFriendCount(flags);

        public ulong GetFriendByIndex(int index, SteamFriendFilter flags) => SteamFriends.GetFriendByIndex(index, flags);

        public bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info) => SteamFriends.TryGetFriendGamePlayed(steamId, out info);

        public string GetFriendPersonaName(ulong steamId) => SteamFriends.GetFriendPersonaName(steamId);

        public bool RequestLobbyData(ulong lobby) => SteamLobbies.RequestLobbyData(lobby);

        public int GetLobbyDataCount(ulong lobby) => SteamLobbies.GetLobbyDataCount(lobby);

        public string GetLobbyData(ulong lobby, string key) => SteamLobbies.GetLobbyData(lobby, key);

        public int EstimatePing(string location) => SteamPing.EstimateFromLocalHost(location);
    }
}

/// <summary>What <see cref="SteamLobbyBrowser"/> needs from Steam; Steam in the game, a fake in tests. Never throws.</summary>
internal interface ILobbyBrowserSource : IFriendSource
{
    /// <summary>The local user's Steam ID, or 0.</summary>
    ulong LocalSteamId { get; }

    /// <summary>The running app's ID, or 0 when Steam isn't available.</summary>
    uint AppId { get; }

    /// <summary>As <see cref="SteamLobbies.RequestLobbyData"/>.</summary>
    bool RequestLobbyData(ulong lobby);

    /// <summary>As <see cref="SteamLobbies.GetLobbyDataCount"/>.</summary>
    int GetLobbyDataCount(ulong lobby);

    /// <summary>As <see cref="SteamLobbies.GetLobbyData"/>.</summary>
    string GetLobbyData(ulong lobby, string key);

    /// <summary>As <see cref="SteamPing.EstimateFromLocalHost"/>.</summary>
    int EstimatePing(string location);
}
