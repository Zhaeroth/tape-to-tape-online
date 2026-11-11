using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Plugin.Startup;

/// <summary>Identifies the installed game build by hashing its code files.</summary>
internal sealed class BuildFingerprint
{
    /// <summary>
    /// Builds the mod has been tested against, keyed by GameAssembly.dll SHA-256. Only builds the mod has actually run on:
    /// 1.0 (Oct 23) was only read offline, so it isn't here. The Nov 6 build is here: it went through DEVELOPMENT.md's
    /// after-update list, and the check tours have run on it. The Nov 1 build went with it (as the Oct 29 one before): this
    /// plugin is compiled against the Nov 6 build's interop, its run repository copy no longer sets the field the Nov 1
    /// build's needs, and Steam moves everyone to the new build.
    /// </summary>
    private static readonly Dictionary<string, TestedBuild> TestedBuilds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0709d469ae6c47b695bba71664bf5619982ee6c3e17eaef6dc8b98fda0a61cec"] = new(
            "Nov 6 2026 build", "50abd75e31fbbd6dd3ab098507b40b9f01ea100b564f593a62efee8fd5ad44fc", 26471720),
    };

    /// <summary>The tested builds' Steam build numbers, for the untested-build notice (<see cref="UntestedBuildNotice.Age"/>).</summary>
    public static IReadOnlyCollection<long> TestedSteamBuilds { get; } = TestedBuilds.Values.Select(b => b.SteamBuild).Where(b => b > 0).ToArray();

    private BuildFingerprint(string gameAssemblySha256, string metadataSha256, TestedBuild? tested)
    {
        GameAssemblySha256 = gameAssemblySha256;
        MetadataSha256 = metadataSha256;
        Tested = tested;
    }

    /// <summary>SHA-256 of GameAssembly.dll, lowercase hex.</summary>
    public string GameAssemblySha256 { get; }

    /// <summary>SHA-256 of global-metadata.dat, lowercase hex.</summary>
    public string MetadataSha256 { get; }

    /// <summary>The matching tested build, or null for a build we haven't seen.</summary>
    public TestedBuild? Tested { get; }

    /// <summary>True when both files match a tested build.</summary>
    public bool IsTested => Tested != null && string.Equals(Tested.MetadataSha256, MetadataSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>A short label for logs and the lobby: the tested build's name, or the first hash digits.</summary>
    public string Label => IsTested ? Tested!.Name : $"untested build {GameAssemblySha256[..12]}";

    /// <summary>Hashes the game files under <paramref name="gameRoot"/>.</summary>
    public static BuildFingerprint Compute(string gameRoot)
    {
        string assembly = Path.Combine(gameRoot, "GameAssembly.dll");
        string metadata = Path.Combine(gameRoot, "Tape to Tape_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        string assemblyHash = Sha256(assembly);
        TestedBuilds.TryGetValue(assemblyHash, out TestedBuild? tested);
        return new BuildFingerprint(assemblyHash, Sha256(metadata), tested);
    }

    /// <summary>
    /// How the installed game differs from the tested builds (<see cref="UntestedBuildNotice.Age"/>), from Steam's app manifest two folders
    /// above <paramref name="gameRoot"/> (a Steam library's <c>steamapps\common\&lt;game&gt;</c>): only its two build numbers are read.
    /// <paramref name="forLog"/> says what was found, with the numbers (never the file's path). Never throws.
    /// </summary>
    public static GameVersionAge SteamAge(string gameRoot, out string forLog)
    {
        long installed = 0;
        long target = 0;
        string? why = null;
        try
        {
            string manifest = Path.GetFullPath(Path.Combine(gameRoot, "..", "..", SteamAppManifest.FileName));
            var file = new FileInfo(manifest);
            if (!file.Exists)
            {
                why = "no Steam app manifest beside the game folder";
            }
            else if (file.Length > SteamAppManifest.MaxChars)
            {
                why = "Steam's app manifest is too big to be one";
            }
            else if (!SteamAppManifest.TryRead(File.ReadAllText(manifest), out installed, out target))
            {
                why = "Steam's app manifest has no build number";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            installed = target = 0;
            why = $"Steam's app manifest couldn't be read ({ex.GetType().Name})";
        }

        GameVersionAge age = UntestedBuildNotice.Age(installed, target, TestedSteamBuilds);
        forLog = UntestedBuildNotice.ForLog(age, installed, target, TestedSteamBuilds, why);
        return age;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

/// <summary>A game build the mod has been verified against.</summary>
/// <param name="Name">Human-readable name.</param>
/// <param name="MetadataSha256">Expected global-metadata.dat hash for this build.</param>
/// <param name="SteamBuild">Steam's build number for it (its app manifest's <c>buildid</c>), for the untested-build notice.</param>
internal sealed record TestedBuild(string Name, string MetadataSha256, long SteamBuild);
