#if HAS_INTEROP
using System;
using System.IO;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// This PC's secret for the keys its progress ledger knows hosts by, and its shared-play record hosting sessions by
/// (<see cref="HostKeys"/>, D135), in <c>%LOCALAPPDATA%\T2T Online\progress\host-key.bin</c>: 32 random bytes, made the
/// first time this PC is welcomed into a host's session, then read once per game. It never leaves this PC and is never
/// logged. A file that isn't a key is replaced (the daily limits per host then start over, and the shared play recorded
/// before is no longer found); one that can't be written leaves a key for this game only. Never throws.
/// </summary>
internal static class HostKeyFile
{
    /// <summary>The file's name, in the progress folder.</summary>
    public const string FileName = "host-key.bin";

    private static byte[]? _secret;

    /// <summary>The ledger's key for host <paramref name="steamId"/>.</summary>
    public static string KeyFor(ulong steamId) => HostKeys.Tag(Secret(), steamId);

    /// <summary>
    /// The shared-play record's key for hosting session <paramref name="session"/> of host <paramref name="steamId"/>
    /// (<see cref="HostKeys.SessionTag"/>; 0 for session 0).
    /// </summary>
    public static ulong SessionKeyFor(ulong steamId, ulong session) => HostKeys.SessionTag(Secret(), steamId, session);

    private static byte[] Secret()
    {
        if (_secret != null)
        {
            return _secret;
        }

        string path = Path.Combine(ProgressLedgerFile.Folder, FileName);
        byte[]? file = null;
        try
        {
            file = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unreadable (locked, say): a key for this game only, so the file isn't replaced by one that may be wrong.
            ModLog.Warn($@"Progress: this PC's key for the daily limits per host (progress\{FileName}) couldn't be read ({CampaignFiles.ErrorText(ex)}); a key for this game only is used, so those limits start over at the next start.");
            return _secret = HostKeys.NewSecret();
        }

        if (HostKeys.TryRead(file, out byte[] secret, out string why))
        {
            return _secret = secret;
        }

        _secret = secret;
        if (NativeLobby.TourHolds.HoldsFile("Progress", "this PC's key for the daily limits per host"))
        {
            return _secret; // made during a check tour: for this game only, and the file (if any) left as it is
        }

        try
        {
            Directory.CreateDirectory(ProgressLedgerFile.Folder);
            string temporary = path + CampaignFiles.PartSuffix;
            File.WriteAllBytes(temporary, secret);
            File.Move(temporary, path, overwrite: true);
            ModLog.Info(file == null
                ? $@"Progress: made this PC's key for the daily limits per host (progress\{FileName}; it never leaves this PC)."
                : $@"Progress: this PC's key for the daily limits per host (progress\{FileName}) wasn't a key ({why}); a new one is made, so those limits start over.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($@"Progress: this PC's key for the daily limits per host (progress\{FileName}) couldn't be saved ({CampaignFiles.ErrorText(ex)}); it holds for this game only.");
        }

        return _secret;
    }
}
#endif
