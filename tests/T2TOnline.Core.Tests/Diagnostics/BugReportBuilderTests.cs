using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class BugReportBuilderTests
{
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;
    private const string Game = @"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape";
    private const string Header = "04:30:17.250 INFO  T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-05.";

    // A lobby's Steam ID: universe 1, account type 8 (chat), the lobby instance flags, an account number.
    private const ulong Lobby = (1UL << 56) | (8UL << 52) | (0x60000UL << 32) | 4242;

    private static readonly DateTime Made = new(2026, 11, 5, 5, 22, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test Seven", TimeSpan.FromHours(-7), "Test Seven", "TS");

    private readonly LogTags _tags = new(Enumerable.Range(1, LogTags.KeyBytes).Select(b => (byte)b).ToArray(), shared: true);
    private readonly LogScrub _scrub;

    public BugReportBuilderTests()
    {
        _scrub = new LogScrub(_tags);
        _scrub.AddFolders(new Dictionary<string, string> { [Game] = "<game folder>", [@"C:\Users\Alex"] = "%USERPROFILE%" });
        _scrub.RegisterName(Sam, "Sam");
    }

    private static BugReportFacts Facts() => new()
    {
        MadeUtc = Made,
        Why = "a test",
        Release = "0.11.0-beta",
        Commit = "0123456789ab",
        Protocol = 9,
        GameBuild = "Nov 1 2026 build",
        MelonLoader = "0.7.3",
        Os = "Microsoft Windows 10.0.19045",
        Runtime = ".NET 6.0.36",
        Settings = "CoopSlot = 3",
        NotFound = new[] { "Unity's Player-prev.log" },
    };

    private static ReportSource Source(ReportFileKind kind, string name, string text) =>
        new(kind, name, () => new MemoryStream(Encoding.UTF8.GetBytes(text)));

    private static ReportSource Throwing(ReportFileKind kind, string name, Exception ex) => new(kind, name, () => throw ex);

    private (BugReportResult Result, Dictionary<string, string> Entries, List<ZipArchiveEntry> Order) Build(
        IReadOnlyList<ReportSource> sessions, IReadOnlyList<ReportSource> melons, IReadOnlyList<ReportSource> unity)
    {
        var zip = new MemoryStream();
        BugReportResult result = BugReportBuilder.Write(zip, Facts(), sessions, melons, unity, Zone, _scrub);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            entries[entry.FullName] = reader.ReadToEnd();
        }

        return (result, entries, archive.Entries.ToList());
    }

    private static string Session(string body) => Header + "\n" + body;

    [Fact]
    public void The_report_holds_the_logs_cleaned_and_the_manifest_last()
    {
        string session = Session(
            $"05:19:00.000 INFO  Log tags: this PC is {TagOf(Alex)} in lobby {LobbyTag()}, host {TagOf(Sam)}; names, Steam IDs and folders are never written to this log.\n" +
            "05:20:00.000 INFO  Online: \"Sam\" (76561199000000002) joined from C:\\Users\\Alex\\Desktop.\n" +
            "05:21:13.000 INFO  MARK 1 (in a match) at 05:21:13 UTC: a moment marked for a bug report (the Insert key).\n");
        string melon = "[21:30:15.500] [MelonLoader.Bootstrap] Using HostFXR Path: C:\\Program Files\\dotnet\\host\\fxr\\8.0.33\\hostfxr.dll\n" +
            $"[21:30:15.750] Command-Line: +connect_lobby {Lobby}\n" +
            $"[21:30:15.750] Core::BasePath = {Game}\n" +
            "[21:30:17.250] [T2T_Online] T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-04.\n";
        string player = $"Loading player data from {Game.Replace('\\', '/')}/Tape to Tape_Data/data.unity3d\n    Renderer: NVIDIA GeForce RTX 4070 (ID=0x2786)\n    VRAM:     12011 MB\n    Driver:   32.0.15.6094\n";

        var (result, entries, order) = Build(
            new[] { Source(ReportFileKind.SessionLog, "session-20261105T050000Z.log", session) },
            new[] { Source(ReportFileKind.MelonLog, "26-11-3_21-30-15.log", melon) },
            new[] { Source(ReportFileKind.UnityLog, "Player.log", player) });

        Assert.Equal(new[] { "session-20261105T050000Z.log", "MelonLoader-20261104T043015Z.log", "Player.log", "report.txt" }, order.Select(e => e.FullName));

        string cleanSession = entries["session-20261105T050000Z.log"];
        Assert.DoesNotContain("Sam", cleanSession, StringComparison.Ordinal);
        Assert.DoesNotContain("7656119", cleanSession, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alex", cleanSession, StringComparison.Ordinal);
        Assert.Contains($"Online: {TagOf(Sam)} joined from %USERPROFILE%\\Desktop.", cleanSession, StringComparison.Ordinal);

        string cleanMelon = entries["MelonLoader-20261104T043015Z.log"];
        Assert.StartsWith("[04:30:15.500] [MelonLoader.Bootstrap]", cleanMelon, StringComparison.Ordinal);
        Assert.Contains("Core::BasePath = <game folder>", cleanMelon, StringComparison.Ordinal);
        Assert.Contains($"+connect_lobby {LobbyTag()}", cleanMelon, StringComparison.Ordinal);
        Assert.Contains("[04:30:17.250] [T2T_Online] T2T Online", cleanMelon, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\dotnet\host", cleanMelon, StringComparison.Ordinal); // kept: not the player's

        string cleanPlayer = entries["Player.log"];
        Assert.Contains("Loading player data from <game folder>/Tape to Tape_Data", cleanPlayer, StringComparison.Ordinal);
        Assert.Contains("Renderer: NVIDIA <graphics card model>", cleanPlayer, StringComparison.Ordinal);
        Assert.DoesNotContain("4070", cleanPlayer, StringComparison.Ordinal);
        Assert.DoesNotContain("12011", cleanPlayer, StringComparison.Ordinal);
        Assert.Contains("Driver:   32.0.15.6094", cleanPlayer, StringComparison.Ordinal);

        Assert.Equal(4, result.MelonTimes);
        Assert.Equal(2, result.GraphicsLines);
        Assert.Equal(1, result.Cleaned.SteamIds);
        Assert.Equal(1, result.Cleaned.Names);
        Assert.Equal(1, result.Cleaned.Lobbies);
        Assert.True(result.Cleaned.Folders >= 3);
        Assert.Equal($"session-20261105T050000Z.log: this PC is {TagOf(Alex)} in lobby {LobbyTag()}, host {TagOf(Sam)}", Assert.Single(result.Tags));
        Assert.Equal("session-20261105T050000Z.log 05:21:13 UTC: MARK 1 (in a match)", Assert.Single(result.Marks));

        string manifest = entries["report.txt"];
        Assert.Contains(BugReportManifest.Promise, manifest, StringComparison.Ordinal);
        Assert.Contains("Made at 2026-11-05 05:22:00 UTC (a test).", manifest, StringComparison.Ordinal);
        Assert.Contains("Settings not at their defaults: CoopSlot = 3", manifest, StringComparison.Ordinal);
        Assert.Contains("  Unity's Player-prev.log: not found", manifest, StringComparison.Ordinal);
        Assert.Contains("MARK 1 (in a match)", manifest, StringComparison.Ordinal);
        Assert.Contains("\r\n", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_files_size_and_sha256_are_those_of_its_bytes_in_the_zip_and_every_entry_is_dated_in_utc()
    {
        var zip = new MemoryStream();
        BugReportResult result = BugReportBuilder.Write(zip, Facts(),
            new[] { Source(ReportFileKind.SessionLog, "session-20261105T050000Z.log", Session("05:19:00.000 INFO  hello\n")) },
            Array.Empty<ReportSource>(), Array.Empty<ReportSource>(), Zone, _scrub);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        ReportFileRow row = Assert.Single(result.Files);
        ZipArchiveEntry entry = archive.GetEntry(row.Name)!;
        using var copy = new MemoryStream();
        using (System.IO.Stream stream = entry.Open())
        {
            stream.CopyTo(copy);
        }

        Assert.Equal(row.Bytes, copy.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(copy.ToArray())).ToLowerInvariant(), row.Sha256);
        Assert.Contains(row.Sha256, new StreamReader(archive.GetEntry("report.txt")!.Open()).ReadToEnd(), StringComparison.Ordinal);
        Assert.All(archive.Entries, e => Assert.Equal(new DateTime(2026, 11, 5, 5, 22, 0), e.LastWriteTime.DateTime));
    }

    [Fact]
    public void Session_logs_before_format_2_are_left_out_and_the_next_newest_taken()
    {
        var (result, entries, _) = Build(
            new[]
            {
                Source(ReportFileKind.SessionLog, "session-20261105T050000Z.log", Session("a\n")),
                Source(ReportFileKind.SessionLog, "session-20261104T050000Z.log", "04:30:17.250 INFO  T2T Online 0.10.0-beta, commit abc starting\nSam\n"),
                Source(ReportFileKind.SessionLog, "session-20261103T050000Z.log", Session("b\n")),
                Source(ReportFileKind.SessionLog, "session-20261102T050000Z.log", Session("c\n")),
                Source(ReportFileKind.SessionLog, "session-20261101T050000Z.log", Session("d\n")),
            },
            Array.Empty<ReportSource>(), Array.Empty<ReportSource>());

        Assert.Equal(new[] { "session-20261105T050000Z.log", "session-20261103T050000Z.log", "session-20261102T050000Z.log" },
            result.Files.Select(f => f.Name));
        Assert.DoesNotContain("session-20261104T050000Z.log", entries.Keys);
        Assert.Contains(result.LeftOut, l => l.Name == "session-20261104T050000Z.log" && l.Why.Contains("before log format 2", StringComparison.Ordinal));
        Assert.Contains("session-20261104T050000Z.log: written before log format 2", entries["report.txt"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_melonloader_log_without_a_format_2_header_is_left_out_and_said_so()
    {
        string old = "[21:30:17.200] T2T Online v0.10.0-beta\n[21:30:17.200] by Alex\n[21:30:17.300] [T2T_Online] T2T Online 0.10.0-beta, commit d2ad starting. \"Riley\" joined.\n";
        string current = "[23:00:00.000] [T2T_Online] T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-05.\n";
        var (result, entries, _) = Build(
            Array.Empty<ReportSource>(),
            new[]
            {
                Source(ReportFileKind.MelonLog, "26-11-4_23-0-0.log", current),
                Source(ReportFileKind.MelonLog, "26-11-3_21-30-15.log", old),
                Source(ReportFileKind.MelonLog, "26-11-2_10-0-0.log", current),
            },
            Array.Empty<ReportSource>());

        Assert.Equal(new[] { "MelonLoader-20261105T060000Z.log" }, result.Files.Select(f => f.Name));
        Assert.DoesNotContain(entries.Values, text => text.Contains("Riley", StringComparison.Ordinal));
        Assert.Contains(result.LeftOut, l => l.Name == "MelonLoader-20261104T043015Z.log" && l.Why.Contains("before log format 2", StringComparison.Ordinal));
        Assert.Single(result.LeftOut); // only the newest two are tried
    }

    [Fact]
    public void A_melonloader_log_whose_name_isnt_melonloaders_is_left_out()
    {
        var (result, _, _) = Build(Array.Empty<ReportSource>(), new[] { Source(ReportFileKind.MelonLog, "Latest.log", "[23:00:00.000] x\n") }, Array.Empty<ReportSource>());

        Assert.Empty(result.Files);
        Assert.Contains("isn't MelonLoader's", Assert.Single(result.LeftOut).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_cant_be_read_is_listed_with_its_folders_labelled_and_the_rest_goes_on()
    {
        var (result, entries, _) = Build(
            new[] { Source(ReportFileKind.SessionLog, "session-20261105T050000Z.log", Session("a\n")) },
            Array.Empty<ReportSource>(),
            new[]
            {
                Throwing(ReportFileKind.UnityLog, "Player.log", new IOException($"The process cannot access the file '{@"C:\Users\Alex\AppData\LocalLow\Player.log"}' because it is being used by another process.")),
                Throwing(ReportFileKind.UnityLog, "Player-prev.log", new FileNotFoundException("gone")),
            });

        Assert.Single(result.Files);
        ReportLeftOut locked = result.LeftOut.Single(l => l.Name == "Player.log");
        Assert.StartsWith("couldn't be read (IOException", locked.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("Alex", locked.Why, StringComparison.Ordinal);
        Assert.Equal("not found", result.LeftOut.Single(l => l.Name == "Player-prev.log").Why);
        Assert.DoesNotContain(@"C:\Users\Alex", entries["report.txt"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_log_is_cut_and_the_report_stops_at_its_size_limit()
    {
        string big = Session(string.Concat(Enumerable.Range(0, 120_000).Select(i => $"05:19:00.000 INFO  line {i:000000} of a long session log\n")));
        Assert.True(big.Length > BugReportFiles.MaxFileBytes);
        var sources = Enumerable.Range(0, 3).Select(i => Source(ReportFileKind.SessionLog, $"session-2026110{5 - i}T050000Z.log", big)).ToArray();
        var melons = Enumerable.Range(0, 2).Select(i => Source(ReportFileKind.MelonLog, $"26-11-{5 - i}_1-0-0.log",
            "[01:00:00.000] [T2T_Online] T2T Online 0.11.0-beta, commit 0, protocol 9, log format 2; UTC date x\n" + string.Concat(Enumerable.Repeat("[01:00:01.000] filler line of a MelonLoader log, long enough\n", 80_000)))).ToArray();
        var unity = new[] { Source(ReportFileKind.UnityLog, "Player.log", new string('u', 5_000_000)), Source(ReportFileKind.UnityLog, "Player-prev.log", "short\n") };

        var (result, entries, _) = Build(sources, melons, unity);

        Assert.All(result.Files, f => Assert.True(f.Bytes <= BugReportFiles.MaxFileBytes + 1024, f.Name));
        Assert.True(result.TextBytes <= BugReportFiles.MaxReportBytes + (8 * 1024));
        Assert.Contains(result.Files, f => f.LeftOutBytes > 0);
        Assert.Contains(result.LeftOut, l => l.Why == "the report reached its size limit");
        string first = entries["session-20261105T050000Z.log"];
        Assert.StartsWith(Header, first, StringComparison.Ordinal);
        Assert.Contains("line 119999 of a long session log", first, StringComparison.Ordinal);
        Assert.Contains("bytes of the middle of this file were left out", first, StringComparison.Ordinal);
    }

    [Fact]
    public void The_games_own_lines_keep_a_custom_teams_or_skaters_name_only_as_its_shape()
    {
        string player = "Loaded custom team from team id: Riley Bay \nUnityEngine.Debug:Log (object)\nState.CampaignState:TryLoadCustomTeam (string)\n" +
            "Scene hierarchy path \"Hockey/Teams/NorthTeam/Alex Casey/Body/Stick/collider\"\n";
        string session = Session("05:19:00.000 WARN  [game Error] TEAMDATA is NULL: Casey Riley\n");
        var zip = new MemoryStream();
        BugReportResult result = BugReportBuilder.Write(zip, Facts(),
            new[] { Source(ReportFileKind.SessionLog, "session-20261105T050000Z.log", session) },
            Array.Empty<ReportSource>(),
            new[] { Source(ReportFileKind.UnityLog, "Player.log", player) },
            Zone, _scrub, text => $"{text?.Length} characters, #0000");
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        string cleanPlayer = new StreamReader(archive.GetEntry("Player.log")!.Open()).ReadToEnd();
        string cleanSession = new StreamReader(archive.GetEntry("session-20261105T050000Z.log")!.Open()).ReadToEnd();
        string manifest = new StreamReader(archive.GetEntry("report.txt")!.Open()).ReadToEnd();

        Assert.Contains("Loaded custom team from team id: <a name: 9 characters, #0000>", cleanPlayer, StringComparison.Ordinal);
        Assert.Contains("Hockey/Teams/NorthTeam/<a name: 10 characters, #0000>/Body", cleanPlayer, StringComparison.Ordinal);
        Assert.Contains("[game Error] TEAMDATA is NULL: <a name: 11 characters, #0000>", cleanSession, StringComparison.Ordinal);
        foreach (string name in new[] { "Riley Bay", "Alex Casey", "Casey Riley" })
        {
            Assert.DoesNotContain(name, cleanPlayer + cleanSession, StringComparison.Ordinal);
        }

        Assert.Equal(3, result.GameNames);
        Assert.Contains("3 names in the game's own lines (skaters, teams, logos) reduced to their length and a hash.", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_the_allow_list_refused_are_listed_as_not_read()
    {
        var zip = new MemoryStream();
        BugReportFacts facts = new()
        {
            MadeUtc = Made,
            Refused = new[] { "2 file(s) in the session logs folder" },
        };
        BugReportBuilder.Write(zip, facts, Array.Empty<ReportSource>(), Array.Empty<ReportSource>(), Array.Empty<ReportSource>(), Zone, _scrub);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        string manifest = new StreamReader(archive.GetEntry("report.txt")!.Open()).ReadToEnd();

        // The allow-list refuses by folder and name only: it can't tell a link, so the manifest doesn't say it can.
        Assert.Contains("Left out:\r\n  2 file(s) in the session logs folder: not read (in a folder a report never reads)", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("link", manifest, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session-20261105T050000Z.log", "this launch's session log", "an earlier launch's session log")]
    [InlineData("session-20261106T050000Z.log", "an earlier launch's session log", "an earlier launch's session log")]
    [InlineData("", "an earlier launch's session log", "an earlier launch's session log")]
    public void Only_this_launchs_own_session_log_is_called_this_launchs(string thisLaunch, string newest, string older)
    {
        // The second and third: this launch's log couldn't be made, so the newest in the folder is an earlier launch's.
        var zip = new MemoryStream();
        BugReportFacts facts = new() { MadeUtc = Made, ThisLaunchLog = thisLaunch };
        BugReportResult result = BugReportBuilder.Write(zip, facts,
            new[]
            {
                Source(ReportFileKind.SessionLog, "session-20261105T050000Z.log", Session("a\n")),
                Source(ReportFileKind.SessionLog, "session-20261104T050000Z.log", Session("b\n")),
            },
            Array.Empty<ReportSource>(), Array.Empty<ReportSource>(), Zone, _scrub);

        Assert.Equal(new[] { newest, older }, result.Files.Select(f => f.What));
    }

    [Fact]
    public void An_empty_report_still_has_its_manifest()
    {
        var (result, entries, _) = Build(Array.Empty<ReportSource>(), Array.Empty<ReportSource>(), Array.Empty<ReportSource>());

        Assert.Empty(result.Files);
        Assert.Equal(new[] { "report.txt" }, entries.Keys);
        Assert.Contains("Files:\r\n  none", entries["report.txt"], StringComparison.Ordinal);
        Assert.Contains("none (no lobby in these logs)", entries["report.txt"], StringComparison.Ordinal);
    }

    private string TagOf(ulong id) => _tags.Player(id);

    private string LobbyTag() => _tags.Lobby(Lobby);
}
