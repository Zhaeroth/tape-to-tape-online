using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Steam.Tests;

/// <summary>What a host publishes about its lobby: only what changed, nothing unasked, and again for a new lobby.</summary>
public class SteamLobbyAdvertiserTests
{
    private const ulong LobbyA = 109775240917663232UL;
    private const ulong LobbyB = 109775240917663233UL;

    private sealed class Sink
    {
        public List<(ulong Lobby, string Key, string Value)> Sets { get; } = new();

        public HashSet<string> Refuse { get; } = new();

        public List<string> Warnings { get; } = new();

        public List<string> Lines { get; } = new();

        public string? Location { get; set; } = "ord=70+26,iad=77+3";

        public bool Set(ulong lobby, string key, string value)
        {
            if (Refuse.Contains(key))
            {
                return false;
            }

            Sets.Add((lobby, key, value));
            return true;
        }

        public bool ReadLocation(out string location)
        {
            location = Location ?? string.Empty;
            return Location is not null;
        }

        public SteamLobbyAdvertiser Make() => new(Set, ReadLocation, Lines.Add, Warnings.Add);

        public string[] Keys() => Sets.Select(set => set.Key).ToArray();
    }

    private static readonly LobbyAdvert Playing = new(ListedState.Playing, ListedMode.Campaign, new ListedSave("Tchelios", 2, 14));

    [Fact]
    public void The_default_is_in_the_lobby_campaign_and_no_save()
    {
        Assert.Equal(new LobbyAdvert(ListedState.Lobby, ListedMode.Campaign, null), LobbyAdvert.Default);
    }

    [Fact]
    public void A_new_lobby_gets_state_mode_and_the_ping_location_but_no_empty_save()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();

        int sent = advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);

        Assert.Equal(3, sent);
        Assert.Equal(new[] { LobbyDataKeys.State, LobbyDataKeys.Mode, LobbyDataKeys.PingLocation }, sink.Keys());
        Assert.Equal("lobby", sink.Sets[0].Value);
        Assert.Equal("campaign", sink.Sets[1].Value);
        Assert.Equal("ord=70+26,iad=77+3", sink.Sets[2].Value);
        Assert.All(sink.Sets, set => Assert.Equal(LobbyA, set.Lobby));
        Assert.Equal("ord=70+26,iad=77+3", advertiser.PublishedLocation);
    }

    [Fact]
    public void What_is_published_is_said_in_the_log_once_and_not_again_while_it_stays()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();

        advertiser.Publish(LobbyA, Playing, TimeSpan.Zero);
        advertiser.Publish(LobbyA, Playing, TimeSpan.FromSeconds(1));

        Assert.Equal(2, sink.Lines.Count);
        Assert.Contains("state \"play\", mode \"campaign\", save \"Tchelios,2,14\"", sink.Lines[0], StringComparison.Ordinal);
        Assert.Contains("ping location is published", sink.Lines[1], StringComparison.Ordinal);

        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromSeconds(2));
        Assert.Equal(3, sink.Lines.Count);
        Assert.Contains("save (none)", sink.Lines[2], StringComparison.Ordinal);

        // The lobby by its log tag, and the ping location by its length: its data centres say where the host is (D127).
        string id = LobbyA.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(sink.Lines, line => Assert.DoesNotContain(id, line, StringComparison.Ordinal));
        Assert.All(sink.Lines, line => Assert.DoesNotContain("ord=", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_sent_again_while_nothing_changes_however_often_it_is_called()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();
        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);
        sink.Sets.Clear();

        for (TimeSpan t = TimeSpan.FromMilliseconds(16); t < TimeSpan.FromMinutes(4); t += TimeSpan.FromMilliseconds(250))
        {
            Assert.Equal(0, advertiser.Publish(LobbyA, LobbyAdvert.Default, t));
        }

        Assert.Empty(sink.Sets);
    }

    [Fact]
    public void Only_the_values_that_changed_are_sent()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();
        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);
        sink.Sets.Clear();

        advertiser.Publish(LobbyA, Playing, TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { LobbyDataKeys.State, LobbyDataKeys.Save }, sink.Keys());
        Assert.Equal("play", sink.Sets[0].Value);
        Assert.Equal("Tchelios,2,14", sink.Sets[1].Value);

        sink.Sets.Clear();
        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { LobbyDataKeys.State, LobbyDataKeys.Save }, sink.Keys());
        Assert.Equal("lobby", sink.Sets[0].Value);
        Assert.Equal("", sink.Sets[1].Value); // a save taken away is published as taken away
    }

    [Fact]
    public void A_quick_game_is_published_as_quick()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();
        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);
        sink.Sets.Clear();

        advertiser.Publish(LobbyA, new LobbyAdvert(ListedState.Lobby, ListedMode.Quick, null), TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { (LobbyDataKeys.Mode, "quick") }, sink.Sets.Select(set => (set.Key, set.Value)));
    }

    [Fact]
    public void A_different_lobby_starts_from_nothing()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();
        advertiser.Publish(LobbyA, Playing, TimeSpan.Zero);
        sink.Sets.Clear();

        int sent = advertiser.Publish(LobbyB, Playing, TimeSpan.FromSeconds(1));

        Assert.Equal(4, sent);
        Assert.All(sink.Sets, set => Assert.Equal(LobbyB, set.Lobby));
        Assert.Equal(LobbyB, advertiser.Lobby);

        advertiser.Reset();
        Assert.Equal(0UL, advertiser.Lobby);
        Assert.Equal(string.Empty, advertiser.PublishedLocation);
    }

    [Fact]
    public void The_ping_location_is_tried_every_five_seconds_until_steam_has_one_and_then_refreshed_rarely()
    {
        var sink = new Sink { Location = null };
        SteamLobbyAdvertiser advertiser = sink.Make();
        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);
        Assert.DoesNotContain(LobbyDataKeys.PingLocation, sink.Keys());

        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(LobbyDataKeys.PingLocation, sink.Keys());

        sink.Location = "ord=70";
        Assert.Equal(1, advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromSeconds(5)));
        Assert.Equal("ord=70", sink.Sets[^1].Value);

        sink.Location = "ord=90";
        Assert.Equal(0, advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromMinutes(4)));
        Assert.Equal(1, advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromSeconds(5) + SteamLobbyAdvertiser.LocationRefresh));
        Assert.Equal("ord=90", advertiser.PublishedLocation);
    }

    [Fact]
    public void A_location_too_long_for_steams_limit_is_not_published()
    {
        var sink = new Sink { Location = new string('x', SteamPing.MaxLocationLength) };
        SteamLobbyAdvertiser advertiser = sink.Make();

        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);

        Assert.DoesNotContain(LobbyDataKeys.PingLocation, sink.Keys());
    }

    [Fact]
    public void A_refusal_is_logged_once_per_key_and_tried_again_after_ten_seconds()
    {
        var sink = new Sink();
        sink.Refuse.Add(LobbyDataKeys.State);
        SteamLobbyAdvertiser advertiser = sink.Make();

        advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.Zero);
        Assert.Single(sink.Warnings);
        Assert.Contains(LobbyDataKeys.State, sink.Warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain(LobbyDataKeys.State, sink.Keys());

        Assert.Equal(0, advertiser.Publish(LobbyA, LobbyAdvert.Default, TimeSpan.FromSeconds(9)));

        sink.Refuse.Clear();
        Assert.Equal(1, advertiser.Publish(LobbyA, LobbyAdvert.Default, SteamLobbyAdvertiser.RetryInterval));
        Assert.Equal("lobby", sink.Sets[^1].Value);
        Assert.Single(sink.Warnings);

        // Refused again later: the same key doesn't log twice.
        sink.Refuse.Add(LobbyDataKeys.State);
        advertiser.Publish(LobbyA, Playing, TimeSpan.FromSeconds(30));
        Assert.Single(sink.Warnings);
    }

    [Fact]
    public void Lobby_zero_and_no_advert_publish_nothing()
    {
        var sink = new Sink();
        SteamLobbyAdvertiser advertiser = sink.Make();

        Assert.Equal(0, advertiser.Publish(0, LobbyAdvert.Default, TimeSpan.Zero));
        Assert.Equal(0, advertiser.Publish(LobbyA, null!, TimeSpan.Zero));
        Assert.Empty(sink.Sets);
    }
}
