using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class SampleWindowTests
{
    [Fact]
    public void Empty_window_summarizes_to_zero() => Assert.Equal(default, new SampleWindow().Summarize());

    [Fact]
    public void Summarizes_mean_p95_and_max()
    {
        var window = new SampleWindow();
        for (int i = 1; i <= 100; i++)
        {
            window.Add(i);
        }

        SampleSummary summary = window.Summarize();
        Assert.Equal(100, summary.Count);
        Assert.Equal(50.5, summary.Mean);
        Assert.Equal(95, summary.P95);
        Assert.Equal(100, summary.Max);
    }

    [Fact]
    public void Single_sample_is_its_own_percentile()
    {
        var window = new SampleWindow();
        window.Add(7);
        Assert.Equal(new SampleSummary(1, 7, 7, 7), window.Summarize());
    }

    [Fact]
    public void Clear_empties_the_window()
    {
        var window = new SampleWindow();
        window.Add(3);
        window.Clear();
        Assert.Equal(0, window.Count);
    }
}
