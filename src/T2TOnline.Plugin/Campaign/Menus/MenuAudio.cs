#if HAS_INTEROP
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The game's own menu sounds and fades, played on a friend's copy for what the host did: a click's sound through the
/// clicked element's own <c>NavigableSFXFMOD.OnClicked</c> (it picks the click, invalid or no sound from the
/// host's result and effect, as on the host), the back sound (<c>NavigableSFXFMOD.PlayBackSfx</c>), a shortcut's
/// trigger or failure sound through its own <c>InputShortcutSFXFMOD.PlayInQueue</c> (as its <c>OnPostInput</c>
/// picks it), the navigation mode (the game's <c>UIInputDispatcher.RefreshNavigationMode</c>: its
/// highlight and select sounds follow). All called, none patched. The reward menus' fade to black: <see cref="RewardFade"/>.
/// Game thread only; the callers guard against exceptions.
/// </summary>
internal static class MenuAudio
{
    /// <summary>
    /// The click sound of <paramref name="navigable"/> for the host's click with its result and effect. False when it has
    /// no sound component (the host's had none either: same prefab).
    /// </summary>
    public static bool Click(AbstractNavigable navigable, byte result, byte effect)
    {
        NavigableSFXFMOD? sfx = navigable.GetComponent<NavigableSFXFMOD>();
        if (sfx == null)
        {
            return false;
        }

        sfx.OnClicked(navigable, (AbstractNavigable.EClickResult)result, (AbstractNavigable.EClickFx)effect);
        return true;
    }

    /// <summary>The game's back sound.</summary>
    public static void Back() => NavigableSFXFMOD.PlayBackSfx();

    /// <summary><paramref name="shortcut"/>'s sound component: on it, or anywhere in <paramref name="open"/> naming it; null when it has none.</summary>
    public static InputShortcutSFXFMOD? SoundOf(AbstractMenu open, UIInputShortcutBase shortcut) =>
        shortcut.GetComponent<InputShortcutSFXFMOD>() ??
        open.GetComponentsInChildren<InputShortcutSFXFMOD>(true).FirstOrDefault(s => s != null && s.m_InputShortcut?.Pointer == shortcut.Pointer);

    /// <summary>
    /// <paramref name="shortcut"/>'s own trigger sound (<paramref name="success"/>) or failure sound, as its sound component
    /// picks it for a press. "" when it played; else why not (no sound component).
    /// </summary>
    public static string Press(AbstractMenu open, UIInputShortcutBase shortcut, bool success)
    {
        InputShortcutSFXFMOD? sfx = SoundOf(open, shortcut);
        if (sfx == null)
        {
            return "it has no sound here";
        }

        if (success || !sfx.m_CheckClickSuccess)
        {
            sfx.PlayInQueue(sfx.m_TriggerEventFMOD, NavigableSFXFMOD.EEventType.Click);
        }
        else
        {
            sfx.PlayInQueue(sfx.m_FailureEventFMOD, NavigableSFXFMOD.EEventType.ClickWhileInvalid);
        }

        return string.Empty;
    }

    /// <summary>This PC's navigation mode (as <c>NavigationController.CurrentNavigationMode</c> reads it), or -1 without the UI.</summary>
    public static int NavMode()
    {
        UIInputDispatcher? dispatcher = Dispatcher();
        return dispatcher == null ? -1 : (int)dispatcher.CurrentNavigationMode;
    }

    /// <summary>
    /// Makes this PC's navigation mode <paramref name="mode"/> through the game's own change (its listeners highlight or
    /// unhighlight the selection), as its last active input player. False, with why, when it can't.
    /// </summary>
    public static bool SetNavMode(int mode, out string problem)
    {
        UIInputDispatcher? dispatcher = Dispatcher();
        if (dispatcher == null)
        {
            problem = "the game's UI input isn't ready";
            return false;
        }

        IUIInputPlayer? player = dispatcher.LastActivePlayer;
        if (player == null)
        {
            problem = "no input player has been active here yet";
            return false;
        }

        dispatcher.RefreshNavigationMode((NavigationMode)mode, player);
        problem = string.Empty;
        return true;
    }

    private static UIInputDispatcher? Dispatcher() => MenuGame.Module()?.InputSource?.ControllingPlayersOrKeyboardMouseInputEvents;
}
#endif
