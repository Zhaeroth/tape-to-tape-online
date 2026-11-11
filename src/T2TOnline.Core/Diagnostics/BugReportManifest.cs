using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// A bug report's <c>report.txt</c>: plain text a player can read before posting it. When and why it was made (UTC), the
/// builds, the PC's OS and runtime, the settings not at their defaults, other mods and mod loaders, this PC's tags in each
/// lobby, the marks, every file with its size and SHA-256, what was left out and why, what was cleaned, and what a report
/// never holds. Windows line ends, so Notepad shows it as written.
/// </summary>
public static class BugReportManifest
{
    /// <summary>The promise every report makes (D127), word for word.</summary>
    public const string Promise = "This report holds no Steam names, Steam IDs, folder names or local times; players appear as tags like P#3fa9c1.";

    /// <summary>The manifest's text.</summary>
    public static string Text(BugReportFacts facts, BugReportResult result)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder(2048);
        Line(text, "T2T Online bug report");
        Line(text, string.Create(CultureInfo.InvariantCulture, $"Made at {facts.MadeUtc:yyyy-MM-dd HH:mm:ss} UTC{(facts.Why.Length > 0 ? $" ({facts.Why})" : string.Empty)}."));
        Line(text, string.Empty);
        Line(text, Promise);
        Line(text, "Attach this zip to a bug report as it is. If the problem involved friends, each player makes one.");
        Line(text, string.Empty);
        Line(text, $"T2T Online: {Or(facts.Release, "unknown")}{(facts.Commit.Length > 0 ? $", commit {facts.Commit}" : string.Empty)}, protocol {facts.Protocol.ToString(CultureInfo.InvariantCulture)}");
        Line(text, $"Game build: {Or(facts.GameBuild, "unknown")}");
        Line(text, $"MelonLoader: {Or(facts.MelonLoader, "unknown")}");
        Line(text, $"OS: {Or(facts.Os, "unknown")}");
        Line(text, $".NET: {Or(facts.Runtime, "unknown")}");
        Line(text, $"Settings not at their defaults: {Or(facts.Settings, "unknown")}");
        Line(text, $"Other MelonLoader mods and plugins: {List(facts.OtherMods)}");
        Line(text, $"Other mod loaders' files in the game folder: {List(facts.OtherLoaders)}");
        Line(text, string.Empty);

        Line(text, "This PC's tags (from the session logs' \"Log tags:\" lines; a friend's report names the same lobby and host):");
        Items(text, result.Tags, "none (no lobby in these logs)");
        Line(text, string.Empty);
        Line(text, "Marks (Insert):");
        Items(text, result.Marks, "none");
        Line(text, string.Empty);

        Line(text, "Files:");
        foreach (ReportFileRow file in result.Files)
        {
            string cut = file.LeftOutBytes > 0
                ? string.Create(CultureInfo.InvariantCulture, $"; {file.LeftOutBytes:N0} bytes of its middle left out")
                : string.Empty;
            Line(text, string.Create(CultureInfo.InvariantCulture, $"  {file.Name}  {file.Bytes:N0} bytes  SHA-256 {file.Sha256}  ({file.What}{cut})"));
        }

        if (result.Files.Count == 0)
        {
            Line(text, "  none");
        }

        if (result.LeftOut.Count > 0 || facts.NotFound.Count > 0 || facts.Refused.Count > 0)
        {
            Line(text, "Left out:");
            foreach (ReportLeftOut left in result.LeftOut)
            {
                Line(text, $"  {left.Name}: {left.Why}");
            }

            foreach (string missing in facts.NotFound)
            {
                Line(text, $"  {missing}: not found");
            }

            foreach (string refused in facts.Refused)
            {
                Line(text, $"  {refused}: not read (in a folder a report never reads)");
            }
        }

        Line(text, string.Empty);
        LogScrubCounts c = result.Cleaned;
        Line(text, string.Create(CultureInfo.InvariantCulture,
            $"Cleaned: {c.SteamIds:N0} Steam IDs, {c.Lobbies:N0} lobby IDs, {c.Names:N0} Steam names, {c.Texts:N0} custom names, {c.Folders:N0} folders, {c.Addresses:N0} IP addresses, " +
            $"{c.Offsets:N0} time zone offsets, {c.Controls:N0} control characters; {result.MelonTimes:N0} MelonLoader times converted to UTC; " +
            $"{result.GraphicsLines:N0} graphics card lines reduced to the maker; {result.GameNames:N0} names in the game's own lines (skaters, teams, logos) " +
            $"reduced to their length and a hash."));
        Line(text, "Never in a report: saves, backups, co-op slots, settings files, developer output, crash dumps.");
        return text.ToString();
    }

    private static string Or(string? value, string otherwise) => string.IsNullOrWhiteSpace(value) ? otherwise : SafeText.Clean(value, 400);

    private static string List(IReadOnlyList<string> items) => items.Count == 0 ? "none" : SafeText.Clean(string.Join(", ", items), 600);

    private static void Items(StringBuilder text, IReadOnlyList<string> items, string none)
    {
        if (items.Count == 0)
        {
            Line(text, $"  {none}");
            return;
        }

        foreach (string item in items)
        {
            Line(text, $"  {item}");
        }
    }

    private static void Line(StringBuilder text, string line) => text.Append(line).Append("\r\n");
}
