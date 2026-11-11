using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Input;

/// <summary>A Rewired player of a friend's own game that had the keyboard, the mouse or both before the mod took them.</summary>
/// <param name="PlayerId">The Rewired player (0 to <see cref="KeyboardOwners.MaxPlayerId"/>).</param>
/// <param name="Keyboard">True when it had the keyboard.</param>
/// <param name="Mouse">True when it had the mouse.</param>
public readonly record struct KeyboardOwner(int PlayerId, bool Keyboard, bool Mouse);

/// <summary>
/// The text of the file a keyboard-and-mouse friend's PC keeps while the mod holds their keyboard and mouse: which of their
/// own Rewired players had them. Written before they're taken and deleted once they're back. Rewired saves its controller
/// assignments, flushed at once, whenever a joystick connects or any controller disconnects, so if the game closes or
/// crashes before they're given back, its next start would load "no keyboard on player 0" and nothing would give it back:
/// the mod's next start does, from this file. One line per player: its id, then "keyboard", "mouse" or both.
/// </summary>
public static class KeyboardOwners
{
    /// <summary>Most players the file lists (Rewired players 0 to 10 in this game).</summary>
    public const int MaxOwners = 16;

    /// <summary>The highest Rewired player id a line may name.</summary>
    public const int MaxPlayerId = 63;

    /// <summary>Most characters read from the file; a longer one is malformed.</summary>
    public const int MaxLength = 4096;

    private const string Header = "# T2T Online: these Rewired players had the keyboard and mouse when an online session took them; given back at the next start if the session didn't.";

    /// <summary>The file's text for <paramref name="owners"/> (at most <see cref="MaxOwners"/>, each with the keyboard or the mouse).</summary>
    public static string Format(IEnumerable<KeyboardOwner> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var text = new StringBuilder(Header).Append('\n');
        int count = 0;
        foreach (KeyboardOwner owner in owners)
        {
            if (owner.PlayerId is < 0 or > MaxPlayerId || !(owner.Keyboard || owner.Mouse))
            {
                throw new ArgumentException($"Player {owner.PlayerId} with {(owner.Keyboard || owner.Mouse ? "an id out of range" : "neither the keyboard nor the mouse")}.", nameof(owners));
            }

            if (++count > MaxOwners)
            {
                throw new ArgumentException($"More than {MaxOwners} players.", nameof(owners));
            }

            text.Append(owner.PlayerId.ToString(CultureInfo.InvariantCulture));
            text.Append(owner.Keyboard ? " keyboard" : string.Empty);
            text.Append(owner.Mouse ? " mouse" : string.Empty);
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads the file's text. False, with nothing read, when it's malformed: too long, a line that isn't a player id followed
    /// by "keyboard" and/or "mouse", an id out of range or repeated, more than <see cref="MaxOwners"/> players. Blank lines
    /// and lines starting with '#' are skipped. Never throws.
    /// </summary>
    public static bool TryParse(string? text, out List<KeyboardOwner> owners)
    {
        owners = new List<KeyboardOwner>();
        if (text == null || text.Length > MaxLength)
        {
            return false;
        }

        var seen = new HashSet<int>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 2 or > 3 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id > MaxPlayerId || !seen.Add(id) || owners.Count >= MaxOwners)
            {
                owners.Clear();
                return false;
            }

            bool keyboard = false;
            bool mouse = false;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] == "keyboard" && !keyboard)
                {
                    keyboard = true;
                }
                else if (parts[i] == "mouse" && !mouse)
                {
                    mouse = true;
                }
                else
                {
                    owners.Clear();
                    return false;
                }
            }

            owners.Add(new KeyboardOwner(id, keyboard, mouse));
        }

        return true;
    }
}
