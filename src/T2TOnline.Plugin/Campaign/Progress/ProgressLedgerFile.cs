#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// Where a friend's shared-progress ledger (<see cref="ProgressLedger"/>) lives, and how it follows a restore of the saves.
/// <list type="bullet">
/// <item>It describes this player's profile and stats (which batches they hold), so it lives with the mod's other
/// player data outside the game folder, <c>%LOCALAPPDATA%\T2T Online\progress\progress-ledger.txt</c> (a game folder
/// cleared for a reinstall keeps it), moved there from <c>UserData/T2TOnline/campaign/</c> at start-up.</item>
/// <item>A restore of older saves (the Backups screen, or the developer's panel) must bring the ledger back to the same moment, or a rejoin of a hosting
/// session still running would skip the batches the restore took away (and a run's squad record and history entry). A
/// copy is taken as each following session starts and ends (<see cref="Snapshot"/>, <see cref="ProgressLedgerHistory"/>),
/// and whenever the ledger turns out to have changed with none after it (<see cref="KeepCopy"/>: a session the game
/// stopped in the middle of, a restore, or a following session's batches before a backup taken by hand), so the copies hold it for any moment; when a restore has put save files in
/// place, the save backups say so (<see cref="Restored"/>) and the copy from when the restored backup was taken becomes
/// the ledger. With no copy that old the ledger is kept (a rejoin may miss what the restore took away, but nothing
/// applies twice). Nothing else moves it: a restore that was undone, or is only partly done and waits for the next
/// start, leaves the saves as they were or unsettled, and a ledger older than the saves would let a rejoin apply again
/// what they already hold.</item>
/// </list>
/// Never throws.
/// </summary>
internal static class ProgressLedgerFile
{
    /// <summary>The ledger's file name.</summary>
    public const string FileName = "progress-ledger.txt";

    private static bool _installed;

    /// <summary>The mod's progress folder outside the game folder.</summary>
    public static string Folder => Path.Combine(CampaignFiles.DataFolder, "progress");

    /// <summary>Where the ledger lives.</summary>
    public static string LivePath => Path.Combine(Folder, FileName);

    /// <summary>Where the ledger's dated copies live.</summary>
    public static string HistoryFolder => Path.Combine(Folder, "ledger-history");

    /// <summary>Where the ledger lived before 0.6.1 (in the game folder).</summary>
    private static string OldPath => Path.Combine(CampaignFiles.CampaignFolder, FileName);

    /// <summary>
    /// At start-up: the ledger moves out of the game folder (once), and gets the dated copy it's owed when the last
    /// session never wrote its own (the game stopped during it).
    /// </summary>
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        MoveFromGameFolder();
        KeepCopy();
    }

    /// <summary>
    /// A following session starts (<paramref name="start"/>) or ends: a dated copy of the ledger as it is now. Not a check tour's
    /// play period: a run of them would push the player's own copies out, as it would their backups (<see cref="SaveBackups.BeginSession"/>).
    /// </summary>
    public static void Snapshot(bool start)
    {
        if (NativeLobby.TourHolds.On)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(HistoryFolder);
            string text = File.Exists(LivePath) ? File.ReadAllText(LivePath) : new ProgressLedger().Format();
            File.WriteAllText(Path.Combine(HistoryFolder, ProgressLedgerHistory.Name(DateTime.UtcNow, start)), text);
            foreach (string old in ProgressLedgerHistory.Expired(Copies()))
            {
                File.Delete(Path.Combine(HistoryFolder, old));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ModLog.Warn($"Progress: the ledger's dated copy couldn't be kept ({CampaignFiles.ErrorText(ex)}); a restore of saves from this time can't bring the ledger back with them.");
        }
    }

    /// <summary>
    /// The saves were restored from a backup taken at <paramref name="backupUtc"/> (<paramref name="name"/> for the log):
    /// the ledger becomes the copy from that moment. Called by the save backups once a restore's files are all in place
    /// (a restore, or one finished at a later start), never for one that was undone. Never throws.
    /// </summary>
    public static void Restored(string name, DateTime backupUtc)
    {
        // First the ledger as it is, if no copy holds it yet: one of the copies picked from below may be owed.
        KeepCopy();
        try
        {
            string? copy = Directory.Exists(HistoryFolder) ? ProgressLedgerHistory.Pick(Copies(), backupUtc) : null;
            if (copy == null)
            {
                ModLog.Warn($"Progress: no copy of the shared-progress ledger is as old as backup {SaveBackupNames.ForLog(name)}; the ledger stays as it is, so shared progress the " +
                    "restore took away won't come again on a rejoin (and nothing applies twice).");
                return;
            }

            // Read through the parser: only what reads goes back.
            string text = ProgressLedger.Parse(File.ReadAllText(Path.Combine(HistoryFolder, copy))).Format();
            Directory.CreateDirectory(Folder);
            string temporary = LivePath + CampaignFiles.PartSuffix;
            File.WriteAllText(temporary, text);
            File.Move(temporary, LivePath, overwrite: true);
            ModLog.Info($"Progress: the shared-progress ledger is back as it was when backup {SaveBackupNames.ForLog(name)} was taken (copy {copy}), with the restored saves.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ModLog.Warn($"Progress: the shared-progress ledger couldn't follow the restore ({CampaignFiles.ErrorText(ex)}); a rejoin may miss shared progress the restore took away.");
            return;
        }

        // The ledger just changed outside a session: a backup taken from now on (before another restore, a host's) goes with it.
        KeepCopy();
    }

    /// <summary>
    /// The ledger as it is gets a dated copy when the newest one doesn't hold it (<see cref="ProgressLedgerCatchUp"/>): a
    /// session the game stopped in the middle of saved it after every batch and never wrote its end copy, and a restore
    /// replaces it. The copy is dated when the ledger was last written, so a backup taken since then restores with it,
    /// whenever this runs: at start-up, around a restore, and before a backup taken by hand during a session. Not during a
    /// check tour (as <see cref="Snapshot"/>). Never throws.
    /// </summary>
    internal static void KeepCopy()
    {
        if (NativeLobby.TourHolds.On)
        {
            return;
        }

        try
        {
            var live = new FileInfo(LivePath);
            if (!live.Exists)
            {
                return;
            }

            string text = File.ReadAllText(LivePath);
            DateTime newestUtc = default;
            string? newest = Directory.Exists(HistoryFolder) ? ProgressLedgerCatchUp.Newest(Copies(), out newestUtc) : null;
            string? newestText = newest != null ? File.ReadAllText(Path.Combine(HistoryFolder, newest)) : null;
            string? owed = ProgressLedgerCatchUp.CopyOwed(text, live.LastWriteTimeUtc, newestText, newest != null ? newestUtc : null, DateTime.UtcNow);
            if (owed == null)
            {
                return;
            }

            Directory.CreateDirectory(HistoryFolder);
            File.WriteAllText(Path.Combine(HistoryFolder, owed), text);
            foreach (string old in ProgressLedgerHistory.Expired(Copies()))
            {
                File.Delete(Path.Combine(HistoryFolder, old));
            }

            ModLog.Info($"Progress: the shared-progress ledger had changed since its newest dated copy ({newest ?? "none"}): its batches since then, a session the game stopped during, or a restore. " +
                $"A copy as it is now is kept ({owed}), so a restore of saves from that time on brings it back with them.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ModLog.Warn($"Progress: the ledger's dated copy couldn't be kept ({CampaignFiles.ErrorText(ex)}); a restore of saves from this time can't bring the ledger back with them.");
        }
    }

    private static IEnumerable<string> Copies() => Directory.GetFiles(HistoryFolder).Select(f => Path.GetFileName(f));

    private static void MoveFromGameFolder()
    {
        try
        {
            if (!File.Exists(OldPath))
            {
                return;
            }

            if (File.Exists(LivePath))
            {
                ModLog.Warn($"Progress: a shared-progress ledger is in both the game folder and {CampaignFiles.DataFolderLabel}; the latter is used.");
                return;
            }

            Directory.CreateDirectory(Folder);
            File.Copy(OldPath, LivePath);
            File.Delete(OldPath);
            ModLog.Info($@"Progress: the shared-progress ledger moved out of the game folder to {CampaignFiles.DataFolderLabel}\progress.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ModLog.Warn($"Progress: the shared-progress ledger couldn't move out of the game folder ({CampaignFiles.ErrorText(ex)}); it's read from there.");
        }
    }

    /// <summary>The ledger to read: the moved one, or the game folder's when it couldn't move.</summary>
    internal static string ReadPath => !File.Exists(LivePath) && File.Exists(OldPath) ? OldPath : LivePath;
}
#endif
