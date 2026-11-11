using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class SaveWritesTests
{
    [Theory]
    [InlineData("profileV2.es3", "profileV2.es3")]
    [InlineData("profileV2.es3.tmp", "profileV2.es3")]
    [InlineData("profileV2.es3.bac", "profileV2.es3")]
    [InlineData("profileV2.es3.tmp.bak", "profileV2.es3")]
    [InlineData("PROFILEV2.ES3.TMP.BAK", "PROFILEV2.ES3")]
    [InlineData("profileV2.es3.bac.tmp", "profileV2.es3.bac")] // one suffix only: nobody's copy
    [InlineData("profileV2.es3.bak", "profileV2.es3.bak")] // .bak alone isn't one of Easy Save's copies
    [InlineData(".tmp", ".tmp")]
    [InlineData("", "")]
    public void A_copys_original_strips_one_easy_save_suffix(string name, string original)
    {
        Assert.Equal(original, EasySaveCopies.Original(name));
    }

    [Fact]
    public void Null_has_no_original()
    {
        Assert.Equal(string.Empty, EasySaveCopies.Original(null));
        Assert.False(EasySaveCopies.IsWriteOf(null, "profileV2.es3"));
        Assert.False(EasySaveCopies.IsWriteOf("profileV2.es3", string.Empty));
    }

    [Fact]
    public void Every_file_easy_saves_commit_and_backup_name_for_an_allowed_save_is_a_write_of_it()
    {
        // ES3IO.CommitBackup deletes X.tmp.bak, copies X to X.tmp.bak, deletes X, moves X.tmp to X,
        // deletes X.tmp.bak; ES3.CreateBackup copies X to X.bac (ES3.CopyFile deletes the old X.bac first). The guard sees
        // the settings' path (X) and each delete's full path.
        foreach (string allowed in new[] { "profileV2.es3", "statsV2.es3", "RunHistoryIndex.es3", "RunHistory_03.es3" })
        {
            string folder = Path.Combine("C:", "Users", "someone", "AppData", "LocalLow", "Excellent Rectangle", "Tape to Tape");
            foreach (string touched in new[] { allowed, $"{allowed}.tmp.bak", $"{allowed}.tmp", $"{allowed}.bac" })
            {
                Assert.True(EasySaveCopies.IsWriteOf(Path.GetFileName(Path.Combine(folder, touched)), allowed), touched);
            }
        }
    }

    [Fact]
    public void Another_files_copies_are_not_writes_of_an_allowed_file()
    {
        Assert.False(EasySaveCopies.IsWriteOf("RunDataV2.es3.tmp.bak", "profileV2.es3"));
        Assert.False(EasySaveCopies.IsWriteOf("statsV2.es3.bac", "profileV2.es3"));
        Assert.False(EasySaveCopies.IsWriteOf("profileV2.es3.old", "profileV2.es3"));
        Assert.False(EasySaveCopies.IsWriteOf("xprofileV2.es3.tmp", "profileV2.es3"));
    }

    private static readonly DateTime Start = new(2026, 10, 31, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_run_file_that_changed_was_written()
    {
        var before = new SaveStamp(true, 1000, Start.AddMinutes(-5));
        Assert.Equal(SaveLanded.Written, RunSaveCheck.Judge(before, Start, new SaveStamp(true, 1000, Start.AddMilliseconds(3)), lastLook: false));
        Assert.Equal(SaveLanded.Written, RunSaveCheck.Judge(before, Start, new SaveStamp(true, 1200, before.WrittenUtc), lastLook: false));
        Assert.Equal(SaveLanded.Written, RunSaveCheck.Judge(default, Start, new SaveStamp(true, 10, Start), lastLook: false)); // a first save
    }

    [Fact]
    public void An_unchanged_run_file_is_looked_at_again_then_counted_as_not_saved()
    {
        var before = new SaveStamp(true, 1000, Start.AddMinutes(-5));
        Assert.Equal(SaveLanded.NotYet, RunSaveCheck.Judge(before, Start, before, lastLook: false));
        Assert.Equal(SaveLanded.NotWritten, RunSaveCheck.Judge(before, Start, before, lastLook: true));
        Assert.Equal(SaveLanded.NotYet, RunSaveCheck.Judge(before, Start, default, lastLook: false));
        Assert.Equal(SaveLanded.NotWritten, RunSaveCheck.Judge(before, Start, default, lastLook: true)); // gone
        Assert.Equal(SaveLanded.NotWritten, RunSaveCheck.Judge(default, Start, default, lastLook: true)); // never there
    }

    [Fact]
    public void An_unchanged_run_file_written_just_before_the_save_is_unclear_not_a_failure()
    {
        // File times are coarser than the clock: a save of the same size moments after the last can keep the stamp.
        var before = new SaveStamp(true, 1000, Start.AddMilliseconds(-20));
        Assert.Equal(SaveLanded.Unclear, RunSaveCheck.Judge(before, Start, before, lastLook: true));
        var older = new SaveStamp(true, 1000, Start - RunSaveCheck.ClockSlack);
        Assert.Equal(SaveLanded.NotWritten, RunSaveCheck.Judge(older, Start, older, lastLook: true));
    }

    [Fact]
    public void A_stamp_is_read_from_the_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"t2t-stamp-{Guid.NewGuid():N}.es3");
        try
        {
            Assert.Equal(default, SaveStamp.Of(path));
            File.WriteAllText(path, "{}");
            SaveStamp stamp = SaveStamp.Of(path);
            Assert.True(stamp.Exists);
            Assert.Equal(2, stamp.Length);
            Assert.Equal(File.GetLastWriteTimeUtc(path), stamp.WrittenUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly KeyValuePair<string, string>[] Folders =
    {
        new(@"C:\Users\alex", "%USERPROFILE%"),
        new(@"C:\Users\alex\AppData\LocalLow\Excellent Rectangle\Tape to Tape", "<save folder>"),
        new(@"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape\", "<game folder>"),
        new(string.Empty, "nothing"),
        new(@"C:\", "a drive"),
    };

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\alex\AppData\LocalLow\Excellent Rectangle\Tape to Tape\profileV2.es3'.", "Could not find file '<save folder>\\profileV2.es3'.")]
    [InlineData(@"Access to the path 'c:/users/ALEX/AppData/Local/T2T Online/backups' is denied.", "Access to the path '%USERPROFILE%/AppData/Local/T2T Online/backups' is denied.")]
    [InlineData(@"The process cannot access 'C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape\UserData\x' or 'C:\Users\alex'", "The process cannot access '<game folder>\\UserData\\x' or '%USERPROFILE%'")]
    [InlineData(@"C:\Users\alexis\file and C:\Users\alex.smith\x stay", @"C:\Users\alexis\file and C:\Users\alex.smith\x stay")]
    [InlineData("Disk full.", "Disk full.")]
    public void File_errors_lose_the_players_folders(string message, string redacted)
    {
        Assert.Equal(redacted, SavePathText.Redact(message, Folders));
    }

    [Fact]
    public void Redacting_nothing_gives_nothing()
    {
        Assert.Equal(string.Empty, SavePathText.Redact(null, Folders));
        Assert.Equal(@"C:\Users\alex\x", SavePathText.Redact(@"C:\Users\alex\x", Array.Empty<KeyValuePair<string, string>>()));
        Assert.Throws<ArgumentNullException>(() => SavePathText.Redact("x", null!));
    }

    [Fact]
    public void Each_place_logs_its_own_share_of_errors()
    {
        var limit = new ErrorLogLimit(2);
        Assert.True(limit.Allow("the update"));
        Assert.True(limit.Allow("the update"));
        Assert.False(limit.Allow("the update"));
        Assert.False(limit.Allow("the update"));
        Assert.True(limit.Allow("restoring your run")); // a later, different error is still logged
        Assert.True(limit.Allow("restoring your run"));
        Assert.False(limit.Allow("restoring your run"));

        IReadOnlyList<KeyValuePair<string, int>> suppressed = limit.TakeSuppressed();
        Assert.Equal(new[] { "restoring your run", "the update" }, suppressed.Select(s => s.Key));
        Assert.Equal(new[] { 1, 2 }, suppressed.Select(s => s.Value));
        Assert.Equal("3 more error(s) weren't logged: restoring your run (1), the update (2)", ErrorLogLimit.Describe(suppressed));

        Assert.Empty(limit.TakeSuppressed());
        Assert.Equal(string.Empty, ErrorLogLimit.Describe(limit.TakeSuppressed()));
        Assert.False(limit.Allow("the update")); // its share stays spent
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorLogLimit(0));
    }

    [Fact]
    public void The_limit_counts_right_from_many_threads()
    {
        var limit = new ErrorLogLimit(5);
        int allowed = 0;
        Parallel.For(0, 1000, i =>
        {
            if (limit.Allow(i % 2 == 0 ? "a" : "b"))
            {
                System.Threading.Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(10, allowed);
        Assert.Equal(990, limit.TakeSuppressed().Sum(s => s.Value));
    }
}
