using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// Whether the game's title takes a Back press from one of its groups now, by the title's own back handler's guards
/// (<c>TitleScreen.IBackHandler.HandleBackInput</c>, verified in the game's code): it does nothing (and still takes the press) while
/// a button's click is being handled, while the title is leaving, while any menu is still over it (<c>m_MenusOverSelf</c>: a menu
/// counts until it is destroyed, after its hide, so a press right after a screen starts to close is lost), and while a group
/// transition runs whose input isn't ready; from any group but press-to-start and Main it then goes to Main. A check tour waits
/// for this before it presses Back there.
/// </summary>
public static class TitleBack
{
    /// <summary>The title's Main group (<c>TitleScreen.EButtonGroup.Main</c>); press-to-start is 0, the other groups above.</summary>
    public const int MainGroup = 1;

    /// <summary>
    /// Null when a Back press now takes the title from <paramref name="group"/> to Main, else why it would be lost, in the
    /// summary's words.
    /// </summary>
    public static string? WhyNot(int group, int menusOver, bool clicking, bool exiting, bool transitioning, bool groupInputReady) =>
        group <= MainGroup ? string.Create(CultureInfo.InvariantCulture, $"the title is on group {group}, not one Back leaves for Main")
        : clicking ? "a title button's click is being handled"
        : exiting ? "the title is leaving"
        : menusOver != 0 ? string.Create(CultureInfo.InvariantCulture, $"{menusOver} menu(s) still over the title (one closing is counted until it is destroyed)")
        : transitioning && !groupInputReady ? "the title's group change runs and doesn't take input yet"
        : null;
}
