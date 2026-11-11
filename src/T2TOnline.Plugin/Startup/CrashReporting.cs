using System;
using System.Runtime.InteropServices;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Switches Unity's own crash reporter (Cloud Diagnostics, native in UnityPlayer.dll) off: no exception reports, and no log
/// lines kept for a native crash's report. The game's build turns it on, and the engine copies that setting back in at each
/// start of its session tracker, which comes between MelonLoader's start and Unity's BeforeSceneLoad callbacks, before the
/// game's first exception. So it's switched off at three moments: once MelonLoader is up (<see cref="SwitchOff"/>, in
/// <see cref="EngineTelemetry"/>'s line), first thing in the BeforeSceneLoad stand-in
/// (<see cref="SwitchOffAtStartupCallbacks"/>) and as the game closes (<see cref="SwitchOffAtQuit"/>); and
/// <see cref="Watch"/> reads the switches once a second, in case a new engine session turns them back on.
/// </summary>
/// <remarks>
/// <para>
/// Read from UnityPlayer.dll (Unity 6000.0.79f1): the reporter is one engine object made at engine start; each of its
/// three paths to the uploader (<c>Uploading Crash Report</c>) checks the capture byte the setter writes, and with that
/// byte at 0 a logged exception does nothing in the reporter at all. The setter writes the byte first and, going from on
/// to off, flushes reports already counted, whose upload checks the same byte. <c>set_logBufferSize</c> throws only for a
/// size above 50; only 0 is ever written. A native crash is Unity's crash handler's (a separate program the player starts
/// before any mod code): it has no switch.
/// </para>
/// <para>
/// Called as the engine's own icalls, resolved through the runtime's export (<see cref="Il2CppApi.ResolveIcall(string)"/>), so it
/// works in the stand-in whether or not Il2CppInterop is up; the delegates live in static fields. The getters read the
/// reporter without checking it exists, so before every call the reporter's global (decoded from the getter's first
/// instruction) is read: while it's null nothing is called. A getter of another shape (another engine) is called as is:
/// Unity's public API, callable whenever scripts run. Nothing here throws.
/// </para>
/// <para>
/// Plain .NET and the IL2CPP runtime only (no T2TOnline.Core): on a mixed install <see cref="InertHousekeeping"/> runs it too,
/// with its lines in MelonLoader's log (<see cref="EarlyLog"/>).
/// </para>
/// </remarks>
internal static class CrashReporting
{
    private const string Handler = "UnityEngine.CrashReportHandler.CrashReportHandler::";
    private const long WatchEveryMs = 1_000;
    private const int MaxBackOnLines = 5;

    private static readonly EngineCalls Engine = new();

    private static bool _armed;
    private static bool _watchOff;
    private static bool _noReporterLogged;
    private static long _nextWatch;
    private static int _backOn;

    private delegate byte ReadByte();

    private delegate void WriteByte(byte value);

    private delegate uint ReadUInt();

    private delegate void WriteUInt(uint value);

    /// <summary>
    /// Switches the reporter off once MelonLoader is up, and returns what it found for <see cref="EngineTelemetry"/>'s line
    /// (a problem gets a line of its own). Never throws.
    /// </summary>
    public static string SwitchOff()
    {
        try
        {
            _armed = true;
            Engine.Resolve();
            CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(Engine);
            WarnIfNotOff(outcome, "once MelonLoader is up");
            return outcome.Text;
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping: Unity's crash reporting couldn't be switched off once MelonLoader is up ({ex.GetType().Name}).");
            return $"{CrashReporterSwitches.CaptureName} unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Switches the reporter off again at Unity's BeforeSceneLoad callbacks (from <see cref="EarlyHousekeeping"/>'s stand-in,
    /// before anything else there), after the engine's session start turned it back on, and logs it. Called from the
    /// engine: never throws.
    /// </summary>
    public static void SwitchOffAtStartupCallbacks()
    {
        try
        {
            _armed = true;
            Engine.Resolve();
            CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(Engine);
            Log(outcome, $"Housekeeping: Unity's crash reporting at Unity's start-up callbacks (the engine's session tracker: {EarlyAnalytics.TrackerState()}): {outcome.Text}.",
                "at Unity's start-up callbacks");
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping: Unity's crash reporting couldn't be switched off at Unity's start-up callbacks ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Every frame (from <c>OnUpdate</c>): once a second, reads the switches (one or two engine calls, no allocation) and
    /// switches them off again if they're back on, logging that its first few times. After a failure it stops, saying so
    /// once. Never throws.
    /// </summary>
    public static void Watch()
    {
        if (!_armed || _watchOff)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextWatch)
        {
            return;
        }

        _nextWatch = now + WatchEveryMs;
        try
        {
            if (!CrashReporterSwitches.AnyOn(Engine))
            {
                return;
            }

            CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(Engine);
            _backOn++;
            if (_backOn > MaxBackOnLines)
            {
                return;
            }

            string line = $"Housekeeping: Unity's crash reporting was back on (time {_backOn}; the engine's session tracker: {EarlyAnalytics.TrackerState()}): " +
                $"{outcome.Text}." + (_backOn == MaxBackOnLines ? " Later times aren't logged this session." : string.Empty);
            Log(outcome, line, "during the session");
        }
        catch (Exception ex)
        {
            _watchOff = true;
            EarlyLog.Warn($"Housekeeping: the check that Unity's crash reporting stays off failed ({ex.GetType().Name}); it's not checked again this session.");
        }
    }

    /// <summary>Switches the reporter off once more as the game closes, before anything of the mod shuts down, and logs it. Never throws.</summary>
    public static void SwitchOffAtQuit()
    {
        try
        {
            Engine.Resolve();
            CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(Engine);
            Log(outcome, $"Housekeeping: Unity's crash reporting as the game closes: {outcome.Text}.", "as the game closes");
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping: Unity's crash reporting couldn't be switched off as the game closes ({ex.GetType().Name}).");
        }
    }

    /// <summary>The line as Info when the reporter is off now; as a warning when it isn't, or when it doesn't exist (once).</summary>
    private static void Log(CrashReporterOutcome outcome, string line, string when)
    {
        if (outcome.NoReporter)
        {
            WarnIfNotOff(outcome, when);
        }
        else if (outcome.StillOn)
        {
            EarlyLog.Warn(line + " It's still on; checked again every second.");
        }
        else
        {
            EarlyLog.Info(line);
        }
    }

    /// <summary>A warning when the reporter is still on, or (once) when it doesn't exist; nothing when it's off.</summary>
    private static void WarnIfNotOff(CrashReporterOutcome outcome, string when)
    {
        if (outcome.NoReporter)
        {
            if (!_noReporterLogged)
            {
                _noReporterLogged = true;
                EarlyLog.Warn($"Housekeeping: Unity's crash reporting {when}: {outcome.Text}; checked again every second.");
            }
        }
        else if (outcome.StillOn)
        {
            EarlyLog.Warn($"Housekeeping: Unity's crash reporting is still on after switching it off {when}: {outcome.Text}. Checked again every second.");
        }
    }

    /// <summary>The reporter's four icalls, resolved once, and the guard on its global.</summary>
    private sealed class EngineCalls : ICrashReporterEngine
    {
        private bool _resolved;
        private ReadByte? _getCapture;
        private WriteByte? _setCapture;
        private ReadUInt? _getBuffer;
        private WriteUInt? _setBuffer;

        // The reporter's global, decoded from a getter (0: not known, so the getters are called as they are), and whether
        // the decoded global can't be read (then the getter itself would fault: nothing is called).
        private nint _global;
        private bool _globalUnreadable;

        /// <inheritdoc />
        public bool ReporterExists => !_globalUnreadable && (_global == 0 || Marshal.ReadIntPtr(_global) != IntPtr.Zero);

        /// <summary>Resolves the icalls and finds the reporter's global, the first time. Throws when the runtime's API can't be called.</summary>
        public void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            nint getCapture = Il2CppApi.ResolveIcall(Handler + "get_enableCaptureExceptions");
            nint setCapture = Il2CppApi.ResolveIcall(Handler + "set_enableCaptureExceptions");
            nint getBuffer = Il2CppApi.ResolveIcall(Handler + "get_logBufferSize");
            nint setBuffer = Il2CppApi.ResolveIcall(Handler + "set_logBufferSize");
            _getCapture = getCapture == 0 ? null : Marshal.GetDelegateForFunctionPointer<ReadByte>(getCapture);
            _setCapture = setCapture == 0 ? null : Marshal.GetDelegateForFunctionPointer<WriteByte>(setCapture);
            _getBuffer = getBuffer == 0 ? null : Marshal.GetDelegateForFunctionPointer<ReadUInt>(getBuffer);
            _setBuffer = setBuffer == 0 ? null : Marshal.GetDelegateForFunctionPointer<WriteUInt>(setBuffer);

            // Both getters load the same global in this engine; either one is enough.
            if (!FindGlobal(getCapture) && !FindGlobal(getBuffer) && (getCapture != 0 || getBuffer != 0))
            {
                EarlyLog.Info("Housekeeping: Unity's crash reporter's getters aren't the shape T2T Online knows (another engine version), so they're called without first checking the reporter exists (Unity's public API).");
            }
        }

        /// <inheritdoc />
        public byte? ReadCapture() => _getCapture?.Invoke();

        /// <inheritdoc />
        public bool WriteCapture(byte value)
        {
            if (_setCapture == null)
            {
                return false;
            }

            _setCapture(value);
            return true;
        }

        /// <inheritdoc />
        public uint? ReadLogBufferSize() => _getBuffer?.Invoke();

        /// <inheritdoc />
        public bool WriteLogBufferSize(uint value)
        {
            if (_setBuffer == null)
            {
                return false;
            }

            _setBuffer(value);
            return true;
        }

        /// <summary>Decodes the global a getter loads first; false when the getter isn't that shape.</summary>
        private bool FindGlobal(nint getter)
        {
            if (getter == 0 || !MemoryAccess.IsReadable(getter, RipRelativeLoad.Length))
            {
                return false;
            }

            byte[] code = new byte[RipRelativeLoad.Length];
            Marshal.Copy(getter, code, 0, code.Length);
            if (!RipRelativeLoad.TryTarget(code, getter, out long target))
            {
                return false;
            }

            if (MemoryAccess.IsReadable((nint)target, IntPtr.Size))
            {
                _global = (nint)target;
            }
            else
            {
                _globalUnreadable = true;
                EarlyLog.Warn("Housekeeping: Unity's crash reporter's global can't be read, so its switches are never called this session.");
            }

            return true;
        }
    }
}
