using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Core.Tests.Session;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>
/// A friend's and a host's whole life, frame by frame, as both PCs run it: real <see cref="LobbyFlow"/>s, <see cref="HostSession"/> and <see cref="FriendSession"/>s over a
/// <see cref="FakeNetwork"/>, with the session's glue (OnlineSession.Lobby.cs, HandleHostEvents, HandleFriendEvents) copied as small as it goes: what the two ends
/// do together is proven here, without the game.
/// </summary>
public class LobbyLifeTests
{
    private const ulong Sam = 200;
    private const ulong Riley = 300;
    private const ulong Casey = 400;

    [Fact]
    public void Three_friends_join_start_play_and_come_home_twice_each_period_exactly_once()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        Pc riley = life.Join(Riley);
        Pc casey = life.Join(Casey);
        life.Run(1);
        Assert.All(new[] { sam, riley, casey }, f => Assert.Equal(LobbyFriendState.InLobby, f.Flow.Friend));
        Assert.Equal(new[] { Sam, Riley, Casey }, life.Builder.FriendsInSeatOrder());

        for (uint round = 1; round <= 2; round++)
        {
            life.Press(LobbyEvent.StartPressed);
            life.Run(3.0 + 0.5);
            Assert.Equal(LobbyHostState.GameFlow, life.Flow.Host);
            Assert.Equal((int)round, life.GameStarts);
            Assert.Equal(new[] { Sam, Riley, Casey }, life.PlayersAssigned.TakeLast(3));
            Assert.All(new[] { sam, riley, casey }, f =>
            {
                Assert.Equal(LobbyFriendState.Playing, f.Flow.Friend);
                Assert.Equal((int)round, f.BeginPlays);
                Assert.Equal(round, f.Flow.AckedEpoch);
            });
            Assert.Equal(3 * (int)round, life.FollowCalls.Count);
            life.Run(1);
            Assert.All(new[] { sam, riley, casey }, f => Assert.True(f.PadPackets > 0));

            life.Title = default; // the host's scene leaves the title
            life.Run(0.5);
            Assert.Equal(LobbyHostState.InGame, life.Flow.Host);
            life.TitleBack();
            life.Run(1);
            Assert.Equal(LobbyHostState.Open, life.Flow.Host);
            Assert.All(new[] { sam, riley, casey }, f =>
            {
                Assert.Equal(LobbyFriendState.InLobby, f.Flow.Friend);
                Assert.Equal((int)round, f.EndPlays);
                Assert.False(f.PlayOn);
            });
            Assert.Empty(life.Following); // their member messages said they're home
        }
    }

    [Fact]
    public void Nothing_of_a_friends_device_reaches_the_host_before_begin_play()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(5);

        Assert.Equal(0, sam.Session!.PadPackets);
        Assert.Equal(0, life.Session.Find(Sam)!.Received!.Packets);
        Assert.Equal(PadState.Neutral, life.Session.Find(Sam)!.Input!.Update(life.Net.Now).State);
        Assert.False(life.Session.Find(Sam)!.Link.IsLagging); // pings keep a silent friend heard

        life.Press(LobbyEvent.StartPressed);
        life.Run(2.5);
        Assert.Equal(0, sam.Session.PadPackets); // the countdown isn't Start yet
        life.Run(1.5);
        Assert.True(sam.PadPacketsAtBeginPlay == 0 && sam.Session.PadPackets > 0);
    }

    [Fact]
    public void A_slow_friend_misses_the_barrier_and_catches_up_when_their_ack_comes()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        Pc riley = life.Join(Riley);
        life.Run(1);
        sam.BeginPlaySeconds = 5; // longer than the barrier: a slow disk at BeginPlay's backup

        life.Press(LobbyEvent.StartPressed);
        life.Run(3.0 + 3.2);

        Assert.Equal(LobbyHostState.GameFlow, life.Flow.Host);
        Assert.Equal(1, life.GameStarts);
        Assert.Contains(Sam, life.Flow.AwaitingAcks);
        Assert.Equal(new[] { Riley }, life.FollowCalls.ToArray());
        Assert.Contains(Sam, life.PlayersAssigned); // their seat's game player doesn't wait for them

        life.Run(3);
        Assert.Equal(LobbyFriendState.Playing, sam.Flow.Friend);
        Assert.Equal(new[] { Riley, Sam }, life.FollowCalls.ToArray());
        Assert.Contains(Sam, life.Following);
    }

    [Fact]
    public void A_friend_joining_in_each_host_state_ends_up_following_or_in_the_lobby()
    {
        var counting = new Life();
        counting.Press(LobbyEvent.StartPressed);
        counting.Run(0.5);
        Pc early = counting.Join(Sam);
        Assert.Equal(LobbyFriendState.InLobby, early.Flow.Friend);
        counting.Run(3.5);
        Assert.Equal(LobbyFriendState.Playing, early.Flow.Friend);
        Assert.Contains(Sam, counting.Following);
        Assert.Contains(Sam, counting.PlayersAssigned);

        var flow = new Life();
        flow.Press(LobbyEvent.StartPressed);
        flow.Run(3.5);
        Assert.Equal(LobbyHostState.GameFlow, flow.Flow.Host);
        Pc late = flow.Join(Riley);
        flow.Run(1);
        Assert.Equal(LobbyFriendState.Playing, late.Flow.Friend);
        Assert.Equal(1, late.BeginPlays);
        Assert.Equal(flow.Flow.Epoch, late.Flow.AckedEpoch);
        Assert.Contains(Riley, flow.Following);
        Assert.DoesNotContain(Riley, flow.PlayersAssigned); // their press joins them, on the screen that's up

        var returning = new Life();
        returning.Press(LobbyEvent.StartPressed);
        returning.Run(3.5);
        returning.Title = default;
        returning.Run(0.5);
        returning.Title = new LobbyTitleView { SceneLoaded = true, Up = true, Preparing = true };
        returning.TitleBackLoading();
        Pc back = returning.Join(Casey);
        Assert.Equal(LobbyFriendState.InLobby, back.Flow.Friend);
        Assert.Equal(0, back.BeginPlays);
    }

    [Fact]
    public void A_drop_in_play_comes_back_as_the_same_period_with_the_campaign_sent_again()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);
        life.Press(LobbyEvent.StartPressed);
        life.Run(4);
        Assert.Single(life.FollowCalls);

        life.CutLink(Sam, true);
        life.Run(SessionTiming.GoneAfterMicros / 1_000_000.0 + 1);
        Assert.Equal(LobbyFriendState.Lost, sam.Flow.Friend);
        Assert.True(sam.PlayOn); // the period holds through the drop
        Assert.True(life.Kept.ContainsKey(Sam));
        Assert.DoesNotContain(Sam, life.Following);

        life.CutLink(Sam, false);
        life.Run(5);
        Assert.Equal(LobbyFriendState.Playing, sam.Flow.Friend);
        Assert.Equal(1, sam.BeginPlays);
        Assert.Equal(0, sam.EndPlays);
        Assert.Equal(new[] { Sam, Sam }, life.FollowCalls.ToArray());
    }

    [Fact]
    public void A_drop_in_the_lobby_comes_back_to_the_lobby_and_follows_a_start_made_meanwhile()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);

        life.CutLink(Sam, true);
        life.Run(SessionTiming.GoneAfterMicros / 1_000_000.0 + 1);
        Assert.Equal(LobbyFriendState.Lost, sam.Flow.Friend);
        life.Press(LobbyEvent.StartPressed, anyway: true);
        life.Run(3.5);
        Assert.Equal(LobbyHostState.GameFlow, life.Flow.Host); // nobody to wait for: their seat is kept, they aren't here

        life.CutLink(Sam, false);
        life.Run(5);
        Assert.Equal(LobbyFriendState.Playing, sam.Flow.Friend);
        Assert.Equal(1, sam.BeginPlays);
        Assert.Contains(Sam, life.Following);
    }

    [Fact]
    public void A_host_gone_for_good_gives_everything_back_in_the_lobby_and_in_play()
    {
        var lobby = new Life();
        Pc sam = lobby.Join(Sam);
        lobby.Run(1);
        lobby.CutLink(Sam, true);
        lobby.Run(LobbyTiming.RejoinInLobbyMicros / 1_000_000.0 + SessionTiming.GoneAfterMicros / 1_000_000.0 + 1);
        Assert.Equal(LobbyFriendState.Idle, sam.Flow.Friend);
        Assert.Contains(LobbyEffect.SayLost, sam.Effects);

        var play = new Life();
        Pc riley = play.Join(Riley);
        play.Run(1);
        play.Press(LobbyEvent.StartPressed);
        play.Run(4);
        Assert.True(riley.PlayOn);
        play.CutLink(Riley, true);
        play.Run(LobbyTiming.RejoinInGameMicros / 1_000_000.0 + SessionTiming.GoneAfterMicros / 1_000_000.0 + 1);
        Assert.Equal(LobbyFriendState.Idle, riley.Flow.Friend);
        Assert.Contains(LobbyEffect.SayLost, riley.Effects);
        Assert.False(riley.PlayOn); // the lock, guard and device: every path out gives them back
    }

    [Fact]
    public void A_board_stuck_on_starting_keeps_a_friend_only_until_they_leave()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);
        life.Press(LobbyEvent.StartPressed);
        life.Run(4);
        Assert.True(sam.PlayOn);

        sam.Leave();
        Assert.Equal(LobbyFriendState.Idle, sam.Flow.Friend);
        Assert.False(sam.PlayOn);
    }

    [Fact]
    public void A_host_backing_out_brings_a_following_friend_home_and_the_next_start_is_a_new_period()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);
        life.Press(LobbyEvent.StartPressed);
        life.Run(3.5);
        Assert.Equal(LobbyHostState.GameFlow, life.Flow.Host);
        Assert.Equal(LobbyFriendState.Playing, sam.Flow.Friend);

        // The squad screen's Back: the title goes idle, and after its second the flow is back at the lobby.
        life.BackOut();
        life.Run(2);
        Assert.Equal(LobbyHostState.Open, life.Flow.Host);
        Assert.Equal(LobbyFriendState.InLobby, sam.Flow.Friend);
        Assert.Equal(1, sam.EndPlays);
        Assert.Empty(life.Following);

        life.Press(LobbyEvent.StartPressed);
        life.Run(4);
        Assert.Equal(2u, life.Flow.Epoch);
        Assert.Equal(2, sam.BeginPlays);
        Assert.Equal(2u, sam.Flow.AckedEpoch);
        Assert.Equal(new[] { Sam, Sam }, life.FollowCalls.ToArray());
    }

    [Fact]
    public void A_host_starting_again_before_a_slow_friend_is_home_keeps_their_period_going()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);
        life.Press(LobbyEvent.StartPressed);
        life.Run(3.5);
        sam.HomeTakesSeconds = 30; // a slow title load
        life.BackOut();
        life.Run(2);
        Assert.Equal(LobbyHostState.Open, life.Flow.Host);
        Assert.Equal(LobbyFriendState.Returning, sam.Flow.Friend);

        life.Press(LobbyEvent.StartPressed, anyway: true); // they show Loading, so Start asks
        life.Run(4);
        Assert.Equal(LobbyHostState.GameFlow, life.Flow.Host);
        Assert.Equal(LobbyFriendState.Playing, sam.Flow.Friend);
        Assert.Equal(1, sam.BeginPlays);
        Assert.Equal(0, sam.EndPlays);
        Assert.Equal(2u, sam.Flow.AckedEpoch);
        Assert.Equal(new[] { Sam, Sam }, life.FollowCalls.ToArray());
    }

    [Fact]
    public void Removing_a_friend_ends_their_session_and_refuses_their_next_hello()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);

        life.Remove(Sam);
        life.Run(1);
        Assert.Equal(LobbyFriendState.Idle, sam.Flow.Friend);
        Assert.Null(life.Session.Find(Sam));

        Pc again = life.Join(Sam);
        life.Run(1);
        Assert.Equal(LobbyFriendState.Idle, again.Flow.Friend);
        Assert.Equal(RejectReason.Removed, again.LastReject);
        Assert.Null(life.Session.Find(Sam));
    }

    [Fact]
    public void A_friend_who_leaves_the_lobby_frees_their_seat_at_once_and_a_drop_keeps_it()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        Pc riley = life.Join(Riley);
        life.Run(1);

        sam.Leave();
        life.Run(1);
        Assert.False(life.Kept.ContainsKey(Sam));
        Assert.Equal(new[] { Riley }, life.Builder.FriendsInSeatOrder());

        life.CutLink(Riley, true);
        life.Run(SessionTiming.GoneAfterMicros / 1_000_000.0 + 1);
        Assert.True(life.Kept.ContainsKey(Riley));
        Assert.Equal(new[] { Riley }, life.Builder.FriendsInSeatOrder());
        Assert.Equal(2, life.Builder.SeatOf(Riley));
    }

    [Fact]
    public void A_pad_friend_who_comes_to_start_with_a_keyboard_says_hello_again_and_still_acks()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(6); // past the hello limit
        sam.AtBeginPlayInput = InputKind.KeyboardMouse;

        life.Press(LobbyEvent.StartPressed);
        life.Run(4);

        Assert.Equal(InputKind.KeyboardMouse, life.Session.Find(Sam)!.Hello.Input);
        Assert.NotNull(life.Session.Find(Sam)!.Keyboard);
        Assert.Equal(LobbyFriendState.Playing, sam.Flow.Friend);
        Assert.Equal(new[] { Sam }, life.FollowCalls.ToArray());
    }

    [Fact]
    public void A_friend_home_says_so_before_the_next_start_so_the_campaign_link_is_theirs_only_in_play()
    {
        var life = new Life();
        Pc sam = life.Join(Sam);
        life.Run(1);
        Assert.Empty(life.Following);
        life.Press(LobbyEvent.StartPressed);
        life.Run(3.5);
        Assert.Contains(Sam, life.Following);
        life.BackOut();
        life.Run(2);
        Assert.Empty(life.Following);
        Assert.Equal(1, life.FollowLefts);
    }

    /// <summary>The host's PC and every friend's, a frame at a time.</summary>
    private sealed class Life
    {
        private const long FrameMicros = 16_000;
        private const ulong HostId = 100;
        private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.9.0-rc", 0x1122334455667788);

        private readonly List<ulong> _members = new() { HostId };
        private readonly Dictionary<ulong, Pc> _pcs = new();
        private readonly HashSet<ulong> _removed = new();
        private long _nextSignal;
        private long _nextBoard;
        private LobbyTitleView _title = new() { SceneLoaded = true, Up = true };
        private byte _nextSlot = 4;
        private bool _pendingTitleScene;

        public Life()
        {
            Session = new HostSession(Identity, Net.For(HostId), 3);
            Session.SetMembers(_members);
            Builder = new LobbyBoardBuilder(HostId, 4);
            Drive(Flow.Handle(LobbyEvent.HostPressed, 0));
            Drive(Flow.Handle(LobbyEvent.LobbyCreated, 0));
        }

        public FakeNetwork Net { get; } = new() { LatencyMicros = 30_000 };

        public HostSession Session { get; }

        public LobbyFlow Flow { get; } = new();

        public LobbyBoardBuilder Builder { get; }

        public int GameStarts { get; private set; }

        public List<ulong> PlayersAssigned { get; } = new();

        public List<ulong> FollowCalls { get; } = new();

        public HashSet<ulong> Following { get; } = new();

        public int FollowLefts { get; private set; }

        public Dictionary<ulong, long> Kept { get; } = new();

        public LobbyTitleView Title
        {
            set => _title = value;
        }

        public Pc Join(ulong id)
        {
            var pc = new Pc(this, id);
            _pcs[id] = pc;
            if (!_members.Contains(id))
            {
                _members.Add(id);
            }

            Session.SetMembers(_members);
            pc.Press();
            Run(0.5);
            return pc;
        }

        public void Press(LobbyEvent press, bool anyway = false)
        {
            // StartFromLobby's rule: someone not here makes Start ask, unless it's answered Start anyway.
            _ = anyway;
            Drive(Flow.Handle(press, Net.Now));
        }

        public void Remove(ulong id)
        {
            _removed.Add(id);
            Session.Remove(id, LeaveReason.Removed);
        }

        public void CutLink(ulong id, bool cut)
        {
            if (!Net.Links.TryGetValue(id, out FakeNetwork.LinkProfile? link))
            {
                link = new FakeNetwork.LinkProfile { Latency = Net.LatencyMicros };
                Net.Links[id] = link;
            }

            link.Cut = cut;
        }

        /// <summary>The game's flow was backed out of: the title is idle again.</summary>
        public void BackOut() => _title = new LobbyTitleView { SceneLoaded = true, Up = true };

        /// <summary>The title scene loaded, and its warm-ups are over.</summary>
        public void TitleBack()
        {
            _pendingTitleScene = true;
            _title = new LobbyTitleView { SceneLoaded = true, Up = true };
        }

        /// <summary>The title scene loaded, and its warm-ups still run.</summary>
        public void TitleBackLoading() => _pendingTitleScene = true;

        public void Run(double seconds)
        {
            long end = Net.Now + (long)(seconds * 1_000_000);
            while (Net.Now < end)
            {
                Net.Now += FrameMicros;
                Frame();
            }
        }

        private void Frame()
        {
            long now = Net.Now;
            Session.WelcomePhase = Flow.WelcomePhase;
            Net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    Session.Receive(from, channel, bytes, now);
                }
                else if (_pcs.TryGetValue(to, out Pc? pc))
                {
                    pc.Inbox(from, channel, bytes);
                }
            });
            Session.Update(now);
            foreach (Pc pc in _pcs.Values)
            {
                pc.Frame(now);
            }

            HostEvents(now);
            ExpireKept(now);
            foreach (Pc pc in _pcs.Values)
            {
                pc.Events(now);
            }

            Drive(Flow.Update(now));
            if (_pendingTitleScene)
            {
                _pendingTitleScene = false;
                Drive(Flow.Handle(LobbyEvent.TitleSceneLoaded, now));
            }

            if (now >= _nextSignal)
            {
                _nextSignal = now + 100_000;
                if (LobbySignals.TitleEvent(_title, Flow.Host) is { } signal)
                {
                    Drive(Flow.Handle(signal, now));
                }
            }

            if (now >= _nextBoard)
            {
                _nextBoard = now + 250_000;
                Publish(now);
            }

            foreach (Pc pc in _pcs.Values)
            {
                pc.Tick(now);
            }
        }

        private void HostEvents(long now)
        {
            while (Session.TryTakeEvent(out HostEvent e))
            {
                switch (e.Kind)
                {
                    case HostEventKind.JoinRequested when _removed.Contains(e.SteamId):
                        Session.Refuse(e.SteamId, RejectReason.Removed);
                        break;
                    case HostEventKind.JoinRequested:
                        Kept.Remove(e.SteamId);
                        Session.Accept(e.SteamId, _nextSlot++, now);
                        break;
                    case HostEventKind.Joined or HostEventKind.Rejoined:
                        Following.Remove(e.SteamId);
                        Builder.SeatOf(e.SteamId);
                        _nextBoard = 0;
                        Drive(Flow.Handle(LobbyEvent.FriendArrived, now, peer: e.SteamId));
                        break;
                    case HostEventKind.LobbyMember:
                        _nextBoard = 0;
                        Drive(Flow.Handle(LobbyEvent.FriendAcked, now, e.Member.AckedEpoch, e.SteamId), e.SteamId);
                        if (e.Member.Stage == LobbyMemberStage.InLobby && Following.Remove(e.SteamId))
                        {
                            FollowLefts++;
                        }

                        break;
                    case HostEventKind.Left:
                        Following.Remove(e.SteamId);
                        _nextBoard = 0;
                        Drive(Flow.Handle(LobbyEvent.FriendGone, now, peer: e.SteamId), e.SteamId);
                        long keep = LobbyTiming.KeepSeatMicros(Flow.Host, e.Reason);
                        if (keep > 0)
                        {
                            Kept[e.SteamId] = now + keep;
                        }

                        break;
                }
            }
        }

        private void ExpireKept(long now)
        {
            foreach (ulong id in Kept.Where(k => now >= k.Value).Select(k => k.Key).ToList())
            {
                Kept.Remove(id);
            }
        }

        private void Drive(IReadOnlyList<LobbyEffect> effects, ulong peer = 0)
        {
            foreach (LobbyEffect effect in effects)
            {
                switch (effect)
                {
                    case LobbyEffect.PublishBoard:
                        Publish(Net.Now);
                        break;
                    case LobbyEffect.AssignGamePlayers:
                        PlayersAssigned.AddRange(Builder.FriendsInSeatOrder().Where(id => Session.Find(id) is { IsJoined: true }));
                        break;
                    case LobbyEffect.CallGameStart:
                        GameStarts++;
                        _title = _title with { StartMenuOver = true }; // the game's own start, its screen over the title
                        break;
                    case LobbyEffect.FriendFollowing:
                        Following.Add(peer);
                        FollowCalls.Add(peer);
                        break;
                }
            }
        }

        private void Publish(long now)
        {
            var seated = new List<LobbySeated>();
            foreach (RemoteFriend friend in Session.Friends)
            {
                if (friend.IsJoined)
                {
                    seated.Add(new LobbySeated { SteamId = friend.SteamId, Name = "Friend", Member = friend.Member, HasMember = friend.HasMember, PingMs = -1 });
                }
            }

            List<(ulong, string, int)> kept = Kept.Select(k => (k.Key, "Friend", 1)).ToList();
            IReadOnlyList<LobbyPerson> people = LobbyPeople.Of(seated, kept, Array.Empty<ulong>(), Array.Empty<(ulong, string)>(), HostId, Flow.BoardPhase, _ => string.Empty);
            foreach (ulong id in Builder.FriendsInSeatOrder())
            {
                if (people.All(p => p.SteamId != id))
                {
                    Builder.Free(id);
                }
            }

            Session.PublishBoard(
                Builder.Build(new LobbyBoardInputs
                {
                    Phase = Flow.BoardPhase,
                    Epoch = Flow.Epoch,
                    CountdownMs = Flow.CountdownMsLeft(now),
                    Host = new LobbyPerson { SteamId = HostId, Name = "Host", Stage = SeatStage.Here, PingMs = -1 },
                    People = people,
                }),
                now);
        }

        internal ISessionTransport TransportFor(ulong id) => Net.For(id);

        internal static SessionIdentity Versions => Identity;

        internal static ulong Host => HostId;
    }

    /// <summary>One friend's PC: its flow, its session, and what its play period took.</summary>
    private sealed class Pc
    {
        private readonly Life _life;
        private readonly ulong _id;
        private readonly Queue<(ulong From, Channel Channel, byte[] Bytes)> _stalled = new();
        private long _stallUntil = long.MinValue;
        private long _homeAt = long.MinValue;
        private long _rejoinUntil = long.MinValue;
        private bool _everJoined;
        private InputKind _input = InputKind.Gamepad;

        public Pc(Life life, ulong id)
        {
            _life = life;
            _id = id;
        }

        public LobbyFlow Flow { get; } = new();

        public FriendSession? Session { get; private set; }

        public int BeginPlays { get; private set; }

        public int EndPlays { get; private set; }

        public bool PlayOn { get; private set; }

        public long PadPacketsAtBeginPlay { get; private set; } = -1;

        public long PadPackets { get; private set; }

        public double BeginPlaySeconds { get; set; }

        public double HomeTakesSeconds { get; set; }

        public InputKind? AtBeginPlayInput { get; set; }

        public RejectReason LastReject { get; private set; }

        public List<LobbyEffect> Effects { get; } = new();

        public void Press()
        {
            Drive(Flow.Handle(LobbyEvent.JoinPressed, _life.Net.Now));
            Drive(Flow.Handle(LobbyEvent.LobbyEntered, _life.Net.Now));
        }

        public void Leave() => Drive(Flow.Handle(LobbyEvent.LeavePressed, _life.Net.Now));

        public void Inbox(ulong from, Channel channel, byte[] bytes)
        {
            if (_life.Net.Now < _stallUntil)
            {
                _stalled.Enqueue((from, channel, bytes)); // this PC's main thread is busy
                return;
            }

            Session?.Receive(from, channel, bytes, _life.Net.Now);
        }

        public void Frame(long now)
        {
            if (now < _stallUntil)
            {
                return;
            }

            if (_stallUntil != long.MinValue)
            {
                // The long frame is over: what waited is read, and BeginPlay's end runs.
                _stallUntil = long.MinValue;
                while (_stalled.TryDequeue(out (ulong From, Channel Channel, byte[] Bytes) message))
                {
                    Session?.Receive(message.From, message.Channel, message.Bytes, now);
                }

                Drive(Flow.Handle(LobbyEvent.PlayReady, now));
            }

            Session?.Update(now);
            if (PlayOn && Session is { State: FriendSessionState.Joined } && _input == InputKind.Gamepad)
            {
                Session.SendPad(now, PadButtons.None, 0, 0, 0, 0, 0, 0);
                PadPackets = Session.PadPackets;
            }
        }

        public void Events(long now)
        {
            if (now < _stallUntil)
            {
                return;
            }

            while (Session != null && Session.TryTakeEvent(out FriendEvent e))
            {
                switch (e.Kind)
                {
                    case FriendEventKind.Joined:
                        _everJoined = true;
                        _rejoinUntil = long.MinValue;
                        Drive(Flow.Handle(LobbyFlow.WelcomeEvent(Session.Welcome.Phase), now));
                        SendMember();
                        break;
                    case FriendEventKind.BoardChanged:
                        if (Session.Board is { } board)
                        {
                            Drive(Flow.Handle(LobbyFlow.BoardEvent(board.Phase), now, board.Epoch));
                        }

                        break;
                    case FriendEventKind.Rejected:
                        LastReject = Session.RejectReason;
                        Drive(Flow.Handle(LobbyEvent.Refused, now));
                        Session = null;
                        break;
                    case FriendEventKind.Ended when Session.EndReason is LeaveReason.TimedOut or LeaveReason.LeftLobby or LeaveReason.None && CanRejoin(now):
                        if (_everJoined)
                        {
                            Drive(Flow.Handle(LobbyEvent.LinkLost, now));
                        }

                        StartSession(now);
                        break;
                    case FriendEventKind.Ended:
                        Drive(Flow.Handle(LobbyEvent.SessionEnded, now));
                        Session = null;
                        break;
                }
            }
        }

        public void Tick(long now)
        {
            Drive(Flow.Update(now));
            if (Flow.Friend != LobbyFriendState.Returning)
            {
                _homeAt = long.MinValue;
                return;
            }

            if (_homeAt == long.MinValue)
            {
                _homeAt = now + (long)(HomeTakesSeconds * 1_000_000);
            }

            if (now >= _homeAt)
            {
                Drive(Flow.Handle(LobbyEvent.OwnTitleBack, now));
            }
        }

        private bool CanRejoin(long now)
        {
            if (_rejoinUntil == long.MinValue)
            {
                _rejoinUntil = now + LobbyTiming.RejoinInGameMicros;
            }

            return now < _rejoinUntil;
        }

        private void StartSession(long now)
        {
            byte device = _input == InputKind.KeyboardMouse ? (byte)0 : (byte)1;
            Session = new FriendSession(Life.Versions, Life.Host, _input, device, _life.TransportFor(_id));
            Session.Start(now);
        }

        private void SendMember() => Session?.SendMember(Flow.Member(false));

        private void Drive(IReadOnlyList<LobbyEffect> effects)
        {
            long now = _life.Net.Now;
            foreach (LobbyEffect effect in effects)
            {
                Effects.Add(effect);
                switch (effect)
                {
                    case LobbyEffect.SayHello:
                        StartSession(now);
                        break;
                    case LobbyEffect.SendMember:
                        SendMember();
                        break;
                    case LobbyEffect.BeginPlay:
                        BeginPlays++;
                        PlayOn = true;
                        PadPacketsAtBeginPlay = Session?.PadPackets ?? -1;
                        if (AtBeginPlayInput is { } input && input != _input)
                        {
                            _input = input;
                            StartSession(now);
                        }

                        if (BeginPlaySeconds > 0)
                        {
                            _stallUntil = now + (long)(BeginPlaySeconds * 1_000_000);
                            BeginPlaySeconds = 0;
                        }
                        else
                        {
                            Drive(Flow.Handle(LobbyEvent.PlayReady, now));
                        }

                        break;
                    case LobbyEffect.EndPlay:
                        EndPlays++;
                        PlayOn = false;
                        break;
                    case LobbyEffect.EndSession:
                        PlayOn = false;
                        Session?.Leave();
                        Session = null;
                        break;
                }
            }
        }
    }
}
