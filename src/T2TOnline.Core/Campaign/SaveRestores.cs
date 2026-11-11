using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a restore puts back from a backup (D46): the player's save files (the game's save folder),
/// co-op slots, or both. A restore of the saves needs the game to restart (it holds the old profile in memory); a co-op
/// slot alone doesn't (the game holds no slot's run at an idle title screen outside a session). Restoring the saves never
/// rolls a co-op run back, and restoring a slot never touches the saves.
/// </summary>
public sealed class RestoreParts
{
    /// <summary>The player's save files and the co-op slots in <paramref name="slots"/> (each once, in order).</summary>
    public RestoreParts(bool saves, IEnumerable<int> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        Saves = saves;
        Slots = slots.Distinct().OrderBy(s => s).ToList();
    }

    /// <summary>The player's save files only (the restore of 0.6.x).</summary>
    public static RestoreParts SavesOnly { get; } = new(true, Array.Empty<int>());

    /// <summary>True when the save files are put back.</summary>
    public bool Saves { get; }

    /// <summary>The co-op slots put back, in order.</summary>
    public IReadOnlyList<int> Slots { get; }

    /// <summary>True when nothing is chosen.</summary>
    public bool IsEmpty => !Saves && Slots.Count == 0;

    /// <summary>True when the game must restart afterwards: the save files are in it.</summary>
    public bool NeedsRestart => Saves;

    /// <summary>
    /// True when <paramref name="manifest"/> holds every part chosen: save files for <see cref="Saves"/>, each slot in
    /// <see cref="Slots"/>; else false, with why (worded to follow "the backup wasn't restored: ").
    /// </summary>
    public bool TryCheck(SaveBackupManifest manifest, out string problem)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (IsEmpty)
        {
            problem = "nothing was chosen to restore";
            return false;
        }

        if (Saves && manifest.Files.Count == 0)
        {
            problem = "it holds no save files";
            return false;
        }

        foreach (int slot in Slots)
        {
            if (manifest.CoopSlot(slot) == null)
            {
                problem = $"it holds no co-op slot {slot.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>The parts in words: "your saves", "co-op slot 2", "your saves and co-op slots 1 and 3".</summary>
    public string Describe()
    {
        string slots = Slots.Count == 0 ? string.Empty
            : Slots.Count == 1 ? $"co-op slot {Slots[0].ToString(CultureInfo.InvariantCulture)}"
            : $"co-op slots {Words.List(Slots.Select(s => s.ToString(CultureInfo.InvariantCulture)))}";
        return Saves ? slots.Length == 0 ? "your saves" : $"your saves and {slots}" : slots.Length == 0 ? "nothing" : slots;
    }

    /// <summary>
    /// The reason kept with the backup of what a restore of these parts from <paramref name="backup"/> replaces:
    /// <c>before restoring &lt;backup&gt;</c> when the saves go back, <c>before restoring co-op slot 2 from &lt;backup&gt;</c> for
    /// slots alone. The Backups screen reads either as a backup taken before a restore (<see cref="BackupRows.ReasonOf"/>).
    /// </summary>
    public string BeforeReason(string backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return Saves ? $"before restoring {backup}" : $"before restoring {Describe()} from {backup}";
    }
}

/// <summary>What the next start does with a restore the game stopped in the middle of (<see cref="RestoreRecovery.Decide(RestoreJournal, SaveBackupManifest?, bool, SaveBackupManifest?, bool, bool)"/>).</summary>
public enum RecoveryAction
{
    /// <summary>Every file and slot was already in place (the journal says so): only what follows the moves is done again.</summary>
    Finish,

    /// <summary>The restored backup is whole and holds everything the journal names: the restore is finished from it.</summary>
    Complete,

    /// <summary>The restored backup can't be used, the one from before the restore can: the restore is undone from it.</summary>
    RollBack,

    /// <summary>Neither backup can be used: nothing is written, the player is told which backups to restore.</summary>
    Report,

    /// <summary>
    /// Nothing written now could stay (<see cref="RestoreRecovery.Decide(RestoreJournal, SaveBackupManifest?, bool, SaveBackupManifest?, bool, bool)"/>):
    /// the journal stays for a start that can keep restored files over Steam Cloud's copies.
    /// </summary>
    Wait,
}

/// <summary>
/// An interrupted restore (finished or undone, never left half done). The journal names the restored backup
/// and the one of the saves from just before; the next start finishes the restore (the player asked for it): with every
/// file already in place, only what follows; else from the restored backup when it's whole; else it puts the files from
/// before back from the second backup.
/// </summary>
public static class RestoreRecovery
{
    /// <summary>
    /// What to do with <paramref name="journal"/>'s restore: <see cref="RecoveryAction.Finish"/> when every file was in
    /// place (<see cref="RestoreJournal.Moved"/>); <see cref="RecoveryAction.Complete"/> when the restored backup
    /// (<paramref name="backup"/>, its files matching its manifest: <paramref name="backupWhole"/>) holds every file and
    /// slot the journal names; else <see cref="RecoveryAction.RollBack"/> when the backup from before
    /// (<paramref name="before"/>, <paramref name="beforeWhole"/>) is whole; else <see cref="RecoveryAction.Report"/>.
    /// </summary>
    public static RecoveryAction Decide(RestoreJournal journal, SaveBackupManifest? backup, bool backupWhole, SaveBackupManifest? before, bool beforeWhole)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Moved)
        {
            return RecoveryAction.Finish;
        }

        if (backup != null && backupWhole && Holds(backup, journal))
        {
            return RecoveryAction.Complete;
        }

        return before != null && beforeWhole ? RecoveryAction.RollBack : RecoveryAction.Report;
    }

    /// <summary>
    /// As <see cref="Decide(RestoreJournal, SaveBackupManifest?, bool, SaveBackupManifest?, bool)"/>, for a start that may not
    /// be able to keep restored files over Steam Cloud's copies: <paramref name="cloudLoadsHeld"/> is false when nothing holds
    /// the game's loads from Steam Cloud (the game's load of a cloud-saved file writes Steam's copy over the local one). A
    /// journal on disk means none of its restore reached Steam Cloud (the restore sends its files only after the journal
    /// goes), so Steam still holds the files from before it. Then a journal naming a cloud-saved file
    /// (<see cref="NamesCloudSavedFiles"/>) is undone from the backup taken just before it
    /// (<see cref="RecoveryAction.RollBack"/>: those are the files Steam holds, so the game's loads write the same bytes) when
    /// that backup is whole and the files weren't all moved yet; otherwise it waits (<see cref="RecoveryAction.Wait"/>) for a
    /// start that holds the loads. A journal of co-op slots or local-only files is decided as always.
    /// </summary>
    public static RecoveryAction Decide(RestoreJournal journal, SaveBackupManifest? backup, bool backupWhole, SaveBackupManifest? before, bool beforeWhole, bool cloudLoadsHeld)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (cloudLoadsHeld || !NamesCloudSavedFiles(journal))
        {
            return Decide(journal, backup, backupWhole, before, beforeWhole);
        }

        // All moved: what follows the moves (the shared-progress ledger back to the backup's time) may have run, which an undo
        // here couldn't take back.
        return !journal.Moved && before != null && beforeWhole ? RecoveryAction.RollBack : RecoveryAction.Wait;
    }

    /// <summary>True when <paramref name="journal"/> names a save file the game keeps in Steam Cloud (<see cref="SaveBackupNames.IsCloudSaved"/>).</summary>
    public static bool NamesCloudSavedFiles(RestoreJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.Files.Any(SaveBackupNames.IsCloudSaved);
    }

    /// <summary>
    /// For a roll-back: each file the journal names, and whether <paramref name="before"/> holds it (it's put back) or not
    /// (it wasn't there before the restore, which backs every existing file up first: it's deleted).
    /// </summary>
    public static IReadOnlyList<(string File, bool PutBack)> RollBackFiles(RestoreJournal journal, SaveBackupManifest before)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(before);
        return journal.Files.Select(f => (f, before.Files.Any(m => string.Equals(m.Name, f, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    /// <summary>For a roll-back: each co-op slot the journal names, and whether <paramref name="before"/> holds it (put back) or not (deleted).</summary>
    public static IReadOnlyList<(int Slot, bool PutBack)> RollBackSlots(RestoreJournal journal, SaveBackupManifest before)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(before);
        return journal.Slots.Select(s => (s, before.CoopSlot(s) != null)).ToList();
    }

    private static bool Holds(SaveBackupManifest manifest, RestoreJournal journal) =>
        journal.Files.All(f => manifest.Files.Any(m => string.Equals(m.Name, f, StringComparison.OrdinalIgnoreCase))) &&
        journal.Slots.All(s => manifest.CoopSlot(s) != null);
}

/// <summary>Where a restored file stands with Steam Cloud (<see cref="CloudPushList.Judge"/>).</summary>
public enum CloudPushState
{
    /// <summary>Not on the list: the game loads it from Steam Cloud as usual.</summary>
    NotListed,

    /// <summary>On the list, and the local file is still the restored one: it goes to Steam Cloud before the game loads it.</summary>
    Restored,

    /// <summary>On the list, but the local file changed since (the game saved it, which also sends it to Steam Cloud): off the list.</summary>
    ChangedSince,
}

/// <summary>What this PC's Steam says of its copy of a save file (<see cref="SteamCopy"/>).</summary>
public enum SteamCopyState : byte
{
    /// <summary>Not known: Steam isn't running for the game, cloud saves are off, or it wasn't asked (the default).</summary>
    Unavailable = 0,

    /// <summary>Steam has a copy, but it couldn't be read whole.</summary>
    Unreadable = 1,

    /// <summary>Steam has no copy of the file.</summary>
    None = 2,

    /// <summary>Steam has a copy: its hash and, when known, when it was saved.</summary>
    Present = 3,
}

/// <summary>
/// Steam Cloud's copy of a save file as this PC's Steam holds it (<c>Steamworks.SteamRemoteStorage</c>: what the game's
/// next load would write over the local file), at one moment: its state, and for one that's there its SHA-256 (uppercase
/// hex) and the time Steam gives it (UTC).
/// </summary>
/// <param name="State">What Steam says.</param>
/// <param name="Sha256">The copy's SHA-256, when <see cref="SteamCopyState.Present"/>.</param>
/// <param name="TimeUtc">When the copy was saved, by Steam's account, when known.</param>
public readonly record struct SteamCopy(SteamCopyState State, string? Sha256, DateTime? TimeUtc)
{
    /// <summary>Not known (see <see cref="SteamCopyState.Unavailable"/>).</summary>
    public static SteamCopy Unavailable => default;

    /// <summary>A copy that couldn't be read whole.</summary>
    public static SteamCopy Unreadable => new(SteamCopyState.Unreadable, null, null);

    /// <summary>No copy.</summary>
    public static SteamCopy NoCopy => new(SteamCopyState.None, null, null);

    /// <summary>A copy with <paramref name="sha256"/>, saved at <paramref name="timeUtc"/> (null: not known).</summary>
    public static SteamCopy Of(string sha256, DateTime? timeUtc)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        return new SteamCopy(SteamCopyState.Present, sha256.ToUpperInvariant(), timeUtc == null ? null : DateTime.SpecifyKind(timeUtc.Value, DateTimeKind.Utc));
    }

    /// <summary>For logs: "saved 2026-10-31 12:00:05 UTC", "none", "not readable", "not known".</summary>
    public string Describe() => State switch
    {
        SteamCopyState.Present => TimeUtc is DateTime at ? $"saved {CloudPushDecision.Utc(at)}" : "there (its time not known)",
        SteamCopyState.None => "none",
        SteamCopyState.Unreadable => "not readable",
        _ => "not known",
    };
}

/// <summary>
/// One restored file Steam Cloud must still get (<see cref="CloudPushList"/>), with what a later start needs to tell
/// whether Steam's copy then is still the one the restore replaced: the restore it came from and when, the
/// file's hash before the restore, and Steam's copy at the restore.
/// </summary>
/// <param name="File">The restored file: its name, size and hash.</param>
/// <param name="Backup">The backup it was restored from (a restore's files belong together).</param>
/// <param name="RestoredAtUtc">When it was restored (null: not known, a 0.7 list).</param>
/// <param name="BeforeSha256">Its hash before the restore; <see cref="NoFileBefore"/> when there was no file; null: not known.</param>
/// <param name="SteamAtRestore">Steam's copy at the restore.</param>
public sealed record CloudPushEntry(SaveFileHash File, string Backup, DateTime? RestoredAtUtc, string? BeforeSha256, SteamCopy SteamAtRestore)
{
    /// <summary><see cref="BeforeSha256"/> for a file that wasn't there before the restore.</summary>
    public const string NoFileBefore = "";

    /// <summary>The file's name.</summary>
    public string Name => File.Name;
}

/// <summary>
/// The restored save files Steam Cloud must still get (D46). The game loads every cloud-saved file through Steam
/// Cloud first, and that load writes Steam's copy over the local file (<c>SteamService.Load</c>): a restored
/// file whose push to Steam Cloud failed (or was lost by Steam's own sync) would be undone at the next start. So each
/// restored cloud-saved file is listed with its hash until it's in Steam Cloud: at the game's next load of it, while the
/// local file is still the restored one and Steam's copy is still the one the restore replaced
/// (<see cref="CloudPushDecision"/>), it's sent to Steam Cloud and the load keeps the local file. Kept beside the backups
/// as text: <c>backup=&lt;name&gt;</c>, then per file
/// <c>file=&lt;name&gt;|&lt;bytes&gt;|&lt;sha256&gt;|&lt;backup&gt;|&lt;restored at&gt;|&lt;before&gt;|&lt;Steam's hash&gt;|&lt;Steam's time&gt;</c>
/// (times as ISO 8601 UTC; <c>-</c> for not known, <c>none</c> for no file; 0.7's lines without the last five read with
/// nothing known). Reading one never throws.
/// </summary>
public sealed class CloudPushList
{
    /// <summary>The list's file name in the backups folder.</summary>
    public const string FileName = "steam-cloud-pending.txt";

    private const int MaxFiles = 256;
    private const string Unknown = "-";
    private const string NoneWord = "none";
    private const string UnreadableWord = "unreadable";

    /// <summary>The files of <paramref name="backup"/>'s restore that Steam Cloud must get, nothing else known (each name once; a later entry replaces an earlier).</summary>
    public CloudPushList(string backup, IEnumerable<SaveFileHash> files)
        : this(backup, (files ?? throw new ArgumentNullException(nameof(files))).Select(f => new CloudPushEntry(f, backup, null, null, SteamCopy.Unavailable)))
    {
    }

    /// <summary>The files Steam Cloud must get, the newest restore being <paramref name="backup"/>'s (each name once; a later entry replaces an earlier).</summary>
    public CloudPushList(string backup, IEnumerable<CloudPushEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(entries);
        Backup = backup;
        var byName = new Dictionary<string, CloudPushEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (CloudPushEntry entry in entries)
        {
            byName[entry.Name] = entry;
        }

        Entries = byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Files = Entries.Select(e => e.File).ToList();
    }

    /// <summary>The newest restore whose files are on the list.</summary>
    public string Backup { get; }

    /// <summary>The files, by name.</summary>
    public IReadOnlyList<SaveFileHash> Files { get; }

    /// <summary>The files with what's known of each one's restore, by name.</summary>
    public IReadOnlyList<CloudPushEntry> Entries { get; }

    /// <summary>True when nothing is left to send.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>The entry for <paramref name="name"/> (ignoring case), or null.</summary>
    public SaveFileHash? Find(string? name) => Entry(name)?.File;

    /// <summary>The entry for <paramref name="name"/> (ignoring case) with what's known of its restore, or null.</summary>
    public CloudPushEntry? Entry(string? name) =>
        string.IsNullOrEmpty(name) ? null : Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The list without <paramref name="name"/>.</summary>
    public CloudPushList Without(string name) =>
        new(Backup, Entries.Where(e => !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The list without <paramref name="names"/> (ignoring case): the files a restore just sent to Steam Cloud, which Steam
    /// uploads as the game closes. Listed on, a later start would send the restored file again over whatever Steam Cloud
    /// holds by then (another PC's newer save, or the copy picked in Steam's own conflict dialog).
    /// </summary>
    public CloudPushList Without(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var gone = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return new CloudPushList(Backup, Entries.Where(e => !gone.Contains(e.Name)));
    }

    /// <summary>This list with a newer restore's (<paramref name="newer"/>): its files replace these of the same name, the others stay.</summary>
    public CloudPushList Then(CloudPushList newer)
    {
        ArgumentNullException.ThrowIfNull(newer);
        return new CloudPushList(newer.Backup, Entries.Concat(newer.Entries));
    }

    /// <summary>The list with each entry replaced by <paramref name="map"/>'s (to fill in what an older list didn't keep).</summary>
    public CloudPushList Map(Func<CloudPushEntry, CloudPushEntry> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new CloudPushList(Backup, Entries.Select(map));
    }

    /// <summary>
    /// Where <paramref name="name"/> stands, its local file's SHA-256 being <paramref name="localSha256"/> (null: there's no
    /// local file, which counts as changed since).
    /// </summary>
    public CloudPushState Judge(string name, string? localSha256)
    {
        SaveFileHash? listed = Find(name);
        return listed == null ? CloudPushState.NotListed
            : localSha256 != null && string.Equals(listed.Sha256, localSha256, StringComparison.OrdinalIgnoreCase) ? CloudPushState.Restored
            : CloudPushState.ChangedSince;
    }

    /// <summary>
    /// True when any listed file is still as restored (<see cref="Judge"/>), its local file's SHA-256 read by
    /// <paramref name="localSha256"/> (null: no local file). Before the game's first load, such a file is one a load without
    /// the mod's guard would replace with Steam Cloud's copy; once none is left, that has happened (or the game saved them).
    /// </summary>
    public bool AnyAsRestored(Func<string, string?> localSha256)
    {
        ArgumentNullException.ThrowIfNull(localSha256);
        foreach (SaveFileHash file in Files)
        {
            if (Judge(file.Name, localSha256(file.Name)) == CloudPushState.Restored)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The list as text.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("backup=").Append(Backup).Append('\n');
        foreach (CloudPushEntry entry in Entries)
        {
            SaveFileHash file = entry.File;
            string steam = entry.SteamAtRestore.State switch
            {
                SteamCopyState.Present => entry.SteamAtRestore.Sha256 ?? Unknown,
                SteamCopyState.None => NoneWord,
                SteamCopyState.Unreadable => UnreadableWord,
                _ => Unknown,
            };
            text.Append("file=").Append(file.Name).Append('|').Append(file.Bytes.ToString(CultureInfo.InvariantCulture)).Append('|').Append(file.Sha256)
                .Append('|').Append(entry.Backup)
                .Append('|').Append(Time(entry.RestoredAtUtc))
                .Append('|').Append(entry.BeforeSha256 == null ? Unknown : entry.BeforeSha256.Length == 0 ? NoneWord : entry.BeforeSha256)
                .Append('|').Append(steam)
                .Append('|').Append(entry.SteamAtRestore.State == SteamCopyState.Present ? Time(entry.SteamAtRestore.TimeUtc) : Unknown)
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a list. False for anything that isn't one: no backup, or one that isn't a backup's name, a file that isn't a
    /// bare save file's name, a bad size, hash or time, an unknown or repeated key, too many files. Never throws.
    /// </summary>
    public static bool TryParse(string? text, out CloudPushList? list)
    {
        list = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? backup = null;
        var lines = new List<string[]>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return false;
            }

            string key = line[..equals];
            string value = line[(equals + 1)..];
            if (key == "backup" && backup == null && IsBackupName(value))
            {
                backup = value;
                continue;
            }

            if (key != "file" || lines.Count >= MaxFiles)
            {
                return false;
            }

            string[] parts = value.Split('|');
            if ((parts.Length != 3 && parts.Length != 8) || !SaveBackupManifest.IsPlainFileName(parts[0]) || !SaveBackupNames.IsSaveFile(parts[0]) ||
                lines.Any(l => string.Equals(l[0], parts[0], StringComparison.OrdinalIgnoreCase)) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) || !IsSha(parts[2]))
            {
                return false;
            }

            lines.Add(parts);
        }

        if (backup == null)
        {
            return false;
        }

        var entries = new List<CloudPushEntry>();
        foreach (string[] parts in lines)
        {
            var file = new SaveFileHash(parts[0], long.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture), parts[2].ToUpperInvariant());
            if (parts.Length == 3)
            {
                entries.Add(new CloudPushEntry(file, backup, null, null, SteamCopy.Unavailable)); // a 0.7 list: nothing more known
                continue;
            }

            if (!IsBackupName(parts[3]) || !TryTime(parts[4], out DateTime? restoredAt) || !TryHash(parts[5], out string? before) ||
                !TrySteam(parts[6], parts[7], out SteamCopy steam))
            {
                return false;
            }

            entries.Add(new CloudPushEntry(file, parts[3], restoredAt, before, steam));
        }

        list = new CloudPushList(backup, entries);
        return true;
    }

    private static bool IsBackupName(string name) => SaveBackupManifest.IsPlainFileName(name) && SaveBackupNames.TryParse(name, out _, out _);

    private static bool IsSha(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    private static string Time(DateTime? utc) => utc is DateTime at ? DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture) : Unknown;

    private static bool TryTime(string text, out DateTime? utc)
    {
        utc = null;
        if (text == Unknown)
        {
            return true;
        }

        if (!DateTime.TryParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at) || at.Kind != DateTimeKind.Utc)
        {
            return false;
        }

        utc = at;
        return true;
    }

    private static bool TryHash(string text, out string? sha)
    {
        sha = text == Unknown ? null : text == NoneWord ? CloudPushEntry.NoFileBefore : text.ToUpperInvariant();
        return text == Unknown || text == NoneWord || IsSha(text);
    }

    private static bool TrySteam(string hash, string time, out SteamCopy steam)
    {
        steam = SteamCopy.Unavailable;
        if (!TryTime(time, out DateTime? at))
        {
            return false;
        }

        switch (hash)
        {
            case Unknown:
                return at == null;
            case NoneWord:
                steam = SteamCopy.NoCopy;
                return at == null;
            case UnreadableWord:
                steam = SteamCopy.Unreadable;
                return at == null;
            default:
                if (!IsSha(hash))
                {
                    return false;
                }

                steam = SteamCopy.Of(hash, at);
                return true;
        }
    }
}

/// <summary>What a later start does with one listed restored file (<see cref="CloudPushDecision"/>).</summary>
public enum CloudPushVerdict
{
    /// <summary>Send the restored file to Steam Cloud, and keep it at the game's load: Steam holds what the restore replaced (or can't be read now).</summary>
    Push,

    /// <summary>Keep the restored file at the game's load, but don't send it (Steam's copy couldn't be read): it stays listed for the next start.</summary>
    KeepLocal,

    /// <summary>Steam Cloud already holds the restored bytes: off the list, and the game's load goes on (it writes the same bytes).</summary>
    AlreadyThere,

    /// <summary>Another device saved since: the whole restore yields, off the list, and the game's load writes Steam's copy (the restored one stays in its backup).</summary>
    Yield,
}

/// <summary>A verdict on a listed file, with why in words (worded to follow the file's name and a colon).</summary>
/// <param name="Verdict">What to do.</param>
/// <param name="Why">Why, for the log.</param>
public readonly record struct CloudPushChoice(CloudPushVerdict Verdict, string Why);

/// <summary>
/// Whether a restored file still waiting for Steam Cloud may be sent over Steam's copy.
/// The list exists for a push that failed (or was lost by Steam's sync), so Steam's copy should be the one the restore
/// replaced; when another device saved since, sending the restored file would overwrite that newer progress, which no
/// backup on this PC holds. Rules, in order: Steam can't be read now: push (as before: it fails while Steam is off, and
/// stays listed); Steam has a copy it can't read: keep the local file, don't send; Steam has no copy: push; Steam's copy
/// is the restored file: off the list; it's the file from before the restore, or Steam's own copy at the restore: push;
/// it's no newer than Steam's copy at the restore, or (that time not known) older than the restore by
/// <see cref="ClockMargin"/>: push; otherwise (or nothing known to tell): yield, and a restore yields whole (a profile
/// and its stats belong together). Game-independent.
/// </summary>
public static class CloudPushDecision
{
    /// <summary>How much older than the restore Steam's copy must be when Steam's own copy at the restore isn't known (two PCs' clocks differ a little).</summary>
    public static readonly TimeSpan ClockMargin = TimeSpan.FromMinutes(5);

    /// <summary>The verdict on <paramref name="entry"/>, Steam's copy now being <paramref name="steamNow"/>.</summary>
    public static CloudPushChoice Decide(CloudPushEntry entry, SteamCopy steamNow)
    {
        ArgumentNullException.ThrowIfNull(entry);
        switch (steamNow.State)
        {
            case SteamCopyState.Unavailable:
                return new CloudPushChoice(CloudPushVerdict.Push, "Steam Cloud can't be read now (Steam isn't running for the game, or cloud saves are off), so the restored file is kept and sent once Steam takes it");
            case SteamCopyState.Unreadable:
                return new CloudPushChoice(CloudPushVerdict.KeepLocal, "Steam Cloud has a copy this PC couldn't read, so the restored file is kept here and not sent over it; it's checked again at the next start");
            case SteamCopyState.None:
                return new CloudPushChoice(CloudPushVerdict.Push, "Steam Cloud has no copy of it");
        }

        string now = steamNow.Sha256 ?? string.Empty;
        if (string.Equals(now, entry.File.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CloudPushChoice(CloudPushVerdict.AlreadyThere, "Steam Cloud already holds the restored copy");
        }

        if (entry.BeforeSha256 is { Length: > 0 } before && string.Equals(now, before, StringComparison.OrdinalIgnoreCase))
        {
            return new CloudPushChoice(CloudPushVerdict.Push, "Steam Cloud still holds the copy the restore replaced");
        }

        SteamCopy then = entry.SteamAtRestore;
        if (then.State == SteamCopyState.Present && string.Equals(now, then.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CloudPushChoice(CloudPushVerdict.Push, "Steam Cloud still holds the copy it had at the restore");
        }

        DateTime? saved = steamNow.TimeUtc;
        if (then.State == SteamCopyState.Present && then.TimeUtc is DateTime thenAt && saved is DateTime notNewer && notNewer <= thenAt)
        {
            return new CloudPushChoice(CloudPushVerdict.Push, $"Steam Cloud's copy (saved {Utc(notNewer)}) is no newer than the one it held at the restore (saved {Utc(thenAt)})");
        }

        bool thenTimeKnown = then.State == SteamCopyState.Present && then.TimeUtc != null;
        if (!thenTimeKnown && entry.RestoredAtUtc is DateTime restoredAt && saved is DateTime older && older < restoredAt - ClockMargin)
        {
            return new CloudPushChoice(CloudPushVerdict.Push, $"Steam Cloud's copy was saved {Utc(older)}, before the restore at {Utc(restoredAt)}");
        }

        string when = saved is DateTime at ? $"saved {Utc(at)}" : "when it was saved isn't known";
        string restore = entry.RestoredAtUtc is DateTime r ? $"the restore at {Utc(r)}" : "the restore (its time isn't known)";
        return new CloudPushChoice(CloudPushVerdict.Yield, $"Steam Cloud has a copy ({when}) that isn't the one the restore replaced, so another device saved it after {restore}");
    }

    /// <summary>
    /// Every entry's verdict, Steam's copies being what <paramref name="steamNow"/> reads (once per file), by name (ignoring
    /// case). A restore yields whole: when one of its files (same backup) yields, all of them do.
    /// </summary>
    public static IReadOnlyDictionary<string, CloudPushChoice> DecideAll(IEnumerable<CloudPushEntry> entries, Func<string, SteamCopy> steamNow)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(steamNow);
        List<CloudPushEntry> all = entries.ToList();
        var choices = new Dictionary<string, CloudPushChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (CloudPushEntry entry in all)
        {
            choices[entry.Name] = Decide(entry, steamNow(entry.Name));
        }

        foreach (IGrouping<string, CloudPushEntry> restore in all.GroupBy(e => e.Backup, StringComparer.OrdinalIgnoreCase))
        {
            CloudPushEntry? first = restore.FirstOrDefault(e => choices[e.Name].Verdict == CloudPushVerdict.Yield);
            if (first == null)
            {
                continue;
            }

            foreach (CloudPushEntry entry in restore.Where(e => choices[e.Name].Verdict != CloudPushVerdict.Yield).ToList())
            {
                choices[entry.Name] = new CloudPushChoice(CloudPushVerdict.Yield,
                    $"{first.Name}, restored with it from backups/{SaveBackupNames.ForLog(restore.Key)}, has a newer copy in Steam Cloud, and a restore's files belong together");
            }
        }

        return choices;
    }

    /// <summary>"2026-10-31 12:00:05 UTC".</summary>
    public static string Utc(DateTime time) =>
        DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>Small wording helpers for the save lines.</summary>
internal static class Words
{
    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string List(IEnumerable<string> items)
    {
        List<string> all = items.ToList();
        return all.Count switch
        {
            0 => string.Empty,
            1 => all[0],
            _ => $"{string.Join(", ", all.Take(all.Count - 1))} and {all[^1]}",
        };
    }
}
