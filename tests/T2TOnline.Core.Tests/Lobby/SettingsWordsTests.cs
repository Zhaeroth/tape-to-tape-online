using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The words of Settings > Online, the Backups screen, the problem report and leaving an online game: their keys, fits, arguments and what they may name.</summary>
public class SettingsWordsTests
{
    // F-keys and the key chords of the developer tools, and the mod's old text panel, in any language's spelling of "panel".
    private static readonly Regex DeveloperThings = new(
        @"\bF\d{1,2}\b|Shift\s*\+|Ctrl\s*\+|Strg\s*\+|panel|panneau|pannello|painel|paneeli|панел|面板|panou|パネル",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Name, WordFit Fit)[] Expected =
    {
        ("online_tab", WordFit.SettingsTab), ("share_achievements", WordFit.SettingsRow), ("share_achievements_tip", WordFit.SettingsTooltip),
        ("share_progress", WordFit.SettingsRow), ("share_progress_tip", WordFit.SettingsTooltip), ("play_online_with", WordFit.SettingsRow),
        ("play_online_with_tip", WordFit.SettingsTooltip), ("direct_connections", WordFit.SettingsRow), ("from_next_start", WordFit.SettingsRow),
        ("direct_connections_tip", WordFit.SettingsTooltip), ("direct_connections_ask", WordFit.Sentence), ("turn_on", WordFit.Prompt),
        ("save_backups", WordFit.SettingsRow), ("view", WordFit.SettingsButton), ("save_backups_tip", WordFit.SettingsTooltip),
        ("save_backups_locked", WordFit.SettingsTooltip), ("problem_report", WordFit.SettingsRow), ("make_report", WordFit.SettingsButton),
        ("making", WordFit.SettingsButton), ("problem_report_tip", WordFit.SettingsTooltip), ("leave_online_game", WordFit.SettingsRow),
        ("end_online_game", WordFit.SettingsRow), ("end", WordFit.SettingsButton), ("leave_button", WordFit.SettingsButton),
        ("leave_online_game_tip", WordFit.SettingsTooltip), ("end_online_game_tip", WordFit.SettingsTooltip), ("ask_leave_online_game", WordFit.Sentence),
        ("ask_end_online_game", WordFit.Sentence), ("leave_first", WordFit.Sentence), ("online_settings", WordFit.TitleRow), ("report_a_problem", WordFit.TitleRow),
        ("report_ready", WordFit.Sentence), ("show_file", WordFit.Prompt), ("report_failed", WordFit.Sentence), ("report_offer", WordFit.Sentence),
        ("hold_insert", WordFit.Sentence), ("your_saves", WordFit.ListTab), ("coop_runs", WordFit.ListTab), ("backup_files", WordFit.Seat),
        ("backup_reason_hosting", WordFit.Seat), ("backup_reason_following", WordFit.Seat), ("backup_reason_start", WordFit.Seat),
        ("backup_reason_restore", WordFit.Seat), ("backup_reason_manual", WordFit.Seat), ("backup_reason_other", WordFit.Seat),
        ("restore_saves_detail", WordFit.Sentence), ("restore_slot_detail", WordFit.Sentence), ("restore", WordFit.Prompt), ("no_backups", WordFit.Sentence),
        ("ask_restore_saves", WordFit.Sentence), ("ask_restore_slot", WordFit.Sentence), ("saves_restored", WordFit.Sentence), ("later", WordFit.Prompt),
        ("restore_failed", WordFit.Sentence), ("my_settings", WordFit.Prompt),
    };

    [Fact]
    public void Every_word_of_the_settings_screens_is_here_with_its_fit()
    {
        Assert.Equal(55, Expected.Length);
        foreach ((string name, WordFit fit) in Expected)
        {
            LobbyWord? word = LobbyWords.Find("t2t/" + name);
            Assert.NotNull(word);
            Assert.Equal(fit, word!.Fit);
        }
    }

    [Fact]
    public void The_words_the_game_already_has_are_its_own_terms_not_copies()
    {
        // The game has Auto (Play Online With's first choice), Not Now and Quit: a screen puts these on its labels, so they read as the game's do.
        Assert.Equal("settings/graphics/controller/auto", LobbyTerms.Auto);
        Assert.Equal("general/not_now", LobbyTerms.NotNow);
        Assert.Equal("general/quit", LobbyTerms.Quit);
        foreach (string key in new[] { "t2t/online_input_auto", "t2t/not_now", "t2t/quit_game" })
        {
            Assert.Null(LobbyWords.Find(key));
        }
    }

    [Fact]
    public void The_online_tab_reads_as_the_titles_online_row()
    {
        Assert.Equal(LobbyWords.Online.Texts, LobbyWords.Find("t2t/online_tab")!.Texts);
        Assert.Equal(LobbyWords.Leave.Texts, LobbyWords.Find("t2t/leave_button")!.Texts);
    }

    [Fact]
    public void Arguments_are_where_the_screens_put_them()
    {
        Assert.Equal("Saved as t2t-report.zip in the game folder, under UserData/T2TOnline/reports. Post it with a few words about what happened at example.org/issues. It holds no Steam names or IDs.",
            LobbyWords.Find("t2t/report_ready")!.Text(0, "t2t-report.zip", "example.org/issues"));
        Assert.Equal("The report couldn't be made: no room left", LobbyWords.Find("t2t/report_failed")!.Text(0, "no room left"));
        Assert.Equal("Restore co-op slot 2 from Nov 5, 21:14?", LobbyWords.Find("t2t/ask_restore_slot")!.Text(0, "2", "Nov 5, 21:14"));
        Assert.Equal("Restore your saves from Nov 5, 21:14?", LobbyWords.Find("t2t/ask_restore_saves")!.Text(0, "Nov 5, 21:14"));
        Assert.Equal("The restore didn't finish: a file is open", LobbyWords.Find("t2t/restore_failed")!.Text(0, "a file is open"));
        Assert.Equal("Files: 1", LobbyWords.Find("t2t/backup_files")!.Text(0, "1"));
    }

    [Fact]
    public void A_file_count_reads_right_for_one_file_in_every_language()
    {
        // A label with a colon, not "{0} files": no plural to agree, so a backup of one file reads as well as one of twelve.
        foreach (string text in LobbyWords.Find("t2t/backup_files")!.Texts)
        {
            Assert.Matches(@"[:：]\s*\{0\}$", text);
        }
    }

    [Fact]
    public void No_word_names_the_address_the_screens_pass_it_in()
    {
        // The project's address is written once (ProjectLinks); a word that shows it takes it as an argument.
        string host = ProjectLinks.Repository.Substring(ProjectLinks.Repository.IndexOf("://", System.StringComparison.Ordinal) + 3);
        foreach (LobbyWord word in LobbyWords.All)
        {
            Assert.All(word.Texts, text => Assert.DoesNotContain(host, text));
            Assert.All(word.Texts, text => Assert.DoesNotContain("://", text));
        }
    }

    [Fact]
    public void The_notices_that_send_the_player_to_settings_name_the_rows_by_their_own_words()
    {
        for (int language = 0; language < LobbyWords.LanguageCount; language++)
        {
            string tab = LobbyWords.OnlineTab.Texts[language];
            string backups = LobbyWords.Find("t2t/save_backups")!.Texts[language];
            string direct = LobbyWords.Find("t2t/direct_connections")!.Texts[language];

            Assert.Contains(tab, LobbyWords.FailShell.Texts[language]);
            foreach (LobbyWord word in new[] { LobbyWords.NoticeSaveCheck, LobbyWords.NoticeRestoreMixed, LobbyWords.NoticeCloudNotHeld })
            {
                Assert.Contains(tab, word.Texts[language]);
                Assert.Contains(backups, word.Texts[language]);
            }

            Assert.Contains(tab, LobbyWords.FailRelayOnly.Texts[language]);
            Assert.Contains(direct, LobbyWords.FailRelayOnly.Texts[language]);
        }
    }

    [Fact]
    public void No_word_names_a_developer_key_or_the_old_panel_in_any_language()
    {
        // Players have no F1 panel, no F7 and no developer keys (the settings' tab and the title's rows are the way): a word that sent them there would send them nowhere.
        foreach (LobbyWord word in LobbyWords.All)
        {
            for (int language = 0; language < LobbyWords.LanguageCount; language++)
            {
                Assert.False(DeveloperThings.IsMatch(word.Texts[language]), $"{word.Key} in {LobbyWords.Languages[language]}: {word.Texts[language]}");
            }
        }
    }

    [Fact]
    public void The_developer_pattern_catches_what_it_is_for()
    {
        foreach (string bad in new[] { "Press F1.", "F7 starts a game", "Ctrl+F5", "Shift + F7", "T2T Online's panel", "le panneau de T2T Online", "im Panel von T2T Online", "T2T Online のパネル", "T2T Online 面板", "панели T2T Online" })
        {
            Assert.Matches(DeveloperThings, bad);
        }

        foreach (string good in new[] { "Settings > Online > Save Backups", "Insert marks a moment", "F" })
        {
            Assert.DoesNotMatch(DeveloperThings, good);
        }
    }

    [Fact]
    public void Every_backup_reason_has_a_word()
    {
        // The reasons SaveBackups records: before hosting, before following a host, before a later Start, before a restore, by hand, and anything older.
        string[] kinds = { "hosting", "following", "start", "restore", "manual", "other" };
        IEnumerable<LobbyWord> words = kinds.Select(kind => LobbyWords.Find("t2t/backup_reason_" + kind)!);
        Assert.All(words, Assert.NotNull);
        Assert.Equal(kinds.Length, words.Select(w => w.Texts[0]).Distinct().Count());
    }

    [Fact]
    public void The_texts_of_the_fits_the_checker_measures_use_no_line_breaks()
    {
        foreach ((string name, WordFit fit) in Expected.Where(e => e.Fit is not WordFit.Sentence))
        {
            Assert.All(LobbyWords.Find("t2t/" + name)!.Texts, text => Assert.DoesNotContain('\n', text));
        }
    }
}
