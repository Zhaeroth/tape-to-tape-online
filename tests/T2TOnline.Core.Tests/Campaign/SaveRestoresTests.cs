using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// What a restore puts back, how the next start puts right a restore the game stopped in the middle of,
/// and the list that keeps restored files restored through Steam Cloud.
/// </summary>
public class SaveRestoresTests
{
    private const string Backup = "20261030-191502-host";
    private const string Before = "20261031-120000-restore";

    private static SaveFileHash File(string name, string text) => SaveFileHash.Of(name, Encoding.UTF8.GetBytes(text));

    private static CoopSlotHash Slot(int slot, string text) => CoopSlotHash.Of(slot, Encoding.UTF8.GetBytes(text));

    private static SaveBackupManifest Manifest(IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> slots) =>
        new("host", "before hosting", new DateTime(2026, 10, 30, 19, 15, 2, DateTimeKind.Utc), files, slots);

    [Fact]
    public void Restore_parts_say_what_they_put_back_and_whether_the_game_restarts()
    {
        Assert.Equal("your saves", RestoreParts.SavesOnly.Describe());
        Assert.True(RestoreParts.SavesOnly.NeedsRestart);
        Assert.Equal("co-op slot 2", new RestoreParts(false, new[] { 2 }).Describe());
        Assert.False(new RestoreParts(false, new[] { 2 }).NeedsRestart);
        Assert.Equal("co-op slots 1 and 3", new RestoreParts(false, new[] { 3, 1, 3 }).Describe());
        Assert.Equal("your saves and co-op slots 1, 2 and 3", new RestoreParts(true, new[] { 2, 3, 1 }).Describe());
        Assert.Equal(new[] { 1, 2, 3 }, new RestoreParts(true, new[] { 2, 3, 1 }).Slots);
        Assert.True(new RestoreParts(false, Array.Empty<int>()).IsEmpty);
        Assert.Equal("nothing", new RestoreParts(false, Array.Empty<int>()).Describe());
    }

    [Fact]
    public void A_restore_takes_only_what_the_backup_holds()
    {
        SaveBackupManifest both = Manifest(new[] { File("profileV2.es3", "p") }, new[] { Slot(2, "run") });
        SaveBackupManifest slotsOnly = Manifest(Array.Empty<SaveFileHash>(), new[] { Slot(1, "run") });

        Assert.True(new RestoreParts(true, new[] { 2 }).TryCheck(both, out string problem));
        Assert.Empty(problem);
        Assert.False(new RestoreParts(false, new[] { 1 }).TryCheck(both, out problem));
        Assert.Equal("it holds no co-op slot 1", problem);
        Assert.False(RestoreParts.SavesOnly.TryCheck(slotsOnly, out problem));
        Assert.Equal("it holds no save files", problem);
        Assert.False(new RestoreParts(false, Array.Empty<int>()).TryCheck(both, out problem));
        Assert.Equal("nothing was chosen to restore", problem);
        Assert.True(new RestoreParts(false, new[] { 1 }).TryCheck(slotsOnly, out _));
    }

    [Fact]
    public void A_restore_stopped_after_its_moves_is_finished_whatever_the_backups_say()
    {
        var journal = new RestoreJournal(Backup, Before, new[] { "profileV2.es3" }, new[] { 2 }, moved: true);
        Assert.Equal(RecoveryAction.Finish, RestoreRecovery.Decide(journal, null, false, null, false));
    }

    [Fact]
    public void A_restore_stopped_mid_way_is_finished_from_a_whole_backup_holding_every_part()
    {
        var journal = new RestoreJournal(Backup, Before, new[] { "profileV2.es3", "statsV2.es3" }, new[] { 2 });
        SaveBackupManifest backup = Manifest(new[] { File("PROFILEV2.es3", "p"), File("statsV2.es3", "s") }, new[] { Slot(2, "run") });
        SaveBackupManifest before = Manifest(new[] { File("profileV2.es3", "old") }, Array.Empty<CoopSlotHash>());

        Assert.Equal(RecoveryAction.Complete, RestoreRecovery.Decide(journal, backup, true, before, true));
        Assert.Equal(RecoveryAction.Complete, RestoreRecovery.Decide(journal, backup, true, null, false));
    }

    [Fact]
    public void A_restore_whose_backup_cant_be_used_is_undone_from_the_one_before_or_only_reported()
    {
        var journal = new RestoreJournal(Backup, Before, new[] { "profileV2.es3", "statsV2.es3" }, new[] { 2 });
        SaveBackupManifest backup = Manifest(new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s") }, new[] { Slot(2, "run") });
        SaveBackupManifest missingSlot = Manifest(new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s") }, Array.Empty<CoopSlotHash>());
        SaveBackupManifest before = Manifest(new[] { File("profileV2.es3", "old") }, Array.Empty<CoopSlotHash>());

        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(journal, backup, false, before, true)); // damaged copies
        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(journal, null, false, before, true)); // the backup is gone
        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(journal, missingSlot, true, before, true)); // doesn't hold what was restored
        Assert.Equal(RecoveryAction.Report, RestoreRecovery.Decide(journal, backup, false, before, false));
        Assert.Equal(RecoveryAction.Report, RestoreRecovery.Decide(journal, null, false, null, false));
    }

    [Fact]
    public void A_start_that_holds_steam_clouds_loads_decides_as_always()
    {
        var journal = new RestoreJournal(Backup, Before, new[] { "profileV2.es3" }, new[] { 2 });
        var moved = new RestoreJournal(Backup, Before, new[] { "profileV2.es3" }, new[] { 2 }, moved: true);
        SaveBackupManifest backup = Manifest(new[] { File("profileV2.es3", "p") }, new[] { Slot(2, "run") });
        SaveBackupManifest before = Manifest(new[] { File("profileV2.es3", "old") }, Array.Empty<CoopSlotHash>());

        Assert.Equal(RecoveryAction.Complete, RestoreRecovery.Decide(journal, backup, true, before, true, cloudLoadsHeld: true));
        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(journal, backup, false, before, true, cloudLoadsHeld: true));
        Assert.Equal(RecoveryAction.Report, RestoreRecovery.Decide(journal, null, false, null, false, cloudLoadsHeld: true));
        Assert.Equal(RecoveryAction.Finish, RestoreRecovery.Decide(moved, null, false, null, false, cloudLoadsHeld: true));
    }

    [Fact]
    public void Without_steam_clouds_loads_held_a_restore_of_cloud_saved_files_is_undone_or_waits()
    {
        // Steam still holds the files from before the restore: finishing it would be undone by the game's first load of each.
        var journal = new RestoreJournal(Backup, Before, new[] { "profileV2.es3", "config.es3" }, new[] { 2 });
        SaveBackupManifest backup = Manifest(new[] { File("profileV2.es3", "p"), File("config.es3", "c") }, new[] { Slot(2, "run") });
        SaveBackupManifest before = Manifest(new[] { File("profileV2.es3", "old"), File("config.es3", "old") }, Array.Empty<CoopSlotHash>());

        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(journal, backup, true, before, true, cloudLoadsHeld: false)); // never completed
        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(journal, null, false, before, true, cloudLoadsHeld: false));
        Assert.Equal(RecoveryAction.Wait, RestoreRecovery.Decide(journal, backup, true, before, false, cloudLoadsHeld: false)); // "before" can't be read
        Assert.Equal(RecoveryAction.Wait, RestoreRecovery.Decide(journal, backup, true, null, false, cloudLoadsHeld: false));

        // All moved: its ledger step may have run, which an undo couldn't take back.
        var moved = new RestoreJournal(Backup, Before, new[] { "profileV2.es3" }, Array.Empty<int>(), moved: true);
        Assert.Equal(RecoveryAction.Wait, RestoreRecovery.Decide(moved, backup, true, before, true, cloudLoadsHeld: false));
    }

    [Fact]
    public void Without_steam_clouds_loads_held_a_restore_of_slots_or_local_files_is_decided_as_always()
    {
        var slots = new RestoreJournal(Backup, Before, Array.Empty<string>(), new[] { 1, 2 });
        var local = new RestoreJournal(Backup, Before, new[] { "CONFIG.es3", "profileV2_before_revert.es3" }, Array.Empty<int>());
        var movedLocal = new RestoreJournal(Backup, Before, new[] { "config.es3" }, Array.Empty<int>(), moved: true);
        SaveBackupManifest backup = Manifest(new[] { File("config.es3", "c"), File("profileV2_before_revert.es3", "r") }, new[] { Slot(1, "a"), Slot(2, "b") });
        SaveBackupManifest before = Manifest(Array.Empty<SaveFileHash>(), Array.Empty<CoopSlotHash>());

        Assert.False(RestoreRecovery.NamesCloudSavedFiles(slots));
        Assert.False(RestoreRecovery.NamesCloudSavedFiles(local));
        Assert.True(RestoreRecovery.NamesCloudSavedFiles(new RestoreJournal(Backup, Before, new[] { "RunDataV2.es3" }, Array.Empty<int>())));
        Assert.Equal(RecoveryAction.Complete, RestoreRecovery.Decide(slots, backup, true, before, true, cloudLoadsHeld: false));
        Assert.Equal(RecoveryAction.Complete, RestoreRecovery.Decide(local, backup, true, before, true, cloudLoadsHeld: false));
        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(slots, null, false, before, true, cloudLoadsHeld: false));
        Assert.Equal(RecoveryAction.Report, RestoreRecovery.Decide(slots, null, false, null, false, cloudLoadsHeld: false));
        Assert.Equal(RecoveryAction.Finish, RestoreRecovery.Decide(movedLocal, null, false, null, false, cloudLoadsHeld: false));
    }

    [Fact]
    public void Undoing_puts_back_what_was_there_and_deletes_what_wasnt()
    {
        // The restore backs every existing file and slot up first, so one the "before" backup lacks wasn't there.
        var journal = new RestoreJournal(Backup, Before, new[] { "profileV2.es3", "RunDataV2.es3" }, new[] { 1, 2 });
        SaveBackupManifest before = Manifest(new[] { File("PROFILEV2.ES3", "old"), File("statsV2.es3", "s") }, new[] { Slot(2, "old run") });

        Assert.Equal(new[] { ("profileV2.es3", true), ("RunDataV2.es3", false) }, RestoreRecovery.RollBackFiles(journal, before));
        Assert.Equal(new[] { (1, false), (2, true) }, RestoreRecovery.RollBackSlots(journal, before));
    }

    [Fact]
    public void The_steam_cloud_list_round_trips_and_finds_files_ignoring_case()
    {
        var list = new CloudPushList(Backup, new[] { File("statsV2.es3", "s"), File("profileV2.es3", "p") });

        Assert.True(CloudPushList.TryParse(list.ToText(), out CloudPushList? read));
        Assert.Equal(Backup, read!.Backup);
        Assert.Equal(new[] { "profileV2.es3", "statsV2.es3" }, read.Files.Select(f => f.Name));
        Assert.Equal(list.Files, read.Files);
        Assert.Equal(File("profileV2.es3", "p"), read.Find("PROFILEV2.ES3"));
        Assert.Null(read.Find("config.es3"));
        Assert.Null(read.Find(null));
        Assert.True(CloudPushList.TryParse(list.ToText().Replace("\n", "\r\n", StringComparison.Ordinal), out _));
    }

    [Fact]
    public void A_listed_file_is_sent_while_it_is_the_restored_one_and_leaves_once_the_game_saved_it()
    {
        SaveFileHash profile = File("profileV2.es3", "p");
        var list = new CloudPushList(Backup, new[] { profile });

        Assert.Equal(CloudPushState.Restored, list.Judge("profileV2.es3", profile.Sha256.ToLowerInvariant()));
        Assert.Equal(CloudPushState.ChangedSince, list.Judge("profileV2.es3", File("profileV2.es3", "p, saved since").Sha256));
        Assert.Equal(CloudPushState.ChangedSince, list.Judge("profileV2.es3", null)); // gone
        Assert.Equal(CloudPushState.NotListed, list.Judge("statsV2.es3", File("statsV2.es3", "s").Sha256));
    }

    [Fact]
    public void A_list_is_still_as_restored_while_any_local_file_is_the_restored_one()
    {
        SaveFileHash profile = File("profileV2.es3", "p");
        SaveFileHash stats = File("statsV2.es3", "s");
        var list = new CloudPushList(Backup, new[] { profile, stats });
        var local = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["profileV2.es3"] = File("profileV2.es3", "Steam's older copy").Sha256,
            ["statsV2.es3"] = stats.Sha256,
        };

        // Before the game's first load: the stats file is still the restored one.
        Assert.True(list.AnyAsRestored(name => local.GetValueOrDefault(name)));

        // After it: Steam's copy (or a save, or nothing) in place of each.
        local["statsV2.es3"] = null;
        Assert.False(list.AnyAsRestored(name => local.GetValueOrDefault(name)));
        Assert.False(new CloudPushList(Backup, Array.Empty<SaveFileHash>()).AnyAsRestored(_ => null));
        Assert.Throws<ArgumentNullException>(() => list.AnyAsRestored(null!));
    }

    [Fact]
    public void The_list_loses_files_as_they_go_and_a_newer_restore_replaces_the_same_names()
    {
        var first = new CloudPushList(Backup, new[] { File("profileV2.es3", "p1"), File("statsV2.es3", "s1") });
        var second = new CloudPushList("20261031-090000-manual", new[] { File("profileV2.es3", "p2"), File("RunDataV2.es3", "r2") });

        CloudPushList merged = first.Then(second);
        Assert.Equal("20261031-090000-manual", merged.Backup);
        Assert.Equal(new[] { "profileV2.es3", "RunDataV2.es3", "statsV2.es3" }, merged.Files.Select(f => f.Name));
        Assert.Equal(File("profileV2.es3", "p2"), merged.Find("profileV2.es3"));

        CloudPushList rest = merged.Without("PROFILEV2.es3").Without("statsV2.es3");
        Assert.Equal(new[] { "RunDataV2.es3" }, rest.Files.Select(f => f.Name));
        Assert.True(rest.Without("RunDataV2.es3").IsEmpty);
        Assert.True(CloudPushList.TryParse(rest.Without("RunDataV2.es3").ToText(), out CloudPushList? empty));
        Assert.True(empty!.IsEmpty);
    }

    [Fact]
    public void Files_a_restore_sent_to_steam_cloud_leave_the_list_and_only_those_steam_didnt_take_stay()
    {
        // A file Steam took goes up as the game closes; listed on, a later start would send the restored file
        // again over what Steam Cloud holds by then (another PC's newer save).
        var listed = new CloudPushList(Backup, new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s"), File("RunDataV2.es3", "r") });

        CloudPushList rest = listed.Without(new[] { "PROFILEV2.ES3", "statsV2.es3", "config.es3" });

        Assert.Equal(Backup, rest.Backup);
        Assert.Equal(new[] { "RunDataV2.es3" }, rest.Files.Select(f => f.Name));
        Assert.Equal(listed.Find("RunDataV2.es3"), rest.Find("RunDataV2.es3"));
        Assert.True(listed.Without(new[] { "profileV2.es3", "statsV2.es3", "RunDataV2.es3" }).IsEmpty);
        Assert.Equal(listed.Files, listed.Without(Array.Empty<string>()).Files);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file=profileV2.es3|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n")]
    [InlineData("backup=logs\n")]
    [InlineData("backup=20261030-191502-host\nbackup=20261030-191502-host\n")]
    [InlineData("backup=20261030-191502-host\nfile=..\\profileV2.es3|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n")]
    [InlineData("backup=20261030-191502-host\nfile=Player.log|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n")]
    [InlineData("backup=20261030-191502-host\nfile=profileV2.es3|-1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n")]
    [InlineData("backup=20261030-191502-host\nfile=profileV2.es3|1|xyz\n")]
    [InlineData("backup=20261030-191502-host\nfile=profileV2.es3|1\n")]
    [InlineData("backup=20261030-191502-host\nfile=profileV2.es3|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD|x\n")]
    [InlineData("backup=20261030-191502-host\nfile=a.es3|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\nfile=A.ES3|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n")]
    [InlineData("backup=20261030-191502-host\nwhat=ever\n")]
    [InlineData("backup=20261030-191502-host\njunk\n")]
    public void A_damaged_steam_cloud_list_is_refused_without_throwing(string? text)
    {
        Assert.False(CloudPushList.TryParse(text, out CloudPushList? list));
        Assert.Null(list);
    }

    [Fact]
    public void The_steam_cloud_list_has_a_cap()
    {
        string many = "backup=20261030-191502-host\n" + string.Join(string.Empty, Enumerable.Range(0, 257).Select(i =>
            $"file=f{i}.es3|1|BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n"));
        Assert.False(CloudPushList.TryParse(many, out _));
    }
}
