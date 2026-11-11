using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>What another PC sent, as a log line may name it: its shape, or its length (a lobby's keys, a hello's release).</summary>
public class SentTextTests
{
    [Theory]
    [InlineData("0.12.0-beta")]
    [InlineData("0.9.0-m4")]
    [InlineData("1.0.0")]
    [InlineData("0.12.0-beta+3c337e6f")]
    public void A_release_is_written_as_it_is(string release) => Assert.Equal(release, SentText.Release(release));

    [Theory]
    [InlineData("Alex", 4)] // a name, not a release
    [InlineData("0.12 Riley says hi", 18)] // a space: words
    [InlineData("0.12<color=red>", 15)] // markup
    [InlineData("0.1\nINFO  Online: forged", 24)] // a line break
    [InlineData("0.12.0-beta-and-a-very-long-tail-of-text", 40)] // past 32
    [InlineData("0.12.0-béta", 11)] // a letter outside ASCII
    public void Anything_else_is_written_as_its_length(string sent, int length) =>
        Assert.Equal($"a value of {length} character(s)", SentText.Release(sent));

    [Fact]
    public void Nothing_is_none()
    {
        Assert.Equal("(none)", SentText.Release(null));
        Assert.Equal("(none)", SentText.Release(string.Empty));
        Assert.Equal("(none)", SentText.Number(string.Empty));
        Assert.Equal("(none)", SentText.Hex(null, 16));
    }

    [Fact]
    public void A_protocol_is_digits_only()
    {
        Assert.Equal("9", SentText.Number("9"));
        Assert.Equal("123456789", SentText.Number("123456789"));
        Assert.Equal("a value of 10 character(s)", SentText.Number("1234567890"));
        Assert.Equal("a value of 4 character(s)", SentText.Number("nine"));
        Assert.Equal("a value of 2 character(s)", SentText.Number("-9"));
    }

    [Fact]
    public void A_game_build_is_exactly_its_hex_digits()
    {
        Assert.Equal("f756bcb8564c2d95", SentText.Hex("f756bcb8564c2d95", 16));
        Assert.Equal("a value of 15 character(s)", SentText.Hex("f756bcb8564c2d9", 16));
        Assert.Equal("a value of 16 character(s)", SentText.Hex("F756BCB8564C2D95", 16)); // the mod writes lower case
        Assert.Equal("a value of 16 character(s)", SentText.Hex("hello there you!", 16));
    }
}
