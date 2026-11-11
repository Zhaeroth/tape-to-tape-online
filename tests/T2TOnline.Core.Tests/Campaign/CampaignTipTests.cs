using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class CampaignTipTests
{
    /// <summary>The world map's first tip, as a host sends it.</summary>
    public static CampaignTipData Sample() => new("campaignMap", "Tip_CampaignMap", 4, true);

    /// <summary>The tip's item as a friend gets it: through a real menu message, written and read.</summary>
    private static MenuState OverTheWire(MenuData data)
    {
        var state = new MenuState(31, CampaignTipData.Type, CampaignTipData.Type, "world_map", 0, data);
        var writer = new BitWriter();
        Assert.True(MenuMessages.Write(writer, MenuEvent.Opened(state)));
        var reader = new BitReader(writer.ToArray());
        Assert.True(MenuMessages.TryRead(MessageType.MenuOpen, ref reader, out MenuEvent? read));
        return read!.State!;
    }

    [Fact]
    public void A_tip_round_trips_as_a_board_item()
    {
        MenuState state = OverTheWire(Sample().ToData());
        Assert.Equal((CampaignTipData.Type, CampaignTipData.Type, "world_map"), (state.Id, state.Type, state.Scene));
        Assert.True(CampaignTipData.TryRead(state.Data, out CampaignTipData? read));
        Assert.Equal(Sample(), read);

        // A tip whose speech has no name, and one at every cap.
        var bare = new CampaignTipData("bossNode", string.Empty, 0, false);
        Assert.True(CampaignTipData.TryRead(OverTheWire(bare.ToData()).Data, out read));
        Assert.Equal(bare, read);
        var full = new CampaignTipData(new string('t', CampaignTipData.MaxNameBytes), new string('é', CampaignTipData.MaxNameBytes / 2), CampaignTipData.MaxLines, true);
        Assert.Null(full.Problem());
        Assert.True(CampaignTipData.TryRead(OverTheWire(full.ToData()).Data, out read));
        Assert.Equal(full, read);
    }

    public static IEnumerable<object[]> BadTips()
    {
        yield return new object[] { new CampaignTipData(string.Empty, "s", 1, true) };
        yield return new object[] { new CampaignTipData(null!, "s", 1, true) };
        yield return new object[] { new CampaignTipData(new string('t', CampaignTipData.MaxNameBytes + 1), "s", 1, true) };
        yield return new object[] { new CampaignTipData("tip", new string('é', CampaignTipData.MaxNameBytes / 2 + 1), 1, true) };
        yield return new object[] { new CampaignTipData("tip", null!, 1, true) };
        yield return new object[] { new CampaignTipData("ti\np", "s", 1, true) };
        yield return new object[] { new CampaignTipData("tip", "s\0", 1, true) };
        yield return new object[] { new CampaignTipData("tip\uD800", "s", 1, true) };
        yield return new object[] { new CampaignTipData("tip", "s", -1, true) };
        yield return new object[] { new CampaignTipData("tip", "s", CampaignTipData.MaxLines + 1, true) };
    }

    [Theory]
    [MemberData(nameof(BadTips))]
    public void Tips_out_of_range_are_not_made(CampaignTipData tip)
    {
        Assert.NotNull(tip.Problem());
        Assert.ThrowsAny<ArgumentException>(() => tip.ToData());
    }

    [Fact]
    public void Malformed_items_are_refused()
    {
        List<KeyValuePair<string, NetValue>> good = Sample().ToData().Values.ToList();
        Assert.True(CampaignTipData.TryRead(new MenuData(good, Array.Empty<MenuItem>()), out _));
        var bad = new (string Key, NetValue? Value)[]
        {
            ("tip.id", null),
            ("tip.id", NetValue.FromInt(3)),
            ("tip.id", NetValue.FromString(string.Empty)),
            ("tip.id", NetValue.FromString("a\tb")),
            ("tip.seq", null),
            ("tip.seq", NetValue.FromBool(true)),
            ("tip.lines", null),
            ("tip.lines", NetValue.FromInt(-1)),
            ("tip.lines", NetValue.FromInt(CampaignTipData.MaxLines + 1)),
            ("tip.lines", NetValue.FromString("4")),
            ("tip.dark", null),
            ("tip.dark", NetValue.FromInt(1)),
        };

        foreach ((string key, NetValue? value) in bad)
        {
            var changed = good.Where(v => v.Key != key).ToList();
            if (value is { } set)
            {
                changed.Add(new(key, set));
            }

            Assert.False(CampaignTipData.TryRead(new MenuData(changed, Array.Empty<MenuItem>()), out CampaignTipData? tip), $"{key} = {value}");
            Assert.Null(tip);
        }

        Assert.False(CampaignTipData.TryRead(MenuData.Empty, out _));
        Assert.False(CampaignTipData.TryRead(null!, out _));
    }

    [Fact]
    public void The_host_shows_a_tip_once_its_overlay_comes_and_ends_it_when_it_goes()
    {
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(null, null, overlayUp: true, 10)); // nothing armed: another speech
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(10, null, overlayUp: false, 10.05)); // its three frames
        Assert.Equal(HostTipStep.Show, CampaignTipTimeline.Host(10, null, overlayUp: true, 10.1));
        Assert.Equal(HostTipStep.Disarm, CampaignTipTimeline.Host(10, null, overlayUp: true, 10 + CampaignTipTimeline.ConfirmSeconds + 0.01));
        Assert.Equal(HostTipStep.Disarm, CampaignTipTimeline.Host(10, null, overlayUp: false, 12));
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(null, 10, overlayUp: true, 20));
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(30, 10, overlayUp: true, 30)); // the next one armed while one is up: the one up first
        Assert.Equal(HostTipStep.End, CampaignTipTimeline.Host(null, 10, overlayUp: false, 20));
        Assert.Equal(HostTipStep.End, CampaignTipTimeline.Host(null, 10, overlayUp: true, 10 + CampaignTipTimeline.MaxTipSeconds + 1));
    }

    [Fact]
    public void A_tips_speech_progress_rides_its_board_item_to_friends_and_a_late_joiner()
    {
        var board = new MenuBoard();
        var friend = new MenuMirror();
        MenuTarget speaker = MenuTarget.AtItem(SpeechProgress.Group, 0);
        MenuEvent opened = board.Opened(CampaignTipData.Type, CampaignTipData.Type, "Map", Sample().ToData())!;
        Assert.Equal(MenuMirrorChange.Opened, friend.Apply(opened));
        uint serial = opened.Serial;
        int line2 = SpeechProgress.Line(2, SpeechStage.Advanced).Value;

        // The host's newest goes out; the same value again doesn't.
        Assert.Equal(MenuMirrorChange.ValueSet, friend.Apply(board.ValueSet(serial, speaker, line2)!));
        Assert.Null(board.ValueSet(serial, speaker, line2));
        Assert.True(friend.TryGet(serial, out MirroredMenu menu));
        Assert.True(SpeechProgress.TryRead(menu.Values[speaker], out SpeechProgress progress));
        Assert.Equal((2, SpeechStage.Advanced), (progress.Key, progress.Stage));

        // A friend who joins mid-tip gets the tip, then its progress: its bubble catches up from line 1.
        var late = new MenuMirror();
        foreach (MenuEvent step in board.Snapshot())
        {
            late.Apply(step);
        }

        Assert.True(late.TryGet(serial, out MirroredMenu lateMenu));
        Assert.Equal(CampaignTipData.Type, lateMenu.State.Type);
        Assert.Equal(line2, lateMenu.Values[speaker]);

        // A rejoin's copy is the same object with the same values; the close leaves what it last said with who kept it.
        Assert.Equal(MenuMirrorChange.Refreshed, friend.Apply(board.Snapshot()[1]));
        Assert.True(friend.TryGet(serial, out MirroredMenu again));
        Assert.Same(menu, again);
        Assert.Equal(MenuMirrorChange.Closed, friend.Apply(board.Closed(serial)!));
        Assert.Equal(line2, menu.Values[speaker]);
    }

    [Fact]
    public void A_long_frame_doesnt_use_up_a_tips_wait_for_its_overlay()
    {
        // The game waits three frames: a scene's loading frames can pass the seconds with only a few frames gone.
        double late = 10 + CampaignTipTimeline.ConfirmSeconds + 1;
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(10, null, overlayUp: false, late, armedFrames: 2));
        Assert.Equal(HostTipStep.Show, CampaignTipTimeline.Host(10, null, overlayUp: true, late, armedFrames: 4));
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(10, null, overlayUp: false, late, armedFrames: CampaignTipTimeline.ConfirmFrames));

        // Past both, the game's own rules kept it back.
        Assert.Equal(HostTipStep.Disarm, CampaignTipTimeline.Host(10, null, overlayUp: false, late, armedFrames: CampaignTipTimeline.ConfirmFrames + 1));

        // Many quick frames alone don't either.
        Assert.Equal(HostTipStep.Wait, CampaignTipTimeline.Host(10, null, overlayUp: false, 10.5, armedFrames: CampaignTipTimeline.ConfirmFrames + 1));
    }

    [Fact]
    public void A_friend_plays_the_tip_on_its_screen_and_runs_it_out_with_the_hosts()
    {
        // Waiting for its screen; there: play; too long: left out.
        Assert.Equal(FriendTipStep.Wait, CampaignTipTimeline.Friend(false, false, onItsScreen: false, false, arrivedAt: 0, 0, now: 3));
        Assert.Equal(FriendTipStep.Play, CampaignTipTimeline.Friend(false, false, onItsScreen: true, false, 0, 0, 3));
        Assert.Equal(FriendTipStep.LeftOut, CampaignTipTimeline.Friend(false, false, onItsScreen: true, false, 0, 0, CampaignTipTimeline.StartWaitSeconds + 0.1));

        // Playing while the host's shows: nothing to do.
        Assert.Equal(FriendTipStep.Wait, CampaignTipTimeline.Friend(true, false, false, overlayUp: true, 0, 0, 30));

        // The host's ended: one never played isn't shown; one playing runs out, is done when its overlay goes, and is
        // taken down when it takes too long.
        Assert.Equal(FriendTipStep.NeverShown, CampaignTipTimeline.Friend(false, true, true, false, 0, 5, 6));
        Assert.Equal(FriendTipStep.RunOut, CampaignTipTimeline.Friend(true, true, false, overlayUp: true, 0, endedAt: 20, now: 20.5));
        Assert.Equal(FriendTipStep.Done, CampaignTipTimeline.Friend(true, true, false, overlayUp: false, 0, 20, 21));
        Assert.Equal(FriendTipStep.TakeDown, CampaignTipTimeline.Friend(true, true, false, overlayUp: true, 0, 20, 20 + CampaignTipTimeline.RunOutSeconds + 0.1));
    }
}
