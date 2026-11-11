using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// A controller screen player's row as the host sends it in the menu's data (D69): what a friend's real screen puts into
/// its icons (<see cref="ControllerScreenState"/>) and what its text panel says when the screen is text. Built here, not in
/// the game glue, so its size is tested against <see cref="MenuData.MaxItemValues"/>: a row with more values than the limit
/// can't be made, and every controller screen with a player on it would go to friends without its text.
/// </summary>
/// <remarks>
/// Version 2 (the real screen on friends): the icon's side, locked position, disabled flag and prompt device as the game's
/// own numbers, and a row for every icon, disabled ones included.
/// </remarks>
public static class ControllerScreenRow
{
    /// <summary>The rows' group in the menu's data.</summary>
    public const string Group = "player";

    /// <summary>How many values a row has (within <see cref="MenuData.MaxItemValues"/>).</summary>
    public const int ValueCount = 16;

    /// <summary>Row value: the game player (slot) the icon is for.</summary>
    public const string SlotKey = "slot";

    /// <summary>Row value: the icon's side as the game's number (<c>ControllerAssignmentPlayer.ESide</c>: left 0, centre 1, right 2).</summary>
    public const string SideKey = "sidev";

    /// <summary>Row value: the icon's locked position as the game's number (<c>ESkaterPosition</c>, 0 to 6; 6 is none).</summary>
    public const string PositionKey = "posv";

    /// <summary>Row value: the icon is disabled (flag).</summary>
    public const string OffKey = "off";

    /// <summary>
    /// Row value: the prompt device the host's game draws that player's button glyphs with (<c>EInputDeviceType</c>), or -1
    /// when it doesn't know.
    /// </summary>
    public const string DeviceKey = "dev";

    /// <summary>Row value: the friend whose controller it is (their Steam ID, as a whole number; 0 for the host's own).</summary>
    public const string SteamKey = "steam";

    /// <summary>Row value: the icon's colour as one RGBA value (-1 when unknown).</summary>
    public const string ColourKey = "color";

    /// <summary>Row value: locked in (the game's ready, flag).</summary>
    public const string ReadyKey = "ready";

    /// <summary>Row value: the game's keyboard flag (flag).</summary>
    public const string KeyboardKey = "keyboard";

    /// <summary>Row value: nobody controls the icon (the AI plays it; flag).</summary>
    public const string AiKey = "ai";

    /// <summary>
    /// A player's row: <paramref name="text"/> is the host's own line for it (a friend writes its own from the rest),
    /// <paramref name="slot"/> the game player, <paramref name="name"/> and <paramref name="steam"/> the friend whose
    /// controller it is (empty and 0 for the host's own), <paramref name="color"/> its marker's colour as one RGBA value
    /// (-1 when unknown), <paramref name="keyboard"/> the game's own keyboard flag, <paramref name="numbers"/> the icon's
    /// state as the game's numbers. Throws, as <see cref="MenuItem"/> does, for a row the protocol can't carry.
    /// </summary>
    public static MenuItem Player(string text, int slot, string name, ulong steam, string side, string position, string skater, string device,
        bool ai, long color, bool ready, bool keyboard, ControllerRowNumbers numbers) =>
        new(Group, new[]
        {
            Value("text", NetValue.FromString(text)),
            Value(SlotKey, NetValue.FromInt(slot)),
            Value("name", NetValue.FromString(name)),
            Value(SteamKey, NetValue.FromInt(unchecked((long)steam))),
            Value("side", NetValue.FromString(side)),
            Value("position", NetValue.FromString(position)),
            Value("skater", NetValue.FromString(skater)),
            Value("device", NetValue.FromString(device)),
            Value(AiKey, NetValue.FromBool(ai)),
            Value(ColourKey, NetValue.FromInt(color)),
            Value(ReadyKey, NetValue.FromBool(ready)),
            Value(KeyboardKey, NetValue.FromBool(keyboard)),
            Value(SideKey, NetValue.FromInt(numbers.Side)),
            Value(PositionKey, NetValue.FromInt(numbers.Position)),
            Value(OffKey, NetValue.FromBool(numbers.Off)),
            Value(DeviceKey, NetValue.FromInt(numbers.Device)),
        });

    private static KeyValuePair<string, NetValue> Value(string key, NetValue value) => new(key, value);
}

/// <summary>A controller screen icon's state as the game's own numbers (row version 2).</summary>
/// <param name="Side">The side (<c>ESide</c>: left 0, centre 1, right 2).</param>
/// <param name="Position">The locked position (<c>ESkaterPosition</c>, 0 to 6; 6 is none).</param>
/// <param name="Off">The icon is disabled.</param>
/// <param name="Device">The prompt device the host draws the player's glyphs with (<c>EInputDeviceType</c>), or -1.</param>
public readonly record struct ControllerRowNumbers(int Side, int Position, bool Off, int Device);
