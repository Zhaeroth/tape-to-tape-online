using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class NodeRewardsDataTests
{
    /// <summary>A General Manager's second reroll: a Fan, a Chaos and a skater-training node.</summary>
    public static NodeRewardsData Sample() => new()
    {
        Reroll = 2,
        Entries = new[]
        {
            new NodeRewardEntry(new MapPoint(1, 3), NodeRewardClass.Fan, "fwd-kovalski"),
            new NodeRewardEntry(new MapPoint(4, 5), NodeRewardClass.Chaos, "fwd-agent-7"),
            new NodeRewardEntry(new MapPoint(0, 9), NodeRewardClass.SkaterTraining, "fwd-moose"),
        },
    };

    public static byte[] Encoded(NodeRewardsData rewards)
    {
        var writer = new BitWriter();
        rewards.Write(writer);
        return writer.ToArray();
    }

    private static NodeRewardsData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = NodeRewardsData.TryRead(ref reader, out NodeRewardsData? rewards);
        Assert.Equal(ok, rewards != null);
        return ok && reader.RemainingBits < 8 ? rewards : null;
    }

    [Fact]
    public void Rewards_round_trip_entry_by_entry()
    {
        NodeRewardsData read = Read(Encoded(Sample()))!;
        Assert.NotNull(read);
        Assert.Equal(2, read.Reroll);
        Assert.Equal(Sample().Entries, read.Entries);
        Assert.Equal("reroll 2: 3 node reward(s)", read.ToString());
        Assert.Equal((1, 1, 1), (read.Count(NodeRewardClass.Fan), read.Count(NodeRewardClass.Chaos), read.Count(NodeRewardClass.SkaterTraining)));

        // The most entries, the far corner of the map, the longest id, the largest reroll.
        var largest = new NodeRewardsData
        {
            Reroll = ushort.MaxValue,
            Entries = Enumerable.Range(0, NodeRewardsData.MaxEntries)
                .Select(i => new NodeRewardEntry(new MapPoint(i % 8 == 0 ? MapPoint.MaxCoordinate : i % 8, i / 8), NodeRewardClass.Fan, new string('i', NodeRewardsData.MaxIdBytes)))
                .ToList(),
        };
        Assert.Null(largest.Problem());
        Assert.Equal(Encoded(largest), Encoded(Read(Encoded(largest))!));
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        NodeRewardEntry good = new(new MapPoint(1, 1), NodeRewardClass.Fan, "fwd");
        yield return new object[] { "reroll 0", new NodeRewardsData { Reroll = 0, Entries = new[] { good } } };
        yield return new object[] { "reroll over 65535", new NodeRewardsData { Reroll = 65536, Entries = new[] { good } } };
        yield return new object[] { "no entries", new NodeRewardsData { Entries = Array.Empty<NodeRewardEntry>() } };
        yield return new object[] { "too many entries", new NodeRewardsData { Entries = Enumerable.Range(0, 65).Select(i => good with { Point = new MapPoint(i % 8, i / 8) }).ToList() } };
        yield return new object[] { "a node off the map", new NodeRewardsData { Entries = new[] { good with { Point = new MapPoint(MapPoint.MaxCoordinate + 1, 0) } } } };
        yield return new object[] { "no node", new NodeRewardsData { Entries = new[] { good with { Point = MapPoint.None } } } };
        yield return new object[] { "a node twice", new NodeRewardsData { Entries = new[] { good, good with { Id = "other" } } } };
        yield return new object[] { "no class", new NodeRewardsData { Entries = new[] { good with { Class = NodeRewardClass.None } } } };
        yield return new object[] { "an unknown class", new NodeRewardsData { Entries = new[] { good with { Class = (NodeRewardClass)4 } } } };
        yield return new object[] { "an empty id", new NodeRewardsData { Entries = new[] { good with { Id = string.Empty } } } };
        yield return new object[] { "a long id", new NodeRewardsData { Entries = new[] { good with { Id = new string('i', NodeRewardsData.MaxIdBytes + 1) } } } };
        yield return new object[] { "an id with a line break", new NodeRewardsData { Entries = new[] { good with { Id = "a\nb" } } } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Rewards_out_of_their_limits_are_refused_when_written_and_never_sent(string what, NodeRewardsData rewards)
    {
        Assert.NotNull(rewards.Problem());
        Assert.ThrowsAny<ArgumentException>(() => rewards.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.NodeRewards(3, rewards));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Crafted_rewards_that_break_a_rule_are_refused()
    {
        Assert.NotNull(Read(Crafted(1, 1, 3, 1, "fwd")));
        Assert.Null(Read(Crafted(0, 1, 3, 1, "fwd"))); // reroll 0
        Assert.Null(Read(Crafted(1, 0, 3, 1, "fwd"))); // no entries
        Assert.Null(Read(Crafted(1, 1, MapPoint.MaxCoordinate + 1, 1, "fwd")));
        Assert.Null(Read(Crafted(1, 1, 3, 0, "fwd")));
        Assert.Null(Read(Crafted(1, 1, 3, 4, "fwd")));
        Assert.Null(Read(Crafted(1, 1, 3, 1, new string('i', NodeRewardsData.MaxIdBytes + 1))));

        // A count past the cap, with nothing behind it.
        var writer = new BitWriter();
        writer.WriteUInt16(1);
        writer.WriteVarUInt(NodeRewardsData.MaxEntries + 1);
        Assert.Null(Read(writer.ToArray()));

        // Cut short at every length; something after it.
        byte[] good = Encoded(Sample());
        for (int length = 0; length < good.Length; length++)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));

        static byte[] Crafted(int reroll, int count, int x, int kind, string id)
        {
            var writer = new BitWriter();
            writer.WriteUInt16((ushort)reroll);
            writer.WriteVarUInt((ulong)count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteByte((byte)x);
                writer.WriteByte((byte)i);
                writer.WriteByte((byte)kind);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(id);
                writer.WriteVarUInt((ulong)bytes.Length);
                writer.WriteBytes(bytes);
            }

            return writer.ToArray();
        }
    }

    [Fact]
    public void An_attachment_of_rewards_round_trips_and_is_only_rewards()
    {
        MenuAttachment sent = MenuAttachment.NodeRewards(13, Sample());
        Assert.Equal((MenuAttachmentKind.NodeRewards, MenuAttachment.NodeRewardsFormat), (sent.Kind, sent.Format));
        Assert.Contains("node rewards for #13", sent.ToString(), StringComparison.Ordinal);
        Assert.True(MenuAttachment.IsKnown(MenuAttachmentKind.NodeRewards));
        Assert.False(sent.TryGetEventChoices(out _));
        Assert.False(sent.TryGetNpcLine(out _));
        Assert.False(sent.TryGetRunSummary(out _));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetNodeRewards(out NodeRewardsData? rewards));
        Assert.Equal(Encoded(Sample()), Encoded(rewards!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.NodeRewards(0, Sample()));
    }
}
