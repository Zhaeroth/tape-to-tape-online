using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Stream;

/// <summary>
/// A friend's rule for a host's game event that names an object not found on this PC (the plugin's
/// <c>Streaming/EventChannel</c>). The host sends each argument's objects by reference; one this PC doesn't have (an object
/// the stream couldn't map here) decodes as nothing.
/// <list type="bullet">
/// <item>An argument that is itself such an object (<c>SoundManager.PickupPuck(Forward)</c>): every listener would be
/// handed null for its parameter, and the event is about an object this PC can't show. It goes to no listener
/// (<see cref="SkipsEvent"/>). An argument the host sent as nothing (a loose puck's possession) is the game's own null and
/// is delivered.</item>
/// <item>An argument class with such an object in a field (a check's checker): the other fields can still matter to a
/// listener (a goal's lights and crowd for a missing scorer), so it's delivered; a listener that threw on it once is
/// skipped for that event from then on while its objects are missing (<see cref="Skips"/>, <see cref="NoteFailure"/>).</item>
/// </list>
/// One per playback. Never throws.
/// </summary>
public sealed class MissingObjects
{
    private readonly HashSet<(string Event, string Owner)> _needTheirObjects = new();

    /// <summary>Listener classes skipped for an event while its objects are missing, as "Event → Owner".</summary>
    public IEnumerable<string> Learned
    {
        get
        {
            foreach ((string e, string owner) in _needTheirObjects)
            {
                yield return $"{e} → {owner}";
            }
        }
    }

    /// <summary>
    /// True when one of the event's arguments is an object the host named that isn't here: for each whole argument (not
    /// one flattened into fields), whether the host sent it as an object and whether it was found here.
    /// </summary>
    public static bool SkipsEvent(IReadOnlyList<(bool SentAsObject, bool FoundHere)> wholeArguments)
    {
        ArgumentNullException.ThrowIfNull(wholeArguments);
        foreach ((bool sent, bool found) in wholeArguments)
        {
            if (sent && !found)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="owner"/> threw on <paramref name="eventName"/> with objects missing, and some are missing now.</summary>
    public bool Skips(string eventName, string owner, bool objectsMissing) => objectsMissing && _needTheirObjects.Contains((eventName, owner));

    /// <summary>
    /// <paramref name="owner"/> threw on <paramref name="eventName"/>: when objects were missing, it's skipped for that event
    /// while they are from now on. True the first time (to log it).
    /// </summary>
    public bool NoteFailure(string eventName, string owner, bool objectsMissing) => objectsMissing && _needTheirObjects.Add((eventName, owner));

    /// <summary>Forgets what was learned (a new playback).</summary>
    public void Clear() => _needTheirObjects.Clear();
}
