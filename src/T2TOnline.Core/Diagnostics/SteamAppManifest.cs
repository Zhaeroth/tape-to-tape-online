using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Reads the two build numbers of Steam's app manifest (<c>steamapps\appmanifest_&lt;app id&gt;.acf</c>, Valve's KeyValues text): the
/// build installed (<c>buildid</c>) and the build Steam is to install (<c>TargetBuildID</c>; the same when no update waits). Only those two
/// keys of the top block are read; the rest of the file (which holds the owner's SteamID64, among others) is skipped unread and nothing of
/// it is kept. Never throws.
/// </summary>
public static class SteamAppManifest
{
    /// <summary>Tape to Tape's Steam app id: its manifest is <c>appmanifest_1566200.acf</c>.</summary>
    public const int GameAppId = 1566200;

    /// <summary>The longest manifest read (a real one is about 1 KB).</summary>
    public const int MaxChars = 256 * 1024;

    /// <summary>The manifest's file name for <see cref="GameAppId"/>.</summary>
    public static string FileName { get; } = string.Create(CultureInfo.InvariantCulture, $"appmanifest_{GameAppId}.acf");

    /// <summary>
    /// The installed build and the build Steam is to install (0 when the file doesn't say). False, with both 0, when the text isn't a
    /// manifest with a positive <c>buildid</c> in its top block, or is over <see cref="MaxChars"/>.
    /// </summary>
    public static bool TryRead(string? text, out long installed, out long target)
    {
        installed = 0;
        target = 0;
        if (string.IsNullOrEmpty(text) || text.Length > MaxChars)
        {
            return false;
        }

        int depth = 0;
        string? key = null;
        int at = 0;
        while (Next(text, ref at, out string? token, out char brace))
        {
            if (token == null)
            {
                if (brace == '{')
                {
                    depth++;
                }
                else if (--depth < 0)
                {
                    return false;
                }

                key = null;
                continue;
            }

            if (key == null)
            {
                key = token;
                continue;
            }

            if (depth == 1)
            {
                if (string.Equals(key, "buildid", StringComparison.OrdinalIgnoreCase))
                {
                    _ = TryNumber(token, out installed);
                }
                else if (string.Equals(key, "TargetBuildID", StringComparison.OrdinalIgnoreCase))
                {
                    _ = TryNumber(token, out target);
                }
            }

            key = null;
        }

        if (installed <= 0)
        {
            installed = 0;
            target = 0;
            return false;
        }

        return true;
    }

    private static bool TryNumber(string token, out long value)
    {
        if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>The next quoted string (<paramref name="token"/>) or brace (<paramref name="brace"/>, token null); false at the end or on text that isn't KeyValues.</summary>
    private static bool Next(string text, ref int at, out string? token, out char brace)
    {
        token = null;
        brace = '\0';
        while (at < text.Length)
        {
            char c = text[at];
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            if (c is '{' or '}')
            {
                brace = c;
                at++;
                return true;
            }

            if (c != '"')
            {
                return false;
            }

            int start = ++at;
            while (at < text.Length && text[at] != '"')
            {
                at += text[at] == '\\' ? 2 : 1;
            }

            if (at >= text.Length)
            {
                return false;
            }

            token = text[start..at];
            at++;
            return true;
        }

        return false;
    }
}
