using System;
using System.IO;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The only files a bug report may read (D127): the mod's session logs (<c>session-*.log</c> directly in the logs folder),
/// MelonLoader's launch logs (<c>*.log</c> directly in <c>MelonLoader\Logs</c>) and Unity's <c>Player.log</c> and
/// <c>Player-prev.log</c> (directly in Unity's log folder). Anything else is refused, and some things always: any path
/// through a <c>progress</c> folder (the shared-progress ledger, <c>shared-play.txt</c>, and <c>host-key.bin</c>, the secret
/// that with the ledger would let anyone match its keys to Steam IDs) or the mod's data folder <c>T2T Online</c> (co-op slots,
/// backups), any file named <c>host-key.bin</c>, and any file directly in one of the mod's other folders (<c>dev</c>,
/// <c>campaign</c>, <c>reports</c>, ...). Paths compare without case, either slash, after
/// <see cref="Path.GetFullPath(string)"/> (so <c>..</c> can't climb out of a folder).
/// </summary>
public sealed class ReportAllowList
{
    /// <summary>The per-PC secret of the progress ledger: never in a report, wherever it is.</summary>
    public const string HostKeyFile = "host-key.bin";

    // Folders never read through, at any depth: the progress ledger's, and the mod's data folder (co-op slots, backups,
    // progress, history logos) as a whole.
    private static readonly string[] RefusedAnywhere = { "progress", "T2T Online" };

    // The mod's other folders, refused as a file's own folder (a name such as "dev" may well be higher up a game's path).
    private static readonly string[] RefusedParents = { "dev", "coop", "backups", "history-logos", "campaign", "custom", "reports", "streams", "captures", "ui-check" };

    private readonly string _sessionLogs;
    private readonly string _melonLogs;
    private readonly string _unityLogs;

    /// <summary>The allow-list for this PC's three log folders (an empty or null folder allows nothing there).</summary>
    public ReportAllowList(string? sessionLogsFolder, string? melonLogsFolder, string? unityLogFolder)
    {
        _sessionLogs = Normal(sessionLogsFolder);
        _melonLogs = Normal(melonLogsFolder);
        _unityLogs = Normal(unityLogFolder);
    }

    /// <summary>
    /// What <paramref name="path"/> is to a bug report, or null when it may not be read. Never throws.
    /// </summary>
    public ReportFileKind? Classify(string? path)
    {
        string full = Normal(path);
        if (full.Length == 0)
        {
            return null;
        }

        string name = Path.GetFileName(full);
        string folder = Path.GetDirectoryName(full) ?? string.Empty;
        if (name.Equals(HostKeyFile, StringComparison.OrdinalIgnoreCase) || ThroughRefusedFolder(folder))
        {
            return null;
        }

        if (Same(folder, _sessionLogs) && name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return ReportFileKind.SessionLog;
        }

        if (Same(folder, _melonLogs) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return ReportFileKind.MelonLog;
        }

        if (Same(folder, _unityLogs) && (name.Equals("Player.log", StringComparison.OrdinalIgnoreCase) || name.Equals("Player-prev.log", StringComparison.OrdinalIgnoreCase)))
        {
            return ReportFileKind.UnityLog;
        }

        return null;
    }

    /// <summary>
    /// A source for <paramref name="path"/> of the <paramref name="kind"/> asked for, or null when the allow-list refuses it
    /// (or it's another kind). The only way the plugin makes a report's sources.
    /// </summary>
    public ReportSource? Source(ReportFileKind kind, string path, Func<string, System.IO.Stream> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (Classify(path) != kind)
        {
            return null;
        }

        string full = Normal(path);
        return new ReportSource(kind, Path.GetFileName(full), () => open(full));
    }

    private static bool ThroughRefusedFolder(string folder)
    {
        string[] parts = folder.Split('\\');
        foreach (string part in parts)
        {
            foreach (string refused in RefusedAnywhere)
            {
                if (part.Equals(refused, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        string parent = parts[^1];
        foreach (string refused in RefusedParents)
        {
            if (parent.Equals(refused, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(string a, string b) => b.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string Normal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Replace('/', '\\')).Replace('/', '\\').TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }
}
