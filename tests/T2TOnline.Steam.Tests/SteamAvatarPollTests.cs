using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Steam.Tests;

/// <summary>The avatar poll with a scripted Steam: when it asks, how often, how many at once, and what it gives up on.</summary>
public class SteamAvatarPollTests
{
    private const ulong Alex = 76561199000000001UL;
    private const ulong Sam = 76561199000000002UL;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private sealed class FakeSteam : ISteamAvatarApi
    {
        public Dictionary<(ulong, SteamAvatarSize), int> Handles { get; } = new();

        public bool? InfoAnswer { get; set; } = true;

        public List<ulong> InfoRequests { get; } = new();

        public int HandleCalls { get; set; }

        public bool? RequestUserInformation(ulong steamId)
        {
            InfoRequests.Add(steamId);
            return InfoAnswer;
        }

        public int GetHandle(ulong steamId, SteamAvatarSize size)
        {
            HandleCalls++;
            return Handles.TryGetValue((steamId, size), out int handle) ? handle : SteamAvatars.Loading;
        }
    }

    private static SteamAvatarKey Key(ulong id, SteamAvatarSize size = SteamAvatarSize.Large) => new(id, size);

    [Fact]
    public void A_new_avatar_waits_asks_for_user_information_and_polls_at_two_hertz()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);

        Assert.Equal(SteamAvatarPhase.Waiting, poll.Want(Key(Alex), TimeSpan.Zero));
        Assert.Equal(1, poll.Poll(TimeSpan.Zero));
        Assert.Equal(new[] { Alex }, steam.InfoRequests);
        Assert.Equal(1, steam.HandleCalls);

        // Nothing is due for half a second, however many frames pass.
        for (TimeSpan t = Frame; t < SteamAvatarPoll.HandlePollInterval; t += Frame)
        {
            Assert.Equal(0, poll.Poll(t));
        }

        Assert.Equal(1, steam.HandleCalls);
        Assert.Equal(1, poll.Poll(SteamAvatarPoll.HandlePollInterval));
        Assert.Equal(2, steam.HandleCalls);
        Assert.Equal(SteamAvatarPhase.Waiting, poll.PhaseOf(Key(Alex)));
    }

    [Fact]
    public void User_information_is_requested_every_five_seconds_at_most_six_times_and_stops_when_steam_has_it()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        poll.Want(Key(Sam), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = SteamAvatars.Loading;
        steam.Handles[(Sam, SteamAvatarSize.Large)] = SteamAvatars.Loading;

        for (TimeSpan t = TimeSpan.Zero; t <= TimeSpan.FromSeconds(29); t += SteamAvatarPoll.HandlePollInterval)
        {
            poll.Want(Key(Alex), t);
            poll.Want(Key(Sam), t);
            poll.Poll(t);
        }

        // 0, 5, 10, 15, 20 and 25 s: six requests each, never a seventh.
        Assert.Equal(6, steam.InfoRequests.Count(id => id == Alex));
        Assert.Equal(6, steam.InfoRequests.Count(id => id == Sam));

        var known = new FakeSteam { InfoAnswer = false };
        var other = new SteamAvatarPoll(known);
        other.Want(Key(Alex), TimeSpan.Zero);
        for (TimeSpan t = TimeSpan.Zero; t <= TimeSpan.FromSeconds(20); t += SteamAvatarPoll.HandlePollInterval)
        {
            other.Poll(t);
        }

        Assert.Single(known.InfoRequests); // "we have everything already": asked once, and not again
    }

    [Fact]
    public void A_handle_moves_the_avatar_to_fetch_and_the_owner_reports_back()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 4242;

        poll.Poll(TimeSpan.Zero);

        Assert.Equal(SteamAvatarPhase.Fetch, poll.PhaseOf(Key(Alex)));
        Assert.True(poll.TryNextFetch(out SteamAvatarKey key, out int handle));
        Assert.Equal(Key(Alex), key);
        Assert.Equal(4242, handle);

        // Fetch isn't polled while it waits for the owner.
        int calls = steam.HandleCalls;
        poll.Poll(TimeSpan.FromSeconds(3));
        Assert.Equal(calls, steam.HandleCalls);

        poll.Fetched(key, handle, TimeSpan.FromSeconds(3));
        Assert.Equal(SteamAvatarPhase.Ready, poll.PhaseOf(key));
        Assert.False(poll.TryNextFetch(out _, out _));
    }

    [Fact]
    public void A_loaded_avatar_that_is_not_seated_is_never_asked_about_again()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 7;
        poll.Poll(TimeSpan.Zero);
        poll.TryNextFetch(out SteamAvatarKey key, out int handle);
        poll.Fetched(key, handle, TimeSpan.Zero);
        int calls = steam.HandleCalls;

        for (TimeSpan t = Frame; t < TimeSpan.FromMinutes(5); t += TimeSpan.FromMilliseconds(250))
        {
            Assert.Equal(0, poll.Poll(t));
        }

        Assert.Equal(calls, steam.HandleCalls);
    }

    [Fact]
    public void A_seated_players_avatar_is_checked_every_ten_seconds_and_a_new_handle_is_fetched_again()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        poll.SetSeated(new[] { Alex });
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 7;
        poll.Poll(TimeSpan.Zero);
        poll.TryNextFetch(out SteamAvatarKey key, out int handle);
        poll.Fetched(key, handle, TimeSpan.Zero);
        int calls = steam.HandleCalls;

        Assert.Equal(0, poll.Poll(TimeSpan.FromSeconds(9.9)));
        Assert.Equal(1, poll.Poll(SteamAvatarPoll.SeatedRefresh));
        Assert.Equal(calls + 1, steam.HandleCalls);
        Assert.Equal(SteamAvatarPhase.Ready, poll.PhaseOf(key)); // the same handle: nothing to do

        steam.Handles[(Alex, SteamAvatarSize.Large)] = 8; // the player changed their avatar
        Assert.Equal(1, poll.Poll(SteamAvatarPoll.SeatedRefresh * 2));
        Assert.Equal(SteamAvatarPhase.Fetch, poll.PhaseOf(key));
        poll.TryNextFetch(out _, out handle);
        Assert.Equal(8, handle);
        poll.Fetched(key, handle, SteamAvatarPoll.SeatedRefresh * 2);
        Assert.Equal(SteamAvatarPhase.Ready, poll.PhaseOf(key));
    }

    [Fact]
    public void Loading_keeps_waiting_and_no_avatar_waits_thirty_seconds_before_it_is_given_up()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        poll.Want(Key(Sam), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = SteamAvatars.NoAvatar;
        steam.Handles[(Sam, SteamAvatarSize.Large)] = SteamAvatars.Loading;

        for (TimeSpan t = TimeSpan.Zero; t < SteamAvatarPoll.NoneGiveUp; t += SteamAvatarPoll.HandlePollInterval)
        {
            poll.Want(Key(Alex), t);
            poll.Want(Key(Sam), t);
            poll.Poll(t);
            Assert.Equal(SteamAvatarPhase.Waiting, poll.PhaseOf(Key(Alex)));
        }

        TimeSpan end = SteamAvatarPoll.NoneGiveUp;
        poll.Want(Key(Alex), end);
        poll.Want(Key(Sam), end);
        poll.Poll(end);

        Assert.Equal(SteamAvatarPhase.None, poll.PhaseOf(Key(Alex)));
        Assert.Equal(SteamAvatarPhase.Waiting, poll.PhaseOf(Key(Sam))); // -1 is "keep asking", never "none"
    }

    [Fact]
    public void An_avatar_given_up_on_is_asked_about_again_every_thirty_seconds_and_found_when_it_appears()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = SteamAvatars.NoAvatar;
        for (TimeSpan t = TimeSpan.Zero; t <= SteamAvatarPoll.NoneGiveUp; t += SteamAvatarPoll.HandlePollInterval)
        {
            poll.Want(Key(Alex), t);
            poll.Poll(t);
        }

        Assert.Equal(SteamAvatarPhase.None, poll.PhaseOf(Key(Alex)));
        int calls = steam.HandleCalls;
        TimeSpan later = SteamAvatarPoll.NoneGiveUp + SteamAvatarPoll.NoneRetry;
        Assert.Equal(0, poll.Poll(later - TimeSpan.FromSeconds(1)));
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 99;
        poll.Want(Key(Alex), later);
        Assert.Equal(1, poll.Poll(later));

        Assert.Equal(calls + 1, steam.HandleCalls);
        Assert.Equal(SteamAvatarPhase.Fetch, poll.PhaseOf(Key(Alex)));
    }

    [Fact]
    public void Giving_up_on_an_avatar_is_reported_once_and_removing_it_lets_it_start_over()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = SteamAvatars.NoAvatar;
        Assert.False(poll.TryTakeGivenUp(out _));

        for (TimeSpan t = TimeSpan.Zero; t <= SteamAvatarPoll.NoneGiveUp + SteamAvatarPoll.NoneRetry + SteamAvatarPoll.NoneRetry; t += SteamAvatarPoll.HandlePollInterval)
        {
            poll.Want(Key(Alex), t);
            poll.Poll(t);
        }

        Assert.True(poll.TryTakeGivenUp(out SteamAvatarKey key));
        Assert.Equal(Key(Alex), key);
        Assert.False(poll.TryTakeGivenUp(out _)); // asked about again twice since, still none: said once

        Assert.True(poll.Remove(key));
        Assert.False(poll.Remove(key));
        Assert.Null(poll.PhaseOf(key));
        Assert.Equal(SteamAvatarPhase.Waiting, poll.Want(key, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Steam_being_unavailable_gives_up_at_once_and_does_not_ask_every_half_second()
    {
        var steam = new FakeSteam();
        steam.Handles[(Alex, SteamAvatarSize.Large)] = SteamAvatars.Unavailable;
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);

        poll.Poll(TimeSpan.Zero);

        Assert.Equal(SteamAvatarPhase.None, poll.PhaseOf(Key(Alex)));
        Assert.True(poll.TryTakeGivenUp(out SteamAvatarKey gone));
        Assert.Equal(Key(Alex), gone);
        Assert.Equal(0, poll.Poll(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void At_most_four_avatars_are_asked_about_per_poll_and_the_rest_come_next_frame()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        for (ulong i = 0; i < 10; i++)
        {
            poll.Want(Key(Alex + i), TimeSpan.Zero);
        }

        Assert.Equal(SteamAvatarPoll.MaxHandlePollsPerStep, poll.Poll(TimeSpan.Zero));
        Assert.Equal(SteamAvatarPoll.MaxHandlePollsPerStep, poll.Poll(Frame));
        Assert.Equal(2, poll.Poll(Frame * 2));
        Assert.Equal(10, steam.HandleCalls);
        Assert.Equal(0, poll.Poll(Frame * 3));
    }

    [Fact]
    public void Fetches_come_one_at_a_time_oldest_first()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        for (ulong i = 0; i < 3; i++)
        {
            poll.Want(Key(Alex + i), TimeSpan.Zero);
            steam.Handles[(Alex + i, SteamAvatarSize.Large)] = 100 + (int)i;
        }

        poll.Poll(TimeSpan.Zero);

        var order = new List<ulong>();
        while (poll.TryNextFetch(out SteamAvatarKey key, out int handle))
        {
            order.Add(key.SteamId);
            poll.Fetched(key, handle, TimeSpan.Zero);
        }

        Assert.Equal(new[] { Alex, Alex + 1, Alex + 2 }, order);
    }

    [Fact]
    public void A_failed_read_is_tried_again_and_after_three_the_avatar_is_none()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 5;
        TimeSpan now = TimeSpan.Zero;
        poll.Poll(now);

        for (int attempt = 1; attempt <= SteamAvatarPoll.MaxReadFailures; attempt++)
        {
            Assert.True(poll.TryNextFetch(out SteamAvatarKey key, out int handle));
            poll.FetchFailed(key, handle, now);
            if (attempt < SteamAvatarPoll.MaxReadFailures)
            {
                Assert.Equal(SteamAvatarPhase.Waiting, poll.PhaseOf(key));
                now += SteamAvatarPoll.ReadRetry;
                poll.Want(key, now);
                poll.Poll(now);
            }
        }

        Assert.Equal(SteamAvatarPhase.None, poll.PhaseOf(Key(Alex)));
        Assert.False(poll.TryNextFetch(out _, out _));
    }

    [Fact]
    public void A_failed_refresh_keeps_the_image_already_loaded()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        poll.SetSeated(new[] { Alex });
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 5;
        poll.Poll(TimeSpan.Zero);
        poll.TryNextFetch(out SteamAvatarKey key, out int handle);
        poll.Fetched(key, handle, TimeSpan.Zero);

        steam.Handles[(Alex, SteamAvatarSize.Large)] = 6;
        poll.Poll(SteamAvatarPoll.SeatedRefresh);
        Assert.True(poll.TryNextFetch(out _, out int next));
        poll.FetchFailed(key, next, SteamAvatarPoll.SeatedRefresh);

        Assert.Equal(SteamAvatarPhase.Ready, poll.PhaseOf(key));
        Assert.Equal(5, poll.HandleOf(key));
    }

    [Fact]
    public void An_avatar_nobody_asks_for_any_more_stops_being_polled_unless_its_player_is_seated()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex), TimeSpan.Zero);
        poll.Want(Key(Sam), TimeSpan.Zero);
        poll.SetSeated(new[] { Sam });

        TimeSpan late = SteamAvatarPoll.WantedFor + TimeSpan.FromSeconds(1);
        steam.HandleCalls = 0;
        Assert.Equal(1, poll.Poll(late));
        Assert.Equal(1, steam.HandleCalls);

        poll.Want(Key(Alex), late);
        Assert.Equal(2, poll.Poll(late + SteamAvatarPoll.HandlePollInterval)); // wanted again: Alex is polled again, with Sam
    }

    [Fact]
    public void The_thirty_third_avatar_drops_the_one_wanted_longest_ago_and_a_seated_player_goes_last()
    {
        var steam = new FakeSteam();
        var poll = new SteamAvatarPoll(steam);
        for (int i = 0; i < SteamAvatarPoll.MaxEntries; i++)
        {
            poll.Want(Key(1000 + (ulong)i), TimeSpan.FromSeconds(i));
        }

        poll.SetSeated(new[] { 1000UL }); // the oldest, but seated
        Assert.False(poll.TryTakeEvicted(out _));

        poll.Want(Key(5000), TimeSpan.FromSeconds(100));

        Assert.Equal(SteamAvatarPoll.MaxEntries, poll.Count);
        Assert.True(poll.TryTakeEvicted(out SteamAvatarKey gone));
        Assert.Equal(Key(1001), gone);
        Assert.False(poll.TryTakeEvicted(out _));
        Assert.NotNull(poll.PhaseOf(Key(1000)));
        Assert.Null(poll.PhaseOf(Key(1001)));
    }

    [Fact]
    public void Wanting_an_avatar_again_keeps_it_from_being_the_oldest()
    {
        var poll = new SteamAvatarPoll(new FakeSteam());
        for (int i = 0; i < SteamAvatarPoll.MaxEntries; i++)
        {
            poll.Want(Key(1000 + (ulong)i), TimeSpan.FromSeconds(i));
        }

        poll.Want(Key(1000), TimeSpan.FromSeconds(50));
        poll.Want(Key(9000), TimeSpan.FromSeconds(51));

        Assert.True(poll.TryTakeEvicted(out SteamAvatarKey gone));
        Assert.Equal(Key(1001), gone);
    }

    [Fact]
    public void The_sizes_of_one_player_are_separate_avatars()
    {
        var steam = new FakeSteam();
        steam.Handles[(Alex, SteamAvatarSize.Large)] = 1;
        steam.Handles[(Alex, SteamAvatarSize.Medium)] = 2;
        var poll = new SteamAvatarPoll(steam);
        poll.Want(Key(Alex, SteamAvatarSize.Large), TimeSpan.Zero);
        poll.Want(Key(Alex, SteamAvatarSize.Medium), TimeSpan.Zero);

        poll.Poll(TimeSpan.Zero);

        Assert.Equal(2, poll.Count);
        Assert.Equal(1, poll.HandleOf(Key(Alex, SteamAvatarSize.Large)));
        Assert.Equal(2, poll.HandleOf(Key(Alex, SteamAvatarSize.Medium)));
    }

    [Fact]
    public void Reports_for_an_avatar_that_was_dropped_are_ignored_and_clear_forgets_everything()
    {
        var poll = new SteamAvatarPoll(new FakeSteam());
        poll.Fetched(Key(Alex), 5, TimeSpan.Zero);
        poll.FetchFailed(Key(Alex), 5, TimeSpan.Zero);
        Assert.Null(poll.PhaseOf(Key(Alex)));
        Assert.Equal(0, poll.HandleOf(Key(Alex)));

        poll.Want(Key(Alex), TimeSpan.Zero);
        poll.SetSeated(null);
        poll.Clear();

        Assert.Equal(0, poll.Count);
        Assert.False(poll.TryTakeEvicted(out _));
    }
}
