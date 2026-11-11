using System;
using System.Collections.Generic;

namespace T2TOnline.Steam;

/// <summary>One avatar the poll tracks: a user and which of Steam's sizes.</summary>
/// <param name="SteamId">The user.</param>
/// <param name="Size">Which avatar.</param>
public readonly record struct SteamAvatarKey(ulong SteamId, SteamAvatarSize Size);

/// <summary>Where one avatar is on its way from Steam to the screen (<see cref="SteamAvatarPoll"/>).</summary>
public enum SteamAvatarPhase
{
    /// <summary>Steam hasn't given an image handle yet; the poll keeps asking.</summary>
    Waiting,

    /// <summary>There is an image handle: the owner reads it with <see cref="SteamAvatarPoll.TryNextFetch"/> and reports back.</summary>
    Fetch,

    /// <summary>The owner has the image (for a seated player the poll keeps watching for a new handle).</summary>
    Ready,

    /// <summary>No avatar: the user has none set, Steam never gave one, or reading it failed again and again. Tried again later.</summary>
    None,
}

/// <summary>
/// The pure state machine that gets avatars out of Steam without callbacks (D20): it decides when to ask Steam, one call per
/// step, and the owner (the plugin's avatar cache) reads the pixels and makes the texture. Per avatar:
/// <list type="bullet">
/// <item><description><b>Waiting</b>: <c>RequestUserInformation</c> now and every <see cref="InfoRequestInterval"/>, at most
/// <see cref="MaxInfoRequests"/> times (stopped as soon as Steam says it has everything); the handle is asked for every
/// <see cref="HandlePollInterval"/>. <c>-1</c> (loading) keeps waiting; <c>0</c> (no avatar) keeps waiting for
/// <see cref="NoneGiveUp"/>, then it is <b>None</b>; a handle above 0 is <b>Fetch</b>.</description></item>
/// <item><description><b>Fetch</b>: waits for <see cref="TryNextFetch"/>, one avatar per call (the owner calls it once a
/// frame), then <see cref="Fetched"/> or <see cref="FetchFailed"/>.</description></item>
/// <item><description><b>Ready</b>: nothing more, except for a seated player (<see cref="SetSeated"/>): the handle is asked
/// for every <see cref="SeatedRefresh"/>, and a new one makes it Fetch again (the old image stays in use meanwhile).</description></item>
/// <item><description><b>None</b>: asked again every <see cref="NoneRetry"/> while somebody still wants it.</description></item>
/// </list>
/// At most <see cref="MaxHandlePollsPerStep"/> avatars are asked about per <see cref="Poll"/>, and only those wanted within
/// <see cref="WantedFor"/> (or seated). At most <see cref="MaxEntries"/> are kept: the one wanted longest ago goes, reported
/// by <see cref="TryTakeEvicted"/>. No allocation once the entries exist. Main thread only; never throws.
/// </summary>
public sealed class SteamAvatarPoll
{
    /// <summary>How many avatars are tracked at most.</summary>
    public const int MaxEntries = 32;

    /// <summary>How many avatars <see cref="Poll"/> asks Steam about per call.</summary>
    public const int MaxHandlePollsPerStep = 4;

    /// <summary>How many times one avatar's user information is requested.</summary>
    public const int MaxInfoRequests = 6;

    /// <summary>How many failed reads of one avatar's image are tried again at once before it is <see cref="SteamAvatarPhase.None"/>.</summary>
    public const int MaxReadFailures = 3;

    /// <summary>Between asks for the handle of an avatar that isn't there yet (2 Hz).</summary>
    public static readonly TimeSpan HandlePollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Between requests for a user's information.</summary>
    public static readonly TimeSpan InfoRequestInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long Steam may say "no avatar" before the avatar is given up on.</summary>
    public static readonly TimeSpan NoneGiveUp = TimeSpan.FromSeconds(30);

    /// <summary>Between asks about an avatar that was given up on.</summary>
    public static readonly TimeSpan NoneRetry = TimeSpan.FromSeconds(30);

    /// <summary>Between asks for the handle of a seated player's loaded avatar (a changed avatar has a new handle).</summary>
    public static readonly TimeSpan SeatedRefresh = TimeSpan.FromSeconds(10);

    /// <summary>After a failed read, before the handle is asked for again.</summary>
    public static readonly TimeSpan ReadRetry = TimeSpan.FromSeconds(2);

    /// <summary>How long an avatar is kept up to date after it was last asked for (unless its player is seated).</summary>
    public static readonly TimeSpan WantedFor = TimeSpan.FromSeconds(60);

    private readonly ISteamAvatarApi _api;
    private readonly Dictionary<SteamAvatarKey, Entry> _entries = new();
    private readonly HashSet<ulong> _seated = new();
    private readonly Queue<SteamAvatarKey> _evicted = new();
    private readonly Queue<SteamAvatarKey> _givenUp = new();
    private long _order;

    /// <summary>Asks Steam through the flat API (<see cref="SteamAvatars"/>).</summary>
    public SteamAvatarPoll()
        : this(NativeAvatarApi.Instance)
    {
    }

    internal SteamAvatarPoll(ISteamAvatarApi api) => _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <summary>How many avatars are tracked.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Says somebody wants this avatar now, and where it is. A new one starts Waiting (and, with <see cref="MaxEntries"/>
    /// tracked already, the one wanted longest ago is dropped: <see cref="TryTakeEvicted"/>). Call it whenever something
    /// is bound to the avatar; it costs a lookup.
    /// </summary>
    public SteamAvatarPhase Want(SteamAvatarKey key, TimeSpan now)
    {
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            entry.LastWanted = now;
            return entry.Phase;
        }

        if (_entries.Count >= MaxEntries)
        {
            Evict();
        }

        entry = new Entry(key, now, _order++);
        _entries[key] = entry;
        return entry.Phase;
    }

    /// <summary>Where an avatar is, or null if it isn't tracked.</summary>
    public SteamAvatarPhase? PhaseOf(SteamAvatarKey key) => _entries.TryGetValue(key, out Entry? entry) ? entry.Phase : null;

    /// <summary>The image handle an avatar is Fetching or has Fetched, or 0 (not tracked, or no handle yet).</summary>
    public int HandleOf(SteamAvatarKey key) => _entries.TryGetValue(key, out Entry? entry) ? entry.Handle : 0;

    /// <summary>Who sits in the lobby now: their avatars are kept up to date, the others' are not once loaded.</summary>
    public void SetSeated(IEnumerable<ulong>? steamIds)
    {
        _seated.Clear();
        if (steamIds is null)
        {
            return;
        }

        foreach (ulong id in steamIds)
        {
            if (id != 0 && _seated.Count < MaxEntries)
            {
                _seated.Add(id);
            }
        }
    }

    /// <summary>
    /// Asks Steam about the avatars that are due (at most <see cref="MaxHandlePollsPerStep"/>; call it every frame, it does
    /// nothing until something is due). Returns how many it asked about.
    /// </summary>
    public int Poll(TimeSpan now)
    {
        int asked = 0;
        foreach (Entry entry in _entries.Values)
        {
            if (asked >= MaxHandlePollsPerStep)
            {
                break;
            }

            if (now < entry.NextPoll || !IsWatched(entry, now))
            {
                continue;
            }

            asked++;
            Step(entry, now);
        }

        return asked;
    }

    /// <summary>
    /// The avatar that has waited longest to be read, if any: its key and image handle. The owner reads it and calls
    /// <see cref="Fetched"/> or <see cref="FetchFailed"/>; it asks for one a frame so that no frame carries more than one
    /// image's reading and upload.
    /// </summary>
    public bool TryNextFetch(out SteamAvatarKey key, out int handle)
    {
        Entry? next = null;
        foreach (Entry entry in _entries.Values)
        {
            if (entry.Phase == SteamAvatarPhase.Fetch && (next is null || entry.FetchOrder < next.FetchOrder))
            {
                next = entry;
            }
        }

        key = next?.Key ?? default;
        handle = next?.Handle ?? 0;
        return next is not null;
    }

    /// <summary>The owner has read and kept image <paramref name="handle"/> of <paramref name="key"/>.</summary>
    public void Fetched(SteamAvatarKey key, int handle, TimeSpan now)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return;
        }

        entry.LoadedHandle = handle;
        entry.Handle = handle;
        entry.Phase = SteamAvatarPhase.Ready;
        entry.ReadFailures = 0;
        entry.NextPoll = now + SeatedRefresh;
    }

    /// <summary>
    /// The owner couldn't read or keep image <paramref name="handle"/> of <paramref name="key"/>. It is asked for again after
    /// <see cref="ReadRetry"/>; after <see cref="MaxReadFailures"/> in a row it is <see cref="SteamAvatarPhase.None"/>, and a
    /// loaded image the owner still has stays Ready.
    /// </summary>
    public void FetchFailed(SteamAvatarKey key, int handle, TimeSpan now)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return;
        }

        entry.ReadFailures++;
        entry.Handle = entry.LoadedHandle;
        bool kept = entry.LoadedHandle > 0;
        if (entry.ReadFailures >= MaxReadFailures)
        {
            if (kept)
            {
                entry.Phase = SteamAvatarPhase.Ready;
            }
            else
            {
                GiveUp(entry);
            }

            entry.NextPoll = now + (kept ? SeatedRefresh : NoneRetry);
            entry.ReadFailures = 0;
            return;
        }

        entry.Phase = kept ? SteamAvatarPhase.Ready : SteamAvatarPhase.Waiting;
        entry.NextPoll = now + ReadRetry;
    }

    /// <summary>An avatar dropped to make room (the owner destroys its image). False when none was dropped since the last call.</summary>
    public bool TryTakeEvicted(out SteamAvatarKey key)
    {
        if (_evicted.Count > 0)
        {
            key = _evicted.Dequeue();
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    /// An avatar that has just become <see cref="SteamAvatarPhase.None"/> (for the owner's log; once each time). False when none
    /// has since the last call.
    /// </summary>
    public bool TryTakeGivenUp(out SteamAvatarKey key)
    {
        if (_givenUp.Count > 0)
        {
            key = _givenUp.Dequeue();
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>Forgets one avatar (the owner lost its image): wanting it again starts it over. False when it wasn't tracked.</summary>
    public bool Remove(SteamAvatarKey key) => _entries.Remove(key);

    /// <summary>Forgets every avatar (the owner destroys its images).</summary>
    public void Clear()
    {
        _entries.Clear();
        _evicted.Clear();
        _givenUp.Clear();
        _seated.Clear();
    }

    private bool IsWatched(Entry entry, TimeSpan now) =>
        _seated.Contains(entry.Key.SteamId) || (entry.Phase != SteamAvatarPhase.Ready && now - entry.LastWanted <= WantedFor);

    /// <summary>Drops the avatar wanted longest ago; a seated player's is the last to go.</summary>
    private void Evict()
    {
        Entry? oldest = null;
        foreach (Entry entry in _entries.Values)
        {
            bool better = oldest is null
                || (_seated.Contains(oldest.Key.SteamId) && !_seated.Contains(entry.Key.SteamId))
                || (_seated.Contains(oldest.Key.SteamId) == _seated.Contains(entry.Key.SteamId) && entry.LastWanted < oldest.LastWanted);
            if (better)
            {
                oldest = entry;
            }
        }

        if (oldest is not null)
        {
            _entries.Remove(oldest.Key);
            _evicted.Enqueue(oldest.Key);
        }
    }

    private void GiveUp(Entry entry)
    {
        if (entry.Phase != SteamAvatarPhase.None)
        {
            entry.Phase = SteamAvatarPhase.None;
            _givenUp.Enqueue(entry.Key);
        }
    }

    /// <summary>One step for one avatar that is due: user information if it's missing, then its handle.</summary>
    private void Step(Entry entry, TimeSpan now)
    {
        SteamAvatarKey key = entry.Key;
        if (entry.Phase is SteamAvatarPhase.Waiting && !entry.InfoDone && entry.InfoRequests < MaxInfoRequests && now >= entry.NextInfo)
        {
            // A friend's avatar downloads by itself once asked for; anyone else (a lobby member) needs this.
            bool? requested = _api.RequestUserInformation(key.SteamId);
            entry.InfoRequests++;
            entry.NextInfo = now + InfoRequestInterval;
            entry.InfoDone = requested != true;
        }

        int handle = _api.GetHandle(key.SteamId, key.Size);
        if (handle == SteamAvatars.Unavailable)
        {
            // Steam can't be asked at all: not an avatar worth waiting for, and not a reason to ask every half second.
            if (entry.Phase != SteamAvatarPhase.Ready)
            {
                GiveUp(entry);
            }

            entry.NextPoll = now + NoneRetry;
            return;
        }

        if (handle > 0)
        {
            entry.NoneSince = null;
            if (handle == entry.LoadedHandle)
            {
                entry.NextPoll = now + SeatedRefresh;
                return;
            }

            entry.Handle = handle;
            entry.Phase = SteamAvatarPhase.Fetch;
            entry.FetchOrder = _order++;
            entry.NextPoll = TimeSpan.MaxValue; // until the owner reports back
            return;
        }

        if (entry.Phase == SteamAvatarPhase.Ready)
        {
            // A loaded avatar stays as it is: Steam saying "none" or "loading" for a moment changes nothing on screen.
            entry.NextPoll = now + SeatedRefresh;
            return;
        }

        if (handle == SteamAvatars.Loading)
        {
            entry.NoneSince = null;
            entry.Phase = SteamAvatarPhase.Waiting;
            entry.NextPoll = now + HandlePollInterval;
            return;
        }

        if (entry.Phase == SteamAvatarPhase.None)
        {
            entry.NextPoll = now + NoneRetry;
            return;
        }

        entry.NoneSince ??= now;
        if (now - entry.NoneSince.Value >= NoneGiveUp)
        {
            GiveUp(entry);
            entry.NextPoll = now + NoneRetry;
        }
        else
        {
            entry.NextPoll = now + HandlePollInterval;
        }
    }

    private sealed class Entry
    {
        public Entry(SteamAvatarKey key, TimeSpan now, long order)
        {
            Key = key;
            LastWanted = now;
            FetchOrder = order;
        }

        public SteamAvatarKey Key { get; }

        public SteamAvatarPhase Phase { get; set; } = SteamAvatarPhase.Waiting;

        public TimeSpan LastWanted { get; set; }

        public TimeSpan NextPoll { get; set; } = TimeSpan.Zero;

        public TimeSpan NextInfo { get; set; } = TimeSpan.Zero;

        public int InfoRequests { get; set; }

        public bool InfoDone { get; set; }

        public TimeSpan? NoneSince { get; set; }

        public int Handle { get; set; }

        public int LoadedHandle { get; set; }

        public int ReadFailures { get; set; }

        public long FetchOrder { get; set; }
    }

    private sealed class NativeAvatarApi : ISteamAvatarApi
    {
        public static readonly NativeAvatarApi Instance = new();

        public bool? RequestUserInformation(ulong steamId) => SteamAvatars.RequestUserInformation(steamId);

        public int GetHandle(ulong steamId, SteamAvatarSize size) => SteamAvatars.GetHandle(steamId, size);
    }
}

/// <summary>The two Steam calls <see cref="SteamAvatarPoll"/> makes; Steam in the game, a fake in tests. Never throws.</summary>
internal interface ISteamAvatarApi
{
    /// <summary>As <see cref="SteamAvatars.RequestUserInformation"/>.</summary>
    bool? RequestUserInformation(ulong steamId);

    /// <summary>As <see cref="SteamAvatars.GetHandle"/>.</summary>
    int GetHandle(ulong steamId, SteamAvatarSize size);
}
