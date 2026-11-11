#if HAS_INTEROP
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppSkaters.Injuries;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The lineup with benchwarmers (<c>BenchwarmerMenu</c>, the run start's "make your squad" screen) as a real view on a friend
/// (D68, v1.1). The game's menu shows three lists of skater cards: the starters' seats
/// (<c>m_ActivePlayersList</c>), the bench's (<c>m_BenchPlayersList</c>) and the benchwarmers (<c>m_BenchwarmersPlayersList</c>, a
/// scrolling list that makes one card per benchwarmer; verified in the game's code: <c>ManualScrollingList.RefreshAndWait</c>).
/// Every change of the lineup is the menu's own <c>SwapPlayerCards</c> (a toggle's second card, an add, a send-back, the random fill at
/// open), which swaps two cards' skaters and edits only the menu's temporary roster (a clone of the run's team its <c>InitData</c>
/// makes); only its confirm applies a roster.
/// <list type="bullet">
/// <item>Host: whether its benchwarmers are loaded (the menu's <c>LoadCurrentTeam</c> ran), its cards in order (each card's
/// skater id, "" for an empty seat) with each list's size, the card toggled for a swap and its side, and its details panel's
/// tab (<see cref="LineupViews.ReadTab"/>), in the menu's data; read again after each swap and toggle.</item>
/// <item>Friend: its own benchwarmers swapped for the host's on the cards (<see cref="ApplyPool"/>: built from the host's run
/// preview, each card refreshed with the game's own <c>SkaterCard.Refresh</c> as the menu's list does, and locked as the
/// host's), then the host's cards followed with the menu's own swaps (<see cref="Follow"/>, as <see cref="LineupViews"/>
/// does for the plain lineup), its toggle and its details panel's tab (<see cref="LineupViews.FollowTab"/>).</item>
/// </list>
/// </summary>
internal static class BenchViews
{
    /// <summary>Row group: one of the host's cards, in order (the starters', the bench's, the benchwarmers'), with its skater's id.</summary>
    public const string Card = "bwcard";

    /// <summary>The version of the lineup data a host sends for the real view (an older host sends none: the panel shows it).</summary>
    public const int ViewVersion = 1;

    private const string ViewKey = "bench.view";
    private const string LoadedKey = "bench.loaded";
    private const string HomeKey = "bench.home";
    private const string StartersKey = "bench.starters";
    private const string BenchKey = "bench.bench";
    private const string PoolKey = "bench.pool";
    private const string ToggledKey = "bench.toggled";
    private const int IdChars = 96;

    /// <summary>True when the host's data is made for a real view (a host before v1.1 sends only the panel's lines).</summary>
    public static bool HostSendsView(MenuData data) => data.Whole(ViewKey) >= ViewVersion;

    /// <summary>True once the host's menu has loaded its team and benchwarmers (its cards are the lineup's from then on).</summary>
    public static bool HostLoaded(MenuData data) => data.Flag(LoadedKey);

    /// <summary>Host: the lineup's cards, lists and toggle, for a friend's copy. <paramref name="loaded"/>: its <c>LoadCurrentTeam</c> ran.</summary>
    public static void Read(BenchwarmerMenu menu, bool loaded, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        values.Add(new(ViewKey, NetValue.FromInt(ViewVersion)));
        values.Add(new(LoadedKey, NetValue.FromBool(loaded)));
        values.Add(new(HomeKey, NetValue.FromBool(menu.m_IsHomeTeam)));
        Lists lists = Cards(menu);
        values.Add(new(StartersKey, NetValue.FromInt(lists.Counts.Starters)));
        values.Add(new(BenchKey, NetValue.FromInt(lists.Counts.Bench)));
        values.Add(new(PoolKey, NetValue.FromInt(lists.Counts.Pool)));
        values.Add(new(ToggledKey, NetValue.FromInt(lists.All.FindIndex(IsToggled))));
        LineupViews.ReadTab(menu.m_StatsBox, values);
        foreach (SkaterCard card in lists.All.Take(BenchwarmerLineup.MaxCards))
        {
            items.Add(new MenuItem(Card, new[] { new KeyValuePair<string, NetValue>("id", NetValue.FromString(SafeText.Clean(SkaterId(card), IdChars))) }));
        }
    }

    /// <summary>The host's cards' skater ids, in order.</summary>
    public static List<string> HostCards(MenuData data) => data.Group(Card).Select(row => row.Text("id")).ToList();

    /// <summary>The host's lists' sizes.</summary>
    public static BenchwarmerCardCounts HostCounts(MenuData data) =>
        new((int)data.Whole(StartersKey, -1), (int)data.Whole(BenchKey, -1), (int)data.Whole(PoolKey, -1));

    /// <summary>The skaters on the host's starters' and bench's cards (empty seats as ""), or null when its data has none yet.</summary>
    public static string[]? HostLineup(MenuData data)
    {
        BenchwarmerCardCounts counts = HostCounts(data);
        List<string> cards = HostCards(data);
        int lineup = counts.Starters + counts.Bench;
        return !HostLoaded(data) || counts.Starters < 0 || counts.Bench < 0 || cards.Count < lineup ? null : cards.Take(lineup).ToArray();
    }

    /// <summary>The menu's benchwarmer cards, in its order (one per benchwarmer of its pool).</summary>
    public static List<SkaterCard> PoolCards(BenchwarmerMenu menu) => Shown(menu.m_BenchwarmersPlayersList?.m_SkaterCards);

    /// <summary>
    /// Friend: the host's benchwarmers on this PC's benchwarmer cards, in the host's order, each card refreshed as the menu's
    /// own list refreshes it (<c>SkaterCard.Refresh</c> with the data the menu's <c>LoadCurrentTeam</c> makes for a pool card:
    /// verified in the game's code) and locked as the host's card is. Returns what was done;
    /// <paramref name="problem"/> when it can't be (another number of cards, a card that doesn't take its skater).
    /// </summary>
    public static string ApplyPool(BenchwarmerMenu menu, IReadOnlyList<ForwardData> built, IReadOnlyList<RunStartBenchwarmer> host, out string problem)
    {
        problem = string.Empty;
        List<SkaterCard> cards = PoolCards(menu);
        if (cards.Count != built.Count || built.Count != host.Count)
        {
            problem = $"this PC's menu has {cards.Count} benchwarmer card(s), the host's {host.Count}";
            return string.Empty;
        }

        TeamRosterData? roster = menu.Team;
        TeamData? original = roster?.OriginalTeamData;
        TeamData? temporary = roster?.TemporaryTeamData;
        InjurySystem? injuries = menu.m_InjuriesSystem;
        if (original == null || temporary == null)
        {
            problem = "this PC's menu has no roster to show the benchwarmers with";
            return string.Empty;
        }

        int locked = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            SkaterCard card = cards[i];
            var data = new SkaterDataForUI(original, built[i], injuries!, null!, EStatBonusesPreviewMode.All, null!)
            {
                ActiveTeam = temporary,
                ActivePosition = ESkaterPosition.Invalid,
                BenchIndex = -1,
            };
            card.Refresh(data, false);
            bool isLocked = !host[i].Unlocked;
            card.SetLocked(isLocked, false);
            card.SetOverallStatHidden(isLocked);
            locked += isLocked ? 1 : 0;

            // Read back: the card shows the host's skater, locked as the host's.
            if (card.m_CurrentSkater?.Skater?.Pointer != built[i].Pointer || card.m_IsLocked != isLocked)
            {
                problem = $"its benchwarmer card {i + 1} didn't take the host's benchwarmer {i + 1}";
                return string.Empty;
            }
        }

        return $"{cards.Count} benchwarmer card(s), {locked} locked";
    }

    /// <summary>
    /// Friend: this PC's cards put in the host's order with the menu's own swaps (on its temporary roster only), then the host's
    /// toggled card toggled. "" when nothing needed doing; what was done otherwise. <paramref name="ready"/> false while the
    /// host's data or this menu's cards aren't loaded yet; <paramref name="refused"/> when the menu refused a swap (its own
    /// rules: tried again at the next look); <paramref name="problem"/> when the cards can't match the host's.
    /// </summary>
    public static string Follow(BenchwarmerMenu open, MenuData data, out string problem, out bool ready, out bool refused)
    {
        problem = string.Empty;
        refused = false;
        ready = false;
        if (!HostLoaded(data))
        {
            return string.Empty;
        }

        Lists lists = Cards(open);
        BenchwarmerCardCounts hostCounts = HostCounts(data);
        List<string> host = HostCards(data);
        problem = BenchwarmerLineup.Mismatch(lists.Counts, hostCounts);
        if (problem.Length > 0)
        {
            return string.Empty;
        }

        if (host.Count != hostCounts.Total)
        {
            problem = $"the host's lineup sent {host.Count} of its {hostCounts.Total} cards";
            return string.Empty;
        }

        ready = true;
        List<SkaterCard> cards = lists.All;
        List<string> here = cards.Select(c => SafeText.Clean(SkaterId(c), IdChars)).ToList();
        IReadOnlyList<(int A, int B)>? swaps = BenchwarmerLineup.Swaps(here, host);
        if (swaps == null)
        {
            problem = "this PC's lineup has other skaters than the host's";
            return string.Empty;
        }

        int done = 0;
        foreach ((int a, int b) in swaps)
        {
            if (!open.SwapPlayerCards(cards[a], cards[b]))
            {
                refused = true; // the menu's own rule (a solo Angus, a full line): the next look works it out again
                break;
            }

            done++;
        }

        string toggle = string.Empty;
        int wanted = (int)data.Whole(ToggledKey, -1);
        int toggled = cards.FindIndex(IsToggled);
        if (swaps.Count == 0 && wanted != toggled)
        {
            // Once the swaps have settled (the menu resets its toggles a frame after a swap). Another card toggled here is
            // cleared first, alone: toggling a second card would swap the two. The host's toggle comes at the next look.
            if (toggled >= 0)
            {
                open.ResetPlayerCardStates();
                toggle = "the toggle cleared";
            }
            else if (wanted >= 0 && wanted < cards.Count && cards[wanted].Toggle is { } card)
            {
                card.SetSelected(true, true);
                toggle = $"card {wanted + 1} toggled for a swap";
            }
        }

        string swapped = done > 0 ? $"{done} swap(s)" : string.Empty;
        string tab = swaps.Count == 0 ? LineupViews.FollowTab(open.m_StatsBox, data) : string.Empty;
        return string.Join("; ", new[] { swapped, toggle, tab }.Where(s => s.Length > 0));
    }

    /// <summary>The skater a card shows ("" for an empty seat).</summary>
    public static string SkaterId(SkaterCard card) => card.m_CurrentSkater?.Skater?.id ?? string.Empty;

    /// <summary>The menu's cards in order (the starters' seats, the bench's, the benchwarmers'), each list's shown cards only, and each list's size.</summary>
    private static Lists Cards(BenchwarmerMenu menu)
    {
        List<SkaterCard> starters = Shown(menu.m_ActivePlayersList?.m_SkaterCards);
        List<SkaterCard> bench = Shown(menu.m_BenchPlayersList?.m_SkaterCards);
        List<SkaterCard> pool = PoolCards(menu);
        return new Lists(starters.Concat(bench).Concat(pool).ToList(), new BenchwarmerCardCounts(starters.Count, bench.Count, pool.Count));
    }

    private static List<SkaterCard> Shown(Il2CppSystem.Collections.Generic.List<SkaterCard>? list)
    {
        var cards = new List<SkaterCard>();
        for (int i = 0; list != null && i < list.Count; i++)
        {
            SkaterCard? card = list[i];
            if (card != null && card.gameObject.activeSelf)
            {
                cards.Add(card);
            }
        }

        return cards;
    }

    /// <summary>True when a card is toggled for a swap.</summary>
    private static bool IsToggled(SkaterCard card) => card.Toggle is { } toggle && toggle.IsSelected;

    private readonly record struct Lists(List<SkaterCard> All, BenchwarmerCardCounts Counts);
}
#endif
