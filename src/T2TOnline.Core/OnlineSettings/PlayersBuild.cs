using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>
/// The players' build (D142): with the DevTools setting off, a player has no F1 panel, no F7, Shift+F7, Ctrl+F5, Ctrl+F6 or other F-key,
/// and every function is on the game's own screens. These are the rules and the English texts that say where each function lives: the
/// menu names come from the words the screens show (<see cref="LobbyWords"/>), so a text and the row it names never drift apart. Texts with
/// no screen of their own (a log line, the corner notice, a file) are English, as the mod's other such texts are.
/// </summary>
public static class PlayersBuild
{
    /// <summary>The line the log says once, after the settings line, with the developer tools off.</summary>
    public const string PlayersModeLine = "Developer tools: off (the players' build).";

    /// <summary>The line the log says once, after the settings line, with the developer tools on.</summary>
    public const string DevelopersModeLine = "Developer tools: on (F1, F7, Shift+F7, Ctrl+F5, Ctrl+F6 and the check tours).";

    /// <summary>Said once when the settings file turns the native lobby off and the developer tools are off.</summary>
    public const string NativeLobbyLine = "Settings: NativeLobby reads as on: the players' build has no other way online.";

    /// <summary>The time the mark key is held to make a report where the native screens aren't used (D140).</summary>
    public const string HoldInsertAdvice = "hold Insert for 2 seconds to make a problem report";

    /// <summary>The game's own Settings menu's Online tab, as a path: "Settings > Online".</summary>
    public static string SettingsOnline { get; } = "Settings > " + English(LobbyWords.OnlineTab);

    /// <summary>The Save Backups row of <see cref="SettingsOnline"/>.</summary>
    public static string SaveBackups { get; } = SettingsOnline + " > " + English(LobbyWords.SaveBackups);

    /// <summary>The Problem Report row of <see cref="SettingsOnline"/>.</summary>
    public static string ProblemReport { get; } = SettingsOnline + " > " + English(LobbyWords.ProblemReport);

    /// <summary>The Leave Online Game row of <see cref="SettingsOnline"/> (a friend in an online game).</summary>
    public static string LeaveOnlineGame { get; } = SettingsOnline + " > " + English(LobbyWords.LeaveOnlineGame);

    /// <summary>The End Online Game row of <see cref="SettingsOnline"/> (the host of an online game).</summary>
    public static string EndOnlineGame { get; } = SettingsOnline + " > " + English(LobbyWords.EndOnlineGame);

    /// <summary>The Play Online With row of <see cref="SettingsOnline"/>.</summary>
    public static string PlayOnlineWith { get; } = SettingsOnline + " > " + English(LobbyWords.PlayOnlineWith);

    /// <summary>The title's Online menu row that makes a report: "Online > Report a Problem".</summary>
    public static string ReportAProblem { get; } = English(LobbyWords.Online) + " > " + English(LobbyWords.ReportAProblem);

    /// <summary>The title's Online menu row that finds a friend's game: "Online > Join Lobby".</summary>
    public static string JoinLobby { get; } = English(LobbyWords.Online) + " > " + English(LobbyWords.JoinLobby);

    /// <summary>The line that names the build mode (<see cref="PlayersModeLine"/> or <see cref="DevelopersModeLine"/>).</summary>
    public static string ModeLine(bool devTools) => devTools ? DevelopersModeLine : PlayersModeLine;

    /// <summary>
    /// Whether the native lobby is on: what the settings file says only with the developer tools on. A player can't turn it back on
    /// without them (the panel is the developer's), and without it there is no way to host: so it reads as on.
    /// </summary>
    public static bool NativeLobbyOn(bool devTools, bool inFile) => !devTools || inFile;

    /// <summary>True when <see cref="NativeLobbyLine"/> is due: the file says off, and it reads as on.</summary>
    public static bool NativeLobbyOverridden(bool devTools, bool inFile) => !devTools && !inFile;

    /// <summary>
    /// What a player does to report a problem, as a sentence's end ("..., then make a problem report in Settings > Online"): the native
    /// screens' row where they're used (<paramref name="nativeScreens"/>: a tested game build whose hooks all resolved), else holding Insert.
    /// </summary>
    public static string ReportAdvice(bool nativeScreens) => nativeScreens ? $"make a problem report in {SettingsOnline}" : HoldInsertAdvice;

    /// <summary>
    /// The way out of an online game: F7 for a developer; the row of Settings > Online for a player (<paramref name="host"/>: End Online
    /// Game, else Leave Online Game).
    /// </summary>
    public static string Leave(bool devTools, bool host) => devTools ? "F7" : host ? EndOnlineGame : LeaveOnlineGame;

    /// <summary>
    /// What still works when the native lobby's screens are off for a game (a failure): a developer's keys, else Steam's own way in.
    /// Hosting needs the title's Online row in the players' build, and the line says so.
    /// </summary>
    public static string WithoutLobby(bool devTools) => devTools
        ? "F1, F7 and Steam joins still work"
        : "Steam's Join Game and invites still join a friend's game, and hosting needs the title's Online row";

    /// <summary>
    /// The offline notice's last line (the corner, English): where the mod's functions are while online is off. On a build whose native
    /// screens are used, Settings > Online (Problem Report, Save Backups); elsewhere the held Insert (the words' own English), as the
    /// screens aren't there. A developer's line adds F1.
    /// </summary>
    public static string OfflineNoticeLine(bool nativeScreens, bool devTools)
    {
        string line = nativeScreens
            ? $"{SettingsOnline} has {English(LobbyWords.ProblemReport)} and {English(LobbyWords.SaveBackups)}."
            : English(LobbyWords.HoldInsert);
        return devTools ? line + " F1 opens the mod's panel." : line;
    }

    /// <summary>A word's English text (the first of its languages).</summary>
    private static string English(LobbyWord word) => word.Texts[0];
}
