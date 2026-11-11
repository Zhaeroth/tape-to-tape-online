#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppControllerConnection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRewired;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Startup;
using UnityEngine;
using UnityEngine.EventSystems;
using ScrollRect = UnityEngine.UI.ScrollRect;

namespace T2TOnline.Plugin.NativeLobby;

// The walk by a keyboard (run.txt's first line "kbwalk"): a virtual keyboard (Input/VirtualKeyboard.cs) whose maps are this PC's own keyboard maps, read
// (LocalKeyboard.Find: nothing changes), joined by its own key at press-to-start as a real keyboard player's is, and the controlling player from then on:
// the screens draw keyboard glyphs. The walks press what the pad walk presses, as the keys the player's UI map binds to each action (Tap routes there):
// the arrows, Enter, Esc, the tab keys. Then the mouse: each row's and bar shortcut's centre raycast (EventSystem.RaycastAll) must hit it first, and in the
// game's own mouse mode (UIInputSource.SetForcedNavigationMode, put back after) pointer enter and click go to the handler a pointer reaches: a row's own
// OnPointerEnter and OnPointerClick, a bar shortcut's Click behind the same mouse-mode check its handler makes. Raw keys (Alt, Shift, Tab, Alt+Tab) and a
// real mouse's wheel stay a person's check: a tour stops on any real press. Each row ends with one line (Core's KeyWalk.Line).
internal static partial class LobbyUiCheck
{
    private static WalkKeyboard? _keys;
    private static bool _modeForced;
    private static NavigationMode _modeBefore;

    private static void AddKeyWalkSteps()
    {
        Steps["kb_start"] = StepKeyStart;
        Steps["kb_settings"] = () => HomeStep(StepKeySettings);
        Steps["kb_online_settings"] = () => HomeStep(StepKeyOnlineSettings);
        Steps["kb_backups"] = () => HomeStep(StepKeyBackups);
        Steps["kb_report"] = () => HomeStep(StepKeyReport);
        Steps["kb_lobby"] = () => HomeStep(StepKeyLobby);
    }

    // ---- The steps ----

    private static IEnumerable<Wait> StepKeyStart()
    {
        TitleScreen title = Title();
        foreach (Wait wait in MakeKeyboard())
        {
            yield return wait;
        }

        if (_keys == null)
        {
            yield break;
        }

        StepNote($"at the start: group {(int)title.m_CurrentButtonGroup}, window focused {Application.isFocused}, the UI's controlling player: {Controlling()}, the navigation mode {UiMode()}");
        foreach (Wait wait in Snap("press_to_start", SettleFrames))
        {
            yield return wait;
        }

        if ((int)title.m_CurrentButtonGroup == 0)
        {
            foreach (Wait wait in KeyPress("the join key", KeyWalk.Join, () => _keys!.GamePlayer >= 0, "the keyboard a game player, by the game's join"))
            {
                yield return wait;
            }

            yield return Seconds(0.5);
            if ((int)Title().m_CurrentButtonGroup == 0)
            {
                StepNote("the title stayed on press-to-start: the first press only joined the keyboard; a person presses again");
                foreach (Wait wait in KeyPress("Enter, a second press", KeyWalk.For(PadButtons.A)!, () => (int)Title().m_CurrentButtonGroup != 0, "off press-to-start"))
                {
                    yield return wait;
                }
            }
        }

        yield return Soft(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 8, "the main group");
        Check("press-to-start", () => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, "the main group");
        Check("the UI's controlling player", () => _keys != null && MenuGame.Module()?.InputSource?.SingleControllingPlayer is { } player && player.NativeId == _keys.RewiredId,
            $"the keyboard (Rewired player {_keys?.RewiredId})");
        yield return Soft(() => OnlineGroup.IsIn && Cursor() == "Campaign", 5, "the cursor on Campaign");
        Check("Main's first focus", () => OnlineGroup.IsIn && Cursor() == "Campaign", "the cursor on Campaign, the Online row in");
        UiAssets.Request(OnlineGroup.Sources);
        yield return Until(() => Array.TrueForAll(OnlineGroup.Sources, UiAssets.Ready) || UiAssets.Problem != null, UiAssets.TimeoutSeconds + 1, "the source prefabs");
        string glyph = Glyph();
        StepNote($"the glyphs: {glyph}");
        Check("keyboard glyphs", () => GlyphIsPad() == false, $"not a pad's (they're {glyph})");
        foreach (Wait wait in Snap("main", SettleFrames))
        {
            yield return wait;
        }
    }

    /// <summary>Settings > Online by the keys: every shown row by the arrows, the toggles by Enter and back, the choice by the arrows; each row by the mouse; Esc out.</summary>
    private static IEnumerable<Wait> StepKeySettings()
    {
        var rows = new Dictionary<SettingsRowId, KeyRow>();
        foreach (Wait wait in SettingsOnOnline())
        {
            yield return wait;
        }

        string glyph = Glyph();
        Check("keyboard glyphs on Settings", () => GlyphIsPad() == false, $"not a pad's (they're {glyph})");
        List<SettingsRowId> shown = ShownRows();
        foreach (SettingsRowId id in shown)
        {
            foreach (Wait wait in ToRow(id))
            {
                yield return wait;
            }

            rows[id] = new KeyRow(id.ToString()) { Arrows = Result(OnRow(id), $"the cursor isn't on {id}") };
        }

        foreach (SettingsRowId id in new[] { SettingsRowId.ShareAchievements, SettingsRowId.ShareProgress })
        {
            Func<bool> value = id == SettingsRowId.ShareAchievements ? () => ModSettings.ShareAchievements : () => ModSettings.ShareProgress;
            bool start = value();
            foreach (Wait wait in ToRow(id))
            {
                yield return wait;
            }

            foreach (Wait wait in WalkPress($"Enter on {id}", PadButtons.A, () => value() != start, start ? "off" : "on"))
            {
                yield return wait;
            }

            foreach (Wait wait in WalkPress($"Enter on {id} again", PadButtons.A, () => value() == start, start ? "on again" : "off again"))
            {
                yield return wait;
            }

            rows[id].Enter = Result(_walkOk && value() == start, "it didn't change and come back");
        }

        OnlineInputChoice input = ModSettings.OnlineInput;
        foreach (Wait wait in ToRow(SettingsRowId.PlayOnlineWith))
        {
            yield return wait;
        }

        PadButtons away = input == OnlineInputChoice.KeyboardMouse ? PadButtons.DPadLeft : PadButtons.DPadRight;
        PadButtons home = away == PadButtons.DPadLeft ? PadButtons.DPadRight : PadButtons.DPadLeft;
        foreach (Wait wait in WalkPress("an arrow on Play Online With", away, () => ModSettings.OnlineInput != input, "another choice"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("and back", home, () => ModSettings.OnlineInput == input, $"{input} again"))
        {
            yield return wait;
        }

        if (rows.TryGetValue(SettingsRowId.PlayOnlineWith, out KeyRow? choice))
        {
            choice.Arrows = Result(_walkOk && ModSettings.OnlineInput == input, "the choice didn't move and come back");
        }

        foreach (Wait wait in Snap("settings_online", 6))
        {
            yield return wait;
        }

        // The mouse: every row's centre hits it; the pointer's enter and click on the toggles (each twice: held in memory, put back). Direct Connections,
        // Save Backups, Problem Report and the session's rows are hit only: their clicks are the pad walk's presses or never made.
        SettingsMenu? menu = Settings();
        foreach (Wait wait in MouseOn())
        {
            yield return wait;
        }

        foreach (SettingsRowId id in shown)
        {
            AbstractNavigable? row = menu == null ? null : SettingsTab.Row(menu, id);
            rows[id].Hit = row == null ? "FAIL no row" : MouseHit(row.gameObject);
            if (row != null && id is SettingsRowId.ShareAchievements or SettingsRowId.ShareProgress && _modeForced)
            {
                Func<bool> value = id == SettingsRowId.ShareAchievements ? () => ModSettings.ShareAchievements : () => ModSettings.ShareProgress;
                bool start = value();
                bool first = PointerClick(row);
                yield return Frames(6);
                bool changed = value() != start;
                bool second = PointerClick(row);
                yield return Frames(6);
                rows[id].Click = Result(first && second && changed && value() == start, $"the click didn't change it and back (changed {changed}, now {value()})");
            }
        }

        foreach (Wait wait in MouseOff())
        {
            yield return wait;
        }

        foreach (Wait wait in ToRow(SettingsRowId.ShareAchievements))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseSettingsToTitle("Settings"))
        {
            yield return wait;
        }

        if (rows.TryGetValue(SettingsRowId.ShareAchievements, out KeyRow? last))
        {
            last.Escape = Result(_walkOk && Settings() == null, "Settings didn't close");
        }

        foreach (KeyRow row in rows.Values)
        {
            SayRow("Settings > Online", row, glyph);
        }
    }

    /// <summary>The Online group by the arrows; Online Settings by Enter (Settings on its Online tab) and by a click; Esc back each time.</summary>
    private static IEnumerable<Wait> StepKeyOnlineSettings()
    {
        var rows = new List<KeyRow>();
        foreach (Wait wait in MainTo("Online"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("Enter on Online", PadButtons.A, () => InGroup() && Cursor() == GroupFocus(), "the Online group"))
        {
            yield return wait;
        }

        string glyph = Glyph();
        AbstractButton[] group = OnlineGroup.Rows;
        Check("the Online group's rows", () => group.Length == 5, $"five rows, {group.Length} in");
        foreach (AbstractButton button in group)
        {
            string name = ObjectName(button);
            foreach (Wait wait in GroupTo(name))
            {
                yield return wait;
            }

            rows.Add(new KeyRow(name) { Arrows = Result(Cursor() == name, $"the cursor isn't on {name}") });
        }

        foreach (Wait wait in GroupTo("OnlineSettings"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("Enter on Online Settings", PadButtons.A, () => Settings() is { IsHiding: false } && ActiveTab() == OnlineIndex() && OnlineIndex() >= 0, "the game's Settings on its Online tab", within: OpenSeconds))
        {
            yield return wait;
        }

        KeyRow settingsRow = rows.Find(r => r.Name == "OnlineSettings") ?? new KeyRow("OnlineSettings");
        settingsRow.Enter = Result(_walkOk, "Settings didn't open on Online");
        foreach (Wait wait in WalkPress("Esc on Settings", PadButtons.B, () => Settings() == null && InGroup() && Cursor() == "OnlineSettings", "the Online group, the cursor on Online Settings", within: OpenSeconds))
        {
            yield return wait;
        }

        settingsRow.Escape = Result(_walkOk, "not back on the row");

        // The mouse: every row's centre; the pointer's click on Online Settings opens the same Settings.
        foreach (Wait wait in MouseOn())
        {
            yield return wait;
        }

        for (int i = 0; i < group.Length && i < rows.Count; i++)
        {
            rows[i].Hit = MouseHit(group[i].gameObject);
        }

        AbstractButton? online = Array.Find(group, b => ObjectName(b) == "OnlineSettings");
        if (online != null && _walkOk && _modeForced)
        {
            bool clicked = PointerClick(online);
            yield return Soft(() => Settings() is { IsHiding: false } && ActiveTab() == OnlineIndex(), OpenSeconds, "Settings by the click");
            settingsRow.Click = Result(clicked && Settings() != null && ActiveTab() == OnlineIndex(), "the click didn't open Settings on Online");
        }

        foreach (Wait wait in MouseOff())
        {
            yield return wait;
        }

        if (Settings() != null)
        {
            foreach (Wait wait in WalkPress("Esc on Settings opened by the click", PadButtons.B, () => Settings() == null && InGroup(), "the Online group", within: OpenSeconds))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("Esc on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }

        foreach (KeyRow row in rows)
        {
            SayRow("the Online menu", row, glyph);
        }
    }

    /// <summary>The Backups screen by the keys (Enter, the tab keys, the arrows, Esc on a restore's question) and by the mouse (its tabs, its rows, its bar's Back).</summary>
    private static IEnumerable<Wait> StepKeyBackups()
    {
        var rows = new List<KeyRow>();
        foreach (Wait wait in SettingsOnOnline())
        {
            yield return wait;
        }

        foreach (Wait wait in BackupsFromSettings())
        {
            yield return wait;
        }

        string glyph = Glyph();
        foreach (Wait wait in BackupsRead())
        {
            yield return wait;
        }

        foreach (Wait wait in BackupsRestoreCancelled())
        {
            yield return wait;
        }

        var restoreRow = new KeyRow("a backup's row") { Arrows = Result(_walkOk, "the rows weren't all reached"), Enter = Result(_walkOk, "no question"), Escape = Result(_walkOk, "the question wasn't cancelled") };
        rows.Add(restoreRow);

        // The mouse: the tabs (each clicked: the rows change), the rows of the tab shown, the bar's prompts; the bar's Back clicked: the screen closes.
        ShellParts? parts = LobbyShell.Parts;
        foreach (Wait wait in MouseOn())
        {
            yield return wait;
        }

        foreach (BackupTab tab in new[] { BackupTab.CoopRuns, BackupTab.Saves })
        {
            Transform? tabRoot = parts == null ? null : FindDeep(parts.Tabs, $"Tab{tab}");
            var tabRow = new KeyRow($"the {tab} tab") { Arrows = KeyWalk.NotPressed };
            tabRow.Hit = tabRoot == null ? "FAIL no tab" : MouseHit(tabRoot.gameObject);
            if (tabRoot != null && _walkOk && _modeForced)
            {
                bool clicked = UnselectableClick(tabRoot.gameObject);
                yield return Soft(() => BackupsScreen.Current?.Tab == tab, 2, $"the {tab} tab by the click");
                tabRow.Click = Result(clicked && BackupsScreen.Current?.Tab == tab, $"the click didn't show the {tab} tab");
            }

            rows.Add(tabRow);
        }

        // The rows as a mouse reaches them in a list longer than its view: a row cut by the list's edge by its visible strip, then each row brought into
        // view by the wheel's own handler and hit; the list dragged back by a drag's own handlers. The list is the Glossary's ScrollRect.
        foreach (Wait wait in BackupsRowsByMouse(parts, rows))
        {
            yield return wait;
        }

        Transform? confirm = parts == null ? null : FindDeep(parts.BottomBar, "PromptConfirm");
        if (confirm != null && confirm.gameObject.activeInHierarchy)
        {
            rows.Add(new KeyRow("the bar's Restore") { Arrows = KeyWalk.NotPressed, Hit = MouseHit(confirm.gameObject) });
        }

        Transform? back = parts?.BackShortcut.transform.parent;
        var backRow = new KeyRow("the bar's Back") { Arrows = KeyWalk.NotPressed, Hit = back == null ? "FAIL no Back" : MouseHit(back.gameObject) };
        if (back != null && _walkOk && _modeForced)
        {
            bool clicked = UnselectableClick(back.gameObject);
            yield return Soft(() => !LobbyShell.IsOpen && Settings() != null, OpenSeconds, "the Backups screen closed by the click");
            backRow.Click = Result(clicked && !LobbyShell.IsOpen && Settings() != null, "the click didn't close the screen");
        }

        rows.Add(backRow);
        foreach (Wait wait in MouseOff())
        {
            yield return wait;
        }

        yield return Soft(() => OnRow(SettingsRowId.SaveBackups), 2, "the cursor back on Save Backups");
        Check("after the Backups screen", () => !LobbyShell.IsOpen && Settings() != null, "Settings, the screen gone");
        if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Backups)
        {
            foreach (Wait wait in WalkPress("Esc on the Backups screen", PadButtons.B, () => !LobbyShell.IsOpen && Settings() != null, "Settings", within: OpenSeconds))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("Esc on Settings", PadButtons.B, () => Settings() == null && TitleIsBack(), "the title", within: OpenSeconds))
        {
            yield return wait;
        }

        foreach (KeyRow row in rows)
        {
            SayRow("Backups", row, glyph);
        }
    }

    /// <summary>
    /// The Backups list's rows by the mouse (the tab shown): the first row cut by an edge of the list's view hit by its visible strip; each row brought fully into the
    /// list's view by the wheel's own handler (<c>ScrollRect.OnScroll</c>, a notch at a time) and hit at its centre; then the list dragged back up by a drag's own
    /// handlers. One line each, and one for the wheel and one for the drag.
    /// </summary>
    private static IEnumerable<Wait> BackupsRowsByMouse(ShellParts? parts, List<KeyRow> rows)
    {
        ScrollRect? scroll = parts?.ScrollView.GetComponent<ScrollRect>();
        RectTransform? viewport = scroll == null ? null : scroll.viewport;
        IReadOnlyList<BackupRow> shownRows = BackupsScreen.Current?.Rows ?? Array.Empty<BackupRow>();
        if (parts == null || scroll == null || viewport == null)
        {
            rows.Add(new KeyRow("the list") { Hit = $"FAIL no {(parts == null ? "screen" : scroll == null ? "ScrollRect" : "viewport")}" });
            yield break;
        }

        var found = new List<Transform?>();
        for (int i = 0; i < shownRows.Count; i++)
        {
            found.Add(FindDeep(parts.ScrollContent, $"{BackupsScreen.Current?.Tab}{i}"));
        }

        // Where the list is now: the first row only partly in view, hit where it shows (a click there must land on it).
        Canvas.ForceUpdateCanvases();
        for (int i = 0; i < found.Count; i++)
        {
            RectTransform? cut = found[i]?.GetComponent<RectTransform>();
            if (cut != null && Overlap(cut, viewport) is { } part && !Inside(cut, viewport))
            {
                StepNote($"row {i + 1} is cut by the list's edge ({Percent(part, ScreenRect(cut))} of it in view)");
                rows.Add(new KeyRow("a row cut by the list's edge") { Hit = MouseHitAt(cut.gameObject, Centre(part)) });
                break;
            }
        }

        int notches = 0;
        int lastInView = -1;
        for (int i = 0; i < found.Count; i++)
        {
            RectTransform? row = found[i]?.GetComponent<RectTransform>();
            if (row == null)
            {
                rows.Add(new KeyRow($"row {i + 1}") { Hit = "FAIL no row" });
                continue;
            }

            int before = notches;
            for (int n = 0; n < MaxNotches && !Inside(row, viewport); n++)
            {
                // The wheel towards the row: a positive delta takes the list towards its top (ScrollRect turns a scroll's delta round), a negative one towards its end.
                float delta = ScreenRect(row).Max.y > ScreenRect(viewport).Max.y ? 1f : -1f;
                if (!Wheel(scroll, delta))
                {
                    break;
                }

                notches++;
                yield return Seconds(WheelSettleSeconds); // the list's elastic end settles between notches
                Canvas.ForceUpdateCanvases();
            }

            if (notches > before)
            {
                StepNote(string.Create(CultureInfo.InvariantCulture, $"the list by the wheel: row {i + 1} in view after {notches - before} notch(es); the list at {scroll.verticalNormalizedPosition:0.00}"));
            }

            bool inView = Inside(row, viewport);
            lastInView = inView ? i : lastInView;
            rows.Add(new KeyRow($"row {i + 1}") { Hit = inView ? MouseHit(row.gameObject) : "FAIL the wheel didn't bring it into view" });
        }

        bool needsWheel = found.Count > 0 && notches > 0;
        rows.Add(new KeyRow("the list by the wheel")
        {
            Hit = !needsWheel ? KeyWalk.NotPressed : lastInView == found.Count - 1 ? "ok" : $"FAIL the last row wasn't reached ({notches} notches)",
        });

        if (needsWheel)
        {
            // Back up by a drag on the list, as a left button held and moved down: the list moves down with it.
            string hit = MouseHitAt(scroll.gameObject, Centre(ScreenRect(viewport)));
            float before = scroll.content.anchoredPosition.y;
            bool dragged = false;
            foreach (Wait wait in Drag(scroll, viewport, ok => dragged = ok))
            {
                yield return wait;
            }

            yield return Seconds(WheelSettleSeconds);
            float after = scroll.content.anchoredPosition.y;
            StepNote(string.Create(CultureInfo.InvariantCulture, $"the list by dragging: the content from {before:0} to {after:0}; the list at {scroll.verticalNormalizedPosition:0.00}"));
            rows.Add(new KeyRow("the list by dragging") { Hit = hit, Click = Result(dragged && after < before - 1, "the drag didn't move the list") });
        }
    }

    private const int MaxNotches = 24;
    private const double WheelSettleSeconds = 0.25;

    /// <summary>One notch of a mouse wheel on <paramref name="scroll"/>, through its own handler (positive: towards the list's top; the handler moves the content by its sensitivity). False when it can't be made.</summary>
    private static bool Wheel(ScrollRect scroll, float delta)
    {
        try
        {
            EventSystem? system = EventSystem.current;
            if (system == null)
            {
                return false;
            }

            var data = new PointerEventData(system) { scrollDelta = new Vector2(0, delta) };
            scroll.OnScroll(data);
            return true;
        }
        catch (Exception ex)
        {
            StepNote($"a wheel notch threw {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>A left-button drag on <paramref name="scroll"/> from its view's centre a third of its height down, through the list's own drag handlers, a frame a step.</summary>
    private static IEnumerable<Wait> Drag(ScrollRect scroll, RectTransform viewport, Action<bool> done)
    {
        PointerEventData? data = null;
        try
        {
            EventSystem? system = EventSystem.current;
            if (system != null)
            {
                Vector2 start = Centre(ScreenRect(viewport));
                data = new PointerEventData(system) { button = PointerEventData.InputButton.Left, position = start, pressPosition = start };
                scroll.OnInitializePotentialDrag(data);
                scroll.OnBeginDrag(data);
            }
        }
        catch (Exception ex)
        {
            StepNote($"a drag threw {ex.GetType().Name}");
            data = null;
        }

        if (data == null)
        {
            done(false);
            yield break;
        }

        (Vector2 min, Vector2 max) = ScreenRect(viewport);
        float step = (max.y - min.y) / 9f;
        bool ok = true;
        for (int i = 1; i <= 3 && ok; i++)
        {
            yield return Frames(1);
            try
            {
                data.position = new Vector2(data.position.x, data.position.y - step);
                scroll.OnDrag(data);
            }
            catch (Exception ex)
            {
                StepNote($"a drag threw {ex.GetType().Name}");
                ok = false;
            }
        }

        yield return Frames(1);
        try
        {
            scroll.OnEndDrag(data);
        }
        catch (Exception ex)
        {
            StepNote($"a drag's end threw {ex.GetType().Name}");
            ok = false;
        }

        done(ok);
    }

    /// <summary>The Online menu's Report a Problem by Enter: the zip, the prompt; Esc closes it; then the row by the mouse.</summary>
    private static IEnumerable<Wait> StepKeyReport()
    {
        foreach (Wait wait in GroupRow("ReportAProblem"))
        {
            yield return wait;
        }

        var row = new KeyRow("ReportAProblem") { Arrows = Result(Cursor() == "ReportAProblem", "the cursor isn't on it") };
        string glyph = Glyph();
        foreach (Wait wait in ReportPress("Enter on Report a Problem", "offered"))
        {
            yield return wait;
        }

        row.Enter = Result(_walkOk, "no report or no prompt");
        row.Escape = Result(_walkOk && !LobbyPrompts.IsOpen, "the prompt didn't close");

        // The mouse after the report: the row itself (its object, not the cursor's: in the game's mouse mode the cursor is on nothing until the
        // pointer is on a row), with its own words back after "Making...".
        foreach (Wait wait in MouseOn())
        {
            yield return wait;
        }

        AbstractButton? report = Array.Find(OnlineGroup.Rows, b => ObjectName(b) == "ReportAProblem");
        row.Hit = report == null ? "FAIL no row" : MouseHit(report.gameObject);
        foreach (Wait wait in MouseOff())
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("Esc on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }

        SayRow("the Online menu", row, glyph);
    }

    /// <summary>Host Lobby by Enter: the arrows over the seats and the column, the tab keys on the mode; the mouse on what the cursor visited and the bar's mode glyphs; Esc closes it.</summary>
    private static IEnumerable<Wait> StepKeyLobby()
    {
        var rows = new List<KeyRow>();
        foreach (Wait wait in GroupRow("HostLobby"))
        {
            yield return wait;
        }

        if (!_walkOk)
        {
            yield break;
        }

        foreach (Wait wait in OpenLobby("Enter on Host Lobby"))
        {
            yield return wait;
        }

        string glyph = Glyph();
        var visited = new List<(KeyRow Row, AbstractNavigable? At)>();
        foreach ((PadButtons button, string what, LobbyFocus focus) in new[]
        {
            (PadButtons.DPadRight, "right", Seat(2)),
            (PadButtons.DPadLeft, "left", Seat(1)),
            (PadButtons.DPadLeft, "left, the host's own seat", Seat(0)), // a host's cursor reaches every seat, its own too (LobbyRules.Move), as the pad walk's
            (PadButtons.DPadLeft, "left, off the seats", Row(LobbyFocusKind.StartRow)),
            (PadButtons.DPadUp, "up", Row(LobbyFocusKind.InviteRow)),
            (PadButtons.DPadDown, "down", Row(LobbyFocusKind.StartRow)),
        })
        {
            foreach (Wait wait in Press(what, button, () => FocusIs(focus), Named(focus)))
            {
                yield return wait;
            }

            visited.Add((new KeyRow(Named(focus)) { Arrows = Result(FocusIs(focus), $"not on {Named(focus)}") }, Navigation().CurrentNavigable));
        }

        LobbyMode before = Sd().Actions.Snapshot().Mode;
        LobbyMode other = before == LobbyMode.Campaign ? LobbyMode.QuickGame : LobbyMode.Campaign;
        PadButtons there = before == LobbyMode.Campaign ? PadButtons.RightBumper : PadButtons.LeftBumper;
        PadButtons back = there == PadButtons.RightBumper ? PadButtons.LeftBumper : PadButtons.RightBumper;
        foreach (Wait wait in Press("the tab key", there, () => Sd().Actions.Snapshot().Mode == other, $"{other}"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("the other tab key", back, () => Sd().Actions.Snapshot().Mode == before, $"{before} again"))
        {
            yield return wait;
        }

        var modeRow = new KeyRow("the mode's tab keys") { Arrows = Result(Sd().Actions.Snapshot().Mode == before, "the mode isn't back") };

        // The mouse: what the cursor visited (seats, Invite, Start), and the bar's LB and RB glyphs clicked: the mode there and back.
        foreach (Wait wait in MouseOn())
        {
            yield return wait;
        }

        foreach ((KeyRow row, AbstractNavigable? at) in visited)
        {
            row.Hit = at == null ? "FAIL nothing selected" : MouseHit(at.gameObject);
            rows.Add(row);
        }

        ShellParts? parts = LobbyShell.Parts;
        string nearName = there == PadButtons.RightBumper ? "PromptTabRight" : "PromptTabLeft";
        string farName = there == PadButtons.RightBumper ? "PromptTabLeft" : "PromptTabRight";
        Transform? near = parts == null ? null : FindDeep(parts.BottomBar, nearName);
        Transform? far = parts == null ? null : FindDeep(parts.BottomBar, farName);

        // Each glyph by its own picture: LB's prompt is parented inside RB's ("[LB][RB] Game Mode"), so RB's hit must not land on LB's glyph.
        modeRow.Hit = near == null ? $"FAIL no {nearName}" : GlyphHit(near, far);
        var otherGlyph = new KeyRow($"the mode's {(farName == "PromptTabLeft" ? "LB" : "RB")} glyph") { Hit = far == null ? $"FAIL no {farName}" : GlyphHit(far, near) };
        if (near != null && far != null && _modeForced)
        {
            // A click on each, a frame apart: each press reads the session's mode, so the second goes back however soon it comes.
            bool first = UnselectableClick(near.gameObject);
            yield return Soft(() => Sd().Actions.Snapshot().Mode == other, 2, "the mode by the click");
            bool changed = Sd().Actions.Snapshot().Mode == other;
            bool second = UnselectableClick(far.gameObject);
            yield return Soft(() => Sd().Actions.Snapshot().Mode == before, 2, "the mode back by the click");
            modeRow.Click = Result(first && second && changed && Sd().Actions.Snapshot().Mode == before, $"the clicks didn't change the mode and back (changed {changed})");
        }

        rows.Add(modeRow);
        rows.Add(otherGlyph);
        foreach (Wait wait in MouseOff())
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("lobby", 6))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("Esc on the lobby, host alone", PadButtons.B, () => Sd().Actions.Snapshot().Role == LobbyRole.None && !LobbyShell.IsOpen && InGroup(), "no session, the Online group", within: ShellSeconds))
        {
            yield return wait;
        }

        modeRow.Escape = Result(Sd().Actions.Snapshot().Role == LobbyRole.None && !LobbyShell.IsOpen, "the lobby didn't close");
        foreach (Wait wait in WalkPress("Esc on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }

        foreach (KeyRow row in rows)
        {
            SayRow("the lobby", row, glyph);
        }
    }

    // ---- Stretches ----

    /// <summary>The cursor onto the Online group's <paramref name="name"/> by up and down.</summary>
    private static IEnumerable<Wait> GroupTo(string name)
    {
        AbstractButton[] group = OnlineGroup.Rows;
        int wanted = Array.FindIndex(group, b => ObjectName(b) == name);
        for (int i = 0; i < group.Length + 1 && _walkOk && InGroup() && Cursor() != name; i++)
        {
            string at = Cursor();
            int here = Array.FindIndex(group, b => ObjectName(b) == at);
            foreach (Wait wait in WalkTap(here >= 0 && here > wanted ? PadButtons.DPadUp : PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        WalkCheck($"the Online group's {name}", () => InGroup() && Cursor() == name, $"the cursor on {name}", gate: false);
    }

    /// <summary>The key an action is bound to, tapped as a person taps it; a FAIL (and no more presses in the step) when no plain key of this PC makes it.</summary>
    private static IEnumerable<Wait> KeyTap(KeyWalkAction? action)
    {
        WalkKeyboard? keys = _keys;
        int element = action == null || keys == null ? -1 : keys.Element(action);
        if (element < 0)
        {
            Fail($"no key of this PC's keyboard makes {action?.What ?? "that pad button's action"}");
            _walkOk = false;
            yield break;
        }

        keys!.Hold(element);
        yield return TapTime();
        keys.Let(element);
        yield return Frames(2);
    }

    /// <summary>A key and its expectation, said either way.</summary>
    private static IEnumerable<Wait> KeyPress(string what, KeyWalkAction action, Func<bool> done, string wanted, double within = 3)
    {
        foreach (Wait wait in KeyTap(action))
        {
            yield return wait;
        }

        yield return Soft(done, within, wanted);
        Check(what, done, wanted);
        yield return Seconds(WalkSettleSeconds);
    }

    // ---- The keyboard ----

    private static IEnumerable<Wait> MakeKeyboard()
    {
        if (_keys != null)
        {
            yield break;
        }

        ReInput.ConfigHelper? config = ReInput.configuration;
        if (config != null && !_focusSettingSaved)
        {
            // Monitors may be off, or the window behind others: Rewired would answer nothing (as for the pad walk's pad).
            _ignoredUnfocused = config.ignoreInputWhenAppNotInFocus;
            _focusSettingSaved = true;
            config.ignoreInputWhenAppNotInFocus = false;
        }

        _keys = WalkKeyboard.Make(out string problem);
        if (_keys == null)
        {
            Problem($"the virtual keyboard couldn't be made: {problem}");
            yield break;
        }

        Campaign.HostDecides.TourPadPlayer = _keys.RewiredId;
        StepNote($"the virtual keyboard: Rewired player {_keys.RewiredId}, {_keys.Describe()}; its presses don't trip the tour's abort, which reads the real keyboard, mouse and joysticks");
        yield return Frames(4);
    }

    /// <summary>The keyboard gone with its game player; the navigation's mode put back if a click step forced it. From any state (the tour's restore).</summary>
    private static void EndKeyWalk()
    {
        if (_modeForced)
        {
            ReleaseMouse();
        }

        WalkKeyboard? keys = _keys;
        _keys = null;
        if (keys == null)
        {
            return;
        }

        Campaign.HostDecides.TourPadPlayer = -1;
        try
        {
            keys.Dispose();
        }
        catch (Exception ex)
        {
            Line($"NOT RESTORED: the virtual keyboard didn't go cleanly ({ModLog.SafeErrorText(ex)}).");
        }

        Line($"The keyboard has gone: the UI's controlling player: {Controlling()}, the navigation mode {UiMode()}.");
    }

    // ---- The mouse ----

    /// <summary>The game's own mouse mode, forced as a mouse's movement makes it (the pointer handlers act only in it); said, and checked.</summary>
    private static IEnumerable<Wait> MouseOn()
    {
        UIInputSource? source = MenuGame.Module()?.InputSource;
        if (source == null)
        {
            Fail("the mouse: the game's UI input isn't there");
            yield break;
        }

        if ((source.SupportedNavigationModes & NavigationMode.Mouse) == 0)
        {
            StepNote($"the mouse: the game's UI doesn't take the mouse here (modes {source.SupportedNavigationModes}); hits only, no clicks");
            yield break;
        }

        _modeBefore = UiMode();
        _modeForced = true;
        source.SetForcedNavigationMode(NavigationMode.Mouse);
        yield return Frames(4);
        Canvas.ForceUpdateCanvases();
        StepNote($"the mouse: the game's navigation mode forced to Mouse (was {_modeBefore}); now {UiMode()}");
        if (UiMode() != NavigationMode.Mouse)
        {
            Fail($"the mouse: the game's navigation mode stayed {UiMode()}: clicks would do nothing");
            ReleaseMouse();
        }
    }

    private static IEnumerable<Wait> MouseOff()
    {
        if (!_modeForced)
        {
            yield break;
        }

        ReleaseMouse();
        yield return Frames(6);
        yield return Seconds(WalkSettleSeconds);
    }

    /// <summary>
    /// The mode back: the one before (forced for a moment, so every dispatcher takes it), then the force cleared, so each takes its last active player's
    /// own again where it has one (a custom controller reports none: verified in the game's code). With no mode before, a keyboard player's.
    /// </summary>
    private static void ReleaseMouse()
    {
        _modeForced = false;
        try
        {
            UIInputSource? source = MenuGame.Module()?.InputSource;
            if (source == null)
            {
                return;
            }

            NavigationMode back = _modeBefore is NavigationMode.Keyboard or NavigationMode.Controller or NavigationMode.Mouse ? _modeBefore : NavigationMode.Keyboard;
            if (back != NavigationMode.Mouse && (source.SupportedNavigationModes & back) != 0)
            {
                source.SetForcedNavigationMode(back);
            }

            source.ClearForcedNavigationMode();
            StepNote($"the mouse: the game's navigation mode back to {UiMode()} (was {_modeBefore} before the mouse)");
        }
        catch (Exception ex)
        {
            Line($"NOT RESTORED: the game's navigation mode couldn't be put back ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static NavigationMode UiMode()
    {
        try
        {
            return MenuGame.Module()?.InputSource?.ControllingPlayersOrKeyboardMouseInputEvents?.CurrentNavigationMode ?? NavigationMode.UNKNOWN;
        }
        catch (Exception)
        {
            return NavigationMode.UNKNOWN;
        }
    }

    /// <summary>What a pointer at <paramref name="target"/>'s centre hits first: its own name when that is it or a part of it, else what's over it.</summary>
    private static string MouseHit(GameObject target)
    {
        RectTransform? rect = target.GetComponent<RectTransform>();
        return rect == null ? "FAIL not a UI object" : MouseHitAt(target, ScreenCentre(rect));
    }

    /// <summary>
    /// What a pointer at <paramref name="point"/> (screen pixels) hits first: <paramref name="target"/>'s own name when that is it or a part of it (but not a part
    /// of <paramref name="notUnder"/>, a prompt parented inside it), else what's over it.
    /// </summary>
    private static string MouseHitAt(GameObject target, Vector2 point, Transform? notUnder = null)
    {
        try
        {
            EventSystem? system = EventSystem.current;
            if (system == null)
            {
                return "FAIL no event system";
            }

            Canvas.ForceUpdateCanvases();
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            var data = new PointerEventData(system) { position = point };
            system.RaycastAll(data, results);
            if (results.Count == 0)
            {
                return "FAIL nothing there";
            }

            GameObject? first = results[0].gameObject;
            string name = UiObjectName.ForLog(target.name);
            if (first != null && first.transform.IsChildOf(target.transform) && (notUnder == null || !first.transform.IsChildOf(notUnder)))
            {
                return name;
            }

            return $"FAIL blocked by {(first == null ? "nothing" : UiObjectName.ForLog(first.name))}";
        }
        catch (Exception ex)
        {
            return $"FAIL the raycast threw {ex.GetType().Name}";
        }
    }

    /// <summary>A bar prompt's glyph by the mouse: the centre of its own shortcut's picture must hit the prompt, and not <paramref name="other"/> when that is parented in it.</summary>
    private static string GlyphHit(Transform prompt, Transform? other)
    {
        RectTransform? glyph = prompt.Find("UIInputShortcut_V2")?.GetComponent<RectTransform>();
        if (glyph == null)
        {
            return "FAIL no glyph";
        }

        Transform? inside = other != null && other.IsChildOf(prompt) ? other : null;
        return MouseHitAt(prompt.gameObject, ScreenCentre(glyph), inside);
    }

    /// <summary><paramref name="rect"/> on screen, in pixels: its lower left and upper right corners.</summary>
    private static (Vector2 Min, Vector2 Max) ScreenRect(RectTransform rect)
    {
        var corners = new Il2CppStructArray<Vector3>(4);
        rect.GetWorldCorners(corners);
        Camera? camera = CameraOf(rect);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return (new Vector2(Math.Min(a.x, b.x), Math.Min(a.y, b.y)), new Vector2(Math.Max(a.x, b.x), Math.Max(a.y, b.y)));
    }

    /// <summary>The part of <paramref name="rect"/> inside <paramref name="clip"/> on screen, or null when none is.</summary>
    private static (Vector2 Min, Vector2 Max)? Overlap(RectTransform rect, RectTransform clip)
    {
        (Vector2 a0, Vector2 a1) = ScreenRect(rect);
        (Vector2 b0, Vector2 b1) = ScreenRect(clip);
        var min = new Vector2(Math.Max(a0.x, b0.x), Math.Max(a0.y, b0.y));
        var max = new Vector2(Math.Min(a1.x, b1.x), Math.Min(a1.y, b1.y));
        return max.x - min.x >= 1 && max.y - min.y >= 1 ? (min, max) : null;
    }

    /// <summary>True when <paramref name="rect"/> is wholly inside <paramref name="clip"/> on screen (a pixel and a half of rounding allowed).</summary>
    private static bool Inside(RectTransform rect, RectTransform clip)
    {
        const float Slack = 1.5f;
        (Vector2 a0, Vector2 a1) = ScreenRect(rect);
        (Vector2 b0, Vector2 b1) = ScreenRect(clip);
        return a0.x >= b0.x - Slack && a0.y >= b0.y - Slack && a1.x <= b1.x + Slack && a1.y <= b1.y + Slack;
    }

    private static Vector2 Centre((Vector2 Min, Vector2 Max) rect) => new((rect.Min.x + rect.Max.x) * 0.5f, (rect.Min.y + rect.Max.y) * 0.5f);

    /// <summary>How much of <paramref name="whole"/>'s height <paramref name="part"/> is, as a percentage.</summary>
    private static string Percent((Vector2 Min, Vector2 Max) part, (Vector2 Min, Vector2 Max) whole)
    {
        float height = whole.Max.y - whole.Min.y;
        return height <= 0 ? "-" : $"{100 * (part.Max.y - part.Min.y) / height:0} %";
    }

    /// <summary>The camera a canvas draws <paramref name="rect"/> with: none for an overlay canvas.</summary>
    private static Camera? CameraOf(RectTransform rect)
    {
        Canvas? canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }

    /// <summary>A row's own pointer enter and click (the game's <c>AbstractButton</c> handlers, acting only in mouse mode). False when it can't be sent.</summary>
    private static bool PointerClick(AbstractNavigable row)
    {
        try
        {
            AbstractButton? button = row.TryCast<AbstractButton>();
            EventSystem? system = EventSystem.current;
            RectTransform? rect = row.GetComponent<RectTransform>();
            if (button == null || system == null || rect == null)
            {
                return false;
            }

            var data = new PointerEventData(system) { position = ScreenCentre(rect) };
            button.OnPointerEnter(data);
            button.OnPointerClick(data);
            return true;
        }
        catch (Exception ex)
        {
            StepNote($"a click threw {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// A bar shortcut's or a tab's click as its pointer handler makes it (the game's <c>UnselectableButtonBase</c>: <c>Click</c> only in mouse mode; its
    /// handler is an explicit interface member, so the same check is made here). False when it can't be made.
    /// </summary>
    private static bool UnselectableClick(GameObject root)
    {
        try
        {
            UnselectableButtonBase? button = root.GetComponent<UnselectableButtonBase>();
            if (button == null || button.m_Mode != NavigationMode.Mouse || !button.IsClickable)
            {
                StepNote($"{UiObjectName.ForLog(root.name)}: no click (button {(button == null ? "none" : $"in mode {button.m_Mode}, clickable {button.IsClickable}")})");
                return false;
            }

            button.Click();
            return true;
        }
        catch (Exception ex)
        {
            StepNote($"a click threw {ex.GetType().Name}");
            return false;
        }
    }

    private static Vector2 ScreenCentre(RectTransform rect)
    {
        var corners = new Il2CppStructArray<Vector3>(4);
        rect.GetWorldCorners(corners);
        Vector3 middle = (corners[0] + corners[2]) * 0.5f;
        return RectTransformUtility.WorldToScreenPoint(CameraOf(rect), middle);
    }

    private static Transform? FindDeep(Transform? parent, string name)
    {
        if (parent == null)
        {
            return null;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
            {
                return child;
            }

            Transform? deeper = FindDeep(child, name);
            if (deeper != null)
            {
                return deeper;
            }
        }

        return null;
    }

    // ---- What's said ----

    /// <summary>The device the UI's glyphs are drawn for and the sprite of the first glyph on screen: "Keyboard_Enter (MouseKeyboard)".</summary>
    private static string Glyph()
    {
        string device = "unknown";
        try
        {
            if (MenuGame.Module()?.InputSpriteMapper is { } mapper)
            {
                device = mapper.GetCurrentDeviceType(-1).ToString();
            }

            GameObject? root = Top()?.gameObject ?? CampaignGame.Title()?.gameObject;
            if (root != null)
            {
                foreach (Component? found in root.GetComponentsInChildren(Il2CppType.Of<InputSpriteUI>(), false))
                {
                    UnityEngine.UI.Image? image = found?.TryCast<InputSpriteUI>()?.m_Image;
                    if (image != null && image.isActiveAndEnabled && image.sprite != null)
                    {
                        return $"{SafeText.Clean(image.sprite.name, 40)} ({device})";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return $"unreadable ({ex.GetType().Name}; {device})";
        }

        return $"none on screen ({device})";
    }

    /// <summary>
    /// True when the game draws the UI's glyphs for a pad (what the keyboard walk must never see), false for another device;
    /// null when the game's sprite mapper can't be read, which proves neither (a check of it fails).
    /// </summary>
    private static bool? GlyphIsPad()
    {
        try
        {
            return MenuGame.Module()?.InputSpriteMapper is { } mapper ? VirtualPadCompat.IsGamepad(mapper.GetCurrentDeviceType(-1)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Result(bool ok, string why) => ok ? "ok" : $"FAIL {why}";

    private static void SayRow(string screen, KeyRow row, string glyph)
    {
        string line = KeyWalk.Line(screen, row.Name, row.Arrows, row.Enter, row.Escape, glyph, row.Hit, row.Click);
        StepNote(line);
        foreach (string field in new[] { row.Arrows, row.Enter, row.Escape, row.Hit, row.Click })
        {
            if (field.StartsWith("FAIL", StringComparison.Ordinal))
            {
                _stepProblem ??= line;
                break;
            }
        }
    }

    /// <summary>One walked row's results, for its line.</summary>
    private sealed class KeyRow
    {
        public KeyRow(string name) => Name = name;

        public string Name { get; }

        public string Arrows { get; set; } = KeyWalk.NotPressed;

        public string Enter { get; set; } = KeyWalk.NotPressed;

        public string Escape { get; set; } = KeyWalk.NotPressed;

        public string Hit { get; set; } = KeyWalk.NotPressed;

        public string Click { get; set; } = KeyWalk.NotPressed;
    }

    /// <summary>
    /// The tour's keyboard: a <see cref="VirtualKeyboard"/> with this PC's own keyboard maps, fed at Rewired's own moment for custom controllers, as a
    /// friend's is; the key of an action looked up in its table (<see cref="KeyWalk.Pick"/>).
    /// </summary>
    private sealed class WalkKeyboard : IDisposable
    {
        private readonly VirtualKeyboard _keyboard;
        private readonly InputBindingSet _controls;
        private readonly int _uiCategory;
        private Il2CppSystem.Action? _hook;
        private ulong _held;
        private bool _faulted;
        private int _gamePlayer = -1;

        private WalkKeyboard(VirtualKeyboard keyboard, InputBindingSet controls, int uiCategory)
        {
            _keyboard = keyboard;
            _controls = controls;
            _uiCategory = uiCategory;
        }

        public int RewiredId => _keyboard.RewiredPlayerId;

        public int GamePlayer => _gamePlayer;

        public static WalkKeyboard? Make(out string problem)
        {
            LocalKeyboard? local = LocalKeyboard.Find(1, out problem);
            if (local == null)
            {
                return null;
            }

            VirtualKeyboard? keyboard = VirtualKeyboard.Create("T2TOnline tour keyboard", -1, null, null, out problem);
            if (keyboard == null)
            {
                return null;
            }

            try
            {
                keyboard.ApplyControls(local.Controls);
                var walk = new WalkKeyboard(keyboard, local.Controls, ReInput.mapping.GetMapCategoryId("UI"));
                walk._hook = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(walk.OnInputSourceUpdate));
                ReInput.add_InputSourceUpdateEvent(walk._hook);
                return walk;
            }
            catch
            {
                keyboard.Dispose();
                throw;
            }
        }

        /// <summary>The key-table element that makes <paramref name="action"/> (the join in any category; the menus' actions in the UI category), or -1.</summary>
        public int Element(KeyWalkAction action) => KeyWalk.Pick(_controls, action.ActionId == KeyWalk.Activate ? -1 : _uiCategory, action);

        /// <summary>The walk's keys by name, for the log.</summary>
        public string Describe()
        {
            string Key(KeyWalkAction? action)
            {
                int element = action == null ? -1 : Element(action);
                return element < 0 ? "none" : KeyWalk.KeyName(_controls.Elements[element].Code);
            }

            return $"{_controls.Elements.Count} keys from this PC's own maps ({_controls.Source}); join {Key(KeyWalk.Join)}, Enter {Key(KeyWalk.For(PadButtons.A))}, Esc {Key(KeyWalk.For(PadButtons.B))}, " +
                $"arrows {Key(KeyWalk.For(PadButtons.DPadUp))} {Key(KeyWalk.For(PadButtons.DPadDown))} {Key(KeyWalk.For(PadButtons.DPadLeft))} {Key(KeyWalk.For(PadButtons.DPadRight))}, " +
                $"tabs {Key(KeyWalk.For(PadButtons.LeftBumper))} {Key(KeyWalk.For(PadButtons.RightBumper))}";
        }

        public void Hold(int element) => _held |= 1UL << element;

        public void Let(int element) => _held &= ~(1UL << element);

        /// <summary>Every frame: the game's join poll for a custom controller, relayed (<see cref="GameJoin"/>).</summary>
        public void Tick()
        {
            if (_faulted)
            {
                return;
            }

            int now = GameJoin.TryRelay(_keyboard, "Keyboard walk");
            if (now != _gamePlayer)
            {
                _gamePlayer = now;
                ModLog.Info($"Lobby check: the keyboard is game player {now}.");
            }
        }

        private void OnInputSourceUpdate()
        {
            if (_faulted)
            {
                return;
            }

            try
            {
                _keyboard.Apply(new KeyboardMouseState(_held, 0, 0), 0);
            }
            catch (Exception ex)
            {
                _faulted = true;
                ModLog.Warn($"Lobby check: the keyboard's input failed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        public void Dispose()
        {
            if (_hook != null)
            {
                ReInput.remove_InputSourceUpdateEvent(_hook);
                _hook = null;
            }

            _keyboard.ApplyNeutral();
            int rewired = _keyboard.RewiredPlayerId;
            int gamePlayer = RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(rewired) : -1;
            Il2CppTape2Tape.Player? player = gamePlayer >= 0 ? Il2Cpp.PlayersManager.Instance?.GetPlayerFromRewired(_keyboard.Player) : null;
            if (player != null)
            {
                RewiredPlayerInputAssigner.SetControlsToActivationMode(rewired);
                Il2Cpp.PlayersManager.Instance!.RemovePlayer(player);
            }

            _keyboard.Dispose();
        }
    }
}
#endif
