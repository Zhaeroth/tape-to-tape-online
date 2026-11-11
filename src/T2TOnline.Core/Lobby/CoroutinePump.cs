using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Lobby;

/// <summary>What a coroutine yielded, in the kinds the pump knows.</summary>
public enum CoroutineYieldKind
{
    /// <summary>Nothing (<c>yield return null</c>): the next frame.</summary>
    NextFrame,

    /// <summary><c>WaitForSeconds</c>: seconds of the game's scaled time.</summary>
    Seconds,

    /// <summary><c>WaitForSecondsRealtime</c>: seconds of real time.</summary>
    RealtimeSeconds,

    /// <summary>A <c>CustomYieldInstruction</c> (or anything else with a "still waiting" question): asked once a frame.</summary>
    KeepWaiting,

    /// <summary>Another coroutine: run inside this one, which goes on once it ends.</summary>
    Nested,

    /// <summary>Something the pump can't wait on (a <c>Coroutine</c> another object runs, <c>WaitForFixedUpdate</c>): one frame, and listed.</summary>
    Unknown,
}

/// <summary>What a coroutine yielded, read from the game by the plugin (plain data, so the pump can be tested).</summary>
public readonly struct CoroutineYield
{
    private CoroutineYield(CoroutineYieldKind kind, double seconds, Func<bool>? keepWaiting, ICoroutineFrame? nested, string what)
    {
        Kind = kind;
        Seconds = seconds;
        KeepWaiting = keepWaiting;
        Nested = nested;
        What = what;
    }

    /// <summary>The kind.</summary>
    public CoroutineYieldKind Kind { get; }

    /// <summary>The seconds of a <see cref="CoroutineYieldKind.Seconds"/> or <see cref="CoroutineYieldKind.RealtimeSeconds"/> wait.</summary>
    public double Seconds { get; }

    /// <summary>A <see cref="CoroutineYieldKind.KeepWaiting"/> wait's question: true while it still waits.</summary>
    public Func<bool>? KeepWaiting { get; }

    /// <summary>A <see cref="CoroutineYieldKind.Nested"/> coroutine.</summary>
    public ICoroutineFrame? Nested { get; }

    /// <summary>What it is, for the log (the game's type name for an unknown one).</summary>
    public string What { get; }

    /// <summary>The next frame.</summary>
    public static CoroutineYield NextFrame(string what = "a frame") => new(CoroutineYieldKind.NextFrame, 0, null, null, what);

    /// <summary><paramref name="seconds"/> of scaled time.</summary>
    public static CoroutineYield Wait(double seconds) => new(CoroutineYieldKind.Seconds, seconds, null, null, "WaitForSeconds");

    /// <summary><paramref name="seconds"/> of real time.</summary>
    public static CoroutineYield WaitRealtime(double seconds) => new(CoroutineYieldKind.RealtimeSeconds, seconds, null, null, "WaitForSecondsRealtime");

    /// <summary>For as long as <paramref name="keepWaiting"/> says so, asked once a frame from the next.</summary>
    public static CoroutineYield While(Func<bool> keepWaiting, string what)
    {
        ArgumentNullException.ThrowIfNull(keepWaiting);
        return new(CoroutineYieldKind.KeepWaiting, 0, keepWaiting, null, what ?? string.Empty);
    }

    /// <summary><paramref name="nested"/>, run to its end first.</summary>
    public static CoroutineYield Into(ICoroutineFrame nested)
    {
        ArgumentNullException.ThrowIfNull(nested);
        return new(CoroutineYieldKind.Nested, 0, null, nested, nested.Name);
    }

    /// <summary>Something of type <paramref name="typeName"/> the pump can't wait on: one frame.</summary>
    public static CoroutineYield Unknown(string typeName) => new(CoroutineYieldKind.Unknown, 0, null, null, typeName ?? "?");
}

/// <summary>One coroutine for the pump: the plugin wraps a game's <c>IEnumerator</c>; tests use plain ones.</summary>
public interface ICoroutineFrame
{
    /// <summary>Its name, for the log.</summary>
    string Name { get; }

    /// <summary>What the last <see cref="MoveNext"/> yielded.</summary>
    CoroutineYield Current { get; }

    /// <summary>Runs it to its next yield; false when it has ended. May throw (the pump fails then).</summary>
    bool MoveNext();
}

/// <summary>Where a pumped coroutine is.</summary>
public enum CoroutinePumpState
{
    /// <summary>Still running: tick it again next frame.</summary>
    Running,

    /// <summary>It ended by itself.</summary>
    Done,

    /// <summary>It threw, its failure callback said so, or it nested too deep: <see cref="CoroutinePump.Failure"/> says why.</summary>
    Failed,

    /// <summary>The stop predicate turned true: it isn't run any further.</summary>
    Stopped,
}

/// <summary>
/// Drives a coroutine one frame at a time from code that isn't a MonoBehaviour (the check tour's own frames), as Unity
/// would run it: a stack of coroutines, the top one run to its next yield once a frame; a nested coroutine pushed and run
/// at once, its parent going on in the frame it ends; <c>WaitForSeconds</c> by the game's scaled clock,
/// <c>WaitForSecondsRealtime</c> by real time, a <c>CustomYieldInstruction</c> while it says it still waits. An exception,
/// the coroutine's own failure callback (<see cref="Fail"/>) or a stack deeper than <see cref="MaxDepth"/> fails it; the
/// stop predicate stops it before the next stretch. Never throws from <see cref="Tick"/>. One thread.
/// </summary>
public sealed class CoroutinePump
{
    /// <summary>The deepest stack of nested coroutines taken.</summary>
    public const int MaxDepth = 16;

    /// <summary>The most stretches run in one frame (nested starts and ends chain within a frame); the rest go on next frame.</summary>
    public const int MaxMovesPerTick = 64;

    /// <summary>The most different unknown yields listed.</summary>
    public const int MaxUnknowns = 8;

    private const int MaxFailureLength = 300;

    private readonly Stack<ICoroutineFrame> _stack = new();
    private readonly Func<bool>? _stop;
    private readonly List<string> _unknowns = new();
    private CoroutineYieldKind _waitKind = CoroutineYieldKind.NextFrame;
    private double _waitUntil;
    private Func<bool>? _keepWaiting;

    /// <summary>A pump for <paramref name="root"/>; <paramref name="stop"/>, when given, is asked before each frame's stretch.</summary>
    public CoroutinePump(ICoroutineFrame root, Func<bool>? stop = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _stack.Push(root);
        _stop = stop;
        Deepest = 1;
    }

    /// <summary>Where it is.</summary>
    public CoroutinePumpState State { get; private set; }

    /// <summary>Why it failed ("" until it does).</summary>
    public string Failure { get; private set; } = string.Empty;

    /// <summary>How many coroutines are on the stack now.</summary>
    public int Depth => _stack.Count;

    /// <summary>The deepest the stack has been.</summary>
    public int Deepest { get; private set; }

    /// <summary>Stretches run so far (<see cref="ICoroutineFrame.MoveNext"/> calls).</summary>
    public long Moves { get; private set; }

    /// <summary>Frames ticked so far.</summary>
    public long Ticks { get; private set; }

    /// <summary>The unknown yields met, by type name, each once (at most <see cref="MaxUnknowns"/>).</summary>
    public IReadOnlyList<string> Unknowns => _unknowns;

    /// <summary>The name of the coroutine on top, or "" once none is.</summary>
    public string Top => _stack.Count > 0 ? _stack.Peek().Name : string.Empty;

    /// <summary>
    /// The coroutine's failure callback: it fails with <paramref name="why"/> (the first reason wins) and isn't run any
    /// further. Safe to call from inside a stretch.
    /// </summary>
    public void Fail(string why)
    {
        if (State == CoroutinePumpState.Running)
        {
            State = CoroutinePumpState.Failed;
            Failure = Cut($"{why} (in {Top}, depth {Depth}, after {Moves} stretches)");
        }
    }

    /// <summary>
    /// One frame: <paramref name="seconds"/> is the game's scaled clock now, <paramref name="realtimeSeconds"/> real time.
    /// Runs what's due and says where it is. Once it isn't <see cref="CoroutinePumpState.Running"/>, nothing runs again.
    /// </summary>
    public CoroutinePumpState Tick(double seconds, double realtimeSeconds)
    {
        if (State != CoroutinePumpState.Running)
        {
            return State;
        }

        Ticks++;
        if (Asks(_stop, "its stop question"))
        {
            State = CoroutinePumpState.Stopped;
            return State;
        }

        if (State != CoroutinePumpState.Running || StillWaiting(seconds, realtimeSeconds))
        {
            return State;
        }

        for (int moves = 0; moves < MaxMovesPerTick; moves++)
        {
            ICoroutineFrame top = _stack.Peek();
            bool more;
            CoroutineYield yielded;
            try
            {
                Moves++;
                more = top.MoveNext();
                yielded = more ? top.Current : default;
            }
            catch (Exception ex)
            {
                Fail(SafeError.Text(ex)); // the type and message, any quoted path cut to its file name: the failure is logged
                return State;
            }

            if (State != CoroutinePumpState.Running)
            {
                return State; // its failure callback, from inside the stretch
            }

            if (!more)
            {
                _stack.Pop();
                if (_stack.Count == 0)
                {
                    State = CoroutinePumpState.Done;
                    return State;
                }

                continue; // the parent goes on from where it waited, this frame
            }

            switch (yielded.Kind)
            {
                case CoroutineYieldKind.Nested when yielded.Nested != null:
                    if (_stack.Count >= MaxDepth)
                    {
                        Fail($"coroutines nested deeper than {MaxDepth}");
                        return State;
                    }

                    _stack.Push(yielded.Nested);
                    Deepest = Math.Max(Deepest, _stack.Count);
                    continue; // a nested coroutine's first stretch runs at once
                case CoroutineYieldKind.Seconds:
                case CoroutineYieldKind.RealtimeSeconds:
                    double wait = double.IsFinite(yielded.Seconds) ? Math.Max(yielded.Seconds, 0) : 0;
                    _waitKind = yielded.Kind;
                    _waitUntil = (yielded.Kind == CoroutineYieldKind.Seconds ? seconds : realtimeSeconds) + wait;
                    return State;
                case CoroutineYieldKind.KeepWaiting when yielded.KeepWaiting != null:
                    _waitKind = CoroutineYieldKind.KeepWaiting;
                    _keepWaiting = yielded.KeepWaiting;
                    return State;
                case CoroutineYieldKind.Unknown:
                    if (_unknowns.Count < MaxUnknowns && !_unknowns.Contains(yielded.What))
                    {
                        _unknowns.Add(yielded.What);
                    }

                    return State;
                default:
                    return State;
            }
        }

        return State; // the frame's budget is spent: the rest next frame
    }

    /// <summary>True while the last yield's wait still runs (clearing it once over); a throwing question fails the pump.</summary>
    private bool StillWaiting(double seconds, double realtimeSeconds)
    {
        switch (_waitKind)
        {
            case CoroutineYieldKind.Seconds when seconds < _waitUntil:
            case CoroutineYieldKind.RealtimeSeconds when realtimeSeconds < _waitUntil:
                return true;
            case CoroutineYieldKind.KeepWaiting:
                if (Asks(_keepWaiting, "its wait's question"))
                {
                    return true;
                }

                break;
        }

        _waitKind = CoroutineYieldKind.NextFrame;
        _keepWaiting = null;
        return State != CoroutinePumpState.Running;
    }

    /// <summary>What <paramref name="question"/> answers (false for none); one that throws fails the pump and answers false.</summary>
    private bool Asks(Func<bool>? question, string what)
    {
        if (question == null)
        {
            return false;
        }

        try
        {
            return question();
        }
        catch (Exception ex)
        {
            Fail($"{what} threw {SafeError.Text(ex)}");
            return false;
        }
    }

    private static string Cut(string text) => text.Length <= MaxFailureLength ? text : string.Concat(text.AsSpan(0, MaxFailureLength - 3), "...");
}
