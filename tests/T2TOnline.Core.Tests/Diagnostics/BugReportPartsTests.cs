using System;
using System.IO;
using System.Linq;
using System.Text;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class ReportExcerptsTests
{
    private static MemoryStream Of(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void A_file_that_fits_is_read_whole()
    {
        ReportExcerpt excerpt = ReportExcerpts.Read(Of("one\ntwo\r\nthree"), 1024);

        Assert.Equal("one\ntwo\r\nthree", excerpt.Text);
        Assert.Equal(0, excerpt.LeftOutBytes);
        Assert.Equal(14, excerpt.SourceBytes);
    }

    [Fact]
    public void A_byte_order_mark_is_dropped()
    {
        var stream = new MemoryStream(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' });
        Assert.Equal("a\n", ReportExcerpts.Read(stream, 64).Text);
    }

    [Fact]
    public void A_long_file_keeps_its_start_and_end_cut_at_line_ends_and_says_what_was_left_out()
    {
        string text = string.Concat(Enumerable.Range(0, 1000).Select(i => $"line {i:0000}\n")); // 10 bytes a line
        ReportExcerpt excerpt = ReportExcerpts.Read(Of(text), 400);

        string[] lines = excerpt.Text.Split('\n');
        Assert.Equal("line 0000", lines[0]);
        Assert.Equal("line 0999", lines[^2]);
        Assert.Equal(string.Empty, lines[^1]);
        Assert.Contains(lines, l => l.StartsWith("----- T2T Online bug report: ", StringComparison.Ordinal) && l.Contains("bytes of the middle", StringComparison.Ordinal));
        Assert.All(lines.Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)), l => Assert.Matches(@"^line \d{4}$", l));
        Assert.True(excerpt.LeftOutBytes > 9000);
        Assert.Equal(text.Length, excerpt.SourceBytes);

        // About a quarter from the start, three quarters from the end.
        int head = lines.TakeWhile(l => !l.StartsWith("-----", StringComparison.Ordinal)).Count();
        Assert.InRange(head, 9, 10);
        Assert.InRange(lines.Length - head - 2, 28, 30);
    }

    [Fact]
    public void Multi_byte_characters_are_never_split()
    {
        string text = string.Concat(Enumerable.Range(0, 200).Select(i => $"Ünïcødé 日本 {i}\n"));
        ReportExcerpt excerpt = ReportExcerpts.Read(Of(text), 300);

        Assert.DoesNotContain('\uFFFD', excerpt.Text);
        Assert.StartsWith("Ünïcødé 日本 0\n", excerpt.Text, StringComparison.Ordinal);
        Assert.EndsWith("Ünïcødé 日本 199\n", excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void One_very_long_line_still_gets_the_left_out_line_on_its_own()
    {
        ReportExcerpt excerpt = ReportExcerpts.Read(Of(new string('x', 5000)), 400);
        Assert.Contains("\n----- T2T Online bug report:", excerpt.Text, StringComparison.Ordinal);
    }
}

public class MelonLogTimesTests
{
    // A zone seven hours behind UTC with daylight time (six behind): March's second Sunday to November's first, at 2:00.
    private static readonly TimeZoneInfo MinusSeven = TimeZoneInfo.CreateCustomTimeZone("Test-7/-6", TimeSpan.FromHours(-7), "Test-7/-6", "Standard", "Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    private static readonly TimeZoneInfo PlusNine = TimeZoneInfo.CreateCustomTimeZone("Test+9", TimeSpan.FromHours(9), "Test+9", "Standard");

    private static MelonLogTimes Clock(TimeZoneInfo zone, string name)
    {
        Assert.True(MelonLogTimes.TryParseName(name, out DateTime start));
        return new MelonLogTimes(zone, start);
    }

    [Theory]
    [InlineData("26-11-3_21-30-15.log", 2026, 11, 3, 21, 30, 15)]
    [InlineData("26-1-9_0-0-0.log", 2026, 1, 9, 0, 0, 0)]
    [InlineData("26-12-31_23-59-59.LOG", 2026, 12, 31, 23, 59, 59)]
    public void MelonLoaders_names_are_read_as_local_start_times(string name, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.True(MelonLogTimes.TryParseName(name, out DateTime start));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), start);
    }

    [Theory]
    [InlineData("Latest.log")]
    [InlineData("26-11-3_21-30-15.txt")]
    [InlineData("26-13-3_21-30-15.log")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_names_are_refused(string? name)
    {
        Assert.False(MelonLogTimes.TryParseName(name, out _));
    }

    [Fact]
    public void Stamps_become_utc_and_the_name_too()
    {
        MelonLogTimes clock = Clock(PlusNine, "26-11-5_13-30-15.log");

        Assert.Equal(new DateTime(2026, 11, 5, 4, 30, 15, DateTimeKind.Utc), clock.StartUtc);
        Assert.Equal("MelonLoader-20261105T043015Z.log", MelonLogTimes.UtcName(clock.StartUtc));
        Assert.Equal("[04:30:15.500] [MelonLoader.Bootstrap] Using HostFXR", clock.Convert("[13:30:15.500] [MelonLoader.Bootstrap] Using HostFXR"));
        Assert.Equal("  at Some.Frame ()", clock.Convert("  at Some.Frame ()"));
        Assert.Equal(string.Empty, clock.Convert(string.Empty));
        Assert.Equal("[13:30] short", clock.Convert("[13:30] short"));
        Assert.Equal(1, clock.Converted);
    }

    [Fact]
    public void Past_midnight_the_day_moves_on()
    {
        // Started Oct 31 at 23:00 daylight time; past midnight it's Nov 1, the day the clock goes back.
        MelonLogTimes clock = Clock(MinusSeven, "26-10-31_23-0-0.log");

        Assert.Equal("[05:00:00.000] a", clock.Convert("[23:00:00.000] a"));
        Assert.Equal("[06:30:00.000] b", clock.Convert("[00:30:00.000] b"));
        Assert.Equal("[07:30:00.000] c", clock.Convert("[01:30:00.000] c")); // the first 01:30 (daylight time)
        Assert.Equal("[08:10:00.000] d", clock.Convert("[01:10:00.000] d")); // the clock went back: standard time now
    }

    [Fact]
    public void The_hour_a_clock_goes_back_keeps_utc_moving_forward()
    {
        MelonLogTimes clock = Clock(MinusSeven, "26-11-1_0-30-0.log");

        Assert.Equal(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), clock.StartUtc);
        Assert.Equal("[07:30:00.000] x", clock.Convert("[01:30:00.000] x"));
        Assert.Equal("[07:59:59.000] x", clock.Convert("[01:59:59.000] x"));
        Assert.Equal("[08:00:05.000] x", clock.Convert("[01:00:05.000] x"));
        Assert.Equal("[08:30:00.000] x", clock.Convert("[01:30:00.000] x"));
        Assert.Equal("[09:30:00.000] x", clock.Convert("[02:30:00.000] x"));
    }

    [Fact]
    public void The_hour_a_clock_skips_reads_with_the_offset_before_it()
    {
        MelonLogTimes clock = Clock(MinusSeven, "26-3-8_1-0-0.log");

        Assert.Equal(new DateTime(2026, 3, 8, 8, 0, 0, DateTimeKind.Utc), clock.StartUtc);
        Assert.Equal("[08:59:00.000] x", clock.Convert("[01:59:00.000] x"));
        Assert.Equal("[09:00:00.000] x", clock.Convert("[03:00:00.000] x"));
        Assert.Equal(new DateTime(2026, 3, 8, 9, 30, 0, DateTimeKind.Utc), clock.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0)));
    }
}

public class UnityLogTextTests
{
    [Fact]
    public void The_graphics_block_keeps_the_maker_and_driver_only()
    {
        Assert.Equal("    Renderer: NVIDIA <graphics card model>", UnityLogText.Reduce("    Renderer: NVIDIA GeForce RTX 4070 (ID=0x2786)", out bool a));
        Assert.True(a);
        Assert.Equal("    VRAM:     (left out of the bug report)", UnityLogText.Reduce("    VRAM:     12011 MB", out bool b));
        Assert.True(b);
        Assert.Equal("Renderer: AMD <graphics card model>", UnityLogText.Reduce("Renderer: AMD Radeon RX 7800 XT (ID=0x747e)", out _));
        Assert.Equal("Renderer: Intel <graphics card model>", UnityLogText.Reduce("Renderer: Intel(R) Arc(TM) A770 Graphics (ID=0x56a0)", out _));
        Assert.Equal("Renderer: <graphics card model>", UnityLogText.Reduce("Renderer: Some Card 3000", out _));
    }

    [Theory]
    [InlineData("    Vendor:   NVIDIA")]
    [InlineData("    Driver:   32.0.15.6094")]
    [InlineData("    Version:  Direct3D 11.0 [level 11.1]")]
    [InlineData("Loading player data from <game folder>/Tape to Tape_Data/data.unity3d")]
    [InlineData("The Iris team won the match.")]
    [InlineData("")]
    public void Other_lines_stay_as_they_are(string line)
    {
        Assert.Same(line, UnityLogText.Reduce(line, out bool reduced));
        Assert.False(reduced);
    }

    [Theory]
    [InlineData("[Vulkan init] Selected physical device NVIDIA GeForce RTX 4070 (deviceID 0x2786)", "[Vulkan init] Selected physical device NVIDIA <graphics card model> (deviceID 0x2786)")]
    [InlineData("GPU: Intel(R) UHD Graphics 630", "GPU: Intel(R) <graphics card model>")]
    public void A_model_name_elsewhere_on_a_makers_line_is_replaced(string line, string expected)
    {
        Assert.Equal(expected, UnityLogText.Reduce(line, out bool reduced));
        Assert.True(reduced);
    }
}

public class BugMarksTests
{
    [Fact]
    public void Marks_count_from_one_and_say_where_and_when_in_utc()
    {
        var marks = new BugMarks();
        DateTime at = new(2026, 11, 5, 5, 21, 13, DateTimeKind.Utc);

        Assert.Equal("MARK 1 (in a match) at 05:21:13 UTC: a moment marked for a bug report (the Insert key).", marks.Next(MarkPlace.Match, at, "the Insert key", out int one));
        Assert.Equal(1, one);
        Assert.StartsWith("MARK 2 (at the title) at 05:21:14 UTC", marks.Next(MarkPlace.Title, at.AddSeconds(1), "x", out int two), StringComparison.Ordinal);
        Assert.Equal(2, two);
        Assert.StartsWith("MARK 3 (in a menu)", marks.Next(MarkPlace.Menu, at, "x", out _), StringComparison.Ordinal);
        Assert.Equal(3, marks.Count);
    }
}

public class BugReportFilesTests
{
    [Fact]
    public void A_reports_name_is_its_utc_time()
    {
        string name = BugReportFiles.ReportName(new DateTime(2026, 11, 5, 5, 22, 0, DateTimeKind.Utc));
        Assert.Equal("T2TOnline-report-20261105T052200Z.zip", name);
        Assert.True(BugReportFiles.IsReportName(name));
        Assert.False(BugReportFiles.IsReportName("T2TOnline-report-latest.zip"));
        Assert.False(BugReportFiles.IsReportName("holiday.zip"));
    }

    [Fact]
    public void The_newest_five_reports_stay_and_nothing_else_is_touched()
    {
        string[] names = Enumerable.Range(1, 8).Select(i => BugReportFiles.ReportName(new DateTime(2026, 11, i, 12, 0, 0, DateTimeKind.Utc)))
            .Append("notes.zip").ToArray();

        Assert.Equal(names.Take(3), BugReportFiles.ReportsToDelete(names).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(BugReportFiles.ReportsToDelete(names.Skip(3)));
    }

    [Fact]
    public void A_player_s_report_goes_in_their_reports_folder_which_is_pruned()
    {
        string player = Path.Combine("Game", "UserData", "T2TOnline", "reports");
        ReportDestination destination = BugReportFiles.Destination(player, tourHolds: false, tourFolder: Path.Combine("Game", "tour"));

        Assert.Equal(player, destination.Folder);
        Assert.True(destination.Prunes);
        Assert.False(destination.ForTour);
    }

    [Fact]
    public void A_check_tour_s_report_goes_in_the_tour_s_own_folder_and_prunes_nothing()
    {
        string player = Path.Combine("Game", "UserData", "T2TOnline", "reports");
        string tour = Path.Combine("Game", "UserData", "T2TOnline", "dev", "ui-check", "20261106T120000Z");
        ReportDestination destination = BugReportFiles.Destination(player, tourHolds: true, tourFolder: tour);

        Assert.Equal(Path.Combine(tour, BugReportFiles.FolderName), destination.Folder);
        Assert.False(destination.Prunes);
        Assert.True(destination.ForTour);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_tour_with_no_folder_of_its_own_still_prunes_nothing_of_the_player_s(string? tour)
    {
        string player = Path.Combine("Game", "UserData", "T2TOnline", "reports");
        ReportDestination destination = BugReportFiles.Destination(player, tourHolds: true, tourFolder: tour);

        Assert.Equal(player, destination.Folder);
        Assert.False(destination.Prunes);
        Assert.False(destination.ForTour);
    }

    [Fact]
    public void Session_logs_are_tried_newest_first_by_the_time_in_their_names()
    {
        var files = new[]
        {
            new ReportCandidate("session-20261103T100000Z.log", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)),
            new ReportCandidate("session-20261105T100000Z.log", new DateTime(2026, 11, 5, 11, 0, 0, DateTimeKind.Utc)),
            new ReportCandidate("session-20261104T100000Z.log", new DateTime(2026, 11, 4, 11, 0, 0, DateTimeKind.Utc)),
            new ReportCandidate("notes.txt", new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc)),
        };

        Assert.Equal(new[] { "session-20261105T100000Z.log", "session-20261104T100000Z.log", "session-20261103T100000Z.log" },
            BugReportFiles.SessionLogsNewestFirst(files).Select(f => f.Name));
    }

    [Fact]
    public void MelonLoader_logs_are_tried_newest_written_first()
    {
        var files = new[]
        {
            new ReportCandidate("26-11-3_9-30-0.log", new DateTime(2026, 11, 3, 16, 49, 0, DateTimeKind.Utc)),
            new ReportCandidate("26-11-3_21-30-15.log", new DateTime(2026, 11, 4, 5, 30, 0, DateTimeKind.Utc)),
            new ReportCandidate("26-11-3_19-45-0.log", new DateTime(2026, 11, 4, 4, 55, 0, DateTimeKind.Utc)),
        };

        Assert.Equal(new[] { "26-11-3_21-30-15.log", "26-11-3_19-45-0.log", "26-11-3_9-30-0.log" }, BugReportFiles.MelonLogsNewestFirst(files).Select(f => f.Name));
    }

    [Fact]
    public void Other_loaders_files_at_the_game_root_are_listed_but_melonloaders_own_are_not()
    {
        string[] root = { "MelonLoader", "Mods", "version.dll", "GameAssembly.dll", "BepInEx", "winhttp.dll", "doorstop_config.ini", "dxgi.dll", "UnityPlayer.dll" };
        Assert.Equal(new[] { "BepInEx", "doorstop_config.ini", "dxgi.dll", "winhttp.dll" }, BugReportFiles.OtherLoaders(root));
        Assert.Empty(BugReportFiles.OtherLoaders(new[] { "MelonLoader", "version.dll", "Plugins" }));
    }
}

public class ReportAllowListTests
{
    private const string Game = @"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape";
    private const string Local = @"C:\Users\Alex\AppData\Local\T2T Online";
    private const string LocalLow = @"C:\Users\Alex\AppData\LocalLow\Excellent Rectangle\Tape to Tape";

    private static readonly ReportAllowList Allow = new(Game + @"\UserData\T2TOnline\logs", Game + @"\MelonLoader\Logs", LocalLow);

    [Theory]
    [InlineData(Game + @"\UserData\T2TOnline\logs\session-20261105T100000Z.log", ReportFileKind.SessionLog)]
    [InlineData(Game + @"/UserData/T2TOnline/logs/SESSION-20261105T100000Z.LOG", ReportFileKind.SessionLog)]
    [InlineData(Game + @"\MelonLoader\Logs\26-11-3_21-30-15.log", ReportFileKind.MelonLog)]
    [InlineData(LocalLow + @"\Player.log", ReportFileKind.UnityLog)]
    [InlineData(LocalLow + @"\Player-prev.log", ReportFileKind.UnityLog)]
    public void The_three_kinds_of_log_are_allowed(string path, ReportFileKind kind)
    {
        Assert.Equal(kind, Allow.Classify(path));
        Assert.NotNull(Allow.Source(kind, path, _ => new MemoryStream()));
    }

    [Theory]
    [InlineData(Local + @"\progress\host-key.bin")]
    [InlineData(Local + @"\progress\progress-ledger.txt")]
    [InlineData(Local + @"\progress\shared-play.txt")]
    [InlineData(Local + @"\coop\coop-1.es3")]
    [InlineData(Local + @"\backups\20261105T100000Z-host\profileV2.es3")]
    [InlineData(Game + @"\UserData\T2TOnline\logs\host-key.bin")]
    [InlineData(Game + @"\UserData\T2TOnline\logs\progress\session-20261105T100000Z.log")]
    [InlineData(Game + @"\UserData\T2TOnline\logs\..\settings.cfg")]
    [InlineData(Game + @"\UserData\T2TOnline\logs\..\..\MelonPreferences.cfg")]
    [InlineData(Game + @"\UserData\T2TOnline\dev\captures\snapshots-20261105T100000Z.t2tsnap")]
    [InlineData(Game + @"\UserData\T2TOnline\logs\notes.txt")]
    [InlineData(Game + @"\UserData\T2TOnline\reports\T2TOnline-report-20261105T100000Z.zip")]
    [InlineData(Game + @"\MelonLoader\Latest.txt")]
    [InlineData(Game + @"\MelonLoader\Logs\..\..\UserData\T2TOnline\settings.cfg")]
    [InlineData(LocalLow + @"\profileV2.es3")]
    [InlineData(LocalLow + @"\statsV2.es3")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Temp\crash.dmp")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused(string? path)
    {
        Assert.Null(Allow.Classify(path));
        Assert.Null(Allow.Source(ReportFileKind.SessionLog, path!, _ => new MemoryStream()));
        Assert.Null(Allow.Source(ReportFileKind.UnityLog, path!, _ => new MemoryStream()));
    }

    [Fact]
    public void A_progress_folder_or_the_host_key_is_refused_even_where_a_log_folder_would_be()
    {
        // Folders set wrongly (a log folder inside the data folder's progress) still never give the progress files.
        var wrong = new ReportAllowList(Local + @"\progress", Local + @"\progress", Local + @"\progress");
        Assert.Null(wrong.Classify(Local + @"\progress\session-20261105T100000Z.log"));
        Assert.Null(wrong.Classify(Local + @"\progress\Player.log"));
        Assert.Null(wrong.Classify(Local + @"\progress\host-key.bin"));
        var anywhere = new ReportAllowList(@"C:\logs", @"C:\logs", @"C:\logs");
        Assert.Null(anywhere.Classify(@"C:\logs\host-key.bin"));
        Assert.Equal(ReportFileKind.SessionLog, anywhere.Classify(@"C:\logs\session-1.log"));
    }

    [Fact]
    public void A_source_of_the_wrong_kind_is_refused()
    {
        Assert.Null(Allow.Source(ReportFileKind.MelonLog, LocalLow + @"\Player.log", _ => new MemoryStream()));
    }

    [Fact]
    public void A_game_folder_with_a_common_word_in_its_path_still_works()
    {
        var dev = new ReportAllowList(@"E:\dev\Steam\steamapps\common\Tape to Tape\UserData\T2TOnline\logs", @"E:\dev\Steam\steamapps\common\Tape to Tape\MelonLoader\Logs", LocalLow);
        Assert.Equal(ReportFileKind.SessionLog, dev.Classify(@"E:\dev\Steam\steamapps\common\Tape to Tape\UserData\T2TOnline\logs\session-1.log"));
    }
}
