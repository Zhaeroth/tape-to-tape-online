using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class ChainLinksTests
{
    private const long Second = 1_000_000;

    // ------------------------------------------------------------------ keys

    [Fact]
    public void Keys_follow_the_hud_naming_and_only_chain_keys_are_recognised()
    {
        Assert.Equal("chain.count", ChainKeys.Count);
        Assert.Equal("chain.0.ball", ChainKeys.Ball(0));
        Assert.Equal("chain.7.skater", ChainKeys.Skater(ChainKeys.MaxChains - 1));
        Assert.Equal("chain.3.right", ChainKeys.Right(3));
        Assert.Equal("chain.3.human", ChainKeys.Human(3));
        foreach (string key in new[] { ChainKeys.Count, ChainKeys.Ball(2), ChainKeys.Human(5), "chain.x" })
        {
            Assert.True(ChainKeys.IsKey(key), key);
        }

        foreach (string key in new[] { "chains", "clock", "player.0.skater", "golfer.who.0", string.Empty })
        {
            Assert.False(ChainKeys.IsKey(key), key);
        }
    }

    // ------------------------------------------------------------------ the host's list

    [Fact]
    public void The_prisoner_bosss_two_chains_are_listed_in_the_hosts_order()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(Ball: 40, Skater: 3, Right: false, Human: false), new ChainLink(41, 12, Right: false, Human: true));
        Assert.True(follower.Changed);
        Assert.Equal(new[] { new ChainLink(40, 3, false, false), new ChainLink(41, 12, false, true) }, follower.Links());
        Assert.Equal(0, follower.Refused);
    }

    [Fact]
    public void Places_past_the_count_and_incomplete_ones_are_left_out()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(40, 3, false, false), new ChainLink(41, 12, true, true));

        // The host's list shrank to one: its old second place stays in the HUD's memory, but past the count.
        Assert.True(follower.Take(ChainKeys.Count, NetValue.FromInt(1)));
        Assert.Equal(new[] { new ChainLink(40, 3, false, false) }, follower.Links());

        // A place whose ball is gone (null) counts for nothing.
        Assert.True(follower.Take(ChainKeys.Ball(0), NetValue.Null));
        Assert.Empty(follower.Links());
    }

    [Fact]
    public void A_ball_named_twice_keeps_its_first_place()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(40, 3, false, false), new ChainLink(40, 12, true, false));
        Assert.Equal(new[] { new ChainLink(40, 3, false, false) }, follower.Links());
    }

    [Fact]
    public void A_new_ball_at_a_faceoff_is_a_new_chain_and_the_old_one_goes()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(40, 3, false, false));
        var made = new List<ChainLink>(follower.Links());
        follower.EndMessage();

        // The host's faceoff: the old ball destroyed, a new one spawned for the same skater.
        Assert.True(follower.Take(ChainKeys.Ball(0), NetValue.FromRef(57)));
        Assert.True(follower.Changed);
        var gone = new List<ChainLink>();
        var missing = new List<ChainLink>();
        ChainSync.Diff(made, follower.Links(), gone, missing);
        Assert.Equal(new[] { new ChainLink(40, 3, false, false) }, gone);
        Assert.Equal(new[] { new ChainLink(57, 3, false, false) }, missing);
    }

    [Fact]
    public void An_unchanged_value_changes_nothing()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(40, 3, false, false));
        follower.EndMessage();

        // The HUD's full set every 5 s sends every value again.
        Host(follower, new ChainLink(40, 3, false, false));
        Assert.False(follower.Changed);
    }

    // ------------------------------------------------------------------ untrusted values

    [Fact]
    public void Values_of_the_wrong_kind_or_out_of_range_are_refused_and_change_nothing()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(40, 3, false, false));
        follower.EndMessage();

        Assert.True(follower.Take(ChainKeys.Count, NetValue.FromInt(ChainKeys.MaxChains + 1)));
        Assert.True(follower.Take(ChainKeys.Count, NetValue.FromInt(-1)));
        Assert.True(follower.Take(ChainKeys.Count, NetValue.FromBool(true)));
        Assert.True(follower.Take(ChainKeys.Ball(0), NetValue.FromInt(41)));
        Assert.True(follower.Take(ChainKeys.Ball(0), NetValue.FromString("go:standard_game/Ball(Clone)#0")));
        Assert.True(follower.Take(ChainKeys.Skater(0), NetValue.FromRef(0)));
        Assert.True(follower.Take(ChainKeys.Right(0), NetValue.FromInt(1)));
        Assert.Equal(7, follower.Refused);
        Assert.False(follower.Changed);
        Assert.Equal(new[] { new ChainLink(40, 3, false, false) }, follower.Links());
    }

    [Fact]
    public void Indexes_past_the_most_chains_and_unknown_fields_are_refused_but_taken()
    {
        var follower = new ChainFollower();
        foreach (string key in new[] { "chain.8.ball", "chain.99.ball", "chain.123.ball", "chain.-1.ball", "chain.0", "chain..ball", "chain.0.colour", "chain.x" })
        {
            Assert.True(follower.Take(key, NetValue.FromRef(40)), key);
        }

        Assert.Equal(8, follower.Refused);
        Assert.False(follower.Changed);
        Assert.Empty(follower.Links());
    }

    [Fact]
    public void Other_keys_are_not_taken()
    {
        var follower = new ChainFollower();
        Assert.False(follower.Take("possession", NetValue.FromRef(40)));
        Assert.False(follower.Take("chains", NetValue.FromInt(1)));
        Assert.Equal(0, follower.Refused);
    }

    [Fact]
    public void Reset_forgets_the_host()
    {
        var follower = new ChainFollower();
        Host(follower, new ChainLink(40, 3, false, false));
        follower.Take(ChainKeys.Count, NetValue.FromInt(-5));
        follower.Reset();
        Assert.Empty(follower.Links());
        Assert.Equal(0, follower.Refused);
        Assert.False(follower.Changed);
    }

    // ------------------------------------------------------------------ the sync

    [Fact]
    public void Diff_keeps_what_the_host_still_lists_as_it_is()
    {
        var a = new ChainLink(40, 3, false, false);
        var b = new ChainLink(41, 12, true, true);
        var gone = new List<ChainLink> { a };
        var missing = new List<ChainLink> { b };
        ChainSync.Diff(new[] { a, b }, new[] { b, a }, gone, missing);
        Assert.Empty(gone);
        Assert.Empty(missing);

        // The same ball on the other skate (or another skater, or the other length) is another chain.
        ChainSync.Diff(new[] { a }, new[] { a with { Right = true } }, gone, missing);
        Assert.Equal(new[] { a }, gone);
        Assert.Equal(new[] { a with { Right = true } }, missing);

        // The end of the list: every chain made goes.
        ChainSync.Diff(new[] { a, b }, System.Array.Empty<ChainLink>(), gone, missing);
        Assert.Equal(new[] { a, b }, gone);
        Assert.Empty(missing);
    }

    [Fact]
    public void Chains_are_made_at_most_so_many_a_second()
    {
        var sync = new ChainSync(perSecond: 3);
        Assert.Equal(3, Enumerable.Range(0, 10).Count(_ => sync.TryTake(0)));
        Assert.Equal(7, sync.Deferred);
        Assert.False(sync.TryTake(Second - 1));
        Assert.True(sync.TryTake(Second));
        Assert.Equal(4, sync.Made);

        // A clock that goes back (a new playback's) starts a new window rather than waiting.
        Assert.True(sync.TryTake(0));
        sync.Reset();
        Assert.Equal(0, sync.Made);
        Assert.Equal(0, sync.Deferred);
        Assert.True(sync.TryTake(5 * Second));
    }

    private static void Host(ChainFollower follower, params ChainLink[] links)
    {
        Assert.True(follower.Take(ChainKeys.Count, NetValue.FromInt(links.Length)));
        for (int i = 0; i < links.Length; i++)
        {
            Assert.True(follower.Take(ChainKeys.Ball(i), NetValue.FromRef(links[i].Ball)));
            Assert.True(follower.Take(ChainKeys.Skater(i), NetValue.FromRef(links[i].Skater)));
            Assert.True(follower.Take(ChainKeys.Right(i), NetValue.FromBool(links[i].Right)));
            Assert.True(follower.Take(ChainKeys.Human(i), NetValue.FromBool(links[i].Human)));
        }
    }
}
