using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class LogHeaderTests
{
    private static readonly DateTime Day = new(2026, 11, 5, 23, 59, 59, DateTimeKind.Utc);

    [Fact]
    public void The_header_names_the_build_the_protocol_the_format_and_the_utc_date()
    {
        Assert.Equal("T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-05.",
            LogHeader.Line("0.11.0-beta", "0123456789ab", 9, Day));
        Assert.Equal("T2T Online 0.11.0-beta, commit unknown, protocol 9, log format 2; UTC date 2026-11-05.",
            LogHeader.Line("0.11.0-beta", string.Empty, 9, Day));
    }

    [Fact]
    public void The_format_is_read_back_from_a_files_first_line()
    {
        string header = LogHeader.Line("0.11.0-beta", "0123456789ab", 9, Day);
        Assert.Equal(LogHeader.Format, LogHeader.FormatOf(header));
        Assert.Equal(LogHeader.Format, LogHeader.FormatOf("00:00:00.001 INFO  " + header));
        Assert.Equal(3, LogHeader.FormatOf("T2T Online 0.12.0-beta, commit x, protocol 10, log format 3; UTC date 2027-01-01."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("03:10:00.001 INFO  T2T Online 0.10.0-beta, commit 0123456789ab starting (build 0.10.0-beta+0123456).")]
    [InlineData("03:10:00.001 INFO  Session logs: deleted the 2 oldest")]
    [InlineData("a log format 2 line that isn't ours")]
    public void A_file_from_an_older_build_has_no_format(string? firstLine)
    {
        Assert.Equal(0, LogHeader.FormatOf(firstLine));
    }

    [Fact]
    public void The_date_line_says_midnight_passed()
    {
        Assert.Equal("UTC date 2026-11-06 (the session went past midnight UTC).", LogHeader.DateLine(Day.AddSeconds(1)));
    }
}
