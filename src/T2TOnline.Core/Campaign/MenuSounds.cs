namespace T2TOnline.Core.Campaign;

/// <summary>
/// What the host's menus play and show besides their data, as menu values,
/// so a friend's copy plays the same sounds through the game's own code: a shortcut's press (<see cref="MenuPress"/>), the
/// host's back sound when its back input didn't close a menu (<see cref="BackGroup"/>), and the host's navigation mode
/// (<see cref="NavModeGroup"/>: the game plays select sounds and shows the highlight only outside mouse mode). No wire
/// change: they fit the existing value event. Each value that must play again alternates between two numbers (the host
/// sends a value only when it changes).
/// </summary>
public static class MenuSounds
{
    /// <summary>Value group (item 0): the host's back sound played in this menu (1 and 2 by turns).</summary>
    public const string BackGroup = "back";

    /// <summary>Value group (item 0): the host's navigation mode (<c>NavigationMode</c>: 0 unknown, 1 mouse, 2 keyboard, 3 controller).</summary>
    public const string NavModeGroup = "navMode";

    /// <summary>The largest navigation mode.</summary>
    public const int MaxNavMode = 3;

    /// <summary>The game's mouse navigation mode (no select sounds, no highlight).</summary>
    public const int MouseNavMode = 1;

    /// <summary>The back group's value for the <paramref name="count"/>th back sound (1 and 2 by turns).</summary>
    public static int BackValue(int count) => 1 + (count & 1);

    /// <summary>True for a back sound's value.</summary>
    public static bool IsBack(MenuTarget target, int value) =>
        target is { Kind: MenuTargetKind.Item, Index: 0 } && target.Group == BackGroup && value is 1 or 2;

    /// <summary>True, with <paramref name="mode"/>, for a navigation mode's value.</summary>
    public static bool TryReadNavMode(MenuTarget target, int value, out int mode)
    {
        mode = value;
        return target is { Kind: MenuTargetKind.Item, Index: 0 } && target.Group == NavModeGroup && value is >= 0 and <= MaxNavMode;
    }

    /// <summary>The navigation mode's name, for logs.</summary>
    public static string NavModeName(int mode) => mode switch
    {
        1 => "Mouse",
        2 => "Keyboard",
        3 => "Controller",
        _ => "Unknown",
    };
}

/// <summary>
/// A shortcut's press in a shared menu (its trigger: a reroll, a skip, a confirm), as a value on the shortcut's path, so a
/// friend's copy plays the shortcut's own trigger or failure sound. The values sit above every list selector's index and
/// both hold values (<see cref="MenuHold"/>), so a value on a path is exactly one of them; each kind alternates between two
/// numbers so a press repeated is a change.
/// </summary>
public static class MenuPress
{
    /// <summary>
    /// True when a shortcut's <c>Release</c> raises its "pressed while it can't trigger" event (<c>EEventType</c> 3, the game's
    /// <c>InputPressedWhileCantTrigger</c>, inlined there; verified in the game's code): it comes from a state it can't
    /// trigger in, and isn't ending a hold (a button down on a shortcut with a hold time or a hold loop fires the hold's release
    /// instead; verified in the game's code).
    /// </summary>
    /// <param name="fromCantTrigger">The call's <c>isFromCantTriggerState</c>.</param>
    /// <param name="buttonDown">The shortcut's <c>m_IsButtonPressed</c> as the call starts.</param>
    /// <param name="requiredHoldTime">Its <c>m_RequiredHoldTime</c>.</param>
    /// <param name="allowHoldLoop">Its <c>m_AllowHoldLoop</c>.</param>
    /// <returns>True when the release is a press on a disabled shortcut.</returns>
    public static bool RaisesCantTrigger(bool fromCantTrigger, bool buttonDown, float requiredHoldTime, bool allowHoldLoop) =>
        fromCantTrigger && !(buttonDown && (requiredHoldTime > 0 || allowHoldLoop));

    /// <summary>A press that worked (and the next one).</summary>
    public const int Success = MenuHold.Held + 1;

    /// <summary>A press that failed (a reroll with none left), and the next one.</summary>
    public const int Failure = Success + 2;

    /// <summary>The value for the <paramref name="count"/>th press on a shortcut, <paramref name="success"/> or not.</summary>
    public static int Value(bool success, int count) => (success ? Success : Failure) + (count & 1);

    /// <summary>True for a press's value.</summary>
    public static bool IsPress(int value) => value is >= Success and <= Failure + 1;

    /// <summary>Reads a value: true, with <paramref name="success"/>, when it's a press; false for anything else.</summary>
    public static bool TryRead(int value, out bool success)
    {
        success = value is Success or Success + 1;
        return IsPress(value);
    }
}
