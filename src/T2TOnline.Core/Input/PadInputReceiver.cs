using System;
using System.Numerics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// The host's side of the remote-pad path for one friend: decodes packets into a <see cref="PadPlayout"/> and plays it
/// out at <see cref="PadInputSender.FrameRate"/> Hz on the host's clock, turning sub-frame taps into one-update presses
/// (<see cref="PadTaps"/>). <see cref="Holds"/> compares each hold's length in the friend's frames with its length in
/// played ticks.
/// </summary>
public sealed class PadInputReceiver : IInputReceiver
{
    /// <summary>Most ticks played in one update after a stall; older ticks are dropped, not replayed in a burst.</summary>
    public const int MaxCatchUpTicks = 16;

    private readonly long _ticksPerSecond;
    private readonly PadState[] _received = new PadState[PadInputCodec.MaxFramesPerPacket];
    private long _start;
    private bool _started;
    private long _ticksPlayed;
    private PadState _played;

    /// <summary>Creates a receiver whose timestamps run at <paramref name="ticksPerSecond"/>.</summary>
    /// <param name="ticksPerSecond">Units per second of every timestamp passed in.</param>
    /// <param name="playout">The playout buffer to use; a default one if null.</param>
    public PadInputReceiver(long ticksPerSecond, PadPlayout? playout = null)
    {
        if (ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), "The clock rate must be positive.");
        }

        _ticksPerSecond = ticksPerSecond;
        Playout = playout ?? new PadPlayout();
    }

    /// <summary>The playout buffer, for its delay and counters.</summary>
    public PadPlayout Playout { get; }

    /// <summary>Hold lengths, friend frames against played ticks.</summary>
    public PlayoutHoldCheck Holds { get; } = new();

    /// <summary>Packets decoded.</summary>
    public long Packets { get; private set; }

    /// <summary>Packets that didn't decode.</summary>
    public long MalformedPackets { get; private set; }

    /// <summary>One-update presses injected for taps shorter than a frame.</summary>
    public long Taps { get; private set; }

    /// <summary>Ticks dropped after stalls longer than <see cref="MaxCatchUpTicks"/>.</summary>
    public long DroppedTicks { get; private set; }

    /// <summary>The state last played.</summary>
    public PadState Current => _played;

    /// <inheritdoc />
    public int BufferedFrames => Playout.BufferedFrames;

    /// <inheritdoc />
    public PadPlayoutStats PlayoutStats => Playout.Stats;

    /// <summary>Decodes one packet from <paramref name="reader"/>. False, adding nothing, if it's malformed.</summary>
    public bool Receive(ref BitReader reader)
    {
        if (!PadInputCodec.TryRead(ref reader, _received, out uint newest, out int count))
        {
            MalformedPackets++;
            return false;
        }

        Packets++;
        Playout.Receive(newest, _received.AsSpan(0, count));
        return true;
    }

    /// <summary>
    /// Plays out every tick reached by <paramref name="now"/>. Returns the state to apply and any presses too short to
    /// show in it (apply those for one update). Neutral until the first packet has arrived.
    /// </summary>
    public (PadState State, PadButtons Taps) Update(long now)
    {
        if (!Playout.IsStarted)
        {
            return (PadState.Neutral, PadButtons.None);
        }

        if (!_started)
        {
            _start = now;
            _started = true;
        }

        long due = ((now - _start) * PadInputSender.FrameRate / _ticksPerSecond) + 1;
        if (due - _ticksPlayed > MaxCatchUpTicks)
        {
            DroppedTicks += due - _ticksPlayed - MaxCatchUpTicks;
            _ticksPlayed = due - MaxCatchUpTicks;
        }

        PadButtons taps = PadButtons.None;
        while (_ticksPlayed < due)
        {
            PadState next = Playout.Next();
            taps |= PadTaps.Between(_played, next);
            _played = next;
            ReadOnlySpan<uint> frames = Playout.PassedFrames;
            ReadOnlySpan<PadState> states = Playout.PassedStates;
            for (int i = 0; i < frames.Length; i++)
            {
                Holds.ObserveFriendFrame(frames[i], states[i].Buttons);
            }

            Holds.ObservePlayedTick(_ticksPlayed, Playout.LastFrame, next.Buttons);
            _ticksPlayed++;
        }

        Taps += BitOperations.PopCount((uint)taps);
        return (_played, taps);
    }
}
