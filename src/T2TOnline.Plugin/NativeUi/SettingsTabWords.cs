using T2TOnline.Core.Lobby;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>The terms the Settings tab, its rows and the Online group's Online Settings row show, all in one place (the mod's words, in the game's 15 languages).</summary>
internal static class SettingsTabWords
{
    /// <summary>The tab's label.</summary>
    public static string OnlineTab => LobbyWords.OnlineTab.Key;

    /// <summary>The Share Achievements row's title (and its tooltip's title).</summary>
    public static string ShareAchievements => LobbyWords.ShareAchievements.Key;

    /// <summary>The Share Achievements row's tooltip text.</summary>
    public static string ShareAchievementsTip => LobbyWords.ShareAchievementsTip.Key;

    /// <summary>The Save Backups row's title, its locked tooltip's title, and the Backups screen's header.</summary>
    public static string SaveBackups => LobbyWords.SaveBackups.Key;

    /// <summary>The Save Backups row's button.</summary>
    public static string View => LobbyWords.View.Key;

    /// <summary>The Save Backups row's locked tooltip text (out of the title).</summary>
    public static string SaveBackupsLocked => LobbyWords.SaveBackupsLocked.Key;

    /// <summary>The Online group's Online Settings row.</summary>
    public static string OnlineSettings => LobbyWords.OnlineSettings.Key;
}
