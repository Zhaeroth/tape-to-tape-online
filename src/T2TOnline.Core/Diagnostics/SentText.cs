using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What another PC sent that a log line names: a lobby's keys (read before its host is known to be a friend, so a stranger's
/// lobby reached by a link sets them), a hello's release, and a friend's reasons for the host's log. Written as it is only when it has the shape of what it should be,
/// else by its length, so nobody's own words reach a log through them (P8). A shape still lets a version's letters through,
/// never a space, markup or more than <see cref="MaxRelease"/> characters.
/// </summary>
public static class SentText
{
    /// <summary>The longest release written as it is.</summary>
    public const int MaxRelease = 32;

    /// <summary>The longest protocol number written as it is.</summary>
    public const int MaxNumber = 9;

    /// <summary>
    /// A release ("0.12.0-beta"): written when it starts with a digit and holds only ASCII letters, digits, '.', '-' and '+',
    /// at most <see cref="MaxRelease"/> characters; "(none)" when empty; else its length.
    /// </summary>
    public static string Release(string? sent) => Shaped(sent, MaxRelease, static (c, i) => i == 0 ? IsDigit(c) : IsDigit(c) || IsLetter(c) || c is '.' or '-' or '+');

    /// <summary>A protocol number: written when it's ASCII digits only, at most <see cref="MaxNumber"/>; "(none)" when empty; else its length.</summary>
    public static string Number(string? sent) => Shaped(sent, MaxNumber, static (c, _) => IsDigit(c));

    /// <summary>A game build: written when it's exactly <paramref name="digits"/> lower-case hex digits; "(none)" when empty; else its length.</summary>
    public static string Hex(string? sent, int digits) =>
        sent is { Length: > 0 } && sent.Length != digits ? Length(sent) : Shaped(sent, digits, static (c, _) => IsDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>
    /// A reason another PC sent (a friend's follow or match load failure): written as it is only when it's one of
    /// <paramref name="known"/>, this build's own fixed reasons; "(none)" when empty; else by its length, <c>a reason of 54
    /// character(s)</c> (the sender's own log has it in full).
    /// </summary>
    public static string Reason(string? sent, IReadOnlyCollection<string> known) => Reason(sent, known, Array.Empty<string>());

    /// <summary>
    /// <see cref="Reason(string?, IReadOnlyCollection{string})"/> for reasons whose first words are fixed and whose rest
    /// varies (a number, an error's text, what the other PC's game said): one that begins with one of
    /// <paramref name="leadIns"/> (this build's own opening words; the longest that fits) is written as those words and the
    /// length of the rest, <c>couldn't load the world map: [34 more character(s)]</c>, so the log still says what kind of
    /// failure it was and nothing of what the other PC put after it.
    /// </summary>
    public static string Reason(string? sent, IReadOnlyCollection<string> known, IReadOnlyList<string> leadIns)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(leadIns);
        if (string.IsNullOrEmpty(sent))
        {
            return "(none)";
        }

        if (known.Contains(sent))
        {
            return sent;
        }

        string? leadIn = null;
        for (int i = 0; i < leadIns.Count; i++)
        {
            string candidate = leadIns[i];
            if (!string.IsNullOrEmpty(candidate) && candidate.Length > (leadIn?.Length ?? 0) && sent.StartsWith(candidate, StringComparison.Ordinal))
            {
                leadIn = candidate;
            }
        }

        return leadIn == null
            ? string.Create(CultureInfo.InvariantCulture, $"a reason of {sent.Length} character(s)")
            : string.Create(CultureInfo.InvariantCulture, $"{leadIn}[{sent.Length - leadIn.Length} more character(s)]");
    }

    private static string Shaped(string? sent, int max, Func<char, int, bool> allowed)
    {
        if (string.IsNullOrEmpty(sent))
        {
            return "(none)";
        }

        if (sent.Length > max)
        {
            return Length(sent);
        }

        for (int i = 0; i < sent.Length; i++)
        {
            if (!allowed(sent[i], i))
            {
                return Length(sent);
            }
        }

        return sent;
    }

    private static string Length(string sent) => string.Create(CultureInfo.InvariantCulture, $"a value of {sent.Length} character(s)");

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
