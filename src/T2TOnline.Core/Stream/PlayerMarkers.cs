using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Stream;

/// <summary>One of the host's players, as its <c>players</c> HUD value lists them: its game player id and Rewired player.</summary>
/// <param name="Id">The game player id (<c>Tape2Tape.Player.PlayerId</c>, from 0).</param>
/// <param name="Rewired">The Rewired player it reads on the host (-1: none).</param>
public readonly record struct HostPlayer(int Id, int Rewired);

/// <summary>What a marker check found wrong with a skater's off-screen box on a friend's PC, and the game's own fix for it.</summary>
public enum MarkerFix
{
    /// <summary>Nothing: hidden, or shown in its controlling player's colour.</summary>
    None,

    /// <summary>Shown in a colour that isn't its controlling player's: the game's own <c>FillStar</c> redraws it.</summary>
    Recolour,

    /// <summary>Shown though no player of the host's controls the skater: the game's own <c>RemoveStar</c> hides it.</summary>
    Hide,
}

/// <summary>What a marker check does about a shown off-screen box's face icon on a friend's PC (<see cref="PlayerMarkers.CheckFaceIcon"/>).</summary>
public enum FaceIconFix
{
    /// <summary>Nothing: the icon is off, or shows a sprite.</summary>
    None,

    /// <summary>The icon is on with no sprite (Unity draws it as a white square): ask the game for the face again.</summary>
    Retry,

    /// <summary>Still no face after the retries: the icon is hidden, so the box shows its player's colour.</summary>
    Hide,
}

/// <summary>
/// The players' colour markers on a friend's PC, as far as they're pure logic (the plugin's
/// <c>Streaming/PlayerProxies</c> reads and writes the game). The host lists its players in the <c>players</c> HUD value
/// and sends each one's controlled skater and star colours; a friend's stand-in players answer the game's "who controls
/// this skater" with them. The game draws a player's colour only when an event tells a widget to (a player taking or
/// leaving a skater, the puck changing hands), so a friend whose stream starts after the host's players took their
/// skaters redraws the markers once, from the stand-ins, in the game states where the host shows them.
/// </summary>
public static class PlayerMarkers
{
    /// <summary>The highest game player id taken from the network (the game hands out 0 to 10).</summary>
    public const int MaxPlayerId = 32;

    /// <summary>The most entries read from one list (the game has at most 11 players).</summary>
    public const int MaxListed = 16;

    /// <summary>The longest list text read (16 entries of "32:10," and then some).</summary>
    public const int MaxListLength = 256;

    /// <summary>How far apart two colour channels may be and still be the same colour (a float sent as is, or rounded by the UI).</summary>
    public const float ColourTolerance = 0.01f;

    /// <summary>The game's <c>GameState.GoalScored</c> (a goal and its replay: the markers are down).</summary>
    public const int GoalScoredState = 7;

    /// <summary>The game's <c>GameState.EndOfPeriod</c>.</summary>
    public const int EndOfPeriodState = 8;

    /// <summary>The game's <c>GameState.EndOfGame</c>.</summary>
    public const int EndOfGameState = 9;

    /// <summary>
    /// Parses the host's player list ("0:0,1:1,2:2": game player, colon, Rewired player). Network input: never throws;
    /// malformed entries, ids outside 0..<see cref="MaxPlayerId"/>, a repeated id (the first counts) and anything past
    /// <see cref="MaxListed"/> entries or <see cref="MaxListLength"/> characters are left out.
    /// </summary>
    public static IReadOnlyList<HostPlayer> ParseList(string? text)
    {
        var players = new List<HostPlayer>();
        if (string.IsNullOrEmpty(text))
        {
            return players;
        }

        string list = text.Length > MaxListLength ? text[..MaxListLength] : text;
        foreach (string entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (players.Count >= MaxListed)
            {
                break;
            }

            int colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon != entry.LastIndexOf(':') ||
                !int.TryParse(entry.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                !int.TryParse(entry.AsSpan(colon + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int rewired) ||
                id > MaxPlayerId || rewired < -1 || players.Exists(p => p.Id == id))
            {
                continue;
            }

            players.Add(new HostPlayer(id, rewired));
        }

        return players;
    }

    /// <summary>
    /// Whether the host shows the players' markers in game state <paramref name="gameState"/>: not during a goal and its
    /// replay or at a period's or the game's end (the game's <c>PlayerIndicator</c> takes them down then, and the next
    /// faceoff's events put them back).
    /// </summary>
    public static bool ShownIn(int gameState) => gameState is not (GoalScoredState or EndOfPeriodState or EndOfGameState);

    /// <summary>
    /// Checks one skater's off-screen box on a friend's PC. The host's box is shown only for a skater a player controls,
    /// in that player's colour (the game writes both together), so: a box shown for a skater no stand-in controls is
    /// hidden; one shown in another colour is redrawn. A hidden box is left alone: the game hides a carrier's box, and
    /// shows it again by its own events.
    /// </summary>
    /// <param name="shown">The box (<c>OffscreenTarget</c>) is enabled.</param>
    /// <param name="colour">Its colour (<c>OffscreenTarget.targetColor</c>).</param>
    /// <param name="controller">The controlling stand-in's colour, or null when none controls the skater.</param>
    public static MarkerFix CheckOffscreenBox(bool shown, ColorValue colour, ColorValue? controller) =>
        !shown ? MarkerFix.None
        : controller is not ColorValue expected ? MarkerFix.Hide
        : SameColour(colour, expected, withAlpha: true) ? MarkerFix.None
        : MarkerFix.Recolour;

    /// <summary>How many times a friend's PC asks the game again for one skater's face per playback before hiding its icon.</summary>
    public const int MaxFaceRetries = 2;

    /// <summary>
    /// Checks a shown off-screen box's face icon on a friend's PC. The box's coloured
    /// image always takes its player's colour; the only part of it that can draw white is the face icon
    /// (<c>Indicator.indicatorIcon</c>, a UGUI Image): the game's <c>OffscreenTarget.UpdateFaceVisual</c> turns it on for a
    /// skater, then sets the face sprite it gets, without checking for none, and an Image with no sprite draws a plain
    /// square in its colour, white. An icon on with no sprite is asked for again with the game's own
    /// <c>UpdateFaceVisual</c>, up to <see cref="MaxFaceRetries"/> times (<paramref name="retries"/> so far); still none
    /// after that, it's hidden, so the box shows the player's colour (the host's shows a face).
    /// </summary>
    /// <param name="iconOn">The icon's object is active.</param>
    /// <param name="hasSprite">The icon has a sprite.</param>
    /// <param name="retries">How many times this skater's face was asked for again this playback.</param>
    public static FaceIconFix CheckFaceIcon(bool iconOn, bool hasSprite, int retries) =>
        !iconOn || hasSprite ? FaceIconFix.None
        : retries < MaxFaceRetries ? FaceIconFix.Retry
        : FaceIconFix.Hide;

    /// <summary>True when every channel (alpha too, if <paramref name="withAlpha"/>) is within <see cref="ColourTolerance"/>.</summary>
    public static bool SameColour(ColorValue a, ColorValue b, bool withAlpha) =>
        Close(a.R, b.R) && Close(a.G, b.G) && Close(a.B, b.B) && (!withAlpha || Close(a.A, b.A));

    /// <summary>A plain name for a marker colour ("orange"), for the status box and the log.</summary>
    public static string ColourName(ColorValue colour)
    {
        if (!colour.IsValid)
        {
            return "unreadable";
        }

        float r = colour.R, g = colour.G, b = colour.B;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float range = max - min;
        if (max < 0.2f)
        {
            return "black";
        }

        if (range / max < 0.2f)
        {
            return max > 0.8f ? "white" : "grey";
        }

        float hue = max == r ? 60 * (((g - b) / range) % 6)
            : max == g ? 60 * (((b - r) / range) + 2)
            : 60 * (((r - g) / range) + 4);
        hue = hue < 0 ? hue + 360 : hue;
        return hue switch
        {
            < 15 => "red",
            < 40 => "orange",
            < 70 => "yellow",
            < 165 => "green",
            < 195 => "cyan",
            < 255 => "blue",
            < 290 => "purple",
            < 335 => "pink",
            _ => "red",
        };
    }

    /// <summary>A colour for the log: its name and channels, "green (0.22, 0.75, 0.00)" (alpha only when it isn't 1).</summary>
    public static string Describe(ColorValue colour)
    {
        if (!colour.IsValid)
        {
            return "unreadable";
        }

        var text = new StringBuilder(ColourName(colour)).Append(" (")
            .Append(colour.R.ToString("0.00", CultureInfo.InvariantCulture)).Append(", ")
            .Append(colour.G.ToString("0.00", CultureInfo.InvariantCulture)).Append(", ")
            .Append(colour.B.ToString("0.00", CultureInfo.InvariantCulture));
        if (!Close(colour.A, 1f))
        {
            text.Append(", alpha ").Append(colour.A.ToString("0.00", CultureInfo.InvariantCulture));
        }

        return text.Append(')').ToString();
    }

    private static bool Close(float a, float b) => Math.Abs(a - b) <= ColourTolerance;
}

/// <summary>
/// Which mismatches a periodic check should act on: only one found the same way in two checks in a row. On a friend's
/// PC a HUD value (a player's new skater) is written just before the event that redraws that skater's marker, so a check
/// that runs between the two sees a mismatch the event is about to fix; a real one is still there a check later.
/// </summary>
/// <typeparam name="TKey">What is checked (a marker, by its object).</typeparam>
/// <typeparam name="TFinding">What was found wrong with it (compared for equality between checks).</typeparam>
public sealed class MarkerWatch<TKey, TFinding>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TFinding> _last = new();

    /// <summary>
    /// This check's finding for <paramref name="key"/>: fine (<paramref name="wrong"/> false), or <paramref name="finding"/>.
    /// True when the same finding was made at the previous check: act on it now (it's forgotten, so a fix that doesn't hold
    /// is seen twice again before the next one).
    /// </summary>
    public bool Confirm(TKey key, bool wrong, TFinding finding)
    {
        if (!wrong)
        {
            _last.Remove(key);
            return false;
        }

        if (_last.TryGetValue(key, out TFinding? before) && EqualityComparer<TFinding>.Default.Equals(before, finding))
        {
            _last.Remove(key);
            return true;
        }

        _last[key] = finding;
        return false;
    }

    /// <summary>Mismatches waiting for a second sighting.</summary>
    public int Waiting => _last.Count;

    /// <summary>Forgets everything (playback ended).</summary>
    public void Clear() => _last.Clear();
}
