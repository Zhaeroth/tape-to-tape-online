using System;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Diagnostics;

/// <summary>What one look at this PC's own Steam name found (<see cref="OwnNameWatch.Seen"/>).</summary>
public enum OwnNameLook
{
    /// <summary>The first name read this launch: the cleaner learns it now.</summary>
    First,

    /// <summary>The name read before, unchanged.</summary>
    Same,

    /// <summary>A new name (a rename, or another account): the cleaner learns it too, and keeps the old one.</summary>
    Changed,

    /// <summary>No name: Steam isn't signed in or can't be asked. Said once until a name comes.</summary>
    NotSignedIn,

    /// <summary>Still no name since the last look said so: nothing new to say.</summary>
    StillNotSignedIn,
}

/// <summary>
/// When to read this PC's own Steam name for the log's cleaner (<see cref="LogScrub.RegisterName"/>), and what each read found:
/// at once, then every <see cref="LookSeconds"/> while it's asked (a rename shows within that), and at every report. A report made
/// before any lobby then cleans the player's own name too (MelonLoader's credit line can be the same text). The name is held only
/// to tell a rename; the lines it gives never hold it. Not thread-safe: the game's thread only.
/// </summary>
public sealed class OwnNameWatch
{
    /// <summary>Seconds between two reads while the watch is ticked.</summary>
    public const double LookSeconds = 30;

    private double _nextLook = double.NegativeInfinity;
    private ulong _id;
    private string? _name;
    private bool _saidNoName;

    /// <summary>True once a name has been read this launch (the cleaner knows it).</summary>
    public bool Known => _name != null;

    /// <summary>True when a read is due at <paramref name="nowSeconds"/> (any monotonic clock): the first at once.</summary>
    public bool Due(double nowSeconds) => nowSeconds >= _nextLook;

    /// <summary>What a read at <paramref name="nowSeconds"/> found: <paramref name="steamId"/> 0 or no name is no name. The next read is due <see cref="LookSeconds"/> later.</summary>
    public OwnNameLook Seen(ulong steamId, string? name, double nowSeconds)
    {
        _nextLook = nowSeconds + LookSeconds;
        if (steamId == 0 || string.IsNullOrWhiteSpace(name) || name == DisplayName.SteamUnknown)
        {
            bool first = !_saidNoName;
            _saidNoName = true;
            return first ? OwnNameLook.NotSignedIn : OwnNameLook.StillNotSignedIn;
        }

        _saidNoName = false;
        if (_name == null)
        {
            _id = steamId;
            _name = name;
            return OwnNameLook.First;
        }

        if (steamId == _id && string.Equals(name, _name, StringComparison.Ordinal))
        {
            return OwnNameLook.Same;
        }

        _id = steamId;
        _name = name;
        return OwnNameLook.Changed;
    }

    /// <summary>
    /// The log line a read is worth, or null: the first name and a change once each, no name once (while none was ever known, a
    /// report says the cleaner can't know it). <paramref name="when"/> says what asked ("Steam signed in", "a report"). Never the name.
    /// </summary>
    public string? Line(OwnNameLook look, string when) => look switch
    {
        OwnNameLook.First => $"Log: this PC's own Steam name is known to the log's cleaner ({when}).",
        OwnNameLook.Changed => $"Log: this PC's own Steam name changed; the log's cleaner knows the new one too ({when}).",
        OwnNameLook.NotSignedIn when !Known => $"Log: this PC's own Steam name isn't known to the log's cleaner ({when}: Steam isn't signed in or can't be asked); names read before are still cleaned.",
        _ => null,
    };
}
