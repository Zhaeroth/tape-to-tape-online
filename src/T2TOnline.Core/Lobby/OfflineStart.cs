using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>Why the developer's offline start (<see cref="OfflineStart"/>) starts the mod as it does.</summary>
public enum OfflineStartReason
{
    /// <summary>No offline start: the mod starts as its checks say.</summary>
    None,

    /// <summary>Online off from start-up, as for a reason the offline shell says (the runtime never starts).</summary>
    Steam,

    /// <summary>As <see cref="Steam"/>, and the Settings tab's rows are told Steam refused relays alone (Direct Connections shows).</summary>
    Relay,

    /// <summary>Online on at start-up; the tour's first step stops the runtime as an error would (one ERROR line, staged).</summary>
    Error,
}

/// <summary>
/// The developer's offline start (<c>run.txt</c>: <c>offline reason=steam|relay|error</c>, a check tour's line): the mod starts as if
/// online were off for that reason, so a tour walks the native screens in the states a player needs them most. Read at start-up, with
/// the developer tools on, from the same line the tour reads later; never reachable without them. <see cref="OfflineStartReason.Steam"/> and
/// <see cref="OfflineStartReason.Relay"/> take the start-up's own way to online off (the offline shell, its notice), with words that say
/// it was the developer's start; <see cref="OfflineStartReason.Error"/> takes the runtime's own stop after an error. Steam not running
/// and Steam refusing relays alone are, for real, states with the runtime up (the session refuses): these reasons give the offline
/// shell those reasons' inputs, not their session.
/// </summary>
public static class OfflineStart
{
    /// <summary>Its tour's name in <c>run.txt</c>.</summary>
    public const string TourName = "offline";

    /// <summary>The key that names the reason.</summary>
    public const string ReasonKey = "reason";

    /// <summary>What the offline notice and the log name as the advice (developers only ever see it).</summary>
    public const string Advice = "Only a check tour's run.txt starts T2T Online this way; the next start is a normal one.";

    /// <summary>The tour's time limit (its walk's steps come with the screens' tours).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>The key, with the reasons it takes.</summary>
    public static readonly UiCheckKey Key = UiCheckKey.Choice(ReasonKey, "why online is off for this start: steam or relay from start-up, error after the runtime stops", "steam", "relay", "error");

    /// <summary>
    /// Its steps, in order: the state first (every walk after it reads that state), then the screens a player needs most in it, by the game's own
    /// navigation (no pad: the shell has none). All run in the player's own language and window.
    /// </summary>
    public static IReadOnlyList<UiCheckStep> Steps { get; } = new[]
    {
        new UiCheckStep("off_state", "The state the reason asks for (for error, the runtime stopped as an error stops it): online off, the native screens' layer and the Settings tab in, a game prompt found and closed through the layer", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("off_settings", "The title's own Settings, to Online by the game's tab button: the rows this state shows (no Leave or End, Direct Connections by the rule, Save Backups open at the title); after an error the cursor on Problem Report", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("off_report", "Problem Report by the game's confirm: the zip, then the game's prompt naming it with Show File (at the title); closed", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("off_backups", "Save Backups by the game's confirm: the Backups screen read (both tabs, every row), one row's Restore asked and cancelled; nothing restored", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("off_offer", "The offer of a report at the idle title (due after an error; asked for by the tour): the game's yes/no, answered Not Now", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("off_notice", "The offline notice's lines: none names F1 or the panel; the way to report it names", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>The tour, for <see cref="UiCheckPlan.Tours"/>.</summary>
    public static UiCheckTour Tour { get; } = new(TourName, "The mod started as if online were off (steam, relay) or as after an error (error): the native screens in that state", Steps, Limit, new[] { Key });

    /// <summary>
    /// The reason the first line of <c>run.txt</c> asks for: its tour must be <see cref="TourName"/> with a <see cref="ReasonKey"/>; any other
    /// line, or one <paramref name="tours"/> refuses, is <see cref="OfflineStartReason.None"/>. Never throws on what the line holds.
    /// </summary>
    public static OfflineStartReason FromRunLine(string? line, IReadOnlyList<UiCheckTour> tours)
    {
        ArgumentNullException.ThrowIfNull(tours);
        if (!UiCheckRequest.TryParse(line, tours, out UiCheckRequest? request, out _) || request == null
            || !string.Equals(request.Tour.Name, TourName, StringComparison.Ordinal))
        {
            return OfflineStartReason.None;
        }

        return Parse(request.Value(ReasonKey));
    }

    /// <summary>The reason a value names (<c>steam</c>, <c>relay</c>, <c>error</c>), or <see cref="OfflineStartReason.None"/>.</summary>
    public static OfflineStartReason Parse(string? value) => value switch
    {
        "steam" => OfflineStartReason.Steam,
        "relay" => OfflineStartReason.Relay,
        "error" => OfflineStartReason.Error,
        _ => OfflineStartReason.None,
    };

    /// <summary>The reason's value as <c>run.txt</c> and the log name it; empty for none.</summary>
    public static string Name(OfflineStartReason reason) => reason switch
    {
        OfflineStartReason.Steam => "steam",
        OfflineStartReason.Relay => "relay",
        OfflineStartReason.Error => "error",
        _ => string.Empty,
    };

    /// <summary>True when the reason keeps the runtime from starting (online off from start-up): <c>steam</c> and <c>relay</c>.</summary>
    public static bool StartsShell(OfflineStartReason reason) => reason is OfflineStartReason.Steam or OfflineStartReason.Relay;

    /// <summary>True when the reason tells the Settings tab's rows that Steam refused relays alone on this PC.</summary>
    public static bool RelayRefused(OfflineStartReason reason) => reason == OfflineStartReason.Relay;

    /// <summary>Why online is off, for the offline notice and the log: "the developer's offline start (steam)".</summary>
    public static string OffReason(OfflineStartReason reason) => $"the developer's offline start ({Name(reason)})";

    /// <summary>The start-up line: <c>Online: off for this start: the developer's offline start (steam).</c></summary>
    public static string StartLine(OfflineStartReason reason) => $"Online: off for this start: {OffReason(reason)}.";

    /// <summary>The line when online is off already for another reason, which the offline start leaves as it is.</summary>
    public static string IgnoredLine(OfflineStartReason reason, string offAlready) =>
        $"Online: {OffReason(reason)} changes nothing: online is off already ({offAlready}).";

    /// <summary>The line just before the runtime's staged stop (<see cref="OfflineStartReason.Error"/>).</summary>
    public const string StopLine = "Online: the developer's offline start (error): the runtime stops now, as after an error; the ERROR line that follows is staged for the check tour.";

    /// <summary>The message of the exception the staged stop throws, which the ERROR line carries.</summary>
    public const string StopMessage = "the developer's offline start (error): the runtime stops as after an error";
}
