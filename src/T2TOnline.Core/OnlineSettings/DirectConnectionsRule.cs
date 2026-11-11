using System;
using System.Globalization;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>What a press of the Direct Connections row does.</summary>
public enum DirectConnectionsPress
{
    /// <summary>Nothing changes (the row shows what is saved).</summary>
    Nothing,

    /// <summary>Turning it on: the game's yes/no asks first (Cancel first); only Turn On saves it.</summary>
    Ask,

    /// <summary>Turning it off: saved at once (relays are the safe side).</summary>
    TurnOff,

    /// <summary>A check tour runs: it never changes, the row goes back.</summary>
    Refuse,

    /// <summary>
    /// Turning it on while someone else is in this PC's online game: not asked, the row goes back and the game's notice says to leave first.
    /// The question is the player's own consent, and in a session another player's press can reach this PC's menus.
    /// </summary>
    RefuseInSession,
}

/// <summary>
/// Direct Connections (D133, D139), safe by construction: relays stay the default; the row shows only where it's needed (Steam refused relay
/// only on this PC, or it's on so it can be turned off); turning it on asks first and takes effect at the next start; turning it off is at once;
/// a check tour never changes it. Turning it on is the player's own consent to show their address (D133): the question is never put while
/// someone else is in this PC's online game (hosting with a friend in it, or in a host's), where a friend's press can answer a menu on the
/// host's screen; it is turned on out of a session, and turning it off is allowed at any time (it only narrows).
/// </summary>
public static class DirectConnectionsRule
{
    /// <summary>The marker's file name, in the mod's data folder: written when Steam refused relay only at a start, gone when relay only held again.</summary>
    public const string MarkerFileName = "relay-refused.txt";

    private const string MarkerStart = "Steam refused relay-only connections";

    /// <summary>
    /// The press of the row from <paramref name="savedOn"/> to <paramref name="toggledTo"/> (the toggle flips before it tells);
    /// <paramref name="othersInSession"/>: someone else is in this PC's online game (<see cref="OthersInSession"/>). A press that
    /// would turn it on with others in the game is refused for the session whether a check tour runs or not (both refusals
    /// leave it off; the session's says why on the game's notice); every other press during a tour is the tour's refusal.
    /// </summary>
    public static DirectConnectionsPress OnPress(bool savedOn, bool toggledTo, bool tourRunning, bool othersInSession) =>
        tourRunning && othersInSession && toggledTo && !savedOn ? DirectConnectionsPress.RefuseInSession
        : tourRunning ? DirectConnectionsPress.Refuse
        : toggledTo == savedOn ? DirectConnectionsPress.Nothing
        : !toggledTo ? DirectConnectionsPress.TurnOff
        : othersInSession ? DirectConnectionsPress.RefuseInSession
        : DirectConnectionsPress.Ask;

    /// <summary>
    /// True when someone else is in this PC's online game: it is in a host's session (<see cref="SettingsRole.Friend"/>, the lobby included), or
    /// it hosts one with <paramref name="othersHosted"/> others in it (lobby members but this PC, or friends' players still seated). A host alone
    /// in their lobby, and a PC in no session, have no one else.
    /// </summary>
    public static bool OthersInSession(SettingsRole sessionRole, int othersHosted) =>
        sessionRole == SettingsRole.Friend || (sessionRole == SettingsRole.Host && othersHosted > 0);

    /// <summary>
    /// True when the question's Turn On may be saved as the answer comes: someone who joined while the question was up makes it no one's
    /// consent (<paramref name="othersInSession"/> then), and a check tour never changes it.
    /// </summary>
    public static bool MayTurnOn(bool tourRunning, bool othersInSession) => !tourRunning && !othersInSession;

    /// <summary>The marker's text: when, in UTC, and nothing else (no names, no paths).</summary>
    public static string MarkerText(DateTime utc) =>
        string.Create(CultureInfo.InvariantCulture, $"{MarkerStart} at {utc.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ} (UTC).\n");

    /// <summary>True when <paramref name="text"/> is a marker this mod wrote.</summary>
    public static bool IsMarker(string? text) => text != null && text.StartsWith(MarkerStart, StringComparison.Ordinal);
}
