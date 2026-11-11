#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using MelonLoader.Utils;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Startup;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

// The Settings walks (padwalk's pw_settings, pw_online_settings, pw_backups, pw_report, and pw_quick's match; kbwalk's kb_* steps press the same way
// through the keyboard): the game's Settings menu with its Online tab (NativeUi/SettingsTab.cs), the Backups screen over it (NativeUi/BackupsScreen.cs)
// and the problem report's rows (NativeUi/ReportFlow.cs), pressed by the tour's pad (or keys) as a person presses them. Each press's effect is asserted
// once; a FAIL line says what was expected and what is on screen instead. A failed expectation a later press depends on ends the step's presses (a
// press on the wrong screen would act on whatever is there: a setting, the title's back), and every step starts and ends on the title's Main group,
// reached by the pad. Nothing here opens Steam's overlay or presses Direct Connections, End Online Game or a restore's confirm; the tour's holds keep
// every setting, save and backup as it was.
internal static partial class LobbyUiCheck
{
    // MatchLoadSeconds and MainRows (the title's Main rows in order) are the replay tour's, shared (LobbyUiCheck.Replay.cs).
    private const double OpenSeconds = 6;
    private const double ReportSeconds = 90;
    private const int MostRbs = 12;
    private const int MostHomePresses = 10;

    // The Backups screen takes A only after its arrival half second (the press that opened it isn't a choice on it).
    private const double BackupsArrivalSeconds = LobbyTiming.InputLockMicros / 1_000_000.0 + 0.2;

    // False once an expectation a later press depends on has failed in this step: no press after it.
    private static bool _walkOk = true;

    // The title Settings' grafts after the walk's first open: every later title open must reuse that instance.
    private static int _titleGrafts = -1;

    private static void AddSettingsWalkSteps()
    {
        Steps["pw_settings"] = () => HomeStep(StepSettingsWalk);
        Steps["pw_online_settings"] = () => HomeStep(StepOnlineSettingsWalk);
        Steps["pw_backups"] = () => HomeStep(StepBackupsWalk);
        Steps["pw_report"] = () => HomeStep(StepReportWalk);
    }

    /// <summary>A step from the title's Main and back to it, whatever happened in between: one step's failure can't move the next one's start.</summary>
    private static IEnumerable<Wait> HomeStep(Func<IEnumerable<Wait>> body)
    {
        foreach (Wait wait in TitleHome())
        {
            yield return wait;
        }

        _walkOk = OnMain();
        if (!_walkOk)
        {
            Fail($"the step can't start: the title's Main isn't up; {SettingsWhere()}");
            yield break;
        }

        try
        {
            foreach (Wait wait in body())
            {
                yield return wait;
            }
        }
        finally
        {
            // A step that ended between the keyboard walk's MouseOn and MouseOff (it threw, or the tour stopped) leaves no
            // forced mouse mode to the steps after it, whose presses would otherwise meet the game's mouse mode.
            if (_modeForced)
            {
                ReleaseMouse();
            }
        }

        if (!_walkOk)
        {
            StepNote("an expectation failed: no press after it; back to the title's Main");
        }

        foreach (Wait wait in TitleHome())
        {
            yield return wait;
        }

        if (!OnMain())
        {
            Fail($"the step ended away from the title's Main; {SettingsWhere()}");
        }
    }

    // ---- The steps ----

    /// <summary>The title's Settings by its row, to Online, every shown row, the toggles and the choice changed and put back, back to the title.</summary>
    private static IEnumerable<Wait> StepSettingsWalk()
    {
        foreach (Wait wait in SettingsFromTitle("A on Settings"))
        {
            yield return wait;
        }

        _titleGrafts = SettingsTab.Grafted;
        WalkCheck("Settings from the title", () => ActiveTab() == 0, "open on Video (tab 1)");
        WalkCheck("the tabs", () => TabCount() == 8 && OnlineIndex() == 7 && Settings() is { } menu && SettingsTab.OnlineTabs(menu) == 1, "eight tabs, Online the eighth and only one");
        StepNote($"the tab: {SettingsTab.Grafted} instance(s) grafted so far, off: {SettingsTab.OffReason ?? "no"}");
        foreach (Wait wait in RbToOnline())
        {
            yield return wait;
        }

        // The panel's intro (the game's Panel_Ctrl, triggered by its SelectSubMenu): its alpha just after and at rest.
        GameObject? panel = Settings() is { } open ? SettingsTab.Panel(open) : null;
        CanvasGroup? fade = panel?.GetComponent<CanvasGroup>();
        float early = fade?.alpha ?? -1;
        yield return Seconds(0.8);
        float rest = fade?.alpha ?? -1;
        Animator? animator = panel?.GetComponent<Animator>();
        StepNote(string.Create(CultureInfo.InvariantCulture, $"the panel: active {panel?.activeInHierarchy}, alpha {early:0.00} just after the tab button, {rest:0.00} at rest, animator initialized {animator?.isInitialized}"));
        WalkCheck("the panel", () => panel != null && panel.activeInHierarchy && rest > 0.95f && animator != null && animator.isInitialized, "the Online panel shown by the game's animator", gate: false);

        // The panel keeps its last row between opens, as the game's own panels do: the first row by up.
        yield return Soft(() => RowAt() >= 0, 2, "the cursor on a row of the Online tab");
        foreach (Wait wait in ToRow(SettingsRowId.ShareAchievements))
        {
            yield return wait;
        }

        string wantedTip = LobbyLocalization.Say(SettingsTabWords.ShareAchievements);
        WalkCheck("the tooltip box", () => TooltipText().Contains(wantedTip, StringComparison.Ordinal), $"the game's tooltip box showing \"{wantedTip}\" (it shows \"{TooltipText()}\")", gate: false);
        foreach (Wait wait in Snap("online_tab", 10))
        {
            yield return wait;
        }

        // Every shown row, top to bottom by down, each one reached (Direct Connections too when the rule shows it: reached, never pressed).
        foreach (SettingsRowId id in ShownRows())
        {
            foreach (Wait wait in ToRow(id))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkObserve("down on the last row", PadButtons.DPadDown))
        {
            yield return wait;
        }

        foreach (Wait wait in ToRow(SettingsRowId.ShareAchievements))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkObserve("up on the first row", PadButtons.DPadUp))
        {
            yield return wait;
        }

        foreach (Wait wait in TogglesAndChoice())
        {
            yield return wait;
        }

        int online = OnlineIndex();
        foreach (Wait wait in WalkPress("LB on Online", PadButtons.LeftBumper, () => ActiveTab() == online - 1, "Stats"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("RB on Stats", PadButtons.RightBumper, () => ActiveTab() == online, "Online"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("RB on Online, the last tab", PadButtons.RightBumper, () => ActiveTab() == online, "still Online (the game's bar stops at its end)"))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseSettingsToTitle("Settings"))
        {
            yield return wait;
        }
    }

    /// <summary>Both toggles pressed twice and the choice moved and back, all held in memory; the rows a person must never press only checked.</summary>
    private static IEnumerable<Wait> TogglesAndChoice()
    {
        bool start = ModSettings.ShareAchievements;
        string fileAtStart = SettingsHash();
        int heldAtStart = SettingsHeld();
        WalkCheck("the toggle shows the setting", () => ShareShows() == start, start ? "on" : "off", gate: false);
        foreach (Wait wait in ToRow(SettingsRowId.ShareAchievements))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on Share Achievements", PadButtons.A, () => ModSettings.ShareAchievements != start && ShareShows() != start, start ? "off" : "on"))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("share_pressed", 4))
        {
            yield return wait;
        }

        WalkCheck("the cursor before A again", () => OnRow(SettingsRowId.ShareAchievements), "on Share Achievements");
        foreach (Wait wait in WalkPress("A on Share Achievements again", PadButtons.A, () => ModSettings.ShareAchievements == start && ShareShows() == start, start ? "on again" : "off again"))
        {
            yield return wait;
        }

        bool progress = ModSettings.ShareProgress;
        foreach (Wait wait in ToRow(SettingsRowId.ShareProgress))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on Share Progress", PadButtons.A, () => ModSettings.ShareProgress != progress, progress ? "off" : "on"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on Share Progress again", PadButtons.A, () => ModSettings.ShareProgress == progress, progress ? "on again" : "off again"))
        {
            yield return wait;
        }

        // The choice by the list's own left and right (its dead ends: the row has no left or right neighbour), and back.
        OnlineInputChoice input = ModSettings.OnlineInput;
        foreach (Wait wait in ToRow(SettingsRowId.PlayOnlineWith))
        {
            yield return wait;
        }

        WalkCheck("the choice shows the setting", () => Settings() is { } shown && SettingsTab.ChoiceIndex(shown) == (int)input, $"{input}", gate: false);
        PadButtons away = input == OnlineInputChoice.KeyboardMouse ? PadButtons.DPadLeft : PadButtons.DPadRight;
        PadButtons home = away == PadButtons.DPadLeft ? PadButtons.DPadRight : PadButtons.DPadLeft;
        foreach (Wait wait in WalkPress($"{(away == PadButtons.DPadLeft ? "left" : "right")} on Play Online With", away, () => ModSettings.OnlineInput != input, "another choice"))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("choice_moved", 4))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("and back", home, () => ModSettings.OnlineInput == input, $"{input} again"))
        {
            yield return wait;
        }

        // Never pressed by a tour: Direct Connections is only checked shown or not by the rule (and reached above); Leave and End only in a session.
        bool directWanted = ModSettings.AllowDirectConnections || NativeUiLayer.RelayRefused;
        WalkCheck("Direct Connections", () => Settings() is { } menu && SettingsTab.Shows(menu, SettingsRowId.DirectConnections) == directWanted,
            directWanted ? "shown (it's on, or Steam refused relay only here): reached by the cursor above, never pressed" : "not shown (it's off and Steam kept relay only)", gate: false);
        WalkCheck("Leave and End", () => Settings() is { } menu && !SettingsTab.Shows(menu, SettingsRowId.LeaveOnlineGame) && !SettingsTab.Shows(menu, SettingsRowId.EndOnlineGame),
            "not shown out of a session", gate: false);

        // A tour's settings changes are held in memory: the file is never written, by the presses or by the change back.
        WalkCheck("the settings file", () => SettingsHash() == fileAtStart, $"untouched (the tour holds settings in memory: {TourHolds.On})", gate: false);
        WalkCheck("the settings held", () => !TourHolds.On || SettingsHeld() >= heldAtStart + 6, $"six more held in memory (held {heldAtStart} at the start, {SettingsHeld()} now)", gate: false);
    }

    /// <summary>The Online group's Online Settings: Settings on the Online tab; back on the row; the title's own Settings on Video; and the other order.</summary>
    private static IEnumerable<Wait> StepOnlineSettingsWalk()
    {
        int grafts = SettingsTab.Grafted;
        foreach (Wait wait in SettingsFromRow())
        {
            yield return wait;
        }

        if (_walkOk)
        {
            foreach (Wait wait in Snap("from_online_settings", 10))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("B on Settings", PadButtons.B, () => Settings() == null && InGroup() && Cursor() == "OnlineSettings", "the Online group, the cursor on Online Settings", within: OpenSeconds))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("B on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }

        // The request was taken once: the title's own Settings is on Video again; then the row again after a plain open.
        foreach (Wait wait in SettingsFromTitle("A on Settings, after the row"))
        {
            yield return wait;
        }

        WalkCheck("the title's Settings after the row", () => ActiveTab() == 0, "on Video: the request didn't leak");
        foreach (Wait wait in CloseSettingsToTitle("Settings"))
        {
            yield return wait;
        }

        foreach (Wait wait in SettingsFromRow())
        {
            yield return wait;
        }

        foreach (Wait wait in CloseSettingsToTitle("OnlineSettings"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("B on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }

        StepNote($"grafts during the step: {SettingsTab.Grafted - grafts} (the title's Settings is one retained instance)");
        WalkCheck("one graft for every title open", () => _titleGrafts < 0 || SettingsTab.Grafted == _titleGrafts,
            $"no graft since the walk's first title open ({SettingsTab.Grafted - _titleGrafts} more)", gate: false);
    }

    /// <summary>
    /// Save Backups: the screen over Settings, both tabs and every row read, one row's Restore asked and answered Cancel, back on the row and on Save
    /// Backups. Nothing restored (the screen and the restore both refuse while a tour holds), nothing backed up (no press reaches that).
    /// </summary>
    private static IEnumerable<Wait> StepBackupsWalk()
    {
        List<string> before = BackupNames();
        foreach (Wait wait in SettingsOnOnline())
        {
            yield return wait;
        }

        foreach (Wait wait in BackupsFromSettings())
        {
            yield return wait;
        }

        foreach (Wait wait in BackupsRead())
        {
            yield return wait;
        }

        foreach (Wait wait in BackupsRestoreCancelled())
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("B on the Backups screen", PadButtons.B, () => !LobbyShell.IsOpen && Settings() != null && OnRow(SettingsRowId.SaveBackups), "Settings, the cursor back on Save Backups", within: OpenSeconds))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseSettingsToTitle("Settings"))
        {
            yield return wait;
        }

        WalkCheck("nothing restored, nothing backed up", () => !SaveBackups.RestartNeeded && BackupNames().SequenceEqual(before, StringComparer.OrdinalIgnoreCase),
            $"no restore waiting for a restart, the {before.Count} backup folder(s) as they were", gate: false);
    }

    /// <summary>A on Save Backups (the cursor on it): the Backups screen over Settings, its arrival half second waited out.</summary>
    private static IEnumerable<Wait> BackupsFromSettings()
    {
        foreach (Wait wait in ToRow(SettingsRowId.SaveBackups))
        {
            yield return wait;
        }

        WalkCheck("Save Backups at the title", () => Settings() is { } menu && SettingsTab.BackupsLocked(menu) == false, "not locked (the game's lock opens at the title)");
        foreach (Wait wait in WalkPress("A on Save Backups", PadButtons.A, () => LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Backups && BackupsScreen.Current != null, "the Backups screen over Settings", within: OpenSeconds))
        {
            yield return wait;
        }

        if (_walkOk)
        {
            StepNote(string.Create(CultureInfo.InvariantCulture, $"over Settings: the shell {LobbyShell.Id}, the title's menu list {string.Join(", ", MenusOver())}, the fade {Title().m_FadeToBlack?.alpha:0.##}"));
            yield return Seconds(BackupsArrivalSeconds);
        }
    }

    /// <summary>Both tabs by the band's own LB and RB, every row by down with what it restores; a picture of each tab.</summary>
    private static IEnumerable<Wait> BackupsRead()
    {
        foreach (BackupTab tab in new[] { BackupTab.Saves, BackupTab.CoopRuns })
        {
            if (!_walkOk)
            {
                yield break;
            }

            if (BackupsScreen.Current?.Tab != tab)
            {
                PadButtons bumper = tab == BackupTab.CoopRuns ? PadButtons.RightBumper : PadButtons.LeftBumper;
                foreach (Wait wait in WalkPress($"{(bumper == PadButtons.RightBumper ? "RB" : "LB")} on the Backups screen", bumper, () => BackupsScreen.Current?.Tab == tab, $"the {tab} tab"))
                {
                    yield return wait;
                }
            }

            IReadOnlyList<BackupRow> rows = BackupsScreen.Current?.Rows ?? Array.Empty<BackupRow>();
            StepNote($"Backups: {tab}: {rows.Count} row(s){(rows.Count == 0 ? ": the screen says there are none" : string.Empty)}");
            for (int i = 0; i < rows.Count && _walkOk; i++)
            {
                string wanted = $"{tab}{i}";
                if (i > 0)
                {
                    foreach (Wait wait in WalkPress($"down to row {i + 1}", PadButtons.DPadDown, () => Cursor() == wanted, wanted))
                    {
                        yield return wait;
                    }
                }
                else
                {
                    yield return Soft(() => Cursor() == wanted, 1, wanted);
                    WalkCheck("the tab's first row", () => Cursor() == wanted, wanted);
                }

                StepNote($"Backups: {tab} row {i + 1}: {SaveBackupNames.ForLog(rows[i].Backup)} ({rows[i].PartForLog}, {rows[i].Files} file(s))");
            }

            foreach (Wait wait in Snap($"backups_{tab.ToString().ToLowerInvariant()}", 4))
            {
                yield return wait;
            }
        }
    }

    /// <summary>A on a row (the first tab with rows, its first row): the game's yes/no, Cancel first; B: cancelled, the cursor back on the row.</summary>
    private static IEnumerable<Wait> BackupsRestoreCancelled()
    {
        BackupsScreen? screen = BackupsScreen.Current;
        BackupTab? with = screen == null ? null : screen.Rows.Count > 0 ? screen.Tab : null;
        if (!_walkOk || screen == null)
        {
            yield break;
        }

        // The tab shown last first (no press to change it); else the other.
        if (with == null)
        {
            BackupTab other = screen.Tab == BackupTab.Saves ? BackupTab.CoopRuns : BackupTab.Saves;
            PadButtons bumper = other == BackupTab.CoopRuns ? PadButtons.RightBumper : PadButtons.LeftBumper;
            foreach (Wait wait in WalkPress($"the other tab, for a row", bumper, () => BackupsScreen.Current?.Tab == other, $"the {other} tab"))
            {
                yield return wait;
            }

            with = BackupsScreen.Current is { Rows.Count: > 0 } ? other : null;
        }

        if (with == null)
        {
            StepNote("no backup on this PC: no restore to ask about (the empty state is pictured)");
            yield break;
        }

        // Up to the first row, then A: the question, its safe answer focused.
        string first = $"{with}0";
        for (int i = 0; i < 12 && _walkOk && BackupsScreen.Current != null && Cursor() != first; i++)
        {
            string at = Cursor();
            foreach (Wait wait in WalkTap(PadButtons.DPadUp))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(0.15);
        }

        WalkCheck("the cursor on the first row", () => Cursor() == first, first);
        foreach (Wait wait in WalkPress("A on a backup's row", PadButtons.A, () => BackupsScreen.Current is { Asking: true } && LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, "the game's yes/no: restore it?", within: 9))
        {
            yield return wait;
        }

        if (_walkOk)
        {
            foreach (Wait wait in Snap("backups_restore_question", 8))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("B on the question", PadButtons.B, () => BackupsScreen.Current is { Asking: false } && !LobbyPrompts.IsOpen && Cursor() == first, "Cancel: the cursor back on the row", within: 5))
        {
            yield return wait;
        }
    }

    /// <summary>A report from Settings > Online's Problem Report, then from the Online menu's Report a Problem: each the game's prompt with its zip, closed by B.</summary>
    private static IEnumerable<Wait> StepReportWalk()
    {
        foreach (Wait wait in SettingsOnOnline())
        {
            yield return wait;
        }

        foreach (Wait wait in ToRow(SettingsRowId.ProblemReport))
        {
            yield return wait;
        }

        foreach (Wait wait in ReportPress("A on Problem Report", "offered"))
        {
            yield return wait;
        }

        WalkCheck("back on Settings", () => Settings() != null && OnRow(SettingsRowId.ProblemReport), "the cursor on Problem Report");
        foreach (Wait wait in CloseSettingsToTitle("Settings"))
        {
            yield return wait;
        }

        foreach (Wait wait in GroupRow("ReportAProblem"))
        {
            yield return wait;
        }

        foreach (Wait wait in ReportPress("A on Report a Problem", "offered"))
        {
            yield return wait;
        }

        // A prompt closed over a title group leaves the cursor to the game's own repair of the selection, which asks the group: the game's Play, Campaign,
        // Practice and Customize groups, and the Online group cloned from Play, select their default row (NavigableParentSelectDefault; the Campaign group's New
        // Run question answered No lands on that group's first row, not on New Run; verified in the game's code). On the Online group that is Host Lobby, so
        // the report's flow hands the cursor back to the row that asked, a frame after the answer (NativeUi/ReportFlow.cs): the wait covers the game's repair
        // (a frame after the prompt's button has gone) and that select, in either order.
        yield return Soft(() => InGroup() && Cursor() == "ReportAProblem", 2, "the cursor back on Report a Problem");
        WalkCheck("back on the Online group", () => InGroup() && Cursor() == "ReportAProblem", "the cursor on Report a Problem");
        foreach (Wait wait in WalkPress("B on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }
    }

    /// <summary>From Main: A on Online, then down to the group's <paramref name="row"/>.</summary>
    private static IEnumerable<Wait> GroupRow(string row)
    {
        foreach (Wait wait in MainTo("Online"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on Online", PadButtons.A, () => InGroup() && Cursor() == GroupFocus(), "the Online group"))
        {
            yield return wait;
        }

        for (int i = 0; i < 5 && _walkOk && InGroup() && Cursor() != row; i++)
        {
            string at = Cursor();
            foreach (Wait wait in WalkTap(PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(0.3);
        }

        WalkCheck($"the Online group's {row}", () => InGroup() && Cursor() == row, $"the cursor on {row}");
    }

    /// <summary>A press on a report's row: the zip made, the game's prompt naming it (Show File <paramref name="showFile"/>), B: closed.</summary>
    private static IEnumerable<Wait> ReportPress(string what, string showFile)
    {
        int before = BugReports.Finished;
        foreach (Wait wait in WalkPress(what, PadButtons.A, () => BugReports.Finished > before, "a report made", within: ReportSeconds))
        {
            yield return wait;
        }

        if (!_walkOk)
        {
            yield break;
        }

        StepNote($"report: {BugReports.Last.Name} ({BugReports.Last.Summary})");
        yield return Soft(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking && ReportFlow.LastShowFile == showFile, 10, "the report's prompt");
        WalkCheck("the report's prompt", () => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking && ReportFlow.LastShowFile == showFile, $"up, Show File {showFile} (it says: {ReportFlow.LastShowFile})");
        if (_walkOk)
        {
            foreach (Wait wait in Snap("report_prompt", 8))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("B on the report's prompt", PadButtons.B, () => !LobbyPrompts.IsOpen, "Close", within: 5))
        {
            yield return wait;
        }
    }

    /// <summary>
    /// In <c>pw_quick</c>'s match (host in a session): the pause menu's Settings > Online (Save Backups locked with its tooltip and refused, End Online Game
    /// shown and reached, never pressed), a report's one-button prompt, and My Settings' look on this pause menu; the match resumed. The match's quit follows.
    /// </summary>
    private static IEnumerable<Wait> InMatchSettings()
    {
        if (Top()?.TryCast<GameMenu>() == null)
        {
            foreach (Wait wait in WalkPress("Start in the match", PadButtons.Start, () => Top()?.TryCast<GameMenu>() != null, "the pause menu", within: 4))
            {
                yield return wait;
            }
        }

        AbstractButton? button = _walkOk ? PauseSettingsButton() : null;
        WalkCheck("the pause menu's Settings button", () => button != null, "there");
        for (int i = 0; i < 8 && _walkOk && Top()?.TryCast<GameMenu>() != null && Navigation().CurrentNavigable?.Pointer != button!.Pointer; i++)
        {
            foreach (Wait wait in WalkTap(PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Seconds(0.25);
        }

        WalkCheck("the pause menu's cursor", () => button != null && Navigation().CurrentNavigable?.Pointer == button.Pointer, "on Settings");
        int grafts = SettingsTab.Grafted;
        foreach (Wait wait in WalkPress("A on the pause menu's Settings", PadButtons.A, () => Settings() is { IsHiding: false } && ActiveTab() >= 0, "the game's Settings over the pause menu", within: OpenSeconds))
        {
            yield return wait;
        }

        if (_walkOk)
        {
            StepNote($"Settings from the pause menu: {(SettingsTab.Grafted > grafts ? "a fresh instance, grafted" : "the same instance as before")}; tabs {TabCount()}, Online at {OnlineIndex() + 1}");
        }

        WalkCheck("the pause menu's Settings", () => ActiveTab() == 0 && TabCount() == 8 && Settings() is { } menu && SettingsTab.OnlineTabs(menu) == 1, "on Video, eight tabs, one Online");
        foreach (Wait wait in RbToOnline())
        {
            yield return wait;
        }

        WalkCheck("Save Backups in a match", () => Settings() is { } menu && SettingsTab.BackupsLocked(menu) == true, "locked (out of the title, as the game's lock)", gate: false);
        foreach (Wait wait in ToRow(SettingsRowId.SaveBackups))
        {
            yield return wait;
        }

        string lockedTip = LobbyLocalization.Say(SettingsTabWords.SaveBackupsLocked);
        WalkCheck("the locked tooltip", () => TooltipText().Contains(lockedTip, StringComparison.Ordinal), $"\"{lockedTip}\" (it shows \"{TooltipText()}\")", gate: false);
        if (_walkOk)
        {
            foreach (Wait wait in Snap("pause_online_locked", 10))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("A on a locked Save Backups", PadButtons.A, () => !LobbyShell.IsOpen && SettingsTab.LastBackupsRefusal is SettingsRowRefusal.NotAtTitle or SettingsRowRefusal.InSession && Settings() != null, "refused (not at the title, an online game on), Settings still up", within: 0.8))
        {
            yield return wait;
        }

        WalkCheck("End Online Game", () => Settings() is { } menu && SettingsTab.Shows(menu, SettingsRowId.EndOnlineGame) && !SettingsTab.Shows(menu, SettingsRowId.LeaveOnlineGame),
            "shown for the host in a session, Leave not", gate: false);
        if (Settings() is { } shown && SettingsTab.Shows(shown, SettingsRowId.EndOnlineGame))
        {
            foreach (Wait wait in ToRow(SettingsRowId.EndOnlineGame))
            {
                yield return wait;
            }

            StepNote("End Online Game: reached by the cursor, never pressed (it would end the session)");
        }

        foreach (Wait wait in ToRow(SettingsRowId.ProblemReport))
        {
            yield return wait;
        }

        foreach (Wait wait in ReportPress("A on Problem Report in a match", "not offered: in a match"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("B on Settings", PadButtons.B, () => Settings() == null && Top()?.TryCast<GameMenu>() != null, "the pause menu", within: OpenSeconds))
        {
            yield return wait;
        }

        // My Settings' look: a following friend's copy of this menu's own Edit Lineup shortcut, drawn here on the host's own pause menu for a picture (the
        // one-PC test has no friend's mirrored menu; a session with a real friend presses the real one), then hidden again.
        GameMenu? pause = Top()?.TryCast<GameMenu>();
        if (_walkOk && pause != null)
        {
            bool drawn = MySettingsView.Update(pause, true);
            yield return Frames(12);
            WalkCheck("My Settings' look", () => drawn && MySettingsView.OffReason == null, $"drawn on the pause menu's bottom right (off: {MySettingsView.OffReason ?? "no"})", gate: false);
            foreach (Wait wait in Snap("pause_my_settings", 4))
            {
                yield return wait;
            }

            _ = MySettingsView.Update(pause, false);
            MySettingsView.Forget();
        }

        foreach (Wait wait in WalkPress("B on the pause menu", PadButtons.B, () => Top()?.TryCast<GameMenu>() == null, "the match again", within: 3))
        {
            yield return wait;
        }
    }

    /// <summary>
    /// Save Backups in a session (the pad walk's lobby Settings): A shows the game's one-button prompt saying to leave first;
    /// B closes it, and Settings is still up with the cursor back on Save Backups.
    /// </summary>
    private static IEnumerable<Wait> LeaveFirstPrompt()
    {
        foreach (Wait wait in WalkPress("A on Save Backups in a session", PadButtons.A,
            () => SettingsTab.LastBackupsRefusal == SettingsRowRefusal.InSession && LeaveFirstUp() && LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Lobby,
            "refused (an online game is on): the game's one-button prompt saying to leave first, no Backups screen", within: 3))
        {
            yield return wait;
        }

        string leaveFirst = LobbyLocalization.Say(LobbyWords.LeaveFirst.Key);
        WalkCheck("the leave-first prompt's words", () => PromptText().Contains(leaveFirst, StringComparison.Ordinal), $"\"{leaveFirst}\" (it says \"{PromptText()}\")", gate: false);
        if (_walkOk)
        {
            foreach (Wait wait in Snap("lobby_settings_leave_first", 6))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in WalkPress("B on the leave-first prompt", PadButtons.B, () => !LobbyPrompts.IsOpen && Settings() != null && OnRow(SettingsRowId.SaveBackups),
            "Settings up again, the cursor back on Save Backups", within: 4))
        {
            yield return wait;
        }
    }

    /// <summary>The game's one-button prompt on top, the mod's (its words read by <see cref="PromptText"/>).</summary>
    private static bool LeaveFirstUp() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking && Top()?.TryCast<CustomizableDismissablePrompt>() != null;

    /// <summary>The words of the game's one-button prompt on top, or "".</summary>
    private static string PromptText() => Top()?.TryCast<CustomizableDismissablePrompt>()?.m_Desc?.text ?? string.Empty;

    // ---- Stretches ----

    /// <summary>
    /// The title's Main group by the pad (or keys), from wherever the last presses left the game: the Backups screen closed, a match quit by the game's own
    /// quit and the title waited for, then B on a menu or a group and A on press-to-start (never B on Main itself: that's press-to-start). Presses only what the
    /// screen shows; never with a session up (a lobby is left by its own steps).
    /// </summary>
    private static IEnumerable<Wait> TitleHome()
    {
        if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Backups)
        {
            LobbyShell.Close("the check tour");
            yield return Soft(() => !LobbyShell.IsOpen, OpenSeconds, "the Backups screen closed");
        }

        if (Session is { } session && session.Actions.Snapshot().Role != LobbyRole.None)
        {
            StepNote("a session is up: not walked home from here (its own steps leave it)");
            yield break;
        }

        if (CampaignGame.Title() == null)
        {
            yield return Soft(() => CampaignGame.Title() != null, 3, "the title");
        }

        if (CampaignGame.Title() == null)
        {
            // Only the replay tour's own Play Now match is quit here; anything else away from the title isn't the walk's to leave.
            Il2Cpp.GameLoader? loader = UnityEngine.Object.FindObjectOfType<Il2Cpp.GameLoader>();
            Il2CppState.GameModeControllers.IGameModeController? controller = loader?._gameModeController;
            if (Replaying && controller != null && PlaceNow(out _) == TourPlace.Match)
            {
                StepNote("in the replay tour's match: the game's own quit");
                controller.Quit();
                yield return Soft(() => CampaignGame.Title() is { m_HasStarted: true } title && !title.m_IsExiting, MatchLoadSeconds, "the title back");
            }
            else
            {
                StepNote($"home: not walked from {TourStarts.Describe(PlaceNow(out string away))}{(away.Length > 0 ? $" ({away})" : string.Empty)}: away from the title, nothing is pressed or loaded");
                yield break;
            }
        }

        for (int i = 0; i < MostHomePresses && !OnMain(); i++)
        {
            TitleScreen? title = CampaignGame.Title();
            TourPlace place = PlaceNow(out string screen);
            if (title == null || place == TourPlace.Loading)
            {
                yield return Seconds(0.5);
                continue;
            }

            // A at press-to-start, B on another group or a screen the tours open; never on the controller screen (its B writes the
            // team selection) or a screen the tours never open (the player's own squad screen, a run's start).
            bool atStart = place == TourPlace.PressStart;
            if (!atStart && place != TourPlace.Group && !(place == TourPlace.TourScreen && screen != "ControllerAssignment"))
            {
                StepNote($"home: not walked from {TourStarts.Describe(place)}{(screen.Length > 0 ? $" ({screen})" : string.Empty)}: not a screen the walk home presses on");
                yield break;
            }

            string before = SettingsWhere();
            foreach (Wait wait in Tap(atStart ? PadButtons.A : PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => OnMain() || SettingsWhere() != before, 2, "the screen to change");
            yield return Seconds(WalkSettleSeconds);
            StepNote($"home: {(atStart ? "A" : "B")} on {before}; now {SettingsWhere()}");
        }
    }

    /// <summary>The title's own Settings row by the pad: from Main, the cursor onto it, A.</summary>
    private static IEnumerable<Wait> SettingsFromTitle(string what)
    {
        foreach (Wait wait in MainTo("Settings"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress(what, PadButtons.A, () => Settings() is { IsHiding: false } && ActiveTab() >= 0, "the game's Settings over the title", within: OpenSeconds))
        {
            yield return wait;
        }

        yield return Seconds(0.4);
    }

    /// <summary>The Online group's Online Settings row by the pad, from Main: Settings on the Online tab.</summary>
    private static IEnumerable<Wait> SettingsFromRow()
    {
        foreach (Wait wait in GroupRow("OnlineSettings"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on Online Settings", PadButtons.A, () => Settings() is { IsHiding: false } && ActiveTab() == OnlineIndex() && OnlineIndex() >= 0, "the game's Settings on its Online tab", within: OpenSeconds))
        {
            yield return wait;
        }

        yield return Seconds(0.4);
    }

    /// <summary>The title's Settings, then RB to the Online tab, the cursor on its first row.</summary>
    private static IEnumerable<Wait> SettingsOnOnline()
    {
        foreach (Wait wait in SettingsFromTitle("A on Settings"))
        {
            yield return wait;
        }

        foreach (Wait wait in RbToOnline())
        {
            yield return wait;
        }

        if (_walkOk)
        {
            // The panel keeps its last selection between opens, as the game's own panels do: the first row by up.
            yield return Soft(() => RowAt() >= 0, 2, "the cursor on a row of the Online tab");
            foreach (Wait wait in ToRow(SettingsRowId.ShareAchievements))
            {
                yield return wait;
            }
        }
    }

    /// <summary>RB once a tap until the Online tab, each tap one tab on.</summary>
    private static IEnumerable<Wait> RbToOnline()
    {
        int online = _walkOk ? OnlineIndex() : -1;
        WalkCheck("the Online tab", () => online >= 0, $"in ({SettingsTab.OffReason ?? "no reason logged"})");
        for (int i = 0; i < MostRbs && _walkOk && ActiveTab() is int at && at >= 0 && at < online; i++)
        {
            foreach (Wait wait in WalkPress($"RB on tab {at + 1}", PadButtons.RightBumper, () => ActiveTab() == at + 1, $"tab {at + 2}", within: 2))
            {
                yield return wait;
            }
        }

        WalkCheck("RB to Online", () => ActiveTab() == online, $"the Online tab ({online + 1})");
        if (_walkOk)
        {
            yield return Seconds(0.3);
        }
    }

    /// <summary>B on Settings: the title back, the cursor where Settings opened from.</summary>
    private static IEnumerable<Wait> CloseSettingsToTitle(string cursorWanted)
    {
        WalkCheck("Settings up before B", () => Settings() != null, "the game's Settings on top");
        foreach (Wait wait in WalkPress("B on Settings", PadButtons.B, () => Settings() == null && TitleIsBack() && Cursor() == cursorWanted, $"the title, the cursor on {cursorWanted}", within: OpenSeconds))
        {
            yield return wait;
        }
    }

    /// <summary>The cursor onto a row of Main, by up and down.</summary>
    private static IEnumerable<Wait> MainTo(string row)
    {
        WalkCheck($"Main before the cursor goes to {row}", () => OnMain(), "the title's Main group");
        int wanted = Array.IndexOf(MainRows, row);
        for (int i = 0; i < MainRows.Length && _walkOk && OnMain() && Cursor() != row; i++)
        {
            string at = Cursor();
            int here = Array.IndexOf(MainRows, at);
            foreach (Wait wait in WalkTap(here > wanted ? PadButtons.DPadUp : PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        WalkCheck($"the cursor on Main's {row}", () => OnMain() && Cursor() == row, row);
    }

    /// <summary>The cursor onto <paramref name="target"/> by up and down (the panel keeps its last row between opens), checked at each press.</summary>
    private static IEnumerable<Wait> ToRow(SettingsRowId target)
    {
        int want = IndexIn(target);
        for (int i = 0; i < 10 && _walkOk && !OnRow(target); i++)
        {
            int at = RowAt();
            WalkCheck("the cursor on one of the Online tab's rows", () => at >= 0, "on a row");
            if (!_walkOk)
            {
                break;
            }

            foreach (Wait wait in WalkTap(at < want ? PadButtons.DPadDown : PadButtons.DPadUp))
            {
                yield return wait;
            }

            yield return Soft(() => RowAt() != at, 1, "the cursor to move");
            yield return Seconds(0.15);
        }

        WalkCheck($"the cursor on {target}", () => OnRow(target), $"on {target}");
    }

    /// <summary>The rows the open Settings' Online tab shows, in its order.</summary>
    private static List<SettingsRowId> ShownRows()
    {
        var shown = new List<SettingsRowId>();
        SettingsMenu? menu = Settings();
        foreach (SettingsRowId id in SettingsRows.Order)
        {
            if (menu != null && SettingsTab.Shows(menu, id))
            {
                shown.Add(id);
            }
        }

        return shown;
    }

    /// <summary>The place in the tab's order of the row the cursor is on, or -1.</summary>
    private static int RowAt()
    {
        SettingsMenu? menu = Settings();
        AbstractNavigable? current = Navigation().CurrentNavigable;
        if (menu == null || current == null)
        {
            return -1;
        }

        foreach (SettingsRowId id in SettingsRows.Order)
        {
            if (SettingsTab.Row(menu, id)?.Pointer == current.Pointer)
            {
                return IndexIn(id);
            }
        }

        return -1;
    }

    private static int IndexIn(SettingsRowId id)
    {
        for (int i = 0; i < SettingsRows.Order.Count; i++)
        {
            if (SettingsRows.Order[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A tap, unless an expectation it depends on has failed.</summary>
    private static IEnumerable<Wait> WalkTap(PadButtons button)
    {
        if (!_walkOk)
        {
            yield break;
        }

        foreach (Wait wait in Tap(button))
        {
            yield return wait;
        }
    }

    /// <summary>A press whose effect is the game's to decide: where it went is said, with nothing expected (never once an expectation failed).</summary>
    private static IEnumerable<Wait> WalkObserve(string what, PadButtons button)
    {
        if (!_walkOk)
        {
            yield break;
        }

        string before = SettingsWhere();
        foreach (Wait wait in Tap(button))
        {
            yield return wait;
        }

        yield return Seconds(0.5);
        StepNote($"{what}: from {before}; now {SettingsWhere()}{(Settings() == null ? " (Settings has gone)" : string.Empty)}");
        if (Settings() == null)
        {
            Fail($"{what}: Settings went away");
            _walkOk = false;
        }
    }

    /// <summary>A press and its expectation; nothing at all once an earlier expectation it depends on has failed.</summary>
    private static IEnumerable<Wait> WalkPress(string what, PadButtons button, Func<bool> done, string wanted, double within = 3, bool gate = true)
    {
        if (!_walkOk)
        {
            StepNote($"{what}: not pressed (an earlier expectation failed)");
            yield break;
        }

        foreach (Wait wait in Tap(button))
        {
            yield return wait;
        }

        yield return Soft(done, within, wanted);
        WalkCheck(what, done, wanted, gate);
        yield return Seconds(WalkSettleSeconds);
    }

    /// <summary>An expectation, said either way. <paramref name="gate"/>: later presses depend on it, so a failure ends the step's presses.</summary>
    private static void WalkCheck(string what, Func<bool> done, string wanted, bool gate = true)
    {
        if (!_walkOk && gate)
        {
            return; // the step's presses stopped at an earlier failure: what follows isn't there to check
        }

        bool ok;
        try
        {
            ok = done();
        }
        catch (Exception ex)
        {
            ok = false;
            wanted += $" (the check threw {ex.GetType().Name})";
        }

        if (ok)
        {
            StepNote($"{what}: {SettingsWhere()}");
            return;
        }

        Fail($"{what}: expected {wanted}; {SettingsWhere()}");
        if (gate)
        {
            _walkOk = false;
        }
    }

    // ---- What's on screen ----

    private static string SettingsWhere()
    {
        AbstractMenu? top = Top();
        TitleScreen? title = CampaignGame.Title();
        return $"cursor {Cursor()}, top {(top == null ? "none" : MenuGame.MenuId(top))}, tab {ActiveTab() + 1} of {TabCount()}, group {(title == null ? -1 : (int)title.m_CurrentButtonGroup)}";
    }

    /// <summary>The game's Settings menu when it's the menu on top, else null.</summary>
    private static SettingsMenu? Settings() => Top()?.TryCast<SettingsMenu>();

    private static int ActiveTab() => Settings()?.m_ActivePanelIndex ?? -1;

    private static int TabCount() => Settings()?.m_SubMenusInOrder?.Length ?? 0;

    private static int OnlineIndex() => SettingsTab.OnlineIndex(Settings());

    private static bool OnRow(SettingsRowId row) =>
        Settings() is { } menu && SettingsTab.Row(menu, row) is { } navigable && Navigation().CurrentNavigable?.Pointer == navigable.Pointer;

    private static bool? ShareShows()
    {
        AbstractNavigable? row = Settings() is { } menu ? SettingsTab.Row(menu, SettingsRowId.ShareAchievements) : null;
        Il2CppBishopGames.UI.Toggle? toggle = row?.GetComponent<Il2CppBishopGames.UI.Toggle>();
        return toggle?.IsSelected;
    }

    /// <summary>The texts of the Settings menu's tooltip box, when it shows.</summary>
    private static string TooltipText()
    {
        Transform? box = Settings()?.transform.Find("ScaledContainer/Canvas/MainContent/Tooltip_DeactivateWhenEmpty");
        if (box == null || !box.gameObject.activeInHierarchy)
        {
            return string.Empty;
        }

        var texts = new List<string>();
        foreach (Component? component in box.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<TMP_Text>(), false))
        {
            TMP_Text? text = component?.TryCast<TMP_Text>();
            if (text != null && !string.IsNullOrEmpty(text.text))
            {
                texts.Add(text.text);
            }
        }

        return string.Join(" | ", texts);
    }

    private static AbstractButton? PauseSettingsButton()
    {
        Il2CppSystem.Collections.Generic.List<GameMenu.PauseButtonData>? buttons = Top()?.TryCast<GameMenu>()?.m_CurrentPauseButtons;
        for (int i = 0; buttons != null && i < buttons.Count; i++)
        {
            if (buttons[i] != null && buttons[i].ButtonType == GameMenu.EPauseMenuButton.Settings)
            {
                return buttons[i].Button;
            }
        }

        return null;
    }

    /// <summary>The tour holds' count of settings held in memory, from its summary part ("... settings held s"); -1 when it can't be read.</summary>
    private static int SettingsHeld()
    {
        const string marker = "settings held ";
        string text = TourHolds.SummaryText;
        int at = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return -1;
        }

        int start = at + marker.Length;
        int end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return int.TryParse(text.AsSpan(start, end - start), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : -1;
    }

    private static string SettingsHash()
    {
        try
        {
            string file = Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline", SettingsFile.FileName);
            return File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "none";
        }
        catch (IOException ex)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }
}
#endif
