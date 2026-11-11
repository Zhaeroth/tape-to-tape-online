#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppState;
using Il2Cpp;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The lineup menu (<c>LineupMenu</c>) as a real view on a friend (v1). The game
/// opens it on a team (<c>LineupMenu.Metadata.Team</c>): the run's team from the map, an event or a reward, a squad's starting
/// team from the squad menu (<c>ChooseMetaMenu.ShowLineup</c>: the squad asset's own <c>startingTeam</c>, locked, as
/// the home team; v1), a match team from the pause menu (<c>GameMenu.ShowLineupMenu</c>, after pointing the team's
/// forwards at the skaters on the ice), the Canada curse or Play Now's controller screen (a team by id, the panel's own). Its <c>InitData</c> makes a temporary roster (<c>TeamRosterData</c>, its team a clone,
/// verified in the game's code: <c>Instantiate</c>), every swap edits only that, and only its leave (<c>TryLeaveMenu</c> →
/// <c>ApplyChanges</c>) writes the team.
/// <list type="bullet">
/// <item>Host: which team it shows (the run's, a squad's starting team by squad id, the match's home or visitor team, or
/// another), the team's print (its
/// forwards and bench by id, <see cref="LineupOrder.Print"/>), the menu's flags, its cards in order (each card's skater id),
/// how many of them are the starters' list's, the card toggled for a swap and the details panel's tab (the selected
/// skater's stats, power-ups or modifiers: <c>SkaterDetailedStats</c>, switched with its own tab shortcuts), in the menu's
/// data; read again at once after each toggle and swap. Its close, when it showed the run's team on the world map, may
/// save the run for friends (<see cref="RunSync.HostLineupClosed"/>).</item>
/// <item>Friend: opens the menu on its own copy of that team (refused with why when its print isn't the host's) with the
/// host's flags and no lineup store, then follows the host's cards with the menu's own swaps (<c>SwapPlayerCards</c>, on
/// its temporary roster only; <see cref="LineupOrder.Swaps(IReadOnlyList{string}, IReadOnlyList{string})"/>), its toggle (the card's own <c>Toggle.SetSelected</c>)
/// and the details panel's tab (the panel's own <c>TrySetTab</c>); the host's cursor comes as every menu's does, and with
/// it the selected skater's details; its leave is refused while following, and it closes with the host's (the base close:
/// nothing is applied).</item>
/// </list>
/// </summary>
internal static class LineupViews
{
    /// <summary>The lineup menu's class.</summary>
    public const string Type = "LineupMenu";

    /// <summary>Row group: one of the host's cards, in order (the active cards, then the bench's), with its skater's id.</summary>
    public const string Card = "card";

    private const string TeamKey = "lineup.team";
    private const string PrintKey = "lineup.print";
    private const string LockedKey = "lineup.locked";
    private const string HomeKey = "lineup.home";
    private const string SkipKey = "lineup.skipSelection";
    private const string StoringKey = "lineup.storing";
    private const string ToggledKey = "lineup.toggled";
    private const string SquadKey = "lineup.squad";
    private const string TeamIdKey = "lineup.teamId";
    private const string StartersKey = "lineup.starters";
    private const string TabKey = "lineup.detailsTab";
    private const string PickedTeam = "picked";
    private const string RunTeam = "run";
    private const string SquadTeam = "squad";
    private const string HomeTeam = "home";
    private const string VisitorTeam = "visitor";
    private const int IdChars = 96;

    // The details panel's tabs (the game's SkaterDetailedStats.ETab: stats, power-ups, modifiers), with room to spare.
    private const int MaxTab = 15;

    private static bool _tabFailureLogged;

    /// <summary>True when the host's lineup shows the run's team (a friend needs the host's run for it).</summary>
    public static bool OnRunTeam(MenuData data) => data.Text(TeamKey) == RunTeam;

    /// <summary>True when the host's lineup shows a match team (a friend needs the host's match for it).</summary>
    public static bool OnMatchTeam(MenuData data) => data.Text(TeamKey) is HomeTeam or VisitorTeam;

    /// <summary>Host: the lineup's team, flags, cards and toggle, for a friend's copy.</summary>
    public static void Read(LineupMenu menu, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        TeamRosterData? roster = menu.Team;
        TeamData? team = roster?.OriginalTeamData;
        string source = Source(team);
        if (source == "other" && team != null && SquadOf(team) is { } squad)
        {
            // The squad menu's lineup: the squad's own starting team, which every PC's game has.
            source = SquadTeam;
            values.Add(new(SquadKey, NetValue.FromString(Core.Diagnostics.SafeText.Clean(squad.id, IdChars))));
        }
        else if (source == "other" && team?.id is { Length: > 0 and <= IdChars } id)
        {
            // A team picked on Play Now's controller screen: a team asset every PC has, or the host's custom one, sent ahead.
            source = PickedTeam;
            values.Add(new(TeamIdKey, NetValue.FromString(id)));
        }

        values.Add(new(TeamKey, NetValue.FromString(source)));
        values.Add(new(PrintKey, NetValue.FromInt(team == null ? 0 : Print(team))));
        values.Add(new(LockedKey, NetValue.FromBool(menu.m_AreSkatersLocked)));
        values.Add(new(HomeKey, NetValue.FromBool(menu.m_IsHomeTeam)));
        values.Add(new(SkipKey, NetValue.FromBool(menu.m_SkipInitialSelection)));
        values.Add(new(StoringKey, NetValue.FromBool(roster != null && (roster.m_IsStoringLineup || roster.m_LineupStore != null))));
        List<SkaterCard> cards = Cards(menu, out int starters);
        int toggled = cards.FindIndex(IsToggled);
        values.Add(new(ToggledKey, NetValue.FromInt(toggled)));
        values.Add(new(StartersKey, NetValue.FromInt(Math.Min(starters, LineupOrder.MaxCards))));
        ReadTab(DetailsPanel(menu), values);

        foreach (SkaterCard card in cards.Take(LineupOrder.MaxCards))
        {
            items.Add(new MenuItem(Card, new[] { new KeyValuePair<string, NetValue>("id", NetValue.FromString(Core.Diagnostics.SafeText.Clean(SkaterId(card), IdChars))) }));
        }
    }

    /// <summary>Which of the game's teams <paramref name="team"/> is: the run's, the match's home or visitor team, or another.</summary>
    private static string Source(TeamData? team)
    {
        if (team == null)
        {
            return "none";
        }

        if (MenuGame.Campaign()?.runData?.teamData is { } run && run.Pointer == team.Pointer)
        {
            return RunTeam;
        }

        GameManager? game = GameMenuCapture.Game();
        return game?._homeTeam?._teamData?.Pointer == team.Pointer ? HomeTeam
            : game?._visitorTeam?._teamData?.Pointer == team.Pointer ? VisitorTeam
            : "other";
    }

    /// <summary>The squad whose starting team <paramref name="team"/> is (the squad menu's lineup), or null.</summary>
    private static RunSquadScriptableObject? SquadOf(TeamData team)
    {
        Il2CppSystem.Collections.Generic.List<RunSquadScriptableObject>? squads = MenuGame.Campaign()?.squads;
        for (int i = 0; squads != null && i < squads.Count; i++)
        {
            if (squads[i]?.startingTeam is { } starting && starting.Pointer == team.Pointer)
            {
                return squads[i];
            }
        }

        return null;
    }

    /// <summary>This PC's squad <paramref name="id"/> (its asset, from the campaign's squads), or null.</summary>
    private static RunSquadScriptableObject? Squad(string id)
    {
        Il2CppSystem.Collections.Generic.List<RunSquadScriptableObject>? squads = MenuGame.Campaign()?.squads;
        for (int i = 0; id.Length > 0 && squads != null && i < squads.Count; i++)
        {
            if (squads[i] is { } squad && squad.id == id)
            {
                return squad;
            }
        }

        return null;
    }

    /// <summary>A team's print: its forwards (empty places too) and bench, by id.</summary>
    private static long Print(TeamData team)
    {
        return LineupOrder.Print(TeamIds(team));
    }

    /// <summary>The team's skaters' ids in order: its forwards, then its bench.</summary>
    public static List<string?> TeamIds(TeamData team)
    {
        var ids = new List<string?>();
        Il2CppSystem.Collections.Generic.List<ForwardData>? forwards = team.GetForwards(true);
        for (int i = 0; forwards != null && i < forwards.Count; i++)
        {
            ids.Add(forwards[i]?.id);
        }

        Il2CppSystem.Collections.Generic.List<ForwardData>? bench = team.GetBench();
        for (int i = 0; bench != null && i < bench.Count; i++)
        {
            ids.Add(bench[i]?.id);
        }

        return ids;
    }

    /// <summary>The ids on the host's cards in its data, in order ("" for an empty seat).</summary>
    public static List<string> HostCards(MenuData data) => data.Group(Card).Select(row => row.Text("id")).ToList();

    /// <summary>How many of the host's cards (the first ones) are its starters' list's; -1 when its data doesn't say.</summary>
    public static int HostStarters(MenuData data) => (int)Math.Clamp(data.Whole(StartersKey, -1), -1, LineupOrder.MaxCards);

    /// <summary>
    /// A run team's roster by id, in order (empty seats as null): its first places are the starters, the rest its bench, as
    /// a team with no lines has them (<c>TeamData.GetForward</c> and <c>GetBench</c> read the roster then).
    /// </summary>
    public static List<string?> RosterIds(TeamData team)
    {
        var ids = new List<string?>();
        Il2CppSystem.Collections.Generic.List<ForwardData>? forwards = team.GetForwards(true);
        for (int i = 0; forwards != null && i < forwards.Count; i++)
        {
            ids.Add(forwards[i]?.id);
        }

        return ids;
    }

    /// <summary>
    /// Host: the host's lineup menu closed (the base close, after its own leave applied the roster to the team). Run sync
    /// looks at the run's team next: a change made on the world map is saved for friends (<see cref="RunSync.HostLineupClosed"/>).
    /// Never throws.
    /// </summary>
    public static void HostClosed()
    {
        try
        {
            RunSync.Instance?.HostLineupClosed();
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: the host's lineup close wasn't passed on ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Friend: the lineup menu's data on this PC's own copy of the host's team (see <see cref="Build"/>), or null with why.
    /// Never throws: a failure is the reason.
    /// </summary>
    public static LineupMenu.Metadata? Request(MenuData data, out string problem, out string about)
    {
        try
        {
            return Build(data, out problem, out about);
        }
        catch (System.Exception ex)
        {
            problem = $"its data couldn't be made here ({ModLog.SafeErrorText(ex)})";
            about = string.Empty;
            return null;
        }
    }

    /// <summary>
    /// Friend: the lineup menu's data on this PC's own copy of the host's team, or null with why (another team, or a copy
    /// that isn't the host's).
    /// </summary>
    private static LineupMenu.Metadata? Build(MenuData data, out string problem, out string about)
    {
        problem = string.Empty;
        about = string.Empty;
        string source = data.Text(TeamKey);
        string squad = data.Text(SquadKey);
        string teamId = data.Text(TeamIdKey);
        string teamProblem = string.Empty;
        TeamData? team = source switch
        {
            PickedTeam => ExhibitionScreen.FindTeam(teamId, out teamProblem),
            RunTeam => MenuGame.Campaign()?.runData?.teamData,
            SquadTeam => Squad(squad)?.startingTeam,
            HomeTeam => GameMenuCapture.Game()?._homeTeam?._teamData,
            VisitorTeam => GameMenuCapture.Game()?._visitorTeam?._teamData,
            _ => null,
        };
        if (team == null)
        {
            problem = source == PickedTeam ? $"this PC doesn't have the host's team {Core.Diagnostics.HostText.KeyForLog(teamId)}{(teamProblem.Length > 0 ? $" ({teamProblem})" : string.Empty)}"
                : source is RunTeam or HomeTeam or VisitorTeam
                ? $"this PC has no {(source == RunTeam ? "run" : source)} team to show it with"
                : source == SquadTeam ? $"this game has no squad {Core.Diagnostics.HostText.KeyForLog(squad)} (or it has no starting team)"
                : $"the host's lineup is of a team this PC doesn't follow ({Core.Diagnostics.HostText.KeyForLog(source)})";
            return null;
        }

        // A picked team's order here may be other than the host's (its lineup edits an asset, for that PC's session): the view's
        // own swaps put the cards in the host's order, and other skaters show it as text (Follow).
        long print = Print(team);
        if (source != PickedTeam && print != data.Whole(PrintKey))
        {
            problem = "your game's copy of the team isn't the host's here (other skaters, or another order)";
            return null;
        }

        string which = source switch
        {
            RunTeam => "the run's team",
            SquadTeam => $"the squad {Core.Diagnostics.HostText.KeyForLog(squad)}'s starting team",
            PickedTeam => $"the host's team {Core.Diagnostics.SafeText.Clean(team.id, 48)} ({LogNames.TextShape(team.teamName)})",
            _ => $"the {source} team",
        };
        about = $"{which}, {data.Group(Card).Count()} card(s){(data.Flag(StoringKey) ? "; the host's lineup store isn't used here" : string.Empty)}";
        return new LineupMenu.Metadata
        {
            Team = team,
            LockPlayers = data.Flag(LockedKey),
            IsHomeTeam = data.Flag(HomeKey, true),
            IsStoringLineup = false, // nothing is ever applied here
            SkipInitialSelection = data.Flag(SkipKey),
            LineupStore = null!,
        };
    }

    /// <summary>
    /// Friend: this PC's cards put in the host's order with the menu's own swaps, and the host's toggled card toggled.
    /// "" when nothing needed doing; what was done otherwise; <paramref name="problem"/> when the cards can't match the
    /// host's (another team, or the menu's cards aren't up yet: <paramref name="ready"/> false).
    /// </summary>
    public static string Follow(LineupMenu open, MenuData data, out string problem, out bool ready)
    {
        problem = string.Empty;
        List<SkaterCard> cards = Cards(open);
        List<string> host = data.Group(Card).Select(row => row.Text("id")).ToList();
        ready = cards.Count > 0 && cards.Count == host.Count;
        if (!ready)
        {
            return string.Empty;
        }

        List<string> here = cards.Select(c => Core.Diagnostics.SafeText.Clean(SkaterId(c), IdChars)).ToList();
        IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(here, host);
        if (swaps == null)
        {
            problem = "this PC's lineup has other skaters than the host's";
            return string.Empty;
        }

        foreach ((int a, int b) in swaps)
        {
            open.SwapPlayerCards(cards[a], cards[b]);
        }

        int wanted = (int)data.Whole(ToggledKey, -1);
        int toggled = cards.FindIndex(IsToggled);
        string toggle = string.Empty;
        if (swaps.Count == 0 && wanted != toggled)
        {
            // Once the swaps have settled (the menu resets its toggles a frame after a swap). Another card toggled here is
            // cleared first, alone: toggling a second card would swap the two. The host's toggle comes at the next look.
            if (toggled >= 0)
            {
                open.ResetPlayerCardStates(false);
                toggle = "the toggle cleared";
            }
            else if (wanted >= 0 && wanted < cards.Count && cards[wanted].Toggle is { } card)
            {
                card.SetSelected(true, true);
                toggle = $"card {wanted + 1} toggled for a swap";
            }
        }

        string swapped = swaps.Count > 0 ? $"{swaps.Count} swap(s)" : string.Empty;
        string tab = swaps.Count == 0 ? FollowTab(DetailsPanel(open), data) : string.Empty;
        return string.Join("; ", new[] { swapped, toggle, tab }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// Host: the tab a lineup's details panel is on (the selected skater's stats, abilities or modifiers; the panel's own
    /// <c>m_CurrentTab</c>), in the menu's data; nothing when the menu has no panel. The benchwarmer lineup has one too
    /// (<see cref="BenchViews"/>).
    /// </summary>
    public static void ReadTab(SkaterDetailedStats? panel, List<KeyValuePair<string, NetValue>> values)
    {
        if (panel != null)
        {
            values.Add(new(TabKey, NetValue.FromInt(panel.m_CurrentTab)));
        }
    }

    /// <summary>
    /// Friend: a details panel on the host's tab (stats, abilities or modifiers), with the panel's own tab switch, as its tab
    /// shortcuts switch it on the host's screen (the shortcut's press itself only plays its sound here; the switch also puts
    /// the panel's compare box on that tab). What was done, or "" when it's on it already, the host's data names none (or one
    /// out of range), or the panel isn't up. A switch that fails (a panel without tabs) is logged once and never fails the
    /// view: its cards and cursor still follow. Never throws.
    /// </summary>
    public static string FollowTab(SkaterDetailedStats? panel, MenuData data)
    {
        try
        {
            int wanted = (int)Math.Clamp(data.Whole(TabKey, -1), -1, MaxTab + 1);
            if (wanted is < 0 or > MaxTab || panel == null || panel.m_CurrentTab == wanted)
            {
                return string.Empty;
            }

            int before = panel.m_CurrentTab;
            Scenes.FriendUi.Apply(() => panel.TrySetTab(wanted, false));
            return panel.m_CurrentTab == before ? string.Empty : $"the details on tab {panel.m_CurrentTab + 1}, as the host's";
        }
        catch (Exception ex)
        {
            if (!_tabFailureLogged)
            {
                _tabFailureLogged = true;
                ModLog.Warn($"Menus: a lineup's details panel couldn't take the host's tab here ({ModLog.SafeErrorText(ex)}); it stays on its own (logged once).");
            }

            return string.Empty;
        }
    }

    /// <summary>The lineup's details panel (the selected skater's stats, compared with a toggled one), or null when it has none.</summary>
    private static SkaterDetailedStats? DetailsPanel(LineupMenu menu)
    {
        SkaterDetailedStats? panel = menu.m_Compare?.m_StatsBox;
        return panel != null ? panel : null;
    }

    /// <summary>The menu's cards in order: the active list's, then the bench's (each list's shown cards only).</summary>
    private static List<SkaterCard> Cards(LineupMenu menu) => Cards(menu, out _);

    /// <summary>The menu's cards in order (see <see cref="Cards(LineupMenu)"/>), and how many are the active list's.</summary>
    private static List<SkaterCard> Cards(LineupMenu menu, out int active)
    {
        var cards = new List<SkaterCard>();
        AddShown(menu.m_ActivePlayersList, cards);
        active = cards.Count;
        AddShown(menu.m_BenchPlayersList, cards);
        return cards;
    }

    /// <summary>A card list's shown cards, in order, onto <paramref name="cards"/>.</summary>
    private static void AddShown(SkaterCardList? list, List<SkaterCard> cards)
    {
        Il2CppSystem.Collections.Generic.List<SkaterCard>? shown = list?.m_SkaterCards;
        for (int i = 0; shown != null && i < shown.Count; i++)
        {
            SkaterCard? card = shown[i];
            if (card != null && card.gameObject.activeSelf)
            {
                cards.Add(card);
            }
        }
    }

    /// <summary>The id of the skater a card shows ("" for an empty seat).</summary>
    private static string SkaterId(SkaterCard card) => card.m_CurrentSkater?.Skater?.id ?? string.Empty;

    /// <summary>True when a card is toggled for a swap.</summary>
    private static bool IsToggled(SkaterCard card) => card.Toggle is { } toggle && toggle.IsSelected;
}
#endif
