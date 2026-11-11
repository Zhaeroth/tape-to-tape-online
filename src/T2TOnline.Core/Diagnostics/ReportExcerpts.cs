using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace T2TOnline.Core.Diagnostics;

/// <summary>A log file's text as a bug report keeps it: whole, or its start and end with a line saying what was left out.</summary>
/// <param name="Text">The text (UTF-8 decoded, line ends as in the file).</param>
/// <param name="SourceBytes">The file's length when it was read.</param>
/// <param name="LeftOutBytes">The bytes of the middle left out (0: the whole file).</param>
public sealed record ReportExcerpt(string Text, long SourceBytes, long LeftOutBytes);

/// <summary>
/// Reads a log for a bug report with a head-and-tail cap: a file that fits is read whole; a longer one keeps its first
/// quarter of the cap (the start-up lines: builds, settings) and its last three quarters (where a problem usually is),
/// each cut at a line end, with one line between saying how many bytes were left out. Only the length the file had when
/// it was opened is read, so a log still being written gives what it held then.
/// </summary>
public static class ReportExcerpts
{
    /// <summary>The share of the cap kept from a long file's start; the rest comes from its end.</summary>
    public const double HeadShare = 0.25;

    /// <summary>
    /// <paramref name="stream"/>'s text, at most about <paramref name="maxBytes"/> of it (see the class). Throws what the
    /// stream throws (the caller lists the file as left out).
    /// </summary>
    public static ReportExcerpt Read(System.IO.Stream stream, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxBytes < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The cap must be at least 2 bytes.");
        }

        long length = stream.CanSeek ? stream.Length : -1;
        if (length < 0)
        {
            // A stream without a length: read up to the cap and say nothing was left out only if it ended there.
            byte[] some = ReadUpTo(stream, maxBytes + 1);
            bool more = some.Length > maxBytes;
            byte[] kept = more ? CutAtLineEnd(some.AsSpan(0, (int)maxBytes)) : some;
            string read = Decode(kept);
            string end = !more ? string.Empty : (read.Length > 0 && read[^1] != '\n' ? "\n" : string.Empty) + LeftOutLine(0, unknown: true);
            return new ReportExcerpt(read + end, kept.Length, 0);
        }

        if (length <= maxBytes)
        {
            byte[] all = ReadUpTo(stream, length);
            return new ReportExcerpt(Decode(all), all.Length, 0);
        }

        long headBytes = Math.Max(1, (long)(maxBytes * HeadShare));
        long tailBytes = Math.Max(1, maxBytes - headBytes);
        byte[] head = CutAtLineEnd(ReadUpTo(stream, headBytes));
        stream.Seek(length - tailBytes, SeekOrigin.Begin);
        byte[] tail = AfterFirstLineEnd(ReadUpTo(stream, tailBytes));
        long leftOut = Math.Max(0, length - head.Length - tail.Length);
        string start = Decode(head);
        string text = start + (start.Length > 0 && start[^1] != '\n' ? "\n" : string.Empty) + LeftOutLine(leftOut, unknown: false) + Decode(tail, skipBom: false);
        return new ReportExcerpt(text, length, leftOut);
    }

    /// <summary>The line that stands for a long file's middle (ending with a line feed).</summary>
    public static string LeftOutLine(long bytes, bool unknown) => unknown
        ? "----- T2T Online bug report: the rest of this file was left out to keep the report small -----\n"
        : string.Create(CultureInfo.InvariantCulture,
            $"----- T2T Online bug report: {bytes:N0} bytes of the middle of this file were left out to keep the report small (its start and end are kept) -----\n");

    private static byte[] ReadUpTo(System.IO.Stream stream, long count)
    {
        var buffer = new byte[(int)Math.Min(count, int.MaxValue - 64)];
        int read = 0;
        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>The bytes up to and including their last line feed (all of them when there is none: one very long line).</summary>
    private static byte[] CutAtLineEnd(ReadOnlySpan<byte> bytes)
    {
        int last = bytes.LastIndexOf((byte)'\n');
        return last < 0 ? bytes.ToArray() : bytes[..(last + 1)].ToArray();
    }

    /// <summary>The bytes after their first line feed (all of them when there is none).</summary>
    private static byte[] AfterFirstLineEnd(ReadOnlySpan<byte> bytes)
    {
        int first = bytes.IndexOf((byte)'\n');
        return first < 0 ? bytes.ToArray() : bytes[(first + 1)..].ToArray();
    }

    private static string Decode(byte[] bytes, bool skipBom = true)
    {
        // UTF-8, a byte that isn't valid becoming U+FFFD; a line feed is never inside a multi-byte character, so the cuts
        // above never split one.
        ReadOnlySpan<byte> span = bytes;
        if (skipBom && span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }
}
