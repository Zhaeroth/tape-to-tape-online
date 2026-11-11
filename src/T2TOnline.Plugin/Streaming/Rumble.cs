#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppControllerVibration;
using Il2CppControllerVibration.Data;
using Il2CppRewired;
using Il2CppSettings;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Host side: the game's controller vibration for a friend's skater, sent to that friend's PC. Every
/// vibration the game makes goes through <c>ControllerVibrationManager.VibrateController(rewiredPlayerId, nudgeType)</c>
/// (its event handler's nudges for a pokecheck, a knockout, a stagger, a blocked punch, a pass sent or received, a shot),
/// which asks <c>Player.SetVibration</c> of that Rewired player's joysticks if this PC's Controller rumble setting is on.
/// For a Rewired player a friend's virtual pad or keyboard drives, the prefix records the nudge into the match stream
/// (<see cref="MessageType.Rumble"/>, realtime) and skips the game's call: the friend's own PC plays it on their own pad,
/// under their own setting, and a virtual controller has nothing to vibrate. The host's own players run the game's code
/// untouched (counted only).
/// </summary>
internal sealed class RumbleRecorder
{
    private static RumbleRecorder? _active;
    private static bool _installed;
    private static int _prefixFailures;

    private readonly StreamRecorder _recorder;
    private readonly RumbleTally _match = new();
    private readonly RumbleTally _session = new();
    private MatchStreamHost? _sessionNetwork;
    private bool _firstLogged;
    private int _ownPlayers;
    private int _failures;

    public RumbleRecorder(StreamRecorder recorder) => _recorder = recorder;

    /// <summary>
    /// Patches <c>ControllerVibrationManager.VibrateController</c> (once). The patch skips the game's vibration of a friend's
    /// virtual controller at any time and records it while a recording runs; anything else goes through.
    /// </summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return true;
        }

        MethodInfo? target = AccessTools.Method(typeof(ControllerVibrationManager), nameof(ControllerVibrationManager.VibrateController), new[] { typeof(int), typeof(NudgeType) });
        if (target == null)
        {
            ModLog.Warn("Rumble recorder: ControllerVibrationManager.VibrateController(int, NudgeType) not found; friends' pads won't vibrate (this PC's own pads vibrate as the game decides).");
            return false;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(typeof(RumbleRecorder).GetMethod(nameof(VibratePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        _installed = true;
        return true;
    }

    /// <summary>
    /// Starts counting a recording's rumbles. <paramref name="network"/> is the session's stream to friends (null for a
    /// recording to a file only): a new one starts the session's totals again.
    /// </summary>
    public void Start(MatchStreamHost? network)
    {
        _match.Clear();
        _ownPlayers = _failures = 0;
        if (network != null && !ReferenceEquals(network, _sessionNetwork))
        {
            _sessionNetwork = network;
            _session.Clear();
            _firstLogged = false;
        }

        _active = this;
    }

    /// <summary>Stops counting and logs the recording's rumbles, with the session's so far.</summary>
    public void Stop()
    {
        if (_active == this)
        {
            _active = null;
        }

        string session = string.Empty;
        if (_sessionNetwork != null && _recorder.Streaming)
        {
            _session.AddFrom(_match);
            session = $"; this session {_session.Total} ({_session.Describe()})";
        }

        ModLog.Info($"Rumble recorder: {_match.Total} rumble(s) for friends this match{(_match.Total > 0 ? $" ({_match.Describe()})" : string.Empty)}, " +
            $"{_ownPlayers} for this PC's own players (the game's own, not sent){session}{(_failures > 0 ? $"; {_failures} couldn't be recorded" : string.Empty)}.");
    }

    /// <summary>
    /// Runs inside the game's call: never throws. False (the game's vibration skipped) for a friend's virtual controller;
    /// true otherwise, and whenever something fails (the game's call can't vibrate a virtual controller anyway).
    /// </summary>
    private static bool VibratePrefix(int rewiredPlayerId, NudgeType nudgeType)
    {
        try
        {
            if (!VirtualPadCompat.IsVirtualPlayer(rewiredPlayerId))
            {
                if (_active != null)
                {
                    _active._ownPlayers++;
                }

                return true;
            }

            _active?.Record(rewiredPlayerId, nudgeType);
            return false;
        }
        catch (Exception ex)
        {
            if (_prefixFailures++ < 3)
            {
                ModLog.Warn($"Rumble recorder: Rewired player {rewiredPlayerId}'s {nudgeType} couldn't be handled ({ModLog.SafeErrorText(ex)}); the game's own call goes ahead.");
            }

            return true;
        }
    }

    private void Record(int rewiredPlayerId, NudgeType nudgeType)
    {
        var rumble = new RumbleMessage((byte)Math.Clamp(rewiredPlayerId, 0, byte.MaxValue), (byte)Math.Clamp((int)nudgeType, 0, byte.MaxValue));
        if (!_recorder.Recording || !rumble.IsValid)
        {
            _failures += rumble.IsValid ? 0 : 1;
            return;
        }

        _recorder.Write(MessageType.Rumble, Channel.Realtime, rumble.Write);
        _match.Add(rewiredPlayerId);
        if (!_firstLogged)
        {
            _firstLogged = true;
            ModLog.Info($"Rumble recorder: the game vibrated a friend's controller (Rewired player {rewiredPlayerId}, {nudgeType}); it goes to their PC in the stream " +
                "(realtime) and plays on their own pad there, and the game's own vibration of that virtual controller is skipped. This PC's own pads vibrate as the game decides.");
        }
    }
}

/// <summary>
/// Friend side: the host's game vibrated this PC's player (<see cref="MessageType.Rumble"/>), so this PC's own pad plays the
/// game's own nudge, as <c>ControllerVibrator.Vibrate</c> does for a couch player's joystick: this PC's own
/// <c>NudgeData</c> of that type (its own <c>ControllerVibrationManager</c>), and for each of its motors
/// <c>Joystick.SetVibration(motor, intensity, duration, false)</c> on the pad the session holds. Gated by this PC's own
/// Controller rumble setting (the host's decides only for the host's pads), and limited (<see cref="RumbleLimiter"/>). A
/// keyboard player has nothing to vibrate, as on the couch. Records for other players are counted.
/// </summary>
internal sealed class RumbleChannel : IPresentationChannel
{
    private const double ManagerRetrySeconds = 5;

    private static readonly MessageType[] HandledTypes = { MessageType.Rumble };

    // The game's six nudges, read once from this PC's own data (assets: they don't change while the game runs).
    private static Nudge?[]? _nudges;
    private static double _nextManagerTry;

    private readonly RumbleLimiter _limiter = new();
    private readonly RumbleCounts _match = new();
    private readonly RumbleCounts _session = new();
    private object? _sessionKey;
    private Joystick? _vibrated;
    private bool _firstPlayedLogged;
    private bool _settingOffLogged;
    private bool _noNudgesLogged;
    private int _failures;

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Handles => HandledTypes;

    /// <inheritdoc />
    public void Begin(IStreamSource source, IReadOnlyDictionary<ushort, ReplayObject> objects)
    {
        _match.Clear();
        _limiter.Reset();
        _vibrated = null;
        _failures = 0;
        // A friend session's playbacks share one stream client (a rejoin keeps it); a recording is a session of its own.
        object key = source is LiveStreamSource live ? live.Client : source;
        if (!ReferenceEquals(key, _sessionKey))
        {
            _sessionKey = key;
            _session.Clear();
            _firstPlayedLogged = _settingOffLogged = false;
        }
    }

    /// <inheritdoc />
    public void Deliver(in TimedMessage message)
    {
        var reader = new BitReader(message.Payload);
        if (!RumbleMessage.TryRead(ref reader, out RumbleMessage rumble))
        {
            _match.Add(RumbleOutcome.Failed);
            return;
        }

        if (rumble.RewiredPlayer != PlayerProxies.YourRewired)
        {
            _match.Add(RumbleOutcome.ForOthers);
            return;
        }

        try
        {
            _match.Add(Play(rumble));
        }
        catch (Exception ex)
        {
            _match.Add(RumbleOutcome.Failed);
            if (_failures++ < 3)
            {
                ModLog.Warn($"Rumble channel: a {(NudgeType)rumble.Nudge} couldn't be played on your pad ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    /// <inheritdoc />
    public void End(bool matchLoaded)
    {
        if (_vibrated != null)
        {
            try
            {
                _vibrated.StopVibration();
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Rumble channel: your pad's vibration couldn't be stopped ({ModLog.SafeErrorText(ex)}); it ends with the nudge.");
            }

            _vibrated = null;
        }

        if (_match.Total == 0)
        {
            return;
        }

        _session.AddFrom(_match);
        ModLog.Info($"Rumble channel: {_match.Describe(PlayerProxies.YourRewired)}; this session {_session[RumbleOutcome.Played]} played, {_session.Limited} limited.");
    }

    /// <inheritdoc />
    public void Finish()
    {
    }

    private RumbleOutcome Play(RumbleMessage rumble)
    {
        if (LocalPad.Held is not LocalPad pad)
        {
            return RumbleOutcome.NoPad;
        }

        Joystick joystick = pad.Joystick;
        if (!joystick.isConnected)
        {
            return RumbleOutcome.Unplugged;
        }

        GameplaySettings? settings = GameplaySettings.Instance;
        if (settings == null || !settings.ControllerRumble)
        {
            if (!_settingOffLogged)
            {
                _settingOffLogged = true;
                ModLog.Info($"Rumble: your own Controller rumble setting is {(settings == null ? "not loaded" : "off")}, so your pad stays still " +
                    "(the host's setting only decides for the host's own pads). Counted.");
            }

            return RumbleOutcome.SettingOff;
        }

        if (Nudges() is not Nudge?[] nudges || nudges[rumble.Nudge] is not Nudge nudge)
        {
            return RumbleOutcome.NoNudgeData;
        }

        if (!joystick.supportsVibration)
        {
            return RumbleOutcome.NoVibration;
        }

        switch (_limiter.Decide(FrameClock.Micros, nudge.Duration))
        {
            case RumbleVerdict.TooMany:
                return RumbleOutcome.TooMany;
            case RumbleVerdict.TooLong:
                return RumbleOutcome.TooLong;
        }

        // As ControllerVibrator.Vibrate: each of the nudge's motors at its intensity for its duration, others left as they are.
        foreach (int motor in nudge.Motors)
        {
            joystick.SetVibration(motor, nudge.Intensity, nudge.Duration, false);
        }

        _vibrated = joystick;
        if (!_firstPlayedLogged)
        {
            _firstPlayedLogged = true;
            ModLog.Info($"Rumble: your pad \"{SafeText.Clean(joystick.name, 48)}\" vibrates with your skater as on the couch (your own Controller rumble setting is on; " +
                $"{joystick.vibrationMotorCount} motor(s); the game's nudges here: {Describe(nudges)}).");
        }

        return RumbleOutcome.Played;
    }

    /// <summary>
    /// The game's six nudges from this PC's own <c>ControllerVibrationManager</c> (it lives for the whole game), read once;
    /// null while it can't be found (asked again at most every few seconds, logged once).
    /// </summary>
    private Nudge?[]? Nudges()
    {
        if (_nudges != null || FrameClock.Seconds < _nextManagerTry)
        {
            return _nudges;
        }

        _nextManagerTry = FrameClock.Seconds + ManagerRetrySeconds;
        ControllerVibrationManager? manager = UnityEngine.Object.FindObjectOfType<ControllerVibrationManager>();
        ControllerVibrator? vibrator = manager?._controllerVibrator;
        if (vibrator == null)
        {
            if (!_noNudgesLogged)
            {
                _noNudgesLogged = true;
                ModLog.Warn("Rumble: this game's ControllerVibrationManager isn't there yet, so its nudges can't be read; your pad stays still until it is (asked again every few seconds).");
            }

            return null;
        }

        var nudges = new Nudge?[RumbleMessage.MaxNudge + 1];
        for (int type = 0; type < nudges.Length; type++)
        {
            NudgeData? data = vibrator.GetNudgeData((NudgeType)type);
            if (data == null)
            {
                continue;
            }

            var motors = new List<int>();
            Il2CppSystem.Collections.Generic.List<MotorType>? list = data.motors;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                motors.Add((int)list[i]);
            }

            nudges[type] = new Nudge(data.duration, data.intensity, motors.ToArray());
        }

        _nudges = nudges;
        return nudges;
    }

    private static string Describe(Nudge?[] nudges) => string.Join("; ", nudges.Select((n, type) => n is Nudge nudge
        ? string.Create(CultureInfo.InvariantCulture, $"{(NudgeType)type} {nudge.Duration:0.00} s at {nudge.Intensity:0.00} on motor(s) {string.Join(", ", nudge.Motors)}")
        : $"{(NudgeType)type} not found"));

    /// <summary>One of the game's nudges: how long, how strong, and which motors (Rewired's motor index is the game's MotorType).</summary>
    private sealed record Nudge(float Duration, float Intensity, int[] Motors);
}
#endif
