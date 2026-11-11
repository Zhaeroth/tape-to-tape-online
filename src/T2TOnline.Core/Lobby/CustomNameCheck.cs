using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// The <c>selfdrive team=custom</c> tour's check of a session log against a custom team's typed names (D127): the names are held
/// in memory only, every line is read once, and only counts and line numbers come out, never a name. A name is found where it
/// stands as typed (case and all) with no letter or digit right before or after it, so a short name inside a longer word is not a
/// hit; a name shorter than <see cref="MinLength"/> is not checked at all (it would match the log's own words), and is counted.
/// Beside the names it looks for the two lines the check exists to show: the log cleaner's count of custom names
/// (<c>Log: the log's cleaner knows N custom name(s)</c>) and a match setup's custom team by its shape (<c>a custom team (</c>).
/// </summary>
public sealed class CustomNameCheck
{
    /// <summary>The shortest typed name looked for.</summary>
    public const int MinLength = 3;

    /// <summary>The most line numbers kept for the summary.</summary>
    public const int MaxLinesKept = 8;

    /// <summary>The cleaner's line, up to its count.</summary>
    public const string CleanerStem = "Log: the log's cleaner knows ";

    /// <summary>A team written by its shape, as a setup line writes a custom team.</summary>
    public const string CustomTeamStem = "a custom team (";

    /// <summary>The setup line a host writes when it starts a match.</summary>
    public const string SetupStem = "Online: sent this match's setup to ";

    /// <summary>The label the log's backstop writes in place of a typed name a line slipped (<see cref="Diagnostics.LogScrub.PlayerTextLabel"/>).</summary>
    public const string BackstopStem = Diagnostics.LogScrub.PlayerTextLabel + " (";

    private readonly List<string> _names = new();
    private readonly HashSet<int> _found = new();
    private readonly List<int> _hitLines = new();
    private readonly List<int> _backstopLines = new();

    /// <summary>The names to look for: empty and repeated ones dropped, those shorter than <see cref="MinLength"/> counted and left out.</summary>
    public CustomNameCheck(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? name in names)
        {
            string text = name?.Trim() ?? string.Empty;
            if (text.Length == 0 || !seen.Add(text))
            {
                continue;
            }

            Held++;
            if (text.Length < MinLength)
            {
                TooShort++;
            }
            else
            {
                _names.Add(text);
            }
        }
    }

    /// <summary>Different typed names held (the short ones included).</summary>
    public int Held { get; }

    /// <summary>Names too short to look for.</summary>
    public int TooShort { get; }

    /// <summary>Names looked for.</summary>
    public int Checked => _names.Count;

    /// <summary>Lines read.</summary>
    public int Lines { get; private set; }

    /// <summary>Lines holding at least one name.</summary>
    public int HitCount { get; private set; }

    /// <summary>Different names found in any line.</summary>
    public int NamesFound => _found.Count;

    /// <summary>The first <see cref="MaxLinesKept"/> lines (from 1) holding a name.</summary>
    public IReadOnlyList<int> HitLines => _hitLines;

    /// <summary>Lines where the backstop wrote its label: a source slipped a name and the cleaner caught it.</summary>
    public int BackstopCount { get; private set; }

    /// <summary>The first <see cref="MaxLinesKept"/> lines (from 1) holding the backstop's label.</summary>
    public IReadOnlyList<int> BackstopLines => _backstopLines;

    /// <summary>The highest count the cleaner's lines gave, or -1 when none was read.</summary>
    public int CleanerCount { get; private set; } = -1;

    /// <summary>A setup line naming a custom team by its shape was read.</summary>
    public bool CustomTeamSetupSeen { get; private set; }

    /// <summary>Reads one line of the log (lines numbered from 1 in the order read). Never throws on what the line holds.</summary>
    public void Read(string? line)
    {
        Lines++;
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        bool hit = false;
        for (int i = 0; i < _names.Count; i++)
        {
            if (Holds(line, _names[i]))
            {
                hit = true;
                _found.Add(i);
            }
        }

        if (hit)
        {
            HitCount++;
            Keep(_hitLines, Lines);
        }

        // The cleaner's own line names the label it writes: not a slip.
        int cleaner = line.IndexOf(CleanerStem, StringComparison.Ordinal);
        if (cleaner >= 0)
        {
            if (LeadingNumber(line, cleaner + CleanerStem.Length) is int count)
            {
                CleanerCount = Math.Max(CleanerCount, count);
            }
        }
        else if (line.Contains(BackstopStem, StringComparison.Ordinal))
        {
            BackstopCount++;
            Keep(_backstopLines, Lines);
        }

        int setup = line.IndexOf(SetupStem, StringComparison.Ordinal);
        if (setup >= 0 && line.IndexOf(CustomTeamStem, setup, StringComparison.Ordinal) >= 0)
        {
            CustomTeamSetupSeen = true;
        }
    }

    /// <summary>What's wrong, or null: a name found, the cleaner's line missing or at 0, no setup naming a custom team.</summary>
    public string? Problem()
    {
        var problems = new List<string>();
        if (HitCount > 0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{NamesFound} of the {Checked} typed name(s) found in {HitCount} line(s) of the session log (lines {Join(_hitLines, HitCount)})"));
        }

        if (CleanerCount <= 0)
        {
            problems.Add(CleanerCount < 0 ? "no line of the log's cleaner's count of custom names" : "the log's cleaner knows 0 custom names");
        }

        if (!CustomTeamSetupSeen)
        {
            problems.Add("no match setup line names a custom team by its shape");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>The check in counts: <c>12 typed name(s) held, 11 checked (1 too short), 0 found in 1840 line(s); the cleaner knows 31; a setup names a custom team; the backstop's label in 0 line(s)</c>.</summary>
    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{Held} typed name(s) held, {Checked} checked ({TooShort} too short), {NamesFound} found in {HitCount} of {Lines} line(s); the cleaner knows {(CleanerCount < 0 ? "?" : CleanerCount.ToString(CultureInfo.InvariantCulture))}; " +
        $"{(CustomTeamSetupSeen ? "a setup names a custom team by its shape" : "no setup names a custom team")}; the backstop's label in {BackstopCount} line(s){(BackstopCount > 0 ? $" (lines {Join(_backstopLines, BackstopCount)})" : string.Empty)}");

    /// <summary>True when <paramref name="line"/> holds <paramref name="name"/> with no letter or digit right before or after it.</summary>
    public static bool Holds(string line, string name)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return false;
        }

        for (int at = line.IndexOf(name, StringComparison.Ordinal); at >= 0; at = line.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int end = at + name.Length;
            bool before = at > 0 && char.IsLetterOrDigit(line[at - 1]);
            bool after = end < line.Length && char.IsLetterOrDigit(line[end]);
            if (!before && !after)
            {
                return true;
            }
        }

        return false;
    }

    private static void Keep(List<int> lines, int line)
    {
        if (lines.Count < MaxLinesKept)
        {
            lines.Add(line);
        }
    }

    private static string Join(List<int> lines, int total)
    {
        var text = new StringBuilder();
        foreach (int line in lines)
        {
            text.Append(text.Length == 0 ? string.Empty : ", ").Append(line.ToString(CultureInfo.InvariantCulture));
        }

        return total > lines.Count ? string.Create(CultureInfo.InvariantCulture, $"{text}, and {total - lines.Count} more") : text.ToString();
    }

    private static int? LeadingNumber(string line, int from)
    {
        int end = from;
        while (end < line.Length && end - from < 9 && line[end] is >= '0' and <= '9')
        {
            end++;
        }

        return end > from && int.TryParse(line.AsSpan(from, end - from), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
    }
}
