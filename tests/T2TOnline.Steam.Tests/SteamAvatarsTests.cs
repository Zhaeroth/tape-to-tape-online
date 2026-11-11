using System;

namespace T2TOnline.Steam.Tests;

/// <summary>The avatar helpers that need no Steam: pixel rows and the accepted sizes.</summary>
public class SteamAvatarsTests
{
    [Fact]
    public void Rows_swap_top_for_bottom_and_each_pixel_keeps_its_four_bytes()
    {
        // 2 x 3 RGBA: row 0 is 1..8, row 1 is 11..18, row 2 is 21..28.
        byte[] image =
        {
            1, 2, 3, 4, 5, 6, 7, 8,
            11, 12, 13, 14, 15, 16, 17, 18,
            21, 22, 23, 24, 25, 26, 27, 28,
        };

        Assert.True(SteamAvatars.FlipRows(image, 2, 3));

        Assert.Equal(
            new byte[]
            {
                21, 22, 23, 24, 25, 26, 27, 28,
                11, 12, 13, 14, 15, 16, 17, 18,
                1, 2, 3, 4, 5, 6, 7, 8,
            },
            image);
    }

    [Fact]
    public void An_even_number_of_rows_flips_too_and_flipping_twice_gives_the_image_back()
    {
        byte[] image = new byte[8 * 4 * 4];
        for (int i = 0; i < image.Length; i++)
        {
            image[i] = (byte)(i * 7);
        }

        byte[] original = (byte[])image.Clone();

        Assert.True(SteamAvatars.FlipRows(image, 8, 4));
        Assert.NotEqual(original, image);
        Assert.Equal(original.AsSpan(0, 32).ToArray(), image.AsSpan(96, 32).ToArray());
        Assert.True(SteamAvatars.FlipRows(image, 8, 4));
        Assert.Equal(original, image);
    }

    [Fact]
    public void The_largest_accepted_image_flips_without_touching_bytes_past_the_image()
    {
        int side = SteamAvatars.MaxSide;
        byte[] buffer = new byte[(side * side * SteamAvatars.BytesPerPixel) + 16]; // a reused buffer is longer than the image
        buffer[0] = 9;
        buffer[^1] = 77;

        Assert.True(SteamAvatars.FlipRows(buffer, side, side));

        Assert.Equal(9, buffer[side * (side - 1) * SteamAvatars.BytesPerPixel]);
        Assert.Equal(77, buffer[^1]);
        Assert.Equal(0, buffer[0]);
    }

    [Theory]
    [InlineData(0, 4, 64)]
    [InlineData(4, 0, 64)]
    [InlineData(-1, 4, 64)]
    [InlineData(4, 4, 63)] // shorter than the image
    public void A_buffer_that_does_not_hold_the_image_is_left_alone(int width, int height, int length)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, (byte)5);

        Assert.False(SteamAvatars.FlipRows(buffer, width, height));

        Assert.All(buffer, b => Assert.Equal(5, b));
    }

    [Fact]
    public void A_single_row_is_its_own_flip()
    {
        byte[] row = { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.True(SteamAvatars.FlipRows(row, 2, 1));

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, row);
    }

    [Theory]
    [InlineData(7u, false)]
    [InlineData(8u, true)]
    [InlineData(32u, true)]
    [InlineData(184u, true)]
    [InlineData(512u, true)]
    [InlineData(513u, false)]
    [InlineData(0u, false)]
    [InlineData(uint.MaxValue, false)]
    public void Only_sizes_from_eight_to_five_hundred_twelve_are_accepted(uint side, bool accepted)
    {
        Assert.Equal(accepted, SteamAvatars.IsAcceptedSide(side));
    }
}
