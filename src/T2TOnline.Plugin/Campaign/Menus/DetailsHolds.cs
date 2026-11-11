#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// "Hold for details" on a friend's real views (v1). The game shows more of a menu only while a hold shortcut is down (the
/// left trigger on a pad, "UI Left Trigger"): a <c>KeywordsTooltip</c>'s boxes under the selected card stay at alpha 0 until
/// its hold input's <c>HoldStart</c> event sets its <c>m_inputPressed</c> (the reward menus, the event menu, the run's end,
/// the Canada curse, the new-unlocks menu, the lineup), the old squad menu spills the selected skater's talents
/// (<c>OnDetailsPressed</c>), the event menu shows its cards' details, the squad screen and <c>ShowWhileHoldingInput</c> fade
/// their details in, a lineup's <c>SkaterDetailedStats</c> shows its compare box. A friend's own input does nothing while it
/// follows, and the host's press happens on the host's PC only, so none of it showed on friends. The host now sends each hold
/// and release of a shortcut in a shared menu (<c>UIInputShortcutBase.FireEvent</c> with <c>HoldStart</c> or
/// <c>HoldRelease</c>) as a value on the shortcut's path (<see cref="MenuHold"/>), and a friend's copy fires the same
/// shortcut's own hold event, so every handler the game registered on it runs as on the host's screen. Only the hold events
/// are fired, never the trigger (a hold-to-confirm shortcut's action stays the host's). One details view listens to the
/// button itself rather than to a shortcut: a skater details panel (<c>SkaterDetailedStats</c>, in the lineup and the
/// run-start lineup) switches its stat boxes to their detailed options on the "UI Left Trigger" action straight from
/// Rewired; its state goes with the menu's data instead (<see cref="ReadDetailedStats"/>, <see cref="FollowDetailedStats"/>).
/// </summary>
internal static class DetailsHolds
{
    /// <summary>How long after the first hold in a kind of menu its screen is read out for the log (its keyword boxes come in a few frames).</summary>
    public const double ReadoutSeconds = 0.6;

    // A menu's skater details panels, by menu object's instance id (few menus are kept at a time), at most this many each.
    private const int MaxPanelMenus = 16;
    private const int MaxPanels = 8;

    private static readonly Dictionary<int, SkaterDetailedStats[]> PanelsByMenu = new();

    // Host: the kinds of menu whose first detailed stats were logged.
    private static readonly HashSet<string> HostDetailedKinds = new(StringComparer.Ordinal);

    /// <summary>
    /// Host: true when <paramref name="type"/> is a hold going down (<paramref name="held"/>) or up. <c>Release</c> fires the
    /// release with <c>InputPressedWhileCantTrigger</c> when it came from a can't-trigger state; that's a release too.
    /// </summary>
    public static bool IsHoldEvent(UIInputShortcutBase.EEventType type, out bool held)
    {
        held = type == UIInputShortcutBase.EEventType.HoldStart;
        return type is UIInputShortcutBase.EEventType.HoldStart or UIInputShortcutBase.EEventType.HoldRelease
            or UIInputShortcutBase.EEventType.InputPressedWhileCantTrigger;
    }

    /// <summary>
    /// Friend: the shortcut <paramref name="target"/> names under <paramref name="root"/>: the path followed, its end checked
    /// by name and for a shortcut. Null when this copy has none there.
    /// </summary>
    public static UIInputShortcutBase? Find(Transform root, MenuTarget target)
    {
        if (target.Kind != MenuTargetKind.Path)
        {
            return null;
        }

        Transform? at = root;
        foreach (int step in target.Path)
        {
            if (at == null || step >= at.childCount)
            {
                return null;
            }

            at = at.GetChild(step);
        }

        UIInputShortcutBase? shortcut = at == null ? null : at.GetComponent<UIInputShortcutBase>();
        return shortcut != null && SafeText.Clean(shortcut.gameObject.name, MenuTarget.MaxLeafBytes) == SafeText.Clean(target.Leaf, MenuTarget.MaxLeafBytes)
            ? shortcut
            : null;
    }

    /// <summary>
    /// Friend: fires the shortcut's own hold (<paramref name="held"/>) or release event, as its <c>Press</c> and <c>Release</c>
    /// do on the host's PC (the handlers, then the shortcut's sound). Its own button state is left alone, so it never counts a
    /// hold time and never triggers. Run inside <see cref="Scenes.FriendUi.Apply"/> (the friend's lock refuses
    /// <c>FireEvent</c> otherwise). Throws what the game throws.
    /// </summary>
    public static void Fire(UIInputShortcutBase shortcut, bool held)
    {
        if (held)
        {
            shortcut.FireEvent(shortcut.OnInputShortcutHoldStart, UIInputShortcutBase.EEventType.HoldStart);
        }
        else
        {
            shortcut.FireEvent(shortcut.OnInputShortcutHoldReleased, UIInputShortcutBase.EEventType.HoldRelease);
        }
    }

    /// <summary>
    /// What <paramref name="shortcut"/>'s hold shows in <paramref name="menu"/>, in words, for the log ("the keyword boxes
    /// (1), the selected skater's talents"); "" when none of the game's known details handlers listens to it (a
    /// hold-to-confirm shortcut: its hold only fills its button and plays its sound).
    /// </summary>
    public static string Shows(AbstractMenu menu, UIInputShortcutBase shortcut)
    {
        var parts = new List<string>();
        IntPtr pointer = shortcut.Pointer;
        int keywords = 0;
        foreach (KeywordsTooltip tooltip in menu.GetComponentsInChildren<KeywordsTooltip>(true))
        {
            keywords += tooltip != null && tooltip.m_HoldToShowInput?.Pointer == pointer ? 1 : 0;
        }

        if (keywords > 0)
        {
            parts.Add($"the keyword boxes ({keywords})");
        }

        int stats = 0;
        foreach (SkaterDetailedStats panel in menu.GetComponentsInChildren<SkaterDetailedStats>(true))
        {
            stats += panel != null && panel.m_HoldToShowInput?.Pointer == pointer ? 1 : 0;
        }

        if (stats > 0)
        {
            parts.Add($"the detailed stats ({stats})");
        }

        int faded = 0;
        foreach (ShowWhileHoldingInput fade in menu.GetComponentsInChildren<ShowWhileHoldingInput>(true))
        {
            faded += fade != null && fade.m_HoldInput?.Pointer == pointer ? 1 : 0;
        }

        if (faded > 0)
        {
            parts.Add($"the held details ({faded})");
        }

        if (menu.TryCast<EventChoiceMenu>() is { } events &&
            (events.m_ShowDetailsShortcut?.Pointer == pointer || events._alternateShowDetailsShortcut?.Pointer == pointer))
        {
            parts.Add("the cards' details");
        }
        else if (menu.TryCast<OldSquadMenu>() is { } oldSquad && oldSquad.m_DetailsShortcut?.Pointer == pointer)
        {
            parts.Add("the selected skater's talents");
        }
        else if (menu.TryCast<ChooseMetaMenu>() is { } squads && squads.m_HoldForDetailsInput?.Pointer == pointer)
        {
            parts.Add("the squad's details");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// What <paramref name="shortcut"/>'s hold shows in <paramref name="menu"/> right now, for the log, in the same words on the
    /// host and on a friend so the two logs compare: each keyword tooltip on it (up or not, and its boxes' titles), the
    /// detailed stats panels and the old squad menu's and the event menu's details state.
    /// </summary>
    public static string Readout(AbstractMenu menu, UIInputShortcutBase shortcut)
    {
        IntPtr pointer = shortcut.Pointer;
        int tooltips = 0;
        int up = 0;
        int boxes = 0;
        var titles = new List<string>();
        foreach (KeywordsTooltip tooltip in menu.GetComponentsInChildren<KeywordsTooltip>(true))
        {
            if (tooltip == null || tooltip.m_HoldToShowInput?.Pointer != pointer)
            {
                continue;
            }

            tooltips++;
            CanvasGroup? fade = tooltip.m_HideWhileInstantiatingBoxes;
            if (!tooltip.IsShown || (fade != null && fade.alpha < 0.5f))
            {
                continue;
            }

            up++;
            Il2CppSystem.Collections.Generic.List<KeywordTooltipBox>? list = tooltip.m_TooltipBoxes;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                KeywordTooltipBox box = list[i];
                if (box == null || !box.gameObject.activeInHierarchy)
                {
                    continue;
                }

                boxes++;
                if (titles.Count < 4)
                {
                    titles.Add($"\"{SafeText.Clean(box.m_Title?.text, 32)}\"");
                }
            }
        }

        var parts = new List<string>();
        if (tooltips > 0)
        {
            parts.Add($"{up} of {tooltips} keyword tooltip(s) up, {boxes} box(es){(titles.Count > 0 ? ": " + string.Join(", ", titles) : string.Empty)}");
        }

        int stats = 0;
        int pressed = 0;
        foreach (SkaterDetailedStats panel in menu.GetComponentsInChildren<SkaterDetailedStats>(true))
        {
            if (panel != null && panel.m_HoldToShowInput?.Pointer == pointer)
            {
                stats++;
                pressed += panel.m_inputPressed ? 1 : 0;
            }
        }

        if (stats > 0)
        {
            parts.Add($"{pressed} of {stats} detailed stats panel(s) showing");
        }

        // The panels' detailed stats ("UI Left Trigger" held, straight from Rewired: DetailedStatsMode), whichever shortcut this is.
        int panelsUp = 0;
        int panelsDetailed = 0;
        foreach (SkaterDetailedStats panel in Panels(menu))
        {
            if (panel != null && panel.isActiveAndEnabled && OptionsOf(panel) is { ShowsMode: true } options)
            {
                panelsUp++;
                panelsDetailed += options.Detailed ? 1 : 0;
            }
        }

        if (panelsUp > 0)
        {
            parts.Add($"{panelsDetailed} of {panelsUp} details panel(s) on their detailed stats");
        }

        if (menu.TryCast<OldSquadMenu>() is { } oldSquad && oldSquad.m_DetailsShortcut?.Pointer == pointer)
        {
            parts.Add(oldSquad.m_IsDetailsHeld ? "the skater's talents shown" : "the skater's talents hidden");
        }

        if (menu.TryCast<EventChoiceMenu>() is { } events &&
            (events.m_ShowDetailsShortcut?.Pointer == pointer || events._alternateShowDetailsShortcut?.Pointer == pointer))
        {
            parts.Add(events.m_IsShowingDetails ? "the cards' details shown" : "the cards' details hidden");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "no details panel of the game's on it";
    }

    /// <summary>
    /// After a hold or release here: what of the game's known details state didn't follow it (a keyword tooltip, a detailed
    /// stats panel or the old squad menu still in the other state), in words; "" when all did.
    /// </summary>
    public static string Unfollowed(AbstractMenu menu, UIInputShortcutBase shortcut, bool held)
    {
        IntPtr pointer = shortcut.Pointer;
        int keywords = 0;
        foreach (KeywordsTooltip tooltip in menu.GetComponentsInChildren<KeywordsTooltip>(true))
        {
            // A tooltip that isn't enabled has no hold registered (the game's own press wouldn't reach it either).
            keywords += tooltip != null && tooltip.isActiveAndEnabled && tooltip.m_HoldToShowInput?.Pointer == pointer && tooltip.m_inputPressed != held ? 1 : 0;
        }

        int stats = 0;
        foreach (SkaterDetailedStats panel in menu.GetComponentsInChildren<SkaterDetailedStats>(true))
        {
            stats += panel != null && panel.isActiveAndEnabled && panel.m_HoldToShowInput?.Pointer == pointer && panel.m_inputPressed != held ? 1 : 0;
        }

        bool oldSquad = menu.TryCast<OldSquadMenu>() is { } squad && squad.m_DetailsShortcut?.Pointer == pointer && squad.m_IsDetailsHeld != held;
        var parts = new List<string>();
        if (keywords > 0)
        {
            parts.Add($"{keywords} keyword tooltip(s)");
        }

        if (stats > 0)
        {
            parts.Add($"{stats} detailed stats panel(s)");
        }

        if (oldSquad)
        {
            parts.Add("the old squad menu's details");
        }

        return string.Join(", ", parts);
    }

    // ---- The details panels' detailed stats (DetailedStatsMode) ----

    /// <summary>
    /// Host: whether the menu's skater details panels show their detailed stats (<see cref="DetailedStatsMode"/>), as a value
    /// of its data; nothing when it has no panel whose mode shows. Each panel switches itself on the "UI Left Trigger" action
    /// straight from Rewired (<c>SkaterDetailedStats.RegisterDetailedStatsInput</c>), so the host's panels are read: whoever
    /// pressed it on the host's PC (the host, or a friend's pad), this is what its screen shows. The first time a kind of menu
    /// shows them it's logged.
    /// </summary>
    public static void ReadDetailedStats(AbstractMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        SkaterDetailedStats[] panels = Panels(menu);
        if (panels.Length == 0)
        {
            return;
        }

        var options = new List<StatDisplayOptions>(panels.Length);
        foreach (SkaterDetailedStats panel in panels)
        {
            if (panel != null && panel.isActiveAndEnabled)
            {
                options.Add(OptionsOf(panel));
            }
        }

        if (DetailedStatsMode.HostValue(options) is not int value)
        {
            return;
        }

        values.Add(new(DetailedStatsMode.Key, NetValue.FromInt(value)));
        if (value == 1 && HostDetailedKinds.Add(MenuGame.TypeName(menu)))
        {
            ModLog.Info($"Menus: the host's {MenuGame.TypeName(menu)} shows its skaters' detailed stats (\"UI Left Trigger\" held); " +
                "friends' copies show them too. Further ones aren't logged.");
        }
    }

    /// <summary>
    /// Friend: this copy's details panels on the host's mode (its data's <see cref="DetailedStatsMode.Key"/>), each with the
    /// game's own switch: the two writes its "UI Left Trigger" handlers make (<c>OnDetailedModeEnabled</c> puts the detailed
    /// options in use, <c>OnDetailedModeDisabled</c> the plain ones), then the refresh they end with
    /// (<c>RefreshStatsBoxesOnly</c>, which only they call). Returns how many panels switched (0: none had to, or the host's
    /// data says nothing). Throws what the game throws (the views' guard catches it).
    /// </summary>
    public static int FollowDetailedStats(AbstractMenu open, long hostValue)
    {
        if (hostValue is not (0 or 1))
        {
            return 0;
        }

        int switched = 0;
        foreach (SkaterDetailedStats panel in Panels(open))
        {
            if (panel == null || !panel.isActiveAndEnabled || DetailedStatsMode.Switch(OptionsOf(panel), hostValue) is not bool detailed)
            {
                continue;
            }

            panel.m_CurrentBasicStatsDisplayOptions = detailed ? panel.m_DetailedModeBasicStatDisplayOptions : panel.m_BasicStatDisplayOptions;
            panel.m_CurrentOverallStatDisplayOptions = detailed ? panel.m_DetailedModeOverallStatDisplayOptions : panel.m_OverallStatDisplayOptions;
            panel.RefreshStatsBoxesOnly();
            switched++;
        }

        return switched;
    }

    /// <summary>A panel's stat display options, as whole numbers.</summary>
    private static StatDisplayOptions OptionsOf(SkaterDetailedStats panel) =>
        new((int)panel.m_CurrentBasicStatsDisplayOptions, (int)panel.m_CurrentOverallStatDisplayOptions, (int)panel.m_BasicStatDisplayOptions,
            (int)panel.m_OverallStatDisplayOptions, (int)panel.m_DetailedModeBasicStatDisplayOptions, (int)panel.m_DetailedModeOverallStatDisplayOptions);

    /// <summary>
    /// A menu's skater details panels (up or not), looked for once per menu object and again when one of them is gone: a menu
    /// is read every fraction of a second, and its panels come with its prefab. Few menus are kept at a time. Kept by the
    /// menu's Unity instance id, which no later object gets while the game runs: a native address is reused once the old
    /// menu's object is collected, and a menu with no panels (kept as an empty list, with nothing to tell it's gone) would
    /// then answer for a new lineup at that address.
    /// </summary>
    private static SkaterDetailedStats[] Panels(AbstractMenu menu)
    {
        int key = menu.GetInstanceID();
        if (PanelsByMenu.TryGetValue(key, out SkaterDetailedStats[]? cached) && Array.TrueForAll(cached, p => p != null))
        {
            return cached;
        }

        if (PanelsByMenu.Count >= MaxPanelMenus)
        {
            PanelsByMenu.Clear();
        }

        var found = new List<SkaterDetailedStats>();
        foreach (SkaterDetailedStats panel in menu.GetComponentsInChildren<SkaterDetailedStats>(true))
        {
            if (panel != null && found.Count < MaxPanels)
            {
                found.Add(panel);
            }
        }

        SkaterDetailedStats[] panels = found.ToArray();
        PanelsByMenu[key] = panels;
        return panels;
    }
}
#endif
