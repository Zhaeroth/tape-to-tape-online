#if HAS_INTEROP
using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>
/// The garbage collectors' counts and heaps on both runtimes, for a periodic report line: .NET's through
/// <c>System.GC</c>, the game's IL2CPP through <c>Il2CppSystem.GC</c> (calls, never patched; the IL2CPP side reads as
/// unreadable, -1, if the interop call fails). Each report keeps its own previous sample, so its line says how many
/// collections ran since it last spoke. Reads take microseconds: called every few seconds at most.
/// </summary>
internal sealed class MemoryReport
{
    private MemorySample? _previous;

    /// <summary>The first sample this report took, or null before any.</summary>
    public MemorySample? First { get; private set; }

    /// <summary>The garbage collectors now.</summary>
    public static MemorySample Read()
    {
        int il2cppCount = -1;
        long il2cppBytes = -1;
        try
        {
            il2cppCount = Il2CppSystem.GC.CollectionCount(0);
            il2cppBytes = Il2CppSystem.GC.GetTotalMemory(false);
        }
        catch (Exception)
        {
            // Left unreadable: the line says so.
        }

        return new MemorySample(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalMemory(false), il2cppCount, il2cppBytes);
    }

    /// <summary>"GC .NET 12/3/1 (+2), IL2CPP 340 (+6); heaps .NET 48.2 MB, IL2CPP 512.0 MB" now, against this report's last sample.</summary>
    public string Take()
    {
        MemorySample now = Read();
        string text = now.Describe(_previous);
        _previous = now;
        First ??= now;
        return text;
    }

    /// <summary>Forgets the samples (a new session).</summary>
    public void Clear()
    {
        _previous = null;
        First = null;
    }
}
#endif
