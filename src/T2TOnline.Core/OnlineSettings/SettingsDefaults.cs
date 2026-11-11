using System;
using System.Collections.Generic;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>
/// The Online panel's place among the Settings menu's default selections: the list the game's navigation walks, in order, when nothing valid is
/// selected (a prompt over the menu has closed, the mouse was left for the pad, a direction is pressed with no cursor). It selects the first entry
/// that is alive, active and navigable, and looks no further than the menu on top. The game's own list names each tab's rows first (only the shown
/// tab's are active and navigable, so that tab's last row gets the cursor back), then the bottom bar and the whole content. A tab whose rows aren't
/// in the list gets no cursor back; the Online panel's rows go in after the last of the game's panels, before what follows them.
/// </summary>
public static class SettingsDefaults
{
    /// <summary>The most entries a menu's list is taken to have (the game's Settings menu has eight).</summary>
    public const int MostEntries = 64;

    /// <summary>
    /// The place for the new entry: after the last entry that is a panel's (<paramref name="isPanel"/>, in the list's order), or at the end when
    /// none is.
    /// </summary>
    public static int InsertIndex(IReadOnlyList<bool> isPanel)
    {
        ArgumentNullException.ThrowIfNull(isPanel);
        for (int i = isPanel.Count - 1; i >= 0; i--)
        {
            if (isPanel[i])
            {
                return i + 1;
            }
        }

        return isPanel.Count;
    }
}
