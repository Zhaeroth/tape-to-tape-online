using System;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class BrowserRowsTests
{
    private const long Now = 100_000_000;
    private static readonly BrowserIdentity Us = new(7, "abc");

    private static LobbyListing Listing(ulong lobby, string host, int players = 2, int max = 4, int protocol = 7, string build = "abc", bool details = true, long age = 0, ulong? hostId = null,
        ListingState state = ListingState.InLobby, bool invited = false, int ping = -1, string mod = "0.9.0-rc") => new()
    {
        LobbyId = lobby,
        HostId = hostId ?? lobby + 1000,
        HostName = host,
        FriendsInIt = new[] { hostId ?? lobby + 1000 },
        HasDetails = details,
        DetailsAgeMicros = age,
        Players = players,
        MaxPlayers = max,
        State = state,
        Protocol = protocol,
        Build = build,
        ModVersion = mod,
        InvitedUs = invited,
        PingMs = ping,
    };

    private static BrowserList Listed(params LobbyListing[] listings)
    {
        var list = new BrowserList();
        list.Update(listings, Us, Now);
        return list;
    }

    [Fact]
    public void Rows_are_in_a_stable_order_by_host_name_whatever_steam_gives()
    {
        BrowserList list = Listed(Listing(1, "Zed"), Listing(2, "alex"), Listing(3, "Mo"));

        Assert.Equal(new[] { "alex", "Mo", "Zed" }, list.Rows.Select(r => r.HostName));

        list.Update(new[] { Listing(3, "Mo", players: 3), Listing(1, "Zed"), Listing(2, "alex", players: 1) }, Us, Now);
        Assert.Equal(new[] { "alex", "Mo", "Zed" }, list.Rows.Select(r => r.HostName)); // player counts don't reshuffle under the cursor
    }

    [Fact]
    public void Two_hosts_with_one_name_stay_in_a_stable_order()
    {
        BrowserList list = Listed(Listing(9, "Sam"), Listing(4, "Sam"));
        Assert.Equal(new ulong[] { 4, 9 }, list.Rows.Select(r => r.LobbyId));
    }

    [Fact]
    public void Focus_starts_on_the_first_joinable_row()
    {
        BrowserList list = Listed(Listing(1, "Al", players: 4), Listing(2, "Bo"), Listing(3, "Cy"));

        Assert.Equal(2UL, list.FocusedLobby);
        Assert.Equal(1, list.FocusIndex);
        Assert.Equal("Bo", list.FocusedRow!.HostName);
    }

    [Fact]
    public void With_only_greyed_rows_focus_starts_on_the_first_which_explains_itself()
    {
        BrowserList list = Listed(Listing(1, "Al", players: 4), Listing(2, "Bo", protocol: 6));

        Assert.Equal(1UL, list.FocusedLobby);
        Assert.False(list.FocusedRow!.Joinable);
    }

    [Fact]
    public void Focus_follows_the_lobby_not_the_row_number()
    {
        BrowserList list = Listed(Listing(2, "Mo"), Listing(3, "Zed"));
        list.Focus(3);
        Assert.Equal(1, list.FocusIndex);

        // A new host sorts above: the row number changes, and focus doesn't.
        Assert.True(list.Update(new[] { Listing(1, "Al"), Listing(2, "Mo"), Listing(3, "Zed") }, Us, Now));

        Assert.Equal(3UL, list.FocusedLobby);
        Assert.Equal(2, list.FocusIndex);
    }

    [Fact]
    public void A_row_that_goes_fades_and_focus_moves_to_the_next()
    {
        BrowserList list = Listed(Listing(1, "Al"), Listing(2, "Bo"), Listing(3, "Cy"));
        list.Focus(2);

        Assert.True(list.Update(new[] { Listing(1, "Al"), Listing(3, "Cy") }, Us, Now));

        Assert.Equal(3UL, list.FocusedLobby);
        BrowserRow gone = list.Rows.Single(r => r.LobbyId == 2);
        Assert.True(gone.Fading);
        Assert.False(gone.Joinable);

        // Its time up, it goes for good.
        list.Update(new[] { Listing(1, "Al"), Listing(3, "Cy") }, Us, Now + BrowserList.FadeMicros);
        Assert.DoesNotContain(list.Rows, r => r.LobbyId == 2);
        Assert.Equal(3UL, list.FocusedLobby);
    }

    [Fact]
    public void The_last_row_going_moves_focus_to_the_one_before()
    {
        BrowserList list = Listed(Listing(1, "Al"), Listing(2, "Bo"));
        list.Focus(2);

        list.Update(new[] { Listing(1, "Al") }, Us, Now);

        Assert.Equal(1UL, list.FocusedLobby);
    }

    [Fact]
    public void A_row_that_comes_back_while_fading_just_stays()
    {
        BrowserList list = Listed(Listing(1, "Al"), Listing(2, "Bo"));
        list.Update(new[] { Listing(1, "Al") }, Us, Now);
        Assert.True(list.Rows.Single(r => r.LobbyId == 2).Fading);

        list.Update(new[] { Listing(1, "Al"), Listing(2, "Bo") }, Us, Now + 1000);

        Assert.False(list.Rows.Single(r => r.LobbyId == 2).Fading);
    }

    [Fact]
    public void The_empty_state_shows_when_nothing_can_be_focused_and_a_row_arriving_takes_focus()
    {
        var list = new BrowserList();
        list.Update(Array.Empty<LobbyListing>(), Us, Now);
        Assert.True(list.IsEmpty);
        Assert.Equal(0UL, list.FocusedLobby);
        Assert.Equal(-1, list.FocusIndex);
        Assert.Null(list.FocusedRow);
        Assert.Null(list.HostingLine());

        list.Update(new[] { Listing(1, "Al") }, Us, Now);
        Assert.False(list.IsEmpty);
        Assert.Equal(1UL, list.FocusedLobby);

        // Only a fading row left: the empty state again, and nothing focused.
        list.Update(Array.Empty<LobbyListing>(), Us, Now);
        Assert.True(list.IsEmpty);
        Assert.Equal(0UL, list.FocusedLobby);
    }

    [Fact]
    public void A_full_lobby_stays_greyed_with_a_lock_and_its_count()
    {
        BrowserRow row = Listed(Listing(1, "Al", players: 4)).Rows[0];

        Assert.Equal(BrowserBlock.Full, row.Block);
        Assert.False(row.Joinable);
        Assert.Equal("4/4", row.Count);
        Assert.Equal((LobbyIcon.Lock, LobbyWords.Full.Key), (row.Mark.Icon, row.Mark.Line.Term));
    }

    [Fact]
    public void Another_protocol_or_game_update_is_a_greyed_row_with_its_reason()
    {
        BrowserRow other = Listed(Listing(1, "Al", protocol: 6)).Rows[0];
        Assert.Equal(BrowserBlock.DifferentMod, other.Block);
        Assert.Equal((LobbyIcon.Lock, LobbyWords.DifferentMod.Key), (other.Mark.Icon, other.Mark.Line.Term));

        BrowserRow update = Listed(Listing(1, "Al", build: "def")).Rows[0];
        Assert.Equal(BrowserBlock.DifferentBuild, update.Block);
        Assert.Equal((LobbyIcon.Lock, LobbyWords.DifferentUpdate.Key), (update.Mark.Icon, update.Mark.Line.Term));

        // A protocol that isn't a number: not ours either.
        Assert.Equal(BrowserBlock.DifferentMod, Listed(Listing(1, "Al", protocol: -1)).Rows[0].Block);
    }

    [Fact]
    public void A_lobby_in_a_game_is_still_joinable_and_says_so()
    {
        BrowserRow row = Listed(Listing(1, "Al", state: ListingState.InGame)).Rows[0];

        Assert.True(row.Joinable);
        Assert.Equal(LobbyWords.InGame.Key, row.Mark.Line.Term);
        Assert.Equal(LobbyWords.InLobby.Key, Listed(Listing(1, "Al")).Rows[0].Mark.Line.Term);
    }

    [Fact]
    public void An_invite_marks_the_row_and_it_says_who()
    {
        BrowserRow row = Listed(Listing(1, "Al", invited: true)).Rows[0];

        Assert.Equal(LobbyIcon.InvitedYou, row.Mark.Icon);
        Assert.Equal("Al invited you.", row.Mark.Line.Text(0));
    }

    [Fact]
    public void An_estimated_ping_is_bars_and_unknown_is_none()
    {
        Assert.Equal((3, 58), (Listed(Listing(1, "Al", ping: 58)).Rows[0].Bars, Listed(Listing(1, "Al", ping: 58)).Rows[0].PingMs));
        Assert.Equal((0, -1), (Listed(Listing(1, "Al", ping: -7)).Rows[0].Bars, Listed(Listing(1, "Al", ping: -7)).Rows[0].PingMs));
    }

    [Fact]
    public void A_lobby_without_details_yet_is_checking_with_the_friends_name_then_goes()
    {
        var waiting = new LobbyListing { LobbyId = 1, HostName = "Sam, Alex", FriendsInIt = new ulong[] { 5, 6 }, DetailsAgeMicros = 1_000_000 };
        BrowserRow row = Listed(waiting).Rows[0];

        Assert.Equal(BrowserBlock.Checking, row.Block);
        Assert.False(row.Joinable);
        Assert.Equal(2, row.Players);
        Assert.Equal("Sam, Alex", row.HostName);
        Assert.Equal((LobbyIcon.Dots, LobbyWords.Checking.Key), (row.Mark.Icon, row.Mark.Line.Term));

        Assert.Empty(Listed(waiting with { DetailsAgeMicros = BrowserList.NoDetailsGiveUpMicros }).Rows);
    }

    [Fact]
    public void A_lobby_steams_browser_answered_for_is_listed_with_nothing_but_its_answer()
    {
        // The plugin's listing has no friends of its own to name: Steam's browser only gives what it has checked. Listing nobody here dropped every real row, and said no friend was hosting.
        LobbyListing fromSteam = Listing(1, "Alex", hostId: 77) with { FriendsInIt = Array.Empty<ulong>(), HasDetails = false };
        Assert.Empty(Listed(fromSteam with { HasDetails = true }).Rows);

        BrowserList list = Listed(fromSteam.WithBrowserAnswer(answered: true, noAnswer: false));

        BrowserRow row = Assert.Single(list.Rows);
        Assert.Equal(("Alex", 77UL, BrowserBlock.None), (row.HostName, row.HostId, row.Block));
        Assert.True(row.Joinable);
        Assert.Equal("Alex is hosting", list.HostingLine()!.Text(0));
    }

    [Fact]
    public void A_lobby_steams_browser_has_no_answer_for_is_checked_then_given_up_on()
    {
        LobbyListing fromSteam = Listing(1, "Sam, Alex", hostId: 0) with { FriendsInIt = Array.Empty<ulong>() };

        Assert.Equal(BrowserBlock.Checking, Assert.Single(Listed(fromSteam.WithBrowserAnswer(answered: false, noAnswer: false)).Rows).Block);
        Assert.Empty(Listed(fromSteam.WithBrowserAnswer(answered: false, noAnswer: true)).Rows);

        // An answer with no host named isn't ours to list either.
        Assert.Empty(Listed(fromSteam.WithBrowserAnswer(answered: true, noAnswer: false)).Rows);
    }

    [Fact]
    public void A_lobby_whose_host_no_longer_names_it_is_not_listed()
    {
        // Steam hands a lobby to someone else when its host goes: it would point at nothing.
        LobbyListing handed = Listing(1, "Al") with { HostId = 5, FriendsInIt = new ulong[] { 6, 7 } };

        Assert.Empty(Listed(handed).Rows);
    }

    [Fact]
    public void A_lobby_without_the_mod_or_a_host_is_not_listed()
    {
        Assert.Empty(Listed(Listing(1, "Al", mod: string.Empty)).Rows);
        Assert.Empty(Listed(Listing(1, "Al") with { HostId = 0 }).Rows);
        Assert.Empty(Listed(Listing(0, "Al")).Rows);
    }

    [Fact]
    public void Friends_in_one_lobby_are_one_row()
    {
        BrowserList list = Listed(Listing(1, "Al"), Listing(1, "Al again"), Listing(2, "Bo"));

        Assert.Equal(2, list.Rows.Count);
    }

    [Fact]
    public void Numbers_from_a_lobby_are_kept_in_their_bounds()
    {
        BrowserRow row = Listed(Listing(1, "Al", players: 99, max: 99)).Rows[0];
        Assert.Equal((LobbyState.MaxSeats, LobbyState.MaxSeats), (row.Players, row.MaxPlayers));
        Assert.Equal(BrowserBlock.Full, row.Block);

        BrowserRow odd = Listed(Listing(1, "Al", players: -3, max: 0)).Rows[0];
        Assert.Equal((0, 1), (odd.Players, odd.MaxPlayers));
    }

    [Fact]
    public void Too_many_rows_are_capped()
    {
        BrowserList list = Listed(Enumerable.Range(1, 500).Select(i => Listing((ulong)i, $"Host {i:000}")).ToArray());

        Assert.Equal(BrowserList.MaxRows, list.Rows.Count);
    }

    [Fact]
    public void An_update_says_when_nothing_changed()
    {
        BrowserList list = Listed(Listing(1, "Al"));

        Assert.False(list.Update(new[] { Listing(1, "Al") }, Us, Now));
        Assert.True(list.Update(new[] { Listing(1, "Al", players: 3) }, Us, Now));
    }

    [Fact]
    public void Moving_wraps_and_skips_fading_rows()
    {
        BrowserList list = Listed(Listing(1, "Al"), Listing(2, "Bo"), Listing(3, "Cy"));
        list.Focus(1);

        list.Move(-1);
        Assert.Equal(3UL, list.FocusedLobby);
        list.Move(1);
        Assert.Equal(1UL, list.FocusedLobby);

        list.Update(new[] { Listing(1, "Al"), Listing(3, "Cy") }, Us, Now);
        list.Move(1);
        Assert.Equal(3UL, list.FocusedLobby); // Bo is fading
        list.Move(-1);
        Assert.Equal(1UL, list.FocusedLobby);

        new BrowserList().Move(1); // nothing to move in: nothing happens
    }

    [Fact]
    public void Focus_on_what_is_not_there_says_false()
    {
        BrowserList list = Listed(Listing(1, "Al"));
        Assert.False(list.Focus(99));
        Assert.Equal(1UL, list.FocusedLobby);
    }

    [Fact]
    public void The_live_line_names_one_host_and_counts_several()
    {
        Assert.Equal("Al is hosting", Listed(Listing(1, "Al")).HostingLine()!.Text(0));
        Assert.Equal("2 friends hosting", Listed(Listing(1, "Al"), Listing(2, "Bo", players: 4)).HostingLine()!.Text(0));
    }
}
