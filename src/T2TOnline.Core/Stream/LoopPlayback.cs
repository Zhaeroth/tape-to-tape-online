using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Stream;

/// <summary>What a friend's PC does with one <see cref="SoundLoopMessage"/> (<see cref="LoopPlayback.Apply"/>).</summary>
public enum LoopStep
{
    /// <summary>Nothing (a move or stop for a loop not playing here, a start over the limit).</summary>
    None,

    /// <summary>Start the loop's sound here.</summary>
    Start,

    /// <summary>Move the loop's sound (a start again for a loop already playing moves it too).</summary>
    Move,

    /// <summary>Stop the loop's sound.</summary>
    Stop,
}

/// <summary>
/// A friend's side of the host's looping gameplay sounds (<see cref="SoundLoopMessage"/>): which loops play here, by the
/// host's handle, and when one must stop although no stop came. A start for a loop not playing starts it (at most
/// <see cref="MaxLoops"/> at once); a start again or a move for one playing moves it and counts as word from the host; a
/// stop stops it. The host says a playing loop's start again every <see cref="LoopTracker.RefreshEveryMicros"/>, so a
/// loop with no word for <see cref="SilenceMicros"/> lost its stop (<see cref="TakeSilent"/>). Times are on the caller's
/// clock, which should run only while the stream does (a stalled or paused stream isn't silence from the host).
/// Game-independent; never throws on what the host sends.
/// </summary>
public sealed class LoopPlayback
{
    /// <summary>The most loops playing at once.</summary>
    public const int MaxLoops = LoopTracker.MaxLoops;

    /// <summary>How long a loop plays with no word from the host before it's stopped.</summary>
    public const long SilenceMicros = 5_000_000;

    private readonly Dictionary<uint, (LoopKind Kind, long HeardAt)> _playing = new();
    private readonly int[] _started = new int[LoopKinds.Count];
    private readonly List<uint> _scratch = new();

    /// <summary>Loops playing now.</summary>
    public int Count => _playing.Count;

    /// <summary>Loops started since the last <see cref="Reset"/>.</summary>
    public int Started => _started.Sum();

    /// <summary>Moves (and starts again) of loops playing.</summary>
    public int Moves { get; private set; }

    /// <summary>Loops the host stopped.</summary>
    public int StoppedByHost { get; private set; }

    /// <summary>Loops stopped for <see cref="SilenceMicros"/> without word from the host.</summary>
    public int Silenced { get; private set; }

    /// <summary>Starts refused for <see cref="MaxLoops"/> playing already.</summary>
    public int Refused { get; private set; }

    /// <summary>Moves and stops for a loop not playing here (it was refused, or stopped already, or its start is still on its way).</summary>
    public int Unknown { get; private set; }

    /// <summary>The kind of a loop playing, or false.</summary>
    public bool TryGetKind(uint handle, out LoopKind kind)
    {
        bool found = _playing.TryGetValue(handle, out (LoopKind Kind, long HeardAt) entry);
        kind = entry.Kind;
        return found;
    }

    /// <summary>What to do with <paramref name="message"/>, heard at <paramref name="nowMicros"/>.</summary>
    public LoopStep Apply(in SoundLoopMessage message, long nowMicros)
    {
        bool playing = _playing.TryGetValue(message.Handle, out (LoopKind Kind, long HeardAt) entry);
        switch (message.Op)
        {
            case SoundLoopOp.Start when playing:
                _playing[message.Handle] = (entry.Kind, nowMicros);
                Moves++;
                return LoopStep.Move;
            case SoundLoopOp.Start when (byte)message.Kind >= LoopKinds.Count:
                Unknown++;
                return LoopStep.None;
            case SoundLoopOp.Start when _playing.Count >= MaxLoops:
                Refused++;
                return LoopStep.None;
            case SoundLoopOp.Start:
                _playing[message.Handle] = (message.Kind, nowMicros);
                _started[(byte)message.Kind]++;
                return LoopStep.Start;
            case SoundLoopOp.Move when playing:
                _playing[message.Handle] = (entry.Kind, nowMicros);
                Moves++;
                return LoopStep.Move;
            case SoundLoopOp.Stop when playing:
                _playing.Remove(message.Handle);
                StoppedByHost++;
                return LoopStep.Stop;
            default:
                Unknown++;
                return LoopStep.None;
        }
    }

    /// <summary>The loop's start failed here: it isn't playing (later moves and its stop count as unknown).</summary>
    public void Forget(uint handle)
    {
        if (_playing.Remove(handle, out (LoopKind Kind, long HeardAt) entry))
        {
            _started[(byte)entry.Kind]--;
        }
    }

    /// <summary>
    /// The loops with no word from the host for <see cref="SilenceMicros"/> at <paramref name="nowMicros"/>, added to
    /// <paramref name="handles"/>: they're no longer playing (stop them).
    /// </summary>
    public void TakeSilent(long nowMicros, List<uint> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        _scratch.Clear();
        foreach (KeyValuePair<uint, (LoopKind Kind, long HeardAt)> pair in _playing)
        {
            if (nowMicros - pair.Value.HeardAt >= SilenceMicros)
            {
                _scratch.Add(pair.Key);
            }
        }

        foreach (uint handle in _scratch)
        {
            _playing.Remove(handle);
            Silenced++;
            handles.Add(handle);
        }
    }

    /// <summary>Every loop playing, added to <paramref name="handles"/>: none is playing any more (stop them all).</summary>
    public void TakeAll(List<uint> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        handles.AddRange(_playing.Keys);
        _playing.Clear();
    }

    /// <summary>Forgets everything and every count (a new playback).</summary>
    public void Reset()
    {
        _playing.Clear();
        Array.Clear(_started);
        Moves = StoppedByHost = Silenced = Refused = Unknown = 0;
    }

    /// <summary>For the log: "magneto 3, retro rocket 1", or "none".</summary>
    public string DescribeStarted()
    {
        IEnumerable<string> kinds = Enumerable.Range(0, LoopKinds.Count).Where(k => _started[k] > 0).Select(k => $"{LoopKinds.Name((LoopKind)k)} {_started[k]}");
        return Started == 0 ? "none" : string.Join(", ", kinds);
    }
}
