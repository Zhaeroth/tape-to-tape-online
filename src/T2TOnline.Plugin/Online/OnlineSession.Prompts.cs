#if HAS_INTEROP
using System;
using T2TOnline.Plugin.Input;

namespace T2TOnline.Plugin.Online;

// The prompt pin's part of a friend's session (Input/PromptPin.cs): while a friend's device is
// with the host's game (BeginPlay to EndPlay: in the lobby their game's own prompts show, as it's their own game), the
// button prompts on their screen show their own device and bindings. Apart from what the session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    private Func<PromptDevice>? _promptDevice;

    /// <summary>
    /// The friend's chosen local device, the one place the prompt pin takes it from (asked twice a second while pinned):
    /// the pad <see cref="LocalPad"/> holds; for a keyboard-and-mouse friend (<see cref="LocalKeyboard"/>), the keyboard
    /// with the maps of the Rewired player its controls were read from; otherwise the keyboard with the maps of Rewired
    /// player 0 (the game's keyboard player on every PC).
    /// </summary>
    private PromptDevice FriendPromptDevice() => _localPad is { } pad
        ? PromptDevice.ForPad(pad)
        : PromptDevice.ForKeyboard(_localKeyboard?.SourcePlayer ?? PromptDevice.KeyboardPlayerId);

    /// <summary>A friend's play period started (BeginPlay): prompts show the friend's own device. The one-PC test's friend side never comes here: that game is the host's.</summary>
    private void PinPrompts() => PromptPin.Pin(_promptDevice ??= FriendPromptDevice);

    /// <summary>The friend's play period or side ended: prompts follow the game's own choice again (left as they are while the game is closing).</summary>
    private void UnpinPrompts(string reason) => PromptPin.Unpin(reason, restore: !_gameClosing);
}
#endif
