using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Lobby;

/// <summary>How a file guarded by a check tour differs at its end from its start.</summary>
public enum UiCheckFileChangeKind
{
    /// <summary>Its bytes differ.</summary>
    Changed,

    /// <summary>It was there at the start and isn't now.</summary>
    Gone,

    /// <summary>It wasn't there at the start.</summary>
    New,
}

/// <summary>One file that differs.</summary>
/// <param name="Label">The file as the summary names it (a label, never a full path).</param>
/// <param name="Kind">How it differs.</param>
public sealed record UiCheckFileChange(string Label, UiCheckFileChangeKind Kind)
{
    /// <inheritdoc />
    public override string ToString() => $"{Label} ({Kind switch { UiCheckFileChangeKind.Gone => "gone", UiCheckFileChangeKind.New => "new", _ => "changed" }})";
}

/// <summary>
/// The files a check tour hashes at its start and compares at its end: the game's own saves (<see cref="HashesSave"/>, and
/// the files of its save folders outside Easy Save, <see cref="HashesSaveIn"/>), the mod's data folder (<c>%LOCALAPPDATA%\T2T Online</c>) but its backups, whose folder names are compared instead, and its
/// read-me; and which hashes the summary may print (the co-op slots', game save data; never the progress ledger's or the
/// host key's). Paths here are relative to the data folder, with either separator.
/// </summary>
public static class UiCheckFiles
{
    /// <summary>The data folder's backups: never hashed (a tour takes and restores none); their folder names are compared.</summary>
    public const string BackupsFolder = "backups";

    // The game's save files, and the copy Easy Save keeps of each (ES3.CreateBackup) and loads when the save itself won't.
    private static readonly string[] SaveEndings = { ".es3", ".es3.bac" };

    // Steam's own marker in each folder it syncs by Auto-Cloud: not the game's file, and Steam may rewrite it at any time.
    private const string SteamCloudMarker = "steam_autocloud.vdf";

    private static readonly string[] SaveFolderNames = { "BracketData", "TeamDataModels" };

    /// <summary>
    /// The data folder's top-level folders a tour doesn't hash: the backups, and (only met when the data folder falls back
    /// to the game's <c>UserData\T2TOnline</c>) the tour's own output, the logs, the reports and a friend's copy of the
    /// host's run.
    /// </summary>
    private static readonly string[] SkippedFolders = { BackupsFolder, "dev", "logs", "reports", "campaign" };

    /// <summary>The data folder's top-level files a tour doesn't hash: the read-me, written at start-up.</summary>
    private static readonly string[] SkippedFiles = { "README.txt" };

    /// <summary>The folders whose files' hashes the summary prints: the co-op slots and Easy Save's copies of them.</summary>
    private static readonly string[] PrintedFolders = { "coop" };

    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>
    /// True when the file named <paramref name="fileName"/> in the game's save folder is hashed: a save (<c>*.es3</c>) or
    /// Easy Save's backup copy of one (<c>*.es3.bac</c>), any case. Never the copies a refused commit leaves
    /// (<c>*.es3.tmp</c>, which the game never reads), the game's logs or anything else there.
    /// </summary>
    public static bool HashesSave(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        foreach (string ending in SaveEndings)
        {
            if (fileName.Length > ending.Length && fileName.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The folders of the game's save folder that hold saves outside Easy Save: the tournament mode's files
    /// (<c>BracketData</c>) and each custom team, skater and goalie as its own JSON file (<c>TeamDataModels</c>). The save
    /// guard refuses Easy Save's writes and the Steam service's only, so the compare is what would show a tour that wrote
    /// one of these. Not <c>CustomLogos</c> (the player's own pictures, which the game only reads).
    /// </summary>
    public static IReadOnlyList<string> SaveFolders => SaveFolderNames;

    /// <summary>
    /// True when the file named <paramref name="fileName"/> in <paramref name="folder"/>, one of the game's save folder's
    /// own folders, is hashed: every file of <see cref="SaveFolders"/>, any case, but Steam's Auto-Cloud marker
    /// (<c>steam_autocloud.vdf</c>, which Steam rewrites by itself).
    /// </summary>
    public static bool HashesSaveIn(string? folder, string? fileName) =>
        !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(fileName)
        && Contains(SaveFolderNames, folder)
        && !string.Equals(fileName, SteamCloudMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the data folder's top-level folder <paramref name="name"/> is hashed.</summary>
    public static bool HashesFolder(string name) => !Contains(SkippedFolders, name);

    /// <summary>True when the file at <paramref name="relativePath"/> in the data folder is hashed.</summary>
    public static bool HashesFile(string relativePath)
    {
        string[] parts = Split(relativePath);
        if (parts.Length == 0)
        {
            return false;
        }

        return parts.Length == 1 ? !Contains(SkippedFiles, parts[0]) : HashesFolder(parts[0]);
    }

    /// <summary>True when the summary may print the hash of the data folder's file at <paramref name="relativePath"/>.</summary>
    public static bool PrintsHash(string relativePath)
    {
        string[] parts = Split(relativePath);
        return parts.Length > 1 && Contains(PrintedFolders, parts[0]);
    }

    /// <summary>The files that differ between <paramref name="before"/> and <paramref name="after"/> (label to hash), by label.</summary>
    public static List<UiCheckFileChange> Compare(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<UiCheckFileChange>();
        foreach (KeyValuePair<string, string> file in before)
        {
            if (!after.TryGetValue(file.Key, out string? now))
            {
                changes.Add(new UiCheckFileChange(file.Key, UiCheckFileChangeKind.Gone));
            }
            else if (!string.Equals(now, file.Value, StringComparison.Ordinal))
            {
                changes.Add(new UiCheckFileChange(file.Key, UiCheckFileChangeKind.Changed));
            }
        }

        foreach (string label in after.Keys)
        {
            if (!before.ContainsKey(label))
            {
                changes.Add(new UiCheckFileChange(label, UiCheckFileChangeKind.New));
            }
        }

        changes.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return changes;
    }

    /// <summary>The names in one list and not the other: "+name" for one added, "-name" for one gone, sorted.</summary>
    public static List<string> NameChanges(IEnumerable<string> before, IEnumerable<string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var was = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        var now = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();
        foreach (string name in now)
        {
            if (!was.Contains(name))
            {
                changes.Add("+" + name);
            }
        }

        foreach (string name in was)
        {
            if (!now.Contains(name))
            {
                changes.Add("-" + name);
            }
        }

        changes.Sort(StringComparer.OrdinalIgnoreCase);
        return changes;
    }

    /// <summary>
    /// The backups' folders in one list and not the other (<see cref="NameChanges"/>: "+" added, "-" gone), each named as a
    /// log names a backup (<see cref="SaveBackupNames.ForLog"/>): a UTC name as it is, an old local-time name as the UTC
    /// time it stands for, any other folder as one T2T Online didn't name. So the line holds no local time and no name a
    /// player gave a folder. Old names are read by <paramref name="clock"/> (null: this PC's zone).
    /// </summary>
    public static List<string> BackupChanges(IEnumerable<string> before, IEnumerable<string> after, BackupClock? clock = null)
    {
        List<string> changes = NameChanges(before, after);
        for (int i = 0; i < changes.Count; i++)
        {
            changes[i] = changes[i][0] + SaveBackupNames.ForLog(changes[i][1..], clock);
        }

        return changes;
    }

    private static string[] Split(string relativePath) =>
        (relativePath ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static bool Contains(string[] names, string name)
    {
        foreach (string candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
