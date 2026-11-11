using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>Where one file or co-op slot a restore's journal names stands now (<see cref="RestoreOnDisk"/>).</summary>
public enum RestoreFileState
{
    /// <summary>As the restored backup holds it.</summary>
    Restored,

    /// <summary>As it was before the restore: the "before" backup's copy, or no file where that backup holds none.</summary>
    AsBefore,

    /// <summary>Neither, as far as the two backups' manifests tell: something wrote it after the restore stopped.</summary>
    WrittenSince,
}

/// <summary>One file or co-op slot a restore's journal names, and where it stands now.</summary>
/// <param name="Label">The save file's name, or <c>co-op slot N</c>.</param>
/// <param name="State">Where it stands.</param>
public readonly record struct RestoreFileNow(string Label, RestoreFileState State);

/// <summary>
/// What is on disk now of the files and co-op slots a restore's journal names (D46, D137), against the journal's two
/// backups. A restore the game stopped in the middle of leaves each of them as the restored backup holds it or as it was
/// before (each file is staged beside its target, verified, and moved into place by a rename; the "before" backup holds
/// exactly the bytes it replaced), and the next start's recovery runs before the game reads or writes a save. So one that
/// is neither was written after the restore stopped, by a start that couldn't put it right (a mixed install runs no
/// recovery; a game version T2T Online can't patch waits) or after a journal that should have gone stayed: putting a
/// backup's file over it would lose what was played. The game rewrites its play-time counter in
/// <see cref="ProfilePlayTime.ProfileFile"/> whenever it saves the profile, a plain start and quit included, so a profile
/// that differs from a backup's copy only in that counter is as that copy (<see cref="ProfilePlayTime.SameApartFromPlayTime"/>,
/// the rule a new backup follows to stand for the newest). A backup whose manifest can't be read vouches for nothing.
/// Game-independent; never throws but for null arguments.
/// </summary>
public sealed class RestoreOnDisk
{
    private RestoreOnDisk(RestoreJournal journal, bool manifestsRead, IReadOnlyList<RestoreFileNow> entries)
    {
        Journal = journal;
        ManifestsRead = manifestsRead;
        Entries = entries;
        Changed = entries.Where(e => e.State == RestoreFileState.WrittenSince).Select(e => e.Label).ToList();
    }

    /// <summary>The journal it was read for.</summary>
    public RestoreJournal Journal { get; }

    /// <summary>True when both backups' manifests could be read (else a file only the missing one could vouch for counts as written since).</summary>
    public bool ManifestsRead { get; }

    /// <summary>Each file the journal names, in its order, then each co-op slot, with where it stands.</summary>
    public IReadOnlyList<RestoreFileNow> Entries { get; }

    /// <summary>The labels of those written since the restore stopped.</summary>
    public IReadOnlyList<string> Changed { get; }

    /// <summary>True when any of them was written since the restore stopped: a recovery that puts files in place keeps what is there first.</summary>
    public bool WrittenSince => Changed.Count > 0;

    /// <summary>True when every one of them is as the restored backup holds it: an all-moved restore still stands, and what follows its moves may be done.</summary>
    public bool AllRestored => Entries.All(e => e.State == RestoreFileState.Restored);

    /// <summary>
    /// Reads where each file and slot <paramref name="journal"/> names stands. <paramref name="backup"/> and <paramref name="before"/>
    /// are the manifests of its restored backup and of the one taken before it (null: can't be read). <paramref name="files"/> are the
    /// save folder's files as they are now, by name (any case; a name missing is no file); <paramref name="slots"/> the co-op slots
    /// (an empty slot file is none). <paramref name="copy"/> gives a backup's copy of a save file (the backup's name, the file's), or
    /// null when it can't be read; it is asked only for <see cref="ProfilePlayTime.ProfileFile"/> when its bytes are neither backup's,
    /// and a copy that doesn't match its manifest is not used.
    /// </summary>
    public static RestoreOnDisk Read(RestoreJournal journal, SaveBackupManifest? backup, SaveBackupManifest? before,
        IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<int, byte[]> slots, Func<string, string, byte[]?>? copy = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(slots);
        var onDisk = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, byte[]> file in files)
        {
            onDisk[file.Key] = file.Value;
        }

        var entries = new List<RestoreFileNow>();
        foreach (string name in journal.Files)
        {
            SaveFileHash? restored = backup?.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            SaveFileHash? was = before?.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            byte[]? bytes = onDisk.TryGetValue(name, out byte[]? found) ? found : null;
            RestoreFileState state = State(backup != null, restored, before != null, was, bytes == null ? null : SaveFileHash.Of(name, bytes));
            if (state == RestoreFileState.WrittenSince && bytes != null && copy != null &&
                string.Equals(name, ProfilePlayTime.ProfileFile, StringComparison.OrdinalIgnoreCase))
            {
                state = AsCopyApartFromPlayTime(restored, copy(journal.Backup, name), bytes) ? RestoreFileState.Restored
                    : AsCopyApartFromPlayTime(was, copy(journal.Before, name), bytes) ? RestoreFileState.AsBefore
                    : state;
            }

            entries.Add(new RestoreFileNow(name, state));
        }

        foreach (int slot in journal.Slots)
        {
            SaveFileHash? restored = backup?.CoopSlot(slot)?.AsFile();
            SaveFileHash? was = before?.CoopSlot(slot)?.AsFile();
            SaveFileHash? now = slots.TryGetValue(slot, out byte[]? bytes) && bytes != null ? CoopSlotHash.Of(slot, bytes).AsFile() : null;
            entries.Add(new RestoreFileNow(SlotLabel(slot), State(backup != null, restored, before != null, was, now)));
        }

        return new RestoreOnDisk(journal, backup != null && before != null, entries);
    }

    /// <summary><c>co-op slot N</c>, as the restore's log lines name a slot.</summary>
    public static string SlotLabel(int slot) => $"co-op slot {slot.ToString(CultureInfo.InvariantCulture)}";

    // A file as the restored backup holds it wins: a restore of a backup the same as the files it replaced is as restored.
    private static RestoreFileState State(bool backupKnown, SaveFileHash? restored, bool beforeKnown, SaveFileHash? was, SaveFileHash? now)
    {
        if (now == null)
        {
            return backupKnown && restored == null ? RestoreFileState.Restored
                : beforeKnown && was == null ? RestoreFileState.AsBefore
                : RestoreFileState.WrittenSince;
        }

        return restored != null && Same(restored, now) ? RestoreFileState.Restored
            : was != null && Same(was, now) ? RestoreFileState.AsBefore
            : RestoreFileState.WrittenSince;
    }

    private static bool AsCopyApartFromPlayTime(SaveFileHash? listed, byte[]? copy, byte[] now) =>
        listed != null && copy != null && Same(listed, SaveFileHash.Of(listed.Name, copy)) && ProfilePlayTime.SameApartFromPlayTime(copy, now);

    private static bool Same(SaveFileHash a, SaveFileHash b) => a.Bytes == b.Bytes && string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a recovery that puts files in place did about what was on disk first (<see cref="RecoveryKeep.Run"/>).</summary>
public enum RecoveryKeepKind
{
    /// <summary>Nothing was written since the restore stopped: no backup is taken.</summary>
    NotNeeded,

    /// <summary>What was on disk is in a backup (a new one, or the newest when it held exactly the same).</summary>
    Kept,

    /// <summary>It was needed and couldn't be taken (or the save folder couldn't be read to tell): the files are put in place anyway.</summary>
    Failed,
}

/// <summary>What a recovery kept first: how it went, the backup that holds what was on disk, or why none could be taken.</summary>
/// <param name="Kind">How it went.</param>
/// <param name="Backup">The backup's folder name (<see cref="RecoveryKeepKind.Kept"/>), else empty.</param>
/// <param name="Problem">Why none could be taken (<see cref="RecoveryKeepKind.Failed"/>), else empty.</param>
public sealed record RecoveryKept(RecoveryKeepKind Kind, string Backup, string Problem)
{
    /// <summary>No backup was needed.</summary>
    public static RecoveryKept None { get; } = new(RecoveryKeepKind.NotNeeded, string.Empty, string.Empty);

    /// <summary>What was on disk is in backup <paramref name="backup"/>.</summary>
    public static RecoveryKept In(string backup) => new(RecoveryKeepKind.Kept, backup ?? throw new ArgumentNullException(nameof(backup)), string.Empty);

    /// <summary>None could be taken, for <paramref name="problem"/> (worded to follow "couldn't be kept first").</summary>
    public static RecoveryKept Failure(string problem) => new(RecoveryKeepKind.Failed, string.Empty, string.IsNullOrEmpty(problem) ? "no backup was taken" : problem);
}

/// <summary>A recovery's writes: what was kept first, and the files and slots that couldn't be put in place.</summary>
/// <param name="Kept">What was kept first.</param>
/// <param name="Failed">The labels of those that couldn't be put in place (empty: all were).</param>
public sealed record RecoveryWrites(RecoveryKept Kept, IReadOnlyList<string> Failed);

/// <summary>
/// A restore the game stopped in the middle of loses nothing when its recovery comes late (D46, D137): before the recovery puts
/// any file in place (<see cref="RecoveryAction.Complete"/>, <see cref="RecoveryAction.RollBack"/>), what is on disk is backed up
/// when something wrote it since the restore stopped (<see cref="RestoreOnDisk.WrittenSince"/>), by the code a restore's own
/// "before" backup uses and with its reason (<see cref="RestoreParts.BeforeReason"/>, read on the Backups screen as a backup
/// taken before a restore). At the very next start nothing can have been written, so nothing is taken and the recovery is as
/// it always was. A backup that can't be taken doesn't stop the recovery: a journal left for ever would leave the saves half
/// restored for ever. Game-independent.
/// </summary>
public static class RecoveryKeep
{
    private const int MaxNames = 8;

    /// <summary>
    /// The reason the backup is kept with: that of a restore of <paramref name="journal"/>'s parts from the backup whose files are about
    /// to be put in place: its restored backup for <see cref="RecoveryAction.Complete"/>, the one taken before it for
    /// <see cref="RecoveryAction.RollBack"/> (an undo restores that one).
    /// </summary>
    public static string Reason(RestoreJournal journal, RecoveryAction action)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new RestoreParts(journal.Files.Count > 0, journal.Slots).BeforeReason(action == RecoveryAction.RollBack ? journal.Before : journal.Backup);
    }

    /// <summary>
    /// A recovery's writes in their order. When <paramref name="onDisk"/> shows a file written since the restore stopped,
    /// <paramref name="keep"/> first (a backup of what is there now); then <paramref name="putInPlace"/>, always and once, told what
    /// was kept (so it can say so first), whatever <paramref name="keep"/> gave: one that failed or threw is reported in the result,
    /// never a reason to stop. <paramref name="onDisk"/> null: the save folder couldn't be read (<paramref name="unread"/> says why),
    /// so nothing can be kept, and the files are put in place anyway.
    /// </summary>
    public static RecoveryWrites Run(RestoreOnDisk? onDisk, string unread, Func<RecoveryKept> keep, Func<RecoveryKept, IReadOnlyList<string>> putInPlace)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(putInPlace);
        RecoveryKept kept;
        if (onDisk == null)
        {
            kept = RecoveryKept.Failure(string.IsNullOrEmpty(unread) ? "the save folder couldn't be read" : unread);
        }
        else if (!onDisk.WrittenSince)
        {
            kept = RecoveryKept.None;
        }
        else
        {
            try
            {
                kept = keep() ?? RecoveryKept.Failure(string.Empty);
            }
            catch (Exception ex)
            {
                kept = RecoveryKept.Failure($"taking it failed: {SafeError.Text(ex)}");
            }
        }

        return new RecoveryWrites(kept, putInPlace(kept) ?? Array.Empty<string>());
    }

    /// <summary>
    /// The log's one line on what was kept before <paramref name="action"/> puts <paramref name="journal"/>'s files in place: nothing
    /// needed (INFO), what is there now kept in <c>backups/&lt;name&gt;</c> (INFO), or why it couldn't be (a WARN:
    /// <see cref="RecoveryKeepKind.Failed"/>). Backups by <see cref="SaveBackupNames.ForLog"/> (D129).
    /// </summary>
    public static string Line(RestoreJournal journal, RecoveryAction action, RestoreOnDisk? onDisk, RecoveryKept kept)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(kept);
        string restore = $"the restore of backups/{SaveBackupNames.ForLog(journal.Backup)}";
        string then = action == RecoveryAction.RollBack
            ? $"the restore is undone from backups/{SaveBackupNames.ForLog(journal.Before)}"
            : $"the restore is finished from backups/{SaveBackupNames.ForLog(journal.Backup)}";
        if (onDisk == null)
        {
            return $"Backups: the save folder couldn't be read to see what changed since {restore} stopped ({kept.Problem}); nothing could be kept first, and {then} anyway, so the saves aren't left half restored.";
        }

        if (kept.Kind == RecoveryKeepKind.NotNeeded)
        {
            string total = onDisk.Entries.Count.ToString(CultureInfo.InvariantCulture);
            return $"Backups: every file {restore} names is as that restore left it ({total} of {total} as restored or as before it), so nothing was written since it stopped and no backup is taken first.";
        }

        string changed = $"{Names(onDisk.Changed)} changed since {restore} stopped ({(onDisk.ManifestsRead ? "neither as restored nor as before it" : "as far as can be told: a backup's manifest can't be read")})";
        return kept.Kind == RecoveryKeepKind.Kept
            ? $"Backups: {changed}; what is there now is kept in backups/{SaveBackupNames.ForLog(kept.Backup)}, then {then}."
            : $"Backups: {changed}; what is there now couldn't be kept first ({kept.Problem}), and {then} anyway, so the saves aren't left half restored.";
    }

    /// <summary>
    /// The log's line (a WARN) for an all-moved restore whose files aren't as restored any more (<see cref="RestoreOnDisk.AllRestored"/>
    /// false): what follows its moves isn't done over them.
    /// </summary>
    public static string OvertakenLine(RestoreOnDisk onDisk)
    {
        ArgumentNullException.ThrowIfNull(onDisk);
        string backup = SaveBackupNames.ForLog(onDisk.Journal.Backup);
        IReadOnlyList<string> notRestored = onDisk.Entries.Where(e => e.State != RestoreFileState.Restored).Select(e => e.Label).ToList();
        return $"Backups: the restore of backups/{backup} was all in place when it stopped, but {Names(notRestored)} aren't as restored now " +
            "(written since, or Steam Cloud's copies put back by a start that couldn't finish it), so what follows it isn't done over them: " +
            $"the shared-progress ledger, Steam Cloud's list and the per-run counters stay as they are; backups/{backup} still holds the restored files.";
    }

    /// <summary>"profileV2.es3", "profileV2.es3 and co-op slot 2", the first few and "and N more".</summary>
    private static string Names(IReadOnlyList<string> labels)
    {
        List<string> shown = labels.Take(MaxNames).Select(l => SafeText.Clean(l, 48)).ToList();
        if (labels.Count > MaxNames)
        {
            shown.Add($"{(labels.Count - MaxNames).ToString(CultureInfo.InvariantCulture)} more");
        }

        return shown.Count == 0 ? "nothing" : Words.List(shown);
    }
}
