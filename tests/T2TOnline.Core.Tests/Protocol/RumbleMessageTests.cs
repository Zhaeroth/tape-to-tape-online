using System;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>The host's rumble for a friend's player (message 49): two bytes, every value outside the game's refused.</summary>
public class RumbleMessageTests
{
    [Fact]
    public void Every_player_and_nudge_the_game_has_round_trips_in_two_bytes()
    {
        for (byte player = 0; player <= RumbleMessage.MaxRewiredPlayer; player++)
        {
            for (byte nudge = 0; nudge <= RumbleMessage.MaxNudge; nudge++)
            {
                var sent = new RumbleMessage(player, nudge);
                byte[] bytes = Write(sent);
                Assert.Equal(RumbleMessage.PayloadBytes, bytes.Length);
                var reader = new BitReader(bytes);
                Assert.True(RumbleMessage.TryRead(ref reader, out RumbleMessage read));
                Assert.Equal(sent, read);
                Assert.Equal(0, reader.RemainingBits);
            }
        }
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(255, 0)]
    [InlineData(0, 6)]
    [InlineData(0, 255)]
    [InlineData(200, 200)]
    public void A_player_or_nudge_the_game_doesn_t_have_is_refused_and_never_written(byte player, byte nudge)
    {
        var reader = new BitReader(new[] { player, nudge });
        Assert.False(RumbleMessage.TryRead(ref reader, out _));
        Assert.False(new RumbleMessage(player, nudge).IsValid);
        Assert.Throws<InvalidOperationException>(() => Write(new RumbleMessage(player, nudge)));
    }

    [Fact]
    public void A_truncated_rumble_is_refused()
    {
        byte[] full = Write(new RumbleMessage(3, 2));
        for (int length = 0; length < full.Length; length++)
        {
            var reader = new BitReader(full.AsSpan(0, length));
            Assert.False(RumbleMessage.TryRead(ref reader, out _), $"{length} bytes");
        }
    }

    [Fact]
    public void Random_bytes_never_throw_and_are_taken_only_when_in_range()
    {
        var random = new Random(49);
        for (int i = 0; i < 20_000; i++)
        {
            var bytes = new byte[random.Next(0, 6)];
            random.NextBytes(bytes);
            var reader = new BitReader(bytes);
            bool read = RumbleMessage.TryRead(ref reader, out RumbleMessage rumble);
            Assert.Equal(bytes.Length >= 2 && bytes[0] <= RumbleMessage.MaxRewiredPlayer && bytes[1] <= RumbleMessage.MaxNudge, read);
            if (read)
            {
                Assert.Equal((bytes[0], bytes[1]), (rumble.RewiredPlayer, rumble.Nudge));
            }
        }
    }

    private static byte[] Write(RumbleMessage message)
    {
        var writer = new BitWriter();
        message.Write(writer);
        return writer.ToArray();
    }
}
