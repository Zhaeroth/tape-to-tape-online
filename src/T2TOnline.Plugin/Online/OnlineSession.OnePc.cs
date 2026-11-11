#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Online;

// The one-PC test (Ctrl+F6) with its friend really in a seat: this game as its own friend over Steam's loopback. The host's side is all real: AcceptFriend and its virtual controller, the seat's
// stages on the real card, the board to the friend's session and its member messages back, the barrier, game players at Start. Two things it can't be: one Steam id on two seats, so the friend sits
// under a stand-in id only Steam's name and picture calls are told about (SoloSeatId); and the friend's own following, which would load the host's own scenes over them, so the friend's flow runs with
// BeginPlay left out: it says so, and acks. Real friends never meet any of this: their ids aren't this PC's, and their flow is the session's own.
internal sealed partial class OnlineSession
{
    /// <summary>The one-PC test's friend on the board: an id Steam knows nobody by (<c>LobbyBoardBuilder</c> gives the host's own id seat 0, and a plausible id could be a stranger's).</summary>
    internal const ulong SoloSeatId = 9000000099;

    private static readonly TimeSpan SoloHomeAfter = TimeSpan.FromSeconds(1.2);

    private LobbyFlow? _soloFlow;
    private bool _soloReady;
    private TimeSpan _soloHomeAt = TimeSpan.MinValue;
    private bool _tourPadFriend;
    private bool _tourHoldHello;
    private PadButtons _tourFriendHeld;

    /// <summary>A friend's id as the board has it.</summary>
    private ulong BoardId(ulong steamId) => steamId == _self ? SoloSeatId : steamId;

    /// <summary>A board's id as the session has it.</summary>
    private ulong FromBoardId(ulong boardId) => boardId == SoloSeatId ? _self : boardId;

    private string BoardNameOf(ulong boardId) => DisplayNameOf(FromBoardId(boardId));

    /// <summary>Someone the board doesn't count as arriving: refused, or just said goodbye while Steam's list still names them.</summary>
    private bool BoardRefused(ulong boardId)
    {
        ulong id = FromBoardId(boardId);
        return IsRefused(id) || _leaving.IsLeaving(id, Now);
    }

    /// <summary>Who's in the Steam lobby, for the board: this PC is the host there, and the one-PC test's friend, until seated, is arriving as anyone in the lobby with no seat is.</summary>
    private ulong[] BoardMembers()
    {
        ulong[] members = _link.Members;
        if (!_solo || _pads.ContainsKey(_self) || _parked.ContainsKey(_self))
        {
            return members;
        }

        var arriving = new ulong[members.Length + 1];
        Array.Copy(members, arriving, members.Length);
        arriving[members.Length] = SoloSeatId;
        return arriving;
    }

    /// <summary>The one-PC test's friend has a welcome: its flow begins, or goes on from a rejoin. A new session object forgot what it last said, so it says it again.</summary>
    private void SoloWelcomed()
    {
        FriendSession friend = _friend!;
        long now = Now;
        if (_soloFlow == null)
        {
            _soloFlow = new LobbyFlow();
            _soloFlow.Handle(LobbyEvent.JoinPressed, now);
            _soloFlow.Handle(LobbyEvent.LobbyEntered, now);
            _soloReady = false;
        }

        RunSolo(_soloFlow.Handle(LobbyFlow.WelcomeEvent(friend.Welcome.Phase), now));
        SendSoloMember();
    }

    private void SoloBoardChanged()
    {
        if (_friend?.Board is { } board && _soloFlow != null)
        {
            RunSolo(_soloFlow.Handle(LobbyFlow.BoardEvent(board.Phase), Now, board.Epoch));
        }
    }

    private void SoloLinkLost()
    {
        if (_soloFlow != null)
        {
            RunSolo(_soloFlow.Handle(LobbyEvent.LinkLost, Now));
        }
    }

    /// <summary>Every frame: the friend's flow's clock, and its game "coming home" a moment after the host is back, so the card says Loading as a real friend's does.</summary>
    private void TickSoloFlow(long now, TimeSpan elapsed)
    {
        if (_soloFlow == null)
        {
            return;
        }

        RunSolo(_soloFlow.Update(now));
        if (_soloFlow.Friend == LobbyFriendState.Idle)
        {
            _soloFlow = null;
            return;
        }

        if (_soloFlow.Friend != LobbyFriendState.Returning || _flow.Host != LobbyHostState.Open)
        {
            _soloHomeAt = TimeSpan.MinValue;
            return;
        }

        if (_soloHomeAt == TimeSpan.MinValue)
        {
            _soloHomeAt = elapsed + SoloHomeAfter;
        }
        else if (elapsed >= _soloHomeAt)
        {
            _soloHomeAt = TimeSpan.MinValue;
            RunSolo(_soloFlow.Handle(LobbyEvent.OwnTitleBack, now));
        }
    }

    private void RunSolo(IReadOnlyList<LobbyEffect> effects)
    {
        foreach (LobbyEffect effect in effects)
        {
            switch (effect)
            {
                case LobbyEffect.SendMember:
                    SendSoloMember();
                    break;
                case LobbyEffect.BeginPlay:
                    ModLog.Info("Lobby (one-PC test): the friend's following isn't run: it would load this game's own scenes over it. Its flow says ready at once, and acks.");
                    RunSolo(_soloFlow!.Handle(LobbyEvent.PlayReady, Now));
                    break;
            }
        }

        while (_soloFlow != null && _soloFlow.TryTakeChange(out LobbyFlowChange change))
        {
            ModLog.Info($"Lobby (one-PC test's friend): phase {change.From} -> {change.To} (epoch {change.Epoch}): {change.Why}.");
        }
    }

    private void SendSoloMember()
    {
        if (_soloFlow != null)
        {
            _friend?.SendMember(_soloFlow.Member(_soloReady));
        }
    }

    // ---- The check tour's reach (developer only) ----

    /// <summary>
    /// The tour's one-PC test: its friend plays with a pad that's only a stream (<see cref="TourFriendPress"/>), so a real <c>VirtualPad</c> is made for it on a PC with no pad connected.
    /// <paramref name="holdHello"/>: its hello doesn't leave until <see cref="TourLetHelloGo"/>, so the seat says Joining for as long as the tour likes.
    /// </summary>
    internal bool TourSeatFriend(bool holdHello)
    {
        _tourPadFriend = true;
        _tourHoldHello = holdHello;
        ToggleSolo();
        return _solo;
    }

    /// <summary>The held hello goes.</summary>
    internal void TourLetHelloGo()
    {
        _tourHoldHello = false;
        if (_friendTransport != null && _dropUntil == TimeSpan.MinValue)
        {
            _friendTransport.Cut = false;
        }
    }

    /// <summary>What the friend's stream says is held on their pad: it goes out over Steam's loopback, the host's playout puts it on their virtual pad.</summary>
    internal void TourFriendPress(PadButtons held) => _tourFriendHeld = held;

    /// <summary>The friend's ready mark, in a real member message.</summary>
    internal void TourFriendReady(bool ready)
    {
        _soloReady = ready;
        SendSoloMember();
    }

    /// <summary>A kick's refusal, forgotten: the tour goes on with them.</summary>
    internal void TourForgetRemoved() => _removed.Remove(_self);

    /// <summary>The friend reports a failed load for the next match (the onepc tour's <c>op_failload</c>); false takes it back.</summary>
    internal void TourFailNextLoad(bool on) => _relay.TourFailNextLoad(on);

    /// <summary>True while the host's stream holds a load failure from the one-PC test's friend.</summary>
    internal bool TourFriendLoadFailed => _relay.StreamTo(_self) is { LoadFailure.Length: > 0 };

    /// <summary>How many friends' skaters the AI plays now.</summary>
    internal int TourStandIns => _standIn.ActiveCount;

    /// <summary>How many times the loading barrier re-zeroed a time scale the game set back, this session.</summary>
    internal int TourBarrierResets => _relay.TourBarrierResets;

    /// <summary>The one-PC test's friend's session has ended (or never began).</summary>
    internal bool TourFriendGone => _friend == null;

    /// <summary>The friend's game player on the host, or -1.</summary>
    internal int TourFriendGamePlayer =>
        _pads.TryGetValue(_self, out RemotePad? pad) && Il2CppControllerConnection.RewiredPlayerInputAssigner.Instance != null ? Il2CppControllerConnection.RewiredPlayerInputAssigner.GetPlayerId(pad.Pad.RewiredPlayerId) : -1;

    /// <summary>What crossed: the board the friend's session holds, and the member message the host's holds, for the tour's line.</summary>
    internal string TourFriendLine()
    {
        string friend = "no friend session";
        if (_friend is { } session)
        {
            if (session.Board is { } board)
            {
                var seats = new List<string>(board.Seats.Count);
                foreach (Core.Protocol.LobbyBoardSeat seat in board.Seats)
                {
                    seats.Add($"{seat.Index}:{seat.Stage}{(seat.IsHost ? " host" : string.Empty)}{(seat.Ready ? " ready" : string.Empty)}{(seat.PingMs >= 0 ? $" {seat.PingMs} ms {seat.Bars} bars" : string.Empty)}{(seat.ReconnectSeconds > 0 ? $" {seat.ReconnectSeconds} s" : string.Empty)}");
                }

                friend = $"the friend's session holds a board: phase {board.Phase}, epoch {board.Epoch}, mode {board.Mode}, seats [{string.Join(", ", seats)}]; {session.BadBoards} bad";
            }
            else
            {
                friend = $"the friend's session has no board yet ({session.State})";
            }
        }

        RemoteFriend? remote = _host?.Find(_self);
        string host = remote == null ? "no friend on the host's session" : remote.HasMember
            ? $"the host's holds their member message: ready {remote.Member.Ready}, stage {remote.Member.Stage}, acked epoch {remote.Member.AckedEpoch}; {remote.BadMembers} bad"
            : "the host's has no member message from them";
        return $"{friend}; {host}; their flow {_soloFlow?.Friend.ToString() ?? "none"}";
    }
}
#endif
