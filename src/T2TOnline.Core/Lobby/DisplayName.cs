using System;
using System.Globalization;
using System.Text;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// A Steam name made ready to draw on a label. A name is untrusted input: tags, typed escapes, zero-width and combining
/// characters ("Zalgo"), bidi overrides, characters no font has. The label itself has rich text and control-character
/// parsing off (the plugin sets them); this does the rest, and a name is never blank: with nothing left it's
/// "Player {n}" in the player's language.
/// </summary>
public static class DisplayName
{
    /// <summary>Longest name kept, in characters (Steam's own cap on a name is 32).</summary>
    public const int MaxChars = 32;

    /// <summary>What Steam gives for someone it knows nothing about: as good as blank.</summary>
    public const string SteamUnknown = "[unknown]";

    /// <summary>
    /// <paramref name="raw"/> ready to draw: normalized (NFC), with control, bidi and private-use characters, format
    /// characters (Cf), combining marks (Mn, Me), typed backslashes and whatever <paramref name="canDraw"/> refuses gone, spaces
    /// tidied, cut with an ellipsis. Blank, or nothing left: <see cref="LobbyWords.PlayerN"/> with <paramref name="playerNumber"/>.
    /// Never throws, and the callback may.
    /// </summary>
    /// <param name="raw">The name from Steam or the board.</param>
    /// <param name="playerNumber">The number in the fallback.</param>
    /// <param name="language">The fallback's language: an index into <see cref="LobbyWords.Languages"/>.</param>
    /// <param name="canDraw">Whether a font can draw a code point (the plugin's <c>TMP_FontAsset.HasCharacters</c>); null keeps everything else.</param>
    /// <param name="maxChars">The cut, at least 2.</param>
    public static string Clean(string? raw, int playerNumber, int language = 0, Func<int, bool>? canDraw = null, int maxChars = MaxChars)
    {
        string kept = Strip(raw, canDraw);
        if (kept.Length == 0)
        {
            return LobbyWords.PlayerN.Text(language, playerNumber.ToString(CultureInfo.InvariantCulture));
        }

        return SafeText.Clean(kept, Math.Max(maxChars, 2));
    }

    /// <summary>
    /// A name for a text with rich text on (a toast, the game's prompt): wrapped in <c>noparse</c>, with anything that would end it taken out
    /// first. That includes a typed escape: TextMesh Pro turns a backslash, "u" and "003C" into a real "&lt;" before it reads tags, whatever its settings
    /// (its <c>PopulateTextProcessingArray</c>), so a name that kept its backslashes could close the <c>noparse</c> and draw markup. A backslash
    /// before anything else stays (a file's path). Never needed on a name label, whose rich text is off.
    /// </summary>
    public static string ForRichText(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // Each pass can make what the other looks for ("\</noparseu003C"): both again until neither finds anything.
        string safe = name;
        string before;
        do
        {
            before = safe;
            safe = WithoutEscapes(safe);
            int found;
            while ((found = safe.IndexOf("</noparse", StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                safe = safe.Remove(found, "</noparse".Length);
            }
        }
        while (!string.Equals(before, safe, StringComparison.Ordinal));

        return $"<noparse>{safe}</noparse>";
    }

    /// <summary>
    /// <paramref name="text"/> without the backslashes TextMesh Pro would read as an escape: a run of them before <c>u</c> and four hex digits,
    /// <c>U</c> and eight, or <c>n</c>, <c>r</c>, <c>t</c>, <c>v</c> (a line break, with its control-character parsing on). Any other backslash stays.
    /// </summary>
    private static string WithoutEscapes(string text)
    {
        if (text.IndexOf('\\', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var kept = new StringBuilder(text.Length);
        int at = 0;
        while (at < text.Length)
        {
            if (text[at] != '\\')
            {
                kept.Append(text[at++]);
                continue;
            }

            int end = at;
            while (end < text.Length && text[end] == '\\')
            {
                end++;
            }

            if (!StartsEscape(text, end))
            {
                kept.Append(text, at, end - at);
            }

            at = end;
        }

        return kept.ToString();
    }

    private static bool StartsEscape(string text, int at) => at < text.Length && text[at] switch
    {
        'n' or 'r' or 't' or 'v' => true,
        'u' => HexFollows(text, at + 1, 4),
        'U' => HexFollows(text, at + 1, 8),
        _ => false,
    };

    private static bool HexFollows(string text, int from, int count)
    {
        if (from + count > text.Length)
        {
            return false;
        }

        for (int i = from; i < from + count; i++)
        {
            if (text[i] is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static string Strip(string? raw, Func<int, bool>? canDraw)
    {
        if (string.IsNullOrEmpty(raw) || raw == SteamUnknown)
        {
            return string.Empty;
        }

        var kept = new StringBuilder(raw.Length);
        bool space = true; // no leading space
        foreach (Rune rune in Normalized(raw).EnumerateRunes())
        {
            if (!Keeps(rune, canDraw))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!space)
                {
                    kept.Append(' ');
                    space = true;
                }

                continue;
            }

            kept.Append(rune.ToString());
            space = false;
        }

        if (kept.Length > 0 && kept[^1] == ' ')
        {
            kept.Length--;
        }

        return kept.ToString();
    }

    private static bool Keeps(Rune rune, Func<int, bool>? canDraw)
    {
        if (rune.Value == Rune.ReplacementChar.Value || rune.Value == '\\')
        {
            return false; // a lone surrogate, or the start of a typed escape
        }

        if (rune.IsBmp && SafeText.IsUnsafe((char)rune.Value) && !Rune.IsWhiteSpace(rune))
        {
            return false;
        }

        if (rune.IsBmp && SafeText.IsUnsafe((char)rune.Value))
        {
            return true; // a tab or line break: tidied to one space by the caller
        }

        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.Format:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.OtherNotAssigned:
                return false;
        }

        if (canDraw == null || Rune.IsWhiteSpace(rune))
        {
            return true;
        }

        try
        {
            return canDraw(rune.Value);
        }
        catch (Exception)
        {
            return true; // the callback failed: drawing a box is better than losing a name
        }
    }

    private static string Normalized(string raw)
    {
        try
        {
            return raw.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return raw; // a lone surrogate: Normalize refuses it, and the runes read it as a replacement
        }
    }
}
