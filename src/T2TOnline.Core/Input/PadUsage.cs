using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Input;

/// <summary>
/// Which of a pad's buttons and axes were used while it played online, for the log when it's given back: with the host's
/// list of the game actions a friend's virtual pad gave the game, it shows where a press stopped (a pad that never sends
/// an element, or an element that never became an action). A pad's trigger can fail to reach the game's actions
/// (a Steam Input layout that doesn't send it), and this says why; each axis's peak tells "pulled to
/// 0.3" from "never moved" (both are "never" past <see cref="AxisThreshold"/>). Observing allocates nothing.
/// </summary>
public sealed class PadUsage
{
    /// <summary>How far an axis must move to count as used (sticks -1 to 1, triggers 0 to 1).</summary>
    public const float AxisThreshold = 0.5f;

    private readonly float[] _peaks = new float[PadState.AxisCount];
    private PadButtons _buttons;
    private int _axes;

    /// <summary>The buttons held at some point.</summary>
    public PadButtons Buttons => _buttons;

    /// <summary>The axes moved past <see cref="AxisThreshold"/> at some point, one bit per <see cref="PadAxis"/>.</summary>
    public int Axes => _axes;

    /// <summary>The furthest <paramref name="axis"/> moved, either way (0 to 1).</summary>
    public float Peak(PadAxis axis) => (uint)axis < PadState.AxisCount ? _peaks[(int)axis] : 0f;

    /// <summary>Notes one reading of the pad.</summary>
    public void Observe(PadButtons held, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
    {
        _buttons |= held & PadButtons.All;
        Note(PadAxis.LeftStickX, leftX);
        Note(PadAxis.LeftStickY, leftY);
        Note(PadAxis.RightStickX, rightX);
        Note(PadAxis.RightStickY, rightY);
        Note(PadAxis.LeftTrigger, leftTrigger);
        Note(PadAxis.RightTrigger, rightTrigger);
    }

    /// <summary>"used A, B, LeftTrigger; never X, RightTrigger" (or "nothing used").</summary>
    public string Describe()
    {
        var used = new List<string>();
        var never = new List<string>();
        for (int i = 0; i < PadState.ButtonCount; i++)
        {
            var button = (PadButtons)(1 << i);
            ((_buttons & button) != 0 ? used : never).Add(button.ToString());
        }

        for (int i = 0; i < PadState.AxisCount; i++)
        {
            ((_axes & (1 << i)) != 0 ? used : never).Add(((PadAxis)i).ToString());
        }

        return used.Count == 0 ? "nothing used" : $"used {string.Join(", ", used)}{(never.Count > 0 ? $"; never {string.Join(", ", never)}" : string.Empty)}";
    }

    /// <summary>
    /// <see cref="Describe"/> with the peaks: "used A, LeftTrigger; never RightTrigger; trigger peaks LT 1.00 RT 0.31; sticks
    /// short of 0.50: RightStickX 0.20". A stick named there moved, but never far enough to count.
    /// </summary>
    public string DescribeWithPeaks()
    {
        var shortSticks = new List<string>();
        for (int i = 0; i <= (int)PadAxis.RightStickY; i++)
        {
            if (_peaks[i] >= TriggerCheck.MovedFloor && _peaks[i] < AxisThreshold)
            {
                shortSticks.Add($"{(PadAxis)i} {TriggerCheck.Format(_peaks[i])}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Describe()}; trigger peaks LT {TriggerCheck.Format(Peak(PadAxis.LeftTrigger))} RT {TriggerCheck.Format(Peak(PadAxis.RightTrigger))}") +
            (shortSticks.Count > 0 ? $"; sticks short of {TriggerCheck.Format(AxisThreshold)}: {string.Join(", ", shortSticks)}" : string.Empty);
    }

    /// <summary>
    /// What the log says about the player's triggers now (<see cref="TriggerCheck.Notice"/>: one stopped short of the game's
    /// threshold, or never moved, while the other reached it), or null.
    /// </summary>
    public string? TriggerNotice(bool steamInput) =>
        TriggerCheck.Notice("RT", Peak(PadAxis.RightTrigger), "LT", Peak(PadAxis.LeftTrigger), steamInput)
        ?? TriggerCheck.Notice("LT", Peak(PadAxis.LeftTrigger), "RT", Peak(PadAxis.RightTrigger), steamInput);

    /// <summary>
    /// True when a trigger was pulled but stopped short of the game's threshold while the other reached it: the one thing play alone
    /// shows, and what a player's screen may say. A trigger that never moved, or only brushed (under <see cref="TriggerCheck.PulledFloor"/>),
    /// may simply not have been pulled: either one skates backwards (the game's <c>IsBackwardsHeld</c> asks both), so a player who uses
    /// one and leaves the other alone has a healthy pad, and that stays in the log (<see cref="TriggerNotice"/>; the panel's check asks for both).
    /// </summary>
    public bool TriggerStoppedShort
    {
        get
        {
            float left = Peak(PadAxis.LeftTrigger);
            float right = Peak(PadAxis.RightTrigger);
            return (StoppedShort(right) && TriggerCheck.Of(left) == TriggerOutcome.Reached) || (StoppedShort(left) && TriggerCheck.Of(right) == TriggerOutcome.Reached);

            static bool StoppedShort(float peak) => TriggerCheck.Of(peak) == TriggerOutcome.Under && peak >= TriggerCheck.PulledFloor;
        }
    }

    /// <summary>A standard pad's element names for the buttons and axes a reader lacks (empty when it has them all).</summary>
    public static string Missing(IEnumerable<int> missingButtons, IEnumerable<PadAxis> missingAxes)
    {
        ArgumentNullException.ThrowIfNull(missingButtons);
        ArgumentNullException.ThrowIfNull(missingAxes);
        var names = new List<string>();
        foreach (int index in missingButtons)
        {
            if (index is >= 0 and < PadState.ButtonCount)
            {
                names.Add(((PadButtons)(1 << index)).ToString());
            }
        }

        foreach (PadAxis axis in missingAxes)
        {
            names.Add(axis.ToString());
        }

        return string.Join(", ", names);
    }

    private void Note(PadAxis axis, float value)
    {
        float size = float.IsFinite(value) ? Math.Min(Math.Abs(value), 1f) : 0f;
        if (size > _peaks[(int)axis])
        {
            _peaks[(int)axis] = size;
        }

        if (size >= AxisThreshold)
        {
            _axes |= 1 << (int)axis;
        }
    }
}
