using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// Game-independent helpers for a match setup's named values (<see cref="Protocol.MatchSetupMessage"/>): what to log
/// about one, and how two differ.
/// </summary>
public static class MatchSetupValues
{
    /// <summary>The host's id for one match: two matches with the same teams still differ by it.</summary>
    public const string MatchIdKey = "match.id";

    /// <summary>
    /// The number the host's game loads the match as (its count of matches loaded, from 1): the number a shared moment from
    /// it carries, so a friend's game knows which of the host's matches it played (D135).
    /// </summary>
    public const string HostSerialKey = "match.serial";

    /// <summary>
    /// The host's number for the match <paramref name="setup"/> describes (<see cref="HostSerialKey"/>), or 0 when it names
    /// none (or not a whole number above 0).
    /// </summary>
    public static int HostSerialOf(IReadOnlyList<KeyValuePair<string, NetValue>> setup) =>
        Get(setup, HostSerialKey) is { Kind: NetValueKind.Whole, WholeValue: > 0 and <= int.MaxValue } value ? (int)value.WholeValue : 0;

    /// <summary>The first value with this key, or <see cref="NetValue.Null"/>.</summary>
    public static NetValue Get(IReadOnlyList<KeyValuePair<string, NetValue>> setup, string key)
    {
        ArgumentNullException.ThrowIfNull(setup);
        foreach (KeyValuePair<string, NetValue> pair in setup)
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return NetValue.Null;
    }

    /// <summary>
    /// What differs between two setups, for a log line (empty when they agree): keys only one side has, and values that
    /// differ. The match id and the host's number for it are left out (a loaded match never has the host's). A key given twice counts by its first value,
    /// as <see cref="Get"/> reads it. A setup can come from a host, which chooses every byte of it, and a team's name and
    /// nickname can be a custom team's, typed by a player (D127, D146): a key is written only when it has an identifier's
    /// shape, a team's name or nickname (a key ending <c>.name</c> or <c>.nickname</c>) only by <paramref name="shape"/>, any other text value only
    /// when it has an identifier's shape (<see cref="HostText.KeyForLog(string?)"/>); numbers and flags as they are.
    /// </summary>
    /// <param name="expected">The setup the match should have (a host's, or a recording's).</param>
    /// <param name="actual">The setup the loaded match has.</param>
    /// <param name="shape">Player-written text as its shape (<see cref="PlayerTextShape.Describe"/>, which never throws).</param>
    /// <param name="maxListed">Most differences written; the rest are counted.</param>
    public static string Differences(
        IReadOnlyList<KeyValuePair<string, NetValue>> expected, IReadOnlyList<KeyValuePair<string, NetValue>> actual, Func<string?, string> shape, int maxListed = 12)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(shape);
        Dictionary<string, NetValue> want = ToDictionary(expected);
        Dictionary<string, NetValue> have = ToDictionary(actual);
        var differences = new List<string>();
        foreach ((string key, NetValue value) in want.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!have.TryGetValue(key, out NetValue other))
            {
                differences.Add($"{HostText.KeyForLog(key)} missing here");
            }
            else if (other != value)
            {
                differences.Add($"{HostText.KeyForLog(key)} {ValueForLog(key, value, shape)} vs {ValueForLog(key, other, shape)}");
            }
        }

        differences.AddRange(have.Keys.Where(k => !want.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{HostText.KeyForLog(k)} only here"));
        if (differences.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", differences.Take(maxListed)) + (differences.Count > maxListed ? $"; and {differences.Count - maxListed} more" : string.Empty);
    }

    // A setup key whose text is a team's name or nickname (home.name, visitor.nickname).
    private static bool IsNameKey(string key) =>
        key.EndsWith(".name", StringComparison.Ordinal) || key.EndsWith(".nickname", StringComparison.Ordinal);

    private static string ValueForLog(string key, NetValue value, Func<string?, string> shape)
    {
        if (value.Kind != NetValueKind.Text)
        {
            return value.ToString();
        }

        return IsNameKey(key) ? $"({shape(value.Text)})" : string.IsNullOrEmpty(value.Text) ? "\"\"" : $"\"{HostText.KeyForLog(value.Text)}\"";
    }

    private static Dictionary<string, NetValue> ToDictionary(IReadOnlyList<KeyValuePair<string, NetValue>> setup)
    {
        var values = new Dictionary<string, NetValue>(StringComparer.Ordinal);
        foreach ((string key, NetValue value) in setup)
        {
            if (key != MatchIdKey && key != HostSerialKey)
            {
                values.TryAdd(key, value);
            }
        }

        return values;
    }
}
