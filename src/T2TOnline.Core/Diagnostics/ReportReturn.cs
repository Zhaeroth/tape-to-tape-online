using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>Where the game is as a problem report's prompt is answered, for the cursor's way back (<see cref="ReportReturn"/>).</summary>
/// <param name="AtTitle">The title scene is the active one.</param>
/// <param name="InSession">An online game is on, starting or ending on this PC.</param>
/// <param name="ScreenOpen">A screen of the mod's is open over the title, or on its way.</param>
/// <param name="PromptOpen">Another prompt of the mod's is up or on its way.</param>
/// <param name="MenuOnTop">A menu of the game's is shown over the title.</param>
/// <param name="GroupShown">The title shows the Online menu, at rest (not between two groups).</param>
/// <param name="RowThere">The Report a Problem row is there and active.</param>
/// <param name="ReportBusy">Another report is being made (the row says so and refuses a press).</param>
public readonly record struct ReportReturnLook(bool AtTitle, bool InSession, bool ScreenOpen, bool PromptOpen, bool MenuOnTop, bool GroupShown, bool RowThere, bool ReportBusy);

/// <summary>
/// The cursor's way back to the Online menu's Report a Problem row once its report's prompt is answered. A prompt closing over a title group
/// leaves the cursor to the game, which gives it to the group's default row: on the Online menu that is Host Lobby, a row whose press starts
/// an online game, right under a prompt whose focused button closes it. So the row that asked takes the cursor back, as the row above it
/// (Online Settings) gets it back from the game's own launcher; only while the Online menu is still what the player looks at, and only for a
/// report that row asked for (over the Settings menu the game puts the cursor back on the row by itself).
/// </summary>
public static class ReportReturn
{
    /// <summary>True for a report asked from the Online menu's row: the only one whose prompt hands the cursor back here.</summary>
    public static bool Applies(string? source) => string.Equals(source, ReportSources.OnlineMenu, StringComparison.Ordinal);

    /// <summary>Why the cursor is left where the game puts it, for the log; null when the row takes it.</summary>
    public static string? WhyNot(in ReportReturnLook look) =>
        !look.AtTitle ? "the title has gone"
        : look.InSession ? "an online game is on"
        : look.ScreenOpen ? "a screen is open over the title"
        : look.PromptOpen ? "another prompt is up"
        : look.MenuOnTop ? "a menu is on top"
        : !look.GroupShown ? "the title shows another group"
        : !look.RowThere ? "the row has gone"
        : look.ReportBusy ? "another report is being made"
        : null;
}
