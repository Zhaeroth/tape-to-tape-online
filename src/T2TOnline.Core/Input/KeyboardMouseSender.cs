using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// The friend's side of the keyboard-and-mouse path, as <see cref="PadInputSender"/> is for pads: samples the friend's key
/// table whenever input is polled, emits a frame for every <see cref="PadInputSender.FrameRate"/> Hz tick reached, and
/// writes packets of the newest <see cref="KeyboardMouseCodec.MaxFramesPerPacket"/> frames (a lost packet costs nothing),
/// each tagged with the generation of the <see cref="InputBindingSet"/> whose table they use.
/// </summary>
public sealed class KeyboardMouseSender
{
    private readonly long _ticksPerSecond;
    private readonly KeyboardMouseSampler _sampler;
    private readonly KeyboardMouseState[] _history = new KeyboardMouseState[KeyboardMouseCodec.MaxFramesPerPacket];
    private long _start;
    private bool _started;

    /// <summary>Creates a sender whose timestamps run at <paramref name="ticksPerSecond"/>.</summary>
    /// <param name="ticksPerSecond">Units per second of every timestamp passed in.</param>
    /// <param name="generation">The binding set's generation (1 to 255) the frames are for.</param>
    /// <param name="elementCount">Elements in that set's key table (1 to 64).</param>
    public KeyboardMouseSender(long ticksPerSecond, byte generation, int elementCount)
    {
        if (ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), "The clock rate must be positive.");
        }

        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Generations start at 1.");
        }

        _ticksPerSecond = ticksPerSecond;
        _sampler = new KeyboardMouseSampler(elementCount);
        Generation = generation;
    }

    /// <summary>The binding set's generation the frames are for.</summary>
    public byte Generation { get; }

    /// <summary>Elements in the key table.</summary>
    public int ElementCount => _sampler.ElementCount;

    /// <summary>Frames emitted so far; the newest frame's number is one less.</summary>
    public uint FramesCaptured { get; private set; }

    /// <summary>Elements held, as last observed.</summary>
    public ulong Held => _sampler.Held;

    /// <summary>
    /// Samples the table at <paramref name="now"/>: <paramref name="held"/> held, <paramref name="pressed"/> went down since
    /// the last call. Returns true when at least one new frame was emitted, which is when a packet should go out
    /// (<see cref="WritePacket"/>). Presses between frames still count.
    /// </summary>
    public bool Capture(long now, ulong held, ulong pressed)
    {
        if (!_started)
        {
            _start = now;
            _started = true;
        }

        _sampler.Observe(held, pressed);
        long due = ((now - _start) * PadInputSender.FrameRate / _ticksPerSecond) + 1;
        if (FramesCaptured >= due)
        {
            return false;
        }

        // After a stall, only a packet's worth of frames is filled in: older ones would never be sent anyway.
        if (due - FramesCaptured > _history.Length)
        {
            FramesCaptured = (uint)(due - _history.Length);
        }

        KeyboardMouseState frame = _sampler.Capture();
        while (FramesCaptured < due)
        {
            Array.Copy(_history, 1, _history, 0, _history.Length - 1);
            _history[^1] = frame;
            FramesCaptured++;
        }

        return true;
    }

    /// <summary>Writes the generation and the newest frames (up to <see cref="KeyboardMouseCodec.MaxFramesPerPacket"/>) as one packet.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been captured yet.</exception>
    public void WritePacket(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (FramesCaptured == 0)
        {
            throw new InvalidOperationException("Nothing captured yet.");
        }

        int count = (int)Math.Min(FramesCaptured, _history.Length);
        writer.WriteByte(Generation);
        KeyboardMouseCodec.Write(writer, ElementCount, FramesCaptured - 1, _history.AsSpan(_history.Length - count));
    }

    /// <summary>Forgets held elements (focus lost, keyboard off). Press counters keep counting.</summary>
    public void ReleaseAll() => _sampler.ReleaseAll();
}
