using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>Steam's app manifest: only the two build numbers of its top block are read.</summary>
public class SteamAppManifestTests
{
    // The shape of a real manifest (Valve's KeyValues text): the app's own numbers are the game's, the owner, size and time are made up.
    private const string Real =
        "\"AppState\"\n{\n" +
        "\t\"appid\"\t\t\"1566200\"\n" +
        "\t\"universe\"\t\t\"1\"\n" +
        "\t\"name\"\t\t\"Tape to Tape\"\n" +
        "\t\"StateFlags\"\t\t\"4\"\n" +
        "\t\"installdir\"\t\t\"Tape to Tape\"\n" +
        "\t\"LastUpdated\"\t\t\"1793923200\"\n" +
        "\t\"SizeOnDisk\"\t\t\"1234567890\"\n" +
        "\t\"buildid\"\t\t\"26471720\"\n" +
        "\t\"LastOwner\"\t\t\"76561199000000001\"\n" +
        "\t\"TargetBuildID\"\t\t\"26471720\"\n" +
        "\t\"InstalledDepots\"\n\t{\n\t\t\"1566201\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"123\"\n\t\t\t\"buildid\"\t\t\"999\"\n\t\t}\n\t}\n" +
        "\t\"UserConfig\"\n\t{\n\t\t\"language\"\t\t\"english\"\n\t}\n" +
        "}\n";

    [Fact]
    public void A_real_manifest_gives_its_build_and_target()
    {
        Assert.True(SteamAppManifest.TryRead(Real, out long installed, out long target));
        Assert.Equal(26471720, installed);
        Assert.Equal(26471720, target);
        Assert.Equal("appmanifest_1566200.acf", SteamAppManifest.FileName);
    }

    [Fact]
    public void Only_the_top_blocks_keys_count()
    {
        // A depot's own "buildid" (deeper) is never the game's.
        string text = Real.Replace("\t\"buildid\"\t\t\"26471720\"\n", string.Empty, StringComparison.Ordinal);
        Assert.False(SteamAppManifest.TryRead(text, out long installed, out long target));
        Assert.Equal(0, installed);
        Assert.Equal(0, target);
    }

    [Fact]
    public void An_update_waiting_shows_as_another_target()
    {
        string text = Real.Replace("\"TargetBuildID\"\t\t\"26471720\"", "\"TargetBuildID\"\t\t\"26500000\"", StringComparison.Ordinal);
        Assert.True(SteamAppManifest.TryRead(text, out long installed, out long target));
        Assert.Equal(26471720, installed);
        Assert.Equal(26500000, target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a manifest")]
    [InlineData("\"AppState\" { \"buildid\" \"abc\" }")]
    [InlineData("\"AppState\" { \"buildid\" \"-5\" }")]
    [InlineData("\"AppState\" { \"buildid\" \"0\" }")]
    [InlineData("}}}")]
    [InlineData("\"AppState\" { \"buildid\" \"265")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.False(SteamAppManifest.TryRead(text, out long installed, out long target));
        Assert.Equal(0, installed);
        Assert.Equal(0, target);
    }

    [Fact]
    public void A_text_over_the_limit_is_refused_unread()
    {
        string big = Real + new string(' ', SteamAppManifest.MaxChars);
        Assert.False(SteamAppManifest.TryRead(big, out _, out _));
    }

    [Fact]
    public void Escaped_quotes_inside_a_value_are_skipped()
    {
        string text = "\"AppState\" { \"name\" \"a \\\"quoted\\\" name\" \"buildid\" \"42\" }";
        Assert.True(SteamAppManifest.TryRead(text, out long installed, out _));
        Assert.Equal(42, installed);
    }
}
