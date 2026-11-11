using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>The players' colour markers on a friend's PC: the host's player list, when to redraw, and the colour checks.</summary>
public class PlayerMarkersTests
{
    // The host's colours as a friend's session received them (HUD channel: player.0/1/2.color).
    private static readonly ColorValue Red = new(0.91f, 0.22f, 0.26f, 1f);
    private static readonly ColorValue Blue = new(0.2f, 0.33f, 0.82f, 1f);
    private static readonly ColorValue Green = new(0.22f, 0.75f, 0f, 1f);
    private static readonly ColorValue White = new(1f, 1f, 1f, 1f);

    [Fact]
    public void The_host_s_list_reads_as_sent()
    {
        Assert.Equal(new[] { new HostPlayer(0, 0), new HostPlayer(1, 1), new HostPlayer(2, 2) }, PlayerMarkers.ParseList("0:0,1:1,2:2"));
        Assert.Equal(new[] { new HostPlayer(3, -1) }, PlayerMarkers.ParseList("3:-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",,,")]
    [InlineData("x:1,1:y,:2,3:,4")]
    [InlineData("1:2:3")]
    [InlineData("-1:0")]
    [InlineData("33:0")]
    [InlineData("1:-2")]
    [InlineData("99999999999999999999:1")]
    public void Malformed_or_out_of_range_entries_are_left_out(string? text) =>
        Assert.Empty(PlayerMarkers.ParseList(text));

    [Fact]
    public void Good_entries_survive_bad_neighbours_and_a_repeat_keeps_the_first()
    {
        Assert.Equal(new[] { new HostPlayer(1, 1), new HostPlayer(2, 5) }, PlayerMarkers.ParseList("junk,1:1,2:5,1:7, 40:1"));
        Assert.Equal(new[] { new HostPlayer(0, 0), new HostPlayer(2, 2) }, PlayerMarkers.ParseList(" 0:0 , 2:2 "));
    }

    [Fact]
    public void A_flood_is_cut_to_the_limits()
    {
        string many = string.Join(",", Enumerable.Range(0, 32).Select(i => $"{i}:{i % 11}"));
        Assert.Equal(PlayerMarkers.MaxListed, PlayerMarkers.ParseList(many).Count);

        string huge = new string('9', 100_000);
        Assert.Empty(PlayerMarkers.ParseList(huge));
        Assert.Equal(new[] { new HostPlayer(1, 1) }, PlayerMarkers.ParseList("1:1," + huge));
    }

    [Theory]
    [InlineData(1, true)] // TeamHasPuck
    [InlineData(5, true)] // BeforePuckDrop
    [InlineData(6, true)] // Faceoff: where a stream starts
    [InlineData(10, true)] // Fight
    [InlineData(7, false)] // GoalScored: the replay takes the markers down
    [InlineData(8, false)] // EndOfPeriod
    [InlineData(9, false)] // EndOfGame
    public void Markers_are_redrawn_only_in_states_where_the_host_shows_them(int state, bool shown) =>
        Assert.Equal(shown, PlayerMarkers.ShownIn(state));

    [Fact]
    public void A_hidden_box_is_left_to_the_game() =>
        Assert.Equal(MarkerFix.None, PlayerMarkers.CheckOffscreenBox(false, White, null));

    [Fact]
    public void A_box_in_its_player_s_colour_is_fine() =>
        Assert.Equal(MarkerFix.None, PlayerMarkers.CheckOffscreenBox(true, new ColorValue(0.224f, 0.749f, 0.002f, 1f), Green));

    [Fact]
    public void A_white_box_on_a_controlled_skater_is_redrawn() =>
        Assert.Equal(MarkerFix.Recolour, PlayerMarkers.CheckOffscreenBox(true, White, Green));

    [Fact]
    public void Another_player_s_colour_or_a_faded_one_is_redrawn()
    {
        Assert.Equal(MarkerFix.Recolour, PlayerMarkers.CheckOffscreenBox(true, Blue, Green));
        Assert.Equal(MarkerFix.Recolour, PlayerMarkers.CheckOffscreenBox(true, Green with { A = 0.7f }, Green));
    }

    [Fact]
    public void A_box_shown_for_a_skater_nobody_controls_is_hidden() =>
        Assert.Equal(MarkerFix.Hide, PlayerMarkers.CheckOffscreenBox(true, Red, null));

    [Fact]
    public void Colours_match_within_the_tolerance_and_alpha_only_when_asked()
    {
        Assert.True(PlayerMarkers.SameColour(Green, new ColorValue(0.229f, 0.741f, 0.009f, 1f), withAlpha: true));
        Assert.False(PlayerMarkers.SameColour(Green, new ColorValue(0.24f, 0.75f, 0f, 1f), withAlpha: true));
        Assert.True(PlayerMarkers.SameColour(Green, Green with { A = 0.5f }, withAlpha: false));
        Assert.False(PlayerMarkers.SameColour(Green, Green with { A = 0.5f }, withAlpha: true));
    }

    [Fact]
    public void The_run_s_colours_have_plain_names()
    {
        Assert.Equal("red", PlayerMarkers.ColourName(Red));
        Assert.Equal("blue", PlayerMarkers.ColourName(Blue));
        Assert.Equal("green", PlayerMarkers.ColourName(Green));
        Assert.Equal("white", PlayerMarkers.ColourName(White));
        Assert.Equal("grey", PlayerMarkers.ColourName(new ColorValue(0.5f, 0.5f, 0.5f, 1f)));
        Assert.Equal("black", PlayerMarkers.ColourName(default));
        Assert.Equal("orange", PlayerMarkers.ColourName(new ColorValue(1f, 0.5f, 0f, 1f)));
        Assert.Equal("unreadable", PlayerMarkers.ColourName(new ColorValue(float.NaN, 0f, 0f, 1f)));
    }

    [Fact]
    public void A_colour_is_described_with_its_channels_and_alpha_only_when_it_isn_t_1()
    {
        Assert.Equal("green (0.22, 0.75, 0.00)", PlayerMarkers.Describe(Green));
        Assert.Equal("green (0.22, 0.75, 0.00, alpha 0.70)", PlayerMarkers.Describe(Green with { A = 0.7f }));
        Assert.Equal("unreadable", PlayerMarkers.Describe(new ColorValue(float.PositiveInfinity, 0f, 0f, 1f)));
    }

    [Fact]
    public void A_mismatch_is_acted_on_only_when_seen_the_same_way_twice_in_a_row()
    {
        var watch = new MarkerWatch<int, ColorValue>();

        // Between a HUD value and its event: wrong once, then fine.
        Assert.False(watch.Confirm(1, true, White));
        Assert.False(watch.Confirm(1, false, default));
        Assert.Equal(0, watch.Waiting);

        // Really wrong: the second sighting acts, and is forgotten.
        Assert.False(watch.Confirm(2, true, White));
        Assert.True(watch.Confirm(2, true, White));
        Assert.Equal(0, watch.Waiting);
        Assert.False(watch.Confirm(2, true, White));

        // Wrong in a different way the second time: wait for a repeat of that.
        Assert.False(watch.Confirm(3, true, White));
        Assert.False(watch.Confirm(3, true, Blue));
        Assert.True(watch.Confirm(3, true, Blue));
    }

    [Fact]
    public void Each_marker_is_watched_on_its_own_and_clear_forgets_them_all()
    {
        var watch = new MarkerWatch<int, ColorValue>();
        Assert.False(watch.Confirm(1, true, White));
        Assert.False(watch.Confirm(2, true, White));
        Assert.True(watch.Confirm(1, true, White));
        Assert.Equal(1, watch.Waiting);

        watch.Clear();
        Assert.Equal(0, watch.Waiting);
        Assert.False(watch.Confirm(2, true, White));
    }

    [Theory]
    [InlineData(false, false, 0, FaceIconFix.None)]
    [InlineData(false, true, 0, FaceIconFix.None)]
    [InlineData(true, true, 0, FaceIconFix.None)]
    [InlineData(true, true, 5, FaceIconFix.None)]
    [InlineData(true, false, 0, FaceIconFix.Retry)]
    [InlineData(true, false, 1, FaceIconFix.Retry)]
    [InlineData(true, false, 2, FaceIconFix.Hide)]
    [InlineData(true, false, 3, FaceIconFix.Hide)]
    public void A_face_icon_on_with_no_sprite_is_asked_for_again_then_hidden(bool iconOn, bool hasSprite, int retries, FaceIconFix fix) =>
        Assert.Equal(fix, PlayerMarkers.CheckFaceIcon(iconOn, hasSprite, retries));

    [Fact]
    public void A_face_icon_needs_two_sightings_like_the_box_s_colour()
    {
        // The game turns the icon on before its face arrives (UpdateFaceVisual awaits it): one check can fall in between.
        var watch = new MarkerWatch<int, FaceIconFix>();
        Assert.False(watch.Confirm(7, PlayerMarkers.CheckFaceIcon(true, false, 0) != FaceIconFix.None, FaceIconFix.Retry));
        Assert.False(watch.Confirm(7, PlayerMarkers.CheckFaceIcon(true, true, 0) != FaceIconFix.None, FaceIconFix.None));
        Assert.False(watch.Confirm(7, true, FaceIconFix.Retry));
        Assert.True(watch.Confirm(7, true, FaceIconFix.Retry));
    }

    [Fact]
    public void The_limits_hold_the_game_s_players() =>
        Assert.True(PlayerMarkers.MaxListed >= 11 && PlayerMarkers.MaxPlayerId >= 10 && PlayerMarkers.MaxListLength >= PlayerMarkers.MaxListed * "32:10,".Length,
            "the game has Player0 to Player10");
}
