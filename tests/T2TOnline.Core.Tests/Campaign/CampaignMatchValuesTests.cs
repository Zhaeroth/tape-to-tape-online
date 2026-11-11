using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Campaign;

public class CampaignMatchValuesTests
{
    private static List<KeyValuePair<string, NetValue>> Setup(NetValue gameType) => new()
    {
        new("mode.gameMode", NetValue.FromInt(0)),
        new(CampaignMatchValues.GameTypeKey, gameType),
    };

    [Fact]
    public void Only_a_rogue_game_type_is_a_campaign_match()
    {
        Assert.True(CampaignMatchValues.IsCampaign(Setup(NetValue.FromInt(1))));
        Assert.False(CampaignMatchValues.IsCampaign(Setup(NetValue.FromInt(0))));
        Assert.False(CampaignMatchValues.IsCampaign(Setup(NetValue.FromString("1"))));
        Assert.False(CampaignMatchValues.IsCampaign(new List<KeyValuePair<string, NetValue>>()));
        Assert.False(CampaignMatchValues.IsCampaign(null));
    }

    [Fact]
    public void The_needed_checkpoint_is_read_only_from_a_whole_number_in_range()
    {
        List<KeyValuePair<string, NetValue>> setup = Setup(NetValue.FromInt(1));
        Assert.Equal(0u, CampaignMatchValues.NeededCheckpoint(setup));
        Assert.Equal(0u, CampaignMatchValues.NeededCheckpoint(null));

        setup.Add(new(CampaignMatchValues.RunCheckpointKey, NetValue.FromInt(7)));
        Assert.Equal(7u, CampaignMatchValues.NeededCheckpoint(setup));

        // A checkpoint number over int.MaxValue travels as a whole number and comes back as itself.
        Assert.Equal(uint.MaxValue, CampaignMatchValues.NeededCheckpoint(new List<KeyValuePair<string, NetValue>> { new(CampaignMatchValues.RunCheckpointKey, NetValue.FromInt(uint.MaxValue)) }));

        foreach (NetValue bad in new[] { NetValue.FromInt(-1), NetValue.FromInt(1L + uint.MaxValue), NetValue.FromString("7"), NetValue.FromBool(true) })
        {
            Assert.Equal(0u, CampaignMatchValues.NeededCheckpoint(new List<KeyValuePair<string, NetValue>> { new(CampaignMatchValues.RunCheckpointKey, bad) }));
        }
    }

    [Fact]
    public void A_campaign_match_is_shared_only_from_the_co_op_slot_and_with_a_checkpoint()
    {
        List<KeyValuePair<string, NetValue>> exhibition = Setup(NetValue.FromInt(0));
        Assert.Null(CampaignMatchValues.WhyNotShared(exhibition, campaignShared: false));
        Assert.Null(CampaignMatchValues.WhyNotShared(exhibition, campaignShared: true));

        // Hosting started during a solo run (D46): friends would wait for a run that never comes.
        List<KeyValuePair<string, NetValue>> campaign = Setup(NetValue.FromInt(1));
        campaign.Add(new(CampaignMatchValues.RunCheckpointKey, NetValue.FromInt(3)));
        Assert.Contains("solo run", CampaignMatchValues.WhyNotShared(campaign, campaignShared: false), StringComparison.Ordinal);
        Assert.Null(CampaignMatchValues.WhyNotShared(campaign, campaignShared: true));

        // A setup that names no checkpoint (the host's run couldn't be sent for it).
        List<KeyValuePair<string, NetValue>> noRun = Setup(NetValue.FromInt(1));
        noRun.Add(new(CampaignMatchValues.RunCheckpointKey, NetValue.FromInt(0)));
        Assert.Contains("names no run checkpoint", CampaignMatchValues.WhyNotShared(noRun, campaignShared: true), StringComparison.Ordinal);
        Assert.Contains("names no run checkpoint", CampaignMatchValues.WhyNotShared(Setup(NetValue.FromInt(1)), campaignShared: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Map_points_round_trip_and_bad_ones_are_refused()
    {
        string text = CampaignMatchValues.FormatPoint(3, 12);
        Assert.Equal("3,12", text);
        Assert.True(CampaignMatchValues.TryParsePoint(text, 255, out int x, out int y));
        Assert.Equal((3, 12), (x, y));

        foreach (string? bad in new[] { null, string.Empty, "3", "3,12,1", "-1,2", "3,256", "3;12", " 3,12", "3,", "a,b", "99999999999,1" })
        {
            Assert.False(CampaignMatchValues.TryParsePoint(bad, 255, out _, out _), bad ?? "null");
        }
    }

    [Fact]
    public void Lists_round_trip_and_are_bounded_in_count_and_range()
    {
        string text = CampaignMatchValues.FormatList(new[] { 0, 2, 3 });
        Assert.Equal("0,2,3", text);
        Assert.True(CampaignMatchValues.TryParseList(text, 8, 0, 4, out int[] values));
        Assert.Equal(new[] { 0, 2, 3 }, values);

        Assert.True(CampaignMatchValues.TryParseList(string.Empty, 8, 0, 4, out int[] none));
        Assert.Empty(none);
        Assert.False(CampaignMatchValues.TryParseList(null, 8, 0, 4, out _));
        Assert.False(CampaignMatchValues.TryParseList("0,5", 8, 0, 4, out _));
        Assert.False(CampaignMatchValues.TryParseList("0,,1", 8, 0, 4, out _));
        Assert.False(CampaignMatchValues.TryParseList("1,2,3", 2, 0, 4, out _));
        Assert.False(CampaignMatchValues.TryParseList("1" + new string(',', 40), 64, 0, 4, out _));
        Assert.False(CampaignMatchValues.TryParseList(new string('9', 5000), 64, 0, int.MaxValue, out _));
        Assert.True(CampaignMatchValues.TryParseList("-3,200", 2, -5, 250, out int[] signed));
        Assert.Equal(new[] { -3, 200 }, signed);
    }

    [Fact]
    public void A_forward_line_takes_any_jersey_number_and_clamps_its_stats()
    {
        // The Act 1 boss's prisoners: a jersey number past the stats' limit refused the whole match on a friend.
        Assert.True(CampaignMatchValues.TryParseForward("72,57,69,61,24601", 1000, out int[] boss, out bool bossClamped));
        Assert.Equal(new[] { 72, 57, 69, 61, 24601 }, boss);
        Assert.False(bossClamped);

        Assert.True(CampaignMatchValues.TryParseForward("1200,-4,50,50,-1", 1000, out int[] odd, out bool oddClamped));
        Assert.Equal(new[] { 1000, 0, 50, 50, -1 }, odd);
        Assert.True(oddClamped);

        Assert.False(CampaignMatchValues.TryParseForward("72,57,69,61", 1000, out int[] few, out _));
        Assert.Empty(few);
        Assert.False(CampaignMatchValues.TryParseForward("72,57,69,61,9,9", 1000, out _, out _));
        Assert.False(CampaignMatchValues.TryParseForward("72,57,x,61,9", 1000, out _, out _));
        Assert.False(CampaignMatchValues.TryParseForward(null, 1000, out _, out _));
        Assert.False(CampaignMatchValues.TryParseForward(string.Empty, 1000, out _, out _));
    }

    [Fact]
    public void A_list_never_holds_more_than_the_hard_limit()
    {
        var many = new int[CampaignMatchValues.MaxListLength + 1];
        string text = CampaignMatchValues.FormatList(many);
        Assert.False(CampaignMatchValues.TryParseList(text, int.MaxValue, 0, 0, out _));
        Assert.True(CampaignMatchValues.TryParseList(CampaignMatchValues.FormatList(many[1..]), int.MaxValue, 0, 0, out int[] most));
        Assert.Equal(CampaignMatchValues.MaxListLength, most.Length);
    }

    [Fact]
    public void A_checkpoint_is_held_when_it_is_the_one_needed_or_later()
    {
        Assert.False(CampaignMatchValues.HoldsCheckpoint(0, 0));
        Assert.True(CampaignMatchValues.HoldsCheckpoint(4, 0));
        Assert.False(CampaignMatchValues.HoldsCheckpoint(0, 3));
        Assert.False(CampaignMatchValues.HoldsCheckpoint(2, 3));
        Assert.True(CampaignMatchValues.HoldsCheckpoint(3, 3));
        Assert.True(CampaignMatchValues.HoldsCheckpoint(5, 3));

        // Across wraparound: 2 comes after uint.MaxValue.
        Assert.True(CampaignMatchValues.HoldsCheckpoint(2, uint.MaxValue));
        Assert.False(CampaignMatchValues.HoldsCheckpoint(uint.MaxValue, 2));
    }

    [Fact]
    public void A_lineup_by_asset_id_maps_onto_the_teams_own_ids()
    {
        var team = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fwd_a"] = "id-1",
            ["fwd_b"] = "id-2",
            ["fwd_c"] = "id-3",
        };

        Assert.True(CampaignMatchValues.TryMapSlots(new[] { "fwd_b", string.Empty, "fwd_a" }, team, out string[] ids, out List<string> missing));
        Assert.Equal(new[] { "id-2", string.Empty, "id-1" }, ids);
        Assert.Empty(missing);

        Assert.False(CampaignMatchValues.TryMapSlots(new[] { "fwd_c", "fwd_x", "fwd_y" }, team, out ids, out missing));
        Assert.Equal(new[] { "id-3", string.Empty, string.Empty }, ids);
        Assert.Equal(new[] { "fwd_x", "fwd_y" }, missing);
    }

    [Fact]
    public void A_roster_is_put_in_the_hosts_order_when_it_holds_the_same_skaters()
    {
        // The host swapped a starter (place 2) with the first bench skater (place 5) and its game hasn't saved the run yet.
        string[] host = { "fwd_a", "fwd_b", "fwd_f", "fwd_d", "fwd_e", "fwd_c", "", "", "", "" };
        string[] here = { "fwd_a", "fwd_b", "fwd_c", "fwd_d", "fwd_e", "fwd_f", "", "", "", "" };
        Assert.True(CampaignMatchValues.TryOrderRoster(host, here, 32, out int[] from));
        Assert.Equal(new[] { 0, 1, 5, 3, 4, 2, 6, 7, 8, 9 }, from);

        string[] after = new string[from.Length];
        for (int i = 0; i < from.Length; i++)
        {
            after[i] = here[from[i]];
        }

        Assert.Equal(host, after);
    }

    [Fact]
    public void A_roster_already_in_the_hosts_order_stays_as_it_is()
    {
        string?[] roster = { "fwd_a", "fwd_b", null, "fwd_c" };
        Assert.True(CampaignMatchValues.TryOrderRoster(new[] { "fwd_a", "fwd_b", "", "fwd_c" }, roster, 32, out int[] from));
        Assert.Equal(new[] { 0, 1, 2, 3 }, from);
    }

    [Fact]
    public void An_empty_seat_moves_like_a_skater_and_a_skater_in_place_never_moves()
    {
        Assert.True(CampaignMatchValues.TryOrderRoster(new[] { "fwd_a", "", "fwd_c", "fwd_b" }, new[] { "fwd_a", "fwd_c", "", "fwd_b" }, 32, out int[] from));
        Assert.Equal(new[] { 0, 2, 1, 3 }, from);
    }

    [Fact]
    public void A_roster_with_other_skaters_is_not_reordered()
    {
        // An older run: a skater the host signed since isn't here, or one it lost still is.
        Assert.False(CampaignMatchValues.TryOrderRoster(new[] { "fwd_a", "fwd_x", "" }, new[] { "fwd_a", "fwd_b", "" }, 32, out int[] from));
        Assert.Empty(from);
        Assert.False(CampaignMatchValues.TryOrderRoster(new[] { "fwd_a", "fwd_b", "" }, new[] { "fwd_a", "fwd_b", "fwd_c" }, 32, out from));
        Assert.False(CampaignMatchValues.TryOrderRoster(new[] { "fwd_a", "fwd_b" }, new[] { "fwd_a", "fwd_b", "" }, 32, out from));
    }

    [Fact]
    public void A_roster_naming_a_skater_twice_or_too_long_is_not_reordered()
    {
        // Two copies of one asset can't be told apart by asset id: which goes where would be a guess.
        Assert.False(CampaignMatchValues.TryOrderRoster(new[] { "fwd_a", "fwd_a", "fwd_b" }, new[] { "fwd_b", "fwd_a", "fwd_a" }, 32, out int[] from));
        Assert.Empty(from);

        string[] long1 = new string[33];
        Array.Fill(long1, string.Empty);
        Assert.False(CampaignMatchValues.TryOrderRoster(long1, long1, 32, out from));
        Assert.True(CampaignMatchValues.TryOrderRoster(long1[..32], long1[..32], 32, out from));
    }
}
