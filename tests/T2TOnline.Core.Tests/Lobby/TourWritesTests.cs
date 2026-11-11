using System;
using System.IO;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TourWritesTests
{
    // A tour's folder somewhere rooted on any platform; nothing here touches the disk.
    private static readonly string Tour = Path.Combine(Path.GetTempPath(), "T2TOnline", "dev", "ui-check", "20261106T120000Z");
    private static readonly string RunFile = TourWrites.RunFileIn(Tour);

    [Fact]
    public void The_run_file_is_named_in_the_tours_folder()
    {
        Assert.Equal("tour-run.es3", TourWrites.RunFileName);
        Assert.Equal(Path.Combine(Tour, "tour-run.es3"), RunFile);
        Assert.Throws<ArgumentNullException>(() => TourWrites.RunFileIn(null!));
        Assert.Throws<ArgumentException>(() => TourWrites.RunFileIn(string.Empty));
    }

    [Theory]
    [InlineData("tour-run.es3")]
    [InlineData("tour-run.es3.tmp")]
    [InlineData("tour-run.es3.bac")]
    [InlineData("tour-run.es3.tmp.bak")]
    [InlineData("TOUR-RUN.ES3")]
    [InlineData("Tour-Run.es3.TMP")]
    public void The_tours_run_file_and_easy_saves_copies_of_it_pass(string name)
    {
        Assert.True(TourWrites.Allows(RunFile, Path.Combine(Tour, name)));
    }

    [Theory]
    [InlineData("tour-run.es3.bac.tmp")]
    [InlineData("tour-run.es3.part")]
    [InlineData("tour-run.es")]
    [InlineData("tour-run.es3x")]
    [InlineData("xtour-run.es3")]
    [InlineData("RunDataV2.es3")]
    [InlineData("profileV2.es3")]
    [InlineData("coop-1.es3")]
    public void Any_other_file_in_the_tours_folder_is_refused(string name)
    {
        Assert.False(TourWrites.Allows(RunFile, Path.Combine(Tour, name)));
    }

    [Fact]
    public void The_same_name_anywhere_else_or_bare_is_refused()
    {
        string otherTour = Path.Combine(Path.GetTempPath(), "T2TOnline", "dev", "ui-check", "20261106T110000Z");
        Assert.False(TourWrites.Allows(RunFile, Path.Combine(otherTour, "tour-run.es3")));
        Assert.False(TourWrites.Allows(RunFile, Path.Combine(Path.GetTempPath(), "T2T Online", "coop", "tour-run.es3")));
        Assert.False(TourWrites.Allows(RunFile, "tour-run.es3"));
        Assert.False(TourWrites.Allows(RunFile, Path.Combine("dev", "ui-check", "20261106T120000Z", "tour-run.es3")));
    }

    [Fact]
    public void A_path_through_dot_dot_counts_as_the_file_it_names()
    {
        string roundabout = Path.Combine(Tour, "..", Path.GetFileName(Tour), "tour-run.es3");
        Assert.True(TourWrites.Allows(RunFile, roundabout));
        Assert.False(TourWrites.Allows(RunFile, Path.Combine(Tour, "..", "tour-run.es3")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tour-run.es3")]
    public void Without_a_rooted_tour_run_file_nothing_passes(string? tourRunFile)
    {
        Assert.False(TourWrites.Allows(tourRunFile, RunFile));
    }

    [Fact]
    public void An_empty_or_unreadable_path_is_refused_without_throwing()
    {
        Assert.False(TourWrites.Allows(RunFile, null));
        Assert.False(TourWrites.Allows(RunFile, string.Empty));
        Assert.False(TourWrites.Allows(RunFile, Path.Combine(Tour, "tour\0run.es3")));
    }

    [Fact]
    public void A_write_passes_only_when_it_names_a_file_and_every_file_is_the_tours()
    {
        string tmp = Path.Combine(Tour, "tour-run.es3.tmp");
        Assert.True(TourWrites.AllowsWrite(RunFile, new[] { tmp, RunFile }));
        Assert.True(TourWrites.AllowsWrite(RunFile, new[] { null, RunFile }));
        Assert.False(TourWrites.AllowsWrite(RunFile, Array.Empty<string?>()));
        Assert.False(TourWrites.AllowsWrite(RunFile, new string?[] { null, null }));
        Assert.False(TourWrites.AllowsWrite(RunFile, new[] { RunFile, Path.Combine(Tour, "profileV2.es3") }));
        Assert.False(TourWrites.AllowsWrite(RunFile, new[] { RunFile, string.Empty }));
        Assert.False(TourWrites.AllowsWrite(null, new[] { RunFile }));
        Assert.Throws<ArgumentNullException>(() => TourWrites.AllowsWrite(RunFile, null!));
    }
}
