#if HAS_INTEROP
using System.Linq;
using Il2CppRewired;
using T2TOnline.Core.Input;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Reads a physical gamepad through Rewired's gamepad template, which gives every pad model (Xbox, PlayStation,
/// Switch Pro...) the same layout, independent of whichever control maps are active.
/// </summary>
internal sealed class GamepadTemplateReader
{
    private readonly IControllerTemplateAxis?[] _axes = new IControllerTemplateAxis?[PadState.AxisCount];
    private readonly IControllerTemplateButton?[] _buttons = new IControllerTemplateButton?[PadState.ButtonCount];

    private GamepadTemplateReader(Joystick joystick) => Joystick = joystick;

    /// <summary>The pad being read.</summary>
    public Joystick Joystick { get; }

    /// <summary>Elements the pad doesn't have; they read as released or centred.</summary>
    public int MissingElements { get; private set; }

    /// <summary>The buttons this pad's template lacks (they read as released).</summary>
    public PadButtons MissingButtons { get; private set; }

    /// <summary>The axes this pad's template lacks, one bit per <see cref="PadAxis"/> (they read as centred).</summary>
    public int MissingAxes { get; private set; }

    /// <summary>The standard pad elements this pad's template lacks, by name ("" when it has them all).</summary>
    public string MissingNames() => PadUsage.Missing(
        Enumerable.Range(0, PadState.ButtonCount).Where(i => _buttons[i] == null),
        Enumerable.Range(0, PadState.AxisCount).Where(i => _axes[i] == null).Select(i => (PadAxis)i));

    /// <summary>A reader for <paramref name="joystick"/>, or null if it doesn't implement the gamepad template.</summary>
    public static GamepadTemplateReader? TryCreate(Joystick joystick)
    {
        ControllerTemplate? template = joystick.GetTemplate(GamepadTemplate.typeGuid)?.TryCast<ControllerTemplate>();
        if (template == null)
        {
            return null;
        }

        var reader = new GamepadTemplateReader(joystick);
        for (int i = 0; i < PadState.AxisCount; i++)
        {
            reader._axes[i] = template.GetElement(GamepadTemplateLayout.AxisElementId((PadAxis)i))?.TryCast<IControllerTemplateAxis>();
            reader.MissingElements += reader._axes[i] == null ? 1 : 0;
            reader.MissingAxes |= reader._axes[i] == null ? 1 << i : 0;
        }

        for (int i = 0; i < PadState.ButtonCount; i++)
        {
            reader._buttons[i] = template.GetElement(GamepadTemplateLayout.ButtonElementId(i))?.TryCast<IControllerTemplateButton>();
            reader.MissingElements += reader._buttons[i] == null ? 1 : 0;
            reader.MissingButtons |= reader._buttons[i] == null ? (PadButtons)(1 << i) : PadButtons.None;
        }

        return reader;
    }

    /// <summary>The buttons held right now.</summary>
    public PadButtons ReadButtons()
    {
        int held = 0;
        for (int i = 0; i < PadState.ButtonCount; i++)
        {
            if (_buttons[i]?.value == true)
            {
                held |= 1 << i;
            }
        }

        return (PadButtons)held;
    }

    /// <summary>An axis value: sticks -1 to 1, triggers 0 to 1.</summary>
    public float ReadAxis(PadAxis axis) => _axes[(int)axis]?.value ?? 0f;
}
#endif
