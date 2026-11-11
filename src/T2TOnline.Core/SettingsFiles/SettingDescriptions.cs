namespace T2TOnline.Core.SettingsFiles;

/// <summary>
/// The descriptions <c>settings.cfg</c> shows above the settings a player reads (the player's entries, always written, and the two
/// developer entries a player may meet). They name the game's own screens, never a developer key the players' build doesn't have
/// (a Core test reads them all). The developer's other entries keep their descriptions where they're made.
/// </summary>
public static class SettingDescriptions
{
    /// <summary>ShareAchievements.</summary>
    public const string ShareAchievements = "Online: achievements earned together in a match or run unlock on every player's own Steam account. Off: you neither send nor receive them. Also in the game: Settings > Online.";

    /// <summary>ShareProgress.</summary>
    public const string ShareProgress = "Online campaign: a run played together counts on every player's own profile (stats, unlocks, bench quests, squad wins, run history), each at their own thresholds. Off: you send none, and what comes in waits until you turn it back on in the same online game. Also in the game: Settings > Online.";

    /// <summary>FightGraceWindow.</summary>
    public const string FightGraceWindow = "Online host: an AI punch at a friend waits up to their lag (at most 100 ms) for their block. Off: punches land on the host's timing.";

    /// <summary>AiStandIn.</summary>
    public const string AiStandIn = "Online host: the AI plays a friend's skater after they've been away 3 s, until they're back. Off: their skater stands still.";

    /// <summary>NurseRealScreen.</summary>
    public const string NurseRealScreen = "Online host: friends open the game's own Nurse procedure screen and follow your presses on it. Off: they see that screen as text over their event screen. Leave it on unless the real screen misbehaves.";

    /// <summary>NativeLobby: a developer setting (with DevTools off it reads as on, whatever the file says).</summary>
    public const string NativeLobby = "Developer: the Online row on the title screen, its group and the lobby's screens. Off works only with DevTools = true (the developer keys then run a session); without DevTools it reads as on, since the Online row is a player's way online.";

    /// <summary>OnlineInput.</summary>
    public const string OnlineInput = "Online friend: what you play with when you join a friend's game: Auto (a gamepad if one is connected, else the keyboard and mouse), Gamepad, or KeyboardMouse. Mouse buttons count as your keyboard maps bind them; the pointer stays on your PC. Also in the game: Settings > Online > Play Online With.";

    /// <summary>CoopSlot.</summary>
    public const string CoopSlot = "Online host: the save slot (1 to 3) your co-op campaign runs are kept in, apart from your solo run. Change it in the lobby's Co-op Save.";

    /// <summary>AllowDirectConnections.</summary>
    public const string AllowDirectConnections = "Online: let Steam connect you straight to the other players when it can. Off (the default): every connection goes through Steam's relays, so the players you connect to never learn your IP address. On: your IP address is shared with the players you connect to. Read when the game starts.";

    /// <summary>DevTools: the developer's switch for every developer key and the check tours.</summary>
    public const string DevTools = "Developer: the developer keys and the check tours (docs/DEVELOPMENT.md lists them). Off (the players' build): no key but Insert does anything, and every function is on the game's own screens.";

    /// <summary>ShowStatusBox.</summary>
    public const string ShowStatusBox = "Developer: the status box in the top-left corner (who is connected, how, and every notice) and the net overlay. Off: a player sees only the game's own toasts and prompts for what is wrong; the box then shows just what those can't carry.";
}
