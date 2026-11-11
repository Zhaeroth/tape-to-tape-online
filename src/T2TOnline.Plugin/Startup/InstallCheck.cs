using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MelonLoader;
using MelonLoader.Utils;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// The install check (D125), first thing in the plugin's constructor: are the plugin, its library and its Steam assembly one
/// build, and is nothing else of T2T Online where MelonLoader looks (an update from 0.10.0 Beta leaves its six flat files and
/// two root documents behind)? The rules are <see cref="InstallLayout"/>'s; this gathers the facts from MelonLoader and the
/// disk. Plain .NET and MelonLoader only: T2TOnline.Core may be the part that's another build, so nothing here may touch it,
/// and no interop assembly may load before MelonLoader's generator (D108). Never throws.
/// </summary>
internal static class InstallCheck
{
    /// <summary>The answer, or null when the check itself failed (<see cref="Failure"/>).</summary>
    public static InstallVerdict? Verdict { get; private set; }

    /// <summary>Why the check couldn't run, or null. A failed check never turns online off by itself.</summary>
    public static string? Failure { get; private set; }

    /// <summary>True on a mixed install: the plugin stays inert (<see cref="InertPlugin"/>).</summary>
    public static bool Inert => Verdict?.Outcome == InstallOutcome.Mixed;

    /// <summary>True when old files of T2T Online are left: online stays off this session, and the title says which to delete.</summary>
    public static bool Leftovers => Verdict?.Outcome == InstallOutcome.Leftovers;

    /// <summary>The plugin's own build ("0.11.0-beta+&lt;commit&gt;"), or its release when the build isn't recorded.</summary>
    public static string Release { get; private set; } = BuildVersion.Release;

    /// <summary>The log's line about the install (relative paths, no player-made folder names), or null before <see cref="Run"/>.</summary>
    public static string? LogLine { get; private set; }

    /// <summary>Runs the check once. Never throws: a failure is kept in <see cref="Failure"/> and the plugin starts as usual.</summary>
    public static void Run()
    {
        if (LogLine != null)
        {
            return;
        }

        try
        {
            Assembly plugin = typeof(InstallCheck).Assembly;
            LoadedPart self = Part(plugin, plugin.GetName().Name ?? InstallLayout.PluginName);
            Release = ReleaseOf(self.Build) ?? BuildVersion.Release;
            AssemblyName[] references = plugin.GetReferencedAssemblies();
            LoaderConfig.CoreConfig? loader = LoaderConfig.Current?.Loader;
            var facts = new InstallFacts(
                MelonEnvironment.MelonBaseDirectory,
                SubfoldersOff: loader?.DisableSubFolderLoad == true,
                ManifestNotNeeded: loader?.DisableSubFolderManifest == true,
                self,
                Loaded(InstallLayout.CoreName),
                Loaded(InstallLayout.SteamName),
                references.FirstOrDefault(r => r.Name == InstallLayout.CoreName)?.Version,
                references.FirstOrDefault(r => r.Name == InstallLayout.SteamName)?.Version);
            Verdict = InstallLayout.Decide(new DiskTree(), facts);
            LogLine = InstallLayout.LogLine(Verdict, Release);
        }
        catch (Exception ex)
        {
            // The type only: a message may hold a full path.
            Failure = ex.GetType().Name;
            Verdict = null;
            LogLine = $"Install check: couldn't be done ({Failure}); T2T Online starts as usual.";
        }
    }

    /// <summary>
    /// For a leftover install: the line the offline shell's corner notice shows under why online is off, which every player sees, naming the old files to delete
    /// (read by <c>T2TOnlinePlugin.SwitchOnlineOff</c> and shown by <c>OfflineShell</c>; English, because the notice is drawn without the mod's words: the game's own prompt has its own words).
    /// </summary>
    public static string Advice =>
        Verdict is { Outcome: InstallOutcome.Leftovers } verdict
            ? $"Close the game and delete these old T2T Online files: {InstallLayout.ForNotice(verdict.Entries)}. Online comes back at the next start."
            : string.Empty;

    // The process's copy of one of our assemblies: the default context's, the one the plugin binds to.
    private static LoadedPart? Loaded(string name)
    {
        Assembly? found = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || !string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (found == null || AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default)
            {
                found = assembly;
            }
        }

        return found == null ? null : Part(found, name);
    }

    private static LoadedPart Part(Assembly assembly, string name)
    {
        string? location = string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
        string? build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new LoadedPart(name, location, assembly.GetName().Version, build);
    }

    // "0.11.0-beta+<commit>" -> "0.11.0-beta" (what the log's other lines call the release).
    private static string? ReleaseOf(string? build)
    {
        if (string.IsNullOrEmpty(build))
        {
            return null;
        }

        int plus = build.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? build : build[..plus];
    }

    /// <summary>The real disk, read without loading anything: a DLL's identity comes from its metadata and its version resource.</summary>
    private sealed class DiskTree : IInstallTree
    {
        public IReadOnlyList<string>? Directories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        public IReadOnlyList<string>? Files(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        public bool FileExists(string path) => File.Exists(path);

        public AssemblyFacts? Assembly(string path)
        {
            try
            {
                AssemblyName name = AssemblyName.GetAssemblyName(path);
                string? build = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                return name.Name == null ? null : new AssemblyFacts(name.Name, name.Version ?? new Version(0, 0), string.IsNullOrEmpty(build) ? null : build);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or System.Security.SecurityException)
            {
                return null;
            }
        }

        public byte[]? Head(string path, int max)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] buffer = new byte[(int)Math.Min(max, stream.Length)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int got = stream.Read(buffer, read, buffer.Length - read);
                    if (got == 0)
                    {
                        break;
                    }

                    read += got;
                }

                return read == buffer.Length ? buffer : buffer[..read];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
