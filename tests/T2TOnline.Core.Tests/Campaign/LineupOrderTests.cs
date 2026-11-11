using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class LineupOrderTests
{
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
    public void TheSameOrderNeedsNoSwap()
    {
        string[] order = { "lw", "ld", "c", "rd", "rw", "g", "b1", "" };
        IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(order, order.ToArray());
        Assert.NotNull(swaps);
        Assert.Empty(swaps!);
    }

    [Fact]
    public void OneSwapIsOneSwap()
    {
        string[] host = { "lw", "ld", "c", "rd", "rw", "g", "b1", "b2" };
        string[] here = { "lw", "ld", "b1", "rd", "rw", "g", "c", "b2" };
        IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(here, host);
        Assert.Equal(new[] { (2, 6) }, swaps);
        Assert.Equal(host, Apply(here, swaps!));
    }

    [Fact]
    public void ASkaterMovedToAnEmptySeatIsAMove()
    {
        string[] here = { "a", "b", "c", "", "" };
        string[] host = { "a", "", "c", "b", "" };
        IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(here, host);
        Assert.Equal(host, Apply(here, swaps!));
        Assert.Single(swaps!);
    }

    [Fact]
    public void ACardInPlaceIsNeverMoved()
    {
        string[] here = { "a", "b", "c", "d" };
        string[] host = { "c", "b", "a", "d" };
        IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(here, host);
        Assert.Equal(new[] { (0, 2) }, swaps);
    }

    [Fact]
    public void EveryPermutationIsReachedWithAtMostNMinusOneSwaps()
    {
        var random = new Random(1234);
        for (int round = 0; round < 500; round++)
        {
            int n = random.Next(1, 14);
            string[] host = Enumerable.Range(0, n).Select(i => random.Next(4) == 0 ? string.Empty : $"s{random.Next(8)}").ToArray();
            string[] here = host.OrderBy(_ => random.Next()).ToArray();
            IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(here, host);
            Assert.NotNull(swaps);
            Assert.True(swaps!.Count <= Math.Max(0, n - 1));
            Assert.Equal(host, Apply(here, swaps));
            Assert.All(swaps, s => Assert.NotEqual(s.A, s.B));
        }
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, new[] { "a", "c" })] // another skater
    [InlineData(new[] { "a", "b" }, new[] { "a", "b", "" })] // another count
    [InlineData(new[] { "a", "a" }, new[] { "a", "b" })] // a skater twice here, once there
    [InlineData(new[] { "a", "" }, new[] { "a", "b" })] // a seat empty here, filled there
    public void AnotherTeamIsRefused(string[] here, string[] host) => Assert.Null(LineupOrder.Swaps(here, host));

    [Fact]
    public void TooManyCardsAreRefused()
    {
        string[] many = Enumerable.Range(0, LineupOrder.MaxCards + 1).Select(i => $"s{i}").ToArray();
        Assert.Null(LineupOrder.Swaps(many, many.ToArray()));
        string[] most = many.Take(LineupOrder.MaxCards).ToArray();
        Assert.Empty(LineupOrder.Swaps(most, most.Reverse().Reverse().ToArray())!);
    }

    [Fact]
    public void NullCardsAreEmptySeats()
    {
        string[] here = { "a", null!, "b" };
        string[] host = { "a", "b", string.Empty };
        IReadOnlyList<(int A, int B)>? swaps = LineupOrder.Swaps(here, host);
        Assert.Equal(new[] { (1, 2) }, swaps);
    }

    [Fact]
    public void ThePrintTellsTeamsApart()
    {
        long print = LineupOrder.Print(new[] { "lw", "ld", "c", null, "g" });
        Assert.Equal(print, LineupOrder.Print(new[] { "lw", "ld", "c", string.Empty, "g" }));
        Assert.NotEqual(print, LineupOrder.Print(new[] { "ld", "lw", "c", string.Empty, "g" }));
        Assert.NotEqual(LineupOrder.Print(new[] { "ab", "c" }), LineupOrder.Print(new[] { "a", "bc" }));
        Assert.NotEqual(LineupOrder.Print(new[] { "a" }), LineupOrder.Print(new[] { "a", string.Empty }));
        Assert.NotEqual(LineupOrder.Print(Array.Empty<string>()), LineupOrder.Print(new[] { string.Empty }));
    }

    [Fact]
    public void ThePrintIsPinned()
    {
        // Both PCs compute it: a change here is a protocol change (the host's print goes in its menu data).
        // FNV-1a 64 over each id's UTF-8 and a 0, then the count as 4 bytes little-endian.
        Assert.Equal(unchecked((long)0x55E59C7831D22794UL), LineupOrder.Print(new List<string> { "skater_a", "skater_b" }));
        Assert.Equal(unchecked((long)0x4D25767F9DCE13F5UL), LineupOrder.Print(Array.Empty<string>()));
    }
}
