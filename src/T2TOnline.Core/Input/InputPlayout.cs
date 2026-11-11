using System;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Input;

/// <summary>
/// Host-side buffer for one friend's input stream (a pad's <see cref="PadState"/> frames, or a keyboard's
/// <see cref="KeyboardMouseState"/> frames). Frames arrive in bursts and out of order; <see cref="Next"/> plays them out
/// at a steady rate (one call per host input tick, 120 Hz) as close behind the newest frame as the friend's jitter
/// allows, so hold times such as pass power and slapshot charge come out exactly as the friend held them.
/// </summary>
/// <typeparam name="T">The frame type.</typeparam>
/// <remarks>
/// <list type="bullet">
/// <item>A frame that hasn't arrived yet (underrun) holds the last state for a tick, which adds one frame of delay.</item>
/// <item>A frame that was lost for good (newer ones exist) repeats the last state and moves on.</item>
/// <item>After each window without underruns, the frames that were spare even at the tightest moment are skipped,
/// keeping <c>minSpareFrames</c> in reserve. That is how delay comes back down once jitter calms.</item>
/// <item>A backlog beyond <c>maxBacklogFrames</c> (after a stall) is cut immediately.</item>
/// <item>Press counters are cumulative, so skipping or repeating frames never loses or duplicates a press.</item>
/// <item>If nothing arrives for <c>staleAfterTicks</c>, output goes neutral (counters kept, <see cref="IPlayoutFrame{T}.Released"/>)
/// so a silent friend's skater doesn't keep running.</item>
/// </list>
/// </remarks>
public class InputPlayout<T>
    where T : struct, IPlayoutFrame<T>
{
    private const int Capacity = 64;
    private const int MaxSkipPerWindow = 4;

    /// <summary>
    /// Packets in a row from far behind playout before it believes them (a quarter second at 120 Hz). One straggler is
    /// dropped: replaying old frames would play old presses again (their 2-bit counters going back read as new taps).
    /// </summary>
    private const int BehindBeforeResync = 30;

    private readonly T[] _states = new T[Capacity];
    private readonly uint[] _frames = new uint[Capacity];
    private readonly bool[] _filled = new bool[Capacity];
    private readonly uint[] _passedFrames = new uint[Capacity];
    private readonly T[] _passedStates = new T[Capacity];
    private int _passedCount;
    private readonly int _minSpare;
    private readonly int _maxBacklog;
    private readonly int _windowLength;
    private readonly int _staleAfterTicks;

    private uint _nextFrame;
    private uint _newestReceived;
    private T _last;
    private int _ticksSinceReceive;
    private int _behindInARow;
    private int _startHoldTicks;
    private int _windowTicks;
    private int _windowMinLead = int.MaxValue;
    private bool _windowHadUnderrun;
    private PadPlayoutStats _stats;

    /// <summary>Creates a playout buffer.</summary>
    /// <param name="minSpareFrames">Frames kept in reserve at the tightest moment, as a safety margin.</param>
    /// <param name="maxBacklogFrames">Buffered frames beyond which the backlog is cut at once.</param>
    /// <param name="windowFrames">Ticks of history behind each decision to cut delay.</param>
    /// <param name="staleAfterTicks">Ticks without any packet before output goes neutral.</param>
    public InputPlayout(int minSpareFrames = 1, int maxBacklogFrames = 16, int windowFrames = 120, int staleAfterTicks = 30)
    {
        if (minSpareFrames < 0 || maxBacklogFrames <= minSpareFrames || maxBacklogFrames > Capacity / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBacklogFrames), "Need 0 <= minSpare < maxBacklog <= 32 frames.");
        }

        if (windowFrames < 1 || staleAfterTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowFrames), "Window and stale limits must be positive.");
        }

        _minSpare = minSpareFrames;
        _maxBacklog = maxBacklogFrames;
        _windowLength = windowFrames;
        _staleAfterTicks = staleAfterTicks;
    }

    /// <summary>True once the first packet has arrived.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>Frames available beyond the one just played: the current playout delay, in frames.</summary>
    public int BufferedFrames => IsStarted ? Math.Max(0, SequenceMath.Distance(_nextFrame, _newestReceived) + 1) : 0;

    /// <summary>
    /// The friend's frame the last <see cref="Next"/> stood for: the frame played, or the lost frame its state was
    /// repeated for. Unchanged while playout holds for a late frame. Comparing these with host ticks shows how much
    /// holds and skips changed the friend's timing.
    /// </summary>
    public uint LastFrame { get; private set; }

    /// <summary>
    /// Every friend frame the last <see cref="Next"/> moved past, in order: the one played (or lost and repeated) and
    /// any skipped to cut delay, each with its state (a frame that never arrived repeats the one before it). Together
    /// they are the friend's input exactly as they held it, whatever playout showed.
    /// </summary>
    public ReadOnlySpan<uint> PassedFrames => _passedFrames.AsSpan(0, _passedCount);

    /// <summary>The states of <see cref="PassedFrames"/>.</summary>
    public ReadOnlySpan<T> PassedStates => _passedStates.AsSpan(0, _passedCount);

    /// <summary>True when no packet has arrived for longer than the stale limit.</summary>
    public bool IsStale => IsStarted && _ticksSinceReceive > _staleAfterTicks;

    /// <summary>Counters for the overlay and logs.</summary>
    public PadPlayoutStats Stats => _stats;

    /// <summary>Adds a decoded packet: frames oldest first, the last one being <paramref name="newestFrame"/>.</summary>
    public void Receive(uint newestFrame, ReadOnlySpan<T> framesOldestFirst)
    {
        if (framesOldestFirst.IsEmpty)
        {
            return;
        }

        if (!IsStarted)
        {
            Restart(newestFrame, framesOldestFirst.Length);
        }
        else if (SequenceMath.Distance(_nextFrame, newestFrame) is int jump && (jump >= Capacity / 2 || jump <= -Capacity / 2))
        {
            if (jump < 0 && ++_behindInARow < BehindBeforeResync)
            {
                _stats = _stats with { Stragglers = _stats.Stragglers + 1 };
                return;
            }

            _stats = _stats with { Resyncs = _stats.Resyncs + 1 };
            Restart(newestFrame, framesOldestFirst.Length);
        }

        _behindInARow = 0;

        _ticksSinceReceive = 0;
        if (SequenceMath.IsNewer(newestFrame, _newestReceived))
        {
            _newestReceived = newestFrame;
        }

        int count = framesOldestFirst.Length;
        for (int i = 0; i < count; i++)
        {
            uint frame = PadInputCodec.FrameNumberAt(newestFrame, count, i);
            int ahead = SequenceMath.Distance(_nextFrame, frame);
            if (ahead < 0 || ahead >= Capacity)
            {
                continue;
            }

            int slot = (int)(frame & (Capacity - 1));
            _frames[slot] = frame;
            _states[slot] = framesOldestFirst[i];
            _filled[slot] = true;
        }
    }

    /// <summary>Returns the state for this host tick. Call exactly once per tick.</summary>
    public T Next()
    {
        if (!IsStarted)
        {
            return default;
        }

        _ticksSinceReceive++;
        _passedCount = 0;
        int slot = (int)(_nextFrame & (Capacity - 1));
        if (_startHoldTicks > 0)
        {
            // Just (re)started with fewer frames than the spare margin: wait instead of starting with no margin.
            _startHoldTicks--;
        }
        else if (_filled[slot] && _frames[slot] == _nextFrame)
        {
            _last = _states[slot];
            _filled[slot] = false;
            LastFrame = _nextFrame;
            Pass(_nextFrame, _last);
            _nextFrame++;
            _stats = _stats with { Played = _stats.Played + 1 };
        }
        else if (SequenceMath.Distance(_nextFrame, _newestReceived) < 0)
        {
            // Not here yet: hold this tick. Playout falls one frame further behind, which is the extra delay.
            _windowHadUnderrun = true;
            _stats = _stats with { Underruns = _stats.Underruns + 1 };
        }
        else
        {
            // Lost for good: repeat the last state and move on.
            LastFrame = _nextFrame;
            Pass(_nextFrame, _last);
            _nextFrame++;
            _stats = _stats with { Missing = _stats.Missing + 1 };
        }

        Adapt();

        if (IsStale)
        {
            _stats = _stats with { StaleTicks = _stats.StaleTicks + 1 };
            return _last.Released();
        }

        return _last;
    }

    private void Adapt()
    {
        int lead = SequenceMath.Distance(_nextFrame, _newestReceived) + 1;
        if (lead > _maxBacklog)
        {
            Skip(lead - (_minSpare + 1));
            lead = _minSpare + 1;
        }

        _windowMinLead = Math.Min(_windowMinLead, lead);
        if (++_windowTicks < _windowLength)
        {
            return;
        }

        if (!_windowHadUnderrun && _windowMinLead > _minSpare)
        {
            Skip(Math.Min(MaxSkipPerWindow, _windowMinLead - _minSpare));
        }

        _windowTicks = 0;
        _windowMinLead = int.MaxValue;
        _windowHadUnderrun = false;
    }

    private void Skip(int frames)
    {
        T state = _passedCount > 0 ? _passedStates[_passedCount - 1] : _last;
        for (int i = 0; i < frames; i++)
        {
            uint frame = unchecked(_nextFrame + (uint)i);
            int slot = (int)(frame & (Capacity - 1));
            if (_filled[slot] && _frames[slot] == frame)
            {
                state = _states[slot];
            }

            Pass(frame, state);
        }

        _nextFrame = unchecked(_nextFrame + (uint)frames);
        _stats = _stats with { Skipped = _stats.Skipped + frames };
    }

    private void Pass(uint frame, in T state)
    {
        if (_passedCount < Capacity)
        {
            _passedFrames[_passedCount] = frame;
            _passedStates[_passedCount] = state;
            _passedCount++;
        }
    }

    private void Restart(uint newestFrame, int framesInPacket)
    {
        // Start playout with the minimum spare behind the newest frame. Never start before the oldest frame we
        // actually have; if the packet is too short for the margin, wait the difference instead.
        Array.Clear(_filled, 0, Capacity);
        _newestReceived = newestFrame;
        int spareAvailable = Math.Min(_minSpare, framesInPacket - 1);
        _nextFrame = unchecked(newestFrame - (uint)spareAvailable);
        LastFrame = unchecked(_nextFrame - 1);
        _startHoldTicks = _minSpare - spareAvailable;
        _windowTicks = 0;
        _windowMinLead = int.MaxValue;
        _windowHadUnderrun = false;
        IsStarted = true;
    }
}

/// <summary>Running counts from an <see cref="InputPlayout{T}"/> (a pad's or a keyboard's).</summary>
/// <param name="Played">Frames played on time.</param>
/// <param name="Missing">Frames lost for good and replaced by the previous state.</param>
/// <param name="Underruns">Ticks where the next frame hadn't arrived yet.</param>
/// <param name="Skipped">Frames skipped to cut delay.</param>
/// <param name="StaleTicks">Ticks played as neutral because the friend went silent.</param>
/// <param name="Resyncs">Times playout jumped because the stream moved too far.</param>
/// <param name="Stragglers">Packets dropped for arriving from far behind playout (their frames were played long ago).</param>
public readonly record struct PadPlayoutStats(long Played, long Missing, long Underruns, long Skipped, long StaleTicks, long Resyncs, long Stragglers = 0);
