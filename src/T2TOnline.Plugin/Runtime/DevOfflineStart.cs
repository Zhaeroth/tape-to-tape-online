#if HAS_INTEROP
using System;
using System.IO;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Runtime;

/// <summary>
/// The developer's offline start (<see cref="OfflineStart"/>): <c>run.txt</c>'s <c>offline reason=steam|relay|error</c>, read at start-up
/// so the check tour it names walks the native screens with online off, or after the runtime stopped on an error. Only with the
/// developer tools on and on a game build the mod patches; the line itself is left for the tour, which deletes it when it reads it.
/// <c>steam</c> and <c>relay</c> take the start-up's own way to online off; <c>error</c> lets the runtime start, and the tour's first step
/// asks for its stop (<see cref="AskStop"/>), which the plugin takes by the path a real error takes. Game thread only. Never throws.
/// </summary>
internal static class DevOfflineStart
{
    private static bool _stopAsked;

    /// <summary>What this start's <c>run.txt</c> asked for (<see cref="OfflineStartReason.None"/> without the developer tools or a line).</summary>
    public static OfflineStartReason Reason { get; private set; }

    /// <summary>True when the reason keeps the runtime from starting (online off from start-up).</summary>
    public static bool StartsShell => OfflineStart.StartsShell(Reason);

    /// <summary>Why online is off when <see cref="StartsShell"/>: "the developer's offline start (steam)".</summary>
    public static string OffReason => OfflineStart.OffReason(Reason);

    /// <summary>What the offline notice advises then (developers only ever see it).</summary>
    public static string Advice => OfflineStart.Advice;

    /// <summary>True when the reason tells the Settings tab's rows that Steam refused relays alone (Direct Connections shows; no marker is written).</summary>
    public static bool RelayRefused => OfflineStart.RelayRefused(Reason);

    /// <summary>
    /// At start-up, once the build is known to be one the mod patches (<paramref name="patchable"/>): the first line of the tours' <c>run.txt</c>,
    /// with the developer tools on; nothing is read otherwise. Not logged here: the start-up says what came of it.
    /// </summary>
    public static void Read(bool patchable)
    {
        Reason = OfflineStartReason.None;
        if (!patchable || !ModSettings.DevTools)
        {
            return;
        }

        try
        {
            string file = Path.Combine(DevOutput.ToursFolder, "run.txt");
            if (File.Exists(file))
            {
                Reason = OfflineStart.FromRunLine(File.ReadLines(file).FirstOrDefault(), UiCheckPlan.Tours);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ModLog.Warn($"Online: the check tours' run.txt couldn't be read at start-up ({ex.GetType().Name}); no developer's offline start.");
        }
    }

    /// <summary>
    /// The start-up's line once its checks have decided: <paramref name="onlineOn"/> online is on (only <c>error</c> leaves it so), else
    /// <paramref name="offReason"/> is why it is off (the offline start's own reason when it took the start-up's way).
    /// </summary>
    public static void NoteStart(bool onlineOn, string offReason)
    {
        if (Reason == OfflineStartReason.None)
        {
            return;
        }

        bool ours = onlineOn ? Reason == OfflineStartReason.Error : StartsShell && offReason == OfflineStart.OffReason(Reason);
        ModLog.Info(ours ? OfflineStart.StartLine(Reason) : OfflineStart.IgnoredLine(Reason, onlineOn ? "online is on" : offReason));
        if (!ours)
        {
            Reason = OfflineStartReason.None;
        }
    }

    /// <summary>
    /// The tour's step for <c>error</c>: the runtime's next update throws (<see cref="ThrowIfStopAsked"/>), and the plugin stops the runtime as
    /// after any error. Only while a check tour holds, and only for <c>error</c>; false otherwise.
    /// </summary>
    public static bool AskStop()
    {
        if (Reason != OfflineStartReason.Error || !TourHolds.On)
        {
            return false;
        }

        _stopAsked = true;
        ModLog.Info(OfflineStart.StopLine);
        return true;
    }

    /// <summary>From the plugin's guard around the runtime's update: the stop asked for, as an exception the guard takes. One field read a frame.</summary>
    public static void ThrowIfStopAsked()
    {
        if (!_stopAsked)
        {
            return;
        }

        _stopAsked = false;
        throw new InvalidOperationException(OfflineStart.StopMessage);
    }
}
#endif
