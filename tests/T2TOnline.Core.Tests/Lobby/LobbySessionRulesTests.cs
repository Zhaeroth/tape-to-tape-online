using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Tests.Lobby;

public class LobbySessionRulesTests
{
    private static readonly LobbyTitleView Idle = new() { SceneLoaded = true, Up = true };

    [Theory]
    [InlineData(LobbyMemberStage.Returning, LobbyHostState.Open, false, true)] // dropped in a game, back after the host went home
    [InlineData(LobbyMemberStage.Playing, LobbyHostState.Open, false, true)] // the same, before their flow has read the board
    [InlineData(LobbyMemberStage.Returning, LobbyHostState.Returning, false, true)]
    [InlineData(LobbyMemberStage.Returning, LobbyHostState.Counting, false, true)]
    [InlineData(LobbyMemberStage.Returning, LobbyHostState.Open, true, false)] // on their way home with the link they had: nothing new
    [InlineData(LobbyMemberStage.InLobby, LobbyHostState.Open, false, false)] // home: nothing of the campaign's reaches them
    [InlineData(LobbyMemberStage.Preparing, LobbyHostState.Open, false, false)] // not following yet
    [InlineData(LobbyMemberStage.Playing, LobbyHostState.InGame, false, false)] // a game runs: their ack gives them the link
    [InlineData(LobbyMemberStage.Playing, LobbyHostState.Starting, false, false)]
    [InlineData(LobbyMemberStage.Playing, LobbyHostState.GameFlow, false, false)]
    public void A_friend_still_following_a_game_thats_over_gets_the_link_that_brings_them_home(LobbyMemberStage stage, LobbyHostState host, bool following, bool expected)
    {
        Assert.Equal(expected, LobbySignals.FollowsAfterGame(stage, host, following));
    }

    [Theory]
    [InlineData(LobbyHostState.Open, false, true)] // no game is starting: a friend's stream drives nothing
    [InlineData(LobbyHostState.Counting, false, true)] // Start can still be cancelled
    [InlineData(LobbyHostState.Starting, false, false)] // the barrier: an honest friend's first frames may come now
    [InlineData(LobbyHostState.GameFlow, false, false)] // the controller screen wants their pad
    [InlineData(LobbyHostState.InGame, false, false)]
    [InlineData(LobbyHostState.Returning, false, false)]
    [InlineData(LobbyHostState.Idle, false, false)]
    [InlineData(LobbyHostState.Open, true, false)] // the one-PC test's friend presses in the lobby
    [InlineData(LobbyHostState.Counting, true, false)]
    public void A_friends_controller_is_held_only_while_the_lobby_is_up_and_no_game_is_starting(LobbyHostState host, bool onePcFriend, bool expected)
    {
        Assert.Equal(expected, LobbySignals.HoldsFriendInput(host, onePcFriend));
    }

    [Fact]
    public void Off_the_title_scene_says_the_scene_left()
    {
        Assert.Equal(LobbyEvent.SceneLeftTitle, LobbySignals.TitleEvent(default, LobbyHostState.Open));
        Assert.Equal(LobbyEvent.SceneLeftTitle, LobbySignals.TitleEvent(new LobbyTitleView { Up = true, Leaving = true }, LobbyHostState.GameFlow));
    }

    [Fact]
    public void A_scene_with_its_script_not_up_says_nothing()
    {
        Assert.Null(LobbySignals.TitleEvent(new LobbyTitleView { SceneLoaded = true }, LobbyHostState.Returning));
    }

    [Theory]
    [InlineData(LobbyHostState.Open)]
    [InlineData(LobbyHostState.Counting)]
    [InlineData(LobbyHostState.Returning)]
    public void Settings_or_a_refresh_over_the_title_is_never_a_start(LobbyHostState state)
    {
        // The first thing to check if friends follow at a Settings press: only a game's start raises TitleBusy outside a start.
        Assert.Null(LobbySignals.TitleEvent(Idle with { OtherMenuOver = true }, state));
        Assert.Null(LobbySignals.TitleEvent(Idle with { Refreshing = true }, state));
        Assert.Null(LobbySignals.TitleEvent(Idle with { Preparing = true }, state));
        Assert.Null(LobbySignals.TitleEvent(Idle with { Clicking = true }, state));
    }

    [Theory]
    [InlineData(LobbyHostState.Open)]
    [InlineData(LobbyHostState.GameFlow)]
    public void What_starts_a_game_is_busy(LobbyHostState state)
    {
        Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle with { Leaving = true }, state));
        Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle with { StartingRun = true }, state));
        Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle with { StartMenuOver = true }, state));
    }

    [Fact]
    public void During_a_start_any_menu_over_the_title_holds_off_the_back_out()
    {
        Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle with { OtherMenuOver = true }, LobbyHostState.GameFlow));
        Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle with { MenuOnTop = true }, LobbyHostState.GameFlow));
        Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle with { Refreshing = true }, LobbyHostState.GameFlow));
        Assert.Equal(LobbyEvent.TitleIdle, LobbySignals.TitleEvent(Idle, LobbyHostState.GameFlow));
    }

    [Theory]
    [InlineData("ControllerAssignment", true)]
    [InlineData("ControllerAssignment_Campaign", true)]
    [InlineData("Settings", false)]
    [InlineData("Glossary", false)]
    [InlineData("T2TOnlineLobby", false)]
    [InlineData("CustomizableYesNoPrompt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_controller_screen_is_a_start_menu(string? id, bool start)
    {
        Assert.Equal(start, LobbySignals.IsStartMenu(id));
    }

    [Fact]
    public void A_flow_given_what_the_title_says_goes_the_whole_way_with_no_friend()
    {
        // The session's tick, as the plugin runs it: only the title's event for each frame.
        var flow = new LobbyFlow();
        long now = 0;
        flow.Handle(LobbyEvent.HostPressed, now);
        flow.Handle(LobbyEvent.LobbyCreated, now);
        Raise(flow, Idle, ref now);
        Assert.Equal(LobbyHostState.Open, flow.Host);
        flow.Handle(LobbyEvent.StartPressed, now);
        Raise(flow, Idle, ref now);
        Assert.Equal(LobbyHostState.Counting, flow.Host);
        now += LobbyTiming.CountdownMicros;
        IReadOnlyList<LobbyEffect> effects = flow.Update(now);
        Assert.Equal(LobbyHostState.GameFlow, flow.Host);
        Assert.Contains(LobbyEffect.CallGameStart, effects);
        Raise(flow, Idle with { StartMenuOver = true }, ref now);
        Assert.Equal(LobbyHostState.GameFlow, flow.Host);
        Raise(flow, Idle with { Leaving = true }, ref now);
        Raise(flow, default, ref now);
        Assert.Equal(LobbyHostState.InGame, flow.Host);
        flow.Handle(LobbyEvent.TitleSceneLoaded, now);
        Assert.Equal(LobbyHostState.Returning, flow.Host);
        Raise(flow, Idle with { Preparing = true }, ref now);
        Assert.Equal(LobbyHostState.Returning, flow.Host);
        Raise(flow, Idle, ref now);
        Assert.Equal(LobbyHostState.Open, flow.Host);
        Assert.True(flow.BackFromGame);
    }

    [Fact]
    public void A_settings_press_in_the_lobby_is_no_implicit_start()
    {
        var flow = new LobbyFlow();
        flow.Handle(LobbyEvent.HostPressed, 0);
        flow.Handle(LobbyEvent.LobbyCreated, 0);
        long now = 0;
        Raise(flow, Idle with { OtherMenuOver = true }, ref now);
        Assert.Equal(LobbyHostState.Open, flow.Host);
        Assert.Equal(0u, flow.Epoch);
    }

    [Fact]
    public void A_backed_out_start_reopens_after_a_second_of_idle()
    {
        var flow = new LobbyFlow();
        flow.Handle(LobbyEvent.HostPressed, 0);
        flow.Handle(LobbyEvent.LobbyCreated, 0);
        flow.Handle(LobbyEvent.StartPressed, 0);
        long now = LobbyTiming.CountdownMicros;
        flow.Update(now);
        Assert.Equal(LobbyHostState.GameFlow, flow.Host);
        Raise(flow, Idle with { StartingRun = true }, ref now);
        Raise(flow, Idle, ref now);
        Assert.Equal(LobbyHostState.GameFlow, flow.Host);
        now += LobbyTiming.BackOutDebounceMicros;
        Raise(flow, Idle, ref now);
        Assert.Equal(LobbyHostState.Open, flow.Host);
    }

    [Fact]
    public void Home_is_the_own_title_with_everything_of_the_hosts_gone()
    {
        var home = new OwnTitleFacts { Following = true, TargetIsTitle = true, ShowingTitle = true, OnTitleScene = true };
        Assert.True(LobbySignals.OwnTitleBack(home));
        Assert.False(LobbySignals.OwnTitleBack(home with { Following = false }));
        Assert.False(LobbySignals.OwnTitleBack(home with { TargetIsTitle = false }));
        Assert.False(LobbySignals.OwnTitleBack(home with { ShowingTitle = false }));
        Assert.False(LobbySignals.OwnTitleBack(home with { OnTitleScene = false }));
        Assert.False(LobbySignals.OwnTitleBack(home with { SceneLoading = true }));
        Assert.False(LobbySignals.OwnTitleBack(home with { MatchBusy = true }));
    }

    [Fact]
    public void A_run_held_is_no_reason_to_stay_away_once_the_follower_has_arrived()
    {
        // A host backing out of an old squad menu: its title view changed nothing, so the follower never put the run-start preview away, and only the play period's end does.
        // Asking about the run kept a locked friend from home until the 60 s watchdog.
        Assert.DoesNotContain(typeof(OwnTitleFacts).GetProperties(), p => p.Name.Contains("Run", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Start_waits_for_the_titles_warm_ups()
    {
        // HostMenus.Showing drops a controller screen opened during them, as a warm-up: friends would see nothing until the match.
        Assert.True(LobbySignals.StartCanRun(Idle, runFileReady: true));
        Assert.False(LobbySignals.StartCanRun(Idle with { Preparing = true }, runFileReady: true));
        Assert.False(LobbySignals.StartCanRun(Idle, runFileReady: false));
        Assert.False(LobbySignals.StartCanRun(new LobbyTitleView { SceneLoaded = true }, runFileReady: true));
    }

    [Fact]
    public void The_games_start_still_due_is_no_back_out()
    {
        var flow = new LobbyFlow();
        flow.Handle(LobbyEvent.HostPressed, 0);
        flow.Handle(LobbyEvent.LobbyCreated, 0);
        flow.Handle(LobbyEvent.StartPressed, 0);
        long now = LobbyTiming.CountdownMicros;
        flow.Update(now);
        Assert.Equal(LobbyHostState.GameFlow, flow.Host);

        // The title idle, and its run file not ready yet: the call waits, and the flow with it.
        for (int i = 0; i < 30; i++)
        {
            now += 100_000;
            flow.Update(now);
            Assert.Equal(LobbyEvent.TitleBusy, LobbySignals.TitleEvent(Idle, flow.Host, gameStartDue: true));
            flow.Handle(LobbyEvent.TitleBusy, now);
        }

        Assert.Equal(LobbyHostState.GameFlow, flow.Host);
        Assert.Equal(LobbyEvent.TitleIdle, LobbySignals.TitleEvent(Idle, flow.Host, gameStartDue: false));
    }

    [Fact]
    public void A_join_at_an_idle_title_goes_at_once()
    {
        Assert.Equal(SteamJoinVerdict.JoinNow, LobbySignals.JoinVerdict(new SteamJoinFacts { AtIdleTitle = true }));
    }

    [Fact]
    public void A_join_during_an_own_run_or_menu_is_held_never_refused()
    {
        Assert.Equal(SteamJoinVerdict.Hold, LobbySignals.JoinVerdict(new SteamJoinFacts { AtIdleTitle = false }));
    }

    [Fact]
    public void In_a_lobby_it_asks_and_in_a_game_it_holds()
    {
        Assert.Equal(SteamJoinVerdict.AskToSwitch, LobbySignals.JoinVerdict(new SteamJoinFacts { InFriendsLobby = true }));
        Assert.Equal(SteamJoinVerdict.Hold, LobbySignals.JoinVerdict(new SteamJoinFacts { InFriendsLobby = true, GameUnderWay = true }));
        Assert.Equal(SteamJoinVerdict.AskToSwitch, LobbySignals.JoinVerdict(new SteamJoinFacts { Hosting = true }));
        Assert.Equal(SteamJoinVerdict.Hold, LobbySignals.JoinVerdict(new SteamJoinFacts { Hosting = true, GameUnderWay = true }));
    }

    [Fact]
    public void Hosting_friends_is_never_left_for_an_invite()
    {
        Assert.Equal(SteamJoinVerdict.RefuseHosting, LobbySignals.JoinVerdict(new SteamJoinFacts { Hosting = true, FriendsSeated = 1 }));
        Assert.Equal(SteamJoinVerdict.RefuseHosting, LobbySignals.JoinVerdict(new SteamJoinFacts { Hosting = true, FriendsSeated = 1, GameUnderWay = true }));
    }

    [Fact]
    public void The_same_lobby_is_nothing_to_do()
    {
        Assert.Equal(SteamJoinVerdict.AlreadyThere, LobbySignals.JoinVerdict(new SteamJoinFacts { SameLobby = true, InFriendsLobby = true }));
        Assert.Equal(SteamJoinVerdict.AlreadyThere, LobbySignals.JoinVerdict(new SteamJoinFacts { SameLobby = true, Hosting = true, FriendsSeated = 2 }));
    }

    [Theory]
    [InlineData(4, 2, LobbyResultKind.MatchWon)]
    [InlineData(1, 3, LobbyResultKind.MatchLost)]
    [InlineData(2, 2, LobbyResultKind.MatchTied)]
    [InlineData(4, 2, LobbyResultKind.MatchScore, true)]
    [InlineData(2, 2, LobbyResultKind.MatchScore, true)]
    public void A_match_that_ended_shows_its_score(int ours, int theirs, LobbyResultKind kind, bool versus = false)
    {
        LobbyResult result = LobbyResults.Of(new LobbyGameFacts { Mode = LobbyMode.QuickGame, MatchEnded = true, Versus = versus, OurScore = ours, TheirScore = theirs });
        Assert.Equal(new LobbyResult(kind, (byte)ours, (byte)theirs), result);
    }

    [Fact]
    public void A_match_quit_mid_way_or_never_read_shows_no_ribbon()
    {
        Assert.Equal(LobbyResult.None, LobbyResults.Of(new LobbyGameFacts { Mode = LobbyMode.QuickGame, OurScore = 1, TheirScore = 0 }));
        Assert.Equal(LobbyResult.None, LobbyResults.Of(new LobbyGameFacts { Mode = LobbyMode.QuickGame, MatchEnded = true, OurScore = -1, TheirScore = -1 }));
    }

    [Fact]
    public void A_run_says_how_it_ended_with_its_act_and_floor()
    {
        var run = new LobbyGameFacts { Mode = LobbyMode.Campaign, RunSeen = true, Act = 2, Floor = 5 };
        Assert.Equal(new LobbyResult(LobbyResultKind.RunWon, 2, 5), LobbyResults.Of(run with { RunWon = true }));
        Assert.Equal(new LobbyResult(LobbyResultKind.RunOver, 2, 5), LobbyResults.Of(run with { RunFailed = true }));
        Assert.Equal(new LobbyResult(LobbyResultKind.RunOver, 2, 5), LobbyResults.Of(run));
        Assert.Equal(new LobbyResult(LobbyResultKind.RunSaved, 2, 5), LobbyResults.Of(run with { RunStillInSlot = true }));
        Assert.Equal(LobbyResult.None, LobbyResults.Of(new LobbyGameFacts { Mode = LobbyMode.Campaign }));
    }

    [Fact]
    public void A_run_ended_keeps_its_act_and_floor_over_the_blank_run_the_game_puts_in_its_place()
    {
        // A run conceded in act 2 at floor 2, then the game's run-end reset (act 0, floor 0) read before the title loaded.
        var facts = new LobbyGameFacts { Mode = LobbyMode.Campaign };
        facts = LobbyResults.WithRun(facts, 2, 2, failed: false);
        facts = LobbyResults.WithRun(facts with { RunFailed = true }, 0, 0, failed: false);
        Assert.Equal(new LobbyResult(LobbyResultKind.RunOver, 2, 2), LobbyResults.Of(facts));
        Assert.Equal(new LobbyResult(LobbyResultKind.RunWon, 3, 7), LobbyResults.Of(LobbyResults.WithRun(LobbyResults.WithRun(new LobbyGameFacts { Mode = LobbyMode.Campaign, RunWon = true }, 3, 7, false), 0, 0, false)));
    }

    [Fact]
    public void A_run_not_on_the_map_yet_is_no_run_and_a_failed_mark_sticks()
    {
        var facts = LobbyResults.WithRun(new LobbyGameFacts { Mode = LobbyMode.Campaign }, 0, 0, failed: true);
        Assert.False(facts.RunSeen);
        Assert.False(facts.RunFailed);
        Assert.Equal(LobbyResult.None, LobbyResults.Of(facts));

        facts = LobbyResults.WithRun(facts, 1, 3, failed: true);
        facts = LobbyResults.WithRun(facts, 1, 4, failed: false);
        Assert.Equal(new LobbyGameFacts { Mode = LobbyMode.Campaign, RunSeen = true, Act = 1, Floor = 4, RunFailed = true }, facts);
    }

    [Fact]
    public void Numbers_past_a_byte_are_the_most_a_byte_says()
    {
        Assert.Equal(255, LobbySignals.Clamp(300));
        Assert.Equal(0, LobbySignals.Clamp(-4));
        Assert.Equal(7, LobbySignals.Clamp(7));
    }

    [Fact]
    public void People_show_once_each_in_their_most_present_stage()
    {
        const ulong self = 1, sam = 2, riley = 3, casey = 4, alex = 5;
        IReadOnlyList<LobbyPerson> people = LobbyPeople.Of(
            new[] { new LobbySeated { SteamId = sam, Name = "Sam", HasMember = true, Member = new LobbyMemberMessage(true, LobbyMemberStage.InLobby, 0), PingMs = 40 } },
            new[] { (riley, "Riley", 27), (sam, "Sam again", 27) },
            new[] { self, sam, riley, casey },
            new[] { (alex, "Alex"), (sam, "Sam"), (casey, "Casey") },
            self,
            LobbyPhase.Open,
            _ => "Casey");

        Assert.Equal(new[] { sam, riley, casey, alex }, people.Select(p => p.SteamId).ToArray());
        Assert.Equal(new[] { SeatStage.Here, SeatStage.Reconnecting, SeatStage.Joining, SeatStage.Invited }, people.Select(p => p.Stage).ToArray());
        Assert.True(people[0].Ready);
        Assert.Equal(27, people[1].ReconnectSeconds);
        Assert.Equal("Casey", people[2].Name);
    }

    [Fact]
    public void Someone_refused_who_stays_in_the_steam_lobby_isnt_arriving()
    {
        // A friend removed, or a stranger refused, whose game doesn't leave the Steam lobby: no seat on the board, and no Wait at Start.
        const ulong self = 1, sam = 2, riley = 3;
        IReadOnlyList<LobbyPerson> people = LobbyPeople.Of(
            System.Array.Empty<LobbySeated>(),
            System.Array.Empty<(ulong, string, int)>(),
            new[] { self, sam, riley },
            System.Array.Empty<(ulong, string)>(),
            self,
            LobbyPhase.Open,
            _ => "Name",
            id => id == sam);

        Assert.Equal(riley, Assert.Single(people).SteamId);
    }

    [Fact]
    public void A_friend_with_no_member_message_yet_is_here_and_not_ready()
    {
        IReadOnlyList<LobbyPerson> people = LobbyPeople.Of(
            new[] { new LobbySeated { SteamId = 2, Name = "Sam" } },
            System.Array.Empty<(ulong, string, int)>(),
            System.Array.Empty<ulong>(),
            System.Array.Empty<(ulong, string)>(),
            1,
            LobbyPhase.Open,
            _ => string.Empty);

        Assert.Equal(SeatStage.Here, Assert.Single(people).Stage);
        Assert.False(people[0].Ready);
    }

    [Fact]
    public void A_friend_coming_home_shows_loading_until_the_lobby_has_them()
    {
        var member = new LobbyMemberMessage(false, LobbyMemberStage.Returning, 3);
        IReadOnlyList<LobbyPerson> people = LobbyPeople.Of(
            new[] { new LobbySeated { SteamId = 2, Name = "Sam", HasMember = true, Member = member } },
            System.Array.Empty<(ulong, string, int)>(),
            System.Array.Empty<ulong>(),
            System.Array.Empty<(ulong, string)>(),
            1,
            LobbyPhase.Open,
            _ => string.Empty);

        Assert.Equal(SeatStage.Loading, Assert.Single(people).Stage);
    }

    [Fact]
    public void Start_waits_for_the_first_not_here_and_its_lines_name_them_by_tag_never_by_the_drawn_name()
    {
        const ulong host = 76561199000000001, riley = 76561199000000002, sam = 76561199000000003;
        const string steamName = "Riley \U0001F525";
        var board = new LobbyBoardMessage
        {
            Phase = LobbyPhase.Open,
            Capacity = 4,
            Seats = new[]
            {
                new LobbyBoardSeat(0, host, "Alex", true, SeatStage.Here, false, InputKind.Gamepad, 0, 0, -1, 0, 0),
                new LobbyBoardSeat(1, riley, steamName, false, SeatStage.Loading, false, InputKind.Gamepad, 0, 1, 40, 3, 0),
                new LobbyBoardSeat(2, sam, "Sam", false, SeatStage.Reconnecting, false, InputKind.KeyboardMouse, 0, 2, -1, 0, 20),
            },
        };

        // The lobby draws the name without what its font lacks: a form of it the log's backstop doesn't know.
        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host, Self = host, NameOf = (_, _) => steamName, CanDraw = c => c < 0x10000 });
        LobbySeat? first = LobbyPeople.FirstNotHere(state);
        Assert.NotNull(first);
        Assert.Equal(riley, first.SteamId);
        Assert.Equal("Riley", first.Name);

        string tag = "P#a1b2c3"; // the caller's tag for first.SteamId
        foreach (string line in new[] { LobbyMeter.StartWaits(tag), LobbyMeter.StartedWithout(tag, 3_000_000, wentOnAnyway: false), LobbyMeter.StartedWithout(tag, 0, wentOnAnyway: true) })
        {
            Assert.Contains(tag, line);
            Assert.DoesNotContain("Riley", line);
        }

        // Everyone here: nobody to wait for.
        Assert.Null(LobbyPeople.FirstNotHere(LobbyState.From(board with { Seats = new[] { board.Seats[0] } }, new LobbyViewInputs { Role = LobbyRole.Host, Self = host })));
    }

    private static void Raise(LobbyFlow flow, in LobbyTitleView view, ref long now)
    {
        now += 100_000;
        flow.Update(now);
        if (LobbySignals.TitleEvent(view, flow.Host) is { } e)
        {
            flow.Handle(e, now);
        }
    }
}
