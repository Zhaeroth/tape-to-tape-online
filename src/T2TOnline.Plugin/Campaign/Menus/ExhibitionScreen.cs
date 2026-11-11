#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppScriptableStates;
using Il2CppTape2Tape.Customization;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// What Play Now's controller screen adds to the campaign's (v1): its two teams and its rules
/// page. It's the same menu class and the same icons (<see cref="ControllerViews"/>); these are the game's own calls for the rest.
/// <list type="bullet">
/// <item>Host: the screen's game mode, teams (id, name, tab, colours), rules and whether its rules page is up, in its data
/// (<see cref="Read"/>). The custom content its teams need goes to friends a moment after they settle
/// (<see cref="HostTick"/>), as a match's does (each piece once a session), with what names it as an attachment.</item>
/// <item>Friend: a team goes in with the panel's own <c>Refresh(team, others, delay)</c>, the overload the game's no-tabs screens use,
/// found by id (this PC's own list, or built with the game's code from the host's custom content, never saved); its tab's name
/// with <c>RefreshActiveTab</c>. This PC's tabs and their selections aren't touched, so its own next use starts from its own
/// saved teams (<c>PrepareForNextShow</c>). The rules page opens with <c>GameSettingsUI.Show_Internal</c>, what the host's
/// <c>DisplayGameSettings_Internal</c> calls, without waiting for a result: its own result is only reached through shortcuts
/// and clicks, refused here, so nothing is saved.</item>
/// </list>
/// Game thread only. Friend calls run inside the friend's guarded apply (their failures are counted there).
/// </summary>
internal static class ExhibitionScreen
{
    /// <summary>How long the host's teams stay put before their custom content is read and sent (a stick flicking through a tab sends nothing).</summary>
    private const double ContentSettleSeconds = 0.4;

    private const int NameChars = 40;

    // Host: the screen whose teams wait to be looked at, when, the screen last read, and its teams last looked at and named to
    // friends (reset when it closes).
    private static ControllerAssignmentMenu? _contentMenu;
    private static IntPtr _contentOwner;
    private static double _contentAt;
    private static string _contentTeams = string.Empty;
    private static string _contentSent = string.Empty;
    private static bool _warmUpLogged;

    /// <summary>
    /// Host: the relay's way to send friends the custom content the screen's teams need, each piece once a session (set by
    /// <see cref="Online.OnlineSession"/>).
    /// </summary>
    public static Action<IReadOnlyList<CustomItem>>? ShareContent { get; set; }

    // ---- Host ----

    /// <summary>
    /// True while this PC's game is warming up its title menus (<c>T2TUIModule.PrepareTitleMenus</c>: it opens and closes the
    /// controller screen under a black cover at each title load): on the host, nothing for friends; on a
    /// friend, its copy isn't asked for until that's over.
    /// </summary>
    public static bool PreparingTitleMenus() => MenuGame.Module()?.TryCast<T2TUIModule>() is { IsPreparingTitleMenus: true };

    /// <summary>
    /// Host: true for a menu of the title's warm-ups: the glossary, the settings and the controller screen, each opened and closed at
    /// once under the black cover at a title load (the three <c>Prepare</c> calls inside the flag). Not a
    /// screen the host is on, so it isn't tracked (logged once).
    /// </summary>
    public static bool IsWarmUp(string type)
    {
        if (type is not (ControllerViews.Type or MenuPayloads.Glossary or MenuPayloads.Settings) || !PreparingTitleMenus())
        {
            return false;
        }

        if (!_warmUpLogged)
        {
            _warmUpLogged = true;
            ModLog.Info("Menus: the host's title warm-ups (its controller screen, glossary and settings) don't go to friends (they aren't on the host's screen either).");
        }

        return true;
    }

    /// <summary>Host: an exhibition screen's settings, for a friend's real screen.</summary>
    public static void Read(ControllerAssignmentMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        values.Add(Value(ControllerScreenState.GameModeKey, NetValue.FromInt((int)menu.m_GameModeForGameSettings)));
        values.Add(Value(ControllerScreenState.GameTypeKey, NetValue.FromInt((int)menu.m_GameTypeForGameSettings)));
        values.Add(Value(ControllerScreenState.RulesPageKey, NetValue.FromBool(menu.m_DisplayGameSettings)));
        values.Add(Value(ControllerScreenState.InRulesKey, NetValue.FromBool(menu.m_IsInGameSettings)));
        if (menu.m_TeamSelect is { } teams)
        {
            values.Add(Value(ControllerScreenState.HomePlayersKey, NetValue.FromInt(teams.homeTeamPlayers?.Count ?? 0)));
            values.Add(Value(ControllerScreenState.VisitorPlayersKey, NetValue.FromInt(teams.visitorTeamPlayers?.Count ?? 0)));
        }

        if (RuleLists(menu.m_GameSettings) is { } lists)
        {
            var choices = new int[lists.Length];
            for (int i = 0; i < lists.Length; i++)
            {
                choices[i] = lists[i]?.m_CurIndex ?? -1;
            }

            string rules = ControllerRules.Format(choices);
            if (rules.Length > 0)
            {
                values.Add(Value(ControllerScreenState.RulesKey, NetValue.FromString(rules)));
            }
        }

        if (menu.Mode != ControllerAssignmentMenu.EMode.TeamSelection)
        {
            return; // no team changes on it: a friend's copy shows the match's own two teams
        }

        ReadTeam(menu.m_HomeTeam, ControllerScreenState.HomeTeamKey, ControllerScreenState.HomeNameKey, ControllerScreenState.HomeTabKey, ControllerScreenState.HomeColorKey, values);
        ReadTeam(menu.m_VisitorTeam, ControllerScreenState.VisitorTeamKey, ControllerScreenState.VisitorNameKey, ControllerScreenState.VisitorTabKey, ControllerScreenState.VisitorColorKey, values);
        string shown = $"{menu.m_HomeTeam?.CurrentTeam?.id}|{menu.m_VisitorTeam?.CurrentTeam?.id}";
        _contentOwner = menu.Pointer;
        if (shown != _contentTeams)
        {
            _contentTeams = shown;
            _contentMenu = menu;
            _contentAt = FrameClock.Seconds + ContentSettleSeconds;
        }
    }

    /// <summary>
    /// Host, every frame of the menu views: the teams on the screen have stayed put a moment, and they use this PC's custom
    /// content: it goes to every friend (who gets it again at a rejoin), and what names it goes as the screen's attachment.
    /// </summary>
    public static void HostTick(HostMenus host, bool friends)
    {
        if (_contentMenu is not { } menu || FrameClock.Seconds < _contentAt)
        {
            return;
        }

        _contentMenu = null;
        try
        {
            ShareHostContent(host, menu, friends);
        }
        catch (Exception ex)
        {
            // Its own failure: friends see that team's name, and the menu views' error budget isn't spent on a host flicking through teams.
            ModLog.Warn($"Menus: the host's controller screen's custom content couldn't be read ({ModLog.SafeErrorText(ex)}); friends see that team's name on their screen.");
        }
    }

    private static void ShareHostContent(HostMenus host, ControllerAssignmentMenu menu, bool friends)
    {
        if (menu.WasCollected || !friends || menu.m_HomeTeam?.CurrentTeam is not { } home || menu.m_VisitorTeam?.CurrentTeam is not { } visitor)
        {
            return;
        }

        var items = new List<CustomItem>();
        var refs = new List<CustomRef>();
        if (!CustomContent.Collect(home, visitor, items, refs, out string problem))
        {
            ModLog.Warn($"Menus: the host's controller screen's custom content can't be sent ({problem}); friends see that team's name on their screen, and the match won't load for them.");
            return;
        }

        string sent = string.Join(",", refs);
        if (refs.Count == 0 || sent == _contentSent)
        {
            return;
        }

        _contentSent = sent;
        ShareContent?.Invoke(items);
        ControllerContentData data = ControllerContentData.Of(refs);
        if (host.Attach(menu, MenuAttachmentKind.ControllerContent, serial => MenuAttachment.ControllerContent(serial, data), data.ToString(), friends, out string cant))
        {
            ModLog.Info($"Menus: the host's controller screen shows this PC's custom content ({refs.Count} piece(s), {items.Count} to send); it goes to friends now, each piece once a session.");
        }
        else
        {
            ModLog.Warn($"Menus: the host's controller screen's custom content can't be named to friends ({cant}); they see that team's name on their screen.");
        }
    }

    /// <summary>Host: the screen closed: its next opening names its teams' custom content to friends again.</summary>
    public static void HostClosed(IntPtr menu)
    {
        if (_contentOwner == menu)
        {
            ForgetContent();
        }
    }

    /// <summary>Host: hosting stopped.</summary>
    public static void ClearHost()
    {
        ForgetContent();
        _warmUpLogged = false;
    }

    // ---- Friend ----

    /// <summary>The game's own metadata for the host's screen here: as the title opens Play Now's, as the pause menu opens an exhibition's.</summary>
    public static ControllerAssignmentMenu.Metadata Request(ControllerScreenState screen) => screen.Kind == ControllerScreenKind.PlayNow
        ? new ControllerAssignmentMenu.Metadata(true, ControllerAssignmentMenu.EMode.TeamSelection, screen.RulesPage, (GameMode)screen.GameMode, (GameType)screen.GameType,
            isLineupLocked: screen.LineupLocked, isOnboarding: false, isOnboardingFromPause: false)
        : new ControllerAssignmentMenu.Metadata(false, ControllerAssignmentMenu.EMode.SideSelection, false, (GameMode)screen.GameMode, (GameType)screen.GameType,
            isLineupLocked: screen.LineupLocked, isOnboarding: false, isOnboardingFromPause: false);

    /// <summary>A team panel as it shows now, for the plan.</summary>
    public static ControllerTeamShown Shown(ControllerAssignmentTeam team) =>
        new(team.CurrentTeam?.id ?? string.Empty, team.m_CurrentTab == null ? -1 : (int)team.m_CurrentTab.Tab, team.IsColorHome);

    /// <summary>
    /// The team with the host's <paramref name="id"/> on this PC: built from the host's custom content, or this PC's own (a
    /// game's built-in team, by id). Null with why when it's named but not here yet, or with none when this PC doesn't have it.
    /// </summary>
    public static TeamData? FindTeam(string id, out string problem)
    {
        TeamData? custom = CustomContent.Team(id, out problem);
        if (custom != null || problem.Length > 0)
        {
            return custom;
        }

        ITeamRepository? repository = CustomContent.TeamRepository();
        return repository == null ? null : MatchSetup.FindTeam(repository, id);
    }

    /// <summary>The panel shows <paramref name="team"/>, in the colours the host's wears, with its own refresh (nothing rolled, nothing saved).</summary>
    public static void ShowTeam(ControllerAssignmentTeam panel, TeamData team, bool colorHome)
    {
        panel.IsColorHome = colorHome;
        panel.Refresh(team, null!, false);
    }

    /// <summary>The panel's tab's name is the host's tab's, from this PC's tab of that kind (its name in this PC's language).</summary>
    public static void ShowTab(ControllerAssignmentMenu open, ControllerAssignmentTeam panel, int tab)
    {
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentMenu.TeamsTabData>? tabs = open.m_TeamsTabs;
        for (int i = 0; tabs != null && i < tabs.Count; i++)
        {
            if (tabs[i] is { } each && (int)each.Tab == tab)
            {
                panel.RefreshActiveTab(each);
                return;
            }
        }
    }

    /// <summary>
    /// A team this PC can't show (yet): the host's name on the panel, its logo and skaters away, so what shows is never another
    /// team's; or back as they were, the panel's next refresh drawing them (<paramref name="waiting"/> false).
    /// </summary>
    public static void WaitingLook(ControllerAssignmentTeam panel, string name, bool waiting)
    {
        TeamInfoUI? info = panel.m_TeamInfo;
        if (info == null)
        {
            return;
        }

        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.UI.Image>? logos = info.m_TeamLogos;
        for (int i = 0; logos != null && i < logos.Length; i++)
        {
            if (logos[i] is { } logo)
            {
                logo.enabled = !waiting;
            }
        }

        if (!waiting)
        {
            return;
        }

        if (info.m_TeamName != null)
        {
            info.m_TeamName.text = name;
        }

        if (info.m_TeamCity != null)
        {
            info.m_TeamCity.text = string.Empty;
        }

        if (info.m_TeamNickname != null)
        {
            info.m_TeamNickname.text = string.Empty;
        }

        info.HideAllSkaterPreviews();
    }

    /// <summary>
    /// The rules page comes up as on the host's screen: the screen's own settings look, then its panel's own show, with the
    /// host's players' counts for its skater choices (<c>homeTeamPlayers</c> are only filled by the exit this PC never runs).
    /// </summary>
    public static void ShowRules(ControllerAssignmentMenu open, ControllerScreenState screen)
    {
        GameSettingsUI panel = open.m_GameSettings;
        if (panel.m_TeamSelection is { } teams)
        {
            // Read by its show: an asset's serialized list, kept here against a null all the same.
            teams.homeTeamPlayers ??= new Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>();
            teams.visitorTeamPlayers ??= new Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>();
        }

        open.m_IsInGameSettings = true;
        open.RefreshAnimNow();
        if (!panel.m_IsShown)
        {
            panel.Show_Internal((GameMode)screen.GameMode, (GameType)screen.GameType);
        }

        if (screen.HomePlayers >= 0 && screen.VisitorPlayers >= 0)
        {
            panel.m_GameSettings?.RefreshItems(screen.HomePlayers, screen.VisitorPlayers);
        }
    }

    /// <summary>The rules page goes as on the host's screen.</summary>
    public static void HideRules(ControllerAssignmentMenu open)
    {
        if (open.m_GameSettings is { m_IsShown: true } panel)
        {
            panel.Hide_Internal();
        }

        open.m_IsInGameSettings = false;
        open.RefreshAnimNow();
    }

    /// <summary>Each rule's list selector shows the host's choice (<paramref name="host"/>, in <see cref="ControllerRules"/>' order). How many changed.</summary>
    public static int SetRules(GameSettingsUI? panel, IReadOnlyList<int> host)
    {
        if (RuleLists(panel) is not { } lists)
        {
            return 0;
        }

        var shown = new int[lists.Length];
        var counts = new int[lists.Length];
        for (int i = 0; i < lists.Length; i++)
        {
            shown[i] = lists[i]?.m_CurIndex ?? -1;
            counts[i] = lists[i]?.m_Items?.Cast<Il2CppSystem.Collections.ICollection>().Count ?? 0;
        }

        IReadOnlyList<(int Rule, int Choice)> changes = ControllerExhibitionPlan.RuleChanges(host, shown, counts);
        foreach ((int rule, int choice) in changes)
        {
            CycleList list = lists[rule]!;
            list.SelectIndex(choice, false);
            list.RefreshCurrentItem();
        }

        return changes.Count;
    }

    /// <summary>
    /// A copy closing: its rules page, if up, goes first, and a team panel showing only the host's name gets its logo back. The
    /// game keeps this screen for the title (retained), and these would stay for this PC's own next use (its <c>InitData</c>
    /// clears only the menu's flags). Never throws.
    /// </summary>
    public static void BeforeClose(ControllerAssignmentMenu open)
    {
        try
        {
            if (open.m_IsInGameSettings)
            {
                HideRules(open);
            }

            foreach (ControllerAssignmentTeam? panel in new[] { open.m_HomeTeam, open.m_VisitorTeam })
            {
                if (panel != null)
                {
                    WaitingLook(panel, string.Empty, waiting: false);
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: couldn't put the controller screen's rules page away before closing it ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>The rules page's list selectors in <see cref="ControllerRules"/>' order, or null while its panel isn't loaded.</summary>
    private static CycleList?[]? RuleLists(GameSettingsUI? panel) =>
        panel?.m_GameSettings is { } rules
            ? new[] { rules.m_Periods, rules.m_PeriodDuration, rules.m_SkaterCount, rules.m_Difficulty, rules.m_PowerUpsEnabled, rules.m_Injuries }
            : null;

    private static void ReadTeam(ControllerAssignmentTeam? panel, string idKey, string nameKey, string tabKey, string colorKey, List<KeyValuePair<string, NetValue>> values)
    {
        TeamData? team = panel?.CurrentTeam;
        string id = team?.id ?? string.Empty;
        ControllerAssignmentMenu.TeamsTabData? tab = panel?.m_CurrentTab;
        if (panel == null || id.Length is 0 or > ControllerScreenState.MaxTeamChars || tab == null)
        {
            return;
        }

        values.Add(Value(idKey, NetValue.FromString(id)));
        values.Add(Value(nameKey, NetValue.FromString(MenuGame.Plain(team!.teamName, NameChars))));
        values.Add(Value(tabKey, NetValue.FromInt((int)tab.Tab)));
        values.Add(Value(colorKey, NetValue.FromBool(panel.IsColorHome)));
    }

    private static void ForgetContent()
    {
        _contentMenu = null;
        _contentOwner = IntPtr.Zero;
        _contentTeams = string.Empty;
        _contentSent = string.Empty;
    }

    private static KeyValuePair<string, NetValue> Value(string key, NetValue value) => new(key, value);
}
#endif
