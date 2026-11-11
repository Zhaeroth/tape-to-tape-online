#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppData;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppState;
using Il2CppState.RunHistory;
using Il2CppStats.PersistentStats;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Campaign.Progress;
using T2TOnline.Plugin.Diagnostics;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The run-end menu (<c>RunEndMenu</c>, menu "RunEnd") as a real view (D43).
/// It opens three ways on the host, and each is shown its own way on a friend's PC:
/// <list type="bullet">
/// <item>The run's end: the host builds the run's history entry with the game's own builder
/// (<c>RunHistorySerializer.CreateEntry</c>, which writes nothing), with the title and the per-match details the host's
/// screen shows, and sends it as a <see cref="MenuAttachmentKind.RunSummary"/> attachment. A friend's game opens the menu
/// in its own history mode with that one entry: everything on it comes from the entry (no save, no campaign state, no
/// stats of this PC), so it's the host's screen whatever this PC's copy of the run says. Its history date is hidden (the
/// host's screen has none).</item>
/// <item>The map menu's "Run stats" (during the run): a friend's game opens it live, once it holds the host's newest run
/// (its campaign state is then the host's run); the four team numbers come from each PC's own stats, so the host's four
/// are written over this PC's.</item>
/// <item>The host looking at its run history at the title: a friend's game opens the menu in its history mode on the
/// host's page, and follows each page the host moves to (<see cref="RunHistoryPages"/>, from the host's
/// <see cref="MenuAttachmentKind.HistoryPage"/> attachments); the date shows the host's local time.</item>
/// </list>
/// The host's sort (<c>RefreshOrder</c>) is mirrored as two state values, the selection by path as for every real view.
/// </summary>
internal static class RunEndView
{
    /// <summary>Value: the map menu's "Run stats" (flag).</summary>
    public const string DuringRun = "duringRun";

    /// <summary>Value: the host is looking at its own run history (flag).</summary>
    public const string History = "history";

    /// <summary>Value: the host's game shows made-up data (a developer option; flag).</summary>
    public const string RandomData = "randomData";

    /// <summary>Value: the menu's least number of skater rows (whole).</summary>
    public const string MinSkaters = "minSkaters";

    /// <summary>Value: a run summary attachment goes with this menu (flag; only at the run's end).</summary>
    public const string WithSummary = "summary";

    private const int StatChars = 24;

    // The four team numbers the live screen shows, in the order of its four texts.
    private static readonly (string Key, StatType Type)[] TeamStats =
    {
        ("teamStat.sticksBroken", StatType.SticksBroken),
        ("teamStat.injuriesInflicted", StatType.InjuriesInflicted),
        ("teamStat.injuriesReceived", StatType.InjuriesReceived),
        ("teamStat.critShots", StatType.TotalCritShots),
    };

    /// <summary>How a host's run-end menu is shown on a friend's PC.</summary>
    public enum Variant
    {
        /// <summary>None of the below: shown as text (made-up data, or the host couldn't make its run summary).</summary>
        Text,

        /// <summary>The run's end, from the host's run summary, in the menu's history mode.</summary>
        Summary,

        /// <summary>The map menu's "Run stats", live, from the host's newest run.</summary>
        DuringRun,

        /// <summary>The host's run history at the title, from its pages (<see cref="RunHistoryPages"/>).</summary>
        History,
    }

    /// <summary>Which way the host's menu with <paramref name="data"/> is shown.</summary>
    public static Variant Of(MenuData data) =>
        data.Flag(History) ? Variant.History
        : data.Flag(RandomData) ? Variant.Text
        : data.Flag(DuringRun) ? Variant.DuringRun
        : data.Flag(WithSummary) ? Variant.Summary
        : Variant.Text;

    /// <summary>
    /// True when the host's menu with <paramref name="data"/> shows the run that just ended (not the map menu's "Run stats",
    /// not the history at the title, not made-up data), whether or not its run summary could be made.
    /// </summary>
    public static bool AtRunsEnd(MenuData data) => !data.Flag(History) && !data.Flag(DuringRun) && !data.Flag(RandomData);

    // ---- Host ----

    /// <summary>Host: true when <paramref name="menu"/> shows the run that just ended (not the history, not during the run, not made up).</summary>
    public static bool IsRunsEnd(RunEndMenu menu) => !menu.m_IsHistoryMode && !menu.m_IsDuringRun && !menu.m_UseRandomData;

    /// <summary>
    /// Host: the menu's values for friends. <paramref name="summary"/>: a run summary goes with it. During the run, the four
    /// team numbers as the host's screen shows them.
    /// </summary>
    public static void Values(RunEndMenu menu, bool summary, List<KeyValuePair<string, NetValue>> values)
    {
        values.Add(new(DuringRun, NetValue.FromBool(menu.m_IsDuringRun)));
        values.Add(new(History, NetValue.FromBool(menu.m_IsHistoryMode)));
        values.Add(new(MinSkaters, NetValue.FromInt(Math.Clamp(menu.m_MinNumSkaters, 0, 99))));
        if (menu.m_UseRandomData)
        {
            values.Add(new(RandomData, NetValue.FromBool(true)));
        }

        if (summary)
        {
            values.Add(new(WithSummary, NetValue.FromBool(true)));
        }

        if (!menu.m_IsDuringRun || menu.m_IsHistoryMode || menu.m_UseRandomData)
        {
            return;
        }

        RunTeamStats? texts = menu.m_TeamStats;
        StatManager? stats = menu.m_StatsManager ?? Injected<StatManager>();
        if (texts == null || stats == null)
        {
            return;
        }

        // The game's own formatting of the four numbers, from the host's stats (each PC's "this run" counters are its own).
        foreach ((string key, StatType type) in TeamStats)
        {
            values.Add(new(key, NetValue.FromString(MenuGame.Plain(texts.GetStatOrZero(stats, type), StatChars))));
        }
    }

    /// <summary>
    /// Host: the run that just ended, as the game's own run history entry for it (<c>RunHistorySerializer.CreateEntry</c>,
    /// which reads the run and writes nothing), as plain data; or null with <paramref name="problem"/>. The entry gets the
    /// title the live screen shows (won or lost, conceded) and the run's matches as the live screen has them (the history
    /// file keeps each match without its score, map point, scorers and goalie; only read here, never changed).
    /// </summary>
    public static RunHistoryData? MakeSummary(RunEndMenu menu, out string problem)
    {
        problem = string.Empty;
        CampaignState? campaign = menu.m_CampaignState ?? MenuGame.Campaign();
        RunData? run = campaign?.runData;
        RunStatsState? stats = campaign?.runStatsState;
        StatManager? statManager = menu.m_StatsManager ?? Injected<StatManager>();
        if (run == null || stats == null || statManager == null || run.teamData == null)
        {
            problem = run == null ? "the game has no run" : stats == null ? "the run has no stats" : statManager == null ? "the game's stats aren't loaded" : "the run has no team";
            return null;
        }

        RunHistoryEntryModel? entry = RunHistorySerializer.CreateEntry(run, stats, !run.isRunFailed, statManager);
        if (entry == null)
        {
            problem = "the game made no entry";
            return null;
        }

        entry.isWin = !run.isRunFailed;
        entry.isConceded = run.isRunConceded;
        if (stats.runMatchScores != null)
        {
            entry.matchScores = stats.runMatchScores;
        }

        RunHistoryData data = RunHistoryMapping.ToData(entry);
        if (!data.IsWithinLimits())
        {
            problem = "the entry is over the run history entry's limits";
            return null;
        }

        return data;
    }

    // ---- Friend ----

    /// <summary>
    /// Friend: the data to open the host's run-end menu with, or null with <paramref name="problem"/>. The run's end: the
    /// menu's history mode with the host's run summary as its one entry; the map's "Run stats": live, as the host's map
    /// menu opens it; the title's run history: the menu's history mode on the host's page (<paramref name="attachment"/> is
    /// then the host's newest page).
    /// </summary>
    public static RunEndMenu.Metadata? Metadata(MenuData data, MenuAttachment? attachment, out string problem)
    {
        problem = string.Empty;
        int minSkaters = (int)Math.Clamp(data.Whole(MinSkaters), 0, 99);
        switch (Of(data))
        {
            case Variant.Summary:
                if (attachment == null || !attachment.TryGetRunSummary(out RunHistoryData? summary) || summary == null)
                {
                    problem = "the host's run summary isn't here";
                    return null;
                }

                if (summary.Team == null || summary.Team.Forwards.Count == 0)
                {
                    problem = "the host's run summary has no team";
                    return null;
                }

                RunHistoryEntryModel entry;
                try
                {
                    entry = RunHistoryMapping.ToGame(summary);
                }
                catch (Exception ex)
                {
                    problem = $"the host's run summary couldn't be made into the game's entry ({ModLog.SafeErrorText(ex)})";
                    return null;
                }

                // One run in the menu's history list: its previous and next buttons stay hidden.
                var runs = new Il2CppList.List<RunHistoryIndexEntry>();
                runs.Add(new RunHistoryIndexEntry { runID = summary.RunId, timestampUtc = summary.TimestampUtc, slot = -1 });
                return new RunEndMenu.Metadata
                {
                    FillWithRandomData = false,
                    IsDuringRun = false,
                    HistoryRuns = runs,
                    HistoryEntry = entry,
                    StartIndex = 0,
                    MinNumSkaters = minSkaters,
                    PlayLoadingTransition = false,
                };
            case Variant.History:
                HistoryPageData? page = RunHistoryPages.Page(attachment, out problem);
                return page == null ? null : RunHistoryPages.Metadata(page, minSkaters, out problem);
            case Variant.DuringRun:
                return new RunEndMenu.Metadata
                {
                    FillWithRandomData = false,
                    IsDuringRun = true,
                    MinNumSkaters = minSkaters,
                    PlayLoadingTransition = false,
                };
            default:
                problem = "this run-end screen isn't one this version shows for real";
                return null;
        }
    }

    /// <summary>
    /// Friend: what the open menu shows that isn't the host's is put right: the history date the run's end has here (the
    /// host's screen has none), and during the run the four team numbers (this PC's own stats) with the host's. Returns
    /// what it changed, for the log ("" for nothing).
    /// </summary>
    public static string Dress(RunEndMenu menu, MenuData data)
    {
        Variant variant = Of(data);
        if (variant == Variant.Summary)
        {
            UnityEngine.GameObject? stamp = menu.m_HistoryTimestamp;
            if (stamp != null && stamp.activeSelf)
            {
                stamp.SetActive(false);
                return "hid the history date";
            }

            return string.Empty;
        }

        RunTeamStats? texts = menu.m_TeamStats;
        if (variant != Variant.DuringRun || texts == null)
        {
            return string.Empty;
        }

        var changed = new List<string>();
        Write(texts.m_SticksBroken, data, TeamStats[0].Key, changed);
        Write(texts.m_InjuriesInflicted, data, TeamStats[1].Key, changed);
        Write(texts.m_InjuriesSuffered, data, TeamStats[2].Key, changed);
        Write(texts.m_CriticalShots, data, TeamStats[3].Key, changed);
        return changed.Count == 0 ? string.Empty : $"the host's team numbers written over this PC's ({string.Join(", ", changed)})";
    }

    /// <summary>
    /// Friend: true when <paramref name="sort"/> and <paramref name="order"/> are the game's, not what <paramref name="menu"/>
    /// shows, and its rows are up (its own opening makes them; a sort before that waits for the next check).
    /// </summary>
    public static bool SortDiffers(RunEndMenu menu, int sort, int order) =>
        Enum.IsDefined(typeof(RunEndMenuSort), sort) && sort != (int)RunEndMenuSort.MAX && Enum.IsDefined(typeof(RunEndMenuSortOrder), order) &&
        ((int)menu.m_CurSortType != sort || (int)menu.m_CurSortOrder != order) && menu.m_Skaters is { Count: > 0 };

    /// <summary>Friend: sorts the rows as the host's are (the menu's own re-sort, which re-orders the rows).</summary>
    public static void Sort(RunEndMenu menu, int sort, int order) => menu.RefreshOrder((RunEndMenuSort)sort, (RunEndMenuSortOrder)order);

    private static void Write(TMP_Text? text, MenuData data, string key, List<string> changed)
    {
        string host = data.Text(key);
        if (text == null || host.Length == 0)
        {
            return;
        }

        string mine = text.text ?? string.Empty;
        if (mine != host)
        {
            text.text = host;
            changed.Add($"{key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..]} {MenuGame.Plain(mine, StatChars)} -> {host}");
        }
    }

    /// <summary>A service from the game's injector (as the menu's own injection gets it), or null.</summary>
    private static T? Injected<T>()
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<T>());
        return found?.TryCast<T>();
    }
}
#endif
