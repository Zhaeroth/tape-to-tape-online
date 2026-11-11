using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>The title screen's button groups (the game's <c>TitleScreen.EButtonGroup</c>, by number).</summary>
public enum TitleGroup
{
    /// <summary>"Press start" (no buttons yet).</summary>
    PressToStart = 0,

    /// <summary>The main buttons (campaign, play, customize...).</summary>
    Main = 1,

    /// <summary>The campaign's buttons (new run, continue, abandon, unlockables, run history...).</summary>
    Campaign = 2,

    /// <summary>Play's buttons (play now, practice, tournament).</summary>
    Play = 3,

    /// <summary>Practice's buttons (tutorial, skirmish, shootout).</summary>
    Practice = 4,

    /// <summary>Customize's buttons (teams, players).</summary>
    Customize = 5,
}

/// <summary>
/// The host's title screen in its view (the "title" screen): the button group it shows (or is moving to) and the
/// title button its cursor is on, by the button's name (the title's own button, never a path: a group's buttons differ
/// between profiles, Continue being there only with a run). Absent details: not known (the host isn't on its title, or its
/// cursor is in a menu over it, or an older host).
/// </summary>
/// <param name="Group">The group, 0 to <see cref="MaxGroup"/>, or null.</param>
/// <param name="Button">The button's name (1 to <see cref="MaxButtonChars"/> letters), or null.</param>
public sealed record TitleViewDetails(int? Group, string? Button)
{
    /// <summary>Whole: the group (<see cref="Group"/>).</summary>
    public const string GroupKey = "titleGroup";

    /// <summary>Text: the button (<see cref="Button"/>); it changes as the cursor moves.</summary>
    public const string ButtonKey = "titleButton";

    /// <summary>Highest group number taken from the network (the game's six, and room).</summary>
    public const int MaxGroup = 15;

    /// <summary>Longest button name.</summary>
    public const int MaxButtonChars = 32;

    /// <summary>Both detail names (what changes on the title while its screen stays the same).</summary>
    public static IReadOnlyCollection<string> Keys { get; } = new[] { GroupKey, ButtonKey };

    /// <summary>The details as named values (only those known).</summary>
    public IReadOnlyList<KeyValuePair<string, NetValue>> ToValues()
    {
        var values = new List<KeyValuePair<string, NetValue>>(2);
        if (Group is int group && group is >= 0 and <= MaxGroup)
        {
            values.Add(new(GroupKey, NetValue.FromInt(group)));
        }

        if (IsName(Button))
        {
            values.Add(new(ButtonKey, NetValue.FromString(Button!)));
        }

        return values;
    }

    /// <summary>The details in <paramref name="view"/>: a group out of range or a malformed name reads as absent. Never throws.</summary>
    public static TitleViewDetails Read(HostView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        long group = view.Whole(GroupKey, -1);
        string button = view.Text(ButtonKey);
        return new TitleViewDetails(group is >= 0 and <= MaxGroup ? (int)group : null, IsName(button) ? button : null);
    }

    /// <summary>True for a button name: 1 to <see cref="MaxButtonChars"/> ASCII letters.</summary>
    public static bool IsName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxButtonChars)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>What a friend's title screen does next to show the host's group and button.</summary>
public enum TitleStep
{
    /// <summary>Nothing (already the same, waiting for a transition, or nothing known).</summary>
    None,

    /// <summary>The game's own press-start (this title still waits at "press start"; it goes to the main buttons).</summary>
    PressStart,

    /// <summary>The game's own move to the host's group.</summary>
    Group,

    /// <summary>The host's button selected.</summary>
    Button,
}

/// <summary>A title screen's state, as the follower reads it from the game.</summary>
/// <param name="Group">The group shown (<c>m_CurrentButtonGroup</c>).</param>
/// <param name="Moving">A move between groups is running (<c>_isTransitioningGroup</c>).</param>
/// <param name="MovingTo">Where it's moving (<c>_transitionTarget</c>; meaningful while <paramref name="Moving"/>).</param>
/// <param name="InputReady">A running move takes input (<c>_groupInputReady</c>): the game's own group change can cut it short.</param>
/// <param name="Button">The title button selected now, by name, or null (none, or not one of the title's).</param>
/// <param name="Busy">The title is starting a run, leaving, refreshing its campaign state or has a menu over it.</param>
public readonly record struct TitleState(int Group, bool Moving, int MovingTo, bool InputReady, string? Button, bool Busy);

/// <summary>
/// A friend's title screen following the host's (game-independent decisions; the glue reads the state and does the step).
/// </summary>
public static class TitleFollow
{
    /// <summary>
    /// The next step for a title in state <paramref name="here"/> to show <paramref name="host"/>. "Press start" is never
    /// followed back (a friend's title stays on its buttons), nor a group this version doesn't know (a newer host's: it
    /// would be handed to the game's own group change as a group the game has none of); a move under way is waited for (the
    /// game's own group change does nothing then unless it takes input); the button comes once the group is the host's and
    /// still.
    /// </summary>
    public static TitleStep Next(TitleViewDetails? host, TitleState here)
    {
        if (host?.Group is not int group || here.Busy || group is <= (int)TitleGroup.PressToStart or > (int)TitleGroup.Customize)
        {
            return TitleStep.None;
        }

        if (here.Group == (int)TitleGroup.PressToStart && !here.Moving)
        {
            return TitleStep.PressStart;
        }

        int shown = here.Moving ? here.MovingTo : here.Group;
        if (shown != group)
        {
            return here.Moving && !here.InputReady ? TitleStep.None : TitleStep.Group;
        }

        if (here.Moving)
        {
            return TitleStep.None;
        }

        return host.Button != null && host.Button != here.Button ? TitleStep.Button : TitleStep.None;
    }
}
