using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What the log says about the game's folder instead of its path (D127): what kind of place it is (a Steam library,
/// Program Files, the user's profile, OneDrive), the kind of drive, whether the path is plain ASCII and how long it is.
/// The things a folder changes about how the game and MelonLoader run, without the folder: a path under the user's
/// profile holds their Windows user name.
/// </summary>
public static class GameFolderShape
{
    /// <summary>
    /// The line: <c>Game folder: a Steam library on a fixed drive, outside the user profile, ASCII, 47 characters.</c>
    /// </summary>
    /// <param name="gameRoot">The game's folder.</param>
    /// <param name="userProfile">The Windows user's profile folder, or null when it couldn't be read.</param>
    /// <param name="driveKind">The drive's kind in words ("fixed", "removable", "network"), or null when unknown.</param>
    public static string Describe(string? gameRoot, string? userProfile, string? driveKind)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return "Game folder: unknown.";
        }

        string path = gameRoot.Replace('/', '\\').TrimEnd('\\');
        var facts = new List<string>
        {
            path.Contains("\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase) ? "a Steam library" : "not in a Steam library",
        };
        if (!string.IsNullOrEmpty(driveKind))
        {
            facts[0] += $" on a {driveKind} drive";
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            facts.Add("on a network share");
        }

        if (path.Contains("\\Program Files", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add("under Program Files");
        }

        string profile = (userProfile ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
        bool inProfile = profile.Length > 3 && (path.Equals(profile, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase));
        facts.Add(profile.Length <= 3 ? "the user profile unknown" : inProfile ? "inside the user profile" : "outside the user profile");
        if (path.Contains("\\OneDrive", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add("in OneDrive");
        }

        bool ascii = true;
        foreach (char c in path)
        {
            ascii &= c < 128;
        }

        facts.Add(ascii ? "ASCII" : "with non-ASCII characters");
        facts.Add(string.Create(CultureInfo.InvariantCulture, $"{path.Length} characters"));
        return $"Game folder: {string.Join(", ", facts)}.";
    }
}
