using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The mod's own temporary files (F8): what a write leaves when the game stops in the middle of it. Every writer names its
/// temporary file <c>&lt;file&gt;</c> + <see cref="PartSuffix"/> and moves it into place once whole, so one start-up sweep
/// of the mod's folders removes them all. Never the game's own copies (<c>.tmp</c>, <c>.bac</c>, <c>.tmp.bak</c>).
/// </summary>
public static class ModTemporaries
{
    /// <summary>The suffix of a file the mod is still writing.</summary>
    public const string PartSuffix = ".t2t-part";

    /// <summary>The suffix of a backup folder still being written (moved to its name once whole).</summary>
    public const string PartialFolderSuffix = ".partial";

    /// <summary>
    /// The one temporary written under another suffix, by 0.10.x and earlier: the shared-progress ledger's
    /// (<c>progress\progress-ledger.txt.tmp</c>). Swept by its exact name.
    /// </summary>
    public const string LegacyLedgerTemporary = "progress-ledger.txt.tmp";

    /// <summary>True for a temporary file of the mod's, by its file name (no folder): <c>*.t2t-part</c> or <see cref="LegacyLedgerTemporary"/>.</summary>
    public static bool IsTemporary(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && !fileName.Contains('/', StringComparison.Ordinal) && !fileName.Contains('\\', StringComparison.Ordinal) &&
        ((fileName.Length > PartSuffix.Length && fileName.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)) ||
         string.Equals(fileName, LegacyLedgerTemporary, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name of the file a temporary was being written for (<c>coop-1.es3</c> for <c>coop-1.es3.t2t-part</c>,
    /// <c>progress-ledger.txt</c> for <see cref="LegacyLedgerTemporary"/>), or null when <paramref name="fileName"/> isn't one
    /// of the mod's temporaries (<see cref="IsTemporary"/>).
    /// </summary>
    public static string? FinishedName(string? fileName) =>
        !IsTemporary(fileName) ? null
        : fileName!.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^PartSuffix.Length]
        : fileName[..^".tmp".Length];

    /// <summary>
    /// True when the start-up sweep removes <paramref name="fileName"/>: one of the mod's temporaries whose finished file is
    /// there (<paramref name="finishedExists"/> is asked for its name), which every reader reads instead. A temporary whose
    /// finished file is missing stays: a first write that stopped before its move leaves the only copy there, and its reader
    /// recovers it (the shared-progress ledger reads it rather than start empty and apply every resent batch again); the
    /// next write of that file replaces it.
    /// </summary>
    public static bool IsStale(string? fileName, Func<string, bool> finishedExists)
    {
        ArgumentNullException.ThrowIfNull(finishedExists);
        string? finished = FinishedName(fileName);
        return finished != null && finishedExists(finished);
    }
}
