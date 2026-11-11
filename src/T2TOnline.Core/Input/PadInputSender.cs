using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// The friend's side of the remote-pad path: samples the physical pad whenever input is polled, emits a frame for
/// every <see cref="FrameRate"/> Hz tick reached, and writes packets of the newest
/// <see cref="PadInputCodec.MaxFramesPerPacket"/> frames, so a lost packet costs nothing.
/// </summary>
public sealed class PadInputSender
{
    /// <summary>Pad frames per second.</summary>
    public const int FrameRate = 120;

    private readonly long _ticksPerSecond;
    private readonly PadSampler _sampler = new();
    private readonly PadState[] _history = new PadState[PadInputCodec.MaxFramesPerPacket];
    private long _start;
    private bool _started;

    /// <summary>Creates a sender whose timestamps run at <paramref name="ticksPerSecond"/>.</summary>
    public PadInputSender(long ticksPerSecond)
    {
        if (ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), "The clock rate must be positive.");
        }

        _ticksPerSecond = ticksPerSecond;
    }

    /// <summary>Frames emitted so far; the newest frame's number is one less.</summary>
    public uint FramesCaptured { get; private set; }

    /// <summary>
    /// Samples the pad at <paramref name="now"/>. Returns true when at least one new frame was emitted, which is when
    /// a packet should go out (<see cref="WritePacket"/>). Presses between frames still count.
    /// </summary>
    public bool Capture(long now, PadButtons held, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
    {
        if (!_started)
        {
            _start = now;
            _started = true;
        }

        _sampler.ObserveButtons(held);
        long due = ((now - _start) * FrameRate / _ticksPerSecond) + 1;
        if (FramesCaptured >= due)
        {
            return false;
        }

        // After a stall, only a packet's worth of frames is filled in: older ones would never be sent anyway.
        if (due - FramesCaptured > _history.Length)
        {
            FramesCaptured = (uint)(due - _history.Length);
        }

        PadState frame = _sampler.Capture(leftX, leftY, rightX, rightY, leftTrigger, rightTrigger);
        while (FramesCaptured < due)
        {
            Array.Copy(_history, 1, _history, 0, _history.Length - 1);
            _history[^1] = frame;
            FramesCaptured++;
        }

        return true;
    }

    /// <summary>Writes the newest frames (up to <see cref="PadInputCodec.MaxFramesPerPacket"/>) as one packet.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been captured yet.</exception>
    public void WritePacket(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (FramesCaptured == 0)
        {
            throw new InvalidOperationException("Nothing captured yet.");
        }

        int count = (int)Math.Min(FramesCaptured, _history.Length);
        PadInputCodec.Write(writer, FramesCaptured - 1, _history.AsSpan(_history.Length - count));
    }

    /// <summary>Forgets held buttons, for example when the pad disconnects. Press counters keep counting.</summary>
    public void ReleaseAll() => _sampler.ReleaseAll();
}
