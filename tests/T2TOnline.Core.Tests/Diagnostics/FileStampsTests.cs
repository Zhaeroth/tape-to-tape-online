using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class FileStampsTests
{
    private static readonly DateTime Evening = new(2026, 10, 30, 17, 50, 0, DateTimeKind.Utc);

    [Fact]
    public void Unchanged_files_report_nothing()
    {
        var before = new[] { new FileStamp("a/Analytics/values", 140, Evening), new FileStamp("a/Analytics/config", 293, Evening) };
        var after = new[] { new FileStamp("a/Analytics/config", 293, Evening), new FileStamp("a/Analytics/values", 140, Evening) };
        Assert.Empty(FileStamps.Changed(before, after));
    }

    [Fact]
    public void New_rewritten_and_gone_files_are_named_in_path_order()
    {
        var before = new[]
        {
            new FileStamp("b/values", 140, Evening),
            new FileStamp("c/config", 293, Evening),
            new FileStamp("d/old", 10, Evening),
        };
        var after = new[]
        {
            new FileStamp("b/values", 140, Evening.AddMinutes(1)),
            new FileStamp("c/config", 300, Evening),
            new FileStamp("a/ArchivedEvents/1", 5, Evening),
        };
        Assert.Equal(new[] { "a/ArchivedEvents/1 (new)", "b/values (rewritten)", "c/config (rewritten)", "d/old (gone)" }, FileStamps.Changed(before, after));
    }

    [Fact]
    public void Paths_compare_without_case_and_repeats_count_once()
    {
        var before = new[] { new FileStamp("Unity/Analytics/values", 140, Evening) };
        var after = new[] { new FileStamp("unity/analytics/VALUES", 140, Evening), new FileStamp("UNITY/Analytics/values", 999, Evening) };
        Assert.Empty(FileStamps.Changed(before, after));
    }

    [Fact]
    public void Nothing_before_and_after_is_no_change()
    {
        Assert.Empty(FileStamps.Changed(Array.Empty<FileStamp>(), Array.Empty<FileStamp>()));
    }

    [Fact]
    public void Newest_is_the_latest_write_or_null()
    {
        Assert.Null(FileStamps.Newest(Array.Empty<FileStamp>()));
        var stamps = new[] { new FileStamp("x", 1, Evening), new FileStamp("y", 1, Evening.AddHours(-5)), new FileStamp("z", 1, Evening.AddSeconds(3)) };
        Assert.Equal(Evening.AddSeconds(3), FileStamps.Newest(stamps));
    }

    [Fact]
    public void Null_inputs_throw()
    {
        Assert.Throws<ArgumentNullException>(() => FileStamps.Changed(null!, Array.Empty<FileStamp>()));
        Assert.Throws<ArgumentNullException>(() => FileStamps.Changed(Array.Empty<FileStamp>(), null!));
        Assert.Throws<ArgumentNullException>(() => FileStamps.Newest(null!));
    }
}
