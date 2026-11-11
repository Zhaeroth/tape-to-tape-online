using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The runs a friend's game leaves unread while a batch waits: only where the policy's decision can't depend on them,
/// and everywhere it can't (a batch whose run was left unread and applied would lose its run-bound parts).
/// </summary>
public class ProgressFactsNeedTests
{
    private const string RunA = "run-a";

    private static ProgressBatch RunEnd() => new(5, 3, RunA, new ProgressEntry[]
    {
        new StatEntry(9, StatOp.IncrementCampaign, StatValue.None), new UnlockPassEntry(2, 8, true, Array.Empty<string>()),
        new SquadEntry(SquadOutcome.Won, "gauntlet", 2, 1800f),
    });

    private static ApplyFacts Facts(bool sharing, ApplyBlock block, bool ending, bool hostRunOver, string run) =>
        new(sharing, block, ending, run, run, Array.Empty<string>(), HostRunOver: hostRunOver);

    [Fact]
    public void The_runs_are_left_unread_exactly_where_the_decision_cant_depend_on_them()
    {
        int unread = 0, read = 0;
        foreach (bool sharing in new[] { true, false })
        {
            foreach (ApplyBlock block in Enum.GetValues<ApplyBlock>())
            {
                foreach (bool ending in new[] { false, true })
                {
                    foreach (bool hostRunOver in new[] { false, true })
                    {
                        BatchDecision withRun = new ProgressApplyPolicy().Decide(RunEnd(), Facts(sharing, block, ending, hostRunOver, RunA), 0);
                        BatchDecision without = new ProgressApplyPolicy().Decide(RunEnd(), Facts(sharing, block, ending, hostRunOver, string.Empty), 0);
                        if (ProgressFactsNeed.WaitsWhateverTheRuns(sharing, block, ending, hostRunOver))
                        {
                            // Unread: the batch waits, for the same reason, with the run or without.
                            Assert.Equal(BatchVerdict.Wait, withRun.Verdict);
                            Assert.Equal(withRun, without);
                            unread++;
                        }
                        else
                        {
                            // Read: the batch applies on the run held, and wouldn't without it.
                            Assert.Equal(BatchDecision.Apply(RunContext.Held), withRun);
                            Assert.NotEqual(withRun, without);
                            read++;
                        }
                    }
                }
            }
        }

        Assert.Equal(48, unread + read);
        Assert.True(unread > 0 && read > 0);
    }

    [Theory]
    [InlineData(false, ApplyBlock.None, false, false, true)] // sharing off: nothing applies
    [InlineData(true, ApplyBlock.None, false, false, false)] // nothing in the way
    [InlineData(true, ApplyBlock.PlayingHostMatch, false, false, true)] // the host's end-of-match screens
    [InlineData(true, ApplyBlock.PlayingHostMatch, false, true, false)] // the host's run is over: its last match holds nothing back
    [InlineData(true, ApplyBlock.HostMatchHere, true, false, false)] // the session is ending
    [InlineData(true, ApplyBlock.NotLoaded, true, true, true)] // this game's saves aren't loaded: never
    [InlineData(true, ApplyBlock.RestartPending, true, false, true)]
    public void The_cases_by_name(bool sharing, ApplyBlock block, bool ending, bool hostRunOver, bool unread)
    {
        Assert.Equal(unread, ProgressFactsNeed.WaitsWhateverTheRuns(sharing, block, ending, hostRunOver));
    }

    [Theory]
    [InlineData(ApplyBlock.None, false, false, false)] // nothing in the way
    [InlineData(ApplyBlock.HostMatchHere, false, false, true)]
    [InlineData(ApplyBlock.HostMatchHere, true, false, false)] // the session's end lifts the host's match
    [InlineData(ApplyBlock.PlayingHostMatch, false, true, false)] // and so does the host's run being over
    [InlineData(ApplyBlock.HostRunLoading, false, true, true)] // but not a run load
    [InlineData(ApplyBlock.HostRunLoading, true, false, false)]
    [InlineData(ApplyBlock.NotLoaded, true, true, true)] // this game's saves not loaded: never lifted
    [InlineData(ApplyBlock.RestartPending, true, true, true)]
    public void The_policys_one_block_rule_by_name(ApplyBlock block, bool ending, bool hostRunOver, bool holds)
    {
        Assert.Equal(holds, ProgressApplyPolicy.BlockHolds(block, ending, hostRunOver));

        // The policy waits for exactly that reason, sharing on.
        BatchDecision decided = new ProgressApplyPolicy().Decide(RunEnd(), Facts(true, block, ending, hostRunOver, RunA), 0);
        Assert.Equal(holds, decided.Verdict == BatchVerdict.Wait);
    }
}
