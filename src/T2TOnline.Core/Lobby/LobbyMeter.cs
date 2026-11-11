using System;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>How someone got into a lobby.</summary>
public enum LobbyRoute
{
    /// <summary>The Host Lobby row.</summary>
    HostRow,

    /// <summary>A row of the Join list.</summary>
    Browser,

    /// <summary>A Steam invite accepted in game.</summary>
    Invite,

    /// <summary>Join Game on a friend in Steam.</summary>
    JoinGame,

    /// <summary>Steam launched the game for an invite (<c>+connect_lobby</c>).</summary>
    ConnectLobby,

    /// <summary>F7 or Shift+F7.</summary>
    Key,

    /// <summary>The F1 panel.</summary>
    Panel,
}

/// <summary>
/// What a play test of the lobby watches, as counts and log lines: presses and seconds from the title to
/// a lobby, the route, how failures and Start's prompts went, and whether anything the lobby offers goes unused. The plugin
/// feeds it and logs its lines. Every line is plain and pasted into diagnosis: a player in one is their log tag, never a name,
/// and cleaned (<see cref="Diagnostics.SafeText"/>) all the same.
/// </summary>
public sealed class LobbyMeter
{
    private long _startedAt = long.MinValue;
    private int _presses;

    /// <summary>Times the countdown was cancelled.</summary>
    public int CountdownCancels { get; private set; }

    /// <summary>Times a ready mark was flipped here.</summary>
    public int ReadyMarks { get; private set; }

    /// <summary>The leave or close prompt answered "stay".</summary>
    public int LeavePromptStayed { get; private set; }

    /// <summary>The leave or close prompt answered "leave".</summary>
    public int LeavePromptLeft { get; private set; }

    /// <summary>Times nothing was selected, and it was repaired. Any is a bug.</summary>
    public int FocusRepairs { get; private set; }

    /// <summary>The Online row was pressed: presses (this one included) and time count from here. (A second press while counting starts over.)</summary>
    public void OnlineOpened(long nowMicros)
    {
        _startedAt = nowMicros;
        _presses = 1;
    }

    /// <summary>A press on the way to a lobby.</summary>
    public void Press()
    {
        if (_startedAt != long.MinValue)
        {
            _presses++;
        }
    }

    /// <summary>The countdown was cancelled.</summary>
    public void CountdownCancelled() => CountdownCancels++;

    /// <summary>A ready mark was flipped.</summary>
    public void ReadyToggled() => ReadyMarks++;

    /// <summary>The leave or close prompt was answered.</summary>
    public void LeavePromptAnswered(bool left)
    {
        if (left)
        {
            LeavePromptLeft++;
        }
        else
        {
            LeavePromptStayed++;
        }
    }

    /// <summary>Nothing was selected, and it was repaired.</summary>
    public void FocusRepaired() => FocusRepairs++;

    /// <summary>A lobby was reached: <c>Lobby: entered by browser, 5 presses, 6.1 s</c>, or for a route with no presses, <c>Lobby: arrived by invite</c>.</summary>
    public string Entered(LobbyRoute route, long nowMicros)
    {
        string how = route switch
        {
            LobbyRoute.HostRow => "the Host Lobby row",
            LobbyRoute.Browser => "browser",
            LobbyRoute.Invite => "invite",
            LobbyRoute.JoinGame => "Join Game",
            LobbyRoute.ConnectLobby => "a launch from an invite",
            LobbyRoute.Key => "key",
            _ => "the F1 panel",
        };
        long started = _startedAt;
        _startedAt = long.MinValue;
        return started == long.MinValue || route is not (LobbyRoute.HostRow or LobbyRoute.Browser)
            ? $"Lobby: arrived by {how}."
            : string.Create(CultureInfo.InvariantCulture, $"Lobby: entered by {how}, {_presses} presses, {Math.Max(0, nowMicros - started) / 1_000_000.0:0.0} s.");
    }

    /// <summary>A join that failed: <c>Lobby: join failed: Full (row age 3 s)</c>. Codes and reasons go here, never on the screen.</summary>
    public static string JoinFailed(LobbyFailure reason, int rowAgeSeconds) =>
        rowAgeSeconds < 0 ? $"Lobby: join failed: {reason}." : string.Create(CultureInfo.InvariantCulture, $"Lobby: join failed: {reason} (row age {rowAgeSeconds} s).");

    /// <summary>
    /// Start was pressed with someone not here, and waits for them: <c>Lobby: start waits: P#a1b2c3 isn't here yet; it goes by
    /// itself when they are.</c> <paramref name="who"/> is their log tag, never a name (<see cref="LobbyPeople.FirstNotHere"/>).
    /// </summary>
    public static string StartWaits(string who) => $"Lobby: start waits: {Diagnostics.SafeText.Clean(who)} isn't here yet; it goes by itself when they are.";

    /// <summary>
    /// A Start with someone not here: <c>Lobby: start: P#a1b2c3 not here, waited 4 s</c>, or that it went on anyway.
    /// <paramref name="who"/> is their log tag, never a name (<see cref="LobbyPeople.FirstNotHere"/>).
    /// </summary>
    public static string StartedWithout(string who, long waitedMicros, bool wentOnAnyway) => wentOnAnyway
        ? $"Lobby: start: {Diagnostics.SafeText.Clean(who)} not here, started anyway."
        : string.Create(CultureInfo.InvariantCulture, $"Lobby: start: {Diagnostics.SafeText.Clean(who)} not here, waited {waitedMicros / 1_000_000} s.");

    /// <summary>The counts for the session's end, in one line: <c>Lobby: ready marks: 2; countdown cancels: 0; leave prompt: stayed 1, left 0; focus repaired: 0.</c></summary>
    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"Lobby: ready marks: {ReadyMarks}; countdown cancels: {CountdownCancels}; leave prompt: stayed {LeavePromptStayed}, left {LeavePromptLeft}; focus repaired: {FocusRepairs}.");
}
