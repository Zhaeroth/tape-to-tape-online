#if HAS_INTEROP
using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Il2CppUltimateReplay;
using Il2CppUltimateReplay.Storage;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Replay;

/// <summary>
/// Which replay scene the game's recorder captures from, and when it last did. Online play depends on it: the host
/// streams that scene's objects (<c>MatchRelay</c>, F3), and <c>MatchSetup</c> knows from it that a match is being
/// recorded. A postfix on <c>ReplayScene.CaptureSnapshot</c> notes only the scene and the time. It's always on and never
/// switches itself off: a failure is logged (the first three, then every thousandth) and the next snapshot is noted as
/// usual. The M0 measurement and capture tools (<see cref="SnapshotProbe"/>) ride on the same postfix when wanted, and
/// their failures stop only them.
/// </summary>
internal static class ReplaySceneTracker
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _installed;
    private static double _lastCaptureAt = -1;
    private static long _failures;

    /// <summary>The scene the recorder captured from most recently (it can change at a faceoff), or null before the first.</summary>
    public static ReplayScene? LastScene { get; private set; }

    /// <summary>Seconds since the game's recorder last captured a snapshot (infinity before the first).</summary>
    public static double SecondsSinceLastSnapshot => _lastCaptureAt < 0 ? double.PositiveInfinity : Clock.Elapsed.TotalSeconds - _lastCaptureAt;

    /// <summary>Seconds on the tracker's clock (the probe's windows and captures use it too).</summary>
    public static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>Patches the recorder (once). Returns false, logging why, if it can't.</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return true;
        }

        MethodInfo? target = AccessTools.Method(typeof(ReplayScene), nameof(ReplayScene.CaptureSnapshot));
        if (target == null)
        {
            ModLog.Warn("Replay scene tracker: ReplayScene.CaptureSnapshot not found; matches can't be streamed.");
            return false;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(typeof(ReplaySceneTracker).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
        _installed = true;
        ModLog.Info("Replay scene tracker: on (notes the scene the game's recorder captures from).");
        return true;
    }

    private static void Postfix(ReplayScene __instance, ReplaySnapshot __result)
    {
        // Inside the game's recorder: never throw, never switch off.
        try
        {
            if (__instance == null)
            {
                return;
            }

            LastScene = __instance;
            _lastCaptureAt = Clock.Elapsed.TotalSeconds;
        }
        catch (Exception ex)
        {
            if (_failures++ < 3 || _failures % 1000 == 0)
            {
                ModLog.Warn($"Replay scene tracker: couldn't note a snapshot ({_failures} so far): {ModLog.SafeErrorText(ex)}");
            }

            return;
        }

        if (__result != null)
        {
            SnapshotProbe.OnSnapshot(__instance, __result);
        }
    }
}
#endif
