using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// Encodes the newest keyboard-and-mouse samples into one packet, as <see cref="PadInputCodec"/> does for pads: each
/// packet repeats up to <see cref="MaxFramesPerPacket"/> consecutive frames, delta-coded against the frame before them
/// (an unchanged frame costs one bit). A changed frame is the held mask (one bit per element of the key table) and the
/// press counters (two bits per element). Both ends know the table's size from the friend's
/// <see cref="InputBindingSet"/>, so it isn't sent.
/// </summary>
public static class KeyboardMouseCodec
{
    /// <summary>Most frames one packet carries.</summary>
    public const int MaxFramesPerPacket = PadInputCodec.MaxFramesPerPacket;

    private const int FrameCountBits = 3;

    /// <summary>
    /// Writes <paramref name="framesOldestFirst"/> of a table of <paramref name="elementCount"/> elements, whose last element
    /// is frame <paramref name="newestFrame"/> and whose earlier elements are the frames immediately before it.
    /// </summary>
    public static void Write(BitWriter writer, int elementCount, uint newestFrame, ReadOnlySpan<KeyboardMouseState> framesOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (elementCount is < 1 or > KeyboardMouseState.MaxElements)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount), $"A key table has 1 to {KeyboardMouseState.MaxElements} elements.");
        }

        if (framesOldestFirst.Length is < 1 or > MaxFramesPerPacket)
        {
            throw new ArgumentOutOfRangeException(nameof(framesOldestFirst), $"A packet carries 1 to {MaxFramesPerPacket} frames.");
        }

        writer.WriteUInt32(newestFrame);
        writer.WriteBits((ulong)(framesOldestFirst.Length - 1), FrameCountBits);
        KeyboardMouseState previous = KeyboardMouseState.Neutral;
        foreach (KeyboardMouseState state in framesOldestFirst)
        {
            KeyboardMouseState frame = state.Masked(elementCount);
            if (frame.Equals(previous))
            {
                writer.WriteBool(false);
                continue;
            }

            writer.WriteBool(true);
            writer.WriteBits(frame.Held, elementCount);
            int low = Math.Min(elementCount, KeyboardMouseState.ElementsPerCounterWord);
            writer.WriteBits(frame.CountersLow, low * KeyboardMouseState.PressCounterBits);
            int high = elementCount - low;
            if (high > 0)
            {
                writer.WriteBits(frame.CountersHigh, high * KeyboardMouseState.PressCounterBits);
            }

            previous = frame;
        }
    }

    /// <summary>
    /// Reads a packet of a table of <paramref name="elementCount"/> elements into <paramref name="destination"/> (oldest
    /// first). Returns false, writing nothing useful, if the packet is malformed, the element count is out of range or
    /// <paramref name="destination"/> is too small. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, int elementCount, Span<KeyboardMouseState> destination, out uint newestFrame, out int frameCount)
    {
        newestFrame = reader.ReadUInt32();
        frameCount = (int)reader.ReadBits(FrameCountBits) + 1;
        if (reader.IsOverrun || elementCount is < 1 or > KeyboardMouseState.MaxElements || frameCount > destination.Length)
        {
            frameCount = 0;
            return false;
        }

        int low = Math.Min(elementCount, KeyboardMouseState.ElementsPerCounterWord);
        int high = elementCount - low;
        KeyboardMouseState previous = KeyboardMouseState.Neutral;
        for (int i = 0; i < frameCount; i++)
        {
            if (reader.ReadBool())
            {
                ulong held = reader.ReadBits(elementCount);
                ulong countersLow = reader.ReadBits(low * KeyboardMouseState.PressCounterBits);
                ulong countersHigh = high > 0 ? reader.ReadBits(high * KeyboardMouseState.PressCounterBits) : 0;
                previous = new KeyboardMouseState(held, countersLow, countersHigh);
            }

            destination[i] = previous;
        }

        if (reader.IsOverrun)
        {
            frameCount = 0;
            return false;
        }

        return true;
    }
}
