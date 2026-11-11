#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Online;

// What is needed to judge a pad from the logs alone:
// each trigger's peak on both PCs, a trigger's first pull reaching the host, the friend's pad so far at each match's end,
// and a notice on the friend's screen when a trigger stops short of the game's 0.50 while the other passes it;
// and the rest of the input and link lines (each bounded): a friend's controls going out, the device taken back
// from a player of this game, a keyboard's brawl keys, each friend's link and actions at the departure and at each match's
// end, and the friends' maps guard's cost at the hosting's end. Apart from what the session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    /// <summary>"Couldn't be put back" warnings written per friend; the rest are counted in their maps summary.</summary>
    private const int UnfixedWarnings = 3;

    private static long _mapsFullChecks;
    private static long _mapsFriendsChecked;
    private static double _mapsWorstFrameMs;
    private static int _mapsWorstFrameFriends;
    private static long _mapsLooks;
    private static long _mapsLookMicros;
    private readonly RepeatLimiter _takenBack = new(3, 60_000, 16);
    private bool _friendWasBusy;
    private bool _triggerNoticeShown;
    private bool _hostWasStreaming;
    private long _controlsSentSeen;
    private byte _controlsSentGeneration;

    /// <summary>
    /// Host, every input update a friend's virtual pad is fed: what it played, with each axis's peak (no allocation), and
    /// a trigger's first pull past the game's threshold marked for its log line (made in the frame's update, not here).
    /// </summary>
    private static void NotePlayed(RemotePad pad, in PadState state, PadButtons taps)
    {
        pad.Played.Observe(state.Buttons | taps, state.GetAxis(PadAxis.LeftStickX), state.GetAxis(PadAxis.LeftStickY), state.GetAxis(PadAxis.RightStickX),
            state.GetAxis(PadAxis.RightStickY), state.GetAxis(PadAxis.LeftTrigger), state.GetAxis(PadAxis.RightTrigger));
        if (pad.TriggersArrived == 3)
        {
            return;
        }

        int arrived = (pad.Played.Peak(PadAxis.LeftTrigger) >= PadUsage.AxisThreshold ? 1 : 0) | (pad.Played.Peak(PadAxis.RightTrigger) >= PadUsage.AxisThreshold ? 2 : 0);
        int fresh = arrived & ~pad.TriggersArrived;
        if (fresh != 0)
        {
            pad.TriggersArrived |= fresh;
            pad.TriggersToLog |= fresh;
        }
    }

    /// <summary>Host: a friend's pad as played here, with its triggers' peaks, as it goes (beside the actions it gave).</summary>
    private static void LogPlayed(RemotePad pad)
    {
        if (pad.Pad is VirtualPad)
        {
            ModLog.Info($"Online: {pad.Tag}'s pad as played here: {pad.Played.DescribeWithPeaks()} (the game's trigger actions need more than {TriggerCheck.Format(PadUsage.AxisThreshold)}).");
        }
    }

    /// <summary>Every frame: the input lines a play test reads (a trigger's first arrival on the host, a friend's pad at each match's end).</summary>
    private void ReportInput()
    {
        if (_host != null)
        {
            foreach ((ulong steamId, RemotePad pad) in _pads)
            {
                if (pad.TriggersToLog != 0)
                {
                    LogTriggerArrival(pad);
                }

                // Their link as it is now, kept for the departure's totals (the session drops it before saying they left).
                if (_host.Find(steamId) is { } friend && !ReferenceEquals(friend, pad.Link))
                {
                    pad.Link = friend;
                    pad.LinkSince = FrameClock.Seconds;
                }
            }

            bool streaming = _relay.HostStreaming;
            if (_hostWasStreaming && !streaming)
            {
                HostMatchOver();
            }

            _hostWasStreaming = streaming;
        }
        else
        {
            _hostWasStreaming = false;
        }

        if (_friend == null)
        {
            _friendWasBusy = false;
            _triggerNoticeShown = false;
            _controlsSentSeen = 0;
            return;
        }

        if (_friend.BindingsSent != _controlsSentSeen)
        {
            _controlsSentSeen = _friend.BindingsSent;
            ControlsWentOut(_friend);
        }

        bool busy = _relay.FriendBusy;
        if (_friendWasBusy && !busy && _localPad != null)
        {
            FriendMatchOver(_localPad);
        }

        _friendWasBusy = busy;
    }

    /// <summary>
    /// Friend: our controls (message 20) went to the host: said once per generation, so a missing
    /// "controls ... applied" line on the host can be told from controls that never left.
    /// </summary>
    private void ControlsWentOut(FriendSession friend)
    {
        if (friend.Bindings is not { } controls || controls.Generation == _controlsSentGeneration)
        {
            return;
        }

        _controlsSentGeneration = controls.Generation;
        string hostTag = _solo ? "the one-PC test's host side" : HostTag();
        ModLog.Info($"Online: your controls (generation {controls.Generation}, {(controls.Device == InputKind.KeyboardMouse ? "keyboard and mouse" : "pad")}, " +
            $"{controls.Bindings.Count} bindings{(controls.KeepsTemplateMaps ? ", the game's defaults" : string.Empty)}) went to {hostTag}; the host logs them as applied.");
    }

    /// <summary>
    /// Friend, as an input update takes this PC's pad, keyboard or mouse back from a player of this game that was given it
    /// (Rewired, or the game's connect handler): the first three per player and device in full, then once a minute with the
    /// count (it happens every input update the game gives it again).
    /// </summary>
    private void NoteTakenBack(int rewiredPlayer, string device)
    {
        switch (_takenBack.Next(string.Create(CultureInfo.InvariantCulture, $"{device} {rewiredPlayer}"), Environment.TickCount64, out int counted))
        {
            case RepeatDecision.Write:
                ModLog.Warn($"Online: Rewired player {rewiredPlayer} had been given your {device}; taken back, so it drives only your online player.");
                break;
            case RepeatDecision.Summarize:
                ModLog.Warn($"Online: Rewired player {rewiredPlayer} was given your {device} again {counted} more time(s) in the last minute; taken back each time.");
                break;
        }
    }

    /// <summary>
    /// Host, a keyboard friend's controls applied: a key table without Q or E can't make the brawl combo (Q+E), so the
    /// game's offers can't be accepted for them; said once per controls.
    /// </summary>
    private static void NoteBrawlKeys(string tag, InputBindingSet controls)
    {
        if (controls.Device != InputKind.KeyboardMouse || BrawlCombo.KeyboardCombo(controls) != 0)
        {
            return;
        }

        bool q = controls.IndexOf(BindingElement.Key(BindingElement.KeyQ)) >= 0;
        bool e = controls.IndexOf(BindingElement.Key(BindingElement.KeyE)) >= 0;
        string missing = !q && !e ? "Q and E" : !q ? "Q" : "E";
        ModLog.Info($"Brawl: {tag}'s key table has no {missing} (their maps bind nothing there), so Q+E can't accept a brawl offer for them.");
    }

    /// <summary>
    /// Host, a match's stream is over: each friend's controller so far, against what its maps bind,
    /// one line each.
    /// </summary>
    private void HostMatchOver()
    {
        foreach ((ulong steamId, RemotePad pad) in _pads)
        {
            try
            {
                (IReadOnlyList<string> given, int of) = pad.Pad.ActionsSeen();
                ActionCoverage coverage = ActionCoverage.Of(given, BoundActions(steamId, pad.Pad));
                ModLog.Info($"Online: {pad.Tag}'s {pad.Pad.Kind.ToLowerInvariant()} so far gave the game {given.Count} of its {of} actions; bound but not yet given: " +
                    $"{ActionCoverage.Names(coverage.NotYet, 400)}.");
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Online: the line about {pad.Tag}'s actions at the match's end failed ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    /// <summary>
    /// Host, as a friend goes or drops (<paramref name="when"/>): their link since they last joined, in all:
    /// packets, loss over the whole link, round trip percentiles, holds, playout. Never throws.
    /// </summary>
    private static void LogLinkTotals(RemotePad pad, string when)
    {
        try
        {
            if (pad.Link is not { } friend)
            {
                return;
            }

            PeerLink link = friend.Link;
            IInputReceiver? input = friend.Received;
            long expected = link.RealtimeLoss.Expected;
            double loss = expected > 0 ? 100.0 * (expected - link.RealtimeLoss.Received) / expected : 0;
            double minutes = Math.Max(0, FrameClock.Seconds - pad.LinkSince) / 60;
            PadPlayoutStats playout = input?.PlayoutStats ?? default;
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Online: {pad.Tag}'s link {when} ({minutes:0.0} min since they last joined): {input?.Packets ?? 0} {(friend.Keyboard != null ? "keyboard" : "pad")} packets, " +
                $"{Math.Max(0, loss):0.00}% lost overall, round trip p50/p95/max {link.RoundTrips.Percentile(0.5) / 1000.0:0}/{link.RoundTrips.Percentile(0.95) / 1000.0:0}/" +
                $"{link.RoundTrips.Percentile(1) / 1000.0:0} ms ({link.RoundTrips.Count} pings), {input?.Holds.Count ?? 0} holds ({input?.Holds.OverOneFrame ?? 0} off by more " +
                $"than a frame), playout {playout.Missing} missing / {playout.Underruns} underruns / {playout.StaleTicks} stale, {link.Malformed} malformed."));
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: {pad.Tag}'s link totals failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// The game's actions a friend's controller's maps bind, by name in the game's order (their own bindings, or for a pad
    /// on the game's defaults the gamepad template maps): for the departure line, made once there.
    /// </summary>
    private static List<string> BoundNames(VirtualController controller)
    {
        IEnumerable<InputBinding> bindings = controller.Controls != null ? controller.Controls.Bindings
            : controller is VirtualPad ? RewiredBindings.PadDefaults(VirtualPadDefinition.MapCategoryIds, out _)
            : Array.Empty<InputBinding>();
        var bound = new HashSet<int>(bindings.Select(b => (int)b.ActionId));
        return RewiredBindings.ActionIds().Where(bound.Contains).Select(RewiredBindings.ActionName).ToList();
    }

    /// <summary>The maps guard's due check of every friend took <paramref name="ticks"/> (Stopwatch) in one frame, for <paramref name="friends"/> friends.</summary>
    private static void NoteMapsFrame(long ticks, int friends)
    {
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        _mapsFullChecks++;
        _mapsFriendsChecked += friends;
        if (ms > _mapsWorstFrameMs)
        {
            _mapsWorstFrameMs = ms;
            _mapsWorstFrameFriends = friends;
        }
    }

    /// <summary>One quick look at a friend's maps took <paramref name="micros"/>.</summary>
    private static void NoteMapsLook(long micros)
    {
        _mapsLooks++;
        _mapsLookMicros += micros;
    }

    /// <summary>
    /// Host, as hosting stops: the friends' maps guard's cost this hosting and the brawl combos the
    /// game wouldn't take; then they start over.
    /// </summary>
    private void ReportHostingInputTotals()
    {
        if (_mapsFullChecks > 0 || _mapsLooks > 0)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Online: the friends' maps guard this session: {_mapsFullChecks} due check(s) of every friend ({_mapsFriendsChecked} friend check(s) in all), slowest frame " +
                $"{_mapsWorstFrameMs:0.00} ms ({_mapsWorstFrameFriends} friend(s) checked in it), {_mapsLooks} look(s), mean {(_mapsLooks == 0 ? 0 : _mapsLookMicros / _mapsLooks)} µs."));
        }

        int notTaken = BrawlComboRelay.TakeNotTaken();
        if (notTaken > 0)
        {
            ModLog.Info($"Brawl: {notTaken} combo(s) from friends came while the game wouldn't take one (no offer open, a brawl under way, or after a goal) this session.");
        }

        _mapsFullChecks = 0;
        _mapsFriendsChecked = 0;
        _mapsWorstFrameMs = 0;
        _mapsWorstFrameFriends = 0;
        _mapsLooks = 0;
        _mapsLookMicros = 0;
    }

    /// <summary>Host: a friend's trigger reached this PC past the game's threshold for the first time: its value and what their maps give on it.</summary>
    private static void LogTriggerArrival(RemotePad pad)
    {
        int toLog = pad.TriggersToLog;
        pad.TriggersToLog = 0;
        try
        {
            IEnumerable<InputBinding> bindings = (IEnumerable<InputBinding>?)pad.Pad.Controls?.Bindings ?? PadRemapReader.TemplateDefaults();
            string[] actions = PadCheckActions.ByElement(bindings, RewiredBindings.ActionName, RewiredBindings.SkipsAction);
            foreach ((int bit, PadAxis axis, string trigger) in new[] { (1, PadAxis.LeftTrigger, "LT"), (2, PadAxis.RightTrigger, "RT") })
            {
                if ((toLog & bit) != 0)
                {
                    string gives = actions[PadCheck.IndexOf(axis)];
                    ModLog.Info($"Online: {pad.Tag}'s pad: {trigger} reached this PC past {TriggerCheck.Format(PadUsage.AxisThreshold)} for the first time (at most " +
                        $"{TriggerCheck.Format(pad.Played.Peak(axis))} so far); their maps give {(gives.Length > 0 ? gives : "no action")} on it.");
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: the line about {pad.Tag}'s triggers failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// Friend: a host's match is over here: this PC's pad so far, with its triggers' peaks; and when a trigger never reached
    /// the game's threshold while the other passed it, a line saying so, with once a session a notice on screen if it moved and
    /// stopped short (the panel's Controller tab says how to fix it). One that never moved is only in the log: it may just not
    /// have been pulled.
    /// </summary>
    private void FriendMatchOver(LocalPad pad)
    {
        try
        {
            ModLog.Info($"Your pad online so far: {pad.Usage.DescribeWithPeaks()}.");
            if (_triggerNoticeShown || _solo)
            {
                return;
            }

            bool steamInput = PadProfile.IsSteamInput(pad.Joystick);
            if (pad.Usage.TriggerNotice(steamInput) is not { } notice)
            {
                return;
            }

            string peaks = $"trigger peaks LT {TriggerCheck.Format(pad.Usage.Peak(PadAxis.LeftTrigger))} " +
                $"RT {TriggerCheck.Format(pad.Usage.Peak(PadAxis.RightTrigger))}{(steamInput ? "; the pad is set up by Steam Input" : string.Empty)}";
            if (!pad.Usage.TriggerStoppedShort)
            {
                ModLog.Info($"Your pad: {notice} (not on screen: it may just not have been pulled; {peaks}).");
                return;
            }

            _triggerNoticeShown = true;
            ModLog.Warn($"Your pad: {notice} (shown on screen once this session; {peaks}).");
            Notify(NoticeId.TriggerShort);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: the line about your pad at the match's end failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
