#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppRogue;
using Il2CppRogue.Powerups.BenchQuestPowerups;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppUICore.ReusableList;
using Il2CppUnlocks;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using T2TOnline.Plugin.Streaming;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The unlockables screen (<c>UnlockableMenu</c>, from the title, the map's menu and the pause menu) as a real view on a
/// friend. The menu builds every row from the game's unlock assets and only three values of each from the profile (unlocked, progress,
/// total), at one place (<c>ConvertUnlockData</c>); its quests tab from the benchwarmers' configs and quests.
/// <list type="bullet">
/// <item>Host: each row's three values as its menu converts them, then (<c>InitTabs</c>) the quest rows and the tabs, as an
/// <see cref="MenuAttachmentKind.UnlockStates"/> attachment (<see cref="UnlockStatesData"/>); the open tab (by what it lists)
/// and the highlighted row (by unlock) as menu steps (<see cref="TabGroup"/>, <see cref="RowGroup"/>).</item>
/// <item>Friend: a plan (<see cref="UnlockPlan"/>) armed before the menu is opened (its rows are converted inside its
/// <c>InitData</c>): each row this PC's game converts takes the host's three values; then, before its tabs are made, each
/// quest row is made the host's (the game's own secret row when the host's is; the game's own open row, its texts made here
/// in this PC's language, when only the host's is open). Everything else (texts, icons, sort, language) is this PC's own
/// game's. The host's tab and highlighted row are followed by what they are, never by place (rows sort by their translated
/// title; a tab is there only when it has rows).</item>
/// </list>
/// Nothing here writes the profile: the menu reads it only, and the mod changes only the rows the menu made.
/// </summary>
internal static class UnlockablesView
{
    /// <summary>The menu's class.</summary>
    public const string Type = "UnlockableMenu";

    /// <summary>Value group: the host's open tab, by its code (<see cref="UnlockStatesData.TabCode"/>).</summary>
    public const string TabGroup = "unlockTab";

    /// <summary>Value group: the host's highlighted row, by its cursor key (<see cref="UnlockStatesData.RowKey"/>, <see cref="UnlockStatesData.QuestKey"/>).</summary>
    public const string RowGroup = "unlockRow";

    // An armed plan older than this is stale (its menu never opened): it's dropped at the next look.
    private const double ArmedSeconds = 20;
    private const int MaxHostMenus = 4;
    private const int MaxErrors = 3;

    // The secret quest row's texts and colours, as the game writes them (UnlockableMenu.<InitQuestsTab>d__40).
    private const string Secret = "???";
    private const string QuestColorOpen = "<color=#FFD65A>";
    private const string QuestColorClose = "</color>";
    private const string Break = "\n\n";

    private static readonly Dictionary<IntPtr, HostTable> HostTables = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static UnlockPlan? _armed;
    private static double _armedAt;
    private static int _errors;

    /// <summary>True while a plan waits for its menu's rows (a friend asked the game to open the host's unlockables).</summary>
    public static bool Armed => Current() != null;

    // ---- Host ----

    /// <summary>
    /// Host: one row the menu just converted (<c>ConvertUnlockData</c>'s postfix): its id, category and the profile's three
    /// values. A menu converting rows again (it's shown again) starts a new table. Reads only.
    /// </summary>
    public static void HostConverted(UnlockableMenu menu, IUnlockData unlock, UnlockData row)
    {
        if (!HostTables.TryGetValue(menu.Pointer, out HostTable? table) || table.Complete)
        {
            Forget();
            HostTables[menu.Pointer] = table = new HostTable();
        }

        string? key = unlock.RewardID;
        int category = (int)row.Category;
        if (table.Rows.Count >= UnlockStatesData.MaxRows || !UnlockStatesData.IsKey(key) || category is < 0 or > UnlockStatesData.MaxCategory ||
            !table.Seen.Add((key!, category)))
        {
            table.Skipped++;
            return;
        }

        table.Keys[row.Pointer] = UnlockStatesData.RowKey(table.Rows.Count);
        table.Rows.Add(new UnlockRowState(key!, category, row.IsUnlocked, Count(row.UnlockProgress), Count(row.UnlockTotal)));
    }

    /// <summary>
    /// Host: the menu is making its tabs (<c>InitTabs</c>'s postfix): the quest rows and the tabs join the rows, and the
    /// whole goes to friends as the menu's attachment (now, or once the menu is on the board). Logged once per open.
    /// </summary>
    public static void HostTabs(UnlockableMenu menu, Il2CppList.List<TabData>? tabs, HostMenus host, bool friends)
    {
        long started = Stopwatch.GetTimestamp();
        if (!HostTables.TryGetValue(menu.Pointer, out HostTable? table) || table.Complete)
        {
            Warn("host tabs", "Menus: the host's UnlockableMenu made its tabs without its rows read (hosting started while it was opening); friends see a note.");
            return;
        }

        table.Complete = true;
        var codes = new List<int>();
        var quests = new List<QuestRowState>();
        string questProblem = string.Empty;
        foreach (TabData tab in Il2CppLists.Items<TabData>(tabs))
        {
            List<UnlockData> rows = Il2CppLists.Items<UnlockData>(tab?.Unlocks).ToList();
            int code = UnlockStatesData.TabCode(rows.Select(r => (int)r.Category));
            table.TabCodes.Add(code);
            if (code >= 0)
            {
                codes.Add(code);
            }

            if (code == UnlockStatesData.QuestCategory)
            {
                questProblem = QuestRows(menu, rows, table, quests);
            }
        }

        var states = new UnlockStatesData { Rows = table.Rows, Quests = quests, TabCodes = codes.Take(UnlockStatesData.MaxTabs).ToList() };
        string? problem = states.Problem();
        if (problem != null)
        {
            ModLog.Warn($"Menus: the host's UnlockableMenu states can't be sent ({problem}); friends see a note.");
            return;
        }

        var writer = new BitWriter(4096);
        states.Write(writer);
        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (!host.Attach(menu, MenuAttachmentKind.UnlockStates, serial => MenuAttachment.UnlockStates(serial, states), states.ToString(), friends, out problem))
        {
            ModLog.Warn($"Menus: the host's UnlockableMenu states can't be sent ({problem}); friends see a note.");
            return;
        }

        int secret = quests.Count(q => q.Secret);
        ModLog.Info($"Menus: the host's UnlockableMenu: {states.Rows.Count} unlock row(s) ({states.Rows.Count(r => r.Unlocked)} unlocked) in {table.TabCodes.Count} tab(s), " +
            string.Create(CultureInfo.InvariantCulture, $"{quests.Count} quest row(s) ({secret} secret), {writer.ByteLength / 1024.0:0.0} KB (tabs read in {ms:0.0} ms){Skipped(table)}{questProblem}; ") +
            "friends get its states for the real menu.");
    }

    /// <summary>Host: the menu opened a tab (<c>RefreshTabContent</c>'s postfix): its code goes out.</summary>
    public static void HostTab(UnlockableMenu menu, HostMenus host)
    {
        int index = menu.m_CurTabIndex;
        if (HostTables.TryGetValue(menu.Pointer, out HostTable? table) && index >= 0 && index < table.TabCodes.Count && table.TabCodes[index] >= 0)
        {
            host.Step(menu, TabGroup, 0, table.TabCodes[index]);
        }
    }

    /// <summary>Host: a row was highlighted (<c>OnRowHighlighted</c>'s postfix): its key goes out (a row not in the table: nothing, logged once).</summary>
    public static void HostRow(UnlockableMenu menu, HostMenus host)
    {
        UnlockData? row = menu.m_SelectedUnlock;
        if (row == null || !HostTables.TryGetValue(menu.Pointer, out HostTable? table))
        {
            return;
        }

        if (table.Keys.TryGetValue(row.Pointer, out int key))
        {
            host.Step(menu, RowGroup, 0, key);
        }
        else if (Logged.Add("host row"))
        {
            ModLog.Info("Menus: the host highlighted an UnlockableMenu row that isn't among the states sent (its id was skipped); friends' highlight stays where it was.");
        }
    }

    /// <summary>
    /// Host: the open tab's code and the highlighted row's key as the menu shows them now (-1 for one not known: its tabs
    /// not made yet, no row highlighted, or a row not among the states). A menu that showed before any friend was in sent
    /// neither as a step: both go on the board when it's first shared (<c>HostMenus.Track</c>), so a friend who joins then
    /// opens on the host's tab and row. Never throws.
    /// </summary>
    public static (int Tab, int Row) HostNow(UnlockableMenu menu)
    {
        try
        {
            if (!HostTables.TryGetValue(menu.Pointer, out HostTable? table))
            {
                return (-1, -1);
            }

            int index = menu.m_CurTabIndex;
            int tab = index >= 0 && index < table.TabCodes.Count ? table.TabCodes[index] : -1;
            UnlockData? row = menu.m_SelectedUnlock;
            int key = row != null && table.Keys.TryGetValue(row.Pointer, out int found) ? found : -1;
            return (tab, key);
        }
        catch (Exception ex)
        {
            Warn("host now", $"Menus: the host's UnlockableMenu tab and row couldn't be read for a friend who joins ({ex.GetType().Name}); they follow from the host's next move.");
            return (-1, -1);
        }
    }

    /// <summary>Host: the quest rows in the quests tab's order, each with its benchwarmer's id; why they're left out, or "".</summary>
    private static string QuestRows(UnlockableMenu menu, List<UnlockData> rows, HostTable table, List<QuestRowState> quests)
    {
        List<QuestConfig> configs = QuestConfigs(menu.m_BenchConfig);
        if (configs.Count != rows.Count)
        {
            return $"; the quest rows are left out (the tab has {rows.Count}, the benchwarmers' configs {configs.Count})";
        }

        for (int i = 0; i < rows.Count && i < UnlockStatesData.MaxQuests; i++)
        {
            UnlockData row = rows[i];
            table.Keys[row.Pointer] = UnlockStatesData.QuestKey(i);
            quests.Add(new QuestRowState(configs[i].Id, row.IsUnlocked && !row.IsSecret, row.IsSecret, Count(row.UnlockProgress), Count(row.UnlockTotal)));
        }

        return string.Empty;
    }

    // ---- Friend ----

    /// <summary>
    /// Friend: the plan for the host's states in <paramref name="attachment"/> (armed with <see cref="Arm"/> just before
    /// the menu is opened), or null with why.
    /// </summary>
    public static UnlockPlan? Plan(MenuAttachment? attachment, out string problem)
    {
        problem = string.Empty;
        if (attachment == null || !attachment.TryGetUnlockStates(out UnlockStatesData? states) || states == null)
        {
            problem = "the host's unlock states aren't here";
            return null;
        }

        return new UnlockPlan(states);
    }

    /// <summary>Friend: arms <paramref name="plan"/>: the next unlockables menu this PC's game converts rows for takes the host's states.</summary>
    public static void Arm(UnlockPlan plan)
    {
        _armed = plan;
        _armedAt = FrameClock.Seconds;
    }

    /// <summary>Friend: disarms <paramref name="plan"/> (or whatever is armed, for null): its view closed or gave up opening.</summary>
    public static void Disarm(UnlockPlan? plan = null)
    {
        if (plan == null || ReferenceEquals(_armed, plan))
        {
            _armed = null;
        }
    }

    /// <summary>Friend: one row this PC's menu just converted: the host's three values in place of this profile's, by id and category.</summary>
    public static void FriendConverted(UnlockableMenu menu, IUnlockData unlock, UnlockData row)
    {
        UnlockPlan? plan = Current(menu);
        if (plan == null)
        {
            return;
        }

        string? key = unlock.RewardID;
        if (key == null || !plan.RowByKey.TryGetValue((key, (int)row.Category), out int index))
        {
            plan.NotSent++;
            return;
        }

        UnlockRowState host = plan.States.Rows[index];
        if (row.IsUnlocked != host.Unlocked || row.UnlockProgress != host.Progress || row.UnlockTotal != host.Total)
        {
            plan.Differ++;
        }

        row.IsUnlocked = host.Unlocked;
        row.UnlockProgress = host.Progress;
        row.UnlockTotal = host.Total;
        plan.Applied++;
        plan.Rows[UnlockStatesData.RowKey(index)] = row;
    }

    /// <summary>
    /// Friend: this PC's menu is about to make its tabs (<c>InitTabs</c>'s prefix; the tabs count their completed rows there):
    /// each quest row is made the host's, and the tabs are named by what they list. The plan is done then (logged).
    /// </summary>
    public static void FriendTabs(UnlockableMenu menu, Il2CppList.List<TabData>? tabs)
    {
        UnlockPlan? plan = Current(menu);
        if (plan == null)
        {
            return;
        }

        _armed = null;
        int index = 0;
        foreach (TabData tab in Il2CppLists.Items<TabData>(tabs))
        {
            List<UnlockData> rows = Il2CppLists.Items<UnlockData>(tab?.Unlocks).ToList();
            int code = UnlockStatesData.TabCode(rows.Select(r => (int)r.Category));
            if (code >= 0 && !plan.Tabs.ContainsKey(code))
            {
                plan.Tabs[code] = index;
            }

            if (code == UnlockStatesData.QuestCategory)
            {
                FriendQuests(menu, rows, plan);
            }

            index++;
        }

        plan.TabCount = index;
        plan.Done = true;
        List<int> theirs = plan.States.TabCodes.Where(c => !plan.Tabs.ContainsKey(c)).ToList();
        string tabsNote = theirs.Count > 0 ? $"; the host's tab(s) {string.Join(", ", theirs)} aren't here (not followed)" : string.Empty;
        ModLog.Info($"Menus: UnlockableMenu: the host's unlock states for {plan.Applied} row(s) are in this PC's menu ({plan.Differ} differ from this profile's; " +
            $"{plan.NotSent} not sent by the host and left as this PC's own); {plan.QuestsApplied} quest row(s) ({plan.MadeSecret} made secret, " +
            $"{plan.MadeOpen} opened with their texts made here){(plan.QuestProblem.Length > 0 ? $" ({plan.QuestProblem})" : string.Empty)}; {index} tab(s){tabsNote}.");
    }

    /// <summary>
    /// Friend: opens the tab with code <paramref name="code"/> as the host's (the game's own tab select). True when it changed
    /// tab; <paramref name="problem"/> says why it couldn't ("" otherwise).
    /// </summary>
    public static bool FollowTab(UnlockableMenu menu, UnlockPlan plan, int code, out string problem)
    {
        problem = string.Empty;
        Il2CppList.List<TabData>? tabs = menu.m_TabsData;
        if (!plan.Tabs.TryGetValue(code, out int index) || tabs == null || index >= tabs.Count)
        {
            problem = $"the host's unlockables tab {code} (no tab here lists the same)";
            return false;
        }

        if (menu.m_CurTabIndex == index)
        {
            return false;
        }

        menu.SelectTab(tabs[index]);
        return true;
    }

    /// <summary>
    /// Friend: the navigable of the row the host highlighted (<paramref name="key"/>), in this PC's open tab: its list moved
    /// to it when it isn't among the rows made; checked to be that unlock's (rows are recycled cells). Null, with why, when
    /// it isn't in the open tab or can't be found.
    /// </summary>
    public static AbstractNavigable? RowNavigable(UnlockableMenu menu, UnlockPlan plan, int key, out string problem)
    {
        problem = string.Empty;
        if (!plan.Rows.TryGetValue(key, out UnlockData? row) || row == null)
        {
            problem = $"the host's unlockables row {key} (no row here took its state)";
            return null;
        }

        Il2CppList.List<TabData>? tabs = menu.m_TabsData;
        int tab = menu.m_CurTabIndex;
        Il2CppList.List<UnlockData>? rows = tabs != null && tab >= 0 && tab < tabs.Count ? tabs[tab]?.Unlocks : null;
        int place = -1;
        for (int i = 0; rows != null && i < rows.Count && place < 0; i++)
        {
            if (rows[i]?.Pointer == row.Pointer)
            {
                place = i;
            }
        }

        UnlockableScrollList? list = menu.m_List;
        if (place < 0 || list == null)
        {
            // Not in the tab open here: for a moment while the tabs change, or for good where this PC's tab lists it
            // elsewhere. Counted (logged when the view closes), not a miss.
            plan.NotInTab++;
            return null;
        }

        BaseCell? cell = list.GetCellByGlobalIndex(place) ?? list.SelectIndex(place);
        UnlockableRow? cellRow = cell?.TryCast<UnlockableRow>();
        return cellRow != null && cellRow._Data_k__BackingField?.Pointer == row.Pointer ? cellRow._Navigable_k__BackingField : null;
    }

    /// <summary>
    /// The benchwarmers whose quests make the quests tab, in its order: the game's configs (<c>BenchConfig.GetAllConfigs</c>)
    /// with their original and reward there and a quest talent among the original's (<c>Find(p =&gt; p is IBenchQuestTalent)</c>),
    /// as <c>UnlockableMenu.&lt;InitQuestsTab&gt;d__40</c> picks them.
    /// </summary>
    private static List<QuestConfig> QuestConfigs(BenchConfig? bench)
    {
        var configs = new List<QuestConfig>();
        if (bench == null)
        {
            return configs;
        }

        foreach (BenchConfig.ConfigData config in Il2CppLists.Items<BenchConfig.ConfigData>(bench.GetAllConfigs()))
        {
            Il2CppData.ForwardData? original = config?.original;
            if (config == null || original == null || config.reward == null)
            {
                continue;
            }

            Talent? quest = Il2CppLists.Items<Talent>(original.powerups).FirstOrDefault(t => t != null && t.TryCast<IBenchQuestTalent>() != null);
            if (quest != null)
            {
                configs.Add(new QuestConfig(original.id ?? string.Empty, config, quest));
            }
        }

        return configs;
    }

    /// <summary>
    /// Friend: each quest row as the host's (by benchwarmer): the host's progress, total and completion; the game's secret
    /// row where the host's is secret; the game's open row (its texts made here) where only the host's is open.
    /// </summary>
    private static void FriendQuests(UnlockableMenu menu, List<UnlockData> rows, UnlockPlan plan)
    {
        List<QuestConfig> configs = QuestConfigs(menu.m_BenchConfig);
        if (configs.Count != rows.Count)
        {
            plan.QuestProblem = $"the quests tab has {rows.Count} row(s), the benchwarmers' configs {configs.Count}: left as this PC's own";
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            UnlockData row = rows[i];
            if (!plan.QuestById.TryGetValue(configs[i].Id, out int index))
            {
                continue; // not sent: this PC's own row
            }

            QuestRowState host = plan.States.Quests[index];
            if (host.Secret)
            {
                if (!row.IsSecret)
                {
                    MakeSecret(row);
                    plan.MadeSecret++;
                }
            }
            else
            {
                if (row.IsSecret)
                {
                    string problem = MakeOpen(menu, row, configs[i]);
                    if (problem.Length > 0)
                    {
                        plan.QuestProblem = $"a quest row couldn't be opened here ({problem}): it stays secret";
                        continue;
                    }

                    plan.MadeOpen++;
                }

                row.IsUnlocked = host.Completed;
                row.UnlockProgress = host.Progress;
                row.UnlockTotal = host.Total;
            }

            plan.QuestsApplied++;
            plan.Rows[UnlockStatesData.QuestKey(index)] = row;
        }
    }

    /// <summary>The game's secret quest row (a benchwarmer not unlocked): "???" texts, a bar of one, no details.</summary>
    private static void MakeSecret(UnlockData row)
    {
        row.Title = Secret;
        row.Description = Secret;
        row.UnlockTitle = Secret;
        row.DetailedDescription = Secret;
        row.ConditionDetails = null!;
        row.UnlockProgress = 0;
        row.UnlockTotal = 1;
        row.IsUnlocked = false;
        row.IsSecret = true;
    }

    /// <summary>
    /// The game's open quest row's texts for a benchwarmer, made here in this PC's language with the getters the game's own
    /// quests tab uses (the benchwarmer's names, its quest talent's description and stat bonus description, the bench
    /// config's completed description, its reward's name). "" when done, else why.
    /// </summary>
    private static string MakeOpen(UnlockableMenu menu, UnlockData row, QuestConfig quest)
    {
        try
        {
            Il2CppData.ForwardData original = quest.Config.original;
            string description = UnlockableMenu.StripKeywordTags(quest.Talent.localizedDescription);
            string bonus = QuestColorOpen + UnlockableMenu.StripKeywordTags(quest.Talent.localizedStatBonusDescription) + QuestColorClose;
            string completed = menu.m_BenchConfig?.GetCompletedNotificationDescription(quest.Config) ?? string.Empty;
            row.Title = original.firstName + " " + original.lastName;
            row.Description = description;
            row.UnlockTitle = quest.Config.reward?.localizedPowerupName ?? string.Empty;
            row.DetailedDescription = completed + Break + bonus;
            row.ConditionDetails = description + Break + bonus + Break + completed;
            row.IsSecret = false;
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"{ModLog.SafeErrorText(ex)}";
        }
    }

    /// <summary>The armed plan, when it isn't stale; with <paramref name="menu"/>, only when it's that menu's (the first one it saw).</summary>
    private static UnlockPlan? Current(UnlockableMenu? menu = null)
    {
        if (_armed != null && FrameClock.Seconds - _armedAt > ArmedSeconds)
        {
            ModLog.Warn("Menus: an UnlockableMenu plan was armed but its menu never converted its rows here; dropped.");
            _armed = null;
        }

        if (_armed == null || menu == null)
        {
            return _armed;
        }

        if (_armed.Menu == IntPtr.Zero)
        {
            _armed.Menu = menu.Pointer;
        }

        return _armed.Menu == menu.Pointer ? _armed : null;
    }

    /// <summary>Makes room for one more host table: the oldest go beyond <see cref="MaxHostMenus"/> (a menu shows a few at most).</summary>
    private static void Forget()
    {
        while (HostTables.Count >= MaxHostMenus)
        {
            HostTables.Remove(HostTables.Keys.First());
        }
    }

    private static int Count(int value) => Math.Clamp(value, 0, UnlockStatesData.MaxCount);

    private static string Skipped(HostTable table) => table.Skipped > 0 ? $"; {table.Skipped} row(s) left out (no id, a repeated one, or past the cap)" : string.Empty;

    private static void Warn(string key, string message)
    {
        if (Logged.Add(key) || _errors++ < MaxErrors)
        {
            ModLog.Warn(message);
        }
    }

    /// <summary>A benchwarmer of the quests tab: its id, its config, its quest talent.</summary>
    private sealed record QuestConfig(string Id, BenchConfig.ConfigData Config, Talent Talent);

    /// <summary>Host: one unlockables menu's rows as it converted them, and the cursor keys of its rows.</summary>
    private sealed class HostTable
    {
        public List<UnlockRowState> Rows { get; } = new();

        public HashSet<(string, int)> Seen { get; } = new();

        /// <summary>Each row's cursor key, by its native object.</summary>
        public Dictionary<IntPtr, int> Keys { get; } = new();

        /// <summary>Each tab's code, by the menu's tab index (-1: an empty tab).</summary>
        public List<int> TabCodes { get; } = new();

        public int Skipped { get; set; }

        /// <summary>Its tabs are made: the next row converted is a new opening.</summary>
        public bool Complete { get; set; }
    }
}

/// <summary>
/// A friend's plan for the host's unlockables (<see cref="UnlockablesView"/>): the host's states, looked up by id, and what
/// this PC's menu made from them (its rows by the host's cursor keys, its tabs by code), for following the host's cursor.
/// </summary>
internal sealed class UnlockPlan
{
    /// <summary>A plan for <paramref name="states"/>.</summary>
    public UnlockPlan(UnlockStatesData states)
    {
        States = states;
        for (int i = 0; i < states.Rows.Count; i++)
        {
            RowByKey[(states.Rows[i].Key, states.Rows[i].Category)] = i;
        }

        for (int i = 0; i < states.Quests.Count; i++)
        {
            QuestById[states.Quests[i].Benchwarmer] = i;
        }
    }

    /// <summary>The host's states.</summary>
    public UnlockStatesData States { get; }

    /// <summary>The host's row index by id and category.</summary>
    public Dictionary<(string, int), int> RowByKey { get; } = new();

    /// <summary>The host's quest row index by benchwarmer.</summary>
    public Dictionary<string, int> QuestById { get; } = new(StringComparer.Ordinal);

    /// <summary>The menu its rows went into (zero until the first).</summary>
    public IntPtr Menu { get; set; }

    /// <summary>Its menu made its tabs: its rows and tabs are known.</summary>
    public bool Done { get; set; }

    /// <summary>This PC's rows by the host's cursor keys.</summary>
    public Dictionary<int, UnlockData> Rows { get; } = new();

    /// <summary>This PC's tab index by tab code.</summary>
    public Dictionary<int, int> Tabs { get; } = new();

    /// <summary>This PC's tab count.</summary>
    public int TabCount { get; set; }

    /// <summary>Rows given the host's states, rows whose state differed here, rows the host didn't send.</summary>
    public int Applied { get; set; }

    /// <summary>Rows whose state here differed from the host's.</summary>
    public int Differ { get; set; }

    /// <summary>Rows the host didn't send (left as this PC's own).</summary>
    public int NotSent { get; set; }

    /// <summary>Quest rows made the host's.</summary>
    public int QuestsApplied { get; set; }

    /// <summary>Quest rows made secret here (the host's benchwarmer isn't unlocked).</summary>
    public int MadeSecret { get; set; }

    /// <summary>Quest rows opened here (only the host's benchwarmer is unlocked), their texts made here.</summary>
    public int MadeOpen { get; set; }

    /// <summary>Why the quest rows (or one of them) aren't the host's, or "".</summary>
    public string QuestProblem { get; set; } = string.Empty;

    /// <summary>Times the host's highlighted row wasn't in the tab open here (while the tabs changed, or a tab listed differently).</summary>
    public int NotInTab { get; set; }
}
#endif
