using System;

namespace T2TOnline.Core.Serialization;

/// <summary>Maps floats in a known range onto fixed-width integers and back.</summary>
public static class Quantization
{
    /// <summary>
    /// Maps <paramref name="value"/>, clamped to [min, max], onto 0 .. 2^bitCount - 1.
    /// NaN maps to the bottom of the range.
    /// </summary>
    public static ulong Quantize(float value, float min, float max, int bitCount)
    {
        ValidateRange(min, max, bitCount);
        ulong steps = MaxSteps(bitCount);
        if (float.IsNaN(value) || value <= min)
        {
            return 0;
        }

        if (value >= max)
        {
            return steps;
        }

        double normalized = (value - (double)min) / ((double)max - min);
        return (ulong)Math.Round(normalized * steps, MidpointRounding.AwayFromZero);
    }

    /// <summary>Inverse of <see cref="Quantize"/>. Values above the top step clamp to <paramref name="max"/>.</summary>
    public static float Dequantize(ulong quantized, float min, float max, int bitCount)
    {
        ValidateRange(min, max, bitCount);
        ulong steps = MaxSteps(bitCount);
        if (quantized >= steps)
        {
            return max;
        }

        return (float)(min + (((double)max - min) * quantized / steps));
    }

    /// <summary>The largest error <see cref="Quantize"/> introduces for this range and width.</summary>
    public static float Resolution(float min, float max, int bitCount)
    {
        ValidateRange(min, max, bitCount);
        return (float)(((double)max - min) / MaxSteps(bitCount) / 2.0);
    }

    private static ulong MaxSteps(int bitCount) => (1UL << bitCount) - 1;

    private static void ValidateRange(float min, float max, int bitCount)
    {
        if (bitCount is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Quantization uses 1 to 32 bits.");
        }

        if (!(max > min) || float.IsInfinity(min) || float.IsInfinity(max))
        {
            throw new ArgumentException("The range must be finite with max greater than min.", nameof(max));
        }
    }
}
