using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A restore the game stopped in the middle of, put right late: what was written since it stopped is backed up before any file
/// goes in place, nothing is backed up when nothing was, a backup that can't be taken doesn't stop the recovery, an all-moved
/// restore isn't finished over files that changed since, and retention keeps the journal's two backups.
/// </summary>
public class RestoreRecoveryKeepTests
{
    private const string Restored = "20261108T200000Z-host";
    private const string Before = "20261110T113000Z-restore";
    private const string Kept = "20261111T090000Z-restore";
    private const string Profile = "profileV2.es3";
    private const string Stats = "statsV2.es3";
    private const string Run = "RunDataV2.es3";

    // The game's profile save around its play-time counter (Easy Save 3 JSON).
    private static byte[] ProfileBytes(string games, string seconds) => Encoding.UTF8.GetBytes(
        "{\r\n\t\"profile\" : {\r\n\t\t\"__type\" : \"ProfileData\",\r\n" +
        $"\t\t\"TttActiveSeconds\" : {seconds},\r\n\t\t\"RogueCampaignGames\" : {games}\r\n\t}}\r\n}}");

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    // What the restored backup holds: the player's saves from an earlier evening, a run, and co-op slot 2.
    private static readonly Dictionary<string, byte[]> RestoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Profile] = ProfileBytes("20", "5000.5"),
        [Stats] = Text("stats from the backup"),
        [Run] = Text("a run from the backup"),
    };

    private static readonly byte[] RestoredSlot = Text("co-op run from the backup");

    // What was there before the restore: newer saves, no run file, an older co-op run in slot 2.
    private static readonly Dictionary<string, byte[]> BeforeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Profile] = ProfileBytes("26", "9000.25"),
        [Stats] = Text("stats from before the restore"),
    };

    private static readonly byte[] BeforeSlot = Text("co-op run from before the restore");

    private static SaveBackupManifest Manifest(string role, string reason, Dictionary<string, byte[]> files, byte[] slot) =>
        new(role, reason, new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc), files.Select(f => SaveFileHash.Of(f.Key, f.Value)), new[] { CoopSlotHash.Of(2, slot) });

    private static SaveBackupManifest Backup => Manifest("host", "before hosting", RestoredFiles, RestoredSlot);

    private static SaveBackupManifest BeforeBackup => Manifest("restore", "before restoring " + Restored, BeforeFiles, BeforeSlot);

    private static RestoreJournal Journal(bool moved = false) => new(Restored, Before, new[] { Profile, Stats, Run }, new[] { 2 }, moved);

    // Each backup's own copies, as the plugin reads them from the backup's folder.
    private static byte[]? Copy(string backup, string file) => backup switch
    {
        Restored => RestoredFiles.GetValueOrDefault(file),
        Before => BeforeFiles.GetValueOrDefault(file),
        _ => null,
    };

    private static RestoreOnDisk Read(Dictionary<string, byte[]> files, byte[]? slot, RestoreJournal? journal = null, SaveBackupManifest? backup = null,
        SaveBackupManifest? before = null, bool knownBackup = true, bool knownBefore = true, Func<string, string, byte[]?>? copy = null) =>
        RestoreOnDisk.Read(journal ?? Journal(), knownBackup ? backup ?? Backup : null, knownBefore ? before ?? BeforeBackup : null, files,
            slot == null ? new Dictionary<int, byte[]>() : new Dictionary<int, byte[]> { [2] = slot }, copy ?? Copy);

    // Where a restore stopped half way leaves things: the profile moved, the stats and the run file not yet, the slot not yet.
    private static Dictionary<string, byte[]> HalfRestored() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Profile] = RestoredFiles[Profile],
        [Stats] = BeforeFiles[Stats],
        ["config.es3"] = Text("display settings, not in the journal"),
    };

    [Fact]
    public void At_the_very_next_start_nothing_was_written_since_and_nothing_is_kept()
    {
        RestoreOnDisk half = Read(HalfRestored(), BeforeSlot);
        Assert.False(half.WrittenSince);
        Assert.Empty(half.Changed);
        Assert.Equal(
            new[] { RestoreFileState.Restored, RestoreFileState.AsBefore, RestoreFileState.AsBefore, RestoreFileState.AsBefore },
            half.Entries.Select(e => e.State));
        Assert.Equal(new[] { Profile, Stats, Run, "co-op slot 2" }, half.Entries.Select(e => e.Label));

        // None moved yet, or all of them: as left by the restore either way.
        Assert.False(Read(new Dictionary<string, byte[]>(BeforeFiles), BeforeSlot).WrittenSince);
        RestoreOnDisk all = Read(new Dictionary<string, byte[]>(RestoredFiles), RestoredSlot, Journal(moved: true));
        Assert.False(all.WrittenSince);
        Assert.True(all.AllRestored);

        // The recovery then puts its files in place and keeps nothing.
        var calls = new List<string>();
        RecoveryWrites writes = RecoveryKeep.Run(half, string.Empty, () => { calls.Add("keep"); return RecoveryKept.In(Kept); }, _ => { calls.Add("put"); return Array.Empty<string>(); });
        Assert.Equal(new[] { "put" }, calls);
        Assert.Equal(RecoveryKeepKind.NotNeeded, writes.Kept.Kind);
        Assert.Empty(writes.Failed);
    }

    [Fact]
    public void A_start_that_played_on_the_half_restored_saves_is_kept_before_the_restore_is_finished_from_its_backup()
    {
        // A start that couldn't put the restore right (a mixed install): Steam Cloud's copy of the profile came back over it, then a
        // game was played, a run started.
        Dictionary<string, byte[]> played = HalfRestored();
        played[Profile] = ProfileBytes("27", "9400.75");
        played[Run] = Text("a run started on the half-restored saves");
        RestoreOnDisk onDisk = Read(played, BeforeSlot);

        Assert.True(onDisk.WrittenSince);
        Assert.Equal(new[] { Profile, Run }, onDisk.Changed);
        Assert.Equal(RecoveryAction.Complete, RestoreRecovery.Decide(Journal(), Backup, true, BeforeBackup, true, cloudLoadsHeld: true));

        var calls = new List<string>();
        RecoveryKept? told = null;
        RecoveryWrites writes = RecoveryKeep.Run(onDisk, string.Empty, () => { calls.Add("keep"); return RecoveryKept.In(Kept); }, kept =>
        {
            told = kept;
            calls.Add("put");
            return Array.Empty<string>();
        });

        Assert.Equal(new[] { "keep", "put" }, calls);
        Assert.Equal(RecoveryKept.In(Kept), writes.Kept);
        Assert.Equal(writes.Kept, told);
        Assert.Equal("before restoring " + Restored, RecoveryKeep.Reason(Journal(), RecoveryAction.Complete));
        Assert.Equal(
            $"Backups: {Profile} and {Run} changed since the restore of backups/{Restored} stopped (neither as restored nor as before it); " +
            $"what is there now is kept in backups/{Kept}, then the restore is finished from backups/{Restored}.",
            RecoveryKeep.Line(Journal(), RecoveryAction.Complete, onDisk, writes.Kept));
    }

    [Fact]
    public void A_start_that_played_on_them_is_kept_before_the_restore_is_undone_too()
    {
        // The restored backup can't be used now (a copy damaged), so the restore is undone from the one taken before it.
        Dictionary<string, byte[]> played = HalfRestored();
        played[Stats] = Text("stats after a game played on the half-restored saves");
        RestoreOnDisk onDisk = Read(played, BeforeSlot);

        Assert.True(onDisk.WrittenSince);
        Assert.Equal(new[] { Stats }, onDisk.Changed);
        Assert.Equal(RecoveryAction.RollBack, RestoreRecovery.Decide(Journal(), Backup, false, BeforeBackup, true, cloudLoadsHeld: true));

        var calls = new List<string>();
        RecoveryWrites writes = RecoveryKeep.Run(onDisk, string.Empty, () => { calls.Add("keep"); return RecoveryKept.In(Kept); }, _ =>
        {
            calls.Add("put");
            return new[] { Run };
        });

        Assert.Equal(new[] { "keep", "put" }, calls);
        Assert.Equal(new[] { Run }, writes.Failed);
        Assert.Equal("before restoring " + Before, RecoveryKeep.Reason(Journal(), RecoveryAction.RollBack)); // an undo restores that one
        Assert.EndsWith($"what is there now is kept in backups/{Kept}, then the restore is undone from backups/{Before}.",
            RecoveryKeep.Line(Journal(), RecoveryAction.RollBack, onDisk, writes.Kept), StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_that_cant_be_taken_never_stops_the_recovery()
    {
        Dictionary<string, byte[]> played = HalfRestored();
        played[Profile] = ProfileBytes("27", "9400.75");
        RestoreOnDisk onDisk = Read(played, BeforeSlot);

        var calls = new List<string>();
        RecoveryWrites failed = RecoveryKeep.Run(onDisk, string.Empty, () => { calls.Add("keep"); return RecoveryKept.Failure("the backup couldn't be written (disk full)"); }, kept =>
        {
            calls.Add("put");
            return Array.Empty<string>();
        });
        Assert.Equal(new[] { "keep", "put" }, calls);
        Assert.Equal(RecoveryKeepKind.Failed, failed.Kept.Kind);
        Assert.Equal(
            $"Backups: {Profile} changed since the restore of backups/{Restored} stopped (neither as restored nor as before it); what is there now couldn't be kept first " +
            $"(the backup couldn't be written (disk full)), and the restore is finished from backups/{Restored} anyway, so the saves aren't left half restored.",
            RecoveryKeep.Line(Journal(), RecoveryAction.Complete, onDisk, failed.Kept));

        // One that throws is the same.
        calls.Clear();
        RecoveryWrites threw = RecoveryKeep.Run(onDisk, string.Empty, () => { calls.Add("keep"); throw new IOException("There is not enough space on the disk."); }, _ =>
        {
            calls.Add("put");
            return Array.Empty<string>();
        });
        Assert.Equal(new[] { "keep", "put" }, calls);
        Assert.Equal(RecoveryKeepKind.Failed, threw.Kept.Kind);
        Assert.StartsWith("taking it failed: IOException", threw.Kept.Problem, StringComparison.Ordinal);

        // A save folder that can't be read: nothing to keep it from, and the files still go in place.
        calls.Clear();
        RecoveryWrites unread = RecoveryKeep.Run(null, "the save files couldn't be read: access denied", () => { calls.Add("keep"); return RecoveryKept.In(Kept); }, _ =>
        {
            calls.Add("put");
            return Array.Empty<string>();
        });
        Assert.Equal(new[] { "put" }, calls);
        Assert.Equal(RecoveryKept.Failure("the save files couldn't be read: access denied"), unread.Kept);
        Assert.Equal(
            $"Backups: the save folder couldn't be read to see what changed since the restore of backups/{Restored} stopped (the save files couldn't be read: access denied); " +
            $"nothing could be kept first, and the restore is undone from backups/{Before} anyway, so the saves aren't left half restored.",
            RecoveryKeep.Line(Journal(), RecoveryAction.RollBack, null, unread.Kept));
    }

    [Fact]
    public void The_line_when_nothing_was_written_since_says_so()
    {
        RestoreOnDisk onDisk = Read(HalfRestored(), BeforeSlot);
        Assert.Equal(
            $"Backups: every file the restore of backups/{Restored} names is as that restore left it (4 of 4 as restored or as before it), " +
            "so nothing was written since it stopped and no backup is taken first.",
            RecoveryKeep.Line(Journal(), RecoveryAction.Complete, onDisk, RecoveryKept.None));
    }

    [Fact]
    public void The_games_play_time_counter_alone_is_not_play()
    {
        // A start that only reached the game's opening and quit: Steam Cloud's copy of the profile (the one from before the restore)
        // saved again with its play-time counter moved on, nothing else.
        Dictionary<string, byte[]> started = HalfRestored();
        started[Profile] = ProfileBytes("26", "9012.5");
        var asked = new List<(string, string)>();
        RestoreOnDisk onDisk = Read(started, BeforeSlot, copy: (backup, file) =>
        {
            asked.Add((backup, file));
            return Copy(backup, file);
        });
        Assert.False(onDisk.WrittenSince);
        Assert.Equal(RestoreFileState.AsBefore, onDisk.Entries[0].State);
        Assert.Equal(new[] { (Restored, Profile), (Before, Profile) }, asked);

        // As the restored backup's, but for the counter.
        started[Profile] = ProfileBytes("20", "5003");
        Assert.Equal(RestoreFileState.Restored, Read(started, BeforeSlot).Entries[0].State);

        // Without the backups' copies, or with one that isn't what its manifest says, or with real play, it's written since.
        Assert.True(Read(started, BeforeSlot, copy: (_, _) => null).WrittenSince);
        Assert.True(Read(started, BeforeSlot, copy: (backup, file) => backup == Restored ? ProfileBytes("21", "5000.5") : Copy(backup, file)).WrittenSince);
        started[Profile] = ProfileBytes("21", "5003");
        Assert.True(Read(started, BeforeSlot).WrittenSince);

        // A copy that isn't what its manifest says vouches for nothing, even when the profile on disk is that copy but for the counter.
        Assert.True(Read(started, BeforeSlot, copy: (backup, file) => backup == Restored ? ProfileBytes("21", "5000.5") : Copy(backup, file)).WrittenSince);
        Assert.True(Read(started, BeforeSlot, copy: (backup, file) => backup == Before ? ProfileBytes("21", "9000.25") : Copy(backup, file)).WrittenSince);

        // Only the profile holds the counter: any other file's difference is a difference, and no copy is asked for it.
        asked.Clear();
        Dictionary<string, byte[]> stats = HalfRestored();
        stats[Stats] = Text("{\"TttActiveSeconds\" : 1}");
        RestoreOnDisk statsOnDisk = Read(stats, BeforeSlot, copy: (backup, file) =>
        {
            asked.Add((backup, file));
            return Copy(backup, file);
        });
        Assert.Equal(new[] { Stats }, statsOnDisk.Changed);
        Assert.Empty(asked);
    }

    [Fact]
    public void A_backup_whose_manifest_cant_be_read_vouches_for_nothing()
    {
        // Without the "before" backup's manifest, a file not yet restored can't be told from one written since: kept, in doubt.
        RestoreOnDisk noBefore = Read(HalfRestored(), BeforeSlot, knownBefore: false);
        Assert.False(noBefore.ManifestsRead);
        Assert.Equal(new[] { Stats, Run, "co-op slot 2" }, noBefore.Changed);
        Assert.Contains("(as far as can be told: a backup's manifest can't be read)", RecoveryKeep.Line(Journal(), RecoveryAction.Complete, noBefore, RecoveryKept.In(Kept)), StringComparison.Ordinal);

        // Without the restored backup's, a file already restored can't be either.
        RestoreOnDisk noBackup = Read(HalfRestored(), BeforeSlot, knownBackup: false);
        Assert.Equal(new[] { Profile }, noBackup.Changed);
        Assert.False(noBackup.AllRestored);
    }

    [Fact]
    public void A_file_gone_is_as_before_only_where_there_was_none_before()
    {
        // The run file wasn't there before the restore: none now is as before. The stats file was in both: none now was deleted since.
        Dictionary<string, byte[]> gone = HalfRestored();
        gone.Remove(Stats);
        RestoreOnDisk onDisk = Read(gone, null);
        Assert.Equal(RestoreFileState.AsBefore, onDisk.Entries.Single(e => e.Label == Run).State);
        Assert.Equal(new[] { Stats, "co-op slot 2" }, onDisk.Changed);

        // A slot the "before" backup didn't hold, and none now: as before.
        SaveBackupManifest beforeWithoutSlot = new("restore", "before restoring " + Restored, DateTime.UtcNow, BeforeFiles.Select(f => SaveFileHash.Of(f.Key, f.Value)), Array.Empty<CoopSlotHash>());
        Assert.Equal(RestoreFileState.AsBefore, Read(HalfRestored(), null, before: beforeWithoutSlot).Entries.Single(e => e.Label == "co-op slot 2").State);

        // Names are matched as Windows does, ignoring case.
        var upper = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["PROFILEV2.ES3"] = RestoredFiles[Profile], ["STATSV2.ES3"] = BeforeFiles[Stats] };
        Assert.False(Read(upper, BeforeSlot).WrittenSince);
    }

    [Fact]
    public void An_all_moved_restore_whose_files_changed_since_isnt_finished_over_them()
    {
        // All in place when the game stopped; then a start that couldn't finish it put Steam Cloud's copies back and played on them.
        RestoreJournal moved = Journal(moved: true);
        Assert.Equal(RecoveryAction.Finish, RestoreRecovery.Decide(moved, Backup, false, BeforeBackup, false, cloudLoadsHeld: true));

        var overtaken = new Dictionary<string, byte[]>(RestoredFiles, StringComparer.OrdinalIgnoreCase)
        {
            [Profile] = BeforeFiles[Profile],
            [Stats] = Text("stats after a game played on Steam Cloud's copies"),
        };
        RestoreOnDisk onDisk = Read(overtaken, RestoredSlot, moved);
        Assert.False(onDisk.AllRestored);
        Assert.Equal(
            $"Backups: the restore of backups/{Restored} was all in place when it stopped, but {Profile} and {Stats} aren't as restored now " +
            "(written since, or Steam Cloud's copies put back by a start that couldn't finish it), so what follows it isn't done over them: " +
            $"the shared-progress ledger, Steam Cloud's list and the per-run counters stay as they are; backups/{Restored} still holds the restored files.",
            RecoveryKeep.OvertakenLine(onDisk));

        // Still all as restored, the counter aside (the game's opening saved the profile): finished as always.
        var stillRestored = new Dictionary<string, byte[]>(RestoredFiles, StringComparer.OrdinalIgnoreCase) { [Profile] = ProfileBytes("20", "5010") };
        Assert.True(Read(stillRestored, RestoredSlot, moved).AllRestored);
        Assert.False(Read(stillRestored, BeforeSlot, moved).AllRestored);
    }

    [Fact]
    public void A_long_list_of_changed_files_is_cut_short()
    {
        string[] names = Enumerable.Range(1, 11).Select(i => $"save{i}.es3").ToArray();
        var journal = new RestoreJournal(Restored, Before, names, Array.Empty<int>());
        var empty = new SaveBackupManifest("host", "before hosting", DateTime.UtcNow, names.Select(n => SaveFileHash.Of(n, Text("x"))));
        RestoreOnDisk onDisk = RestoreOnDisk.Read(journal, empty, empty, names.ToDictionary(n => n, n => Text("played")), new Dictionary<int, byte[]>());
        Assert.StartsWith("Backups: save1.es3, save2.es3, save3.es3, save4.es3, save5.es3, save6.es3, save7.es3, save8.es3 and 3 more changed since",
            RecoveryKeep.Line(journal, RecoveryAction.Complete, onDisk, RecoveryKept.In(Kept)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_older_backups_name_reads_in_utc_in_the_lines()
    {
        // A backup named in local time before 0.11.0 is named by its UTC time, never as it is on disk (D129).
        var journal = new RestoreJournal("20261103-191502-host", Before, new[] { Profile }, Array.Empty<int>());
        RestoreOnDisk onDisk = RestoreOnDisk.Read(journal, Backup, BeforeBackup, new Dictionary<string, byte[]> { [Profile] = Text("played") }, new Dictionary<int, byte[]>());
        string line = RecoveryKeep.Line(journal, RecoveryAction.Complete, onDisk, RecoveryKept.In(Kept));
        Assert.DoesNotContain("20261103-191502-host", line, StringComparison.Ordinal);
        Assert.Contains("(named in local time on disk)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("20261103-191502-host", RecoveryKeep.OvertakenLine(onDisk), StringComparison.Ordinal);
    }

    [Fact]
    public void The_kept_backup_has_a_restores_own_reason_which_the_backups_screen_reads_as_one()
    {
        // The restore's own backup of what it replaces says the same as before.
        Assert.Equal("before restoring " + Restored, RestoreParts.SavesOnly.BeforeReason(Restored));
        Assert.Equal("before restoring co-op slot 2 from " + Restored, new RestoreParts(false, new[] { 2 }).BeforeReason(Restored));
        Assert.Equal("before restoring " + Restored, new RestoreParts(true, new[] { 2 }).BeforeReason(Restored));

        var slotsOnly = new RestoreJournal(Restored, Before, Array.Empty<string>(), new[] { 1, 3 });
        Assert.Equal("before restoring co-op slots 1 and 3 from " + Restored, RecoveryKeep.Reason(slotsOnly, RecoveryAction.Complete));
        Assert.Equal("before restoring co-op slots 1 and 3 from " + Before, RecoveryKeep.Reason(slotsOnly, RecoveryAction.RollBack));
        foreach (RecoveryAction action in new[] { RecoveryAction.Complete, RecoveryAction.RollBack })
        {
            Assert.Equal(BackupReasonKind.Restore, BackupRows.ReasonOf(Kept, RecoveryKeep.Reason(Journal(), action)));
            Assert.Equal(BackupReasonKind.Restore, BackupRows.ReasonOf(Kept, RecoveryKeep.Reason(slotsOnly, action)));
        }
    }

    [Fact]
    public void Retention_keeps_an_unfinished_restores_two_backups()
    {
        var clock = new BackupClock(TimeZoneInfo.Utc);

        // An older evening's two backups (the restored one is the second of its day), then a busy day of twelve, hourly from 10:00;
        // the restore's "before" backup at 11:30, the first one past the newest ten.
        var names = new List<string> { "20261108T190000Z-host", Restored };
        names.AddRange(Enumerable.Range(0, 12).Select(i => $"20261110T{10 + i:00}0000Z-host"));
        names.Insert(5, Before);
        string?[] always = { "20261108T190000Z-host", "20261110T210000Z-host", null };

        // Without a journal both go, as before: the selection is the one Prune made.
        IReadOnlyList<string> plain = BackupRetention.Expired(names, 10, 7, always, null, clock);
        var oldKeep = new List<string?>(always);
        oldKeep.AddRange(SaveBackupNames.FirstOfEachDay(names, 7, clock));
        Assert.Equal(SaveBackupNames.Expired(names, 10, oldKeep, clock), plain);
        Assert.Contains(Restored, plain);
        Assert.Contains(Before, plain);

        // While the journal is on disk, neither goes; nothing else changes.
        IReadOnlyList<string> withJournal = BackupRetention.Expired(names, 10, 7, always, Journal(), clock);
        Assert.DoesNotContain(Restored, withJournal);
        Assert.DoesNotContain(Before, withJournal);
        Assert.Equal(plain.Where(n => n != Restored && n != Before), withJournal);
    }
}
