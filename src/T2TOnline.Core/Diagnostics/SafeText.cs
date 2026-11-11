using System;
using System.Text;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Text from someone else (a peer's mod version or reason, a Steam name, a recording) made safe for one log line or the
/// status box: line breaks and other control characters (C0, C1, Unicode line and paragraph separators), bidirectional
/// overrides, private-use characters (the status box's own markup) and lone surrogates become '?', and it's cut to a
/// length. A peer can't forge log lines (which get pasted into diagnosis) or restyle the overlay. Emoji and other
/// properly paired characters stay.
/// </summary>
public static class SafeText
{
    /// <summary>The text, cleaned and cut to <paramref name="maxLength"/> characters (an ellipsis marks a cut). Null gives "".</summary>
    public static string Clean(string? text, int maxLength = 64)
    {
        if (maxLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "The length must be at least 2.");
        }

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        bool cut = text.Length > maxLength;
        if (!cut && IsClean(text))
        {
            return text;
        }

        int limit = cut ? maxLength - 1 : text.Length;
        var builder = new StringBuilder(limit + 1);
        for (int i = 0; i < limit; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (i + 1 >= limit)
                {
                    break; // a pair the cut would split
                }

                builder.Append(c).Append(text[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(c) || IsUnsafe(c) ? '?' : c);
            }
        }

        if (cut)
        {
            builder.Append('\u2026');
        }

        return builder.ToString();
    }

    /// <summary>True for a character a peer must not get into a log line or the status box (surrogates aside).</summary>
    public static bool IsUnsafe(char c) =>
        char.IsControl(c) ||
        c is '\u2028' or '\u2029' or '\u061C' or '\u200E' or '\u200F' ||
        c is >= '\u202A' and <= '\u202E' ||
        c is >= '\u2066' and <= '\u2069' ||
        c is >= '\uE000' and <= '\uF8FF';

    private static bool IsClean(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c) || IsUnsafe(c))
            {
                return false;
            }
        }

        return true;
    }
}
