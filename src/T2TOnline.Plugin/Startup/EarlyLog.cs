using System;
using System.Runtime.CompilerServices;
using MelonLoader;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// The log of the blocks that keep crash reporting and analytics off (<see cref="EarlyHousekeeping"/>,
/// <see cref="EarlyAnalytics"/>, <see cref="CrashReporting"/>, <see cref="EngineTelemetry"/>, <see cref="Housekeeping"/>).
/// They run on a mixed install too (<see cref="InertPlugin"/>), where T2TOnline.Core may be another build, so the session
/// log (which uses it) can't be: there the lines go to MelonLoader's log through the plugin's own logger. Otherwise they go
/// to the session log, as the rest of the mod's. Each sink is called from a method of its own, compiled only when called,
/// so a line on a mixed install never loads the session log's code. Never throws.
/// </summary>
internal static class EarlyLog
{
    private static MelonLogger.Instance? _melon;

    /// <summary>On a mixed install: the plugin's MelonLoader logger, from <see cref="InertPlugin"/>, before anything logs.</summary>
    public static void UseMelonLoader(MelonLogger.Instance logger) => _melon = logger;

    /// <summary>Informational message.</summary>
    public static void Info(string message)
    {
        try
        {
            if (InstallCheck.Inert)
            {
                ToMelon(message, warn: false);
            }
            else
            {
                SessionInfo(message);
            }
        }
        catch (Exception)
        {
            // A log line must never stop a block that keeps crash reporting off.
        }
    }

    /// <summary>Something unexpected that was handled.</summary>
    public static void Warn(string message)
    {
        try
        {
            if (InstallCheck.Inert)
            {
                ToMelon(message, warn: true);
            }
            else
            {
                SessionWarn(message);
            }
        }
        catch (Exception)
        {
            // As above.
        }
    }

    /// <summary>A failure, with the exception if there is one (on a mixed install, its type only: no scrubber runs there).</summary>
    public static void Error(string message, Exception? exception = null)
    {
        try
        {
            if (InstallCheck.Inert)
            {
                ToMelon(exception == null ? message : $"{message} ({exception.GetType().Name})", warn: true);
            }
            else
            {
                SessionError(message, exception);
            }
        }
        catch (Exception)
        {
            // As above.
        }
    }

    /// <summary>
    /// <paramref name="exception"/> for a line here (D127): through <see cref="ModLog.SafeErrorText"/> (type and message,
    /// folders labelled, one line) when the session log is in use; on a mixed install its type only, as
    /// <see cref="Error"/> writes it (no scrubber runs there, and an exception's message can hold a folder). Never throws.
    /// </summary>
    public static string ErrorText(Exception? exception)
    {
        if (exception == null)
        {
            return "no exception";
        }

        try
        {
            return InstallCheck.Inert ? exception.GetType().Name : SessionErrorText(exception);
        }
        catch (Exception)
        {
            return exception.GetType().Name;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string SessionErrorText(Exception exception) => ModLog.SafeErrorText(exception);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ToMelon(string message, bool warn)
    {
        if (_melon is { } melon)
        {
            if (warn)
            {
                melon.Warning(message);
            }
            else
            {
                melon.Msg(message);
            }
        }
        else if (warn)
        {
            MelonLogger.Warning(message);
        }
        else
        {
            MelonLogger.Msg(message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SessionInfo(string message) => ModLog.Info(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SessionWarn(string message) => ModLog.Warn(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SessionError(string message, Exception? exception) => ModLog.Error(message, exception);
}
