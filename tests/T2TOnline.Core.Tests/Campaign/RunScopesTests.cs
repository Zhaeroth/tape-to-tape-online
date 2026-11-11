using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class RunScopesTests
{
    private const string Solo = "RunDataV2.es3";
    private const string Coop = "coop-1.es3";

    private static RunScope Scope(int goalsThisRun, params (int Stat, StatValue Run, StatValue Act)[] stats) =>
        new(new[] { 0, goalsThisRun, 0, 0, 0 }, stats.ToDictionary(s => s.Stat, s => new ScopeValues(s.Run, s.Act)));

    [Fact]
    public void Each_run_file_keeps_its_own_counters_and_the_live_one_is_remembered()
    {
        // Regression case: the solo run has 25 injured goals in it; the co-op run must start from nothing.
        var store = new RunScopeStore();
        Assert.Null(store.Live);
        store.SetLive(Solo);
        store.Set(Solo, Scope(4, (58, StatValue.Whole(25), StatValue.Whole(3)), (67, StatValue.Real(812.5f), StatValue.Real(40.25f))));
        Assert.False(store.TryGet(Coop, out RunScope fresh));
        Assert.Equal(new int[RunScopeStore.QuestCounters], fresh.Quests);
        Assert.Empty(fresh.Stats);
        store.SetLive(Coop);

        RunScopeStore read = RunScopeStore.Parse(store.Format());
        Assert.Equal(Coop, read.Live);
        Assert.True(read.TryGet(Solo, out RunScope solo));
        Assert.Equal(new[] { 0, 4, 0, 0, 0 }, solo.Quests);
        Assert.Equal(new ScopeValues(StatValue.Whole(25), StatValue.Whole(3)), solo.Stats[58]);
        Assert.Equal(new ScopeValues(StatValue.Real(812.5f), StatValue.Real(40.25f)), solo.Stats[67]);
        Assert.False(read.Changed);
    }

    [Fact]
    public void Run_files_are_matched_by_name_whatever_the_case_and_set_again_replaces()
    {
        var store = new RunScopeStore();
        store.Set(Coop, Scope(1));
        store.Set("COOP-1.es3", Scope(2));
        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet(Coop, out RunScope scope));
        Assert.Equal(2, scope.Quests[1]);
    }

    [Theory]
    [InlineData("RunDataV2.es3", true)]
    [InlineData("coop-3.es3", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a file.es3", false)]
    [InlineData(@"C:\saves\coop-1.es3", false)]
    [InlineData("campaign/coop-1.es3", false)]
    public void Only_plain_file_names_are_run_files(string? name, bool ok)
    {
        Assert.Equal(ok, RunScopeStore.IsRunFile(name));
        if (!ok)
        {
            Assert.Throws<ArgumentException>(() => new RunScopeStore().Set(name!, RunScope.Empty));
        }
    }

    [Fact]
    public void The_oldest_run_files_go_first_but_never_the_live_one()
    {
        var store = new RunScopeStore();
        store.SetLive("live.es3");
        store.Set("live.es3", Scope(9));
        for (int i = 0; i < RunScopeStore.MaxScopes + 3; i++)
        {
            store.Set($"coop-{i}.es3", Scope(i));
        }

        Assert.Equal(RunScopeStore.MaxScopes, store.Count);
        Assert.True(store.TryGet("live.es3", out _));
        Assert.False(store.TryGet("coop-0.es3", out _));
        Assert.True(store.TryGet($"coop-{RunScopeStore.MaxScopes + 2}.es3", out _));
    }

    [Fact]
    public void A_damaged_store_keeps_what_still_reads_and_never_throws()
    {
        string text = string.Join("\r\n", new[]
        {
            "# comment",
            "live coop-1.es3",
            "scope RunDataV2.es3 1 2 3 4 5",
            "scope bad 1 2",
            "stat RunDataV2.es3 12 w 3 1",
            "stat RunDataV2.es3 13 r NaN 1",
            "stat RunDataV2.es3 99999 w 1 1",
            "stat RunDataV2.es3 14 x 1 1",
            "stat coop-2.es3 15 r 1.5 0.5",
            "live a/b.es3",
        });
        RunScopeStore store = RunScopeStore.Parse(text);
        Assert.Equal("coop-1.es3", store.Live);
        Assert.True(store.TryGet(Solo, out RunScope solo));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, solo.Quests);
        Assert.Equal(new[] { 12 }, solo.Stats.Keys);
        Assert.True(store.TryGet("coop-2.es3", out RunScope other)); // a stat line alone makes a scope at zero counts
        Assert.Equal(new int[RunScopeStore.QuestCounters], other.Quests);
        Assert.Equal(StatValue.Real(1.5f), other.Stats[15].Run);

        var random = new Random(5);
        const string Alphabet = "live scope stat w r RunDataV2.es3 coop-1 0123456789.-\n #\t";
        for (int i = 0; i < 2000; i++)
        {
            char[] noise = new char[random.Next(0, 200)];
            for (int c = 0; c < noise.Length; c++)
            {
                noise[c] = Alphabet[random.Next(Alphabet.Length)];
            }

            _ = RunScopeStore.Parse(new string(noise)).Format();
        }
    }

    [Fact]
    public void A_scope_needs_five_quest_counts() =>
        Assert.Throws<ArgumentException>(() => new RunScope(new[] { 1, 2 }, new Dictionary<int, ScopeValues>()));

    // ------------------------------------------------------------------ the ledger's dated copies

    private static readonly DateTime Noon = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_copys_name_says_when_and_which_end_of_a_session()
    {
        string name = ProgressLedgerHistory.Name(Noon.AddMilliseconds(123), start: true);
        Assert.True(ProgressLedgerHistory.TryParse(name, out DateTime utc, out bool start));
        Assert.Equal(Noon.AddMilliseconds(123), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.True(start);
        Assert.True(ProgressLedgerHistory.TryParse(ProgressLedgerHistory.Name(Noon, start: false), out _, out bool end));
        Assert.False(end);
        Assert.False(ProgressLedgerHistory.TryParse("progress-ledger.txt", out _, out _));
        Assert.False(ProgressLedgerHistory.TryParse("ledger-yesterday-start.txt", out _, out _));
        Assert.False(ProgressLedgerHistory.TryParse(null, out _, out _));
    }

    [Fact]
    public void A_restore_takes_the_ledger_as_it_was_when_its_backup_was_taken()
    {
        // Session 1 from 12:00 to 13:00, session 2 from 14:00 (its backup taken a moment before its start copy) to 15:00.
        string s1Start = ProgressLedgerHistory.Name(Noon, true), s1End = ProgressLedgerHistory.Name(Noon.AddHours(1), false);
        string s2Start = ProgressLedgerHistory.Name(Noon.AddHours(2).AddSeconds(1), true), s2End = ProgressLedgerHistory.Name(Noon.AddHours(3), false);
        var copies = new[] { s2End, "other.txt", s1Start, s2Start, s1End };

        Assert.Equal(s2Start, ProgressLedgerHistory.Pick(copies, Noon.AddHours(2))); // session 2's backup: its own start copy, taken right after it
        Assert.Equal(s1End, ProgressLedgerHistory.Pick(copies, Noon.AddHours(1.5))); // a manual backup between sessions
        Assert.Equal(s2End, ProgressLedgerHistory.Pick(copies, Noon.AddHours(4))); // "before restoring": now
        Assert.Equal(s1Start, ProgressLedgerHistory.Pick(copies, Noon.AddSeconds(-1))); // the first session's own backup
        Assert.Null(ProgressLedgerHistory.Pick(copies, Noon.AddHours(-1))); // older than every copy: not known
        Assert.Null(ProgressLedgerHistory.Pick(new[] { s1End }, Noon)); // an end copy after it can't stand for it
    }

    [Fact]
    public void A_backups_own_start_copy_stands_for_it_whatever_the_copies_before_it_say()
    {
        // Session 1 never ended (the game stopped in the middle of it: no end copy), yet its batches are in the saves and in the
        // ledger. Session 2's backup is taken with those saves, and its start copy, a moment later, holds that ledger. The copy
        // before the backup is session 1's start: the ledger from before every one of those batches. Restoring session 2's
        // backup with it, a rejoin of the hosting session would apply them all a second time.
        string s1Start = ProgressLedgerHistory.Name(Noon, true);
        string s2Start = ProgressLedgerHistory.Name(Noon.AddHours(2).AddSeconds(1), true), s2End = ProgressLedgerHistory.Name(Noon.AddHours(3), false);
        var copies = new[] { s2End, s1Start, s2Start };
        Assert.Equal(s2Start, ProgressLedgerHistory.Pick(copies, Noon.AddHours(2)));

        // The first start copy after the backup, and only one taken within the tolerance; an end copy never stands for a backup before it.
        string s3Start = ProgressLedgerHistory.Name(Noon.AddHours(2).AddSeconds(20), true);
        Assert.Equal(s2Start, ProgressLedgerHistory.Pick(copies.Append(s3Start), Noon.AddHours(2)));
        Assert.Equal(s2Start, ProgressLedgerHistory.Pick(copies, Noon.AddHours(2).AddSeconds(1))); // taken in the same moment
        DateTime tooEarly = Noon.AddHours(2).AddSeconds(1) - ProgressLedgerHistory.StartTolerance - TimeSpan.FromSeconds(1);
        Assert.Equal(s1Start, ProgressLedgerHistory.Pick(copies, tooEarly));
        string endOnly = ProgressLedgerHistory.Name(Noon.AddHours(2).AddSeconds(1), false);
        Assert.Equal(s1Start, ProgressLedgerHistory.Pick(new[] { s1Start, endOnly }, Noon.AddHours(2)));
    }

    [Fact]
    public void Copies_beyond_the_newest_kept_go_but_the_oldest_stays()
    {
        List<string> names = Enumerable.Range(0, ProgressLedgerHistory.Kept + 5).Select(i => ProgressLedgerHistory.Name(Noon.AddMinutes(i), i % 2 == 0)).ToList();
        IReadOnlyList<string> expired = ProgressLedgerHistory.Expired(names.Append("unrelated.txt"));
        Assert.Equal(4, expired.Count);
        Assert.DoesNotContain(names[0], expired);
        Assert.Equal(names.Skip(1).Take(4), expired);
        Assert.Empty(ProgressLedgerHistory.Expired(names.Take(ProgressLedgerHistory.Kept + 1)));
    }
}
