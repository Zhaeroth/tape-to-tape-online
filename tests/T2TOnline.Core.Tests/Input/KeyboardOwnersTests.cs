using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

/// <summary>
/// The file a keyboard friend's PC keeps while the mod holds their keyboard and mouse (<see cref="KeyboardOwners"/>): read
/// back exactly at the next start, and nothing read from a file that isn't one of ours.
/// </summary>
public class KeyboardOwnersTests
{
    [Fact]
    public void Owners_written_are_read_back_as_they_were()
    {
        var owners = new[] { new KeyboardOwner(0, true, true), new KeyboardOwner(3, true, false), new KeyboardOwner(10, false, true) };
        string text = KeyboardOwners.Format(owners);

        Assert.StartsWith("#", text, StringComparison.Ordinal);
        Assert.Contains("0 keyboard mouse\n", text, StringComparison.Ordinal);
        Assert.True(KeyboardOwners.TryParse(text, out List<KeyboardOwner> read));
        Assert.Equal(owners, read);
        Assert.True(KeyboardOwners.TryParse(text.Replace("\n", "\r\n", StringComparison.Ordinal), out read)); // Windows line ends
        Assert.Equal(owners, read);
    }

    [Fact]
    public void An_empty_list_reads_as_no_owners()
    {
        Assert.True(KeyboardOwners.TryParse(KeyboardOwners.Format(Array.Empty<KeyboardOwner>()), out List<KeyboardOwner> read));
        Assert.Empty(read);
        Assert.True(KeyboardOwners.TryParse(string.Empty, out read));
        Assert.Empty(read);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0 keyboard keyboard")]
    [InlineData("0 mouse keyboard mouse")]
    [InlineData("0 keys")]
    [InlineData("-1 keyboard")]
    [InlineData("+1 keyboard")]
    [InlineData("64 keyboard")]
    [InlineData("99999999999 keyboard")]
    [InlineData("0 keyboard\n0 mouse")]
    [InlineData("zero keyboard")]
    [InlineData("0 KEYBOARD")]
    public void A_malformed_file_reads_as_nothing(string text)
    {
        Assert.False(KeyboardOwners.TryParse(text, out List<KeyboardOwner> read));
        Assert.Empty(read);
    }

    [Fact]
    public void Too_long_or_too_many_owners_or_null_read_as_nothing()
    {
        Assert.False(KeyboardOwners.TryParse(null, out _));
        Assert.False(KeyboardOwners.TryParse(new string('#', KeyboardOwners.MaxLength + 1), out _));
        string many = string.Join("\n", Enumerable.Range(0, KeyboardOwners.MaxOwners + 1).Select(i => $"{i} keyboard"));
        Assert.False(KeyboardOwners.TryParse(many, out List<KeyboardOwner> read));
        Assert.Empty(read);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(41);
        const string Alphabet = "0123456789 keyboardmouse#\n\r-+\t";
        for (int i = 0; i < 20_000; i++)
        {
            char[] chars = new char[random.Next(0, 80)];
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = Alphabet[random.Next(Alphabet.Length)];
            }

            if (KeyboardOwners.TryParse(new string(chars), out List<KeyboardOwner> read))
            {
                Assert.All(read, o => Assert.True(o.PlayerId is >= 0 and <= KeyboardOwners.MaxPlayerId && (o.Keyboard || o.Mouse)));
                Assert.True(read.Count <= KeyboardOwners.MaxOwners);
            }
        }
    }

    [Fact]
    public void Owners_the_file_cannot_carry_are_refused_when_written()
    {
        Assert.Throws<ArgumentException>(() => KeyboardOwners.Format(new[] { new KeyboardOwner(0, false, false) }));
        Assert.Throws<ArgumentException>(() => KeyboardOwners.Format(new[] { new KeyboardOwner(-1, true, false) }));
        Assert.Throws<ArgumentException>(() => KeyboardOwners.Format(new[] { new KeyboardOwner(KeyboardOwners.MaxPlayerId + 1, true, false) }));
        Assert.Throws<ArgumentException>(() => KeyboardOwners.Format(Enumerable.Range(0, KeyboardOwners.MaxOwners + 1).Select(i => new KeyboardOwner(i, true, false))));
    }
}
