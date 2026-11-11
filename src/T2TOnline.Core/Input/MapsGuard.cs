using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Input;

/// <summary>What a check of a friend's virtual controller found (<see cref="MapsGuard.Check"/>).</summary>
public enum MapsVerdict
{
    /// <summary>Nothing to compare with yet (the maps were never built).</summary>
    NoExpectation,

    /// <summary>The maps are the ones the mod built.</summary>
    Fine,

    /// <summary>The maps drifted: put them back (<see cref="MapsGuard.NoteFix"/> after).</summary>
    Fix,

    /// <summary>The maps drifted, and putting them back failed too often: only counted from now on.</summary>
    CountOnly,
}

/// <summary>
/// One friend's virtual controller maps on the host, kept as the mod built them (the friend's own maps, or the game's
/// template maps when they're on the defaults) for the whole session: what they should be (<see cref="Expected"/>, taken
/// right after every build), checks against what they are, which drifts were logged (once per kind, then counted), whether
/// the categories the game switches on and off changed, and the counts for the friend's summary line. The game's side
/// (reading the maps, rebuilding them) is the plugin's; nothing here touches the game.
/// </summary>
public sealed class MapsGuard
{
    /// <summary>Fixes in a row that may fail before the guard only counts.</summary>
    public const int DefaultMaxFailedFixes = 3;

    /// <summary>Category changes named in the log per friend; later ones are counted.</summary>
    public const int DefaultCategoryChangesNamed = 12;

    private readonly int _maxFailedFixes;
    private readonly int _categoryChangesNamed;
    private readonly HashSet<string> _kindsLogged = new(StringComparer.Ordinal);
    private int _failedInARow;
    private int[] _watchedEnabled = Array.Empty<int>();
    private int[] _watchedPresent = Array.Empty<int>();
    private bool _watched;

    /// <summary>A guard that gives up fixing after <paramref name="maxFailedFixes"/> failures in a row and names <paramref name="categoryChangesNamed"/> category changes.</summary>
    public MapsGuard(int maxFailedFixes = DefaultMaxFailedFixes, int categoryChangesNamed = DefaultCategoryChangesNamed)
    {
        if (maxFailedFixes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFailedFixes));
        }

        if (categoryChangesNamed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(categoryChangesNamed));
        }

        _maxFailedFixes = maxFailedFixes;
        _categoryChangesNamed = categoryChangesNamed;
    }

    /// <summary>The maps as the mod last built them, or null before the first build.</summary>
    public ControllerMapsPrint? Expected { get; private set; }

    /// <summary>The categories on at the last reading whose maps were all there: what a map put back is set to.</summary>
    public IReadOnlyList<int> LastGoodEnabled { get; private set; } = Array.Empty<int>();

    /// <summary>Times the mod built the maps (a controller made, the friend's controls applied).</summary>
    public int Applied { get; private set; }

    /// <summary>Full checks.</summary>
    public int Checks { get; private set; }

    /// <summary>Checks that found drift.</summary>
    public int Drifts { get; private set; }

    /// <summary>Drifts put back.</summary>
    public int Corrected { get; private set; }

    /// <summary>Drifts that couldn't be put back.</summary>
    public int NotCorrected { get; private set; }

    /// <summary>Times the categories that are on changed (the game switching them: menus, matches, goalie, brawls).</summary>
    public int CategoryChanges { get; private set; }

    /// <summary>True once fixing failed <c>maxFailedFixes</c> times in a row: drift is only counted until the next build.</summary>
    public bool GaveUp => _failedInARow >= _maxFailedFixes;

    /// <summary>
    /// The mod built the maps (<paramref name="built"/>, read back right after): that's what they should be from now on.
    /// A failed run of fixes starts over.
    /// </summary>
    public void NoteApplied(ControllerMapsPrint built)
    {
        ArgumentNullException.ThrowIfNull(built);
        Expected = built;
        Applied++;
        _failedInARow = 0;
        if (built.Categories.Count > 0)
        {
            LastGoodEnabled = built.Enabled;
        }
    }

    /// <summary>
    /// The maps were built but couldn't be read back: nothing to compare with until the next build (a stale expectation
    /// would make every check a drift).
    /// </summary>
    public void ForgetExpected() => Expected = null;

    /// <summary>
    /// A quick look at the controller's maps (each map's category and whether it's on, <paramref name="maps"/>): true when
    /// the categories that are on, or the categories with a map, differ from the last look, so a full check should follow
    /// now. <paramref name="name"/> is set when this change is one to name in the log (the first ones), with
    /// <paramref name="was"/> the categories on before.
    /// </summary>
    public bool Watch(IEnumerable<(int Category, bool Enabled)> maps, out bool name, out IReadOnlyList<int> was)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var present = new SortedSet<int>();
        var enabled = new SortedSet<int>();
        foreach ((int category, bool on) in maps)
        {
            present.Add(category);
            if (on)
            {
                enabled.Add(category);
            }
        }

        int[] enabledNow = enabled.ToArray();
        int[] presentNow = present.ToArray();
        was = _watchedEnabled;
        bool first = !_watched;
        bool changed = !first && (!enabledNow.SequenceEqual(_watchedEnabled) || !presentNow.SequenceEqual(_watchedPresent));
        bool enabledChanged = !first && !enabledNow.SequenceEqual(_watchedEnabled);
        _watched = true;
        _watchedEnabled = enabledNow;
        _watchedPresent = presentNow;
        if (Expected != null && Expected.Categories.Keys.All(present.Contains))
        {
            LastGoodEnabled = enabledNow;
        }

        name = false;
        if (enabledChanged)
        {
            CategoryChanges++;
            name = CategoryChanges <= _categoryChangesNamed;
        }

        return changed;
    }

    /// <summary>
    /// A full check of <paramref name="now"/> against <see cref="Expected"/>: what to do, the <paramref name="drift"/>
    /// found, and whether this kind of drift is new (<paramref name="logIt"/>: name it in the log; else count it).
    /// </summary>
    public MapsVerdict Check(ControllerMapsPrint now, out MapsDrift? drift, out bool logIt)
    {
        ArgumentNullException.ThrowIfNull(now);
        drift = null;
        logIt = false;
        Checks++;
        if (Expected == null)
        {
            return MapsVerdict.NoExpectation;
        }

        MapsDrift found = MapsDrift.Between(Expected, now);
        if (found.None)
        {
            LastGoodEnabled = now.Enabled;
            return MapsVerdict.Fine;
        }

        drift = found;
        Drifts++;
        logIt = _kindsLogged.Add(found.Kind);
        if (GaveUp)
        {
            NotCorrected++;
            return MapsVerdict.CountOnly;
        }

        return MapsVerdict.Fix;
    }

    /// <summary>
    /// The maps were put back after <see cref="Check"/> said <see cref="MapsVerdict.Fix"/>; <paramref name="after"/> is
    /// them read again. True when they're the expected maps again. <paramref name="gaveUp"/> is true when this failure is
    /// the one that stops further fixes (said once).
    /// </summary>
    public bool NoteFix(ControllerMapsPrint after, out bool gaveUp)
    {
        ArgumentNullException.ThrowIfNull(after);
        gaveUp = false;
        if (Expected != null && after.SameMapsAs(Expected))
        {
            Corrected++;
            _failedInARow = 0;
            return true;
        }

        NotCorrected++;
        _failedInARow++;
        gaveUp = _failedInARow == _maxFailedFixes;
        return false;
    }

    /// <summary>
    /// The friend's summary, for the log when they leave: "maps applied 2 time(s), checked 140 time(s), drift corrected 0
    /// time(s); map categories changed 9 time(s); fingerprint 1a2b3c4d".
    /// </summary>
    public string Summary() =>
        string.Create(CultureInfo.InvariantCulture,
            $"maps applied {Applied} time(s), checked {Checks} time(s), drift corrected {Corrected} time(s)") +
        (NotCorrected > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({NotCorrected} not corrected)") : string.Empty) +
        string.Create(CultureInfo.InvariantCulture, $"; map categories changed {CategoryChanges} time(s)") +
        (Expected != null ? $"; fingerprint {Expected.Tag}" : string.Empty);
}

/// <summary>
/// When the host checks friends' virtual controller maps: every <see cref="Interval"/>, and sooner when something asks
/// (a scene loaded: the game sets its players up again). Pure timing, no game.
/// </summary>
public sealed class MapsCheckSchedule
{
    private TimeSpan _next;
    private TimeSpan _requestedAt = TimeSpan.MaxValue;
    private string _requestedWhy = string.Empty;

    /// <summary>A schedule checking every <paramref name="interval"/>, the first time at once.</summary>
    public MapsCheckSchedule(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        Interval = interval;
        _next = TimeSpan.MinValue;
    }

    /// <summary>The longest time between checks.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Asks for a check at <paramref name="at"/> (or sooner, if another request or the interval comes first).</summary>
    public void Request(TimeSpan at, string why)
    {
        if (at < _requestedAt)
        {
            _requestedAt = at;
            _requestedWhy = why ?? string.Empty;
        }
    }

    /// <summary>True when a check is due at <paramref name="now"/>, with why ("every 3 s", or the request's reason).</summary>
    public bool Due(TimeSpan now, out string why)
    {
        if (now >= _requestedAt)
        {
            why = _requestedWhy;
            return true;
        }

        if (now >= _next)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"every {Interval.TotalSeconds:0.#} s");
            return true;
        }

        why = string.Empty;
        return false;
    }

    /// <summary>A check ran at <paramref name="now"/>: requests up to then are done, the next periodic one is an interval on.</summary>
    public void Done(TimeSpan now)
    {
        _next = now + Interval;
        if (_requestedAt <= now)
        {
            _requestedAt = TimeSpan.MaxValue;
            _requestedWhy = string.Empty;
        }
    }
}
