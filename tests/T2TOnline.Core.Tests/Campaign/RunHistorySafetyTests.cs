using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A host's run history entry on a friend's PC (D42, D135): what the game's history page can't show never reaches the
/// friend's files, and the save may write every slot the game has.
/// </summary>
public class RunHistorySafetyTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_entry_with_its_team_is_kept_and_one_without_is_refused()
    {
        Assert.Null(RunHistorySafety.Refusal(RunHistoryDataTests.Sample()));

        var teamless = new RunHistoryData { RunId = "9f0c2d4e-1b2a-4c3d-8e7f-0123456789ab", SquadId = "gauntlet", TimestampUtc = Now.Ticks };
        string? refusal = RunHistorySafety.Refusal(teamless);
        Assert.NotNull(refusal);
        Assert.Contains("no team", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] // the run ended this moment
    [InlineData(-5)] // five seconds ago
    [InlineData(-3 * 24 * 3600)] // a batch that came again days later
    [InlineData(2 * 3600)] // the host's clock two hours fast
    [InlineData(24 * 3600)] // a day fast, to the tick
    public void An_honest_hosts_time_is_kept(int secondsFromNow)
    {
        long host = Now.Ticks + (secondsFromNow * TimeSpan.TicksPerSecond);
        Assert.Equal(host, RunHistorySafety.Timestamp(host, Now, out bool replaced));
        Assert.False(replaced);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(3_155_378_976_000_000_000L)] // one past DateTime.MaxValue.Ticks
    public void Hostile_times_would_throw_in_the_games_page_and_become_this_pcs_time_now(long host)
    {
        // What the game's history page does with the entry's time (and what made the unchecked value fatal).
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new DateTime(host, DateTimeKind.Utc));

        long kept = RunHistorySafety.Timestamp(host, Now, out bool replaced);
        Assert.True(replaced);
        Assert.Equal(Now.Ticks, kept);
        Assert.Equal(Now, new DateTime(kept, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(long.MaxValue, 3_155_378_975_999_999_999L)]
    [InlineData(long.MinValue, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData(3_155_378_976_000_000_000L, 3_155_378_975_999_999_999L)]
    [InlineData(0L, 0L)]
    [InlineData(638_656_000_000_000_000L, 638_656_000_000_000_000L)]
    public void Every_entry_made_for_the_game_carries_a_date(long host, long expected)
    {
        long ticks = RunHistorySafety.AsDate(host);
        Assert.Equal(expected, ticks);
        Assert.Equal(ticks, new DateTime(ticks, DateTimeKind.Utc).Ticks); // what the game's run-end menu does with it
    }

    [Fact]
    public void A_time_far_ahead_of_this_pcs_clock_would_head_the_list_for_good_and_becomes_now()
    {
        long dayAndATick = Now.Ticks + RunHistorySafety.MostAhead.Ticks + 1;
        Assert.Equal(Now.Ticks, RunHistorySafety.Timestamp(dayAndATick, Now, out bool replaced));
        Assert.True(replaced);

        Assert.Equal(Now.Ticks, RunHistorySafety.Timestamp(DateTime.MaxValue.Ticks, Now, out replaced)); // a date, but year 9999
        Assert.True(replaced);
    }

    [Fact]
    public void The_kept_time_is_a_date_whatever_this_pcs_clock_says()
    {
        foreach (DateTime clock in new[] { DateTime.MinValue, DateTime.MaxValue, new DateTime(DateTime.MaxValue.Ticks - 1, DateTimeKind.Utc), Now })
        {
            foreach (long host in new[] { long.MinValue, -1L, 0L, Now.Ticks, DateTime.MaxValue.Ticks, long.MaxValue })
            {
                long kept = RunHistorySafety.Timestamp(host, clock, out _);
                Assert.InRange(kept, 0, DateTime.MaxValue.Ticks);
            }
        }
    }

    [Fact]
    public void Matches_against_teams_this_game_has_are_all_shown_and_nothing_is_made()
    {
        // An honest host's run: every opponent is one of the game's own teams, which this PC's game has too.
        MatchLine[] matches = { Against("team-a", 1), Against("team-b", 1), Against("team-a", 2) };
        var asked = new List<string>();

        IReadOnlyList<MatchLine> shown = RunHistorySafety.ShownMatches(matches, id =>
        {
            asked.Add(id);
            return true;
        });

        Assert.Same(matches, shown);
        Assert.Equal(new[] { "team-a", "team-b" }, asked); // each team asked about once
        Assert.Same(Array.Empty<MatchLine>(), RunHistorySafety.ShownMatches(Array.Empty<MatchLine>(), _ => false));
    }

    [Fact]
    public void A_match_against_a_team_this_game_lacks_is_left_out_and_the_rest_keep_their_order()
    {
        // The game's page finds each match's opponent among its own teams and reads that team's logo without asking
        // whether there is one: a match naming a team this game doesn't have (or none) would make the entry throw there.
        MatchLine first = Against("team-a", 1), unknown = Against("no-such-team", 1), second = Against("team-b", 2);
        MatchLine unnamed = Against(null, 2), empty = Against(string.Empty, 3), last = Against("team-a", 3);
        var asked = new List<string>();

        IReadOnlyList<MatchLine> shown = RunHistorySafety.ShownMatches(new[] { first, unknown, second, unnamed, empty, unknown, last }, id =>
        {
            asked.Add(id);
            return id is "team-a" or "team-b";
        });

        Assert.Equal(new[] { first, second, last }, shown);
        Assert.Equal(new[] { "team-a", "no-such-team", "team-b" }, asked); // a match naming no team is never asked about

        // A game whose teams can't be read shows none of the matches: the page stays whole.
        Assert.Empty(RunHistorySafety.ShownMatches(new[] { first, second }, _ => false));
    }

    private static MatchLine Against(string? teamId, int act) => new() { TeamId = teamId, Act = act, Score = "3-1", Win = true, SelfScore = 3, OtherScore = 1 };

    [Fact]
    public void Every_slot_the_game_has_is_let_through()
    {
        // The game keeps 500 runs (RunHistoryFiles.MaxRuns): the save may pick any slot of them.
        string[] names = RunHistoryFileNames.All("RunHistoryIndex.es3", 500, slot => $"RunHistory_{slot:D2}.es3");
        Assert.Equal(501, names.Length);
        Assert.Equal("RunHistoryIndex.es3", names[0]);
        Assert.Equal("RunHistory_00.es3", names[1]);
        Assert.Contains("RunHistory_199.es3", names);
        Assert.Contains("RunHistory_200.es3", names);
        Assert.Equal("RunHistory_499.es3", names[^1]);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void When_the_game_cant_say_its_known_names_and_slots_are_used()
    {
        string[] names = RunHistoryFileNames.All(null, RunHistoryFileNames.Slots, null);
        Assert.Equal(RunHistoryFileNames.Slots + 1, names.Length);
        Assert.Equal(RunHistoryFileNames.Index, names[0]);
        Assert.Equal("RunHistory_07.es3", names[8]);
        Assert.Equal("RunHistory_499.es3", names[^1]);

        // A name the game gives none for falls back by itself.
        names = RunHistoryFileNames.All(string.Empty, 3, slot => slot == 1 ? null : $"slot{slot}");
        Assert.Equal(new[] { RunHistoryFileNames.Index, "slot0", "RunHistory_01.es3", "slot2" }, names);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(500, 500)]
    [InlineData(1_000_000, RunHistoryFileNames.MostSlots)]
    public void The_slot_count_is_bounded(int gameSlots, int expected)
    {
        Assert.Equal(expected + 1, RunHistoryFileNames.All(null, gameSlots, null).Length);
    }
}
