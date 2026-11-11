using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class UiCheckFilesTests
{
    [Theory]
    [InlineData(@"coop\coop-1.es3", true)]
    [InlineData(@"coop\coop-1.es3.bac", true)]
    [InlineData("coop/coop-2.es3", true)]
    [InlineData(@"progress\run-scopes.txt", true)]
    [InlineData(@"progress\host-key.bin", true)]
    [InlineData(@"progress\ledger-history\ledger-20260101T120000000Z-start.txt", true)]
    [InlineData("shared-play.txt", true)]
    [InlineData("README.txt", false)]
    [InlineData("readme.TXT", false)]
    [InlineData(@"backups\20260101T100000Z-host\RunDataV2.es3", false)]
    [InlineData(@"backups\first-backup.txt", false)]
    [InlineData(@"dev\ui-check\20260101T120500Z\summary.txt", false)]
    [InlineData(@"logs\session-20260101T120000Z.log", false)]
    [InlineData(@"reports\T2TOnline-report.zip", false)]
    [InlineData(@"campaign\HostRun.es3", false)]
    [InlineData(@"other\README.txt", true)]
    [InlineData("", false)]
    public void The_data_folder_is_hashed_but_its_backups_readme_and_a_tours_own_output(string relative, bool hashed)
    {
        Assert.Equal(hashed, UiCheckFiles.HashesFile(relative));
    }

    [Theory]
    [InlineData(@"coop\coop-1.es3", true)]
    [InlineData(@"coop\coop-1.es3.bac", true)]
    [InlineData(@"progress\host-key.bin", false)]
    [InlineData(@"progress\run-scopes.txt", false)]
    [InlineData("shared-play.txt", false)]
    [InlineData("coop", false)]
    public void Only_the_co_op_slots_hashes_are_printed(string relative, bool printed)
    {
        Assert.Equal(printed, UiCheckFiles.PrintsHash(relative));
    }

    [Fact]
    public void The_compare_names_each_file_changed_gone_or_new_by_label()
    {
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"saves\RunDataV2.es3"] = "A",
            [@"%LOCALAPPDATA%\T2T Online\coop\coop-1.es3.bac"] = "B",
            ["UserData/T2TOnline/settings.cfg"] = "C",
            [@"%LOCALAPPDATA%\T2T Online\progress\run-scopes.txt"] = "D",
        };
        var after = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"saves\RunDataV2.es3"] = "A",
            [@"%LOCALAPPDATA%\T2T Online\coop\coop-1.es3.bac"] = "B2",
            [@"%LOCALAPPDATA%\T2T Online\progress\run-scopes.txt"] = "D",
            [@"%LOCALAPPDATA%\T2T Online\progress\host-key.bin"] = "E",
        };

        List<UiCheckFileChange> changes = UiCheckFiles.Compare(before, after);

        Assert.Equal(new[]
        {
            @"%LOCALAPPDATA%\T2T Online\coop\coop-1.es3.bac (changed)",
            @"%LOCALAPPDATA%\T2T Online\progress\host-key.bin (new)",
            "UserData/T2TOnline/settings.cfg (gone)",
        }, changes.Select(c => c.ToString()));
        Assert.Empty(UiCheckFiles.Compare(before, before));
    }

    [Fact]
    public void Backup_folder_names_are_compared_as_added_and_gone()
    {
        string[] before = { "20260101T100000Z-host", "20260102T100000Z-host" };
        Assert.Empty(UiCheckFiles.NameChanges(before, before.Reverse()));
        Assert.Equal(new[] { "+20260103T100000Z-host", "-20260101T100000Z-host" }, UiCheckFiles.NameChanges(before, new[] { "20260102T100000Z-host", "20260103T100000Z-host" }));
    }

    [Fact]
    public void Backup_changes_name_each_folder_as_a_log_does()
    {
        // An old local-time name is said as the UTC time it stands for (19:15 at UTC-7 is 02:15 UTC the next day), and a
        // folder T2T Online didn't name is never said by its name: no line holds a local time or a name a player chose.
        var clock = new BackupClock(TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "UTC-7", "UTC-7"));
        string[] before = { "20261105T100000Z-host", "20261103-191502-host", "Notes for Alex" };
        string[] after = { "20261105T100000Z-host", "20261106T070000Z-friend", "Riley's copy" };

        Assert.Equal(new[]
        {
            "+20261106T070000Z-friend",
            "+a folder T2T Online didn't name",
            "-20261104T021502Z-host (named in local time on disk)",
            "-a folder T2T Online didn't name",
        }, UiCheckFiles.BackupChanges(before, after, clock));
        Assert.Empty(UiCheckFiles.BackupChanges(before, before.Reverse(), clock));
    }

    [Theory]
    [InlineData("profileV2.es3", true)]
    [InlineData("statsV2.es3", true)]
    [InlineData("CONFIG.ES3", true)]
    [InlineData("profileV2.es3.bac", true)] // Easy Save's backup copy, which it loads when the save itself won't
    [InlineData("profileV2.ES3.BAC", true)]
    [InlineData("statsV2.es3.tmp", false)] // a refused commit's leftover, which the game never reads
    [InlineData("statsV2.es3.tmp.bak", false)]
    [InlineData("Player.log", false)]
    [InlineData("eventcache", false)]
    [InlineData("sentry-unity.lock", false)]
    [InlineData(".es3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_game_save_folder_is_hashed_for_its_saves_and_their_backup_copies(string? name, bool hashed)
    {
        Assert.Equal(hashed, UiCheckFiles.HashesSave(name));
    }

    [Theory]
    [InlineData("TeamDataModels", "CustomTeam-0123.json", true)] // a custom team, skater or goalie: one JSON file each
    [InlineData("TeamDataModels", "CustomForward-0123.json", true)]
    [InlineData("teamdatamodels", "CustomGoalie-0123.json", true)]
    [InlineData("BracketData", "BracketSave.json", true)] // the tournament mode's files
    [InlineData("BracketData", "Leaderboard.json", true)]
    [InlineData("BracketData", "steam_autocloud.vdf", false)] // Steam's marker, which Steam rewrites by itself
    [InlineData("TeamDataModels", "STEAM_AUTOCLOUD.VDF", false)]
    [InlineData("CustomLogos", "bears.png", false)] // the player's own pictures: the game only reads them
    [InlineData("Sentry", "session.json", false)]
    [InlineData("Unity", "anything", false)]
    [InlineData("", "BracketSave.json", false)]
    [InlineData(null, "BracketSave.json", false)]
    [InlineData("BracketData", "", false)]
    [InlineData("BracketData", null, false)]
    public void The_games_save_folders_outside_easy_save_are_hashed_but_for_steams_marker(string? folder, string? name, bool hashed)
    {
        Assert.Equal(hashed, UiCheckFiles.HashesSaveIn(folder, name));
    }

    [Fact]
    public void The_save_folders_outside_easy_save_are_the_tournaments_and_the_custom_teams()
    {
        // The save guard refuses Easy Save's writes and the Steam service's only: these two folders are what it can't hold.
        Assert.Equal(new[] { "BracketData", "TeamDataModels" }, UiCheckFiles.SaveFolders);
    }

    [Theory]
    [InlineData("backups", false)]
    [InlineData("Backups", false)]
    [InlineData("coop", true)]
    [InlineData("progress", true)]
    [InlineData("dev", false)]
    public void Top_level_folders_skipped(string name, bool hashed)
    {
        Assert.Equal(hashed, UiCheckFiles.HashesFolder(name));
    }
}
