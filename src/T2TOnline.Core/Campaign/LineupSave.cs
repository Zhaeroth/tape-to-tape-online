namespace T2TOnline.Core.Campaign;

/// <summary>What the host's run sync does once the host's lineup menu has closed (<see cref="LineupSave.Decide"/>).</summary>
public enum LineupSaveStep
{
    /// <summary>The run's team changed since the run was last saved, on the world map with nothing under way: the run is saved now.</summary>
    Save,

    /// <summary>The team is as the run was last saved (a lineup closed without a change, or one the game saved itself).</summary>
    Unchanged,

    /// <summary>The host isn't sharing its campaign (not hosting, or its game plays the solo run): nothing to do.</summary>
    NotShared,

    /// <summary>Not on the world map (a match's lineup, the run's start): the game's own next save brings the change.</summary>
    NotOnMap,

    /// <summary>An event's lineup (the event screen is still up): the event's exit saves the run.</summary>
    EventOpen,

    /// <summary>
    /// The map is busy (a scene loading, a match preview up, a node being entered, a save's checkpoint still on its way, the
    /// campaign tutorial running).
    /// </summary>
    Busy,
}

/// <summary>
/// Host (v1): a lineup change the host makes on the world map reaches friends before the next match. The game's lineup menu
/// applies its swaps to the run's team in memory only (<c>TeamRosterData.ApplyChanges</c>), and the map saves the run only at
/// its save points (a node, a map load, a relic change after a menu closes), while friends get the run only from its file
/// (a run checkpoint, made at each save). So the host's game saves the shared run once, with the game's own save, when its
/// lineup closed on the map with a team other than the one last saved. Anywhere else the game's own next save comes first:
/// an event's exit, a match's end. A friend's match setup puts its copy of the run team in the host's order meanwhile.
/// </summary>
public static class LineupSave
{
    /// <summary>
    /// The step after the host's lineup closed. <paramref name="shared"/>: hosting with the co-op slot as the game's run file;
    /// <paramref name="onMap"/>: the world map is the active scene and has finished building; <paramref name="eventOpen"/>:
    /// an event screen is up (its lineup closed back into it); <paramref name="busy"/>: a scene load, a match preview, a node
    /// entry, a pending checkpoint or the campaign tutorial. <paramref name="saved"/>: the run team's print as the run was last saved (null: not
    /// known in this session); <paramref name="now"/>: its print now (<see cref="LineupOrder.Print"/>).
    /// </summary>
    public static LineupSaveStep Decide(bool shared, bool onMap, bool eventOpen, bool busy, long? saved, long now)
    {
        if (!shared)
        {
            return LineupSaveStep.NotShared;
        }

        if (saved == now)
        {
            return LineupSaveStep.Unchanged;
        }

        if (!onMap)
        {
            return LineupSaveStep.NotOnMap;
        }

        if (eventOpen)
        {
            return LineupSaveStep.EventOpen;
        }

        return busy ? LineupSaveStep.Busy : LineupSaveStep.Save;
    }

    /// <summary>The step in words, for the host's log line ("" for the steps that say nothing: unchanged, not shared).</summary>
    public static string Words(LineupSaveStep step) => step switch
    {
        LineupSaveStep.Save => "the shared run is saved now, so friends get it before the next match (and a friend who joins gets this run)",
        LineupSaveStep.NotOnMap => "not on the world map: friends get it with the game's next run save (a match's end), and a friend's match setup takes the host's order meanwhile",
        LineupSaveStep.EventOpen => "in an event: friends get it with the event's own save at its exit",
        LineupSaveStep.Busy => "the map is busy (a scene loading, a match preview, a node entry, a save on its way or the campaign tutorial): friends get it with the game's next run save, and a friend's match setup takes the host's order meanwhile",
        _ => string.Empty,
    };
}
