using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The files a restore put in place, by name and SHA-256: read back right after the move, and, when
/// the game must restart (your saves were restored), kept in <see cref="FileName"/> beside the restore journal, so the next
/// start can say they're still as restored. Text, one <c>key=value</c> per line:
/// <code>
/// backup=20261031-120000-host
/// file=profileV2.es3|&lt;sha256&gt;
/// slot=2|&lt;sha256&gt;
/// </code>
/// </summary>
public sealed class RestoreCheck
{
    /// <summary>The file's name, beside the restore journal.</summary>
    public const string FileName = "restore-check.txt";

    /// <summary>Most files and slots a check holds (more than a backup can).</summary>
    public const int MaxEntries = 64;

    private const int Sha256HexLength = 64;

    /// <summary>A check of <paramref name="files"/> and <paramref name="slots"/> restored from <paramref name="backup"/>.</summary>
    public RestoreCheck(string backup, IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> slots)
    {
        Backup = backup ?? throw new ArgumentNullException(nameof(backup));
        Files = files?.ToList() ?? throw new ArgumentNullException(nameof(files));
        Slots = slots?.ToList() ?? throw new ArgumentNullException(nameof(slots));
    }

    /// <summary>The backup's name.</summary>
    public string Backup { get; }

    /// <summary>The save files restored (their sizes aren't kept in the text: 0 when read from it).</summary>
    public IReadOnlyList<SaveFileHash> Files { get; }

    /// <summary>The co-op slots restored (their sizes aren't kept in the text: 0 when read from it).</summary>
    public IReadOnlyList<CoopSlotHash> Slots { get; }

    /// <summary>How many files and slots the check holds.</summary>
    public int Count => Files.Count + Slots.Count;

    /// <summary>The check as text.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("backup=").Append(Backup).Append('\n');
        foreach (SaveFileHash file in Files)
        {
            text.Append("file=").Append(file.Name).Append('|').Append(file.Sha256).Append('\n');
        }

        foreach (CoopSlotHash slot in Slots)
        {
            text.Append("slot=").Append(slot.Slot.ToString(CultureInfo.InvariantCulture)).Append('|').Append(slot.Sha256).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a check. False for anything that isn't one: a backup name that isn't one, a file name with a folder in it or that
    /// isn't a save file, a co-op slot that isn't one, a hash that isn't SHA-256 hex, an unknown or repeated key, too many entries.
    /// Never throws.
    /// </summary>
    public static bool TryParse(string? text, out RestoreCheck? check)
    {
        check = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? backup = null;
        var files = new List<SaveFileHash>();
        var slots = new List<CoopSlotHash>();
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
            if (key == "backup" && backup == null && SaveBackupManifest.IsPlainFileName(value) && SaveBackupNames.TryParse(value, out _, out _))
            {
                backup = value;
                continue;
            }

            int bar = value.LastIndexOf('|');
            string name = bar > 0 ? value[..bar] : string.Empty;
            string sha = bar > 0 ? value[(bar + 1)..] : string.Empty;
            if (!IsHash(sha) || files.Count + slots.Count >= MaxEntries)
            {
                return false;
            }

            if (key == "file" && SaveBackupManifest.IsPlainFileName(name) && SaveBackupNames.IsSaveFile(name) &&
                !files.Exists(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(new SaveFileHash(name, 0, sha));
            }
            else if (key == "slot" && name.Length == 1 && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int slot) &&
                SaveBackupManifest.IsCoopSlot(slot) && !slots.Exists(s => s.Slot == slot))
            {
                slots.Add(new CoopSlotHash(slot, 0, sha));
            }
            else
            {
                return false;
            }
        }

        if (backup == null)
        {
            return false;
        }

        check = new RestoreCheck(backup, files, slots);
        return true;
    }

    /// <summary>
    /// Compares each file and slot with what's there now; <paramref name="file"/> and <paramref name="slot"/> give its SHA-256 now
    /// (null when it's gone or can't be read).
    /// </summary>
    public RestoreReading Read(Func<string, string?> file, Func<int, string?> slot)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(slot);
        var differ = new List<string>();
        foreach (SaveFileHash entry in Files)
        {
            if (!Same(entry.Sha256, file(entry.Name)))
            {
                differ.Add(entry.Name);
            }
        }

        foreach (CoopSlotHash entry in Slots)
        {
            if (!Same(entry.Sha256, slot(entry.Slot)))
            {
                differ.Add($"co-op slot {entry.Slot.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return new RestoreReading(Backup, Count, differ);
    }

    private static bool Same(string expected, string? now) => now != null && string.Equals(expected, now, StringComparison.OrdinalIgnoreCase);

    private static bool IsHash(string text) => text.Length == Sha256HexLength && text.All(Uri.IsHexDigit);
}

/// <summary>
/// What a <see cref="RestoreCheck"/> found, and the log's words for it: the backup as <see cref="SaveBackupNames.ForLog"/> names it
/// (an older backup's local-time name as its UTC time, so no line holds a local time, D129).
/// </summary>
/// <param name="Backup">The backup the files came from.</param>
/// <param name="Total">How many files and slots were looked at.</param>
/// <param name="Differ">Those that aren't as restored.</param>
public sealed record RestoreReading(string Backup, int Total, IReadOnlyList<string> Differ)
{
    /// <summary>True when every one is as restored.</summary>
    public bool Same => Differ.Count == 0;

    /// <summary>Right after the move: "Backups: the restored files read back as in backups/...".</summary>
    public string ReadBackLine() =>
        $"Backups: the restored files read back as in backups/{LogBackup}: {Total - Differ.Count} of {Total} match its manifest (SHA-256){(Same ? "." : $"; DIFFER: {Names()}.")}";

    /// <summary>At the next start, before the game loads them.</summary>
    public string StartLine() => Same
        ? $"Backups: at this start the {Total} file(s) restored from backups/{LogBackup} last time are as restored."
        : $"Backups: {Differ.Count} of the {Total} file(s) restored from backups/{LogBackup} last time changed before this start: {Names()} (the game or Steam Cloud wrote them).";

    /// <summary>At that start's title screen, once the game has loaded them (through Steam Cloud, for the files it keeps there).</summary>
    public string TitleLine() => Same
        ? $"Backups: at the title screen the {Total} file(s) restored from backups/{LogBackup} are still as restored."
        : $"Backups: at the title screen {Differ.Count} of the {Total} file(s) restored from backups/{LogBackup} aren't as restored: {Names()} " +
          "(the game's start-up or Steam Cloud's load wrote them; the Steam Cloud lines before this say when that was meant).";

    private string LogBackup => SaveBackupNames.ForLog(Backup);

    private string Names() => string.Join(", ", Differ.Select(d => SafeText.Clean(d, 48)));
}
