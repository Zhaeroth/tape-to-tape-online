using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The garbage collectors' counts and heap sizes at one moment, on both runtimes the mod lives in: .NET's (the mod's own
/// managed code, MelonLoader's runtime) and IL2CPP's (the game's, Boehm GC with incremental slices). Read for the cost
/// reports and a 10-minute memory line: a host's measured sections can stall every 30-60 s for about 3 ms or 6-7 ms,
/// which matches one and two slices of an incremental GC, and without these counts nothing in the log confirms it or
/// shows a heap growing over a long session.
/// </summary>
/// <param name="Gen0">.NET generation 0 collections so far.</param>
/// <param name="Gen1">.NET generation 1 collections so far.</param>
/// <param name="Gen2">.NET generation 2 collections so far.</param>
/// <param name="ManagedBytes">.NET's heap, in bytes.</param>
/// <param name="Il2CppCollections">IL2CPP's collections so far (-1 when it couldn't be read).</param>
/// <param name="Il2CppBytes">IL2CPP's heap, in bytes (-1 when it couldn't be read).</param>
public readonly record struct MemorySample(int Gen0, int Gen1, int Gen2, long ManagedBytes, int Il2CppCollections, long Il2CppBytes)
{
    /// <summary>
    /// "GC .NET 12/3/1 (+2), IL2CPP 340 (+6); heaps .NET 48.2 MB, IL2CPP 512.0 MB": collections by generation, how many
    /// more than in <paramref name="before"/> (none when it's null), and the heaps now.
    /// </summary>
    public string Describe(MemorySample? before)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        string managedMore = before is MemorySample b ? string.Create(culture, $" (+{Math.Max(0, Gen0 - b.Gen0)})") : string.Empty;
        string il2cpp = Il2CppCollections < 0 ? "IL2CPP unreadable"
            : string.Create(culture, $"IL2CPP {Il2CppCollections}") +
              (before is MemorySample p && p.Il2CppCollections >= 0 ? string.Create(culture, $" (+{Math.Max(0, Il2CppCollections - p.Il2CppCollections)})") : string.Empty);
        string heaps = string.Create(culture, $"heaps .NET {Megabytes(ManagedBytes):0.0} MB, ") +
            (Il2CppBytes < 0 ? "IL2CPP unreadable" : string.Create(culture, $"IL2CPP {Megabytes(Il2CppBytes):0.0} MB"));
        return string.Create(culture, $"GC .NET {Gen0}/{Gen1}/{Gen2}{managedMore}, {il2cpp}; {heaps}");
    }

    /// <summary>
    /// "IL2CPP heap 512.0 MB (+12.5 since the first sample), .NET 48.2 MB (+0.3)": the growth line's figures against the
    /// session's first sample.
    /// </summary>
    public string DescribeGrowth(MemorySample first)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        string il2cpp = Il2CppBytes < 0 || first.Il2CppBytes < 0 ? "IL2CPP heap unreadable"
            : string.Create(culture, $"IL2CPP heap {Megabytes(Il2CppBytes):0.0} MB ({Signed(Megabytes(Il2CppBytes - first.Il2CppBytes))} since the first sample)");
        return il2cpp + string.Create(culture, $", .NET {Megabytes(ManagedBytes):0.0} MB ({Signed(Megabytes(ManagedBytes - first.ManagedBytes))})");
    }

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    private static string Signed(double megabytes) => megabytes.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
}
