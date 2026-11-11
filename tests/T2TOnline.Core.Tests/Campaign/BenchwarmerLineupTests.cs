using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>The lineup with benchwarmers on a friend: following the host's cards, and the check after its first checkpoint.</summary>
public class BenchwarmerLineupTests
{
    // Five starter seats (two empty: the squad has three skaters plus the superstar), four bench seats, nine benchwarmers.
    private static readonly string[] Opened =
    {
        "ss", "c1", "c2", string.Empty, string.Empty,
        "b1", string.Empty, string.Empty, string.Empty,
        "boring", "brewster", "kovalski", "mark", "cassoulet", "kirby", "stumple", "johnny", "sally",
    };

    private static readonly BenchwarmerCardCounts Counts = new(5, 4, 9);

    private static string[] Apply(IReadOnlyList<string> start, IReadOnlyList<(int A, int B)> swaps)
    {
        string[] now = start.ToArray();
        foreach ((int a, int b) in swaps)
        {
            (now[a], now[b]) = (now[b], now[a]);
        }

        return now;
    }

    [Fact]
    public void A_benchwarmer_put_in_an_empty_starter_seat_is_the_host_s_one_swap()
    {
        // The host's add (AddPlayerToActive: a swap of the benchwarmer's card and the first empty seat).
        string[] host = Opened.ToArray();
        (host[3], host[11]) = (host[11], host[3]);
        IReadOnlyList<(int A, int B)>? swaps = BenchwarmerLineup.Swaps(Opened, host);
        Assert.Equal(new[] { (3, 11) }, swaps);
        Assert.Equal(host, Apply(Opened, swaps!));
    }

    [Fact]
    public void The_host_s_random_fill_at_open_is_reached_from_this_PC_s_opening()
    {
        // The host's AutoSelect filled both empty starter seats from the pool before this PC's copy opened.
        string[] host = Opened.ToArray();
        (host[3], host[14]) = (host[14], host[3]);
        (host[4], host[9]) = (host[9], host[4]);
        IReadOnlyList<(int A, int B)>? swaps = BenchwarmerLineup.Swaps(Opened, host);
        Assert.NotNull(swaps);
        Assert.Equal(2, swaps!.Count);
        Assert.Equal(host, Apply(Opened, swaps));
    }

    [Fact]
    public void A_benchwarmer_sent_back_is_a_swap_with_its_pool_card()
    {
        string[] after = Opened.ToArray();
        (after[3], after[11]) = (after[11], after[3]); // kovalski starts
        string[] back = Opened.ToArray();
        IReadOnlyList<(int A, int B)>? swaps = BenchwarmerLineup.Swaps(after, back);
        Assert.Equal(new[] { (3, 11) }, swaps);
    }

    [Fact]
    public void Another_skater_set_can_t_be_followed()
    {
        string[] host = Opened.ToArray();
        host[12] = "someone-else";
        Assert.Null(BenchwarmerLineup.Swaps(Opened, host));
        Assert.Null(BenchwarmerLineup.Swaps(Opened, Opened.Take(10).ToArray()));
    }

    [Fact]
    public void More_cards_than_a_plain_lineup_are_followed_up_to_the_cap()
    {
        string[] many = Enumerable.Range(0, BenchwarmerLineup.MaxCards).Select(i => $"s{i}").ToArray();
        string[] reversed = many.Reverse().ToArray();
        Assert.True(BenchwarmerLineup.MaxCards > LineupOrder.MaxCards);
        Assert.Null(LineupOrder.Swaps(many, reversed));
        IReadOnlyList<(int A, int B)>? swaps = BenchwarmerLineup.Swaps(many, reversed);
        Assert.NotNull(swaps);
        Assert.Equal(reversed, Apply(many, swaps!));

        string[] tooMany = many.Append("one-more").ToArray();
        Assert.Null(BenchwarmerLineup.Swaps(tooMany, tooMany.Reverse().ToArray()));
    }

    [Fact]
    public void The_lists_must_have_as_many_cards_as_the_host_s()
    {
        Assert.Equal(string.Empty, BenchwarmerLineup.Mismatch(Counts, new BenchwarmerCardCounts(5, 4, 9)));
        Assert.Equal("this PC's menu has 5 starter seat(s), 4 bench seat(s) and 8 benchwarmer(s), the host's 5 starter seat(s), 4 bench seat(s) and 9 benchwarmer(s)",
            BenchwarmerLineup.Mismatch(new BenchwarmerCardCounts(5, 4, 8), Counts));

        // Same total, other lists: the places mean other lists, so it can't follow either.
        Assert.NotEqual(string.Empty, BenchwarmerLineup.Mismatch(new BenchwarmerCardCounts(5, 5, 8), Counts));
        Assert.Contains("at most 40", BenchwarmerLineup.Mismatch(new BenchwarmerCardCounts(5, 20, 16), new BenchwarmerCardCounts(5, 20, 16)), StringComparison.Ordinal);
        Assert.Equal(18, Counts.Total);
    }

    [Fact]
    public void The_run_after_the_lineup_has_the_skaters_it_showed_in_any_order()
    {
        string[] shown = { "ss", "c1", "c2", "kovalski", "kirby", "b1", string.Empty };
        string[] team = { "kirby", "ss", "b1", "c2", "kovalski", "c1", null! };
        Assert.Null(BenchwarmerLineup.Difference(shown, team));
        Assert.Null(BenchwarmerLineup.Difference(Array.Empty<string>(), new[] { string.Empty }));
    }

    [Fact]
    public void A_run_with_other_skaters_says_which()
    {
        string[] shown = { "ss", "c1", "kovalski", "kirby" };
        string[] team = { "ss", "c1", "sally", "kirby", "johnny" };
        Assert.Equal("1 the host's lineup showed aren't in the run (kovalski); 2 in the run weren't on it (sally, johnny)", BenchwarmerLineup.Difference(shown, team));
        Assert.Equal(new[] { "kovalski" }, BenchwarmerLineup.Missing(shown, team));

        // Only more in the run: nothing shown is missing (the friend's view showed no skater the run lacks).
        Assert.Empty(BenchwarmerLineup.Missing(new[] { "ss" }, new[] { "ss", "extra" }));
        Assert.Equal("1 in the run weren't on it (extra)", BenchwarmerLineup.Difference(new[] { "ss" }, new[] { "ss", "extra" }));

        // A skater shown twice (it can't be) must be there twice.
        Assert.NotNull(BenchwarmerLineup.Difference(new[] { "a", "a" }, new[] { "a" }));

        // Long lists name a few.
        string? many = BenchwarmerLineup.Difference(Enumerable.Range(0, 9).Select(i => $"x{i}"), Array.Empty<string>());
        Assert.Contains("9 the host's lineup showed", many, StringComparison.Ordinal);
        Assert.EndsWith(", …)", many, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_teams_bench_is_part_of_its_roster_and_counts_once()
    {
        // A run team has no lines: its roster holds the starters, then the bench, and the game's bench is that same part of
        // it. Counting the bench again made the check name a bench skater as one the lineup didn't show.
        string[] shown = { "ss", "c1", "c2", "c3", "c4", "b1", string.Empty, string.Empty, string.Empty };
        string?[] roster = { "ss", "c1", "c2", "c3", "c4", "b1", null, null, null, null };
        Assert.Null(BenchwarmerLineup.Difference(shown, roster));
        Assert.Equal("1 in the run weren't on it (b1)", BenchwarmerLineup.Difference(shown, roster.Concat(new[] { "b1" })));
    }
}
