using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class NameShapeTests
{
    private static string C(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Fact]
    public void A_latin_name_says_its_length_and_script_only()
    {
        Assert.Equal("P#a0c1d2's Steam name: 4 characters, Latin.", NameShape.Describe("P#a0c1d2", "Alex"));
        Assert.Equal("P#a0c1d2's Steam name: 9 characters, Latin and digits.", NameShape.Describe("P#a0c1d2", "Sam 12345"));
    }

    [Fact]
    public void Scripts_unsafe_characters_and_the_fonts_are_counted()
    {
        string name = "Riley Рили" + C(0x202E) + C(0x1F3D2) + "李";
        string line = NameShape.Describe("P#3fa9c1", name, codePoint => codePoint < 0x3000);
        Assert.Equal("P#3fa9c1's Steam name: 13 characters, Latin, Cyrillic, emoji and Han, 1 control or direction character (never shown), 2 the game's fonts can't draw.", line);
        Assert.DoesNotContain("Riley", line);
    }

    [Fact]
    public void Everything_drawn_is_said_and_a_failing_font_check_counts_nothing()
    {
        Assert.Equal("P#a0c1d2's Steam name: 2 characters, Latin, all drawn by the game's fonts.", NameShape.Describe("P#a0c1d2", "Al", _ => true));
        Assert.Equal("P#a0c1d2's Steam name: 2 characters, Latin, all drawn by the game's fonts.",
            NameShape.Describe("P#a0c1d2", "Al", _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[unknown]")]
    public void A_name_steam_doesnt_know_yet_says_so(string? name)
    {
        Assert.Equal("P#a0c1d2's Steam name: not known on this PC yet.", NameShape.Describe("P#a0c1d2", name));
    }

    [Theory]
    [InlineData('A', "Latin")]
    [InlineData('é', "Latin")]
    [InlineData('×', "symbols")]
    [InlineData('Ω', "Greek")]
    [InlineData('א', "Hebrew")]
    [InlineData('ا', "Arabic")]
    [InlineData('ก', "Thai")]
    [InlineData('가', "Hangul")]
    [InlineData('あ', "Hiragana")]
    [InlineData('ア', "Katakana")]
    [InlineData('അ', "other letters")]
    [InlineData('_', "symbols")]
    [InlineData(' ', "spaces")]
    public void Scripts_are_named(char c, string script)
    {
        Assert.Equal(script, NameShape.ScriptOf(c));
    }
}
