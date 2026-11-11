using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class NodeEntryTests
{
    private static readonly MapPoint Coach = new(2, 3);
    private static readonly MapPoint Chaos = new(1, 4);

    [Fact]
    public void The_host_numbers_each_entry_and_says_which_is_under_way()
    {
        var host = new NodeEntryHost();
        Assert.Null(host.Current);

        NodeEntry first = host.Begin(Coach);
        Assert.Equal(new NodeEntry(Coach, 1), first);
        Assert.Equal(first, host.Current);

        Assert.Equal(first, host.End());
        Assert.Null(host.Current);
        Assert.Null(host.End());

        // A second click on the same node is a new entry; clearing (hosting stopped) keeps the count going.
        Assert.Equal(new NodeEntry(Coach, 2), host.Begin(Coach));
        host.Clear();
        Assert.Null(host.Current);
        Assert.Equal(new NodeEntry(Chaos, 3), host.Begin(Chaos));
        Assert.Throws<ArgumentException>(() => host.Begin(MapPoint.None));
    }

    [Fact]
    public void A_friend_plays_each_entry_once_and_returns_when_the_hosts_is_over()
    {
        var friend = new NodeEntryFollow();
        Assert.Equal(NodeEntryAction.None, friend.Step(null, mapReady: true, introPlaying: false).Action);

        var entry = new NodeEntry(Coach, 1);
        NodeEntryStep play = friend.Step(entry, mapReady: true, introPlaying: false);
        Assert.Equal(NodeEntryAction.Play, play.Action);
        Assert.Equal(entry, play.Entry);
        Assert.Equal(entry, friend.Playing);

        // The same entry in later views (the cursor, the camera moving) plays nothing more.
        Assert.Equal(NodeEntryAction.None, friend.Step(entry, mapReady: true, introPlaying: false).Action);

        // The host's camera came back: this PC's comes back (the glue does it only with no event screen up here).
        NodeEntryStep back = friend.Step(null, mapReady: true, introPlaying: false);
        Assert.Equal(NodeEntryAction.Return, back.Action);
        Assert.Equal(entry, back.Entry);
        Assert.Null(friend.Playing);
        Assert.Equal(NodeEntryAction.None, friend.Step(null, mapReady: true, introPlaying: false).Action);
    }

    [Fact]
    public void An_entry_this_map_cant_play_is_left_out_with_why_and_not_tried_again()
    {
        var friend = new NodeEntryFollow();
        var entry = new NodeEntry(Coach, 4);
        NodeEntryStep skipped = friend.Step(entry, mapReady: false, introPlaying: false);
        Assert.Equal(NodeEntryAction.Skip, skipped.Action);
        Assert.Contains("world map", skipped.Why, StringComparison.Ordinal);
        Assert.Null(friend.Playing);
        Assert.Equal(NodeEntryAction.None, friend.Step(entry, mapReady: true, introPlaying: false).Action);

        // Nothing was played, so nothing comes back when the host's is over.
        Assert.Equal(NodeEntryAction.None, friend.Step(null, mapReady: true, introPlaying: false).Action);

        NodeEntryStep intro = friend.Step(new NodeEntry(Chaos, 5), mapReady: true, introPlaying: true);
        Assert.Equal(NodeEntryAction.Skip, intro.Action);
        Assert.Contains("intro", intro.Why, StringComparison.Ordinal);

        Assert.Equal(NodeEntryAction.Play, friend.Step(new NodeEntry(Chaos, 6), mapReady: true, introPlaying: false).Action);
    }

    [Fact]
    public void An_entry_that_ended_here_by_itself_brings_nothing_back()
    {
        var friend = new NodeEntryFollow();
        var entry = new NodeEntry(Coach, 2);
        Assert.Equal(NodeEntryAction.Play, friend.Step(entry, mapReady: true, introPlaying: false).Action);

        // This PC's event screen closed and brought its camera back before the host's view said so.
        friend.Ended();
        Assert.Null(friend.Playing);
        Assert.Equal(NodeEntryAction.None, friend.Step(entry, mapReady: true, introPlaying: false).Action);
        Assert.Equal(NodeEntryAction.None, friend.Step(null, mapReady: true, introPlaying: false).Action);
    }

    [Fact]
    public void A_newer_entry_replaces_one_still_held_and_a_reset_plays_the_hosts_again()
    {
        var friend = new NodeEntryFollow();
        Assert.Equal(NodeEntryAction.Play, friend.Step(new NodeEntry(Coach, 1), mapReady: true, introPlaying: false).Action);

        // The host's game pushed into another node (a node it couldn't enter, then the next) before this view said it was over.
        NodeEntryStep next = friend.Step(new NodeEntry(Chaos, 2), mapReady: true, introPlaying: false);
        Assert.Equal(NodeEntryAction.Play, next.Action);
        Assert.Equal(new NodeEntry(Chaos, 2), friend.Playing);

        // Following again (a rejoin): the host's entry under way plays here once more, from the next view.
        friend.Reset();
        Assert.Equal(0, friend.Taken);
        Assert.Null(friend.Playing);
        Assert.Equal(NodeEntryAction.Play, friend.Step(new NodeEntry(Chaos, 2), mapReady: true, introPlaying: false).Action);
    }

    [Fact]
    public void An_entry_reads_as_its_node_and_number() => Assert.Equal("2,3 #9", new NodeEntry(Coach, 9).ToString());
}
