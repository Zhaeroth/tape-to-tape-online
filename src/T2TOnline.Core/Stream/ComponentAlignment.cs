using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// How a friend's freshly loaded replay object takes over the host's component ids (<see cref="ComponentAlignment.Align"/>):
/// for each local component, the host id it answers to from now on, or none.
/// </summary>
/// <param name="HostIdFor">Per local component, in its own order: the host's id it takes, or null when no host component
/// is its counterpart (it keeps an id no state of this object names, and stays as it is).</param>
/// <param name="Matched">Local components that took a host id.</param>
/// <param name="HostOnly">Host components with no counterpart here: their states name an id nothing here answers to, so the
/// game skips them.</param>
/// <param name="LocalOnly">Local components with no counterpart on the host.</param>
/// <param name="ByType">True when the counterparts were found by component type; false when by position (no types known).</param>
public sealed record ComponentMatch(IReadOnlyList<ushort?> HostIdFor, int Matched, int HostOnly, int LocalOnly, bool ByType)
{
    /// <summary>True when every component on both sides has its counterpart (the object is driven as on the host).</summary>
    public bool Whole => HostOnly == 0 && LocalOnly == 0;
}

/// <summary>
/// Matching a friend's replay object's components to the host's. A recorded state names each component by its replay id, and
/// the game's own <c>ReplayObject.DeserializeReplayComponents</c> skips an id the object doesn't have, with no error. Two
/// games can give the same object different components: a component added at run time (a talent's effect, granted by a relic
/// that picks its talent at random on each PC) joins the object's list on one PC only. Matched by position, every
/// component after the difference would take another type's state; matched by type (each side's components in order, the
/// next local one of the same type), the rest still line up, and only the difference stays as it is.
/// Pure logic. Never throws.
/// </summary>
public static class ComponentAlignment
{
    /// <summary>
    /// A component type's identity on the wire: the protocol's FNV-1a (<see cref="Fnv"/>) over the UTF-8 bytes of its full
    /// class name ("Namespace.Class"; the class alone when it has no namespace), its two halves folded into 32 bits. The
    /// same build on both PCs names its classes the same.
    /// </summary>
    public static uint TypeHash(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        ulong hash = Fnv.Hash(Encoding.UTF8.GetBytes(fullName));
        return unchecked((uint)hash ^ (uint)(hash >> 32));
    }

    /// <summary>
    /// The host id each local component takes. With both sides' types (<paramref name="hostTypes"/> as long as
    /// <paramref name="hostIds"/>, <paramref name="localTypes"/> one per local component): each host component in order
    /// takes the next local component of its type not yet taken; a host component with none left is host-only. Without
    /// them: by position, and only when the counts agree (else nothing is matched: the object can't be driven safely).
    /// </summary>
    public static ComponentMatch Align(IReadOnlyList<ushort> hostIds, IReadOnlyList<uint>? hostTypes, int localCount, IReadOnlyList<uint>? localTypes)
    {
        ArgumentNullException.ThrowIfNull(hostIds);
        localCount = Math.Max(0, localCount);
        var hostIdFor = new ushort?[localCount];
        bool typed = hostTypes != null && hostTypes.Count == hostIds.Count && localTypes != null && localTypes.Count == localCount;
        if (!typed)
        {
            if (hostIds.Count != localCount)
            {
                return new ComponentMatch(hostIdFor, 0, hostIds.Count, localCount, false);
            }

            for (int i = 0; i < localCount; i++)
            {
                hostIdFor[i] = hostIds[i];
            }

            return new ComponentMatch(hostIdFor, localCount, 0, 0, false);
        }

        // Per type, the next local index to look from: in-order matching keeps several components of one type (each
        // child's transform) in their own order.
        var nextFrom = new Dictionary<uint, int>();
        int matched = 0;
        int hostOnly = 0;
        for (int h = 0; h < hostIds.Count; h++)
        {
            uint type = hostTypes![h];
            int from = nextFrom.TryGetValue(type, out int start) ? start : 0;
            int found = -1;
            for (int l = from; l < localCount; l++)
            {
                if (localTypes![l] == type && hostIdFor[l] == null)
                {
                    found = l;
                    break;
                }
            }

            if (found < 0)
            {
                hostOnly++;
                nextFrom[type] = localCount;
                continue;
            }

            hostIdFor[found] = hostIds[h];
            nextFrom[type] = found + 1;
            matched++;
        }

        return new ComponentMatch(hostIdFor, matched, hostOnly, localCount - matched, true);
    }

    /// <summary>
    /// An id for a local component with no host counterpart: its own when no host component of the object uses it, else
    /// the highest id none of them uses (so no host state reaches it). Ids are 16-bit, and an object has at most 64
    /// components, so one is always free.
    /// </summary>
    public static ushort FreeId(ushort own, IReadOnlyList<ushort> hostIds)
    {
        ArgumentNullException.ThrowIfNull(hostIds);
        var used = new HashSet<ushort>(hostIds);
        if (!used.Contains(own))
        {
            return own;
        }

        for (int id = ushort.MaxValue; id > 0; id--)
        {
            if (!used.Contains((ushort)id))
            {
                return (ushort)id;
            }
        }

        return own;
    }

    /// <summary>
    /// True when two entries of one object name the same components: the same ids and the same types, in the same order
    /// (none and an empty list alike). A host announces an object again when its list changes during play (a skater's
    /// talent added or removed a component); one that names the same components changes nothing here.
    /// </summary>
    public static bool SameComponents(ObjectEntry a, ObjectEntry b) => Same(a.Components, b.Components) && Same(a.ComponentTypes, b.ComponentTypes);

    /// <summary>
    /// The component types <paramref name="after"/> has more of than <paramref name="before"/> (added) and fewer of
    /// (removed), each as often as the count differs: what a skater's list gained and lost, for a log line.
    /// </summary>
    public static (List<uint> Added, List<uint> Removed) Difference(IReadOnlyList<uint>? before, IReadOnlyList<uint>? after)
    {
        var counts = new Dictionary<uint, int>();
        foreach (uint type in after ?? Array.Empty<uint>())
        {
            counts[type] = counts.GetValueOrDefault(type) + 1;
        }

        foreach (uint type in before ?? Array.Empty<uint>())
        {
            counts[type] = counts.GetValueOrDefault(type) - 1;
        }

        var added = new List<uint>();
        var removed = new List<uint>();
        foreach (KeyValuePair<uint, int> pair in counts)
        {
            for (int i = 0; i < pair.Value; i++)
            {
                added.Add(pair.Key);
            }

            for (int i = 0; i < -pair.Value; i++)
            {
                removed.Add(pair.Key);
            }
        }

        return (added, removed);
    }

    /// <summary>"3 matched by type; 1 of the host's (not here), 0 of this PC's (not on the host)", for a log line.</summary>
    public static string Describe(ComponentMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return $"{match.Matched} matched {(match.ByType ? "by type" : "by position")}; {match.HostOnly} of the host's not here, {match.LocalOnly} of this PC's not on the host";
    }

    private static bool Same<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
        where T : IEquatable<T>
    {
        int count = a?.Count ?? 0;
        if (count != (b?.Count ?? 0))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!a![i].Equals(b![i]))
            {
                return false;
            }
        }

        return true;
    }
}
