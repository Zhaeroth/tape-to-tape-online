using System;
using System.Globalization;

namespace T2TOnline.Core.Input;

/// <summary>How far a trigger was pulled at most (<see cref="TriggerCheck.Of"/>).</summary>
public enum TriggerOutcome
{
    /// <summary>It reached <see cref="PadUsage.AxisThreshold"/>: the game's trigger actions fire.</summary>
    Reached,

    /// <summary>It moved, but never past <see cref="PadUsage.AxisThreshold"/>: the game's trigger actions never fire, as on a couch.</summary>
    Under,

    /// <summary>It never moved past <see cref="TriggerCheck.MovedFloor"/>.</summary>
    NeverMoved,
}

/// <summary>
/// What a trigger's peak says, and what the player can do about it: a pad's RT can fail to reach a host's game
/// (a Steam-configured pad's, say) though RT goes through exactly LT's
/// code. The game's trigger actions need more than 0.50 (both input behaviours' button dead zone), on a couch too, so a
/// trigger that stops short is never rescaled; the player is told where it stops and where to fix it (Steam Input's layout
/// for the game, when the pad is set up by Steam Input). Game-independent.
/// </summary>
public static class TriggerCheck
{
    /// <summary>Below this a trigger hasn't moved (the dead zone Rewired's "Steam Controller" profile gives its triggers).</summary>
    public const float MovedFloor = 0.05f;

    /// <summary>
    /// Below this a trigger that moved is more likely brushed than pulled (a finger resting on a trigger the player doesn't use, while the other skates backwards): a
    /// peak there says nothing about the pad, so it stays in the log and the panel's check, and no notice calls the pad broken.
    /// </summary>
    public const float PulledFloor = 0.25f;

    /// <summary>
    /// Rewired's "Steam Controller" profile (its "Steam-configured controller"): what Rewired 1.1.62 reads a pad as when Steam
    /// Input sets it up (a <c>[STEAMCONFIGURED]</c> device with Windows Gaming Input on).
    /// </summary>
    public static readonly Guid SteamInputProfile = new("2694f4b9-9d84-4f55-9ee8-78fbba744b7d");

    /// <summary>True when Rewired's profile for a pad (its hardware type) is the one Steam Input's pads get.</summary>
    public static bool IsSteamInput(Guid profile) => profile == SteamInputProfile;

    /// <summary>How far a trigger whose peak was <paramref name="peak"/> got.</summary>
    public static TriggerOutcome Of(float peak) =>
        !float.IsFinite(peak) || peak < MovedFloor ? TriggerOutcome.NeverMoved
        : peak >= PadUsage.AxisThreshold ? TriggerOutcome.Reached
        : TriggerOutcome.Under;

    /// <summary>A peak as the logs and the panel write it ("0.38").</summary>
    public static string Format(float peak) => (float.IsFinite(peak) ? Math.Clamp(peak, 0f, 1f) : 0f).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// What to tell the player about trigger <paramref name="name"/> ("RT") whose peak was <paramref name="peak"/>, when the
    /// other trigger (<paramref name="otherName"/>) reached <paramref name="otherPeak"/>: null when there's nothing to say
    /// (it reached the game's threshold, or the other one didn't either, so it may just not have been pulled). A trigger
    /// that moved but stopped short is said even when the other didn't reach. <paramref name="steamInput"/>: the pad is set
    /// up by Steam Input, whose layout for the game is where a trigger is sent, or not.
    /// </summary>
    public static string? Advice(string name, float peak, string otherName, float otherPeak, bool steamInput)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(otherName);
        TriggerOutcome outcome = Of(peak);
        bool otherReached = Of(otherPeak) == TriggerOutcome.Reached;
        string full = name == "RT" ? "Right Trigger" : name == "LT" ? "Left Trigger" : name;
        return outcome switch
        {
            TriggerOutcome.Under when steamInput =>
                $"{name} reaches {Format(peak)} at most: the game's trigger actions need more than 0.50, on a couch too. In Steam, open Tape to Tape's " +
                $"controller layout (the controller icon on its page in your Steam Library, or in Big Picture) and set {full} to the gamepad's {full}, " +
                "analog, full range; then press Reset here.",
            TriggerOutcome.Under =>
                $"{name} reaches {Format(peak)} at most: the game's trigger actions need more than 0.50, on a couch too (a worn trigger, or its range set " +
                "lower in the pad's own software).",
            TriggerOutcome.NeverMoved when otherReached && steamInput =>
                $"{name} never moved while {otherName} did: if you pulled it, Steam Input's layout for Tape to Tape isn't sending the {full.ToLowerInvariant()} to the game. In Steam, open " +
                $"Tape to Tape's controller layout (the controller icon on its page in your Steam Library, or in Big Picture) and set {full} to the " +
                $"gamepad's {full} (analog); then press Reset here.",
            TriggerOutcome.NeverMoved when otherReached =>
                $"{name} never moved while {otherName} did: if you pulled it, this pad doesn't send its {full.ToLowerInvariant()} to Windows as a trigger (check its own " +
                "software or mode switch); online it's never pulled.",
            _ => null,
        };
    }

    /// <summary>
    /// The short line a friend's log gets at a match's end when a trigger never reached the game's threshold while the other
    /// did (<see cref="Advice"/> has the details for a developer's controller check); null otherwise. Their screen says so only for
    /// one that stopped short (<see cref="PadUsage.TriggerStoppedShort"/>, the words' <c>notice_trigger</c>), and the line's advice is
    /// that notice's: where a player can look at the trigger's range themselves.
    /// </summary>
    public static string? Notice(string name, float peak, string otherName, float otherPeak, bool steamInput)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Of(otherPeak) != TriggerOutcome.Reached)
        {
            return null;
        }

        return Of(peak) switch
        {
            TriggerOutcome.Under => $"Your pad's {name} reaches {Format(peak)} at most; the game needs more than 0.50 (as on a couch). Check its range in Steam or in the pad's software.",
            TriggerOutcome.NeverMoved => steamInput
                ? $"Your pad's {name} hasn't sent anything this session while {otherName} has: Steam Input's layout may not send it. Check Tape to Tape's controller layout in Steam."
                : $"Your pad's {name} hasn't sent anything this session while {otherName} has. Check its range in the pad's software.",
            _ => null,
        };
    }
}
