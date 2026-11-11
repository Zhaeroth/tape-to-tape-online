#if HAS_INTEROP
namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The native lobby's kit, as <see cref="Runtime.ModRuntime"/> runs it: its one patch at start-up, then each frame the native screens' layer (at this point
/// of the runtime's frame), the title's Online group and the lobby's screens. What works whatever online's state (the Settings menu's Online tab, the held
/// prefabs, the shell's and the prompts' ticks) is the layer's (<see cref="NativeUi.NativeUiLayer"/>), which outlives the runtime. Every part guards itself;
/// the runtime's <c>Safely</c> guards the whole.
/// </summary>
internal static class LobbyKit
{
    /// <summary>Puts the patch in (a failure turns off only the native lobby).</summary>
    public static void Install(HarmonyLib.Harmony harmony)
    {
        LobbyPatches.Install(harmony);
    }

    /// <summary>Call once per frame.</summary>
    public static void Update()
    {
        NativeUi.NativeUiLayer.TickFromKit();
        OnlineGroup.Tick();
        LobbyScreens.Instance.Tick();
    }
}
#endif
