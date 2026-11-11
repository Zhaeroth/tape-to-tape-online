using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class NoticesTests
{
    private static readonly Regex FKey = new(@"\bF\d+\b");
    private static readonly Regex Argument = new(@"\{\d\}");

    private static IEnumerable<NoticeSpec> Shown => Notices.All.Where(s => s.IsShown);

    [Fact]
    public void The_table_has_one_row_for_every_id_in_the_ids_order()
    {
        NoticeId[] ids = Enum.GetValues<NoticeId>();

        Assert.Equal(ids.Length, Notices.All.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i], Notices.All[i].Id);
            Assert.Same(Notices.All[i], Notices.Of(ids[i]));
        }

        // The ids are numbered from 0 with no gap: the table is indexed by them.
        Assert.Equal(Enumerable.Range(0, ids.Length).Select(i => (NoticeId)i), ids);
    }

    [Fact]
    public void A_late_joiner_is_told_whose_run_and_where_it_is_as_soon_as_its_on_their_screen()
    {
        NoticeSpec spec = Notices.Of(NoticeId.JoinedRun);
        Assert.Equal(NoticeKind.Toast, spec.Kind);
        Assert.Equal(ToastTiming.Now, spec.InPlay);
        Assert.Equal(3, spec.Args);
        Assert.Equal("Joined Alex's run. Act 2, Floor 5.", Notices.English(spec, new[] { "Alex", "2", "5" }));
    }

    [Fact]
    public void A_Steam_invite_arriving_is_a_toast_naming_who_sent_it_and_the_lobbys_screen_does_not_hide_it()
    {
        NoticeSpec spec = Notices.Of(NoticeId.InviteReceived);

        Assert.Equal(NoticeKind.Toast, spec.Kind);
        Assert.Same(LobbyWords.ToastInvitedYou, spec.Word);
        Assert.Equal(1, spec.Args);
        Assert.Equal(NoticeDelivery.Toast, Notices.Deliver(spec, new NoticeContext(true, false, true, AtTitle: true))); // the lobby says nothing of another lobby's invite
        Assert.Equal(ToastTiming.Now, Notices.TimingFor(spec, new NoticeContext(true, false, false, AtTitle: true)));
        Assert.Equal(ToastTiming.NextStop, Notices.TimingFor(spec, new NoticeContext(true, true, false))); // never over play
        Assert.Equal("Alex invited you.", Notices.English(spec, new[] { "Alex" }));
    }

    [Fact]
    public void An_id_this_version_does_not_know_is_a_log_only_row_and_never_throws()
    {
        NoticeSpec spec = Notices.Of((NoticeId)9999);

        Assert.Equal(NoticeKind.LogOnly, spec.Kind);
        Assert.Equal(NoticeDelivery.None, Notices.Deliver(spec, new NoticeContext(true, false, false)));
        Assert.Equal(NoticeKind.LogOnly, Notices.Of((NoticeId)(-1)).Kind);
    }

    [Fact]
    public void A_log_only_row_has_no_word_and_takes_one_text_and_a_shown_row_has_a_word_or_makes_its_own_line()
    {
        foreach (NoticeSpec spec in Notices.All)
        {
            if (spec.Kind == NoticeKind.LogOnly)
            {
                Assert.Null(spec.Word);
                Assert.False(spec.Dynamic, spec.Id.ToString());
                Assert.Equal(1, spec.Args);
                Assert.False(spec.QuietInLobby, spec.Id.ToString());
            }
            else if (spec.Dynamic)
            {
                Assert.Null(spec.Word);
                Assert.Equal(1, spec.Args);
            }
            else
            {
                Assert.NotNull(spec.Word);
            }
        }
    }

    [Fact]
    public void Every_word_a_row_says_is_in_the_table_of_words_and_wraps()
    {
        foreach (NoticeSpec spec in Shown.Where(s => s.Word != null))
        {
            Assert.Same(spec.Word, LobbyWords.Find(spec.Word!.Key));
            Assert.True(spec.Word.Fit is WordFit.Sentence or WordFit.Seat, $"{spec.Id}: a toast or a prompt's body wraps, so it isn't a button's or a header's word");
        }
    }

    [Fact]
    public void A_rows_arguments_are_the_ones_its_word_has_in_every_language()
    {
        foreach (NoticeSpec spec in Shown.Where(s => s.Word != null))
        {
            foreach (string text in spec.Word!.Texts)
            {
                int marks = Argument.Matches(text).Select(m => m.Value).Distinct().Count();
                Assert.True(marks == spec.Args, $"{spec.Id}: {text} has {marks} argument(s), the row says {spec.Args}");
            }
        }
    }

    [Fact]
    public void Nothing_a_player_reads_names_an_f_key_a_code_or_the_log()
    {
        // No notice names an F-key (no word does: JoinFailuresTests), or a code, or sends the player to the log.
        foreach (NoticeSpec spec in Shown.Where(s => s.Word != null))
        {
            for (int language = 0; language < LobbyWords.LanguageCount; language++)
            {
                string text = spec.Word!.Text(language, "Sam", "Sam");
                Assert.DoesNotMatch(FKey, text);
                Assert.DoesNotMatch(@"\(see the log\)|0x[0-9a-fA-F]+|Exception", text);
                Assert.DoesNotContain(spec.Id.ToString(), text);
            }
        }
    }

    [Fact]
    public void The_new_words_are_short_in_english()
    {
        foreach (LobbyWord word in LobbyWords.All.Where(w => w.Key.StartsWith("t2t/notice_", StringComparison.Ordinal)))
        {
            // The ">" between the names of a menu path (Settings > Online > Save Backups) is not a word.
            int words = word.Texts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(part => part != ">");
            Assert.True(words <= 18, $"{word.Key} has {words} words"); // 17 and the four names of a path to Save Backups, less the three words they replaced
        }
    }

    [Fact]
    public void Every_new_word_is_used_by_a_row()
    {
        HashSet<LobbyWord> used = Notices.All.Where(s => s.Word != null).Select(s => s.Word!).ToHashSet();
        foreach (LobbyWord word in LobbyWords.All.Where(w => w.Key.StartsWith("t2t/notice_", StringComparison.Ordinal)))
        {
            Assert.Contains(word, used);
        }
    }

    [Fact]
    public void What_must_stay_visible_is_a_prompt_or_a_toast_as_the_table_says()
    {
        // Co-op run not saving, save check warnings, backup failed, "Saves restored: restart", a game update turning online off: one button each.
        NoticeId[] blocking =
        {
            NoticeId.CoopSaveFailed, NoticeId.RunNotSaved, NoticeId.SaveCheck, NoticeId.BackupFailed, NoticeId.RestoredRestart, NoticeId.OnlineOff, NoticeId.OnlineBroken,
            NoticeId.OnlineSwitchedOff, NoticeId.CloudNewer, NoticeId.RestoreMixed, NoticeId.RestoreWaits, NoticeId.OwnRunInPlay, NoticeId.PadsFailed,
            NoticeId.SteamOff, NoticeId.SteamWaiting, NoticeId.RestoreBlocksSession, NoticeId.InstallLeftovers, NoticeId.RelayOnlyRefused,
            NoticeId.CloudNotHeld,
        };

        Assert.Equal(blocking.OrderBy(i => i), Notices.All.Where(s => s.Kind == NoticeKind.Blocking).Select(s => s.Id).OrderBy(i => i));

        // A friend's lost connection in play: a toast (the lobby's reconnecting state carries it while the lobby is up).
        Assert.Equal(NoticeKind.Toast, Notices.Of(NoticeId.HostReconnecting).Kind);
        Assert.Equal(NoticeKind.Toast, Notices.Of(NoticeId.HostBack).Kind);
        Assert.Equal(NoticeKind.Toast, Notices.Of(NoticeId.FriendDropped).Kind);
        Assert.Equal(NoticeKind.Toast, Notices.Of(NoticeId.PadLost).Kind);
    }

    [Fact]
    public void Old_files_of_the_mod_are_a_prompt_naming_them_in_every_language()
    {
        NoticeSpec spec = Notices.Of(NoticeId.InstallLeftovers);

        Assert.Equal(NoticeKind.Blocking, spec.Kind);
        Assert.Same(LobbyWords.NoticeInstallLeftovers, spec.Word);
        Assert.Equal(1, spec.Args);
        Assert.Equal(NoticeDelivery.Prompt, Notices.Deliver(spec, new NoticeContext(true, false, false, AtTitle: true)));
        Assert.Equal("Online is off until old T2T Online files are deleted. Close the game, then delete: Plugins/T2TOnline.dll, LICENSE",
            Notices.English(spec, new[] { "Plugins/T2TOnline.dll, LICENSE" }));
        for (int language = 0; language < LobbyWords.LanguageCount; language++)
        {
            string text = spec.Word!.Text(language, "Plugins/T2TOnline.dll");
            Assert.Contains("Plugins/T2TOnline.dll", text, StringComparison.Ordinal);
            Assert.Contains("T2T Online", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_online_off_prompts_say_what_the_online_buttons_prompts_say()
    {
        Assert.Same(LobbyWords.FailOnlineOff, Notices.Of(NoticeId.OnlineOff).Word);
        Assert.Same(LobbyWords.FailOnlineBroken, Notices.Of(NoticeId.OnlineBroken).Word);
        Assert.Same(LobbyWords.FailSteam, Notices.Of(NoticeId.SteamOff).Word);
        Assert.Same(LobbyWords.FailSteamWaiting, Notices.Of(NoticeId.SteamWaiting).Word);
        Assert.Same(LobbyWords.FailRestored, Notices.Of(NoticeId.RestoreBlocksSession).Word);
        Assert.Equal(JoinFailures.Message(LobbyFailure.OnlineOff).Line.Term, Notices.Of(NoticeId.OnlineOff).Word!.Key);
        Assert.Equal(JoinFailures.Message(LobbyFailure.OnlineBroken).Line.Term, Notices.Of(NoticeId.OnlineBroken).Word!.Key);
        Assert.Equal(JoinFailures.Message(LobbyFailure.SteamOff).Line.Term, Notices.Of(NoticeId.SteamOff).Word!.Key);
        Assert.Equal(JoinFailures.Message(LobbyFailure.RestoredBackup).Line.Term, Notices.Of(NoticeId.RestoreBlocksSession).Word!.Key);
    }

    [Fact]
    public void Only_what_the_lobby_shows_itself_is_quiet_while_it_is_up()
    {
        NoticeId[] quiet = { NoticeId.FriendJoined, NoticeId.FriendLeft, NoticeId.FriendDropped, NoticeId.FriendBack, NoticeId.HostBack, NoticeId.HostReconnecting };

        Assert.Equal(quiet.OrderBy(i => i), Notices.All.Where(s => s.QuietInLobby).Select(s => s.Id).OrderBy(i => i));
        Assert.All(Notices.All.Where(s => s.QuietInLobby), s => Assert.Equal(NoticeKind.Toast, s.Kind));
    }

    [Theory]
    [InlineData(NoticeId.PadLost, true, false, false, NoticeDelivery.Toast)]
    [InlineData(NoticeId.PadLost, true, true, false, NoticeDelivery.Toast)]
    [InlineData(NoticeId.PadLost, false, false, false, NoticeDelivery.StatusBox)]
    [InlineData(NoticeId.CoopSaveFailed, true, false, false, NoticeDelivery.Prompt)]
    [InlineData(NoticeId.CoopSaveFailed, true, true, false, NoticeDelivery.Toast)]
    [InlineData(NoticeId.CoopSaveFailed, false, false, false, NoticeDelivery.StatusBox)]
    [InlineData(NoticeId.CoopSaveFailed, false, true, true, NoticeDelivery.StatusBox)]
    [InlineData(NoticeId.FriendJoined, true, false, true, NoticeDelivery.None)]
    [InlineData(NoticeId.FriendJoined, true, false, false, NoticeDelivery.Toast)]
    [InlineData(NoticeId.FriendJoined, true, true, false, NoticeDelivery.Toast)]
    [InlineData(NoticeId.FriendJoined, false, false, true, NoticeDelivery.StatusBox)]
    [InlineData(NoticeId.SessionLeft, true, false, false, NoticeDelivery.None)]
    [InlineData(NoticeId.SessionLeft, false, false, false, NoticeDelivery.None)]
    public void A_notice_goes_where_the_screens_and_the_play_allow(NoticeId id, bool screens, bool inPlay, bool lobbyUp, NoticeDelivery expected)
    {
        Assert.Equal(expected, Notices.Deliver(Notices.Of(id), new NoticeContext(screens, inPlay, lobbyUp)));
    }

    [Fact]
    public void A_shown_notice_is_never_lost_and_a_prompt_never_sits_over_a_game()
    {
        foreach (NoticeSpec spec in Notices.All)
        {
            foreach (bool screens in new[] { false, true })
            {
                foreach (bool inPlay in new[] { false, true })
                {
                    foreach (bool lobby in new[] { false, true })
                    {
                        NoticeDelivery delivery = Notices.Deliver(spec, new NoticeContext(screens, inPlay, lobby));
                        string where = $"{spec.Id} screens {screens} play {inPlay} lobby {lobby}";
                        if (!spec.IsShown)
                        {
                            Assert.Equal(NoticeDelivery.None, delivery);
                            continue;
                        }

                        // Without the native screens it's the status box, always: nothing is lost.
                        Assert.True(screens || delivery == NoticeDelivery.StatusBox, where);

                        // Quiet is only ever the lobby's own screen being there to say it.
                        Assert.True(delivery != NoticeDelivery.None || (screens && lobby && spec.QuietInLobby), where);
                        Assert.False(delivery == NoticeDelivery.Prompt && inPlay, where);
                        Assert.False(delivery == NoticeDelivery.Prompt && spec.Kind != NoticeKind.Blocking, where);
                    }
                }
            }
        }
    }

    [Fact]
    public void A_toast_waits_for_the_games_own_stop_only_in_play()
    {
        NoticeSpec dropped = Notices.Of(NoticeId.FriendDropped);
        NoticeSpec pad = Notices.Of(NoticeId.LobbyReplaced);

        Assert.Equal(ToastTiming.Now, Notices.TimingFor(dropped, new NoticeContext(true, false, false)));
        Assert.Equal(ToastTiming.NextFaceoff, Notices.TimingFor(dropped, new NoticeContext(true, true, false)));
        Assert.Equal(ToastTiming.Now, Notices.TimingFor(pad, new NoticeContext(true, false, true)));
        Assert.Equal(ToastTiming.NextStop, Notices.TimingFor(pad, new NoticeContext(true, true, false)));

        // A shown notice that waits in play never waits for nothing: its timing is a real one.
        NoticeId[] atOnce = { NoticeId.HostBack, NoticeId.HostReconnecting, NoticeId.PadLost, NoticeId.PressToJoin, NoticeId.PressToJoinKeyboard, NoticeId.JoinedRun };
        Assert.All(Shown.Where(spec => !atOnce.Contains(spec.Id)), spec => Assert.NotEqual(ToastTiming.Now, spec.InPlay));
    }

    [Fact]
    public void A_title_under_a_game_shows_a_toast_at_once_as_no_matchs_stop_comes_there()
    {
        NoticeSpec joined = Notices.Of(NoticeId.FriendJoined);
        NoticeSpec saves = Notices.Of(NoticeId.BackupFailed);
        var underStart = new NoticeContext(true, true, false, AtTitle: true);

        Assert.Equal(ToastTiming.Now, Notices.TimingFor(joined, underStart));
        Assert.Equal(NoticeDelivery.Toast, Notices.Deliver(saves, underStart)); // still no prompt: the game's next menu would come over it
        Assert.Equal(ToastTiming.NextFaceoff, Notices.TimingFor(joined, new NoticeContext(true, true, false, AtTitle: false)));
    }

    [Theory]
    [InlineData(NoticeId.HostReconnecting)] // a friend's stops are the host's pops, over the link that's gone
    [InlineData(NoticeId.HostBack)]
    [InlineData(NoticeId.PadLost)] // their player stands still from now
    [InlineData(NoticeId.PressToJoin)] // said once the game they came into is on their screen
    [InlineData(NoticeId.PressToJoinKeyboard)]
    public void What_no_stop_can_bring_shows_at_once_in_play(NoticeId id)
    {
        Assert.Equal(ToastTiming.Now, Notices.TimingFor(Notices.Of(id), new NoticeContext(true, true, false)));
        Assert.Equal(NoticeDelivery.Toast, Notices.Deliver(Notices.Of(id), new NoticeContext(true, true, false)));
    }

    [Fact]
    public void The_english_is_the_word_filled_in_or_the_sites_text_or_the_sites_line()
    {
        Assert.Equal("Sam dropped. Their seat is kept.", Notices.English(Notices.Of(NoticeId.FriendDropped), new[] { "Sam" }));
        Assert.Equal("Controller disconnected. Reconnect it, or press a button on another.", Notices.English(Notices.Of(NoticeId.PadLost), Array.Empty<string>()));
        Assert.Equal("Left the online session.", Notices.English(Notices.Of(NoticeId.SessionLeft), new[] { "Left the online session." }));
        Assert.Equal(string.Empty, Notices.English(Notices.Of(NoticeId.SessionLeft), Array.Empty<string>()));
        Assert.Equal(string.Empty, Notices.English(Notices.Of(NoticeId.SessionLeft), null!));

        LobbyLine refusal = JoinFailures.HostToast(Core.Protocol.RejectReason.SessionFull, "Sam");
        Assert.Equal("Sam couldn't join: your lobby is full.", Notices.English(Notices.Of(NoticeId.FriendRefused), new[] { "unused" }, refusal));
        Assert.Equal(string.Empty, Notices.English(Notices.Of(NoticeId.FriendRefused), Array.Empty<string>()));

        // A site that passes too few arguments leaves the mark; one that passes none for a word with none is fine; neither throws.
        Assert.Equal("{0} dropped. Their seat is kept.", Notices.English(Notices.Of(NoticeId.FriendDropped), Array.Empty<string>()));
        Assert.Equal("Sam dropped. Their seat is kept.", Notices.LineOf(Notices.Of(NoticeId.FriendDropped), "Sam").Text(0));
        Assert.NotNull(Notices.LineOf(Notices.Of(NoticeId.SessionLeft), (string[])null!).Args);
    }

    [Fact]
    public void A_blocking_notice_is_the_one_button_prompt_that_leaves_the_player_where_they_were()
    {
        var line = new LobbyLine(LobbyWords.NoticeCoopSaveFailed);
        FailureMessage prompt = Notices.Prompt(line);

        Assert.Equal(LobbyFailure.Notice, prompt.Reason);
        Assert.Equal(FailureButtons.Ok, prompt.Buttons);
        Assert.Equal(FailureLanding.Stay, prompt.Landing);
        Assert.False(prompt.IsToast);
        Assert.Equal(line, prompt.Line);
        Assert.Throws<ArgumentNullException>(() => Notices.Prompt(null!));
    }

    [Fact]
    public void A_notice_has_a_text_in_every_language_the_game_has()
    {
        foreach (NoticeSpec spec in Shown.Where(s => s.Word != null))
        {
            Assert.Equal(LobbyWords.LanguageCount, spec.Word!.Texts.Count);
            for (int language = 0; language < LobbyWords.LanguageCount; language++)
            {
                Assert.False(string.IsNullOrWhiteSpace(Notices.LineOf(spec, "Sam", "Sam").Text(language)), $"{spec.Id} {language}");
            }
        }
    }

    [Fact]
    public void Steam_Cloud_putting_older_copies_back_over_a_restore_is_a_prompt_in_every_language()
    {
        NoticeSpec spec = Notices.Of(NoticeId.CloudNotHeld);

        Assert.Equal(NoticeKind.Blocking, spec.Kind);
        Assert.Same(LobbyWords.NoticeCloudNotHeld, spec.Word);
        Assert.Equal(0, spec.Args);
        Assert.Equal(NoticeDelivery.Prompt, Notices.Deliver(spec, new NoticeContext(true, false, false, AtTitle: true)));
        Assert.Equal("Steam Cloud may have undone your restore: redo it in Settings > Online > Save Backups once online is back.", Notices.English(spec, Array.Empty<string>()));
        for (int language = 0; language < LobbyWords.LanguageCount; language++)
        {
            string text = spec.Word!.Text(language);
            Assert.Contains("Steam Cloud", text, StringComparison.Ordinal);
            Assert.Contains(LobbyWords.SaveBackups.Texts[language], text, StringComparison.Ordinal); // the path names the row that has them
        }
    }

    [Fact]
    public void The_failure_table_still_answers_every_reason_with_the_notice_reason_added()
    {
        foreach (LobbyFailure failure in Enum.GetValues<LobbyFailure>())
        {
            Assert.NotEmpty(JoinFailures.Message(failure, "Alex").Line.Text(0));
        }
    }
}
