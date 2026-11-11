#if HAS_INTEROP
using System;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Startup;

/// <summary>One-off checks of engine features later milestones depend on.</summary>
internal static class EngineChecks
{
    /// <summary>
    /// Client mode turns physics off with <c>Physics.simulationMode</c>, which the game never calls, so its binding may be
    /// stripped. This reads it and sets it to the value it already has: harmless, and proves the call works.
    /// </summary>
    public static void CheckPhysicsSimulationMode()
    {
        try
        {
            SimulationMode mode = Physics.simulationMode;
            Physics.simulationMode = mode;
            ModLog.Info($"Engine check: Physics.simulationMode can be read and set (it is {mode}, left unchanged).");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Engine check: Physics.simulationMode is not usable ({ModLog.SafeErrorText(ex)}). Client mode needs its fallback.");
        }
    }
}
#endif
