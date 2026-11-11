using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class LobbyWordsTests
{
    private static readonly Regex Argument = new(@"\{\d\}");

    [Fact]
    public void The_languages_are_the_games_in_its_order()
    {
        Assert.Equal(new[]
        {
            "English", "French", "Spanish", "German", "Italian", "Portuguese (Brazil)", "Finnish", "Russian", "Swedish", "Chinese (Simplified)", "Czech", "Ukrainian", "Polish", "Romanian", "Japanese",
        }, LobbyWords.Languages);
        Assert.Equal(LobbyWords.LanguageCount, LobbyWords.Languages.Count);
    }

    [Fact]
    public void Every_word_has_every_language_and_none_is_blank()
    {
        Assert.NotEmpty(LobbyWords.All);
        foreach (LobbyWord word in LobbyWords.All)
        {
            Assert.Equal(LobbyWords.LanguageCount, word.Texts.Count);
            Assert.All(word.Texts, text => Assert.False(string.IsNullOrWhiteSpace(text), word.Key));
        }
    }

    [Fact]
    public void Every_key_is_unique_and_in_the_mods_namespace()
    {
        Assert.Equal(LobbyWords.All.Count, LobbyWords.All.Select(w => w.Key).Distinct().Count());
        Assert.All(LobbyWords.All, word => Assert.StartsWith("t2t/", word.Key));
    }

    [Fact]
    public void Every_word_keeps_its_format_arguments_in_every_language()
    {
        foreach (LobbyWord word in LobbyWords.All)
        {
            string[] english = Argument.Matches(word.Texts[0]).Select(m => m.Value).Distinct().OrderBy(a => a).ToArray();
            foreach (string text in word.Texts)
            {
                string[] here = Argument.Matches(text).Select(m => m.Value).Distinct().OrderBy(a => a).ToArray();
                Assert.True(english.SequenceEqual(here), $"{word.Key}: {text}");
            }
        }
    }

    [Fact]
    public void Every_brace_is_part_of_a_format_argument()
    {
        // A stray brace in a translation is what string.Format throws on; Fill doesn't, but it'd still show.
        foreach (LobbyWord word in LobbyWords.All)
        {
            Assert.All(word.Texts, text => Assert.DoesNotContain("{", Argument.Replace(text, string.Empty)));
            Assert.All(word.Texts, text => Assert.DoesNotContain("}", Argument.Replace(text, string.Empty)));
        }
    }

    [Fact]
    public void Every_word_field_is_in_the_table_and_the_table_finds_them()
    {
        // The table is filled by the fields' initializers, in order: a field left out of it can't happen, and this says so.
        List<LobbyWord> fields = typeof(LobbyWords).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(LobbyWord)).Select(f => (LobbyWord)f.GetValue(null)!).ToList();

        Assert.Equal(fields, LobbyWords.All);
        Assert.All(fields, word => Assert.Same(word, LobbyWords.Find(word.Key)));
        Assert.Null(LobbyWords.Find("general/start"));
        Assert.Null(LobbyWords.Find(null));
    }

    [Fact]
    public void The_first_drafted_terms_the_screens_say_are_all_here()
    {
        // Of the 36 terms first listed for the native lobby, the ten no screen says are not kept: no word is carried in 15 languages that nobody reads.
        string[] drafted =
        {
            "online", "host_lobby", "join_lobby", "lobby", "friends_lobbies", "invite", "invite_friends", "leave_lobby", "close_lobby", "not_ready", "waiting_players", "waiting_host", "host", "refresh",
            "no_friends_playing", "coop_save", "slot", "kick", "join_failed", "joined", "left", "host_chooses", "version_mismatch", "in_game", "game_starting", "player_n",
        };
        string[] gone = { "no_games", "empty", "view_profile", "ping", "connecting", "lobby_full", "host_left", "steam_off", "mod_off", "players_count" };

        Assert.Equal(26, drafted.Length);
        Assert.All(drafted, key => Assert.NotNull(LobbyWords.Find("t2t/" + key)));
        Assert.All(gone, key => Assert.Null(LobbyWords.Find("t2t/" + key)));
    }

    [Fact]
    public void The_words_a_screen_needs_are_named_and_say_what_the_tables_say()
    {
        Assert.Equal("Friends need T2T Online installed.", LobbyWords.FriendsNeedMod.Text(0));
        Assert.Equal("Steam overlay", LobbyWords.SteamOverlay.Text(0));
        Assert.Equal("Joining Alex's lobby", LobbyWords.JoiningLobby.Text(0, "Alex"));
        Assert.Equal("Alex invited you. Leave your run and it'll still be there.", LobbyWords.ToastInviteHeldRun.Text(0, "Alex"));
        Assert.Equal("Leave Alex's lobby and join Sam's?", LobbyWords.AskSwitchLobby.Text(0, "Alex", "Sam"));
        Assert.Equal("Reconnecting to Alex...", LobbyWords.ReconnectingTo.Text(0, "Alex"));
        Assert.Equal("Alex is hosting", LobbyWords.IsHosting.Text(0, "Alex"));
        Assert.Equal("Joined Alex's run. Act 2, Floor 5.", LobbyWords.BannerJoinedRun.Text(0, "Alex", "2", "5"));
    }

    [Fact]
    public void Never_quick_play()
    {
        Assert.All(LobbyWords.All, word => Assert.DoesNotContain("Quick Play", word.Texts[0]));
        Assert.Equal("v2ui/title_screen/quick_match", LobbyTerms.QuickGame);
    }

    [Fact]
    public void No_word_uses_a_character_the_latin_fonts_lack()
    {
        // The game's fonts lack these whatever the language, so a word with one would fall to another font or a box. The font check does the rest.
        char[] missing = { '$', '^', '`', '|', '~', '@', '·' };
        foreach (LobbyWord word in LobbyWords.All)
        {
            Assert.All(word.Texts, text => Assert.True(text.IndexOfAny(missing) < 0, $"{word.Key}: {text}"));
        }
    }

    [Fact]
    public void Text_gives_english_for_a_language_it_does_not_know()
    {
        Assert.Equal("Online", LobbyWords.Online.Text(-1));
        Assert.Equal("Online", LobbyWords.Online.Text(99));
        Assert.Equal("联机", LobbyWords.Online.Text(9));
    }

    [Fact]
    public void Fill_never_throws_and_leaves_what_it_cant_fill()
    {
        Assert.Equal("a {0} b", LobbyWords.Fill("a {0} b", null));
        Assert.Equal("a {0} b", LobbyWords.Fill("a {0} b", System.Array.Empty<string>()));
        Assert.Equal("x {1}", LobbyWords.Fill("{0} {1}", new[] { "x" }));
        Assert.Equal("{x} {", LobbyWords.Fill("{x} {", new[] { "a" }));
        Assert.Equal(string.Empty, LobbyWords.Fill(null!, new[] { "a" }));
        Assert.Equal("b a", LobbyWords.Fill("{1} {0}", new[] { "a", "b" }));

        // A name holding a format mark or braces is what it is: never read as one.
        Assert.Equal("{1} joined", LobbyWords.Fill("{0} joined", new[] { "{1}" }));
    }

    [Fact]
    public void A_line_reads_the_mods_words_and_the_games_through_the_plugin()
    {
        Assert.Equal("Sam joined", new LobbyLine(LobbyWords.Joined, "Sam").Text(0));
        Assert.Equal("Act 2", LobbyLine.Game(LobbyTerms.ActN, "2").Text(0, term => term == LobbyTerms.ActN ? "Act {0}" : null));

        // No reader, or a term the game doesn't have: nothing rather than a term's name on the screen.
        Assert.Equal(string.Empty, LobbyLine.Game(LobbyTerms.ActN, "2").Text(0));
        Assert.Equal(string.Empty, LobbyLine.Game("nope/none").Text(0, _ => null));
        Assert.Equal("Sam joined", new LobbyLine(LobbyWords.Joined, "Sam").Text(99));
    }

    [Fact]
    public void Lines_compare_by_what_they_say()
    {
        Assert.Equal(new LobbyLine(LobbyWords.Joined, "Sam"), new LobbyLine(LobbyWords.Joined, "Sam"));
        Assert.Equal(new LobbyLine(LobbyWords.Joined, "Sam").GetHashCode(), new LobbyLine(LobbyWords.Joined, "Sam").GetHashCode());
        Assert.NotEqual(new LobbyLine(LobbyWords.Joined, "Sam"), new LobbyLine(LobbyWords.Joined, "Alex"));
        Assert.NotEqual(new LobbyLine(LobbyWords.Joined, "Sam"), new LobbyLine(LobbyWords.Left, "Sam"));
        Assert.NotEqual(new LobbyLine(LobbyWords.Joined, "Sam"), new LobbyLine(LobbyWords.Joined));
        Assert.False(new LobbyLine(LobbyWords.Joined).Equals(null));
        Assert.Empty(new LobbyLine("x", null!).Args);
    }

    [Fact]
    public void Numbers_are_the_same_on_any_pc()
    {
        Assert.Equal("1500", LobbyWords.Number(1500));
    }

    [Fact]
    public void Chinese_never_says_hosting_as_booking_a_room()
    {
        // 开房 alone is what people say for taking a hotel room. 离开房间 and 打开房间 (leave, open a room) only contain the letters.
        int chinese = LobbyWords.Languages.ToList().IndexOf("Chinese (Simplified)");
        foreach (LobbyWord word in LobbyWords.All)
        {
            string text = word.Texts[chinese].Replace("离开房间", string.Empty).Replace("打开房间", string.Empty);
            Assert.DoesNotContain("开房", text);
        }
    }

    [Fact]
    public void A_lost_host_is_a_lost_connection_not_a_lost_game()
    {
        // In a sports game "the game is lost" reads as a defeat.
        Assert.Equal("Lost the connection to Alex's game. Your own progress is safe.", LobbyWords.FailHostLost.Text(0, "Alex"));
    }

    [Fact]
    public void Spanish_keeps_the_games_gender_for_a_run()
    {
        // The game's own glossary says "una Run" (glossary/campaign/desc): feminine, as the ribbon's "Run ganada" is.
        int spanish = LobbyWords.Languages.ToList().IndexOf("Spanish");
        foreach (LobbyWord word in LobbyWords.All)
        {
            Assert.DoesNotMatch(@"\b(el|un|este|tu propio) Run\b|\bRun cooperativo\b", word.Texts[spanish].ToLowerInvariant().Replace("run", "Run"));
        }
    }

    [Fact]
    public void The_settings_file_says_what_off_does_as_the_rows_tooltip_does()
    {
        // settings.cfg and Settings > Online describe one setting: what "Off" does must read the same in both.
        Assert.Contains(OffSentence(LobbyWords.ShareAchievementsTip), T2TOnline.Core.SettingsFiles.SettingDescriptions.ShareAchievements, System.StringComparison.Ordinal);
        Assert.Contains(OffSentence(LobbyWords.ShareProgressTip), T2TOnline.Core.SettingsFiles.SettingDescriptions.ShareProgress, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_translations_say_they_are_drafts()
    {
        Assert.False(LobbyWords.TranslationsReviewed);
    }

    private static string OffSentence(LobbyWord tooltip)
    {
        string english = tooltip.Texts[0];
        int off = english.IndexOf("Off: ", System.StringComparison.Ordinal);
        Assert.True(off >= 0, tooltip.Key);
        return english[off..];
    }
}
