using System;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class DisplayNameTests
{
    private static string Clean(string? raw, Func<int, bool>? canDraw = null) => DisplayName.Clean(raw, 2, 0, canDraw);

    [Fact]
    public void A_plain_name_stays_as_it_is()
    {
        Assert.Equal("Riley", Clean("Riley"));
        Assert.Equal("Casey Park", Clean("Casey Park"));
        Assert.Equal("Жора", Clean("Жора"));
        Assert.Equal("玩家一号", Clean("玩家一号"));
    }

    [Fact]
    public void Tags_stay_as_text_so_a_label_with_rich_text_off_shows_them_harmlessly()
    {
        Assert.Equal("<color=red>x</color>", Clean("<color=red>x</color>"));
        Assert.Equal("<size=500>big", Clean("<size=500>big"));
        Assert.Equal("<sprite=0>", Clean("<sprite=0>"));
    }

    [Fact]
    public void Typed_escapes_lose_their_backslash_and_real_line_breaks_become_spaces()
    {
        Assert.Equal("anb", Clean("a\\nb"));
        Assert.Equal("atb", Clean("a\\tb"));
        Assert.Equal("a b", Clean("a\nb"));
        Assert.Equal("a b", Clean("a\r\n\tb"));
        Assert.Equal("a b", Clean("a\u2028b"));
    }

    [Fact]
    public void Spaces_are_tidied()
    {
        Assert.Equal("a b", Clean("  a    b  "));
        Assert.Equal("a b", Clean("a\u00A0\u3000b"));
    }

    [Fact]
    public void Zero_width_format_characters_go()
    {
        Assert.Equal("ab", Clean("a\u200Bb"));
        Assert.Equal("ab", Clean("a\u200Db"));
        Assert.Equal("ab", Clean("a\uFEFFb"));
        Assert.Equal("ab", Clean("a\u00ADb"));
        Assert.Equal("ab", Clean("a\U000E0041b")); // a tag character
    }

    [Fact]
    public void Zalgo_loses_its_marks_after_nfc_keeps_what_composed()
    {
        Assert.Equal("Zalgo", Clean("Z\u0351\u036B\u0343a\u0300\u0301\u0302\u0303\u0304l\u0334g\u0335o\u0336").Replace("\u00C0", "a", StringComparison.Ordinal).Replace("à", "a", StringComparison.Ordinal));
        Assert.Equal("é", Clean("e\u0301")); // composed: a plain letter that the fonts have
        Assert.Equal("Ñandú", Clean("N\u0303andu\u0301"));
        Assert.Equal("a", Clean("a\u20DD")); // an enclosing mark
    }

    [Fact]
    public void Bidi_control_and_private_use_characters_go()
    {
        Assert.Equal("abc", Clean("a\u202Eb\u202Cc"));
        Assert.Equal("ab", Clean("a\u2066b\u2069"));
        Assert.Equal("ab", Clean("a\u200Fb"));
        Assert.Equal("ab", Clean("a\u0007b"));
        Assert.Equal("ab", Clean("a\uE000b"));
        Assert.Equal("ab", Clean("a\U000F0000b"));
    }

    [Fact]
    public void Lone_surrogates_cant_throw_and_go()
    {
        Assert.Equal("ab", Clean("a\uD83Db"));
        Assert.Equal("ab", Clean("a\uDE00b"));
        Assert.Equal("Player 2", Clean("\uD83D"));
    }

    [Fact]
    public void Emoji_stay_if_a_font_can_draw_them_and_go_if_not()
    {
        Assert.Equal("a😀b", Clean("a😀b"));
        Assert.Equal("ab", Clean("a😀b", canDraw: c => c < 0x10000));
    }

    [Fact]
    public void Right_to_left_text_stays_with_its_override_gone_or_goes_if_it_cant_be_drawn()
    {
        Assert.Equal("שלום", Clean("\u202Bשלום\u202C"));
        Assert.Equal("Player 2", Clean("שלום", canDraw: c => c < 0x100));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t ")]
    [InlineData("\u200B\u200D")]
    [InlineData("\u0301\u0302")]
    [InlineData("\u202E")]
    [InlineData("\\")]
    [InlineData(DisplayName.SteamUnknown)]
    public void A_name_with_nothing_left_is_never_blank(string? raw)
    {
        Assert.Equal("Player 2", Clean(raw));
    }

    [Fact]
    public void The_fallback_is_in_the_players_language_with_their_number()
    {
        Assert.Equal("玩家 3", DisplayName.Clean(null, 3, 9));
        Assert.Equal("Joueur 1", DisplayName.Clean("  ", 1, 1));
        Assert.Equal("Player 4", DisplayName.Clean(null, 4, 99)); // a language it doesn't know
    }

    [Fact]
    public void A_name_no_font_can_draw_falls_back()
    {
        Assert.Equal("Player 2", Clean("玩家一号", canDraw: c => c < 0x100));
    }

    [Fact]
    public void A_callback_that_throws_keeps_the_name()
    {
        Assert.Equal("Sam", Clean("Sam", canDraw: _ => throw new InvalidOperationException("the font's gone")));
    }

    [Fact]
    public void The_callback_is_asked_about_each_code_point_but_never_a_space()
    {
        var asked = new System.Collections.Generic.List<int>();
        Clean("a b😀", canDraw: c =>
        {
            asked.Add(c);
            return true;
        });

        Assert.Equal(new[] { 'a', 'b', 0x1F600 }, asked);
    }

    [Fact]
    public void An_overlong_name_is_cut_with_an_ellipsis_at_its_cap_and_never_inside_a_character()
    {
        string cut = Clean(new string('W', 100));
        Assert.Equal(DisplayName.MaxChars, cut.Length);
        Assert.EndsWith("\u2026", cut);

        string pairs = Clean(string.Concat(Enumerable.Repeat("😀", 40)));
        Assert.True(pairs.Length <= DisplayName.MaxChars);
        Assert.EndsWith("\u2026", pairs);
        Assert.False(char.IsHighSurrogate(pairs[^2]));

        Assert.Equal(new string('a', 31) + "\u2026", DisplayName.Clean(new string('a', 40), 1));
        Assert.Equal("aaa\u2026", DisplayName.Clean("aaaaaa", 1, maxChars: 4));
        Assert.Equal("a\u2026", DisplayName.Clean("aaaaaa", 1, maxChars: 0)); // the cut has a floor
    }

    [Fact]
    public void For_rich_text_a_name_goes_in_noparse_and_cant_close_it()
    {
        Assert.Equal("<noparse>Sam</noparse>", DisplayName.ForRichText("Sam"));
        Assert.Equal("<noparse><color=red>x</noparse>", DisplayName.ForRichText("<color=red>x"));
        Assert.Equal("<noparse>x>y</noparse>", DisplayName.ForRichText("x</noparse>y"));
        Assert.Equal("<noparse>x>y</noparse>", DisplayName.ForRichText("x</NOPARSE>y"));
        Assert.Equal("<noparse>x>>y</noparse>", DisplayName.ForRichText("x<</noparse/noparse>>y"));
        Assert.Equal(string.Empty, DisplayName.ForRichText(string.Empty));
        Assert.Equal(string.Empty, DisplayName.ForRichText(null!));
    }

    [Fact]
    public void For_rich_text_a_typed_escape_cant_make_a_tag_or_a_line_break()
    {
        // TextMesh Pro reads a backslash, u and four hex digits (or U and eight) as that character before it reads tags, whatever its
        // settings: a name holding "<" that way would end the noparse. With its control-character parsing on, n, r, t and v are line breaks.
        const string Bs = "\\";
        string escaped = DisplayName.ForRichText(Bs + "u003C/noparse>" + Bs + "u003Csize=999>");
        Assert.Equal("<noparse>u003C/noparse>u003Csize=999></noparse>", escaped);
        Assert.Equal("<noparse>U0000003Cb></noparse>", DisplayName.ForRichText(Bs + "U0000003Cb>"));
        Assert.Equal("<noparse>u003C</noparse>", DisplayName.ForRichText(Bs + Bs + "u003C")); // a run of backslashes goes whole
        Assert.Equal("<noparse>anbrctdve</noparse>", DisplayName.ForRichText("a" + Bs + "nb" + Bs + "rc" + Bs + "td" + Bs + "ve"));

        // Taking one out can make the other: both again until neither is left.
        Assert.Equal("<noparse>u003C</noparse>", DisplayName.ForRichText(Bs + "</noparseu003C"));
        Assert.Equal("<noparse>x>y</noparse>", DisplayName.ForRichText("x</" + Bs + "noparse>y"));
    }

    [Fact]
    public void For_rich_text_a_backslash_that_starts_no_escape_stays()
    {
        const string Bs = "\\";
        Assert.Equal("<noparse>Plugins" + Bs + "T2TOnline.dll</noparse>", DisplayName.ForRichText("Plugins" + Bs + "T2TOnline.dll"));
        Assert.Equal("<noparse>UserLibs" + Bs + "T2TOnline.Core.dll</noparse>", DisplayName.ForRichText("UserLibs" + Bs + "T2TOnline.Core.dll"));
        Assert.Equal("<noparse>a" + Bs + "u12</noparse>", DisplayName.ForRichText("a" + Bs + "u12")); // too few hex digits: no escape
        Assert.Equal("<noparse>a" + Bs + "</noparse>", DisplayName.ForRichText("a" + Bs));
    }

    [Fact]
    public void A_lines_arguments_for_rich_text_lose_their_escapes()
    {
        LobbyLine line = new LobbyLine(LobbyWords.Joined, "\\" + "u003C/noparse><b>").ForRichText();

        Assert.Equal("<noparse>u003C/noparse><b></noparse> joined", line.Text(0));
    }

    [Fact]
    public void Random_text_never_throws_and_never_comes_out_blank()
    {
        var random = new Random(555);
        for (int i = 0; i < 3000; i++)
        {
            char[] chars = new char[random.Next(0, 80)];
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = (char)random.Next(0, 0x10000);
            }

            string name = DisplayName.Clean(new string(chars), 2);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(name.Length <= DisplayName.MaxChars);
            Assert.DoesNotContain("\n", name);
        }
    }
}
