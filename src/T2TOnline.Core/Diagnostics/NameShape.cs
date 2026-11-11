using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What a log may say about a Steam name instead of the name (D127): its length, the scripts it's written in, how many
/// characters were taken out as unsafe and how many the game's fonts can't draw. Enough to follow a bug where a name
/// shows wrong, without the name.
/// </summary>
public static class NameShape
{
    /// <summary>
    /// The line for <paramref name="tag"/>'s name: <c>P#a0c1d2's Steam name: 15 characters, Latin and Cyrillic, 2 the game's
    /// fonts can't draw.</c> <paramref name="canDraw"/> says whether the fonts draw a code point (null: not checked). Never
    /// throws, and the callback may.
    /// </summary>
    public static string Describe(string tag, string? name, Func<int, bool>? canDraw = null)
    {
        if (string.IsNullOrEmpty(name) || name == Lobby.DisplayName.SteamUnknown)
        {
            return $"{tag}'s Steam name: not known on this PC yet.";
        }

        int characters = 0;
        int unsafeCount = 0;
        int cantDraw = 0;
        var scripts = new List<string>();
        for (int i = 0; i < name.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                codePoint = char.ConvertToUtf32(name[i], name[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(name[i]) || SafeText.IsUnsafe(name[i]))
            {
                characters++;
                unsafeCount++;
                continue;
            }
            else
            {
                codePoint = name[i];
            }

            characters++;
            string script = ScriptOf(codePoint);
            if (!scripts.Contains(script))
            {
                scripts.Add(script);
            }

            if (canDraw != null && script is not ("spaces" or "marks") && !Draws(canDraw, codePoint))
            {
                cantDraw++;
            }
        }

        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{tag}'s Steam name: {characters} character{(characters == 1 ? string.Empty : "s")}");
        scripts.Remove("spaces");
        if (scripts.Count > 0)
        {
            line.Append(", ").Append(Join(scripts));
        }

        if (unsafeCount > 0)
        {
            line.Append(CultureInfo.InvariantCulture, $", {unsafeCount} control or direction character{(unsafeCount == 1 ? string.Empty : "s")} (never shown)");
        }

        if (canDraw != null)
        {
            line.Append(cantDraw == 0
                ? ", all drawn by the game's fonts"
                : string.Create(CultureInfo.InvariantCulture, $", {cantDraw} the game's fonts can't draw"));
        }

        return line.Append('.').ToString();
    }

    /// <summary>The script, or kind of character, a code point belongs to, in words.</summary>
    public static string ScriptOf(int codePoint)
    {
        switch (codePoint)
        {
            case >= '0' and <= '9':
                return "digits";
            case >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0xC0 and <= 0x24F and not (0xD7 or 0xF7) or >= 0x1E00 and <= 0x1EFF
                or >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A:
                return "Latin";
            case >= 0x370 and <= 0x3FF or >= 0x1F00 and <= 0x1FFF:
                return "Greek";
            case >= 0x400 and <= 0x52F:
                return "Cyrillic";
            case >= 0x530 and <= 0x58F:
                return "Armenian";
            case >= 0x590 and <= 0x5FF:
                return "Hebrew";
            case >= 0x600 and <= 0x6FF or >= 0x750 and <= 0x77F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF:
                return "Arabic";
            case >= 0x900 and <= 0x97F:
                return "Devanagari";
            case >= 0xE00 and <= 0xE7F:
                return "Thai";
            case >= 0x10A0 and <= 0x10FF:
                return "Georgian";
            case >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF:
                return "Hangul";
            case >= 0x3040 and <= 0x309F:
                return "Hiragana";
            case >= 0x30A0 and <= 0x30FF or >= 0xFF66 and <= 0xFF9F:
                return "Katakana";
            case >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF:
                return "Han";
            case >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x2300 and <= 0x23FF:
                return "emoji";
        }

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return category switch
        {
            UnicodeCategory.SpaceSeparator => "spaces",
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format => "marks",
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => "other letters",
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => "other digits",
            _ => "symbols",
        };
    }

    private static bool Draws(Func<int, bool> canDraw, int codePoint)
    {
        try
        {
            return canDraw(codePoint);
        }
        catch (Exception)
        {
            return true; // unknown: not counted
        }
    }

    private static string Join(List<string> words) =>
        words.Count == 1 ? words[0] : string.Join(", ", words.GetRange(0, words.Count - 1)) + " and " + words[^1];
}
