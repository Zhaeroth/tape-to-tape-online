using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class FollowTests
{
    private static HostView View(uint epoch, string screen = "map", uint checkpoint = 0, params (string Key, NetValue Value)[] values)
    {
        var list = new List<KeyValuePair<string, NetValue>>();
        foreach ((string key, NetValue value) in values)
        {
            list.Add(new KeyValuePair<string, NetValue>(key, value));
        }

        return new HostView(epoch, screen, checkpoint, list);
    }

    private static RunCheckpoint Checkpoint(uint sequence) =>
        new(sequence, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = new[] { (byte)sequence } });

    private static HostView RoundTrip(HostView view)
    {
        var writer = new BitWriter();
        HostViewMessage.Write(writer, view);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(HostViewMessage.TryRead(ref reader, out HostView? read));
        return read!;
    }

    [Fact]
    public void A_view_round_trips_and_its_hash_ignores_detail_order()
    {
        HostView view = View(9, "map", 4, ("node", NetValue.FromString("3,2")), ("act", NetValue.FromInt(2)));
        HostView read = RoundTrip(view);
        Assert.Equal(9u, read.Epoch);
        Assert.Equal("map", read.Screen);
        Assert.Equal(4u, read.Checkpoint);
        Assert.Equal("3,2", read.Text("node"));
        Assert.Equal(2, read.Whole("act"));
        Assert.Equal(-1, read.Whole("missing", -1));
        Assert.Equal(view.ContentHash, read.ContentHash);
        Assert.Equal(view.ContentHash, View(10, "map", 5, ("act", NetValue.FromInt(2)), ("node", NetValue.FromString("3,2"))).ContentHash);
        Assert.NotEqual(view.ContentHash, View(9, "map", 4, ("act", NetValue.FromInt(3)), ("node", NetValue.FromString("3,2"))).ContentHash);
        Assert.Equal("map (act=2, node=\"3,2\")", view.ToString());
    }

    [Fact]
    public void Views_can_be_compared_without_the_cursor()
    {
        HostView a = View(1, "map", 1, ("node", NetValue.FromString("1,1")), ("sel", NetValue.FromString("2,1")), ("camX", NetValue.FromFloat(3)));
        HostView cursorMoved = View(2, "map", 1, ("node", NetValue.FromString("1,1")), ("sel", NetValue.FromString("2,2")));
        HostView moved = View(3, "map", 1, ("node", NetValue.FromString("2,2")), ("sel", NetValue.FromString("2,2")));
        Assert.True(cursorMoved.SameExcept(a, SceneViewKeys.Transient));
        Assert.True(a.SameExcept(cursorMoved, SceneViewKeys.Transient));
        Assert.False(moved.SameExcept(cursorMoved, SceneViewKeys.Transient));
        Assert.False(moved.SameExcept(null, SceneViewKeys.Transient));
        Assert.False(View(4, "preview", 1, ("node", NetValue.FromString("2,2"))).SameExcept(moved, SceneViewKeys.Transient));
    }

    [Fact]
    public void Bad_views_are_refused()
    {
        Assert.False(TryReadHandmade(writer =>
        {
            writer.WriteUInt32(1);
            writer.WriteShortString("", HostView.MaxScreenBytes);
            writer.WriteUInt32(0);
            writer.WriteVarUInt(0);
        }));
        Assert.False(TryReadHandmade(writer =>
        {
            writer.WriteUInt32(1);
            writer.WriteShortString("map", HostView.MaxScreenBytes);
            writer.WriteUInt32(0);
            writer.WriteVarUInt(2);
            writer.WriteShortString("node", HostView.MaxKeyBytes);
            NetValue.FromInt(1).Write(writer);
            writer.WriteShortString("node", HostView.MaxKeyBytes);
            NetValue.FromInt(2).Write(writer);
        }));
        Assert.False(TryReadHandmade(writer =>
        {
            writer.WriteUInt32(1);
            writer.WriteShortString("map", HostView.MaxScreenBytes);
            writer.WriteUInt32(0);
            writer.WriteVarUInt(HostView.MaxValues + 1);
        }));
        Assert.False(TryReadHandmade(writer =>
        {
            writer.WriteUInt32(1);
            writer.WriteShortString("map\n", HostView.MaxScreenBytes);
            writer.WriteUInt32(0);
            writer.WriteVarUInt(0);
        }));
    }

    [Fact]
    public void Text_details_from_the_host_are_cleaned()
    {
        HostView read = RoundTrip(View(1, "menu", 0, ("title", NetValue.FromString("a\u0007b"))));
        Assert.DoesNotContain('\u0007', read.Text("title"));
    }

    [Fact]
    public void Random_view_bytes_never_throw()
    {
        var random = new Random(6);
        for (int i = 0; i < 3000; i++)
        {
            byte[] junk = new byte[random.Next(0, 64)];
            random.NextBytes(junk);
            var reader = new BitReader(junk);
            _ = HostViewMessage.TryRead(ref reader, out _);
            reader = new BitReader(junk);
            _ = FollowReportMessage.TryRead(ref reader, out _);
        }
    }

    [Fact]
    public void A_report_round_trips_and_unknown_states_are_refused()
    {
        var writer = new BitWriter();
        FollowReportMessage.Write(writer, new FollowReport(5, FollowState.Failed, 3, "no such team"));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(FollowReportMessage.TryRead(ref reader, out FollowReport report));
        Assert.Equal(new FollowReport(5, FollowState.Failed, 3, "no such team"), report);

        writer.Reset();
        FollowReportMessage.Write(writer, new FollowReport(5, (FollowState)99, 3, ""));
        reader = new BitReader(writer.AsSpan());
        Assert.False(FollowReportMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void The_follower_waits_for_the_run_the_view_needs()
    {
        var follower = new CampaignFollower();
        Assert.Equal(FollowStep.None, follower.Next(0));
        Assert.True(follower.OfferView(View(1, "map", checkpoint: 2)));
        Assert.Equal(FollowStep.None, follower.Next(0));
        Assert.Equal(FollowState.WaitingForRun, follower.Report.State);

        follower.OfferCheckpoint(Checkpoint(1));
        Assert.Equal(FollowStep.None, follower.Next(0)); // still older than the view needs
        follower.OfferCheckpoint(Checkpoint(2));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        Assert.Equal(FollowState.Moving, follower.Report.State);
        Assert.Equal(FollowStep.None, follower.Next(0)); // busy until the glue answers

        follower.Arrived(follower.Target!, 2);
        Assert.Equal(FollowState.Following, follower.Report.State);
        Assert.Equal(2u, follower.Report.Checkpoint);
        Assert.Equal(FollowStep.None, follower.Next(0));
    }

    [Fact]
    public void A_newer_checkpoint_on_the_same_view_updates_the_run()
    {
        var follower = new CampaignFollower();
        follower.OfferCheckpoint(Checkpoint(1));
        follower.OfferView(View(1, "map", checkpoint: 1));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        follower.Arrived(follower.Target!, 1);

        follower.OfferCheckpoint(Checkpoint(2));
        Assert.Equal(FollowStep.UpdateRun, follower.Next(0));
        Assert.Equal(FollowStep.None, follower.Next(0));
        follower.RunUpdated(2, ok: true);
        Assert.Equal(2u, follower.AppliedCheckpoint);
        Assert.Equal(FollowStep.None, follower.Next(0));

        follower.OfferCheckpoint(Checkpoint(3));
        Assert.Equal(FollowStep.UpdateRun, follower.Next(0));
        follower.RunUpdated(0, ok: false); // not retried every frame
        Assert.Equal(FollowStep.None, follower.Next(0));
        Assert.Equal(2u, follower.AppliedCheckpoint);
    }

    [Fact]
    public void A_screen_that_leaves_a_checkpoint_for_later_is_not_asked_again()
    {
        var follower = new CampaignFollower();
        follower.OfferCheckpoint(Checkpoint(1));
        follower.OfferView(View(1, "match", checkpoint: 1));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        follower.Arrived(follower.Target!, 1);

        follower.OfferCheckpoint(Checkpoint(2));
        Assert.Equal(FollowStep.UpdateRun, follower.Next(0));
        follower.RunUpdated(0, ok: true); // noted; the next scene change brings it in
        Assert.Equal(1u, follower.AppliedCheckpoint);
        for (int frame = 0; frame < 5; frame++)
        {
            Assert.Equal(FollowStep.None, follower.Next(frame));
        }

        follower.OfferCheckpoint(Checkpoint(3));
        Assert.Equal(FollowStep.UpdateRun, follower.Next(0)); // a newer one is asked about
        follower.RunUpdated(3, ok: true);
        Assert.Equal(3u, follower.AppliedCheckpoint);
        Assert.Equal(3u, follower.Report.Checkpoint);
    }

    [Fact]
    public void A_failed_move_is_retried_a_few_times_then_left_until_the_next_view()
    {
        var follower = new CampaignFollower();
        follower.OfferView(View(1));
        long now = 0;
        for (int attempt = 1; attempt <= CampaignFollower.MaxAttempts; attempt++)
        {
            Assert.Equal(FollowStep.GoToView, follower.Next(now));
            follower.Missed("scene didn't load", now);
            Assert.Equal(FollowState.Failed, follower.Report.State);
            Assert.Equal(FollowStep.None, follower.Next(now + CampaignFollower.RetryAfterMicros - 1));
            now += CampaignFollower.RetryAfterMicros;
        }

        Assert.Equal(FollowStep.None, follower.Next(now + 60_000_000));
        Assert.Equal("scene didn't load", follower.Report.Reason);

        follower.OfferView(View(2, "menu"));
        Assert.Equal(FollowStep.GoToView, follower.Next(now));
    }

    [Fact]
    public void A_follower_has_given_up_only_when_its_last_try_failed_and_the_view_is_still_missed()
    {
        var follower = new CampaignFollower();
        Assert.False(follower.GaveUp);
        follower.OfferView(View(1));
        long now = 0;
        for (int attempt = 1; attempt <= CampaignFollower.MaxAttempts; attempt++)
        {
            Assert.False(follower.GaveUp); // not before the last try, not while one is out
            Assert.Equal(FollowStep.GoToView, follower.Next(now));
            Assert.False(follower.GaveUp);
            follower.Missed("scene didn't load", now);
            Assert.Equal(attempt == CampaignFollower.MaxAttempts, follower.GaveUp);
            now += CampaignFollower.RetryAfterMicros;
        }

        follower.OfferView(View(2, "menu")); // the host moved on: fresh tries
        Assert.False(follower.GaveUp);
    }

    [Fact]
    public void A_follower_that_got_there_on_its_last_try_has_not_given_up()
    {
        var follower = new CampaignFollower();
        HostView view = View(1);
        follower.OfferView(view);
        long now = 0;
        for (int attempt = 1; attempt < CampaignFollower.MaxAttempts; attempt++)
        {
            Assert.Equal(FollowStep.GoToView, follower.Next(now));
            follower.Missed("scene didn't load", now);
            now += CampaignFollower.RetryAfterMicros;
        }

        Assert.Equal(FollowStep.GoToView, follower.Next(now));
        follower.Arrived(view, 0);
        Assert.False(follower.GaveUp);
    }

    [Fact]
    public void Cursor_moves_neither_hurry_nor_add_to_the_tries_of_a_failing_move()
    {
        HostView Map(uint epoch, string cursor, uint checkpoint = 1, string node = "1,1") => View(epoch, "map", checkpoint,
            ("node", NetValue.FromString(node)), ("sel", NetValue.FromString(cursor)));

        var follower = new CampaignFollower();
        follower.OfferCheckpoint(Checkpoint(1));
        follower.OfferView(Map(1, "2,1"));
        long now = 0;
        uint epoch = 1;
        for (int attempt = 1; attempt <= CampaignFollower.MaxAttempts; attempt++)
        {
            Assert.Equal(FollowStep.GoToView, follower.Next(now));
            follower.Missed("the world map didn't load", now);
            follower.OfferView(Map(++epoch, attempt % 2 == 0 ? "2,1" : "3,1")); // the host's cursor wanders meanwhile
            Assert.Equal(FollowStep.None, follower.Next(now + 1)); // not straight away
            now += CampaignFollower.RetryAfterMicros;
        }

        follower.OfferView(Map(++epoch, "4,1"));
        Assert.Equal(FollowStep.None, follower.Next(now + 60_000_000)); // still given up on: the place is the same

        follower.OfferView(Map(++epoch, "4,1", node: "4,1")); // the host moved on: fresh tries
        Assert.Equal(FollowStep.GoToView, follower.Next(now));
        follower.Arrived(follower.Target!, 1);
        for (int move = 0; move < CampaignFollower.MaxAttempts + 2; move++)
        {
            follower.OfferView(Map(++epoch, move % 2 == 0 ? "3,2" : "5,2", node: "4,1")); // cursor moves once there
            Assert.Equal(FollowStep.GoToView, follower.Next(now));
            follower.Arrived(follower.Target!, 0);
        }

        Assert.Equal(FollowState.Following, follower.Report.State);
    }

    [Fact]
    public void A_view_that_changes_mid_move_is_followed_next()
    {
        var follower = new CampaignFollower();
        follower.OfferView(View(1, "map"));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        HostView first = follower.Target!;
        follower.OfferView(View(2, "match"));
        follower.Arrived(first, 0);
        Assert.Equal(FollowState.Moving, follower.Report.State); // arrived at the old one; not following the new one yet
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        Assert.Equal("match", follower.Target!.Screen);
    }

    [Fact]
    public void A_move_that_fails_after_the_host_moved_on_is_reported_against_its_own_view_and_the_new_one_goes_at_once()
    {
        // Regression case: the failure was reported against the new view and held it back 2 s.
        var follower = new CampaignFollower();
        follower.OfferView(View(1, "title"));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        follower.Arrived(follower.Target!, 0);
        follower.OfferView(View(2, "map", 0, ("node", NetValue.FromInt(1))));
        Assert.Equal(FollowStep.GoToView, follower.Next(10)); // on the way to view 2
        follower.OfferView(View(3, "preview", 0, ("node", NetValue.FromInt(2)))); // the host moves on meanwhile
        follower.Missed("view 2's scene failed to load", 20);

        Assert.True(follower.TryTakeReport(out FollowReport report));
        Assert.Equal(new FollowReport(2, FollowState.Failed, 0, "view 2's scene failed to load"), report);
        Assert.Equal(FollowStep.GoToView, follower.Next(30)); // view 3, at once
        Assert.Equal(3u, follower.Target!.Epoch);
        Assert.Equal(new FollowReport(3, FollowState.Moving, 0, string.Empty), follower.Report);
        follower.Missed("view 3 failed too", 40); // a failure of the view it was sent to counts as before
        Assert.Equal(new FollowReport(3, FollowState.Failed, 0, "view 3 failed too"), follower.Report);
        Assert.Equal(FollowStep.None, follower.Next(40 + CampaignFollower.RetryAfterMicros - 1));
        Assert.Equal(FollowStep.GoToView, follower.Next(40 + CampaignFollower.RetryAfterMicros));
    }

    [Fact]
    public void The_place_a_move_is_leaving_offered_again_mid_move_is_not_taken_as_reached()
    {
        // Regression case: the host went back while this game was on its way; it had been marked as following.
        HostView Map(uint epoch, string node) => View(epoch, "map", 0, ("node", NetValue.FromString(node)));

        var follower = new CampaignFollower();
        follower.OfferView(Map(1, "1,1"));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        follower.Arrived(follower.Target!, 0);
        follower.OfferView(Map(2, "2,2"));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        HostView leaving = follower.Target!;
        follower.OfferView(Map(3, "1,1"));
        Assert.Equal(1u, follower.Current!.Epoch);
        Assert.Equal(FollowState.Moving, follower.Report.State);
        follower.Arrived(leaving, 0);
        Assert.Equal(FollowStep.GoToView, follower.Next(0)); // it's at 2,2 now: back to 1,1
        Assert.Equal(3u, follower.Target!.Epoch);
    }

    [Fact]
    public void Old_views_are_ignored_and_the_same_place_under_a_new_number_needs_no_move()
    {
        var follower = new CampaignFollower();
        follower.OfferView(View(5, "map", 0, ("node", NetValue.FromString("1,1"))));
        Assert.Equal(FollowStep.GoToView, follower.Next(0));
        follower.Arrived(follower.Target!, 0);
        Assert.False(follower.OfferView(View(4, "title")));

        Assert.True(follower.OfferView(View(6, "map", 0, ("node", NetValue.FromString("1,1")))));
        Assert.Equal(FollowStep.None, follower.Next(0));
        Assert.Equal(6u, follower.Current!.Epoch);
        Assert.Equal(new FollowReport(6, FollowState.Following, 0, ""), follower.Report);
    }

    [Fact]
    public void Reports_are_taken_once_per_change()
    {
        var follower = new CampaignFollower();
        Assert.False(follower.TryTakeReport(out _));
        follower.OfferView(View(1));
        follower.Next(0);
        Assert.True(follower.TryTakeReport(out FollowReport moving));
        Assert.Equal(FollowState.Moving, moving.State);
        Assert.False(follower.TryTakeReport(out _));
        follower.Arrived(follower.Target!, 0);
        Assert.True(follower.TryTakeReport(out FollowReport following));
        Assert.Equal(FollowState.Following, following.State);
        follower.Reset();
        Assert.True(follower.TryTakeReport(out FollowReport none));
        Assert.Equal(FollowState.None, none.State);
        Assert.Null(follower.Current);
    }

    [Fact]
    public void The_tracker_knows_when_every_friend_follows_a_view()
    {
        var tracker = new FollowTracker();
        ulong[] friends = { 1, 2 };
        Assert.False(tracker.AllFollowing(friends, 3));
        tracker.Record(1, new FollowReport(3, FollowState.Following, 0, ""));
        tracker.Record(2, new FollowReport(3, FollowState.Moving, 0, ""));
        Assert.False(tracker.AllFollowing(friends, 3));
        tracker.Record(2, new FollowReport(4, FollowState.Moving, 0, "")); // already past it
        Assert.True(tracker.AllFollowing(friends, 3));
        Assert.False(tracker.AllFollowing(friends, 4));
        tracker.Forget(2);
        Assert.True(tracker.AllFollowing(new ulong[] { 1 }, 3));
        Assert.True(tracker.AllFollowing(Array.Empty<ulong>(), 9));
    }

    private static bool TryReadHandmade(Action<BitWriter> body)
    {
        var writer = new BitWriter();
        body(writer);
        var reader = new BitReader(writer.AsSpan());
        return HostViewMessage.TryRead(ref reader, out _);
    }
}
