using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A restored file still waiting for Steam Cloud goes there only while Steam's copy is the
/// one the restore replaced; when another device saved since, the whole restore yields. The list keeps what that needs.
/// </summary>
public class CloudPushDecisionTests
{
    private const string Backup = "20261030-191502-host";
    private const string Other = "20261031-090000-manual";
    private static readonly DateTime RestoredAt = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static string Sha(string text) => SaveFileHash.HashOf(Encoding.UTF8.GetBytes(text));

    private static SaveFileHash File(string name, string text) => SaveFileHash.Of(name, Encoding.UTF8.GetBytes(text));

    private static CloudPushEntry Entry(string name = "profileV2.es3", string? before = "old", SteamCopy steam = default, string backup = Backup, DateTime? restoredAt = null) =>
        new(File(name, "restored " + name), backup, restoredAt ?? RestoredAt, before == null ? null : before.Length == 0 ? CloudPushEntry.NoFileBefore : Sha(before), steam);

    private static CloudPushVerdict Verdict(CloudPushEntry entry, SteamCopy now) => CloudPushDecision.Decide(entry, now).Verdict;

    [Fact]
    public void Steam_not_readable_keeps_todays_push_and_a_copy_that_cant_be_read_is_never_overwritten()
    {
        Assert.Equal(CloudPushVerdict.Push, Verdict(Entry(), SteamCopy.Unavailable));
        Assert.Equal(CloudPushVerdict.KeepLocal, Verdict(Entry(), SteamCopy.Unreadable));
        Assert.Equal(CloudPushVerdict.Push, Verdict(Entry(), SteamCopy.NoCopy));
    }

    [Fact]
    public void Steam_holding_the_restored_bytes_takes_the_file_off_the_list()
    {
        CloudPushEntry entry = Entry();
        Assert.Equal(CloudPushVerdict.AlreadyThere, Verdict(entry, SteamCopy.Of(entry.File.Sha256.ToLowerInvariant(), RestoredAt.AddDays(1))));
    }

    [Fact]
    public void Steam_still_holding_what_the_restore_replaced_gets_the_restored_file()
    {
        // By the hash from before the restore, whatever Steam's clock says (two PCs' clocks may differ).
        Assert.Equal(CloudPushVerdict.Push, Verdict(Entry(before: "old"), SteamCopy.Of(Sha("old"), RestoredAt.AddHours(5))));

        // By Steam's own copy at the restore (a push that failed then).
        SteamCopy then = SteamCopy.Of(Sha("steam then"), RestoredAt.AddMinutes(-30));
        Assert.Equal(CloudPushVerdict.Push, Verdict(Entry(before: "local", steam: then), SteamCopy.Of(Sha("steam then"), RestoredAt.AddMinutes(-30))));

        // A different copy no newer than Steam's at the restore.
        Assert.Equal(CloudPushVerdict.Push, Verdict(Entry(before: "local", steam: then), SteamCopy.Of(Sha("an older one"), RestoredAt.AddHours(-2))));
    }

    [Fact]
    public void Another_devices_save_after_the_restore_makes_it_yield()
    {
        SteamCopy then = SteamCopy.Of(Sha("steam then"), RestoredAt.AddMinutes(-30));
        CloudPushChoice choice = CloudPushDecision.Decide(Entry(steam: then), SteamCopy.Of(Sha("laptop"), RestoredAt.AddHours(2)));
        Assert.Equal(CloudPushVerdict.Yield, choice.Verdict);
        Assert.Contains("2026-10-31 14:00:00 UTC", choice.Why, StringComparison.Ordinal);
        Assert.Contains("another device", choice.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_steams_copy_at_the_restore_the_restores_time_decides_with_a_margin()
    {
        // Steam wasn't running at the restore: only a copy clearly older than the restore is the one it replaced.
        CloudPushEntry entry = Entry(before: "local");
        Assert.Equal(CloudPushVerdict.Push, Verdict(entry, SteamCopy.Of(Sha("x"), RestoredAt - CloudPushDecision.ClockMargin - TimeSpan.FromSeconds(1))));
        Assert.Equal(CloudPushVerdict.Yield, Verdict(entry, SteamCopy.Of(Sha("x"), RestoredAt.AddMinutes(-4))));
        Assert.Equal(CloudPushVerdict.Yield, Verdict(entry, SteamCopy.Of(Sha("x"), RestoredAt.AddMinutes(1))));
        Assert.Equal(CloudPushVerdict.Yield, Verdict(entry, SteamCopy.Of(Sha("x"), null))); // its time not known

        // Steam had no copy at the restore: one there now is newer unless clearly older than the restore.
        Assert.Equal(CloudPushVerdict.Yield, Verdict(Entry(before: "", steam: SteamCopy.NoCopy), SteamCopy.Of(Sha("x"), RestoredAt.AddMinutes(10))));
    }

    [Fact]
    public void A_restore_finished_at_start_up_needs_the_exact_bytes_it_replaced()
    {
        // A restore the game stopped in the middle of is finished at the next start with what its "before" backup holds:
        // its hash as the replaced file's, its time as the restore's, Steam's copy at the restore not known. Steam Cloud
        // holds the profile the restore replaced, saved after that backup was taken.
        DateTime backupTaken = RestoredAt.AddMinutes(-20);
        SteamCopy replaced = SteamCopy.Of(Sha("profile, counter at 5404.7"), RestoredAt.AddMinutes(-2));

        // That backup held the replaced bytes: the restored profile goes to Steam Cloud.
        Assert.Equal(CloudPushVerdict.Push, Verdict(Entry(before: "profile, counter at 5404.7", restoredAt: backupTaken), replaced));

        // A backup of the same profile but for the play-time counter would have the whole restore yield to its own saves,
        // as if another device had saved them: so a restore's "before" backup is never one that differs in that counter.
        Assert.Equal(CloudPushVerdict.Yield, Verdict(Entry(before: "profile, counter at 5400.2", restoredAt: backupTaken), replaced));
    }

    [Fact]
    public void A_list_from_an_older_version_with_nothing_known_yields_to_any_other_copy()
    {
        var old = new CloudPushEntry(File("profileV2.es3", "r"), Backup, null, null, SteamCopy.Unavailable);
        Assert.Equal(CloudPushVerdict.Yield, Verdict(old, SteamCopy.Of(Sha("x"), RestoredAt.AddYears(-1))));
        Assert.Equal(CloudPushVerdict.Push, Verdict(old, SteamCopy.NoCopy));
        Assert.Equal(CloudPushVerdict.AlreadyThere, Verdict(old, SteamCopy.Of(old.File.Sha256, null)));
    }

    [Fact]
    public void A_restore_yields_whole_and_another_restores_files_are_decided_on_their_own()
    {
        CloudPushEntry profile = Entry("profileV2.es3", before: "p0");
        CloudPushEntry stats = Entry("statsV2.es3", before: "s0");
        CloudPushEntry history = Entry("RunHistoryIndex.es3", before: "h0", backup: Other);
        var steam = new Dictionary<string, SteamCopy>
        {
            ["profileV2.es3"] = SteamCopy.Of(Sha("p0"), RestoredAt.AddHours(-1)), // still the one replaced
            ["statsV2.es3"] = SteamCopy.Of(Sha("laptop stats"), RestoredAt.AddHours(3)), // another device saved
            ["RunHistoryIndex.es3"] = SteamCopy.Of(Sha("h0"), RestoredAt.AddHours(-1)),
        };
        var asked = new List<string>();

        IReadOnlyDictionary<string, CloudPushChoice> choices = CloudPushDecision.DecideAll(new[] { profile, stats, history }, name =>
        {
            asked.Add(name);
            return steam[name];
        });

        Assert.Equal(CloudPushVerdict.Yield, choices["statsV2.es3"].Verdict);
        Assert.Equal(CloudPushVerdict.Yield, choices["PROFILEV2.ES3"].Verdict);
        Assert.Contains("statsV2.es3", choices["profileV2.es3"].Why, StringComparison.Ordinal);
        Assert.Contains($"backups/{SaveBackupNames.ForLog(Backup)}", choices["profileV2.es3"].Why, StringComparison.Ordinal); // logged: no local-time name
        Assert.DoesNotContain(Backup, choices["profileV2.es3"].Why, StringComparison.Ordinal);
        Assert.Equal(CloudPushVerdict.Push, choices["RunHistoryIndex.es3"].Verdict);
        Assert.Equal(3, asked.Count); // each read once
    }

    [Fact]
    public void The_list_keeps_each_files_restore_and_round_trips()
    {
        var entries = new[]
        {
            Entry("profileV2.es3", before: "p0", steam: SteamCopy.Of(Sha("steam"), RestoredAt.AddMinutes(-3))),
            Entry("statsV2.es3", before: "", steam: SteamCopy.NoCopy),
            Entry("RunDataV2.es3", before: null, steam: SteamCopy.Unreadable, backup: Other, restoredAt: RestoredAt.AddDays(-1)),
            Entry("GameplaySettings.es3", before: "g", steam: SteamCopy.Unavailable),
        };
        var list = new CloudPushList(Backup, entries);

        Assert.True(CloudPushList.TryParse(list.ToText(), out CloudPushList? read));
        Assert.Equal(list.Entries, read!.Entries);
        Assert.Equal(list.Files, read.Files);
        Assert.Equal(Other, read.Entry("rundatav2.es3")!.Backup);
        Assert.Equal(CloudPushEntry.NoFileBefore, read.Entry("statsV2.es3")!.BeforeSha256);
        Assert.Equal(DateTimeKind.Utc, read.Entry("profileV2.es3")!.SteamAtRestore.TimeUtc!.Value.Kind);
        Assert.True(CloudPushList.TryParse(list.ToText().Replace("\n", "\r\n", StringComparison.Ordinal), out _));

        // Then, Without and Map keep what's known.
        CloudPushList rest = list.Without("statsV2.es3").Then(new CloudPushList(Other, new[] { Entry("statsV2.es3", before: "s9", backup: Other) }));
        Assert.Equal(Other, rest.Entry("statsV2.es3")!.Backup);
        Assert.Equal(Sha("p0"), rest.Entry("profileV2.es3")!.BeforeSha256);
        Assert.Null(rest.Map(e => e with { BeforeSha256 = null }).Entry("profileV2.es3")!.BeforeSha256);
    }

    [Fact]
    public void A_list_an_older_version_wrote_still_reads_with_nothing_more_known()
    {
        string text = $"backup={Backup}\nfile=profileV2.es3|1|{Sha("p")}\n";
        Assert.True(CloudPushList.TryParse(text, out CloudPushList? list));
        CloudPushEntry entry = Assert.Single(list!.Entries);
        Assert.Equal(Backup, entry.Backup);
        Assert.Null(entry.RestoredAtUtc);
        Assert.Null(entry.BeforeSha256);
        Assert.Equal(SteamCopyState.Unavailable, entry.SteamAtRestore.State);

        // The backup line may come after the files.
        Assert.True(CloudPushList.TryParse($"file=profileV2.es3|1|{Sha("p")}\nbackup={Backup}\n", out CloudPushList? later));
        Assert.Equal(Backup, later!.Entries[0].Backup);
    }

    [Theory]
    [InlineData("|20261030-191502-host|-|-|-|-|x")] // nine fields
    [InlineData("|logs|-|-|-|-")] // not a backup's name
    [InlineData("|20261030-191502-host|yesterday|-|-|-")] // a bad time
    [InlineData("|20261030-191502-host|2026-10-31T12:00:00.0000000+02:00|-|-|-")] // not UTC
    [InlineData("|20261030-191502-host|-|xyz|-|-")] // a bad hash before
    [InlineData("|20261030-191502-host|-|-|xyz|-")] // a bad Steam hash
    [InlineData("|20261030-191502-host|-|-|none|2026-10-31T12:00:00.0000000Z")] // a time for no copy
    [InlineData("|20261030-191502-host|-|-|-|2026-10-31T12:00:00.0000000Z")] // a time for nothing known
    public void A_damaged_line_of_the_newer_format_is_refused(string tail)
    {
        string text = $"backup={Backup}\nfile=profileV2.es3|1|{Sha("p")}{tail}\n";
        Assert.False(CloudPushList.TryParse(text, out CloudPushList? list));
        Assert.Null(list);
    }

    [Fact]
    public void Parsing_noise_never_throws()
    {
        var random = new Random(4);
        string good = new CloudPushList(Backup, new[] { Entry(steam: SteamCopy.Of(Sha("s"), RestoredAt)) }).ToText();
        for (int i = 0; i < 2000; i++)
        {
            char[] text = good.ToCharArray();
            for (int k = random.Next(1, 6); k > 0; k--)
            {
                text[random.Next(text.Length)] = "|=-:.0aZ\n"[random.Next(9)];
            }

            _ = CloudPushList.TryParse(new string(text), out _);
        }
    }

    [Fact]
    public void Describing_a_copy_reads_for_the_log()
    {
        Assert.Equal("saved 2026-10-31 12:00:00 UTC", SteamCopy.Of(Sha("s"), RestoredAt).Describe());
        Assert.Equal("none", SteamCopy.NoCopy.Describe());
        Assert.Equal("not readable", SteamCopy.Unreadable.Describe());
        Assert.Equal("not known", SteamCopy.Unavailable.Describe());
        Assert.Equal(Sha("s").ToUpperInvariant(), SteamCopy.Of(Sha("s").ToLowerInvariant(), null).Sha256);
    }
}
