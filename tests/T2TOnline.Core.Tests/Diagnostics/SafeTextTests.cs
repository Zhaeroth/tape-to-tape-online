using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class SafeTextTests
{
    private static string C(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Fact]
    public void Plain_text_comes_back_as_is()
    {
        Assert.Equal("0.5.0-m3", SafeText.Clean("0.5.0-m3"));
        Assert.Equal(string.Empty, SafeText.Clean(null));
        Assert.Equal(string.Empty, SafeText.Clean(string.Empty));
    }

    [Fact]
    public void Line_breaks_and_controls_cannot_forge_a_log_line()
    {
        Assert.Equal("v1??12:00 INFO  fake", SafeText.Clean("v1\r\n12:00 INFO  fake"));
        Assert.Equal("a?b?c?d", SafeText.Clean("a" + C(0x2028) + "b" + C(0x85) + "c" + C(0x1B) + "d"));
    }

    [Fact]
    public void Bidi_overrides_and_the_status_box_markup_are_replaced()
    {
        Assert.Equal("?evil", SafeText.Clean(C(0x202E) + "evil"));
        Assert.Equal("?FF0000?red?", SafeText.Clean(C(0xE000) + "FF0000" + C(0xE001) + "red" + C(0xE002)));
        Assert.Equal("a?b", SafeText.Clean("a" + C(0x2067) + "b"));
    }

    [Fact]
    public void Emoji_stay_but_a_lone_surrogate_does_not()
    {
        string skater = C(0x26F8) + C(0x1F3D2);
        Assert.Equal(skater, SafeText.Clean(skater));
        Assert.Equal("a?b", SafeText.Clean("a" + (char)0xD83C + "b"));
    }

    [Fact]
    public void Long_text_is_cut_with_an_ellipsis_never_splitting_a_pair()
    {
        Assert.Equal("abcd" + C(0x2026), SafeText.Clean("abcdefgh", 5));
        string cut = SafeText.Clean("abc" + C(0x1F3D2) + "defg", 5);
        Assert.Equal("abc" + C(0x2026), cut);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => SafeText.Clean("x", 1));
    }
}
