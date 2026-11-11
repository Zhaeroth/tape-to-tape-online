using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// What a check tour's holds kept from the player's data and Steam account, counted for the tour's summary: achievement
/// unlocks held back, one-off save allowances refused, writes refused or held in memory (the game's saves the guard refused
/// and the mod's own records kept in memory), settings changed in memory only, and what would have reached another person:
/// invites held (an invite or Steam's invite window asked for) and joins refused (a lobby the tour pressed, or a join Steam
/// asked for). Also which lines were said once this tour (<see cref="First"/>). Thread-safe (the game may save from a worker
/// thread); <see cref="Reset"/> at each tour's start.
/// </summary>
public sealed class TourHoldTally
{
    private readonly object _gate = new();
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);
    private long _achievements;
    private long _allowances;
    private long _writes;
    private long _playerData;
    private long _settings;
    private long _invites;
    private long _joins;

    /// <summary>Achievement unlocks held back (calls skipped; a repeated unlock counts each time).</summary>
    public long AchievementsHeld
    {
        get
        {
            lock (_gate)
            {
                return _achievements;
            }
        }
    }

    /// <summary>One-off save allowances refused.</summary>
    public long AllowancesRefused
    {
        get
        {
            lock (_gate)
            {
                return _allowances;
            }
        }
    }

    /// <summary>Writes refused or held in memory.</summary>
    public long WritesRefused
    {
        get
        {
            lock (_gate)
            {
                return _writes;
            }
        }
    }

    /// <summary>
    /// The writes refused or held that left some of the player's data in memory only (all but a refused write of the game's
    /// settings or of the tour's own run file, <see cref="TourEnd.HoldsPlayerData"/>): what the game's next save would write.
    /// </summary>
    public long PlayerDataHeld
    {
        get
        {
            lock (_gate)
            {
                return _playerData;
            }
        }
    }

    /// <summary>Settings changes held in memory.</summary>
    public long SettingsHeld
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>Invites held: an invite to a friend, or Steam's invite window, asked for while the tour ran.</summary>
    public long InvitesHeld
    {
        get
        {
            lock (_gate)
            {
                return _invites;
            }
        }
    }

    /// <summary>Joins refused: another lobby the tour was asked to join, or a join Steam asked for while the tour ran.</summary>
    public long JoinsRefused
    {
        get
        {
            lock (_gate)
            {
                return _joins;
            }
        }
    }

    /// <summary>Every count back to 0 and every line unsaid: a new tour.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _achievements = _allowances = _writes = _playerData = _settings = _invites = _joins = 0;
            _said.Clear();
        }
    }

    /// <summary>An invite (or Steam's invite window) was asked for and held.</summary>
    public void InviteHeld()
    {
        lock (_gate)
        {
            _invites++;
        }
    }

    /// <summary>A join was refused or dropped.</summary>
    public void JoinRefused()
    {
        lock (_gate)
        {
            _joins++;
        }
    }

    /// <summary>
    /// Why the tour fails for what its holds turned away, or null when they turned nothing away: a tour never invites or
    /// joins anyone, so a held invite or a refused join means something it drove (or someone, through Steam) asked for one,
    /// and the log's lines say which.
    /// </summary>
    public string? ReachProblem()
    {
        lock (_gate)
        {
            return _invites == 0 && _joins == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture,
                    $"{_invites} invite(s) held and {_joins} join(s) refused: a tour never invites or joins anyone (the log's \"Lobby check:\" and \"Steam:\" lines say what asked)");
        }
    }

    /// <summary>An achievement unlock was held back.</summary>
    public void AchievementHeld()
    {
        lock (_gate)
        {
            _achievements++;
        }
    }

    /// <summary>An allowance was refused.</summary>
    public void AllowanceRefused()
    {
        lock (_gate)
        {
            _allowances++;
        }
    }

    /// <summary>
    /// A write was refused or held in memory; <paramref name="playerData"/> false when it left none of the player's data in
    /// memory only (a refused write of the game's settings, which the tour puts back, or of its own run file).
    /// </summary>
    public void WriteRefused(bool playerData = true)
    {
        lock (_gate)
        {
            _writes++;
            _playerData += playerData ? 1 : 0;
        }
    }

    /// <summary>A setting's change was held in memory.</summary>
    public void SettingHeld()
    {
        lock (_gate)
        {
            _settings++;
        }
    }

    /// <summary>True the first time <paramref name="key"/> is asked about since the tour began: its line is said then, once.</summary>
    public bool First(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _said.Add(key);
        }
    }

    /// <summary>
    /// The summary's part: <c>achievements held back h; allowances refused r; writes refused w; settings held s; invites held i;
    /// joins refused j</c>.
    /// </summary>
    public string Summary()
    {
        lock (_gate)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"achievements held back {_achievements}; allowances refused {_allowances}; writes refused {_writes}; settings held {_settings}; invites held {_invites}; joins refused {_joins}");
        }
    }
}
