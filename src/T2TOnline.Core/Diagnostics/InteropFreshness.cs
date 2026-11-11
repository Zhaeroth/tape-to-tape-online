using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Whether MelonLoader's generator is about to remake the game's interop assemblies, by the generator's own rules (D108): the
/// hash it recorded in its Config.cfg is missing or isn't the installed GameAssembly.dll's, its force switch is on, or a tool it
/// runs is missing (it downloads it again, and then always regenerates). A tool's version isn't compared: the generator may take
/// one from MelonLoader's remote API, so a guess could clear files it then doesn't remake, and with none the game can't start.
/// </summary>
public static class InteropFreshness
{
    private const string HashKey = "GameAssemblyHash";

    /// <summary>The hash in the text of the generator's Config.cfg (TOML, <c>GameAssemblyHash = "..."</c>), or null if there's none.</summary>
    public static string? ReadGameAssemblyHash(string configText)
    {
        foreach (string raw in configText.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(HashKey, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = line.Substring(HashKey.Length).TrimStart();
            if (!rest.StartsWith('='))
            {
                continue;
            }

            string value = rest.Substring(1).Trim().Trim('"');
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>True when the game's hash makes the generator run on this launch. An exact comparison, as the generator's <c>Equals</c> is.</summary>
    public static bool WillRegenerate(string? recordedHash, string currentHash) =>
        string.IsNullOrEmpty(recordedHash) || !string.Equals(recordedHash, currentHash, StringComparison.Ordinal);

    /// <summary>
    /// True when the generator runs on this launch for any reason the mod can know for sure: the hash, <paramref name="forced"/>
    /// (<c>force_regeneration</c> in Loader.cfg, or its launch option), or <paramref name="toolMissing"/> (Cpp2IL or its plugin).
    /// </summary>
    public static bool WillRegenerate(string? recordedHash, string currentHash, bool forced, bool toolMissing) =>
        forced || toolMissing || WillRegenerate(recordedHash, currentHash);

    /// <summary>Why, in words for the log (the first reason that holds); empty when the generator won't run.</summary>
    public static string Why(string? recordedHash, string currentHash, bool forced, bool toolMissing) =>
        forced ? "MelonLoader is told to regenerate"
        : toolMissing ? "a tool MelonLoader's generator runs is missing"
        : string.IsNullOrEmpty(recordedHash) ? "MelonLoader hasn't finished making them before"
        : WillRegenerate(recordedHash, currentHash) ? "the game changed since MelonLoader made them"
        : string.Empty;
}
