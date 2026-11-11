#if HAS_INTEROP
using Il2CppControllerConnection;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>Joins virtual pads and keyboards through the game's own join code.</summary>
internal static class GameJoin
{
    /// <summary>The game's "activate" action (RewiredPlayerInputAssigner.ActivateControllers reads action 31).</summary>
    private const int ActivateActionId = 31;

    /// <summary>
    /// The game only polls Rewired players that have a joystick or mouse for its join button, so a virtual pad's or
    /// keyboard's press (the Activation maps: A on a pad, Space, Return, WASD and more on a keyboard) is relayed here,
    /// through the game's own join code. Call once per frame; returns the controller's game player (0 or more), or -1
    /// while it isn't joined. <paramref name="seat"/> names the controller in the log: a fixed label or a friend's tag, never a
    /// Steam name (D127).
    /// </summary>
    public static int TryRelay(VirtualController pad, string seat)
    {
        RewiredPlayerInputAssigner? assigner = RewiredPlayerInputAssigner.Instance;
        if (assigner == null)
        {
            return -1;
        }

        int rewiredId = pad.RewiredPlayerId;
        int gamePlayer = RewiredPlayerInputAssigner.GetPlayerId(rewiredId);
        if (gamePlayer < 0 && (pad.Player.GetButtonDown(ActivateActionId) || pad.Player.GetNegativeButtonDown(ActivateActionId)))
        {
            ModLog.Info($"{seat}: join button pressed on the {(pad.IsKeyboard ? "virtual keyboard" : "virtual pad")}; asking the game to join Rewired player {rewiredId}.");
            assigner.AssignNextPlayer(rewiredId);
            gamePlayer = RewiredPlayerInputAssigner.GetPlayerId(rewiredId);
            if (gamePlayer < 0)
            {
                ModLog.Warn($"{seat}: the game did not join the {(pad.IsKeyboard ? "virtual keyboard" : "virtual pad")} (its [PlayerAssigner] lines above say why).");
            }
        }

        return gamePlayer;
    }
}
#endif
