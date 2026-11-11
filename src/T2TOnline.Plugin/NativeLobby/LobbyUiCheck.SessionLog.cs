#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.NativeLobby;

// What a step reads back from this launch's own session log (ModLog's file, which flushes every line): the lines a step exists to
// show, and the custom names check of "selfdrive team=custom". Read with sharing, so the log goes on being written; never the path in
// a line (ModLog.FileName is the file's name only).
internal static partial class LobbyUiCheck
{
    // The most of the log a step reads: the session log stops well before this (LogRetention.MaxFileBytes).
    private const long MostLogBytes = 64L * 1024 * 1024;

    /// <summary>The session log's length now (where a step's own lines will start), or 0 when there's no file.</summary>
    private static long SessionLogLength()
    {
        try
        {
            return ModLog.FilePath is { } path && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// This launch's session log from byte <paramref name="from"/> (0: the whole file; else a length <see cref="SessionLogLength"/> gave), as
    /// lines; null with <paramref name="problem"/> when it can't be read.
    /// </summary>
    private static List<string>? SessionLogLines(long from, out string problem)
    {
        problem = string.Empty;
        if (ModLog.FilePath is not { } path || !File.Exists(path))
        {
            problem = "this launch has no session log file";
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length - Math.Max(from, 0) > MostLogBytes)
            {
                problem = "the session log is larger than a step reads";
                return null;
            }

            // Every write is a whole line, flushed (ModLog), so a length read earlier is a line's start.
            stream.Seek(Math.Clamp(from, 0, stream.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            var lines = new List<string>();
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"the session log couldn't be read ({ModLog.SafeErrorText(ex)})";
            return null;
        }
    }

    /// <summary>How many lines of the session log from byte <paramref name="from"/> hold <paramref name="stem"/>; -1 when it can't be read.</summary>
    private static int SessionLogCount(long from, string stem)
    {
        List<string>? lines = SessionLogLines(from, out _);
        if (lines == null)
        {
            return -1;
        }

        int count = 0;
        foreach (string line in lines)
        {
            count += line.Contains(stem, StringComparison.Ordinal) ? 1 : 0;
        }

        return count;
    }
}
#endif
