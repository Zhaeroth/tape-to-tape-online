using System;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>The host's campaign run team's look beyond its run file (away kits, transitions, goalie colours): a custom content item.</summary>
public class RunLookTests
{
    /// <summary>A custom campaign team's look: an away kit with a missing scheme, no away goalie kit, three transitions, goalie colours.</summary>
    public static RunLookData Sample() => new()
    {
        Away = Kit(0.1f),
        AwayGoalie = null,
        PrimaryTransition = new ColorValue(1, 0, 0, 1),
        SecondaryTransition = new ColorValue(0, 1, 0, 1),
        TertiaryTransition = new ColorValue(0, 0, 1, 0.5f),
        Goalie = Kit(0.7f),
    };

    private static ColorSchemeData?[] Kit(float shade) => Enumerable.Range(0, FreeAgentData.SchemeCount)
        .Select(i => i == 2 ? null : new ColorSchemeData(new ColorValue(shade, 0.5f, 0.25f, 1), new ColorValue(0, 0, 0, 1), new ColorValue(1, 1, 1, 1)))
        .ToArray();

    [Fact]
    public void A_look_round_trips_as_an_item()
    {
        RunLookData sent = Sample();
        CustomItem item = sent.ToItem();
        Assert.Equal((CustomContentKind.RunLook, RunLookData.Key), (item.Kind, item.Key));
        Assert.False(item.IsLogo);
        Assert.True(RunLookData.TryRead(item.Bytes, out RunLookData? read));
        Assert.Null(sent.FirstDifference(read!));
        Assert.Null(read!.AwayGoalie);
        Assert.Null(read.Away![2]);
        Assert.Equal(item.Bytes, read.ToBytes());
        Assert.Equal("away kit, 3 transition colours, goalie colours", read.ToString());
        Assert.Equal("runlook", CustomRef.Name(CustomContentKind.RunLook));
        Assert.True(CustomRef.TryParse(item.Ref.Format(), out CustomRef back));
        Assert.Equal(item.Ref, back);
    }

    [Fact]
    public void Differences_are_named()
    {
        RunLookData host = Sample();
        RunLookData other = new()
        {
            Away = host.Away,
            AwayGoalie = host.AwayGoalie,
            PrimaryTransition = new ColorValue(0.5f, 0, 0, 1),
            SecondaryTransition = host.SecondaryTransition,
            TertiaryTransition = host.TertiaryTransition,
            Goalie = host.Goalie,
        };
        Assert.StartsWith("first transition colour (0.5/0/0/1 here, 1/0/0/1 on the host's)", host.FirstDifference(other), StringComparison.Ordinal);
        RunLookData noAway = new() { Goalie = host.Goalie, PrimaryTransition = host.PrimaryTransition, SecondaryTransition = host.SecondaryTransition, TertiaryTransition = host.TertiaryTransition };
        Assert.StartsWith("away kit (none here, some on the host's)", host.FirstDifference(noAway), StringComparison.Ordinal);
    }

    [Fact]
    public void Looks_out_of_range_or_malformed_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new RunLookData { PrimaryTransition = new ColorValue(float.NaN, 0, 0, 1) }.ToBytes());
        Assert.Throws<ArgumentException>(() => new RunLookData { Away = new ColorSchemeData?[3] }.ToBytes());
        Assert.NotNull(new RunLookData { Goalie = Kit(2000) }.Problem());

        byte[] good = Sample().ToBytes();
        Assert.False(RunLookData.TryRead(Array.Empty<byte>(), out _));
        Assert.False(RunLookData.TryRead(new byte[RunLookData.MaxBytes + 1], out _));
        byte[] version = (byte[])good.Clone();
        version[0] = 2;
        Assert.False(RunLookData.TryRead(version, out _));
        for (int length = 1; length < good.Length; length++)
        {
            Assert.False(RunLookData.TryRead(good.AsSpan(0, length), out RunLookData? cut));
            Assert.Null(cut);
        }

        Assert.False(RunLookData.TryRead(good.Concat(new byte[] { 0 }).ToArray(), out _));

        // A colour past the range, crafted.
        var writer = new BitWriter();
        writer.WriteByte(RunLookData.Version);
        writer.WriteVarUInt(7);
        writer.WriteBool(false);
        writer.WriteBool(false);
        for (int i = 0; i < 12; i++)
        {
            writer.WriteSingle(i == 4 ? float.PositiveInfinity : 1);
        }

        writer.WriteBool(false);
        Assert.False(RunLookData.TryRead(writer.ToArray(), out _));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(66);
        byte[] seed = Sample().ToBytes();
        for (int round = 0; round < 2000; round++)
        {
            byte[] bytes = round % 2 == 0 ? new byte[random.Next(0, 400)] : (byte[])seed.Clone();
            if (round % 2 == 0)
            {
                random.NextBytes(bytes);
            }
            else
            {
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            }

            if (RunLookData.TryRead(bytes, out RunLookData? look))
            {
                Assert.Null(look!.Problem());
                Assert.True(RunLookData.TryRead(look.ToBytes(), out _));
            }
        }
    }

    [Fact]
    public void A_look_that_comes_back_reaches_the_friend_again()
    {
        // The host's run team goes from look A to B and back to A (a run start left for another squad, then the first again).
        RunLookData a = Sample();
        RunLookData b = new()
        {
            Away = a.Away,
            AwayGoalie = a.AwayGoalie,
            PrimaryTransition = new ColorValue(0.2f, 0.2f, 0.2f, 1),
            SecondaryTransition = a.SecondaryTransition,
            TertiaryTransition = a.TertiaryTransition,
            Goalie = a.Goalie,
        };

        // Unnumbered, the third send is the first item again: the friend's store takes it as a duplicate and never hands it
        // over, so the friend would keep wearing B.
        var unnumbered = new CustomContentReceiver();
        Assert.Equal(new[] { true, true, false }, new[] { a, b, a }.Select(look => Delivered(unnumbered, look.ToItem())).ToArray());

        // Numbered as the host numbers each change, every send is an item of its own, and the newest is A.
        var numbered = new CustomContentReceiver();
        RunLookData[] sent = { a.WithSerial(1), b.WithSerial(2), a.WithSerial(3) };
        Assert.All(sent, look => Assert.True(Delivered(numbered, look.ToItem())));
        Assert.Null(sent[0].FirstDifference(sent[2]));
        Assert.NotEqual(sent[0].ToItem().Hash, sent[2].ToItem().Hash);
        Assert.True(RunLookData.TryRead(sent[2].ToItem().Bytes, out RunLookData? last));
        Assert.Equal(3u, last!.Serial);
        Assert.Null(a.FirstDifference(last));

        // A serial past 32 bits, crafted, is refused.
        var writer = new BitWriter();
        writer.WriteByte(RunLookData.Version);
        writer.WriteVarUInt((ulong)uint.MaxValue + 1);
        writer.WriteBool(false);
        writer.WriteBool(false);
        for (int i = 0; i < 12; i++)
        {
            writer.WriteSingle(1);
        }

        writer.WriteBool(false);
        Assert.False(RunLookData.TryRead(writer.ToArray(), out _));
    }

    [Fact]
    public void The_item_kind_holds_only_a_small_look()
    {
        Assert.True(CustomItem.IsKnown(CustomContentKind.RunLook));
        Assert.Null(CustomItem.Check(CustomContentKind.RunLook, RunLookData.Key, RunLookData.MaxBytes));
        Assert.NotNull(CustomItem.Check(CustomContentKind.RunLook, RunLookData.Key, RunLookData.MaxBytes + 1));
    }

    /// <summary>Sends <paramref name="item"/> to <paramref name="receiver"/> piece by piece; true when the receiver handed it over as a new item.</summary>
    private static bool Delivered(CustomContentReceiver receiver, CustomItem item)
    {
        int offset = 0;
        while (offset < item.Bytes.Length)
        {
            var writer = new BitWriter();
            offset = CustomContentMessage.Write(writer, item, offset);
            var reader = new BitReader(writer.AsSpan());
            Assert.True(CustomContentMessage.TryRead(ref reader, out CustomChunk chunk));
            Assert.True(receiver.Receive(chunk));
        }

        return receiver.TryTakeCompleted(out CustomItem? completed) && completed!.Hash == item.Hash;
    }
}
