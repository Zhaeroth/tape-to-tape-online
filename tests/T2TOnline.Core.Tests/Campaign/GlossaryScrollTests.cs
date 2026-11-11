using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class GlossaryScrollTests
{
    [Theory]
    [InlineData(500f, 100f, 500f, 0)] // at the top
    [InlineData(500f, 100f, 100f, 1000)] // at the bottom
    [InlineData(500f, 100f, 252f, 620)]
    [InlineData(500f, 100f, 600f, -250)] // scrolled above the list
    [InlineData(500f, 100f, -300f, 2000)] // below it
    [InlineData(500f, 100f, 100000f, -GlossaryScroll.Limit)] // far off: clamped
    [InlineData(500f, 100f, -100000f, GlossaryScroll.Limit)]
    [InlineData(-10f, -410f, -133.6f, 309)] // world heights below 0 work the same
    public void A_rows_place_is_in_thousandths_of_the_list(float top, float bottom, float row, int milli) =>
        Assert.Equal(milli, GlossaryScroll.Milli(top, bottom, row));

    [Theory]
    [InlineData(100f, 100f, 50f)] // no height
    [InlineData(100f, 200f, 50f)] // upside down
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(100f, float.NegativeInfinity, 0f)]
    [InlineData(100f, 0f, float.PositiveInfinity)]
    public void No_place_without_a_list_of_some_height(float top, float bottom, float row) => Assert.Null(GlossaryScroll.Milli(top, bottom, row));

    [Fact]
    public void The_shift_puts_the_row_where_the_hosts_sits()
    {
        // Here the row is at 20% of a 400-high list, the host's at 62%: the content moves down 168 so the row sits at 62%.
        float shift = GlossaryScroll.Shift(host: 620, here: 200, areaHeight: 400f);
        Assert.Equal(-168f, shift, 3);
        Assert.Equal(620, GlossaryScroll.Milli(500f, 100f, 500f - 80f + shift));

        Assert.Equal(0f, GlossaryScroll.Shift(300, 300, 400f));
        Assert.Equal(40f, GlossaryScroll.Shift(0, 100, 400f), 3); // here lower than the host's: up
        Assert.Equal(0f, GlossaryScroll.Shift(100, 0, 0f)); // no height, no move
        Assert.Equal(0f, GlossaryScroll.Shift(100, 0, float.NaN));
    }

    [Fact]
    public void Only_a_move_of_some_size_is_sent_again()
    {
        Assert.True(GlossaryScroll.Moved(null, 0));
        Assert.False(GlossaryScroll.Moved(500, 504));
        Assert.False(GlossaryScroll.Moved(500, 496));
        Assert.True(GlossaryScroll.Moved(500, 505));
        Assert.True(GlossaryScroll.Moved(500, 495));
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-5000L, true)]
    [InlineData(5000L, true)]
    [InlineData(5001L, false)]
    [InlineData(-5001L, false)]
    [InlineData(long.MaxValue, false)]
    public void Only_values_within_the_limit_are_read(long value, bool read)
    {
        Assert.Equal(read, GlossaryScroll.TryRead(value, out int milli));
        Assert.Equal(read ? (int)value : 0, milli);
    }

    [Theory]
    [InlineData(620, "62% of the list's height")]
    [InlineData(0, "0% of the list's height")]
    [InlineData(1000, "100% of the list's height")]
    [InlineData(-1, "above the list")]
    [InlineData(1001, "below the list")]
    public void Places_read_as_words(int milli, string words) => Assert.Equal(words, GlossaryScroll.Describe(milli));
}
