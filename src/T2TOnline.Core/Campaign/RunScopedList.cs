using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>What became of an item given to <see cref="RunScopedList{T}.Add"/>.</summary>
public enum RunScopedAdd
{
    /// <summary>Kept, for its run (or waiting for one).</summary>
    Added,

    /// <summary>Already kept.</summary>
    AlreadyNoted,

    /// <summary>The list is full.</summary>
    Full,

    /// <summary>Its run is one a later run replaced (its screen is behind): not kept.</summary>
    EarlierRun,
}

/// <summary>
/// Items noted during a session, each with the host's run it came in: a friend's own new unlocks, shown on the host's
/// new-unlocks screen for that run only. The list follows one run at a time: the first item of another run starts that run's
/// turn, which drops the earlier run's items (a run whose screen never opened: they stay on the player's profile, just not on
/// a later run's screen) and gives the items waiting for their run (noted before it was known: the squad menu's grants, at the
/// run's start) to it. An item of a run whose turn is over (a late copy of an earlier run's progress) isn't kept. Each item is
/// kept once; at most <see cref="Capacity"/>. Not thread-safe.
/// </summary>
/// <typeparam name="T">The item (an unlock's id).</typeparam>
public sealed class RunScopedList<T>
    where T : notnull
{
    /// <summary>The usual most items kept.</summary>
    public const int DefaultCapacity = 64;

    /// <summary>Most earlier runs remembered (their late items refused).</summary>
    public const int MaxEarlierRuns = 32;

    private readonly List<(T Item, string? Run)> _items = new();
    private readonly List<string> _earlier = new();
    private readonly IEqualityComparer<T> _comparer;

    /// <summary>A list keeping at most <paramref name="capacity"/> items, told apart by <paramref name="comparer"/> (the default when null).</summary>
    public RunScopedList(int capacity = DefaultCapacity, IEqualityComparer<T>? comparer = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "A list keeps at least one item.");
        }

        Capacity = capacity;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <summary>The most items kept.</summary>
    public int Capacity { get; }

    /// <summary>How many items are kept, of the run whose turn it is and waiting for one.</summary>
    public int Count => _items.Count;

    /// <summary>The run whose turn it is, or null before any.</summary>
    public string? Run { get; private set; }

    /// <summary>
    /// Notes <paramref name="item"/> as one of <paramref name="run"/>'s (null or "": its run isn't known yet, it waits for the
    /// next). A run that isn't the current one starts its turn first (<paramref name="dropped"/>: how many of the earlier
    /// run's items went). Says what became of the item.
    /// </summary>
    public RunScopedAdd Add(T item, string? run, out int dropped)
    {
        dropped = 0;
        string? known = string.IsNullOrEmpty(run) ? null : run;
        if (known != null)
        {
            if (IsEarlier(known))
            {
                return RunScopedAdd.EarlierRun;
            }

            dropped = Turn(known);
        }

        if (_items.Any(e => _comparer.Equals(e.Item, item)))
        {
            return RunScopedAdd.AlreadyNoted;
        }

        if (_items.Count >= Capacity)
        {
            return RunScopedAdd.Full;
        }

        _items.Add((item, known));
        return RunScopedAdd.Added;
    }

    /// <summary>
    /// The items for <paramref name="run"/>'s screen: it becomes the current run if it isn't (the earlier run's items dropped,
    /// <paramref name="dropped"/>; the waiting ones its), and its items are given. An earlier run's screen gets none. With no
    /// run known (null or ""), every item kept, and none dropped.
    /// </summary>
    public List<T> Take(string? run, out int dropped)
    {
        dropped = 0;
        if (string.IsNullOrEmpty(run))
        {
            return _items.Select(e => e.Item).ToList();
        }

        if (IsEarlier(run))
        {
            return new List<T>();
        }

        dropped = Turn(run);
        return _items.Select(e => e.Item).ToList();
    }

    /// <summary>The items <see cref="Take"/> would give for <paramref name="run"/>, changing nothing.</summary>
    public List<T> Peek(string? run)
    {
        if (string.IsNullOrEmpty(run))
        {
            return _items.Select(e => e.Item).ToList();
        }

        return IsEarlier(run) ? new List<T>() : _items.Where(e => e.Run == null || string.Equals(e.Run, run, StringComparison.Ordinal)).Select(e => e.Item).ToList();
    }

    /// <summary>
    /// The host's run <paramref name="run"/> is the one being played again, whose turn was over (the host went back to its co-op slot
    /// within a session; a late copy of its progress never comes through here): its turn starts anew, the current run's items dropped
    /// (<paramref name="dropped"/>). False, and nothing changes, for a run that isn't an earlier one.
    /// </summary>
    public bool BroughtBack(string? run, out int dropped)
    {
        dropped = 0;
        if (string.IsNullOrEmpty(run) || !_earlier.Remove(run))
        {
            return false;
        }

        dropped = Turn(run);
        return true;
    }

    /// <summary>Forgets every item (the run whose turn it is and the earlier ones stay known).</summary>
    public void Clear() => _items.Clear();

    /// <summary>Forgets everything, the runs too (a new session).</summary>
    public void Reset()
    {
        _items.Clear();
        _earlier.Clear();
        Run = null;
    }

    private bool IsEarlier(string run) => _earlier.Contains(run, StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="run"/>'s turn: when it isn't the current run, the current one's turn is over (its items dropped) and
    /// the waiting items are <paramref name="run"/>'s. How many items were dropped.
    /// </summary>
    private int Turn(string run)
    {
        if (string.Equals(run, Run, StringComparison.Ordinal))
        {
            return 0;
        }

        int dropped = _items.RemoveAll(e => e.Run != null);
        if (Run != null)
        {
            if (_earlier.Count >= MaxEarlierRuns)
            {
                _earlier.RemoveAt(0);
            }

            _earlier.Add(Run);
        }

        Run = run;
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i] = (_items[i].Item, run);
        }

        return dropped;
    }
}
