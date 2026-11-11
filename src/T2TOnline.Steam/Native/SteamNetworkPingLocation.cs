using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>SteamNetworkPingLocation_t</c> (steamnetworkingtypes.h): 512 opaque bytes (<c>uint8 m_data[512]</c>).
/// Valid only inside the process that made it; to share a location, convert it to a string with
/// <see cref="SteamPing"/>.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public readonly struct SteamNetworkPingLocation
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 512;

    [FieldOffset(0)]
    private readonly ulong _head;

    /// <summary>True for a default (all-zero) location: never filled in by Steam, at least in its first 8 bytes.</summary>
    public bool IsDefault => _head == 0;
}
