#if HAS_INTEROP
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppRogue;
using Il2CppRogue.CanadaCurses;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The Canada curse (<c>CanadaCurseMenu</c>, a Team Canada match's curse picks, before the match and at a later faceoff) as
/// a real view on a friend (v1). The host's picker rolls the curses on offer (each made by its catalog entry,
/// <c>CanadaCurseCatalog.Entry.Create</c>) and opens the menu with them, the pick's number, the Canada team and the scene's speech
/// bubbles; the menu's own sequence slides Pucklin and Hatfreeze in over the blurred backdrop, plays their lines and shows the
/// cards, and a card's click runs its exit (a white fade, the faceoff, the backdrop hidden).
/// <list type="bullet">
/// <item>Host: each curse's catalog key and id, the pick's number and which side's team Canada is, in the menu's data.</item>
/// <item>Friend (the friend's own picker never runs: <c>CampaignMatch</c> skips it and removes its load cover): makes the
/// same curses with the catalog's own <c>Create</c> (each id read back), takes its own Canada team (that side's, its id
/// checked) and the scene's speech bubbles, and opens the real menu with them; the menu's reveal and white-reached
/// callbacks are left null (verified in the game's code: <c>RunSequence</c> and <c>ExitSequence</c> skip a
/// null one). Its own pick and lineup press are refused; it closes with the host's (the base close, its speech canvas put back and
/// its backdrop hidden, then the in-match menu's backdrop watchdog). Its dialogue follows the host's presses (the speech mirror:
/// Pucklin and Hatfreeze are its two speakers, <see cref="SpeechMirror"/>), so its cards come when the host's dialogue ends, at
/// the latest once the host's cards are up (<see cref="CardsGroup"/>: whatever still speaks here runs out); the curses made for it
/// are destroyed once it has hidden (v1, as the host's game destroys the ones not picked).</item>
/// </list>
/// </summary>
internal static class CanadaViews
{
    /// <summary>The Canada curse menu's class.</summary>
    public const string Type = "CanadaCurseMenu";

    /// <summary>Row group: a curse on offer (its catalog key, id and level).</summary>
    public const string Curse = "curse";

    /// <summary>
    /// Value group: item 0 is 1 once the host's curse cards are up (so its dialogue is over, <see cref="HostCardsUp"/>; v1,
    /// a friend's dialogue can't hang on a line the host's game never had).
    /// </summary>
    public const string CardsGroup = "canada";

    private static readonly MenuTarget CardsTarget = MenuTarget.AtItem(CardsGroup, 0);

    private const string PickKey = "canada.pick";
    private const string SideKey = "canada.side";
    private const string TeamKey = "canada.team";
    private const string HomeSide = "home";
    private const string VisitorSide = "visitor";
    private const int MaxCurses = 8;
    private const float FadeOut = 0.2f;

    // The curses made here for each menu's data (by the data object), the newest few; and those of closed menus, destroyed
    // once their menu has hidden (at least ReleaseSeconds after the close; given up after ReleaseGiveUpSeconds).
    private const int MaxKept = 4;
    private const double ReleaseSeconds = 1;
    private const double ReleaseGiveUpSeconds = 10;
    private static readonly List<(System.IntPtr Data, List<Relic> Curses)> Kept = new();
    private static readonly List<Releasing> Closed = new();

    /// <summary>
    /// Host, once a menu: which curses it offers, to compare with each friend's "opens here" line; one with no
    /// entry in the game's catalog is said too (no friend can make it: the pick goes to them as text).
    /// </summary>
    public static void NameForLog(string id, uint serial, MenuData data)
    {
        List<MenuItem> rows = data.Group(Curse).ToList();
        ModLog.Info($"Menus: the host's {id} (#{serial}) is pick {PickOf(data)}: " +
            $"{string.Join(", ", rows.Select(row => $"{row.Text("id")} ({row.Text("key")}, level {row.Whole("level")})"))}; Canada on the {data.Text(SideKey)} side ({data.Text(TeamKey)}).");
        foreach (MenuItem row in rows.Where(row => row.Text("key").Length == 0))
        {
            ModLog.Warn($"Menus: the host's Canada curse {row.Text("id")} has no entry in the game's curse catalog; friends can't make it and see the pick as text.");
        }
    }

    /// <summary>The pick's number in the host's data, as both PCs' lines say it (from 1; 0: not said).</summary>
    public static long PickOf(MenuData data) => data.Whole(PickKey, -1) + 1;

    /// <summary>The curse at <paramref name="index"/> of the host's data in the logs' words: "curse 2 (id)".</summary>
    public static string CurseWords(MenuData data, int index) =>
        $"curse {index + 1} ({(data.Group(Curse).ElementAtOrDefault(index)?.Text("id") is { } id ? HostText.KeyForLog(id) : "not in the host's data")})";

    /// <summary>Which of the menu's cards <paramref name="navigable"/> is (-1: none of them): the card's own list, as its click handler looks it up.</summary>
    public static int CardOf(CanadaCurseMenu menu, AbstractNavigable? navigable)
    {
        Il2CppList.List<AbstractNavigable>? cards = menu.m_CardNavigables;
        for (int i = 0; navigable != null && cards != null && i < cards.Count; i++)
        {
            if (cards[i]?.Pointer == navigable.Pointer)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Host: the curses on offer, the pick's number and the Canada team's side, for a friend's copy.</summary>
    public static void Read(CanadaCurseMenu menu, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        CanadaCurseMenu.Metadata? data = menu.m_Metadata;
        if (data == null)
        {
            return;
        }

        values.Add(new(PickKey, NetValue.FromInt(data.PickIndex)));
        TeamData? team = data.CanadaTeam;
        GameManager? game = GameMenuCapture.Game();
        string side = team == null ? "none"
            : game?._homeTeam?._teamData?.Pointer == team.Pointer ? HomeSide
            : game?._visitorTeam?._teamData?.Pointer == team.Pointer ? VisitorSide
            : "other";
        values.Add(new(SideKey, NetValue.FromString(side)));
        values.Add(new(TeamKey, NetValue.FromString(SafeText.Clean(team?.id, 96))));
        Il2CppList.List<Relic>? relics = data.Relics;
        for (int i = 0; relics != null && i < relics.Count && i < MaxCurses; i++)
        {
            Relic? relic = relics[i];
            items.Add(new MenuItem(Curse, new KeyValuePair<string, NetValue>[]
            {
                new("key", NetValue.FromString(KeyOf(relic))),
                new("id", NetValue.FromString(SafeText.Clean(relic?.id, 96))),
                new("level", NetValue.FromInt(relic?.level ?? 0)),
            }));
        }
    }

    /// <summary>The catalog key of the entry that makes a curse of <paramref name="relic"/>'s class ("" when none does).</summary>
    private static string KeyOf(Relic? relic)
    {
        string? type = relic?.GetIl2CppType()?.FullName;
        Il2CppList.List<CanadaCurseCatalog.Entry>? entries = CanadaCurseCatalog.Entries;
        for (int i = 0; type != null && entries != null && i < entries.Count; i++)
        {
            CanadaCurseCatalog.Entry? entry = entries[i];
            if (entry?.RelicType?.FullName == type)
            {
                return SafeText.Clean(entry.Key, 64);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Friend: the menu's data with the host's curses made here (see <see cref="Build"/>), or null with why. Never throws: a
    /// failure is the reason.
    /// </summary>
    public static CanadaCurseMenu.Metadata? Request(MenuData data, out string problem, out string about)
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
    /// Friend: the menu's data with the same curses (made here by the catalog's own entries), this PC's Canada team and the
    /// scene's speech bubbles, or null with why.
    /// </summary>
    private static CanadaCurseMenu.Metadata? Build(MenuData data, out string problem, out string about)
    {
        problem = string.Empty;
        about = string.Empty;
        long pick = data.Whole(PickKey, -1);
        if (pick is < 0 or > 9)
        {
            problem = "the host's curse pick didn't say which pick it is";
            return null;
        }

        string side = data.Text(SideKey);
        GameManager? game = GameMenuCapture.Game();
        TeamData? team = side == HomeSide ? game?._homeTeam?._teamData : side == VisitorSide ? game?._visitorTeam?._teamData : null;
        if (team == null || SafeText.Clean(team.id, 96) != data.Text(TeamKey))
        {
            problem = team == null ? $"this PC's match has no Canada team on the host's side ({HostText.KeyForLog(side)})" : "this PC's match has another team on Canada's side";
            return null;
        }

        SpeechBubbleController? bubbles = UnityEngine.Object.FindObjectOfType<SpeechBubbleController>();
        if (bubbles == null)
        {
            problem = "this PC's match scene has no speech bubbles for Pucklin and Hatfreeze";
            return null;
        }

        // As many as the host's game reads (a crafted host could ask for a curse made per row of its menu data).
        List<MenuItem> rows = data.Group(Curse).ToList();
        if (rows.Count == 0 || rows.Count > MaxCurses)
        {
            problem = rows.Count == 0 ? "the host's curse pick offered no curses" : $"the host's curse pick offered {rows.Count} curses (at most {MaxCurses} are shown)";
            return null;
        }

        // Every curse made here goes again if this menu's data can't be made whole (a later curse missing or not the host's,
        // or the game's code throwing): nothing else holds them.
        var made = new List<Relic>(rows.Count);
        bool kept = false;
        try
        {
            var curses = new Il2CppList.List<Relic>();
            var names = new List<string>();
            foreach (MenuItem row in rows)
            {
                Relic? curse = Make(row.Text("key"), out string why);
                if (curse != null)
                {
                    made.Add(curse);
                }

                if (curse == null || SafeText.Clean(curse.id, 96) != row.Text("id"))
                {
                    problem = curse == null ? why : $"the curse \"{HostText.KeyForLog(row.Text("key"))}\" made here isn't the host's ({SafeText.Clean(curse.id, 48)})";
                    return null;
                }

                curses.Add(curse);
                names.Add($"{SafeText.Clean(curse.id, 48)} level {curse.level}");
                if (curse.level != row.Whole("level"))
                {
                    // Made by the same catalog entry as the host's: only a changed catalog can do this. The card still shows it.
                    ModLog.Warn($"Menus: {Type}: the curse {SafeText.Clean(curse.id, 48)} made here is level {curse.level}, the host's is level {row.Whole("level")}; it shows as made here.");
                }
            }

            about = $"pick {pick + 1}, {curses.Count} curse(s) made here ({string.Join(", ", names)}), Canada on the {side} side";

            // OnWhiteReached and OnReadyToReveal stay null: the menu skips a null one, and the picker's own (the reveal of the
            // loaded match, the faceoff's wait) are the host's.
            var metadata = new CanadaCurseMenu.Metadata(curses, (int)pick, team, bubbles);
            kept = true;
            Kept.Add((metadata.Pointer, made));
            if (Kept.Count > MaxKept)
            {
                Kept.RemoveAt(0); // a request that never opened: its curses are left to the game's unused-asset unload
            }

            return metadata;
        }
        finally
        {
            if (!kept)
            {
                Destroy(made, "it can't be shown here");
            }
        }
    }

    /// <summary>
    /// Destroys curses made here for a menu that can't be shown, or whose menu has closed and hidden (a curse relic
    /// registers nothing when made or destroyed; verified in the game's code: <c>Relic</c> has only an <c>OnEnable</c>,
    /// and the curse classes add none; the host's own game destroys the curses not picked while its menu still closes). Never
    /// throws.
    /// </summary>
    private static void Destroy(List<Relic> made, string why)
    {
        int destroyed = 0;
        foreach (Relic curse in made)
        {
            try
            {
                UnityEngine.Object.Destroy(curse);
                destroyed++;
            }
            catch (System.Exception ex)
            {
                ModLog.Warn($"Menus: a curse made here couldn't be destroyed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        if (destroyed > 0)
        {
            ModLog.Info($"Menus: {Type}: destroyed the {destroyed} curse(s) made here for the host's pick ({why}).");
        }
    }

    /// <summary>
    /// Friend, every frame: the curses of a closed menu are destroyed once it has hidden and <see cref="ReleaseSeconds"/>
    /// passed (one that doesn't hide within <see cref="ReleaseGiveUpSeconds"/> keeps them: the game's unused-asset unload
    /// frees them at the next scene). Never throws.
    /// </summary>
    public static void ReleaseTick()
    {
        double now = Streaming.FrameClock.Seconds;
        for (int i = Closed.Count - 1; i >= 0; i--)
        {
            Releasing closed = Closed[i];
            double age = now - closed.Since;
            bool hidden;
            try
            {
                hidden = closed.Menu == null || closed.Menu.IsHidden;
            }
            catch (System.Exception)
            {
                hidden = true;
            }

            if (hidden && age >= ReleaseSeconds)
            {
                Closed.RemoveAt(i);
                Destroy(closed.Curses, "its menu closed and hid");
            }
            else if (age > ReleaseGiveUpSeconds)
            {
                Closed.RemoveAt(i);
                ModLog.Info($"Menus: {Type}: its menu didn't hide within {ReleaseGiveUpSeconds:0} s of its close; the {closed.Curses.Count} curse(s) made here are left to the game.");
            }
        }
    }

    /// <summary>Friend: following stopped; the curses waiting for their menu to hide are left to the game.</summary>
    public static void ClearReleases()
    {
        Kept.Clear();
        Closed.Clear();
    }

    /// <summary>A curse made by its catalog entry, as the host's picker makes it; null with why.</summary>
    private static Relic? Make(string key, out string why)
    {
        why = string.Empty;
        Il2CppList.List<CanadaCurseCatalog.Entry>? entries = CanadaCurseCatalog.Entries;
        for (int i = 0; key.Length > 0 && entries != null && i < entries.Count; i++)
        {
            CanadaCurseCatalog.Entry? entry = entries[i];
            if (entry?.Key == key)
            {
                Relic? made = entry.Create?.Invoke();
                why = made == null ? $"the curse \"{HostText.KeyForLog(key)}\" couldn't be made here" : string.Empty;
                return made;
            }
        }

        why = $"the curse \"{HostText.KeyForLog(key)}\" isn't in this game's catalog";
        return null;
    }

    /// <summary>True once <paramref name="menu"/> has shown its cards (a friend's copy: the host's highlight has something to land on; the host's: <see cref="CardsGroup"/>).</summary>
    public static bool CardsUp(CanadaCurseMenu menu) => (menu.m_Cards?.Count ?? 0) > 0;

    /// <summary>Friend: true once the host's curse cards are up, as its menu's values say.</summary>
    public static bool HostCardsUp(MirroredMenu menu) => menu.Values.TryGetValue(CardsTarget, out int up) && up == 1;

    /// <summary>
    /// Friend: what the menu's own exit would have put back, before the base close: its speech canvas and its blurred
    /// backdrop; the watchdog checks a second later that the backdrop is gone. Never throws (the close goes on; logged).
    /// </summary>
    public static void BeforeClose(CanadaCurseMenu open)
    {
        // Its curses go once it has hidden (ReleaseTick).
        System.IntPtr data = open.m_Metadata?.Pointer ?? System.IntPtr.Zero;
        int at = data == System.IntPtr.Zero ? -1 : Kept.FindLastIndex(k => k.Data == data);
        if (at >= 0)
        {
            Closed.Add(new Releasing(open, Kept[at].Curses, Streaming.FrameClock.Seconds));
            Kept.RemoveAt(at);
        }

        int owners = -1;
        bool held = false;
        try
        {
            open.RestoreSpeechCanvas();
            _ = SceneBackdrop.Hide(open, FadeOut);
            Il2CppList.Dictionary<Il2CppSystem.Object, int>? all = SceneBackdrop.s_Instance?.m_Owners;
            owners = all?.Count ?? -1;
            held = all != null && all.ContainsKey(open);
        }
        catch (System.Exception ex)
        {
            ModLog.Warn($"Menus: the Canada curse menu's speech canvas or backdrop couldn't be put back here ({ModLog.SafeErrorText(ex)}); the watchdog checks the backdrop in a second.");
        }

        GameMenuViews.WatchBackdrop(open, "Canada curse menu");
        ModLog.Info($"Menus: the Canada curse menu closed here; {(held ? "its backdrop is still up (checked again in a second)" : "backdrop restored")} " +
            $"({(owners < 0 ? "no backdrop in this scene" : $"{owners} backdrop owner(s) left")}), speech canvas put back.");
    }

    /// <summary>A closed menu's curses, waiting for it to hide.</summary>
    private sealed record Releasing(CanadaCurseMenu Menu, List<Relic> Curses, double Since);
}
#endif
