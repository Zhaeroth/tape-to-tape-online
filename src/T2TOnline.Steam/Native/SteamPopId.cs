using System;

namespace T2TOnline.Steam.Native;

/// <summary>Steam Datagram Relay data-center ids (<c>SteamNetworkingPOPID</c>), as in steamnetworkingtypes.h.</summary>
public static class SteamPopId
{
    /// <summary>
    /// The 3- or 4-character code of a POP id, such as "fra" or "sea", or an empty string for 0. Follows
    /// <c>GetSteamNetworkingLocationPOPStringFromID</c>: characters from bits 16, 8, 0, then 24, up to the first zero.
    /// Non-printable characters come out as '?'.
    /// </summary>
    public static string ToCode(uint popId)
    {
        Span<char> code = stackalloc char[4];
        int length = 0;
        if (Append(code, ref length, popId >> 16) && Append(code, ref length, popId >> 8) && Append(code, ref length, popId))
        {
            Append(code, ref length, popId >> 24);
        }

        return new string(code[..length]);
    }

    private static bool Append(Span<char> code, ref int length, uint bits)
    {
        uint c = bits & 0xFF;
        if (c == 0)
        {
            return false;
        }

        code[length++] = c is >= 0x20 and < 0x7F ? (char)c : '?';
        return true;
    }
}
