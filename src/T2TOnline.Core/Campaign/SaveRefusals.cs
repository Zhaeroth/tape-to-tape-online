using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>What a save write the save guard refused on a friend's PC means for client mode's leak audit (<see cref="SaveRefusals"/>).</summary>
public enum RefusalKind
{
    /// <summary>
    /// The game's own settings, profile or stats, written by presentation a friend's PC runs too (the match's load writes
    /// its settings; a reward menu shown as a view writes the profile): the guard doing its job, not a leak.
    /// </summary>
    Expected,

    /// <summary>A run file: run code ran in client mode. A leak.</summary>
    Run,

    /// <summary>Any other file, or a write naming none: not known to be harmless. A leak.</summary>
    Other,
}

/// <summary>
/// Classifies the save writes the save guard refused while client mode held. Counted alike, every refused write is
/// a WARN "LEAK" in the audit after each campaign match, though most are the guard working:
/// <c>config.es3</c> as each match loads, and <c>profileV2.es3</c> with Easy Save's backup and Steam's save right after a
/// reward menu opens on the friend. Refused writes of the files in <see cref="ExpectedFiles"/> (and Easy Save's copies of
/// them) are reported as the guard's job; a run file's (<see cref="RunFiles"/>, the mod's own <c>HostRun.es3</c>, a co-op
/// slot) or anything else stay a leak.
/// </summary>
public static class SaveRefusals
{
    /// <summary>The game's settings, profile and stats files, whose refused writes are expected while following.</summary>
    public static IReadOnlyList<string> ExpectedFiles { get; } = new[] { "config.es3", "profileV2.es3", "statsV2.es3" };

    /// <summary>Run files: a refused write of one means run code ran in client mode.</summary>
    public static IReadOnlyList<string> RunFiles { get; } = new[] { "RunDataV2.es3", "HostRun.es3" };

    /// <summary>How a refused write naming <paramref name="fileName"/> (a name, no folders; null or empty for none) is reported.</summary>
    public static RefusalKind Classify(string? fileName)
    {
        string file = EasySaveCopies.Original(fileName);
        if (file.Length == 0)
        {
            return RefusalKind.Other;
        }

        if (RunFiles.Contains(file, StringComparer.OrdinalIgnoreCase) ||
            (file.StartsWith("coop-", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".es3", StringComparison.OrdinalIgnoreCase)))
        {
            return RefusalKind.Run;
        }

        return ExpectedFiles.Contains(file, StringComparer.OrdinalIgnoreCase) ? RefusalKind.Expected : RefusalKind.Other;
    }

    /// <summary>
    /// The kind of a write that named <paramref name="fileNames"/> (one write may name several, Easy Save's backup and its
    /// original): the worst of them, <see cref="RefusalKind.Run"/> over <see cref="RefusalKind.Other"/> over
    /// <see cref="RefusalKind.Expected"/>; a write naming no file is <see cref="RefusalKind.Other"/>.
    /// </summary>
    public static RefusalKind Classify(IEnumerable<string?> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        RefusalKind worst = RefusalKind.Expected;
        bool any = false;
        foreach (string? name in fileNames)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            any = true;
            RefusalKind kind = Classify(name);
            worst = kind == RefusalKind.Run || worst == RefusalKind.Run ? RefusalKind.Run
                : kind == RefusalKind.Other || worst == RefusalKind.Other ? RefusalKind.Other
                : RefusalKind.Expected;
        }

        return any ? worst : RefusalKind.Other;
    }
}
