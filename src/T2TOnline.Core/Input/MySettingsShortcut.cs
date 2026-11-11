using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Input;

/// <summary>Why a following friend's "My Settings" shortcut isn't on the host's pause menu now (<see cref="MySettingsShortcut.Availability"/>).</summary>
public enum MySettingsHidden
{
    /// <summary>It shows: a press opens the friend's own Settings.</summary>
    None = 0,

    /// <summary>The host's pause menu isn't drawn for real on this PC (none, another context, a host overlay over it, or it showed as text).</summary>
    NoPauseMenu,

    /// <summary>This PC doesn't follow a host as a joined friend with its device in the session (the lobby, a rejoin, the session over).</summary>
    NotFollowing,

    /// <summary>The friend's own Settings or remapper is open already.</summary>
    OwnMenuOpen,

    /// <summary>The friend's own menus are off until the game restarts, after an error.</summary>
    OwnMenusOff,

    /// <summary>The friend's own new-unlocks screen has their device.</summary>
    UnlocksOpen,

    /// <summary>The question about a match that couldn't load has their device.</summary>
    QuestionUp,

    /// <summary>One of the mod's prompts (the game's yes/no) is up on this PC.</summary>
    PromptOpen,

    /// <summary>The friend's pad is unplugged.</summary>
    PadUnplugged,

    /// <summary>The shortcut failed on this PC and is off for the session.</summary>
    Off,
}

/// <summary>Where a following friend's My Settings is.</summary>
public enum MySettingsPlace
{
    /// <summary>The host's pause menu as their PC draws it: a copy of its own bottom shortcut, "[RT] My Settings".</summary>
    PauseMenu,

    /// <summary>The world map's own prompt bar on their PC: its "[Start] Menu" entry, which opens their own Settings while they follow.</summary>
    MapBar,
}

/// <summary>What the friend's PC knows when it decides whether the shortcut shows (<see cref="MySettingsShortcut.Availability"/>).</summary>
/// <param name="PauseMenuShown">Its place is on screen: the host's pause menu drawn for real here, its canvas not covered; or the world map's
/// Menu prompt shown with none of the host's screens up.</param>
/// <param name="Following">This PC follows a host as a joined friend, its device with the host's game.</param>
/// <param name="OwnMenuOpen">The friend's own Settings or remapper has their device.</param>
/// <param name="OwnMenusOff">Own menus are off after an error.</param>
/// <param name="UnlocksOpen">The friend's own new-unlocks screen is up or has their device.</param>
/// <param name="QuestionUp">The question about a match that couldn't load has their device.</param>
/// <param name="PromptOpen">A prompt of the mod's is up.</param>
/// <param name="PadUnplugged">A pad friend's pad is unplugged.</param>
/// <param name="Off">The shortcut failed here and is off for the session.</param>
public readonly record struct MySettingsFacts(
    bool PauseMenuShown,
    bool Following,
    bool OwnMenuOpen = false,
    bool OwnMenusOff = false,
    bool UnlocksOpen = false,
    bool QuestionUp = false,
    bool PromptOpen = false,
    bool PadUnplugged = false,
    bool Off = false);

/// <summary>
/// A following friend's "[RT] My Settings" on the host's pause menu as their PC draws it: when it shows. A prompt that can't act isn't
/// there (the native lobby's rule), so it's hidden for the first reason that holds, in the order of <see cref="MySettingsHidden"/>.
/// </summary>
public static class MySettingsShortcut
{
    /// <summary>Why the shortcut is hidden in <paramref name="facts"/>, or <see cref="MySettingsHidden.None"/> when it shows.</summary>
    public static MySettingsHidden Availability(in MySettingsFacts facts) =>
        facts.Off ? MySettingsHidden.Off
        : !facts.PauseMenuShown ? MySettingsHidden.NoPauseMenu
        : !facts.Following ? MySettingsHidden.NotFollowing
        : facts.OwnMenuOpen ? MySettingsHidden.OwnMenuOpen
        : facts.OwnMenusOff ? MySettingsHidden.OwnMenusOff
        : facts.UnlocksOpen ? MySettingsHidden.UnlocksOpen
        : facts.QuestionUp ? MySettingsHidden.QuestionUp
        : facts.PromptOpen ? MySettingsHidden.PromptOpen
        : facts.PadUnplugged ? MySettingsHidden.PadUnplugged
        : MySettingsHidden.None;

    /// <summary>The reason in plain words, for the log (the host's pause menu's shortcut).</summary>
    public static string Describe(MySettingsHidden why) => Describe(why, MySettingsPlace.PauseMenu);

    /// <summary>The reason in plain words, for the log, for the shortcut at <paramref name="place"/>.</summary>
    public static string Describe(MySettingsHidden why, MySettingsPlace place) => why switch
    {
        MySettingsHidden.None => "it shows",
        MySettingsHidden.NoPauseMenu when place == MySettingsPlace.MapBar => "the map's Menu prompt isn't on screen (a menu is up, or the map isn't)",
        MySettingsHidden.NoPauseMenu => "the host's pause menu isn't drawn here",
        MySettingsHidden.NotFollowing => "this PC isn't following the host's game right now",
        MySettingsHidden.OwnMenuOpen => "your own menu is open",
        MySettingsHidden.OwnMenusOff => "your own menus are off until the game restarts, after an error",
        MySettingsHidden.UnlocksOpen => "your new-unlocks screen is up",
        MySettingsHidden.QuestionUp => "a question about the host's match is up",
        MySettingsHidden.PromptOpen => "a prompt is up",
        MySettingsHidden.PadUnplugged => "your pad is unplugged",
        MySettingsHidden.Off => "it failed earlier and is off for this session",
        _ => why.ToString(),
    };
}

/// <summary>
/// The elements that press a following friend's "My Settings": what their own maps bind the shortcut's action to, as the button prompt
/// on their screen shows it. On the host's pause menu that's the game's <c>UI_UIRightTrigger</c> in the UI maps (<see cref="Defaults"/>:
/// RT, Left Alt); on the world map, the map bar's Menu entry, <c>Default_Pause</c> in any category (<see cref="StartDefaults"/>: Start,
/// Escape). A pad's elements from a pad's controls (the game's defaults send no bindings); a key from a keyboard's controls (and for a pad
/// friend, whose keyboard maps aren't read, the game's key).
/// </summary>
public sealed class MySettingsBinding
{
    /// <summary>Unity's key code for Left Alt: the game's keyboard binding of <c>UI_UIRightTrigger</c>.</summary>
    public const ushort LeftAlt = 308;

    /// <summary>Unity's key code for Escape: the game's keyboard binding of <c>Default_Pause</c>.</summary>
    public const ushort Escape = 27;

    /// <summary>How far an axis must go to press (the game's trigger actions fire there: <see cref="PadUsage.AxisThreshold"/>).</summary>
    public const float AxisThreshold = PadUsage.AxisThreshold;

    private static readonly PadAxis[] DefaultAxes = { PadAxis.RightTrigger };
    private static readonly bool[] DefaultNegative = { false };
    private static readonly BindingElement[] DefaultKeys = { BindingElement.Key(LeftAlt) };
    private static readonly PadAxis[] NoAxes = Array.Empty<PadAxis>();
    private static readonly bool[] NoNegative = Array.Empty<bool>();
    private static readonly BindingElement[] StartKeys = { BindingElement.Key(Escape) };

    private readonly PadAxis[] _axes;
    private readonly bool[] _negative;
    private readonly BindingElement[] _keys;

    private MySettingsBinding(PadButtons buttons, PadAxis[] axes, bool[] negative, BindingElement[] keys)
    {
        Buttons = buttons;
        _axes = axes;
        _negative = negative;
        Keys = keys;
        _keys = keys;
        PadName = Name(buttons, axes);
        KeyName = keys.Length == 0 ? "no key" : KeyNameOf(keys[0]);
    }

    /// <summary>The game's own bindings: RT on a pad, Left Alt on the keyboard.</summary>
    public static MySettingsBinding Defaults { get; } = new(PadButtons.None, DefaultAxes, DefaultNegative, DefaultKeys);

    /// <summary>The game's own bindings of <c>Default_Pause</c> (the world map bar's Menu): Start on a pad, Escape on the keyboard.</summary>
    public static MySettingsBinding StartDefaults { get; } = new(PadButtons.Start, NoAxes, NoNegative, StartKeys);

    /// <summary>The pad buttons that press it.</summary>
    public PadButtons Buttons { get; }

    /// <summary>The pad axes that press it, past <see cref="AxisThreshold"/> their way.</summary>
    public IReadOnlyList<PadAxis> Axes => _axes;

    /// <summary>The keys that press it (with the modifiers each needs).</summary>
    public IReadOnlyList<BindingElement> Keys { get; }

    /// <summary>The pad's element for the log ("RT" on the game's defaults).</summary>
    public string PadName { get; }

    /// <summary>The key for the log ("Left Alt" on the game's defaults).</summary>
    public string KeyName { get; }

    /// <summary>
    /// The elements in <paramref name="controls"/> (the friend's own controls; null: the game's defaults) bound to action
    /// <paramref name="actionId"/> in map category <paramref name="uiCategory"/>. A pad's set gives the pad's elements and the default key;
    /// a keyboard's its keys and the default pad elements. Where the set binds the action to nothing usable, the game's default stands.
    /// </summary>
    public static MySettingsBinding From(InputBindingSet? controls, int uiCategory, int actionId) =>
        uiCategory < 0 ? Defaults : From(controls, uiCategory, actionId, Defaults);

    /// <summary>
    /// The elements in <paramref name="controls"/> bound to action <paramref name="actionId"/> in map category <paramref name="category"/> (any
    /// category when it's negative), with <paramref name="defaults"/>' pad elements or key where the set has none of its own.
    /// </summary>
    public static MySettingsBinding From(InputBindingSet? controls, int category, int actionId, MySettingsBinding defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (controls == null || actionId < 0)
        {
            return defaults;
        }

        PadButtons buttons = PadButtons.None;
        var axes = new List<PadAxis>();
        var negative = new List<bool>();
        var keys = new List<BindingElement>();
        foreach (InputBinding binding in controls.Bindings)
        {
            if ((category >= 0 && binding.Category != category) || binding.ActionId != actionId || !binding.IsValid || binding.Negative)
            {
                continue;
            }

            BindingElement element = binding.Element;
            switch (element.Kind)
            {
                case BindingElementKind.PadButton when controls.Device == InputKind.Gamepad:
                    buttons |= (PadButtons)(1 << element.Code);
                    break;
                case BindingElementKind.PadAxis when controls.Device == InputKind.Gamepad && !axes.Contains((PadAxis)element.Code):
                    axes.Add((PadAxis)element.Code);
                    negative.Add(binding.Range == BindingRange.Negative ^ binding.Invert);
                    break;
                case BindingElementKind.Key when controls.Device == InputKind.KeyboardMouse && !keys.Contains(element):
                    keys.Add(element);
                    break;
            }
        }

        bool padOwn = buttons != PadButtons.None || axes.Count > 0;
        return new MySettingsBinding(
            padOwn ? buttons : defaults.Buttons,
            padOwn ? axes.ToArray() : defaults._axes,
            padOwn ? negative.ToArray() : defaults._negative,
            keys.Count > 0 ? keys.ToArray() : defaults._keys);
    }

    /// <summary>True when the pad's element is down in this reading (a button held, an axis past <see cref="AxisThreshold"/> its way).</summary>
    public bool PadDown(PadButtons held, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
    {
        if ((held & Buttons) != 0)
        {
            return true;
        }

        for (int i = 0; i < _axes.Length; i++)
        {
            float value = Value(_axes[i], leftX, leftY, rightX, rightY, leftTrigger, rightTrigger);
            if (_negative[i] ? value <= -AxisThreshold : value >= AxisThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The reading with the pad's element left out (its buttons up, its axes at rest): what goes to the host while the press is kept.</summary>
    public void LeaveOut(ref PadButtons held, ref float leftX, ref float leftY, ref float rightX, ref float rightY, ref float leftTrigger, ref float rightTrigger)
    {
        held &= ~Buttons;
        foreach (PadAxis axis in _axes)
        {
            switch (axis)
            {
                case PadAxis.LeftStickX: leftX = 0; break;
                case PadAxis.LeftStickY: leftY = 0; break;
                case PadAxis.RightStickX: rightX = 0; break;
                case PadAxis.RightStickY: rightY = 0; break;
                case PadAxis.LeftTrigger: leftTrigger = 0; break;
                case PadAxis.RightTrigger: rightTrigger = 0; break;
            }
        }
    }

    /// <summary>True for a key that is a modifier itself (Shift, Control, Alt, AltGr, the Windows and Command keys): it acts once let go alone (<see cref="ModifierPress"/>).</summary>
    public static bool IsModifierKey(BindingElement key) => key.Kind == BindingElementKind.Key && key.Code is >= 303 and <= 313;

    private static float Value(PadAxis axis, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger) => axis switch
    {
        PadAxis.LeftStickX => leftX,
        PadAxis.LeftStickY => leftY,
        PadAxis.RightStickX => rightX,
        PadAxis.RightStickY => rightY,
        PadAxis.LeftTrigger => leftTrigger,
        _ => rightTrigger,
    };

    private static string Name(PadButtons buttons, PadAxis[] axes)
    {
        if (axes.Length > 0)
        {
            return axes[0] switch
            {
                PadAxis.RightTrigger => "RT",
                PadAxis.LeftTrigger => "LT",
                _ => axes[0].ToString(),
            };
        }

        for (int bit = 0; bit < PadState.ButtonCount; bit++)
        {
            if (((int)buttons & (1 << bit)) != 0)
            {
                return ((PadButtons)(1 << bit)).ToString();
            }
        }

        return "no pad element";
    }

    private static string KeyNameOf(BindingElement key) => key.Modifiers == KeyModifiers.None ? key.Code switch
    {
        303 => "Right Shift",
        304 => "Left Shift",
        305 => "Right Ctrl",
        306 => "Left Ctrl",
        307 => "Right Alt",
        LeftAlt => "Left Alt",
        Escape => "Esc",
        _ => key.ToString(),
    } : key.ToString();
}

/// <summary>What a key's press did this frame (<see cref="MySettingsPress.Key"/>).</summary>
public enum MySettingsKeyStep
{
    /// <summary>Nothing yet.</summary>
    None,

    /// <summary>It presses the shortcut.</summary>
    Fire,

    /// <summary>A modifier's press that went with another key, lost the window's focus, or outlived the shortcut: nothing happens.</summary>
    Dropped,
}

/// <summary>
/// The presses of a following friend's "My Settings", from what their PC reads each frame. A pad's element and a plain key act as they go
/// down while the shortcut shows, as the game's shortcuts do; a modifier key (Left Alt, the game's) acts when it's let go alone
/// (<see cref="ModifierPress"/>), so Alt+Tab never opens it. Down before the shortcut showed counts for nothing: a press begins on it.
/// </summary>
public sealed class MySettingsPress
{
    private bool _padDown;
    private bool _keyDown;

    /// <summary>True while a modifier's press waits to see what it was for.</summary>
    public bool KeyWaits { get; private set; }

    /// <summary>One pad reading: true when the element went down now, the shortcut showing.</summary>
    public bool Pad(bool down, bool live)
    {
        bool fires = down && !_padDown && live;
        _padDown = down;
        return fires;
    }

    /// <summary>
    /// One frame of the key: <paramref name="down"/> held now; <paramref name="modifier"/> it's a modifier key; <paramref name="otherKeyWentDown"/>
    /// another key went down this frame; <paramref name="focused"/> the game's window has the focus.
    /// </summary>
    public MySettingsKeyStep Key(bool down, bool live, bool modifier, bool otherKeyWentDown, bool focused)
    {
        bool began = down && !_keyDown;
        _keyDown = down;
        if (began)
        {
            if (!live)
            {
                KeyWaits = false;
                return MySettingsKeyStep.None;
            }

            if (!modifier)
            {
                return MySettingsKeyStep.Fire;
            }

            KeyWaits = true; // the frame it went down: wait (another key going down with it is the same chord)
            return MySettingsKeyStep.None;
        }

        if (!KeyWaits)
        {
            return MySettingsKeyStep.None;
        }

        switch (ModifierPress.Step(down, otherKeyWentDown, focused))
        {
            case ModifierStep.Act:
                KeyWaits = false;
                return live ? MySettingsKeyStep.Fire : MySettingsKeyStep.Dropped;
            case ModifierStep.Drop:
                KeyWaits = false;
                return MySettingsKeyStep.Dropped;
            default:
                return MySettingsKeyStep.None;
        }
    }

    /// <summary>Forgets every press (the session ended, or the friend's device came back from a lend).</summary>
    public void Reset()
    {
        _padDown = false;
        _keyDown = false;
        KeyWaits = false;
    }
}

/// <summary>
/// Whether an element is kept from the host: from the moment it goes down while the shortcut shows until it's let go, even once the
/// shortcut has gone (the press was the friend's own, never the host's game's). One held from before the shortcut showed goes on as it was.
/// </summary>
public struct MySettingsKeep
{
    private bool _down;

    /// <summary>True while the element is kept from the host.</summary>
    public bool Kept { get; private set; }

    /// <summary>Presses kept from the host so far.</summary>
    public int Count { get; private set; }

    /// <summary>One reading: <paramref name="down"/> held now, <paramref name="live"/> the shortcut showing. True while it's kept.</summary>
    public bool Step(bool down, bool live)
    {
        if (down && !_down && live)
        {
            Kept = true;
            Count++;
        }

        if (!down)
        {
            Kept = false;
        }

        _down = down;
        return Kept;
    }

    /// <summary>Forgets the element (its count stays).</summary>
    public void Reset()
    {
        _down = false;
        Kept = false;
    }

    /// <summary>Forgets everything, the count too.</summary>
    public void Clear() => this = default;
}

/// <summary>Keeps a friend's key-table bits from the host (<see cref="MySettingsKeep"/> over a mask of elements).</summary>
public static class MySettingsBits
{
    /// <summary>The key-table bits of <paramref name="keys"/> in <paramref name="controls"/> (a keyboard's; 0 for a pad's or keys it doesn't carry).</summary>
    public static ulong Mask(InputBindingSet? controls, IReadOnlyList<BindingElement> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (controls == null || controls.Device != InputKind.KeyboardMouse)
        {
            return 0;
        }

        ulong mask = 0;
        foreach (BindingElement key in keys)
        {
            int index = controls.IndexOf(key);
            if (index is >= 0 and < 64)
            {
                mask |= 1UL << index;
            }
        }

        return mask;
    }

    /// <summary>The key-table bits of every mouse button in <paramref name="controls"/> (a keyboard's; 0 otherwise).</summary>
    public static ulong MouseMask(InputBindingSet? controls)
    {
        if (controls == null || controls.Device != InputKind.KeyboardMouse)
        {
            return 0;
        }

        ulong mask = 0;
        for (int i = 0; i < controls.Elements.Count && i < 64; i++)
        {
            if (controls.Elements[i].Kind == BindingElementKind.MouseButton)
            {
                mask |= 1UL << i;
            }
        }

        return mask;
    }
}
