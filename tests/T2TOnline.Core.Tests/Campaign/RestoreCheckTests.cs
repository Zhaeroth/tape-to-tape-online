using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A restore's files read back right after the move, and again at the next start, from a small list kept beside the journal.</summary>
public class RestoreCheckTests
{
    private const string Backup = "20261031-120000-host";
    private const string UtcBackup = "20261105T200000Z-restore";
    private static readonly string HashA = new('A', 64);
    private static readonly string HashB = new('b', 64);

    // How the lines name Backup, an older backup's local-time name: as its UTC time, marked (SaveBackupNames.ForLog).
    private static readonly string Logged = SaveBackupNames.ForLog(Backup);

    private static RestoreCheck Check(string backup = Backup) => new(
        backup,
        new[] { new SaveFileHash("profileV2.es3", 10, HashA), new SaveFileHash("config.es3", 5, HashB) },
        new[] { new CoopSlotHash(2, 20, HashA) });

    [Fact]
    public void It_round_trips_as_text()
    {
        Assert.True(RestoreCheck.TryParse(Check().ToText(), out RestoreCheck? read));
        Assert.Equal(Backup, read!.Backup);
        Assert.Equal(3, read.Count);
        Assert.Equal(new[] { "profileV2.es3", "config.es3" }, read.Files.Select(f => f.Name));
        Assert.Equal(HashB, read.Files[1].Sha256);
        Assert.Equal(2, read.Slots[0].Slot);
    }

    [Fact]
    public void Windows_line_ends_read_too()
    {
        Assert.True(RestoreCheck.TryParse(Check().ToText().Replace("\n", "\r\n", StringComparison.Ordinal), out RestoreCheck? read));
        Assert.Equal(3, read!.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("file=profileV2.es3|AAAA")]
    [InlineData("backup=not-a-backup")]
    [InlineData("backup=20261031-120000-host\nbackup=20261031-120000-host")]
    [InlineData("backup=20261031-120000-host\nother=1")]
    [InlineData("backup=20261031-120000-host\nfile=../evil.es3|" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("backup=20261031-120000-host\nfile=notes.txt|" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("backup=20261031-120000-host\nfile=profileV2.es3|nothex")]
    [InlineData("backup=20261031-120000-host\nslot=0|" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("backup=20261031-120000-host\nslot=x|" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void What_isnt_a_check_is_refused(string? text)
    {
        Assert.False(RestoreCheck.TryParse(text, out RestoreCheck? read));
        Assert.Null(read);
    }

    [Fact]
    public void A_slot_or_file_listed_twice_or_too_many_entries_are_refused()
    {
        string hash = HashA;
        Assert.False(RestoreCheck.TryParse($"backup={Backup}\nslot=1|{hash}\nslot=1|{hash}", out _));
        Assert.False(RestoreCheck.TryParse($"backup={Backup}\nfile=profileV2.es3|{hash}\nfile=PROFILEV2.es3|{HashB}", out _));

        var lines = new List<string> { $"backup={Backup}" };
        for (int i = 0; i <= RestoreCheck.MaxEntries; i++)
        {
            lines.Add($"file=f{i}.es3|{hash}");
        }

        Assert.False(RestoreCheck.TryParse(string.Join('\n', lines), out _));
    }

    [Fact]
    public void Files_as_restored_read_the_same_in_any_case_of_hash()
    {
        RestoreReading reading = Check().Read(name => name == "config.es3" ? HashB.ToUpperInvariant() : HashA, _ => HashA.ToLowerInvariant());

        Assert.True(reading.Same);
        Assert.Equal($"Backups: the restored files read back as in backups/{Logged}: 3 of 3 match its manifest (SHA-256).", reading.ReadBackLine());
        Assert.Equal($"Backups: at this start the 3 file(s) restored from backups/{Logged} last time are as restored.", reading.StartLine());
        Assert.Equal($"Backups: at the title screen the 3 file(s) restored from backups/{Logged} are still as restored.", reading.TitleLine());
    }

    [Fact]
    public void A_file_changed_or_gone_is_named()
    {
        RestoreReading reading = Check().Read(name => name == "config.es3" ? null : new string('C', 64), _ => null);

        Assert.False(reading.Same);
        Assert.Equal(new[] { "profileV2.es3", "config.es3", "co-op slot 2" }, reading.Differ);
        Assert.Equal($"Backups: the restored files read back as in backups/{Logged}: 0 of 3 match its manifest (SHA-256); DIFFER: profileV2.es3, config.es3, co-op slot 2.", reading.ReadBackLine());
        Assert.Equal($"Backups: 3 of the 3 file(s) restored from backups/{Logged} last time changed before this start: profileV2.es3, config.es3, co-op slot 2 (the game or Steam Cloud wrote them).", reading.StartLine());
        Assert.Contains("at the title screen 3 of the 3 file(s)", reading.TitleLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lines_never_hold_an_older_backups_local_time_name()
    {
        RestoreReading same = Check().Read(name => name == "config.es3" ? HashB : HashA, _ => HashA);
        RestoreReading differ = Check().Read(_ => null, _ => null);

        foreach (string line in new[] { same.ReadBackLine(), same.StartLine(), same.TitleLine(), differ.ReadBackLine(), differ.StartLine(), differ.TitleLine() })
        {
            Assert.DoesNotContain(Backup, line, StringComparison.Ordinal);
            Assert.Contains("(named in local time on disk)", line, StringComparison.Ordinal);
        }

        // A backup named in UTC (every one since 0.11.0) is named as it is.
        Assert.Equal($"Backups: at this start the 3 file(s) restored from backups/{UtcBackup} last time are as restored.",
            Check(UtcBackup).Read(name => name == "config.es3" ? HashB : HashA, _ => HashA).StartLine());
    }
}
