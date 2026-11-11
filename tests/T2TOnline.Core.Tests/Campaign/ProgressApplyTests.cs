using System;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A friend's decisions about the host's batches, apart from the game.</summary>
public class ProgressApplyTests
{
    private const string RunA = "run-a";
    private const string RunB = "run-b";
    private static readonly StatEntry Goal = new(9, StatOp.IncrementCampaign, StatValue.None);
    private static readonly ApplyAbilities Everything = new(true, true, true, true);

    private static ProgressBatch RunEnd(string run = RunA) => new(5, 3, run, new ProgressEntry[]
    {
        Goal, new UnlockPassEntry(2, 8, true, Array.Empty<string>()), new QuestEntry(QuestStep.RunWon, 0),
        new SquadEntry(SquadOutcome.Won, "gauntlet", 2, 1800f), new ComicsEntry(),
    });

    private static ApplyFacts Facts(string held = "", string view = "", bool ending = false, ApplyBlock block = ApplyBlock.None,
        bool sharing = true, string[]? kept = null) => new(sharing, block, ending, held, view, kept ?? Array.Empty<string>());

    [Fact]
    public void Nothing_applies_with_sharing_off_or_at_an_unsafe_moment()
    {
        var policy = new ProgressApplyPolicy();
        Assert.Equal("ShareProgress is off on this PC", policy.Decide(RunEnd(), Facts(RunA, sharing: false), 0).Why);
        foreach (ApplyBlock block in Enum.GetValues<ApplyBlock>().Where(b => b != ApplyBlock.None))
        {
            BatchDecision decision = policy.Decide(RunEnd(), Facts(RunA, block: block), 0);
            Assert.Equal(BatchVerdict.Wait, decision.Verdict);
            Assert.Equal(ProgressApplyPolicy.Describe(block), decision.Why);
        }
    }

    [Fact]
    public void As_a_session_ends_the_hosts_match_and_run_loads_no_longer_hold_a_batch_back_but_unloaded_saves_do()
    {
        var policy = new ProgressApplyPolicy();
        foreach (ApplyBlock block in new[] { ApplyBlock.PlayingHostMatch, ApplyBlock.HostMatchHere, ApplyBlock.HostRunLoading })
        {
            Assert.Equal(BatchDecision.Apply(RunContext.Held), policy.Decide(RunEnd(), Facts(RunA, ending: true, block: block), 0));
        }

        foreach (ApplyBlock block in new[] { ApplyBlock.NotLoaded, ApplyBlock.RestartPending })
        {
            Assert.Equal(BatchVerdict.Wait, policy.Decide(RunEnd(), Facts(RunA, ending: true, block: block), 0).Verdict);
        }
    }

    [Fact]
    public void Once_the_hosts_run_is_over_its_last_match_still_loaded_here_holds_nothing_back()
    {
        var policy = new ProgressApplyPolicy();
        foreach (ApplyBlock block in new[] { ApplyBlock.PlayingHostMatch, ApplyBlock.HostMatchHere })
        {
            Assert.Equal(BatchVerdict.Wait, policy.Decide(RunEnd(), Facts(RunA, block: block), 0).Verdict);
            Assert.True(ProgressApplyPolicy.LiftedAtRunEnd(block, hostRunOver: true));
            Assert.Equal(BatchDecision.Apply(RunContext.Held), policy.Decide(RunEnd(), Facts(RunA, block: block) with { HostRunOver = true }, 0));
        }

        // A run loading here, or this game's own saves not loaded or waiting for a restart, still hold it back.
        foreach (ApplyBlock block in new[] { ApplyBlock.HostRunLoading, ApplyBlock.NotLoaded, ApplyBlock.RestartPending })
        {
            Assert.False(ProgressApplyPolicy.LiftedAtRunEnd(block, hostRunOver: true));
            BatchDecision decision = policy.Decide(RunEnd(), Facts(RunA, block: block) with { HostRunOver = true }, 0);
            Assert.Equal((BatchVerdict.Wait, ProgressApplyPolicy.Describe(block)), (decision.Verdict, decision.Why));
        }

        Assert.False(ProgressApplyPolicy.LiftedAtRunEnd(ApplyBlock.PlayingHostMatch, hostRunOver: false));
        Assert.Equal("ShareProgress is off on this PC", policy.Decide(RunEnd(), Facts(RunA, block: ApplyBlock.PlayingHostMatch, sharing: false) with { HostRunOver = true }, 0).Why);
    }

    [Fact]
    public void The_hosts_match_is_said_first_while_it_holds_batches_back_and_this_games_saves_first_once_it_doesnt()
    {
        int asked = 0;
        Func<bool> No() => () => { asked++; return false; };
        Func<bool> Yes() => () => { asked++; return true; };

        // In a match: the match, without a game call.
        Assert.Equal(ApplyBlock.PlayingHostMatch, ProgressApplyPolicy.BlockOf(true, true, false, false, Yes(), Yes(), Yes()));
        Assert.Equal(ApplyBlock.HostMatchHere, ProgressApplyPolicy.BlockOf(false, true, false, false, Yes(), Yes(), Yes()));
        Assert.Equal(0, asked);

        // The host's run is over, or the session ends, while the match is still here: this game's own saves come first.
        Assert.Equal(ApplyBlock.NotLoaded, ProgressApplyPolicy.BlockOf(true, true, false, true, No(), Yes(), No()));
        Assert.Equal(ApplyBlock.RestartPending, ProgressApplyPolicy.BlockOf(true, false, true, false, No(), No(), Yes()));
        Assert.Equal(ApplyBlock.HostRunLoading, ProgressApplyPolicy.BlockOf(true, false, false, true, Yes(), No(), No()));
        Assert.Equal(ApplyBlock.PlayingHostMatch, ProgressApplyPolicy.BlockOf(true, true, false, true, No(), No(), No()));
        Assert.Equal(ApplyBlock.HostMatchHere, ProgressApplyPolicy.BlockOf(false, true, true, false, No(), No(), No()));

        // Out of the match: as before.
        Assert.Equal(ApplyBlock.None, ProgressApplyPolicy.BlockOf(false, false, false, false, No(), No(), No()));
        Assert.Equal(ApplyBlock.NotLoaded, ProgressApplyPolicy.BlockOf(false, false, false, false, No(), Yes(), Yes()));

        // What the policy then makes of them: a lifted match applies, this game's saves never do.
        var policy = new ProgressApplyPolicy();
        ApplyBlock over = ProgressApplyPolicy.BlockOf(true, true, false, true, No(), No(), No());
        Assert.Equal(BatchVerdict.Apply, policy.Decide(RunEnd(), Facts(RunA, block: over) with { HostRunOver = true }, 0).Verdict);
        ApplyBlock restoring = ProgressApplyPolicy.BlockOf(true, true, true, false, No(), No(), Yes());
        Assert.Equal(BatchVerdict.Wait, policy.Decide(RunEnd(), Facts(RunA, ending: true, block: restoring), 0).Verdict);
    }

    [Fact]
    public void A_batch_without_run_bound_parts_applies_whatever_run_is_held()
    {
        var policy = new ProgressApplyPolicy();
        var stats = new ProgressBatch(1, 0, RunA, new ProgressEntry[] { Goal });
        Assert.Equal(BatchDecision.Apply(RunContext.None), policy.Decide(stats, Facts(RunB, view: RunA), 0));
    }

    [Fact]
    public void A_runs_end_applies_on_the_run_held_or_on_this_pcs_kept_copy_in_every_order_of_events()
    {
        var policy = new ProgressApplyPolicy();

        // The run is still held (the title follower waited for it).
        Assert.Equal(RunContext.Held, policy.Decide(RunEnd(), Facts(RunA, view: string.Empty, kept: new[] { RunA }), 0).Context);

        // Another view loaded the next run's checkpoint first.
        Assert.Equal(RunContext.Kept, policy.Decide(RunEnd(), Facts(RunB, view: RunB, kept: new[] { RunA, RunB }), 0).Context);

        // The session ended and this PC's own run is back (or nothing is held).
        Assert.Equal(RunContext.Kept, policy.Decide(RunEnd(), Facts(string.Empty, ending: true, kept: new[] { RunA }), 0).Context);

        // A rejoin of the same hosting session, back on this PC's title screen, sends it again.
        Assert.Equal(RunContext.Kept, policy.Decide(RunEnd(), Facts(string.Empty, view: string.Empty, kept: new[] { RunA }), 0).Context);

        // Only after this game restarted (no copy kept) are its run-bound parts lost, and the line says why.
        BatchDecision lost = policy.Decide(RunEnd(), Facts("own-run", view: string.Empty), 0);
        Assert.Equal((BatchVerdict.Apply, RunContext.None), (lost.Verdict, lost.Context));
        Assert.Contains("no copy of it", lost.Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_for_the_run_the_host_is_on_waits_for_it_to_load_then_applies_without_it()
    {
        var policy = new ProgressApplyPolicy();
        ProgressBatch batch = RunEnd(RunB);
        Assert.Equal("the host's run it's for isn't loaded here yet", policy.Decide(batch, Facts(RunA, view: RunB), 0).Why);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(RunA, view: RunB), ProgressApplyPolicy.RunWaitMicros - 1).Verdict);
        Assert.Equal(RunContext.Held, policy.Decide(batch, Facts(RunB, view: RunB), ProgressApplyPolicy.RunWaitMicros - 1).Context);

        BatchDecision late = policy.Decide(batch, Facts(RunA, view: RunB), ProgressApplyPolicy.RunWaitMicros); // a new wait
        Assert.Equal(BatchVerdict.Wait, late.Verdict);
        BatchDecision given = policy.Decide(batch, Facts(RunA, view: RunB), 2 * ProgressApplyPolicy.RunWaitMicros);
        Assert.Equal((BatchVerdict.Apply, RunContext.None), (given.Verdict, given.Context));
        Assert.Contains("didn't load here within 20 s", given.Skipped, StringComparison.Ordinal);

        // Not as a session ends, and not for a batch whose only run-bound part is the comics.
        Assert.Equal(BatchVerdict.Apply, policy.Decide(batch, Facts(RunA, view: RunB, ending: true), 0).Verdict);
        var comics = new ProgressBatch(6, 0, RunB, new ProgressEntry[] { new ComicsEntry() });
        Assert.Equal(BatchVerdict.Apply, new ProgressApplyPolicy().Decide(comics, Facts(RunA, view: RunB), 0).Verdict);
    }

    [Fact]
    public void A_batch_from_outside_a_run_skips_its_run_bound_parts()
    {
        BatchDecision decision = new ProgressApplyPolicy().Decide(RunEnd(string.Empty), Facts(RunA), 0);
        Assert.Equal((BatchVerdict.Apply, RunContext.None), (decision.Verdict, decision.Context));
        Assert.Contains("the host wasn't in a run", decision.Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void Unlock_checks_wait_a_while_for_this_game_to_track_stats_then_are_skipped()
    {
        var policy = new ProgressApplyPolicy();
        Assert.False(policy.WaitsForGate(tracking: true, ending: false, 0, out bool skip));
        Assert.False(skip);
        Assert.True(policy.WaitsForGate(tracking: false, ending: false, 0, out _));
        Assert.True(policy.WaitsForGate(tracking: false, ending: false, ProgressApplyPolicy.GateWaitMicros - 1, out _));
        Assert.False(policy.WaitsForGate(tracking: false, ending: false, ProgressApplyPolicy.GateWaitMicros, out skip));
        Assert.True(skip);
        Assert.False(policy.WaitsForGate(tracking: false, ending: true, 0, out skip)); // no waiting as a session ends
        Assert.True(skip);
    }

    [Fact]
    public void Which_entries_apply_in_which_context()
    {
        ProgressEntry[] entries =
        {
            Goal, new StatTextEntry(22, 21, "Moose", true), EntityStatsTests.Skater(), new RunHistoryRecord(new RunHistoryData { RunId = RunA }),
            new UnlockPassEntry(2, 8, true, Array.Empty<string>()), new QuestEntry(QuestStep.RunWon, 0),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 2, 1f), new ComicsEntry(),
        };

        Assert.All(entries, e => Assert.Equal(EntryVerdict.Apply, ProgressApplyPolicy.For(e, RunContext.Held, Everything)));
        Assert.All(entries, e => Assert.Equal(EntryVerdict.Apply, ProgressApplyPolicy.For(e, RunContext.Kept, Everything)));
        Assert.Equal(new[] { true, true, true, true, false, false, false, false },
            entries.Select(e => ProgressApplyPolicy.For(e, RunContext.None, Everything) == EntryVerdict.Apply));

        Assert.Equal(EntryVerdict.SkipNoStats, ProgressApplyPolicy.For(Goal, RunContext.Held, Everything with { Stats = false }));
        Assert.Equal(EntryVerdict.SkipChecksOff, ProgressApplyPolicy.For(entries[4], RunContext.Held, Everything with { Checks = false }));
        Assert.Equal(EntryVerdict.SkipNoUnlocks, ProgressApplyPolicy.For(entries[4], RunContext.Held, Everything with { Unlocks = false }));
        Assert.Equal(EntryVerdict.SkipNoQuests, ProgressApplyPolicy.For(entries[5], RunContext.Held, Everything with { Quests = false }));
        Assert.All(Enum.GetValues<EntryVerdict>().Where(v => v is not EntryVerdict.Apply and not EntryVerdict.SkipNoRun),
            v => Assert.NotEmpty(ProgressApplyPolicy.Note(v)));
    }

    [Fact]
    public void A_stat_operation_never_wraps_a_whole_number_nor_takes_a_value_below_zero()
    {
        var add = new StatEntry(9, StatOp.IncrementCampaignBy, StatValue.Whole(5));
        var takeBack = new StatEntry(10, StatOp.IncrementBy, StatValue.Whole(-1));
        Assert.Equal(StatVerdict.Apply, ProgressApplyPolicy.Check(add, 10, whole: true));
        Assert.Equal(StatVerdict.Overflow, ProgressApplyPolicy.Check(add, int.MaxValue - 4, whole: true));
        Assert.Equal(StatVerdict.Apply, ProgressApplyPolicy.Check(add, int.MaxValue - 4, whole: false)); // a real number doesn't wrap
        Assert.Equal(StatVerdict.Apply, ProgressApplyPolicy.Check(takeBack, 1, whole: true));
        Assert.Equal(StatVerdict.BelowZero, ProgressApplyPolicy.Check(takeBack, 0, whole: true));
        Assert.Equal(StatVerdict.BelowZero, ProgressApplyPolicy.Check(takeBack, double.NaN, whole: true));

        // A reset and a record add nothing: they're never refused for it (each has its own checks on the wire).
        Assert.Equal(StatVerdict.Apply, ProgressApplyPolicy.Check(new StatEntry(19, StatOp.ResetCampaign, StatValue.None), 7, whole: true));
        Assert.Equal(StatVerdict.Apply, ProgressApplyPolicy.Check(new StatEntry(20, StatOp.TrySetLargerFromStat, StatValue.Whole(19)), int.MaxValue, whole: true));
    }

    [Fact]
    public void A_reset_changes_the_lifetime_value_too_and_derived_stats_follow_the_values_changed()
    {
        Assert.Equal(new[] { (19, StatBucket.Campaign), (19, StatBucket.Lifetime) },
            ProgressApplyPolicy.Changes(new StatEntry(19, StatOp.ResetCampaign, StatValue.None)));
        Assert.Equal(new[] { (2, StatBucket.Campaign) }, ProgressApplyPolicy.Changes(new StatEntry(2, StatOp.IncrementCampaign, StatValue.None)));
        Assert.Equal(new[] { (2, StatBucket.Lifetime) }, ProgressApplyPolicy.Changes(new StatEntry(2, StatOp.Increment, StatValue.None)));

        // A lost match: GamesLost goes up for both values, so the W-L record is worked out again for both.
        var changed = new[] { new StatEntry(2, StatOp.Increment, StatValue.None), new StatEntry(2, StatOp.IncrementCampaign, StatValue.None) }
            .SelectMany(ProgressApplyPolicy.Changes);
        Assert.Equal(new[] { (3, StatBucket.Lifetime), (3, StatBucket.Campaign) }, DerivedStats.Affected(changed).Select(a => (a.Stat.Stat, a.Bucket)));
    }

    [Fact]
    public void A_run_history_entry_is_saved_only_for_the_batchs_run_and_one_this_pc_held()
    {
        var entry = new RunHistoryData { RunId = RunA };
        Assert.Null(ProgressApplyPolicy.HistoryRefusal(entry, RunA, runSeen: true));
        Assert.NotNull(ProgressApplyPolicy.HistoryRefusal(entry, RunB, runSeen: true));
        Assert.NotNull(ProgressApplyPolicy.HistoryRefusal(entry, string.Empty, runSeen: true));
        Assert.Contains("never held", ProgressApplyPolicy.HistoryRefusal(entry, RunA, runSeen: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_holder_follows_this_profiles_own_lifetime_record()
    {
        var watch = new ProgressRecordWatch();
        var holder = new StatTextEntry(ProgressGameStats.TeamWithMostGoalsInOneGame, ProgressGameStats.MostGoalsInOneGame, "Moose", true);
        Assert.False(watch.Holds(holder)); // no record operation yet
        watch.Applied(new StatEntry(21, StatOp.TrySetLargerCampaign, StatValue.Whole(7)), rose: true); // the campaign record doesn't name it
        Assert.False(watch.Holds(holder));
        watch.Applied(new StatEntry(21, StatOp.TrySetLarger, StatValue.Whole(7)), rose: false); // this profile's is higher
        Assert.False(watch.Holds(holder));
        watch.Applied(new StatEntry(21, StatOp.TrySetLarger, StatValue.Whole(9)), rose: true);
        Assert.True(watch.Holds(holder));
        watch.Clear();
        Assert.False(watch.Holds(holder));
    }

    [Fact]
    public void Batches_apply_at_most_a_frames_worth_and_more_as_a_session_ends()
    {
        Assert.True(ProgressApplyPolicy.MaxPerFrame < ProgressApplyPolicy.MaxAtEnd);
        Assert.True(ProgressApplyPolicy.HoldsRun(RunEnd()));
        Assert.False(ProgressApplyPolicy.HoldsRun(new ProgressBatch(1, 0, RunA, new ProgressEntry[] { Goal, new ComicsEntry() })));
        Assert.False(ProgressApplyPolicy.HoldsRun(RunEnd(string.Empty)));
        Assert.Equal(new[] { false, true, true, true, true }, RunEnd().Entries.Select(ProgressApplyPolicy.IsRunBound));
    }
}
