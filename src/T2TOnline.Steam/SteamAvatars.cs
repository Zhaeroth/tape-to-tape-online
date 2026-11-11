using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>Which of Steam's three avatar images to ask for.</summary>
public enum SteamAvatarSize
{
    /// <summary>Steam's small avatar (32 x 32 in practice; read the size, never assume it).</summary>
    Small = 0,

    /// <summary>Steam's medium avatar (64 x 64 in practice).</summary>
    Medium = 1,

    /// <summary>Steam's large avatar (184 x 184 in practice; the docs say 128).</summary>
    Large = 2,
}

/// <summary>
/// Steam avatars through the flat API, without callbacks (D20): <c>AvatarImageLoaded_t</c> is never heard, so a caller asks
/// for the image handle again until Steam has one (<see cref="SteamAvatarPoll"/> does that politely). An image handle is
/// turned into RGBA bytes with <c>GetImageSize</c> and <c>GetImageRGBA</c>. Every call here is an optional export: a
/// steam_api64 without one turns off only that call, which then answers as if Steam weren't there. None of these throw.
/// <para>
/// Sizes come from <c>GetImageSize</c>, never from a doc (the large avatar is 184 x 184, the docs say 128), and are accepted
/// between <see cref="MinSide"/> and <see cref="MaxSide"/> on each side. Steam hands the rows top row first.
/// </para>
/// </summary>
public static class SteamAvatars
{
    /// <summary>Steam's answer for a user with no avatar set (<c>0</c>).</summary>
    public const int NoAvatar = 0;

    /// <summary>Steam's answer while the avatar's data is still being loaded (<c>-1</c>): ask again later.</summary>
    public const int Loading = -1;

    /// <summary>Not Steam's: this library couldn't ask (Steam isn't available, the call's export is missing, or the ID is 0).</summary>
    public const int Unavailable = int.MinValue;

    /// <summary>The smallest width or height accepted.</summary>
    public const int MinSide = 8;

    /// <summary>The largest width or height accepted.</summary>
    public const int MaxSide = 512;

    /// <summary>Bytes in one RGBA pixel.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Asks Steam for a user's name and avatar (<c>RequestUserInformation</c>, not name only). True: the request was made and
    /// the data is on its way. False: Steam has everything already. Null: Steam can't be asked (not available, the ID is 0, or
    /// steam_api64 lacks the call).
    /// </summary>
    public static bool? RequestUserInformation(ulong steamId)
    {
        nint self = SteamInterfaces.Friends(nameof(SteamNative.RequestUserInformation));
        if (self == 0 || steamId == 0)
        {
            return null;
        }

        try
        {
            return SteamNative.RequestUserInformation(self, steamId, requireNameOnly: false);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return null;
        }
    }

    /// <summary>
    /// The image handle of a user's avatar: above 0 a handle for <see cref="TryRead"/>; <see cref="NoAvatar"/> when the user has
    /// none set; <see cref="Loading"/> while Steam is still fetching it; <see cref="Unavailable"/> when Steam can't be asked.
    /// Any other number Steam might answer is read as <see cref="NoAvatar"/>. Works for the local user and for lobby members
    /// who aren't friends (once <see cref="RequestUserInformation"/> has been answered).
    /// </summary>
    public static int GetHandle(ulong steamId, SteamAvatarSize size)
    {
        string export = size switch
        {
            SteamAvatarSize.Small => nameof(SteamNative.GetSmallFriendAvatar),
            SteamAvatarSize.Medium => nameof(SteamNative.GetMediumFriendAvatar),
            SteamAvatarSize.Large => nameof(SteamNative.GetLargeFriendAvatar),
            _ => string.Empty,
        };
        nint self = export.Length == 0 ? 0 : SteamInterfaces.Friends(export);
        if (self == 0 || steamId == 0)
        {
            return Unavailable;
        }

        try
        {
            int handle = size switch
            {
                SteamAvatarSize.Small => SteamNative.GetSmallFriendAvatar(self, steamId),
                SteamAvatarSize.Medium => SteamNative.GetMediumFriendAvatar(self, steamId),
                _ => SteamNative.GetLargeFriendAvatar(self, steamId),
            };
            return handle > 0 ? handle : handle == Loading ? Loading : NoAvatar;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return Unavailable;
        }
    }

    /// <summary>True when <paramref name="side"/> is a width or height this library accepts.</summary>
    public static bool IsAcceptedSide(uint side) => side is >= MinSide and <= MaxSide;

    /// <summary>The size of an image handle, if Steam knows it and it is between <see cref="MinSide"/> and <see cref="MaxSide"/> on each side.</summary>
    public static bool TryGetSize(int handle, out int width, out int height)
    {
        width = 0;
        height = 0;
        nint utils = SteamInterfaces.Utils(nameof(SteamNative.GetImageSize));
        if (utils == 0 || handle <= 0)
        {
            return false;
        }

        try
        {
            if (!SteamNative.GetImageSize(utils, handle, out uint w, out uint h) || !IsAcceptedSide(w) || !IsAcceptedSide(h))
            {
                return false;
            }

            width = (int)w;
            height = (int)h;
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// Reads an image handle's pixels into <paramref name="buffer"/> (RGBA, top row first, <paramref name="width"/> x
    /// <paramref name="height"/> x 4 bytes from its start), growing it if it is too small: pass the same buffer each time to
    /// reuse it. False, with the sizes 0, when the handle is unknown, its size is out of range, or Steam can't be asked.
    /// </summary>
    public static bool TryRead(int handle, ref byte[]? buffer, out int width, out int height)
    {
        if (!TryGetSize(handle, out width, out height))
        {
            width = 0;
            height = 0;
            return false;
        }

        int size = width * height * BytesPerPixel;
        if (buffer is null || buffer.Length < size)
        {
            buffer = new byte[size];
        }

        nint utils = SteamInterfaces.Utils(nameof(SteamNative.GetImageRGBA));
        if (utils == 0)
        {
            width = 0;
            height = 0;
            return false;
        }

        try
        {
            if (SteamNative.GetImageRGBA(utils, handle, ref buffer[0], size))
            {
                return true;
            }
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
        }

        width = 0;
        height = 0;
        return false;
    }

    /// <summary>
    /// Turns an image of <paramref name="width"/> x <paramref name="height"/> RGBA pixels upside down in place (the first row
    /// swaps with the last, and so on): Steam hands the top row first and Unity's textures start at the bottom row. False,
    /// touching nothing, when <paramref name="rgba"/> is shorter than the image or a size is not positive.
    /// </summary>
    public static bool FlipRows(Span<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * BytesPerPixel > rgba.Length)
        {
            return false;
        }

        int stride = width * BytesPerPixel;
        Span<byte> row = stride <= 2048 ? stackalloc byte[2048] : new byte[stride];
        row = row[..stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Span<byte> upper = rgba.Slice(top * stride, stride);
            Span<byte> lower = rgba.Slice(bottom * stride, stride);
            upper.CopyTo(row);
            lower.CopyTo(upper);
            row.CopyTo(lower);
        }

        return true;
    }
}
