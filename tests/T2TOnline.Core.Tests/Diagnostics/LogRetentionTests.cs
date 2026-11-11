using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class LogRetentionTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static SessionLogFile Log(double daysAgo, long bytes = 100_000)
    {
        DateTime at = Now.AddDays(-daysAgo);
        return new SessionLogFile($"session-{at.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.log", bytes, at);
    }

    private static List<SessionLogFile> Deleted(IEnumerable<SessionLogFile> files, out IReadOnlyList<KeyValuePair<SessionLogFile, LogRemoval>> plan)
    {
        plan = LogRetention.ToDelete(files, Now);
        return plan.Select(p => p.Key).ToList();
    }

    [Fact]
    public void A_few_small_recent_logs_are_all_kept()
    {
        Assert.Empty(LogRetention.ToDelete(Enumerable.Range(0, 10).Select(i => Log(i * 0.5)), Now));
        Assert.Equal(string.Empty, LogRetention.Describe(Array.Empty<KeyValuePair<SessionLogFile, LogRemoval>>()));
    }

    [Fact]
    public void Past_twenty_files_the_oldest_go_leaving_room_for_this_sessions()
    {
        List<SessionLogFile> files = Enumerable.Range(0, 25).Select(i => Log(i * 0.1)).ToList();
        List<SessionLogFile> deleted = Deleted(files, out var plan);

        Assert.Equal(6, deleted.Count); // 19 kept + the new one = 20
        Assert.Equal(files.Skip(19), deleted);
        Assert.All(plan, p => Assert.Equal(LogRemoval.Count, p.Value));
    }

    [Fact]
    public void Logs_older_than_thirty_days_go_but_the_newest_five_stay_whatever_their_age()
    {
        List<SessionLogFile> files = Enumerable.Range(0, 8).Select(i => Log(40 + i)).ToList();
        List<SessionLogFile> deleted = Deleted(files, out var plan);

        Assert.Equal(files.Skip(LogRetention.AlwaysKept - 1), deleted); // 4 old ones + the new session's = the newest 5
        Assert.All(plan, p => Assert.Equal(LogRemoval.Age, p.Value));
    }

    [Fact]
    public void The_folder_stays_within_100_mb_with_room_for_this_sessions_20()
    {
        // Ten 15 MB logs: the newest four (60 MB) always stay; with this session's 20 MB, 80 MB is all the rest may use.
        List<SessionLogFile> files = Enumerable.Range(0, 10).Select(i => Log(i * 0.1, 15 * Mb)).ToList();
        List<SessionLogFile> deleted = Deleted(files, out var plan);

        Assert.Equal(files.Skip(5), deleted);
        Assert.All(plan, p => Assert.Equal(LogRemoval.Size, p.Value));
        long kept = files.Except(deleted).Sum(f => f.Bytes);
        Assert.True(kept + LogRetention.MaxFileBytes <= LogRetention.MaxTotalBytes);
    }

    [Fact]
    public void Once_over_the_size_budget_every_older_log_goes_even_a_small_one()
    {
        var files = new List<SessionLogFile> { Log(0, Mb), Log(1, Mb), Log(2, Mb), Log(3, Mb), Log(4, 79 * Mb), Log(5, Mb) };
        List<SessionLogFile> deleted = Deleted(files, out _);
        Assert.Equal(new[] { files[4], files[5] }, deleted);
    }

    [Fact]
    public void The_newest_five_stay_even_past_the_size_budget()
    {
        List<SessionLogFile> files = Enumerable.Range(0, 6).Select(i => Log(i, 20 * Mb)).ToList();
        List<SessionLogFile> deleted = Deleted(files, out _);
        Assert.Equal(files.Skip(4), deleted);
    }

    [Fact]
    public void A_log_is_dated_by_its_name_and_by_its_write_time_without_one()
    {
        var named = new SessionLogFile("session-20261001T080000Z.log", 1, Now);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), LogRetention.StampOf(named));
        var renamed = new SessionLogFile("session-copy.log", 1, Now.AddDays(-3));
        Assert.Equal(Now.AddDays(-3), LogRetention.StampOf(renamed));
    }

    [Fact]
    public void The_line_says_how_many_and_why()
    {
        var plan = new List<KeyValuePair<SessionLogFile, LogRemoval>>
        {
            new(Log(40), LogRemoval.Age),
            new(Log(41), LogRemoval.Age),
            new(Log(2), LogRemoval.Count),
        };
        Assert.Equal("Session logs: deleted the 3 oldest (2 older than 30 days, 1 past 20 files); the newest 5 are always kept.",
            LogRetention.Describe(plan));
    }
}
