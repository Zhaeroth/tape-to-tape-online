using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// Run sync's save safety (D41, D46), scenario by scenario: when the game may save or delete its run, and what the save
/// guard does. The case of a run continued while the title screen still loaded the
/// run from before a switch is covered here too.
/// </summary>
public class RunSaveSafetyTests
{
    private static readonly TitleView IdleTitle = new(Loaded: true, Up: true, StartingRun: false, Idle: true);
    private static readonly TitleView BusyTitle = new(Loaded: true, Up: true, StartingRun: false, Idle: false);
    private static readonly TitleView StartingTitle = new(Loaded: true, Up: true, StartingRun: true, Idle: false);
    private static readonly TitleView NoTitle = new(Loaded: false, Up: false, StartingRun: false, Idle: false);

    /// <summary>
    /// Run sync's refresh of an idle title screen (at <paramref name="now"/>, or once the run was marked), then the title idle
    /// past the floor: the run is current again (if nothing else holds it). Before the floor it isn't.
    /// </summary>
    private static StaleChange? Settle(RunSaveSafety safety, double now)
    {
        Assert.True(safety.RefreshWanted);
        safety.TitleRefreshed();
        double from = Math.Max(now, safety.StaleSince);
        if (from - safety.StaleSince < RunSaveSafety.StaleFloorSeconds)
        {
            Assert.Null(safety.WatchStale(IdleTitle, hostLoadQueued: false, from));
        }

        return safety.WatchStale(IdleTitle, hostLoadQueued: false, safety.StaleSince + RunSaveSafety.StaleFloorSeconds);
    }

    [Fact]
    public void A_pc_that_never_had_a_session_saves_its_run_as_the_game_does()
    {
        var safety = new RunSaveSafety();

        Assert.Null(safety.RunSaveBlock);
        Assert.False(safety.MayHoldAnotherRun);
        Assert.Equal(RunSaveSafety.SoloSlot, safety.WantedSlot);
        Assert.Equal(RunSaveSafety.SoloSlot, safety.ActiveSlot);
        Assert.False(safety.HostOnCoopSlot);
        Assert.False(safety.RefreshWanted);
        Assert.Null(safety.WatchStale(IdleTitle, hostLoadQueued: false, 100));
        Assert.Null(safety.OtherSceneLoaded("world_map", 100));
        Assert.False(safety.TitleSceneLoaded());
    }

    [Fact]
    public void A_host_switches_to_its_co_op_slot_and_saves_once_the_title_screen_reloaded_it()
    {
        var safety = new RunSaveSafety();
        safety.StartHosting(2);
        Assert.Equal(2, safety.WantedSlot);
        Assert.False(safety.HostOnCoopSlot); // the solo run is still the run file: not shared
        Assert.Null(safety.RunSaveBlock); // ... and still saved, as the solo run

        safety.Switched(2, titleLoading: false, now: 10);
        Assert.Equal(2, safety.ActiveSlot);
        Assert.True(safety.HostOnCoopSlot);
        Assert.Contains("co-op slot 2", safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.True(safety.MayHoldAnotherRun);

        StaleChange? change = Settle(safety, 10);
        Assert.True(change is { NowCurrent: true });
        Assert.Contains("reloaded", change!.Value.How, StringComparison.Ordinal);
        Assert.Contains("co-op slot 2", change.Value.Why, StringComparison.Ordinal);
        Assert.Null(safety.RunSaveBlock);
        Assert.False(safety.MayHoldAnotherRun);
    }

    [Fact]
    public void The_title_screen_must_be_idle_and_refreshed_and_past_the_floor()
    {
        var safety = new RunSaveSafety();
        safety.StartHosting(1);
        safety.Switched(1, titleLoading: false, now: 0);

        Assert.Null(safety.WatchStale(IdleTitle, hostLoadQueued: false, 5)); // not refreshed yet
        safety.TitleRefreshed();
        Assert.Null(safety.WatchStale(BusyTitle, hostLoadQueued: false, 5)); // a menu over it
        Assert.Null(safety.WatchStale(new TitleView(true, false, false, false), hostLoadQueued: false, 5)); // not up
        Assert.Null(safety.WatchStale(IdleTitle, hostLoadQueued: true, 5)); // a host's run queued
        Assert.True(safety.WatchStale(IdleTitle, hostLoadQueued: false, 5) is { NowCurrent: true });
    }

    [Fact]
    public void A_run_started_from_the_title_after_its_refresh_is_the_run_files()
    {
        var safety = new RunSaveSafety();
        safety.StartHosting(1);
        safety.Switched(1, titleLoading: false, now: 0);
        safety.TitleRefreshed();

        StaleChange? change = safety.WatchStale(StartingTitle, hostLoadQueued: false, 0.1);

        Assert.True(change is { NowCurrent: true });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void Leaving_the_title_screen_for_a_run_makes_it_current_before_the_maps_first_save()
    {
        var safety = new RunSaveSafety();
        safety.StartHosting(1);
        safety.Switched(1, titleLoading: false, now: 0);
        safety.TitleRefreshed();

        StaleChange? change = safety.OtherSceneLoaded("world_map", 0.2);

        Assert.True(change is { NowCurrent: true });
        Assert.Contains("world_map", change!.Value.How, StringComparison.Ordinal);
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void Continue_while_the_title_still_loads_the_run_from_before_a_switch_keeps_run_saves_held_until_the_title()
    {
        // The title screen's own refresh (OnEnable) loads the solo run; the switch to the co-op slot happens after it
        // started, and Continue awaits that same load: the solo run would be saved into the co-op slot.
        var safety = new RunSaveSafety();
        safety.StartHosting(1);
        Assert.False(safety.TitleSceneLoaded());
        safety.Switched(1, titleLoading: true, now: 0);
        Assert.True(safety.LoadBeforeSwitch);

        StaleChange? change = safety.OtherSceneLoaded("world_map", 0.3);

        Assert.True(change is { NowCurrent: false });
        Assert.Equal(RunSaveSafety.StartedBeforeReload, change!.Value.Why);
        Assert.True(safety.StaleUntilTitle);
        Assert.NotNull(safety.RunSaveBlock);
        Assert.Null(safety.OtherSceneLoaded("standard_game", 5)); // still held, whatever loads next
        Assert.Null(safety.WatchStale(NoTitle, hostLoadQueued: false, 6));

        // Back at the title: its own refresh loads the co-op slot now; run sync refreshes it once idle, and then it's current.
        Assert.False(safety.TitleSceneLoaded());
        Assert.False(safety.LoadBeforeSwitch);
        Assert.NotNull(safety.RunSaveBlock);
        Assert.True(Settle(safety, 20) is { NowCurrent: true });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void A_run_starting_or_the_title_left_before_the_refresh_after_a_switch_mid_load_is_held_too()
    {
        var starting = new RunSaveSafety();
        starting.StartHosting(1);
        starting.Switched(1, titleLoading: true, now: 0);
        Assert.True(starting.WatchStale(StartingTitle, hostLoadQueued: false, 0.1) is { NowCurrent: false });
        Assert.True(starting.StaleUntilTitle);

        var left = new RunSaveSafety();
        left.StartHosting(1);
        left.Switched(1, titleLoading: true, now: 0);
        Assert.True(left.WatchStale(NoTitle, hostLoadQueued: false, 0.1) is { NowCurrent: false });
        Assert.NotNull(left.RunSaveBlock);
    }

    [Fact]
    public void After_run_syncs_own_refresh_a_switch_mid_load_no_longer_holds_a_continued_run()
    {
        var safety = new RunSaveSafety();
        safety.StartHosting(1);
        safety.Switched(1, titleLoading: true, now: 0);
        safety.TitleRefreshed(); // the title's own load had finished (it was idle), and the refresh loads the co-op slot

        Assert.False(safety.LoadBeforeSwitch);
        Assert.True(safety.OtherSceneLoaded("world_map", 0.5) is { NowCurrent: true });
    }

    [Fact]
    public void A_host_that_stops_mid_co_op_run_keeps_its_slot_until_the_title_then_gets_its_solo_run_back()
    {
        var safety = new RunSaveSafety();
        safety.StartHosting(3);
        safety.Switched(3, titleLoading: false, now: 0);
        Settle(safety, 0);

        safety.StopHosting();
        Assert.False(safety.HostOnCoopSlot);
        Assert.Equal(RunSaveSafety.SoloSlot, safety.WantedSlot);
        Assert.Equal(3, safety.ActiveSlot);
        Assert.Null(safety.RunSaveBlock); // the co-op run keeps saving to its slot until the title screen

        safety.Switched(RunSaveSafety.SoloSlot, titleLoading: false, now: 50);
        Assert.Contains("your solo run", safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.True(Settle(safety, 50) is { NowCurrent: true });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void The_co_op_slot_changes_only_at_an_idle_title_while_hosting_or_while_a_co_op_run_is_the_run_file()
    {
        var safety = new RunSaveSafety();
        Assert.False(safety.CoopSlotLocked(idleTitle: false, out string problem));
        Assert.Equal(string.Empty, problem);

        safety.StartHosting(1);
        Assert.True(safety.CoopSlotLocked(idleTitle: false, out problem));
        Assert.Contains("hosting", problem, StringComparison.Ordinal);
        Assert.False(safety.CoopSlotLocked(idleTitle: true, out _));

        safety.Switched(1, titleLoading: false, now: 0);
        safety.StopHosting();
        Assert.True(safety.CoopSlotLocked(idleTitle: false, out problem));
        Assert.Contains("slot 1", problem, StringComparison.Ordinal);

        safety.CoopSlotChosen(2);
        Assert.Equal(2, safety.HostSlot);
    }

    [Fact]
    public void A_friend_following_never_writes_its_run_file()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);

        Assert.True(safety.MayLoadHostRun);
        Assert.True(safety.InFriendSession);
        Assert.Equal("a friend's run file is never written", safety.RunSaveBlock);
        Assert.True(safety.MayHoldAnotherRun);

        safety.HostRunPointed();
        Assert.True(safety.HoldingHostRun);
        Assert.Equal(RunSaveSafety.HostRunSlot, safety.ActiveSlot);
        Assert.Equal(RunSaveSafety.HostRunSlot, safety.WantedSlot); // following: run sync never switches the file itself
        Assert.Null(safety.OtherSceneLoaded("world_map", 1));
        Assert.Null(safety.WatchStale(IdleTitle, hostLoadQueued: false, 100));
        Assert.Equal("a friend's run file is never written", safety.RunSaveBlock);
    }

    [Fact]
    public void Run_saves_are_skipped_from_a_sessions_start_before_the_guard_is_asked()
    {
        var safety = new RunSaveSafety();
        safety.GuardHeld(true); // not following: means nothing
        Assert.False(safety.GuardHolds);

        safety.StartFollowing(guardHolds: false);
        Assert.NotNull(safety.RunSaveBlock);
        Assert.False(safety.MayLoadHostRun);
        safety.GuardHeld(true);
        Assert.True(safety.MayLoadHostRun);
    }

    [Fact]
    public void A_friend_whose_save_guard_cant_hold_loads_nothing_and_still_skips_run_saves()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: false);

        Assert.False(safety.MayLoadHostRun);
        Assert.NotNull(safety.RunSaveBlock);
        Assert.False(safety.PutHostRunAway(atSettledTitle: true, now: 1)); // nothing was loaded
        Assert.False(safety.StopFollowing(gameClosing: false));
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void A_friend_whose_session_ends_on_the_title_gets_its_run_back_once_the_title_reloaded_it()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();

        Assert.True(safety.PutHostRunAway(atSettledTitle: true, now: 30));
        safety.PointedBackToSolo(titleLoading: false);
        Assert.False(safety.HoldingHostRun);
        Assert.Equal(RunSaveSafety.SoloSlot, safety.ActiveSlot);
        Assert.False(safety.StaleUntilTitle);
        Assert.False(safety.StopFollowing(gameClosing: false)); // the guard isn't kept: the title is here
        Assert.Contains("put away", safety.RunSaveBlock, StringComparison.Ordinal);

        Assert.True(Settle(safety, 30) is { NowCurrent: true });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void A_friend_whose_session_ends_on_the_hosts_map_stays_held_and_guarded_until_its_title_loads()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();

        Assert.True(safety.PutHostRunAway(atSettledTitle: false, now: 30));
        safety.PointedBackToSolo(titleLoading: false);
        Assert.True(safety.StopFollowing(gameClosing: false));
        safety.GuardKeptUntilTitle(true);
        Assert.True(safety.GuardUntilTitle);

        // A world-map menu closing, or quitting the map, saves the run: still skipped.
        Assert.Null(safety.OtherSceneLoaded("world_map", 31));
        Assert.Null(safety.WatchStale(NoTitle, hostLoadQueued: false, 32));
        Assert.Contains("title screen has loaded", safety.RunSaveBlock, StringComparison.Ordinal);

        Assert.True(safety.TitleSceneLoaded()); // the guard's hold ends here
        Assert.False(safety.GuardUntilTitle);
        Assert.NotNull(safety.RunSaveBlock);
        Assert.True(Settle(safety, 40) is { NowCurrent: true });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void A_friend_game_closing_doesnt_keep_the_guard()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();
        safety.PutHostRunAway(atSettledTitle: false, now: 1);

        Assert.False(safety.StopFollowing(gameClosing: true));
        Assert.NotNull(safety.RunSaveBlock); // nothing saves the host's run over the friend's before the game closes
    }

    [Fact]
    public void A_friend_who_rejoins_before_its_title_loads_takes_the_new_sessions_hold()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();
        safety.PutHostRunAway(atSettledTitle: false, now: 1);
        safety.PointedBackToSolo(titleLoading: false);
        Assert.True(safety.StopFollowing(gameClosing: false));
        safety.GuardKeptUntilTitle(true);

        safety.StartFollowing(guardHolds: true); // a drop, then the rejoin
        Assert.False(safety.GuardUntilTitle);
        Assert.False(safety.TitleSceneLoaded()); // following: the session's own hold stays, nothing to release
        Assert.Equal("a friend's run file is never written", safety.RunSaveBlock);

        // The second session ends at a settled title.
        safety.HostRunPointed();
        Assert.True(safety.PutHostRunAway(atSettledTitle: true, now: 60));
        safety.PointedBackToSolo(titleLoading: false);
        Assert.False(safety.StopFollowing(gameClosing: false));
        Assert.True(Settle(safety, 60) is { NowCurrent: true });
    }

    [Fact]
    public void A_title_still_loading_the_hosts_run_when_it_is_put_away_holds_a_continue_before_the_refresh()
    {
        // The friend's title screen was loading the host's run (its run file then) when the session ended there.
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();
        safety.PutHostRunAway(atSettledTitle: true, now: 10);
        safety.PointedBackToSolo(titleLoading: true);
        Assert.False(safety.StopFollowing(gameClosing: false));

        Assert.True(safety.OtherSceneLoaded("world_map", 10.5) is { NowCurrent: false });
        Assert.NotNull(safety.RunSaveBlock);
    }

    [Fact]
    public void A_load_of_the_hosts_run_given_up_on_holds_run_saves_until_it_lands_then_the_title_reloads()
    {
        // The session ends (or the load times out) while the game's loader still runs: it ends by putting the host's run in memory.
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();
        safety.LoadOrphaned();
        safety.PutHostRunAway(atSettledTitle: true, now: 5);
        safety.PointedBackToSolo(titleLoading: false);
        safety.StopFollowing(gameClosing: false);

        Assert.Contains("still running", safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.False(safety.MaySwitchRunFile(hostLoadRunning: false));
        Assert.False(safety.WatchesTitle(hostLoadQueued: false));
        Assert.Null(safety.OtherSceneLoaded("world_map", 6));

        Assert.Equal(OrphanLanded.ResetAtTitle, safety.OrphanFinished(campaignThere: true, quietTitle: true, now: 20));
        Assert.False(safety.OrphanRunning);
        Assert.True(safety.MaySwitchRunFile(hostLoadRunning: false));
        Assert.Contains("finished after the session ended", safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.True(Settle(safety, 20) is { NowCurrent: true });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void A_given_up_load_landing_away_from_the_title_holds_run_saves_until_the_title_is_back()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();
        safety.LoadOrphaned();
        safety.PutHostRunAway(atSettledTitle: true, now: 5);
        safety.PointedBackToSolo(titleLoading: false);
        safety.StopFollowing(gameClosing: false);
        Assert.True(Settle(safety, 5) is null); // the orphan still runs: nothing changes

        Assert.Equal(OrphanLanded.HeldUntilTitle, safety.OrphanFinished(campaignThere: true, quietTitle: false, now: 30));
        Assert.True(safety.StaleUntilTitle);
        Assert.Null(safety.OtherSceneLoaded("world_map", 31));
        Assert.False(safety.TitleSceneLoaded());
        Assert.True(Settle(safety, 40) is { NowCurrent: true });
    }

    [Fact]
    public void A_given_up_load_landing_during_a_new_session_or_without_a_campaign_changes_nothing()
    {
        var following = new RunSaveSafety();
        following.StartFollowing(guardHolds: true);
        following.LoadOrphaned();
        Assert.Equal(OrphanLanded.Nothing, following.OrphanFinished(campaignThere: true, quietTitle: true, now: 1));
        Assert.Equal("a friend's run file is never written", following.RunSaveBlock);

        var noCampaign = new RunSaveSafety();
        noCampaign.LoadOrphaned();
        Assert.Equal(OrphanLanded.Nothing, noCampaign.OrphanFinished(campaignThere: false, quietTitle: true, now: 1));
        Assert.Null(noCampaign.RunSaveBlock);
    }

    [Fact]
    public void No_run_file_switch_while_a_load_of_the_hosts_run_runs()
    {
        var safety = new RunSaveSafety();
        Assert.True(safety.MaySwitchRunFile(hostLoadRunning: false));
        Assert.False(safety.MaySwitchRunFile(hostLoadRunning: true));
    }

    [Fact]
    public void Hosting_alone_never_holds_the_hosts_saves_once_its_slot_is_loaded()
    {
        // F6 (the one-PC test) only hosts: no campaign is followed on the host's PC.
        var safety = new RunSaveSafety();
        safety.StartHosting(1);
        safety.Switched(1, titleLoading: false, now: 0);
        Settle(safety, 0);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(safety.OtherSceneLoaded("world_map", 10 + i));
            Assert.False(safety.TitleSceneLoaded());
            Assert.Null(safety.RunSaveBlock);
        }
    }

    [Fact]
    public void Marking_stale_again_while_held_until_the_title_keeps_that_hold()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.HostRunPointed();
        safety.PutHostRunAway(atSettledTitle: false, now: 0);
        safety.PointedBackToSolo(titleLoading: false);
        safety.StopFollowing(gameClosing: true);

        safety.Switched(RunSaveSafety.SoloSlot, titleLoading: false, now: 1); // a later mark doesn't lift "until the title"
        Assert.True(safety.StaleUntilTitle);
    }

    [Theory]
    [InlineData(OwnRunPlace.Map, "title screen first")]
    [InlineData(OwnRunPlace.Match, "finish it")]
    public void A_join_waits_while_the_players_own_campaign_run_is_in_play(OwnRunPlace place, string advice)
    {
        // What the run did since its last save point is only in memory; the way to the title saves it.
        string? why = RunSaveSafety.JoinRefusal(place);
        Assert.NotNull(why);
        Assert.Contains(advice, why, StringComparison.Ordinal);
        Assert.Contains("the game saves your run on the way", why, StringComparison.Ordinal);
        Assert.Null(RunSaveSafety.JoinRefusal(OwnRunPlace.None));
    }

    [Fact]
    public void A_session_started_during_the_own_run_on_its_map_never_saves_that_run_until_the_title_reloaded_the_file()
    {
        // The backstop for that: the join raced a Continue, and the session started on the friend's own world map. The host
        // never shared a run here (nothing put away), and the session ends with the friend still on its own map.
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: false);
        safety.OwnRunLeftInPlay(now: 5);
        safety.GuardHeld(true);
        Assert.Contains("never written", safety.RunSaveBlock, StringComparison.Ordinal);

        Assert.False(safety.PutHostRunAway(atSettledTitle: false, now: 20)); // nothing of the host's was held here
        Assert.True(safety.StopFollowing(gameClosing: false)); // the guard stays on until the title loads
        safety.GuardKeptUntilTitle(true);
        Assert.Contains(RunSaveSafety.OwnRunInPlay, safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.Contains("title screen has loaded", safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.True(safety.MayHoldAnotherRun);

        // Quitting the map, or its match's end, saves the run: still skipped.
        Assert.Null(safety.OtherSceneLoaded("world_map", 21));
        Assert.Null(safety.WatchStale(NoTitle, hostLoadQueued: false, 22));
        Assert.NotNull(safety.RunSaveBlock);

        // The title loads (and its own refresh reads the run file): the run file's run is the one that comes back.
        Assert.True(safety.TitleSceneLoaded());
        Assert.NotNull(safety.RunSaveBlock);
        Assert.True(Settle(safety, 30) is { NowCurrent: true, Why: RunSaveSafety.OwnRunInPlay });
        Assert.Null(safety.RunSaveBlock);
    }

    [Fact]
    public void A_session_started_during_the_own_run_that_then_went_to_the_title_and_loaded_the_hosts_run_still_holds()
    {
        var safety = new RunSaveSafety();
        safety.StartFollowing(guardHolds: true);
        safety.OwnRunLeftInPlay(now: 1);
        Assert.False(safety.TitleSceneLoaded()); // the title follower loaded the title: "until the title" is met, the session still holds
        safety.HostRunPointed();

        Assert.True(safety.PutHostRunAway(atSettledTitle: false, now: 50)); // the session ends on the host's map
        safety.PointedBackToSolo(titleLoading: false);
        Assert.True(safety.StopFollowing(gameClosing: false));
        Assert.Contains("put away", safety.RunSaveBlock, StringComparison.Ordinal);
        Assert.True(safety.StaleUntilTitle);
    }
}
