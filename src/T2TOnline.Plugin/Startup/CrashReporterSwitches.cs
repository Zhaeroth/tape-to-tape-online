using System;

namespace T2TOnline.Plugin.Startup;

// Plain .NET only: it runs on a mixed install too, where T2TOnline.Core may be another build (InertPlugin). Linked into the
// Core tests, which check it with a fake engine.

/// <summary>
/// The engine calls behind Unity's crash reporter's two switches (<c>CrashReportHandler.enableCaptureExceptions</c> and
/// <c>CrashReportHandler.logBufferSize</c>). The plugin binds them to the engine's own internal calls.
/// </summary>
internal interface ICrashReporterEngine
{
    /// <summary>False when the reporter is known not to exist, so nothing may be called; true when it exists or can't be told.</summary>
    bool ReporterExists { get; }

    /// <summary>The exception-capture switch (0 is off), or null when the engine has no getter for it.</summary>
    byte? ReadCapture();

    /// <summary>Sets the exception-capture switch. False when the engine has no setter for it.</summary>
    bool WriteCapture(byte value);

    /// <summary>How many log lines the reporter keeps for a crash report, or null when the engine has no getter for it.</summary>
    uint? ReadLogBufferSize();

    /// <summary>Sets how many log lines the reporter keeps. False when the engine has no setter for it.</summary>
    bool WriteLogBufferSize(uint value);
}

/// <summary>What <see cref="CrashReporterSwitches.SwitchOff"/> found and did.</summary>
/// <param name="Text">For the log: each switch with what it read before and after, such as
/// "CrashReportHandler.enableCaptureExceptions on -> off, CrashReportHandler.logBufferSize 10 -> 0".</param>
/// <param name="Changed">True when a switch was on and was written.</param>
/// <param name="StillOn">True when a switch is on (or can't be read back) after its write.</param>
/// <param name="NoReporter">True when the reporter doesn't exist, so nothing was read or written.</param>
internal readonly record struct CrashReporterOutcome(string Text, bool Changed, bool StillOn, bool NoReporter);

/// <summary>
/// Switches Unity's crash reporter off: no exception reports (capture off), and no log lines kept for a native crash's
/// report (a buffer of 0). Each switch is read, written only when it's on, and read again; the buffer is only ever
/// written 0 (the engine throws for a size above 50, which through an engine call can't be caught).
/// </summary>
internal static class CrashReporterSwitches
{
    /// <summary>The exception-capture switch's name in the log.</summary>
    public const string CaptureName = "CrashReportHandler.enableCaptureExceptions";

    /// <summary>The log-buffer switch's name in the log.</summary>
    public const string BufferName = "CrashReportHandler.logBufferSize";

    /// <summary>
    /// True when the reporter exists and either switch is on. No allocation: for a frequent check. Throws what the engine
    /// calls throw.
    /// </summary>
    public static bool AnyOn(ICrashReporterEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.ReporterExists)
        {
            return false;
        }

        return engine.ReadCapture() is byte capture && capture != 0
            || engine.ReadLogBufferSize() is uint size && size != 0;
    }

    /// <summary>Switches both off where they're on, and says what it found. Never throws for what the engine calls throw.</summary>
    public static CrashReporterOutcome SwitchOff(ICrashReporterEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        bool exists;
        try
        {
            exists = engine.ReporterExists;
        }
        catch (Exception ex)
        {
            return new CrashReporterOutcome($"{CaptureName} and {BufferName} not touched (the crash reporter couldn't be found: {ex.GetType().Name})", false, false, true);
        }

        if (!exists)
        {
            return new CrashReporterOutcome($"{CaptureName} and {BufferName} not touched (the crash reporter doesn't exist yet)", false, false, true);
        }

        string capture = Capture(engine, out bool captureChanged, out bool captureStillOn);
        string buffer = Buffer(engine, out bool bufferChanged, out bool bufferStillOn);
        return new CrashReporterOutcome($"{capture}, {buffer}", captureChanged || bufferChanged, captureStillOn || bufferStillOn, false);
    }

    private static string Capture(ICrashReporterEngine engine, out bool changed, out bool stillOn)
    {
        changed = false;
        stillOn = false;
        byte? before;
        try
        {
            before = engine.ReadCapture();
        }
        catch (Exception ex)
        {
            return $"{CaptureName} unavailable ({ex.GetType().Name})";
        }

        if (before is not byte on)
        {
            return $"{CaptureName} unavailable";
        }

        if (on == 0)
        {
            return $"{CaptureName} off";
        }

        string problem;
        try
        {
            problem = engine.WriteCapture(0) ? string.Empty : " (no setter in this engine)";
            changed = problem.Length == 0;
        }
        catch (Exception ex)
        {
            problem = $" (couldn't set it: {ex.GetType().Name})";
        }

        string after;
        try
        {
            byte? read = engine.ReadCapture();
            after = read is byte value ? (value != 0 ? "on" : "off") : "unavailable";
        }
        catch (Exception ex)
        {
            after = $"unavailable ({ex.GetType().Name})";
        }

        stillOn = after != "off";
        return $"{CaptureName} on -> {after}{problem}";
    }

    private static string Buffer(ICrashReporterEngine engine, out bool changed, out bool stillOn)
    {
        changed = false;
        stillOn = false;
        uint? before;
        try
        {
            before = engine.ReadLogBufferSize();
        }
        catch (Exception ex)
        {
            return $"{BufferName} unavailable ({ex.GetType().Name})";
        }

        if (before is not uint size)
        {
            return $"{BufferName} unavailable";
        }

        if (size == 0)
        {
            return $"{BufferName} 0";
        }

        string problem;
        try
        {
            problem = engine.WriteLogBufferSize(0) ? string.Empty : " (no setter in this engine)";
            changed = problem.Length == 0;
        }
        catch (Exception ex)
        {
            problem = $" (couldn't set it: {ex.GetType().Name})";
        }

        string after;
        try
        {
            after = engine.ReadLogBufferSize() is uint value ? Format(value) : "unavailable";
        }
        catch (Exception ex)
        {
            after = $"unavailable ({ex.GetType().Name})";
        }

        stillOn = after != "0";
        return $"{BufferName} {Format(size)} -> {after}{problem}";
    }

    private static string Format(uint value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
