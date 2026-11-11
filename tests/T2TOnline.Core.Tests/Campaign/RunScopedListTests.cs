using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class RunScopedListTests
{
    private static RunScopedList<string> List(int capacity = RunScopedList<string>.DefaultCapacity) => new(capacity, StringComparer.Ordinal);

    [Fact]
    public void A_runs_items_show_on_its_screen()
    {
        RunScopedList<string> list = List();
        Assert.Equal(RunScopedAdd.Added, list.Add("relic-a", "run-1", out int dropped));
        Assert.Equal(RunScopedAdd.Added, list.Add("squad-b", "run-1", out dropped));
        Assert.Equal(0, dropped);
        Assert.Equal("run-1", list.Run);
        Assert.Equal(new[] { "relic-a", "squad-b" }, list.Peek("run-1"));
        Assert.Equal(new[] { "relic-a", "squad-b" }, list.Take("run-1", out dropped));
        Assert.Equal(0, dropped);
        Assert.Equal(2, list.Count); // taking shows them; the screen's close clears them
        list.Clear();
        Assert.Empty(list.Take("run-1", out _));
        Assert.Equal("run-1", list.Run);
    }

    [Fact]
    public void A_run_brought_back_within_a_session_has_its_turn_again()
    {
        RunScopedList<string> list = List();
        list.Add("relic-a", "run-1", out _);
        list.Add("relic-b", "run-2", out _); // the host went to another slot: run 1's turn is over
        Assert.Equal(RunScopedAdd.EarlierRun, list.Add("relic-late", "run-1", out _));

        // The host's run 1 again (this PC loaded its checkpoint): its unlocks are kept and show on its screen.
        Assert.True(list.BroughtBack("run-1", out int dropped));
        Assert.Equal(1, dropped); // run 2's relic-b
        Assert.Equal("run-1", list.Run);
        Assert.Equal(RunScopedAdd.Added, list.Add("relic-c", "run-1", out _));
        Assert.Equal(new[] { "relic-c" }, list.Take("run-1", out _));

        // Run 2's turn is over now, and a late copy of its progress is refused.
        Assert.Equal(RunScopedAdd.EarlierRun, list.Add("relic-d", "run-2", out _));
    }

    [Theory]
    [InlineData("run-1")] // the current run
    [InlineData("run-3")] // one never seen
    [InlineData("")]
    [InlineData(null)]
    public void Only_an_earlier_run_can_be_brought_back(string? run)
    {
        RunScopedList<string> list = List();
        list.Add("relic-a", "run-1", out _);
        Assert.False(list.BroughtBack(run, out int dropped));
        Assert.Equal(0, dropped);
        Assert.Equal("run-1", list.Run);
        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void An_earlier_runs_items_dont_carry_to_a_later_runs_screen()
    {
        // Run 1's screen never opened (the host conceded): its unlocks stay on the profile, not on run 2's screen.
        RunScopedList<string> list = List();
        list.Add("relic-a", "run-1", out _);
        list.Add("relic-b", "run-1", out _);
        Assert.Equal(RunScopedAdd.Added, list.Add("relic-c", "run-2", out int dropped));
        Assert.Equal(2, dropped);
        Assert.Equal(new[] { "relic-c" }, list.Take("run-2", out dropped));
        Assert.Equal(0, dropped);

        // A screen opening for a run with nothing noted yet drops the earlier run's too.
        list.Add("relic-d", "run-2", out _);
        Assert.Empty(list.Peek("run-3"));
        Assert.Empty(list.Take("run-3", out dropped));
        Assert.Equal(2, dropped);
        Assert.Equal("run-3", list.Run);
    }

    [Fact]
    public void Items_waiting_for_their_run_take_the_next_one()
    {
        // The squad menu grants at the run's start, before the run's id is known: they're the next run's.
        RunScopedList<string> list = List();
        list.Add("relic-a", "run-1", out _);
        Assert.Equal(RunScopedAdd.Added, list.Add("squad-grant", null, out int dropped));
        Assert.Equal(0, dropped);
        Assert.Equal(new[] { "relic-a", "squad-grant" }, list.Peek("run-1")); // until the next run is noted, they'd show with this one
        Assert.Equal(RunScopedAdd.Added, list.Add("relic-b", "run-2", out dropped));
        Assert.Equal(1, dropped); // run 1's relic-a
        Assert.Equal(new[] { "squad-grant", "relic-b" }, list.Take("run-2", out _));

        // At a screen: the waiting items are the screen's run's.
        list.Clear();
        list.Add("grant-2", string.Empty, out _);
        Assert.Equal(new[] { "grant-2" }, list.Take("run-3", out dropped));
        Assert.Equal(0, dropped);
        Assert.Equal("run-3", list.Run);
    }

    [Fact]
    public void An_earlier_runs_late_items_are_refused()
    {
        // A late copy of run 1's progress applied during run 2: its unlocks don't take run 2's screen over.
        RunScopedList<string> list = List();
        list.Add("relic-a", "run-1", out _);
        list.Add("relic-b", "run-2", out _);
        list.Add("grant", null, out _);
        Assert.Equal(RunScopedAdd.EarlierRun, list.Add("relic-late", "run-1", out int dropped));
        Assert.Equal(0, dropped);
        Assert.Equal(new[] { "relic-b", "grant" }, list.Peek("run-2"));
        Assert.Empty(list.Peek("run-1"));
        Assert.Empty(list.Take("run-1", out dropped)); // an earlier run's screen gets none, and changes nothing
        Assert.Equal(0, dropped);
        Assert.Equal("run-2", list.Run);
    }

    [Fact]
    public void Without_a_known_run_every_item_shows()
    {
        RunScopedList<string> list = List();
        list.Add("relic-a", "run-1", out _);
        list.Add("grant", null, out _);
        Assert.Equal(new[] { "relic-a", "grant" }, list.Peek(null));
        Assert.Equal(new[] { "relic-a", "grant" }, list.Take(string.Empty, out int dropped));
        Assert.Equal(0, dropped);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Each_item_is_kept_once_and_the_list_has_a_cap()
    {
        RunScopedList<string> list = List(capacity: 3);
        Assert.Equal(RunScopedAdd.Added, list.Add("a", "run-1", out _));
        Assert.Equal(RunScopedAdd.AlreadyNoted, list.Add("a", "run-1", out _));
        Assert.Equal(RunScopedAdd.AlreadyNoted, list.Add("a", null, out _));
        list.Add("b", "run-1", out _);
        list.Add("c", null, out _);
        Assert.Equal(RunScopedAdd.Full, list.Add("d", "run-1", out _));
        Assert.Equal(3, list.Count);

        // A new run's turn makes room (the earlier run's go).
        Assert.Equal(RunScopedAdd.Added, list.Add("d", "run-2", out int dropped));
        Assert.Equal(2, dropped);
        Assert.Equal(new[] { "c", "d" }, list.Take("run-2", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunScopedList<string>(0));
    }

    [Fact]
    public void Only_so_many_earlier_runs_are_remembered_and_a_reset_forgets_them()
    {
        RunScopedList<string> list = List();
        for (int run = 0; run <= RunScopedList<string>.MaxEarlierRuns + 1; run++)
        {
            Assert.Equal(RunScopedAdd.Added, list.Add($"u{run}", $"run-{run}", out _));
        }

        // The oldest is forgotten (its late items would be taken as a new run's); the next ones are still refused.
        Assert.Equal(RunScopedAdd.EarlierRun, list.Add("late", "run-1", out _));
        Assert.Equal(RunScopedAdd.Added, list.Add("late", "run-0", out _));

        list.Reset();
        Assert.Null(list.Run);
        Assert.Equal(0, list.Count);
        Assert.Equal(RunScopedAdd.Added, list.Add("again", "run-1", out _));
        Assert.Equal(new[] { "again" }, list.Peek("run-1"));
    }
}
