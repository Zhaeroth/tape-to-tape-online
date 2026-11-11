using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using T2TOnline.Plugin.Hooks;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Always-on safety patches. A modded game must never send crash reports or analytics to the developers:
/// any crash it has may be ours, and they shouldn't spend time on it. Crash reporting and analytics are stopped
/// before they start by <see cref="EarlyHousekeeping"/>; this class adds the Harmony patches that can only be applied
/// once MelonLoader is fully up, and checks that the early block worked.
/// </summary>
internal static class Housekeeping
{
    private static readonly (string Type, string Method, string Why)[] SkippedMethods =
    {
        // Unity Analytics never registers (see EarlyHousekeeping), so every game analytics event would only log a
        // handled exception. Skipping the game's recorder keeps the logs clean.
        ("Tape2Tape.Analytics.UnityAnalyticsService", "RecordEventInternal", "game analytics events"),

        // The end-of-match event reads the analytics service itself, not through RecordEventInternal: unregistered, that
        // throws, and the in-game menu's end-of-match handler (InGameMenu.OnEndOfGameOpenUI, which calls it first) stopped
        // before clearing its two PlayerPrefs keys and opening its menu (seen on the first campaign match).
        ("Tape2Tape.Analytics.UnityAnalyticsService", "LogEndOfGameEvent", "game end-of-match analytics"),

        // The game's leftover beta stats uploader: subscribed to GameplayEvents.EndOfGame in its Awake, it posts a match's
        // stats over plain HTTP to a fixed address. Skipping it has no side effect: it only builds a JSON from objects it
        // makes and starts the upload, and the event's other listeners still run.
        ("Stats.BetaData", "SendData", "the game's leftover match-stats upload"),
    };

    private const int MaxSentryRestartLogs = 5;
    private static bool _sentryRecheckOff;
    private static int _sentryRestarts;

    /// <summary>Applies every housekeeping patch. Returns the names of the ones that could not be applied.</summary>
    public static IReadOnlyList<string> Apply(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        var failed = new List<string>();
        var skip = new HarmonyMethod(typeof(Housekeeping).GetMethod(nameof(SkipOriginal), BindingFlags.NonPublic | BindingFlags.Static));
        foreach ((string typeName, string methodName, string why) in SkippedMethods)
        {
            try
            {
                // Every overload by that name, never a generic one (IL2CPP shares one body between its instantiations).
                Type? type = Il2CppTypeLocator.Find(typeName);
                List<MethodInfo> overloads = type == null
                    ? new List<MethodInfo>()
                    : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == methodName && !m.IsGenericMethodDefinition).ToList();
                if (overloads.Count == 0)
                {
                    failed.Add($"{typeName}.{methodName}");
                    EarlyLog.Warn($"Housekeeping: {why} not found ({typeName}.{methodName}).");
                    continue;
                }

                foreach (MethodInfo method in overloads)
                {
                    harmony.Patch(method, prefix: skip);
                }

                EarlyLog.Info($"Housekeeping: {why} disabled ({overloads.Count} method(s)).");
            }
            catch (Exception ex)
            {
                failed.Add($"{typeName}.{methodName}");
                EarlyLog.Error($"Housekeeping: could not disable {why}", ex);
            }
        }

        return failed;
    }

    /// <summary>
    /// Confirms Sentry isn't running. If it is, the early block missed it: closes it as a second line of defence and
    /// returns a failure, so online mode stays off until the block is fixed.
    /// </summary>
    public static string? VerifySentryOff()
    {
        try
        {
            Type? sdk = Il2CppTypeLocator.Find("Sentry.SentrySdk");
            PropertyInfo? isEnabled = sdk?.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static);
            if (sdk == null || isEnabled == null)
            {
                EarlyLog.Warn("Housekeeping check: Sentry.SentrySdk.IsEnabled not found, so Sentry can't be confirmed off.");
                return "Sentry.SentrySdk.IsEnabled";
            }

            if (isEnabled.GetValue(null) is not true)
            {
                EarlyLog.Info("Housekeeping check: Sentry is not running.");
                return null;
            }

            EarlyLog.Warn("Housekeeping check: Sentry started despite the early block. Closing it now, without sending what it queued.");
            string queue = CloseWithoutSending(sdk);
            EarlyLog.Warn(isEnabled.GetValue(null) is true
                ? $"Housekeeping check: Sentry is still running after Close ({queue})."
                : $"Housekeeping check: Sentry closed ({queue}).");
            return "Sentry started despite the early block";
        }
        catch (Exception ex)
        {
            EarlyLog.Error("Housekeeping check: could not query Sentry", ex);
            return "Sentry check failed";
        }
    }

    /// <summary>
    /// The same check, repeated during the session (every 30 s: one property read), in case something starts Sentry
    /// later. Quiet while Sentry is off. If it's running, closes it without sending what it queued
    /// (<see cref="CloseWithoutSending"/>), logs it as an error and returns why, so online mode can go off. After a failed
    /// check it stops checking.
    /// </summary>
    public static string? RecheckSentry()
    {
        if (_sentryRecheckOff)
        {
            return null;
        }

        try
        {
            Type? sdk = Il2CppTypeLocator.Find("Sentry.SentrySdk");
            PropertyInfo? isEnabled = sdk?.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static);
            if (sdk == null || isEnabled == null)
            {
                // The start-up check already said so (and kept online mode off).
                _sentryRecheckOff = true;
                return null;
            }

            if (isEnabled.GetValue(null) is not true)
            {
                return null;
            }

            _sentryRestarts++;
            string queue = CloseWithoutSending(sdk);
            bool stillRunning = isEnabled.GetValue(null) is true;
            if (_sentryRestarts <= MaxSentryRestartLogs)
            {
                EarlyLog.Error($"Housekeeping re-check: SENTRY CRASH REPORTING WAS RUNNING during the session (time {_sentryRestarts}); " +
                    (stillRunning ? $"it is still running after Close ({queue})." : $"closed it ({queue})."));
            }

            return "Sentry crash reporting started during the session";
        }
        catch (Exception ex)
        {
            _sentryRecheckOff = true;
            EarlyLog.Error("Housekeeping re-check: could not query Sentry; no more re-checks this session", ex);
            return "the Sentry re-check failed";
        }
    }

    /// <summary>
    /// Closes Sentry without sending what it queued. <c>SentrySdk.Close</c> alone would send it: it disposes the hub,
    /// whose <c>Dispose</c> waits for the client to flush for up to the options' shutdown timeout, and the background
    /// worker keeps sending until then. With that timeout set to zero first, the flush returns at once and the worker exits
    /// dropping its queue ("Exiting immediately due to 0 shutdown timeout"; the game's Sentry, disassembled:
    /// <c>Hub.Dispose</c> passes the options' <c>ShutdownTimeout</c> to <c>FlushAsync</c>, and the worker loop checks it
    /// for zero). Only an envelope already in the transport's hands can still go. Returns what happened to the queue, for
    /// the log. Never throws.
    /// </summary>
    private static string CloseWithoutSending(Type sdk)
    {
        string queue;
        try
        {
            object? options = sdk.GetProperty("CurrentOptions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            PropertyInfo? timeout = options?.GetType().GetProperty("ShutdownTimeout", BindingFlags.Public | BindingFlags.Instance);
            if (options == null || timeout is not { CanWrite: true })
            {
                queue = "its queue may have gone out: its shutdown timeout wasn't found";
            }
            else
            {
                timeout.SetValue(options, Zero(timeout.PropertyType));
                queue = Ticks(timeout.GetValue(options)) == 0
                    ? "its queue dropped: the shutdown timeout was set to 0 first"
                    : "its queue may have gone out: its shutdown timeout couldn't be set to 0";
            }
        }
        catch (Exception ex)
        {
            queue = $"its queue may have gone out: its shutdown timeout couldn't be set to 0 ({EarlyLog.ErrorText(ex)})";
        }

        try
        {
            sdk.GetMethod("Close", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            queue += $"; Close failed ({EarlyLog.ErrorText(ex)})";
        }

        return queue;
    }

    /// <summary>A zero <paramref name="type"/> (the interop's TimeSpan): its static Zero, else its default value.</summary>
    private static object? Zero(Type type)
    {
        const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        return type.GetProperty("Zero", Static)?.GetValue(null)
            ?? type.GetField("Zero", Static)?.GetValue(null)
            ?? (type.IsValueType ? Activator.CreateInstance(type) : null);
    }

    /// <summary>A TimeSpan's ticks, read by name (the interop's TimeSpan); -1 when they can't be read.</summary>
    private static long Ticks(object? span) =>
        span?.GetType().GetProperty("Ticks", BindingFlags.Public | BindingFlags.Instance)?.GetValue(span) is long ticks ? ticks : -1;

    /// <summary>
    /// Checks whether the game's own copy of Sentry's native crash handler is running, to confirm the early block
    /// worked. Other programs ship a crashpad_handler.exe too (peripheral and launcher software often runs several), so
    /// only the one in the game folder counts. Returns true when the game's handler is running.
    /// </summary>
    public static bool ReportCrashHandlerState(string gameRoot)
    {
        try
        {
            string gameHandler = Path.GetFullPath(Path.Combine(gameRoot, "crashpad_handler.exe"));
            int ours = 0;
            int others = 0;
            foreach (Process process in Process.GetProcessesByName("crashpad_handler"))
            {
                using (process)
                {
                    if (string.Equals(MainModulePath(process), gameHandler, StringComparison.OrdinalIgnoreCase))
                    {
                        ours++;
                    }
                    else
                    {
                        others++;
                    }
                }
            }

            if (ours == 0)
            {
                EarlyLog.Info($"Housekeeping check: the game's crashpad_handler is not running ({others} from other programs ignored).");
                return false;
            }

            EarlyLog.Warn($"Housekeeping check: the game's crashpad_handler is running ({ours}). Sentry's native SDK started despite the early block.");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Listing processes can fail on Windows with a Win32Exception too (the process list can't be read): the check is then
            // unknown, which the log says, and what follows in the caller (the engine check) still runs.
            EarlyLog.Warn($"Housekeeping check failed: {EarlyLog.ErrorText(ex)}");
            return false;
        }
    }

    /// <summary>A process's executable path, or null when it can't be read (another user's or a protected process).</summary>
    private static string? MainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName is string path ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // The game's own handler runs as the same user, so it's always readable; anything unreadable isn't ours.
            return null;
        }
    }

    /// <summary>Harmony prefix: returning false skips the original; non-void methods return their default value.</summary>
    private static bool SkipOriginal() => false;
}
