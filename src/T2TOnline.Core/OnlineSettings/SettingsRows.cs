using System;
using System.Collections.Generic;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>A row of the game's Settings menu's Online tab (D139), top to bottom.</summary>
public enum SettingsRowId
{
    /// <summary>Share Achievements (toggle).</summary>
    ShareAchievements,

    /// <summary>Share Progress (toggle).</summary>
    ShareProgress,

    /// <summary>Play Online With: Auto, Gamepad, Keyboard (choice).</summary>
    PlayOnlineWith,

    /// <summary>Direct Connections (toggle): shown only when it's on or Steam refused relays alone on this PC.</summary>
    DirectConnections,

    /// <summary>Save Backups [View] (button): the game's main-menu-only lock.</summary>
    SaveBackups,

    /// <summary>Problem Report [Make Report] (button).</summary>
    ProblemReport,

    /// <summary>Leave Online Game (button): a friend in a session.</summary>
    LeaveOnlineGame,

    /// <summary>End Online Game (button): the host of a session.</summary>
    EndOnlineGame,
}

/// <summary>What a row is made from.</summary>
public enum SettingsRowKind
{
    /// <summary>The game's two-line toggle row.</summary>
    Toggle,

    /// <summary>The game's choice row (a cycle list).</summary>
    Choice,

    /// <summary>The game's button row with a title.</summary>
    Button,
}

/// <summary>This PC's part in an online session, as the Settings tab sees it.</summary>
public enum SettingsRole
{
    /// <summary>No session.</summary>
    None,

    /// <summary>Hosting.</summary>
    Host,

    /// <summary>In a host's session.</summary>
    Friend,
}

/// <summary>Why a shown row can't be used now.</summary>
public enum SettingsRowRefusal
{
    /// <summary>It can be used.</summary>
    None,

    /// <summary>Not at the title: the game's own main-menu-only lock dims it and refuses its press.</summary>
    NotAtTitle,

    /// <summary>A session is on (host or friend).</summary>
    InSession,

    /// <summary>One of the mod's screens is open already (the lobby under Settings).</summary>
    ScreenOpen,
}

/// <summary>Where the Settings menu is opened, for which rows show and which can be used.</summary>
/// <param name="AtTitle">The title screen's scene is the active one (the game's main-menu-only lock is open).</param>
/// <param name="Role">This PC's part in a session.</param>
/// <param name="ScreenOpen">One of the mod's screens is open under Settings.</param>
/// <param name="DirectConnectionsOn">The Direct Connections setting is on.</param>
/// <param name="RelayRefused">Steam refused relays alone on this PC this game.</param>
/// <param name="State">What the mod runs this session: a session role counts only with online on (<see cref="SessionRole"/>).</param>
public readonly record struct SettingsContext(bool AtTitle, SettingsRole Role, bool ScreenOpen, bool DirectConnectionsOn, bool RelayRefused, SettingsState State = SettingsState.OnlineOn)
{
    /// <summary>
    /// <see cref="Role"/> while online is on; none otherwise: with online off there is no session, and after an error the session was
    /// left (a flow its error stopped half-way must not keep Leave or End shown, or Save Backups refused).
    /// </summary>
    public SettingsRole SessionRole => SettingsStates.SessionRole(State, Role);
}

/// <summary>A row as it shows now.</summary>
/// <param name="Id">Which row.</param>
/// <param name="Kind">What it's made from.</param>
/// <param name="Refusal">Why it can't be used now, or <see cref="SettingsRowRefusal.None"/>.</param>
public sealed record SettingsRow(SettingsRowId Id, SettingsRowKind Kind, SettingsRowRefusal Refusal)
{
    /// <summary>True when it can be used now.</summary>
    public bool Usable => Refusal == SettingsRowRefusal.None;
}

/// <summary>
/// The Online tab's rows (D139): their order, what each is made from, when each shows and when each can be used. The plugin makes the rows this
/// build has (<see cref="Built"/>); a later build adds rows there and nothing else changes.
/// </summary>
public static class SettingsRows
{
    /// <summary>Every row, in the tab's order.</summary>
    public static readonly IReadOnlyList<SettingsRowId> Order = new[]
    {
        SettingsRowId.ShareAchievements, SettingsRowId.ShareProgress, SettingsRowId.PlayOnlineWith, SettingsRowId.DirectConnections,
        SettingsRowId.SaveBackups, SettingsRowId.ProblemReport, SettingsRowId.LeaveOnlineGame, SettingsRowId.EndOnlineGame,
    };

    /// <summary>The rows this build makes: every row of the tab.</summary>
    public static readonly IReadOnlyList<SettingsRowId> Built = Order;

    /// <summary>What a row is made from.</summary>
    public static SettingsRowKind KindOf(SettingsRowId id) => id switch
    {
        SettingsRowId.ShareAchievements or SettingsRowId.ShareProgress or SettingsRowId.DirectConnections => SettingsRowKind.Toggle,
        SettingsRowId.PlayOnlineWith => SettingsRowKind.Choice,
        SettingsRowId.SaveBackups or SettingsRowId.ProblemReport or SettingsRowId.LeaveOnlineGame or SettingsRowId.EndOnlineGame => SettingsRowKind.Button,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a row"),
    };

    /// <summary>
    /// True when the row shows in <paramref name="context"/>. Direct Connections only when it's on (so it can be turned off) or Steam refused relays alone;
    /// Leave only for a friend in a session, End only for its host (with online on: <see cref="SettingsContext.SessionRole"/>); every other row always.
    /// </summary>
    public static bool Shown(SettingsRowId id, in SettingsContext context) => id switch
    {
        SettingsRowId.DirectConnections => context.DirectConnectionsOn || context.RelayRefused,
        SettingsRowId.LeaveOnlineGame => context.SessionRole == SettingsRole.Friend,
        SettingsRowId.EndOnlineGame => context.SessionRole == SettingsRole.Host,
        _ => true,
    };

    /// <summary>
    /// Why a shown row can't be used in <paramref name="context"/>. Save Backups only at the title (the game's own lock says so first), out of a session (<see cref="SettingsContext.SessionRole"/>),
    /// with none of the mod's screens open under Settings; every other row always.
    /// </summary>
    public static SettingsRowRefusal Refusal(SettingsRowId id, in SettingsContext context)
    {
        if (id != SettingsRowId.SaveBackups)
        {
            return SettingsRowRefusal.None;
        }

        return !context.AtTitle ? SettingsRowRefusal.NotAtTitle
            : context.SessionRole != SettingsRole.None ? SettingsRowRefusal.InSession
            : context.ScreenOpen ? SettingsRowRefusal.ScreenOpen
            : SettingsRowRefusal.None;
    }

    /// <summary>The rows of <paramref name="built"/> that show in <paramref name="context"/>, in the tab's order, each with why it can't be used.</summary>
    public static IReadOnlyList<SettingsRow> For(in SettingsContext context, IReadOnlyCollection<SettingsRowId> built)
    {
        ArgumentNullException.ThrowIfNull(built);
        var rows = new List<SettingsRow>(built.Count);
        foreach (SettingsRowId id in Order)
        {
            if (Contains(built, id) && Shown(id, context))
            {
                rows.Add(new SettingsRow(id, KindOf(id), Refusal(id, context)));
            }
        }

        return rows;
    }

    /// <summary>The refusal as the log says it.</summary>
    public static string Describe(SettingsRowRefusal refusal) => refusal switch
    {
        SettingsRowRefusal.None => "usable",
        SettingsRowRefusal.NotAtTitle => "not at the title: the game's lock",
        SettingsRowRefusal.InSession => "an online game is on",
        SettingsRowRefusal.ScreenOpen => "one of the mod's screens is open under Settings",
        _ => refusal.ToString(),
    };

    private static bool Contains(IReadOnlyCollection<SettingsRowId> built, SettingsRowId id)
    {
        foreach (SettingsRowId row in built)
        {
            if (row == id)
            {
                return true;
            }
        }

        return false;
    }
}
