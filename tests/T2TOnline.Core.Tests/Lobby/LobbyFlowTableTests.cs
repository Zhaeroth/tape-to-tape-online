using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;
using E = T2TOnline.Core.Lobby.LobbyEffect;
using Ev = T2TOnline.Core.Lobby.LobbyEvent;
using F = T2TOnline.Core.Lobby.LobbyFriendState;
using H = T2TOnline.Core.Lobby.LobbyHostState;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>
/// Every state with every event: the rows below are everything that does something, in its exact effects' order, and every other
/// pair is asserted to do nothing at all. The effects' order is the contract: EndPlay can't be skipped or come late.
/// </summary>
public class LobbyFlowTableTests
{
    private const ulong Peer = 7;
    private const long Now = 100_000_000;

    private static readonly Dictionary<(H, Ev), (H To, E[] Effects)> HostRows = new()
    {
        [(H.Idle, Ev.HostPressed)] = (H.Creating, new[] { E.CreateLobby }),

        [(H.Creating, Ev.LobbyCreated)] = (H.Open, new[] { E.StartHosting, E.ClearNewRunFlag, E.ShowLobby, E.PublishBoard }),
        [(H.Creating, Ev.LobbyFailed)] = (H.Idle, new[] { E.ShowFailure }),
        [(H.Creating, Ev.LeavePressed)] = (H.Idle, new[] { E.EndSession, E.ShowOnlineGroup }),
        [(H.Creating, Ev.SessionEnded)] = (H.Idle, new[] { E.EndSession, E.ShowOnlineGroup }),

        [(H.Open, Ev.StartPressed)] = (H.Counting, new[] { E.PublishBoard }),
        [(H.Open, Ev.WaitPressed)] = (H.Open, new[] { E.PublishBoard }),
        [(H.Open, Ev.TitleBusy)] = (H.GameFlow, new[] { E.PublishBoard, E.AssignGamePlayers, E.CloseLobbyForStart }),
        [(H.Open, Ev.SceneLeftTitle)] = (H.InGame, new[] { E.PublishBoard, E.HideLobby }),
        [(H.Open, Ev.LeavePressed)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),
        [(H.Open, Ev.SessionEnded)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),

        [(H.Counting, Ev.CancelPressed)] = (H.Open, new[] { E.PublishBoard }),
        [(H.Counting, Ev.TitleBusy)] = (H.GameFlow, new[] { E.PublishBoard, E.AssignGamePlayers, E.CloseLobbyForStart }),
        [(H.Counting, Ev.SceneLeftTitle)] = (H.InGame, new[] { E.PublishBoard, E.HideLobby }),
        [(H.Counting, Ev.LeavePressed)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),
        [(H.Counting, Ev.SessionEnded)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),

        [(H.Starting, Ev.FriendAcked)] = (H.GameFlow, new[] { E.FriendFollowing, E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }),
        [(H.Starting, Ev.FriendGone)] = (H.GameFlow, new[] { E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }),
        [(H.Starting, Ev.TitleBusy)] = (H.GameFlow, new[] { E.PublishBoard, E.AssignGamePlayers, E.CloseLobbyForStart }),
        [(H.Starting, Ev.SceneLeftTitle)] = (H.InGame, new[] { E.PublishBoard, E.HideLobby }),
        [(H.Starting, Ev.LeavePressed)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),
        [(H.Starting, Ev.SessionEnded)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),

        [(H.GameFlow, Ev.SceneLeftTitle)] = (H.InGame, Array.Empty<E>()),
        [(H.GameFlow, Ev.TitleSceneLoaded)] = (H.Returning, new[] { E.ClearNewRunFlag, E.ShowLobby, E.PublishBoard }),
        [(H.GameFlow, Ev.TitleReady)] = (H.Open, new[] { E.ClearNewRunFlag, E.ShowLobby, E.PublishBoard }),
        [(H.GameFlow, Ev.LeavePressed)] = (H.Idle, new[] { E.EndSession, E.ShowOnlineGroup }),
        [(H.GameFlow, Ev.SessionEnded)] = (H.Idle, new[] { E.EndSession, E.ShowOnlineGroup }),

        [(H.InGame, Ev.TitleSceneLoaded)] = (H.Returning, new[] { E.ClearNewRunFlag, E.ShowLobby, E.PublishBoard }),
        [(H.InGame, Ev.TitleReady)] = (H.Open, new[] { E.ClearNewRunFlag, E.ShowLobby, E.PublishBoard }),
        [(H.InGame, Ev.LeavePressed)] = (H.Idle, new[] { E.EndSession, E.ShowOnlineGroup }),
        [(H.InGame, Ev.SessionEnded)] = (H.Idle, new[] { E.EndSession, E.ShowOnlineGroup }),

        [(H.Returning, Ev.TitleReady)] = (H.Open, new[] { E.PublishBoard }),
        [(H.Returning, Ev.TitleIdle)] = (H.Open, new[] { E.PublishBoard }),
        [(H.Returning, Ev.SceneLeftTitle)] = (H.InGame, new[] { E.BackupAgain, E.PublishBoard, E.HideLobby }),
        [(H.Returning, Ev.LeavePressed)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),
        [(H.Returning, Ev.SessionEnded)] = (H.Idle, new[] { E.HideLobby, E.EndSession, E.ShowOnlineGroup }),
    };

    private static readonly Dictionary<(F, Ev), (F To, E[] Effects)> FriendRows = new()
    {
        [(F.Idle, Ev.JoinPressed)] = (F.JoiningLobby, new[] { E.JoinLobby }),

        [(F.JoiningLobby, Ev.LobbyEntered)] = (F.Greeting, new[] { E.SayHello }),
        [(F.JoiningLobby, Ev.LobbyFailed)] = (F.Idle, new[] { E.EndSession, E.ShowFailure, E.ShowJoinList }),
        [(F.JoiningLobby, Ev.LeavePressed)] = (F.Idle, new[] { E.EndSession, E.ShowJoinList }),
        [(F.JoiningLobby, Ev.SessionEnded)] = (F.Idle, new[] { E.EndSession, E.ShowFailure, E.ShowJoinList }),

        [(F.Greeting, Ev.WelcomedLobby)] = (F.InLobby, new[] { E.ShowLobby, E.SendMember }),
        [(F.Greeting, Ev.WelcomedPlaying)] = (F.Preparing, new[] { E.SendMember, E.BeginPlay }),
        [(F.Greeting, Ev.Refused)] = (F.Idle, new[] { E.EndSession, E.ShowFailure, E.ShowJoinList }),
        [(F.Greeting, Ev.LeavePressed)] = (F.Idle, new[] { E.EndSession, E.ShowJoinList }),
        [(F.Greeting, Ev.SessionEnded)] = (F.Idle, new[] { E.EndSession, E.ShowFailure, E.ShowJoinList }),
        [(F.Greeting, Ev.LinkLost)] = (F.Lost, new[] { E.ShowReconnecting }),

        [(F.InLobby, Ev.BoardStarting)] = (F.Preparing, new[] { E.SendMember, E.BeginPlay }),
        [(F.InLobby, Ev.BoardPlaying)] = (F.Preparing, new[] { E.SendMember, E.BeginPlay }),
        [(F.InLobby, Ev.LinkLost)] = (F.Lost, new[] { E.ShowReconnecting }),
        [(F.InLobby, Ev.LeavePressed)] = (F.Idle, new[] { E.HideLobby, E.EndSession, E.ShowJoinList }),
        [(F.InLobby, Ev.SessionEnded)] = (F.Idle, new[] { E.HideLobby, E.EndSession, E.ShowFailure, E.ShowJoinList }),

        [(F.Preparing, Ev.PlayReady)] = (F.Playing, new[] { E.SendMember, E.CloseLobbyForStart }),
        [(F.Preparing, Ev.PlayFailed)] = (F.Idle, new[] { E.EndPlay, E.HideLobby, E.EndSession, E.ShowFailure, E.ShowJoinList }),
        [(F.Preparing, Ev.LinkLost)] = (F.Lost, new[] { E.ShowReconnecting }),
        [(F.Preparing, Ev.LeavePressed)] = (F.Idle, new[] { E.EndPlay, E.HideLobby, E.EndSession, E.ShowJoinList }),
        [(F.Preparing, Ev.SessionEnded)] = (F.Idle, new[] { E.EndPlay, E.HideLobby, E.EndSession, E.ShowFailure, E.ShowJoinList }),

        [(F.Playing, Ev.BoardOpen)] = (F.Returning, new[] { E.SendMember }),
        [(F.Playing, Ev.BoardCounting)] = (F.Returning, new[] { E.SendMember }),
        [(F.Playing, Ev.BoardStarting)] = (F.Playing, new[] { E.SendMember }), // the rows' boards name a newer epoch
        [(F.Playing, Ev.BoardPlaying)] = (F.Playing, new[] { E.SendMember }),
        [(F.Playing, Ev.LinkLost)] = (F.Lost, new[] { E.ShowReconnecting }),
        [(F.Playing, Ev.LeavePressed)] = (F.Idle, new[] { E.EndPlay, E.EndSession, E.ShowJoinList }),
        [(F.Playing, Ev.SessionEnded)] = (F.Idle, new[] { E.EndPlay, E.EndSession, E.ShowFailure, E.ShowJoinList }),

        [(F.Returning, Ev.OwnTitleBack)] = (F.InLobby, new[] { E.EndPlay, E.ShowLobby, E.SendMember }),
        [(F.Returning, Ev.BoardStarting)] = (F.Playing, new[] { E.SendMember }),
        [(F.Returning, Ev.BoardPlaying)] = (F.Playing, new[] { E.SendMember }),
        [(F.Returning, Ev.LinkLost)] = (F.Lost, new[] { E.ShowReconnecting }),
        [(F.Returning, Ev.LeavePressed)] = (F.Idle, new[] { E.EndPlay, E.EndSession, E.ShowJoinList }),
        [(F.Returning, Ev.SessionEnded)] = (F.Idle, new[] { E.EndPlay, E.EndSession, E.ShowFailure, E.ShowJoinList }),

        // Lost from the lobby: a friend whose play hasn't begun.
        [(F.Lost, Ev.LinkBack)] = (F.InLobby, new[] { E.SendMember }),
        [(F.Lost, Ev.RejoinExpired)] = (F.Idle, new[] { E.HideLobby, E.EndSession, E.SayLost, E.ShowJoinList }),
        [(F.Lost, Ev.Refused)] = (F.Idle, new[] { E.HideLobby, E.EndSession, E.ShowFailure, E.ShowJoinList }),
        [(F.Lost, Ev.LeavePressed)] = (F.Idle, new[] { E.HideLobby, E.EndSession, E.ShowJoinList }),
        [(F.Lost, Ev.SessionEnded)] = (F.Idle, new[] { E.HideLobby, E.EndSession, E.ShowFailure, E.ShowJoinList }),
        [(F.Lost, Ev.WelcomedLobby)] = (F.InLobby, new[] { E.SendMember }),
        [(F.Lost, Ev.WelcomedPlaying)] = (F.Preparing, new[] { E.SendMember, E.BeginPlay }),
        [(F.Lost, Ev.BoardOpen)] = (F.InLobby, new[] { E.SendMember }),
        [(F.Lost, Ev.BoardCounting)] = (F.InLobby, new[] { E.SendMember }),
        [(F.Lost, Ev.BoardStarting)] = (F.Preparing, new[] { E.SendMember, E.BeginPlay }),
        [(F.Lost, Ev.BoardPlaying)] = (F.Preparing, new[] { E.SendMember, E.BeginPlay }),
    };

    public static IEnumerable<object[]> HostCases() =>
        from H state in Enum.GetValues<H>() from Ev e in Enum.GetValues<Ev>() select new object[] { state, e };

    public static IEnumerable<object[]> FriendCases() =>
        from F state in Enum.GetValues<F>() from Ev e in Enum.GetValues<Ev>() select new object[] { state, e };

    /// <summary>A flow in <paramref name="state"/> with one friend seated (who's awaited from the barrier on, and acked after it).</summary>
    internal static LobbyFlow HostAt(H state)
    {
        var flow = new LobbyFlow();
        long t = Now - 50_000_000;
        if (state == H.Idle)
        {
            return flow;
        }

        flow.Handle(Ev.HostPressed, t);
        if (state == H.Creating)
        {
            return flow;
        }

        flow.Handle(Ev.LobbyCreated, t);
        flow.Handle(Ev.FriendArrived, t, peer: Peer);
        if (state == H.Open)
        {
            return flow;
        }

        flow.Handle(Ev.StartPressed, t);
        if (state == H.Counting)
        {
            return flow;
        }

        flow.Update(t + LobbyTiming.CountdownMicros);
        if (state == H.Starting)
        {
            return flow;
        }

        flow.Handle(Ev.FriendAcked, t + LobbyTiming.CountdownMicros, flow.Epoch, Peer);
        if (state == H.GameFlow)
        {
            return flow;
        }

        flow.Handle(Ev.SceneLeftTitle, t + LobbyTiming.CountdownMicros);
        if (state == H.InGame)
        {
            return flow;
        }

        flow.Handle(Ev.TitleSceneLoaded, t + LobbyTiming.CountdownMicros);
        return flow;
    }

    /// <summary>A flow in <paramref name="state"/>, at epoch 1 from Preparing on; Lost is lost from the lobby.</summary>
    internal static LobbyFlow FriendAt(F state)
    {
        var flow = new LobbyFlow();
        long t = Now - 50_000_000;
        if (state == F.Idle)
        {
            return flow;
        }

        flow.Handle(Ev.JoinPressed, t);
        if (state == F.JoiningLobby)
        {
            return flow;
        }

        flow.Handle(Ev.LobbyEntered, t);
        if (state == F.Greeting)
        {
            return flow;
        }

        flow.Handle(Ev.WelcomedLobby, t);
        if (state == F.InLobby)
        {
            return flow;
        }

        if (state == F.Lost)
        {
            flow.Handle(Ev.LinkLost, t);
            return flow;
        }

        flow.Handle(Ev.BoardStarting, t, epoch: 1);
        if (state == F.Preparing)
        {
            return flow;
        }

        flow.Handle(Ev.PlayReady, t);
        if (state == F.Playing)
        {
            return flow;
        }

        flow.Handle(Ev.BoardOpen, t, epoch: 1);
        return flow;
    }

    [Theory]
    [MemberData(nameof(HostCases))]
    public void Every_host_state_with_every_event(H state, Ev e)
    {
        LobbyFlow flow = HostAt(state);
        Assert.Equal(state, flow.Host);

        // Everything that needs a friend or an epoch gets them: an ack of this Start from the friend waited for.
        IReadOnlyList<E> effects = flow.Handle(e, Now, flow.Epoch, Peer);

        if (HostRows.TryGetValue((state, e), out (H To, E[] Effects) row))
        {
            Assert.Equal(row.To, flow.Host);
            Assert.Equal(row.Effects, effects);
        }
        else if (state == H.Idle && e == Ev.JoinPressed)
        {
            Assert.Equal(H.Idle, flow.Host); // a friend now
            Assert.Equal(F.JoiningLobby, flow.Friend);
        }
        else
        {
            Assert.Equal(state, flow.Host);
            Assert.Empty(effects);
        }
    }

    [Theory]
    [MemberData(nameof(FriendCases))]
    public void Every_friend_state_with_every_event(F state, Ev e)
    {
        LobbyFlow flow = FriendAt(state);
        Assert.Equal(state, flow.Friend);

        IReadOnlyList<E> effects = flow.Handle(e, Now, epoch: 2);

        if (FriendRows.TryGetValue((state, e), out (F To, E[] Effects) row))
        {
            Assert.Equal(row.To, flow.Friend);
            Assert.Equal(row.Effects, effects);
        }
        else if (state == F.Idle && e == Ev.HostPressed)
        {
            Assert.Equal(F.Idle, flow.Friend); // a host now
            Assert.Equal(H.Creating, flow.Host);
        }
        else
        {
            Assert.Equal(state, flow.Friend);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void Every_event_and_state_has_its_case()
    {
        // A new event or state gets its own rows or does nothing, and this says so by being counted.
        Assert.Equal(8, Enum.GetValues<H>().Length);
        Assert.Equal(8, Enum.GetValues<F>().Length);
        Assert.Equal(HostCases().Count(), 8 * Enum.GetValues<Ev>().Length);
    }

    [Fact]
    public void Nothing_a_flow_is_told_can_throw()
    {
        var random = new Random(4711);
        foreach (int seed in Enumerable.Range(0, 40))
        {
            var flow = new LobbyFlow();
            for (int i = 0; i < 2000; i++)
            {
                long now = random.NextInt64(-1_000_000, 500_000_000);
                var e = (Ev)random.Next(-3, Enum.GetValues<Ev>().Length + 3);
                flow.Handle(e, now, (uint)random.Next(0, 4), (ulong)random.Next(0, 4));
                flow.Update(now);
                flow.CountdownMsLeft(now);
                flow.IsInputLocked(now);
                flow.RejoinSecondsLeft(now);
                while (flow.TryTakeChange(out LobbyFlowChange change))
                {
                    Assert.NotEmpty(change.Line);
                }
            }
        }
    }

    [Fact]
    public void Every_begin_play_gets_its_end_play_first_on_every_way_out()
    {
        // Random walks over the friend's events: BeginPlay and EndPlay alternate, EndPlay comes first in its list when the session ends,
        // and a friend back to Idle has no play left running. That's what keeps a device, a guard or a lock from being left behind.
        var random = new Random(90210);
        Ev[] events = Enum.GetValues<Ev>();
        for (int walk = 0; walk < 200; walk++)
        {
            var flow = new LobbyFlow();
            flow.Handle(Ev.JoinPressed, 0);
            int running = 0;
            long now = 0;
            for (int i = 0; i < 300; i++)
            {
                now += random.Next(0, 5_000_000);
                Ev e = events[random.Next(events.Length)];
                if (e == Ev.HostPressed)
                {
                    continue;
                }

                IReadOnlyList<E> effects = i % 3 == 0 ? flow.Update(now) : flow.Handle(e, now, (uint)random.Next(0, 3));
                foreach (E effect in effects)
                {
                    if (effect == E.BeginPlay)
                    {
                        Assert.Equal(0, running);
                        running++;
                    }
                    else if (effect == E.EndPlay)
                    {
                        Assert.Equal(1, running);
                        running--;
                    }
                }

                Assert.Equal(running == 1, flow.PlayActive);
                if (effects.Contains(E.EndPlay) && flow.Friend == F.Idle)
                {
                    Assert.Equal(E.EndPlay, effects[0]);
                }

                if (flow.Friend == F.Idle)
                {
                    Assert.Equal(0, running);
                    flow.Handle(Ev.JoinPressed, now);
                }
            }
        }
    }

    [Fact]
    public void Lost_from_a_game_ends_its_play_on_each_way_out()
    {
        foreach ((Ev e, E[] effects) in new (Ev, E[])[]
        {
            (Ev.RejoinExpired, new[] { E.EndPlay, E.EndSession, E.SayLost, E.ShowJoinList }),
            (Ev.Refused, new[] { E.EndPlay, E.EndSession, E.ShowFailure, E.ShowJoinList }),
            (Ev.LeavePressed, new[] { E.EndPlay, E.EndSession, E.ShowJoinList }),
            (Ev.SessionEnded, new[] { E.EndPlay, E.EndSession, E.ShowFailure, E.ShowJoinList }),
        })
        {
            LobbyFlow flow = FriendAt(F.Playing);
            flow.Handle(Ev.LinkLost, Now);
            Assert.Equal(effects, flow.Handle(e, Now));
            Assert.Equal(F.Idle, flow.Friend);
            Assert.False(flow.PlayActive);
        }
    }

    [Fact]
    public void An_update_with_nothing_due_allocates_no_effects()
    {
        foreach (H state in Enum.GetValues<H>())
        {
            LobbyFlow flow = HostAt(state);
            Assert.Same(Array.Empty<E>(), flow.Update(Now - 50_000_000 + LobbyTiming.CountdownMicros - 1));
        }

        LobbyFlow title = HostAt(H.GameFlow);
        Assert.Same(Array.Empty<E>(), title.Handle(Ev.TitleBusy, Now));
        Assert.Same(Array.Empty<E>(), HostAt(H.InGame).Handle(Ev.SceneLeftTitle, Now));
    }
}
