using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// Encodes the newest pad samples into one packet. Each packet repeats up to
/// <see cref="MaxFramesPerPacket"/> consecutive frames, so a lost packet costs nothing as long as a later one arrives.
/// Frames are delta-coded against the frame before them; an unchanged frame costs one bit.
/// </summary>
public static class PadInputCodec
{
    /// <summary>Most frames one packet carries.</summary>
    public const int MaxFramesPerPacket = 8;

    private const int FrameCountBits = 3;
    private const int ButtonBits = PadState.ButtonCount;
    private const int CounterBits = PadState.ButtonCount * PadState.PressCounterBits;

    /// <summary>
    /// Writes <paramref name="framesOldestFirst"/>, whose last element is frame <paramref name="newestFrame"/>
    /// and whose earlier elements are the frames immediately before it.
    /// </summary>
    public static void Write(BitWriter writer, uint newestFrame, ReadOnlySpan<PadState> framesOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (framesOldestFirst.Length is < 1 or > MaxFramesPerPacket)
        {
            throw new ArgumentOutOfRangeException(nameof(framesOldestFirst), $"A packet carries 1 to {MaxFramesPerPacket} frames.");
        }

        writer.WriteUInt32(newestFrame);
        writer.WriteBits((ulong)(framesOldestFirst.Length - 1), FrameCountBits);

        PadState previous = PadState.Neutral;
        foreach (PadState frame in framesOldestFirst)
        {
            WriteDelta(writer, previous, frame);
            previous = frame;
        }
    }

    /// <summary>
    /// Reads a packet into <paramref name="destination"/> (oldest first). Returns false, writing nothing useful,
    /// if the packet is malformed or <paramref name="destination"/> is too small.
    /// </summary>
    public static bool TryRead(ref BitReader reader, Span<PadState> destination, out uint newestFrame, out int frameCount)
    {
        newestFrame = reader.ReadUInt32();
        frameCount = (int)reader.ReadBits(FrameCountBits) + 1;
        if (reader.IsOverrun || frameCount > destination.Length)
        {
            frameCount = 0;
            return false;
        }

        PadState previous = PadState.Neutral;
        for (int i = 0; i < frameCount; i++)
        {
            previous = ReadDelta(ref reader, previous);
            destination[i] = previous;
        }

        if (reader.IsOverrun)
        {
            frameCount = 0;
            return false;
        }

        return true;
    }

    /// <summary>The frame number of the element at <paramref name="index"/> in a decoded packet.</summary>
    public static uint FrameNumberAt(uint newestFrame, int frameCount, int index) =>
        unchecked(newestFrame - (uint)(frameCount - 1 - index));

    private static void WriteDelta(BitWriter writer, in PadState previous, in PadState frame)
    {
        if (frame.Equals(previous))
        {
            writer.WriteBool(false);
            return;
        }

        writer.WriteBool(true);

        bool digital = !frame.DigitalEquals(previous);
        writer.WriteBool(digital);
        if (digital)
        {
            writer.WriteBits((ulong)frame.Buttons, ButtonBits);
            writer.WriteBits(frame.PressCounters, CounterBits);
        }

        bool sticks = !frame.SticksEqual(previous);
        writer.WriteBool(sticks);
        if (sticks)
        {
            writer.WriteInt16(frame.LeftStickX);
            writer.WriteInt16(frame.LeftStickY);
            writer.WriteInt16(frame.RightStickX);
            writer.WriteInt16(frame.RightStickY);
        }

        bool triggers = !frame.TriggersEqual(previous);
        writer.WriteBool(triggers);
        if (triggers)
        {
            writer.WriteUInt16(frame.LeftTrigger);
            writer.WriteUInt16(frame.RightTrigger);
        }
    }

    private static PadState ReadDelta(ref BitReader reader, in PadState previous)
    {
        if (!reader.ReadBool())
        {
            return previous;
        }

        PadButtons buttons = previous.Buttons;
        uint counters = previous.PressCounters;
        if (reader.ReadBool())
        {
            buttons = (PadButtons)reader.ReadBits(ButtonBits);
            counters = (uint)reader.ReadBits(CounterBits);
        }

        short lx = previous.LeftStickX, ly = previous.LeftStickY, rx = previous.RightStickX, ry = previous.RightStickY;
        if (reader.ReadBool())
        {
            lx = reader.ReadInt16();
            ly = reader.ReadInt16();
            rx = reader.ReadInt16();
            ry = reader.ReadInt16();
        }

        ushort lt = previous.LeftTrigger, rt = previous.RightTrigger;
        if (reader.ReadBool())
        {
            lt = reader.ReadUInt16();
            rt = reader.ReadUInt16();
        }

        return new PadState(buttons, counters, lx, ly, rx, ry, lt, rt);
    }
}
