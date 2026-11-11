using System;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's game entering an event node on the world map: a click on an NPC or training
/// node plays the game's node entry before the event screen opens (<c>WorldMapMenusHandler.PushCameraToNode</c>: the
/// camera zooms out, holds, then zooms into the node's door while the team's zamboni drives there, and the screen plunges
/// in). <paramref name="Number"/> tells one entry from the next (counted from 1 in a hosting session), so a friend plays
/// each entry once however many map views carry it.
/// </summary>
/// <param name="Node">The node being entered.</param>
/// <param name="Number">Which entry this is (1 to <see cref="NodeEntry.MaxNumber"/>).</param>
public readonly record struct NodeEntry(MapPoint Node, int Number)
{
    /// <summary>The largest entry number taken from the network.</summary>
    public const int MaxNumber = int.MaxValue;

    /// <summary>"x,y #n".</summary>
    public override string ToString() => $"{Node} #{Number}";
}

/// <summary>
/// The host's side of <see cref="NodeEntry"/>: numbers each entry its game starts, and says which one is under way (the
/// game's camera push is held from the click until the camera has come back: after the event screen, or at once for a
/// node that can't be entered). Pure logic; the game glue reads the game's state.
/// </summary>
public sealed class NodeEntryHost
{
    private int _count;

    /// <summary>The entry under way, or null.</summary>
    public NodeEntry? Current { get; private set; }

    /// <summary>The game started entering <paramref name="node"/>: a new entry, under way from now.</summary>
    public NodeEntry Begin(MapPoint node)
    {
        if (node.IsNone)
        {
            throw new ArgumentException("An entry needs a node.", nameof(node));
        }

        _count = _count >= NodeEntry.MaxNumber ? 1 : _count + 1;
        var entry = new NodeEntry(node, _count);
        Current = entry;
        return entry;
    }

    /// <summary>The game's camera came back from the node (or never held it): no entry under way. Returns the one that ended, if any.</summary>
    public NodeEntry? End()
    {
        NodeEntry? ended = Current;
        Current = null;
        return ended;
    }

    /// <summary>Hosting stopped: nothing under way (the numbers carry on, so a friend never mistakes a new entry for an old one).</summary>
    public void Clear() => Current = null;
}

/// <summary>What a friend's map does about the host's node entry now.</summary>
public enum NodeEntryAction
{
    /// <summary>Nothing to do.</summary>
    None = 0,

    /// <summary>Play the game's own node entry here, into the same node.</summary>
    Play = 1,

    /// <summary>Leave this entry out (this map can't play it now); it isn't tried again.</summary>
    Skip = 2,

    /// <summary>
    /// The host's entry is over and this PC's is still held: bring this camera back (the glue does that only when no event
    /// screen is up here, whose own close brings it back).
    /// </summary>
    Return = 3,
}

/// <summary>One decision about the host's node entry.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Entry">The entry it's about (default for <see cref="NodeEntryAction.None"/>).</param>
/// <param name="Why">For <see cref="NodeEntryAction.Skip"/>: why ("" otherwise).</param>
public readonly record struct NodeEntryStep(NodeEntryAction Action, NodeEntry Entry, string Why);

/// <summary>
/// A friend's side of <see cref="NodeEntry"/>: each entry of the host's plays once on this PC's map, as the host's game played
/// it, so the event screen opens here from the same zoom as on the host's screen (its <c>Show</c> waits for this PC's own
/// arrival, as the host's waits for its own). An entry this map can't play (not loaded, an act's intro playing) is left out
/// with why; when the host's entry is over while this PC's is still held, the camera comes back. Pure logic; the game glue
/// plays, returns and reads the game's state.
/// </summary>
public sealed class NodeEntryFollow
{
    /// <summary>The newest entry number played or left out here (0: none yet).</summary>
    public int Taken { get; private set; }

    /// <summary>The entry this PC played whose camera may still be held here, or null.</summary>
    public NodeEntry? Playing { get; private set; }

    /// <summary>
    /// What to do now. <paramref name="host"/>: the entry the host's newest map view carries (null: none under way);
    /// <paramref name="mapReady"/>: this PC shows the host's world map, loaded; <paramref name="introPlaying"/>: an act's
    /// intro moves this map's camera now.
    /// </summary>
    public NodeEntryStep Step(NodeEntry? host, bool mapReady, bool introPlaying)
    {
        if (host is not { } entry)
        {
            if (Playing is { } mine)
            {
                Playing = null;
                return new NodeEntryStep(NodeEntryAction.Return, mine, string.Empty);
            }

            return default;
        }

        if (entry.Number == Taken)
        {
            return default;
        }

        Taken = entry.Number;
        if (!mapReady || introPlaying)
        {
            // An older entry still held here is the glue's to end (the game's next push replaces a held one itself).
            Playing = null;
            return new NodeEntryStep(NodeEntryAction.Skip, entry, !mapReady ? "this PC isn't on the host's world map yet" : "an act's intro is moving this map's camera");
        }

        Playing = entry;
        return new NodeEntryStep(NodeEntryAction.Play, entry, string.Empty);
    }

    /// <summary>This PC's entry is over by itself (its event screen closed and brought the camera back, the map moved on): nothing is held here now.</summary>
    public void Ended() => Playing = null;

    /// <summary>Following started or stopped: nothing taken, nothing held (a host's entry under way plays once on the next view).</summary>
    public void Reset()
    {
        Taken = 0;
        Playing = null;
    }
}
