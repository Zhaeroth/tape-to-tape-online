namespace T2TOnline.Core.Lobby;

/// <summary>What a press made with a modifier key does this frame (<see cref="ModifierPress"/>).</summary>
public enum ModifierStep
{
    /// <summary>The key is still down: nothing yet.</summary>
    Wait,

    /// <summary>The key was let go alone: the press acts.</summary>
    Act,

    /// <summary>It was part of something else: nothing happens.</summary>
    Drop,
}

/// <summary>
/// The game's keyboard map puts <c>UI_UIRightTrigger</c> on Left Alt, <c>UI_UILeftTrigger</c> on Left Ctrl and <c>UI_UISpecial2</c> on the Shifts, and a shortcut acts as its key goes down. Where the
/// press opens something, that would answer Alt+Tab with the game's Settings, Shift+Tab (Steam's overlay) with the invite list and Ctrl+F6 with a question. So a press made with one of these keys
/// acts when the key is let go, alone. Another key in between, or the window losing focus, and it was for something else.
/// </summary>
public static class ModifierPress
{
    /// <summary>What the press does this frame.</summary>
    /// <param name="keyDown">The modifier is still down.</param>
    /// <param name="otherKeyWentDown">A key went down this frame, after the frame of the press.</param>
    /// <param name="focused">The game's window has the focus.</param>
    public static ModifierStep Step(bool keyDown, bool otherKeyWentDown, bool focused) =>
        !focused || otherKeyWentDown ? ModifierStep.Drop : keyDown ? ModifierStep.Wait : ModifierStep.Act;
}
