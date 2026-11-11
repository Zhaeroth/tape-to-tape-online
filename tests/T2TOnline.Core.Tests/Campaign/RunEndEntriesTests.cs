using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// What a complete run's end carries to friends: the squad record's best scorer, the run's end for the
/// benchwarmers, the Nurse's offers and the NPC lines seen, with their limits.
/// </summary>
public class RunEndEntriesTests
{
    [Fact]
    public void A_squad_records_best_scorer_is_none_or_a_short_name_with_goals()
    {
        Assert.True(ProgressLimits.IsValid(new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f)));
        Assert.True(ProgressLimits.IsValid(new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, "Kovalski", 1)));
        Assert.True(ProgressLimits.IsValid(new SquadEntry(SquadOutcome.Lost, "gauntlet", 3, 10f, "Zoë Brûlé", ProgressLimits.MaxRunGoals)));
        ProgressEntry[] bad =
        {
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, "Kovalski", 0), // a scorer who didn't score
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, string.Empty, 4), // goals without a scorer
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, "Kovalski", ProgressLimits.MaxRunGoals + 1),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, "Kovalski", -1),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, "Ko\nvalski", 2),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, new string('é', 33), 2), // 66 UTF-8 bytes
            new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 10f, null!, 0),
        };
        Assert.All(bad, e => Assert.False(ProgressLimits.IsValid(e), e.ToString()));
    }

    [Fact]
    public void Profile_marks_are_a_known_procedure_or_a_plain_dialogue_id()
    {
        Assert.True(ProgressLimits.IsValid(ProfileMarkEntry.NurseOffered(0)));
        Assert.True(ProgressLimits.IsValid(ProfileMarkEntry.NurseOffered(ProgressLimits.MaxProcedure)));
        Assert.True(ProgressLimits.IsValid(ProfileMarkEntry.DialogueSeen("coachNode")));
        ProgressEntry[] bad =
        {
            ProfileMarkEntry.NurseOffered(-1),
            ProfileMarkEntry.NurseOffered(ProgressLimits.MaxProcedure + 1),
            new ProfileMarkEntry(ProfileMark.NurseOffered, 1, "coachNode"),
            new ProfileMarkEntry(ProfileMark.DialogueSeen, 1, "coachNode"),
            ProfileMarkEntry.DialogueSeen(string.Empty),
            ProfileMarkEntry.DialogueSeen("two words"),
            ProfileMarkEntry.DialogueSeen(new string('a', ProgressLimits.MaxIdBytes + 1)),
            new ProfileMarkEntry(ProfileMark.None, 0, string.Empty),
            new ProfileMarkEntry((ProfileMark)3, 0, string.Empty),
            new ProfileMarkEntry(ProfileMark.DialogueSeen, 0, null!),
        };
        Assert.All(bad, e => Assert.False(ProgressLimits.IsValid(e), e.ToString()));
        Assert.Equal("Nurse procedure 4 offered", ProfileMarkEntry.NurseOffered(4).Describe());
        Assert.Equal("dialogue chaosNode/visit_1 seen", ProfileMarkEntry.DialogueSeen("chaosNode/visit_1").Describe());
    }

    [Fact]
    public void A_batch_holds_one_runs_end_and_so_many_marks()
    {
        Assert.Throws<ArgumentException>(() => new ProgressBatch(1, 0, "run-1", new ProgressEntry[] { new RunEndingEntry(), new RunEndingEntry() }));
        var marks = Enumerable.Range(0, ProgressLimits.MaxMarksPerBatch).Select(i => (ProgressEntry)ProfileMarkEntry.NurseOffered(i % 10)).ToList();
        Assert.True(ProgressLimits.IsWithinBatchLimits(marks));
        marks.Add(ProfileMarkEntry.DialogueSeen("coachNode"));
        Assert.False(ProgressLimits.IsWithinBatchLimits(marks));
        Assert.Throws<ArgumentException>(() => new ProgressBatch(1, 0, "run-1", marks));

        var batch = new ProgressBatch(1, 0, "run-1", new ProgressEntry[] { new RunEndingEntry(), ProfileMarkEntry.NurseOffered(1), ProfileMarkEntry.DialogueSeen("a") });
        Assert.Equal("0 stat operations, 0 unlock checks, the run's end for the benchwarmers, 2 profile marks", batch.Describe());
    }

    [Fact]
    public void The_batcher_seals_before_a_second_runs_end_and_before_too_many_marks()
    {
        var batcher = new ProgressBatcher();
        Assert.True(batcher.Add(new RunEndingEntry(), "run-1", 0));
        Assert.True(batcher.Add(new RunEndingEntry(), "run-1", 0)); // the title's abandon after the run's end, say
        for (int i = 0; i < ProgressLimits.MaxMarksPerBatch + 1; i++)
        {
            Assert.True(batcher.Add(ProfileMarkEntry.NurseOffered(i % 10), "run-1", 0));
        }

        batcher.Seal("test");
        var reasons = new List<string>();
        while (batcher.TryTake(0, live: false, out SealedProgress sealedBatch))
        {
            reasons.Add(sealedBatch.Reason);
            Assert.True(ProgressLimits.IsWithinBatchLimits(sealedBatch.Entries)); // every one makes a batch
            _ = new ProgressBatch(1, 0, sealedBatch.Run, sealedBatch.Entries);
        }

        Assert.Equal(new[] { "another run's end", "the profile marks filled the batch", "test" }, reasons);
    }

    [Fact]
    public void The_runs_best_scorer_is_the_first_with_the_most_goals_as_the_game_takes_it()
    {
        Assert.Equal(("Brewster", 5), RunScorers.Best(new (string?, int)[] { ("Kovalski", 3), ("Brewster", 5), ("Sally", 5), ("Kirby", 1) }, out string problem));
        Assert.Empty(problem);
        Assert.Equal((string.Empty, 0), RunScorers.Best(new (string?, int)[] { ("Kovalski", 0), ("Brewster", 0) }, out _)); // nobody scored
        Assert.Equal((string.Empty, 0), RunScorers.Best(Array.Empty<(string?, int)>(), out _));
        Assert.Equal(("Kovalski", ProgressLimits.MaxRunGoals), RunScorers.Best(new (string?, int)[] { ("Kovalski", 5000) }, out _));

        // A name the record can't carry: none, and why.
        Assert.Equal((string.Empty, 0), RunScorers.Best(new (string?, int)[] { ("Bad\tname", 4) }, out problem));
        Assert.Contains("can't carry", problem, StringComparison.Ordinal);
        Assert.Equal((string.Empty, 0), RunScorers.Best(new (string?, int)[] { (null, 4) }, out problem));
        Assert.NotEmpty(problem);
    }

    [Theory]
    [InlineData("coachNode", true, "coachNode")]
    [InlineData("chaosNode/visit_1", true, "chaosNode")]
    [InlineData("chaosNode/visit_0", true, "chaosNode")] // a random line's mark
    [InlineData("chaosNode/visit_16", true, "chaosNode")]
    [InlineData("fan-number.1_node/visit_9999", true, "fan-number.1_node")]
    [InlineData("chaosNode/run_0a1b2c3d-1234", false, "")] // a run's mark: only means something in that run
    [InlineData("chaosNode/visit_", false, "")]
    [InlineData("chaosNode/visit_12345", false, "")]
    [InlineData("chaosNode/visit_1x", false, "")]
    [InlineData("chaosNode/visit_1/visit_2", false, "")]
    [InlineData("/visit_1", false, "")]
    [InlineData("tip:first goal", false, "")]
    [InlineData("tip:firstGoal", false, "")]
    [InlineData("", false, "")]
    public void Only_an_event_NPC_lines_seen_id_is_marked(string id, bool line, string pool)
    {
        Assert.Equal(line, ProfileMarks.TryReadNpcLine(id, out string read));
        Assert.Equal(pool, read);
        Assert.Equal(line, ProfileMarks.Refusal(ProfileMarkEntry.DialogueSeen(id)) == null);
    }

    [Fact]
    public void A_line_belongs_to_its_own_pool_only()
    {
        Assert.True(ProfileMarks.IsLineOf("chaosNode/visit_2", "chaosNode"));
        Assert.True(ProfileMarks.IsLineOf("chaosNode", "chaosNode"));
        Assert.False(ProfileMarks.IsLineOf("chaosNode/visit_2", "coachNode"));
        Assert.False(ProfileMarks.IsLineOf("chaosNode/run_x", "chaosNode"));
        Assert.Null(ProfileMarks.Refusal(ProfileMarkEntry.NurseOffered(3)));
        Assert.Contains("isn't an event NPC's line", ProfileMarks.Refusal(ProfileMarkEntry.DialogueSeen("tip:firstGoal")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_runs_end_is_run_bound_and_worth_holding_the_run_for_and_marks_are_the_profiles()
    {
        Assert.True(ProgressApplyPolicy.IsRunBound(new RunEndingEntry()));
        Assert.False(ProgressApplyPolicy.IsRunBound(ProfileMarkEntry.NurseOffered(1)));
        Assert.True(ProgressApplyPolicy.HoldsRun(new ProgressBatch(1, 0, "run-1", new ProgressEntry[] { new RunEndingEntry() })));
        Assert.False(ProgressApplyPolicy.HoldsRun(new ProgressBatch(1, 0, "run-1", new ProgressEntry[] { ProfileMarkEntry.NurseOffered(1) })));

        var can = new ApplyAbilities(true, true, true, true);
        Assert.Equal(EntryVerdict.SkipNoRun, ProgressApplyPolicy.For(new RunEndingEntry(), RunContext.None, can));
        Assert.Equal(EntryVerdict.Apply, ProgressApplyPolicy.For(new RunEndingEntry(), RunContext.Kept, can));
        Assert.Equal(EntryVerdict.Apply, ProgressApplyPolicy.For(ProfileMarkEntry.DialogueSeen("coachNode"), RunContext.None, default));
    }
}
