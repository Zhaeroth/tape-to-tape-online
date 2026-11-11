using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// How a friend's log names an object of a host's object table (D127). The game names each skater's object after the
/// skater, first name and last (<c>GameControllerBase.InitializeForward</c>), and a custom skater's name is typed by its
/// player, so a skater's or a referee's entry never goes by the name the host sent: it goes by the asset id its stable key
/// holds (<c>skater 0f2b7633-...</c>, the same on the host and on every friend), or <c>a skater</c> when the key has none.
/// Any other object keeps the host's name for it (its prefab's, or one the game's code gave it).
/// </summary>
public static class ObjectEntryLogText
{
    /// <summary>What a stream object's stable key starts with when the object is a skater (then its side, then its asset id).</summary>
    public const string SkaterKeyPrefix = "F:";

    /// <summary>What a stream object's stable key starts with when the object is a referee (a skater's object too).</summary>
    public const string RefereeKeyPrefix = "R:";

    /// <summary>What a skater with no usable asset id in its key is called.</summary>
    public const string UnnamedSkater = "a skater";

    /// <summary>The longest asset id written.</summary>
    public const int MaxIdLength = 48;

    /// <summary>True for the stable key of a skater's or a referee's object, which the game names after the skater.</summary>
    public static bool IsSkaterKey(string? key) =>
        key != null && (key.StartsWith(SkaterKeyPrefix, StringComparison.Ordinal) || key.StartsWith(RefereeKeyPrefix, StringComparison.Ordinal));

    /// <summary>
    /// A skater by its asset id for a log line: <c>skater &lt;id&gt;</c>, or <see cref="UnnamedSkater"/> when
    /// <paramref name="assetId"/> is empty, unknown (<c>?</c>), over <see cref="MaxIdLength"/> characters or not shaped like
    /// an id (ASCII letters, digits, hyphens, underscores and dots: the id may come from a host's key).
    /// </summary>
    public static string Skater(string? assetId)
    {
        if (string.IsNullOrEmpty(assetId) || assetId.Length > MaxIdLength)
        {
            return UnnamedSkater;
        }

        bool any = false;
        foreach (char c in assetId)
        {
            bool shaped = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.';
            if (!shaped)
            {
                return UnnamedSkater;
            }

            any = true;
        }

        return any ? $"skater {assetId}" : UnnamedSkater;
    }

    /// <summary>
    /// An object a host's table names, for a log line on a PC that has only the table's entry: <paramref name="name"/> as the
    /// host sent it, but for a skater's or referee's object (<paramref name="key"/>: its stable key, <c>F:H:&lt;id&gt;</c>,
    /// <c>F:V:&lt;id&gt;</c>, <c>R:&lt;id&gt;</c>) the skater by the key's asset id (<see cref="Skater"/>), never the name.
    /// </summary>
    public static string OfEntry(string? key, string? name)
    {
        if (!IsSkaterKey(key))
        {
            return name ?? string.Empty;
        }

        // "R:<id>", or "F:<side>:<id>" (the side is one character).
        int idAt = key!.StartsWith(RefereeKeyPrefix, StringComparison.Ordinal) ? RefereeKeyPrefix.Length : key.IndexOf(':', SkaterKeyPrefix.Length) + 1;
        return Skater(idAt > 0 && idAt < key.Length ? key[idAt..] : null);
    }
}

/// <summary>
/// The names of the skaters a PC's log lines have met this launch (their objects' names, their data's asset names): a
/// custom skater's is typed by its player, and which is which isn't asked. A text that may hold one, a scene path through
/// a skater's object or an asset's name from a host, is written with each replaced by its shape (D127). At most
/// <see cref="MaxNames"/> names, the oldest forgotten first. One thread.
/// </summary>
public sealed class PlayerWrittenNames
{
    /// <summary>Most names kept (a match's two teams have fewer skaters, with a name each for the object and its data).</summary>
    public const int MaxNames = 128;

    /// <summary>The shortest name kept: a shorter one would be replaced inside ordinary words.</summary>
    public const int MinLength = 3;

    private readonly List<string> _names = new();

    /// <summary>Names kept now.</summary>
    public int Count => _names.Count;

    /// <summary>Keeps <paramref name="name"/> (trimmed) as a skater's; null, short and known ones change nothing.</summary>
    public void Note(string? name)
    {
        string? kept = name?.Trim();
        if (kept == null || kept.Length < MinLength || _names.Contains(kept))
        {
            return;
        }

        if (_names.Count >= MaxNames)
        {
            _names.RemoveAt(0);
        }

        _names.Add(kept);
    }

    /// <summary>
    /// <paramref name="text"/> with every kept name in it replaced by <c>(&lt;shape&gt;)</c>, the longest names first (a name
    /// inside a longer one doesn't split it). A text with none comes back as it is; null as the empty string.
    /// </summary>
    /// <param name="text">The text to write.</param>
    /// <param name="shape">Player-written text as its shape (<see cref="PlayerTextShape.Describe"/>, which never throws).</param>
    public string Hide(string? text, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (string.IsNullOrEmpty(text) || _names.Count == 0)
        {
            return text ?? string.Empty;
        }

        string result = text;
        List<string>? found = null;
        foreach (string name in _names)
        {
            if (result.Contains(name, StringComparison.Ordinal))
            {
                (found ??= new List<string>()).Add(name);
            }
        }

        if (found == null)
        {
            return result;
        }

        found.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        foreach (string name in found)
        {
            result = result.Replace(name, $"({shape(name)})", StringComparison.Ordinal);
        }

        return result;
    }
}
