namespace T2TOnline.Core.Lobby;

/// <summary>
/// A lobby UI object's name for the log. The invite list's rows are named after the friend's Steam ID and the Join list's after
/// the lobby's ID (<c>Friend7656119...</c>, <c>Lobby1097752...</c>: each row is found by it), and a line about the cursor
/// would write it (D127): such a name is written as the row it is.
/// </summary>
public static class UiObjectName
{
    /// <summary>The fewest trailing digits that make a name an ID's (a Steam ID has 17, a lobby ID 18; a check tour's rows have 1 or 2).</summary>
    public const int IdDigits = 15;

    /// <summary>
    /// <paramref name="name"/> as the log may write it: <c>a friend's row</c> for <c>Friend</c> and an ID, <c>a lobby's row</c>
    /// for <c>Lobby</c> and an ID, another name ending in an ID with <c>&lt;id&gt;</c> for it, anything else as it is.
    /// </summary>
    public static string ForLog(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        int digits = 0;
        while (digits < name.Length && name[name.Length - 1 - digits] is >= '0' and <= '9')
        {
            digits++;
        }

        if (digits < IdDigits)
        {
            return name;
        }

        string prefix = name[..^digits];
        return prefix switch
        {
            "Friend" => "a friend's row",
            "Lobby" => "a lobby's row",
            _ => prefix + "<id>",
        };
    }
}
