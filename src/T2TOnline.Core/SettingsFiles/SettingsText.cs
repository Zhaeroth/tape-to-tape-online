using System;
using System.Collections.Generic;
using System.Text;

namespace T2TOnline.Core.SettingsFiles;

/// <summary>How the mod's settings are opened at start-up (D126).</summary>
public enum SettingsOpening
{
    /// <summary>The mod's own file holds the settings: read them from it.</summary>
    UseOwnFile,

    /// <summary>Neither file holds them: write the defaults to the mod's own file.</summary>
    CreateOwnFile,

    /// <summary>Only MelonLoader's shared file holds them (an update from 0.10.x or earlier): move them to the mod's own file.</summary>
    MoveFromSharedFile,
}

/// <summary>
/// The settings files' text, read without MelonLoader (D126): whether a TOML file has the mod's section, how the settings
/// are opened, and the one-line summary of the settings not at their defaults.
/// </summary>
public static class SettingsText
{
    /// <summary>The longest <see cref="Summary"/> (a log line's part).</summary>
    public const int SummaryMaxLength = 400;

    /// <summary>
    /// How the settings are opened: from the mod's own file when it has the section (it wins over an old copy in the shared
    /// file); else moved from the shared file when that has it; else created with the defaults. An own file without the
    /// section (empty, say, after a start that stopped between creating it and saving it) counts as none, so the shared
    /// file's values are never dropped for it.
    /// </summary>
    public static SettingsOpening Decide(bool ownFileHasSection, bool sharedFileHasSection) =>
        ownFileHasSection ? SettingsOpening.UseOwnFile
        : sharedFileHasSection ? SettingsOpening.MoveFromSharedFile
        : SettingsOpening.CreateOwnFile;

    /// <summary>
    /// True when <paramref name="toml"/> has a table header for <paramref name="section"/> (<c>[T2TOnline]</c>, with
    /// spaces, quotes or a comment after it), outside multi-line strings. Arrays of tables (<c>[[...]]</c>) and dotted
    /// names (<c>[T2TOnline.x]</c>) aren't it. Null or empty text has none. Never throws.
    /// </summary>
    public static bool HasSection(string? toml, string section)
    {
        if (string.IsNullOrEmpty(toml) || string.IsNullOrEmpty(section))
        {
            return false;
        }

        string? openString = null; // the delimiter of the multi-line string this line is inside, if any
        foreach (string raw in toml.Split('\n'))
        {
            string line = raw.Trim();
            if (openString != null)
            {
                if (Count(line, openString) % 2 == 1)
                {
                    openString = null;
                }

                continue;
            }

            if (line.StartsWith('[') && !line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (TableName(line) == section)
                {
                    return true;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            foreach (string delimiter in MultiLineDelimiters)
            {
                if (Count(line, delimiter) % 2 == 1)
                {
                    openString = delimiter;
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The settings not at their defaults as one line part: <c>CoopSlot = 3, DevTools = true</c>, in the order given, or
    /// <c>none</c>. Names and values only (the caller gives typed values, never text a player typed); cut with <c>...</c>
    /// at <see cref="SummaryMaxLength"/>.
    /// </summary>
    public static string Summary(IEnumerable<KeyValuePair<string, string>> notAtDefault)
    {
        ArgumentNullException.ThrowIfNull(notAtDefault);
        var text = new StringBuilder();
        foreach (KeyValuePair<string, string> setting in notAtDefault)
        {
            string part = $"{setting.Key} = {setting.Value}";
            int separator = text.Length > 0 ? 2 : 0;
            if (text.Length + separator + part.Length > SummaryMaxLength - 5)
            {
                text.Append(text.Length > 0 ? ", ..." : "...");
                return text.ToString();
            }

            if (separator > 0)
            {
                text.Append(", ");
            }

            text.Append(part);
        }

        return text.Length == 0 ? "none" : text.ToString();
    }

    private static readonly string[] MultiLineDelimiters = { "\"\"\"", "'''" };

    /// <summary>The name in a table header line (<c>[ "T2TOnline" ] # x</c> gives T2TOnline), or null when it isn't one.</summary>
    private static string? TableName(string line)
    {
        int i = 1;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        if (i >= line.Length)
        {
            return null;
        }

        string name;
        if (line[i] is '"' or '\'')
        {
            int close = line.IndexOf(line[i], i + 1);
            if (close < 0)
            {
                return null;
            }

            name = line[(i + 1)..close];
            i = close + 1;
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }

            return i < line.Length && line[i] == ']' ? name : null;
        }

        int end = line.IndexOf(']', i);
        return end < 0 ? null : line[i..end].TrimEnd(' ', '\t');
    }

    private static int Count(string line, string delimiter)
    {
        int count = 0;
        for (int at = line.IndexOf(delimiter, StringComparison.Ordinal); at >= 0; at = line.IndexOf(delimiter, at + delimiter.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
