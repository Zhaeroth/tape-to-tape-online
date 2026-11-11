using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The <c>README.txt</c> the mod keeps in each of its two data folders, and the developer folder's notice: what each
/// folder holds, what deleting it loses, that nothing in it is sent anywhere, and how to report a bug. Plain ASCII with
/// Windows line ends, so Notepad shows it as written. Written at start-up when missing or different.
/// </summary>
public static class DataFolderReadme
{
    /// <summary>The file name in each folder.</summary>
    public const string FileName = "README.txt";

    /// <summary>The developer folder's notice file name (<c>UserData\T2TOnline\dev\</c>).</summary>
    public const string DevNoticeFileName = "DO-NOT-SHARE.txt";

    /// <summary>The text for <c>&lt;game&gt;\UserData\T2TOnline\README.txt</c>.</summary>
    public static string GameFolderText { get; } = Lines(
        "T2T Online (a mod for Tape to Tape): its files in the game folder",
        "",
        "This folder belongs to the T2T Online mod. Nothing in it is sent anywhere.",
        "",
        "settings.cfg   The mod's settings; a player's are in Settings > Online.",
        "               Delete this file to go back to the defaults.",
        "logs\\          One log per game start, kept for a while to help fix bugs.",
        "reports\\       The bug reports you made (the newest few are kept).",
        "campaign\\      Files that last one online session at most.",
        "custom\\        Files that last one online session at most.",
        "dev\\           Developer tools only (see dev\\DO-NOT-SHARE.txt).",
        "",
        "To report a problem: in the game, Settings > Online > Problem Report (or",
        "Online > Report a Problem on the title screen). It makes one zip that is",
        "meant to be shared, so you don't need to send anything from here by hand.",
        "",
        "With the game closed, deleting this folder removes the mod's settings and",
        "logs. Your game saves are kept by the game itself, and your co-op runs and",
        "save backups are kept in %LOCALAPPDATA%\\T2T Online (its README.txt says what",
        "deleting them loses).");

    /// <summary>The text for <c>%LOCALAPPDATA%\T2T Online\README.txt</c>.</summary>
    public static string DataFolderText { get; } = Lines(
        "T2T Online (a mod for Tape to Tape): your co-op runs and save backups",
        "",
        "The T2T Online mod keeps these files here, outside the game folder, so that",
        "reinstalling the game or the mod can't delete them. Nothing in this folder is",
        "sent anywhere, and the game itself never reads it.",
        "",
        "coop\\           A host's co-op campaign runs (save slots 1 to 3). These are the",
        "                only copies: deleting them loses those co-op runs for good.",
        "                (A .bac file beside one is the game's own leftover copy.)",
        "backups\\        Copies of your game saves and co-op runs, taken before online",
        "                sessions; Settings > Online > Save Backups restores one.",
        "                Deleting them loses the backups, not your saves.",
        "progress\\       What friends' games shared with yours, so nothing counts",
        "                twice, a host's per-run counters, and how much each host",
        "                changed your stats in the last day, with how long you played",
        "                with it (a host can change only what that play explains).",
        "                host-key.bin is this PC's own secret for those records:",
        "                never share it.",
        "history-logos\\  Hosts' team logos, shown on your run history page.",
        "",
        "To remove T2T Online completely, delete this folder too, with the game closed.",
        "Your own game saves are kept by the game itself and stay.",
        "",
        "To report a problem: in the game, Settings > Online > Problem Report.");

    /// <summary>
    /// The text for <c>&lt;game&gt;\UserData\T2TOnline\README.txt</c> on a PC without a local application data folder, where
    /// that folder also keeps the co-op runs and backups.
    /// </summary>
    public static string SharedFolderText { get; } = GameFolderText[..GameFolderText.IndexOf("With the game closed", StringComparison.Ordinal)] + Lines(
        "This PC has no %LOCALAPPDATA% folder, so this folder also keeps:",
        "",
        "coop\\           A host's co-op campaign runs (save slots 1 to 3). These are the",
        "                only copies: deleting them loses those co-op runs for good.",
        "backups\\        Copies of your game saves and co-op runs, taken before online",
        "                sessions; Settings > Online > Save Backups restores one.",
        "progress\\       What friends' games shared with yours, so nothing counts",
        "                twice, a host's per-run counters, and how much each host",
        "                changed your stats in the last day, with how long you played",
        "                with it (a host can change only what that play explains).",
        "                host-key.bin is this PC's own secret for those records:",
        "                never share it.",
        "history-logos\\  Hosts' team logos, shown on your run history page.",
        "",
        "With the game closed, deleting this folder removes the mod's settings, logs,",
        "co-op runs and backups for good. Your game saves are kept by the game itself.");

    /// <summary>The text for <c>UserData\T2TOnline\dev\DO-NOT-SHARE.txt</c>.</summary>
    public static string DevNoticeText { get; } = Lines(
        "T2T Online developer output: do not share this folder",
        "",
        "Only the mod's developer tools write here (the DevTools setting).",
        "",
        "ui-check\\   The check tours' screenshots. They can show your Steam friends",
        "            list. Never share them or put them in a bug report.",
        "captures\\   Recordings of a few seconds of a match.",
        "streams\\    Recordings of whole matches.",
        "",
        "Each folder keeps only its newest few. Safe to delete with the game closed.");

    /// <summary>True when a README holding <paramref name="existing"/> (null: none) must be written with <paramref name="wanted"/>.</summary>
    public static bool NeedsWrite(string? existing, string wanted) => !string.Equals(existing, wanted, StringComparison.Ordinal);

    private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";
}
