using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The mod's version as the build stamps it (an assembly's informational version, such as "0.9.2-beta+3c337e6f..."): the
/// release sent in the lobby and the handshake ("0.9.2-beta"), shown on screen as <see cref="ForScreen"/> says, and the commit it was built from (logged).
/// Two PCs run the same build of the mod when their releases match; every package gets its own release (the package
/// script refuses to write a second zip under one), so a rebuilt zip can always be told apart.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>What's shown when the version is missing.</summary>
    public const string Unknown = "unknown";

    /// <summary>The release: the version before any '+' (build metadata), trimmed; <see cref="Unknown"/> when there's none.</summary>
    public static string Release(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return Unknown;
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        string release = (plus < 0 ? informational : informational[..plus]).Trim();
        return release.Length == 0 ? Unknown : release;
    }

    /// <summary>A release as a player reads it: "0.9.2-beta" is "0.9.2 Beta". Any other release reads as it is, an older peer's "0.9.1-m4" too.</summary>
    public static string ForScreen(string? release)
    {
        const string Beta = "-beta";
        string text = Release(release);
        return text.EndsWith(Beta, StringComparison.OrdinalIgnoreCase) ? text[..^Beta.Length] + " Beta" : text;
    }

    /// <summary>
    /// The commit the build was made from: the build metadata after '+', cut to <paramref name="length"/> characters; empty
    /// when there's none.
    /// </summary>
    public static string Commit(string? informational, int length = 12)
    {
        if (length < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The length must be at least 1.");
        }

        int plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (plus < 0)
        {
            return string.Empty;
        }

        string commit = informational![(plus + 1)..].Trim();
        return commit.Length <= length ? commit : commit[..length];
    }

    /// <summary>
    /// True when both name the same release (build metadata ignored, letter case too). A missing version on either side
    /// never matches.
    /// </summary>
    public static bool SameRelease(string? one, string? other)
    {
        string a = Release(one);
        string b = Release(other);
        return a != Unknown && b != Unknown && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
