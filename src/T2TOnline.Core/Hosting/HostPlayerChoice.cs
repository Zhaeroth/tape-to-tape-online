namespace T2TOnline.Core.Hosting;

/// <summary>
/// Which of the players the game's UI tracks is the host's own (D45; HostDecides in the plugin): the first on one of this
/// PC's pads, else the first on its keyboard or mouse, never a friend's (a virtual pad). Offer each tracked player in order
/// with <see cref="Consider"/>, then read <see cref="Chosen"/>.
/// </summary>
public struct HostPlayerChoice
{
    // Index + 1 of the first candidate of each kind; 0 for none.
    private int _pad;
    private int _keyboard;

    /// <summary>The tracked player at <paramref name="index"/>: whether it's a friend's, and what controls it has.</summary>
    public void Consider(int index, bool friends, bool hasPad, bool hasKeyboardOrMouse)
    {
        if (friends || index < 0)
        {
            return;
        }

        if (hasPad)
        {
            if (_pad == 0)
            {
                _pad = index + 1;
            }
        }
        else if (hasKeyboardOrMouse && _keyboard == 0)
        {
            _keyboard = index + 1;
        }
    }

    /// <summary>True once a pad player was found: no later candidate can be chosen over it.</summary>
    public readonly bool HasPad => _pad != 0;

    /// <summary>The chosen player's index (a pad player first, else a keyboard player), or -1 when there's none.</summary>
    public readonly int Chosen => _pad != 0 ? _pad - 1 : _keyboard - 1;
}
