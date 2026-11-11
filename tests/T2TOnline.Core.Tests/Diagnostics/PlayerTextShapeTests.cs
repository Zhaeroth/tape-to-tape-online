using System;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>Player-written text in logs as its length and a keyed hash (D127), never the text.</summary>
public class PlayerTextShapeTests
{
    private static readonly byte[] KeyA = Enumerable.Range(1, PlayerTextShape.KeyBytes).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(101, PlayerTextShape.KeyBytes).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_name_is_its_length_and_a_short_hash()
    {
        string shape = new PlayerTextShape(KeyA).Describe("Riley Casey");
        Assert.Matches("^11 characters, #[0-9a-f]{4}$", shape);
        Assert.DoesNotContain("Riley", shape, StringComparison.Ordinal);
        Assert.Matches("^1 character, #[0-9a-f]{4}$", new PlayerTextShape(KeyA).Describe("A"));
    }

    [Fact]
    public void One_key_gives_one_text_one_hash_and_another_key_another()
    {
        var a = new PlayerTextShape(KeyA);
        Assert.Equal(a.Describe("Sam Alex"), new PlayerTextShape(KeyA).Describe("Sam Alex"));
        Assert.NotEqual(a.Describe("Sam Alex"), a.Describe("Sam Alec"));
        Assert.NotEqual(a.Describe("Sam Alex"), new PlayerTextShape(KeyB).Describe("Sam Alex"));
        Assert.NotEqual(PlayerTextShape.ForLaunch().Describe("Sam Alex"), PlayerTextShape.ForLaunch().Describe("Sam Alex"));
    }

    [Fact]
    public void Characters_count_as_a_reader_sees_them()
    {
        var shape = new PlayerTextShape(KeyA);
        Assert.StartsWith("2 characters, ", shape.Describe(char.ConvertFromUtf32(0x1F3D2) + "e\u0301"), StringComparison.Ordinal);
        Assert.StartsWith("3 characters, ", shape.Describe("\u674E\u5C0F\u9F8D"), StringComparison.Ordinal);
        Assert.StartsWith("1 character, ", shape.Describe("\uD800"), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_text_and_bad_keys()
    {
        Assert.Equal("empty", new PlayerTextShape(KeyA).Describe(null));
        Assert.Equal("empty", new PlayerTextShape(KeyA).Describe(string.Empty));
        Assert.Throws<ArgumentNullException>(() => new PlayerTextShape(null!));
        Assert.Throws<ArgumentException>(() => new PlayerTextShape(new byte[8]));
    }

    [Fact]
    public void The_key_is_copied()
    {
        byte[] key = (byte[])KeyA.Clone();
        var shape = new PlayerTextShape(key);
        string before = shape.Describe("Casey");
        key[0] ^= 0xFF;
        Assert.Equal(before, shape.Describe("Casey"));
    }
}
