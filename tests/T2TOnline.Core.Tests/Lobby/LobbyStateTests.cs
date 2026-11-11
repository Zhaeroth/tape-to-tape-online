using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Tests.Lobby;

public class LobbyStateTests
{
    private const ulong HostId = 100;
    private const ulong Sam = 200;
    private const ulong Casey = 300;
    private const ulong Riley = 400;

    internal static LobbyPerson Person(ulong id, string name, SeatStage stage = SeatStage.Here, bool ready = false, int ping = 58, double loss = 0, int reconnect = 0) => new()
    {
        SteamId = id,
        Name = name,
        Stage = stage,
        Ready = ready,
        DeviceKind = InputKind.Gamepad,
        DeviceFamily = 1,
        PingMs = ping,
        LossPercent = loss,
        ReconnectSeconds = reconnect,
    };

    internal static LobbyBoardMessage BoardWith(LobbyBoardBuilder builder, params LobbyPerson[] people) => builder.Build(new LobbyBoardInputs
    {
        Host = Person(HostId, "Alex"),
        People = people,
    });

    [Fact]
    public void The_host_has_seat_0_and_friends_take_the_lowest_free_seat_in_arrival_order()
    {
        var builder = new LobbyBoardBuilder(HostId);

        Assert.Equal(0, builder.SeatOf(HostId));
        Assert.Equal(1, builder.SeatOf(Sam));
        Assert.Equal(2, builder.SeatOf(Casey));
        Assert.Equal(1, builder.SeatOf(Sam)); // kept
        Assert.Equal(new[] { Sam, Casey }, builder.FriendsInSeatOrder());

        Assert.True(builder.Free(Sam));
        Assert.False(builder.Free(Sam));
        Assert.Equal(1, builder.SeatOf(Riley)); // the seat that opened
        Assert.Equal(new[] { Riley, Casey }, builder.FriendsInSeatOrder());
    }

    [Fact]
    public void A_full_lobby_has_no_seat_for_another()
    {
        var builder = new LobbyBoardBuilder(HostId, capacity: 2);
        Assert.Equal(1, builder.SeatOf(Sam));
        Assert.Equal(-1, builder.SeatOf(Casey));
        Assert.Equal(-1, builder.SeatOf(0));
    }

    [Fact]
    public void Capacity_is_clamped_to_what_the_board_can_say()
    {
        Assert.Equal(1, new LobbyBoardBuilder(HostId, 0).Capacity);
        Assert.Equal(LobbyState.MaxSeats, new LobbyBoardBuilder(HostId, 99).Capacity);
    }

    [Fact]
    public void The_board_has_the_host_first_and_people_on_their_seats()
    {
        var builder = new LobbyBoardBuilder(HostId);
        builder.SeatOf(Casey); // Casey came first

        LobbyBoardMessage board = BoardWith(builder, Person(Sam, "Sam"), Person(Casey, "Casey", ready: true));

        Assert.Equal(new[] { 0, 1, 2 }, board.Seats.Select(s => (int)s.Index));
        Assert.Equal(new[] { HostId, Casey, Sam }, board.Seats.Select(s => s.SteamId));
        Assert.True(board.Seats[0].IsHost);
        Assert.Equal(-1, board.Seats[0].PingMs); // nothing to measure
        Assert.Equal(0, board.Seats[0].Bars);
        Assert.True(board.Seats[1].Ready);
        Assert.Equal(58, board.Seats[1].PingMs);
        Assert.Equal(3, board.Seats[1].Bars);
    }

    [Fact]
    public void A_person_who_does_not_fit_is_left_out_of_a_full_board()
    {
        var builder = new LobbyBoardBuilder(HostId, capacity: 2);

        LobbyBoardMessage board = BoardWith(builder, Person(Sam, "Sam"), Person(Casey, "Casey"));

        Assert.Equal(new[] { HostId, Sam }, board.Seats.Select(s => s.SteamId));
    }

    [Fact]
    public void An_invited_ghost_shows_on_the_next_free_seat_and_moves_when_someone_arrives()
    {
        var builder = new LobbyBoardBuilder(HostId);

        LobbyBoardMessage first = BoardWith(builder, Person(Riley, "Riley", SeatStage.Invited));
        Assert.Equal((byte)1, first.Seats.Single(s => s.SteamId == Riley).Index);
        Assert.Equal(-1, builder.FriendsInSeatOrder().ToList().IndexOf(Riley)); // a ghost holds no seat

        LobbyBoardMessage second = BoardWith(builder, Person(Sam, "Sam"), Person(Riley, "Riley", SeatStage.Invited));
        Assert.Equal((byte)1, second.Seats.Single(s => s.SteamId == Sam).Index);
        Assert.Equal((byte)2, second.Seats.Single(s => s.SteamId == Riley).Index);
    }

    [Fact]
    public void An_invited_ghost_with_no_seat_free_is_left_out()
    {
        var builder = new LobbyBoardBuilder(HostId, capacity: 2);

        LobbyBoardMessage board = BoardWith(builder, Person(Sam, "Sam"), Person(Riley, "Riley", SeatStage.Invited));

        Assert.Equal(new[] { HostId, Sam }, board.Seats.Select(s => s.SteamId));
    }

    [Fact]
    public void Who_is_both_seated_and_invited_shows_once()
    {
        var builder = new LobbyBoardBuilder(HostId);

        LobbyBoardMessage board = BoardWith(builder, Person(Sam, "Sam"), Person(Sam, "Sam", SeatStage.Invited), Person(HostId, "Alex", SeatStage.Invited));

        Assert.Equal(new[] { HostId, Sam }, board.Seats.Select(s => s.SteamId));
    }

    [Fact]
    public void The_board_leaves_the_save_out_in_quick_game_and_the_countdown_out_of_any_other_phase()
    {
        var builder = new LobbyBoardBuilder(HostId);
        var save = new LobbySaveRow(2, true, "squad-a", 2, 5, 0);

        LobbyBoardMessage quick = builder.Build(new LobbyBoardInputs { Mode = LobbyMode.QuickGame, Save = save, CountdownMs = 2000, Host = Person(HostId, "Alex") });
        Assert.Equal(LobbySaveRow.None, quick.Save);
        Assert.Equal(0, quick.CountdownMs);

        LobbyBoardMessage counting = builder.Build(new LobbyBoardInputs { Mode = LobbyMode.Campaign, Save = save, Phase = LobbyPhase.Counting, CountdownMs = 2000, Host = Person(HostId, "Alex") });
        Assert.Equal(save, counting.Save);
        Assert.Equal(2000, counting.CountdownMs);
    }

    [Fact]
    public void Names_are_cleaned_and_numbers_clamped_on_the_board()
    {
        var builder = new LobbyBoardBuilder(HostId);

        LobbyBoardMessage board = BoardWith(builder, Person(Sam, "Sam\nthe\u202Efirst", ping: 99_999, reconnect: 1_000_000));

        LobbyBoardSeat seat = board.Seats.Single(s => s.SteamId == Sam);
        Assert.Equal("Sam?the?first", seat.Name);
        Assert.Equal(short.MaxValue, seat.PingMs);
        Assert.Equal(ushort.MaxValue, seat.ReconnectSeconds);
    }

    [Fact]
    public void Member_stages_read_as_seat_stages_by_where_the_lobby_is()
    {
        Assert.Equal(SeatStage.Here, LobbyPerson.StageOf(new LobbyMemberMessage(false, LobbyMemberStage.InLobby, 0), LobbyPhase.Open));
        Assert.Equal(SeatStage.Loading, LobbyPerson.StageOf(new LobbyMemberMessage(false, LobbyMemberStage.Preparing, 0), LobbyPhase.Starting));
        Assert.Equal(SeatStage.Here, LobbyPerson.StageOf(new LobbyMemberMessage(false, LobbyMemberStage.Playing, 1), LobbyPhase.Playing));
        Assert.Equal(SeatStage.Loading, LobbyPerson.StageOf(new LobbyMemberMessage(false, LobbyMemberStage.Playing, 1), LobbyPhase.Open));
        Assert.Equal(SeatStage.Loading, LobbyPerson.StageOf(new LobbyMemberMessage(false, LobbyMemberStage.Returning, 1), LobbyPhase.Open));
    }

    [Fact]
    public void A_state_fills_every_seat_and_marks_its_own()
    {
        var builder = new LobbyBoardBuilder(HostId);
        LobbyBoardMessage board = BoardWith(builder, Person(Sam, "Sam", ready: true));

        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend, Self = Sam });

        Assert.Equal(4, state.Seats.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, state.Seats.Select(s => s.Index));
        Assert.Equal(new[] { 1, 2, 3, 4 }, state.Seats.Select(s => s.PlayerNumber));
        Assert.True(state.Seats[2].IsEmpty);
        Assert.Equal(SeatStage.Open, state.Seats[3].Stage);
        Assert.Equal(HostId, state.HostSeat!.SteamId);
        Assert.Equal(Sam, state.SelfSeat!.SteamId);
        Assert.True(state.SelfSeat.Ready);
        Assert.Equal(2, state.Players);
        Assert.False(state.Experimental);
    }

    [Fact]
    public void A_state_with_more_seats_is_experimental_and_laid_out_for_it()
    {
        LobbyState state = LobbyState.From(new LobbyBoardBuilder(HostId, 10).Build(new LobbyBoardInputs { Host = Person(HostId, "Alex") }), new LobbyViewInputs { Role = LobbyRole.Host, Self = HostId });

        Assert.Equal(10, state.Seats.Count);
        Assert.True(state.Experimental);
    }

    [Fact]
    public void The_countdown_runs_down_by_the_boards_age_and_stops_at_zero()
    {
        var board = new LobbyBoardMessage { Phase = LobbyPhase.Counting, CountdownMs = 3000 };

        Assert.Equal(3000, LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend }).CountdownMs);
        Assert.Equal(1500, LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend, BoardAgeMicros = 1_500_000 }).CountdownMs);
        Assert.Equal(0, LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend, BoardAgeMicros = 9_000_000 }).CountdownMs);
        Assert.Equal(3000, LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend, BoardAgeMicros = -5 }).CountdownMs);
        Assert.Equal(0, LobbyState.From(board with { Phase = LobbyPhase.Open }, new LobbyViewInputs { Role = LobbyRole.Friend }).CountdownMs);
    }

    [Fact]
    public void Start_is_allowed_only_for_a_host_at_an_open_lobby_on_a_ready_title()
    {
        var open = new LobbyBoardMessage { Phase = LobbyPhase.Open };

        LobbyState ok = LobbyState.From(open, new LobbyViewInputs { Role = LobbyRole.Host, TitleReady = true });
        Assert.True(ok.StartAllowed);
        Assert.Equal(StartBlock.None, ok.WhyNot);

        Assert.Equal(StartBlock.TitleBusy, LobbyState.From(open, new LobbyViewInputs { Role = LobbyRole.Host, TitleReady = false }).WhyNot);
        Assert.Equal(StartBlock.NotOpen, LobbyState.From(open with { Phase = LobbyPhase.Counting }, new LobbyViewInputs { Role = LobbyRole.Host, TitleReady = true }).WhyNot);
        Assert.Equal(StartBlock.NotHost, LobbyState.From(open, new LobbyViewInputs { Role = LobbyRole.Friend, TitleReady = true }).WhyNot);
        Assert.False(LobbyState.None.StartAllowed);
    }

    [Fact]
    public void Who_is_not_here_is_arriving_loading_or_reconnecting_and_never_only_invited()
    {
        var builder = new LobbyBoardBuilder(HostId);
        LobbyBoardMessage board = BoardWith(builder,
            Person(Sam, "Sam", SeatStage.Joining),
            Person(Casey, "Casey", SeatStage.Loading),
            Person(Riley, "Riley", SeatStage.Invited));

        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host, Self = HostId, TitleReady = true });
        Assert.Equal(new[] { Sam, Casey }, state.NotHere.Select(s => s.SteamId));
        Assert.True(state.StartAllowed); // it asks; it never blocks
        Assert.Equal(3, state.Players); // an invited ghost isn't a player

        LobbyBoardMessage withDrop = BoardWith(new LobbyBoardBuilder(HostId), Person(Sam, "Sam", SeatStage.Reconnecting, reconnect: 27));
        Assert.Equal(new[] { Sam }, LobbyState.From(withDrop, new LobbyViewInputs { Role = LobbyRole.Host }).NotHere.Select(s => s.SteamId));
    }

    [Fact]
    public void What_only_this_pc_knows_comes_through()
    {
        LobbyState state = LobbyState.From(new LobbyBoardMessage(), new LobbyViewInputs { Role = LobbyRole.Friend, InputLocked = true, BackFromGame = true, Reconnecting = true, WaitingForEveryone = true });

        Assert.True(state.InputLocked);
        Assert.True(state.BackFromGame);
        Assert.True(state.Reconnecting);
        Assert.True(state.WaitingForEveryone);
    }

    [Fact]
    public void Names_on_a_state_are_ready_to_draw_and_never_blank()
    {
        LobbyBoardMessage board = BoardWith(new LobbyBoardBuilder(HostId), Person(Sam, "  Sam  "), Person(Casey, string.Empty));

        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host });
        Assert.Equal("Sam", state.Seats[1].Name);
        Assert.Equal("Player 3", state.Seats[2].Name);
        Assert.Equal("Alex", state.HostName);

        LobbyState chinese = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host, Language = 9 });
        Assert.Equal("玩家 3", chinese.Seats[2].Name);
    }

    [Fact]
    public void A_pcs_own_steam_name_wins_and_the_boards_stands_in_for_a_stranger()
    {
        LobbyBoardMessage board = BoardWith(new LobbyBoardBuilder(HostId), Person(Sam, "Sam from the board"), Person(Casey, "Casey from the board"), Person(Riley, "Riley from the board"));

        LobbyState state = LobbyState.From(board, new LobbyViewInputs
        {
            Role = LobbyRole.Friend,
            NameOf = (id, boardName) => id switch
            {
                Sam => "Sam Steam",
                Casey => DisplayName.SteamUnknown, // a friend of a friend
                Riley => throw new System.InvalidOperationException("Steam's gone"),
                _ => boardName,
            },
        });

        Assert.Equal("Sam Steam", state.Seats[1].Name);
        Assert.Equal("Casey from the board", state.Seats[2].Name);
        Assert.Equal("Riley from the board", state.Seats[3].Name);
        Assert.Equal("Alex", state.Seats[0].Name);
    }

    [Fact]
    public void A_name_no_font_can_draw_falls_back_on_the_state()
    {
        LobbyBoardMessage board = BoardWith(new LobbyBoardBuilder(HostId), Person(Sam, "玩家"));

        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host, CanDraw = c => c < 0x100 });

        Assert.Equal("Player 2", state.Seats[1].Name);
    }

    [Fact]
    public void A_seat_past_the_boards_capacity_is_left_out_not_thrown()
    {
        var board = new LobbyBoardMessage { Capacity = 2, Seats = new[] { new LobbyBoardSeat(5, 9, "x", false, SeatStage.Here, false, InputKind.Gamepad, 0, 0, -1, 0, 0) } };

        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host });

        Assert.Equal(2, state.Seats.Count);
        Assert.All(state.Seats, s => Assert.True(s.IsEmpty));
    }
}
