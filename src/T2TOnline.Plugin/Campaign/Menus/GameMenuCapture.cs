#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppSkaters;
using Il2CppSkaters.Injuries;
using Il2CppState;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using Goaltender = Il2CppGoaltender.Goaltender;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of the real in-match menu: what the host's
/// <c>GameMenu</c> shows, read after its own <c>Show</c> has drawn it (at <c>T2TMenu.Show</c>), as a
/// <see cref="GameMenuModel"/> in its <see cref="MenuData"/>: its tabs and pause buttons from its own fields, both teams'
/// stats, every forward's stats by index, the forwards on the ice, the timeline's goals and injuries (each skater a
/// forward by index or a side's goalie, with the goalies' fingerprints), and two checks a friend compares after drawing its
/// copy (the stats texts, the injury list). Also the glossary's tab and entry. The readers of the drawn menu are shared
/// with the friend's side (<see cref="StatsTexts"/>, <see cref="InjuryDigest"/>, <see cref="GoaliePrints"/>).
/// </summary>
internal static class GameMenuCapture
{
    /// <summary>Value: the glossary's open tab (index).</summary>
    public const string GlossaryTab = "gl.tab";

    /// <summary>Value: the glossary entry shown (index among its entries ordered by id; absent for none).</summary>
    public const string GlossaryEntry = "gl.entry";

    /// <summary>Value: how many tabs the host's glossary has (a friend mirrors tabs only when its own count is the same).</summary>
    public const string GlossaryTabs = "gl.tabs";

    /// <summary>Value: a check of the glossary's entries by id (a friend mirrors entries only when its own is the same).</summary>
    public const string GlossaryEntries = "gl.entries";

    private const int MaxDigest = 240;

    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, (GameMenuInjuryIcon Icon, int Type, bool Hd)> Icons = new();
    private static IntPtr _iconsOf;
    private static GameManager? _game;
    private static string _lastProblem = string.Empty;
    private static readonly Dictionary<IntPtr, string> StartIds = new();
    private static IntPtr _startIdsOf;

    /// <summary>The running match's game manager, or null (looked up again once the one it had is gone).</summary>
    public static GameManager? Game()
    {
        if (_game == null)
        {
            GameManager? found = UnityEngine.Object.FindObjectOfType<GameManager>();
            _game = found != null ? found : null;
        }

        return _game;
    }

    /// <summary>
    /// Host: adds the menu's numbers to <paramref name="values"/> (nothing, logged once per problem, when they can't be
    /// read or don't fit; the menu still goes to friends, as text). Never throws: the menu's text must go out regardless.
    /// </summary>
    public static void Read(GameMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        string problem;
        try
        {
            GameMenuModel? model = Model(menu, out problem);
            if (model != null && model.TryWrite(values, out problem, out bool rowsLeftOut))
            {
                _lastProblem = string.Empty;
                if (rowsLeftOut)
                {
                    Once("injury-rows-left", "Menus: the in-match menu's injury list rows didn't fit its data (a long timeline); friends compare their own list with the host's instead (logged once).");
                }

                return;
            }
        }
        catch (Exception ex)
        {
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        if (problem != _lastProblem)
        {
            _lastProblem = problem;
            ModLog.Warn($"Menus: the in-match menu's numbers can't go to friends ({problem}); they see it as text.");
        }
    }

    /// <summary>Host: the glossary's tab, entry and the checks of both (see <see cref="GlossaryTab"/>). Never throws (logged once).</summary>
    public static void ReadGlossary(GlossaryMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        try
        {
            var read = new List<KeyValuePair<string, NetValue>>
            {
                new(GlossaryTab, NetValue.FromInt(Math.Clamp(menu.m_CurTabIndex, 0, 1023))),
                new(GlossaryTabs, NetValue.FromInt(Math.Min(menu.m_Tabs?.Count ?? 0, 1023))),
                new(GlossaryEntries, NetValue.FromInt(EntriesCheck(menu))),
            };
            int entry = EntryIndex(menu, menu.m_ShownEntry);
            if (entry >= 0)
            {
                read.Add(new(GlossaryEntry, NetValue.FromInt(entry)));
            }

            values.AddRange(read);
        }
        catch (Exception ex)
        {
            Once("glossary", $"Menus: the glossary's tab and entry can't go to friends ({ex.GetType().Name}); they see its first tab.");
        }

        try
        {
            // Where its highlighted row sits in its list (v1): a friend's list scrolls to match.
            GlossaryScrolls.Read(menu, values);
        }
        catch (Exception ex)
        {
            Once("glossary-row", $"Menus: where the glossary's row sits can't go to friends ({ex.GetType().Name}); their list sits where its own selection puts it.");
        }
    }

    /// <summary>The glossary's entries ordered by id (ordinal): the same order on every PC and in every language.</summary>
    public static List<GlossaryEntrySO> Entries(GlossaryMenu menu)
    {
        var entries = new List<GlossaryEntrySO>();
        Il2CppSystem.Collections.Generic.List<GlossaryEntrySO>? all = menu._allEntries;
        for (int i = 0; all != null && i < all.Count; i++)
        {
            if (all[i] != null)
            {
                entries.Add(all[i]);
            }
        }

        return entries.OrderBy(e => e.Id ?? string.Empty, StringComparer.Ordinal).ToList();
    }

    /// <summary>A check of the glossary's entry ids, in id order.</summary>
    public static uint EntriesCheck(GlossaryMenu menu) => GameMenuModel.TextCheck(Entries(menu).Select(e => e.Id));

    /// <summary>The index of <paramref name="entry"/> among the glossary's entries ordered by id, or -1.</summary>
    public static int EntryIndex(GlossaryMenu menu, GlossaryEntrySO? entry)
    {
        if (entry == null)
        {
            return -1;
        }

        int index = Entries(menu).FindIndex(e => e.Pointer == entry.Pointer);
        return index <= 1023 ? index : -1;
    }

    /// <summary>
    /// The drawn menu's stats texts, in a fixed order, for <see cref="GameMenuModel.TextCheck"/>: each team block's score
    /// and stats, and each skater row's shown flag, jersey number and stats; then how many timeline rows it drew. Names
    /// are left out (a name can differ by language without the numbers differing).
    /// </summary>
    public static List<string?> StatsTexts(GameMenu menu)
    {
        var texts = new List<string?>();
        InGameStatsUI? stats = menu.m_StatsUI;
        foreach (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<InGameTeamStatsUI>? side in new[] { stats?.m_HomeTeamStats, stats?.m_VisitorTeamStats })
        {
            for (int i = 0; side != null && i < side.Length; i++)
            {
                InGameTeamStatsUI? team = side[i];
                if (team == null)
                {
                    texts.Add(null);
                    continue;
                }

                foreach (TMP_Text? text in new[] { team.m_TeamScore, team.m_Shots, team.m_Passes, team.m_PassPercentage, team.m_TimeOnAttack, team.m_Hits, team.m_Interceptions, team.m_BlockedShots, team.m_FaceOffsWon })
                {
                    texts.Add(Text(text));
                }

                foreach (InGameSkaterStatsUI? row in new[] { team.m_StatsLW, team.m_StatsLD, team.m_StatsC, team.m_StatsRD, team.m_StatsRW, team.m_StatsG })
                {
                    if (row == null)
                    {
                        texts.Add(null);
                        continue;
                    }

                    texts.Add(row.gameObject.activeSelf ? "1" : "0");
                    foreach (TMP_Text? text in new[] { row.m_Number, row.m_Goals, row.m_Assists, row.m_Shots, row.m_Passes, row.m_Hits, row.m_Interceptions, row.m_BlockedShots })
                    {
                        texts.Add(Text(text));
                    }
                }
            }
        }

        texts.Add((menu.m_TimelineUI?.m_Items?.Count ?? -1).ToString(CultureInfo.InvariantCulture));
        return texts;
    }

    /// <summary>
    /// The drawn menu's injury list, as a digest: each row's forward (its asset id's fingerprint) and healing times before and
    /// after. "-" when the menu has no list.
    /// </summary>
    public static string InjuryDigest(GameMenu menu)
    {
        Il2CppSystem.Collections.Generic.List<InjuryChangeData>? rows = menu.m_InjuryChangeList?.m_InjuryChangesData;
        if (rows == null)
        {
            return "-";
        }

        var digest = new StringBuilder(rows.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < rows.Count; i++)
        {
            InjuryChangeData? row = rows[i];
            string entry = row == null ? ",?" : string.Create(CultureInfo.InvariantCulture,
                $",{GameMenuModel.IdPrint(row.Skater?.OriginalId):x}:{Math.Clamp(row.PreviousHealingTime, -99, 999)}:{Math.Clamp(row.NewHealingTime, -99, 999)}");
            if (digest.Length + entry.Length > MaxDigest)
            {
                digest.Append(",+");
                break;
            }

            digest.Append(entry);
        }

        return digest.ToString();
    }

    /// <summary>The menu's model, or null with why.</summary>
    private static GameMenuModel? Model(GameMenu menu, out string problem)
    {
        GameManager? game = Game();
        Team? home = game?._homeTeam;
        Team? visitor = game?._visitorTeam;
        if (home == null || visitor == null)
        {
            problem = "no match is running";
            return null;
        }

        int tabs = 0;
        int tab = 0;
        Il2CppSystem.Collections.Generic.List<GameMenu.TabData>? tabList = menu.m_CurrentTabs;
        for (int i = 0; tabList != null && i < tabList.Count; i++)
        {
            GameMenu.TabData? data = tabList[i];
            if (data == null)
            {
                continue;
            }

            int type = (int)data.TabType & GameMenuModel.TabFlags;
            tabs |= type;
            if (tab == 0 && type is 1 or 2 or 4 or 8 && data.m_RootObject != null && data.m_RootObject.activeSelf)
            {
                tab = type;
            }
        }

        int buttons = 0;
        Il2CppSystem.Collections.Generic.List<GameMenu.PauseButtonData>? buttonList = menu.m_CurrentPauseButtons;
        for (int i = 0; buttonList != null && i < buttonList.Count; i++)
        {
            buttons |= buttonList[i] == null ? 0 : (int)buttonList[i].ButtonType & GameMenuModel.ButtonFlags;
        }

        var roster = new Roster(Forwards(home), Forwards(visitor), Goalie(home), Goalie(visitor));
        var events = new List<GameMenuEvent>();
        int left = 0;
        int assistsLeft = 0;
        Il2CppSystem.Collections.Generic.List<GameTimeline.EventData>? timeline = menu.m_TimelineUI?.m_GameTimeline?._events;
        for (int i = 0; timeline != null && i < timeline.Count && events.Count < GameMenuModel.MaxEvents; i++)
        {
            GameMenuEvent? read = Event(timeline[i], roster, ref assistsLeft);
            if (read == null)
            {
                left++;
            }
            else
            {
                events.Add(read);
            }
        }

        if (left > 0)
        {
            Once($"timeline:{left}", $"Menus: {left} timeline event(s) of the in-match menu name no skater of either team; friends' timelines leave them out.");
        }

        if (assistsLeft > 0)
        {
            Once($"assists:{assistsLeft}", $"Menus: {assistsLeft} assist(s) in the in-match menu's timeline name no skater of either team; friends' timelines show those goals with the assists left out.");
        }

        problem = string.Empty;
        List<GameMenuInjuryRow>? rows = InjuryRows(menu, out bool shown);
        return new GameMenuModel(tabs, tab, buttons, menu.m_ShowTycoonSponsors, Side(home, roster.Home, roster.HomeGoalie), Side(visitor, roster.Visitor, roster.VisitorGoalie),
            events, InjuryDigest(menu), GameMenuModel.TextCheck(StatsTexts(menu)), rows, shown);
    }

    /// <summary>
    /// The drawn menu's injury list as rows (v1, format 3): each row's forward (its asset id's print), side, icon (found
    /// among the game's own icons of each injury type) and healing times, in the list's order; at most
    /// <see cref="GameMenuModel.MaxInjuryRows"/> (more are left out, logged once). Null when the menu has no list.
    /// </summary>
    public static List<GameMenuInjuryRow>? InjuryRows(GameMenu menu, out bool shown)
    {
        shown = false;
        InjuryChangeList? list = menu.m_InjuryChangeList;
        Il2CppSystem.Collections.Generic.List<InjuryChangeData>? data = list?.m_InjuryChangesData;
        if (list == null || data == null)
        {
            return null;
        }

        shown = list.gameObject.activeSelf;
        var rows = new List<GameMenuInjuryRow>();
        int unnamed = 0;
        for (int i = 0; i < data.Count && rows.Count < GameMenuModel.MaxInjuryRows; i++)
        {
            InjuryChangeData? row = data[i];
            if (row == null)
            {
                continue;
            }

            UnityEngine.Sprite? sprite = row.InjuryIcon;
            (GameMenuInjuryIcon icon, int type, bool hd) = Icon(list.m_InjurySystem, sprite);
            unnamed += icon == GameMenuInjuryIcon.None && sprite != null ? 1 : 0;
            rows.Add(new GameMenuInjuryRow(GameMenuModel.IdPrint(row.Skater?.OriginalId), row.IsHomeTeam, icon, type, hd,
                Math.Clamp(row.PreviousHealingTime, GameMenuModel.MinHealing, GameMenuModel.MaxHealing), Math.Clamp(row.NewHealingTime, GameMenuModel.MinHealing, GameMenuModel.MaxHealing)));
        }

        if (data.Count > GameMenuModel.MaxInjuryRows)
        {
            Once("injury-rows-many", $"Menus: the in-match menu's injury list has {data.Count} rows; friends get the first {GameMenuModel.MaxInjuryRows} (logged once).");
        }

        if (unnamed > 0)
        {
            Once("injury-icons", $"Menus: {unnamed} injury list icon(s) are none of the injury system's own; friends show those rows without an icon (logged once).");
        }

        return rows;
    }

    /// <summary>
    /// Which of the injury system's own icons <paramref name="sprite"/> is (its kind, injury type and definition); none for
    /// another. The icons are looked up once per injury system (16 at most).
    /// </summary>
    private static (GameMenuInjuryIcon Icon, int Type, bool Hd) Icon(InjurySystem? system, UnityEngine.Sprite? sprite)
    {
        if (system == null || sprite == null)
        {
            return (GameMenuInjuryIcon.None, 0, false);
        }

        if (_iconsOf != system.Pointer)
        {
            Icons.Clear();
            _iconsOf = system.Pointer;
            foreach (InjuryType type in Enum.GetValues<InjuryType>())
            {
                foreach (bool hd in new[] { true, false })
                {
                    // The game's list asks for the high-definition ones (verified in the game's code: InjuryChangeList.Refresh): those first.
                    AddIcon(system.GetInjuryIcon(type, hd), GameMenuInjuryIcon.Injury, type, hd);
                    AddIcon(system.GetRecoveryIcon(type, hd), GameMenuInjuryIcon.Recovery, type, hd);
                }
            }
        }

        return Icons.TryGetValue(sprite.Pointer, out (GameMenuInjuryIcon, int, bool) found) ? found : (GameMenuInjuryIcon.None, 0, false);

        static void AddIcon(UnityEngine.Sprite? icon, GameMenuInjuryIcon kind, InjuryType type, bool hd)
        {
            if (icon != null && (int)type is >= 0 and <= GameMenuModel.MaxInjuryType)
            {
                Icons.TryAdd(icon.Pointer, (kind, (int)type, hd));
            }
        }
    }

    /// <summary>A side's team stats, forwards (by asset id), line, forwards' stats and its goalie's fingerprints.</summary>
    private static GameMenuSide Side(Team team, List<Forward> forwards, Goaltender? goalie)
    {
        TeamStats stats = team.teamStats;
        int[] counts = { stats.shotsOnNet, stats.passesAttempt, stats.passesSuccess, stats.knockoutsPerformed, stats.interception, stats.blockedShots, stats.faceoffWon };
        var ids = forwards.Select(f => GameMenuModel.IdPrint(MatchAssetId(f))).ToList();
        List<Forward> turned = forwards.Where(f => MatchAssetId(f) != AssetId(f)).ToList();
        if (turned.Count > 0)
        {
            // By asset id as the match began, never by object name: the game names a skater's object after the skater (D127).
            Once($"turned:{_startIdsOf}:{team.Pointer}", $"Menus: {turned.Count} of a team's forwards play as another skater since the match began here " +
                $"({string.Join(", ", turned.Take(4).Select(SkaterLogText.Of))}: the game turned them); " +
                "friends' in-match menus name them as they began.");
        }

        var line = new List<int>();
        Il2CppSystem.Collections.Generic.List<Forward>? playing = team._PlayingForwards_k__BackingField;
        int missing = 0;
        for (int i = 0; playing != null && i < playing.Count && line.Count < GameMenuModel.MaxLine; i++)
        {
            int index = playing[i] == null ? -1 : forwards.FindIndex(f => f.Pointer == playing[i].Pointer);
            if (index >= 0 && !line.Contains(index))
            {
                line.Add(index);
            }
            else
            {
                missing++;
            }
        }

        if (missing > 0)
        {
            Once($"line:{missing}", $"Menus: {missing} forward(s) on the ice aren't in their team's forwards; friends' in-match menus leave their rows out.");
        }

        var skaters = new List<GameMenuSkater>();
        for (int i = 0; i < forwards.Count; i++)
        {
            SkaterStats? s = forwards[i].gameStats;
            if (s != null)
            {
                skaters.Add(new GameMenuSkater(i, new[] { Count(s.goals), Count(s.assists), Count(s.shotsOnNet), Count(s.passesAttempt), Count(s.knockoutsPerformed), Count(s.interception), Count(s.blockedShots) }));
            }
        }

        return new GameMenuSide(counts.Select(Count).ToArray(), Math.Clamp(float.IsFinite(stats.timeOnAttack) ? stats.timeOnAttack : 0, 0, GameMenuModel.MaxSeconds), ids, line, skaters,
            goalie == null ? null : GoaliePrints(goalie));
    }

    /// <summary>A side's goalie (<c>Team.goaltender</c>), or null when it has none (Unity's null too).</summary>
    public static Goaltender? Goalie(Team team)
    {
        Goaltender? goalie = team.goaltender;
        return goalie != null ? goalie : null;
    }

    /// <summary>A goalie's fingerprints (<see cref="GameMenuModel.GoaliePrints"/>): of its data's asset id and its own id.</summary>
    public static IReadOnlyList<ushort> GoaliePrints(Goaltender goalie)
    {
        Il2CppData.GoaltenderData? data = goalie._goaltenderData_k__BackingField;
        return GameMenuModel.GoaliePrints(data?.OriginalId, data?.id);
    }

    /// <summary>A side's forwards (the game's <c>Team.AllForwards</c>), up to the most a model carries.</summary>
    public static List<Forward> Forwards(Team team)
    {
        var forwards = new List<Forward>();
        Il2CppSystem.Collections.Generic.List<Forward>? all = team.AllForwards;
        for (int i = 0; all != null && i < all.Count && forwards.Count < GameMenuModel.MaxForwards; i++)
        {
            if (all[i] == null)
            {
                break; // indices past a gap can't be named the same way on a friend's PC
            }

            forwards.Add(all[i]);
        }

        return forwards;
    }

    /// <summary>
    /// A forward's asset id (<c>SkaterData.OriginalId</c>): what names it on both PCs. Its <c>id</c> doesn't for every
    /// forward (a friend's copy of an opponent or an exhibition team's forward gets a fresh one; Match/MatchSetup).
    /// </summary>
    public static string AssetId(Forward forward) => forward._forwardData?.OriginalId ?? string.Empty;

    /// <summary>
    /// Both sides, as a match's stream starts (the host's HUD recorder, a friend's playback): every forward's asset id as
    /// the match began (<see cref="MatchAssetId"/>). Never throws.
    /// </summary>
    public static void RememberForwards()
    {
        try
        {
            StartIds.Clear();
            _startIdsOf = IntPtr.Zero;
            GameManager? game = Game();
            if (game == null)
            {
                return;
            }

            foreach (Team? team in new[] { game._homeTeam, game._visitorTeam })
            {
                if (team == null)
                {
                    continue;
                }

                foreach (Forward forward in Forwards(team))
                {
                    StartIds[forward.Pointer] = AssetId(forward);
                }
            }

            _startIdsOf = game.Pointer;
        }
        catch (Exception ex)
        {
            Once("start-ids", $"Menus: this match's forwards couldn't be noted as it began ({ex.GetType().Name}); the in-match menu matches them by their skater data as it is then.");
        }
    }

    /// <summary>
    /// The asset id that names a forward in the in-match menu: the one it had as the match's stream started
    /// (<see cref="RememberForwards"/>), else its own now. A forward the game turns into another skater during play takes
    /// that skater's data (<c>Forward.Initialize</c>: the cup cultists' evil twin, <c>TransformationCultistAI.TransformInElite</c>;
    /// a zap, <c>ZapComponent._Transform</c>), on the host only: named by its current data, the host's forward and this PC's
    /// would differ, and a friend's menu fell back to text.
    /// </summary>
    public static string MatchAssetId(Forward forward)
    {
        GameManager? game = _startIdsOf == IntPtr.Zero ? null : Game();
        return game != null && game.Pointer == _startIdsOf && StartIds.TryGetValue(forward.Pointer, out string? id) ? id : AssetId(forward);
    }

    /// <summary>
    /// A timeline event, or null when it names no skater of either team (its scorer or the injured). An assist naming none
    /// is left out of its goal, counted in <paramref name="assistsLeft"/>.
    /// </summary>
    private static GameMenuEvent? Event(GameTimeline.EventData? data, Roster roster, ref int assistsLeft)
    {
        if (data == null)
        {
            return null;
        }

        float time = float.IsFinite(data.timeStamp) ? Math.Clamp(data.timeStamp, 0, GameMenuModel.MaxSeconds) : 0;
        int period = Math.Clamp(data.period, 0, GameMenuModel.MaxPeriod);
        if (data.TryCast<GameTimeline.GoalEventData>() is { } goal)
        {
            if (Ref(goal.scorer, roster) is not { } scorer)
            {
                return null;
            }

            var assists = new List<GameMenuRef>();
            var totals = new List<int>();
            Il2CppSystem.Collections.Generic.List<Skater>? helpers = goal.assists;
            Il2CppSystem.Collections.Generic.List<int>? helperTotals = goal.totalAssists;
            for (int i = 0; helpers != null && i < helpers.Count && assists.Count < GameMenuModel.MaxAssists; i++)
            {
                if (Ref(helpers[i], roster) is not { } helper)
                {
                    assistsLeft++;
                    continue;
                }

                assists.Add(helper);
                totals.Add(helperTotals != null && i < helperTotals.Count ? Count(helperTotals[i]) : 0);
            }

            return GameMenuEvent.Goal(period, time, scorer, Count(goal.scorerGoals), assists, totals, Count(goal.newHomeTeamScore), Count(goal.newVisitorTeamScore));
        }

        if (data.TryCast<GameTimeline.InjuryEventData>() is { } injury && Ref(injury.injuredPlayer, roster) is { } injured)
        {
            return GameMenuEvent.Injury(period, time, injured, Math.Clamp((int)injury.newInjury, 0, GameMenuModel.MaxInjuryType), Math.Clamp(injury.injuryDuration, 0, GameMenuModel.MaxInjuryDuration));
        }

        return null;
    }

    /// <summary>A forward of either side by its index, or either side's goalie; null for anyone else.</summary>
    private static GameMenuRef? Ref(Skater? skater, Roster roster)
    {
        if (skater == null)
        {
            return null;
        }

        IntPtr pointer = skater.Pointer;
        int index = roster.Home.FindIndex(f => f.Pointer == pointer);
        if (index >= 0)
        {
            return new GameMenuRef(true, index);
        }

        index = roster.Visitor.FindIndex(f => f.Pointer == pointer);
        if (index >= 0)
        {
            return new GameMenuRef(false, index);
        }

        if (roster.HomeGoalie?.Pointer == pointer)
        {
            return GameMenuRef.GoalieOf(true);
        }

        return roster.VisitorGoalie?.Pointer == pointer ? GameMenuRef.GoalieOf(false) : null;
    }

    private static int Count(int value) => Math.Clamp(value, 0, GameMenuModel.MaxCount);

    private static string? Text(TMP_Text? text) => text == null ? null : text.text ?? string.Empty;

    private static void Once(string key, string line)
    {
        if (Logged.Count < 32 && Logged.Add(key))
        {
            ModLog.Info(line);
        }
    }

    /// <summary>Who a timeline event may name: each side's forwards (by index) and goalie.</summary>
    private sealed record Roster(List<Forward> Home, List<Forward> Visitor, Goaltender? HomeGoalie, Goaltender? VisitorGoalie);
}
#endif
