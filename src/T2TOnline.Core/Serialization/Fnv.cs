using System;

namespace T2TOnline.Core.Serialization;

/// <summary>
/// 64-bit FNV-1a: the protocol's content hash (match setups, run checkpoints, host views). Not cryptographic: it tells two
/// honest peers whether they hold the same content; every message is still validated on its own.
/// </summary>
public static class Fnv
{
    /// <summary>The hash of nothing; start here.</summary>
    public const ulong OffsetBasis = 14695981039346656037UL;

    private const ulong Prime = 1099511628211UL;

    /// <summary>Adds one byte to <paramref name="hash"/>.</summary>
    public static ulong Add(ulong hash, byte value) => (hash ^ value) * Prime;

    /// <summary>Adds <paramref name="bytes"/> to <paramref name="hash"/>.</summary>
    public static ulong Add(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * Prime;
        }

        return hash;
    }

    /// <summary>The hash of <paramref name="bytes"/>.</summary>
    public static ulong Hash(ReadOnlySpan<byte> bytes) => Add(OffsetBasis, bytes);
}
