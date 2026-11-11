using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// A list of save file names in one short piece of a log line. The save guard lets the game's run history save write its
/// index and any of its 500 slot files (the game picks the slot itself), and a line naming each would be about 10 KB at
/// every run's end. So files that differ only in a number (<c>RunHistory_00.es3</c> to <c>RunHistory_499.es3</c>) are
/// said as one range with their count once there are <see cref="GroupFrom"/> of them, and every other file is named (the
/// profile, the stats, the index: the ones a diagnosis asks about), up to <see cref="MostNamed"/> pieces. Names only:
/// the caller passes no folder. Game-independent; never throws.
/// </summary>
public static class SaveFileListText
{
    /// <summary>Files that differ only in a number are said as one piece from this many.</summary>
    public const int GroupFrom = 4;

    /// <summary>Pieces (a name, or a numbered family) said before "and N more".</summary>
    public const int MostNamed = 8;

    // A number longer than this isn't read as one (it would overflow, and no save file has such a name).
    private const int MostDigits = 9;

    /// <summary>
    /// <paramref name="names"/> in words: each name once (ignoring case), in the order given, with files that differ only
    /// in a number together where the first of them stood: <c>RunHistory_00.es3 to RunHistory_499.es3 (500 files)</c> when
    /// their numbers have no gap, else <c>37 files from RunHistory_02.es3 to RunHistory_480.es3</c>. "none" for an empty
    /// list; null and empty names are left out.
    /// </summary>
    public static string Describe(IEnumerable<string?>? names)
    {
        var pieces = new List<Piece>();
        var families = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in names ?? Array.Empty<string?>())
        {
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                continue;
            }

            if (!TrySplit(name, out string family, out long number))
            {
                pieces.Add(new Piece(name));
                continue;
            }

            if (!families.TryGetValue(family, out Piece? piece))
            {
                piece = new Piece(name);
                families[family] = piece;
                pieces.Add(piece);
            }

            piece.Add(name, number);
        }

        if (pieces.Count == 0)
        {
            return "none";
        }

        // A family too small to count is named file by file, where it stood.
        List<string> words = pieces.SelectMany(p => p.Words()).ToList();
        return words.Count <= MostNamed
            ? string.Join(", ", words)
            : string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", words.Take(MostNamed))} and {words.Count - MostNamed} more");
    }

    /// <summary>
    /// Splits <paramref name="name"/> at the number right before its extension (<c>RunHistory_07.es3</c>: the family
    /// <c>RunHistory_#.es3</c>, 7). False for a name with no such number.
    /// </summary>
    private static bool TrySplit(string name, out string family, out long number)
    {
        family = string.Empty;
        number = 0;
        int dot = name.LastIndexOf('.');
        int end = dot < 0 ? name.Length : dot;
        int start = end;
        while (start > 0 && name[start - 1] is >= '0' and <= '9')
        {
            start--;
        }

        if (start == end || end - start > MostDigits)
        {
            return false;
        }

        number = long.Parse(name.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture);
        family = string.Concat(name.AsSpan(0, start), "#", name.AsSpan(end));
        return true;
    }

    /// <summary>One name, or the files of one numbered family.</summary>
    private sealed class Piece
    {
        private readonly List<(string Name, long Number)> _members = new();
        private readonly string _single;

        public Piece(string name) => _single = name;

        public void Add(string name, long number) => _members.Add((name, number));

        public IEnumerable<string> Words()
        {
            if (_members.Count == 0)
            {
                return new[] { _single };
            }

            if (_members.Count < GroupFrom)
            {
                return _members.Select(m => m.Name);
            }

            (string Name, long Number) first = _members.MinBy(m => m.Number);
            (string Name, long Number) last = _members.MaxBy(m => m.Number);
            bool whole = last.Number - first.Number + 1 == _members.Count && _members.Select(m => m.Number).Distinct().Count() == _members.Count;
            return new[]
            {
                whole
                    ? string.Create(CultureInfo.InvariantCulture, $"{first.Name} to {last.Name} ({_members.Count} files)")
                    : string.Create(CultureInfo.InvariantCulture, $"{_members.Count} files from {first.Name} to {last.Name}"),
            };
        }
    }
}
