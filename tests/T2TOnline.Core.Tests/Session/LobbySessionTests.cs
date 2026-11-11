using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Tests.Protocol;

namespace T2TOnline.Core.Tests.Session;

/// <summary>The lobby's board and member messages through real sessions on a <see cref="FakeNetwork"/>.</summary>
public class LobbySessionTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const ulong SecondId = 300;
    private const long Ms = 1000;
    private const long Second = 1_000_000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.9.0-m4", 0x1122334455667788);

    private static LobbyBoardMessage Board(uint epoch = 1) => LobbyMessagesTests.Sample() with { Epoch = epoch };

    [Fact]
    public void A_friend_who_just_joined_gets_the_board_at_once_after_the_welcome()
    {
        var h = new Harness();
        h.Host.PublishBoard(Board(), h.Net.Now);

        h.Join(FriendId);

        Assert.NotNull(h.Friend.Board);
        Assert.Equal(1u, h.Friend.Board!.Epoch);
        Assert.Contains(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.BoardChanged);
        Assert.Equal(0, h.Friend.Link.Malformed);
    }

    [Fact]
    public void Without_a_board_a_joining_friend_gets_none_and_nothing_breaks()
    {
        var h = new Harness();

        h.Join(FriendId);

        Assert.Null(h.Friend.Board);
        Assert.Null(h.Host.Board);
    }

    [Fact]
    public void The_welcome_says_what_the_host_flow_says()
    {
        var h = new Harness();
        h.Host.WelcomePhase = SessionPhase.Lobby;
        h.Join(FriendId);
        Assert.Equal(SessionPhase.Lobby, h.Friend.Welcome.Phase);

        var second = new Harness();
        second.Host.WelcomePhase = SessionPhase.Playing;
        second.Join(FriendId);
        Assert.Equal(SessionPhase.Playing, second.Friend.Welcome.Phase);

        var none = new Harness();
        none.Join(FriendId);
        Assert.Equal(SessionPhase.Unknown, none.Friend.Welcome.Phase);
    }

    [Fact]
    public void A_repeated_welcome_updates_the_phase_a_friend_sees()
    {
        var h = new Harness();
        h.Host.WelcomePhase = SessionPhase.Lobby;
        h.Join(FriendId);

        h.Host.WelcomePhase = SessionPhase.Playing;
        byte[] hello = HelloBytes();
        h.Host.Receive(FriendId, Channel.Events, hello, h.Net.Now);
        h.Run(50 * Ms);

        Assert.Equal(SessionPhase.Playing, h.Friend.Welcome.Phase);
    }

    [Fact]
    public void The_same_board_again_sends_nothing()
    {
        var h = new Harness();
        h.Join(FriendId);
        Assert.True(h.Host.PublishBoard(Board(), h.Net.Now));
        h.Run(300 * Ms);
        int sent = h.BoardsTo(FriendId);
        h.TakeFriendEvents();
        long changes = h.Host.BoardChanges;

        Assert.False(h.Host.PublishBoard(Board(), h.Net.Now));
        h.Run(300 * Ms);

        Assert.Equal(changes, h.Host.BoardChanges);
        Assert.DoesNotContain(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.BoardChanged);
        Assert.Equal(sent, h.BoardsTo(FriendId));
    }

    [Fact]
    public void Boards_go_at_most_four_times_a_second_per_friend_and_the_newest_wins()
    {
        var h = new Harness();
        h.Join(FriendId);
        int before = h.BoardsTo(FriendId);

        // A host's selector held down: a new board every frame for one second.
        for (uint i = 1; i <= 60; i++)
        {
            h.Host.PublishBoard(Board(i), h.Net.Now);
            h.Run(16 * Ms);
        }

        h.Run(300 * Ms);

        int sent = h.BoardsTo(FriendId) - before;
        Assert.InRange(sent, 4, 6);
        Assert.Equal(60u, h.Friend.Board!.Epoch); // the last one goes once its time's up
    }

    [Fact]
    public void A_friend_is_told_of_a_board_at_most_four_times_a_second_but_always_holds_the_newest()
    {
        var h = new Harness();
        h.Join(FriendId);
        h.TakeFriendEvents();

        // A misbehaving host sends straight past its own limit.
        for (uint i = 1; i <= 20; i++)
        {
            h.Friend.Receive(HostId, Channel.Events, BoardBytes(Board(i)), h.Net.Now);
            Assert.Equal(i, h.Friend.Board!.Epoch);
            h.Net.Now += Ms;
        }

        Assert.Single(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.BoardChanged);

        h.Run(SessionTiming.LobbyBoardIntervalMicros);
        Assert.Single(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.BoardChanged);
        h.Run(Second);
        Assert.DoesNotContain(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.BoardChanged);
    }

    [Fact]
    public void Each_friend_gets_the_board_once_on_their_own_clock()
    {
        var h = new Harness();
        h.Join(FriendId);
        h.Host.PublishBoard(Board(), h.Net.Now);
        h.Run(300 * Ms);
        int first = h.BoardsTo(FriendId);

        // A second friend joins later: theirs goes at once, and the first gets no copy.
        FriendSession second = h.AddFriend(SecondId);
        h.Join(SecondId);

        Assert.NotNull(second.Board);
        Assert.Equal(first, h.BoardsTo(FriendId));
        Assert.Equal(1, h.BoardsTo(SecondId));
    }

    [Fact]
    public void A_hello_again_gets_the_board_again_but_not_faster_than_its_limit()
    {
        var h = new Harness();
        h.Join(FriendId);
        h.Host.PublishBoard(Board(), h.Net.Now);
        h.Run(300 * Ms);
        int before = h.BoardsTo(FriendId);

        // Their game restarted: a new session, which holds no board until it's sent again.
        h.Friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 1, h.Net.For(FriendId));
        h.Friend.Start(h.Net.Now);
        h.Run(50 * Ms);
        Assert.Equal(before + 1, h.BoardsTo(FriendId));
        Assert.NotNull(h.Friend.Board);

        // Hellos on top of each other can't make it send more: one for each interval.
        for (int i = 0; i < 20; i++)
        {
            h.Host.Receive(FriendId, Channel.Events, HelloBytes(), h.Net.Now);
            h.Run(10 * Ms);
        }

        Assert.InRange(h.BoardsTo(FriendId) - before, 1, 2);
    }

    [Fact]
    public void A_board_the_transport_refused_is_sent_again()
    {
        var h = new Harness();
        h.Join(FriendId);
        bool refuse = true;
        h.Net.Refuse = (_, _, _, bytes) => refuse && bytes[0] == (byte)MessageType.LobbyBoard;

        h.Host.PublishBoard(Board(), h.Net.Now);
        h.Run(100 * Ms);
        Assert.Null(h.Friend.Board);
        Assert.True(h.Net.Refused > 0);

        refuse = false;
        h.Run(300 * Ms);
        Assert.NotNull(h.Friend.Board);
    }

    [Fact]
    public void A_friend_not_yet_accepted_gets_no_board()
    {
        var h = new Harness();
        h.Host.PublishBoard(Board(), h.Net.Now);
        h.Friend.Start(h.Net.Now);
        h.Run(50 * Ms);
        Assert.NotNull(h.Host.Find(FriendId));

        h.Host.PublishBoard(Board(2), h.Net.Now);
        h.Run(300 * Ms);

        Assert.Equal(0, h.BoardsTo(FriendId));
    }

    [Fact]
    public void A_board_before_the_welcome_on_another_channel_or_malformed_is_counted_not_taken()
    {
        var h = new Harness();
        h.Friend.Start(h.Net.Now);
        h.Friend.Receive(HostId, Channel.Events, BoardBytes(Board()), h.Net.Now);
        Assert.Null(h.Friend.Board);
        Assert.Equal(1, h.Friend.Link.Malformed);

        var joined = new Harness();
        joined.Join(FriendId);
        joined.Friend.Receive(HostId, Channel.Realtime, BoardBytes(Board()), joined.Net.Now);
        Assert.Null(joined.Friend.Board);
        Assert.Equal(1, joined.Friend.BadBoards);

        joined.Friend.Receive(HostId, Channel.Events, Message(MessageType.LobbyBoard, 99, 0), joined.Net.Now);
        Assert.Null(joined.Friend.Board);
        Assert.Equal(2, joined.Friend.BadBoards);

        byte[] tooBig = Message(MessageType.LobbyBoard, new byte[LobbyBoardMessage.MaxPayloadBytes + 1]);
        joined.Friend.Receive(HostId, Channel.Events, tooBig, joined.Net.Now);
        Assert.Equal(3, joined.Friend.BadBoards);
        Assert.Equal(3, joined.Friend.Link.Malformed);
        Assert.DoesNotContain(joined.TakeFriendEvents(), e => e.Kind == FriendEventKind.BoardChanged);
    }

    [Fact]
    public void A_board_from_anyone_but_the_host_is_dropped_unread()
    {
        var h = new Harness();
        h.Join(FriendId);

        h.Friend.Receive(SecondId, Channel.Events, BoardBytes(Board()), h.Net.Now);

        Assert.Null(h.Friend.Board);
        Assert.Equal(1, h.Friend.FromStrangers);
    }

    [Fact]
    public void Our_member_message_reaches_the_host_as_an_event_and_on_the_friend_entry()
    {
        var h = new Harness();
        h.Join(FriendId);
        h.TakeHostEvents();

        var member = new LobbyMemberMessage(true, LobbyMemberStage.InLobby, 0);
        Assert.True(h.Friend.SendMember(member));
        h.Run(50 * Ms);

        HostEvent e = Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.LobbyMember);
        Assert.Equal((FriendId, member), (e.SteamId, e.Member));
        Assert.Equal(member, h.Host.Find(FriendId)!.Member);
        Assert.True(h.Host.Find(FriendId)!.HasMember);
        Assert.Equal(0, h.Host.Find(FriendId)!.Link.Malformed);
    }

    [Fact]
    public void The_same_member_message_again_sends_nothing_and_raises_nothing()
    {
        var h = new Harness();
        h.Join(FriendId);
        var member = new LobbyMemberMessage(true, LobbyMemberStage.InLobby, 0);
        h.Friend.SendMember(member);
        h.Run(50 * Ms);
        h.TakeHostEvents();

        Assert.False(h.Friend.SendMember(member));

        // Straight past the friend's own check, as a misbehaving friend would.
        h.Host.Receive(FriendId, Channel.Events, MemberBytes(member), h.Net.Now);
        h.Run(Second);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.LobbyMember);
    }

    [Fact]
    public void A_friend_flipping_their_mark_is_taken_at_most_four_times_a_second_and_the_newest_wins()
    {
        var h = new Harness();
        h.Join(FriendId);
        h.TakeHostEvents();

        // Every frame for a second: straight to the host, past anything the friend would do.
        for (int i = 0; i < 60; i++)
        {
            h.Host.Receive(FriendId, Channel.Events, MemberBytes(new LobbyMemberMessage(i % 2 == 0, LobbyMemberStage.InLobby, (uint)i)), h.Net.Now);
            h.Run(16 * Ms);
        }

        h.Run(SessionTiming.LobbyMemberIntervalMicros + Ms);

        List<HostEvent> taken = h.TakeHostEvents().Where(e => e.Kind == HostEventKind.LobbyMember).ToList();
        Assert.InRange(taken.Count, 4, 6);
        Assert.Equal(59u, taken[^1].Member.AckedEpoch);
        Assert.Equal(59u, h.Host.Find(FriendId)!.Member.AckedEpoch);
    }

    [Fact]
    public void Nobody_can_speak_for_another_seat()
    {
        // The message has no sender in it: whoever the transport says sent it is who it's from.
        var h = new Harness();
        h.AddFriend(SecondId);
        h.Join(FriendId);
        h.Join(SecondId);
        h.TakeHostEvents();

        var member = new LobbyMemberMessage(true, LobbyMemberStage.Playing, 9);
        h.Host.Receive(SecondId, Channel.Events, MemberBytes(member), h.Net.Now);

        HostEvent e = Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.LobbyMember);
        Assert.Equal(SecondId, e.SteamId);
        Assert.False(h.Host.Find(FriendId)!.HasMember);
        Assert.Equal(member, h.Host.Find(SecondId)!.Member);
    }

    [Fact]
    public void A_member_message_from_someone_not_joined_a_stranger_or_on_another_channel_is_refused()
    {
        var h = new Harness();
        var member = new LobbyMemberMessage(true, LobbyMemberStage.InLobby, 0);

        // A stranger.
        h.Host.Receive(999, Channel.Events, MemberBytes(member), h.Net.Now);
        Assert.Equal(1, h.Host.FromStrangers);

        // A lobby member who hasn't said hello.
        h.Host.Receive(FriendId, Channel.Events, MemberBytes(member), h.Net.Now);
        Assert.Equal(1, h.Host.MalformedFromMembers);

        // Waiting, not accepted.
        h.Friend.Start(h.Net.Now);
        h.Run(20 * Ms);
        h.Host.Receive(FriendId, Channel.Events, MemberBytes(member), h.Net.Now);
        Assert.Equal(1, h.Host.Find(FriendId)!.Link.Malformed);
        Assert.False(h.Host.Find(FriendId)!.HasMember);

        h.Host.Accept(FriendId, 1, h.Net.Now);
        h.Run(50 * Ms);
        h.TakeHostEvents();

        // On the wrong channel, and cut short or with a stage it doesn't know.
        h.Host.Receive(FriendId, Channel.Realtime, MemberBytes(member), h.Net.Now);
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.LobbyMember, 1), h.Net.Now);
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.LobbyMember, 0, 99, 0), h.Net.Now);
        Assert.Equal(3, h.Host.Find(FriendId)!.BadMembers);
        Assert.False(h.Host.Find(FriendId)!.HasMember);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.LobbyMember);
    }

    [Fact]
    public void Our_member_message_goes_again_after_every_welcome()
    {
        var h = new Harness();
        h.Friend.SendMember(new LobbyMemberMessage(true, LobbyMemberStage.InLobby, 0)); // before joining: it waits
        h.Join(FriendId);
        Assert.True(h.Host.Find(FriendId)!.HasMember);
        Assert.True(h.Host.Find(FriendId)!.Member.Ready);
        h.TakeHostEvents();

        // A repeated hello starts the host over, and its welcome brings the member message back.
        h.Host.Receive(FriendId, Channel.Events, HelloBytes(), h.Net.Now);
        Assert.False(h.Host.Find(FriendId)!.HasMember);
        h.Run(SessionTiming.LobbyMemberIntervalMicros + (50 * Ms));

        Assert.True(h.Host.Find(FriendId)!.HasMember);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.LobbyMember);
    }

    [Fact]
    public void Our_member_message_sends_nothing_before_joining_or_after_the_end()
    {
        var h = new Harness();
        Assert.False(h.Friend.SendMember(new LobbyMemberMessage(false, LobbyMemberStage.InLobby, 0)));
        h.Join(FriendId);
        h.Friend.Leave();
        Assert.False(h.Friend.SendMember(new LobbyMemberMessage(true, LobbyMemberStage.InLobby, 0)));
    }

    [Fact]
    public void A_friend_that_followed_the_flow_acks_through_real_sessions()
    {
        // The whole of it, through the wire: the board says Starting, the flow's ack reaches the host's flow, and the barrier opens.
        var h = new Harness();
        var hostFlow = new LobbyFlow();
        var friendFlow = new LobbyFlow();
        hostFlow.Handle(LobbyEvent.HostPressed, 0);
        hostFlow.Handle(LobbyEvent.LobbyCreated, 0);
        h.Host.WelcomePhase = hostFlow.WelcomePhase;
        var builder = new LobbyBoardBuilder(HostId);
        void Publish() => h.Host.PublishBoard(builder.Build(new LobbyBoardInputs
        {
            Phase = hostFlow.BoardPhase,
            Epoch = hostFlow.Epoch,
            CountdownMs = hostFlow.CountdownMsLeft(h.Net.Now),
            Host = new LobbyPerson { SteamId = HostId, Name = "Alex", Stage = SeatStage.Here },
            People = new[] { new LobbyPerson { SteamId = FriendId, Name = "Sam", Stage = SeatStage.Here } },
        }), h.Net.Now);
        Publish();
        friendFlow.Handle(LobbyEvent.JoinPressed, h.Net.Now);
        friendFlow.Handle(LobbyEvent.LobbyEntered, h.Net.Now);
        h.Join(FriendId);
        Assert.Equal(new[] { LobbyEffect.ShowLobby, LobbyEffect.SendMember }, friendFlow.Handle(LobbyFlow.WelcomeEvent(h.Friend.Welcome.Phase), h.Net.Now));
        h.Friend.SendMember(new LobbyMemberMessage(false, friendFlow.MemberStage, friendFlow.AckedEpoch));
        hostFlow.Handle(LobbyEvent.FriendArrived, h.Net.Now, peer: FriendId);

        hostFlow.Handle(LobbyEvent.StartPressed, h.Net.Now);
        Publish();
        h.Run(300 * Ms);
        Assert.Equal(LobbyPhase.Counting, h.Friend.Board!.Phase);
        Assert.Empty(friendFlow.Handle(LobbyFlow.BoardEvent(h.Friend.Board.Phase), h.Net.Now, h.Friend.Board.Epoch));

        h.Net.Now += LobbyTiming.CountdownMicros;
        Assert.Equal(new[] { LobbyEffect.PublishBoard }, hostFlow.Update(h.Net.Now));
        Publish();
        h.Run(300 * Ms);
        Assert.Equal(LobbyPhase.Starting, h.Friend.Board!.Phase);
        Assert.Equal(new[] { LobbyEffect.SendMember, LobbyEffect.BeginPlay }, friendFlow.Handle(LobbyFlow.BoardEvent(h.Friend.Board.Phase), h.Net.Now, h.Friend.Board.Epoch));
        friendFlow.Handle(LobbyEvent.PlayReady, h.Net.Now);
        h.Friend.SendMember(new LobbyMemberMessage(false, friendFlow.MemberStage, friendFlow.AckedEpoch));
        h.Run(300 * Ms);

        HostEvent ack = h.TakeHostEvents().Last(e => e.Kind == HostEventKind.LobbyMember);
        Assert.Equal(1u, ack.Member.AckedEpoch);
        IReadOnlyList<LobbyEffect> effects = hostFlow.Handle(LobbyEvent.FriendAcked, h.Net.Now, ack.Member.AckedEpoch, ack.SteamId);
        Assert.Equal(new[] { LobbyEffect.FriendFollowing, LobbyEffect.PublishBoard, LobbyEffect.AssignGamePlayers, LobbyEffect.CallGameStart, LobbyEffect.CloseLobbyForStart }, effects);
    }

    private static byte[] HelloBytes()
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.Hello, 0, 0, 0).Write(writer);
        new HelloMessage(Identity.ProtocolVersion, Identity.ModVersion, Identity.GameBuild, InputKind.Gamepad, 1).Write(writer);
        return writer.ToArray();
    }

    private static byte[] BoardBytes(LobbyBoardMessage board)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.LobbyBoard, 0, 0, 0).Write(writer);
        board.Write(writer);
        return writer.ToArray();
    }

    private static byte[] MemberBytes(LobbyMemberMessage member)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.LobbyMember, 0, 0, 0).Write(writer);
        member.Write(writer);
        return writer.ToArray();
    }

    private static byte[] Message(MessageType type, params byte[] payload)
    {
        var writer = new BitWriter();
        new MessageHeader(type, 0, 0, 0).Write(writer);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    /// <summary>A host and its friends on a <see cref="FakeNetwork"/>, counting the boards that go to each.</summary>
    private sealed class Harness
    {
        private readonly Dictionary<ulong, FriendSession> _friends = new();
        private readonly Dictionary<ulong, int> _boards = new();

        public Harness()
        {
            Net = new FakeNetwork { LatencyMicros = 10 * Ms };
            Net.Refuse = (_, to, _, bytes) =>
            {
                if (bytes[0] == (byte)MessageType.LobbyBoard)
                {
                    _boards[to] = _boards.GetValueOrDefault(to) + 1;
                }

                return false;
            };
            Host = new HostSession(Identity, Net.For(HostId), maxFriends: 3);
            Host.SetMembers(new[] { HostId, FriendId, SecondId });
            Friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 1, Net.For(FriendId));
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        public FriendSession Friend
        {
            get => _friends[FriendId];
            set => _friends[FriendId] = value;
        }

        public FriendSession AddFriend(ulong id)
        {
            var friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 1, Net.For(id));
            _friends[id] = friend;
            return friend;
        }

        public int BoardsTo(ulong id) => _boards.GetValueOrDefault(id);

        public void Join(ulong id)
        {
            FriendSession friend = _friends[id];
            friend.Start(Net.Now);
            for (int i = 0; i < 1000 && friend.State == FriendSessionState.Connecting; i++)
            {
                Run(Ms);
                while (Host.TryTakeEvent(out HostEvent e))
                {
                    if (e.Kind == HostEventKind.JoinRequested)
                    {
                        Host.Accept(e.SteamId, 1, Net.Now);
                    }

                    _hostEvents.Add(e);
                }
            }

            Assert.Equal(FriendSessionState.Joined, friend.State);
            Run(50 * Ms);
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Net.Now += Ms;
                Net.Deliver((from, to, channel, bytes) =>
                {
                    if (to == HostId)
                    {
                        Host.Receive(from, channel, bytes, Net.Now);
                    }
                    else if (_friends.TryGetValue(to, out FriendSession? friend))
                    {
                        friend.Receive(from, channel, bytes, Net.Now);
                    }
                });
                Host.Update(Net.Now);
                foreach (FriendSession friend in _friends.Values)
                {
                    friend.Update(Net.Now);
                }
            }
        }

        private readonly List<HostEvent> _hostEvents = new();

        public List<HostEvent> TakeHostEvents()
        {
            var events = new List<HostEvent>(_hostEvents);
            _hostEvents.Clear();
            while (Host.TryTakeEvent(out HostEvent e))
            {
                events.Add(e);
            }

            return events;
        }

        public List<FriendEvent> TakeFriendEvents()
        {
            var events = new List<FriendEvent>();
            while (Friend.TryTakeEvent(out FriendEvent e))
            {
                events.Add(e);
            }

            return events;
        }
    }
}
