namespace T2TOnline.Core.Lobby;

/// <summary>
/// What the native lobby needs that the game already has a word for: the need, and the game's own term. Screens put these
/// on cloned labels (<c>Localize.SetTerm</c>), so they read as the game does in all 15 languages, and no game text is copied
/// here. <c>tools/checks/lobbywords_check.py</c> checks each still exists in the game's source. "Quick Game" is the
/// game's word, never "Quick Play". Each stays on one line: that tool reads them by line.
/// </summary>
public static class LobbyTerms
{
    /// <summary>Start.</summary>
    public const string Start = "general/start";

    /// <summary>Back.</summary>
    public const string Back = "general/back";

    /// <summary>Ready!, the word on the game's own ready banner.</summary>
    public const string Ready = "v2ui/controller_assignment/player_ready";

    /// <summary>{0}/{1} Players, a formatted count for a header.</summary>
    public const string PlayersOf = "v2/ui/controller_assignment/player_count";

    /// <summary>Game Mode, the mode selector's label.</summary>
    public const string GameMode = "v2ui/choose_meta/game_mode";

    /// <summary>Campaign, a mode.</summary>
    public const string Campaign = "v2ui/title_screen/campaign";

    /// <summary>Quick Game, a mode.</summary>
    public const string QuickGame = "v2ui/title_screen/quick_match";

    /// <summary>New Run: Start's word on an empty slot, and the second prompt on a used one.</summary>
    public const string NewRun = "v2ui/title_screen/new_run";

    /// <summary>Continue Run: Start's word on a used slot.</summary>
    public const string ContinueRun = "v2ui/title_screen/continue_run";

    /// <summary>Act {0}.</summary>
    public const string ActN = "v2ui/run_end/act";

    /// <summary>Select.</summary>
    public const string Select = "navbar/select";

    /// <summary>Confirm.</summary>
    public const string Confirm = "general/confirm";

    /// <summary>Cancel: the countdown's and the Wait's B.</summary>
    public const string Cancel = "general/cancel";

    /// <summary>Close: the close prompt's answer.</summary>
    public const string Close = "general/close";

    /// <summary>Ok: a failure's only button.</summary>
    public const string Ok = "general/ok";

    /// <summary>Yes.</summary>
    public const string Yes = "general/yes";

    /// <summary>No.</summary>
    public const string No = "general/no";

    /// <summary>Settings: a player's own settings from the lobby.</summary>
    public const string Settings = "general/settings";

    /// <summary>Loading..., a seat's state.</summary>
    public const string Loading = "ui/loading_screen/title";

    /// <summary>Auto: Play Online With's first choice (the game's Controller Icon Type row's own word).</summary>
    public const string Auto = "settings/graphics/controller/auto";

    /// <summary>Not Now: the offer's safe answer (blank in Italian in the game's own source, where the game falls back).</summary>
    public const string NotNow = "general/not_now";

    /// <summary>Quit: the saves-restored prompt's answer that closes the game (the game's own quit word, as on its quit prompt).</summary>
    public const string Quit = "general/quit";

    /// <summary>Controller, a device.</summary>
    public const string Controller = "settings/controls/gamepad";

    /// <summary>Keyboard, a device.</summary>
    public const string Keyboard = "settings/controls/keyboard";
}
