using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a friend's game took from the host's run ends in one session, for its stop line: the run
/// history entries saved, the squad results recorded and the unlocks earned; and the entries that failed here, each batch with
/// a failure warned about (the first <see cref="MaxFailureLines"/>, then only counted). Not thread-safe.
/// </summary>
public sealed class ProgressRunEnds
{
    /// <summary>Batches with a failed entry warned about in a session; further ones are counted in its stop line.</summary>
    public const int MaxFailureLines = 5;

    /// <summary>Most squad results or unlocks named in the stop line.</summary>
    public const int MaxNamed = 16;

    private readonly List<string> _squads = new();
    private readonly List<string> _unlocks = new();
    private int _squadCount;
    private int _unlockCount;
    private int _failureLines;

    /// <summary>Run history entries saved here.</summary>
    public int Histories { get; private set; }

    /// <summary>Entries of the host's batches that failed here.</summary>
    public int FailedEntries { get; private set; }

    /// <summary>A run history entry was saved here.</summary>
    public void HistorySaved() => Histories++;

    /// <summary>A squad result was recorded here ("gauntlet win on difficulty 2").</summary>
    public void Squad(string squad, bool won, int difficulty)
    {
        _squadCount++;
        if (_squads.Count < MaxNamed)
        {
            _squads.Add($"{HostText.KeyForLog(squad)} {(won ? "win" : "loss")} on difficulty {difficulty}");
        }
    }

    /// <summary>The host's progress unlocked <paramref name="id"/> here.</summary>
    public void Unlocked(string id)
    {
        _unlockCount++;
        if (_unlocks.Count < MaxNamed)
        {
            _unlocks.Add(HostText.KeyForLog(id));
        }
    }

    /// <summary><paramref name="entries"/> of a batch failed here: counted, and true when that batch's warning goes in the log.</summary>
    public bool Failed(int entries)
    {
        FailedEntries += entries;
        _failureLines++;
        return _failureLines <= MaxFailureLines;
    }

    /// <summary>
    /// The stop line's part: "; run's end: ...". <paramref name="duplicates"/>: batches that came again and were skipped.
    /// </summary>
    public string Part(int duplicates) =>
        $"; run's end: {Histories} history entr{(Histories == 1 ? "y" : "ies")} saved, {_squadCount} squad result(s) recorded{Named(_squads, _squadCount)}, " +
        $"{_unlockCount} unlock(s){Named(_unlocks, _unlockCount)}, {FailedEntries} entr{(FailedEntries == 1 ? "y" : "ies")} failed; " +
        $"{duplicates} batch(es) that came again were already applied here (skipped)";

    private static string Named(List<string> names, int count) =>
        names.Count == 0 ? string.Empty : $" ({string.Join(", ", names)}{(count > names.Count ? $", {count - names.Count} more" : string.Empty)})";
}
