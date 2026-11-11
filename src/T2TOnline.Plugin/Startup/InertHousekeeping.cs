using System;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
using T2TOnline.Plugin.Hooks;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// On a mixed install (<see cref="InertPlugin"/>) the plugin is out of MelonLoader's calls, but MelonLoader still runs the game
/// modded, so crash reporting and analytics stay off just as on a whole install: the start-up callbacks are blocked
/// (<see cref="EarlyHousekeeping"/>, whose analytics stand-in also switches Unity's crash reporter off again), and on the
/// first frame Unity's engine telemetry and crash reporter are switched off (<see cref="EngineTelemetry"/>), the
/// housekeeping patches go in with a Harmony instance of their own (the plugin's own was cleared as it left MelonLoader's
/// calls; the end-of-match skip keeps the game's menu working with analytics blocked) and Sentry is checked. Then the crash
/// reporter is watched every second, the rest every 30 s, and switched off once more as the game closes. Everything here is
/// plain .NET, MelonLoader and the IL2CPP runtime (no T2TOnline.Core, which may be another build), and logs to MelonLoader's
/// log (<see cref="EarlyLog"/>). Online play doesn't exist on this path, so a failure is only logged. Never throws.
/// </summary>
internal static class InertHousekeeping
{
    private const long RecheckMs = 30_000;
    private const string HarmonyId = "T2TOnline.Housekeeping";

    // Kept alive for as long as MelonLoader holds the subscriptions.
    private static LemonAction? _onUpdate;
    private static LemonAction? _onQuit;
    private static HarmonyLib.Harmony? _harmony;
    private static bool _started;
    private static bool _updateFailed;
    private static long _nextRecheck = long.MaxValue;

    /// <summary>
    /// From <c>OnPreInitialization</c> (<see cref="InertPlugin.Stop"/>), before Unity runs any game code: the lines go to
    /// MelonLoader's log through <paramref name="plugin"/>'s logger, and the start-up callbacks are blocked. MelonLoader's
    /// update and quit events are taken later, by <see cref="Subscribe"/>. Never throws.
    /// </summary>
    public static void Start(MelonPlugin plugin)
    {
        Step("its logger", () => EarlyLog.UseMelonLoader(plugin.LoggerInstance));
        Step("the start-up blocks", () =>
        {
            if (EarlyHousekeeping.Apply().Count > 0)
            {
                EarlyLog.Warn("Housekeeping (mixed install): part of crash reporting or analytics couldn't be blocked before start-up (see above).");
            }
        });
    }

    /// <summary>
    /// Takes MelonLoader's update and quit events (<see cref="InertPlugin.Stop"/>), only once the plugin is out of
    /// MelonLoader's calls: MelonLoader ties every subscription to the assembly that declares its method, static methods
    /// included, and drops them all when a plugin of that assembly unregisters (<c>MelonAction</c> keeps the method's
    /// assembly; <c>MelonAssembly.UnregisterMelons</c> raises its <c>OnUnregister</c>, which unsubscribes them). Taken
    /// before that, they'd never run. Never throws.
    /// </summary>
    public static void Subscribe()
    {
        Step("its update and quit events", () =>
        {
            _onUpdate = Update;
            _onQuit = Quit;
            MelonEvents.OnUpdate.Subscribe(_onUpdate);
            MelonEvents.OnApplicationQuit.Subscribe(_onQuit);
        });
    }

    // Every frame: the first one switches the rest off; then a tick count compared, the crash reporter read once a second.
    private static void Update()
    {
        if (_updateFailed)
        {
            return;
        }

        try
        {
            if (!_started)
            {
                _started = true;
                FirstFrame();
                _nextRecheck = Environment.TickCount64 + RecheckMs;
            }

            CrashReporting.Watch();
            long now = Environment.TickCount64;
            if (now >= _nextRecheck)
            {
                _nextRecheck = now + RecheckMs;
                Recheck();
            }
        }
        catch (Exception ex)
        {
            _updateFailed = true;
            EarlyLog.Warn($"Housekeeping (mixed install): the checks during the session failed ({ex.GetType().Name}); they're off for this session.");
        }
    }

    // Once Il2CppInterop and Harmony are up (MelonLoader's first update comes after both): as OnInitializeMelon does on a whole install.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FirstFrame()
    {
        Step("Unity's engine telemetry", SwitchTelemetryOff);
        Step("the housekeeping patches", () =>
        {
            Il2CppTypeLocator.Invalidate();
            _harmony = new HarmonyLib.Harmony(HarmonyId);
            if (Housekeeping.Apply(_harmony).Count > 0)
            {
                EarlyLog.Warn("Housekeeping (mixed install): part of the housekeeping patches couldn't go in (see above).");
            }
        });
        Step("the Sentry check", () => Housekeeping.VerifySentryOff());
        Step("the crash handler check", () => Housekeeping.ReportCrashHandlerState(MelonEnvironment.GameRootDirectory));
        EarlyLog.Info("Housekeeping (mixed install): crash reporting and analytics are kept off while the game runs modded; checked every second (Unity's crash reporter) and every 30 s.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SwitchTelemetryOff()
    {
#if HAS_INTEROP
        EngineTelemetry.SwitchOff();
#else
        EarlyLog.Info($"Housekeeping: Unity's engine telemetry: {CrashReporting.SwitchOff()}.");
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Recheck()
    {
#if HAS_INTEROP
        EngineTelemetry.Recheck();
#endif
        Housekeeping.RecheckSentry();
    }

    private static void Quit() => CrashReporting.SwitchOffAtQuit();

    private static void Step(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping (mixed install): {what} failed ({ex.GetType().Name}).");
        }
    }
}
