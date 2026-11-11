using System.Collections.Generic;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class RetakeScheduleTests
{
    [Fact]
    public void A_take_over_a_follower_asked_for_waits_for_the_next_frame()
    {
        // The follower's game destroyed a talent's component this frame (frame 10): it's still in the object's list until
        // the frame's end, so the second take-over runs at frame 11, when it has gone.
        var schedule = new RetakeSchedule();
        var due = new List<(ushort NetId, RetakeReasons Reasons)>();
        schedule.Request(14, 11, RetakeReasons.ChangedHere);

        schedule.TakeDue(10, due);
        Assert.Empty(due);
        Assert.Equal(1, schedule.Count);

        schedule.TakeDue(11, due);
        Assert.Equal(new[] { ((ushort)14, RetakeReasons.ChangedHere) }, due);
        Assert.Equal(0, schedule.Count);

        schedule.TakeDue(12, due);
        Assert.Empty(due);
    }

    [Fact]
    public void Requests_for_one_object_merge_into_one_take_over_after_both()
    {
        var schedule = new RetakeSchedule();
        var due = new List<(ushort NetId, RetakeReasons Reasons)>();
        schedule.Request(3, 20, RetakeReasons.HostChanged);
        schedule.Request(3, 21, RetakeReasons.ChangedHere);
        schedule.Request(3, 19, RetakeReasons.HostChanged);

        schedule.TakeDue(20, due);
        Assert.Empty(due);
        schedule.TakeDue(21, due);
        Assert.Equal(new[] { ((ushort)3, RetakeReasons.HostChanged | RetakeReasons.ChangedHere) }, due);
    }

    [Fact]
    public void Objects_due_at_once_all_come_out_and_the_rest_wait()
    {
        var schedule = new RetakeSchedule();
        var due = new List<(ushort NetId, RetakeReasons Reasons)>();
        schedule.Request(1, 5, RetakeReasons.HostChanged);
        schedule.Request(2, 5, RetakeReasons.ChangedHere);
        schedule.Request(3, 6, RetakeReasons.ChangedHere);

        schedule.TakeDue(5, due);
        Assert.Equal(2, due.Count);
        Assert.Contains(((ushort)1, RetakeReasons.HostChanged), due);
        Assert.Contains(((ushort)2, RetakeReasons.ChangedHere), due);
        Assert.Equal(1, schedule.Count);
    }

    [Fact]
    public void One_frame_takes_at_most_so_many_and_the_rest_wait_for_the_next()
    {
        // A host announcing many changed lists at once: each frame takes over a few, none is lost.
        var schedule = new RetakeSchedule();
        var due = new List<(ushort NetId, RetakeReasons Reasons)>();
        for (int id = 0; id < 40; id++)
        {
            schedule.Request((ushort)id, 1, RetakeReasons.HostChanged);
        }

        var taken = new HashSet<ushort>();
        for (long frame = 1; frame <= 3; frame++)
        {
            schedule.TakeDue(frame, due, max: 16);
            Assert.True(due.Count <= 16);
            foreach ((ushort netId, RetakeReasons _) in due)
            {
                Assert.True(taken.Add(netId));
            }
        }

        Assert.Equal(40, taken.Count);
        Assert.Equal(0, schedule.Count);
    }

    [Fact]
    public void At_most_so_many_wait_and_the_rest_are_counted()
    {
        var schedule = new RetakeSchedule();
        for (int id = 0; id < RetakeSchedule.MaxPending + 10; id++)
        {
            schedule.Request((ushort)id, 1, RetakeReasons.HostChanged);
        }

        Assert.Equal(RetakeSchedule.MaxPending, schedule.Count);
        Assert.Equal(10, schedule.Dropped);

        // One already waiting still merges at the limit.
        schedule.Request(0, 2, RetakeReasons.ChangedHere);
        Assert.Equal(10, schedule.Dropped);

        schedule.Clear();
        Assert.Equal(0, schedule.Count);
        Assert.Equal(0, schedule.Dropped);
    }
}
