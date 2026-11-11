using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What a log may say about what a host's game sends (D127): a modified host chooses every byte of it, so it's
/// player-written text to this PC's log. One rule for all of it: an identifier (a key, an id, a menu's or an element's
/// name, a map point, a term key) is written only when it has an identifier's shape (<see cref="KeyForLog(string?)"/>), a
/// text a player reads only as its shape (<see cref="TextShape"/>) or by the key it was made from; a host's field names
/// never, only how many there are and where they first differ from this build's own.
/// </summary>
public static class HostText
{
    /// <summary>The longest key written as it is (a term key of the game's is at most 84 characters, a relic id 96).</summary>
    public const int MaxKeyLength = 96;

    /// <summary>The longest text value of a host's view written as it is (a map path of 40 points).</summary>
    public const int MaxValueLength = 256;

    /// <summary>The longest list of this build's own names written (the rest is cut).</summary>
    public const int MaxOwnNamesLength = 200;

    // Unity's own endings of an object's name: a copy made at run time, and a duplicate's number.
    private const string CloneEnding = "(Clone)";

    /// <summary>
    /// A host's identifier for a log line: the identifier itself when it's shaped like one (at most
    /// <see cref="MaxKeyLength"/> characters of ASCII letters, digits, dots, slashes, underscores, hyphens, commas,
    /// semicolons and plus signs, optionally ending in Unity's <c>(Clone)</c> or <c> (1)</c>, as every key, id, menu,
    /// element, point and term key an honest host sends is), else <c>a key of N characters</c>.
    /// </summary>
    public static string KeyForLog(string? key) => KeyForLog(key, MaxKeyLength);

    /// <summary>
    /// <see cref="KeyForLog(string?)"/> with another length limit (<paramref name="maxLength"/>): a view's text value, which
    /// can be a map path, is longer than a key.
    /// </summary>
    public static string KeyForLog(string? key, int maxLength)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "an empty key";
        }

        return key.Length <= maxLength && IsKey(key) ? key : string.Create(CultureInfo.InvariantCulture, $"a key of {key.Length} characters");
    }

    /// <summary>
    /// True when <paramref name="key"/> has an identifier's shape (see <see cref="KeyForLog(string?)"/>; no length limit):
    /// one or more of the identifier's characters, then any of Unity's name endings.
    /// </summary>
    public static bool IsKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        int end = key.Length;
        while (true)
        {
            if (end > CloneEnding.Length && string.CompareOrdinal(key, end - CloneEnding.Length, CloneEnding, 0, CloneEnding.Length) == 0)
            {
                end -= CloneEnding.Length;
                continue;
            }

            // " (12)": a space, a parenthesis, digits, a parenthesis.
            int digits = end - 2;
            while (digits >= 0 && key[digits] is >= '0' and <= '9')
            {
                digits--;
            }

            if (key[end - 1] == ')' && digits < end - 2 && digits >= 2 && key[digits] == '(' && key[digits - 1] == ' ')
            {
                end = digits - 1;
                continue;
            }

            break;
        }

        if (end == 0)
        {
            return false;
        }

        for (int i = 0; i < end; i++)
        {
            if (!(key[i] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '/' or '_' or '-' or ',' or ';' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A text a host sent that a player reads (a button's words, a name), for a log line: only its length,
    /// <c>11 characters</c> (characters as a reader counts them), or <c>empty</c>. Where a line has the per-launch hash of
    /// player-written text, it passes that instead (<c>ShownTextLog</c>'s <c>shape</c>).
    /// </summary>
    public static string TextShape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "empty";
        }

        int characters;
        try
        {
            characters = new StringInfo(text).LengthInTextElements;
        }
        catch (ArgumentException)
        {
            characters = text.Length;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{characters} character{(characters == 1 ? string.Empty : "s")}");
    }

    /// <summary>
    /// How a host's field names for an event differ from this build's, for a log line, without the host's text: how many
    /// each side has, this build's own names, and the first field (from 1) that differs.
    /// </summary>
    /// <param name="host">The host's field names (network input).</param>
    /// <param name="own">This build's field names for the same event.</param>
    public static string FieldsDifference(IReadOnlyList<string> host, IReadOnlyList<string> own)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(own);
        int first = 0;
        while (first < host.Count && first < own.Count && string.Equals(host[first], own[first], StringComparison.Ordinal))
        {
            first++;
        }

        string names = own.Count == 0 ? "none" : string.Join(", ", own);
        if (names.Length > MaxOwnNamesLength)
        {
            names = names[..MaxOwnNamesLength] + "...";
        }

        string where = first >= host.Count && first >= own.Count ? "no field differs" : $"the first that differs is field {first + 1}";
        return $"the host's {host.Count} against this build's {own.Count} ({names}); {where}";
    }
}
