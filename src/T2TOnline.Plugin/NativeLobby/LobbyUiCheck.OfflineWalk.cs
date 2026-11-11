#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Runtime;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.NativeLobby;

// The offline-start tour's walk (after off_state, LobbyUiCheck.Offline.cs): the native screens a player needs most when online is off or stopped after an
// error, in that state. The shell has no virtual pad and no Online group, so the walk acts by the game's own paths: the title's LaunchSettings and its tab
// button's handler, a row confirmed through the game's navigation, a prompt closed by the game's own hide (its safe answer). Nothing is restored or backed up;
// the reports made are named in the summary, so each zip can be scanned before it is shared.
internal static partial class LobbyUiCheck
{
    /// <summary>The title's own Settings, to Online by the game's tab button (RB's handler), the rows this state shows; after an error the cursor on Problem Report.</summary>
    private static IEnumerable<Wait> StepOffSettings()
    {
        foreach (Wait wait in OfflineMain())
        {
            yield return wait;
        }

        Title().LaunchSettings();
        yield return Until(() => Settings() is { IsHiding: false } && ActiveTab() >= 0, OpenSeconds, "the title's Settings");
        if (Settings() is not { } menu)
        {
            Problem($"the title's Settings didn't open ({SettingsWhere()})");
            yield break;
        }

        OffCheck("Settings from the title", ActiveTab() == 0 && TabCount() == 8 && OnlineIndex() == 7, $"on Video, eight tabs, Online the eighth ({SettingsWhere()})");
        int online = OnlineIndex();
        for (int i = 0; i < MostRbs && online >= 0 && ActiveTab() is int at && at >= 0 && at < online; i++)
        {
            menu.OnNextTabClicked(null!); // the game's RB handler: the next tab's toggle (it reads nothing of the shortcut; verified in the game's code)
            yield return Until(() => ActiveTab() == at + 1, 2, $"tab {at + 2}");
        }

        OffCheck("the tab button to Online", ActiveTab() == online, $"the Online tab ({SettingsWhere()})");
        yield return Seconds(0.8);
        SettingsContext context = TabRows.Context();
        StepNote($"the rows: {string.Join(", ", ShownRows())}; {DescribeState(context)}");
        CheckStateRows("this state", context, menu);
        if (context.State == SettingsState.AfterError)
        {
            yield return Soft(() => OnRow(SettingsRowId.ProblemReport), 2, "the cursor on Problem Report");
            OffCheck("the landing after an error", OnRow(SettingsRowId.ProblemReport), $"the cursor on Problem Report ({SettingsWhere()})");
        }

        foreach (Wait wait in Snap("offline_settings", 6))
        {
            yield return wait;
        }

        foreach (Wait wait in HideSettings())
        {
            yield return wait;
        }
    }

    /// <summary>Problem Report by the game's confirm: the zip, the game's prompt naming it, Show File offered at the title; closed by the game's hide.</summary>
    private static IEnumerable<Wait> StepOffReport()
    {
        foreach (Wait wait in OfflineMain())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenSettingsOnline("the offline title"))
        {
            yield return wait;
        }

        AbstractNavigable? row = Settings() is { } menu ? SettingsTab.Row(menu, SettingsRowId.ProblemReport) : null;
        if (row == null)
        {
            Problem("no Problem Report row");
            yield break;
        }

        int before = BugReports.Finished;
        Navigation().SetCurrentNavigable(row, true, true);
        yield return Frames(6);
        Navigation().OnConfirm();
        yield return Until(() => BugReports.Finished > before, ReportSeconds, "a report made");
        OffCheck("Problem Report", BugReports.Finished > before && BugReports.Last.Name.Length > 0, "a report made");
        if (BugReports.Finished > before)
        {
            StepNote($"report: {BugReports.Last.Name} ({BugReports.Last.Summary})");
        }

        yield return Until(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking && ReportFlow.LastShowFile == "offered", 10, "the report's prompt");
        OffCheck("the report's prompt", LobbyPrompts.IsOpen && ReportFlow.LastShowFile == "offered", $"up, Show File offered at the title (it says: {ReportFlow.LastShowFile})");
        foreach (Wait wait in Snap("offline_report_prompt", 4))
        {
            yield return wait;
        }

        foreach (Wait wait in HidePrompt())
        {
            yield return wait;
        }

        // A prompt the game closes by itself still answers whoever asked (Close here), and the cursor is back on the row that asked: the game's own repair of
        // the selection, a frame after the prompt's button has gone (the Online panel's rows are among the menu's default selections).
        OffCheck("the prompt's answer", !LobbyPrompts.IsOpen && !ReportFlow.PromptShown, "Close (its safe answer) taken by the report's flow");
        yield return Soft(() => OnRow(SettingsRowId.ProblemReport), 2, "the cursor back on Problem Report");
        OffCheck("the cursor after the prompt", OnRow(SettingsRowId.ProblemReport), $"back on Problem Report, Settings still up ({SettingsWhere()})");

        foreach (Wait wait in HideSettings())
        {
            yield return wait;
        }
    }

    /// <summary>Save Backups by the game's confirm: both tabs read, one row's restore asked and cancelled; nothing restored, no backup folder new or gone.</summary>
    private static IEnumerable<Wait> StepOffBackups()
    {
        List<string> before = BackupNames();
        foreach (Wait wait in OfflineMain())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenSettingsOnline("the offline title"))
        {
            yield return wait;
        }

        foreach (Wait wait in OpenBackupsByConfirm())
        {
            yield return wait;
        }

        BackupsScreen? screen = BackupsScreen.Current;
        if (screen == null)
        {
            yield break;
        }

        foreach (BackupTab tab in new[] { BackupTab.Saves, BackupTab.CoopRuns })
        {
            screen.SelectTab(tab);
            yield return Frames(8);
            IReadOnlyList<BackupRow> rows = screen.Rows;
            StepNote($"Backups: {tab}: {rows.Count} row(s): {string.Join(" | ", rows.Select(r => $"{SaveBackupNames.ForLog(r.Backup)} ({r.PartForLog})"))}");
        }

        foreach (Wait wait in Snap("offline_backups", 4))
        {
            yield return wait;
        }

        if (screen.Rows.Count == 0)
        {
            screen.SelectTab(BackupTab.Saves);
            yield return Frames(8);
        }

        if (screen.Rows.Count > 0)
        {
            yield return Soft(() => Navigation().CurrentNavigable is { } at && ObjectName(at).StartsWith(screen.Tab.ToString(), StringComparison.Ordinal), 2, "the cursor on a row");
            Navigation().OnConfirm();
            yield return Until(() => BackupsScreen.Current is { Asking: true } && LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, 9, "the restore's question");
            OffCheck("the restore's question", BackupsScreen.Current is { Asking: true }, "the game's yes/no, Cancel first");
            foreach (Wait wait in HidePrompt())
            {
                yield return wait;
            }

            OffCheck("the question's answer", BackupsScreen.Current is { Asking: false }, "Cancel (its safe answer)");
        }
        else
        {
            StepNote("no backup on this PC: no restore to ask about");
        }

        foreach (Wait wait in CloseBackupsAndSettings())
        {
            yield return wait;
        }

        OffCheck("nothing restored, nothing backed up", !SaveBackups.RestartNeeded && BackupNames().SequenceEqual(before, StringComparer.OrdinalIgnoreCase),
            $"no restore waiting for a restart, the {before.Count} backup folder(s) as they were");
    }

    /// <summary>The report's offer at the idle title (due after an error; the tour asks to see it), answered Not Now by the game's hide.</summary>
    private static IEnumerable<Wait> StepOffOffer()
    {
        foreach (Wait wait in OfflineMain())
        {
            yield return wait;
        }

        bool due = ReportOfferWatch.OfferForTour();
        StepNote($"the offer: {(due ? $"due ({ReportOfferWatch.Pending})" : "nothing due")}");
        if (!due)
        {
            if (DevOfflineStart.Reason == OfflineStartReason.Error)
            {
                Fail("the offer: nothing is due after the runtime stopped on an error");
            }

            yield break;
        }

        yield return Until(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, 10, "the offer on the game's prompt");
        OffCheck("the offer", LobbyPrompts.IsOpen, "the game's yes/no at the idle title, Not Now first");
        foreach (Wait wait in Snap("offline_offer", 4))
        {
            yield return wait;
        }

        foreach (Wait wait in HidePrompt())
        {
            yield return wait;
        }

        OffCheck("the offer's answer", ReportOfferWatch.OfferedThisLaunch && !LobbyPrompts.IsOpen, "Not Now (its safe answer), once this launch");
    }

    /// <summary>The offline notice's lines: where the native screens are used it names Settings > Online; the players' build names no F1 and no panel.</summary>
    private static IEnumerable<Wait> StepOffNotice()
    {
        IReadOnlyList<string> lines = OfflineShell.NoticeLines;
        string wanted = PlayersBuild.OfflineNoticeLine(true, ModSettings.DevTools);
        string players = PlayersBuild.OfflineNoticeLine(true, false);
        bool named = lines.Any(l => l.Trim() == wanted);
        bool f1 = ContainsF1(players) || lines.Any(l => ContainsF1(l.Replace(wanted, players, StringComparison.Ordinal)));
        StepNote($"the offline notice: {lines.Count} line(s); F1 or the panel named in the players' build: {(f1 ? "YES" : "no")}; a way to report named: {(named ? PlayersBuild.SettingsOnline : "none")}");
        OffCheck("the offline notice", lines.Count > 0 && named, $"its last line \"{wanted}\"");
        OffCheck("no F1 for players", !f1, "no line names F1 or the panel in the players' build");
        yield break;

        static bool ContainsF1(string line) => line.Contains("F1", StringComparison.Ordinal) || line.Contains("panel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The title's Main group, by the game's own press-to-start when it's up (no pad in the shell).</summary>
    private static IEnumerable<Wait> OfflineMain()
    {
        foreach (Wait wait in CloseLeftovers())
        {
            yield return wait;
        }

        if (CampaignGame.Title() is { } title && (int)title.m_CurrentButtonGroup == 0 && !title._isTransitioningGroup)
        {
            title.PressedToStart(null!);
        }

        yield return Until(() => CampaignGame.Title() is { } t && (int)t.m_CurrentButtonGroup >= 1 && !t._isTransitioningGroup, 8, "the title past press-to-start");
        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }
    }
}
#endif
