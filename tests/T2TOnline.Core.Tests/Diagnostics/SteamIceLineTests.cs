using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>
/// Steam's own per-connection ICE line, read from its networking debug output (a developer's proof of relay only): only the
/// two shapes Steam prints, rebuilt from what was parsed, so the connection's description (which names the peer) and any
/// other text never reach the log.
/// </summary>
public class SteamIceLineTests
{
    private const string Description = "[#2853418395 P2P steamid:76561199000000001 vport 0]";

    [Fact]
    public void Ice_off_by_the_connections_own_setting_reads_as_relay_only()
    {
        SteamIceReading? reading = SteamIceLine.Read($"{Description} ICE failed 3998 ICE not enabled by local user options\n");

        Assert.NotNull(reading);
        Assert.False(reading!.IceOn);
        Assert.Equal("ICE failed 3998 ICE not enabled by local user options", reading.Text);
        Assert.Equal("ICE: Steam's own line for this connection: \"ICE failed 3998 ICE not enabled by local user options\": no ICE on it, so it is relay only, as set.",
            SteamIceLine.LogLine(reading, relayOnly: true, out bool warn));
        Assert.False(warn);
    }

    [Fact]
    public void The_transport_line_reads_its_two_numbers_and_ice_is_on_only_with_candidate_types_allowed()
    {
        SteamIceReading? on = SteamIceLine.Read($"{Description} P2P_Transport_ICE_Enable=0x7FFFFFFF, AllowedCandidateTypes=0x00000016");
        SteamIceReading? none = SteamIceLine.Read($"{Description} P2P_Transport_ICE_Enable=0x0, AllowedCandidateTypes=0x0\n");

        Assert.NotNull(on);
        Assert.True(on!.IceOn);
        Assert.Equal("P2P_Transport_ICE_Enable=0x7fffffff, AllowedCandidateTypes=0x16", on.Text);
        Assert.NotNull(none);
        Assert.False(none!.IceOn);
        Assert.Equal("P2P_Transport_ICE_Enable=0x0, AllowedCandidateTypes=0x0", none.Text);
    }

    [Fact]
    public void Ice_on_under_relay_only_is_a_warning_and_allowed_ice_is_not()
    {
        var on = new SteamIceReading("P2P_Transport_ICE_Enable=0x7fffffff, AllowedCandidateTypes=0x16", true);

        Assert.Contains("although relay only is set", SteamIceLine.LogLine(on, relayOnly: true, out bool warn), StringComparison.Ordinal);
        Assert.True(warn);
        Assert.Contains("direct connections allowed, as set", SteamIceLine.LogLine(on, relayOnly: false, out warn), StringComparison.Ordinal);
        Assert.False(warn);
        Assert.Contains("although direct connections are allowed", SteamIceLine.LogLine(new SteamIceReading("x", false), relayOnly: false, out warn), StringComparison.Ordinal);
        Assert.False(warn);
        Assert.Throws<ArgumentNullException>(() => SteamIceLine.LogLine(null!, true, out _));
    }

    [Fact]
    public void Nothing_of_the_description_or_any_other_text_is_kept()
    {
        foreach (string line in new[]
        {
            $"{Description} ICE failed 3998 ICE not enabled by local user options",
            $"{Description} P2P_Transport_ICE_Enable=0x0, AllowedCandidateTypes=0x0",
            $"   12.345678 {Description} P2P_Transport_ICE_Enable=0x0, AllowedCandidateTypes=0x0",
        })
        {
            string text = SteamIceLine.Read(line)!.Text;
            Assert.DoesNotContain("7656119", text, StringComparison.Ordinal);
            Assert.DoesNotContain("#", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[#1 P2P steamid:76561199000000001] Sending P2P ConnectRequest")]
    [InlineData("[#1 P2P steamid:76561199000000001] ICE failed 5003 Peer at 192.0.2.10:27015 timed out")]
    [InlineData("[#1 P2P steamid:76561199000000001] ICE failed x ICE not enabled by local user options")]
    [InlineData("[#1 P2P steamid:76561199000000001] ICE failed 3998 ICE not enabled by local user options and more")]
    [InlineData("[#1 P2P steamid:76561199000000001] P2P_Transport_ICE_Enable=0x0, AllowedCandidateTypes=0x0 Alex")]
    [InlineData("[#1 P2P steamid:76561199000000001] P2P_Transport_ICE_Enable=0x123456789, AllowedCandidateTypes=0x0")]
    [InlineData("[#1 P2P steamid:76561199000000001] P2P_Transport_ICE_Enable=0x, AllowedCandidateTypes=0x0")]
    [InlineData("[#1 P2P steamid:76561199000000001] P2P_Transport_ICE_Enable=0x0 AllowedCandidateTypes=0x0")]
    public void Any_other_line_reads_as_nothing(string? line)
    {
        Assert.Null(SteamIceLine.Read(line));
    }

    [Fact]
    public void A_line_longer_than_any_of_steams_reads_as_nothing()
    {
        string padded = new string(' ', SteamIceLine.MaxLength) + "[#1] ICE failed 3998 ICE not enabled by local user options";
        Assert.Null(SteamIceLine.Read(padded));
    }
}
