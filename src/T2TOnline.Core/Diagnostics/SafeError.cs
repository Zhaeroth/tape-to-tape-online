using System;
using System.Reflection;
using System.Text;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// An exception for a log line (D127): its type and message, and its inner exception's, with folders as labels, on one
/// line of at most <see cref="MaxLength"/> characters. A file error's message holds the file's full path, and a path
/// under the user's profile holds their Windows user name: every <c>ex.Message</c> that reaches a log goes through here.
/// </summary>
public static class SafeError
{
    /// <summary>The longest text returned, in characters.</summary>
    public const int MaxLength = 160;

    /// <summary>What a quoted path outside the known folders becomes, before its file name.</summary>
    public const string OtherFolderLabel = "<folder>";

    private static readonly char[] Slashes = { '\\', '/' };

    /// <summary>
    /// <paramref name="exception"/> as one safe line: <c>IOException: Could not find file '&lt;game folder&gt;\x.es3'.</c>
    /// <paramref name="scrub"/> labels the known folders (and any IDs and names in it); a quoted path it didn't know keeps
    /// only its file name. Null gives "". Never throws.
    /// </summary>
    public static string Text(Exception? exception, LogScrub scrub)
    {
        ArgumentNullException.ThrowIfNull(scrub);
        return Compose(exception, scrub);
    }

    /// <summary>
    /// <paramref name="exception"/> as one line for code with no scrubber of its own (Core's parts that log through the
    /// plugin's <c>ModLog</c>, whose backstop then labels the known folders and turns IDs and names into tags): type and
    /// message, every quoted path cut to <c>&lt;folder&gt;\&lt;file name&gt;</c>, at most <see cref="MaxLength"/>
    /// characters. Null gives "". Never throws.
    /// </summary>
    public static string Text(Exception? exception) => Compose(exception, null);

    private static string Compose(Exception? exception, LogScrub? scrub)
    {
        if (exception == null)
        {
            return string.Empty;
        }

        try
        {
            // A reflection or type initialiser wrapper says nothing itself: what it wraps does.
            Exception shown = exception is TargetInvocationException or TypeInitializationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            var text = new StringBuilder();
            text.Append(shown.GetType().Name).Append(": ").Append(shown.Message);
            if (shown.InnerException is { } inner)
            {
                text.Append(" (inner ").Append(inner.GetType().Name).Append(": ").Append(inner.Message).Append(')');
            }

            string line = OtherPaths(scrub?.Scrub(text.ToString()) ?? text.ToString());
            return SafeText.Clean(line, MaxLength);
        }
        catch (Exception)
        {
            return exception.GetType().Name; // the message may hold a full path: left out when it can't be checked
        }
    }

    /// <summary>A path in single or double quotes that still starts with a drive or a share: only its file name stays.</summary>
    private static string OtherPaths(string text)
    {
        StringBuilder? builder = null;
        int copied = 0;
        for (int i = 0; i + 3 < text.Length; i++)
        {
            char quote = text[i];
            if (quote is not ('\'' or '"') || !StartsPath(text, i + 1))
            {
                continue;
            }

            int end = text.IndexOf(quote, i + 1);
            if (end < 0)
            {
                end = text.Length;
            }

            int name = text.LastIndexOfAny(Slashes, end - 1, end - i - 1);
            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, i + 1 - copied).Append(OtherFolderLabel).Append('\\');
            builder.Append(text, name + 1, end - name - 1);
            copied = end;
            i = end;
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private static bool StartsPath(string text, int at) =>
        at + 2 < text.Length &&
        ((Ascii.IsLetter(text[at]) && text[at + 1] == ':' && text[at + 2] is '\\' or '/') ||
         (text[at] == '\\' && text[at + 1] == '\\'));
}
