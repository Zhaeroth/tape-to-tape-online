using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>Why a browser row can't be joined. The row stays, greyed with a lock and the reason in words, and A explains.</summary>
public enum BrowserBlock
{
    /// <summary>Nothing: A joins.</summary>
    None,

    /// <summary>Its details haven't come yet: A waits for them.</summary>
    Checking,

    /// <summary>No seat left.</summary>
    Full,

    /// <summary>Another T2T Online protocol.</summary>
    DifferentMod,

    /// <summary>Another game update.</summary>
    DifferentBuild,
}

/// <summary>What the host's lobby is doing, from <c>t2t.state</c>.</summary>
public enum ListingState
{
    /// <summary>Not said (no details yet).</summary>
    Unknown,

    /// <summary>The lobby's screen is up.</summary>
    InLobby,

    /// <summary>A game is running. Still joinable: a dropped friend must get back.</summary>
    InGame,
}

/// <summary>
/// One lobby as Steam shows it to someone outside it, plain: what friends' game info names, plus what its lobby data says
/// (read without joining: Steam says "do not join lobbies just to find out their metadata"). Steam's values are untrusted.
/// </summary>
public sealed record LobbyListing
{
    /// <summary>The lobby's Steam ID.</summary>
    public ulong LobbyId { get; init; }

    /// <summary>Its host, from <c>t2t.host</c>; 0 before its details.</summary>
    public ulong HostId { get; init; }

    /// <summary>The host's name, cleaned; or before its details the friends' who are in it.</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>Friends whose game info names this lobby (Steam's <c>GetFriendGamePlayed</c>). Friends in one lobby are one row, whoever names it.</summary>
    public IReadOnlyCollection<ulong> FriendsInIt { get; init; } = Array.Empty<ulong>();

    /// <summary>True once its lobby data has been read.</summary>
    public bool HasDetails { get; init; }

    /// <summary>How long ago its details were last answered, in microseconds.</summary>
    public long DetailsAgeMicros { get; init; }

    /// <summary>People in it, from <c>t2t.players</c>, kept seats included.</summary>
    public int Players { get; init; }

    /// <summary>Its seats (Steam's member limit).</summary>
    public int MaxPlayers { get; init; } = LobbyState.TunedSeats;

    /// <summary>What it's doing.</summary>
    public ListingState State { get; init; }

    /// <summary>Its mode, or null when not said.</summary>
    public LobbyMode? Mode { get; init; }

    /// <summary>Its save: squad id, act and floor, from <c>t2t.save</c> (read and checked by the Steam library's <c>ListedSave</c>).</summary>
    public LobbySaveRow Save { get; init; } = LobbySaveRow.None;

    /// <summary>Its protocol from <c>t2t.protocol</c>, or -1 when it isn't a number.</summary>
    public int Protocol { get; init; } = -1;

    /// <summary>Its game build from <c>t2t.build</c>.</summary>
    public string Build { get; init; } = string.Empty;

    /// <summary>Its mod version from <c>t2t.mod</c>. Empty means it isn't a T2T Online lobby at all.</summary>
    public string ModVersion { get; init; } = string.Empty;

    /// <summary>An estimated ping to the host in ms (from <c>t2t.pingloc</c>), or -1.</summary>
    public int PingMs { get; init; } = -1;

    /// <summary>True for a live invite from this host (Steam's <c>OnLobbyInvite</c>).</summary>
    public bool InvitedUs { get; init; }

    /// <summary>
    /// This listing with what Steam's browser found out about it (its <c>ListingAnswer</c>, a type this assembly doesn't see), which is what <see cref="BrowserList.Update"/> goes by.
    /// <paramref name="answered"/>: its details were read, and the browser has seen the host they name in a friend's game info for this lobby, so that host is in <see cref="FriendsInIt"/>.
    /// <paramref name="noAnswer"/>: Steam sent nothing in time: it may have closed, and it's given up on until it answers. Neither: its details are still on their way.
    /// </summary>
    public LobbyListing WithBrowserAnswer(bool answered, bool noAnswer) => this with
    {
        HasDetails = answered,
        FriendsInIt = answered && HostId != 0 ? new[] { HostId } : FriendsInIt,
        DetailsAgeMicros = noAnswer ? BrowserList.NoDetailsGiveUpMicros : 0,
    };
}

/// <summary>Our own versions, to compare with a listing's.</summary>
/// <param name="Protocol">Our protocol.</param>
/// <param name="Build">Our game build.</param>
public readonly record struct BrowserIdentity(int Protocol, string Build);

/// <summary>One row of the Join list.</summary>
public sealed record BrowserRow
{
    /// <summary>The lobby: what focus follows.</summary>
    public ulong LobbyId { get; init; }

    /// <summary>Its host, for the avatar and a profile.</summary>
    public ulong HostId { get; init; }

    /// <summary>The host's name.</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>People in it.</summary>
    public int Players { get; init; }

    /// <summary>Its seats.</summary>
    public int MaxPlayers { get; init; }

    /// <summary>What it's doing.</summary>
    public ListingState State { get; init; }

    /// <summary>Its mode, or null when not said.</summary>
    public LobbyMode? Mode { get; init; }

    /// <summary>Its save row.</summary>
    public LobbySaveRow Save { get; init; } = LobbySaveRow.None;

    /// <summary>The estimated ping's bars (0 when unknown).</summary>
    public int Bars { get; init; }

    /// <summary>The estimated ping, for focus; -1 when unknown.</summary>
    public int PingMs { get; init; } = -1;

    /// <summary>True for a live invite from this host.</summary>
    public bool InvitedUs { get; init; }

    /// <summary>Why it can't be joined; <see cref="BrowserBlock.None"/> when A joins.</summary>
    public BrowserBlock Block { get; init; }

    /// <summary>True when A joins now. A greyed row can still be focused, and explains itself.</summary>
    public bool Joinable => Block == BrowserBlock.None && !Fading;

    /// <summary>The host no longer names a lobby: the row fades out and can't take focus.</summary>
    public bool Fading { get; init; }

    /// <summary>Its mark: why it's greyed, or what it's doing; colour never alone.</summary>
    public LobbyMark Mark => Block switch
    {
        BrowserBlock.Checking => new LobbyMark(LobbyIcon.Dots, new LobbyLine(LobbyWords.Checking)),
        BrowserBlock.Full => new LobbyMark(LobbyIcon.Lock, new LobbyLine(LobbyWords.Full)),
        BrowserBlock.DifferentMod => new LobbyMark(LobbyIcon.Lock, new LobbyLine(LobbyWords.DifferentMod)),
        BrowserBlock.DifferentBuild => new LobbyMark(LobbyIcon.Lock, new LobbyLine(LobbyWords.DifferentUpdate)),
        _ when InvitedUs => new LobbyMark(LobbyIcon.InvitedYou, new LobbyLine(LobbyWords.ToastInvitedYou, HostName)),
        _ when State == ListingState.InGame => new LobbyMark(LobbyIcon.None, new LobbyLine(LobbyWords.InGame)),
        _ => new LobbyMark(LobbyIcon.None, new LobbyLine(LobbyWords.InLobby)),
    };

    /// <summary>The count as the row shows it: "2/4".</summary>
    public string Count => string.Create(CultureInfo.InvariantCulture, $"{Players}/{MaxPlayers}");
}

/// <summary>
/// The Join list: rows in a stable order (by host name: sorting by players would reshuffle under the cursor), focus that
/// follows the lobby and not the row number, full and greyed rows, a row that goes fading out while focus moves to the
/// next, friends in one lobby as one row, and the empty state. Pure: the plugin feeds it what Steam says, on its own schedule.
/// </summary>
public sealed class BrowserList
{
    /// <summary>How long a gone row stays, fading.</summary>
    public const long FadeMicros = 400_000;

    /// <summary>A lobby whose details never came for this long is gone (it may have closed).</summary>
    public const long NoDetailsGiveUpMicros = 15_000_000;

    /// <summary>Most rows kept: friends are capped by Steam, this by reason.</summary>
    public const int MaxRows = 64;

    private readonly List<BrowserRow> _rows = new();
    private readonly Dictionary<ulong, long> _goneAt = new();
    private int _focus = -1;

    /// <summary>The rows, in order, fading ones included.</summary>
    public IReadOnlyList<BrowserRow> Rows => _rows;

    /// <summary>The focused lobby, or 0 with none (the empty state's Host Lobby has focus).</summary>
    public ulong FocusedLobby => _focus >= 0 && _focus < _rows.Count ? _rows[_focus].LobbyId : 0;

    /// <summary>The focused row, or null.</summary>
    public BrowserRow? FocusedRow => _focus >= 0 && _focus < _rows.Count ? _rows[_focus] : null;

    /// <summary>The focused row's place, or -1.</summary>
    public int FocusIndex => _focus;

    /// <summary>True when there's nothing to focus: show "No friends are hosting right now." and Host Lobby.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (BrowserRow row in _rows)
            {
                if (!row.Fading)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Takes what Steam says now. Friends naming one lobby are one row. Listed: a T2T Online lobby whose named host is a friend
    /// whose own game info names it (when the host's gone, Steam hands the lobby to someone else, and it would point at nothing).
    /// A lobby whose details haven't come shows as Checking, until they do or <see cref="NoDetailsGiveUpMicros"/>.
    /// True when the rows changed. Focus stays on its lobby; if that went, on the row after it. Never throws.
    /// </summary>
    public bool Update(IEnumerable<LobbyListing> listings, in BrowserIdentity mine, long nowMicros)
    {
        ArgumentNullException.ThrowIfNull(listings);

        ulong focused = FocusedLobby;
        int focusedAt = _focus;
        var fresh = new Dictionary<ulong, BrowserRow>();
        foreach (LobbyListing listing in listings)
        {
            if (listing == null || fresh.Count >= MaxRows || fresh.ContainsKey(listing.LobbyId))
            {
                continue;
            }

            if (ToRow(listing, mine) is { } row)
            {
                fresh[row.LobbyId] = row;
                _goneAt.Remove(row.LobbyId);
            }
        }

        // A row that went stays a moment, fading; one still fading from before goes when its time is up.
        var next = new List<BrowserRow>(fresh.Values);
        foreach (BrowserRow old in _rows)
        {
            if (fresh.ContainsKey(old.LobbyId))
            {
                continue;
            }

            if (!_goneAt.TryGetValue(old.LobbyId, out long goneAt))
            {
                goneAt = nowMicros;
                _goneAt[old.LobbyId] = goneAt;
            }

            if (nowMicros - goneAt < FadeMicros)
            {
                next.Add(old with { Fading = true });
            }
            else
            {
                _goneAt.Remove(old.LobbyId);
            }
        }

        next.Sort(static (a, b) =>
        {
            int names = string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase);
            return names != 0 ? names : a.LobbyId.CompareTo(b.LobbyId);
        });
        bool changed = next.Count != _rows.Count;
        for (int i = 0; !changed && i < next.Count; i++)
        {
            changed = next[i] != _rows[i];
        }

        bool hadFocus = focused != 0;
        _rows.Clear();
        _rows.AddRange(next);
        _focus = -1;
        if (hadFocus)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].LobbyId == focused && !_rows[i].Fading)
                {
                    _focus = i;
                    return changed;
                }
            }

            _focus = NextFocusable(Math.Max(focusedAt, 0));
            return changed;
        }

        _focus = DefaultFocus();
        return changed;
    }

    /// <summary>Moves focus <paramref name="by"/> rows, wrapping around: this is a linear list. Fading rows are skipped.</summary>
    public void Move(int by)
    {
        int count = _rows.Count;
        if (count == 0 || by == 0)
        {
            return;
        }

        int at = _focus < 0 ? 0 : _focus;
        int step = by > 0 ? 1 : -1;
        for (int moved = 0; moved < Math.Abs(by); moved++)
        {
            for (int tried = 0; tried < count; tried++)
            {
                at = ((at + step) % count + count) % count;
                if (!_rows[at].Fading)
                {
                    break;
                }
            }
        }

        if (!_rows[at].Fading)
        {
            _focus = at;
        }
    }

    /// <summary>Puts focus on a lobby's row (a mouse's, or the row someone came back to). False if it isn't there.</summary>
    public bool Focus(ulong lobbyId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].LobbyId == lobbyId && !_rows[i].Fading)
            {
                _focus = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>The Online group's live line: "Alex is hosting", "2 friends hosting", or null with nobody. Greyed rows count: it's still news.</summary>
    public LobbyLine? HostingLine()
    {
        string name = string.Empty;
        int hosts = 0;
        foreach (BrowserRow row in _rows)
        {
            if (!row.Fading)
            {
                hosts++;
                name = row.HostName;
            }
        }

        return hosts switch
        {
            0 => null,
            1 => new LobbyLine(LobbyWords.IsHosting, name),
            _ => new LobbyLine(LobbyWords.FriendsHosting, LobbyWords.Number(hosts)),
        };
    }

    private static BrowserRow? ToRow(LobbyListing listing, in BrowserIdentity mine)
    {
        if (listing.LobbyId == 0)
        {
            return null;
        }

        if (!listing.HasDetails)
        {
            // Named by a friend, its data not here yet.
            return listing.DetailsAgeMicros >= NoDetailsGiveUpMicros ? null : new BrowserRow
            {
                LobbyId = listing.LobbyId,
                HostName = listing.HostName,
                Players = Math.Max(listing.FriendsInIt.Count, 1),
                MaxPlayers = Math.Max(listing.MaxPlayers, 1),
                Block = BrowserBlock.Checking,
                InvitedUs = listing.InvitedUs,
            };
        }

        if (listing.ModVersion.Length == 0 || listing.HostId == 0 || !Names(listing.FriendsInIt, listing.HostId))
        {
            return null; // not ours, or a host who no longer names it: Steam's handed it on, and it points at nothing
        }

        int max = Math.Clamp(listing.MaxPlayers, 1, LobbyState.MaxSeats);
        int players = Math.Clamp(listing.Players, 0, max);
        return new BrowserRow
        {
            LobbyId = listing.LobbyId,
            HostId = listing.HostId,
            HostName = listing.HostName,
            Players = players,
            MaxPlayers = max,
            State = listing.State,
            Mode = listing.Mode,
            Save = listing.Save,
            Bars = SignalBars.Of(listing.PingMs),
            PingMs = listing.PingMs < 0 ? -1 : listing.PingMs,
            InvitedUs = listing.InvitedUs,
            Block = listing.Protocol != mine.Protocol ? BrowserBlock.DifferentMod
                : !string.Equals(listing.Build, mine.Build, StringComparison.Ordinal) ? BrowserBlock.DifferentBuild
                : players >= max ? BrowserBlock.Full
                : BrowserBlock.None,
        };
    }

    private static bool Names(IReadOnlyCollection<ulong> friends, ulong host)
    {
        foreach (ulong friend in friends)
        {
            if (friend == host)
            {
                return true;
            }
        }

        return false;
    }

    private int DefaultFocus()
    {
        int first = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Joinable)
            {
                return i;
            }

            if (first < 0 && !_rows[i].Fading)
            {
                first = i;
            }
        }

        return first;
    }

    private int NextFocusable(int from)
    {
        for (int i = from; i < _rows.Count; i++)
        {
            if (!_rows[i].Fading)
            {
                return i;
            }
        }

        for (int i = Math.Min(from, _rows.Count) - 1; i >= 0; i--)
        {
            if (!_rows[i].Fading)
            {
                return i;
            }
        }

        return -1;
    }
}
