#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Core;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;

namespace T2TOnline.Plugin.NativeLobby;

// The screens tour's pictures of the new native screens (on the fake session, at the title): the Online group's five rows, Settings > Online in every state
// its rows have, the problem report's prompts and the Backups screen, each in the player's language here and again in all 15 languages (scr_langs) and at
// every shape (scr_sizes). States a fake session can't make (a friend's own menu, a host in a match, Steam refusing relay only, online off, after an error)
// are drawn through the rows' check-only context (TabRows.ForCheck, held only while a tour holds, cleared after each picture and at every restore). The
// screens are opened by the game's own paths (the title's LaunchSettings with the Online tab asked for, as the Online group's row does; the rows' confirm
// through the game's navigation) and closed by the game's own hide; nothing is pressed that changes a setting, makes a backup or restores one.
internal static partial class LobbyUiCheck
{
    // The rows' states the pictures show, beyond the title's own: each a context for the rows, as the real game makes it there.
    private static readonly (string Name, SettingsContext Context)[] PicturedStates =
    {
        ("lobby", new SettingsContext(AtTitle: true, SettingsRole.Host, ScreenOpen: true, DirectConnectionsOn: false, RelayRefused: false)),
        ("pause", new SettingsContext(AtTitle: false, SettingsRole.Host, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false)),
        ("friend", new SettingsContext(AtTitle: false, SettingsRole.Friend, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false)),
        ("direct", new SettingsContext(AtTitle: true, SettingsRole.None, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: true)),
        ("offline", new SettingsContext(AtTitle: true, SettingsRole.None, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false, SettingsState.OnlineOff)),
        ("error", new SettingsContext(AtTitle: true, SettingsRole.None, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false, SettingsState.AfterError)),
    };

    private static void AddScreensNativeSteps()
    {
        Steps["scr_online_group"] = StepScrOnlineGroup;
        Steps["scr_settings_online"] = StepScrSettingsOnline;
        Steps["scr_report"] = StepScrReport;
        Steps["scr_backups"] = StepScrBackups;
    }

    /// <summary>
    /// Everything the new screens' checks hold, let go: the keyboard walk's keyboard and mouse mode, the rows' check context, the custom team's
    /// names <c>selfdrive team=custom</c> holds. From any state.
    /// </summary>
    private static void EndNativeChecks()
    {
        TabRows.ForCheck = null;
        EndCustomTeam();
        EndKeyWalk();
    }

    // ---- The steps ----

    /// <summary>The Online group's five rows in order, each with the cursor on it.</summary>
    private static IEnumerable<Wait> StepScrOnlineGroup()
    {
        foreach (Wait wait in MainShape())
        {
            yield return wait;
        }

        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        Fake.SetListings();
        OnlineGroup.Enter(Title());
        yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 3, "the Online group");
        AbstractButton[] rows = OnlineGroup.Rows;
        string names = string.Join(", ", rows.Select(r => ObjectName(r)));
        StepNote($"the Online group's rows: {names}");
        if (names != "HostLobby, JoinLobby, OnlineSettings, ReportAProblem, OnlineBack")
        {
            Problem($"the rows are {names}, not Host Lobby, Join Lobby, Online Settings, Report a Problem, Back");
        }

        foreach (AbstractButton row in rows)
        {
            Navigation().SetCurrentNavigable(row, true, true);
            yield return Frames(10);
            foreach (Wait wait in Snap($"online_group_{ObjectName(row).ToLowerInvariant()}", 4))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }
    }

    /// <summary>Settings > Online at the title, each row with its tooltip; then each state of <see cref="PicturedStates"/>.</summary>
    private static IEnumerable<Wait> StepScrSettingsOnline()
    {
        foreach (Wait wait in OpenSettingsOnline("the title"))
        {
            yield return wait;
        }

        SettingsMenu? menu = Settings();
        if (menu == null)
        {
            yield break;
        }

        foreach (SettingsRowId id in ShownRows())
        {
            if (SettingsTab.Row(menu, id) is { } row)
            {
                Navigation().SetCurrentNavigable(row, true, true);
                yield return Frames(8);
                foreach (Wait wait in Snap($"settings_online_{id.ToString().ToLowerInvariant()}", 4))
                {
                    yield return wait;
                }
            }
        }

        foreach (Wait wait in HideSettings())
        {
            yield return wait;
        }

        foreach ((string name, SettingsContext context) in PicturedStates)
        {
            TabRows.ForCheck = context;
            try
            {
                foreach (Wait wait in OpenSettingsOnline(name))
                {
                    yield return wait;
                }

                if (Settings() is { } shown)
                {
                    string rows = string.Join(", ", ShownRows());
                    StepNote($"{name}: {DescribeState(context)}; rows {rows}; Save Backups {(SettingsTab.BackupsLocked(shown) == true ? "locked" : "open")}");
                    CheckStateRows(name, context, shown);
                    foreach (Wait wait in Snap($"settings_online_{name}", 6))
                    {
                        yield return wait;
                    }
                }

                foreach (Wait wait in HideSettings())
                {
                    yield return wait;
                }
            }
            finally
            {
                TabRows.ForCheck = null;
            }
        }
    }

    /// <summary>The problem report's prompts as the flow shows them: ready at the title, ready away from it, failed; and the offer after an error.</summary>
    private static IEnumerable<Wait> StepScrReport()
    {
        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        foreach ((string name, Func<bool> ask) in ReportPrompts())
        {
            foreach (Wait wait in PromptPicture(name, ask))
            {
                yield return wait;
            }
        }

        // Over Settings > Online, where its Problem Report row asks: the prompt closed, the cursor is back on the row (the game's own repair of the selection, a
        // frame after the prompt's button has gone: the Online panel's rows are among the menu's default selections). No picture: the prompt's is above.
        foreach (Wait wait in OpenSettingsOnline("the title"))
        {
            yield return wait;
        }

        if (Settings() is { } menu && SettingsTab.Row(menu, SettingsRowId.ProblemReport) is { } row)
        {
            Navigation().SetCurrentNavigable(row, true, true);
            yield return Frames(8);
            if (ReportPrompts()[0].Ask())
            {
                yield return Until(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, 9, "the report's prompt over Settings");
                yield return Seconds(0.55);
                foreach (Wait wait in HidePrompt())
                {
                    yield return wait;
                }

                yield return Soft(() => OnRow(SettingsRowId.ProblemReport), 2, "the cursor back on Problem Report");
                if (OnRow(SettingsRowId.ProblemReport))
                {
                    StepNote($"the report's prompt over Settings > Online, closed: the cursor is back on Problem Report ({SettingsWhere()})");
                }
                else
                {
                    Problem($"the cursor wasn't back on Problem Report after the report's prompt closed over Settings > Online ({SettingsWhere()})");
                }
            }
            else
            {
                Problem("the report's prompt couldn't be asked over Settings > Online (another is open)");
            }
        }
        else
        {
            Problem($"no Problem Report row to ask from ({SettingsWhere()})");
        }

        foreach (Wait wait in HideSettings())
        {
            yield return wait;
        }
    }

    /// <summary>The Backups screen over Settings > Online: both tabs with the cursor on a row and the details pane, an empty tab if one is, the restore's question.</summary>
    private static IEnumerable<Wait> StepScrBackups()
    {
        foreach (Wait wait in OpenSettingsOnline("the title"))
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
            yield return Frames(10);
            StepNote($"Backups: {tab}: {screen.Rows.Count} row(s){(screen.Rows.Count == 0 ? ", the empty state" : string.Empty)}");
            foreach (Wait wait in Snap($"backups_{tab.ToString().ToLowerInvariant()}", 4))
            {
                yield return wait;
            }
        }

        // The restore's question, by the game's confirm on the focused row (the screen's own A), answered by the game's hide: Cancel, the safe answer.
        BackupTab? with = screen.Rows.Count > 0 ? screen.Tab : null;
        if (with == null)
        {
            screen.SelectTab(BackupTab.Saves);
            yield return Frames(10);
            with = screen.Rows.Count > 0 ? BackupTab.Saves : null;
        }

        if (with == null)
        {
            StepNote("no backup on this PC: no restore question to picture");
        }
        else
        {
            yield return Soft(() => Navigation().CurrentNavigable is { } at && ObjectName(at).StartsWith(with.ToString()!, StringComparison.Ordinal), 2, "the cursor on a row");
            Navigation().OnConfirm();
            yield return Until(() => BackupsScreen.Current is { Asking: true } && LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, 9, "the restore's question");
            yield return Seconds(0.55);
            foreach (Wait wait in Snap("backups_restore_question", 0))
            {
                yield return wait;
            }

            foreach (Wait wait in HidePrompt())
            {
                yield return wait;
            }

            if (BackupsScreen.Current is { Asking: true })
            {
                Problem("the restore's question didn't take its safe answer when it closed");
            }
        }

        foreach (Wait wait in CloseBackupsAndSettings())
        {
            yield return wait;
        }
    }

    // ---- In every language and at every shape ----

    /// <summary>
    /// For <c>scr_langs</c>: Settings > Online and the Backups screen opened once in the player's language, each pictured in every language (their words follow
    /// a change), back in the player's language before they close (a title menu closed in another language makes the game prepare the title's menus again);
    /// then the report's ready prompt in each language.
    /// </summary>
    private static IEnumerable<Wait> NativeScreensInEveryLanguage()
    {
        foreach (Wait wait in OpenSettingsOnline("the title"))
        {
            yield return wait;
        }

        foreach (Wait wait in EachLanguage("settings_online"))
        {
            yield return wait;
        }

        foreach (Wait wait in OpenBackupsByConfirm())
        {
            yield return wait;
        }

        if (BackupsScreen.Current != null)
        {
            foreach (Wait wait in EachLanguage("backups"))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in CloseBackupsAndSettings())
        {
            yield return wait;
        }

        // The ready prompt and the offer's "closed" sentence, the longest of the report's, in each language.
        List<(string Name, Func<bool> Ask)> prompts = ReportPrompts();
        (string Name, Func<bool> Ask)[] each = { prompts[0], prompts.Find(p => p.Name == ReportOfferClosedPrompt) };
        foreach (string wanted in LobbyWords.Languages)
        {
            if (FindLanguage(wanted) is { } language)
            {
                foreach (Wait wait in SetLanguage(language.Name, language.Code))
                {
                    yield return wait;
                }

                foreach ((string name, Func<bool> ask) in each)
                {
                    foreach (Wait wait in PromptPicture($"{name}_{language.Code}", ask))
                    {
                        yield return wait;
                    }
                }
            }
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
    }

    /// <summary>For <c>scr_sizes</c>, at the size and in the language set now: Settings > Online and the Backups screen, opened and closed in the player's language.</summary>
    private static IEnumerable<Wait> NativeScreensAtThisSize(string languageName, string languageCode)
    {
        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        foreach (Wait wait in OpenSettingsOnline("the title"))
        {
            yield return wait;
        }

        foreach (Wait wait in SetLanguage(languageName, languageCode))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("settings_online", 8))
        {
            yield return wait;
        }

        foreach (Wait wait in OpenBackupsByConfirm())
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("backups", 8))
        {
            yield return wait;
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseBackupsAndSettings())
        {
            yield return wait;
        }

        foreach (Wait wait in SetLanguage(languageName, languageCode))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> EachLanguage(string picture)
    {
        foreach (string wanted in LobbyWords.Languages)
        {
            if (FindLanguage(wanted) is { } language)
            {
                foreach (Wait wait in SetLanguage(language.Name, language.Code))
                {
                    yield return wait;
                }

                yield return Seconds(0.6);
                foreach (Wait wait in Snap($"{picture}_{language.Code}", 4))
                {
                    yield return wait;
                }
            }
            else
            {
                Problem($"the game has no language {wanted}");
            }
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
    }

    // ---- Stretches ----

    /// <summary>The game's Settings over the title on its Online tab, as the Online group's row opens it (the tab's request, then the title's own LaunchSettings).</summary>
    private static IEnumerable<Wait> OpenSettingsOnline(string from)
    {
        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        SettingsTab.RequestOnlineTab("the check tour");
        Title().LaunchSettings();
        yield return Until(() => Settings() is { IsHiding: false } && OnlineIndex() >= 0 && ActiveTab() == OnlineIndex(), OpenSeconds, $"Settings on the Online tab ({from})");
        if (Settings() == null || ActiveTab() != OnlineIndex())
        {
            Problem($"Settings didn't open on the Online tab ({from}; {SettingsWhere()})");
            yield break;
        }

        yield return Seconds(0.8); // the panel's intro
    }

    /// <summary>Save Backups' own row confirmed through the game's navigation: the Backups screen over Settings, its arrival half second over.</summary>
    private static IEnumerable<Wait> OpenBackupsByConfirm()
    {
        SettingsMenu? menu = Settings();
        AbstractNavigable? row = menu == null ? null : SettingsTab.Row(menu, SettingsRowId.SaveBackups);
        if (row == null)
        {
            Problem("no Save Backups row to open the Backups screen from");
            yield break;
        }

        Navigation().SetCurrentNavigable(row, true, true);
        yield return Frames(6);
        Navigation().OnConfirm();
        yield return Until(() => LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Backups && BackupsScreen.Current != null, OpenSeconds, "the Backups screen");
        if (BackupsScreen.Current == null)
        {
            Problem($"the Backups screen didn't open (Save Backups: {SettingsTab.LastBackupsRefusal})");
            yield break;
        }

        yield return Seconds(BackupsArrivalSeconds);
    }

    private static IEnumerable<Wait> CloseBackupsAndSettings()
    {
        if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Backups)
        {
            LobbyShell.Close("the check tour");
            yield return Until(() => !LobbyShell.IsOpen && Settings() != null, OpenSeconds, "the Backups screen closed");
        }

        foreach (Wait wait in HideSettings())
        {
            yield return wait;
        }
    }

    /// <summary>The game's Settings closed by its own hide (its writes are refused while the tour holds), the title back.</summary>
    private static IEnumerable<Wait> HideSettings()
    {
        if (Settings() is { } menu)
        {
            _ = menu.Hide();
            yield return Until(() => Settings() == null && TitleIsBack(), OpenSeconds, "Settings closed");
        }
    }

    /// <summary>
    /// The prompt on top closed by the game's own hide, as the game closes one by itself (another menu's start, a scene load): the flow that asked takes its safe
    /// answer (Close, Not Now, Cancel). Over when the prompts have told whoever asked, a frame after the game has destroyed the hidden menu: what a step reads next is
    /// the flow's state after its answer.
    /// </summary>
    private static IEnumerable<Wait> HidePrompt()
    {
        AbstractMenu? top = Top();
        if (LobbyPrompts.IsOpen && top != null && (top.TryCast<PromptTemplateMenu>() != null || top.TryCast<CustomizableDismissablePrompt>() != null))
        {
            _ = top.Hide();
        }

        yield return Until(() => !LobbyPrompts.IsOpen, 5, "the prompt gone");
    }

    /// <summary>A prompt asked by <paramref name="ask"/>, pictured, and closed.</summary>
    private static IEnumerable<Wait> PromptPicture(string name, Func<bool> ask)
    {
        if (!ask())
        {
            Problem($"{name}: the prompt couldn't be asked (another is open)");
            yield break;
        }

        yield return Until(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, 9, $"the {name} prompt");
        yield return Seconds(0.55);
        foreach (Wait wait in Snap(name, 0))
        {
            yield return wait;
        }

        foreach (Wait wait in HidePrompt())
        {
            yield return wait;
        }
    }

    /// <summary>
    /// The report's prompts with the words and buttons the flow gives them (NativeUi/ReportFlow.cs, ReportOfferWatch.cs), for their pictures: the zip's name is a
    /// made-up one of the real form; nothing is made or offered.
    /// </summary>
    private static List<(string Name, Func<bool> Ask)> ReportPrompts()
    {
        static string Title() => LobbyLocalization.Say(LobbyWords.ProblemReport.Key);
        static string Ready() => LobbyLocalization.Say(new LobbyLine(LobbyWords.ReportReady, BugReportFiles.ReportName(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)), ProjectLinks.IssuesShort).ForRichText());
        static string Close() => LobbyLocalization.Say(LobbyTerms.Close);
        return new List<(string, Func<bool>)>
        {
            ("report_ready_title", () => LobbyPrompts.Ask(Title(), Ready(), Close(), LobbyLocalization.Say(LobbyWords.ShowFile.Key), static _ => { })),
            ("report_ready_away", () => LobbyPrompts.Tell(Title(), Ready(), Close(), static () => { })),
            ("report_failed", () => LobbyPrompts.Tell(Title(), LobbyLocalization.Say(new LobbyLine(LobbyWords.ReportFailed, "the disk is full").ForRichText()), Close(), static () => { })),
            ("report_offer", () => LobbyPrompts.Ask(Title(), LobbyLocalization.Say(LobbyWords.ReportOffer.Key), LobbyLocalization.Say(LobbyTerms.NotNow), LobbyLocalization.Say(LobbyWords.MakeReport.Key), static _ => { })),
            (ReportOfferClosedPrompt, () => LobbyPrompts.Ask(Title(), LobbyLocalization.Say(LobbyWords.ReportOfferClosed.Key), LobbyLocalization.Say(LobbyTerms.NotNow), LobbyLocalization.Say(LobbyWords.MakeReport.Key), static _ => { })),
        };
    }

    // The offer after a launch that closed during an online game (ReportOffer.WordFor): the offer's other sentence, with the same buttons.
    private const string ReportOfferClosedPrompt = "report_offer_closed";

    /// <summary>What a state's rows must be, as Core's rule says (<see cref="SettingsRows.Shown"/>), checked against the open menu.</summary>
    private static void CheckStateRows(string name, in SettingsContext context, SettingsMenu menu)
    {
        foreach (SettingsRowId id in SettingsRows.Order)
        {
            bool wanted = SettingsRows.Shown(id, context);
            if (SettingsTab.Shows(menu, id) != wanted)
            {
                Problem($"{name}: {id} {(wanted ? "isn't shown" : "is shown")}");
            }
        }

        if (SettingsTab.BackupsLocked(menu) != !context.AtTitle)
        {
            Problem($"{name}: Save Backups {(context.AtTitle ? "is locked at the title" : "isn't locked away from it")}");
        }
    }

    private static string DescribeState(in SettingsContext context) =>
        $"{SettingsStates.Describe(context.State)}, role {context.SessionRole}, {(context.AtTitle ? "at the title" : "away from it")}{(context.ScreenOpen ? ", a screen under Settings" : string.Empty)}{(context.RelayRefused ? ", Steam refused relay only" : string.Empty)}";
}
#endif
