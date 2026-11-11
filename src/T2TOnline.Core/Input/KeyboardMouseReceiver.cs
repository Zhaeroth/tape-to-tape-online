using System;
using System.Numerics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// The host's side of the keyboard-and-mouse path for one friend, as <see cref="PadInputReceiver"/> is for pads: decodes
/// packets into an <see cref="InputPlayout{T}"/> and plays it out at <see cref="PadInputSender.FrameRate"/> Hz on the host's
/// clock, turning sub-frame taps into one-update presses (<see cref="KeyboardMouseTaps"/>). Frames are read against the key
/// table of the friend's binding set (<see cref="UseTable"/>); packets of any other generation are counted
/// (<see cref="StaleGeneration"/>) and dropped, and until a table is in use the output is neutral.
/// </summary>
public sealed class KeyboardMouseReceiver : IInputReceiver
{
    /// <summary>Most ticks played in one update after a stall; older ticks are dropped, not replayed in a burst.</summary>
    public const int MaxCatchUpTicks = PadInputReceiver.MaxCatchUpTicks;

    private readonly long _ticksPerSecond;
    private readonly KeyboardMouseState[] _received = new KeyboardMouseState[KeyboardMouseCodec.MaxFramesPerPacket];
    private long _start;
    private bool _started;
    private long _ticksPlayed;
    private KeyboardMouseState _played;
    private bool _counting; // false until the first frame of the table in use is played (Update)

    /// <summary>Creates a receiver whose timestamps run at <paramref name="ticksPerSecond"/>.</summary>
    public KeyboardMouseReceiver(long ticksPerSecond)
    {
        if (ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), "The clock rate must be positive.");
        }

        _ticksPerSecond = ticksPerSecond;
        Playout = new InputPlayout<KeyboardMouseState>();
        Holds = new PlayoutHoldCheck(KeyboardMouseState.MaxElements);
    }

    /// <summary>The generation of the table in use, or 0 before one is.</summary>
    public byte Generation { get; private set; }

    /// <summary>Elements in the table in use, or 0 before one is.</summary>
    public int ElementCount { get; private set; }

    /// <summary>The playout buffer, for its delay and counters (a new one with each table).</summary>
    public InputPlayout<KeyboardMouseState> Playout { get; private set; }

    /// <inheritdoc />
    public PlayoutHoldCheck Holds { get; private set; }

    /// <inheritdoc />
    public long Packets { get; private set; }

    /// <inheritdoc />
    public long MalformedPackets { get; private set; }

    /// <summary>Packets for a generation other than the table in use (sent before the friend's bindings arrived, or after a change).</summary>
    public long StaleGeneration { get; private set; }

    /// <inheritdoc />
    public long Taps { get; private set; }

    /// <inheritdoc />
    public long DroppedTicks { get; private set; }

    /// <inheritdoc />
    public int BufferedFrames => Playout.BufferedFrames;

    /// <inheritdoc />
    public PadPlayoutStats PlayoutStats => Playout.Stats;

    /// <summary>The state last played.</summary>
    public KeyboardMouseState Current => _played;

    /// <summary>
    /// Reads frames against the table of generation <paramref name="generation"/> (1 to 255) with
    /// <paramref name="elementCount"/> elements (1 to 64) from now on. A different table starts playout and the hold check
    /// over, with nothing held; the same one again changes nothing.
    /// </summary>
    public void UseTable(byte generation, int elementCount)
    {
        if (generation == 0 || elementCount is < 1 or > KeyboardMouseState.MaxElements)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount), "Generation 1 to 255, 1 to 64 elements.");
        }

        if (generation == Generation && elementCount == ElementCount)
        {
            return;
        }

        Generation = generation;
        ElementCount = elementCount;
        Playout = new InputPlayout<KeyboardMouseState>();
        Holds = new PlayoutHoldCheck(elementCount);
        _played = KeyboardMouseState.Neutral;
        _started = false;
        _counting = false;
        _ticksPlayed = 0;
    }

    /// <summary>
    /// Decodes one packet (generation and frames) from <paramref name="reader"/>. False, adding nothing, if it's malformed;
    /// true for a packet of another generation too (counted in <see cref="StaleGeneration"/>, its frames dropped).
    /// </summary>
    public bool Receive(ref BitReader reader)
    {
        byte generation = reader.ReadByte();
        if (reader.IsOverrun || generation == 0)
        {
            MalformedPackets++;
            return false;
        }

        if (generation != Generation)
        {
            StaleGeneration++;
            return true;
        }

        if (!KeyboardMouseCodec.TryRead(ref reader, ElementCount, _received, out uint newest, out int count))
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
    /// show in it (apply those for one update). Neutral until the first packet of the table in use has arrived.
    /// </summary>
    public (KeyboardMouseState State, ulong Taps) Update(long now)
    {
        if (!Playout.IsStarted)
        {
            return (KeyboardMouseState.Neutral, 0);
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

        ulong taps = 0;
        while (_ticksPlayed < due)
        {
            KeyboardMouseState next = Playout.Next();
            if (!_counting && Playout.PassedFrames.Length > 0)
            {
                // The first frame of this table played: its counters hold every press since the friend's sender started,
                // made while this table wasn't in use yet (its frames dropped as stale, up to a second after a quick
                // rejoin) and played by no one since. They'd come out now as taps, an Escape or a Space long after it
                // was pressed; counting starts here instead. What's held now shows as held.
                _played = next.Released();
                _counting = true;
            }

            taps |= KeyboardMouseTaps.Between(_played, next, ElementCount);
            _played = next;
            ReadOnlySpan<uint> frames = Playout.PassedFrames;
            ReadOnlySpan<KeyboardMouseState> states = Playout.PassedStates;
            for (int i = 0; i < frames.Length; i++)
            {
                Holds.ObserveFriendFrame(frames[i], states[i].Held);
            }

            Holds.ObservePlayedTick(_ticksPlayed, Playout.LastFrame, next.Held);
            _ticksPlayed++;
        }

        Taps += BitOperations.PopCount(taps);
        return (_played, taps);
    }
}
