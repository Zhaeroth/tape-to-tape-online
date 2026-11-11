using System;
using System.Runtime.InteropServices;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Switches the engine's own analytics (Unity Analytics, native in UnityPlayer.dll) off before it can start: its
/// initialize-on-startup is set to false in Unity's BeforeSceneLoad start-up callbacks, the moment Unity documents for it
/// ("setting this property to false at runtime ... pauses Analytics initialization"). <see cref="EarlyHousekeeping"/>
/// calls <see cref="HoldStartup"/> from the stand-in it gives <c>Ua2CoreInitializeCallback.Register</c>, a BeforeSceneLoad
/// callback; the icalls are resolved through the runtime's own export. In a test run MelonLoader's start
/// (<see cref="EngineTelemetry.SwitchOff"/>) ran about 1.5 s before these callbacks, so this found the switch already off
/// and is the second line; the session state it reads is the engine's session tracker, which read "started" here while
/// every switch was off and the analytics files (<see cref="AnalyticsFiles"/>) stayed unwritten.
/// </summary>
/// <remarks>
/// Read from UnityPlayer.dll (Unity 6000.0.79f1): <c>Analytics::IsInitialized</c> is "the analytics manager exists"
/// (a global pointer compared with zero), and <c>set_initializeOnStartupInternal</c> writes a byte of that manager without
/// checking it exists, so it's called only once <c>IsInitialized</c> (read as a byte) says so; the manager's start-up check
/// initialises it when that byte is set (or when a string it holds isn't empty: not traced, so the log's session state is
/// what confirms the hold), and only <c>ResumeInitialization</c> (never called here) sets the byte again.
/// All three are one-byte C++ bools, so the delegates are typed <c>byte</c> (see <see cref="EngineTelemetry"/> for why not
/// <c>bool</c>). <c>AnalyticsSessionInfo::get_sessionState</c> (an int, from an object made just before the manager) says
/// whether a session had already started. Nothing else is touched: the user-tracking switch, for one, runs engine code
/// when changed. <see cref="EngineTelemetry"/> switches the rest off once MelonLoader is up, and reports this switch again.
/// </remarks>
internal static class EarlyAnalytics
{
    private const string Analytics = "UnityEngine.Analytics.Analytics::";

    private static bool _done;

    private delegate byte ReadByte();

    private delegate void WriteByte(byte value);

    private delegate int ReadInt();

    /// <summary>Sets the engine analytics' initialize-on-startup to false, once, and logs what it found. Never throws.</summary>
    public static void HoldStartup()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        try
        {
            nint isInitialized = Il2CppApi.ResolveIcall(Analytics + "IsInitialized");
            nint get = Il2CppApi.ResolveIcall(Analytics + "get_initializeOnStartupInternal");
            nint set = Il2CppApi.ResolveIcall(Analytics + "set_initializeOnStartupInternal");
            if (isInitialized == 0 || get == 0 || set == 0)
            {
                EarlyLog.Info("Housekeeping: Unity Analytics' initialize-on-startup is unavailable in this engine (no icall), so there's nothing to hold at start-up.");
                return;
            }

            if (Marshal.GetDelegateForFunctionPointer<ReadByte>(isInitialized)() == 0)
            {
                EarlyLog.Warn("Housekeeping: at Unity's start-up callbacks the engine's analytics manager doesn't exist yet, so its initialize-on-startup couldn't be set; " +
                    "the switch-off once MelonLoader is up still runs.");
                return;
            }

            var read = Marshal.GetDelegateForFunctionPointer<ReadByte>(get);
            byte before = read();
            if (before != 0)
            {
                Marshal.GetDelegateForFunctionPointer<WriteByte>(set)(0);
            }

            byte after = read();
            string message = $"Housekeeping: Unity Analytics' initialize-on-startup {OnOff(before)} -> {OnOff(after)} at Unity's start-up callbacks " +
                $"(the engine's session tracker: {Session()}; whether analytics ran shows in its files, checked as the game closes).";
            if (after == 0)
            {
                EarlyLog.Info(message);
            }
            else
            {
                EarlyLog.Warn(message + " It's still on; the switch-off once MelonLoader is up tries again.");
            }
        }
        catch (Exception ex)
        {
            // Called from the engine: nothing may get out.
            EarlyLog.Warn($"Housekeeping: Unity Analytics' initialize-on-startup couldn't be set at start-up ({EarlyLog.ErrorText(ex)}).");
        }
    }

    /// <summary>
    /// The engine's session tracker's state for a log line (<see cref="CrashReporting"/>'s): "stopped", "started", ..., or
    /// why it can't be read. Read only once the analytics manager exists, like <see cref="HoldStartup"/>. Never throws.
    /// </summary>
    internal static string TrackerState()
    {
        try
        {
            nint isInitialized = Il2CppApi.ResolveIcall(Analytics + "IsInitialized");
            if (isInitialized == 0)
            {
                return "unknown (no icall)";
            }

            return Marshal.GetDelegateForFunctionPointer<ReadByte>(isInitialized)() == 0 ? "unknown (no analytics manager yet)" : Session();
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name})";
        }
    }

    /// <summary>The engine's analytics session state ("stopped", "started", ...), or why it can't be read. Only once the manager exists.</summary>
    private static string Session()
    {
        nint state = Il2CppApi.ResolveIcall("UnityEngine.Analytics.AnalyticsSessionInfo::get_sessionState");
        return state == 0 ? "unknown (no icall)" : SessionStateName(Marshal.GetDelegateForFunctionPointer<ReadInt>(state)());
    }

    /// <summary>A session state's name (UnityEngine.Analytics.AnalyticsSessionState).</summary>
    internal static string SessionStateName(int state) => state switch
    {
        0 => "stopped",
        1 => "started",
        2 => "paused",
        3 => "resumed",
        _ => $"in state {state}",
    };

    private static string OnOff(byte value) => value != 0 ? "on" : "off";
}
