#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppFMODUnity;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The lobby's moments, through the game's own sound queue (<c>NavigableSFXFMOD.PlayInGlobalQueue</c>), with the game's events: nothing shipped,
/// and the queue's anti-overlap is the game's. Each PC's own: the mod's sound forwarding only carries <c>event:/SFX/</c>. Moves, clicks and shortcuts bring their own sounds on their clones.
/// </summary>
internal static class LobbySounds
{
    /// <summary>A player arrived: the controller screen's change of side.</summary>
    public const string Arrived = "event:/UI/Choose Team/ui_chooseteam_changeside";

    /// <summary>A player left.</summary>
    public const string Left = "event:/UI/_General/ui_general_back_menu";

    /// <summary>A ready mark on.</summary>
    public const string Ready = "event:/UI/Choose Team/ui_chooseteam_ready";

    /// <summary>A ready mark off.</summary>
    public const string Unready = "event:/UI/Choose Team/ui_chooseteam_unready";

    /// <summary>Every seat taken and ready.</summary>
    public const string AllReady = "event:/UI/Choose Team/ui_chooseteam_team_rdy_transition";

    /// <summary>The mode changed by L or R.</summary>
    public const string ModeChanged = "event:/UI/Choose Team/ui_chooseteam_L-R_changeCategory";

    /// <summary>A step of the countdown.</summary>
    public const string CountdownStep = "event:/UI/_General/ui_general_left_right";

    /// <summary>The invite panel opening or the state changing inside the shell.</summary>
    public const string PanelIn = "event:/UI/Pause menu/ui_pausemenu_editlineup_in";

    /// <summary>The invite panel closing.</summary>
    public const string PanelOut = "event:/UI/Pause menu/ui_pausemenu_editlineup_out";

    /// <summary>A press that does nothing.</summary>
    public const string Grunt = "event:/UI/_General/ui_general_grunt";

    private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);

    /// <summary>Plays one of the events above in the game's queue. A failure is logged once per event; the lobby goes on silent.</summary>
    public static void Play(string sound)
    {
        try
        {
            NavigableSFXFMOD.PlayInGlobalQueue(RuntimeManager.PathToEventReference(sound), NavigableSFXFMOD.EEventType.Click, true, null!, Vector3.zero);
        }
        catch (Exception ex)
        {
            if (Failed.Add(sound))
            {
                ModLog.Warn($"Lobby: the sound {sound} can't be played ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    /// <summary>The game's own sound for a match starting (<c>GameStartMenusHandler.PlayMatchStartingSound</c>).</summary>
    public static void GameStarting()
    {
        try
        {
            GameStartMenusHandler.PlayMatchStartingSound();
        }
        catch (Exception ex)
        {
            if (Failed.Add("match starting"))
            {
                ModLog.Warn($"Lobby: the match starting sound can't be played ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }
}
#endif
