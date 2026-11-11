#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;
using T2TOnline.Steam.Native;

namespace T2TOnline.Plugin.Online;

// Which Steam sessions the host keeps. Steam keeps what an accepted peer sends on a channel nobody reads, with no limit but its
// size per message, until the session closes (and closing throws it away), so the host accepts a session only from a lobby
// member it admitted or is still deciding on, and closes the session of one it refused or removed once the refusal has left
// (Core's SessionAdmission has the rule); it drops what reaches it on the bulk channel, which only a host sends on; and every
// 10 s it logs each peer's incoming rate, Steam's count beside what was read here. Apart from what the session does
// (OnlineSession.cs) and its reports (OnlineSession.Text.cs, OnlineSession.Load.cs).
internal sealed partial class OnlineSession
{
    /// <summary>A session's first close per peer is logged; at most this many such lines a hosting (the summary counts the rest).</summary>
    private const int CloseLinesLogged = 16;

    private readonly SessionAdmission _admission = new();
    private readonly InboundTally _inbound = new();
    private readonly HashSet<ulong> _closeLogged = new();
    private readonly List<ulong> _admissionPending = new();
    private readonly List<ulong> _inboundPeers = new();
    private readonly StringBuilder _inboundText = new();
    private SteamMessageDropped? _hostBulkDropped;
    private bool _admissionOn;
    private bool _admissionFailed;
    private bool _discardFailed;
    private bool _closesOffSaid;
    private TimeSpan _inboundSince = TimeSpan.MinValue;
    private int _closedSessions;
    private int _closedAfterRefusal;
    private int _closedRefusalStuck;
    private int _closedNoHello;
    private int _closedNoSession;

    /// <summary>
    /// Host, at each member poll (once a second, and when the lobby's members are read again): every lobby member's Steam
    /// session as the rule says, accepted (without callbacks, a peer's first messages wait until we accept its session or
    /// send to it, and accepting only works once its request is in, so a hello can wait up to a second), left alone, or closed.
    /// Never from inside a drain (a member refresh for an unknown sender's message): the batch being handled came on those
    /// sessions, and the next poll is at most a second away. A failure of the rule is logged and every member's session is
    /// accepted again for this hosting, as before the rule.
    /// </summary>
    private void AcceptPendingSessions()
    {
        if (_host == null || _receiver.Draining)
        {
            return;
        }

        if (!_admissionOn)
        {
            StartAdmission();
        }

        if (_admissionFailed)
        {
            AcceptEveryMember();
            return;
        }

        try
        {
            AdmitSessions(Now);
        }
        catch (Exception ex)
        {
            _admissionFailed = true;
            ModLog.Error("Online: the rule for which Steam sessions the host keeps failed; every lobby member's session is accepted again for this hosting, as before it", ex);
            AcceptEveryMember();
        }
    }

    /// <summary>The rule before <see cref="SessionAdmission"/>: every lobby member not joined is accepted. Only after the rule failed.</summary>
    private void AcceptEveryMember()
    {
        foreach (ulong member in _link.Members)
        {
            if (member != _self && _host!.Find(member) is not { IsJoined: true })
            {
                SteamMessages.AcceptSession(member);
            }
        }
    }

    /// <summary>
    /// One pass of the rule: the lobby's members, the peers removed this hosting who are out of the lobby (a goodbye then a
    /// close), and refusals waiting to close for peers out of the lobby. This PC's own account (the one-PC test) is never judged.
    /// </summary>
    private void AdmitSessions(long now)
    {
        _admission.BeginPass();
        ulong[] members = _link.Members;
        foreach (ulong member in members)
        {
            if (member != _self)
            {
                Admit(member, inLobby: true, now);
            }
        }

        foreach (ulong removed in _removed)
        {
            if (removed != _self && Array.IndexOf(members, removed) < 0)
            {
                Admit(removed, inLobby: false, now);
            }
        }

        _admissionPending.Clear();
        _admission.CopyPending(_admissionPending);
        foreach (ulong peer in _admissionPending)
        {
            if (peer != _self && Array.IndexOf(members, peer) < 0 && !_removed.Contains(peer))
            {
                Admit(peer, inLobby: false, now);
            }
        }

        _admissionPending.Clear();
        _admission.EndPass();
    }

    /// <summary>The rule's verdict for one peer, carried out.</summary>
    private void Admit(ulong peer, bool inLobby, long now)
    {
        PeerStanding standing = StandingOf(peer);
        bool refusalOut = _admission.ClosePending(peer) && RefusalOut(peer);
        SessionVerdict verdict = _admission.Judge(peer, inLobby, standing, refusalOut, now);
        switch (verdict)
        {
            case SessionVerdict.Accept:
                SteamMessages.AcceptSession(peer);
                break;
            case SessionVerdict.CloseRefused or SessionVerdict.CloseRefusalStuck or SessionVerdict.CloseNoHello:
                CloseSession(peer, verdict);
                break;
        }
    }

    /// <summary>
    /// Where <paramref name="peer"/> stands: joined (never touched) before removed (the host's own decision, every hello refused)
    /// before admitted (a hello waiting for its answer, a seat kept for them).
    /// </summary>
    private PeerStanding StandingOf(ulong peer)
    {
        if (_host!.Find(peer) is { IsJoined: true })
        {
            return PeerStanding.Joined;
        }

        if (_removed.Contains(peer))
        {
            return PeerStanding.Removed;
        }

        return _host.Find(peer) != null || _pads.ContainsKey(peer) || _parked.ContainsKey(peer) ? PeerStanding.Admitted : PeerStanding.None;
    }

    /// <summary>
    /// True when nothing of ours still has to reach <paramref name="peer"/>: nothing held in the retrying transport, and Steam's
    /// session connected with nothing pending or unacknowledged, or ended (nothing more can leave). Still connecting: not yet.
    /// </summary>
    private bool RefusalOut(ulong peer)
    {
        if (_hostTransport is { } transport && transport.QueuedBytes(peer) > 0)
        {
            return false;
        }

        SteamNetworkingConnectionState state = SteamMessages.GetSessionInfo(peer, out _, out SteamNetConnectionRealTimeStatus status);
        return state switch
        {
            SteamNetworkingConnectionState.Connected => status.PendingReliable + status.SentUnackedReliable == 0,
            SteamNetworkingConnectionState.Connecting or SteamNetworkingConnectionState.FindingRoute => false,
            _ => true,
        };
    }

    /// <summary>Host: a refusal went to <paramref name="peer"/> (<see cref="HostEventKind.Refused"/>): their session closes once it has left.</summary>
    private void NoteRefusedSession(ulong peer)
    {
        if (peer == _self || _host == null)
        {
            return;
        }

        if (!_admissionOn)
        {
            StartAdmission(); // a hello on a session still open from before the hosting's first member poll
        }

        if (!_admissionFailed)
        {
            _admission.Refused(peer, Now);
        }
    }

    /// <summary>
    /// Closes <paramref name="peer"/>'s Steam session for <paramref name="why"/>: nothing kept for them in the retrying transport
    /// may open it again. The first close per peer is logged with Steam's answer; a failure turns closing off (said once).
    /// </summary>
    private void CloseSession(ulong peer, SessionVerdict why)
    {
        _hostTransport?.Forget(peer);
        SteamSessionClose result = _sessions.Close(peer);
        switch (result)
        {
            case SteamSessionClose.Closed:
                _closedSessions++;
                _closedAfterRefusal += why == SessionVerdict.CloseRefused ? 1 : 0;
                _closedRefusalStuck += why == SessionVerdict.CloseRefusalStuck ? 1 : 0;
                _closedNoHello += why == SessionVerdict.CloseNoHello ? 1 : 0;
                break;
            case SteamSessionClose.NoSession:
                _closedNoSession++;
                break;
        }

        if (result == SteamSessionClose.Failed && !_closesOffSaid)
        {
            _closesOffSaid = true;
            ModLog.Warn($"Online: Steam couldn't close the session with {Tag(peer)} ({_sessions.CloseFailure}); closing sessions is off for this launch, and the sessions of players the host refused end when Steam times them out, as before. Online play goes on.");
            return;
        }

        if (result is SteamSessionClose.Failed or SteamSessionClose.Off)
        {
            return; // said once, above
        }

        if (_closeLogged.Count < CloseLinesLogged && _closeLogged.Add(peer))
        {
            ModLog.Info($"Online: closing the Steam session with {Tag(peer)} ({CloseReason(peer, why)}); Steam: {CloseResult(result)}.");
        }
    }

    /// <summary>Why a session is closed, for its line.</summary>
    private string CloseReason(ulong peer, SessionVerdict why)
    {
        if (why == SessionVerdict.CloseNoHello)
        {
            return $"in the lobby {SessionAdmission.WindowMicros / 1_000_000} s with no hello to answer";
        }

        string what = _removed.Contains(peer) ? "removed by the host"
            : _refusals.TryGetValue(peer, out Refusals? refusals) ? $"refused: {refusals.Reason}"
            : "refused";
        return why == SessionVerdict.CloseRefused
            ? $"{what}; what was sent them went through"
            : $"{what}; what was sent them still hadn't gone through after {SessionAdmission.CloseAtMostMicros / 1_000_000} s";
    }

    /// <summary>Steam's answer to a close, in words.</summary>
    private static string CloseResult(SteamSessionClose result) => result switch
    {
        SteamSessionClose.Closed => "closed (what it had queued is gone)",
        SteamSessionClose.NoSession => "there was no session to close",
        SteamSessionClose.Unavailable => "it can't close sessions here (not available)",
        SteamSessionClose.NotAllowed => "not asked (this PC's own account)",
        _ => "not closed",
    };

    /// <summary>
    /// Host, every frame: what reaches the host on the bulk channel is dropped unread, at most
    /// <see cref="SessionTiming.DiscardMessagesPerDrain"/> a frame (only a host sends on it: a friend's game never does). Not while
    /// this PC is a friend in another session too (that side reads the channel). A failure turns the discard off, logged.
    /// </summary>
    private void DiscardHostBulk()
    {
        if (_discardFailed || (_friend != null && !_solo))
        {
            return;
        }

        try
        {
            _hostBulkDropped ??= HostBulkDropped;
            _receiver.Discard(SteamSessionTransport.SteamChannel(Channel.Bulk, shifted: false), _hostBulkDropped, SessionTiming.DiscardMessagesPerDrain);
        }
        catch (Exception ex)
        {
            _discardFailed = true;
            ModLog.Error("Online: dropping what reaches the host on the bulk channel failed; it's left in Steam's queue from now on, as before", ex);
        }
    }

    /// <summary>One message dropped from the host's bulk channel: counted for its sender; the first of a hosting said, by tag.</summary>
    private void HostBulkDropped(ulong sender, int channel, int size)
    {
        if (_inbound.Dropped(sender, size))
        {
            ModLog.Warn($"Online: {Tag(sender)} sent the host a message on the bulk channel, which only a host sends on (a modified client, or a host this PC left still sending): " +
                "it's dropped unread, and so is every later one (the 10 s inbound line counts them).");
        }
    }

    /// <summary>
    /// Host, every 10 s beside the upstream line: each peer's incoming rate by Steam's count (everything that arrived on their
    /// session, headers and acknowledgements included) and what was read here from them (and dropped from the bulk channel),
    /// with the messages a second. The numbers a bound on what a peer may send needs. Joined friends, other lobby members with
    /// a session, and anyone counted here; never this PC's own account.
    /// </summary>
    private void ReportInbound()
    {
        if (_host == null)
        {
            return;
        }

        TimeSpan elapsed = _clock.Elapsed;
        double seconds = _inboundSince == TimeSpan.MinValue ? LoadReportInterval.TotalSeconds : Math.Max((elapsed - _inboundSince).TotalSeconds, 0.001);
        _inboundSince = elapsed;
        _inboundPeers.Clear();
        foreach (RemoteFriend friend in _host.Friends)
        {
            AddInboundPeer(friend.SteamId);
        }

        foreach (ulong member in _link.Members)
        {
            AddInboundPeer(member);
        }

        foreach (ulong peer in _inbound.Peers)
        {
            AddInboundPeer(peer);
        }

        _inboundText.Clear();
        foreach (ulong peer in _inboundPeers)
        {
            SteamNetworkingConnectionState state = SteamMessages.GetSessionInfo(peer, out _, out SteamNetConnectionRealTimeStatus status);
            _inbound.TryGet(peer, out InboundWindow window);
            if (state != SteamNetworkingConnectionState.Connected && window.IsEmpty)
            {
                continue;
            }

            string tag = _pads.TryGetValue(peer, out RemotePad? pad) ? pad.Tag : Tag(peer);
            bool seated = _host.Find(peer) is { IsJoined: true };
            _inboundText.Append(_inboundText.Length > 0 ? "; " : string.Empty).Append(tag).Append(seated ? string.Empty : ", not seated,")
                .Append(CultureInfo.InvariantCulture, $" {status.InBytesPerSecond * 8 / 1000.0:0} / {window.ReadBytes * 8 / seconds / 1000:0} kbps, {window.ReadMessages / seconds:0} msg/s");
            if (window.DroppedMessages > 0)
            {
                _inboundText.Append(CultureInfo.InvariantCulture, $", {window.DroppedBytes * 8 / seconds / 1000:0} kbps ({window.DroppedMessages} message(s)) dropped unread from the bulk channel");
            }

            if (state != SteamNetworkingConnectionState.Connected)
            {
                _inboundText.Append(" (session ").Append(state.ToString()).Append(')');
            }
        }

        _inbound.NextWindow();
        if (_inboundText.Length > 0)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Online inbound per peer over {seconds:0} s, by Steam's count / read here: {_inboundText}."));
        }
    }

    /// <summary>Adds <paramref name="peer"/> to the inbound line's peers, once, never this PC's own account.</summary>
    private void AddInboundPeer(ulong peer)
    {
        if (peer != 0 && peer != _self && !_inboundPeers.Contains(peer))
        {
            _inboundPeers.Add(peer);
        }
    }

    /// <summary>A hosting's first member poll: the rule and the counts start afresh.</summary>
    private void StartAdmission()
    {
        _admissionOn = true;
        _admissionFailed = false;
        _admission.Clear();
        _inbound.Clear();
        _closeLogged.Clear();
        _closedSessions = _closedAfterRefusal = _closedRefusalStuck = _closedNoHello = _closedNoSession = 0;
        _inboundSince = _clock.Elapsed;
    }

    /// <summary>
    /// The hosting's end: what the rule closed and what the bulk channel dropped, when anything was, then everything forgotten.
    /// Nothing is closed here: the goodbyes just sent must leave, and Steam idles out the sessions left, as before.
    /// </summary>
    private void EndAdmission()
    {
        if (!_admissionOn)
        {
            return;
        }

        _admissionOn = false;
        if (_closedSessions + _closedNoSession > 0)
        {
            ModLog.Info($"Online: this hosting closed {_closedSessions} Steam session(s) of players it refused, removed or never heard from " +
                $"({_closedAfterRefusal} once the refusal or goodbye went through, {_closedRefusalStuck} with it still on its way after {SessionAdmission.CloseAtMostMicros / 1_000_000} s, " +
                $"{_closedNoHello} after {SessionAdmission.WindowMicros / 1_000_000} s in the lobby with no hello); Steam had no session {_closedNoSession} time(s).");
        }

        if (_inbound.DroppedMessages > 0)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Online: this hosting dropped {_inbound.DroppedMessages} message(s) ({_inbound.DroppedBytes / 1024.0:0.0} KB) sent to it on the bulk channel, the first from {Tag(_inbound.FirstDropFrom)}."));
        }

        _admission.Clear();
        _inbound.Clear();
        _closeLogged.Clear();
        _inboundSince = TimeSpan.MinValue;
    }
}
#endif
