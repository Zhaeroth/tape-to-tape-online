using System;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A host's custom logo kept for a friend's run history (D55 as amended): content names made
/// from the bytes, never a path, and a store capped in count and size.
/// </summary>
public class HistoryLogoNamesTests
{
    private static readonly DateTime Noon = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_logos_key_and_names_come_from_its_bytes()
    {
        byte[] png = Encoding.ASCII.GetBytes("abc");
        string key = HistoryLogoNames.Key(png);
        Assert.Equal("ba7816bf8f01cfea", key); // the first 8 bytes of SHA-256("abc")
        Assert.Equal("t2t_hostlogo_ba7816bf8f01cfea.png", HistoryLogoNames.ContentName(key));
        Assert.Equal("ba7816bf8f01cfea.png", HistoryLogoNames.FileName(key));
        Assert.True(HistoryLogoNames.TryParseContentName("t2t_hostlogo_ba7816bf8f01cfea.png", out string read));
        Assert.Equal(key, read);
        Assert.True(HistoryLogoNames.TryParseFileName("ba7816bf8f01cfea.png", out read));
        Assert.Equal(key, read);
        Assert.NotEqual(key, HistoryLogoNames.Key(Encoding.ASCII.GetBytes("abd")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom_logo.png")]
    [InlineData("t2t_hostlogo_BA7816BF8F01CFEA.png")]
    [InlineData("t2t_hostlogo_ba7816bf8f01cfe.png")]
    [InlineData("t2t_hostlogo_ba7816bf8f01cfea0.png")]
    [InlineData("t2t_hostlogo_ba7816bf8f01cfea.PNG")]
    [InlineData("t2t_hostlogo_..\\..\\x.png")]
    [InlineData("t2t_hostlogo_ba7816bf8f01cfe/.png")]
    [InlineData("t2t_hostlogo_ba7816bf8f01cfeg.png")]
    public void Any_other_name_is_left_to_the_game(string? name)
    {
        Assert.False(HistoryLogoNames.TryParseContentName(name, out string key));
        Assert.Empty(key);
    }

    [Fact]
    public void A_name_that_isnt_a_key_is_refused_when_made()
    {
        Assert.Throws<ArgumentException>(() => HistoryLogoNames.ContentName("../x"));
        Assert.Throws<ArgumentException>(() => HistoryLogoNames.FileName("NUL"));
        Assert.False(HistoryLogoNames.TryParseFileName("notes.txt", out _));
        Assert.False(HistoryLogoNames.TryParseFileName("con.png", out _));
    }

    [Fact]
    public void The_store_keeps_the_newest_32_and_48_mb_never_the_one_just_stored()
    {
        HistoryLogoFile Logo(int i, long bytes, double ageHours) =>
            new(HistoryLogoNames.FileName(i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)), bytes, Noon.AddHours(-ageHours));

        // 34 small logos: the two oldest go.
        var many = Enumerable.Range(0, 34).Select(i => Logo(i, 1000, i)).Append(new HistoryLogoFile("readme.txt", 5, Noon.AddDays(-9))).ToList();
        Assert.Equal(new[] { many[33].FileName, many[32].FileName }, HistoryLogoNames.Expired(many, null));

        // 13 logos of 4 MB (52 MB): the oldest goes, unless it's the one just stored (then the next oldest).
        var large = Enumerable.Range(0, 13).Select(i => Logo(i, 4 * 1024 * 1024, i)).ToList();
        Assert.Equal(new[] { large[12].FileName }, HistoryLogoNames.Expired(large, null));
        string justStored = 12.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new[] { large[11].FileName }, HistoryLogoNames.Expired(large, justStored));

        Assert.Empty(HistoryLogoNames.Expired(large.Take(12), null));
    }
}
