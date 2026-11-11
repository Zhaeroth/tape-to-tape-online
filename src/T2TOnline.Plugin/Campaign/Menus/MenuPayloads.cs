#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppI2.Loc;
using Il2CppRogue;
using Il2CppRogue.Rewards;
using Il2CppRogue.Stickers;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Native;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of a menu's data: what each menu was opened with, read from the
/// menu after its <c>InitData</c>, as <see cref="MenuData"/>.
/// <list type="bullet">
/// <item>Reward menus: the offers by asset id and level (relics; talents and abilities; bench powers) with their
/// stickers, the skater choices and forced skater by run skater id, the category and the reroll count. The friend
/// rebuilds them from its own copy of the host's run and its own repositories.</item>
/// <item>Every menu: the host's screen as text, for the friend's spectator panel (and as its fallback when the real
/// menu can't be shown): each choice (a navigable with nothing navigable inside it) with its label, its localization
/// term when it has one, its kind (event choices) and its path; then the other lines of text, a line per row of the
/// layout.</item>
/// </list>
/// </summary>
internal static class MenuPayloads
{
    // Menus are told apart by their class; the id (their definition's name) is what opens them.

    /// <summary>The relic reward menu's class.</summary>
    public const string RelicReward = "RelicRewardMenu";

    /// <summary>The power-up (talent, ability, bench power) reward menu's class.</summary>
    public const string PowerUpReward = "SkaterPowerupRewardMenu";

    /// <summary>The squad, difficulty and game mode menu's class.</summary>
    public const string ChooseMeta = "ChooseMetaMenu";

    /// <summary>The new-unlocks menu's class (each player's own unlocks: never shown with the host's).</summary>
    public const string PermanentUnlock = "PermanentUnlockMenu";

    /// <summary>The in-match menu's class (pause, end of period, end of game).</summary>
    public const string Game = "GameMenu";

    /// <summary>The run-end menu's class (the run's end, the map's "Run stats", the title's run history; <see cref="RunEndView"/>).</summary>
    public const string RunEnd = "RunEndMenu";

    /// <summary>The glossary's class (opened from the pause menu, and from the title screen).</summary>
    public const string Glossary = "GlossaryMenu";

    /// <summary>The event menu's class (every event and NPC node; <see cref="EventCapture"/>, <see cref="EventViews"/>).</summary>
    public const string EventChoice = "EventChoiceMenu";

    /// <summary>
    /// Value: the host's newest run checkpoint when a reward menu was read (whole). The menu was made from that run: a
    /// friend opens the real menu once it holds that checkpoint (a reward's skaters and team come from its own copy of the
    /// run). A value, not a field of the message: older friends ignore it.
    /// </summary>
    public const string RunCheckpoint = "run.checkpoint";

    /// <summary>Row group: an offered relic (id, level, stickers).</summary>
    public const string Relic = "relic";

    /// <summary>Row group: an offered talent or ability (id, level, kind, stickers).</summary>
    public const string PowerUp = "powerup";

    /// <summary>Row group: an offered bench power (id, stickers).</summary>
    public const string Bench = "bench";

    /// <summary>Row group: a skater the power-up can go to (run skater id).</summary>
    public const string Skater = "skater";

    /// <summary>Row group: a choice on the host's screen (text, path, and term, kind, off, a list selector's item and its term when they apply).</summary>
    public const string Choice = "choice";

    /// <summary>Row group: another line of the host's screen (text, and term when it's one localized text).</summary>
    public const string Line = "line";

    /// <summary>The list of rows heading an exhibition controller screen's text panel: its two teams (above its players).</summary>
    public const string Teams = "teams";

    /// <summary>
    /// Row group: a player on the controller assignment screen (<see cref="ControllerScreenRow"/>); a friend writes its own
    /// line from it, with "you" on its own.
    /// </summary>
    public const string Player = ControllerScreenRow.Group;

    /// <summary>Most choices on a panel.</summary>
    public const int MaxChoices = 24;

    /// <summary>Choice row value: the item a list selector shows (<see cref="CycleItem"/>).</summary>
    public const string CycleItemKey = "item";

    /// <summary>Choice row value: the term that gives the item a list selector shows, in the host's language.</summary>
    public const string CycleItemTermKey = "itemTerm";

    private const int CellChars = 60;
    private const int RowChars = 150;
    private const int MaxOffers = 12;

    /// <summary>
    /// Menus never mirrored (by id or class): the loading transitions (each PC plays its own on its own loads). The brawl offer
    /// and fight controls are mirrored since v1 (<see cref="BrawlViews"/>); the host's settings go as a note (no data:
    /// each PC's settings are its own, <see cref="NoteOnly"/>). The native lobby's shells are each PC's own too: a friend's is drawn from the board.
    /// </summary>
    public static bool Skipped(string id, string type) =>
        id.StartsWith("Woosh", StringComparison.Ordinal) || type.StartsWith("Woosh", StringComparison.Ordinal) ||
        type is "InvalidDataMenu" || NativeLobby.LobbyShell.IsShellId(id);

    /// <summary>The settings menu's class: a friend shows a note for it (FriendMenus.Overlays.cs), so no data is read or sent.</summary>
    public const string Settings = "SettingsMenu";

    /// <summary>True for a menu friends show as a note only (the host's settings: this PC's audio, video and controls): its data is empty.</summary>
    public static bool NoteOnly(AbstractMenu menu) => MenuGame.TypeName(menu) == Settings;

    /// <summary>
    /// Host: the friend whose virtual pad is a game player (their Steam ID and name), or null for this PC's own players and
    /// nobody's (set by <see cref="Online.OnlineSession"/>).
    /// </summary>
    public static Func<int, PadOwner?>? PadOwnerOf { get; set; }

    /// <summary>
    /// The data of <paramref name="menu"/> as it shows now. <paramref name="summary"/>: a run summary attachment goes with it
    /// (a run-end menu at the run's end whose summary the host made). <paramref name="events"/>: an event menu's class and
    /// whether the host's choices go with it (made once per opening, <see cref="EventCapture"/>).
    /// </summary>
    public static MenuData Read(AbstractMenu menu, bool summary = false, EventInfo? events = null)
    {
        if (NoteOnly(menu))
        {
            return MenuData.Empty;
        }

        var values = new List<KeyValuePair<string, NetValue>>();
        var items = new List<MenuItem>();
        if (menu.TryCast<RelicRewardMenu>() is { } relics)
        {
            Relics(relics, values, items);
            values.Add(Value(RunCheckpoint, NetValue.FromInt(HostRun())));
        }
        else if (menu.TryCast<SkaterPowerupRewardMenu>() is { } powerUps)
        {
            PowerUps(powerUps, values, items);
            values.Add(Value(RunCheckpoint, NetValue.FromInt(HostRun())));
        }
        else if (menu.TryCast<GameMenu>() is { } game)
        {
            values.Add(Value("context", NetValue.FromString(game.m_Context.ToString())));
            GameMenuCapture.Read(game, values);
        }
        else if (menu.TryCast<GlossaryMenu>() is { } glossary)
        {
            GameMenuCapture.ReadGlossary(glossary, values);
        }
        else if (events != null && menu.TryCast<EventChoiceMenu>() is { } eventMenu)
        {
            values.Add(Value(EventCapture.ClassKey, NetValue.FromString(events.Class)));
            values.Add(Value(EventCapture.SetKey, NetValue.FromBool(events.Set)));
            if (!events.Set && events.Text.Length > 0)
            {
                values.Add(Value(EventCapture.TextKey, NetValue.FromString(MenuGame.Plain(events.Text, RowChars))));
            }

            // Its skaters and the run's relics, talents and abilities come from a friend's copy of the host's run: the one
            // the host held when the event opened.
            values.Add(Value(RunCheckpoint, NetValue.FromInt(events.Run)));
            if (events.Takeover)
            {
                // The Nurse's procedure screen is up over it: its text first (a friend shows it over the event screen).
                NurseScreen.Read(eventMenu, values, items);
            }
        }
        else if (menu.TryCast<RunEndMenu>() is { } runEnd)
        {
            RunEndView.Values(runEnd, summary, values);
            if (runEnd.m_IsDuringRun && !runEnd.m_IsHistoryMode)
            {
                // Shown live on a friend's PC: from its copy of the host's run, once it holds this one.
                values.Add(Value(RunCheckpoint, NetValue.FromInt(HostRun())));
            }
        }
        else if (menu.TryCast<ControllerAssignmentMenu>() is { } assignment)
        {
            ControllerViews.Read(assignment, values, items);
        }
        else if (menu.TryCast<PermanentUnlockMenu>() is { } unlocks)
        {
            UnlockViews.Read(unlocks, values);
        }
        else if (menu.TryCast<CanadaCurseMenu>() is { } curse)
        {
            CanadaViews.Read(curse, values, items);
        }
        else if (menu.TryCast<LineupMenu>() is { } lineup)
        {
            LineupViews.Read(lineup, values, items);
            values.Add(Value(RunCheckpoint, NetValue.FromInt(HostRun()))); // a friend needs this run for the run's team
        }
        else if (menu.TryCast<OldSquadMenu>() is { } oldSquad)
        {
            RunStartCapture.Values(oldSquad, values);
        }
        else if (menu.TryCast<BrawlFightMenu>() is { } brawl)
        {
            BrawlViews.Read(brawl, values);
        }

        // The lineup with benchwarmers: its cards for a friend's real view (first: within the row limit), and why it's text
        // when it is; then its roster as lines for a friend that shows it as text (its cards' own texts would only repeat it).
        BenchwarmerMenu? bench = menu.TryCast<BenchwarmerMenu>();
        if (bench != null)
        {
            BenchViews.Read(bench, RunStartCapture.Loaded(bench), values, items);
            RunStartCapture.Values(bench, values);
            RunStartCapture.Bench(bench, items);
        }

        // Its skater details panels' detailed stats ("UI Left Trigger" held on the host's PC), when it has any.
        DetailsHolds.ReadDetailedStats(menu, values);
        Panel(menu, menu.TryCast<EventChoiceMenu>() != null, items, lines: bench == null);
        return new MenuData(values, items);
    }

    /// <summary>The host's newest run checkpoint (0: none, or not hosting).</summary>
    internal static long HostRun() => Online.CampaignRelay.Current is { Hosting: true } relay ? relay.LatestRun?.Sequence ?? 0 : 0;

    /// <summary>The joined path of a target ("3/0/2"), as a choice row carries it.</summary>
    public static string PathText(MenuTarget target) => string.Join("/", target.Path);

    private static void Relics(RelicRewardMenu menu, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        values.Add(Value("isTeamHome", NetValue.FromBool(menu.m_IsTeamHome)));
        values.Add(Value("rerollCount", NetValue.FromInt(menu.m_RerollCount)));
        values.Add(Value("category", NetValue.FromInt((int)menu.m_Category)));
        values.Add(Value("forcedMatchPick", NetValue.FromBool(menu.m_ForcedMatchPick)));
        values.Add(Value(RewardFadeRules.PostGameKey, NetValue.FromBool(RogueRewardsHandler.IsPostGameReward)));
        string? term = menu.m_Metadata?.TitleTerm;
        if (!string.IsNullOrEmpty(term))
        {
            values.Add(Value("titleTerm", NetValue.FromString(term)));
        }

        foreach (RelicReward reward in Il2CppLists.Items(menu.m_RelicRewards).Take(MaxOffers))
        {
            Relic? relic = reward?.relic;
            items.Add(new MenuItem(Relic, new[]
            {
                Value("id", NetValue.FromString(relic?.id ?? string.Empty)),
                Value("level", NetValue.FromInt(relic?.level ?? 0)),
                Value("stickers", NetValue.FromString(Stickers(reward))),
            }));
        }
    }

    private static void PowerUps(SkaterPowerupRewardMenu menu, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        values.Add(Value("isHomeTeam", NetValue.FromBool(menu.m_IsHomeTeam)));
        values.Add(Value("rerollCount", NetValue.FromInt(menu.m_RerollCount)));
        values.Add(Value("category", NetValue.FromInt((int)menu.m_Category)));
        values.Add(Value("forcedSkater", NetValue.FromString(menu.m_ForcedSkater?.id ?? string.Empty)));
        values.Add(Value(RewardFadeRules.PostGameKey, NetValue.FromBool(RogueRewardsHandler.IsPostGameReward)));
        values.Add(Value("hasPowerUps", NetValue.FromBool(menu.m_PowerUpRewards != null)));
        values.Add(Value("hasBench", NetValue.FromBool(menu.m_BenchRewards != null)));
        values.Add(Value("hasSkaters", NetValue.FromBool(menu.m_Skaters != null)));
        foreach (PowerUpReward reward in Il2CppLists.Items(menu.m_PowerUpRewards).Take(MaxOffers))
        {
            PowerUp? power = reward?.powerUp;
            string kind = power == null ? string.Empty
                : power.TryCast<Talent>() != null ? "talent"
                : power.TryCast<Ability>() != null ? "ability"
                : MenuGame.TypeName(power);
            items.Add(new MenuItem(PowerUp, new[]
            {
                Value("id", NetValue.FromString(power?.id ?? string.Empty)),
                Value("level", NetValue.FromInt(power?.level ?? 0)),
                Value("kind", NetValue.FromString(kind)),
                Value("stickers", NetValue.FromString(Stickers(reward))),
            }));
        }

        foreach (BenchPowerReward reward in Il2CppLists.Items(menu.m_BenchRewards).Take(MaxOffers))
        {
            items.Add(new MenuItem(Bench, new[]
            {
                Value("id", NetValue.FromString(reward?.benchPower?.ID ?? string.Empty)),
                Value("stickers", NetValue.FromString(Stickers(reward))),
            }));
        }

        foreach (Il2CppData.SkaterData skater in Il2CppLists.Items(menu.m_Skaters).Take(MaxOffers))
        {
            items.Add(new MenuItem(Skater, new[] { Value("id", NetValue.FromString(skater?.id ?? string.Empty)) }));
        }
    }

    /// <summary>A reward's stickers as "Stat:value" pairs, comma-separated ("" for none).</summary>
    private static string Stickers(Reward? reward)
    {
        Il2CppSystem.Collections.Generic.List<Sticker>? stickers = reward?.stickers;
        if (stickers == null || stickers.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (int i = 0; i < stickers.Count && i < 8; i++)
        {
            Sticker? sticker = stickers[i];
            if (sticker != null)
            {
                parts.Add($"{sticker.Stat}:{sticker.Value}");
            }
        }

        return string.Join(",", parts);
    }

    /// <summary>A colour as one whole value: red, green, blue and alpha, a byte each (red highest).</summary>
    public static long Rgba(Color color)
    {
        static long Byte(float channel) => (long)Math.Round(Math.Clamp(channel, 0f, 1f) * 255f);
        return (Byte(color.r) << 24) | (Byte(color.g) << 16) | (Byte(color.b) << 8) | Byte(color.a);
    }

    /// <summary>The colour of <see cref="Rgba"/>'s value, or null when it's out of range.</summary>
    public static Color? FromRgba(long rgba) =>
        rgba is < 0 or > uint.MaxValue ? null : new Color(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    /// <summary>
    /// One player's line on the controller screen's panel ("P2 · Sam · playing, locked at centre (B. Smith), online pad,
    /// ready"; "AI" when the AI plays it).
    /// </summary>
    public static string PlayerLine(int slot, string who, string side, string position, string skater, string device, bool ai, bool ready)
    {
        var parts = new List<string> { side };
        if (position.Length > 0)
        {
            parts.Add(skater.Length > 0 ? $"{position} ({skater})" : position);
        }

        if (device.Length > 0)
        {
            parts.Add(device);
        }

        if (ai)
        {
            parts.Add("AI");
        }

        if (ready)
        {
            parts.Add("ready");
        }

        return $"P{slot + 1} · {who} · {string.Join(", ", parts)}";
    }

    /// <summary>The host's screen as text: choices first, then (with <paramref name="lines"/>) the other lines, within the data's row limit.</summary>
    private static void Panel(AbstractMenu menu, bool eventChoices, List<MenuItem> items, bool lines = true)
    {
        Transform root = menu.transform;
        var navigables = new List<AbstractNavigable>();
        foreach (AbstractNavigable navigable in root.GetComponentsInChildren<AbstractNavigable>(false))
        {
            if (navigable != null)
            {
                navigables.Add(navigable);
            }
        }

        // A choice is a navigable with no navigable inside it (a list's parent isn't a choice; its items are).
        var all = new HashSet<IntPtr>(navigables.Select(n => n.transform.Pointer));
        var parents = new HashSet<IntPtr>();
        foreach (AbstractNavigable navigable in navigables)
        {
            for (Transform? at = navigable.transform.parent; at != null && at.Pointer != root.Pointer; at = at.parent)
            {
                if (all.Contains(at.Pointer))
                {
                    parents.Add(at.Pointer);
                }
            }
        }

        var leaves = new HashSet<IntPtr>();
        int choices = 0;
        foreach (AbstractNavigable navigable in navigables)
        {
            if (parents.Contains(navigable.transform.Pointer) || choices >= MaxChoices || items.Count >= MenuData.MaxItems)
            {
                continue;
            }

            MenuTarget? path = MenuTargets.PathTo(root, navigable);
            if (path == null)
            {
                continue;
            }

            leaves.Add(navigable.transform.Pointer);
            string label = Label(navigable.transform, out string term);
            var row = new List<KeyValuePair<string, NetValue>>
            {
                Value("text", NetValue.FromString(label)),
                Value("path", NetValue.FromString(PathText(path))),
            };
            if (term.Length > 0)
            {
                row.Add(Value("term", NetValue.FromString(term)));
            }

            if (!navigable.IsNavigable)
            {
                row.Add(Value("off", NetValue.FromBool(true)));
            }

            CycleItem(navigable, row);
            if (eventChoices && navigable.GetComponentInParent<EventChoiceItem>() is { } item && item != null)
            {
                row.Add(Value("kind", NetValue.FromString(item.ChoiceData.ChoiceType.ToString())));
            }

            items.Add(new MenuItem(Choice, row));
            choices++;
        }

        if (lines)
        {
            Lines(root, leaves, items);
        }
    }

    /// <summary>The menu's other text, a line per row of the layout (the texts sharing a parent), in hierarchy order.</summary>
    private static void Lines(Transform root, HashSet<IntPtr> choices, List<MenuItem> items)
    {
        IntPtr parent = IntPtr.Zero;
        var cells = new List<string>();
        string term = string.Empty;
        string previous = string.Empty;
        void Flush()
        {
            if (cells.Count == 0 || items.Count >= MenuData.MaxItems)
            {
                cells.Clear();
                return;
            }

            string text = MenuGame.Plain(string.Join(" · ", cells), RowChars);
            if (text.Length > 0 && text != previous)
            {
                var row = new List<KeyValuePair<string, NetValue>> { Value("text", NetValue.FromString(text)) };
                if (cells.Count == 1 && term.Length > 0)
                {
                    row.Add(Value("term", NetValue.FromString(term)));
                }

                items.Add(new MenuItem(Line, row));
                previous = text;
            }

            cells.Clear();
            term = string.Empty;
        }

        foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(false))
        {
            if (items.Count >= MenuData.MaxItems)
            {
                break;
            }

            if (text == null || InChoice(root, text.transform, choices))
            {
                continue;
            }

            string shown = MenuGame.Shown(text, CellChars);
            if (shown.Length == 0)
            {
                continue;
            }

            IntPtr at = text.transform.parent?.Pointer ?? IntPtr.Zero;
            if (at != parent)
            {
                Flush();
                parent = at;
            }

            if (cells.Count == 0)
            {
                term = TermOf(text, shown);
            }

            if (!cells.Contains(shown))
            {
                cells.Add(shown);
            }
        }

        Flush();
    }

    /// <summary>A choice's label: its visible texts, joined; with its localization term when it's one localized text.</summary>
    private static string Label(Transform choice, out string term)
    {
        term = string.Empty;
        var cells = new List<string>();
        TMP_Text? first = null;
        foreach (TMP_Text text in choice.GetComponentsInChildren<TMP_Text>(false))
        {
            string shown = MenuGame.Shown(text, CellChars);
            if (shown.Length > 0 && !cells.Contains(shown) && cells.Count < 4)
            {
                cells.Add(shown);
                first ??= text;
            }
        }

        if (cells.Count == 1 && first != null)
        {
            term = TermOf(first, cells[0]);
        }

        return MenuGame.Plain(string.Join(" · ", cells), RowChars);
    }

    /// <summary>
    /// The localization term that made <paramref name="text"/> say <paramref name="shown"/>, or "": only when the term alone
    /// gives that text here (not a text a script set over it, nor one filled with parameters), so a friend's own
    /// translation of it says the same thing.
    /// </summary>
    private static string TermOf(TMP_Text text, string shown)
    {
        Localize? localize = text.GetComponent<Localize>();
        string term = localize != null && !string.IsNullOrEmpty(localize.mTerm) ? localize.mTerm : string.Empty;
        return term.Length > 0 && MenuGame.Plain(MenuGame.Localized(term), CellChars) == shown ? term : string.Empty;
    }

    /// <summary>
    /// A list selector's choice row: the item it shows (<c>CycleList.m_ItemText</c>, its
    /// label's own text is only its placeholder) as <see cref="CycleItemKey"/>, and the term that gives that text in the
    /// host's language as <see cref="CycleItemTermKey"/> (the squad screen's difficulty: <c>T2TUtils.GetDifficultyLocKey</c>;
    /// else the text's own localization term), so a friend whose profile lacks the choice shows the host's in its own
    /// language.
    /// </summary>
    private static void CycleItem(AbstractNavigable navigable, List<KeyValuePair<string, NetValue>> row)
    {
        if (navigable.TryCast<CycleList>() is not { } list || list.m_ItemText is not { } text)
        {
            return;
        }

        string shown = MenuGame.Shown(text, CellChars);
        if (shown.Length == 0)
        {
            return;
        }

        row.Add(Value(CycleItemKey, NetValue.FromString(shown)));

        // Its items come with its Init (the squad screen's, from RefreshSelectableDifficultiesAndGameModes, after the menu's
        // first frames); until then CurrentItem throws (it indexes a null list; verified in the game's code), and the label
        // shows its placeholder: the row goes without a term, and a later read has it. Any other state the getter refuses
        // costs only the term, never the menu's whole text.
        string term;
        try
        {
            term = list.m_Items != null && list.CurrentItem?.TryCast<ChooseMetaMenu.DifficultyItem>() is { } difficulty
                ? T2TUtils.GetDifficultyLocKey(difficulty.Difficulty) ?? string.Empty
                : string.Empty;
        }
        catch (Exception)
        {
            term = string.Empty;
        }

        if (term.Length == 0 || MenuGame.Plain(MenuGame.Localized(term), CellChars) != shown)
        {
            term = TermOf(text, shown);
        }

        if (term.Length > 0)
        {
            row.Add(Value(CycleItemTermKey, NetValue.FromString(term)));
        }
    }

    private static bool InChoice(Transform root, Transform text, HashSet<IntPtr> choices)
    {
        for (Transform? at = text; at != null && at.Pointer != root.Pointer; at = at.parent)
        {
            if (choices.Contains(at.Pointer))
            {
                return true;
            }
        }

        return false;
    }

    private static KeyValuePair<string, NetValue> Value(string key, NetValue value) => new(key, value);
}

/// <summary>What the host's event menu sends with its data (made once per opening).</summary>
/// <param name="Class">The event's class ("CoachEvent"), or "".</param>
/// <param name="Set">True when the host's choices go with it as an attachment.</param>
/// <param name="Text">Why it shows as text on friends ("" when it doesn't).</param>
/// <param name="Run">The host's newest run checkpoint when it opened.</param>
/// <param name="Takeover">The Nurse's procedure screen has taken it over (its text goes with the data).</param>
internal sealed record EventInfo(string Class, bool Set, string Text, long Run, bool Takeover = false);

/// <summary>A friend's virtual pad on the host: whose it is.</summary>
/// <param name="SteamId">The friend's Steam ID.</param>
/// <param name="Name">The friend's Steam name.</param>
internal readonly record struct PadOwner(ulong SteamId, string Name);
#endif
