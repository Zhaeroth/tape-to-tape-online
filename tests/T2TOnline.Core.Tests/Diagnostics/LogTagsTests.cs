using System;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class LogTagsTests
{
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;
    private const ulong Lobby = 109775240000000003;

    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, LogTags.KeyBytes).ToArray();

    [Fact]
    public void Two_pcs_with_the_hosts_key_give_everyone_the_same_tags()
    {
        byte[] key = LogTags.NewKey();
        Assert.True(LogTags.TryParseKey(LogTags.ToHex(key), out byte[] read)); // through lobby data
        var host = new LogTags(key, shared: true);
        var friend = new LogTags(read, shared: true);

        Assert.Equal(host.Player(Alex), friend.Player(Alex));
        Assert.Equal(host.Player(Sam), friend.Player(Sam));
        Assert.Equal(host.Lobby(Lobby), friend.Lobby(Lobby));
        Assert.NotEqual(host.Player(Alex), host.Player(Sam));
    }

    [Fact]
    public void Another_key_gives_other_tags()
    {
        var one = new LogTags(Key(1), shared: true);
        var two = new LogTags(Key(2), shared: true);

        Assert.NotEqual(one.Player(Alex), two.Player(Alex));
        Assert.NotEqual(one.Lobby(Lobby), two.Lobby(Lobby));
        Assert.NotEqual(LogTags.ForLaunch().Player(Alex), LogTags.ForLaunch().Player(Alex));
    }

    [Fact]
    public void Tags_have_their_form_and_never_show_the_id()
    {
        var tags = new LogTags(Key(7), shared: false);
        string player = tags.Player(Alex);
        string lobby = tags.Lobby(Lobby);

        Assert.Matches("^P#[0-9a-f]{6}$", player);
        Assert.Matches("^L#[0-9a-f]{6}$", lobby);
        Assert.DoesNotContain("7656", player);
        Assert.False(tags.Shared);
        Assert.Equal(player, tags.Player(Alex)); // stable
    }

    [Fact]
    public void A_player_and_a_lobby_with_the_same_number_get_unrelated_digits()
    {
        var tags = new LogTags(Key(3), shared: true);
        Assert.NotEqual(tags.Player(Lobby)[2..], tags.Lobby(Lobby)[2..]);
    }

    [Fact]
    public void The_key_from_lobby_data_must_be_exactly_32_hex_digits()
    {
        Assert.True(LogTags.TryParseKey("00112233445566778899aabbccddeeff", out byte[] key));
        Assert.Equal(0xff, key[15]);
        Assert.True(LogTags.TryParseKey("00112233445566778899AABBCCDDEEFF", out _));
        Assert.False(LogTags.TryParseKey(null, out _));
        Assert.False(LogTags.TryParseKey(string.Empty, out _));
        Assert.False(LogTags.TryParseKey("00112233445566778899aabbccddeef", out _));
        Assert.False(LogTags.TryParseKey("00112233445566778899aabbccddeeff0", out _));
        Assert.False(LogTags.TryParseKey("00112233445566778899aabbccddeefg", out _));
        Assert.False(LogTags.TryParseKey(" 0112233445566778899aabbccddeeff", out byte[] none));
        Assert.Empty(none);
    }

    [Fact]
    public void The_key_is_written_as_32_lowercase_hex_digits()
    {
        string hex = LogTags.ToHex(Enumerable.Range(0xA0, LogTags.KeyBytes).Select(b => (byte)b).ToArray());
        Assert.Equal("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf", hex);
        Assert.Equal(LogTags.KeyHexLength, LogTags.ToHex(LogTags.NewKey()).Length);
    }

    [Fact]
    public void A_key_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new LogTags(new byte[15], shared: true));
    }

    [Fact]
    public void Two_members_whose_short_tags_collide_widen_every_tag_on_both_pcs()
    {
        // Find two IDs whose six digits collide under one key (the birthday bound makes it a few thousand tries).
        var tags = new LogTags(Key(9), shared: true);
        var seen = new System.Collections.Generic.Dictionary<string, ulong>();
        ulong first = 0;
        ulong second = 0;
        for (ulong id = 76561199000000000; first == 0; id++)
        {
            string tag = tags.Player(id);
            if (seen.TryGetValue(tag, out ulong other))
            {
                first = other;
                second = id;
            }
            else
            {
                seen[tag] = id;
            }
        }

        var host = new LogTags(Key(9), shared: true);
        var friend = new LogTags(Key(9), shared: true);
        Assert.False(host.AddMembers(new[] { first, Alex }));
        Assert.Equal(LogTags.ShortDigits, host.Digits);
        Assert.True(host.AddMembers(new[] { first, Alex, second }));
        Assert.True(friend.AddMembers(new[] { second, first }));

        Assert.Equal(LogTags.LongDigits, host.Digits);
        Assert.Matches("^P#[0-9a-f]{8}$", host.Player(Alex));
        Assert.Equal(host.Player(first), friend.Player(first));
        Assert.NotEqual(host.Player(first), host.Player(second));
        Assert.False(host.AddMembers(new[] { Sam })); // already wide
    }
}
