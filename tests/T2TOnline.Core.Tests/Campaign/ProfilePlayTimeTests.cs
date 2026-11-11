using System;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class ProfilePlayTimeTests
{
    // The shape of the game's profile save around the counter (Easy Save 3 JSON, tabs and CRLF).
    private static byte[] Profile(string seconds, string completed = "3600.5", string separator = " : ") => Encoding.UTF8.GetBytes(
        "{\r\n\t\"profile\" : {\r\n\t\t\"__type\" : \"ProfileData\",\r\n\t\t\"HadPreexistingProgressAtStart\" : true,\r\n" +
        $"\t\t\"TttActiveSeconds\"{separator}{seconds},\r\n\t\t\"CompletedRogueRunSeconds\" : {completed},\r\n\t\t\"RogueCampaignGames\" : 12\r\n\t}}\r\n}}");

    [Fact]
    public void Two_saves_a_few_seconds_of_play_apart_are_the_same()
    {
        // Two saves a few seconds apart with nothing played between them.
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Profile("5400.25"), Profile("5404.75")));
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Profile("999.5"), Profile("1000.25")));
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Profile("7"), Profile("7.5")));
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Profile("1.5E+07"), Profile("1.6e7")));
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Profile("5400.25"), Profile("5400.25")));
    }

    [Fact]
    public void Any_other_difference_is_a_difference()
    {
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("5400.25"), Profile("5404.75", completed: "3700.0")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("5400.25", separator: " : "), Profile("5404.75", separator: ": ")));

        byte[] a = Profile("5400.25");
        byte[] b = Profile("5404.75");
        b[Encoding.ASCII.GetString(b).LastIndexOf("12", StringComparison.Ordinal)] = (byte)'7'; // 12 games -> 72
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(a, b));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(a, a.AsSpan(0, a.Length - 1)));

        // A change of the same length before the counter.
        byte[] before = Profile("5404.75");
        before[Encoding.ASCII.GetString(before).IndexOf("true", StringComparison.Ordinal)] = (byte)'T';
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(a, before));
    }

    [Fact]
    public void Without_one_plain_counter_nothing_is_the_same()
    {
        byte[] none = Encoding.UTF8.GetBytes("{\r\n\t\"profile\" : {\r\n\t\t\"RogueCampaignGames\" : 12\r\n\t}\r\n}");
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(none, none));

        byte[] twice = Encoding.UTF8.GetBytes("{\"TttActiveSeconds\" : 1,\"TttActiveSeconds\" : 2}");
        byte[] twiceToo = Encoding.UTF8.GetBytes("{\"TttActiveSeconds\" : 1,\"TttActiveSeconds\" : 3}");
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(twice, twiceToo));

        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("\"5400\""), Profile("\"5404\"")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("null"), Profile("null")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("1."), Profile("2.")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("1e"), Profile("2e")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Profile("-"), Profile("-")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Encoding.UTF8.GetBytes("{\"TttActiveSeconds\" 5}"), Encoding.UTF8.GetBytes("{\"TttActiveSeconds\" 6}")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(Encoding.UTF8.GetBytes("{\"TttActiveSeconds\" :"), Encoding.UTF8.GetBytes("{\"TttActiveSeconds\" :")));
        Assert.False(ProfilePlayTime.SameApartFromPlayTime(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_counter_is_found_anywhere_in_the_file()
    {
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Encoding.UTF8.GetBytes("{\"TttActiveSeconds\":-3}"), Encoding.UTF8.GetBytes("{\"TttActiveSeconds\":4}")));
        Assert.True(ProfilePlayTime.SameApartFromPlayTime(Encoding.UTF8.GetBytes("\"TttActiveSeconds\"\t:\t1"), Encoding.UTF8.GetBytes("\"TttActiveSeconds\"\t:\t22")));
    }
}
