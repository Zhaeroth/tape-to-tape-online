using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Serialization;

public class QuantizationTests
{
    [Fact]
    public void Range_ends_map_to_first_and_last_steps()
    {
        Assert.Equal(0UL, Quantization.Quantize(-30f, -30f, 30f, 16));
        Assert.Equal(65_535UL, Quantization.Quantize(30f, -30f, 30f, 16));
        Assert.Equal(-30f, Quantization.Dequantize(0, -30f, 30f, 16));
        Assert.Equal(30f, Quantization.Dequantize(65_535, -30f, 30f, 16));
    }

    [Fact]
    public void Out_of_range_and_nan_values_clamp()
    {
        Assert.Equal(0UL, Quantization.Quantize(-100f, -1f, 1f, 8));
        Assert.Equal(255UL, Quantization.Quantize(100f, -1f, 1f, 8));
        Assert.Equal(0UL, Quantization.Quantize(float.NaN, -1f, 1f, 8));
        Assert.Equal(1f, Quantization.Dequantize(1000, -1f, 1f, 8));
    }

    [Fact]
    public void Error_stays_within_the_stated_resolution()
    {
        var random = new Random(1234);
        const float Min = -32f, Max = 32f;
        const int Bits = 16;
        float resolution = Quantization.Resolution(Min, Max, Bits);
        for (int i = 0; i < 10_000; i++)
        {
            float value = (float)((random.NextDouble() * (Max - Min)) + Min);
            float back = Quantization.Dequantize(Quantization.Quantize(value, Min, Max, Bits), Min, Max, Bits);
            Assert.True(Math.Abs(back - value) <= resolution * 1.0001f, $"{value} came back as {back}");
        }
    }

    [Fact]
    public void Writer_and_reader_use_the_same_mapping()
    {
        var writer = new BitWriter();
        writer.WriteQuantized(12.34f, -30f, 30f, 16);
        var reader = new BitReader(writer.AsSpan());
        Assert.Equal(12.34f, reader.ReadQuantized(-30f, 30f, 16), 3);
    }

    [Fact]
    public void Invalid_ranges_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Quantization.Quantize(0f, 1f, 1f, 8));
        Assert.Throws<ArgumentException>(() => Quantization.Quantize(0f, 0f, float.PositiveInfinity, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.Quantize(0f, 0f, 1f, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.Quantize(0f, 0f, 1f, 33));
    }
}
