using T2TOnline.Core.Input;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// The pad button a check tour presses for one of the game's on-screen shortcuts (a <c>UIInputShortcutBase</c>, as on the
/// run's summary and the new-unlocks screen), from the Rewired action the shortcut registered: the button whose press makes
/// that action in the game's menus, by the game's own pad map (its X makes "UI Special", the Join list's Refresh; its Y
/// "UI Special 2", the lobby's Invite).
/// </summary>
public static class TourShortcuts
{
    /// <summary>The longest hold a tour makes for a shortcut that asks to be held (seconds).</summary>
    public const double MaxHoldSeconds = 3;

    /// <summary>The pad button that makes the Rewired action <paramref name="actionId"/> in the game's menus, or null for one the tour has no button for (the triggers, the sticks, an unknown id).</summary>
    public static PadButtons? ButtonFor(int actionId) => actionId switch
    {
        KeyWalk.UiConfirm => PadButtons.A,
        KeyWalk.UiBack => PadButtons.B,
        KeyWalk.UiSpecial => PadButtons.X,
        KeyWalk.UiSpecial2 => PadButtons.Y,
        KeyWalk.UiTabLeft => PadButtons.LeftBumper,
        KeyWalk.UiTabRight => PadButtons.RightBumper,
        KeyWalk.UiStart => PadButtons.Start,
        _ => null,
    };

    /// <summary>
    /// How long the tour holds the button for a shortcut that needs <paramref name="requiredHoldSeconds"/> (0 for a tap): a
    /// quarter second past it, at most <see cref="MaxHoldSeconds"/>; a tap's 0.1 s when none is needed (or the value is wrong).
    /// </summary>
    public static double HoldSeconds(double requiredHoldSeconds) =>
        double.IsFinite(requiredHoldSeconds) && requiredHoldSeconds > 0 ? System.Math.Min(requiredHoldSeconds + 0.25, MaxHoldSeconds) : 0.1;
}
