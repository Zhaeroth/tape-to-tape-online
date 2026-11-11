using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The first line of every session log (and of the mod's lines in MelonLoader's): which build wrote it, in which log
/// format, on which UTC date. A file without it (or with an older format) was written by a build that logged Steam names
/// and IDs: the first start of format 2 deletes those session logs, and a bug report leaves such files out.
/// </summary>
public static class LogHeader
{
    /// <summary>
    /// This build's log format. 2: players and lobbies as tags, folders as labels, no local times (D127). A change to what
    /// a log may hold gets the next number.
    /// </summary>
    public const int Format = 2;

    private const string Start = "T2T Online ";
    private const string FormatWords = ", log format ";

    /// <summary>
    /// The header: <c>T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-05.</c>
    /// </summary>
    public static string Line(string release, string commit, int protocol, DateTime utcNow) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Start}{release}, commit {(string.IsNullOrEmpty(commit) ? "unknown" : commit)}, protocol {protocol}{FormatWords}{Format}; UTC date {utcNow:yyyy-MM-dd}.");

    /// <summary>The line written when a session goes past midnight UTC.</summary>
    public static string DateLine(DateTime utcNow) =>
        string.Create(CultureInfo.InvariantCulture, $"UTC date {utcNow:yyyy-MM-dd} (the session went past midnight UTC).");

    /// <summary>
    /// The log format a session log's first line names, or 0 when it names none (a build before format 2). The line may
    /// carry the file's time and level first.
    /// </summary>
    public static int FormatOf(string? firstLine)
    {
        if (string.IsNullOrEmpty(firstLine))
        {
            return 0;
        }

        int start = firstLine.IndexOf(Start, StringComparison.Ordinal);
        int at = start < 0 ? -1 : firstLine.IndexOf(FormatWords, start, StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        at += FormatWords.Length;
        int end = at;
        while (end < firstLine.Length && end - at < 6 && Ascii.IsDigit(firstLine[end]))
        {
            end++;
        }

        return int.TryParse(firstLine.AsSpan(at, end - at), NumberStyles.None, CultureInfo.InvariantCulture, out int format) ? format : 0;
    }
}
