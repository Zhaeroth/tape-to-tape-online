using System.Reflection;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// This build's version, from the assembly's informational version (Directory.Build.props, plus the commit the SDK adds:
/// "0.9.2-beta+3c337e6..."). <see cref="Release"/> is what's sent (the lobby, the handshake) and logged, and
/// <see cref="ReleaseVersion.ForScreen"/> is how a player reads it ("0.9.2 Beta"); <see cref="Full"/>, with the commit, goes to the log.
/// </summary>
internal static class ModVersion
{
    /// <summary>The informational version, commit included ("0.9.2-beta+3c337e6...").</summary>
    public static string Full { get; } =
        typeof(ModVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? BuildVersion.Release;

    /// <summary>The release ("0.9.2-beta"): sent to the other PCs and logged.</summary>
    public static string Release { get; } = ReleaseVersion.Release(Full);

    /// <summary>The commit it was built from (12 characters), or empty when the build didn't record one.</summary>
    public static string Commit { get; } = ReleaseVersion.Commit(Full);

    /// <summary>For the log: the release and, when known, the commit ("0.9.2-beta, commit 3c337e6f0a1b").</summary>
    public static string ForLog => Commit.Length > 0 ? $"{Release}, commit {Commit}" : Release;

    /// <summary>
    /// What to show when the other PC (<paramref name="who"/>, a clean display name) runs another release of the mod on the
    /// same protocol, so they connect but may differ; null when the releases match. <paramref name="hosting"/>: this PC
    /// hosts (else the other PC does). <paramref name="theirs"/> came over the network: it's cleaned before it's shown.
    /// </summary>
    public static string? MismatchNotice(string who, string? theirs, bool hosting)
    {
        if (ReleaseVersion.SameRelease(theirs, Release))
        {
            return null;
        }

        string their = SafeText.Clean(ReleaseVersion.ForScreen(theirs), 32);
        string ours = ReleaseVersion.ForScreen(Release);
        return hosting
            ? $"{who} runs T2T Online {their}, you run {ours}: ask them to install your release, so you both run the same version."
            : $"{who} runs T2T Online {their}, you run {ours}: install the host's release, so you both run the same version.";
    }
}
