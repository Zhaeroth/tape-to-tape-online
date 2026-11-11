using System;
using System.Threading.Tasks;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class ErrorTallyTests
{
    [Fact]
    public void Errors_are_counted_since_the_mark_and_the_first_one_after_it_kept()
    {
        var tally = new ErrorTally();
        tally.Note("before the tour");
        Assert.Equal(1, tally.Total);
        Assert.Null(tally.FirstSinceMark);

        tally.Mark();
        Assert.Equal(0, tally.SinceMark);
        tally.Note("Online: the session failed");
        tally.Note("Online: the session failed again");
        Assert.Equal(2, tally.SinceMark);
        Assert.Equal(3, tally.Total);
        Assert.Equal("Online: the session failed", tally.FirstSinceMark);

        tally.Mark();
        Assert.Equal(0, tally.SinceMark);
        Assert.Null(tally.FirstSinceMark);
    }

    [Fact]
    public void The_first_line_is_cut()
    {
        var tally = new ErrorTally();
        tally.Mark();
        tally.Note(new string('e', 500));
        Assert.Equal(ErrorTally.MaxLineLength, tally.FirstSinceMark!.Length);
        Assert.EndsWith("...", tally.FirstSinceMark, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(2, 2, null)]
    [InlineData(1, 3, null)]
    [InlineData(1, 0, "1 ERROR line(s) since the tour started, none staged by its steps")]
    [InlineData(3, 2, "3 ERROR line(s) since the tour started, 2 of them staged by its steps")]
    [InlineData(1, -4, "1 ERROR line(s) since the tour started, none staged by its steps")]
    public void A_tour_fails_on_more_errors_than_it_staged(long errors, int staged, string? verdict)
    {
        Assert.Equal(verdict, ErrorTally.Verdict(errors, staged));
    }

    [Fact]
    public void Counting_from_many_threads_loses_nothing()
    {
        var tally = new ErrorTally();
        tally.Mark();
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 1000; i++)
            {
                tally.Note("x");
            }
        });
        Assert.Equal(8000, tally.SinceMark);
    }
}
