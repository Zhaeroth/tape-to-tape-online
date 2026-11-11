using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>A burst of avatar loads as one log line: counts and sizes, never whose.</summary>
public class AvatarLoadTallyTests
{
    [Fact]
    public void Nothing_counted_says_nothing()
    {
        var tally = new AvatarLoadTally();

        Assert.Equal(0, tally.Count);
        Assert.Equal(string.Empty, tally.Take(3));
    }

    [Fact]
    public void One_size_is_one_count()
    {
        var tally = new AvatarLoadTally();
        for (int i = 0; i < 5; i++)
        {
            tally.Add("Medium", 64, 64);
        }

        Assert.Equal("loaded 5 Medium avatars (64x64); 8 in the cache.", tally.Take(8));
        Assert.Equal(0, tally.Count);
        Assert.Equal(string.Empty, tally.Take(8)); // taken: counting starts again
    }

    [Fact]
    public void Sizes_are_listed_in_the_order_they_came()
    {
        var tally = new AvatarLoadTally();
        tally.Add("Medium", 64, 64);
        tally.Add("Large", 184, 184);
        tally.Add("Medium", 64, 64);
        tally.Add("Small", 32, 32);

        Assert.Equal("loaded 2 Medium avatars (64x64), 1 Large avatar (184x184) and 1 Small avatar (32x32); 4 in the cache.", tally.Take(4));
    }

    [Fact]
    public void Past_eight_kinds_the_rest_are_counted_as_more()
    {
        var tally = new AvatarLoadTally();
        for (int i = 0; i < 10; i++)
        {
            tally.Add("Medium", 60 + i, 60 + i);
        }

        string line = tally.Take(10);

        Assert.StartsWith("loaded 1 Medium avatar (60x60), ", line, System.StringComparison.Ordinal);
        Assert.EndsWith(" and 2 more; 10 in the cache.", line, System.StringComparison.Ordinal);
    }
}
