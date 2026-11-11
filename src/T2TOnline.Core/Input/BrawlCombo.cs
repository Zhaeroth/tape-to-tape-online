namespace T2TOnline.Core.Input;

/// <summary>
/// The game's brawl-offer combo, as the couch has it (<c>GameControllerBase.CheckFightComboInput</c>): two inputs held
/// together, one of them pressed just now (LB+RB on a pad, Q+E on the keyboard). Pure: the host feeds it each friend's
/// played input, once per input update.
/// </summary>
public static class BrawlCombo
{
    /// <summary>The pad's combo: both shoulders.</summary>
    public const PadButtons PadCombo = PadButtons.LeftBumper | PadButtons.RightBumper;

    /// <summary>
    /// True when every input of <paramref name="combo"/> (a mask with at least one bit) is down this update and at least one
    /// of them went down since the last (<paramref name="previousHeld"/>). A tap (pressed and let go between two updates,
    /// <paramref name="taps"/>) counts as down and as just pressed, as it does for the controller it's applied to.
    /// </summary>
    public static bool Fires(ulong previousHeld, ulong held, ulong taps, ulong combo)
    {
        if (combo == 0)
        {
            return false;
        }

        ulong down = held | taps;
        if ((down & combo) != combo)
        {
            return false;
        }

        ulong justPressed = (held & ~previousHeld) | taps;
        return (justPressed & combo) != 0;
    }

    /// <summary>The keyboard's combo over a friend's key table: Q and E (none when either isn't in it).</summary>
    public static ulong KeyboardCombo(InputBindingSet? bindings)
    {
        int q = bindings?.IndexOf(BindingElement.Key(BindingElement.KeyQ)) ?? -1;
        int e = bindings?.IndexOf(BindingElement.Key(BindingElement.KeyE)) ?? -1;
        return q < 0 || e < 0 ? 0 : (1UL << q) | (1UL << e);
    }
}

/// <summary>
/// One friend's combo on the host, update by update (<see cref="BrawlCombo.Fires"/> over what was held the update before).
/// While their controller is held neutral (they're away, the lobby holds it, or the AI plays for them) the combo can't
/// fire, and what they hold when it's theirs again counts only once let go, as their controller's own buttons do: a combo
/// held through the hand-back is no fresh press.
/// </summary>
public sealed class BrawlComboWatch
{
    private ulong _previousHeld;
    private bool _afterHold;

    /// <summary>Their controller is held neutral this update: no combo now, and what's down at the hand-back waits to be let go.</summary>
    public void Hold() => _afterHold = true;

    /// <summary>
    /// Their played input this update (<paramref name="held"/>, and <paramref name="taps"/> pressed and let go since the last):
    /// true when <paramref name="combo"/> fires now.
    /// </summary>
    public bool Next(ulong held, ulong taps, ulong combo)
    {
        if (_afterHold)
        {
            // The first update after a hold: what's down now was down before it, so it isn't a press.
            _afterHold = false;
            _previousHeld = held | taps;
            return false;
        }

        bool fires = BrawlCombo.Fires(_previousHeld, held, taps, combo);
        _previousHeld = held;
        return fires;
    }
}
