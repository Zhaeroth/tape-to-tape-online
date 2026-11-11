using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Campaign;

public class BackupRowsTests
{
    private const int Slots = 3;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<BackupSlotRun>> NoRuns = new Dictionary<string, IReadOnlyList<BackupSlotRun>>();

    private static BackupInfo Backup(string name, int day, int hour, string reason, int files, params int[] slots) =>
        new(name, new DateTime(2026, 11, day, hour, 14, 0, DateTimeKind.Local), reason, files, slots);

    private static BackupSlotRun Run(int slot, string sha, BackupRunKind kind = BackupRunKind.Run, string squad = "vanilla", int act = 2, int floor = 3) =>
        new(slot, sha, kind, squad, act, floor);

    private static string Words(IEnumerable<LobbyLine> lines) => string.Join(" | ", lines.Select(l => l.Term + "(" + string.Join(",", l.Args) + ")"));

    [Fact]
    public void Your_saves_has_a_row_per_backup_with_files_in_the_lists_order_newest_first()
    {
        // As the list gives them: newest first.
        var backups = new[]
        {
            Backup("20261105T201400Z-friend", 5, 21, "before following a host", 7),
            Backup("20261104T201400Z-host", 4, 21, "before this Start", 0, 2),
            Backup("20261103T201400Z-host", 3, 21, "before hosting", 6),
        };

        BackupRowSet set = BackupRows.Build(backups, NoRuns, Slots);

        Assert.Equal(new[] { "20261105T201400Z-friend", "20261103T201400Z-host" }, set.Saves.Select(r => r.Backup));
        Assert.All(set.Saves, r => Assert.Equal(BackupTab.Saves, r.Tab));
        Assert.All(set.Saves, r => Assert.Null(r.Run));
        Assert.Same(set.Saves, set.Of(BackupTab.Saves));
        Assert.Empty(set.CoopRuns);
    }

    [Fact]
    public void Two_backups_taken_at_one_time_stay_in_the_lists_order()
    {
        // The list orders two of one moment by name, the later name first.
        var backups = new[]
        {
            Backup("20261105T201400Z-host-2", 5, 21, "before this Start", 6),
            Backup("20261105T201400Z-host", 5, 21, "before hosting", 6),
        };

        BackupRowSet set = BackupRows.Build(backups, NoRuns, Slots);

        Assert.Equal(new[] { "20261105T201400Z-host-2", "20261105T201400Z-host" }, set.Saves.Select(r => r.Backup));
    }

    [Fact]
    public void On_the_night_the_clocks_go_back_the_later_backup_stays_first_though_its_time_reads_earlier()
    {
        // 01:50 before the change (05:50 UTC) and 01:05 after it (06:05 UTC), where the clocks go back an hour at 02:00. The list gives them
        // by the moment each was taken; a row has only the local time it shows, and sorted by that the earlier backup would come first.
        var later = new BackupInfo("20261101T060500Z-host", new DateTime(2026, 11, 1, 1, 5, 0, DateTimeKind.Local), "before this Start", 6, new[] { 1 });
        var earlier = new BackupInfo("20261101T055000Z-host", new DateTime(2026, 11, 1, 1, 50, 0, DateTimeKind.Local), "before hosting", 6, new[] { 1 });
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>>
        {
            [later.Name] = new[] { Run(1, "after") },
            [earlier.Name] = new[] { Run(1, "before") },
        };

        BackupRowSet set = BackupRows.Build(new[] { later, earlier }, runs, Slots);

        Assert.Equal(new[] { later.Name, earlier.Name }, set.Saves.Select(r => r.Backup));
        Assert.Equal(new[] { later.Name, earlier.Name }, set.CoopRuns.Select(r => r.Backup));
        Assert.Equal(new[] { "2026-11-01 01:05", "2026-11-01 01:50" }, set.Saves.Select(r => BackupRows.TimeLabel(r.TakenAt))); // the times shown are as they were
    }

    [Fact]
    public void Of_two_copies_of_one_file_the_one_the_list_gives_first_is_kept_whatever_their_local_times()
    {
        // The same slot file in both backups of that night: the newer backup's row stays, the older one's is the repeat.
        var later = new BackupInfo("20261101T060500Z-host", new DateTime(2026, 11, 1, 1, 5, 0, DateTimeKind.Local), "before this Start", 6, new[] { 2 });
        var earlier = new BackupInfo("20261101T055000Z-host", new DateTime(2026, 11, 1, 1, 50, 0, DateTimeKind.Local), "before hosting", 6, new[] { 2 });
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>>
        {
            [later.Name] = new[] { Run(2, "same") },
            [earlier.Name] = new[] { Run(2, "same") },
        };

        BackupRowSet set = BackupRows.Build(new[] { later, earlier }, runs, Slots);

        Assert.Equal(later.Name, Assert.Single(set.CoopRuns).Backup);
        Assert.Equal(1, set.Repeats);
    }

    [Fact]
    public void A_missing_entry_in_the_list_is_skipped()
    {
        var backups = new BackupInfo[] { Backup("20261105T201400Z-host", 5, 21, "before hosting", 6), null!, Backup("20261104T201400Z-host", 4, 21, "before hosting", 6) };

        Assert.Equal(new[] { "20261105T201400Z-host", "20261104T201400Z-host" }, BackupRows.Build(backups, NoRuns, Slots).Saves.Select(r => r.Backup));
    }

    [Fact]
    public void Co_op_runs_has_a_row_per_backup_and_slot_slots_in_order()
    {
        var backups = new[]
        {
            Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 1, 2),
            Backup("20261104T201400Z-host", 4, 21, "before hosting", 6, 3),
        };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>>
        {
            ["20261105T201400Z-host"] = new[] { Run(2, "b"), Run(1, "a") },
            ["20261104T201400Z-host"] = new[] { Run(3, "c") },
        };

        BackupRowSet set = BackupRows.Build(backups, runs, Slots);

        Assert.Equal(new[] { ("20261105T201400Z-host", 1), ("20261105T201400Z-host", 2), ("20261104T201400Z-host", 3) }, set.CoopRuns.Select(r => (r.Backup, r.Slot)));
        Assert.All(set.CoopRuns, r => Assert.Equal(BackupTab.CoopRuns, r.Tab));
    }

    [Fact]
    public void Lost_runs_and_repeats_are_left_out_and_counted_and_the_newest_copy_stays()
    {
        var backups = new[]
        {
            Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 1, 2),
            Backup("20261104T201400Z-host", 4, 21, "before hosting", 6, 1, 2),
            Backup("20261103T201400Z-host", 3, 21, "before hosting", 6, 1),
        };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>>
        {
            ["20261105T201400Z-host"] = new[] { Run(1, "same"), Run(2, "lost", BackupRunKind.Lost) },
            ["20261104T201400Z-host"] = new[] { Run(1, "same"), Run(2, "older") },
            ["20261103T201400Z-host"] = new[] { Run(1, "same") },
        };

        BackupRowSet set = BackupRows.Build(backups, runs, Slots);

        Assert.Equal(new[] { ("20261105T201400Z-host", 1), ("20261104T201400Z-host", 2) }, set.CoopRuns.Select(r => (r.Backup, r.Slot)));
        Assert.Equal(1, set.LostRuns);
        Assert.Equal(2, set.Repeats);
    }

    [Fact]
    public void The_same_bytes_in_two_slots_are_two_rows()
    {
        var backups = new[] { Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 1, 2) };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>> { ["20261105T201400Z-host"] = new[] { Run(1, "same"), Run(2, "same") } };

        Assert.Equal(2, BackupRows.Build(backups, runs, Slots).CoopRuns.Count);
    }

    [Fact]
    public void An_unreadable_run_is_listed_without_words_and_slots_past_this_versions_are_left_out()
    {
        var backups = new[] { Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 1, 4) };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>>
        {
            ["20261105T201400Z-host"] = new[] { new BackupSlotRun(1, "a", BackupRunKind.Unreadable, string.Empty, 0, 0), Run(4, "d") },
        };

        BackupRowSet set = BackupRows.Build(backups, runs, Slots);

        BackupRow row = Assert.Single(set.CoopRuns);
        Assert.Equal(1, row.Slot);
        Assert.Empty(row.Detail);
    }

    [Fact]
    public void A_slot_run_the_backup_info_does_not_list_is_left_out()
    {
        var backups = new[] { Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 1) };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>> { ["20261105T201400Z-host"] = new[] { Run(1, "a"), Run(2, "b") } };

        Assert.Equal(new[] { 1 }, BackupRows.Build(backups, runs, Slots).CoopRuns.Select(r => r.Slot));
    }

    [Fact]
    public void No_backups_make_two_empty_tabs()
    {
        BackupRowSet set = BackupRows.Build(Array.Empty<BackupInfo>(), NoRuns, Slots);

        Assert.Empty(set.Of(BackupTab.Saves));
        Assert.Empty(set.Of(BackupTab.CoopRuns));
        Assert.Empty(BackupRowSet.Empty.Saves);
        Assert.Equal(0, set.LostRuns + set.Repeats);
    }

    [Theory]
    [InlineData("20261105T201400Z-host", "before hosting", BackupReasonKind.Hosting)]
    [InlineData("20261105T201400Z-host", "before this Start", BackupReasonKind.Start)]
    [InlineData("20261105T201400Z-friend", "before following a host", BackupReasonKind.Following)]
    [InlineData("20261030-191502-friend", "before following Alex", BackupReasonKind.Following)]
    [InlineData("20261105T201400Z-restore", "before restoring 20261104T201400Z-host", BackupReasonKind.Restore)]
    [InlineData("20261105T201400Z-restore", "before restoring co-op slot 2 from 20261104T201400Z-host", BackupReasonKind.Restore)]
    [InlineData("20261105T201400Z-manual", "taken from the panel", BackupReasonKind.Manual)]
    [InlineData("20261105T201400Z-manual-2", "anything", BackupReasonKind.Manual)]
    [InlineData("20261105T201400Z-host", "(its manifest is missing or damaged)", BackupReasonKind.Other)]
    [InlineData("not a backup", "", BackupReasonKind.Other)]
    [InlineData(null, null, BackupReasonKind.Other)]
    public void The_reason_comes_from_the_manifest_then_the_role(string? name, string? reason, BackupReasonKind expected) =>
        Assert.Equal(expected, BackupRows.ReasonOf(name, reason));

    [Fact]
    public void Each_reason_has_its_own_word()
    {
        var words = Enum.GetValues<BackupReasonKind>().Select(BackupRows.ReasonWord).ToList();

        Assert.Equal(words.Count, words.Distinct().Count());
        Assert.Same(LobbyWords.BackupReasonHosting, BackupRows.ReasonWord(BackupReasonKind.Hosting));
        Assert.Same(LobbyWords.BackupReasonOther, BackupRows.ReasonWord(BackupReasonKind.Other));
    }

    [Fact]
    public void The_time_is_local_digits_only()
    {
        Assert.Equal("2026-11-05 21:14", BackupRows.TimeLabel(new DateTime(2026, 11, 5, 21, 14, 59, DateTimeKind.Local)));
    }

    [Fact]
    public void A_saves_row_says_its_time_its_reason_and_its_files_and_restores_the_saves_alone()
    {
        BackupRow row = Assert.Single(BackupRows.Build(new[] { Backup("20261105T201400Z-friend", 5, 21, "before following a host", 7) }, NoRuns, Slots).Saves);

        Assert.Equal("{0}(2026-11-05 21:14)", Words(row.Title));
        Assert.Equal($"{LobbyWords.BackupReasonFollowing.Key}() | {LobbyWords.BackupFiles.Key}(7)", Words(row.Detail));
        Assert.Same(LobbyWords.RestoreSavesDetail, row.DetailWord);
        Assert.Equal(new LobbyLine(LobbyWords.AskRestoreSaves, "2026-11-05 21:14"), row.Question);
        Assert.True(row.Parts.Saves);
        Assert.Empty(row.Parts.Slots);
        Assert.Equal("saves", row.PartForLog);
        Assert.Equal(0, row.Slot);
    }

    [Fact]
    public void A_co_op_row_says_its_time_its_slot_and_its_run_and_restores_that_slot_alone()
    {
        var backups = new[] { Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 2) };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>> { ["20261105T201400Z-host"] = new[] { Run(2, "b", squad: "oldschool", act: 3, floor: 5) } };

        BackupRow row = Assert.Single(BackupRows.Build(backups, runs, Slots).CoopRuns);

        Assert.Equal($"{{0}}(2026-11-05 21:14) | {LobbyWords.Slot.Key}(2)", Words(row.Title));
        Assert.Equal($"{BackupRows.SquadTerm}(oldschool) | {LobbyTerms.ActN}(3) | {LobbyWords.FloorN.Key}(5)", Words(row.Detail));
        Assert.Same(LobbyWords.RestoreSlotDetail, row.DetailWord);
        Assert.Equal(new LobbyLine(LobbyWords.AskRestoreSlot, "2", "2026-11-05 21:14"), row.Question);
        Assert.False(row.Parts.Saves);
        Assert.Equal(new[] { 2 }, row.Parts.Slots);
        Assert.Equal("slot 2", row.PartForLog);
    }

    [Fact]
    public void A_backup_with_files_and_slots_is_on_both_tabs()
    {
        var backups = new[] { Backup("20261105T201400Z-host", 5, 21, "before hosting", 6, 1) };
        var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>> { ["20261105T201400Z-host"] = new[] { Run(1, "a") } };

        BackupRowSet set = BackupRows.Build(backups, runs, Slots);

        Assert.Single(set.Saves);
        Assert.Single(set.CoopRuns);
    }

    private static bool Shell(string id) => id.StartsWith("T2TOnline", StringComparison.Ordinal);

    [Fact]
    public void The_screen_may_restore_with_only_settings_and_the_mods_screens_over_the_title()
    {
        Assert.True(BackupRestoreTitle.Allows(RestoreAsker.BackupsScreen, false, new[] { "Settings" }, Shell));
        Assert.True(BackupRestoreTitle.Allows(RestoreAsker.BackupsScreen, false, new[] { "Settings", "T2TOnlineBackups" }, Shell));
        Assert.True(BackupRestoreTitle.Allows(RestoreAsker.BackupsScreen, false, Array.Empty<string>(), Shell));
        Assert.False(BackupRestoreTitle.Allows(RestoreAsker.BackupsScreen, false, new[] { "Settings", "Glossary" }, Shell));
        Assert.False(BackupRestoreTitle.Allows(RestoreAsker.BackupsScreen, false, new[] { "settings" }, Shell));
        Assert.False(BackupRestoreTitle.Allows(RestoreAsker.BackupsScreen, true, new[] { "Settings" }, Shell));
    }

    [Fact]
    public void The_panel_keeps_its_idle_title_rule()
    {
        Assert.True(BackupRestoreTitle.Allows(RestoreAsker.Panel, false, Array.Empty<string>(), Shell));
        Assert.True(BackupRestoreTitle.Allows(RestoreAsker.Panel, false, new[] { "T2TOnlineLobby" }, Shell));
        Assert.False(BackupRestoreTitle.Allows(RestoreAsker.Panel, false, new[] { "Settings" }, Shell));
        Assert.False(BackupRestoreTitle.Allows(RestoreAsker.Panel, true, Array.Empty<string>(), Shell));
    }
}
