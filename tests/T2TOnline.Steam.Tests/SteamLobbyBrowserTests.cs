using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

/// <summary>The friends browser's data with a scripted Steam: what it asks and when, and which lobbies it believes.</summary>
public class SteamLobbyBrowserTests
{
    private const uint TapeToTape = 1566200;
    private const ulong Me = 76561199000000000UL;
    private const ulong Alex = 76561199000000001UL;
    private const ulong Sam = 76561199000000002UL;
    private const ulong Casey = 76561199000000003UL;
    private const ulong LobbyA = 109775240917663232UL;
    private const ulong LobbyB = 109775240917663233UL;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private sealed class FakeSteam : ILobbyBrowserSource
    {
        private readonly List<(ulong Id, string Name, FriendGameInfo? Game)> _friends = new();

        /// <summary>What the lobbies' owners have set, as Steam's servers have it.</summary>
        public Dictionary<ulong, Dictionary<string, string>> Remote { get; } = new();

        /// <summary>What Steam's local copy holds.</summary>
        public Dictionary<ulong, Dictionary<string, string>> Cache { get; } = new();

        public List<ulong> Requests { get; } = new();

        private readonly Dictionary<ulong, TimeSpan> _pending = new();

        public bool AcceptRequests { get; set; } = true;

        /// <summary>How long Steam takes to answer a request it accepted (null: it never answers).</summary>
        public TimeSpan? Latency { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>The test's clock, set before every poll.</summary>
        public TimeSpan Now { get; set; }

        public int Scans { get; private set; }

        public int Reads { get; private set; }

        public int PingCalls { get; private set; }

        public int PingAnswer { get; set; } = 42;

        public ulong LocalSteamId { get; set; } = Me;

        public uint AppId { get; set; } = TapeToTape;

        public FakeSteam PlayingIn(ulong friend, string name, ulong lobby)
        {
            _friends.RemoveAll(f => f.Id == friend);
            _friends.Add((friend, name, new FriendGameInfo(TapeToTape, lobby)));
            return this;
        }

        public FakeSteam Leaves(ulong friend)
        {
            _friends.RemoveAll(f => f.Id == friend);
            return this;
        }

        public FakeSteam Lobby(ulong lobby, params (string Key, string Value)[] data)
        {
            Remote[lobby] = data.ToDictionary(pair => pair.Key, pair => pair.Value);
            return this;
        }

        public void Deliver(ulong lobby)
        {
            Cache[lobby] = new Dictionary<string, string>(Remote[lobby]);
        }

        public int GetFriendCount(SteamFriendFilter flags)
        {
            Scans++;
            return _friends.Count;
        }

        public ulong GetFriendByIndex(int index, SteamFriendFilter flags) => index < _friends.Count ? _friends[index].Id : 0;

        public bool TryGetFriendGamePlayed(ulong steamId, out FriendGameInfo info)
        {
            FriendGameInfo? game = _friends.First(f => f.Id == steamId).Game;
            info = game ?? default;
            return game.HasValue;
        }

        public string GetFriendPersonaName(ulong steamId) => _friends.First(f => f.Id == steamId).Name;

        public bool RequestLobbyData(ulong lobby)
        {
            Requests.Add(lobby);
            if (!AcceptRequests)
            {
                return false;
            }

            _pending.TryAdd(lobby, Now);
            return true;
        }

        public int GetLobbyDataCount(ulong lobby)
        {
            Reads++;
            Settle(lobby);
            return Cache.TryGetValue(lobby, out Dictionary<string, string>? data) ? data.Count : 0;
        }

        public string GetLobbyData(ulong lobby, string key)
        {
            Settle(lobby);
            return Cache.TryGetValue(lobby, out Dictionary<string, string>? data) && data.TryGetValue(key, out string? value) ? value : string.Empty;
        }

        private void Settle(ulong lobby)
        {
            if (_pending.TryGetValue(lobby, out TimeSpan since) && Latency is { } latency && Now - since >= latency)
            {
                _pending.Remove(lobby);
                if (Remote.ContainsKey(lobby))
                {
                    Deliver(lobby);
                }
            }
        }

        public int EstimatePing(string location)
        {
            PingCalls++;
            return PingAnswer;
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Browser = new SteamLobbyBrowser(Steam, Info.Add, Warnings.Add);
        }

        public FakeSteam Steam { get; } = new();

        public List<string> Info { get; } = new();

        public List<string> Warnings { get; } = new();

        public SteamLobbyBrowser Browser { get; }

        public TimeSpan Now { get; private set; }

        public void Run(TimeSpan duration, bool open = true)
        {
            TimeSpan end = Now + duration;
            while (Now < end)
            {
                Steam.Now = Now;
                Browser.Poll(Now, open);
                Now += Tick;
            }
        }
    }

    private static (string, string)[] Hosted(ulong host = Alex, string players = "2", string state = "lobby", string mode = "campaign", string save = "Tchelios,2,14", string pingloc = "ord=70") => new[]
    {
        (LobbyDataKeys.Mod, "0.9.0-rc"),
        (LobbyDataKeys.Protocol, "7"),
        (LobbyDataKeys.Build, "abc123"),
        (LobbyDataKeys.Host, host.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        (LobbyDataKeys.Players, players),
        (LobbyDataKeys.State, state),
        (LobbyDataKeys.Mode, mode),
        (LobbyDataKeys.Save, save),
        (LobbyDataKeys.PingLocation, pingloc),
    };

    [Fact]
    public void A_browser_nobody_opened_asks_steam_nothing()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());

        h.Run(TimeSpan.FromSeconds(30), open: false);

        Assert.Equal(0, h.Steam.Scans);
        Assert.Equal(0, h.Steam.Reads);
        Assert.Empty(h.Steam.Requests);
        Assert.False(h.Browser.Active);
        Assert.Empty(h.Browser.Listings);
    }

    [Fact]
    public void An_open_browser_scans_every_two_seconds_requests_every_five_and_reads_twice_a_second()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());

        h.Run(TimeSpan.FromSeconds(10) - Tick);

        Assert.Equal(5, h.Steam.Scans); // 0, 2, 4, 6, 8 s
        Assert.Equal(new[] { LobbyA, LobbyA }, h.Steam.Requests); // 0 and 5 s
        Assert.InRange(h.Steam.Reads, 19, 20); // every 0.5 s while open
        Assert.True(h.Browser.Active);
    }

    [Fact]
    public void A_lobby_that_has_no_data_yet_is_checking_then_answered_the_moment_its_data_arrives()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Steam.Latency = null;

        h.Run(TimeSpan.FromSeconds(1));
        SteamLobbyListing waiting = Assert.Single(h.Browser.Listings);
        Assert.Equal(ListingAnswer.Checking, waiting.Answer);
        Assert.Equal(new[] { "Alex" }, waiting.FriendNames);
        Assert.Equal(1, waiting.FriendsSeen);
        Assert.Equal(0UL, waiting.HostId);
        Assert.Equal(ListedMode.Unknown, waiting.Mode);

        h.Steam.Deliver(LobbyA);
        h.Run(Tick * 6);

        SteamLobbyListing answered = Assert.Single(h.Browser.Listings);
        Assert.Equal(ListingAnswer.Answered, answered.Answer);
        Assert.Equal(Alex, answered.HostId);
        Assert.Equal("Alex", answered.HostName);
        Assert.Equal("0.9.0-rc", answered.ModVersion);
        Assert.Equal("7", answered.Protocol);
        Assert.Equal("abc123", answered.Build);
        Assert.Equal(2, answered.Players);
        Assert.Equal(ListedMode.Campaign, answered.Mode);
        Assert.Equal(ListedState.Lobby, answered.State);
        Assert.Equal(new ListedSave("Tchelios", 2, 14), answered.Save);
        Assert.Equal(42, answered.PingMs);
        Assert.Contains(h.Info, line => line.Contains(" answered: host ", StringComparison.Ordinal) && line.Contains("an estimate", StringComparison.Ordinal));
    }

    [Fact]
    public void Its_lines_name_no_friend_and_no_steam_or_lobby_id()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).PlayingIn(Sam, "Sam", LobbyA).Lobby(LobbyA, Hosted(host: Alex));
        h.Steam.PlayingIn(Casey, "Casey", LobbyB).Lobby(LobbyB, Hosted(host: Sam)); // stale: names a host who isn't in it
        h.Steam.Deliver(LobbyA);
        h.Steam.Deliver(LobbyB);

        h.Run(TimeSpan.FromSeconds(8));
        h.Steam.Leaves(Alex).Leaves(Sam).Leaves(Casey);
        h.Run(TimeSpan.FromSeconds(6));

        Assert.Contains(h.Info, line => line.Contains("appeared: 2 friend(s) in it", StringComparison.Ordinal));
        Assert.Contains(h.Info, line => line.Contains("not listing lobby", StringComparison.Ordinal));
        Assert.Contains(h.Info, line => line.Contains("no longer listed", StringComparison.Ordinal));
        foreach (string line in h.Info.Concat(h.Warnings))
        {
            foreach (string personal in new[] { "Alex", "Sam", "Casey", "765611", LobbyA.ToString(System.Globalization.CultureInfo.InvariantCulture), LobbyB.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            {
                Assert.DoesNotContain(personal, line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_copy_of_the_data_that_was_already_there_is_not_an_answer_until_a_request_has_had_time_to_be_answered()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted(state: "lobby"));
        h.Steam.Deliver(LobbyA); // Steam's copy from earlier
        h.Steam.Latency = null;

        h.Run(SteamLobbyBrowser.AnswerWait - Tick * 2);
        Assert.Equal(ListingAnswer.Checking, Assert.Single(h.Browser.Listings).Answer);

        // Meanwhile the host started a game; the next answer brings it.
        h.Steam.Lobby(LobbyA, Hosted(state: "play"));
        h.Steam.Deliver(LobbyA);
        h.Run(Tick * 3);

        SteamLobbyListing listing = Assert.Single(h.Browser.Listings);
        Assert.Equal(ListingAnswer.Answered, listing.Answer);
        Assert.Equal(ListedState.Playing, listing.State);
    }

    [Fact]
    public void Without_an_accepted_request_a_cached_copy_is_never_trusted_and_the_lobby_is_listed_without_details()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Steam.Deliver(LobbyA);
        h.Steam.AcceptRequests = false;

        h.Run(SteamLobbyBrowser.NoAnswerAfter + TimeSpan.FromSeconds(1));

        SteamLobbyListing listing = Assert.Single(h.Browser.Listings);
        Assert.Equal(ListingAnswer.NoAnswer, listing.Answer);
        Assert.Equal(ListedMode.Unknown, listing.Mode);
        Assert.Equal(1, h.Warnings.Count(line => line.Contains("didn't take the request", StringComparison.Ordinal)));
        Assert.Equal(1, h.Info.Count(line => line.Contains("sent no details", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_lobby_steam_never_answers_is_listed_as_no_answer_and_found_when_it_does()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Steam.Latency = null;

        h.Run(SteamLobbyBrowser.NoAnswerAfter - Tick);
        Assert.Equal(ListingAnswer.Checking, Assert.Single(h.Browser.Listings).Answer);
        h.Run(Tick * 3);
        Assert.Equal(ListingAnswer.NoAnswer, Assert.Single(h.Browser.Listings).Answer);

        h.Steam.Deliver(LobbyA);
        h.Run(Tick * 6);
        Assert.Equal(ListingAnswer.Answered, Assert.Single(h.Browser.Listings).Answer);
    }

    [Fact]
    public void A_lobby_whose_data_names_a_host_no_friend_shows_in_it_is_stale_and_not_listed()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Sam, "Sam", LobbyA).Lobby(LobbyA, Hosted(host: Alex)); // Alex left; Steam passed the lobby to Sam

        h.Run(TimeSpan.FromSeconds(8));

        Assert.Empty(h.Browser.Listings);
        Assert.Equal(1, h.Info.Count(line => line.Contains("not listing lobby", StringComparison.Ordinal)));

        // Alex is in it according to their own game info: now it counts.
        h.Steam.PlayingIn(Alex, "Alex", LobbyA);
        h.Run(TimeSpan.FromSeconds(3));
        SteamLobbyListing listing = Assert.Single(h.Browser.Listings);
        Assert.Equal(Alex, listing.HostId);
        Assert.Equal(new[] { "Sam", "Alex" }, listing.FriendNames);
        Assert.Equal(2, listing.FriendsSeen);
    }

    [Fact]
    public void Your_own_lobby_is_not_listed()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Sam, "Sam", LobbyA).Lobby(LobbyA, Hosted(host: Me));

        h.Run(TimeSpan.FromSeconds(5));

        Assert.Empty(h.Browser.Listings);
        Assert.DoesNotContain(h.Info, line => line.Contains("not listing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lobby_that_names_no_host_is_listed_as_it_is_so_its_version_can_be_explained()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Sam, "Sam", LobbyA).Lobby(LobbyA, (LobbyDataKeys.Mod, "0.7.3-rc"), (LobbyDataKeys.Protocol, "5"), (LobbyDataKeys.Build, "abc123"));

        h.Run(TimeSpan.FromSeconds(3));

        SteamLobbyListing listing = Assert.Single(h.Browser.Listings);
        Assert.Equal(ListingAnswer.Answered, listing.Answer);
        Assert.Equal(0UL, listing.HostId);
        Assert.Equal("", listing.HostName);
        Assert.Equal(new[] { "Sam" }, listing.FriendNames);
        Assert.Equal("5", listing.Protocol);
        Assert.Equal(ListedMode.Unknown, listing.Mode);
        Assert.Equal(ListedState.Unknown, listing.State);
        Assert.Null(listing.Save);
        Assert.Equal(-1, listing.PingMs);
        Assert.Equal(ListedCompat.TheirsIsOlder, ListedLobby.CheckCompat(listing.ModVersion, listing.Protocol, listing.Build, 7, "abc123"));
    }

    [Fact]
    public void Garbage_in_a_lobbys_data_is_absent_not_trusted()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(
            LobbyA,
            (LobbyDataKeys.Mod, new string('m', 5000)),
            (LobbyDataKeys.Protocol, new string('9', 500)),
            (LobbyDataKeys.Build, new string('b', 500)),
            (LobbyDataKeys.Host, Alex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (LobbyDataKeys.Players, "lots"),
            (LobbyDataKeys.State, "<b>play</b>"),
            (LobbyDataKeys.Mode, "everything"),
            (LobbyDataKeys.Save, "../../etc,passwd"),
            (LobbyDataKeys.PingLocation, new string('x', 5000)));

        h.Run(TimeSpan.FromSeconds(3));

        SteamLobbyListing listing = Assert.Single(h.Browser.Listings);
        Assert.Equal(32, listing.ModVersion.Length);
        Assert.Equal(8, listing.Protocol.Length);
        Assert.Equal(32, listing.Build.Length);
        Assert.Equal(0, listing.Players);
        Assert.Equal(ListedState.Unknown, listing.State);
        Assert.Equal(ListedMode.Unknown, listing.Mode);
        Assert.Null(listing.Save);
        Assert.Equal(-1, listing.PingMs); // a location this long is not one: no estimate is asked for
        Assert.Equal(0, h.Steam.PingCalls);
    }

    [Fact]
    public void Words_in_a_lobbys_data_reach_the_log_only_as_their_length()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(
            LobbyA,
            (LobbyDataKeys.Mod, "Riley Jones"),
            (LobbyDataKeys.Protocol, "9"),
            (LobbyDataKeys.Build, "Main St 12"),
            (LobbyDataKeys.Host, Alex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (LobbyDataKeys.Save, "Riley Jones,2,14"));
        h.Steam.PlayingIn(Sam, "Sam", LobbyB).Lobby(LobbyB, Hosted(host: Sam, save: "tchel,3,7"));

        h.Run(TimeSpan.FromSeconds(3));

        Assert.Equal(new ListedSave("Riley Jones", 2, 14), h.Browser.Listings.Single(l => l.LobbyId == LobbyA).Save); // shown as it is
        string strange = Assert.Single(h.Info, line => line.Contains(" answered: ", StringComparison.Ordinal) && line.Contains("protocol \"9\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Riley", strange, StringComparison.Ordinal);
        Assert.DoesNotContain("Main", strange, StringComparison.Ordinal);
        Assert.Contains("T2T Online (11 character(s), not as T2T Online writes it)", strange, StringComparison.Ordinal);
        Assert.Contains("save (11 character(s), not as T2T Online writes it) act 2 floor 14", strange, StringComparison.Ordinal);

        string plain = Assert.Single(h.Info, line => line.Contains(" answered: ", StringComparison.Ordinal) && line.Contains("protocol \"7\"", StringComparison.Ordinal));
        Assert.Contains("T2T Online \"0.9.0-rc\", protocol \"7\", build \"abc123\"", plain, StringComparison.Ordinal);
        Assert.Contains("save \"tchel\" act 3 floor 7", plain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "(none)")]
    [InlineData("0.12.0-beta", "\"0.12.0-beta\"")]
    [InlineData("0709d469a61cec", "\"0709d469a61cec\"")]
    [InlineData("old_school+2", "\"old_school+2\"")]
    [InlineData("a b", "(3 character(s), not as T2T Online writes it)")]
    [InlineData("é", "(1 character(s), not as T2T Online writes it)")]
    [InlineData("x\"y", "(3 character(s), not as T2T Online writes it)")]
    public void A_value_from_another_pc_is_logged_as_it_is_only_in_the_shape_the_mod_writes(string value, string logged) =>
        Assert.Equal(logged, SteamLobbyBrowser.ShowRemote(value));

    [Fact]
    public void A_lobby_no_friends_game_info_names_goes_after_two_scans_in_a_row_but_not_after_one()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Single(h.Browser.Listings);

        SteamLobbyListing before = h.Browser.Listings[0];
        h.Steam.Leaves(Alex);
        h.Run(SteamLobbyBrowser.ScanInterval * 0.6); // one scan sees nothing
        Assert.Same(before, Assert.Single(h.Browser.Listings)); // and the row stays
        h.Steam.PlayingIn(Alex, "Alex", LobbyA); // a blink: back before the next
        h.Run(TimeSpan.FromSeconds(6));
        Assert.Same(before, Assert.Single(h.Browser.Listings)); // the same row all along

        h.Steam.Leaves(Alex);
        h.Run(SteamLobbyBrowser.ScanInterval * SteamLobbyBrowser.MissedScansToRemove + TimeSpan.FromSeconds(1));
        Assert.Empty(h.Browser.Listings);
        Assert.Contains(h.Info, line => line.Contains("is no longer listed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_is_the_same_object_until_something_in_it_changes()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted(players: "2"));
        h.Run(TimeSpan.FromSeconds(3));
        IReadOnlyList<SteamLobbyListing> before = h.Browser.Listings;
        int version = h.Browser.Version;

        h.Run(TimeSpan.FromSeconds(12)); // many reads, requests and scans of the same data

        Assert.Same(before, h.Browser.Listings);
        Assert.Equal(version, h.Browser.Version);

        h.Steam.Lobby(LobbyA, Hosted(players: "3"));
        h.Run(TimeSpan.FromSeconds(6)); // the next request's answer brings it

        Assert.NotSame(before, h.Browser.Listings);
        Assert.Equal(3, h.Browser.Listings[0].Players);
        Assert.True(h.Browser.Version > version);
    }

    [Fact]
    public void Lobbies_stay_in_the_order_they_first_appeared_and_new_ones_go_below()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Sam, "Sam", LobbyB).Lobby(LobbyB, Hosted(host: Sam));
        h.Run(TimeSpan.FromSeconds(3));
        h.Steam.PlayingIn(Casey, "Casey", LobbyA).Lobby(LobbyA, Hosted(host: Casey));
        h.Run(TimeSpan.FromSeconds(4));

        Assert.Equal(new[] { LobbyB, LobbyA }, h.Browser.Listings.Select(l => l.LobbyId));
        Assert.Equal(new[] { 0, 1 }, h.Browser.Listings.Select(l => l.Sequence));
    }

    [Fact]
    public void Two_friends_in_one_lobby_are_one_row()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).PlayingIn(Sam, "Sam", LobbyA).Lobby(LobbyA, Hosted());

        h.Run(TimeSpan.FromSeconds(3));

        SteamLobbyListing listing = Assert.Single(h.Browser.Listings);
        Assert.Equal(2, listing.FriendsSeen);
        Assert.Equal("Alex", listing.HostName);
        Assert.Single(h.Steam.Requests); // one request for the lobby, not one per friend in it
    }

    [Fact]
    public void At_most_sixteen_lobbies_are_followed()
    {
        var h = new Harness();
        for (ulong i = 0; i < 20; i++)
        {
            h.Steam.PlayingIn(1000 + i, $"host {i}", LobbyA + i).Lobby(LobbyA + i, Hosted(host: 1000 + i));
        }

        h.Run(TimeSpan.FromSeconds(3));

        Assert.Equal(SteamLobbyBrowser.MaxLobbies, h.Browser.Listings.Count);
        Assert.Equal(SteamLobbyBrowser.MaxLobbies, h.Steam.Requests.Distinct().Count());
    }

    [Fact]
    public void Refresh_scans_and_requests_at_once_and_keeps_an_unopened_browser_going_for_fifteen_seconds()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());

        h.Browser.Refresh(h.Now);
        h.Run(SteamLobbyBrowser.Linger - Tick, open: false);
        Assert.True(h.Browser.Active);
        Assert.Single(h.Browser.Listings);
        int scans = h.Steam.Scans;

        h.Run(TimeSpan.FromSeconds(1) + Tick, open: false);
        Assert.False(h.Browser.Active);
        h.Run(TimeSpan.FromSeconds(20), open: false);

        Assert.Equal(scans, h.Steam.Scans); // nothing more once the time is up
        Assert.Single(h.Browser.Listings); // what was found stays for a join to look up

        // A second look while it is going re-requests everything now.
        h.Browser.Refresh(h.Now);
        h.Run(Tick * 2, open: false);
        h.Browser.Refresh(h.Now);
        int requests = h.Steam.Requests.Count;
        h.Run(Tick * 2, open: false);
        Assert.True(h.Steam.Requests.Count > requests);
    }

    [Fact]
    public void Stop_ends_a_lingering_look_at_once_but_not_an_open_browser()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Browser.Refresh(h.Now);
        h.Run(TimeSpan.FromSeconds(3), open: false);
        Assert.True(h.Browser.Active);

        h.Browser.Stop();
        h.Run(Tick, open: false);
        Assert.False(h.Browser.Active);

        h.Run(Tick, open: true);
        Assert.True(h.Browser.Active);
        h.Browser.Stop();
        h.Run(Tick, open: true);
        Assert.True(h.Browser.Active);
    }

    [Fact]
    public void Opening_again_starts_from_nothing_so_a_lobby_that_closed_meanwhile_is_not_shown()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Single(h.Browser.Listings);

        h.Run(TimeSpan.FromSeconds(1), open: false);
        h.Steam.Leaves(Alex);
        h.Steam.Now = h.Now;
        h.Browser.Poll(h.Now, open: true);

        Assert.True(h.Browser.Active);
        Assert.Empty(h.Browser.Listings);
    }

    [Fact]
    public void The_ping_estimate_is_asked_when_the_location_changes_and_every_five_seconds_and_retried_while_steam_does_not_know()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted(pingloc: "ord=70"));
        h.Steam.PingAnswer = SteamPing.Unknown;

        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(-1, h.Browser.Listings[0].PingMs); // an estimate of "not known yet" is no number
        int calls = h.Steam.PingCalls;
        Assert.InRange(calls, 1, 2);

        h.Steam.PingAnswer = 63;
        h.Run(SteamLobbyBrowser.PingRetry + TimeSpan.FromSeconds(1));
        Assert.Equal(63, h.Browser.Listings[0].PingMs);

        int settled = h.Steam.PingCalls;
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(settled, h.Steam.PingCalls); // not asked on every read

        h.Steam.PingAnswer = 80;
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(80, h.Browser.Listings[0].PingMs); // refreshed after five seconds
    }

    [Fact]
    public void A_lobby_steam_lets_go_of_while_a_friend_still_names_it_keeps_what_was_read()
    {
        var h = new Harness();
        h.Steam.PlayingIn(Alex, "Alex", LobbyA).Lobby(LobbyA, Hosted());
        h.Run(TimeSpan.FromSeconds(3));
        SteamLobbyListing before = Assert.Single(h.Browser.Listings);

        h.Steam.Cache.Remove(LobbyA);
        h.Steam.Latency = null;
        h.Run(TimeSpan.FromSeconds(2));

        Assert.Same(before, Assert.Single(h.Browser.Listings));
    }

    [Fact]
    public void Without_steam_nothing_is_listed_and_nothing_throws()
    {
        var h = new Harness();
        h.Steam.AppId = 0;
        h.Steam.PlayingIn(Alex, "Alex", LobbyA);

        h.Run(TimeSpan.FromSeconds(5));

        Assert.Empty(h.Browser.Listings);
        h.Browser.Clear();
        Assert.Empty(h.Browser.Listings);
    }
}
