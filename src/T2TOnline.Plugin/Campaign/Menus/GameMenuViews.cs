#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppBishopGames.UI;
using Il2CppSkaters;
using Il2CppSkaters.Injuries;
using Il2CppState;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using Goaltender = Il2CppGoaltender.Goaltender;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's real in-match menu: the game's own <c>GameMenu</c>, drawn with the host's numbers, its own match flow kept out.
/// <list type="bullet">
/// <item>Before it opens, the host's numbers (<see cref="GameMenuModel"/>) are read and matched against this PC's match
/// (<see cref="Prepare"/>): each forward the host names by index must be this PC's forward at that index (its asset id's
/// fingerprint), and each side's goalie this PC's goalie of that side (<see cref="GameMenuModel.SameGoalie"/>). A forward
/// on the ice that isn't makes the menu show as text; a forward's numbers or a timeline event naming a skater that isn't
/// is left out (an assist alone, from its goal), and logged.</item>
/// <item>For as long as its <c>Show</c> draws it (<see cref="BeforeShow"/> to <see cref="AfterShow"/>), the host's team
/// stats, forwards' stats, line and timeline are written into this PC's own <c>Team</c>, <c>Skater</c> and
/// <c>GameTimeline</c>, and put back right after: nothing outside the drawn menu changes. Its context is made the
/// end-of-game one for those calls (pause and end of period draw the same, but that context writes no time scale, raises
/// no <c>OnGamePause</c>, switches no action maps and owns its blurred backdrop itself), and put back too;
/// <c>SendEndOfGameStats</c> (a profile stats save) is refused by its own patch. If <c>Show</c> throws before its postfix,
/// the next frame puts everything back.</item>
/// <item>Its close (<c>Hide_Internal</c>, the base close: <c>GameMenu.Hide</c>, which ends the period, never runs from
/// here) hides its backdrop and shows the scoreboard again, and a watchdog checks a second later that no backdrop is left
/// holding the gameplay camera off (a frozen, blurred screen); one left is taken down and logged.</item>
/// </list>
/// Every menu this PC asked to open for the host is handled so, even one that opened only after the host closed it (and
/// is closed once it's up). Nothing here throws: the patches guard every entry, and a failure makes the view show as text.
/// The glossary the in-match menu opens finds the row of the host's entry here too (<see cref="GlossaryRow"/>).
/// </summary>
internal static class GameMenuViews
{
    private const double WatchSeconds = 1;
    private const float FadeOut = 0.2f;
    private const int MaxOurs = 8;

    private static readonly Dictionary<IntPtr, Shown> Ours = new();
    private static readonly List<Watched> Watching = new();
    private static readonly Dictionary<IntPtr, string> Failures = new();
    private static Swap? _swap;
    private static int _swapFrame;
    private static bool _glossaryRowsOff;
    private static IntPtr _glossaryLogged;

    /// <summary>True between a menu of ours' <c>Show</c> prefix and its postfix (its <c>SendEndOfGameStats</c> is refused then).</summary>
    public static bool InSwappedShow => _swap != null;

    /// <summary>
    /// The plan to open the host's in-match menu with: its numbers read and checked against this PC's match, and the
    /// game's own menu data. Null with why (for the log and the panel) when it can't be shown for real.
    /// </summary>
    public static GameMenuPlan? Prepare(MenuData data, out string problem)
    {
        if (!Enum.TryParse(data.Text("context"), out GameMenu.EContext context) || !Enum.IsDefined(context))
        {
            problem = "the host's menu didn't say which it is (pause, end of period, end of game)";
            return null;
        }

        if (!GameMenuModel.TryRead(data, out GameMenuModel? model, out problem))
        {
            return null;
        }

        Resolved? resolved = Resolve(model!, out problem);
        if (resolved == null)
        {
            return null;
        }

        int tab = model!.Tab != 0 ? model.Tab : LowestFlag(model.Tabs);
        var request = new GameMenu.Metadata
        {
            DefaultTab = (GameMenu.ETab)tab,
            TabsToShow = (GameMenu.ETab)model.Tabs,
            PauseButtonsToShow = (GameMenu.EPauseMenuButton)model.Buttons,
            OpenControllerAssignment = false, // the host's controller screen comes as its own menu
            Context = context,
            ShowTycoonSponsors = model.Tycoon,
        };
        return new GameMenuPlan(model, context, request);
    }

    /// <summary>A menu of ours got its data: from now until it closes, its <c>Show</c> is made safe (and given <paramref name="plan"/>'s numbers when there's a plan).</summary>
    public static void Mark(AbstractMenu menu, GameMenuPlan? plan)
    {
        GameMenu? game = menu.TryCast<GameMenu>();
        if (game == null)
        {
            return;
        }

        if (Ours.Count >= MaxOurs && !Ours.ContainsKey(menu.Pointer))
        {
            Prune();
        }

        Ours[menu.Pointer] = new Shown(game, plan);
        Failures.Remove(menu.Pointer);
    }

    /// <summary>The game gave <paramref name="menu"/> data of its own: it isn't ours (any more).</summary>
    public static void Unmark(AbstractMenu menu)
    {
        Ours.Remove(menu.Pointer);
        Failures.Remove(menu.Pointer);
    }

    /// <summary>True for an in-match menu this PC opened for the host.</summary>
    public static bool IsOurs(AbstractMenu menu) => Ours.ContainsKey(menu.Pointer);

    /// <summary>
    /// True when this PC's game shows (or is making) its own in-match menu: the host's then shows as text (a second one
    /// could take that menu over). One of ours up, or closed within the last second (still hiding), isn't its own.
    /// </summary>
    public static bool OwnMenuUp()
    {
        Il2CppUI.InGameMenu? own = Il2CppUI.InGameMenu.Instance;
        return own != null && own.IsGameMenuActive && Ours.Count == 0 && Watching.Count == 0;
    }

    /// <summary>
    /// The host's pause menu as this PC draws it for real now: one of ours with the host's numbers, in the pause context, shown (not hidden
    /// or hiding), not failed to text and not covered by a host overlay; null otherwise. A friend's My Settings goes on it
    /// (<see cref="MySettingsView"/>). Read each frame: no allocation, two calls per menu of ours.
    /// </summary>
    public static GameMenu? PauseShown()
    {
        foreach (KeyValuePair<IntPtr, Shown> pair in Ours)
        {
            Shown shown = pair.Value;
            if (shown.Plan is { Context: GameMenu.EContext.Pause } && !shown.CanvasHidden && !Failures.ContainsKey(pair.Key)
                && shown.Menu != null && !shown.Menu.WasCollected && !shown.Menu.IsHidden && !shown.Menu.IsHiding)
            {
                return shown.Menu;
            }
        }

        return null;
    }

    /// <summary>A view of ours failed (its numbers couldn't be written): why, once; the view then shows as text.</summary>
    public static bool TakeFailure(AbstractMenu menu, out string why)
    {
        bool failed = Failures.Remove(menu.Pointer, out string? found);
        why = found ?? string.Empty;
        return failed;
    }

    /// <summary>
    /// Covers or uncovers a view of ours while the host's glossary is up over it (the host's game hides its in-match menu's
    /// canvas for a full-screen overlay, and shows it again when the overlay goes).
    /// </summary>
    public static void Cover(AbstractMenu menu, bool covered)
    {
        if (!Ours.TryGetValue(menu.Pointer, out Shown? shown))
        {
            return;
        }

        UnityEngine.Canvas? canvas = shown.Menu.m_Canvas;
        if (canvas == null)
        {
            return;
        }

        if (covered && !shown.CanvasHidden && canvas.enabled)
        {
            canvas.enabled = false;
            shown.CanvasHidden = true;
        }
        else if (!covered && shown.CanvasHidden)
        {
            canvas.enabled = true;
            shown.CanvasHidden = false;
        }
    }

    /// <summary>
    /// A friend's glossary: the navigable of the row that shows <paramref name="entry"/> in the open tab, the list moved to
    /// it when it isn't among the rows the list has made (it makes rows only around what's on screen; its own
    /// <c>SelectIndex</c>). Null when the open tab doesn't have the entry (the host's tab step is still to come) or its row
    /// doesn't hold it. Selecting the row's navigable shows the same entry (the menu's own <c>OnRowHighlighted</c>). Never
    /// throws: a failure is logged once and the rows aren't looked for again (the entry still follows the host's).
    /// </summary>
    public static AbstractNavigable? GlossaryRow(GlossaryMenu glossary, GlossaryEntrySO entry)
    {
        if (_glossaryRowsOff)
        {
            return null;
        }

        try
        {
            Il2CppList.List<GlossaryEntrySO>? entries = glossary.m_Entries;
            GlossaryScrollList? list = glossary.m_List;
            if (entries == null || list == null)
            {
                return null;
            }

            // The list's data is the open tab's entries, in its order: a row's place in the list is its entry's index here.
            int index = -1;
            for (int i = 0; i < entries.Count && index < 0; i++)
            {
                if (entries[i]?.Pointer == entry.Pointer)
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                return null;
            }

            Il2CppUICore.ReusableList.BaseCell? cell = list.GetCellByGlobalIndex(index);
            bool moved = cell == null;
            cell ??= list.SelectIndex(index);
            GlossaryRow? row = cell?.TryCast<GlossaryRow>();
            AbstractNavigable? navigable = row != null && row._Entry_k__BackingField?.Pointer == entry.Pointer ? row._Navigable_k__BackingField : null;
            if (navigable != null && glossary.Pointer != _glossaryLogged)
            {
                _glossaryLogged = glossary.Pointer;
                ModLog.Info($"Menus: the glossary's row of the host's entry was found here ({(moved ? "the list moved to it" : "among its rows")}); it's highlighted as the host's.");
            }

            return navigable;
        }
        catch (Exception ex)
        {
            _glossaryRowsOff = true;
            ModLog.Warn($"Menus: the glossary's row of the host's entry couldn't be found here ({ModLog.SafeErrorText(ex)}); " +
                "the glossary's entry still follows the host's, its highlighted row doesn't (until the game restarts).");
            return null;
        }
    }

    /// <summary>
    /// The <c>GameMenu.Show</c> prefix: for a menu of ours, the end-of-game context and the host's numbers, recorded so
    /// <see cref="AfterShow"/> (or the next frame) puts them back.
    /// </summary>
    public static void BeforeShow(GameMenu menu)
    {
        if (!Ours.TryGetValue(menu.Pointer, out Shown? shown))
        {
            return;
        }

        Restore("a menu of ours showing before the last was put back"); // never expected: one Show at a time
        var swap = new Swap(menu);
        _swap = swap;
        _swapFrame = UnityEngine.Time.frameCount;
        swap.Context = menu.m_Context;
        if (swap.Context != GameMenu.EContext.EndOfGame)
        {
            menu.m_Context = GameMenu.EContext.EndOfGame;
        }

        GameMenuPlan? plan = shown.Plan;
        if (plan == null)
        {
            return; // a late menu, closed once it's up: drawn safely, without the host's numbers
        }

        Resolved? resolved = Resolve(plan.Model, out string problem);
        if (resolved == null)
        {
            Fail(menu, problem);
            return;
        }

        try
        {
            Write(menu, swap, resolved);
            shown.Written = resolved;
        }
        catch (Exception ex)
        {
            Fail(menu, $"its numbers couldn't be written here ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// The <c>GameMenu.Show</c> postfix: everything <see cref="BeforeShow"/> changed is put back (the menu has drawn
    /// itself), the drawn stats are checked against the host's, and the open is logged.
    /// </summary>
    public static void AfterShow(GameMenu menu)
    {
        if (_swap == null || _swap.Menu.Pointer != menu.Pointer)
        {
            return;
        }

        Restore(null);
        if (!Ours.TryGetValue(menu.Pointer, out Shown? shown) || shown.Plan == null || Failures.ContainsKey(menu.Pointer))
        {
            return;
        }

        GameMenuModel model = shown.Plan.Model;
        string check;
        try
        {
            uint mine = GameMenuModel.TextCheck(GameMenuCapture.StatsTexts(menu));
            check = model.Check == mine ? "the drawn stats match the host's" : $"the drawn stats differ from the host's (check {mine:x8} here, {model.Check:x8} there)";
        }
        catch (Exception ex)
        {
            check = $"the drawn stats couldn't be checked ({ex.GetType().Name})";
        }

        string injuries = Injuries(menu, shown, model);
        Resolved? written = shown.Written;
        string goalies = written is { GoalieEvents: > 0 } ? $" ({written.GoalieEvents} with a goalie)" : string.Empty;
        ModLog.Info($"Menus: real in-match menu open here ({shown.Plan.Context}): {written?.TeamValues ?? 0} team value(s) and {written?.SkaterValues ?? 0} forward value(s) written, " +
            $"{written?.Events ?? 0} of {model.Events.Count} timeline event(s){goalies}{Left(written)}; {check}; {injuries}. All put back once drawn.");
    }

    /// <summary>
    /// A menu is closing (<c>Hide_Internal</c>, every close): for one of ours, the canvas and injury list as they were,
    /// its backdrop hidden and the scoreboard shown again (as the game's own close does), and the watchdog set.
    /// </summary>
    public static void Hiding(AbstractMenu menu)
    {
        if (!Ours.Remove(menu.Pointer, out Shown? shown))
        {
            return;
        }

        Failures.Remove(menu.Pointer);
        GameMenu game = shown.Menu;
        Guard("showing its canvas again", () => Uncover(shown));
        Guard("putting its injury list back", () =>
        {
            if (shown.InjuryHidden && game.m_InjuryChangeList != null)
            {
                game.m_InjuryChangeList.gameObject.SetActive(shown.InjuryWasActive);
                shown.InjuryHidden = false;
            }
        });
        Guard("hiding its backdrop", () => _ = SceneBackdrop.Hide(game, FadeOut));
        Guard("showing the scoreboard again", () => game.SetScoreboardHidden(false));
        bool held = false;
        int owners = -1;
        Guard("checking the backdrop", () =>
        {
            Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Object, int>? all = SceneBackdrop.s_Instance?.m_Owners;
            owners = all?.Count ?? -1;
            held = all != null && all.ContainsKey(game);
        });
        Watching.Add(new Watched(game, FrameClock.Seconds, "in-match menu"));
        ModLog.Info($"Menus: real in-match menu closed here{(shown.Plan != null ? $" ({shown.Plan.Context})" : string.Empty)}; " +
            $"{(held ? "its backdrop is still up (checked again in a second)" : "backdrop restored")} ({(owners < 0 ? "no backdrop in this scene" : $"{owners} backdrop owner(s) left")}), scoreboard back on.");
    }

    /// <summary>
    /// Another real menu of ours that shows the blurred backdrop (the Canada curse) has closed: the watchdog checks a second
    /// later that it no longer holds it (and takes it down if it does). <paramref name="what"/> names it for the log.
    /// </summary>
    public static void WatchBackdrop(AbstractMenu menu, string what) => Watching.Add(new Watched(menu, FrameClock.Seconds, what));

    /// <summary>
    /// Every frame: anything a <c>Show</c> left changed (it threw before its postfix) is put back; a closed menu still
    /// holding the backdrop a second after its close is taken off it (the gameplay camera stays off while one does);
    /// menus that are gone are forgotten.
    /// </summary>
    public static void Tick()
    {
        if (_swap != null && UnityEngine.Time.frameCount != _swapFrame)
        {
            GameMenu menu = _swap.Menu;
            Restore("its Show didn't finish");
            Fail(menu, "it failed while drawing itself here");
        }

        for (int i = Watching.Count - 1; i >= 0; i--)
        {
            Watched watched = Watching[i];
            if (FrameClock.Seconds - watched.Since < WatchSeconds)
            {
                continue;
            }

            Watching.RemoveAt(i);
            if (Ours.ContainsKey(watched.Menu.Pointer))
            {
                continue; // shown again for the host since
            }

            Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Object, int>? owners = SceneBackdrop.s_Instance?.m_Owners;
            if (owners != null && owners.ContainsKey(watched.Menu))
            {
                ModLog.Warn($"Menus: a real {watched.What} closed {WatchSeconds:0} s ago still held the blurred backdrop here ({owners.Count} owner(s)); taking it down so the match shows again.");
                _ = SceneBackdrop.Hide(watched.Menu, FadeOut);
            }
        }

        if (Ours.Count > 0 && UnityEngine.Time.frameCount % 120 == 0)
        {
            Prune();
        }
    }

    /// <summary>Writes the host's numbers, recording each old value in <paramref name="swap"/> before changing it.</summary>
    private static void Write(GameMenu menu, Swap swap, Resolved resolved)
    {
        foreach (SideWrite side in resolved.Sides)
        {
            TeamStats old = side.Team.teamStats;
            swap.Teams.Add((side.Team, old));
            TeamStats stats = old;
            stats.shotsOnNet = side.Counts[0];
            stats.passesAttempt = side.Counts[1];
            stats.passesSuccess = side.Counts[2];
            stats.knockoutsPerformed = side.Counts[3];
            stats.interception = side.Counts[4];
            stats.blockedShots = side.Counts[5];
            stats.faceoffWon = side.Counts[6];
            stats.timeOnAttack = side.Attack;
            side.Team.teamStats = stats;

            foreach ((Forward forward, int[] counts) in side.Stats)
            {
                SkaterStats before = forward.gameStats;
                swap.Skaters.Add((forward, before));
                SkaterStats edit = forward.gameStats;
                edit.goals = counts[0];
                edit.assists = counts[1];
                edit.shotsOnNet = counts[2];
                edit.passesAttempt = counts[3];
                edit.knockoutsPerformed = counts[4];
                edit.interception = counts[5];
                edit.blockedShots = counts[6];
                forward.gameStats = edit;
            }

            var line = new Il2CppList.List<Forward>();
            foreach (Forward forward in side.Line)
            {
                line.Add(forward);
            }

            swap.Lines.Add((side.Team, side.Team._PlayingForwards_k__BackingField));
            side.Team._PlayingForwards_k__BackingField = line;
        }

        GameTimeline? timeline = menu.m_TimelineUI?.m_GameTimeline;
        if (timeline == null)
        {
            throw new InvalidOperationException("this PC's match has no timeline for the menu");
        }

        var events = new Il2CppList.List<GameTimeline.EventData>();
        foreach (Func<GameTimeline.EventData> make in resolved.MakeEvents)
        {
            events.Add(make());
        }

        swap.Timeline = timeline;
        swap.Events = timeline._events;
        timeline._events = events;
    }

    /// <summary>Puts back everything the open <c>Show</c> changed (each step guarded). <paramref name="why"/>: logged when it wasn't the postfix.</summary>
    private static void Restore(string? why)
    {
        Swap? swap = _swap;
        if (swap == null)
        {
            return;
        }

        _swap = null;
        if (why != null)
        {
            ModLog.Warn($"Menus: putting this PC's match back after a real in-match menu ({why}).");
        }

        Guard("putting the timeline back", () =>
        {
            if (swap.Timeline != null)
            {
                swap.Timeline._events = swap.Events!;
            }
        });
        for (int i = swap.Lines.Count - 1; i >= 0; i--)
        {
            (Team team, Il2CppList.List<Forward>? line) = swap.Lines[i];
            Guard("putting a line back", () => team._PlayingForwards_k__BackingField = line!);
        }

        for (int i = swap.Skaters.Count - 1; i >= 0; i--)
        {
            (Forward forward, SkaterStats stats) = swap.Skaters[i];
            Guard("putting a forward's stats back", () => forward.gameStats = stats);
        }

        for (int i = swap.Teams.Count - 1; i >= 0; i--)
        {
            (Team team, TeamStats stats) = swap.Teams[i];
            Guard("putting a team's stats back", () =>
            {
                // Only the menu's fields: the goals stay as the HUD's mirror has them now.
                TeamStats now = team.teamStats;
                now.shotsOnNet = stats.shotsOnNet;
                now.passesAttempt = stats.passesAttempt;
                now.passesSuccess = stats.passesSuccess;
                now.knockoutsPerformed = stats.knockoutsPerformed;
                now.interception = stats.interception;
                now.blockedShots = stats.blockedShots;
                now.faceoffWon = stats.faceoffWon;
                now.timeOnAttack = stats.timeOnAttack;
                team.teamStats = now;
            });
        }

        Guard("putting the menu's context back", () =>
        {
            if (swap.Menu != null && swap.Menu.m_Context != swap.Context)
            {
                swap.Menu.m_Context = swap.Context;
            }
        });
    }

    /// <summary>
    /// The host's numbers matched against this PC's match: every forward the host names by index must be this PC's (its
    /// asset id's fingerprint), each side's goalie this PC's (<see cref="GameMenuModel.SameGoalie"/>); the line must be, or
    /// it's null with why. A forward's numbers or an event naming a skater that isn't is left out (counted); an assist
    /// naming one is left out of its goal (counted), as the host's game leaves out one it can't name.
    /// </summary>
    private static Resolved? Resolve(GameMenuModel model, out string problem)
    {
        GameManager? game = GameMenuCapture.Game();
        Team? home = game?._homeTeam;
        Team? visitor = game?._visitorTeam;
        if (home == null || visitor == null)
        {
            problem = "your game has no match running";
            return null;
        }

        var resolved = new Resolved();
        var good = new Dictionary<bool, List<Forward?>>();
        var goalies = new Dictionary<bool, Goaltender?>();
        foreach ((bool isHome, Team team) in new[] { (true, home), (false, visitor) })
        {
            GameMenuSide side = model.Side(isHome);
            string name = isHome ? "home" : "visiting";
            List<Forward> forwards = GameMenuCapture.Forwards(team);
            goalies[isHome] = ResolveGoalie(team, side, name, resolved);

            // This PC's forward at each of the host's indices, when it's the host's (null when it isn't, or there's none).
            var named = new List<Forward?>();
            for (int i = 0; i < side.Ids.Count; i++)
            {
                named.Add(i < forwards.Count && GameMenuModel.IdPrint(GameMenuCapture.MatchAssetId(forwards[i])) == side.Ids[i] ? forwards[i] : null);
            }

            good[isHome] = named;
            int unmatched = named.Count(f => f == null);
            if (unmatched > 0 || forwards.Count != side.Ids.Count)
            {
                resolved.Notes.Add($"{unmatched} of the host's {side.Ids.Count} {name} forwards aren't this PC's at the same place ({forwards.Count} here)");
            }

            var write = new SideWrite(team, side.Counts.ToArray(), side.TimeOnAttack);
            foreach (int index in side.Line)
            {
                if (named[index] is not { } onIce)
                {
                    problem = $"your game's {name} forwards on the ice aren't the host's (forward {index} differs)";
                    return null;
                }

                write.Line.Add(onIce);
            }

            var sent = side.Skaters.ToDictionary(s => s.Index, s => s.Counts);
            for (int i = 0; i < named.Count; i++)
            {
                if (named[i] is { } forward)
                {
                    write.Stats.Add((forward, sent.TryGetValue(i, out IReadOnlyList<int>? counts) ? counts.ToArray() : new int[GameMenuModel.SkaterCounts]));
                }
                else if (sent.ContainsKey(i))
                {
                    resolved.SkippedForwards++;
                }
            }

            resolved.Sides.Add(write);
        }

        foreach (GameMenuEvent e in model.Events)
        {
            if (Named(good, goalies, e.Who) is not { } actor || (e.Kind == GameMenuEventKind.Injury && !Enum.IsDefined(typeof(InjuryType), e.InjuryType)))
            {
                resolved.SkippedEvents++;
                continue;
            }

            var assistants = new List<(Skater Skater, int Total)>();
            bool goalie = e.Who.IsGoalie;
            for (int i = 0; i < e.Assists.Count; i++)
            {
                if (Named(good, goalies, e.Assists[i]) is { } helper)
                {
                    assistants.Add((helper, e.AssistTotals[i]));
                    goalie |= e.Assists[i].IsGoalie;
                }
                else
                {
                    resolved.SkippedAssists++;
                }
            }

            resolved.GoalieEvents += goalie ? 1 : 0;
            resolved.MakeEvents.Add(e.Kind == GameMenuEventKind.Goal ? () => Goal(e, actor, assistants) : () => Injury(e, actor));
        }

        if (resolved.SkippedForwards > 0)
        {
            resolved.Notes.Add($"{resolved.SkippedForwards} forward(s)' numbers left out");
        }

        if (resolved.SkippedEvents > 0)
        {
            resolved.Notes.Add($"{resolved.SkippedEvents} timeline event(s) left out");
        }

        if (resolved.SkippedAssists > 0)
        {
            resolved.Notes.Add($"{resolved.SkippedAssists} assist(s) left out of their goals");
        }

        problem = string.Empty;
        return resolved;
    }

    /// <summary>
    /// This PC's goalie of <paramref name="team"/> when it's the host's goalie of that side (a fingerprint in common), else
    /// null; noted when the host named one and this PC's isn't it.
    /// </summary>
    private static Goaltender? ResolveGoalie(Team team, GameMenuSide side, string name, Resolved resolved)
    {
        if (side.Goalie.Count == 0)
        {
            return null;
        }

        Goaltender? goalie = GameMenuCapture.Goalie(team);
        if (goalie != null && GameMenuModel.SameGoalie(side.Goalie, GameMenuCapture.GoaliePrints(goalie)))
        {
            return goalie;
        }

        resolved.Notes.Add($"the host's {name} goalie isn't this PC's");
        return null;
    }

    /// <summary>This PC's skater the host names: a forward by its index, or a side's goalie; null when it isn't the host's.</summary>
    private static Skater? Named(Dictionary<bool, List<Forward?>> good, Dictionary<bool, Goaltender?> goalies, GameMenuRef who)
    {
        if (who.IsGoalie)
        {
            return goalies.TryGetValue(who.Home, out Goaltender? goalie) ? goalie : null;
        }

        return good.TryGetValue(who.Home, out List<Forward?>? side) && who.Index < side.Count ? side[who.Index] : null;
    }

    private static GameTimeline.GoalEventData Goal(GameMenuEvent e, Skater scorer, List<(Skater Skater, int Total)> helpers)
    {
        var assists = new Il2CppList.List<Skater>();
        var totals = new Il2CppList.List<int>();
        foreach ((Skater helper, int total) in helpers)
        {
            assists.Add(helper);
            totals.Add(total);
        }

        return new GameTimeline.GoalEventData
        {
            period = e.Period,
            timeStamp = e.Time,
            scorer = scorer,
            scorerGoals = e.ScorerGoals,
            assists = assists,
            totalAssists = totals,
            newHomeTeamScore = e.HomeScore,
            newVisitorTeamScore = e.VisitorScore,
        };
    }

    private static GameTimeline.InjuryEventData Injury(GameMenuEvent e, Skater injured) => new()
    {
        period = e.Period,
        timeStamp = e.Time,
        injuredPlayer = injured,
        newInjury = (InjuryType)e.InjuryType,
        injuryDuration = e.Duration,
    };

    /// <summary>
    /// The injury list: the host's rows written into this PC's list when they came (format 3, <see cref="WriteInjuries"/>);
    /// else left as drawn when it's the host's, hidden (put back at the close) when it isn't.
    /// </summary>
    private static string Injuries(GameMenu menu, Shown shown, GameMenuModel model)
    {
        try
        {
            if (model.InjuryRows is { } rows)
            {
                return WriteInjuries(menu, shown, model, rows);
            }

            string mine = GameMenuCapture.InjuryDigest(menu);
            if (model.Injuries.Length == 0 || mine == model.Injuries)
            {
                return "injury list as the host's";
            }

            InjuryChangeList? list = menu.m_InjuryChangeList;
            if (list == null)
            {
                return "no injury list here";
            }

            // This PC's run is the one from before the match, and its injuries aren't tracked during a host's match.
            shown.InjuryWasActive = list.gameObject.activeSelf;
            shown.InjuryHidden = true;
            list.gameObject.SetActive(false);
            return "injury list hidden (this PC's differs from the host's)";
        }
        catch (Exception ex)
        {
            return $"injury list not checked ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// The host's injury list rows (v1) written into this PC's own list after its <c>Show</c>
    /// drew its own: each row's forward found in this PC's copy of the run's team by its asset id's print, its icon from this
    /// PC's injury system (the same kind, type and definition), made with the game's own <c>InjuryChangeData</c> and shown by
    /// the list's own <c>AutoCyclingList.Refresh</c> with the game's own delegates (the ones its own refresh in this
    /// <c>Show</c> passed, kept by the list); shown or hidden as the host's (put back at the close). Nothing else to put back: the next <c>Show</c> refreshes
    /// it. A list it can't write is hidden, as before.
    /// </summary>
    private static string WriteInjuries(GameMenu menu, Shown shown, GameMenuModel model, IReadOnlyList<GameMenuInjuryRow> rows)
    {
        InjuryChangeList? list = menu.m_InjuryChangeList;
        Il2CppList.List<InjuryChangeData>? data = list?.m_InjuryChangesData;
        AutoCyclingList? cycling = list?.m_List;
        if (list == null || data == null || cycling == null)
        {
            return "no injury list here";
        }

        // The list's own delegates, as the game's own refresh in this Show passed them (verified in the game's code:
        // AutoCyclingList.Refresh stores them).
        Il2CppSystem.Func<AutoCyclingList.ItemData, AutoCyclingList.EItemState>? state = cycling.m_ItemStateGetter;
        Il2CppSystem.Predicate<AutoCyclingList.ItemData>? changed = cycling.m_ItemChangedCheck;
        Il2CppData.TeamData? team = MenuGame.Campaign()?.runData?.teamData;
        if ((state == null || changed == null || team == null) && rows.Count == 0 && data.Count == 0)
        {
            // Nothing to write and nothing to clear: the game's own list fills only in the campaign (verified in the game's
            // code: InjuryChangeList.Refresh), so an exhibition's is empty on both PCs.
            SetShown(list, shown, model.InjuryListShown);
            return "injury list as the host's (empty here too)";
        }

        if (state == null || changed == null || team == null)
        {
            SetShown(list, shown, false);
            return $"injury list hidden ({(team == null ? "this PC holds no run team for the host's rows" : "the game's own list code hasn't run here")})";
        }

        var forwards = new Dictionary<ushort, Il2CppData.ForwardData>();
        Il2CppList.List<Il2CppData.ForwardData>? all = team.GetForwards(false);
        for (int i = 0; all != null && i < all.Count; i++)
        {
            if (all[i] is { } forward)
            {
                forwards.TryAdd(GameMenuModel.IdPrint(forward.OriginalId), forward);
            }
        }

        InjurySystem? system = list.m_InjurySystem;
        data.Clear();
        int left = 0;
        foreach (GameMenuInjuryRow row in rows)
        {
            if (!forwards.TryGetValue(row.Print, out Il2CppData.ForwardData? forward))
            {
                left++;
                continue;
            }

            UnityEngine.Sprite? icon = system == null ? null : row.Icon switch
            {
                GameMenuInjuryIcon.Injury => system.GetInjuryIcon((InjuryType)row.Type, row.Hd),
                GameMenuInjuryIcon.Recovery => system.GetRecoveryIcon((InjuryType)row.Type, row.Hd),
                _ => null,
            };
            data.Add(new InjuryChangeData(forward, team, row.Home, icon, row.Previous, row.New));
        }

        cycling.Refresh(data.Cast<Il2CppSystem.Collections.IList>(), state, changed);
        SetShown(list, shown, model.InjuryListShown);
        string check = GameMenuCapture.InjuryDigest(menu) == model.Injuries ? "injury list as the host's" : "injury list written (its digest differs from the host's)";
        string leftOut = left > 0 ? $", {left} naming no forward of this PC's run team left out" : string.Empty;
        return $"{check} ({rows.Count - left} row(s) written{leftOut}{(model.InjuryListShown ? string.Empty : ", hidden as the host's")})";
    }

    /// <summary>Shows or hides the injury list (its state before is put back at the close).</summary>
    private static void SetShown(InjuryChangeList list, Shown shown, bool show)
    {
        bool active = list.gameObject.activeSelf;
        if (active == show)
        {
            return;
        }

        if (!shown.InjuryHidden)
        {
            shown.InjuryWasActive = active;
            shown.InjuryHidden = true;
        }

        list.gameObject.SetActive(show);
    }

    private static string Left(Resolved? written) => written == null || written.Notes.Count == 0 ? string.Empty : $" ({string.Join("; ", written.Notes)})";

    private static void Uncover(Shown shown)
    {
        UnityEngine.Canvas? canvas = shown.Menu.m_Canvas;
        if (shown.CanvasHidden && canvas != null)
        {
            canvas.enabled = true;
        }

        shown.CanvasHidden = false;
    }

    private static void Fail(GameMenu menu, string why)
    {
        Failures[menu.Pointer] = why;
        ModLog.Warn($"Menus: a real in-match menu failed here ({why}); it shows as text.");
    }

    /// <summary>Forgets menus that are gone (destroyed with their scene: the game's own destroy hid their backdrop).</summary>
    private static void Prune()
    {
        foreach (IntPtr gone in Ours.Where(p => p.Value.Menu == null || p.Value.Menu.WasCollected).Select(p => p.Key).ToList())
        {
            Ours.Remove(gone);
            Failures.Remove(gone);
        }
    }

    private static int LowestFlag(int flags)
    {
        for (int bit = 1; bit <= GameMenuModel.TabFlags; bit <<= 1)
        {
            if ((flags & bit) != 0)
            {
                return bit;
            }
        }

        return 0;
    }

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Menus: failed while {what} for a real in-match menu", ex);
        }
    }

    /// <summary>A menu of ours, while it's up.</summary>
    private sealed class Shown
    {
        public Shown(GameMenu menu, GameMenuPlan? plan)
        {
            Menu = menu;
            Plan = plan;
        }

        public GameMenu Menu { get; }

        public GameMenuPlan? Plan { get; }

        public Resolved? Written { get; set; }

        public bool CanvasHidden { get; set; }

        public bool InjuryHidden { get; set; }

        public bool InjuryWasActive { get; set; }
    }

    /// <summary>What one <c>Show</c> changed, to put back.</summary>
    private sealed class Swap
    {
        public Swap(GameMenu menu) => Menu = menu;

        public GameMenu Menu { get; }

        public GameMenu.EContext Context { get; set; }

        public List<(Team Team, TeamStats Stats)> Teams { get; } = new();

        public List<(Forward Forward, SkaterStats Stats)> Skaters { get; } = new();

        public List<(Team Team, Il2CppList.List<Forward>? Line)> Lines { get; } = new();

        public GameTimeline? Timeline { get; set; }

        public Il2CppList.List<GameTimeline.EventData>? Events { get; set; }
    }

    /// <summary>One side's numbers, matched to this PC's objects.</summary>
    private sealed class SideWrite
    {
        public SideWrite(Team team, int[] counts, float attack)
        {
            Team = team;
            Counts = counts;
            Attack = attack;
        }

        public Team Team { get; }

        public int[] Counts { get; }

        public float Attack { get; }

        public List<(Forward Forward, int[] Counts)> Stats { get; } = new();

        public List<Forward> Line { get; } = new();
    }

    /// <summary>The host's numbers matched to this PC's match.</summary>
    private sealed class Resolved
    {
        public List<SideWrite> Sides { get; } = new();

        public List<Func<GameTimeline.EventData>> MakeEvents { get; } = new();

        public List<string> Notes { get; } = new();

        public int SkippedForwards { get; set; }

        public int SkippedEvents { get; set; }

        public int SkippedAssists { get; set; }

        public int GoalieEvents { get; set; }

        public int TeamValues => Sides.Count * (GameMenuModel.TeamCounts + 1);

        public int SkaterValues => Sides.Sum(s => s.Stats.Count) * GameMenuModel.SkaterCounts;

        public int Events => MakeEvents.Count;
    }

    private readonly record struct Watched(AbstractMenu Menu, double Since, string What);
}

/// <summary>The game's menu data for a friend's real in-match menu, and the host's numbers it's drawn with.</summary>
internal sealed class GameMenuPlan
{
    /// <summary>A plan: the host's numbers, the menu's context and the game's data to open it with.</summary>
    public GameMenuPlan(GameMenuModel model, GameMenu.EContext context, GameMenu.Metadata request)
    {
        Model = model;
        Context = context;
        Request = request;
    }

    /// <summary>The host's numbers.</summary>
    public GameMenuModel Model { get; }

    /// <summary>Which menu it is (pause, end of period, end of game).</summary>
    public GameMenu.EContext Context { get; }

    /// <summary>The game's data to open it with (what <c>InGameMenu</c> would pass, from the host's).</summary>
    public GameMenu.Metadata Request { get; }
}
#endif
