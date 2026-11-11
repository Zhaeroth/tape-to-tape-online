using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class UnlockStatesDataTests
{
    /// <summary>A small screen: rows in three categories (one id in two of them), quest rows of each kind, and the tabs.</summary>
    public static UnlockStatesData Sample() => new()
    {
        Rows = new[]
        {
            new UnlockRowState("relic_bean_diet", 2, true, 1, 1),
            new UnlockRowState("talent_sniper", 1, false, 37, 50),
            new UnlockRowState("ability_dash", 0, false, 0, 3),
            new UnlockRowState("talent_sniper", 4, true, 50, 50), // the same id in another category is another row
            new UnlockRowState("équipe_été", 3, false, 0, 0),
        },
        Quests = new[]
        {
            new QuestRowState("fwd-kirby", true, false, 5, 5),
            new QuestRowState("fwd-sally", false, false, 2, 7),
            new QuestRowState("fwd-johnny", false, true, 0, 1),
        },
        TabCodes = new[] { UnlockStatesData.AllTab, 0, 1, 2, 3, 4, UnlockStatesData.QuestCategory },
    };

    public static byte[] Encoded(UnlockStatesData states)
    {
        var writer = new BitWriter();
        states.Write(writer);
        return writer.ToArray();
    }

    private static UnlockStatesData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = UnlockStatesData.TryRead(ref reader, out UnlockStatesData? states);
        Assert.Equal(ok, states != null);
        return ok && reader.RemainingBits < 8 ? states : null;
    }

    [Fact]
    public void States_round_trip_row_by_row()
    {
        UnlockStatesData sample = Sample();
        UnlockStatesData read = Read(Encoded(sample))!;
        Assert.NotNull(read);
        Assert.Equal(sample.Rows, read.Rows);
        Assert.Equal(sample.Quests, read.Quests);
        Assert.Equal(sample.TabCodes, read.TabCodes);
        Assert.Equal("5 unlock row(s) (2 unlocked), 3 quest row(s) (1 completed, 1 secret), 7 tab(s)", read.ToString());

        // Empty, and every list at its cap with ids at theirs.
        var empty = new UnlockStatesData();
        Assert.Equal(Encoded(empty), Encoded(Read(Encoded(empty))!));
        string key = new('k', UnlockStatesData.MaxKeyBytes - 4);
        var full = new UnlockStatesData
        {
            Rows = Enumerable.Range(0, UnlockStatesData.MaxRows)
                .Select(i => new UnlockRowState($"{key}{i:0000}", i % (UnlockStatesData.MaxCategory + 1), i % 2 == 0, UnlockStatesData.MaxCount, UnlockStatesData.MaxCount)).ToList(),
            Quests = Enumerable.Range(0, UnlockStatesData.MaxQuests).Select(i => new QuestRowState($"{key}{i:0000}", false, true, 0, 1)).ToList(),
            TabCodes = Enumerable.Range(0, UnlockStatesData.MaxTabs).Select(i => i % (UnlockStatesData.MaxTabCode + 1)).ToList(),
        };
        Assert.Null(full.Problem());
        byte[] bytes = Encoded(full);
        Assert.True(bytes.Length < MenuAttachment.MaxBodyBytes);
        Assert.Equal(bytes, Encoded(Read(bytes)!));
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        UnlockRowState row = new("relic", 2, false, 0, 1);
        QuestRowState quest = new("fwd-kirby", false, false, 0, 1);
        yield return new object[] { "961 rows", new UnlockStatesData { Rows = Enumerable.Range(0, UnlockStatesData.MaxRows + 1).Select(i => row with { Key = $"r{i}" }).ToList() } };
        yield return new object[] { "64 quest rows", new UnlockStatesData { Quests = Enumerable.Range(0, UnlockStatesData.MaxQuests + 1).Select(i => quest with { Benchwarmer = $"q{i}" }).ToList() } };
        yield return new object[] { "17 tabs", new UnlockStatesData { TabCodes = Enumerable.Repeat(1, UnlockStatesData.MaxTabs + 1).ToList() } };
        yield return new object[] { "a 65-byte id", new UnlockStatesData { Rows = new[] { row with { Key = new string('x', UnlockStatesData.MaxKeyBytes + 1) } } } };
        yield return new object[] { "a 66-byte id in two-byte letters", new UnlockStatesData { Rows = new[] { row with { Key = new string('é', 33) } } } };
        yield return new object[] { "an empty id", new UnlockStatesData { Rows = new[] { row with { Key = string.Empty } } } };
        yield return new object[] { "an id with a control character", new UnlockStatesData { Rows = new[] { row with { Key = "re\nlic" } } } };
        yield return new object[] { "an id that isn't text", new UnlockStatesData { Rows = new[] { row with { Key = "\uD800" } } } };
        yield return new object[] { "a missing benchwarmer", new UnlockStatesData { Quests = new[] { quest with { Benchwarmer = null! } } } };
        yield return new object[] { "a category past the cap", new UnlockStatesData { Rows = new[] { row with { Category = UnlockStatesData.MaxCategory + 1 } } } };
        yield return new object[] { "a negative category", new UnlockStatesData { Rows = new[] { row with { Category = -1 } } } };
        yield return new object[] { "progress past the cap", new UnlockStatesData { Rows = new[] { row with { Progress = UnlockStatesData.MaxCount + 1 } } } };
        yield return new object[] { "a negative total", new UnlockStatesData { Quests = new[] { quest with { Total = -1 } } } };
        yield return new object[] { "an id twice in one category", new UnlockStatesData { Rows = new[] { row, row with { Unlocked = true } } } };
        yield return new object[] { "a benchwarmer twice", new UnlockStatesData { Quests = new[] { quest, quest with { Progress = 1 } } } };
        yield return new object[] { "a secret completed quest", new UnlockStatesData { Quests = new[] { quest with { Completed = true, Secret = true } } } };
        yield return new object[] { "a tab code past the cap", new UnlockStatesData { TabCodes = new[] { UnlockStatesData.MaxTabCode + 1 } } };
        yield return new object[] { "a missing row", new UnlockStatesData { Rows = new UnlockRowState[] { null! } } };
        yield return new object[] { "a missing list", new UnlockStatesData { Quests = null! } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void States_out_of_their_limits_are_refused_when_written_and_never_sent(string what, UnlockStatesData states)
    {
        Assert.False(string.IsNullOrEmpty(what));
        Assert.NotNull(states.Problem());
        Assert.ThrowsAny<ArgumentException>(() => states.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.UnlockStates(5, states));
    }

    [Fact]
    public void Crafted_bodies_that_break_a_rule_are_refused()
    {
        byte[] good = Encoded(Sample());
        Assert.NotNull(Read(good));
        for (int length = 0; length < good.Length; length++)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));

        // One row, written by hand: its flags with a bit this version doesn't know, its id over the cap or empty, its counts
        // past the cap; the counts of rows, quest rows and tabs past theirs.
        Assert.NotNull(Read(Body(flags: 1)));
        Assert.Null(Read(Body(flags: 2)));
        Assert.Null(Read(Body(flags: 0x80)));
        Assert.Null(Read(Body(keyLength: UnlockStatesData.MaxKeyBytes + 1)));
        Assert.Null(Read(Body(keyLength: 0)));
        Assert.NotNull(Read(Body(keyLength: UnlockStatesData.MaxKeyBytes)));
        Assert.Null(Read(Body(progress: UnlockStatesData.MaxCount + 1UL)));
        Assert.Null(Read(Body(progress: ulong.MaxValue)));
        Assert.Null(Read(Body(category: UnlockStatesData.MaxCategory + 1)));
        Assert.Null(Read(Body(rows: UnlockStatesData.MaxRows + 1UL)));
        Assert.Null(Read(Body(quests: UnlockStatesData.MaxQuests + 1UL)));
        Assert.Null(Read(Body(tabs: UnlockStatesData.MaxTabs + 1UL)));
        Assert.Null(Read(Body(questFlags: 4)));
        Assert.Null(Read(Body(questFlags: 3))); // secret and completed
        Assert.NotNull(Read(Body(questFlags: 2)));
        Assert.Null(Read(Body(tabCode: UnlockStatesData.MaxTabCode + 1)));

        // An id that isn't UTF-8.
        byte[] bad = Body(keyLength: 2);
        Assert.NotNull(Read(bad));
        Assert.Null(Read(Body(keyLength: 2, keyByte: 0xC3)));

        static byte[] Body(byte flags = 0, int keyLength = 5, ulong progress = 3, int category = 2, ulong rows = 1, ulong quests = 1, ulong tabs = 1,
            byte questFlags = 0, int tabCode = 2, byte keyByte = (byte)'a')
        {
            var writer = new BitWriter();
            writer.WriteVarUInt(rows);
            writer.WriteVarUInt((ulong)keyLength);
            for (int i = 0; i < keyLength; i++)
            {
                writer.WriteByte(keyByte);
            }

            writer.WriteByte((byte)category);
            writer.WriteByte(flags);
            writer.WriteVarUInt(progress);
            writer.WriteVarUInt(10);
            writer.WriteVarUInt(quests);
            writer.WriteVarUInt(3);
            writer.WriteBytes("fwd"u8);
            writer.WriteByte(questFlags);
            writer.WriteVarUInt(0);
            writer.WriteVarUInt(1);
            writer.WriteVarUInt(tabs);
            writer.WriteByte((byte)tabCode);
            return writer.ToArray();
        }
    }

    [Fact]
    public void Cursor_keys_name_a_row_or_a_quest_row()
    {
        Assert.Equal(7, UnlockStatesData.RowKey(7));
        Assert.Equal(UnlockStatesData.MaxRows + 2, UnlockStatesData.QuestKey(2));
        Assert.Equal(MenuTarget.MaxIndex, UnlockStatesData.MaxKey); // every key fits a menu step

        Assert.True(UnlockStatesData.TryKey(UnlockStatesData.RowKey(959), out bool quest, out int index));
        Assert.Equal((false, 959), (quest, index));
        Assert.True(UnlockStatesData.TryKey(UnlockStatesData.QuestKey(62), out quest, out index));
        Assert.Equal((true, 62), (quest, index));
        Assert.False(UnlockStatesData.TryKey(-1, out _, out _));
        Assert.False(UnlockStatesData.TryKey(UnlockStatesData.MaxKey + 1, out _, out _));
    }

    [Fact]
    public void A_tab_is_named_by_what_it_lists()
    {
        Assert.Equal(2, UnlockStatesData.TabCode(new[] { 2, 2, 2 }));
        Assert.Equal(UnlockStatesData.QuestCategory, UnlockStatesData.TabCode(new[] { 10 }));
        Assert.Equal(UnlockStatesData.AllTab, UnlockStatesData.TabCode(new[] { 2, 1, 2 }));
        Assert.Equal(-1, UnlockStatesData.TabCode(Array.Empty<int>()));
        Assert.Equal(-1, UnlockStatesData.TabCode(new[] { UnlockStatesData.MaxCategory + 1 }));
        Assert.Equal(-1, UnlockStatesData.TabCode(new[] { -3 }));
    }

    [Fact]
    public void An_attachment_of_states_round_trips_and_is_only_states()
    {
        MenuAttachment sent = MenuAttachment.UnlockStates(30, Sample());
        Assert.Equal((MenuAttachmentKind.UnlockStates, MenuAttachment.UnlockStatesFormat), (sent.Kind, sent.Format));
        Assert.Contains("unlock states for #30", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetHistoryPage(out _));
        Assert.False(sent.TryGetRunSummary(out _));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetUnlockStates(out UnlockStatesData? states));
        Assert.Equal(Encoded(Sample()), Encoded(states!));

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.UnlockStates(0, Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.UnlockStates(1, null!));
        Assert.False(MenuAttachment.Prompt(30, PromptDataTests.Notice()).TryGetUnlockStates(out _));

        // A states body under another format, or another kind's body under this kind, isn't one.
        byte[] body = Encoded(Sample());
        Assert.Null(Wire(30, MenuAttachmentKind.UnlockStates, 2, body));
        Assert.Null(Wire(30, MenuAttachmentKind.UnlockStates, MenuAttachment.UnlockStatesFormat, PromptDataTests.Encoded(PromptDataTests.Notice())));
        Assert.NotNull(Wire(30, MenuAttachmentKind.UnlockStates, MenuAttachment.UnlockStatesFormat, body));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[] seed = Encoded(Sample());
        var random = new Random(66);
        int read = 0;
        for (int round = 0; round < 6000; round++)
        {
            byte[] bytes = (byte[])seed.Clone();
            if (round % 4 == 3)
            {
                bytes = new byte[random.Next(0, 160)];
                random.NextBytes(bytes);
            }
            else
            {
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            var reader = new BitReader(bytes);
            if (!UnlockStatesData.TryRead(ref reader, out UnlockStatesData? states))
            {
                Assert.Null(states);
                continue;
            }

            read++;
            Assert.Null(states!.Problem());
            Assert.Equal(Encoded(states), Encoded(Read(Encoded(states))!));
            _ = states.ToString();
        }

        Assert.True(read > 0, "some changed bodies should still read");
    }

    /// <summary>An attachment message around <paramref name="body"/> as a host would send it, read back (null when refused).</summary>
    internal static MenuAttachment? Wire(uint serial, MenuAttachmentKind kind, byte format, byte[] body)
    {
        var writer = new BitWriter();
        writer.WriteUInt32(serial);
        writer.WriteByte((byte)kind);
        writer.WriteByte(format);
        writer.WriteVarUInt((ulong)body.Length);
        byte[] deflated = Compression.Deflate(body);
        writer.WriteVarUInt((ulong)deflated.Length);
        writer.WriteBytes(deflated);
        var reader = new BitReader(writer.AsSpan());
        return MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read) ? read : null;
    }
}
