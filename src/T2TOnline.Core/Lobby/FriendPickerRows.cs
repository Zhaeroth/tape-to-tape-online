using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>What a friend is doing, as Steam says (<c>GetFriendPersonaState</c>, <c>GetFriendGamePlayed</c>).</summary>
public enum PickerActivity
{
    /// <summary>Offline, or showing as offline: left out of the list.</summary>
    Offline,

    /// <summary>Online, in no game (or busy, away: the row can't tell them apart usefully).</summary>
    Online,

    /// <summary>In another game.</summary>
    OtherGame,

    /// <summary>In Tape to Tape: they come first.</summary>
    PlayingT2T,
}

/// <summary>One of Steam's friends, plain.</summary>
/// <param name="SteamId">Their Steam ID.</param>
/// <param name="Name">Their name, cleaned.</param>
/// <param name="Activity">What they're doing.</param>
/// <param name="InThisLobby">True when they're seated or arriving in this lobby.</param>
public readonly record struct PickerFriend(ulong SteamId, string Name, PickerActivity Activity, bool InThisLobby);

/// <summary>A friend's mark on the invite list.</summary>
public enum PickerMark
{
    /// <summary>Nothing: A invites.</summary>
    None,

    /// <summary>Invited within the last 2 minutes: Steam took it. Never "delivered": Steam doesn't say.</summary>
    Invited,

    /// <summary>Already seated or arriving here.</summary>
    InThisLobby,
}

/// <summary>One row of the invite list.</summary>
/// <param name="SteamId">Their Steam ID: what focus follows.</param>
/// <param name="Name">Their name.</param>
/// <param name="Activity">What they're doing.</param>
/// <param name="Mark">Their mark.</param>
/// <param name="ResendSeconds">Seconds until they can be invited again; 0 when they can be now.</param>
public sealed record PickerRow(ulong SteamId, string Name, PickerActivity Activity, PickerMark Mark, int ResendSeconds)
{
    /// <summary>True when A sends an invite.</summary>
    public bool CanInvite => Mark == PickerMark.None;

    /// <summary>The row's mark: what they're doing, or why A doesn't invite. Colour never alone.</summary>
    public LobbyMark Line => Mark switch
    {
        PickerMark.Invited => new LobbyMark(LobbyIcon.Invited, new LobbyLine(LobbyWords.Invited)),
        PickerMark.InThisLobby => new LobbyMark(LobbyIcon.Device, new LobbyLine(LobbyWords.InThisLobby)),
        _ => new LobbyMark(LobbyIcon.None, new LobbyLine(Activity switch
        {
            PickerActivity.PlayingT2T => LobbyWords.PlayingT2T,
            PickerActivity.OtherGame => LobbyWords.PlayingOther,
            _ => LobbyWords.OnlineNow,
        })),
    };
}

/// <summary>
/// The host's invite list: online friends, Tape to Tape's players first, then by name; Invited marks with a 2-minute resend
/// window; who's already here marked; offline friends left out. Focus follows the friend, not the row. Pure: the plugin
/// feeds it Steam's friends and sends the invites.
/// </summary>
public sealed class FriendPickerRows
{
    /// <summary>An invite's mark lasts this long, and then the friend can be invited again.</summary>
    public const long ResendMicros = 120_000_000;

    /// <summary>Most rows kept: online friends, by the list's order.</summary>
    public const int MaxRows = 128;

    private readonly Dictionary<ulong, long> _invitedAt = new();
    private readonly List<PickerRow> _rows = new();
    private int _focus = -1;

    /// <summary>The rows, in order.</summary>
    public IReadOnlyList<PickerRow> Rows => _rows;

    /// <summary>The focused friend, or 0 with none.</summary>
    public ulong FocusedFriend => _focus >= 0 && _focus < _rows.Count ? _rows[_focus].SteamId : 0;

    /// <summary>The focused row's place, or -1.</summary>
    public int FocusIndex => _focus;

    /// <summary>True with nobody to show: say so, and offer Steam's overlay if it's on.</summary>
    public bool IsEmpty => _rows.Count == 0;

    /// <summary>Steam took an invite to this friend (<c>InviteUserToLobby</c> said true): their mark starts. A false from Steam isn't an invite: toast it instead.</summary>
    public void Invited(ulong steamId, long nowMicros)
    {
        if (steamId != 0)
        {
            _invitedAt[steamId] = nowMicros;
        }
    }

    /// <summary>
    /// Takes Steam's friends as they are now, and rebuilds the rows. Focus stays on its friend; if that went, on the row after it; with none yet, on the first who can be invited (those already here
    /// come first by name or game, and A does nothing on them). True when what a row shows changed: a mark's seconds aren't shown. Never throws.
    /// </summary>
    public bool Update(IEnumerable<PickerFriend> friends, long nowMicros)
    {
        ArgumentNullException.ThrowIfNull(friends);

        ulong focused = FocusedFriend;
        int focusedAt = _focus;
        PickerRow[] before = _rows.ToArray();
        _rows.Clear();
        var sorted = new List<PickerFriend>();
        var seen = new HashSet<ulong>();
        foreach (PickerFriend friend in friends)
        {
            if (friend.Activity != PickerActivity.Offline && friend.SteamId != 0 && seen.Add(friend.SteamId))
            {
                sorted.Add(friend);
            }
        }

        sorted.Sort(static (a, b) =>
        {
            int playing = (b.Activity == PickerActivity.PlayingT2T).CompareTo(a.Activity == PickerActivity.PlayingT2T);
            if (playing != 0)
            {
                return playing;
            }

            int names = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return names != 0 ? names : a.SteamId.CompareTo(b.SteamId);
        });
        foreach (PickerFriend friend in sorted)
        {
            if (_rows.Count >= MaxRows)
            {
                break;
            }

            long invitedAt = _invitedAt.TryGetValue(friend.SteamId, out long at) ? at : long.MinValue;
            bool invited = invitedAt != long.MinValue && nowMicros - invitedAt < ResendMicros;
            int left = invited ? (int)Math.Clamp((ResendMicros - (nowMicros - invitedAt) + 999_999) / 1_000_000, 0, int.MaxValue) : 0;
            PickerMark mark = friend.InThisLobby ? PickerMark.InThisLobby : invited ? PickerMark.Invited : PickerMark.None;
            _rows.Add(new PickerRow(friend.SteamId, friend.Name, friend.Activity, mark, mark == PickerMark.Invited ? left : 0));
        }

        ForgetOldInvites(nowMicros);
        bool changed = !SameRows(before);
        if (focused == 0)
        {
            _focus = FirstInvitable();
            return changed;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].SteamId == focused)
            {
                _focus = i;
                return changed;
            }
        }

        _focus = _rows.Count == 0 ? -1 : Math.Min(Math.Max(focusedAt, 0), _rows.Count - 1);
        return changed;
    }

    /// <summary>Moves focus <paramref name="by"/> rows, wrapping around: this is a linear list.</summary>
    public void Move(int by)
    {
        int count = _rows.Count;
        if (count == 0)
        {
            return;
        }

        _focus = (((_focus < 0 ? 0 : _focus) + by) % count + count) % count;
    }

    /// <summary>Puts focus on a friend's row. False if they aren't there.</summary>
    public bool Focus(ulong steamId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].SteamId == steamId)
            {
                _focus = i;
                return true;
            }
        }

        return false;
    }

    private int FirstInvitable()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].CanInvite)
            {
                return i;
            }
        }

        return _rows.Count == 0 ? -1 : 0;
    }

    private bool SameRows(PickerRow[] other)
    {
        if (other.Length != _rows.Count)
        {
            return false;
        }

        for (int i = 0; i < other.Length; i++)
        {
            PickerRow a = other[i];
            PickerRow b = _rows[i];
            if (a.SteamId != b.SteamId || a.Activity != b.Activity || a.Mark != b.Mark || !string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ForgetOldInvites(long nowMicros)
    {
        List<ulong>? old = null;
        foreach (KeyValuePair<ulong, long> invited in _invitedAt)
        {
            if (nowMicros - invited.Value >= ResendMicros)
            {
                (old ??= new List<ulong>()).Add(invited.Key);
            }
        }

        if (old != null)
        {
            foreach (ulong id in old)
            {
                _invitedAt.Remove(id);
            }
        }
    }
}
