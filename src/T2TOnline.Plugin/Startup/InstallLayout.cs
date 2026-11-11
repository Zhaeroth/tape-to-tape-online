using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace T2TOnline.Plugin.Startup;

// The install check's decision (D125). Plain .NET only: it runs first in the plugin's constructor, before anything loads
// T2TOnline.Core (which may be another build), and this file is linked into the Core tests, which check every case on an
// in-memory folder tree. The facts come from Startup/InstallCheck.cs.

/// <summary>The three kinds of folder MelonLoader 0.7.3 loads from, in its order: libraries, then plugins, then mods.</summary>
internal enum MelonPhase
{
    /// <summary>Every managed DLL is loaded (a library).</summary>
    UserLibs,

    /// <summary>DLLs with <c>MelonInfo</c> are loaded as plugins.</summary>
    Plugins,

    /// <summary>DLLs with <c>MelonInfo</c> are loaded as mods.</summary>
    Mods,
}

/// <summary>What the install check found.</summary>
internal enum InstallOutcome
{
    /// <summary>One build, one copy: nothing to do.</summary>
    Clean,

    /// <summary>The build that runs is whole, but other files of T2T Online are where MelonLoader looks (or at the game folder's root): online stays off until they're deleted.</summary>
    Leftovers,

    /// <summary>T2T Online's library or Steam assembly isn't the plugin's build (or isn't there): the plugin stays inert.</summary>
    Mixed,
}

/// <summary>
/// One folder MelonLoader looks in: its <see cref="Phase"/>; <see cref="Top"/>, the Melon folder it belongs to (a folder
/// right under <c>Mods</c>, <c>Plugins</c> or <c>UserLibs</c> that holds <c>manifest.json</c>), or null for those three
/// root folders; <see cref="Scanned"/>, false for a Melon folder while MelonLoader's <c>disable_subfolder_load</c> is set.
/// </summary>
internal sealed record MelonFolder(string Path, MelonPhase Phase, string? Top, bool Scanned);

/// <summary>A DLL's identity read from its file without loading it: its assembly name, version and build (the informational version, "0.11.0-beta+&lt;commit&gt;").</summary>
internal sealed record AssemblyFacts(string Name, Version Version, string? Build);

/// <summary>One of T2T Online's assemblies as this process has it: its file (null when it isn't loaded at all), version and build.</summary>
internal sealed record LoadedPart(string Name, string? Location, Version? Version, string? Build);

/// <summary>
/// What the check decides on. <see cref="BaseDirectory"/> is MelonLoader's base folder (the game folder); the two settings are
/// MelonLoader's (<c>disable_subfolder_load</c>, <c>disable_subfolder_manifest</c>); <see cref="Core"/> and <see cref="Steam"/>
/// are null when the process hasn't loaded them; the wanted versions are the plugin's references.
/// </summary>
internal sealed record InstallFacts(
    string BaseDirectory,
    bool SubfoldersOff,
    bool ManifestNotNeeded,
    LoadedPart Plugin,
    LoadedPart? Core,
    LoadedPart? Steam,
    Version? CoreWanted,
    Version? SteamWanted);

/// <summary>A file or folder the player is asked to delete: as the game folder shows it, and as the log may write it (rule: no player-made names in logs).</summary>
internal sealed record InstallEntry(string Path, string ForLog);

/// <summary>
/// The check's answer. <see cref="Entries"/>: what to delete (leftovers), or the files of another build (mixed), each a path
/// relative to the game folder with <c>\</c>, a folder ending in <c>\</c>. <see cref="Problems"/>: why a mixed install is mixed, for
/// the log. <see cref="LoadedFrom"/>: the folder the plugin was loaded from, for the log.
/// </summary>
internal sealed record InstallVerdict(
    InstallOutcome Outcome,
    IReadOnlyList<InstallEntry> Entries,
    IReadOnlyList<string> Problems,
    string LoadedFrom,
    bool Flat,
    bool SubfoldersOff,
    int Unreadable);

/// <summary>
/// The folders and files the check reads (the plugin's is the real disk, the tests' a dictionary). Paths are full paths
/// made with <see cref="Path.Combine(string, string)"/>. A listing that fails returns null; a folder that isn't there, an
/// empty list. Never throws.
/// </summary>
internal interface IInstallTree
{
    /// <summary>The folders directly inside <paramref name="path"/>, or null when it can't be listed.</summary>
    IReadOnlyList<string>? Directories(string path);

    /// <summary>The files directly inside <paramref name="path"/>, or null when it can't be listed.</summary>
    IReadOnlyList<string>? Files(string path);

    /// <summary>True when the file exists.</summary>
    bool FileExists(string path);

    /// <summary>A DLL's identity, or null when it isn't a readable .NET assembly.</summary>
    AssemblyFacts? Assembly(string path);

    /// <summary>Up to <paramref name="max"/> bytes from the start of a file, or null when it can't be read.</summary>
    byte[]? Head(string path, int max);
}

/// <summary>
/// The install check's rules (D125): MelonLoader 0.7.3's folder rules (<c>MelonFolderHandler.ScanForFolders</c>,
/// <c>FindSubFolders</c>, <c>AddFolder</c>) ported, T2T Online's files found in every folder they name, and the outcome:
/// <see cref="InstallOutcome.Mixed"/> when the library or Steam assembly isn't the plugin's build, else
/// <see cref="InstallOutcome.Leftovers"/> when any other file of T2T Online is there, else <see cref="InstallOutcome.Clean"/>.
/// </summary>
internal static class InstallLayout
{
    /// <summary>The plugin's assembly name.</summary>
    public const string PluginName = "T2TOnline";

    /// <summary>The library's assembly name.</summary>
    public const string CoreName = "T2TOnline.Core";

    /// <summary>The Steam assembly's name.</summary>
    public const string SteamName = "T2TOnline.Steam";

    /// <summary>The longest a list of files is on the game's prompt: the rest are counted.</summary>
    public const int ScreenEntries = 8;

    /// <summary>The longest one path is on the game's prompt: a longer one keeps its end (the file's name).</summary>
    public const int ScreenPathLength = 64;

    // How many files a second copy's folder may hold for it to be named as one folder (more, and each file is named).
    private const int CollapseLimit = 64;

    // How a log line names the game folder (the session log scrubber's label for it).
    private const string GameFolderLabel = "<game folder>";

    // What a root document of 0.10.0 Beta is: its SHA-256 with any carriage return taken out (both zips carry them with LF only).
    private static readonly Dictionary<string, string> OldRootDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LICENSE"] = "232e8ccfbe998660924b5cbcdf1a771954c876fc3769abaca79761e1136be070",
        ["THIRD_PARTY_NOTICES.md"] = "1b828a5fba3921e02f96d971f52df50a99532f5ef92355be043fdcf16b48ace3",
    };

    // What earlier releases (0.5.x) put at the game folder's root, found by name: nothing else is called that.
    private static readonly string[] OldRootFiles = { "T2T Online - Install.cmd", "T2T Online - INSTALL.txt" };

    // A Melon folder of T2T Online holds these besides the assemblies (the zip's documents, and 0.10.0's names for them).
    private static readonly HashSet<string> PackageDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "LICENSE", "THIRD_PARTY_NOTICES.md", "T2T Online - READ ME FIRST.txt",
    };

    // The folder names a log may write as they are: MelonLoader's and the zip's. Any other was named by the player.
    private static readonly HashSet<string> KnownFolders = new(StringComparer.OrdinalIgnoreCase) { "Mods", "Plugins", "UserLibs", PluginName };

    // The characters the game's prompt fonts draw (NotoSans Black, SemiBold and SemiBoldItalic have every printable ASCII
    // character but these); anything else in a path is shown as '?'.
    private const string Undrawable = "$@^`|~";

    /// <summary>The DLL file names of T2T Online's three assemblies.</summary>
    public static IReadOnlyList<string> DllNames { get; } = new[] { PluginName + ".dll", CoreName + ".dll", SteamName + ".dll" };

    /// <summary>The debug symbols' file names.</summary>
    public static IReadOnlyList<string> PdbNames { get; } = new[] { PluginName + ".pdb", CoreName + ".pdb", SteamName + ".pdb" };

    /// <summary>
    /// Every folder MelonLoader 0.7.3 would look in under <paramref name="baseDirectory"/>, in its order: the root
    /// <c>UserLibs</c>, <c>Plugins</c> and <c>Mods</c>, then each Melon folder below them as it finds them. Melon folders are
    /// listed even when <paramref name="subfoldersOff"/> (they're then not <see cref="MelonFolder.Scanned"/>), so a copy in
    /// one is still found. A folder that can't be listed is counted in <paramref name="unreadable"/>.
    /// </summary>
    public static IReadOnlyList<MelonFolder> Folders(IInstallTree tree, string baseDirectory, bool subfoldersOff, bool manifestNotNeeded, out int unreadable)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<MelonFolder> folders = Scan(tree, baseDirectory, subfoldersOff, manifestNotNeeded, failed);
        unreadable = failed.Count;
        return folders;
    }

    /// <summary>0.10.0 Beta's two documents at the game folder's root, by name: the SHA-256 of each without carriage returns.</summary>
    public static IReadOnlyDictionary<string, string> OldRootDocumentHashes => OldRootDocuments;

    /// <summary>The check's answer for <paramref name="facts"/> on <paramref name="tree"/>. Never throws for what the tree gives it.</summary>
    public static InstallVerdict Decide(IInstallTree tree, InstallFacts facts) => Decide(tree, facts, OldRootDocuments);

    /// <summary>The same, with the old root documents' hashes given (<see cref="OldRootDocumentHashes"/> in the game).</summary>
    public static InstallVerdict Decide(IInstallTree tree, InstallFacts facts, IReadOnlyDictionary<string, string> oldRootDocuments)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(oldRootDocuments);
        string root = Full(facts.BaseDirectory);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<MelonFolder> folders = Scan(tree, root, facts.SubfoldersOff, facts.ManifestNotNeeded, failed);
        List<OurFile> ours = FindOurs(tree, folders, failed);
        int unreadable = failed.Count;
        var names = new LogNames();

        MelonFolder? pluginFolder = facts.Plugin.Location == null ? null : FolderOf(folders, facts.Plugin.Location);
        string loadedFrom = facts.Plugin.Location == null ? "an unknown place" : names.Folder(Relative(root, Path.GetDirectoryName(facts.Plugin.Location) ?? root));
        bool flat = pluginFolder is { Top: null };

        // Mixed first: it's decided from what the process loaded, whatever the folders hold.
        var problems = new List<string>();
        CheckPart(facts, facts.Core, facts.CoreWanted, CoreName, root, names, problems);
        CheckPart(facts, facts.Steam, facts.SteamWanted, SteamName, root, names, problems);
        if (problems.Count > 0)
        {
            var other = new List<string>();
            foreach (OurFile file in ours.Where(f => f.Facts != null && !SameBuild(facts.Plugin, f.Facts)))
            {
                other.Add(file.Path);
                other.AddRange(ours.Where(p => p.Pdb && SameStem(p.Path, file.Path)).Select(p => p.Path));
            }

            foreach (LoadedPart? part in new[] { facts.Core, facts.Steam })
            {
                if (part?.Location != null && !SameBuild(facts.Plugin, part))
                {
                    other.Add(part.Location);
                }
            }

            return new InstallVerdict(InstallOutcome.Mixed, Entries(root, other, ours, names), problems, loadedFrom, flat, facts.SubfoldersOff, unreadable);
        }

        // The build is whole: everything of T2T Online that isn't what runs (or its debug symbols) is left over.
        string? home = pluginFolder?.Top;
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (facts.Plugin.Location != null)
        {
            kept.Add(Full(facts.Plugin.Location));
        }

        foreach ((LoadedPart? part, string name) in new[] { (facts.Core, CoreName), (facts.Steam, SteamName) })
        {
            OurFile? preferred = ours.FirstOrDefault(f => !f.Pdb && f.Folder.Phase == MelonPhase.UserLibs && f.Folder.Scanned &&
                string.Equals(f.Folder.Top, home, StringComparison.OrdinalIgnoreCase) &&
                f.Facts != null && string.Equals(f.Facts.Name, name, StringComparison.OrdinalIgnoreCase) && SameBuild(facts.Plugin, f.Facts));
            string? keep = preferred?.Path ?? part?.Location;
            if (keep != null)
            {
                kept.Add(Full(keep));
            }
        }

        var leftovers = new List<string>();
        foreach (OurFile file in ours)
        {
            bool keptFile = file.Pdb ? kept.Any(dll => SameStem(dll, file.Path)) : kept.Contains(file.Path);
            if (!keptFile)
            {
                leftovers.Add(file.Path);
            }
        }

        leftovers.AddRange(OldRootFilesIn(tree, root, oldRootDocuments));
        leftovers = Collapse(tree, root, leftovers, ours, kept);
        return new InstallVerdict(leftovers.Count > 0 ? InstallOutcome.Leftovers : InstallOutcome.Clean, Entries(root, leftovers, ours, names),
            Array.Empty<string>(), loadedFrom, flat, facts.SubfoldersOff, unreadable);
    }

    /// <summary>The one line the log gets about the install (rule: relative paths, no player-made folder names).</summary>
    public static string LogLine(InstallVerdict verdict, string release)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var line = new StringBuilder("Install check: ");
        string files = string.Join(", ", verdict.Entries.Select(e => e.ForLog));
        switch (verdict.Outcome)
        {
            case InstallOutcome.Mixed:
                line.Append(CultureInfo.InvariantCulture, $"mixed: {string.Join("; ", verdict.Problems)}. T2T Online {release} (loaded from {verdict.LoadedFrom}) is off this session: no patches, nothing online.");
                if (verdict.Entries.Count > 0)
                {
                    line.Append(CultureInfo.InvariantCulture, $" Files of another build: {files}.");
                }

                line.Append(" To fix: close the game, delete those files, then unzip the T2T Online zip into the game folder again.");
                break;
            case InstallOutcome.Leftovers:
                line.Append(CultureInfo.InvariantCulture, $"old files, online is off this session: {verdict.Entries.Count} to delete: {files}. ");
                line.Append(CultureInfo.InvariantCulture, $"T2T Online {release} itself is whole and loaded from {verdict.LoadedFrom}.");
                break;
            default:
                line.Append(CultureInfo.InvariantCulture, $"clean: T2T Online {release} and its two libraries are one build, loaded from {verdict.LoadedFrom}; no other copies.");
                break;
        }

        if (verdict.Flat)
        {
            line.Append(" The flat folders (Plugins and UserLibs) work; the zip installs as Mods\\T2TOnline.");
        }

        if (verdict.SubfoldersOff)
        {
            line.Append(" MelonLoader's Melon folders are off (disable_subfolder_load in UserData\\Loader.cfg), so Mods\\T2TOnline isn't loaded.");
        }

        if (verdict.Unreadable > 0)
        {
            line.Append(CultureInfo.InvariantCulture, $" {verdict.Unreadable} folder(s) couldn't be read and weren't checked.");
        }

        return line.ToString();
    }

    /// <summary>
    /// The files for the game's prompt: <c>/</c> between folders (the prompt's text reads a backslash and a letter as an
    /// escape), only characters its fonts draw, each path at most <see cref="ScreenPathLength"/> long, at most
    /// <see cref="ScreenEntries"/> of them and the rest counted ("(+3)").
    /// </summary>
    public static string ForScreen(IReadOnlyList<InstallEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var shown = new List<string>();
        foreach (InstallEntry entry in entries.Take(ScreenEntries))
        {
            var path = new StringBuilder(entry.Path.Length);
            foreach (char c in entry.Path)
            {
                path.Append(c is '\\' or '/' ? '/' : c is < ' ' or > '~' || Undrawable.Contains(c, StringComparison.Ordinal) ? '?' : c);
            }

            string text = path.ToString();
            shown.Add(text.Length <= ScreenPathLength ? text : "..." + text[^(ScreenPathLength - 3)..]);
        }

        string list = string.Join(", ", shown);
        return entries.Count > ScreenEntries ? $"{list} (+{entries.Count - ScreenEntries})" : list;
    }

    /// <summary>The files as the offline shell's corner notice and the mixed install's notice show them (English, Unity's own GUI: backslashes are fine there).</summary>
    public static string ForNotice(IReadOnlyList<InstallEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string list = string.Join(", ", entries.Take(ScreenEntries).Select(e => e.Path));
        return entries.Count > ScreenEntries ? $"{list} (+{entries.Count - ScreenEntries})" : list;
    }

    /// <summary>The mixed install's notice, in English (the game's words need T2T Online's library, which is the part that's wrong).</summary>
    public static IReadOnlyList<string> MixedNotice(InstallVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var lines = new List<string> { "T2T Online is off: part of it is from another version, so the game runs without it." };
        lines.Add(verdict.Entries.Count > 0
            ? $"   Close the game, delete {ForNotice(verdict.Entries)}, then unzip the T2T Online zip into the game folder again."
            : "   Close the game, then unzip the T2T Online zip into the game folder again and choose Replace.");
        lines.Add("   \"T2T Online - READ ME FIRST.txt\" in the game folder says how.");
        return lines;
    }

    // ScanForFolders: the three root folders first (each its phase's first folder), then the Melon folders below them.
    private static List<MelonFolder> Scan(IInstallTree tree, string baseDirectory, bool subfoldersOff, bool manifestNotNeeded, HashSet<string> unreadable)
    {
        var folders = new List<MelonFolder>
        {
            new(Path.Combine(baseDirectory, "UserLibs"), MelonPhase.UserLibs, null, true),
            new(Path.Combine(baseDirectory, "Plugins"), MelonPhase.Plugins, null, true),
            new(Path.Combine(baseDirectory, "Mods"), MelonPhase.Mods, null, true),
        };

        foreach (MelonFolder root in folders.ToArray())
        {
            FindSubFolders(tree, root.Phase, root.Path, null, requireManifest: true, !subfoldersOff, manifestNotNeeded, folders, unreadable);
        }

        return folders;
    }

    private static void FindSubFolders(IInstallTree tree, MelonPhase phase, string path, string? top, bool requireManifest, bool scanned, bool manifestNotNeeded,
        List<MelonFolder> folders, HashSet<string> unreadable)
    {
        IReadOnlyList<string>? directories = tree.Directories(path);
        if (directories == null)
        {
            unreadable.Add(path);
            return;
        }

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (Excluded(name) || (requireManifest && !manifestNotNeeded && !tree.FileExists(Path.Combine(directory, "manifest.json"))))
            {
                continue;
            }

            // AddFolder: a folder named UserLibs is a library folder; Plugins becomes a plugin folder below Plugins or Mods; Mods stays Mods below Mods.
            MelonPhase kind = name switch
            {
                "UserLibs" => MelonPhase.UserLibs,
                "Plugins" => phase >= MelonPhase.Plugins ? MelonPhase.Plugins : phase,
                "Mods" => phase >= MelonPhase.Mods ? MelonPhase.Mods : phase,
                _ => phase,
            };
            string melonFolder = top ?? directory;
            folders.Add(new MelonFolder(directory, kind, melonFolder, scanned));
            FindSubFolders(tree, kind, directory, melonFolder, requireManifest: false, scanned, manifestNotNeeded, folders, unreadable);
        }
    }

    // MelonLoader's name exclusions: a name starting with '~' or '.', or exactly Broken, Retired or Disabled.
    private static bool Excluded(string name) =>
        name.StartsWith('~') || name.StartsWith('.') || name is "Broken" or "Retired" or "Disabled";

    private static List<OurFile> FindOurs(IInstallTree tree, IReadOnlyList<MelonFolder> folders, HashSet<string> unreadable)
    {
        var ours = new List<OurFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MelonFolder folder in folders)
        {
            IReadOnlyList<string>? files = tree.Files(folder.Path);
            if (files == null)
            {
                unreadable.Add(folder.Path);
                continue;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string path = Full(file);
                if (!seen.Add(path))
                {
                    continue;
                }

                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name.StartsWith(PluginName, StringComparison.OrdinalIgnoreCase))
                {
                    // Any file name: MelonLoader goes by the assembly's own name ("T2TOnline (1).dll" is a second copy). One of our
                    // names that can't be read is ours all the same.
                    AssemblyFacts? facts = tree.Assembly(file);
                    if (facts != null ? IsOurs(facts.Name) : DllNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        ours.Add(new OurFile(path, folder, facts, Pdb: false));
                    }
                }
                else if (PdbNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    ours.Add(new OurFile(path, folder, null, Pdb: true));
                }
            }
        }

        return ours;
    }

    private static IEnumerable<string> OldRootFilesIn(IInstallTree tree, string root, IReadOnlyDictionary<string, string> oldRootDocuments)
    {
        foreach ((string name, string hash) in oldRootDocuments)
        {
            string path = Path.Combine(root, name);
            if (tree.FileExists(path) && tree.Head(path, 64 * 1024) is { } bytes && string.Equals(HashWithoutCarriageReturns(bytes), hash, StringComparison.Ordinal))
            {
                yield return Full(path);
            }
        }

        foreach (string name in OldRootFiles)
        {
            string path = Path.Combine(root, name);
            if (tree.FileExists(path))
            {
                yield return Full(path);
            }
        }
    }

    /// <summary>
    /// <paramref name="line"/> with each of <paramref name="folders"/> (either kind of slash, any case) written as
    /// <c>&lt;game folder&gt;</c>, the session log scrubber's label: for a line that goes to MelonLoader's log without that
    /// scrubber (it needs T2TOnline.Core). Folders of three characters or fewer (a drive) are left alone. Never throws.
    /// </summary>
    public static string LabelGameFolder(string line, params string?[] folders)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (string? folder in folders ?? Array.Empty<string?>())
        {
            string trimmed = folder?.TrimEnd('\\', '/') ?? string.Empty;
            if (trimmed.Length > 3)
            {
                line = line.Replace(trimmed, GameFolderLabel, StringComparison.OrdinalIgnoreCase)
                    .Replace(trimmed.Replace('\\', '/'), GameFolderLabel, StringComparison.OrdinalIgnoreCase);
            }
        }

        return line;
    }

    /// <summary>The SHA-256 of a file's bytes with every carriage return taken out, in lower-case hex: the same for a file with CRLF or LF endings.</summary>
    internal static string HashWithoutCarriageReturns(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        byte[] kept = bytes.Where(b => b != (byte)'\r').ToArray();
        return Convert.ToHexString(SHA256.HashData(kept)).ToLowerInvariant();
    }

    // A second copy that is only T2T Online (its Melon folder holds nothing of anyone else's and nothing that runs) is named as
    // one folder: deleting that is the whole fix, and it leaves no empty folder with a manifest behind.
    private static List<string> Collapse(IInstallTree tree, string root, List<string> leftovers, List<OurFile> ours, HashSet<string> kept)
    {
        var ourPaths = new HashSet<string>(ours.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in leftovers)
        {
            OurFile? file = ours.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            string? top = file?.Folder.Top;
            if (top != null && !kept.Any(k => Inside(k, top)) && OnlyOurs(tree, top, ourPaths))
            {
                if (named.Add(top))
                {
                    result.Add(top + Path.DirectorySeparatorChar);
                }

                continue;
            }

            result.Add(path);
        }

        return result;
    }

    private static bool OnlyOurs(IInstallTree tree, string top, HashSet<string> ourPaths)
    {
        var pending = new Stack<string>();
        pending.Push(top);
        int files = 0;
        bool anyDll = false;
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            IReadOnlyList<string>? inside = tree.Files(folder);
            IReadOnlyList<string>? below = tree.Directories(folder);
            if (inside == null || below == null)
            {
                return false;
            }

            foreach (string file in inside)
            {
                if (++files > CollapseLimit)
                {
                    return false;
                }

                bool ourFile = ourPaths.Contains(Full(file));
                anyDll |= ourFile && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if (!ourFile && !PackageDocuments.Contains(Path.GetFileName(file)))
                {
                    return false;
                }
            }

            foreach (string directory in below)
            {
                pending.Push(directory);
            }
        }

        return anyDll;
    }

    private static List<InstallEntry> Entries(string root, IEnumerable<string> paths, List<OurFile> ours, LogNames names)
    {
        var entries = new List<InstallEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            bool folder = path.EndsWith(Path.DirectorySeparatorChar);
            string full = folder ? Full(path.TrimEnd(Path.DirectorySeparatorChar)) : Full(path);
            if (!seen.Add(full))
            {
                continue;
            }

            string relative = Relative(root, full);
            if (folder)
            {
                entries.Add(new InstallEntry(relative + "\\", names.Folder(relative) + "\\"));
                continue;
            }

            OurFile? file = ours.FirstOrDefault(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));
            entries.Add(new InstallEntry(relative, names.File(relative, file?.Facts?.Name)));
        }

        // Folders' files first, then the game folder's own (the old documents), each part by name.
        return entries.OrderBy(e => e.Path.Contains('\\', StringComparison.Ordinal) ? 0 : 1).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CheckPart(InstallFacts facts, LoadedPart? part, Version? wanted, string name, string root, LogNames names, List<string> problems)
    {
        if (part == null)
        {
            problems.Add($"{name} isn't loaded (its DLL is missing, or MelonLoader couldn't load it)");
            return;
        }

        string where = part.Location == null ? "an unknown place" : names.File(Relative(root, Full(part.Location)), name);
        if (wanted != null && part.Version != null && part.Version < wanted)
        {
            problems.Add($"{name} {Describe(part)} is older than the {wanted} the plugin needs ({where})");
        }
        else if (!SameBuild(facts.Plugin, part))
        {
            problems.Add($"{name} is {Describe(part)}, the plugin {Describe(facts.Plugin)} ({where})");
        }
    }

    private static string Describe(LoadedPart part) => part.Build ?? part.Version?.ToString() ?? "of an unknown build";

    // One build: the same informational version ("0.11.0-beta+<commit>"); without one, the same assembly version.
    private static bool SameBuild(LoadedPart plugin, LoadedPart part) =>
        plugin.Build != null ? string.Equals(plugin.Build, part.Build, StringComparison.Ordinal) : plugin.Version == part.Version;

    private static bool SameBuild(LoadedPart plugin, AssemblyFacts facts) =>
        plugin.Build != null ? string.Equals(plugin.Build, facts.Build, StringComparison.Ordinal) : plugin.Version == facts.Version;

    private static bool IsOurs(string assemblyName) =>
        assemblyName.Equals(PluginName, StringComparison.OrdinalIgnoreCase) || assemblyName.Equals(CoreName, StringComparison.OrdinalIgnoreCase) ||
        assemblyName.Equals(SteamName, StringComparison.OrdinalIgnoreCase);

    // A PDB and a DLL of one assembly: the same folder and the same name before the extension.
    private static bool SameStem(string a, string b) =>
        string.Equals(Path.GetDirectoryName(a), Path.GetDirectoryName(b), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b), StringComparison.OrdinalIgnoreCase);

    private static MelonFolder? FolderOf(IReadOnlyList<MelonFolder> folders, string file)
    {
        string? directory = Path.GetDirectoryName(Full(file));
        return folders.FirstOrDefault(f => string.Equals(Full(f.Path), directory, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Inside(string path, string folder) =>
        path.StartsWith(Full(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // A path under the game folder, with '\' whatever the platform; one outside it keeps only its name.
    private static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            relative = relative == "." ? string.Empty : Path.GetFileName(path);
        }

        return relative.Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\');
    }

    /// <summary>A file of T2T Online's found in a folder MelonLoader looks in.</summary>
    private sealed record OurFile(string Path, MelonFolder Folder, AssemblyFacts? Facts, bool Pdb);

    /// <summary>
    /// How a path is written in the log: MelonLoader's folder names, the zip's and T2T Online's file names as they are; any
    /// other folder as "[folder 1]", "[folder 2]"... (a player named it), the same label for the same name within one line; a
    /// copy of an assembly under another file name as "[a copy of T2TOnline.dll]".
    /// </summary>
    private sealed class LogNames
    {
        private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

        public string Folder(string relative) => relative.Length == 0 ? "the game folder" : string.Join("\\", relative.Split('\\').Select(Segment));

        public string File(string relative, string? assemblyName)
        {
            int cut = relative.LastIndexOf('\\');
            string folder = cut < 0 ? string.Empty : relative[..cut];
            string name = cut < 0 ? relative : relative[(cut + 1)..];
            string shown = DllNames.Contains(name, StringComparer.OrdinalIgnoreCase) || PdbNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                OldRootDocuments.ContainsKey(name) || OldRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? name
                : $"[a copy of {(assemblyName != null && IsOurs(assemblyName) ? assemblyName : PluginName)}.dll]";
            return folder.Length == 0 ? shown : $"{Folder(folder)}\\{shown}";
        }

        private string Segment(string name)
        {
            if (KnownFolders.Contains(name))
            {
                return name;
            }

            if (!_labels.TryGetValue(name, out string? label))
            {
                label = $"[folder {_labels.Count + 1}]";
                _labels[name] = label;
            }

            return label;
        }
    }
}
