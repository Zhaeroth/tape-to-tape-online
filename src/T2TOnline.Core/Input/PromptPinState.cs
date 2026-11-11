using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Input;

/// <summary>
/// A friend's prompt pin without the game (the game's side is Plugin/Input/PromptPin.cs): which device family every
/// player's button prompts are pinned to, when a prompt is the pin's to draw, what the game's own automatic choice would
/// have been meanwhile (its "shadow", kept the way the game keeps it: one device per game player and a default), and
/// which screens have had their one log line. Device types are the game's own values (BishopGames.Input.EInputDeviceType,
/// by declaration order): 0 keyboard and mouse, 1 Xbox, 2 PlayStation, 3 Nintendo, 4 a generic pad, 5 other.
/// </summary>
public sealed class PromptPinState
{
    /// <summary>The game's keyboard-and-mouse device type.</summary>
    public const int MouseKeyboard = 0;

    /// <summary>The game's Xbox pad device type.</summary>
    public const int GamepadXbox = 1;

    /// <summary>The game's PlayStation pad device type.</summary>
    public const int GamepadDualShock = 2;

    /// <summary>The game's Nintendo pad device type.</summary>
    public const int GamepadNintendo = 3;

    /// <summary>The game's generic pad device type.</summary>
    public const int GamepadGeneric = 4;

    /// <summary>The game's "no device it knows" type (no controller, or a custom one): it draws no prompt for it.</summary>
    public const int Other = 5;

    /// <summary>Screens logged per pinned session at most (<see cref="FirstOnScreen"/>).</summary>
    public const int DefaultMaxScreens = 32;

    /// <summary>Most game players the shadow keeps (the game's mapper has 11).</summary>
    public const int MaxPlayers = 64;

    private readonly int _maxScreens;
    private readonly HashSet<(string Scene, int Category)> _screens = new();
    private int[] _shadow = Array.Empty<int>();

    /// <summary>Creates an unpinned state that logs at most <paramref name="maxScreens"/> screens per pinned session.</summary>
    public PromptPinState(int maxScreens = DefaultMaxScreens)
    {
        if (maxScreens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxScreens));
        }

        _maxScreens = maxScreens;
    }

    /// <summary>True between <see cref="Pin"/> and <see cref="Unpin"/>.</summary>
    public bool Pinned { get; private set; }

    /// <summary>The device family prompts are pinned to (the friend's own device); <see cref="Other"/> when unpinned.</summary>
    public int Family { get; private set; } = Other;

    /// <summary>True once the game's own choice is being followed (<see cref="TrackGame"/>); kept after <see cref="Unpin"/>.</summary>
    public bool ShadowKnown { get; private set; }

    /// <summary>How many game players the shadow keeps (0 until <see cref="TrackGame"/>).</summary>
    public int Players => _shadow.Length;

    /// <summary>The device the game's own default prompts would use now (what prompts tied to no player follow).</summary>
    public int ShadowDefault { get; private set; } = Other;

    /// <summary>How often the game's own choice changed while pinned.</summary>
    public int ShadowChanges { get; private set; }

    /// <summary>Prompts the pin drew from the friend's own bindings.</summary>
    public int Drawn { get; private set; }

    /// <summary>Prompts the pin answered with no binding found in the friend's maps (the game draws none then, as on the couch).</summary>
    public int NotFound { get; private set; }

    /// <summary>Screens whose line was logged.</summary>
    public int ScreensLogged { get; private set; }

    /// <summary>New screens past the cap, not logged.</summary>
    public int ScreensNotLogged { get; private set; }

    /// <summary>True for the device types the game draws prompts for (keyboard and mouse, and the four pad families).</summary>
    public static bool IsKnown(int device) => device is >= MouseKeyboard and <= GamepadGeneric;

    /// <summary>The game's name for a device type, as the log shows it ("GamepadXbox").</summary>
    public static string DeviceName(int device) => device switch
    {
        MouseKeyboard => "MouseKeyboard",
        GamepadXbox => "GamepadXbox",
        GamepadDualShock => "GamepadDualShock",
        GamepadNintendo => "GamepadNintendo",
        GamepadGeneric => "GamepadGeneric",
        Other => "Other",
        _ => "device " + device.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The device the game's "Controller icons" setting forces on every player (<c>SystemSettings._controllerIcon</c>:
    /// 0 is automatic detection, n is device type n - 1), or -1 when it's automatic.
    /// </summary>
    public static int ForcedDevice(int controllerIconSetting) => controllerIconSetting > 0 ? controllerIconSetting - 1 : -1;

    /// <summary>
    /// Pins every player's prompts to <paramref name="family"/>, a device type the game draws (<see cref="IsKnown"/>). The
    /// counters and the screens logged start again; the shadow is unknown until <see cref="TrackGame"/>.
    /// </summary>
    public void Pin(int family)
    {
        if (!IsKnown(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, "Not a device type the game draws prompts for.");
        }

        Pinned = true;
        Family = family;
        ShadowKnown = false;
        _shadow = Array.Empty<int>();
        ShadowDefault = Other;
        ShadowChanges = 0;
        Drawn = 0;
        NotFound = 0;
        ScreensLogged = 0;
        ScreensNotLogged = 0;
        _screens.Clear();
    }

    /// <summary>The friend picked up another device mid-session: prompts follow it. Ignored when unpinned.</summary>
    public void Refamily(int family)
    {
        if (!IsKnown(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, "Not a device type the game draws prompts for.");
        }

        if (Pinned)
        {
            Family = family;
        }
    }

    /// <summary>The session ended. The shadow is kept, so the game's own choice can be put back.</summary>
    public void Unpin()
    {
        Pinned = false;
        Family = Other;
    }

    /// <summary>
    /// Starts following the game's own choice from its state just before the pin is applied: each game player's device
    /// (<paramref name="players"/>, at most <see cref="MaxPlayers"/>) and the default's.
    /// </summary>
    public void TrackGame(IReadOnlyList<int> players, int defaultDevice)
    {
        ArgumentNullException.ThrowIfNull(players);
        int count = Math.Min(players.Count, MaxPlayers);
        _shadow = new int[count];
        for (int i = 0; i < count; i++)
        {
            _shadow[i] = players[i];
        }

        ShadowDefault = defaultDevice;
        ShadowKnown = true;
    }

    /// <summary>What the game's own choice would be now for <paramref name="gamePlayer"/> (out of range: the default's).</summary>
    public int Shadow(int gamePlayer) => (uint)gamePlayer < (uint)_shadow.Length ? _shadow[gamePlayer] : ShadowDefault;

    /// <summary>
    /// The game's automatic detection chose <paramref name="device"/> for <paramref name="gamePlayer"/> (-1: no game
    /// player), as its <c>RefreshIconsMode</c> does: <c>ChangeControllingDevice(device, gamePlayer, alsoSetDefaultData:
    /// true)</c>, so a game player in range and the default both take it, and otherwise only the default.
    /// </summary>
    public void GameChose(int device, int gamePlayer)
    {
        if (!ShadowKnown)
        {
            return;
        }

        bool changed = false;
        if ((uint)gamePlayer < (uint)_shadow.Length && _shadow[gamePlayer] != device)
        {
            _shadow[gamePlayer] = device;
            changed = true;
        }

        if (ShadowDefault != device)
        {
            ShadowDefault = device;
            changed = true;
        }

        if (changed && Pinned)
        {
            ShadowChanges++;
        }
    }

    /// <summary>The game's "Controller icons" setting forced <paramref name="device"/> on every player and the default.</summary>
    public void GameForced(int device)
    {
        if (!ShadowKnown)
        {
            return;
        }

        bool changed = ShadowDefault != device;
        ShadowDefault = device;
        for (int i = 0; i < _shadow.Length; i++)
        {
            changed |= _shadow[i] != device;
            _shadow[i] = device;
        }

        if (changed && Pinned)
        {
            ShadowChanges++;
        }
    }

    /// <summary>True when a prompt the game asks for on <paramref name="requestedDevice"/> is the pin's to draw.</summary>
    public bool Draws(int requestedDevice) => Pinned && requestedDevice == Family;

    /// <summary>Counts a prompt the pin drew: <paramref name="found"/> is false when the friend's maps had no binding for it.</summary>
    public void Drew(bool found)
    {
        Drawn++;
        if (!found)
        {
            NotFound++;
        }
    }

    /// <summary>
    /// True the first time a prompt is drawn on a kind of screen (a scene and an action category) while pinned, so that
    /// screen gets its one log line; at most the cap's worth per pinned session, the rest counted.
    /// </summary>
    public bool FirstOnScreen(string scene, int category)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!Pinned || _screens.Contains((scene, category)))
        {
            return false;
        }

        if (_screens.Count >= _maxScreens)
        {
            ScreensNotLogged++;
            return false;
        }

        _screens.Add((scene, category));
        ScreensLogged++;
        return true;
    }

    /// <summary>The shadow for the log: "default MouseKeyboard; game players 0 MouseKeyboard, 1-10 GamepadGeneric".</summary>
    public string DescribeShadow()
    {
        if (!ShadowKnown)
        {
            return "not known";
        }

        var text = new StringBuilder("default ").Append(DeviceName(ShadowDefault));
        if (_shadow.Length == 0)
        {
            return text.ToString();
        }

        text.Append(_shadow.Length == 1 ? "; game player " : "; game players ");
        int start = 0;
        for (int i = 1; i <= _shadow.Length; i++)
        {
            if (i < _shadow.Length && _shadow[i] == _shadow[start])
            {
                continue;
            }

            if (start > 0)
            {
                text.Append(", ");
            }

            text.Append(start.ToString(CultureInfo.InvariantCulture));
            if (i - 1 > start)
            {
                text.Append('-').Append((i - 1).ToString(CultureInfo.InvariantCulture));
            }

            text.Append(' ').Append(DeviceName(_shadow[start]));
            start = i;
        }

        return text.ToString();
    }
}
