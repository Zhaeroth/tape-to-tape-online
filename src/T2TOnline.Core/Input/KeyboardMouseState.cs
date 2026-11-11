using System;

namespace T2TOnline.Core.Input;

/// <summary>
/// One sample of a friend's keyboard and mouse buttons: which elements of their key table
/// (<see cref="InputBindingSet.Elements"/>) are held, and a 2-bit press counter per element. As with
/// <see cref="PadState"/>, a counter advances for every press, so a tap shorter than a sample still reaches the host.
/// Element <c>i</c> is bit <c>i</c> of <see cref="Held"/>; its counter is bits <c>2i</c> and <c>2i+1</c> of the 128-bit
/// value <see cref="CountersHigh"/>:<see cref="CountersLow"/>.
/// </summary>
public readonly struct KeyboardMouseState : IEquatable<KeyboardMouseState>, IPlayoutFrame<KeyboardMouseState>
{
    /// <summary>Most elements a key table has.</summary>
    public const int MaxElements = 64;

    /// <summary>Bits used by each element's press counter.</summary>
    public const int PressCounterBits = 2;

    /// <summary>Elements whose counters are in <see cref="CountersLow"/> (the rest are in <see cref="CountersHigh"/>).</summary>
    public const int ElementsPerCounterWord = 32;

    /// <summary>Creates a sample from raw values.</summary>
    public KeyboardMouseState(ulong held, ulong countersLow, ulong countersHigh)
    {
        Held = held;
        CountersLow = countersLow;
        CountersHigh = countersHigh;
    }

    /// <summary>Nothing held, no presses counted.</summary>
    public static KeyboardMouseState Neutral => default;

    /// <summary>Held elements, element <c>i</c> at bit <c>i</c>.</summary>
    public ulong Held { get; }

    /// <summary>The press counters of elements 0 to 31, two bits each.</summary>
    public ulong CountersLow { get; }

    /// <summary>The press counters of elements 32 to 63, two bits each.</summary>
    public ulong CountersHigh { get; }

    /// <summary>A mask of the first <paramref name="elementCount"/> elements (0 to 64).</summary>
    public static ulong MaskOf(int elementCount) => elementCount >= MaxElements ? ulong.MaxValue : elementCount <= 0 ? 0 : (1UL << elementCount) - 1;

    /// <summary>True when element <paramref name="element"/> is held (false for an element outside the table).</summary>
    public bool IsHeld(int element) => (uint)element < MaxElements && (Held & (1UL << element)) != 0;

    /// <summary>The press counter (0 to 3) of element <paramref name="element"/>.</summary>
    public int GetPressCounter(int element)
    {
        if ((uint)element >= MaxElements)
        {
            throw new ArgumentOutOfRangeException(nameof(element));
        }

        ulong word = element < ElementsPerCounterWord ? CountersLow : CountersHigh;
        return (int)((word >> ((element % ElementsPerCounterWord) * PressCounterBits)) & 0b11);
    }

    /// <summary>This sample with the counter of <paramref name="element"/> one press further on (modulo 4).</summary>
    public KeyboardMouseState WithPress(int element)
    {
        if ((uint)element >= MaxElements)
        {
            throw new ArgumentOutOfRangeException(nameof(element));
        }

        int shift = (element % ElementsPerCounterWord) * PressCounterBits;
        ulong word = element < ElementsPerCounterWord ? CountersLow : CountersHigh;
        ulong next = (((word >> shift) & 0b11) + 1) & 0b11;
        word = (word & ~(0b11UL << shift)) | (next << shift);
        return element < ElementsPerCounterWord ? new KeyboardMouseState(Held, word, CountersHigh) : new KeyboardMouseState(Held, CountersLow, word);
    }

    /// <summary>This sample with only the first <paramref name="elementCount"/> elements kept (held and counters).</summary>
    public KeyboardMouseState Masked(int elementCount)
    {
        int lowElements = Math.Clamp(elementCount, 0, ElementsPerCounterWord);
        int highElements = Math.Clamp(elementCount - ElementsPerCounterWord, 0, ElementsPerCounterWord);
        return new KeyboardMouseState(Held & MaskOf(elementCount), CountersLow & CounterMask(lowElements), CountersHigh & CounterMask(highElements));
    }

    /// <summary>This sample with nothing held; the press counters are kept.</summary>
    public KeyboardMouseState Released() => new(0, CountersLow, CountersHigh);

    /// <inheritdoc />
    public bool Equals(KeyboardMouseState other) => Held == other.Held && CountersLow == other.CountersLow && CountersHigh == other.CountersHigh;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeyboardMouseState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Held, CountersLow, CountersHigh);

    /// <summary>Value equality.</summary>
    public static bool operator ==(KeyboardMouseState left, KeyboardMouseState right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(KeyboardMouseState left, KeyboardMouseState right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"held={Held:X16} counters={CountersHigh:X16}{CountersLow:X16}";

    /// <summary>A mask of the counters of the first <paramref name="elements"/> elements of one word (0 to 32).</summary>
    internal static ulong CounterMask(int elements) => elements >= ElementsPerCounterWord ? ulong.MaxValue : (1UL << (elements * PressCounterBits)) - 1;
}
