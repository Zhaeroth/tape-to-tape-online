using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// Host: friends who said they're leaving (a quit, which also takes them out of the Steam lobby) while Steam's member list
/// still names them. That list lags the goodbye (Steam's own update, then the session's poll once a second), and anyone in it
/// with no seat is arriving: on the board as Joining, holding their seat, and in Start's prompt. Someone here isn't arriving,
/// until Steam stops listing them, they say hello again, or <see cref="LingerMicros"/> passes (then, still listed, they're
/// back in the lobby, or Steam is slow, and arriving is the truth). At most <see cref="MaxMembers"/> are held, the oldest going
/// first. Pure logic; not thread-safe (one game thread).
/// </summary>
public sealed class LeavingMembers
{
    /// <summary>How long a goodbye keeps someone from arriving while Steam still lists them.</summary>
    public const long LingerMicros = 10_000_000;

    /// <summary>The most held at once (a lobby has four seats; goodbyes come from sessions the host had).</summary>
    public const int MaxMembers = 16;

    private readonly Dictionary<ulong, long> _since = new();
    private readonly List<ulong> _gone = new();

    /// <summary>How many are held now.</summary>
    public int Count => _since.Count;

    /// <summary><paramref name="steamId"/> said goodbye at <paramref name="nowMicros"/>.</summary>
    public void Left(ulong steamId, long nowMicros)
    {
        if (steamId == 0)
        {
            return;
        }

        if (!_since.ContainsKey(steamId) && _since.Count >= MaxMembers)
        {
            ulong oldest = 0;
            long oldestAt = long.MaxValue;
            foreach ((ulong id, long at) in _since)
            {
                if (at < oldestAt)
                {
                    oldest = id;
                    oldestAt = at;
                }
            }

            _since.Remove(oldest);
        }

        _since[steamId] = nowMicros;
    }

    /// <summary><paramref name="steamId"/> said hello again: arriving for real from here.</summary>
    public void Back(ulong steamId) => _since.Remove(steamId);

    /// <summary>True when <paramref name="steamId"/> said goodbye less than <see cref="LingerMicros"/> before <paramref name="nowMicros"/>.</summary>
    public bool IsLeaving(ulong steamId, long nowMicros) =>
        _since.TryGetValue(steamId, out long at) && (nowMicros < at || unchecked((ulong)(nowMicros - at)) < LingerMicros);

    /// <summary>After Steam's member list is read again: whoever it no longer names, or whose goodbye is old, is forgotten.</summary>
    public void Prune(IReadOnlyList<ulong> members, long nowMicros)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (_since.Count == 0)
        {
            return;
        }

        _gone.Clear();
        foreach (ulong id in _since.Keys)
        {
            if (!IsLeaving(id, nowMicros) || !Lists(members, id))
            {
                _gone.Add(id);
            }
        }

        foreach (ulong id in _gone)
        {
            _since.Remove(id);
        }
    }

    /// <summary>Forgets everyone (the session ended).</summary>
    public void Clear() => _since.Clear();

    private static bool Lists(IReadOnlyList<ulong> members, ulong id)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] == id)
            {
                return true;
            }
        }

        return false;
    }
}
