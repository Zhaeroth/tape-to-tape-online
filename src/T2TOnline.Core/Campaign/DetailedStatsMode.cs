using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// One skater details panel's stat display options (the game's <c>SkaterDetailedStats</c>, each an
/// <c>SkaterStatBox.EDisplayOptions</c> as a whole number): the ones its stat boxes use now, its plain ones and its detailed
/// ones. The panel's own handlers of the "UI Left Trigger" action switch the current pair to the detailed pair while the
/// action is held and back to the plain pair when it's let go (<see cref="DetailedStatsMode"/>).
/// </summary>
/// <param name="CurrentBasic">The basic stats' options in use (<c>m_CurrentBasicStatsDisplayOptions</c>).</param>
/// <param name="CurrentOverall">The overall stat's options in use (<c>m_CurrentOverallStatDisplayOptions</c>).</param>
/// <param name="Basic">The plain basic stats' options (<c>m_BasicStatDisplayOptions</c>).</param>
/// <param name="Overall">The plain overall stat's options (<c>m_OverallStatDisplayOptions</c>).</param>
/// <param name="DetailedBasic">The detailed basic stats' options (<c>m_DetailedModeBasicStatDisplayOptions</c>).</param>
/// <param name="DetailedOverall">The detailed overall stat's options (<c>m_DetailedModeOverallStatDisplayOptions</c>).</param>
public readonly record struct StatDisplayOptions(int CurrentBasic, int CurrentOverall, int Basic, int Overall, int DetailedBasic, int DetailedOverall)
{
    /// <summary>True when the panel's detailed pair differs from its plain pair: only then does its mode show.</summary>
    public bool ShowsMode => Basic != DetailedBasic || Overall != DetailedOverall;

    /// <summary>True when the panel shows its detailed stats now.</summary>
    public bool Detailed => ShowsMode && CurrentBasic == DetailedBasic && CurrentOverall == DetailedOverall;

    /// <summary>True when the panel shows its plain stats now.</summary>
    public bool Plain => CurrentBasic == Basic && CurrentOverall == Overall;
}

/// <summary>
/// The skater details panels' detailed stats (v1). A <c>SkaterDetailedStats</c> panel (the lineup's details box and its
/// compare box, the run-start lineup's) listens to the "UI Left Trigger" action straight from Rewired, on every player that
/// controls the menus (<c>RegisterDetailedStatsInput</c>: just pressed and just released), not through a shortcut of its
/// menu, so the host's press reached only the host's panels. The host's menu data says whether its panels show their
/// detailed stats (<see cref="Key"/>); a friend's copy switches its own panels with the handlers' own two writes and the
/// panel's own refresh.
/// </summary>
public static class DetailedStatsMode
{
    /// <summary>The menu data's value: 1 while the host's panels show their detailed stats, 0 while they show the plain ones.</summary>
    public const string Key = "details.detailedStats";

    /// <summary>
    /// Host: the menu's value from its panels (only the ones up now): 1 when one shows its detailed stats, 0 when none does,
    /// null when no panel's mode shows (no panel, or none whose detailed options differ from its plain ones).
    /// </summary>
    public static int? HostValue(IEnumerable<StatDisplayOptions> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        int? value = null;
        foreach (StatDisplayOptions panel in panels)
        {
            if (!panel.ShowsMode)
            {
                continue;
            }

            if (panel.Detailed)
            {
                return 1;
            }

            value = 0;
        }

        return value;
    }

    /// <summary>
    /// Friend: the mode a panel of this PC's copy is to be switched to for the host's value: true (detailed), false (plain),
    /// or null when it shows the host's already, its mode doesn't show, or the value isn't one (an older host sends none:
    /// -1; anything else from the network is ignored).
    /// </summary>
    public static bool? Switch(StatDisplayOptions panel, long hostValue)
    {
        if (hostValue is not (0 or 1) || !panel.ShowsMode)
        {
            return null;
        }

        bool wanted = hostValue == 1;
        if (wanted ? panel.Detailed : panel.Plain)
        {
            return null;
        }

        return wanted;
    }
}
