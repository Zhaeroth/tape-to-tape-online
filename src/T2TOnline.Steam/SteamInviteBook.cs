using System;
using System.Collections.Generic;

namespace T2TOnline.Steam;

/// <summary>A Steam invite to a lobby that this PC received while the game ran (<c>LobbyInvite_t</c>).</summary>
/// <param name="LobbyId">The lobby we are invited to (checked to be one).</param>
/// <param name="FromSteamId">Who invited us.</param>
/// <param name="At">When it arrived, on the caller's clock.</param>
public readonly record struct SteamInvite(ulong LobbyId, ulong FromSteamId, TimeSpan At);

/// <summary>
/// The Steam invites this PC received while the game ran, so the friends browser can mark the game that invited the player.
/// Steam tells the game with <c>LobbyInvite_t</c> (the game's Facepunch.Steamworks raises <c>SteamMatchmaking.OnLobbyInvite</c>,
/// which the mod's plugin listens to: this library registers no callbacks, D20). Accepting is a different event
/// (<see cref="SteamJoinInbox"/>). The newest <see cref="Capacity"/> are kept, each for <see cref="Life"/>; one lobby has
/// one invite (a newer one replaces it). "Invited" is only that an invite arrived: Steam doesn't say whether it still stands.
/// Safe from any thread.
/// </summary>
public sealed class SteamInviteBook
{
    /// <summary>How many invites are kept.</summary>
    public const int Capacity = 8;

    /// <summary>How long an invite counts as live.</summary>
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly List<(SteamInvite Invite, bool Announced)> _invites = new();

    /// <summary>The one the plugin's event handler writes and the browser reads.</summary>
    public static SteamInviteBook Shared { get; } = new();

    /// <summary>Changes whenever an invite is added, forgotten or expires, so a reader can tell its list is out of date.</summary>
    public int Version { get; private set; }

    /// <summary>How many live invites are held (as of the last <see cref="Add"/> or <see cref="Prune"/>).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _invites.Count;
            }
        }
    }

    /// <summary>
    /// Keeps an invite to <paramref name="lobbyId"/> from <paramref name="fromSteamId"/>. False, keeping nothing, when the ID
    /// isn't a lobby's.
    /// </summary>
    public bool Add(ulong lobbyId, ulong fromSteamId, TimeSpan now)
    {
        if (!SteamJoinRequests.IsLobbyId(lobbyId))
        {
            return false;
        }

        lock (_gate)
        {
            _invites.RemoveAll(entry => entry.Invite.LobbyId == lobbyId);
            if (_invites.Count >= Capacity)
            {
                _invites.RemoveAt(0);
            }

            _invites.Add((new SteamInvite(lobbyId, fromSteamId, now), false));
            Version++;
            return true;
        }
    }

    /// <summary>True when a live invite to <paramref name="lobbyId"/> is held.</summary>
    public bool IsInvited(ulong lobbyId, TimeSpan now) => TryGet(lobbyId, now, out _);

    /// <summary>The live invite to <paramref name="lobbyId"/>, if one is held.</summary>
    public bool TryGet(ulong lobbyId, TimeSpan now, out SteamInvite invite)
    {
        lock (_gate)
        {
            foreach ((SteamInvite held, _) in _invites)
            {
                if (held.LobbyId == lobbyId && now - held.At <= Life)
                {
                    invite = held;
                    return true;
                }
            }
        }

        invite = default;
        return false;
    }

    /// <summary>The oldest invite nobody has been told about yet, once (for a toast: "Alex invited you"). False when there is
    /// none.</summary>
    public bool TryTakeUnannounced(TimeSpan now, out SteamInvite invite)
    {
        lock (_gate)
        {
            for (int i = 0; i < _invites.Count; i++)
            {
                (SteamInvite held, bool announced) = _invites[i];
                if (!announced && now - held.At <= Life)
                {
                    _invites[i] = (held, true);
                    invite = held;
                    return true;
                }
            }
        }

        invite = default;
        return false;
    }

    /// <summary>Drops the invite to <paramref name="lobbyId"/> (the player joined it). False when none was held.</summary>
    public bool Forget(ulong lobbyId)
    {
        lock (_gate)
        {
            if (_invites.RemoveAll(entry => entry.Invite.LobbyId == lobbyId) == 0)
            {
                return false;
            }

            Version++;
            return true;
        }
    }

    /// <summary>Drops the invites that are older than <see cref="Life"/>, changing <see cref="Version"/> if any went.</summary>
    public void Prune(TimeSpan now)
    {
        lock (_gate)
        {
            if (_invites.RemoveAll(entry => now - entry.Invite.At > Life) > 0)
            {
                Version++;
            }
        }
    }

    /// <summary>Drops every invite (tests, and when Steam is gone).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_invites.Count > 0)
            {
                _invites.Clear();
                Version++;
            }
        }
    }
}
