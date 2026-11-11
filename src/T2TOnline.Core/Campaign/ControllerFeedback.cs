namespace T2TOnline.Core.Campaign;

/// <summary>
/// The controller screen's feedback (the bump of a refused move, the side arrows, the ready pulse...) as a menu step:
/// the host's game plays one through <c>ControllerAssignmentMenu.PlayFeedback_Internal</c> on an icon's elements, the
/// screen's global ones or a team's; it goes to friends as a value of <see cref="Group"/> whose item is the target (the
/// icon's game player, <see cref="GlobalTarget"/>, <see cref="TeamTarget"/>, <see cref="HomeTeamTarget"/> or
/// <see cref="VisitorTeamTarget"/>) and whose value packs the feedback type, its
/// negative flag, its options and a two-bit count, so two equal feedbacks in a row are two values (the menu board drops a
/// value equal to the last). A friend plays the same call on its own screen's same elements.
/// </summary>
public static class ControllerFeedback
{
    /// <summary>The steps' value group.</summary>
    public const string Group = "ca.fb";

    /// <summary>The target of the screen's global elements.</summary>
    public const int GlobalTarget = 16;

    /// <summary>The target of the campaign team's elements.</summary>
    public const int TeamTarget = 17;

    /// <summary>The target of the home team's elements (the exhibition's right side).</summary>
    public const int HomeTeamTarget = 18;

    /// <summary>The target of the visitor team's elements (the exhibition's left side).</summary>
    public const int VisitorTeamTarget = 19;

    /// <summary>The highest target (0 to 15 are game players).</summary>
    public const int MaxTarget = VisitorTeamTarget;

    /// <summary>The highest feedback type carried (the game has 23, 0 to 22).</summary>
    public const int MaxType = 31;

    /// <summary>The options' bits carried (the game's skip-audio 1 and skip-animation 2; its "all", -1, reads as both).</summary>
    public const int OptionBits = 3;

    /// <summary>
    /// The step value of feedback <paramref name="type"/> (<c>EControllerAssignmentFeedback</c>), negative or not, with
    /// <paramref name="options"/> (<c>EOptions</c>) and the <paramref name="count"/> of this target's steps so far (its low
    /// two bits are kept). False for a type this version can't carry. At most <see cref="MenuTarget.MaxIndex"/>.
    /// </summary>
    public static bool TryEncode(int type, bool negative, int options, int count, out int value)
    {
        value = 0;
        if (type is < 0 or > MaxType)
        {
            return false;
        }

        value = type | (negative ? 1 << 5 : 0) | ((options & OptionBits) << 6) | ((count & 3) << 8);
        return true;
    }

    /// <summary>A step back as the feedback to play: false for a target or value out of range. Never throws.</summary>
    public static bool TryDecode(int target, int value, out ControllerFeedbackStep step)
    {
        step = default;
        if (target is < 0 or > MaxTarget || value is < 0 or > MenuTarget.MaxIndex)
        {
            return false;
        }

        step = new ControllerFeedbackStep(target, value & MaxType, (value & (1 << 5)) != 0, (value >> 6) & OptionBits);
        return true;
    }
}

/// <summary>One feedback to play on a friend's controller screen.</summary>
/// <param name="Target">The icon's game player, or one of <see cref="ControllerFeedback"/>'s other targets.</param>
/// <param name="Type">The game's feedback type.</param>
/// <param name="Negative">Its negative flag (a refused move).</param>
/// <param name="Options">Its options (skip audio 1, skip animation 2).</param>
public readonly record struct ControllerFeedbackStep(int Target, int Type, bool Negative, int Options);
