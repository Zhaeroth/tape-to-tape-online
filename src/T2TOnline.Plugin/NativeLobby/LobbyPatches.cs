#if HAS_INTEROP
using System;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The native lobby's one patch: a postfix on <c>TitleScreen.SelectDefaultButton</c>, whose jump table ignores any group above 5
/// (<c>cmp edi, 5; ja</c>), so the Online group's first selection is made here, as cases 1-5 make theirs. Target: unique,
/// not generic, an enum by value; called directly by <c>ApplyActiveGroup</c>, <c>UpdateGroupTransition</c> and
/// <c>&lt;LaunchPlayNow&gt;g__Launch</c> (checked across the game's code), so it runs. If it can't go in, only the native lobby is off.
/// </summary>
internal static class LobbyPatches
{
    /// <summary>True when the postfix went in.</summary>
    public static bool Installed { get; private set; }

    /// <summary>Puts the patch in. A failure is logged and turns off only the native lobby.</summary>
    public static void Install(HarmonyLib.Harmony harmony)
    {
        var failed = new System.Collections.Generic.List<string>();
        Campaign.Scenes.ScenePatches.Postfix(harmony, failed, typeof(TitleScreen), nameof(TitleScreen.SelectDefaultButton), typeof(LobbyPatches), nameof(SelectDefaultPostfix));
        Installed = failed.Count == 0;
        if (!Installed)
        {
            ModLog.Warn($"Lobby: the Online row stays off for this game: a patch couldn't go in ({string.Join(", ", failed)}). {Core.OnlineSettings.PlayersBuild.WithoutLobby(Startup.ModSettings.DevTools)}.");
        }
    }

    private static void SelectDefaultPostfix(TitleScreen __instance, TitleScreen.EButtonGroup group)
    {
        try
        {
            if ((int)group == OnlineGroup.GroupValue)
            {
                OnlineGroup.SelectDefault(__instance);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the Online group's first selection failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
