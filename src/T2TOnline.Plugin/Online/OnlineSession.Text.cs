#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Startup;
using T2TOnline.Steam;
using T2TOnline.Steam.Native;

namespace T2TOnline.Plugin.Online;

// The session's words, apart from what it does (OnlineSession.cs): the log's reports every 5 s, refusals, the net
// overlay's lines and the status box, and how names are shown.
internal sealed partial class OnlineSession
{
    /// <summary>The rate rule for this PC's own link line as a friend (<see cref="ReportFriend(bool)"/>).</summary>
    private readonly LinkReportQuiet _friendQuiet = new();

    /// <summary>
    /// A refusal goes in the log in full the first time for a sender (and when the reason changes), then as a count once a
    /// minute (<see cref="ReportRefusals"/>): a friend on the wrong version may keep saying hello.
    /// </summary>
    private void LogRefusal(in HostEvent e, string tag)
    {
        if (TourRefusal(e.Reject, tag))
        {
            return; // the check tour's own refusal: a line, no toast (OnlineSession.TourHolds.cs)
        }

        if (_refusals.TryGetValue(e.SteamId, out Refusals? refusals) && refusals.Reason == e.Reject)
        {
            refusals.Since++;
            return;
        }

        _refusals[e.SteamId] = new Refusals(tag, e.Reject, _clock.Elapsed);
        ModLog.Warn($"Online: refused {tag}: {e.Reject} (their T2T Online {SentText.Release(e.Hello.ModVersion)}, protocol {e.Hello.ProtocolVersion}, " +
            $"game build {e.Hello.GameBuild:x16}, input {e.Hello.Input}; ours {_identity.ModVersion}, {ProtocolInfo.Version}, {_buildHex}).");
        // On the host's screen too (once per friend and reason): the friend is told, but the host should know why too.
        Notify(NoticeId.FriendRefused, JoinFailures.HostToast(e.Reject, DisplayNameOf(e.SteamId)), JoinFailures.HostToast(e.Reject, tag));
    }

    /// <summary>A refusal in words: to the friend refused (<paramref name="toFriend"/>), or to the host who refused them.</summary>
    private static string RejectSentence(RejectReason reason, bool toFriend) => reason switch
    {
        RejectReason.ProtocolMismatch => toFriend ? "you have another T2T Online version (both need the same one)" : "they have another T2T Online version",
        RejectReason.GameBuildMismatch => toFriend ? "you're on another game update (both need the same one)" : "they're on another game update",
        RejectReason.SessionFull => toFriend ? "their game is full" : "your game is full",
        RejectReason.NotInLobby => toFriend ? "you aren't in their Steam lobby" : "they aren't in your Steam lobby",
        RejectReason.NoControllerSlot => toFriend ? "their game couldn't make a controller for you" : "no controller could be made for them (see the log)",
        RejectReason.UnsupportedInput => toFriend ? "their game doesn't know your kind of controller (another T2T Online version?)" : "their game sent a kind of controller this version doesn't know",
        RejectReason.HostUnavailable => toFriend ? "online play is off on their game" : "online play is off here",
        RejectReason.NotAFriend => toFriend ? "you aren't on their Steam friends list" : "they aren't on your Steam friends list",
        RejectReason.FriendshipUnconfirmed => toFriend ? "their Steam couldn't check their friends list (they need to restart Steam and the game)" : "Steam couldn't check your friends list (restart Steam and the game)",
        _ => "no reason given",
    };

    /// <summary>
    /// Why a friend's session ended, in words, for the notice (<paramref name="joined"/>: it had joined at some point), naming the host as <paramref name="host"/>
    /// (their name for the screen, their tag for the log).
    /// </summary>
    private static string EndSentence(LeaveReason reason, bool joined, string host) => reason switch
    {
        LeaveReason.HostClosed => $"{host} ended the session.",
        LeaveReason.Removed => $"{host} removed you from the session.",
        LeaveReason.TimedOut when !joined => $"{host}'s game didn't answer. Are they still hosting? {JoinWay} tries again.",
        LeaveReason.TimedOut or LeaveReason.LeftLobby or LeaveReason.None => $"Lost {host}, and couldn't get back in. {JoinWay} tries again.",
        _ => "The session is over.",
    };

    private void ReportRefusals()
    {
        foreach (Refusals refusals in _refusals.Values)
        {
            TimeSpan since = _clock.Elapsed - refusals.LoggedAt;
            if (refusals.Since > 0 && since >= RefusalSummaryInterval)
            {
                ModLog.Warn($"Online: refused {refusals.Tag} {refusals.Since} more time(s) in the last {since.TotalSeconds:0} s ({refusals.Reason}).");
                refusals.Since = 0;
                refusals.LoggedAt = _clock.Elapsed;
            }
        }
    }

    private void Report()
    {
        if (FightGrace.Changes != _graceReported)
        {
            _graceReported = FightGrace.Changes; // a punch held or let go since the last report
            ModLog.Info(FightGrace.Summary());
        }

        ReportRefusals();

        if (_host != null)
        {
            foreach (RemoteFriend friend in _host.Friends)
            {
                if (_pads.TryGetValue(friend.SteamId, out RemotePad? pad) && friend.Received != null)
                {
                    ReportRemote(friend, pad);
                }
            }
        }

        ReportFriend();
        ReportUndelivered();
        ReportLoad();
    }

    /// <summary>Messages that never reached a session, when there are any and the counts changed since the last report.</summary>
    private void ReportUndelivered()
    {
        long strangers = (_host?.FromStrangers ?? 0) + (_friend?.FromStrangers ?? 0);
        long malformed = _host?.MalformedFromMembers ?? 0;
        long hellos = _host?.HellosHeldBack ?? 0;
        if (strangers + malformed + hellos + _receiver.Skipped + _receiver.Dropped + _sessions.Restarts == 0)
        {
            return;
        }

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{strangers} from senders outside the session, {malformed} malformed from lobby members, {hellos} first hellos too soon after the last, {_receiver.Skipped} that looked wrong, " +
            $"{_receiver.Dropped} dropped after a handler failed; Steam sessions restarted {_sessions.Restarts} time(s)");
        if (line != _undelivered)
        {
            _undelivered = line;
            ModLog.Info($"Online: so far, messages not delivered: {line}.");
        }
    }

    private void ReportRemote(RemoteFriend friend, RemotePad pad)
    {
        PeerLink link = friend.Link;
        IInputReceiver input = friend.Received!;
        double seconds = Math.Max((_clock.Elapsed - pad.LastReport).TotalSeconds, 0.001);
        pad.LastReport = _clock.Elapsed;
        long packets = input.Packets - pad.LastPackets;
        long bytes = link.BytesIn - pad.LastBytes;
        pad.LastPackets = input.Packets;
        pad.LastBytes = link.BytesIn;
        PadPlayoutStats playout = input.PlayoutStats;
        SampleSummary rtt = link.RoundTripMs.Summarize();
        link.RoundTripMs.Clear();
        SampleSummary holds = input.Holds.ErrorMs.Summarize();
        input.Holds.ErrorMs.Clear();
        double loss = link.RealtimeLoss.TakeWindowLossPercent();

        // Read at every report, written or not: D133's guard follows the link's route here (NoteRoute), and readings further apart
        // than its gap start it over, so a line left out by the rate rule mustn't take the reading with it.
        string route = Route(friend.SteamId);
        if (!WriteInputLine(friend, input, loss, rtt, out string quiet))
        {
            // Nothing moved since the last line written (the rate rule); the hold lines below still go.
            LogRecentHolds(friend, pad, input);
            return;
        }

        ModLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"Online: {pad.Tag} (Rewired player {pad.Pad.RewiredPlayerId}, game player {pad.GamePlayer}): {packets / seconds:0} {(friend.Keyboard != null ? "keyboard" : "pad")} packets/s, " +
            $"{bytes * 8 / seconds / 1000:0.0} kbps in, {loss:0.0}% lost, {link.Malformed} malformed; round trip mean/p95/max {rtt} ms; " +
            $"playout delay {input.BufferedFrames} frames, so far {playout.Played} played, {playout.Missing} missing, {playout.Underruns} underruns, " +
            $"{playout.Skipped} skipped, {playout.StaleTicks} stale, {playout.Resyncs} resyncs, {input.DroppedTicks} dropped ticks; " +
            $"holds {holds.Count} this window, {input.Holds.Count} total, {input.Holds.OverOneFrame} changed by more than a frame, {input.Holds.Lost} lost or merged, " +
            $"{input.Holds.CutShort} cut short by silence, |played - theirs| mean/p95/max {holds} ms; " +
            $"{input.Taps} taps") +
            (friend.Keyboard is { } keys ? $"; key table generation {keys.Generation} ({keys.ElementCount} keys), {keys.StaleGeneration} packets of another table dropped" : string.Empty) +
            (friend.BadRemaps > 0 ? $"; {friend.BadRemaps} controls messages refused" : string.Empty) +
            (friend.RejoinsHeldBack > 0 ? $"; {friend.RejoinsHeldBack} repeated hellos held back (rejoins too close together)" : string.Empty) +
            $"; {route}.{quiet}");
        LogRecentHolds(friend, pad, input);
    }

    /// <summary>The first few holds a friend's input played here, each with its error, then the recent list cleared.</summary>
    private static void LogRecentHolds(RemoteFriend friend, RemotePad pad, IInputReceiver input)
    {
        foreach (HoldComparison hold in input.Holds.Recent)
        {
            if (pad.HoldsLogged++ < HoldsLoggedIndividually)
            {
                string what = friend.Keyboard != null && friend.Bindings is { } table && hold.Element < table.Elements.Count ? RewiredBindings.ElementName(table.Elements[hold.Element]) : hold.Button.ToString();
                ModLog.Info($"Online hold: {pad.Tag} {what} held {hold.FriendMs:0} ms ({hold.FriendFrames} frames) on their {(friend.Keyboard != null ? "keyboard" : "pad")}, {hold.PlayedTicks} ticks here ({hold.ErrorFrames:+0;-0;0}).");
            }
        }

        input.Holds.Recent.Clear();
    }

    private void ReportFriend() => ReportFriend(final: false);

    /// <summary>
    /// The friend's own link line, every 5 s: written by the rate rule (<see cref="LinkReportQuiet"/>: a new state or route, a rising round trip, a
    /// refused send, at least every 30 s), and always as the session ends (<paramref name="final"/>). The route is read every time, written or not:
    /// D133's guard reads it there (<see cref="NoteRoute"/>).
    /// </summary>
    private void ReportFriend(bool final)
    {
        if (_friend == null)
        {
            return;
        }

        PeerLink link = _friend.Link;
        SampleSummary rtt = link.RoundTripMs.Summarize();
        link.RoundTripMs.Clear();
        long sent = _friend.PadPackets + _friend.KeyboardPackets;
        long packets = sent - _friendLastPackets;
        long bytes = link.BytesOut - _friendLastBytes;
        double seconds = Math.Max((_clock.Elapsed - _friendLastReport).TotalSeconds, 0.001);
        _friendLastPackets = sent;
        _friendLastBytes = link.BytesOut;
        _friendLastReport = _clock.Elapsed;
        string route = string.Empty;
        string kind = _friend.State.ToString();
        if (!_solo)
        {
            route = $"; {Route(_friend.Host, out string routeKind)}";
            kind = $"{kind}, {routeKind}";
        }

        if (_friendQuiet.ShouldWrite(kind, rtt.Count > 0 ? rtt.P95 : 0, link.SendFailures, _clock.Elapsed.TotalSeconds, final, out int leftOut))
        {
            string where = _solo ? "Online one-PC test (friend side)" : $"Online (friend of {HostTag()})";
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"{where}: {_friend.State}; {packets / seconds:0} {(_friend.Input == InputKind.KeyboardMouse ? "keyboard" : "pad")} packets/s, {bytes * 8 / seconds / 1000:0.0} kbps out, " +
                $"{link.SendFailures} sends refused; round trip mean/p95/max {rtt} ms; host clock offset {(_friend.Clock.HasEstimate ? string.Create(CultureInfo.InvariantCulture, $"{_friend.Clock.OffsetMicros / 1000.0:0.0} ms") : "not yet")}") +
                route + (leftOut > 0 ? $" ({leftOut} quiet report(s) left out)" : string.Empty) + ".");
        }

        if (_soloHolds != null)
        {
            foreach (MatchedHold hold in _soloHolds.Matched)
            {
                double errorMs = hold.Error / 1000.0;
                _soloHoldErrorMs.Add(Math.Abs(errorMs));
                if (_soloHoldsLogged++ < HoldsLoggedIndividually)
                {
                    ModLog.Info($"Online one-PC hold: {hold.Button} held {hold.SentDuration / 1000.0:0} ms on the real pad, {errorMs:+0;-0;0} ms on the virtual pad.");
                }
            }

            _soloHolds.Matched.Clear();
            ModLog.Info($"Online one-PC test hold times so far: {_soloHoldErrorMs.Count} presses compared; virtual minus real, |error| mean/p95/max {_soloHoldErrorMs.Summarize()} ms.");
        }
    }

    private string Route(ulong peer) => Route(peer, out _);

    /// <summary>The link to <paramref name="peer"/> for a report line, with <paramref name="kind"/> its state and route alone (for the rate rule).</summary>
    private string Route(ulong peer, out string kind)
    {
        if (peer == _self)
        {
            kind = "loopback";
            return "route: loopback";
        }

        // A session without a route yet isn't "direct": IsRelayed is false until a route is found. Relayed or direct only:
        // a relay's data centre would say where a player is (D127), and "two relays" (each side's nearest differs) is all a
        // stall needs from it; the queue and pending bytes explain the rest.
        SteamNetworkingConnectionState state = SteamMessages.GetSessionInfo(peer, out SteamNetConnectionInfo info, out SteamNetConnectionRealTimeStatus status);
        string route = LinkShape.Route(state == SteamNetworkingConnectionState.Connected, info.IsRelayed, info.RelayPopId, info.RemotePopId);
        NoteRoute(peer, state == SteamNetworkingConnectionState.Connected, info.IsRelayed); // D133's guard (OnlineSession.Privacy.cs)
        kind = $"session {state}, {route}";
        return $"session {state}, {route}, Steam ping {status.Ping} ms, " +
            string.Create(CultureInfo.InvariantCulture,
                $"quality local {status.QualityLocal:0.00} remote {status.QualityRemote:0.00}, queue {status.QueueTimeMicroseconds / 1000.0:0.0} ms, " +
                $"pending {status.PendingReliable} B reliable ({status.SentUnackedReliable} B unacked) and {status.PendingUnreliable} B unreliable, " +
                $"Steam out {status.OutBytesPerSecond * 8 / 1000.0:0} kbps / in {status.InBytesPerSecond * 8 / 1000.0:0} kbps (send rate {status.SendRateBytesPerSecond * 8 / 1000.0:0} kbps)") +
            (state is SteamNetworkingConnectionState.ClosedByPeer or SteamNetworkingConnectionState.ProblemDetectedLocally ? $"; ended ({info.EndReason}): {SafeText.Clean(info.EndDebug, 128)}" : string.Empty);
    }

    /// <summary>The net overlay's numbers: per friend on the host, the link to the host on a friend's PC.</summary>
    private void BuildNet(TimeSpan elapsed)
    {
        _net.Begin(elapsed);
        CultureInfo culture = CultureInfo.InvariantCulture;
        if (_host != null)
        {
            _net.Line($"T2T Online net · hosting · {_host.Friends.Count(f => f.IsJoined)} friend(s) in · F9 hides");
            foreach (RemoteFriend friend in _host.Friends)
            {
                string key = friend.SteamId.ToString(culture);
                string name = _pads.TryGetValue(friend.SteamId, out RemotePad? pad) ? pad.DisplayName : "a friend";
                PeerLink link = friend.Link;
                double loss = _net.LossPercent($"{key}.loss", link.RealtimeLoss.Expected, link.RealtimeLoss.Received);
                string padLine = friend.Received is { } input
                    ? string.Create(culture, $"{(friend.Keyboard != null ? "keys" : "pad")} {_net.PerSecond($"{key}.pad", input.Packets):0}/s, playout {input.BufferedFrames} fr, {_net.Delta($"{key}.underruns", input.PlayoutStats.Underruns)} underruns")
                    : "not joined yet";
                _net.Line(string.Create(culture,
                    $"{name}: ping {NetOverlay.Ms(link.LastRoundTripMicros)} ms · loss {loss:0.0}% · {padLine}{(link.IsLagging ? " · SILENT" : string.Empty)}"));
                if (_relay.StreamTo(friend.SteamId) is not { } stream)
                {
                    continue;
                }

                if (stream.Streaming)
                {
                    double rate = _net.PerSecond($"{key}.rate", stream.SnapshotsSent);
                    long snapshots = _net.Delta($"{key}.snapshots", stream.SnapshotsSent);
                    long bytes = _net.Delta($"{key}.bytes", stream.SnapshotBytes);
                    _net.Line(string.Create(culture,
                        $"   stream {_net.PerSecond($"{key}.streambytes", stream.BytesSent) * 8 / 1000:0} kbps · {rate:0} snapshots/s, {(snapshots == 0 ? 0 : bytes / snapshots)} B · " +
                        $"{stream.Peer.Keyframes} keyframes, {stream.Peer.Held} held · {stream.SendFailures + link.SendFailures} refused"));
                }
                else
                {
                    _net.Line(stream.LoadFailure.Length > 0 ? $"   couldn't load the match: {SafeText.Clean(stream.LoadFailure, MatchReadyMessage.MaxReasonBytes)}"
                        : stream.Loading ? "   loading the match" : "   not in a match");
                }

                if (stream.Streaming && stream.ReportMicros != long.MinValue && Now - stream.ReportMicros < 3_000_000)
                {
                    StreamReport report = stream.Report;
                    _net.Line(string.Create(culture,
                        $"   they see it {NetOverlay.Ms(report.RenderBehindMicros)} ms behind the stream (aim {NetOverlay.Ms(report.TargetDelayMicros)}, jitter {NetOverlay.Ms(report.JitterMicros)}) · " +
                        $"held {100.0 * report.HeldFrames / Math.Max(1, report.Frames):0.0}% · {report.Frames} fps"));
                }
            }
        }
        else if (_friend != null)
        {
            PeerLink link = _friend.Link;
            double loss = _net.LossPercent("host.loss", link.RealtimeLoss.Expected, link.RealtimeLoss.Received);
            _net.Line(_solo ? "T2T Online net · one-PC test (friend side) · F9 hides" : $"T2T Online net · {_hostName}'s session · F9 hides");
            _net.Line(string.Create(culture,
                $"ping {NetOverlay.Ms(link.LastRoundTripMicros)} ms · loss {loss:0.0}% · {(_friend.Input == InputKind.KeyboardMouse ? "keys" : "pad")} out {_net.PerSecond("pad", _friend.PadPackets + _friend.KeyboardPackets):0}/s, " +
                $"{_net.PerSecond("bytesout", link.BytesOut) * 8 / 1000:0} kbps · in {_net.PerSecond("bytesin", link.BytesIn) * 8 / 1000:0} kbps{(link.IsLagging ? " · HOST SILENT" : string.Empty)}"));
            if (_relay.FromHost is { } client)
            {
                _net.Line(string.Create(culture,
                    $"stream: {_net.PerSecond("decoded", client.Receiver.Decoded):0} snapshots/s · {client.Receiver.MissingBase} without their base · {client.Receiver.Malformed} malformed"));
            }

            if (_relay.PlayingHostMatch)
            {
                StreamReport report = _relay.LastStreamReport;
                _net.Line(string.Create(culture,
                    $"view: {NetOverlay.Ms(report.RenderBehindMicros)} ms behind the stream (aim {NetOverlay.Ms(report.TargetDelayMicros)}, jitter {NetOverlay.Ms(report.JitterMicros)}) · " +
                    $"held {100.0 * report.HeldFrames / Math.Max(1, report.Frames):0.0}% ({report.Holds} holds) · {report.Frames} fps"));
            }
        }
        else
        {
            _net.Line("T2T Online net · not in a session (F7 hosts or joins) · F9 hides");
        }
    }

    /// <summary>
    /// The status box's lines, rebuilt four times a second (its countdowns move by the second, notices show at once), and
    /// handed to the overlay as a new list only when they changed. The full box is a developer's (the ShowStatusBox setting), and the
    /// session's only view for anyone in one with no native screens (the NativeLobby setting off, or they failed); a player's holds only
    /// what the game's own toasts and prompts can't carry: a notice that fell back, a restore waiting for a restart, online being off,
    /// the mod stopped after an error.
    /// </summary>
    private void RefreshStatus(TimeSpan elapsed)
    {
        if (elapsed < _nextStatus)
        {
            return;
        }

        _nextStatus = elapsed + StatusInterval;
        _statusLines.Clear();
        if (_broken)
        {
            _statusLines.Add(_modStopped
                ? "T2T Online stopped after an error (see the log); the game carries on, and a restart brings online play back"
                : "T2T Online · online is off after an error (see the log); restart the game to play online");
            if (_notice != null && elapsed < _noticeUntil)
            {
                _statusLines.Add(_notice);
            }
        }
        else if (ModSettings.ShowStatusBox || (!ScreensReady && (_host != null || _friend != null || _link.Creating || _link.Joining)))
        {
            BuildStatus(_statusLines, elapsed);
        }
        else
        {
            BuildPlayerStatus(_statusLines, elapsed);
        }

        if (!SameLines(_statusLines, _status))
        {
            _status = _statusLines.ToArray();
        }
    }

    private static bool SameLines(List<string> lines, IReadOnlyList<string> shown)
    {
        if (lines.Count != shown.Count)
        {
            return false;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i], shown[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What a player's status box holds with the developer's off: a notice the native screens couldn't carry (<see cref="ShowInBox"/>), the restart a restore waits for, and at the
    /// title, for a little while, that online is off (an install check). Nothing about who is connected or how: the lobby and the game's own toasts say what a player needs.
    /// </summary>
    private void BuildPlayerStatus(List<string> lines, TimeSpan elapsed)
    {
        if (Campaign.SaveBackups.RestartNeeded)
        {
            lines.Add(Campaign.SaveBackups.RestartNotice);
        }

        if (_notice != null && elapsed < _noticeUntil)
        {
            lines.Add(_notice);
        }

        if (lines.Count == 0 && _startedAt != TimeSpan.MinValue && elapsed - _startedAt < ReadyNoticeFor)
        {
            if (!T2TOnlinePlugin.OnlineAvailable)
            {
                lines.Add($"T2T Online {ReleaseVersion.ForScreen(_identity.ModVersion)} · online is off this session (see the log)");
            }
            else if (_self == 0)
            {
                lines.Add($"T2T Online {ReleaseVersion.ForScreen(_identity.ModVersion)} · {_steamOff}, so online play is off");
            }
        }
    }

    private void BuildStatus(List<string> lines, TimeSpan elapsed)
    {
        if (_link.Creating)
        {
            lines.Add("T2T Online · creating a lobby...");
        }
        else if (_link.Joining)
        {
            lines.Add(_link.JoiningName.Length > 0 ? $"T2T Online · joining {_link.JoiningName}'s lobby..." : "T2T Online · joining a friend's lobby...");
        }

        if (_host != null)
        {
            lines.Add(_pads.Count + _parked.Count == 0
                ? $"T2T Online · hosting · friends join from Online > Join Lobby{(ModSettings.DevTools ? " (or F7)" : string.Empty)} · {LeaveWay} ends"
                : $"T2T Online · hosting · {LeaveWay} ends");
            if (_link.OutOfLobby)
            {
                lines.Add(_link.Replacing ? "   Steam lost your lobby: making a new one; friends in the session carry on"
                    : "   friends can't join right now: reconnecting to Steam (friends in the session carry on)");
            }

            foreach (RemoteFriend friend in _host.Friends)
            {
                if (!_pads.TryGetValue(friend.SteamId, out RemotePad? pad))
                {
                    continue;
                }

                string state = pad.GamePlayer >= 0 ? $"P{pad.GamePlayer + 1}" : "not joined yet";
                if (friend.Link.LastRoundTripMicros >= 0)
                {
                    state += $" · {friend.Link.LastRoundTripMicros / 1000} ms";
                }

                state += friend.Link.IsLagging ? " · lagging" : friend.PadConnected ? $" · {DeviceLabel(pad.Pad)}" : $" · {AwayLabel(friend.AwayReason)}";
                state += _standIn.IsStandingIn(pad.Pad.RewiredPlayerId) ? " · the AI plays for them" : string.Empty;
                lines.Add($"   {pad.DisplayName}: {state}");
            }

            foreach ((RemotePad pad, TimeSpan until) in _parked.Values)
            {
                TimeSpan left = until - elapsed;
                string standIn = _standIn.IsStandingIn(pad.Pad.RewiredPlayerId) ? " (the AI plays for them)" : string.Empty;
                lines.Add(pad.RemovalDeferred || left < TimeSpan.Zero
                    ? $"   {pad.DisplayName}: dropped, keeping their player until this match is over{standIn}"
                    : $"   {pad.DisplayName}: dropped, keeping their player {(int)left.TotalMinutes}:{left.Seconds:00}{standIn}");
            }
        }

        if (_friend != null)
        {
            string rtt = _friend.Link.LastRoundTripMicros >= 0 ? $" · {_friend.Link.LastRoundTripMicros / 1000} ms" : string.Empty;
            string key = _solo ? SoloKey : LeaveWay;
            string state = _friend.State switch
            {
                // A first join isn't "lost" yet: the host hasn't answered (it may be loading, or not hosting any more).
                FriendSessionState.Connecting when !_everJoined => $"waiting for {_hostName} to answer... ({key} cancels)",
                FriendSessionState.Connecting when _relinking => $"reconnecting to {_hostName}...",
                FriendSessionState.Connecting => $"lost {_hostName}, rejoining... ({key} stops)",
                FriendSessionState.Joined => _solo ? $"one-PC test: your {Device()} plays as the friend{rtt} · {key} stops" : $"your {Device()} plays on {_hostName}'s game{rtt} · {key} leaves",
                _ => "leaving...",
            };
            lines.Add(_solo ? $"   {state}" : $"T2T Online · {state}");
            if (OwnMenuStatusLine() is { Length: > 0 } ownMenu)
            {
                lines.Add(ownMenu); // your own remapper or settings (OnlineSession.Remap.cs)
            }

            if (_localPad is { IsDetached: true } && !_localPadWasConnected)
            {
                lines.Add("   your pad is disconnected: reconnect it to carry on");
            }
            else if (_friend.State == FriendSessionState.Joined)
            {
                lines.Add(PadLine()); // which pad goes to the host, and what the host reads it as
            }

            if (!_solo && _link.OutOfLobby)
            {
                lines.Add("   reconnecting to Steam (you're out of the lobby; the game carries on)");
            }
        }

        lines.AddRange(_relay.Status());
        lines.AddRange(_campaign.Status());
        if (Campaign.SaveBackups.RestartNeeded)
        {
            lines.Add(Campaign.SaveBackups.RestartNotice);
        }
        if (_notice != null && elapsed < _noticeUntil)
        {
            lines.Add(_notice);
        }

        if (_simulated != null && lines.Count > 0)
        {
            lines.Add($"   {_simulated}");
        }

        // At the title screen, for a little while: the mod loaded, and whether online play is on (an install check).
        if (lines.Count == 0 && _startedAt != TimeSpan.MinValue && elapsed - _startedAt < ReadyNoticeFor)
        {
            lines.Add(!T2TOnlinePlugin.OnlineAvailable ? $"T2T Online {ReleaseVersion.ForScreen(_identity.ModVersion)} · online is off this session (see the log)"
                : _self == 0 ? $"T2T Online {ReleaseVersion.ForScreen(_identity.ModVersion)} · {_steamOff}, so online play is off"
                : ModSettings.DevTools ? $"T2T Online {ReleaseVersion.ForScreen(_identity.ModVersion)} is ready · F7 to play online · F1 the mod's panel · F9 net overlay"
                : $"T2T Online {ReleaseVersion.ForScreen(_identity.ModVersion)} is ready · Online on the title screen");
        }
    }

    /// <summary>What this PC plays with as a friend, for the status box: "pad" or "keyboard".</summary>
    private string Device() => _localKeyboard != null ? "keyboard" : "pad";

    // Steam names, and anything a peer sent, are cleaned before they reach the screen (a name by the label's rules,
    // ScreenName: no typed escape survives into a toast or a prompt). The log never holds a name or a Steam ID: it names
    // players by their tags (D127, OnlineSession.LogTags.cs).
    private string DisplayNameOf(ulong steamId) => steamId == _self ? "you (one-PC test)" : ScreenName.Of(SteamFriends.GetFriendPersonaName(steamId), 64);

    /// <summary>The log tags in use now (the lobby's, or this launch's own outside one).</summary>
    private static LogTags Tags => ModLog.Scrub.Tags;

    /// <summary>How a log line names a player: their tag (<c>P#a0c1d2</c>), the same in every PC's log of the lobby.</summary>
    private static string Tag(ulong steamId) => ModLog.Scrub.Tags.Player(steamId);

    /// <summary>How a friend's log lines name their host: its tag ("the host" before there is one).</summary>
    private string HostTag() => _hostSteamId == 0 ? "the host" : Tag(_hostSteamId);

    /// <summary>How the log names the host of a game from the friends list: its tag, or "a friend" when its lobby names no host.</summary>
    private static string GameHostTag(JoinableGame game) => game.HostId != 0 ? Tag(game.HostId) : "a friend";
}
#endif
