using System.Collections.Generic;

namespace T2TOnline.Core.Stream;

/// <summary>
/// What a friend's PC does with a host object whose stable key finds a local object another of the host's objects already
/// drives (<see cref="SharedKeys.Decide"/>).
/// </summary>
public enum SharedKeyStep
{
    /// <summary>
    /// Both host objects drive the local one: the same match (replay ids are the recording's own), a recording with no
    /// component ids, or the same component ids (the host announced one object again).
    /// </summary>
    Share,

    /// <summary>
    /// The host made a template object again (a ball and chain's ball at each faceoff, a dropped helmet, a fake puck) with
    /// new component ids, and the local object is a copy made for the stream: the new one gets a copy of its own, a hidden
    /// copy of the same template or a new one, and the copy standing for the old one hides with it.
    /// </summary>
    NewCopy,

    /// <summary>
    /// The new host object has other component ids than the scene object here that another of the host's objects drives:
    /// it can't drive that object (the game skips the components a state names that it can't find), so it only shows with
    /// it. Logged, never claimed to work.
    /// </summary>
    ShareUndriven,
}

/// <summary>
/// The friend side's rule for two host objects under one stable key (the plugin's
/// <c>Streaming/StreamObjects.MapNew</c>). A recorded state names its components by their replay ids, and the game's own
/// <c>ReplayObject.DeserializeReplayComponents</c> skips an id the object doesn't have, with no error: a second host object
/// whose ids differ from the ones the local object answers to moves nothing there. A prefab clone gets new ids at its
/// <c>Awake</c>, so every object the host's game makes again during play (under the key its last one had) has new ones.
/// Pure logic. Never throws.
/// </summary>
public static class SharedKeys
{
    /// <summary>
    /// What to do with the host object whose component ids are <paramref name="hostIds"/>, whose key finds a local object
    /// that answers to <paramref name="localIds"/> (null: not known) and is a copy made for the stream when
    /// <paramref name="isCopy"/>. <paramref name="sameMatch"/>: the stream plays onto the match it was recorded in.
    /// </summary>
    public static SharedKeyStep Decide(bool sameMatch, bool isCopy, IReadOnlyList<ushort>? hostIds, IReadOnlyList<ushort>? localIds)
    {
        if (sameMatch || hostIds is not { Count: > 0 } || localIds is null || SameIds(hostIds, localIds))
        {
            return SharedKeyStep.Share;
        }

        return isCopy ? SharedKeyStep.NewCopy : SharedKeyStep.ShareUndriven;
    }

    /// <summary>True when both lists hold the same ids in the same order (a state's components are matched by position when an object joins).</summary>
    public static bool SameIds(IReadOnlyList<ushort>? a, IReadOnlyList<ushort>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
