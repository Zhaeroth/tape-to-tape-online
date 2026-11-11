using System;
using System.Collections.Generic;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>
/// What the mod runs this session, as the native screens see it: the Settings menu's Online tab, the Backups screen and the
/// game's prompts work in all three, and the rows differ only by what a session needs.
/// </summary>
public enum SettingsState
{
    /// <summary>The mod's runtime runs: online sessions can start (or say why not: Steam off, relays refused).</summary>
    OnlineOn,

    /// <summary>Online is off from start-up on a game build the mod patches: only the offline shell runs.</summary>
    OnlineOff,

    /// <summary>The runtime stopped after an error: the offline shell runs, and nothing follows a host any more.</summary>
    AfterError,
}

/// <summary>The states as the log says them, and where the Online tab lands in each.</summary>
public static class SettingsStates
{
    /// <summary>The state in a few words ("online on", "online off", "after an error").</summary>
    public static string Describe(SettingsState state) => state switch
    {
        SettingsState.OnlineOn => "online on",
        SettingsState.OnlineOff => "online off",
        SettingsState.AfterError => "after an error",
        _ => state.ToString(),
    };

    /// <summary>
    /// The part in a session the rows show for: <paramref name="role"/> while online is on, none otherwise (with online off there is no session, and
    /// after an error the session was left, whatever a flow its error stopped half-way still says). One rule for a context
    /// (<see cref="SettingsContext.SessionRole"/>) and for the look each frame that asks whether the rows shown still fit.
    /// </summary>
    public static SettingsRole SessionRole(SettingsState state, SettingsRole role) => state == SettingsState.OnlineOn ? role : SettingsRole.None;

    /// <summary>
    /// The row the Online tab's cursor lands on when it opens in <paramref name="context"/>, of the rows of <paramref name="built"/> that
    /// show: after an error Problem Report, the obvious next step; otherwise the first row. Null when no row shows.
    /// </summary>
    public static SettingsRowId? Landing(in SettingsContext context, IReadOnlyCollection<SettingsRowId> built)
    {
        ArgumentNullException.ThrowIfNull(built);
        IReadOnlyList<SettingsRow> rows = SettingsRows.For(context, built);
        if (rows.Count == 0)
        {
            return null;
        }

        if (context.State == SettingsState.AfterError)
        {
            foreach (SettingsRow row in rows)
            {
                if (row.Id == SettingsRowId.ProblemReport)
                {
                    return row.Id;
                }
            }
        }

        return rows[0].Id;
    }
}
