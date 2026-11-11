#if HAS_INTEROP
using System;
using Il2CppInterop.Runtime;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online.Panel;

namespace T2TOnline.Plugin.Online;

// The mouse cursor at a press on the pad a session holds: the game hides and
// locks it whenever a button on one of its players' pads is pressed (HideCursor.OnGamePadPerformed, wired to a Rewired
// "any gamepad action"), and shows it again when the mouse moves. A pad the session holds is on no player, so that never
// fired and a cursor shown by a mouse move stayed up; now the game's own OnGamePadPerformed runs at such a press too (not
// while the mod's panel holds the cursor: PanelInput skips that method then). Apart from what the session does
// (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    /// <summary>How often the game's cursor hider is looked for again while it isn't there.</summary>
    private static readonly TimeSpan HideCursorLookInterval = TimeSpan.FromSeconds(5);

    private PadButtons _padHeldBefore;
    private bool _padTriggersBefore;
    private bool _padPressedForCursor;
    private Il2Cpp.HideCursor? _hideCursor;
    private TimeSpan _nextHideCursorLook;
    private bool _hideCursorOff;
    private bool _hideCursorLogged;

    /// <summary>
    /// Input update: notes a press on the held pad (a button gone down, or a trigger past half way) for the cursor, which is
    /// hidden in the next frame's update (<see cref="HideCursorAfterPadPress"/>), not inside Rewired's update.
    /// </summary>
    private void NotePadPress(PadButtons held, float leftTrigger, float rightTrigger)
    {
        bool triggers = leftTrigger > 0.5f || rightTrigger > 0.5f;
        if ((held & ~_padHeldBefore) != PadButtons.None || (triggers && !_padTriggersBefore))
        {
            _padPressedForCursor = true;
        }

        _padHeldBefore = held;
        _padTriggersBefore = triggers;
    }

    /// <summary>
    /// Every frame: after a press on the held pad, the game's own cursor hider runs as it would for a pad on one of its players
    /// (the cursor hidden and locked until the mouse moves). Not while the mod's panel holds the cursor. Never throws: a failure
    /// is logged and the cursor is left to the game from then on.
    /// </summary>
    private void HideCursorAfterPadPress(TimeSpan elapsed)
    {
        if (!_padPressedForCursor)
        {
            return;
        }

        _padPressedForCursor = false;
        if (_hideCursorOff || _localPad is not { IsDetached: true } || PanelInput.Held)
        {
            return;
        }

        try
        {
            if (_hideCursor == null || _hideCursor.WasCollected)
            {
                _hideCursor = null;
                if (elapsed < _nextHideCursorLook)
                {
                    return;
                }

                _nextHideCursorLook = elapsed + HideCursorLookInterval;
                _hideCursor = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<Il2Cpp.HideCursor>())?.TryCast<Il2Cpp.HideCursor>();
                if (_hideCursor == null)
                {
                    return;
                }
            }

            _hideCursor.OnGamePadPerformed(null);
            if (!_hideCursorLogged)
            {
                _hideCursorLogged = true;
                ModLog.Info("Your cursor: hidden at a press on your pad, as this game's own HideCursor does for a pad on one of its players (a mouse move shows it again).");
            }
        }
        catch (Exception ex)
        {
            _hideCursorOff = true;
            ModLog.Warn($"Your cursor: hiding it at a press on your pad failed ({ModLog.SafeErrorText(ex)}); it's left to the game from now on.");
        }
    }
}
#endif
