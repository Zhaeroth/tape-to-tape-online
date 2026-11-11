using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// The one rule that matches the game's language name (I2's <c>LocalizationManager.CurrentLanguage</c>, or a column of its
/// source) to a place in <see cref="LobbyWords.Languages"/>: both the mod's lines in this PC's language and its words in the
/// game's source go by it, so the two never disagree. Game-independent.
/// </summary>
public static class GameLanguages
{
    /// <summary>
    /// The place in <see cref="LobbyWords.Languages"/> of the language <paramref name="name"/> names, or -1. A name matches the
    /// first language it starts with, case ignored: the game's own names match exactly, and a region variant a game update
    /// might add ("Spanish (Mexico)") reads as its language. No language's name starts another's, so the order doesn't matter.
    /// </summary>
    public static int IndexOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return -1;
        }

        IReadOnlyList<string> languages = LobbyWords.Languages;
        for (int i = 0; i < languages.Count; i++)
        {
            if (name.StartsWith(languages[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
