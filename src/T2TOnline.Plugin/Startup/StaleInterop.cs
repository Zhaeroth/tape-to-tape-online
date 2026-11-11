using System;
using System.IO;
using System.Security.Cryptography;
using MelonLoader;
using MelonLoader.Utils;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Clears MelonLoader's interop assemblies when its generator is about to remake them (D108). A plugin loads before the generator
/// runs, and MelonLoader sorts plugins by their references (<c>DependencyGraph</c>, which loads each one), so every interop
/// assembly this plugin references is mapped before then, and the generator can't delete a mapped file: <c>Failed to start
/// MelonLoader</c> closes the game on every launch. The plugin's constructor comes before that sort, so with the old files gone
/// the launch is a first launch: the sort skips what isn't there, the generator makes new files, and the plugin runs on them.
/// The rules are the generator's own, as far as they can be known for sure (<see cref="InteropFreshness"/>): a tool's version
/// change, which only a new MelonLoader brings, isn't covered.
/// <para>
/// Called from the plugin's constructor: nothing here may touch an interop type, or it would map the file it is about to delete.
/// </para>
/// </summary>
internal static class StaleInterop
{
    /// <summary>True when the generator runs on this launch by the rules here (whether or not any file was there to clear).</summary>
    internal static bool GeneratorExpected { get; private set; }

    /// <summary>What was done, for the log once it's open (the constructor comes first); null if nothing was.</summary>
    internal static string? Report { get; private set; }

    /// <summary>Clears the old files if the generator will run on this launch. Never throws: a failure is reported, and the generator's own error follows.</summary>
    internal static void ClearIfGameChanged()
    {
        try
        {
            string interop = MelonEnvironment.Il2CppAssembliesDirectory;
            string gameAssembly = Path.Combine(MelonEnvironment.GameRootDirectory, "GameAssembly.dll");
            if (!Directory.Exists(interop) || !File.Exists(gameAssembly))
            {
                return;
            }

            string generator = MelonEnvironment.Il2CppAssemblyGeneratorDirectory;
            string config = Path.Combine(generator, "Config.cfg");
            string? recorded = File.Exists(config) ? InteropFreshness.ReadGameAssemblyHash(File.ReadAllText(config)) : null;
            bool forced = LoaderConfig.Current?.UnityEngine?.ForceRegeneration == true;
            // Cpp2IL's ShouldSetup and its plugin's: a missing file is downloaded again, and generation is then always needed.
            bool toolMissing = !File.Exists(Path.Combine(generator, "Cpp2IL", "Cpp2IL.exe")) ||
                !File.Exists(Path.Combine(generator, "Cpp2IL", "Plugins", "Cpp2IL.Plugin.StrippedCodeRegSupport.dll"));
            string current = HashOf(gameAssembly);
            if (!InteropFreshness.WillRegenerate(recorded, current, forced, toolMissing))
            {
                return;
            }

            GeneratorExpected = true;
            string why = InteropFreshness.Why(recorded, current, forced, toolMissing);

            int removed = 0;
            int kept = 0;
            string? firstProblem = null;
            foreach (string file in Directory.GetFiles(interop))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    kept++;
                    firstProblem ??= $"{Path.GetFileName(file)}: {e.Message}";
                }
            }

            Report = kept == 0
                ? $"MelonLoader remakes its interop assemblies on this launch ({why}): the {removed} old files are cleared first, so it can (a few minutes)."
                : $"MelonLoader remakes its interop assemblies on this launch ({why}): {removed} old files cleared, {kept} couldn't be, so its generator may stop the game ({firstProblem}).";
        }
        catch (Exception e)
        {
            Report = $"Couldn't check MelonLoader's interop assemblies against the game: {e.GetType().Name}: {e.Message}";
        }
    }

    // The generator's hash, MelonUtils.ComputeSimpleSHA512Hash: SHA-512 in upper-case hex (read here in pieces, not whole).
    private static string HashOf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA512 sha512 = SHA512.Create();
        return Convert.ToHexString(sha512.ComputeHash(stream));
    }
}
