using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>SteamNetworkingIdentity</c> (steamnetworkingtypes.h, <c>#pragma pack(1)</c>): 136 bytes.
/// Offsets: <c>m_eType</c> 0 (int), <c>m_cbSize</c> 4 (int), then a 128-byte union at 8 whose
/// <c>m_steamID64</c> member is a uint64 at 8. Only the Steam ID form is used by this project.
/// The managed helpers below do exactly what the SDK's <c>SetSteamID64</c>, <c>GetSteamID64</c> and <c>Clear</c> do
/// (GameNetworkingSockets, steamnetworkingtypes.cpp), so building an identity needs no native call;
/// <see cref="SteamRuntime.VerifyIdentityLayout"/> cross-checks them against the real exports in game.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = SizeInBytes)]
public struct SteamNetworkingIdentity
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 136;

    /// <summary>Offset of <c>m_eType</c>.</summary>
    public const int TypeOffset = 0;

    /// <summary>Offset of <c>m_cbSize</c>.</summary>
    public const int ByteCountOffset = 4;

    /// <summary>Offset of the union, and of its <c>m_steamID64</c> member.</summary>
    public const int SteamId64Offset = 8;

    [FieldOffset(TypeOffset)]
    private SteamNetworkingIdentityType _type;

    [FieldOffset(ByteCountOffset)]
    private int _byteCount;

    [FieldOffset(SteamId64Offset)]
    private ulong _steamId64;

    /// <summary>The kind of identity (<c>m_eType</c>).</summary>
    public readonly SteamNetworkingIdentityType Type => _type;

    /// <summary>Number of meaningful bytes in the union (<c>m_cbSize</c>); 8 for a Steam ID.</summary>
    public readonly int ByteCount => _byteCount;

    /// <summary>The Steam ID, or 0 if this isn't a Steam ID identity (the SDK's <c>GetSteamID64</c>).</summary>
    public readonly ulong SteamId64 => _type == SteamNetworkingIdentityType.SteamId ? _steamId64 : 0;

    /// <summary>An identity for a 64-bit Steam ID (the SDK's <c>SetSteamID64</c>).</summary>
    public static SteamNetworkingIdentity FromSteamId(ulong steamId64)
    {
        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamId64(steamId64);
        return identity;
    }

    /// <summary>Sets this identity to a Steam ID, like the SDK's <c>SetSteamID64</c>.</summary>
    public void SetSteamId64(ulong steamId64)
    {
        this = default;
        _type = SteamNetworkingIdentityType.SteamId;
        _byteCount = sizeof(ulong);
        _steamId64 = steamId64;
    }

    /// <summary>Resets to the invalid identity (all zero bytes), like the SDK's <c>Clear</c>.</summary>
    public void Clear() => this = default;
}
