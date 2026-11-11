using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a friend's real controller screen must do to show the host's (<see cref="ControllerScreenState"/>): the game's own
/// calls per icon, in the order the game's code needs them, worked out from what each icon shows now. The game glue runs the
/// steps and nothing else; the order matters because moving an icon to the centre clears its lock and position
/// (<c>ControllerAssignmentPlayer.SetSide</c>), so the side goes first and the
/// lock and position are compared after it.
/// </summary>
public static class ControllerScreenPlan
{
    /// <summary>"Never painted here" for <see cref="ControllerIconShown.Colour"/>: the game's own colour shows.</summary>
    public const long Unpainted = -2;

    /// <summary>
    /// The steps that make the icons <paramref name="shown"/> on this PC look as <paramref name="host"/>'s. <paramref name="self"/>
    /// is this player's Steam ID: their own icon keeps the game's own glyphs (their own device), every other icon is drawn
    /// with the device the host draws it with. <paramref name="first"/>: the screen just opened here, so nothing slides.
    /// An icon here the host doesn't have is disabled; a host icon this PC lacks is listed in <see cref="ControllerPlan.Missing"/>.
    /// While the host's screen has no icon that isn't disabled (its first read, before its icons are made), nothing is done.
    /// </summary>
    public static ControllerPlan For(IReadOnlyList<ControllerIconShown> shown, ControllerScreenState host, ulong self, bool first)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(host);
        if (!host.HasIcons)
        {
            return new ControllerPlan(Array.Empty<ControllerStep>(), refreshReady: false, Array.Empty<int>(), waiting: true);
        }

        var steps = new List<ControllerStep>();
        var seen = new HashSet<int>();
        bool refresh = first;
        foreach (ControllerIconShown icon in shown)
        {
            if (!seen.Add(icon.Slot))
            {
                continue; // a second icon of the same player (the game never makes one): the first is the host's
            }

            if (!host.TryGet(icon.Slot, out ControllerIcon want))
            {
                if (!icon.Off)
                {
                    steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Off, 1));
                    refresh = true;
                }

                continue;
            }

            refresh |= Plan(icon, want, self, first, steps);
        }

        var missing = host.Icons.Where(i => !seen.Contains(i.Slot)).Select(i => i.Slot).ToList();
        return new ControllerPlan(steps, refresh, missing, waiting: false);
    }

    /// <summary>
    /// The steps as the log says them, by player ("P3 playing, locked at C, ready; P1 not ready"), with campaign words for the
    /// sides when <paramref name="campaign"/> (left and right are both "playing" there). Colour and glyph steps are named
    /// only when nothing else changed for that player.
    /// </summary>
    public static string Describe(ControllerPlan plan, bool campaign)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>();
        foreach (IGrouping<int, ControllerStep> player in plan.Steps.GroupBy(s => s.Slot))
        {
            var words = new List<string>();
            foreach (ControllerStep step in player)
            {
                string? word = step.Kind switch
                {
                    ControllerStepKind.Controls => step.Value == 0 ? "the AI plays it" : step.Keyboard ? "keyboard" : "gamepad",
                    ControllerStepKind.Side => SideWord((int)step.Value, campaign),
                    ControllerStepKind.Position => step.Value == ControllerScreenState.NoPosition ? "unlocked" : $"locked at {PositionWord((int)step.Value)}",
                    ControllerStepKind.Ready => step.Value != 0 ? "ready" : "not ready",
                    ControllerStepKind.Off => step.Value != 0 ? "off" : "on",
                    _ => null,
                };
                if (word != null)
                {
                    words.Add(word);
                }
            }

            if (words.Count == 0)
            {
                words.Add(player.Any(s => s.Kind == ControllerStepKind.Colour) ? "its colour" : "its button glyphs");
            }

            parts.Add($"P{player.Key + 1} {string.Join(", ", words)}");
        }

        return string.Join("; ", parts);
    }

    /// <summary>A side in words: in the campaign both sides play and the centre doesn't.</summary>
    public static string SideWord(int side, bool campaign) => side switch
    {
        ControllerScreenState.CentreSide => campaign ? "not playing" : "to the middle",
        0 => campaign ? "playing" : "to the left",
        _ => campaign ? "playing" : "to the right",
    };

    /// <summary>A locked position's short name, as the game's enum names it (RW, RD, C, LD, LW, Goalie).</summary>
    public static string PositionWord(int position) => position switch
    {
        0 => "RW",
        1 => "RD",
        2 => "C",
        3 => "LD",
        4 => "LW",
        5 => "Goalie",
        _ => "none",
    };

    /// <summary>One icon's steps; true when one of them changes what the menu's ready check reads.</summary>
    private static bool Plan(ControllerIconShown icon, ControllerIcon want, ulong self, bool first, List<ControllerStep> steps)
    {
        bool refresh = false;
        if (icon.Keyboard != want.Keyboard || icon.Controlled != want.Controlled)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Controls, want.Controlled ? 1 : 0, Keyboard: want.Keyboard));
            refresh = true;
        }

        int position = icon.Position;
        bool ready = icon.Ready;
        if (icon.Side != want.Side)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Side, want.Side, Tween: !first));
            refresh = true;
            if (want.Side == ControllerScreenState.CentreSide)
            {
                // The game's own move to the centre unlocks the icon and clears its position.
                position = ControllerScreenState.NoPosition;
                ready = false;
            }
        }

        if (position != want.Position)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Position, want.Position));
            refresh = true;
        }

        if (ready != want.Ready)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Ready, want.Ready ? 1 : 0));
            refresh = true;
        }

        if (icon.Off != want.Off)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Off, want.Off ? 1 : 0));
            refresh = true;
        }

        long colour = icon.Colour == Unpainted ? -1 : icon.Colour;
        if (colour != want.Colour)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Colour, want.Colour));
        }

        int glyphs = want.Steam != 0 && want.Steam == self ? -1 : want.Device;
        if (icon.Glyphs != glyphs)
        {
            steps.Add(new ControllerStep(icon.Slot, ControllerStepKind.Glyphs, glyphs));
        }

        return refresh;
    }
}

/// <summary>What a friend's controller screen does to show the host's (<see cref="ControllerScreenPlan.For"/>).</summary>
public sealed class ControllerPlan
{
    internal ControllerPlan(IReadOnlyList<ControllerStep> steps, bool refreshReady, IReadOnlyList<int> missing, bool waiting)
    {
        Steps = steps;
        RefreshReady = refreshReady;
        Missing = missing;
        Waiting = waiting;
    }

    /// <summary>The game calls to make, in order.</summary>
    public IReadOnlyList<ControllerStep> Steps { get; }

    /// <summary>Run the menu's own ready check and player counts afterwards (an icon's side, lock, control or state changed, or the screen just opened).</summary>
    public bool RefreshReady { get; }

    /// <summary>The host's players this PC's screen has no icon for.</summary>
    public IReadOnlyList<int> Missing { get; }

    /// <summary>The host's screen has no icon that isn't disabled yet: nothing is done.</summary>
    public bool Waiting { get; }
}

/// <summary>What one game call does to an icon.</summary>
public enum ControllerStepKind
{
    /// <summary>The control flags: <see cref="ControllerStep.Value"/> 1 when someone controls it, <see cref="ControllerStep.Keyboard"/> its keyboard look.</summary>
    Controls,

    /// <summary>The side (<see cref="ControllerStep.Value"/>), sliding when <see cref="ControllerStep.Tween"/>.</summary>
    Side,

    /// <summary>The locked position (<see cref="ControllerStep.Value"/>, 6 for none).</summary>
    Position,

    /// <summary>Locked in or not (<see cref="ControllerStep.Value"/> 1 or 0).</summary>
    Ready,

    /// <summary>Disabled or not (<see cref="ControllerStep.Value"/> 1 or 0).</summary>
    Off,

    /// <summary>The colour (<see cref="ControllerStep.Value"/>: one RGBA value, or -1 for the game's own).</summary>
    Colour,

    /// <summary>The button glyphs' device (<see cref="ControllerStep.Value"/>: the game's device number, or -1 for the game's own glyphs).</summary>
    Glyphs,
}

/// <summary>One game call on the icon of game player <paramref name="Slot"/>.</summary>
/// <param name="Slot">The icon's game player.</param>
/// <param name="Kind">What changes.</param>
/// <param name="Value">To what (see <see cref="ControllerStepKind"/>).</param>
/// <param name="Keyboard">For <see cref="ControllerStepKind.Controls"/>: the keyboard look.</param>
/// <param name="Tween">For <see cref="ControllerStepKind.Side"/>: the icon slides there.</param>
public readonly record struct ControllerStep(int Slot, ControllerStepKind Kind, long Value, bool Keyboard = false, bool Tween = false);

/// <summary>One icon of a friend's controller screen as it shows now.</summary>
/// <param name="Slot">The game player.</param>
/// <param name="Side">Its side.</param>
/// <param name="Position">Its locked position (6 for none).</param>
/// <param name="Ready">Locked in.</param>
/// <param name="Off">Disabled.</param>
/// <param name="Keyboard">The keyboard flag.</param>
/// <param name="Controlled">The controlled flag.</param>
/// <param name="Colour">The colour this PC last painted it with (<see cref="ControllerScreenPlan.Unpainted"/> or -1: the game's own).</param>
/// <param name="Glyphs">The device its glyphs are forced to (-1: the game's own).</param>
public readonly record struct ControllerIconShown(int Slot, int Side, int Position, bool Ready, bool Off, bool Keyboard, bool Controlled, long Colour, int Glyphs);
