using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>A friend's loops: started, moved and stopped as the host says, and never left playing.</summary>
public class LoopPlaybackTests
{
    private const long Ms = 1000;

    [Fact]
    public void A_loop_starts_moves_and_stops_as_the_host_says()
    {
        var loops = new LoopPlayback();
        Assert.Equal(LoopStep.Start, loops.Apply(SoundLoopMessage.Start(1, LoopKind.Magneto, 0, 0, 0), 0));
        Assert.True(loops.TryGetKind(1, out LoopKind kind));
        Assert.Equal(LoopKind.Magneto, kind);
        Assert.Equal(LoopStep.Move, loops.Apply(SoundLoopMessage.Move(1, 1, 0, 0), 50 * Ms));
        Assert.Equal(LoopStep.Stop, loops.Apply(SoundLoopMessage.Stop(1), 900 * Ms));
        Assert.Equal(0, loops.Count);
        Assert.Equal((1, 1, 1, "magneto 1"), (loops.Started, loops.Moves, loops.StoppedByHost, loops.DescribeStarted()));
    }

    [Fact]
    public void A_start_again_for_a_loop_playing_only_moves_it()
    {
        // The host's start every 2 s, and a friend who heard the first one.
        var loops = new LoopPlayback();
        loops.Apply(SoundLoopMessage.Start(4, LoopKind.RetroRocket, 0, 0, 0), 0);
        Assert.Equal(LoopStep.Move, loops.Apply(SoundLoopMessage.Start(4, LoopKind.RetroRocket, 3, 0, 0), 2_000 * Ms));
        Assert.Equal((1, 1), (loops.Count, loops.Started));
    }

    [Fact]
    public void A_late_friend_starts_the_loop_from_the_host_s_start_again()
    {
        // Its first start went before this friend got the stream: moves for it are nothing until the start again comes.
        var loops = new LoopPlayback();
        Assert.Equal(LoopStep.None, loops.Apply(SoundLoopMessage.Move(7, 1, 0, 0), 0));
        Assert.Equal(LoopStep.Start, loops.Apply(SoundLoopMessage.Start(7, LoopKind.ExplodingPuck, 2, 0, 0), 1_500 * Ms));
        Assert.Equal(1, loops.Unknown);
    }

    [Fact]
    public void Moves_and_stops_for_a_loop_not_playing_are_nothing()
    {
        var loops = new LoopPlayback();
        Assert.Equal(LoopStep.None, loops.Apply(SoundLoopMessage.Stop(9), 0));
        Assert.Equal(LoopStep.None, loops.Apply(SoundLoopMessage.Move(9, 0, 0, 0), 0));
        loops.Apply(SoundLoopMessage.Start(9, LoopKind.Magnet, 0, 0, 0), 0);
        loops.Apply(SoundLoopMessage.Stop(9), 0);
        Assert.Equal(LoopStep.None, loops.Apply(SoundLoopMessage.Stop(9), 0));
        Assert.Equal(3, loops.Unknown);
    }

    [Fact]
    public void Past_sixteen_at_once_starts_are_refused()
    {
        var loops = new LoopPlayback();
        for (uint handle = 1; handle <= 20; handle++)
        {
            loops.Apply(SoundLoopMessage.Start(handle, LoopKind.Magnet, 0, 0, 0), 0);
        }

        Assert.Equal((LoopPlayback.MaxLoops, 4), (loops.Count, loops.Refused));

        // One stops: room for the next start.
        loops.Apply(SoundLoopMessage.Stop(1), 0);
        Assert.Equal(LoopStep.Start, loops.Apply(SoundLoopMessage.Start(21, LoopKind.Magnet, 0, 0, 0), 0));
    }

    [Fact]
    public void A_loop_the_host_has_gone_silent_about_stops_after_five_seconds()
    {
        var loops = new LoopPlayback();
        loops.Apply(SoundLoopMessage.Start(1, LoopKind.Magneto, 0, 0, 0), 0);
        loops.Apply(SoundLoopMessage.Start(2, LoopKind.GravityWell, 0, 0, 0), 0);
        var silent = new List<uint>();

        // Loop 1 hears its start again every 2 s; loop 2's stop was lost.
        for (long t = 0; t <= 12_000 * Ms; t += 100 * Ms)
        {
            if (t > 0 && t % (2_000 * Ms) == 0)
            {
                loops.Apply(SoundLoopMessage.Start(1, LoopKind.Magneto, 0, 0, 0), t);
            }

            loops.TakeSilent(t, silent);
            if (t == 4_900 * Ms)
            {
                Assert.Empty(silent);
            }
        }

        Assert.Equal(new[] { 2u }, silent);
        Assert.Equal((1, 1), (loops.Count, loops.Silenced));
        Assert.True(loops.TryGetKind(1, out _));
    }

    [Fact]
    public void Everything_stops_at_once_when_playback_holds_or_ends()
    {
        var loops = new LoopPlayback();
        loops.Apply(SoundLoopMessage.Start(1, LoopKind.Magneto, 0, 0, 0), 0);
        loops.Apply(SoundLoopMessage.Start(2, LoopKind.FusRoDahCharge, 0, 0, 0), 0);
        var all = new List<uint>();
        loops.TakeAll(all);
        all.Sort();
        Assert.Equal(new[] { 1u, 2u }, all);
        Assert.Equal(0, loops.Count);
        Assert.Equal(LoopStep.None, loops.Apply(SoundLoopMessage.Stop(1), 0));
    }

    [Fact]
    public void A_start_that_failed_here_is_forgotten()
    {
        var loops = new LoopPlayback();
        loops.Apply(SoundLoopMessage.Start(3, LoopKind.Magnet, 0, 0, 0), 0);
        loops.Forget(3);
        Assert.Equal((0, 0), (loops.Count, loops.Started));
        Assert.Equal(LoopStep.None, loops.Apply(SoundLoopMessage.Move(3, 0, 0, 0), 0));

        // The host's start again tries again.
        Assert.Equal(LoopStep.Start, loops.Apply(SoundLoopMessage.Start(3, LoopKind.Magnet, 0, 0, 0), 2_000 * Ms));
    }

    [Fact]
    public void A_start_of_a_kind_this_build_doesn_t_know_is_nothing()
    {
        var loops = new LoopPlayback();
        Assert.Equal(LoopStep.None, loops.Apply(new SoundLoopMessage(1, SoundLoopOp.Start, (LoopKind)200, 0, 0, 0), 0));
        Assert.Equal((0, 1), (loops.Count, loops.Unknown));
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var loops = new LoopPlayback();
        loops.Apply(SoundLoopMessage.Start(1, LoopKind.Magnet, 0, 0, 0), 0);
        loops.Apply(SoundLoopMessage.Move(2, 0, 0, 0), 0);
        loops.Reset();
        Assert.Equal((0, 0, 0, "none"), (loops.Count, loops.Started, loops.Unknown, loops.DescribeStarted()));
    }
}
