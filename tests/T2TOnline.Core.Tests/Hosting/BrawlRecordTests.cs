using T2TOnline.Core.Hosting;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Hosting;

/// <summary>The host's record of a friend in a brawl: how AI punches met their fighter, and how late their block came.</summary>
public class BrawlRecordTests
{
    [Fact]
    public void A_brawl_nobody_punched_says_so()
    {
        var record = new BrawlRecord();
        record.Begin(0);
        record.End(1_000_000);
        Assert.Equal(0, record.Reached);
        Assert.Equal("no AI punch had their fighter in reach at its apex", record.DescribeApexes());
    }

    [Fact]
    public void A_block_already_up_counts_though_nothing_waited()
    {
        // Tallies of the punches that waited alone miss a friend whose block was already up: the game blocks those at once.
        var record = new BrawlRecord();
        record.Begin(0);
        record.Met(ApexMeeting.BlockUp);
        record.Met(ApexMeeting.BlockUpOtherWay);
        record.Met(ApexMeeting.CouldNotBlock);
        record.End(1_000_000);
        Assert.Equal(3, record.Reached);
        Assert.Equal("3 AI punch(es) had their fighter in reach at the apex: their block was up for 2 (1 of them the other way: partial damage), 1 came while they couldn't block, 0 waited for their block",
            record.DescribeApexes());
    }

    [Fact]
    public void A_block_after_a_timed_out_wait_measures_how_late_it_came()
    {
        var record = new BrawlRecord();
        record.Begin(0);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.TimedOut, startedMicros: 1_000_000, deadlineMicros: 1_085_000);
        record.Feed(FightActions.Jab, FightActions.None, 1_100_000); // a punch isn't a block
        record.Feed(FightActions.BlockHigh, FightActions.None, 1_225_000);
        Assert.Equal(1, record.LateBlocks);
        Assert.Equal(140_000, record.LateBlock(0));

        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.TimedOut, 2_000_000, 2_080_000);
        record.Feed(FightActions.None, FightActions.None, 3_000_000);
        record.Feed(FightActions.BlockLow, FightActions.None, 3_100_000); // more than 1 s after: not this wait's
        Assert.Equal(1, record.LateBlocks);
        Assert.Equal(1, record.NoLateBlock);
    }

    [Fact]
    public void A_block_pressed_in_the_windows_last_frame_counts_before_its_end()
    {
        var record = new BrawlRecord();
        record.Begin(0);
        record.Met(ApexMeeting.Waited);
        record.Feed(FightActions.BlockLow, FightActions.None, 1_082_000); // arrived, but the game raises it next frame
        record.WaitEnded(GraceOutcome.TimedOut, 1_000_000, 1_085_000);
        Assert.Equal(-3_000, record.LateBlock(0));

        // One pressed before the wait began isn't this wait's.
        record.Feed(FightActions.None, FightActions.None, 1_500_000);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.TimedOut, 2_000_000, 2_080_000);
        Assert.Equal(1, record.LateBlocks);
        record.End(3_000_000);
    }

    [Fact]
    public void A_block_pressed_in_the_frame_the_timeout_is_seen_counts_just_after_its_end()
    {
        // The timeout is seen a frame at most after the deadline; a press played in that frame came before it was told.
        var record = new BrawlRecord();
        record.Begin(0);
        record.Met(ApexMeeting.Waited);
        record.Feed(FightActions.BlockHigh, FightActions.None, 1_087_000);
        record.WaitEnded(GraceOutcome.TimedOut, 1_000_000, 1_085_000);
        Assert.Equal(1, record.LateBlocks);
        Assert.Equal(2_000, record.LateBlock(0));
        record.Feed(FightActions.BlockHigh, FightActions.None, 1_200_000); // still held: no new press, nothing pending
        Assert.Equal(1, record.LateBlocks);
        Assert.Equal(0, record.NoLateBlock);
    }

    [Fact]
    public void A_wait_with_no_block_before_the_next_or_the_end_counts_as_none()
    {
        var record = new BrawlRecord();
        record.Begin(0);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.TimedOut, 1_000_000, 1_080_000);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.TimedOut, 1_300_000, 1_380_000);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.Blocked, 1_600_000, 1_680_000);
        record.End(2_000_000);
        Assert.Equal(2, record.NoLateBlock);
        Assert.Equal(0, record.LateBlocks);
        Assert.Equal("3 AI punch(es) had their fighter in reach at the apex: their block was up for 0, 0 came while they couldn't block, 3 waited for their block: " +
            "1 blocked in time, 0 the other way, 2 timed out (no block came after the window: none within 1 s after 2)", record.DescribeApexes());
    }

    [Fact]
    public void The_line_names_late_blocks_and_every_other_end()
    {
        var record = new BrawlRecord();
        record.Begin(0);
        for (int i = 0; i < 8; i++)
        {
            long start = (i + 1) * 1_000_000L;
            record.Met(ApexMeeting.Waited);
            record.WaitEnded(GraceOutcome.TimedOut, start, start + 80_000);
            record.Feed(FightActions.BlockHigh, FightActions.None, start + 80_000 + ((i + 1) * 10_000));
            record.Feed(FightActions.None, FightActions.None, start + 500_000);
        }

        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.Unblockable, 0, 0);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.Interrupted, 0, 0);
        record.Met(ApexMeeting.WindowOff);
        record.End(20_000_000);
        Assert.Equal(8, record.LateBlocks);
        Assert.Equal("11 AI punch(es) had their fighter in reach at the apex: their block was up for 0, 0 came while they couldn't block, 1 went on at once (the fight grace window is off), " +
            "10 waited for their block: 0 blocked in time, 0 the other way, 8 timed out (their next block came +10, +20, +30, +40, +50, +60 ms after the window " +
            "and 2 more (range +10 to +80)), 1 went on early (they couldn't block any more), 1 interrupted", record.DescribeApexes());
    }

    [Fact]
    public void Begin_forgets_the_last_brawl()
    {
        var record = new BrawlRecord();
        record.Begin(0);
        record.Met(ApexMeeting.Waited);
        record.WaitEnded(GraceOutcome.TimedOut, 0, 80_000);
        record.Begin(5_000_000);
        record.Feed(FightActions.BlockHigh, FightActions.None, 5_010_000);
        Assert.Equal(0, record.Reached);
        Assert.Equal(0, record.LateBlocks);
        Assert.Equal(0, record.NoLateBlock);
        Assert.Equal(0, record.Ended(GraceOutcome.TimedOut));
        Assert.Equal(1, record.Presses.PressesOf(FightActions.BlockHigh));
    }
}
