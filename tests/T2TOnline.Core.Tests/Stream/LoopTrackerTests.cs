using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>The host's polls of the game's loop instances, turned into the stream's starts, moves and stops.</summary>
public class LoopTrackerTests
{
    private const long Ms = 1000;
    private const ulong Magnet = 0xA1;
    private const ulong Rocket = 0xB2;

    [Fact]
    public void A_loop_starts_moves_and_stops_with_its_instance()
    {
        var tracker = new LoopTracker();
        Assert.Equal(new[] { SoundLoopMessage.Start(1, LoopKind.Magneto, 1, 0, 2) }, Poll(tracker, 0, (Magnet, LoopKind.Magneto, true, 1, 0, 2)));

        // Moved 0.1 m: not yet; 0.3 m: a move.
        Assert.Empty(Poll(tracker, 50 * Ms, (Magnet, LoopKind.Magneto, true, 1.1f, 0, 2)));
        Assert.Equal(new[] { SoundLoopMessage.Move(1, 1.3f, 0, 2) }, Poll(tracker, 100 * Ms, (Magnet, LoopKind.Magneto, true, 1.3f, 0, 2)));

        // Stopping (its fade-out): a stop, once; the fading instance isn't a new loop, and once gone it's forgotten.
        Assert.Equal(new[] { SoundLoopMessage.Stop(1) }, Poll(tracker, 200 * Ms, (Magnet, LoopKind.Magneto, false, 1.3f, 0, 2)));
        Assert.Empty(Poll(tracker, 250 * Ms, (Magnet, LoopKind.Magneto, false, 1.3f, 0, 2)));
        Assert.True(tracker.Any);
        Assert.Empty(PollKinds(tracker, 300 * Ms, new[] { LoopKind.Magneto }));
        Assert.False(tracker.Any);
        Assert.Equal((1, 1, 1, 0, 0), (tracker.Started, tracker.Stopped, tracker.Moves, tracker.Refreshes, tracker.Playing));
    }

    [Fact]
    public void A_loop_gone_without_stopping_first_stops()
    {
        // Released and gone between two polls (a stop with no fade, the match's reset).
        var tracker = new LoopTracker();
        Poll(tracker, 0, (Rocket, LoopKind.RetroRocket, true, 0, 0, 0));
        Assert.Equal(new[] { SoundLoopMessage.Stop(1) }, PollKinds(tracker, 16 * Ms, new[] { LoopKind.RetroRocket }));
        Assert.Equal(0, tracker.Known(LoopKind.RetroRocket));
    }

    [Fact]
    public void Moves_wait_for_distance_and_time()
    {
        var tracker = new LoopTracker();
        Poll(tracker, 0, (Magnet, LoopKind.Magnet, true, 0, 0, 0));

        // Racing along at 30 m/s, polled at every snapshot (60 a second) for a second: a move every other snapshot.
        const long Snapshot = 16_667;
        int moves = 0;
        for (int k = 1; k < 60; k++)
        {
            moves += Poll(tracker, k * Snapshot, (Magnet, LoopKind.Magnet, true, k * Snapshot * 30f / 1_000_000, 0, 0)).Count(m => m.Op == SoundLoopOp.Move);
        }

        Assert.Equal(29, moves);

        // Standing still: the last stretch's move, then nothing (the start again comes at 2 s).
        int before = tracker.Moves;
        for (int k = 60; k < 115; k++)
        {
            Poll(tracker, k * Snapshot, (Magnet, LoopKind.Magnet, true, 30, 0, 0));
        }

        Assert.Equal(before + 1, tracker.Moves);
        Assert.Equal(0, tracker.Refreshes);
    }

    [Fact]
    public void A_playing_loop_starts_again_every_two_seconds_for_friends_who_weren_t_there()
    {
        var tracker = new LoopTracker();
        var starts = new List<long>();
        for (long t = 0; t <= 7_000 * Ms; t += 16 * Ms)
        {
            if (Poll(tracker, t, (Magnet, LoopKind.GravityWell, true, 5, 5, 5)).Any(m => m.Op == SoundLoopOp.Start && m.Handle == 1 && m.Kind == LoopKind.GravityWell))
            {
                starts.Add(t);
            }
        }

        Assert.Equal(4, starts.Count); // 0 s, then every 2 s
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, LoopTracker.RefreshEveryMicros, LoopTracker.RefreshEveryMicros + (16 * Ms)));
        Assert.Equal((1, 3), (tracker.Started, tracker.Refreshes));
    }

    [Fact]
    public void Each_instance_is_its_own_loop_and_stops_come_first()
    {
        var tracker = new LoopTracker();
        Poll(tracker, 0, (Magnet, LoopKind.Magneto, true, 0, 0, 0), (Magnet + 1, LoopKind.Magneto, true, 9, 0, 0));
        List<SoundLoopMessage> output = Poll(tracker, 16 * Ms, (Magnet + 1, LoopKind.Magneto, true, 9, 0, 0), (Rocket, LoopKind.RetroRocket, true, 1, 1, 1));
        Assert.Equal(new[] { SoundLoopMessage.Stop(1), SoundLoopMessage.Start(3, LoopKind.RetroRocket, 1, 1, 1) }, output);
        Assert.Equal("magneto 2, retro rocket 1", tracker.DescribeStarted());
    }

    [Fact]
    public void A_kind_not_polled_is_left_as_it_was()
    {
        // A poll that skipped the rocket's kind (nothing counted there) mustn't stop it.
        var tracker = new LoopTracker();
        Poll(tracker, 0, (Rocket, LoopKind.RetroRocket, true, 0, 0, 0));
        Assert.Empty(Poll(tracker, 16 * Ms, (Magnet, LoopKind.Magnet, false, 0, 0, 0)));
        Assert.Equal(1, tracker.Playing);
        Assert.Empty(PollKinds(tracker, 32 * Ms, new[] { LoopKind.Magnet }));
        Assert.Equal(1, tracker.Playing);
    }

    [Fact]
    public void An_instance_first_seen_stopping_is_never_a_loop()
    {
        var tracker = new LoopTracker();
        Assert.Empty(Poll(tracker, 0, (Magnet, LoopKind.Magnet, false, 0, 0, 0)));
        Assert.Empty(Poll(tracker, 16 * Ms, (Magnet, LoopKind.Magnet, false, 0, 0, 0)));
        Assert.Equal((0, 0), (tracker.Started, tracker.Stopped));
    }

    [Fact]
    public void An_instance_not_playing_yet_at_its_first_poll_starts_when_it_plays()
    {
        // The poll runs in the frame the game started it: FMOD may not say it plays until its next update.
        var tracker = new LoopTracker();
        Assert.Empty(Poll(tracker, 0, (Magnet, LoopKind.Magneto, false, 1, 0, 2)));
        Assert.Equal(new[] { SoundLoopMessage.Start(1, LoopKind.Magneto, 3, 0, 2) }, Poll(tracker, 16 * Ms, (Magnet, LoopKind.Magneto, true, 3, 0, 2)));
        Assert.Equal((1, 1), (tracker.Started, tracker.Playing));

        // Its stop works as any loop's, and a loop stopped never starts again.
        Assert.Equal(new[] { SoundLoopMessage.Stop(1) }, Poll(tracker, 32 * Ms, (Magnet, LoopKind.Magneto, false, 3, 0, 2)));
        Assert.Empty(Poll(tracker, 48 * Ms, (Magnet, LoopKind.Magneto, true, 3, 0, 2)));
        Assert.Equal((1, 0), (tracker.Started, tracker.Playing));
    }

    [Fact]
    public void An_instance_not_playing_yet_is_refused_past_sixteen_when_it_plays()
    {
        var tracker = new LoopTracker();
        var seen = Enumerable.Range(0, LoopTracker.MaxLoops).Select(i => ((ulong)(100 + i), LoopKind.Magnet, true, 0f, 0f, (float)i)).ToList();
        Poll(tracker, 0, seen.ToArray());
        seen.Add((Rocket, LoopKind.RetroRocket, false, 0f, 0f, 0f));
        Assert.Empty(Poll(tracker, 16 * Ms, seen.ToArray()));
        seen[^1] = (Rocket, LoopKind.RetroRocket, true, 0f, 0f, 0f);
        Assert.Empty(Poll(tracker, 32 * Ms, seen.ToArray()));
        Assert.Equal((1, LoopTracker.MaxLoops), (tracker.Refused, tracker.Playing));
    }

    [Fact]
    public void Past_sixteen_at_once_more_are_refused_and_stay_refused()
    {
        var tracker = new LoopTracker();
        var seen = Enumerable.Range(0, 20).Select(i => ((ulong)(100 + i), LoopKind.Magnet, true, 0f, 0f, (float)i)).ToArray();
        List<SoundLoopMessage> output = Poll(tracker, 0, seen);
        Assert.Equal(LoopTracker.MaxLoops, output.Count);
        Assert.Equal(Enumerable.Range(1, LoopTracker.MaxLoops).Select(i => (uint)i), output.Select(m => m.Handle));
        Assert.Equal(4, tracker.Refused);

        // One stops: the refused ones stay refused (never half-heard), and nothing new comes from them.
        output = Poll(tracker, 16 * Ms, seen.Skip(1).ToArray());
        Assert.Equal(new[] { SoundLoopMessage.Stop(1) }, output);
        Assert.Equal(4, tracker.Refused);
    }

    [Fact]
    public void A_broken_position_is_held_to_one_a_friend_takes()
    {
        var tracker = new LoopTracker();
        SoundLoopMessage start = Poll(tracker, 0, (Magnet, LoopKind.Magnet, true, float.NaN, 1e9f, float.NegativeInfinity)).Single();
        Assert.True(start.IsValid);
        Assert.Equal((0f, SoundLoopMessage.MaxCoordinate, 0f), (start.X, start.Y, start.Z));
    }

    [Fact]
    public void A_new_recording_starts_handles_again()
    {
        var tracker = new LoopTracker();
        Poll(tracker, 0, (Magnet, LoopKind.Magnet, true, 0, 0, 0));
        tracker.Reset();
        Assert.False(tracker.Any);
        Assert.Equal(1u, Poll(tracker, 0, (Rocket, LoopKind.RetroRocket, true, 0, 0, 0)).Single().Handle);
        Assert.Equal((1, "retro rocket 1"), (tracker.Started, tracker.DescribeStarted()));
    }

    private static List<SoundLoopMessage> Poll(LoopTracker tracker, long now, params (ulong Instance, LoopKind Kind, bool Playing, float X, float Y, float Z)[] seen)
    {
        var output = new List<SoundLoopMessage>();
        tracker.BeginPoll(now);
        foreach ((ulong instance, LoopKind kind, bool playing, float x, float y, float z) in seen)
        {
            tracker.Seen(instance, kind, playing, x, y, z);
        }

        tracker.EndPoll(output);
        return output;
    }

    private static List<SoundLoopMessage> PollKinds(LoopTracker tracker, long now, LoopKind[] emptyKinds)
    {
        var output = new List<SoundLoopMessage>();
        tracker.BeginPoll(now);
        foreach (LoopKind kind in emptyKinds)
        {
            tracker.Polled(kind);
        }

        tracker.EndPoll(output);
        return output;
    }
}
