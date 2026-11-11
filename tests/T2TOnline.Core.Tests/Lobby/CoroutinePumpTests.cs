using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class CoroutinePumpTests
{
    /// <summary>A coroutine from a C# iterator of yields, logging each stretch it runs.</summary>
    private sealed class Script : ICoroutineFrame
    {
        private readonly IEnumerator<CoroutineYield> _steps;

        public Script(string name, IEnumerable<CoroutineYield> steps, List<string>? log = null)
        {
            Name = name;
            _steps = steps.GetEnumerator();
            Log = log ?? new List<string>();
        }

        public string Name { get; }

        public List<string> Log { get; }

        public CoroutineYield Current { get; private set; }

        public bool MoveNext()
        {
            Log.Add(Name);
            bool more = _steps.MoveNext();
            Current = more ? _steps.Current : default;
            return more;
        }
    }

    private static IEnumerable<CoroutineYield> Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return CoroutineYield.NextFrame();
        }
    }

    private static IEnumerable<CoroutineYield> One(CoroutineYield yielded)
    {
        yield return yielded;
    }

    [Fact]
    public void Null_yields_run_one_stretch_a_frame_until_the_end()
    {
        var pump = new CoroutinePump(new Script("root", Frames(3)));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(0, 0));
        Assert.Equal(4, pump.Moves);
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(0, 0));
        Assert.Equal(4, pump.Moves); // nothing runs after the end
    }

    [Fact]
    public void Wait_for_seconds_counts_the_scaled_clock_from_the_yield()
    {
        var pump = new CoroutinePump(new Script("root", One(CoroutineYield.Wait(2))));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(10, 100));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(11.9, 500)); // real time doesn't count
        Assert.Equal(1, pump.Moves);
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(12, 500));
    }

    [Fact]
    public void Realtime_seconds_count_real_time_whatever_the_time_scale()
    {
        var pump = new CoroutinePump(new Script("root", One(CoroutineYield.WaitRealtime(1))));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 50));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(1000, 50.5)); // the scaled clock doesn't count
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(1000, 51));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void A_wait_of_no_time_is_one_frame(double seconds)
    {
        var pump = new CoroutinePump(new Script("root", One(CoroutineYield.Wait(seconds))));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(5, 5));
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(5, 5));
    }

    [Fact]
    public void Keep_waiting_is_asked_from_the_next_frame_while_it_says_so()
    {
        int asked = 0;
        bool wait = true;
        var pump = new CoroutinePump(new Script("root", One(CoroutineYield.While(() => { asked++; return wait; }, "WaitUntil"))));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(0, asked);
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(2, asked);
        wait = false;
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(0, 0));
    }

    [Fact]
    public void A_nested_coroutine_runs_at_once_and_its_parent_goes_on_in_the_frame_it_ends()
    {
        var log = new List<string>();
        var child = new Script("child", Frames(1), log);
        IEnumerable<CoroutineYield> Parent()
        {
            yield return CoroutineYield.Into(child);
            yield return CoroutineYield.NextFrame();
        }

        var pump = new CoroutinePump(new Script("parent", Parent(), log));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(new[] { "parent", "child" }, log); // the child's first stretch, the frame it was yielded
        Assert.Equal(2, pump.Depth);

        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(new[] { "parent", "child", "child", "parent" }, log); // the child ends; the parent goes on the same frame
        Assert.Equal(1, pump.Depth);
        Assert.Equal(2, pump.Deepest);

        Assert.Equal(CoroutinePumpState.Done, pump.Tick(0, 0));
        Assert.Equal(0, pump.Depth);
    }

    [Fact]
    public void A_wait_inside_a_nested_coroutine_holds_the_whole_stack()
    {
        IEnumerable<CoroutineYield> Parent()
        {
            yield return CoroutineYield.Into(new Script("child", One(CoroutineYield.Wait(1))));
        }

        var pump = new CoroutinePump(new Script("parent", Parent()));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0.5, 0));
        Assert.Equal(2, pump.Moves);
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(1, 0)); // the child ends, then the parent, in one frame
        Assert.Equal(4, pump.Moves);
    }

    [Fact]
    public void Nesting_deeper_than_the_cap_fails()
    {
        static IEnumerable<CoroutineYield> Forever(int depth)
        {
            yield return CoroutineYield.Into(new Script($"level {depth + 1}", Forever(depth + 1)));
        }

        var pump = new CoroutinePump(new Script("level 0", Forever(0)));
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
        Assert.Contains($"nested deeper than {CoroutinePump.MaxDepth}", pump.Failure, StringComparison.Ordinal);
        Assert.Equal(CoroutinePump.MaxDepth, pump.Deepest);
    }

    [Fact]
    public void A_frame_runs_at_most_its_budget_of_stretches()
    {
        static IEnumerable<CoroutineYield> Children()
        {
            for (int i = 0; i < 100; i++)
            {
                yield return CoroutineYield.Into(new Script($"child {i}", Array.Empty<CoroutineYield>()));
            }
        }

        var pump = new CoroutinePump(new Script("root", Children()));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePump.MaxMovesPerTick, pump.Moves);
        while (pump.Tick(0, 0) == CoroutinePumpState.Running)
        {
        }

        Assert.Equal(CoroutinePumpState.Done, pump.State);
        Assert.Equal(201, pump.Moves);
    }

    [Fact]
    public void An_exception_fails_it_with_where_and_how_far()
    {
        IEnumerable<CoroutineYield> Throws()
        {
            yield return CoroutineYield.NextFrame();
            throw new InvalidOperationException("no attainable node");
        }

        var pump = new CoroutinePump(new Script("walker", Throws()));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
        Assert.Contains("InvalidOperationException: no attainable node", pump.Failure, StringComparison.Ordinal);
        Assert.Contains("in walker, depth 1, after 2 stretches", pump.Failure, StringComparison.Ordinal);
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
    }

    [Fact]
    public void A_failure_from_an_exception_never_holds_a_full_path()
    {
        // The failure goes into the tour's log line: a file error's message names the file by its full path, which names the
        // user's folder. Only the file's name is kept, as every exception a log line writes.
        IEnumerable<CoroutineYield> Throws()
        {
            yield return CoroutineYield.NextFrame();
            throw new System.IO.FileNotFoundException(@"Could not find file 'C:\Users\Alex\AppData\LocalLow\Game\RunDataV2.es3'.");
        }

        var pump = new CoroutinePump(new Script("walker", Throws()));
        pump.Tick(0, 0);
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
        Assert.Contains(@"FileNotFoundException: Could not find file '<folder>\RunDataV2.es3'.", pump.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("Alex", pump.Failure, StringComparison.Ordinal);

        var waiting = new CoroutinePump(new Script("root", One(CoroutineYield.While(() => throw new UnauthorizedAccessException(@"Access to the path 'C:\Users\Alex\x' is denied."), "WaitUntil"))));
        waiting.Tick(0, 0);
        Assert.Equal(CoroutinePumpState.Failed, waiting.Tick(0, 0));
        Assert.DoesNotContain("Alex", waiting.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_coroutines_own_failure_callback_fails_it_even_from_inside_a_stretch()
    {
        CoroutinePump? pump = null;
        IEnumerable<CoroutineYield> Walker()
        {
            pump!.Fail("phase=WorldMap TIMEOUT waiting for the preview");
            yield return CoroutineYield.NextFrame();
            throw new InvalidOperationException("never reached");
        }

        pump = new CoroutinePump(new Script("walker", Walker()));
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
        Assert.StartsWith("phase=WorldMap TIMEOUT waiting for the preview", pump.Failure, StringComparison.Ordinal);
        pump.Fail("a second reason");
        Assert.DoesNotContain("second", pump.Failure, StringComparison.Ordinal); // the first reason wins
    }

    [Fact]
    public void A_throwing_wait_question_fails_it()
    {
        var pump = new CoroutinePump(new Script("root", One(CoroutineYield.While(() => throw new InvalidOperationException("gone"), "WaitUntil"))));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
        Assert.Contains("its wait's question threw InvalidOperationException: gone", pump.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stop_question_stops_it_before_the_next_stretch()
    {
        bool stop = false;
        var script = new Script("root", Frames(10));
        var pump = new CoroutinePump(script, () => stop);
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        stop = true;
        Assert.Equal(CoroutinePumpState.Stopped, pump.Tick(0, 0));
        Assert.Single(script.Log);
        stop = false;
        Assert.Equal(CoroutinePumpState.Stopped, pump.Tick(0, 0));
        Assert.Single(script.Log);
    }

    [Fact]
    public void A_stop_question_that_throws_fails_it()
    {
        var pump = new CoroutinePump(new Script("root", Frames(2)), () => throw new InvalidOperationException("the tour's gone"));
        Assert.Equal(CoroutinePumpState.Failed, pump.Tick(0, 0));
        Assert.Contains("its stop question threw", pump.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_yields_are_one_frame_each_and_listed_once()
    {
        IEnumerable<CoroutineYield> Odd()
        {
            yield return CoroutineYield.Unknown("Coroutine");
            yield return CoroutineYield.Unknown("Coroutine");
            yield return CoroutineYield.Unknown("WaitForFixedUpdate");
        }

        var pump = new CoroutinePump(new Script("root", Odd()));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Running, pump.Tick(0, 0));
        Assert.Equal(CoroutinePumpState.Done, pump.Tick(0, 0));
        Assert.Equal(new[] { "Coroutine", "WaitForFixedUpdate" }, pump.Unknowns);
    }

    [Fact]
    public void A_long_failure_is_cut()
    {
        var pump = new CoroutinePump(new Script("root", Frames(1)));
        pump.Fail(new string('x', 1000));
        Assert.True(pump.Failure.Length <= 300);
        Assert.EndsWith("...", pump.Failure, StringComparison.Ordinal);
    }
}
