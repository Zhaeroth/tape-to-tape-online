#if HAS_INTEROP
using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// Steam Cloud's copy of a save file, read with the game's own Facepunch layer (<c>Steamworks.SteamRemoteStorage</c>), the
/// copy the game's next load would write over the local file (<c>SteamService.Load</c>). Used to tell whether a
/// restored file may still go to Steam Cloud (<see cref="CloudPushDecision"/>). Each read checks, as the
/// game's load does, that Steam runs for the game (<c>SteamClient.IsValid</c>) and cloud saves are on
/// (<c>IsCloudEnabled</c>) before touching remote storage: its calls dereference Steam's interface unchecked. Calls only,
/// nothing patched; never throws.
/// </summary>
internal static class SteamCloudCopies
{
    // A save file Steam would hand back larger than this isn't one of the game's (theirs are a few hundred KB at most).
    private const int MaxFileBytes = 16 * 1024 * 1024;

    // DateTime's ticks without its kind bits (the top two of Il2CppSystem.DateTime's _dateData).
    private const ulong TicksMask = 0x3FFFFFFFFFFFFFFF;

    /// <summary>Steam's copy of <paramref name="file"/> (a bare save file name) now, and why when it isn't known or readable.</summary>
    public static SteamCopy Read(string file, out string why)
    {
        why = string.Empty;
        try
        {
            if (!Il2CppSteamworks.SteamClient.IsValid)
            {
                why = "Steam isn't running for the game";
                return SteamCopy.Unavailable;
            }

            if (!Il2CppSteamworks.SteamRemoteStorage.IsCloudEnabled)
            {
                why = "Steam Cloud is off for this game or account";
                return SteamCopy.Unavailable;
            }

            if (!Il2CppSteamworks.SteamRemoteStorage.FileExists(file))
            {
                return SteamCopy.NoCopy;
            }

            int size = Il2CppSteamworks.SteamRemoteStorage.FileSize(file);
            if (size <= 0 || size > MaxFileBytes)
            {
                why = $"Steam gives its size as {size} bytes";
                return SteamCopy.Unreadable;
            }

            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>? read = Il2CppSteamworks.SteamRemoteStorage.FileRead(file);
            if (read == null || read.Length != size)
            {
                why = "Steam's read of it came back short";
                return SteamCopy.Unreadable;
            }

            byte[] bytes = read.AsSpan().ToArray();
            Il2CppSystem.DateTime time = Il2CppSteamworks.SteamRemoteStorage.FileTime(file);
            long ticks = (long)(time._dateData & TicksMask);
            DateTime? at = ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
            return SteamCopy.Of(SaveFileHash.HashOf(bytes), at);
        }
        catch (Exception ex)
        {
            why = $"reading it failed ({ex.GetType().Name})";
            return SteamCopy.Unreadable;
        }
    }
}
#endif
