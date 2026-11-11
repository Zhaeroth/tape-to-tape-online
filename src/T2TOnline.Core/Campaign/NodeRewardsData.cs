using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>A map node reward's class, as the General Manager's reroll picks them. Values are part of the protocol: only add.</summary>
public enum NodeRewardClass : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>Fan Number 1's node reward (<c>STS.NodeRewards.FanNodeReward</c>).</summary>
    Fan = 1,

    /// <summary>Chaos's node reward (<c>ChaosNodeReward</c>).</summary>
    Chaos = 2,

    /// <summary>A skater training's node reward (<c>TrainingReward</c> with <c>RewardType</c> Skater).</summary>
    SkaterTraining = 3,
}

/// <summary>One map node's reward: the node, the reward's class and the reward's id (a skater's id).</summary>
/// <param name="Point">The node.</param>
/// <param name="Class">The reward's class.</param>
/// <param name="Id">The reward's id (<c>INodeReward.RewardID</c>).</param>
public readonly record struct NodeRewardEntry(MapPoint Point, NodeRewardClass Class, string Id)
{
    /// <summary>For logs: "0,3 Skater 0f2b7633-..."; the id is a host's, so it goes by <see cref="Diagnostics.HostText.KeyForLog(string?)"/>.</summary>
    public override string ToString() => $"{Point} {Class} {Diagnostics.HostText.KeyForLog(Id)}";
}

/// <summary>
/// The rewards of the map's Fan, Chaos and skater-training nodes after the host's General Manager rerolled them: a General
/// Manager squad's run rerolls them in place at each page of the General Manager's event
/// (<c>GeneralManagerEvent.RerollSkaterNodeRewards</c>), at random, so a friend's map takes the host's ids and builds each
/// reward with the game's own <c>SetRewardFromID</c>, the way a map load does. Sent as the event menu's
/// <see cref="MenuAttachmentKind.NodeRewards"/> attachment, format <see cref="MenuAttachment.NodeRewardsFormat"/>, the newest
/// replacing the one before. Checked when it's written and when it's read; immutable.
/// <code>
/// u16 reroll · varuint count (1..64) · count × (u8 x · u8 y · u8 class · name id)
/// </code>
/// </summary>
public sealed class NodeRewardsData
{
    /// <summary>Most entries (an act's map has about 50 nodes).</summary>
    public const int MaxEntries = 64;

    /// <summary>Longest reward id, in UTF-8 bytes.</summary>
    public const int MaxIdBytes = 96;

    /// <summary>The host's reroll number this session, from 1 (for logs: a newer one replaces an older one anyway).</summary>
    public int Reroll { get; init; } = 1;

    /// <summary>The rewards, one per node.</summary>
    public IReadOnlyList<NodeRewardEntry> Entries { get; init; } = Array.Empty<NodeRewardEntry>();

    /// <summary>For logs: "reroll 2: 5 node reward(s)".</summary>
    public override string ToString() => $"reroll {Reroll}: {Entries.Count} node reward(s)";

    /// <summary>Why the rewards can't be sent, or null when they can.</summary>
    public string? Problem() => Check();

    /// <summary>Writes the rewards. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteUInt16((ushort)Reroll);
        writer.WriteVarUInt((ulong)Entries.Count);
        foreach (NodeRewardEntry entry in Entries)
        {
            writer.WriteByte((byte)entry.Point.X);
            writer.WriteByte((byte)entry.Point.Y);
            writer.WriteByte((byte)entry.Class);
            EventWire.Name(writer, entry.Id);
        }
    }

    /// <summary>Reads rewards. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out NodeRewardsData? rewards)
    {
        rewards = null;
        int reroll = reader.ReadUInt16();
        bool ok = true;
        int count = EventWire.Count(ref reader, MaxEntries, ref ok);
        if (!ok)
        {
            return false;
        }

        var entries = new List<NodeRewardEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int x = reader.ReadByte();
            int y = reader.ReadByte();
            var kind = (NodeRewardClass)reader.ReadByte();
            string? id = EventWire.ReadName(ref reader);
            if (reader.IsOverrun || id == null)
            {
                return false;
            }

            entries.Add(new NodeRewardEntry(new MapPoint(x, y), kind, id));
        }

        var read = new NodeRewardsData { Reroll = reroll, Entries = entries };
        if (reader.IsOverrun || read.Check() != null)
        {
            return false;
        }

        rewards = read;
        return true;
    }

    private string? Check()
    {
        if (Reroll is < 1 or > ushort.MaxValue)
        {
            return $"reroll {Reroll} is out of range";
        }

        if (Entries == null || Entries.Count is 0 or > MaxEntries)
        {
            return $"{Entries?.Count ?? 0} node rewards (1 to {MaxEntries})";
        }

        var points = new HashSet<MapPoint>();
        foreach (NodeRewardEntry entry in Entries)
        {
            if (entry.Point.X is < 0 or > MapPoint.MaxCoordinate || entry.Point.Y is < 0 or > MapPoint.MaxCoordinate)
            {
                return $"node {entry.Point} is off the map";
            }

            if (!points.Add(entry.Point))
            {
                return $"node {entry.Point} is there twice";
            }

            if (entry.Class is < NodeRewardClass.Fan or > NodeRewardClass.SkaterTraining)
            {
                return $"node {entry.Point}'s reward class {entry.Class} is no class";
            }

            string? problem = EventWire.CheckName(entry.Id, $"node {entry.Point}'s reward id", required: true);
            if (problem != null)
            {
                return problem;
            }

            if (Encoding.UTF8.GetByteCount(entry.Id) > MaxIdBytes)
            {
                return $"node {entry.Point}'s reward id is over {MaxIdBytes} bytes";
            }
        }

        return null;
    }

    /// <summary>The entries of the given class, for logs and tests.</summary>
    public int Count(NodeRewardClass kind) => Entries.Count(e => e.Class == kind);
}
