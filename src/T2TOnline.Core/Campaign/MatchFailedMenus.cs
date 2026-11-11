namespace T2TOnline.Core.Campaign;

/// <summary>
/// A friend's game whose load of the host's match failed, so it sits on the world map or at the title while the host plays
/// that match: which of the host's menus it keeps off its screen, and which of the run's end it shows for real though the
/// host's is in the match's scene. Pure logic.
/// </summary>
public static class MatchFailedMenus
{
    /// <summary>The in-match menu's class (pause, end of period, end of game).</summary>
    public const string Game = "GameMenu";

    /// <summary>The glossary's class (opened from the in-match menu).</summary>
    public const string Glossary = "GlossaryMenu";

    /// <summary>The brawl menu's class.</summary>
    public const string Brawl = "BrawlFightMenu";

    /// <summary>The Canada curse's pick menu's class (opened in the match).</summary>
    public const string CanadaCurse = "CanadaCurseMenu";

    /// <summary>The lineup menu's class (the match team's, in the match).</summary>
    public const string Lineup = "LineupMenu";

    /// <summary>
    /// True for the match's own menus: the in-match menu, the glossary from it, the brawl, the controller screen, the Canada
    /// curse's pick, the match team's lineup. While the match couldn't load here they're kept off this PC's screen, not even
    /// as text: they'd only cover the screen it's on. The run's menus (rewards, events, the run's end, new unlocks) aren't.
    /// </summary>
    public static bool KeptOff(string type) =>
        type is Game or Glossary or Brawl or MenuExits.ControllerScreen or CanadaCurse or Lineup;

    /// <summary>
    /// True when the host's run-end screen of <paramref name="type"/> shows for real on this PC's own screen though the host's
    /// is in the match's scene. The run-end menu at its run's end (<paramref name="runsEnd"/>) is built here from the host's run
    /// summary alone, in the menu's history mode, so it shows on the world map (<paramref name="onMap"/>: the game shows the
    /// same menu there for a run that ends on the map, and as the map menu's "Run stats") and at the title
    /// (<paramref name="atTitle"/>: its run history). The new-unlocks menu shows on the world map (the game's run's end on the
    /// map shows it there); the game never shows it at the title. Anything else, anywhere else: text.
    /// </summary>
    public static bool ShowsHere(string type, bool runsEnd, bool onMap, bool atTitle) => type switch
    {
        MenuExits.RunEnd => runsEnd && (onMap || atTitle),
        MenuExits.PermanentUnlock => onMap,
        _ => false,
    };
}
