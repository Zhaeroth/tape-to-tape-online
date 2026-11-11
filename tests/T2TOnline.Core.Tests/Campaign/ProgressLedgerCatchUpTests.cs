using System;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The dated copy a friend's ledger is owed when it changed with none after it: a restore of saves taken since then
/// must find the ledger those saves go with.
/// </summary>
public class ProgressLedgerCatchUpTests
{
    private const string Before = "t2t-progress-ledger 1\n";
    private const string Through10 = "t2t-progress-ledger 1\nsession 00000000000000aa 10\n";

    private static DateTime At(int hour, int minute, int second, int millisecond = 0) => new(2026, 11, 10, hour, minute, second, millisecond, DateTimeKind.Utc);

    [Fact]
    public void A_ledger_the_newest_copy_holds_or_no_ledger_is_owed_nothing()
    {
        Assert.Null(ProgressLedgerCatchUp.CopyOwed(null, At(12, 0, 0), Before, At(11, 0, 0), At(13, 0, 0)));
        Assert.Null(ProgressLedgerCatchUp.CopyOwed(Through10, At(12, 0, 0), Through10, At(12, 0, 1), At(13, 0, 0)));

        // No copy at all: the ledger there is gets its first.
        Assert.Equal("ledger-20261110T120000000Z-end.txt", ProgressLedgerCatchUp.CopyOwed(Through10, At(12, 0, 0), null, null, At(13, 0, 0)));
    }

    [Fact]
    public void A_session_the_game_stopped_during_leaves_the_ledger_a_copy_from_its_last_write()
    {
        // The session's start copy holds the ledger from before its batches; the game stopped with ten applied and saved.
        string start = ProgressLedgerHistory.Name(At(12, 0, 1), start: true);
        string[] copies = { start };
        string? newest = ProgressLedgerCatchUp.Newest(copies, out DateTime newestUtc);
        Assert.Equal(start, newest);

        string? owed = ProgressLedgerCatchUp.CopyOwed(Through10, At(12, 20, 0), Before, newestUtc, At(18, 0, 0));
        Assert.Equal("ledger-20261110T122000000Z-end.txt", owed);

        // A backup taken later at the title (before another restore) holds saves with those ten batches.
        Assert.Equal(start, ProgressLedgerHistory.Pick(copies, At(13, 0, 0))); // without the copy: the ledger from before them
        Assert.Equal(owed, ProgressLedgerHistory.Pick(copies.Append(owed!), At(13, 0, 0)));

        // The stopped session's own backup, taken a second before its start copy, still goes with that one.
        Assert.Equal(start, ProgressLedgerHistory.Pick(copies.Append(owed!), At(12, 0, 0)));
    }

    [Fact]
    public void A_restore_leaves_the_ledger_it_put_back_a_copy_that_never_stands_for_the_backup_taken_before_it()
    {
        string start = ProgressLedgerHistory.Name(At(10, 0, 1), start: true);
        string end = ProgressLedgerHistory.Name(At(11, 0, 0), start: false);
        string[] copies = { start, end };
        string? newest = ProgressLedgerCatchUp.Newest(copies, out DateTime newestUtc);
        Assert.Equal(end, newest);

        // At 15:00:00.250 a restore put the ledger from before the session back (the end copy holds the session's).
        string? owed = ProgressLedgerCatchUp.CopyOwed(Before, At(15, 0, 0, 250), Through10, newestUtc, At(15, 0, 0, 300));
        Assert.Equal("ledger-20261110T150000250Z-end.txt", owed);
        string[] all = copies.Append(owed!).ToArray();

        // A backup taken afterwards holds the restored saves: the ledger put back, not the session's.
        Assert.Equal(end, ProgressLedgerHistory.Pick(copies, At(16, 0, 0))); // without the copy
        Assert.Equal(owed, ProgressLedgerHistory.Pick(all, At(16, 0, 0)));

        // The backup the restore took first, two seconds before, holds the saves from before it: the session's ledger.
        Assert.Equal(end, ProgressLedgerHistory.Pick(all, At(14, 59, 58)));
        Assert.True(ProgressLedgerHistory.TryParse(owed, out _, out bool isStart));
        Assert.False(isStart);
    }

    [Fact]
    public void The_copy_is_newer_than_every_other_and_never_dated_ahead_of_the_clock()
    {
        DateTime newestUtc = At(12, 0, 0, 500);

        // Written before the newest copy (a clock set back, a folder put back by hand): the ledger there is still comes last.
        Assert.Equal("ledger-20261110T120000501Z-end.txt", ProgressLedgerCatchUp.CopyOwed(Through10, At(9, 0, 0), Before, newestUtc, At(13, 0, 0)));

        // Within the newest copy's own millisecond.
        Assert.Equal("ledger-20261110T120000501Z-end.txt",
            ProgressLedgerCatchUp.CopyOwed(Through10, newestUtc.AddTicks(4000), Before, newestUtc, At(13, 0, 0)));

        // A file time ahead of the clock.
        Assert.Equal("ledger-20261110T130000000Z-end.txt", ProgressLedgerCatchUp.CopyOwed(Through10, At(23, 0, 0), Before, newestUtc, At(13, 0, 0)));
    }

    [Fact]
    public void The_newest_copy_is_by_its_time_whatever_the_order_and_the_other_files()
    {
        string a = ProgressLedgerHistory.Name(At(9, 0, 0), start: true);
        string b = ProgressLedgerHistory.Name(At(11, 0, 0), start: false);
        string c = ProgressLedgerHistory.Name(At(10, 0, 0), start: true);
        Assert.Equal(b, ProgressLedgerCatchUp.Newest(new[] { a, "notes.txt", b, c, "ledger-nonsense-end.txt" }, out DateTime utc));
        Assert.Equal(At(11, 0, 0), utc);
        Assert.Null(ProgressLedgerCatchUp.Newest(new[] { "notes.txt" }, out _));
        Assert.Null(ProgressLedgerCatchUp.Newest(Array.Empty<string>(), out _));
    }
}
