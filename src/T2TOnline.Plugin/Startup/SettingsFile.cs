using System;
using System.IO;
using System.Reflection;
using System.Threading;
using MelonLoader;
using T2TOnline.Core.SettingsFiles;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// The mod's own settings file, <c>UserData\T2TOnline\settings.cfg</c> (D126): one MelonLoader category in a file of its
/// own, so deleting the mod's folder removes its settings, and MelonLoader's shared <c>MelonPreferences.cfg</c> is left as
/// MelonLoader keeps it. Moved there once from the shared file. Only MelonLoader's public preferences API is used, with
/// <c>printmsg: false</c> (MelonLoader prints the full path otherwise), plus one internal call by reflection to take the
/// old section out of the shared file (MelonLoader 0.7.3's public <c>RemoveCategoryFromFile</c> can't reach that file).
/// No game type is touched.
/// </summary>
internal static class SettingsFile
{
    /// <summary>The file's name in <c>UserData\T2TOnline</c>.</summary>
    public const string FileName = "settings.cfg";

    /// <summary>How logs and messages name the mod's file (no folder above the game's).</summary>
    public const string Label = "UserData/T2TOnline/settings.cfg";

    /// <summary>How logs and messages name MelonLoader's shared file.</summary>
    public const string SharedLabel = "UserData/MelonPreferences.cfg";

    /// <summary>A stale entry from 0.9.x that nothing reads (replaced by SnapshotMeasure).</summary>
    private const string StaleEntry = "SnapshotProbe";

    private static string _ownPath = string.Empty;
    private static string _sharedPath = string.Empty;
    private static int _ownSaves;
    private static int _sharedSaves;
    private static int _ownLoads;
    private static bool _listening;

    /// <summary>True once <see cref="Open"/> left the settings in the mod's own file (false: in the shared file, this session).</summary>
    public static bool OnOwnFile { get; private set; }

    /// <summary>
    /// Opens category <paramref name="identifier"/> in <paramref name="path"/>, its entries declared by
    /// <paramref name="createEntries"/>. When the mod's file doesn't hold the settings yet, they're read from MelonLoader's
    /// shared file (<paramref name="sharedPath"/>, as MelonLoader loaded it), written to the mod's file, checked there, and
    /// only then taken out of the shared file, which is saved at once. When both hold them, the mod's file wins. A file
    /// that can't be written leaves the settings in the shared file for this session (tried again next start). Each step
    /// says what it did through <paramref name="info"/> and <paramref name="warn"/>. Throws only what MelonLoader's own
    /// category calls throw (the caller's start-up catch then applies the defaults).
    /// </summary>
    public static MelonPreferences_Category Open(string identifier, string displayName, string path, string sharedPath,
        Action<MelonPreferences_Category> createEntries, Action<string> info, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(createEntries);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(warn);
        _ownPath = Full(path);
        _sharedPath = Full(sharedPath);
        Listen();

        bool ownHasSection = HasSection(path, identifier, assumeWhenUnreadable: true);
        bool sharedHasSection = HasSection(sharedPath, identifier, assumeWhenUnreadable: false);
        SettingsOpening opening = SettingsText.Decide(ownHasSection, sharedHasSection);
        MelonPreferences_Category category = MelonPreferences.CreateCategory(identifier, displayName);
        bool onOwnFile = opening == SettingsOpening.UseOwnFile
            ? UseOwnFile(category, path, createEntries, warn)
            : MoveToOwnFile(category, path, sharedPath, opening, createEntries, info, warn);
        OnOwnFile = onOwnFile;
        if (!onOwnFile)
        {
            return category;
        }

        category.DeleteEntry(StaleEntry);
        RemoveFromSharedFile(identifier, sharedPath, sharedHasSection, info, warn);
        return category;
    }

    /// <summary>
    /// Saves <paramref name="category"/> to its file now. False, with why, when MelonLoader didn't write it (MelonLoader's
    /// own log line says why; it catches its own errors, so its saved event is how success is seen).
    /// </summary>
    public static bool Save(MelonPreferences_Category? category, out string problem)
    {
        if (category == null)
        {
            problem = "the settings weren't loaded at start-up";
            return false;
        }

        int before = Volatile.Read(ref _ownSaves);
        int sharedBefore = Volatile.Read(ref _sharedSaves);
        category.SaveToFile(printmsg: false);
        if (Volatile.Read(ref _ownSaves) != before || Volatile.Read(ref _sharedSaves) != sharedBefore)
        {
            problem = string.Empty;
            return true;
        }

        problem = $"{Label} couldn't be written (MelonLoader's log says why)";
        return false;
    }

    /// <summary>The mod's file holds the settings: the category reads them from it.</summary>
    private static bool UseOwnFile(MelonPreferences_Category category, string path, Action<MelonPreferences_Category> createEntries, Action<string> warn)
    {
        int loadsBefore = Volatile.Read(ref _ownLoads);
        category.SetFilePath(path, autoload: true, printmsg: false);
        createEntries(category);
        if (Volatile.Read(ref _ownLoads) == loadsBefore)
        {
            warn($"Settings: {Label} couldn't be read (MelonLoader's line in its log says why; a hand edit may have broken it); the defaults apply this session.");
        }

        return true;
    }

    /// <summary>
    /// The mod's file doesn't hold the settings: the entries read the shared file's values as MelonLoader loaded them (the
    /// defaults when it has none), then the category moves to the mod's file and is saved and checked there. False, with the
    /// category back on the shared file, when that failed.
    /// </summary>
    private static bool MoveToOwnFile(MelonPreferences_Category category, string path, string sharedPath, SettingsOpening opening,
        Action<MelonPreferences_Category> createEntries, Action<string> info, Action<string> warn)
    {
        createEntries(category);
        string problem;
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                // MelonLoader watches the file's folder: a missing one fails the watch and turns its watching off for every
                // settings file this session.
                Directory.CreateDirectory(folder);
            }

            if (!File.Exists(path))
            {
                // Created before MelonLoader starts watching it, so each save is a rewrite of an existing file (the case seen
                // to raise one change event, which MelonLoader's save flag absorbs, rather than a reload that logs the path).
                File.WriteAllText(path, string.Empty);
            }

            category.SetFilePath(path, autoload: false, printmsg: false);
            if (Save(category, out problem) && HasSection(path, category.Identifier, assumeWhenUnreadable: false))
            {
                info(opening == SettingsOpening.MoveFromSharedFile
                    ? $"Settings: moved T2T Online's settings from {SharedLabel} to {Label}."
                    : $"Settings: created {Label} with the defaults.");
                return true;
            }

            problem = problem.Length > 0 ? problem : $"{Label} doesn't hold them after the save";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = $"{Label} couldn't be written ({ex.GetType().Name})";
        }

        category.SetFilePath(sharedPath, autoload: false, printmsg: false);
        warn($"Settings: {problem}, so they stay in {SharedLabel} this session; moving them is tried again at the next start.");
        return false;
    }

    /// <summary>
    /// Takes the category's section out of MelonLoader's copy of the shared file and saves that file at once, so neither a
    /// crash nor MelonLoader's save at quit can write it back. Said once when there was one; a warning when it stays.
    /// </summary>
    private static void RemoveFromSharedFile(string identifier, string sharedPath, bool onDisk, Action<string> info, Action<string> warn)
    {
        bool removed;
        try
        {
            object? file = typeof(MelonPreferences).GetField("DefaultFile", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            MethodInfo? remove = file?.GetType().GetMethod("RemoveCategoryFromDocument", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (remove == null)
            {
                if (onDisk)
                {
                    warn($"Settings: the old [{identifier}] section in {SharedLabel} couldn't be removed (this MelonLoader doesn't offer the way T2T Online uses); " +
                        "it's no longer read, and you can delete it with the game closed.");
                }

                return;
            }

            removed = remove.Invoke(file, new object[] { identifier }) is true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or ArgumentException or InvalidOperationException)
        {
            warn($"Settings: the old [{identifier}] section in {SharedLabel} couldn't be removed ({ex.GetType().Name}); it's no longer read, and you can delete it with the game closed.");
            return;
        }

        if (!removed && !onDisk)
        {
            return;
        }

        int before = Volatile.Read(ref _sharedSaves);
        MelonPreferences.Save();
        bool saved = Volatile.Read(ref _sharedSaves) != before;
        if (saved && !HasSection(sharedPath, identifier, assumeWhenUnreadable: true))
        {
            info($"Settings: removed the old [{identifier}] section from {SharedLabel}.");
        }
        else
        {
            warn($"Settings: the old [{identifier}] section in {SharedLabel} couldn't be removed (MelonLoader didn't save the file); it's no longer read, " +
                "and you can delete it with the game closed. Tried again at the next start.");
        }
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> has the section; false when it's missing or hasn't;
    /// <paramref name="assumeWhenUnreadable"/> when it exists but can't be read.
    /// </summary>
    private static bool HasSection(string path, string identifier, bool assumeWhenUnreadable)
    {
        try
        {
            return File.Exists(path) && SettingsText.HasSection(File.ReadAllText(path), identifier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return assumeWhenUnreadable;
        }
    }

    /// <summary>MelonLoader's saved and loaded events, which name the file: listened to once, for the whole session.</summary>
    private static void Listen()
    {
        if (_listening)
        {
            return;
        }

        MelonPreferences.OnPreferencesSaved.Subscribe(OnSaved);
        MelonPreferences.OnPreferencesLoaded.Subscribe(OnLoaded);
        _listening = true;
    }

    // MelonLoader raises these on its file watcher's thread too: counters only, and never a throw.
    private static void OnSaved(string path)
    {
        if (Same(path, _ownPath))
        {
            Interlocked.Increment(ref _ownSaves);
        }
        else if (Same(path, _sharedPath))
        {
            Interlocked.Increment(ref _sharedSaves);
        }
    }

    private static void OnLoaded(string path)
    {
        if (Same(path, _ownPath))
        {
            Interlocked.Increment(ref _ownLoads);
        }
    }

    private static bool Same(string? path, string full) => full.Length > 0 && string.Equals(Full(path), full, StringComparison.OrdinalIgnoreCase);

    private static string Full(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }
}
