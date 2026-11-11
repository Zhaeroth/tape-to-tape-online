using System;
using System.Text;

namespace T2TOnline.Steam.Tests;

/// <summary>
/// The lobby data a host publishes, pinned as it goes out: a friend's browser on another release reads these keys and values
/// (<see cref="SteamLobbyAdvertiser"/> writes them, <see cref="SteamLobbyBrowser"/> reads them). A failure here means the
/// published text changed: a friend on an older release would no longer read it, so change it only with the protocol.
/// </summary>
public class LobbyDataLayoutTests
{
    [Fact]
    public void The_keys_are_the_ones_every_release_reads()
    {
        Assert.Equal("t2t.mod", LobbyDataKeys.Mod);
        Assert.Equal("t2t.protocol", LobbyDataKeys.Protocol);
        Assert.Equal("t2t.build", LobbyDataKeys.Build);
        Assert.Equal("t2t.host", LobbyDataKeys.Host);
        Assert.Equal("t2t.players", LobbyDataKeys.Players);
        Assert.Equal("t2t.state", LobbyDataKeys.State);
        Assert.Equal("t2t.mode", LobbyDataKeys.Mode);
        Assert.Equal("t2t.save", LobbyDataKeys.Save);
        Assert.Equal("t2t.logtag", LobbyDataKeys.LogTag);
        Assert.Equal("t2t.pingloc", LobbyDataKeys.PingLocation);
    }

    [Fact]
    public void The_state_and_mode_words_are_the_ones_every_release_reads()
    {
        Assert.Equal("lobby", ListedLobby.StateText(ListedState.Lobby));
        Assert.Equal("play", ListedLobby.StateText(ListedState.Playing));
        Assert.Equal("campaign", ListedLobby.ModeText(ListedMode.Campaign));
        Assert.Equal("quick", ListedLobby.ModeText(ListedMode.Quick));
    }

    [Fact]
    public void A_save_is_published_as_these_bytes()
    {
        // squad,act,floor in UTF-8: "squad-a,2,5".
        Assert.Equal("73717561642D612C322C35", Convert.ToHexString(Encoding.UTF8.GetBytes(new ListedSave("squad-a", 2, 5).ToText())));
    }
}
