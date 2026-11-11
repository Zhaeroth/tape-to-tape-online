using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The GC and heap figures in the cost reports and the 10-minute memory line.</summary>
public class MemorySampleTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void The_first_sample_reads_counts_and_heaps_without_growth()
    {
        var sample = new MemorySample(12, 3, 1, 48 * Mb, 340, 512 * Mb);
        Assert.Equal("GC .NET 12/3/1, IL2CPP 340; heaps .NET 48.0 MB, IL2CPP 512.0 MB", sample.Describe(null));
    }

    [Fact]
    public void A_later_sample_says_how_many_more_collections_ran()
    {
        var before = new MemorySample(10, 3, 1, 40 * Mb, 334, 500 * Mb);
        var now = new MemorySample(12, 3, 1, 48 * Mb, 340, 512 * Mb);
        Assert.Equal("GC .NET 12/3/1 (+2), IL2CPP 340 (+6); heaps .NET 48.0 MB, IL2CPP 512.0 MB", now.Describe(before));
    }

    [Fact]
    public void An_unreadable_il2cpp_side_says_so()
    {
        var before = new MemorySample(1, 0, 0, Mb, -1, -1);
        var now = new MemorySample(2, 0, 0, Mb, -1, -1);
        Assert.Equal("GC .NET 2/0/0 (+1), IL2CPP unreadable; heaps .NET 1.0 MB, IL2CPP unreadable", now.Describe(before));
        Assert.Equal("IL2CPP heap unreadable, .NET 1.0 MB (0.0)", now.DescribeGrowth(before));
    }

    [Fact]
    public void Growth_is_against_the_first_sample_either_way()
    {
        var first = new MemorySample(0, 0, 0, 40 * Mb, 0, 500 * Mb);
        Assert.Equal("IL2CPP heap 512.5 MB (+12.5 since the first sample), .NET 39.5 MB (-0.5)",
            new MemorySample(9, 1, 0, (long)(39.5 * Mb), 80, (long)(512.5 * Mb)).DescribeGrowth(first));
    }

    [Fact]
    public void Counts_that_went_down_never_read_negative() =>
        Assert.Contains("(+0)", new MemorySample(1, 0, 0, Mb, 2, Mb).Describe(new MemorySample(5, 0, 0, Mb, 9, Mb)));
}
