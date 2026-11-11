#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;
using T2TOnline.Steam.Native;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// What a long session costs this PC, in the log (for a two-hour, four-player session): the host's
/// upstream and input cost every 10 s, both PCs' memory every 10 minutes, each stretch without window focus with what it
/// cost friends' input on the host, and the rate rule that keeps a friend's quiet 5 s input line from burying the rest.
/// </summary>
internal sealed partial class OnlineSession
{
    private static readonly TimeSpan LoadReportInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MemoryLineInterval = TimeSpan.FromMinutes(10);

    /// <summary>Focus changes logged in full per session (the session-end line has the totals).</summary>
    private const int FocusLinesLogged = 20;

    private readonly Dictionary<ulong, QuietReports> _quietReports = new();
    private readonly Dictionary<ulong, (long Dropped, long Underruns, long Missing)> _inputAtFocusLoss = new();
    private readonly CostMeter _feedCost = new("feeding");
    private readonly CostMeter _actionsCost = new("noting their actions");
    private readonly CostMeter _mapsGuardCost = new("maps guard");
    private readonly MemoryReport _sessionMemory = new();
    private readonly StringBuilder _loadText = new();
    private TimeSpan _nextLoadReport;
    private TimeSpan _nextMemoryLine;
    private TimeSpan _memorySince;
    private bool _loadOnline;
    private int _focusLines;

    /// <summary>A start stamp for a host input-cost meter: 0 (nothing measured) unless hosting with a friend's controller in.</summary>
    private long InputCostStart() => _host != null && _pads.Count > 0 ? CostMeter.Start() : 0;

    /// <summary>Adds the time since <paramref name="start"/> (from <see cref="InputCostStart"/>) to <paramref name="meter"/>.</summary>
    private static void InputCostAdd(CostMeter meter, long start)
    {
        if (start != 0)
        {
            meter.AddSince(start);
        }
    }

    /// <summary>
    /// Every report (5 s): the host's upstream and input cost every 10 s with a friend in, and on both PCs the memory line
    /// every 10 minutes of a session. Never throws: a failure here costs only these lines.
    /// </summary>
    private void ReportLoad()
    {
        try
        {
            TimeSpan elapsed = _clock.Elapsed;
            bool online = _host != null || _friend != null;
            if (online != _loadOnline)
            {
                // A session started or ended: its own memory line timing and samples.
                _loadOnline = online;
                _sessionMemory.Clear();
                _memorySince = elapsed;
                _nextMemoryLine = elapsed + MemoryLineInterval;
                _nextLoadReport = elapsed + LoadReportInterval;
                _quietReports.Clear();
                _focusLines = 0;
                if (online)
                {
                    _sessionMemory.Take(); // the session's first sample, which the growth is measured from
                }
            }

            if (!online)
            {
                return;
            }

            if (elapsed >= _nextMemoryLine)
            {
                _nextMemoryLine = elapsed + MemoryLineInterval;
                MemorySample now = MemoryReport.Read();
                string growth = _sessionMemory.First is MemorySample first ? now.DescribeGrowth(first) : "no first sample";
                ModLog.Info($"Online: memory after {(elapsed - _memorySince).TotalMinutes:0} min of this session: {_sessionMemory.Take()}; {growth}.");
            }

            if (_host != null && elapsed >= _nextLoadReport)
            {
                _nextLoadReport = elapsed + LoadReportInterval;
                ReportUpstream();
                ReportInbound(); // each peer's incoming rate, beside it (OnlineSession.Admission.cs)
                ReportInputCost();
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: the load report failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// Host, every 10 s with a friend joined: what goes up to all of them by Steam's own count (three friends at about 430
    /// kbps each in a match is 1.3 Mbps), and what waits.
    /// </summary>
    private void ReportUpstream()
    {
        _loadText.Clear();
        int friends = 0;
        double outKbps = 0, inKbps = 0;
        long reliable = 0, unreliable = 0;
        foreach (RemoteFriend friend in _host!.Friends)
        {
            if (!friend.IsJoined || friend.SteamId == _self)
            {
                continue;
            }

            if (SteamMessages.GetSessionInfo(friend.SteamId, out _, out SteamNetConnectionRealTimeStatus status) != SteamNetworkingConnectionState.Connected)
            {
                continue;
            }

            friends++;
            double friendOut = status.OutBytesPerSecond * 8 / 1000.0;
            outKbps += friendOut;
            inKbps += status.InBytesPerSecond * 8 / 1000.0;
            reliable += status.PendingReliable;
            unreliable += status.PendingUnreliable;
            string tag = _pads.TryGetValue(friend.SteamId, out RemotePad? pad) ? pad.Tag : Tag(friend.SteamId);
            _loadText.Append(_loadText.Length > 0 ? ", " : string.Empty).Append(CultureInfo.InvariantCulture, $"{tag} {friendOut:0}");
        }

        if (friends > 0)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Online upstream: {friends} friend(s), {outKbps:0} kbps out in all by Steam's count ({_loadText}), {inKbps:0} kbps in in all; ") +
                $"pending {reliable} B reliable, {unreliable} B unreliable in all.");
        }
    }

    /// <summary>
    /// Host, every 10 s with a friend's controller in: the main-thread cost of their input: feeding
    /// their virtual controllers each input update, noting the game actions they gave each frame, and the maps guard.
    /// </summary>
    private void ReportInputCost()
    {
        string costs = string.Join("; ", new[] { _feedCost, _actionsCost, _mapsGuardCost }.Select(m => m.DescribeInterval()).Where(t => t.Length > 0));
        if (costs.Length > 0)
        {
            ModLog.Info($"Online input cost on this PC's main thread since the last report (mean/p95/max, per input update or frame) for {_pads.Count} friend controller(s): {costs}.");
        }
    }

    /// <summary>
    /// Host: whether a friend's 5 s input line is written now (<see cref="QuietReports"/>), and the suffix saying how many
    /// quiet ones were left out before it.
    /// </summary>
    private bool WriteInputLine(RemoteFriend friend, IInputReceiver input, double lossPercent, SampleSummary roundTrip, out string suffix)
    {
        if (!_quietReports.TryGetValue(friend.SteamId, out QuietReports? quiet))
        {
            quiet = new QuietReports();
            _quietReports[friend.SteamId] = quiet;
        }

        PadPlayoutStats playout = input.PlayoutStats;
        double p95 = roundTrip.Count > 0 ? roundTrip.P95 : 0;
        long malformed = friend.Link.Malformed + friend.BadRemaps;

        // A friend home in the lobby sends nothing, and their playout counts the silence at 120 ticks a second: that's no link's trouble, and no reason for a line every 5 s.
        var counters = friend.HasMember && friend.Member.Stage == Core.Lobby.LobbyMemberStage.InLobby
            ? new InputReportCounters(0, p95, 0, 0, 0, 0, 0, input.Holds.OverOneFrame, malformed)
            : new InputReportCounters(lossPercent, p95, playout.Missing, playout.StaleTicks, playout.Resyncs, input.DroppedTicks, playout.Underruns, input.Holds.OverOneFrame, malformed);
        bool write = quiet.ShouldWrite(counters, _clock.Elapsed.TotalSeconds, out int leftOut);
        suffix = leftOut > 0 ? $" ({leftOut} quiet report(s) left out)" : string.Empty;
        return write;
    }

    /// <summary>
    /// The window lost or got back focus while online (both PCs): logged with how long it was unfocused, the frames run
    /// meanwhile and the longest of them, and on a host what friends' input lost meanwhile (a host's
    /// Alt-Tab can cost a friend dropped ticks, and this line ties the two).
    /// </summary>
    private void LogFocusChange(bool focused)
    {
        if (!focused)
        {
            _inputAtFocusLoss.Clear();
            foreach ((ulong steamId, RemotePad _) in _pads)
            {
                if (_host?.Find(steamId)?.Received is IInputReceiver input)
                {
                    _inputAtFocusLoss[steamId] = (input.DroppedTicks, input.PlayoutStats.Underruns, input.PlayoutStats.Missing);
                }
            }

            if (_focusLines++ < FocusLinesLogged)
            {
                ModLog.Info("Online: the game window lost focus; the game keeps running and reading pads (not the keyboard or mouse).");
            }

            return;
        }

        if (_focusLines++ >= FocusLinesLogged)
        {
            return;
        }

        FocusSpell? spell = FocusGuard.LastSpell;
        string how = spell is FocusSpell s
            ? string.Create(CultureInfo.InvariantCulture, $" after {s.Seconds:0.0} s unfocused ({s.Frames} frames, {s.FramesPerSecond:0} fps meanwhile; longest frame {s.LongestFrameMs:0} ms)")
            : string.Empty;
        ModLog.Info($"Online: the game window has focus again{how}; the keyboard and mouse are back{FriendsInputMeanwhile()}" +
            $"{(_focusLines == FocusLinesLogged ? " Further focus changes are counted (the session's end says how many)." : string.Empty)}");
    }

    /// <summary>
    /// "; friends' input meanwhile (...): P#3fa9c1 52 dropped ticks, 28 underruns, 0 missing." on a host with friends in (each by
    /// their tag), else ".".
    /// </summary>
    private string FriendsInputMeanwhile()
    {
        if (_host == null || _inputAtFocusLoss.Count == 0)
        {
            return ".";
        }

        var parts = new List<string>();
        foreach ((ulong steamId, (long dropped, long underruns, long missing)) in _inputAtFocusLoss)
        {
            if (_host.Find(steamId)?.Received is IInputReceiver input && _pads.TryGetValue(steamId, out RemotePad? pad))
            {
                parts.Add($"{pad.Tag} {Math.Max(0, input.DroppedTicks - dropped)} dropped ticks, {Math.Max(0, input.PlayoutStats.Underruns - underruns)} underruns, " +
                    $"{Math.Max(0, input.PlayoutStats.Missing - missing)} missing");
            }
        }

        return parts.Count == 0 ? "." : $"; friends' input meanwhile (frames late here beyond the catch-up limit drop ticks): {string.Join("; ", parts)}.";
    }
}
#endif
