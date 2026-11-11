using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class ScreenNameTests
{
    private const string Backslash = "\\";

    [Theory]
    [InlineData("Riley")]
    [InlineData("Casey Park")]
    [InlineData("Жора")]
    [InlineData("玩家一号")]
    [InlineData("Sam_99 [TTT]")]
    [InlineData("Alex \U0001F3D2")]
    [InlineData("Player 1")]
    [InlineData("<b>Riley</b>")]
    public void An_ordinary_name_shows_as_it_did(string name)
    {
        Assert.Equal(SafeText.Clean(name), ScreenName.Of(name, 64));
        Assert.Equal(SafeText.Clean(name, 32), ScreenName.Of(name, 32));
    }

    [Fact]
    public void A_typed_escape_cant_reach_a_toast_or_a_prompt()
    {
        string name = "Riley" + Backslash + "u" + "003C/noparse><size=999>"; // a typed escape for '<'
        string shown = ScreenName.Of(name, 64);
        Assert.DoesNotContain("\\", shown, System.StringComparison.Ordinal);
        Assert.Contains(@"\", SafeText.Clean(name), System.StringComparison.Ordinal); // what the screens had before
        Assert.DoesNotContain("\\", ScreenName.Of(@"Casey\nPark", 64), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Direction_and_control_characters_go_rather_than_show_as_question_marks()
    {
        Assert.Equal("Riley", ScreenName.Of("\u202ERiley\u200F", 64));
        Assert.Equal("Riley Park", ScreenName.Of("Riley\tPark", 64));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(DisplayName.SteamUnknown, DisplayName.SteamUnknown)]
    [InlineData("\u0301\u0301", "\u0301\u0301")]
    [InlineData("\u200B", "\u200B")]
    public void A_name_with_nothing_drawable_left_comes_back_as_before_without_a_stand_in(string? raw, string expected)
    {
        Assert.Equal(expected, ScreenName.Of(raw, 64));
        Assert.Equal(SafeText.Clean(raw), ScreenName.Of(raw, 64));
    }

    [Fact]
    public void A_long_name_is_cut_with_an_ellipsis()
    {
        string shown = ScreenName.Of(new string('a', 40), 32);
        Assert.Equal(32, shown.Length);
        Assert.EndsWith("\u2026", shown, System.StringComparison.Ordinal);
    }
}
