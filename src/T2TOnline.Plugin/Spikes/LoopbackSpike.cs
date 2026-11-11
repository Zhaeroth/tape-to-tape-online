#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Il2CppControllerConnection;
using Il2CppInterop.Runtime;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using Player = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Spikes;

/// <summary>
/// M0.2: your real gamepad drives a virtual pad through the whole remote-input path (<see cref="PadLoopback"/>), and
/// the game sees only the virtual pad. Toggled with F8. If the join screen, menus, skating and brawls all work this
/// way, a friend's pad will work the same way once the link is a real network.
/// </summary>
internal sealed class LoopbackSpike
{
    private const int HoldsLoggedIndividually = 40;

    private readonly int _delayMs;
    private readonly SampleWindow _holdErrorMs = new();
    private HoldMatcher _holds = new();
    private LocalPad? _local;
    private VirtualPad? _pad;
    private PadLoopback? _loop;
    private Il2CppSystem.Action? _inputHook;
    private int _holdsLogged;
    private bool _joined;
    private bool _faulted;

    /// <summary>Creates the spike; <paramref name="delayMs"/> is the simulated one-way link delay.</summary>
    public LoopbackSpike(int delayMs) => _delayMs = Math.Clamp(delayMs, 0, 1000);

    /// <summary>True while the loopback is running.</summary>
    public bool Active => _pad != null;

    /// <summary>Starts the loopback, or stops it if it's running.</summary>
    public void Toggle()
    {
        if (Active)
        {
            Stop("F8 pressed");
        }
        else
        {
            Start();
        }
    }

    /// <summary>Per-frame housekeeping: joining through the game, faults, a pad being unplugged.</summary>
    public void Update()
    {
        if (!Active)
        {
            return;
        }

        if (_faulted)
        {
            Stop("an error in the input path");
            return;
        }

        if (!_local!.IsConnected)
        {
            Stop("the gamepad was disconnected");
            return;
        }

        if (!_joined)
        {
            int gamePlayer = GameJoin.TryRelay(_pad!, "Loopback");
            if (gamePlayer >= 0)
            {
                _joined = true;
                ModLog.Info($"Loopback: the virtual pad is game player {gamePlayer}.");
            }
        }
    }

    /// <summary>Stops the loopback, logs what it measured, and gives the real pad back to the game.</summary>
    public void Stop(string reason)
    {
        if (!Active)
        {
            return;
        }

        if (_inputHook != null)
        {
            ReInput.remove_InputSourceUpdateEvent(_inputHook);
            _inputHook = null;
        }

        PadPlayoutStats playout = _loop!.PlayoutStats;
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Loopback off ({reason}). Sent {_loop.PacketsSent} packets, {_loop.BytesSent} bytes " +
            $"({(_loop.PacketsSent == 0 ? 0 : _loop.BytesSent / (double)_loop.PacketsSent):0.0} B/packet). Playout: {playout.Played} played, " +
            $"{playout.Missing} missing, {playout.Underruns} underruns, {playout.Skipped} skipped, {playout.Resyncs} resyncs."));
        SampleSummary holds = _holdErrorMs.Summarize();
        ModLog.Info($"Loopback hold times: {holds.Count} presses compared; virtual minus real, |error| mean/p95/max {holds} ms.");

        // The real pad goes to the player the virtual pad was driving, with that player's current control categories,
        // so a joined player keeps playing; any other goes back to "press to join".
        Player owner = _pad!.Player;
        bool joined = RewiredPlayerInputAssigner.Instance != null && RewiredPlayerInputAssigner.GetPlayerId(owner.id) >= 0;
        IReadOnlyCollection<int>? categories = joined ? _pad.CurrentEnabledCategories() : null;
        _pad.Dispose();
        _pad = null;
        _loop = null;
        _local!.GiveBack(owner, categories);
        _local = null;
    }

    private void Start()
    {
        if (Online.CampaignRelay.Current is { Hosting: true })
        {
            // Its virtual pad would look like a friend's to the host's menus (Campaign/HostDecides), which would stop
            // answering this pad.
            ModLog.Warn("Loopback (F8): not while hosting (the menus would take your looped-back pad for a friend's); end the session first (F7).");
            return;
        }

        _faulted = false;
        _joined = false;
        _holdsLogged = 0;
        _holds = new HoldMatcher();
        _holdErrorMs.Clear();

        LocalPad? local = LocalPad.Find();
        if (local == null)
        {
            ModLog.Warn("Loopback: no connected gamepad that Rewired recognizes as a gamepad. Plug one in and press F8 again.");
            return;
        }

        // A player the game has already joined keeps its slot: the virtual pad takes it over with the same control
        // categories on (menus, gameplay...). Otherwise the virtual pad starts in "press to join" mode.
        local.Detach();
        VirtualPad? virtualPad;
        string problem;
        try
        {
            virtualPad = VirtualPad.Create("T2TOnline loopback", local.DeviceType, local.OwnerPlayerId, local.OwnerJoined ? local.OwnerCategories : null, out problem);
        }
        catch (Exception ex)
        {
            virtualPad = null;
            problem = $"{ModLog.SafeErrorText(ex)}";
            ModLog.Error("Loopback: creating the virtual pad threw", ex);
        }

        if (virtualPad == null)
        {
            ModLog.Warn($"Loopback: could not create the virtual pad: {problem}. The real gamepad is untouched.");
            local.GiveBackToOwner();
            return;
        }

        _local = local;
        _pad = virtualPad;
        _loop = new PadLoopback(Stopwatch.Frequency, _delayMs * Stopwatch.Frequency / 1000);
        _inputHook = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnInputSourceUpdate));
        ReInput.add_InputSourceUpdateEvent(_inputHook);
        Joystick pad = local.Joystick;
        ModLog.Info($"Loopback on: \"{pad.name}\" ({pad.hardwareName}) now drives the virtual pad on Rewired player {virtualPad.RewiredPlayerId}" +
            $" through the full input path, {_delayMs} ms simulated delay ({local.Reader.MissingElements} template elements missing)." +
            $" Was on Rewired player {local.OwnerPlayerId}. Press the join button to join; F8 turns it off.");
    }

    /// <summary>Rewired's hook for feeding custom controllers: runs after input sources update, before players read them.</summary>
    private void OnInputSourceUpdate()
    {
        if (_faulted || _pad == null)
        {
            return;
        }

        try
        {
            long now = Stopwatch.GetTimestamp();
            (PadButtons held, float lx, float ly, float rx, float ry, float lt, float rt) = _local!.Read();
            _loop!.Capture(now, held, lx, ly, rx, ry, lt, rt);
            (PadState state, PadButtons taps) = _loop.Update(now);
            _pad.Apply(state, taps);
            _holds.ObserveSent(now, held);
            _holds.ObservePlayed(now, state.Buttons | taps);
            LogHolds();
        }
        catch (Exception ex)
        {
            // Never throw into Rewired. Update() shuts the loopback down on the next frame.
            _faulted = true;
            ModLog.Error("Loopback: the input path failed", ex);
        }
    }

    private void LogHolds()
    {
        foreach (MatchedHold hold in _holds.Matched)
        {
            double errorMs = hold.Error * 1000.0 / Stopwatch.Frequency;
            _holdErrorMs.Add(Math.Abs(errorMs));
            if (_holdsLogged++ < HoldsLoggedIndividually)
            {
                ModLog.Info($"Loopback hold: {hold.Button} held {hold.SentDuration * 1000.0 / Stopwatch.Frequency:0} ms on the real pad, {errorMs:+0;-0;0} ms on the virtual pad.");
            }
        }

        _holds.Matched.Clear();
    }
}
#endif
