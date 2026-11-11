using System;
using System.Runtime.InteropServices;
using System.Threading;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// Steam's networking debug output (<c>ISteamNetworkingUtils::SetDebugOutputFunction</c>), for a developer's check: while
/// it's on, every line Steam's networking prints at the level asked for or coarser goes to one handler. Steam calls the
/// function from whichever thread prints the line (its own service thread too, holding its own lock), so the handler must
/// be quick, thread-safe, and touch neither Steam nor Unity. One function for the whole process, kept alive in a static field:
/// Steam may still call it just after it was unregistered, and then no handler is set.
/// <para>
/// Only <see cref="Start"/> and <see cref="Stop"/> call Steam (from the game's thread, like the rest of this library); they
/// never throw and return false when Steam or the export isn't there. Nothing a handler throws reaches Steam.
/// </para>
/// </summary>
public static class SteamDebugOutput
{
    // The one function Steam is given, alive for the life of the process.
    private static readonly OutputFunction Function = Deliver;
    private static readonly nint FunctionPointer = Marshal.GetFunctionPointerForDelegate(Function);

    private static Action<SteamNetworkingDebugOutputType, string>? _handler;
    private static long _faults;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputFunction(SteamNetworkingDebugOutputType type, nint message);

    /// <summary>How many lines a handler threw on (each swallowed, so Steam never sees it).</summary>
    public static long Faults => Interlocked.Read(ref _faults);

    /// <summary>
    /// Registers the function at <paramref name="detailLevel"/> with <paramref name="handler"/> getting each line (Steam's
    /// thread or the game's; see the class). False when Steam isn't available or its library lacks the export: nothing is
    /// registered then. A second call replaces the handler and the level.
    /// </summary>
    public static bool Start(SteamNetworkingDebugOutputType detailLevel, Action<SteamNetworkingDebugOutputType, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (detailLevel <= SteamNetworkingDebugOutputType.None)
        {
            return false;
        }

        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.SetDebugOutputFunction));
        if (self == 0)
        {
            return false;
        }

        Volatile.Write(ref _handler, handler);
        try
        {
            SteamNative.SetDebugOutputFunction(self, detailLevel, FunctionPointer);
            return true;
        }
        catch (Exception e)
        {
            Volatile.Write(ref _handler, null);
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// Unregisters the function (Steam's own default: no function) and drops the handler first, so a line Steam prints
    /// meanwhile goes nowhere. False when Steam couldn't be told (the handler is dropped all the same).
    /// </summary>
    public static bool Stop()
    {
        Volatile.Write(ref _handler, null);
        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.SetDebugOutputFunction));
        if (self == 0)
        {
            return false;
        }

        try
        {
            SteamNative.SetDebugOutputFunction(self, SteamNetworkingDebugOutputType.None, 0);
            return true;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>What Steam calls with each line (a UTF-8 C string): to the handler, if one is set. Never throws.</summary>
    internal static void Deliver(SteamNetworkingDebugOutputType type, nint message)
    {
        try
        {
            Action<SteamNetworkingDebugOutputType, string>? handler = Volatile.Read(ref _handler);
            if (handler == null || message == 0)
            {
                return;
            }

            string? text = Marshal.PtrToStringUTF8(message);
            if (text != null)
            {
                handler(type, text);
            }
        }
        catch (Exception)
        {
            // Nothing may unwind into Steam's own code.
            Interlocked.Increment(ref _faults);
        }
    }

    /// <summary>For tests: the handler as <see cref="Start"/> would set it, without Steam.</summary>
    internal static void SetHandlerForTest(Action<SteamNetworkingDebugOutputType, string>? handler) => Volatile.Write(ref _handler, handler);
}
