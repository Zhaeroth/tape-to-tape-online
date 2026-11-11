#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Unity's own engine telemetry: the Analytics module (with its device stats), the UGS analytics switch and performance
/// reporting. The player starts them natively from its build settings, before any managed code runs, so
/// <see cref="EarlyHousekeeping"/> (which stops the managed services) can't reach them. This reads them as soon as the
/// interop assemblies are up, switches them off, and logs both; <see cref="Recheck"/> repeats that every 30 s in case the
/// engine switches one back on (a remote config fetch can).
/// <para>
/// They're called as the engine's own icalls, through delegates typed with <c>byte</c>, not through the interop's
/// properties. The engine returns a C++ bool in AL alone, and the interop restores these stripped properties with
/// delegates typed <c>bool</c>, which marshal a four-byte BOOL and so read whatever the upper bytes of EAX held
/// (disassembled: the false paths are <c>xor al, al</c> after a pointer load). So every switch read "on" after
/// being set off, and <c>Analytics.IsInitialized</c> always read true, which would let the interop's setters run against
/// a missing analytics manager and crash. Here the Analytics switches are read and set only once <c>IsInitialized</c>
/// (read as a byte) says the manager exists, as Unity's own properties intend. An icall the engine was built without is
/// logged as unavailable: there's nothing to switch off.
/// </para>
/// <para>
/// Unity's crash reporter is switched off first, by <see cref="CrashReporting"/> (its two switches lead this line), which
/// also switches it off again at the BeforeSceneLoad callbacks and watches it once a second instead of every 30 s.
/// Analytics' initialize-on-startup is also held at Unity's BeforeSceneLoad callbacks (<see cref="EarlyAnalytics"/>), which
/// in practice run after this. The session state logged here is the engine's session tracker; whether analytics
/// actually ran shows in its local files (<see cref="AnalyticsFiles"/>).
/// </para>
/// </summary>
internal static class EngineTelemetry
{
    private const string AnalyticsIcall = "UnityEngine.Analytics.Analytics::";
    private const string On = "on";
    private const string Off = "off";

    private static readonly (string Name, string Getter, string? Setter, bool NeedsAnalytics)[] Switches =
    {
        ("Analytics.enabled", AnalyticsIcall + "get_enabledInternal", AnalyticsIcall + "set_enabledInternal", true),
        ("Analytics.initializeOnStartup", AnalyticsIcall + "get_initializeOnStartupInternal", AnalyticsIcall + "set_initializeOnStartupInternal", true),
        ("Analytics.playerOptedOut", AnalyticsIcall + "get_playerOptedOutInternal", null, true),
        ("Analytics.deviceStatsEnabled", AnalyticsIcall + "get_deviceStatsEnabledInternal", AnalyticsIcall + "set_deviceStatsEnabledInternal", true),
        ("AnalyticsCommon.ugsAnalyticsEnabled", "UnityEngine.Analytics.AnalyticsCommon::get_ugsAnalyticsEnabledInternal",
            "UnityEngine.Analytics.AnalyticsCommon::set_ugsAnalyticsEnabledInternal", false),
        ("PerformanceReporting.enabled", "UnityEngine.Analytics.PerformanceReporting::get_enabled",
            "UnityEngine.Analytics.PerformanceReporting::set_enabled", false),
    };

    // Resolved icalls (null: the engine has none by that name). The delegates live here so they're never collected.
    private static readonly Dictionary<string, Delegate?> Icalls = new();

    private static string _stillOn = string.Empty;
    private static int _rechecks;

    private delegate byte ReadByte();

    private delegate void WriteByte(byte value);

    private delegate int ReadInt();

    /// <summary>
    /// Switches still on are switched off again; the log says so only when the set still on changes (and when they're
    /// all off at last). Cheap: a few engine calls. Never throws.
    /// </summary>
    public static void Recheck()
    {
        try
        {
            _rechecks++;
            var stillOn = new List<string>();
            foreach (var entry in Switches)
            {
                if (entry.Setter == null || Read(entry.Getter, entry.NeedsAnalytics) != On)
                {
                    continue;
                }

                SetOff(entry.Setter);
                if (Read(entry.Getter, entry.NeedsAnalytics) == On)
                {
                    stillOn.Add(entry.Name);
                }
            }

            string now = string.Join(", ", stillOn);
            if (now == _stillOn)
            {
                return;
            }

            _stillOn = now;
            if (stillOn.Count == 0)
            {
                EarlyLog.Info($"Housekeeping: Unity's engine telemetry is off now (recheck {_rechecks}).");
            }
            else
            {
                EarlyLog.Warn($"Housekeeping: Unity's engine telemetry still on at recheck {_rechecks}: {now}.");
            }
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping: engine telemetry recheck failed: {EarlyLog.ErrorText(ex)}");
        }
    }

    /// <summary>
    /// Logs the engine's telemetry switches, turns Unity's crash reporter (first: <see cref="CrashReporting"/>), analytics
    /// (and its initialize-on-startup), device stats, UGS analytics and performance reporting off, and logs the result with
    /// the analytics session's state.
    /// </summary>
    public static void SwitchOff()
    {
        var report = new List<string> { CrashReporting.SwitchOff() };
        var stillOn = new List<string>();
        foreach (var entry in Switches)
        {
            string before = Read(entry.Getter, entry.NeedsAnalytics);
            if (entry.Setter == null || before != On)
            {
                report.Add($"{entry.Name} {before}");
                continue;
            }

            string problem = SetOff(entry.Setter);
            string after = Read(entry.Getter, entry.NeedsAnalytics);
            report.Add($"{entry.Name} {before} -> {after}{problem}");
            if (after == On)
            {
                stillOn.Add(entry.Name);
            }
        }

        report.Add($"Analytics session {Session()}");
        EarlyLog.Info($"Housekeeping: Unity's engine telemetry: {string.Join(", ", report)}.");
        _stillOn = string.Join(", ", stillOn);
        if (stillOn.Count > 0)
        {
            EarlyLog.Warn($"Housekeeping: Unity's engine telemetry is still on after switching it off: {_stillOn}. " +
                "Tried again every 30 s; the log says when it's off.");
        }
    }

    /// <summary>"on", "off", "not started" (the analytics manager doesn't exist yet) or "unavailable".</summary>
    private static string Read(string getter, bool needsAnalytics)
    {
        try
        {
            if (needsAnalytics)
            {
                if (Icall<ReadByte>(AnalyticsIcall + "IsInitialized") is not { } initialized)
                {
                    return "unavailable";
                }

                if (initialized() == 0)
                {
                    return "not started";
                }
            }

            return Icall<ReadByte>(getter) is { } read ? (read() != 0 ? On : Off) : "unavailable";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// The analytics session's state ("stopped", "started", ...), "not started" (no analytics manager) or "unavailable".
    /// Read only once the manager exists: the icall reads an object made just before it, without checking.
    /// </summary>
    private static string Session()
    {
        try
        {
            if (Icall<ReadByte>(AnalyticsIcall + "IsInitialized") is not { } initialized)
            {
                return "unavailable";
            }

            if (initialized() == 0)
            {
                return "not started";
            }

            return Icall<ReadInt>("UnityEngine.Analytics.AnalyticsSessionInfo::get_sessionState") is { } state
                ? EarlyAnalytics.SessionStateName(state())
                : "unavailable";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>Calls a setter with false; empty, or why it couldn't. Only called after its getter read "on".</summary>
    private static string SetOff(string setter)
    {
        try
        {
            if (Icall<WriteByte>(setter) is not { } write)
            {
                return " (no setter in this engine)";
            }

            write(0);
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $" (couldn't set it: {ex.GetType().Name})";
        }
    }

    private static T? Icall<T>(string name) where T : Delegate
    {
        if (!Icalls.TryGetValue(name, out Delegate? found))
        {
            IntPtr pointer = IL2CPP.il2cpp_resolve_icall(name);
            found = pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
            Icalls[name] = found;
        }

        return found as T;
    }
}
#endif
