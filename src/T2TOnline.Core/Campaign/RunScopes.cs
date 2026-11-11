using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace T2TOnline.Core.Campaign;

/// <summary>A stat's "this run" and "this act" values (a whole or a real number each, as the stat counts).</summary>
/// <param name="Run">The value this run.</param>
/// <param name="Act">The value this act.</param>
public readonly record struct ScopeValues(StatValue Run, StatValue Act);

/// <summary>
/// One run's per-run counters, which the game keeps in the profile and the stats file rather than in the run: every stat's
/// "this run" and "this act" value (the "most ... in a run" records and the run history read them) and the bench quests'
/// per-run counts. A stat not listed is at zero.
/// </summary>
public sealed class RunScope
{
    /// <summary>A scope; <paramref name="quests"/> must hold <see cref="RunScopeStore.QuestCounters"/> counts.</summary>
    public RunScope(IReadOnlyList<int> quests, IReadOnlyDictionary<int, ScopeValues> stats)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(stats);
        if (quests.Count != RunScopeStore.QuestCounters)
        {
            throw new ArgumentException($"{RunScopeStore.QuestCounters} bench quest counts.", nameof(quests));
        }

        Quests = quests.ToArray();
        Stats = new Dictionary<int, ScopeValues>(stats);
    }

    /// <summary>A new run's: everything at zero.</summary>
    public static RunScope Empty => new(new int[RunScopeStore.QuestCounters], new Dictionary<int, ScopeValues>());

    /// <summary>The bench quests' per-run counts (the order of <see cref="ProgressLedger"/>'s run counts).</summary>
    public IReadOnlyList<int> Quests { get; }

    /// <summary>Each stat's run and act values, by the game's <c>StatType</c> number (not listed: zero).</summary>
    public IReadOnlyDictionary<int, ScopeValues> Stats { get; }

    /// <summary>
    /// True when <paramref name="other"/> holds the same counters: the same quest counts, and every stat at the same values
    /// (a stat one of them doesn't list counts as zero; a whole and a real number compare by value).
    /// </summary>
    public bool SameAs(RunScope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Quests.SequenceEqual(other.Quests))
        {
            return false;
        }

        foreach (int stat in Stats.Keys.Union(other.Stats.Keys))
        {
            ScopeValues mine = Stats.TryGetValue(stat, out ScopeValues a) ? a : default;
            ScopeValues theirs = other.Stats.TryGetValue(stat, out ScopeValues b) ? b : default;
            if (Number(mine.Run) != Number(theirs.Run) || Number(mine.Act) != Number(theirs.Act))
            {
                return false;
            }
        }

        return true;
    }

    private static double Number(StatValue value) => value.Kind == StatValueKind.Real ? value.RealValue : value.WholeValue;
}

/// <summary>
/// A host's per-run counters for each run file it plays (the solo run and each co-op slot, D46): the game has one set, so
/// when the run file switches, the outgoing run's counters are kept here and the incoming run's come back (zero for a run
/// never kept). <see cref="Live"/> says whose counters are in the game's memory (and, once saved, its files): a game that
/// closed on a co-op slot starts again on the solo run file with the slot's counters, and the store says so. After a
/// restore (<see cref="RunScopeRestore"/>) <see cref="Reload"/> says the counters in memory belong to nobody yet: the
/// live run file's kept scope goes into memory first. Saved as a small text file by the game glue; parsing is tolerant
/// (a bad line is skipped). Game-independent. Not thread-safe.
/// </summary>
public sealed class RunScopeStore
{
    /// <summary>Run files remembered (the solo run and three co-op slots fit with room to spare; the oldest go first).</summary>
    public const int MaxScopes = 8;

    /// <summary>The bench quest counts kept per run.</summary>
    public const int QuestCounters = ProgressLedger.RunCounters;

    private static readonly char[] PathSeparators = { '/', '\\', ':' };

    // What separates the words of a line, the CR of a CR LF line ending included (as in the ledger).
    private static readonly char[] Blanks = { ' ', '\t', '\r' };

    private readonly List<(string File, RunScope Scope)> _scopes = new();
    private bool _reload;

    /// <summary>The run file whose counters are in the game's memory, or null when never switched.</summary>
    public string? Live { get; private set; }

    /// <summary>True when something changed since the last <see cref="Format"/>.</summary>
    public bool Changed { get; private set; }

    /// <summary>Run files kept.</summary>
    public int Count => _scopes.Count;

    /// <summary>The run files kept, oldest first (a copy).</summary>
    public IReadOnlyList<string> Files => _scopes.Select(s => s.File).ToList();

    /// <summary>
    /// True after a restore changed what the game's files hold (D46): the counters in memory once the game has
    /// loaded its files are not <see cref="Live"/>'s, and <see cref="Live"/>'s kept scope must be written into memory
    /// before anything else (a switch puts that scope aside, not memory's).
    /// </summary>
    public bool Reload
    {
        get => _reload;
        set
        {
            if (_reload != value)
            {
                _reload = value;
                Changed = true;
            }
        }
    }

    /// <summary>True for a run file name the store keeps: a plain file name (no path, no spaces), as the game and the mod name them.</summary>
    public static bool IsRunFile(string? name) =>
        ProgressLimits.IsPlausibleId(name) && name!.IndexOfAny(PathSeparators) < 0;

    /// <summary>Run file <paramref name="file"/>'s counters, or false when none are kept.</summary>
    public bool TryGet(string file, out RunScope scope)
    {
        foreach ((string kept, RunScope keptScope) in _scopes)
        {
            if (string.Equals(kept, file, StringComparison.OrdinalIgnoreCase))
            {
                scope = keptScope;
                return true;
            }
        }

        scope = RunScope.Empty;
        return false;
    }

    /// <summary>Keeps run file <paramref name="file"/>'s counters (the oldest other file goes beyond <see cref="MaxScopes"/>).</summary>
    public void Set(string file, RunScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsRunFile(file))
        {
            throw new ArgumentException("A run file's name.", nameof(file));
        }

        _scopes.RemoveAll(s => string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase));
        _scopes.Add((file, scope));
        while (_scopes.Count > MaxScopes)
        {
            int oldest = _scopes.FindIndex(s => !string.Equals(s.File, Live, StringComparison.OrdinalIgnoreCase));
            _scopes.RemoveAt(oldest < 0 ? 0 : oldest);
        }

        Changed = true;
    }

    /// <summary>Forgets run file <paramref name="file"/>'s counters, if any are kept (<see cref="Live"/> stays as it is).</summary>
    public void Forget(string file)
    {
        if (_scopes.RemoveAll(s => string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Changed = true;
        }
    }

    /// <summary>Run file <paramref name="file"/>'s counters are the ones in the game's memory now.</summary>
    public void SetLive(string file)
    {
        if (!IsRunFile(file))
        {
            throw new ArgumentException("A run file's name.", nameof(file));
        }

        if (!string.Equals(Live, file, StringComparison.Ordinal))
        {
            Live = file;
            Changed = true;
        }
    }

    /// <summary>A copy of the store (its <see cref="Changed"/> cleared).</summary>
    public RunScopeStore Clone()
    {
        var copy = new RunScopeStore { Live = Live, _reload = _reload };
        copy._scopes.AddRange(_scopes);
        return copy;
    }

    /// <summary>The store as text (and <see cref="Changed"/> is cleared).</summary>
    public string Format()
    {
        var text = new StringBuilder();
        text.Append("# T2T Online: each run file's per-run counters (the bench quests' counts this run, every stat's value this run and " +
            "this act) while another run file is the game's; 'live' is the one in the game's memory ('reload': after a restore, " +
            "its kept counters go into memory first).\n");
        if (Live != null)
        {
            text.Append(CultureInfo.InvariantCulture, $"live {Live}\n");
        }

        if (_reload)
        {
            text.Append("reload\n");
        }

        foreach ((string file, RunScope scope) in _scopes)
        {
            text.Append(CultureInfo.InvariantCulture, $"scope {file} {string.Join(' ', scope.Quests.Select(q => q.ToString(CultureInfo.InvariantCulture)))}\n");
            foreach ((int stat, ScopeValues values) in scope.Stats.OrderBy(s => s.Key))
            {
                bool real = values.Run.Kind == StatValueKind.Real || values.Act.Kind == StatValueKind.Real;
                text.Append(CultureInfo.InvariantCulture, $"stat {file} {stat} {(real ? 'r' : 'w')} {Number(values.Run, real)} {Number(values.Act, real)}\n");
            }
        }

        Changed = false;
        return text.ToString();
    }

    /// <summary>A store from <see cref="Format"/>'s text; lines that don't read are skipped. Never throws.</summary>
    public static RunScopeStore Parse(string? text)
    {
        var store = new RunScopeStore();
        var quests = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var stats = new Dictionary<string, Dictionary<int, ScopeValues>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string[] parts = raw.Split('#')[0].Split(Blanks, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "live" && IsRunFile(parts[1]))
            {
                store.Live = parts[1];
            }
            else if (parts.Length == 1 && parts[0] == "reload")
            {
                store._reload = true;
            }
            else if (parts.Length == 2 + QuestCounters && parts[0] == "scope" && IsRunFile(parts[1]))
            {
                int[] counts = new int[QuestCounters];
                bool ok = true;
                for (int i = 0; i < QuestCounters && ok; i++)
                {
                    ok = int.TryParse(parts[2 + i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out counts[i]);
                }

                if (ok)
                {
                    Note(order, parts[1]);
                    quests[parts[1]] = counts;
                }
            }
            else if (parts.Length == 6 && parts[0] == "stat" && IsRunFile(parts[1]) &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int stat) && stat <= ProgressLimits.MaxStat &&
                parts[3] is "w" or "r" && TryValue(parts[4], parts[3] == "r", out StatValue run) && TryValue(parts[5], parts[3] == "r", out StatValue act))
            {
                Note(order, parts[1]);
                if (!stats.TryGetValue(parts[1], out Dictionary<int, ScopeValues>? values))
                {
                    stats[parts[1]] = values = new Dictionary<int, ScopeValues>();
                }

                values[stat] = new ScopeValues(run, act);
            }
        }

        foreach (string file in order)
        {
            store.Set(file, new RunScope(
                quests.TryGetValue(file, out int[]? counts) ? counts : new int[QuestCounters],
                stats.TryGetValue(file, out Dictionary<int, ScopeValues>? values) ? values : new Dictionary<int, ScopeValues>()));
        }

        store._reload &= store.Live != null; // a reload needs a live run file to reload
        store.Changed = false;
        return store;
    }

    private static void Note(List<string> order, string file)
    {
        if (!order.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            order.Add(file);
        }
    }

    private static string Number(StatValue value, bool real) => real
        ? (value.Kind == StatValueKind.Real ? value.RealValue : value.WholeValue).ToString("R", CultureInfo.InvariantCulture)
        : value.WholeValue.ToString(CultureInfo.InvariantCulture);

    private static bool TryValue(string text, bool real, out StatValue value)
    {
        value = StatValue.None;
        if (real)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number))
            {
                return false;
            }

            value = StatValue.Real(number);
            return true;
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int whole))
        {
            return false;
        }

        value = StatValue.Whole(whole);
        return true;
    }
}
