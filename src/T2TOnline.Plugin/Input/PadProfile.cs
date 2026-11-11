#if HAS_INTEROP
using System;
using System.Globalization;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Which of Rewired's controller profiles a pad was read as (its hardware type), for the logs and the controller check: a
/// pad set up by Steam Input reads as Rewired's "Steam Controller" profile (<see cref="TriggerCheck.SteamInputProfile"/>),
/// whose triggers come only if Steam Input's layout for the game sends them.
/// Reads only; never throws.
/// </summary>
internal static class PadProfile
{
    /// <summary>The profile Rewired read <paramref name="controller"/> as, or <see cref="Guid.Empty"/> when it can't be read.</summary>
    public static Guid Of(Controller controller)
    {
        try
        {
            Il2CppSystem.Guid g = controller.hardwareTypeGuid;
            return new Guid(g._a, g._b, g._c, g._d, g._e, g._f, g._g, g._h, g._i, g._j, g._k);
        }
        catch (Exception)
        {
            return Guid.Empty;
        }
    }

    /// <summary>True when <paramref name="controller"/> is set up by Steam Input (Rewired's "Steam Controller" profile).</summary>
    public static bool IsSteamInput(Controller controller) => TriggerCheck.IsSteamInput(Of(controller));

    /// <summary>"Rewired profile 2694f4b9 \"Steam Controller\": set up by Steam Input" (the profile's first 8 hex digits and its hardware name).</summary>
    public static string Describe(Joystick joystick)
    {
        Guid profile = Of(joystick);
        if (profile == Guid.Empty)
        {
            return "Rewired profile unknown";
        }

        string hardware;
        try
        {
            hardware = SafeText.Clean(joystick.hardwareName, 48);
        }
        catch (Exception)
        {
            hardware = "?";
        }

        string hex = profile.ToString("N", CultureInfo.InvariantCulture)[..8];
        return $"Rewired profile {hex} \"{hardware}\"{(TriggerCheck.IsSteamInput(profile) ? ": set up by Steam Input" : string.Empty)}";
    }
}
#endif
