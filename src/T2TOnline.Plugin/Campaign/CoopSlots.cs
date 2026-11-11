#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader.Utils;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// The co-op save slots (D46: players choose a co-op save slot before a session starts, so their own saves are never
/// overwritten). While hosting, the game's run file is the chosen slot's file
/// (<c>%LOCALAPPDATA%\T2T Online\coop\coop-&lt;n&gt;.es3</c>) instead of the solo run's <c>RunDataV2.es3</c>, so a co-op run
/// never replaces the host's solo run and can be continued later with the same friends. <see cref="RunSync"/> switches the
/// game over at the title screen; this reads the slots for the lobby's Save row (and the developer's F1 panel) and changes the chosen one.
/// </summary>
internal static class CoopSlots
{
    /// <summary>The number of co-op slots.</summary>
    public const int Count = 3;

    // Each slot's last reading, kept while its file's size and last write stay the same (the panel asks every few seconds).
    private static readonly Dictionary<int, (SaveStamp Stamp, CoopSlotInfo Info)> Known = new();
    private static bool _moved;

    /// <summary>The slot co-op runs are kept in (the CoopSlot setting).</summary>
    public static int Selected => ModSettings.CoopSlot;

    /// <summary>
    /// Each slot, for the panel: whether a run is in it (one the game can continue), its squad, act, floor and when it was
    /// saved. A slot's file is read again only when its size or last write changed.
    /// </summary>
    public static IReadOnlyList<CoopSlotInfo> Describe()
    {
        MoveFromGameFolder();
        var slots = new List<CoopSlotInfo>(Count);
        for (int slot = 1; slot <= Count; slot++)
        {
            slots.Add(DescribeSlot(slot));
        }

        return slots;
    }

    /// <summary>One slot as <see cref="Describe"/> gives it (the lobby's save row asks often: its file's read again only when its stamp changed).</summary>
    public static CoopSlotInfo DescribeOne(int slot)
    {
        MoveFromGameFolder();
        return DescribeSlot(slot);
    }

    /// <summary>
    /// Makes <paramref name="slot"/> the co-op slot: any time outside hosting, or at the title screen (with nothing open on
    /// it) while hosting or while a co-op run is the game's run. False, with why, when it can't; the reason reads on after
    /// "Slot N wasn't selected: ". Saved in the mod's settings.
    /// </summary>
    public static bool TrySelect(int slot, out string problem)
    {
        if (slot is < 1 or > Count)
        {
            problem = $"there are only slots 1 to {Count}";
            return false;
        }

        int before = Selected;
        RunSync? sync = RunSync.Instance;
        if (slot != before && sync != null && !sync.CanChangeCoopSlot(out problem))
        {
            return false;
        }

        if (slot != before && !ModSettings.TrySetCoopSlot(slot))
        {
            problem = "the mod's settings aren't loaded";
            return false;
        }

        problem = string.Empty;
        if (slot != before)
        {
            ModLog.Info($"Co-op slots: slot {slot} is the co-op slot now (it was slot {before}).");
            sync?.CoopSlotChanged();
        }

        return true;
    }

    /// <summary>
    /// At start-up (or the panel's first look), before any run file points at a slot: co-op slots still in the game folder
    /// (0.6.0 kept them in <c>UserData\T2TOnline\campaign</c>, which a reinstall or the mod's uninstall deletes) move to
    /// <see cref="CampaignFiles.SlotsFolder"/>, with Easy Save's backup copy. A slot already there is never replaced: the old
    /// file then stays where it was, and the log says so. Once per game session; never throws.
    /// </summary>
    public static void MoveFromGameFolder()
    {
        if (_moved)
        {
            return;
        }

        _moved = true;
        try
        {
            MoveSlots();
        }
        catch (Exception ex)
        {
            ModLog.Error("Co-op slots: moving them out of the game folder failed", ex);
        }
    }

    private static void MoveSlots()
    {
        for (int slot = 1; slot <= Count; slot++)
        {
            string old = CampaignFiles.OldSlotPath(slot);
            string target = CampaignFiles.SlotPath(slot);
            var written = new List<string>();
            try
            {
                if (!File.Exists(old) || string.Equals(Path.GetFullPath(old), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(target))
                {
                    ModLog.Warn($"Co-op slots: slot {slot} is already in {CampaignFiles.DataFolderLabel}, so the older copy in UserData\\T2TOnline\\campaign " +
                        $"(coop-{slot}.es3) is left there untouched.");
                    continue;
                }

                // Copied whole and checked before the old one goes (the game folder and the user's folder can be on two drives);
                // the run file last, so a slot whose run file is there is a whole one.
                foreach (string suffix in new[] { ".bac", string.Empty })
                {
                    if (File.Exists(old + suffix))
                    {
                        byte[] bytes = CampaignFiles.ReadShared(old + suffix);
                        written.Add(target + suffix);
                        CampaignFiles.WriteAtomically(target + suffix, bytes);
                        if (SaveFileHash.HashOf(CampaignFiles.ReadShared(target + suffix)) != SaveFileHash.HashOf(bytes))
                        {
                            throw new IOException($"the copy of coop-{slot}.es3{suffix} doesn't match the original");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // The old file is the slot until it's copied whole: what was copied of it goes again.
                written.ForEach(TryDelete);
                ModLog.Warn($"Co-op slots: slot {slot} couldn't be moved out of the game folder ({CampaignFiles.ErrorText(ex)}); it stays in UserData\\T2TOnline\\campaign, " +
                    "where it isn't used, until it's moved (tried again at the next start).");
                continue;
            }

            try
            {
                CampaignFiles.DeleteWithCopies(old);
                ModLog.Info($"Co-op slots: slot {slot} moved from UserData\\T2TOnline\\campaign to {CampaignFiles.SlotLabel(slot)}, where a reinstall can't delete it.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Co-op slots: slot {slot} was copied to {CampaignFiles.SlotLabel(slot)}, which is the slot now, but its old copy in " +
                    $"UserData\\T2TOnline\\campaign couldn't be removed ({CampaignFiles.ErrorText(ex)}).");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Co-op slots: couldn't remove {Path.GetFileName(path)} ({CampaignFiles.ErrorText(ex)}).");
        }
    }

    private static CoopSlotInfo DescribeSlot(int slot)
    {
        string path = CampaignFiles.RunFileFor(slot); // a campaign tour's own run file while it stands in for the slots
        var empty = new CoopSlotInfo(slot, false, string.Empty, 0, 0, DateTime.MinValue);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                Known.Remove(slot);
                return empty;
            }

            var stamp = new SaveStamp(true, file.Length, file.LastWriteTimeUtc);
            if (Known.TryGetValue(slot, out (SaveStamp Stamp, CoopSlotInfo Info) known) && known.Stamp == stamp)
            {
                return known.Info;
            }

            CoopSlotInfo info = Summarize(slot, path, file);
            Known[slot] = (stamp, info);
            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Known.Remove(slot);
            ModLog.Warn($"Co-op slots: couldn't read slot {slot} ({CampaignFiles.ErrorText(ex)}).");
            return new CoopSlotInfo(slot, true, "a run", 0, 0, DateTime.MinValue);
        }
    }

    private static CoopSlotInfo Summarize(int slot, string path, FileInfo file)
    {
        if (file.Length > RunCheckpoint.MaxPartBytes)
        {
            return new CoopSlotInfo(slot, true, "a run", 0, 0, file.LastWriteTime);
        }

        byte[] bytes = CampaignFiles.ReadShared(path);
        if (!RunFileSummary.TryRead(bytes, out RunFileSummary? run) || run == null || run.SquadId.Length == 0)
        {
            return new CoopSlotInfo(slot, true, "a run", 0, 0, file.LastWriteTime);
        }

        // A lost run stays in its file until the game deletes it, but the game won't continue it.
        return run.Failed
            ? new CoopSlotInfo(slot, false, string.Empty, 0, 0, file.LastWriteTime)
            : new CoopSlotInfo(slot, true, CampaignGame.SquadName(run.SquadId), run.Act, run.Floor, file.LastWriteTime, run.SquadId);
    }
}

/// <summary>One co-op slot: whether a run is in it, and that run's squad, act and floor, and when it was saved. <paramref name="SquadId"/> is the squad's id, which each PC names in its own language (empty when not known).</summary>
internal sealed record CoopSlotInfo(int Slot, bool HasRun, string Squad, int Act, int Floor, DateTime SavedAt, string SquadId = "");

/// <summary>
/// The mod's own campaign files. Absolute paths: Easy Save keeps an absolute path as it is (its <c>FullPath</c>), so the
/// game reads and writes these instead of files in its own save folder.
/// <list type="bullet">
/// <item>What must outlive the game folder (a reinstall, or the mod's uninstall, deletes UserData): the co-op slots and the
/// save backups, in <c>%LOCALAPPDATA%\T2T Online</c>.</item>
/// <item>What lasts only a session: the host's run on a friend's PC, under <c>UserData\T2TOnline\campaign</c>.</item>
/// </list>
/// </summary>
internal static class CampaignFiles
{
    /// <summary>The suffix of a file the mod is still writing; it's moved into place once whole.</summary>
    public const string PartSuffix = T2TOnline.Core.Diagnostics.ModTemporaries.PartSuffix;

    /// <summary>How <see cref="DataFolder"/> is named in logs and on screen (without the player's user name).</summary>
    public const string DataFolderLabel = @"%LOCALAPPDATA%\T2T Online";

    private static string? _dataFolder;

    /// <summary>The mod's folder in UserData (logs, the friend's copy of the host's run, the check tours).</summary>
    public static string ModFolder => Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline");

    /// <summary>
    /// The mod's folder outside the game folder, <c>%LOCALAPPDATA%\T2T Online</c>: the co-op slots and the backups, which
    /// must survive the game folder being cleared. On a PC without a local application data folder, <see cref="ModFolder"/>.
    /// </summary>
    public static string DataFolder => _dataFolder ??= FindDataFolder();

    /// <summary>Where the host's run lives on a friend's PC.</summary>
    public static string CampaignFolder => Path.Combine(ModFolder, "campaign");

    /// <summary>Where the co-op slots live.</summary>
    public static string SlotsFolder => Path.Combine(DataFolder, "coop");

    /// <summary>Where save backups go.</summary>
    public static string BackupsFolder => Path.Combine(DataFolder, "backups");

    /// <summary>Where save backups went before 0.6.1 (moved by <see cref="SaveBackups.StartUp"/>).</summary>
    public static string OldBackupsFolder => Path.Combine(ModFolder, "backups");

    /// <summary>A friend's copy of the host's run (D41): the game loads it in place of the friend's own run file.</summary>
    public static string HostRunPath => Path.Combine(CampaignFolder, "HostRun.es3");

    /// <summary>Co-op slot <paramref name="slot"/>'s run file.</summary>
    public static string SlotPath(int slot) => Path.Combine(SlotsFolder, $"coop-{slot}.es3");

    /// <summary>Co-op slot <paramref name="slot"/>'s run file before 0.6.1 (moved by <see cref="CoopSlots.MoveFromGameFolder"/>).</summary>
    public static string OldSlotPath(int slot) => Path.Combine(CampaignFolder, $"coop-{slot}.es3");

    /// <summary>Co-op slot <paramref name="slot"/>'s file as logs and the panel name it.</summary>
    public static string SlotLabel(int slot) => $@"{DataFolderLabel}\coop\coop-{slot}.es3";

    /// <summary>
    /// The run file co-op slot <paramref name="slot"/>'s runs are played in: the slot's own file, or, while a campaign check
    /// tour has set one, the tour's own run file for every slot (<see cref="NativeLobby.TourHolds.RunFile"/>), so the
    /// player's slots are never touched. <see cref="SlotPath"/> stays the slot's own file (backups and restores use it).
    /// </summary>
    public static string RunFileFor(int slot) => NativeLobby.TourHolds.RunFile ?? SlotPath(slot);

    /// <summary><see cref="RunFileFor"/> as logs name it.</summary>
    public static string RunFileLabel(int slot) => NativeLobby.TourHolds.RunFile is { } tour ? DevOutput.Label(tour) : SlotLabel(slot);

    /// <summary>True for an absolute path: the game itself only ever names its save files, so an absolute one is the mod's.</summary>
    public static bool IsAbsolute(string? path) => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path);

    /// <summary>True for a path inside one of the mod's folders (<see cref="ModFolder"/> or <see cref="DataFolder"/>).</summary>
    public static bool IsModPath(string? path)
    {
        if (!IsAbsolute(path))
        {
            return false;
        }

        string full = Path.GetFullPath(path!);
        return IsInside(full, ModFolder) || IsInside(full, DataFolder);
    }

    /// <summary>
    /// An exception's message for the log, with the folders that would name the player (their profile, the game's save
    /// folder, the game folder) replaced by placeholders. Never throws.
    /// </summary>
    public static string ErrorText(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        try
        {
            return SavePathText.Redact(ex.Message, new[]
            {
                new KeyValuePair<string, string>(UnityEngine.Application.persistentDataPath, "<save folder>"),
                new KeyValuePair<string, string>(MelonEnvironment.GameRootDirectory, "<game folder>"),
                new KeyValuePair<string, string>(DataFolder, DataFolderLabel),
                new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
                new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
            });
        }
        catch (Exception)
        {
            return ex.GetType().Name; // the folders couldn't be looked up: the message may hold a full path, so it's left out
        }
    }

    /// <summary>A file's bytes, read while others may have it open.</summary>
    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[stream.Length];
        int read = 0;
        while (read < bytes.Length)
        {
            int got = stream.Read(bytes, read, bytes.Length - read);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return read == bytes.Length ? bytes : bytes[..read];
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> whole or not at all: a temporary file, then moved into
    /// place. Throws (IO errors) when it couldn't, leaving <paramref name="path"/> as it was and no temporary file behind.
    /// </summary>
    public static void WriteAtomically(string path, byte[] bytes)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = path + PartSuffix;
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Campaign files: couldn't remove {Path.GetFileName(temporary)} after a failed write ({ErrorText(cleanup)}).");
            }

            throw;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> and Easy Save's copies of it (<see cref="EasySaveCopies.Suffixes"/>) and the mod's
    /// own leftover. Returns how many went.
    /// </summary>
    public static int DeleteWithCopies(string path)
    {
        int deleted = 0;
        var files = new List<string> { path, path + PartSuffix };
        foreach (string suffix in EasySaveCopies.Suffixes)
        {
            files.Add(path + suffix);
        }

        foreach (string file in files)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
                deleted++;
            }
        }

        return deleted;
    }

    private static bool IsInside(string fullPath, string folder)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindDataFolder()
    {
        string local = string.Empty;
        try
        {
            local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch (PlatformNotSupportedException)
        {
            // none on this platform
        }

        if (string.IsNullOrEmpty(local) || !Path.IsPathRooted(local))
        {
            ModLog.Warn("Campaign files: this PC has no local application data folder, so the co-op slots and backups stay in the game folder (UserData\\T2TOnline).");
            return ModFolder;
        }

        return Path.Combine(local, "T2T Online");
    }
}
#endif
