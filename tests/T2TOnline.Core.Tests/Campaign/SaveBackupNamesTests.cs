using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>Backup names in UTC beside the old local-time ones (D129): every order by the moment taken, across zones and DST.</summary>
public class SaveBackupNamesTests
{
    private static readonly TimeZoneInfo PlusNine = TimeZoneInfo.CreateCustomTimeZone("Test+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
    private static readonly TimeZoneInfo MinusSeven = TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "UTC-7", "UTC-7");

    /// <summary>UTC-8, and UTC-7 from the second Sunday of March to the first Sunday of November at 02:00 (Mar 8 and Nov 1 in 2026).</summary>
    private static readonly TimeZoneInfo MinusEightWithDst = TimeZoneInfo.CreateCustomTimeZone("Test-8/-7", TimeSpan.FromHours(-8), "UTC-8 with DST", "Standard", "Daylight", new[]
    {
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
    });

    [Fact]
    public void A_new_name_is_utc_and_marked_whatever_the_time_given()
    {
        var utc = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal("20261105T100000Z-host", SaveBackupNames.Make(utc, "host", Array.Empty<string>()));
        Assert.Equal("20261105T100000Z-host", SaveBackupNames.Make(utc.ToLocalTime(), "host", Array.Empty<string>()));
        Assert.Equal("20261105T100000Z-host", SaveBackupNames.Make(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), "host", Array.Empty<string>()));
        Assert.Equal("20261105T100000Z-host-2", SaveBackupNames.Make(utc, "host", new[] { "20261105T100000Z-HOST" }));
        Assert.True(SaveBackupNames.IsUtcName("20261105T100000Z-host-2"));
        Assert.False(SaveBackupNames.IsUtcName("20261105-100000-host"));
        Assert.False(SaveBackupNames.IsUtcName("notes"));
    }

    [Theory]
    [InlineData("20261105T100000Z-host", "host")]
    [InlineData("20261105T100000Z-friend-2", "friend")]
    [InlineData("20261105T100000Z-restore", "restore")]
    public void A_new_name_reads_back_as_utc(string name, string role)
    {
        Assert.True(SaveBackupNames.TryParse(name, new BackupClock(PlusNine), out DateTime at, out string read));
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), at);
        Assert.Equal(DateTimeKind.Utc, at.Kind);
        Assert.Equal(role, read);
    }

    [Theory]
    [InlineData("20261105T100000-host")]
    [InlineData("20261105T100000Z")]
    [InlineData("20261105T100000Z-")]
    [InlineData("20261305T100000Z-host")]
    [InlineData("20261105T100000Z-Host")]
    [InlineData("20261105T100000Z-host.partial")]
    [InlineData("20261105-100000Z-host")]
    public void Near_misses_of_the_new_form_arent_backups(string name)
    {
        Assert.False(SaveBackupNames.TryParse(name, out _, out _));
    }

    [Fact]
    public void An_old_name_is_read_in_the_zone_it_was_taken_in()
    {
        Assert.True(SaveBackupNames.TryParse("20261105-180000-host", new BackupClock(PlusNine), out DateTime plusNine, out _));
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), plusNine);
        Assert.True(SaveBackupNames.TryParse("20261105-200000-host", new BackupClock(MinusSeven), out DateTime minusSeven, out _));
        Assert.Equal(new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc), minusSeven);
    }

    [Fact]
    public void At_utc_plus_nine_the_first_backups_after_the_update_are_the_newest()
    {
        // The last local-time names were taken at 09:00 and 18:00 local (00:00 and 09:00 UTC); then the update, and backups at
        // 18:30 and 21:00 local. Read as written, "093000" would sort before "180000" and the newest would be pushed out.
        string[] names = { "20261105-090000-host", "20261105-180000-host", "20261105T093000Z-host", "20261105T120000Z-friend" };
        var clock = new BackupClock(PlusNine);

        Assert.Equal(new[] { "20261105T120000Z-friend", "20261105T093000Z-host", "20261105-180000-host", "20261105-090000-host" }, SaveBackupNames.NewestFirst(names, clock));
        Assert.Equal(new[] { "20261105-180000-host", "20261105-090000-host" }, SaveBackupNames.Expired(names, 2, clock));
        Assert.Equal("20261105-090000-host", SaveBackupNames.Oldest(names, clock));
        Assert.Equal(new[] { "20261105-090000-host" }, SaveBackupNames.FirstOfEachDay(names, 7, clock));
    }

    [Fact]
    public void At_utc_minus_seven_an_evenings_backups_stay_one_day_across_the_update()
    {
        // 20:00 local before the update; 20:30 and 22:00 local after it, already the next day in UTC.
        string[] names = { "20261105-200000-host", "20261106T033000Z-host", "20261106T050000Z-host" };
        var clock = new BackupClock(MinusSeven);

        IReadOnlyList<string> firsts = SaveBackupNames.FirstOfEachDay(names, 7, clock);
        Assert.Equal(new[] { "20261105-200000-host" }, firsts); // one evening, one day: its first is the one from before it
        Assert.Equal(new[] { "20261106T033000Z-host" }, SaveBackupNames.Expired(names, 1, firsts, clock));
    }

    [Fact]
    public void A_fall_back_night_keeps_its_order_and_its_first()
    {
        // Nov 1, 2026: 01:30 happens twice (read as standard time). Then the update at 03:30 local (11:30 UTC).
        string[] names = { "20261101-013000-host", "20261101T113000Z-host", "20261101-003000-host", "20261101-030000-host", "20261031-230000-host" };
        var clock = new BackupClock(MinusEightWithDst);

        Assert.Equal(new[] { "20261101T113000Z-host", "20261101-030000-host", "20261101-013000-host", "20261101-003000-host", "20261031-230000-host" },
            SaveBackupNames.NewestFirst(names, clock));
        Assert.Equal(new[] { "20261101-003000-host", "20261031-230000-host" }, SaveBackupNames.FirstOfEachDay(names, 7, clock));
        Assert.True(SaveBackupNames.TryParse("20261101-013000-host", clock, out DateTime ambiguous, out _));
        Assert.Equal(new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc), ambiguous);
    }

    [Fact]
    public void A_spring_forward_time_that_never_happened_reads_without_throwing()
    {
        // Mar 8, 2026: 02:00 to 03:00 local doesn't exist.
        string[] names = { "20260308-013000-host", "20260308-023000-host", "20260308-033000-host", "20260308T120000Z-host" };
        var clock = new BackupClock(MinusEightWithDst);

        Assert.True(SaveBackupNames.TryParse("20260308-023000-host", clock, out DateTime gap, out _));
        Assert.Equal(new DateTime(2026, 3, 8, 10, 30, 0, DateTimeKind.Utc), gap); // the standard offset
        Assert.True(SaveBackupNames.TryParse("20260308-033000-host", clock, out DateTime after, out _));
        Assert.Equal(new DateTime(2026, 3, 8, 10, 30, 0, DateTimeKind.Utc), after);
        Assert.Equal(new[] { "20260308T120000Z-host", "20260308-033000-host", "20260308-023000-host", "20260308-013000-host" }, SaveBackupNames.NewestFirst(names, clock));
        Assert.Equal(new[] { "20260308-013000-host" }, SaveBackupNames.FirstOfEachDay(names, 7, clock));
    }

    [Fact]
    public void A_manifest_time_wins_over_the_zone_for_an_old_name()
    {
        // Taken at 12:00 local on a PC at UTC+9 (03:00 UTC); the PC is now at UTC-7, where 12:00 reads 19:00 UTC.
        string[] names = { "20261105-120000-host", "20261105T100000Z-host" };
        var withManifest = new BackupClock(MinusSeven, new[] { new KeyValuePair<string, DateTime>("20261105-120000-HOST", new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc)) });

        Assert.Equal(new[] { "20261105T100000Z-host", "20261105-120000-host" }, SaveBackupNames.NewestFirst(names, withManifest));
        Assert.Equal(new[] { "20261105-120000-host", "20261105T100000Z-host" }, SaveBackupNames.NewestFirst(names, new BackupClock(MinusSeven)));
        Assert.Equal(new[] { "20261105-120000-host" }, SaveBackupNames.Expired(names, 1, withManifest));
    }

    [Fact]
    public void A_manifest_time_never_moves_a_new_name()
    {
        string[] names = { "20261105T100000Z-host", "20261105T090000Z-host" };
        var clock = new BackupClock(TimeZoneInfo.Utc, new[] { new KeyValuePair<string, DateTime>("20261105T100000Z-host", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)) });

        Assert.Equal(new[] { "20261105T100000Z-host", "20261105T090000Z-host" }, SaveBackupNames.NewestFirst(names, clock));
    }

    [Fact]
    public void A_clock_needs_a_zone()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupClock(null!));
        Assert.Same(TimeZoneInfo.Local, BackupClock.Local.Zone);
    }

    [Fact]
    public void A_log_names_a_backup_by_its_utc_time_only()
    {
        // A new name is UTC already; an old local-time one is named by the UTC time it stands for, so no log line holds a
        // local time (D127): 19:15 at UTC-7 is 02:15 UTC the next day.
        var clock = new BackupClock(MinusSeven);
        Assert.Equal("20261105T100000Z-host", SaveBackupNames.ForLog("20261105T100000Z-host", clock));
        Assert.Equal("20261105T100000Z-friend-2", SaveBackupNames.ForLog("20261105T100000Z-friend-2", clock));
        Assert.Equal("20261104T021502Z-host (named in local time on disk)", SaveBackupNames.ForLog("20261103-191502-host", clock));
        Assert.Equal("20261104T021502Z-restore-3 (named in local time on disk)", SaveBackupNames.ForLog("20261103-191502-restore-3", clock));

        // The manifest's time wins for an old name, as it does in every order.
        var known = new BackupClock(PlusNine, new[] { new KeyValuePair<string, DateTime>("20261103-191502-host", new DateTime(2026, 11, 3, 10, 15, 2, DateTimeKind.Utc)) });
        Assert.Equal("20261103T101502Z-host (named in local time on disk)", SaveBackupNames.ForLog("20261103-191502-host", known));
    }

    [Theory]
    [InlineData("before hosting", "before hosting")]
    [InlineData("before following a host", "before following a host")]
    [InlineData("before following Alex", "before following a host")] // 0.10.0's manifests named the host (D129)
    [InlineData("before restoring 20261105T100000Z-host", "before restoring 20261105T100000Z-host")]
    [InlineData("before restoring 20261103-191502-host", "before restoring 20261104T021502Z-host (named in local time on disk)")]
    [InlineData("before restoring your saves from 20261103-191502-friend", "before restoring your saves from 20261104T021502Z-friend (named in local time on disk)")]
    [InlineData(null, "")]
    public void A_log_gives_a_backups_reason_without_a_local_time_or_a_name(string? reason, string expected)
    {
        Assert.Equal(expected, SaveBackupNames.ReasonForLog(reason, new BackupClock(MinusSeven)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notes")]
    [InlineData("Alex's saves")]
    [InlineData("20261105T100000Z-")]
    [InlineData("20261105-100000-Host Name")]
    public void A_log_never_repeats_a_folder_name_the_mod_didnt_make(string? name)
    {
        Assert.Equal("a folder T2T Online didn't name", SaveBackupNames.ForLog(name, new BackupClock(PlusNine)));
    }
}
