using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class SaveBackupFilesTests
{
    private static SaveFileHash File(string name, string text) => SaveFileHash.Of(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void A_hash_is_the_files_sha256_as_get_filehash_prints_it()
    {
        // SHA-256 of "abc" (FIPS 180-2 test vector).
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", SaveFileHash.HashOf(Encoding.ASCII.GetBytes("abc")));
        SaveFileHash file = File("profileV2.es3", "abc");
        Assert.Equal(3, file.Bytes);
    }

    [Fact]
    public void A_manifest_round_trips()
    {
        var taken = new DateTime(2026, 10, 30, 19, 15, 2, DateTimeKind.Utc);
        var manifest = new SaveBackupManifest("friend", "before joining Alex's session", taken, new[] { File("statsV2.es3", "s"), File("profileV2.es3", "p") });

        Assert.True(SaveBackupManifest.TryParse(manifest.ToJson(), out SaveBackupManifest? read));
        Assert.Equal("friend", read!.Role);
        Assert.Equal("before joining Alex's session", read.Reason);
        Assert.Equal(taken, read.TakenAtUtc);
        Assert.Equal(DateTimeKind.Utc, read.TakenAtUtc.Kind);
        Assert.Equal(new[] { "profileV2.es3", "statsV2.es3" }, read.Files.Select(f => f.Name));
        Assert.Equal(manifest.Files, read.Files);
        Assert.Equal(2, read.TotalBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[]}")]
    [InlineData("{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"files\":[]}")]
    [InlineData("{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[{\"name\":\"..\\\\x.es3\",\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]}")]
    [InlineData("{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[{\"name\":\"a.es3\",\"bytes\":-1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]}")]
    [InlineData("{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[{\"name\":\"a.es3\",\"bytes\":1,\"sha256\":\"xyz\"}]}")]
    [InlineData("{\"version\":1,\"role\":\"host\",\"reason\":\"r\\uD800\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[]}")] // parses, but can't be read
    public void A_damaged_or_foreign_manifest_is_refused_without_throwing(string json)
    {
        Assert.False(SaveBackupManifest.TryParse(json, out SaveBackupManifest? manifest));
        Assert.Null(manifest);
    }

    [Fact]
    public void The_same_file_twice_is_refused()
    {
        string json = new SaveBackupManifest("host", "r", DateTime.UtcNow, new[] { File("a.es3", "1") }).ToJson();
        string twice = json.Replace("\"files\": [", "\"files\": [{\"name\":\"A.ES3\",\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"},", StringComparison.Ordinal);
        Assert.NotEqual(json, twice);
        Assert.False(SaveBackupManifest.TryParse(twice, out _));
    }

    [Fact]
    public void Compare_names_every_file_once_with_what_happened_to_it()
    {
        var before = new[] { File("profileV2.es3", "p1"), File("RunDataV2.es3", "run"), File("statsV2.es3", "s"), File("old.es3", "o") };
        var after = new[] { File("PROFILEV2.es3", "p2-longer"), File("RunDataV2.es3", "run"), File("statsV2.es3", "s"), File("RunHistory_00.es3", "h") };

        IReadOnlyList<SaveFileDifference> differences = SaveBackupManifest.Compare(before, after);

        Assert.Equal(5, differences.Count);
        Assert.Equal(new[] { "old.es3", "PROFILEV2.es3", "RunDataV2.es3", "RunHistory_00.es3", "statsV2.es3" }, differences.Select(d => d.Name));
        Assert.Equal(SaveFileChange.Removed, differences[0].Change);
        Assert.Equal((1L, 0L), (differences[0].BytesBefore, differences[0].BytesAfter));
        Assert.Equal(SaveFileChange.Changed, differences[1].Change);
        Assert.Equal((2L, 9L), (differences[1].BytesBefore, differences[1].BytesAfter));
        Assert.Equal(SaveFileChange.Unchanged, differences[2].Change);
        Assert.Equal(SaveFileChange.Added, differences[3].Change);
        Assert.Equal(SaveFileChange.Unchanged, differences[4].Change);
    }

    [Fact]
    public void A_backup_name_is_its_utc_time_and_role_and_never_repeats()
    {
        var at = new DateTime(2026, 10, 30, 19, 15, 2, DateTimeKind.Utc);
        Assert.Equal("20261030T191502Z-host", SaveBackupNames.Make(at, "host", Array.Empty<string>()));
        Assert.Equal("20261030T191502Z-host-2", SaveBackupNames.Make(at, "host", new[] { "20261030T191502Z-HOST" }));
        Assert.Equal("20261030T191502Z-host-3", SaveBackupNames.Make(at, "host", new[] { "20261030T191502Z-host", "20261030T191502Z-host-2" }));
        Assert.Throws<ArgumentException>(() => SaveBackupNames.Make(at, "../x", Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => SaveBackupNames.Make(at, "Host", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("20261030-191502-host", "host")]
    [InlineData("20261030-191502-friend-2", "friend")]
    [InlineData("20261030-191502-restore", "restore")]
    public void An_old_local_time_backup_name_reads_back(string name, string role)
    {
        Assert.True(SaveBackupNames.TryParse(name, new BackupClock(TimeZoneInfo.Utc), out DateTime at, out string read));
        Assert.Equal(new DateTime(2026, 10, 30, 19, 15, 2), at);
        Assert.Equal(role, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("logs")]
    [InlineData("20261030-191502")]
    [InlineData("20261030-191502-")]
    [InlineData("20261399-191502-host")]
    [InlineData("20261030-191502-Host")]
    [InlineData("20261030-191502-host.partial")]
    public void Other_folder_names_arent_backups(string name)
    {
        Assert.False(SaveBackupNames.TryParse(name, out _, out _));
    }

    [Fact]
    public void Only_backups_beyond_the_newest_few_expire_and_other_folders_never_do()
    {
        string[] names =
        {
            "20261030-191502-host", "20261029-080000-friend", "20261030-191502-host-2", "notes", "20261031-000000-manual",
            "20261028-120000-host", "20261030-191502-host.partial",
        };

        Assert.Equal(new[] { "20261029-080000-friend", "20261028-120000-host" }, SaveBackupNames.Expired(names, 3));
        Assert.Empty(SaveBackupNames.Expired(names, 10));
        Assert.Equal(5, SaveBackupNames.Expired(names, 0).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => SaveBackupNames.Expired(names, -1));
    }

    [Fact]
    public void The_first_backup_of_each_of_the_newest_days_is_found_whatever_else_is_there()
    {
        string[] names =
        {
            "20261101-230000-host", "20261101-190000-host", "20261101-190000-host-2", "20261031-120000-friend", "20261031-080000-manual",
            "20261030-120000-host", "20261030-120000-host.partial", "notes",
        };

        Assert.Equal(new[] { "20261101-190000-host", "20261031-080000-manual" }, SaveBackupNames.FirstOfEachDay(names, 2));
        Assert.Equal(new[] { "20261101-190000-host", "20261031-080000-manual", "20261030-120000-host" }, SaveBackupNames.FirstOfEachDay(names, 7));
        Assert.Empty(SaveBackupNames.FirstOfEachDay(names, 0));
        Assert.Empty(SaveBackupNames.FirstOfEachDay(new[] { "notes" }, 7));
    }

    [Fact]
    public void A_night_of_games_doesnt_push_out_what_came_before_it()
    {
        // A backup per Start: twelve games tonight, with the newest ten kept, would leave nothing from before tonight's first.
        var names = new List<string> { "20261030-200000-host", "20261030-210000-host" };
        for (int game = 0; game < 12; game++)
        {
            names.Add($"20261101-20{game:00}00-host");
        }

        IReadOnlyList<string> expired = SaveBackupNames.Expired(names, 10, SaveBackupNames.FirstOfEachDay(names, 7));
        Assert.DoesNotContain("20261030-200000-host", expired); // the first of the day before
        Assert.DoesNotContain("20261101-200000-host", expired); // the first of tonight
        Assert.Equal(new[] { "20261101-200100-host", "20261030-210000-host" }, expired);
    }

    [Fact]
    public void Keeping_the_oldest_spares_the_first_backup_however_many_follow()
    {
        string[] names =
        {
            "20261030-191502-host", "20261029-080000-friend", "20261030-191502-host-2", "notes", "20261031-000000-manual",
            "20261028-120000-host", "20261028-120000-host-2",
        };

        Assert.Equal(new[] { "20261029-080000-friend", "20261028-120000-host-2" }, SaveBackupNames.Expired(names, 3, keepOldest: true));
        Assert.Empty(SaveBackupNames.Expired(names, 5, keepOldest: true));
        Assert.Equal(new[] { "20261028-120000-host" }, SaveBackupNames.Expired(names, 5));
        Assert.DoesNotContain("20261028-120000-host", SaveBackupNames.Expired(names, 0, keepOldest: true));
        Assert.Equal(5, SaveBackupNames.Expired(names, 0, keepOldest: true).Count);
        Assert.Empty(SaveBackupNames.Expired(new[] { "notes" }, 0, keepOldest: true));
        Assert.Equal(
            new[] { "20261031-000000-manual", "20261030-191502-host-2", "20261030-191502-host", "20261029-080000-friend", "20261028-120000-host-2", "20261028-120000-host" },
            SaveBackupNames.NewestFirst(names));
    }

    [Fact]
    public void The_same_saves_again_are_the_same_files_and_any_change_is_not()
    {
        var manifest = new SaveBackupManifest("host", "r", DateTime.UtcNow, new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s") });

        Assert.True(manifest.HoldsSameFiles(new[] { File("statsV2.es3", "s"), File("PROFILEV2.ES3", "p") }));
        Assert.False(manifest.HoldsSameFiles(new[] { File("profileV2.es3", "p"), File("statsV2.es3", "t") }));
        Assert.False(manifest.HoldsSameFiles(new[] { File("profileV2.es3", "p") }));
        Assert.False(manifest.HoldsSameFiles(new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s"), File("RunDataV2.es3", "r") }));
        Assert.True(new SaveBackupManifest("host", "r", DateTime.UtcNow, Array.Empty<SaveFileHash>()).HoldsSameFiles(Array.Empty<SaveFileHash>()));
    }

    [Theory]
    [InlineData("profileV2.es3", true)]
    [InlineData("RunHistory_03.ES3", true)]
    [InlineData("profileV2.es3.bac", false)]
    [InlineData("RunDataV2.es3.tmp", false)]
    [InlineData(".es3", false)]
    [InlineData("Player.log", false)]
    public void Save_files_are_the_es3_files(string name, bool save)
    {
        Assert.Equal(save, SaveBackupNames.IsSaveFile(name));
    }

    [Theory]
    [InlineData("profileV2.es3", true)]
    [InlineData("RunDataV2.es3", true)]
    [InlineData("lifetime-stats.es3", true)]
    [InlineData("config.es3", false)]
    [InlineData("CONFIG.ES3", false)]
    [InlineData("profileV2_before_revert.es3", false)]
    [InlineData("profileV2.es3.bac", false)]
    public void A_restore_pushes_to_steam_cloud_only_what_the_game_keeps_there(string name, bool cloud)
    {
        Assert.Equal(cloud, SaveBackupNames.IsCloudSaved(name));
    }

    [Theory]
    [InlineData("profileV2.es3", true)]
    [InlineData("..", false)]
    [InlineData("a/b.es3", false)]
    [InlineData("a\\b.es3", false)]
    [InlineData("C:x.es3", false)]
    [InlineData(" ", false)]
    public void Manifest_file_names_are_bare_names(string name, bool plain)
    {
        Assert.Equal(plain, SaveBackupManifest.IsPlainFileName(name));
    }

    [Fact]
    public void A_backup_named_by_a_wrong_clock_is_kept_when_it_was_just_taken()
    {
        // A PC whose clock reads 2020 takes a backup; by the name's time it's the oldest, or one of the expired.
        string[] names =
        {
            "20261031-000000-manual", "20261030-191502-host", "20261029-080000-friend", "20261028-120000-host", "20200101-000000-host",
        };

        Assert.Contains("20200101-000000-host", SaveBackupNames.Expired(names, 2));
        IReadOnlyList<string> expired = SaveBackupNames.Expired(names, 2, new[] { "20261028-120000-host", "20200101-000000-host", null });
        Assert.Equal(new[] { "20261029-080000-friend" }, expired);
    }

    [Fact]
    public void The_first_backup_is_kept_by_its_mark_not_by_the_time_in_its_name()
    {
        string[] names = { "20261031-000000-manual", "20261030-191502-host", "20261029-080000-friend", "20200101-000000-host" };

        // The first backup ever taken was 20261029-080000-friend; a later one named by a wrong clock sorts below it.
        IReadOnlyList<string> expired = SaveBackupNames.Expired(names, 1, new[] { "20261029-080000-FRIEND" });
        Assert.Equal(new[] { "20261030-191502-host", "20200101-000000-host" }, expired);
        Assert.Equal(SaveBackupNames.Expired(names, 1), SaveBackupNames.Expired(names, 1, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => SaveBackupNames.Expired(names, 1, (IEnumerable<string?>)null!));
    }

    [Fact]
    public void The_oldest_backup_is_by_the_time_in_its_name()
    {
        Assert.Equal("20261028-120000-host", SaveBackupNames.Oldest(new[] { "notes", "20261030-191502-host", "20261028-120000-host", "20261029-080000-friend" }));
        Assert.Null(SaveBackupNames.Oldest(new[] { "notes", "20261030-191502-host.partial" }));
    }

    [Theory]
    [InlineData("profileV2.es3", true)]
    [InlineData("statsV2.es3", true)]
    [InlineData("RunDataV2.es3", true)]
    [InlineData("GameplaySettings.es3", true)]
    [InlineData("config.es3", false)]
    [InlineData("CONFIG.ES3", false)]
    [InlineData("profileV2.es3.bac", false)]
    public void Every_save_but_the_local_settings_goes_to_steam_cloud(string name, bool cloud)
    {
        // SystemSettings loads and stores config.es3 with isCloudSave false (Oct 29 build).
        Assert.Equal(cloud, SaveBackupNames.IsCloudSaved(name));
    }

    [Fact]
    public void A_restore_journal_round_trips()
    {
        var journal = new RestoreJournal("20261030-191502-host", "20261031-120000-restore", new[] { "profileV2.es3", "statsV2.es3" });

        Assert.True(RestoreJournal.TryParse(journal.ToText(), out RestoreJournal? read));
        Assert.Equal("20261030-191502-host", read!.Backup);
        Assert.Equal("20261031-120000-restore", read.Before);
        Assert.Equal(new[] { "profileV2.es3", "statsV2.es3" }, read.Files);
        Assert.True(RestoreJournal.TryParse(journal.ToText().Replace("\n", "\r\n", StringComparison.Ordinal), out _));
        Assert.True(RestoreJournal.TryParse("backup=20261030-191502-host\nbefore=20261031-120000-restore\n", out RestoreJournal? none));
        Assert.Empty(none!.Files);
    }

    [Fact]
    public void A_manifest_with_co_op_slots_round_trips_and_keeps_them_apart_from_the_files()
    {
        // The co-op slots go into every backup, in its coop folder, with their hashes.
        var taken = new DateTime(2026, 10, 31, 6, 37, 35, DateTimeKind.Utc);
        var manifest = new SaveBackupManifest("host", "before hosting", taken, new[] { File("profileV2.es3", "p") },
            new[] { CoopSlotHash.Of(3, Encoding.UTF8.GetBytes("three")), CoopSlotHash.Of(1, Encoding.UTF8.GetBytes("one")) });

        Assert.True(SaveBackupManifest.TryParse(manifest.ToJson(), out SaveBackupManifest? read));
        Assert.Equal(new[] { 1, 3 }, read!.CoopSlots.Select(s => s.Slot));
        Assert.Equal(manifest.CoopSlots, read.CoopSlots);
        Assert.Equal("coop-3.es3", read.CoopSlot(3)!.FileName);
        Assert.Null(read.CoopSlot(2));
        Assert.Equal(new[] { "profileV2.es3" }, read.Files.Select(f => f.Name));
        Assert.Equal(1 + 5 + 3, read.TotalBytes);
        Assert.Equal("coop", SaveBackupManifest.CoopFolder);
    }

    [Fact]
    public void A_manifest_without_slots_is_the_format_0_6_wrote_and_an_old_one_reads_with_none()
    {
        var manifest = new SaveBackupManifest("friend", "r", new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), new[] { File("a.es3", "1") });
        Assert.DoesNotContain("coopSlots", manifest.ToJson(), StringComparison.Ordinal);
        Assert.Equal(new SaveBackupManifest("friend", "r", manifest.TakenAtUtc, manifest.Files, Array.Empty<CoopSlotHash>()).ToJson(), manifest.ToJson());

        // A manifest 0.6.3 wrote (the shape of that version's session backups).
        const string old = "{\"version\":1,\"role\":\"host\",\"reason\":\"before hosting\",\"takenAtUtc\":\"2026-10-31T12:00:00.0000000Z\",\"files\":[" +
            "{\"name\":\"config.es3\",\"bytes\":1234,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]}";
        Assert.True(SaveBackupManifest.TryParse(old, out SaveBackupManifest? read));
        Assert.Empty(read!.CoopSlots);
        Assert.Single(read.Files);
    }

    [Theory]
    [InlineData("\"coopSlots\":{}")]
    [InlineData("\"coopSlots\":[{\"slot\":0,\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]")]
    [InlineData("\"coopSlots\":[{\"slot\":10,\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]")]
    [InlineData("\"coopSlots\":[{\"slot\":\"1\",\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]")]
    [InlineData("\"coopSlots\":[{\"slot\":1,\"bytes\":-1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]")]
    [InlineData("\"coopSlots\":[{\"slot\":1,\"bytes\":1,\"sha256\":\"xyz\"}]")]
    [InlineData("\"coopSlots\":[{\"slot\":1,\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}," +
        "{\"slot\":1,\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]")]
    [InlineData("\"coopSlots\":[1,2]")]
    public void Bad_co_op_slots_make_a_manifest_damaged(string slots)
    {
        string json = "{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[]," + slots + "}";
        Assert.False(SaveBackupManifest.TryParse(json, out SaveBackupManifest? manifest));
        Assert.Null(manifest);
    }

    [Fact]
    public void Too_many_co_op_slots_are_refused_and_a_manifest_cant_be_made_with_bad_ones()
    {
        string entries = string.Join(",", Enumerable.Range(1, 10).Select(i =>
            $"{{\"slot\":{i},\"bytes\":1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}}"));
        Assert.False(SaveBackupManifest.TryParse("{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[],\"coopSlots\":[" + entries + "]}", out _));
        Assert.Throws<ArgumentException>(() => new SaveBackupManifest("host", "r", DateTime.UtcNow, Array.Empty<SaveFileHash>(), new[] { new CoopSlotHash(0, 1, "A") }));
        Assert.Throws<ArgumentException>(() => new SaveBackupManifest("host", "r", DateTime.UtcNow, Array.Empty<SaveFileHash>(),
            new[] { new CoopSlotHash(1, 1, "A"), new CoopSlotHash(1, 2, "B") }));
    }

    [Fact]
    public void The_same_saves_with_a_changed_co_op_slot_are_a_new_backup()
    {
        CoopSlotHash slot1 = CoopSlotHash.Of(1, Encoding.UTF8.GetBytes("run"));
        var manifest = new SaveBackupManifest("host", "r", DateTime.UtcNow, new[] { File("profileV2.es3", "p") }, new[] { slot1 });

        Assert.True(manifest.HoldsSame(new[] { File("profileV2.es3", "p") }, new[] { slot1 }));
        Assert.True(manifest.HoldsSameFiles(new[] { File("profileV2.es3", "p") }));
        Assert.False(manifest.HoldsSame(new[] { File("profileV2.es3", "p") }, new[] { CoopSlotHash.Of(1, Encoding.UTF8.GetBytes("run, a floor on")) }));
        Assert.False(manifest.HoldsSame(new[] { File("profileV2.es3", "p") }, Array.Empty<CoopSlotHash>()));
        Assert.False(manifest.HoldsSame(new[] { File("profileV2.es3", "p") }, new[] { slot1, CoopSlotHash.Of(2, Encoding.UTF8.GetBytes("run")) }));
        Assert.False(manifest.HoldsSame(new[] { File("profileV2.es3", "q") }, new[] { slot1 }));
    }

    [Fact]
    public void Saves_changed_only_in_one_file_are_told_apart_from_any_other_change()
    {
        CoopSlotHash slot1 = CoopSlotHash.Of(1, Encoding.UTF8.GetBytes("run"));
        SaveFileHash counters = File("run-scopes.txt", "c");
        var manifest = new SaveBackupManifest("host", "r", DateTime.UtcNow, new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s") }, new[] { slot1 }, counters);
        SaveFileHash[] profileChanged = { File("PROFILEV2.ES3", "p2"), File("statsV2.es3", "s") };

        Assert.True(manifest.HoldsSameApartFrom(profileChanged, new[] { slot1 }, counters, ProfilePlayTime.ProfileFile));

        // Nothing changed: that's HoldsSame's case, not this one.
        Assert.False(manifest.HoldsSameApartFrom(new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s") }, new[] { slot1 }, counters, ProfilePlayTime.ProfileFile));

        // Any change beside it, or the file itself added or gone.
        Assert.False(manifest.HoldsSameApartFrom(new[] { File("profileV2.es3", "p2"), File("statsV2.es3", "s2") }, new[] { slot1 }, counters, ProfilePlayTime.ProfileFile));
        Assert.False(manifest.HoldsSameApartFrom(new[] { File("profileV2.es3", "p2"), File("statsV2.es3", "s"), File("RunDataV2.es3", "r") }, new[] { slot1 }, counters, ProfilePlayTime.ProfileFile));
        Assert.False(manifest.HoldsSameApartFrom(new[] { File("statsV2.es3", "s") }, new[] { slot1 }, counters, ProfilePlayTime.ProfileFile));
        Assert.False(manifest.HoldsSameApartFrom(profileChanged, new[] { CoopSlotHash.Of(1, Encoding.UTF8.GetBytes("run, a floor on")) }, counters, ProfilePlayTime.ProfileFile));
        Assert.False(manifest.HoldsSameApartFrom(profileChanged, Array.Empty<CoopSlotHash>(), counters, ProfilePlayTime.ProfileFile));
        Assert.False(manifest.HoldsSameApartFrom(profileChanged, new[] { slot1 }, File("run-scopes.txt", "c2"), ProfilePlayTime.ProfileFile));
        Assert.False(manifest.HoldsSameApartFrom(profileChanged, new[] { slot1 }, null, ProfilePlayTime.ProfileFile));

        // Only the file named may differ.
        Assert.False(manifest.HoldsSameApartFrom(profileChanged, new[] { slot1 }, counters, "statsV2.es3"));
        Assert.Throws<ArgumentException>(() => manifest.HoldsSameApartFrom(profileChanged, new[] { slot1 }, counters, ""));
    }

    [Fact]
    public void A_restores_backup_of_what_it_replaces_stands_in_only_for_the_same_bytes()
    {
        CoopSlotHash slot1 = CoopSlotHash.Of(1, Encoding.UTF8.GetBytes("run"));
        SaveFileHash counters = File("run-scopes.txt", "c");
        var newest = new SaveBackupManifest("host", "before hosting", DateTime.UtcNow, new[] { File("profileV2.es3", "p"), File("statsV2.es3", "s") }, new[] { slot1 }, counters);
        SaveBackupManifest Now(string profile, string stats, string role = "host") =>
            new(role, role == "restore" ? "before restoring 20261101T120000Z-host" : "before hosting", DateTime.UtcNow,
                new[] { File("profileV2.es3", profile), File("statsV2.es3", stats) }, new[] { slot1 }, counters);

        // A session or the panel: the newest backup stands in when only the profile changed (its play-time counter, read next).
        Assert.Equal(BackupStandIn.Same, newest.StandsFor(Now("p", "s"), exact: false));
        Assert.Equal(BackupStandIn.IfPlayTimeOnly, newest.StandsFor(Now("p2", "s"), exact: false));
        Assert.Equal(BackupStandIn.None, newest.StandsFor(Now("p2", "s2"), exact: false));
        Assert.Equal(BackupStandIn.None, newest.StandsFor(Now("p", "s2"), exact: false));

        // A restore's backup of what it replaces: its hashes stand for the replaced files (Steam Cloud's check after a
        // restore finished at start-up), so only the very same bytes will do.
        Assert.Equal(BackupStandIn.Same, newest.StandsFor(Now("p", "s", "restore"), exact: true));
        Assert.Equal(BackupStandIn.None, newest.StandsFor(Now("p2", "s", "restore"), exact: true));
        Assert.Throws<ArgumentNullException>(() => newest.StandsFor(null!, exact: false));
    }

    [Fact]
    public void A_restore_journal_with_co_op_slots_and_its_moved_mark_round_trips()
    {
        var journal = new RestoreJournal("20261030-191502-host", "20261031-120000-restore", new[] { "profileV2.es3" }, new[] { 3, 1, 3 });
        Assert.False(journal.Moved);
        Assert.Equal(new[] { 1, 3 }, journal.Slots);

        Assert.True(RestoreJournal.TryParse(journal.ToText(), out RestoreJournal? read));
        Assert.Equal(new[] { 1, 3 }, read!.Slots);
        Assert.False(read.Moved);
        Assert.DoesNotContain("moved", journal.ToText(), StringComparison.Ordinal);

        Assert.True(RestoreJournal.TryParse(journal.AllMoved().ToText(), out RestoreJournal? moved));
        Assert.True(moved!.Moved);
        Assert.Equal(new[] { "profileV2.es3" }, moved.Files);
        Assert.Equal(new[] { 1, 3 }, moved.Slots);

        // A slots-only restore names no file.
        Assert.True(RestoreJournal.TryParse("backup=20261030-191502-host\nbefore=20261031-120000-restore\nslot=2\n", out RestoreJournal? slotOnly));
        Assert.Empty(slotOnly!.Files);
        Assert.Equal(new[] { 2 }, slotOnly.Slots);
    }

    [Theory]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nslot=0\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nslot=10\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nslot=-1\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nslot=x\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nslot=2\nslot=2\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nmoved=2\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nmoved=1\nmoved=1\n")]
    public void A_journal_with_bad_slots_or_marks_is_refused(string text)
    {
        Assert.False(RestoreJournal.TryParse(text, out RestoreJournal? journal));
        Assert.Null(journal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("backup=20261030-191502-host\n")]
    [InlineData("before=20261031-120000-restore\n")]
    [InlineData("backup=logs\nbefore=20261031-120000-restore\n")]
    [InlineData("backup=20261030-191502-host\nbefore=..\\x\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nfile=..\\profileV2.es3\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nfile=Player.log\n")]
    [InlineData("backup=20261030-191502-host\nbackup=20261030-191502-host\nbefore=20261031-120000-restore\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\nwhat=ever\n")]
    [InlineData("backup=20261030-191502-host\nbefore=20261031-120000-restore\njunk\n")]
    public void A_damaged_restore_journal_is_refused_without_throwing(string? text)
    {
        Assert.False(RestoreJournal.TryParse(text, out RestoreJournal? journal));
        Assert.Null(journal);
    }
}
