#if HAS_INTEROP
using System;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// What the session holds back while the developer's check tour runs (UiCheckHolds): a tour on a player's own PC, with
// their real Steam account, never reaches another person. Its lobby is private, the session shows no rich presence, the
// host lets in only the tour's one-PC stand-in (this same account, which never joins the Steam lobby: it says hello as the
// lobby's owner), nothing joins another lobby, whether a screen asks or Steam does (an invite accepted, Join Game), and no
// invite is sent nor Steam's invite window opened. A refused join or a held invite is counted, and fails the tour's summary.
internal sealed partial class OnlineSession
{
    /// <summary>The held-off presence was logged for this tour.</summary>
    private bool _presenceHeldSaid;

    /// <summary>True while the check tour holds (from before its first step until after its last restore: <see cref="TourHolds.On"/>).</summary>
    private static bool TourRuns => TourHolds.On;

    /// <summary>Every frame before the network is read: whom the host lets in.</summary>
    private void SyncTourAdmission()
    {
        if (_host != null)
        {
            _host.AdmitOnly = UiCheckHolds.AdmitOnly(TourRuns, _self);
        }
    }

    /// <summary>True when a lobby made now is private.</summary>
    private static bool TourLobbyPrivate() => UiCheckHolds.PrivateLobby(TourRuns);

    /// <summary>The hosting line's words for the lobby's kind.</summary>
    private string HostedLobbyKind() => _link.IsPrivate ? "private lobby" : "friends-only lobby";

    /// <summary>The hosting line's end for a tour's private lobby.</summary>
    private const string TourLobbyNote = "A check tour runs: no join key, no invites, only its one-PC stand-in is let in.";

    /// <summary>True when the session's rich presence is held off: said once per tour.</summary>
    private bool TourHoldsPresence()
    {
        if (!UiCheckHolds.HoldsPresence(TourRuns))
        {
            _presenceHeldSaid = false;
            return false;
        }

        if (!_presenceHeldSaid)
        {
            _presenceHeldSaid = true;
            ModLog.Info("Steam: rich presence held off: a check tour runs (no join key, no status).");
        }

        return true;
    }

    /// <summary>True when a join of <paramref name="lobby"/> is refused because a tour runs: said, and nothing starts.</summary>
    private static bool TourRefusesJoin(ulong lobby)
    {
        if (!UiCheckHolds.RefusesJoin(TourRuns, lobby, NowhereLobby))
        {
            return false;
        }

        TourHolds.JoinRefused();
        ModLog.Warn($"Lobby check: a join of lobby {Tags.Lobby(lobby)} was refused: a tour never joins anyone's game.");
        return true;
    }

    /// <summary>
    /// True when an invite is refused because a tour runs: <paramref name="what"/> (what asked, never who) is said and counted,
    /// and the tour's summary fails on it. Every way to Steam's invites asks this first (<see cref="InviteFriend"/>,
    /// <see cref="InviteFriends"/>, the lobby's invite list).
    /// </summary>
    private static bool TourRefusesInvite(string what)
    {
        if (!TourRuns)
        {
            return false;
        }

        TourHolds.InviteHeld(what);
        return true;
    }

    /// <summary>
    /// While a tour runs, a join Steam asks for (an invite accepted, Join Game, a game started for one) is dropped, said in
    /// the log only: the tour's screens stay as they are. True when it was (the usual handling is skipped then).
    /// </summary>
    private bool TourDropsSteamJoins(TimeSpan elapsed)
    {
        if (!TourRuns)
        {
            return false;
        }

        while (_steamJoins.Poll(elapsed, steamReady: true, gameStarted: true, out SteamJoinRequest request) == SteamJoinPoll.Ready)
        {
            TourHolds.JoinRefused();
            ModLog.Warn($"Steam: a request to join lobby {Tags.Lobby(request.LobbyId)} ({request.Source}) came while a check tour runs; dropped (a tour never joins anyone).");
        }

        _steamJoinWaiting = false;
        return true;
    }

    /// <summary>True when <paramref name="reject"/> is the tour's refusal (only it sends <see cref="RejectReason.HostUnavailable"/>): logged by tag, no toast.</summary>
    private static bool TourRefusal(RejectReason reject, string tag)
    {
        if (reject != RejectReason.HostUnavailable || !TourRuns)
        {
            return false;
        }

        ModLog.Warn($"Online: refused {tag}'s hello: a check tour runs, and only its one-PC stand-in is let in.");
        return true;
    }
}
#endif
