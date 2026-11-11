using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// A Steam name for the mod's own texts on the screen (a notice, a toast, a prompt, a friend's pad): cleaned by the
/// name label's rules (<see cref="DisplayName.Clean"/>: typed escapes, controls, direction and format characters gone,
/// spaces tidied), so it can't restyle a line wherever it is drawn. A name with nothing drawable left (blank,
/// <see cref="DisplayName.SteamUnknown"/>, only marks or controls) gets no "Player N" stand-in here: it comes back as
/// <see cref="SafeText.Clean"/> makes it, which a caller already treats as no name when it is empty.
/// </summary>
public static class ScreenName
{
    // DisplayName's stand-ins for a name with nothing left, as it writes them (language 0) for players 1 and 2.
    private static readonly string StandInOne = LobbyWords.PlayerN.Text(0, "1");
    private static readonly string StandInTwo = LobbyWords.PlayerN.Text(0, "2");

    /// <summary><paramref name="raw"/> cleaned for the screen (see the class), cut at <paramref name="maxChars"/> with an ellipsis. Never throws.</summary>
    /// <param name="raw">The name as Steam gives it.</param>
    /// <param name="maxChars">The cut, at least 2.</param>
    public static string Of(string? raw, int maxChars = DisplayName.MaxChars)
    {
        int max = Math.Max(2, maxChars);
        string drawn = DisplayName.Clean(raw, 1, maxChars: max);
        if (drawn != StandInOne || DisplayName.Clean(raw, 2, maxChars: max) != StandInTwo)
        {
            return drawn; // a name, even one that reads "Player 1"
        }

        return SafeText.Clean(raw, max);
    }
}
