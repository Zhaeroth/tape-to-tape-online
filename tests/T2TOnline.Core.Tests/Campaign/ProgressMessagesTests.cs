using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class ProgressMessagesTests
{
    private const ulong Session = 0x1234_5678_9abc_def0;

    private static List<ProgressEntry> EveryKind() => new()
    {
        new StatEntry(9, StatOp.Increment, StatValue.None),
        new StatEntry(9, StatOp.IncrementCampaign, StatValue.None),
        new StatEntry(12, StatOp.IncrementCampaignBy, StatValue.Whole(3)),
        new StatEntry(40, StatOp.IncrementBy, StatValue.Whole(2)),
        new StatEntry(21, StatOp.TrySetLargerCampaign, StatValue.Whole(ProgressLimits.MaxStatValue)),
        new StatEntry(11, StatOp.TrySetSmaller, StatValue.Real(12.75f)),
        new UnlockPassEntry(4, 1, true, Array.Empty<string>()),
        new UnlockPassEntry(0, -1, false, new[] { "BeanDiet", "TribalMask" }),
        new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 3456.5f),
        new SquadEntry(SquadOutcome.Lost, "generalmanager", 0, 0f),
        new ComicsEntry(),
        new RunHistoryRecord(RunHistoryDataTests.Sample()),
        new QuestEntry(QuestStep.MatchEnded, 0),
        new QuestEntry(QuestStep.NewAct, 2),
        new QuestEntry(QuestStep.RunWon, 0),
        new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 3456.5f, "Kovalski", 14),
        new SquadEntry(SquadOutcome.Lost, "gauntlet", 3, 99f, "Zoë Brûlé", ProgressLimits.MaxRunGoals),
        new RunEndingEntry(),
        ProfileMarkEntry.NurseOffered(0),
        ProfileMarkEntry.NurseOffered(ProgressLimits.MaxProcedure),
        ProfileMarkEntry.DialogueSeen("chaosNode/visit_1"),
    };

    private static byte[] Message(ProgressBatch batch, uint entitledFrom = 1)
    {
        byte[] body = ProgressBatchMessage.EncodeBody(batch)!;
        var writer = new BitWriter();
        ProgressBatchMessage.Write(writer, Session, entitledFrom, body);
        return writer.ToArray();
    }

    private static bool Read(byte[] bytes, out ProgressMessage? message)
    {
        var reader = new BitReader(bytes);
        return ProgressBatchMessage.TryRead(ref reader, out message);
    }

    [Fact]
    public void A_batch_of_every_kind_round_trips_in_order()
    {
        var batch = new ProgressBatch(7, 12, "4f8e2b1c-run", EveryKind());
        Assert.True(Read(Message(batch, entitledFrom: 3), out ProgressMessage? message));
        Assert.Equal(Session, message!.Session);
        Assert.Equal(3u, message.EntitledFrom);
        Assert.Equal(7u, message.Batch.Sequence);
        Assert.Equal(12u, message.Batch.Checkpoint);
        Assert.Equal("4f8e2b1c-run", message.Batch.Run);
        Assert.Equal(batch.Entries.Count, message.Batch.Entries.Count);
        for (int i = 0; i < batch.Entries.Count; i++)
        {
            ProgressEntry sent = batch.Entries[i], read = message.Batch.Entries[i];
            switch (sent)
            {
                case UnlockPassEntry pass:
                    var readPass = Assert.IsType<UnlockPassEntry>(read);
                    Assert.Equal((pass.Timing, pass.NotificationTiming, pass.Queue), (readPass.Timing, readPass.NotificationTiming, readPass.Queue));
                    Assert.Equal(pass.Earned, readPass.Earned);
                    break;
                case RunHistoryRecord history:
                    Assert.Equal(RunHistoryDataTests.Encoded(history.Entry), RunHistoryDataTests.Encoded(Assert.IsType<RunHistoryRecord>(read).Entry));
                    break;
                default:
                    Assert.Equal(sent, read); // value records: field by field
                    break;
            }
        }
    }

    [Fact]
    public void An_adding_operation_that_takes_a_little_back_round_trips()
    {
        // The game takes back a goal against it cancelled: TotalGoalsAgainst by -1.
        var entries = new ProgressEntry[]
        {
            new StatEntry(10, StatOp.IncrementBy, StatValue.Whole(-1)),
            new StatEntry(10, StatOp.IncrementCampaignBy, StatValue.Whole(-ProgressLimits.MaxStatDecrement)),
            new StatEntry(60, StatOp.IncrementPlayNowBy, StatValue.Real(-2.5f)),
        };
        Assert.True(Read(Message(new ProgressBatch(1, 0, string.Empty, entries)), out ProgressMessage? message));
        Assert.Equal(entries, message!.Batch.Entries);
        Assert.Equal(-1, ProgressLimits.CreditOf((StatEntry)entries[0]));
        Assert.True(ProgressLimits.IsWithinBatchLimits(entries));
    }

    [Fact]
    public void The_body_is_encoded_once_and_read_behind_each_friends_own_prefix()
    {
        var batch = new ProgressBatch(5, 0, string.Empty, new ProgressEntry[] { new StatEntry(9, StatOp.IncrementCampaign, StatValue.None) });
        byte[] body = ProgressBatchMessage.EncodeBody(batch)!;
        foreach (uint entitledFrom in new uint[] { 1, 5 })
        {
            var writer = new BitWriter();
            writer.WriteBits(5, 3); // a header that isn't byte-aligned
            ProgressBatchMessage.Write(writer, Session, entitledFrom, body);
            var reader = new BitReader(writer.ToArray());
            Assert.Equal(5ul, reader.ReadBits(3));
            Assert.True(ProgressBatchMessage.TryRead(ref reader, out ProgressMessage? message));
            Assert.Equal(entitledFrom, message!.EntitledFrom);
            Assert.Equal(new StatEntry(9, StatOp.IncrementCampaign, StatValue.None), message.Batch.Entries.Single());
        }
    }

    [Fact]
    public void Entries_outside_the_limits_never_make_a_batch()
    {
        ProgressEntry[] bad =
        {
            new StatEntry(-1, StatOp.Increment, StatValue.None),
            new StatEntry(ProgressLimits.MaxStat + 1, StatOp.Increment, StatValue.None),
            new StatEntry(1, StatOp.None, StatValue.None),
            new StatEntry(1, (StatOp)18, StatValue.Whole(1)), // an operation this build doesn't know
            new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.ResetCampaign, StatValue.Whole(1)), // a reset takes no value
            new StatEntry(1, StatOp.Increment, StatValue.Whole(1)), // a plain increment takes no value
            new StatEntry(1, StatOp.IncrementBy, StatValue.None),
            new StatEntry(1, StatOp.TrySetSmaller, StatValue.Real(float.NaN)),
            new StatEntry(1, StatOp.TrySetSmaller, StatValue.Real(float.PositiveInfinity)),
            new StatEntry(1, StatOp.IncrementCampaignBy, StatValue.Whole(-ProgressLimits.MaxStatDecrement - 1)), // takes too much back at once
            new StatEntry(1, StatOp.IncrementCampaignBy, StatValue.Whole(ProgressLimits.MaxStatValue + 1)),
            new StatEntry(1, StatOp.IncrementBy, StatValue.Real(-ProgressLimits.MaxStatDecrement - 0.5f)),
            new StatEntry(1, StatOp.IncrementBy, StatValue.Real(ProgressLimits.MaxStatValue * 2f)),
            new StatEntry(1, StatOp.TrySetSmallerCampaign, StatValue.Whole(-1)), // a fastest time below zero
            new StatEntry(1, StatOp.TrySetLargerCampaign, StatValue.Whole(int.MaxValue)),
            new UnlockPassEntry(ProgressLimits.MaxTiming + 1, 0, true, Array.Empty<string>()),
            new UnlockPassEntry(0, -2, true, Array.Empty<string>()),
            new UnlockPassEntry(0, ProgressLimits.MaxNotificationTiming + 1, true, Array.Empty<string>()),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 1, 1f, new string('n', ProgressLimits.MaxScorerBytes + 1), 3), // a best scorer's name past its cap
            new SquadEntry(SquadOutcome.Won, "gauntlet", 1, 1f, new string('é', (ProgressLimits.MaxScorerBytes / 2) + 1), 3), // in bytes, not letters
            new SquadEntry(SquadOutcome.Won, "gauntlet", 1, 1f, "Kov\nalski", 3),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 1, 1f, "Kovalski", 0), // a scorer with no goals
            new SquadEntry(SquadOutcome.Won, "gauntlet", 1, 1f, "Kovalski", ProgressLimits.MaxRunGoals + 1),
            new UnlockPassEntry(0, 0, true, new[] { "has space" }),
            new UnlockPassEntry(0, 0, true, Enumerable.Repeat("Id", ProgressLimits.MaxEarned + 1).ToArray()),
            new SquadEntry(SquadOutcome.None, "gauntlet", 1, 0f),
            new SquadEntry(SquadOutcome.Won, string.Empty, 1, 0f),
            new SquadEntry(SquadOutcome.Won, "gauntlet", ProgressLimits.MaxDifficulty + 1, 0f),
            new SquadEntry(SquadOutcome.Won, "gauntlet", 1, -1f),
            new RunHistoryRecord(null!),
            new RunHistoryRecord(RunHistoryDataTests.Huge()),
            new RunHistoryRecord(new RunHistoryData { RunTime = float.PositiveInfinity }),
            new QuestEntry(QuestStep.None, 0),
            new QuestEntry(QuestStep.NewAct, ProgressLimits.MaxAct + 1),
        };
        foreach (ProgressEntry entry in bad)
        {
            Assert.False(ProgressLimits.IsValid(entry), entry.ToString());
            Assert.Throws<ArgumentException>(() => new ProgressBatch(1, 0, string.Empty, new[] { entry }));
        }

        // Each of those limits at its very edge is still an entry, and reads back.
        var edges = new ProgressEntry[]
        {
            new UnlockPassEntry(ProgressLimits.MaxTiming, ProgressLimits.MaxNotificationTiming, true, Enumerable.Repeat("Id", ProgressLimits.MaxEarned).ToArray()),
            new SquadEntry(SquadOutcome.Won, new string('s', ProgressLimits.MaxIdBytes), ProgressLimits.MaxDifficulty, 1f, new string('é', ProgressLimits.MaxScorerBytes / 2), ProgressLimits.MaxRunGoals),
            new QuestEntry(QuestStep.RunFailed, ProgressLimits.MaxAct),
            ProfileMarkEntry.DialogueSeen(new string('d', ProgressLimits.MaxIdBytes)),
            new StatEntry(ProgressLimits.MaxStat, StatOp.IncrementBy, StatValue.Whole(-ProgressLimits.MaxStatDecrement)),
        };
        Assert.All(edges, e => Assert.True(ProgressLimits.IsValid(e), e.ToString()));
        Assert.True(Read(Message(new ProgressBatch(1, 0, new string('r', ProgressLimits.MaxIdBytes), edges)), out ProgressMessage? atEdges));
        Assert.Equal(edges.Skip(1), atEdges!.Batch.Entries.Skip(1));

        Assert.False(ProgressLimits.IsValid(null));
        Assert.Throws<ArgumentException>(() => new ProgressBatch(0, 0, string.Empty, Array.Empty<ProgressEntry>()));
        Assert.Throws<ArgumentException>(() => new ProgressBatch(1, 0, "not a run", Array.Empty<ProgressEntry>()));
    }

    [Theory]
    [InlineData("BeanDiet", true)]
    [InlineData("4f8e2b1c-9d0a-4e3f-8b7a-123456789abc", true)]
    [InlineData("generalmanager", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("in side", false)]
    [InlineData("tab\there", false)]
    [InlineData("café", false)]
    [InlineData("x\u0000", false)]
    public void Ids_are_plain_identifiers(string id, bool plausible) => Assert.Equal(plausible, ProgressLimits.IsPlausibleId(id));

    [Fact]
    public void An_id_too_long_is_not_plausible() => Assert.False(ProgressLimits.IsPlausibleId(new string('a', ProgressLimits.MaxIdBytes + 1)));

    [Fact]
    public void The_caps_on_what_a_host_sends_and_a_friend_keeps_are_the_ones_written_here()
    {
        // The other tests check that each cap is enforced, at the cap's own value; this pins the values, so that a cap
        // raised (a host credits more, a friend holds or stores more) is a change someone made here on purpose.
        Assert.Equal(
            new (string Cap, long Value)[]
            {
                ("MaxStat", 4095), ("MaxTiming", 31), ("MaxEarned", 8), ("MaxIdBytes", 64), ("MaxTextBytes", 128), ("MaxDifficulty", 63), ("MaxAct", 15),
                ("MaxRunGoals", 1000), ("MaxScorerBytes", 64), ("MaxProcedure", 63), ("MaxMarksPerBatch", 64), ("MaxRunHistoryBytes", 256 * 1024),
                ("MaxNotificationTiming", 0xFFFF), ("MaxStatValue", 1_000_000), ("MaxStatDecrement", 100), ("MaxStatDecrementPerSession", 1_000),
                ("MaxStatCreditPerBatch", 1_000_000), ("MaxStatCreditPerSession", 1_000_000), ("MaxStatCreditPerHostDay", 200_000),
                ("MaxStatDecrementPerHostDay", 1_000), ("batch entries", 4096), ("batch message bytes", 500 * 1024),
                ("inbox held", 256), ("outbox bytes", 8L * 1024 * 1024), ("resend ranges", 8),
                ("ledger hosts", 64), ("ledger sessions", 32), ("ledger runs", 32), ("ledger once", 96), ("ledger missing", 64), ("ledger unlocks", 256),
                ("entity list", 16), ("entities per session", 1024), ("checkpoint parts", 8), ("checkpoint part bytes", 4 * 1024 * 1024),
                ("checkpoint message bytes", 500 * 1024), ("run file depth", 64), ("run file bytes", 4 * 1024 * 1024),
                ("run copies kept", 3), ("run copies followed", 8), ("run copy asks", 3),
                ("unlock rows", 960), ("quest rows", 63), ("unlock key bytes", 64), ("unlock count", 1_000_000), ("unlock tabs", 16),
                ("history logos kept", 32), ("history logo bytes kept", 48L * 1024 * 1024), ("history logos sent", 8), ("history logo bytes sent", 12L * 1024 * 1024),
            },
            new (string, long)[]
            {
                ("MaxStat", ProgressLimits.MaxStat), ("MaxTiming", ProgressLimits.MaxTiming), ("MaxEarned", ProgressLimits.MaxEarned),
                ("MaxIdBytes", ProgressLimits.MaxIdBytes), ("MaxTextBytes", ProgressLimits.MaxTextBytes), ("MaxDifficulty", ProgressLimits.MaxDifficulty),
                ("MaxAct", ProgressLimits.MaxAct), ("MaxRunGoals", ProgressLimits.MaxRunGoals), ("MaxScorerBytes", ProgressLimits.MaxScorerBytes),
                ("MaxProcedure", ProgressLimits.MaxProcedure), ("MaxMarksPerBatch", ProgressLimits.MaxMarksPerBatch),
                ("MaxRunHistoryBytes", ProgressLimits.MaxRunHistoryBytes), ("MaxNotificationTiming", ProgressLimits.MaxNotificationTiming),
                ("MaxStatValue", ProgressLimits.MaxStatValue), ("MaxStatDecrement", ProgressLimits.MaxStatDecrement),
                ("MaxStatDecrementPerSession", ProgressLimits.MaxStatDecrementPerSession), ("MaxStatCreditPerBatch", ProgressLimits.MaxStatCreditPerBatch),
                ("MaxStatCreditPerSession", ProgressLimits.MaxStatCreditPerSession), ("MaxStatCreditPerHostDay", ProgressLimits.MaxStatCreditPerHostDay),
                ("MaxStatDecrementPerHostDay", ProgressLimits.MaxStatDecrementPerHostDay), ("batch entries", ProgressBatchMessage.MaxEntries),
                ("batch message bytes", ProgressBatchMessage.MaxMessageBytes), ("inbox held", ProgressInbox.MaxHeld), ("outbox bytes", ProgressOutbox.MaxStoredBytes),
                ("resend ranges", ProgressOutbox.MaxResendRanges), ("ledger hosts", ProgressLedger.MaxBudgetHosts), ("ledger sessions", ProgressLedger.MaxSessions),
                ("ledger runs", ProgressLedger.MaxRuns), ("ledger once", ProgressLedger.MaxOnce), ("ledger missing", ProgressLedger.MaxMissing),
                ("ledger unlocks", ProgressLedger.MaxUnlocks), ("entity list", EntityStatSchema.MaxListLength), ("entities per session", EntityCredit.MaxEntitiesPerSession),
                ("checkpoint parts", RunCheckpoint.MaxParts), ("checkpoint part bytes", RunCheckpoint.MaxPartBytes),
                ("checkpoint message bytes", RunCheckpointMessage.MaxMessageBytes), ("run file depth", RunFileTypes.MaxDepth), ("run file bytes", RunFileTypes.MaxBytes),
                ("run copies kept", RunCopyStore.MaxRuns), ("run copies followed", RunCopyRequests.MaxRuns), ("run copy asks", RunCopyRequests.MaxAsks),
                ("unlock rows", UnlockStatesData.MaxRows), ("quest rows", UnlockStatesData.MaxQuests), ("unlock key bytes", UnlockStatesData.MaxKeyBytes),
                ("unlock count", UnlockStatesData.MaxCount), ("unlock tabs", UnlockStatesData.MaxTabs),
                ("history logos kept", HistoryLogoNames.MaxFiles), ("history logo bytes kept", HistoryLogoNames.MaxTotalBytes),
                ("history logos sent", HistoryLogoShare.MaxLogos), ("history logo bytes sent", HistoryLogoShare.MaxBytes),
            });
    }

    [Fact]
    public void A_truncated_message_is_refused()
    {
        byte[] bytes = Message(new ProgressBatch(2, 1, "run-1", EveryKind()));
        for (int length = 0; length < bytes.Length - 1; length++)
        {
            Assert.False(Read(bytes[..length], out _), $"read {length} of {bytes.Length} bytes");
        }
    }

    [Fact]
    public void A_changed_byte_reads_false_or_within_the_limits()
    {
        byte[] bytes = Message(new ProgressBatch(2, 1, "run-1", EveryKind()));
        for (int i = 0; i < bytes.Length; i++)
        {
            foreach (byte flip in new byte[] { 0x01, 0x10, 0x80, 0xFF })
            {
                byte[] copy = (byte[])bytes.Clone();
                copy[i] ^= flip;
                if (Read(copy, out ProgressMessage? message))
                {
                    Assert.All(message!.Batch.Entries, e => Assert.True(ProgressLimits.IsValid(e)));
                    Assert.True(message.Batch.Run.Length == 0 || ProgressLimits.IsPlausibleId(message.Batch.Run));
                }
            }
        }
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(11);
        byte[] real = Message(new ProgressBatch(2, 1, "run-1", EveryKind()));
        for (int i = 0; i < 5000; i++)
        {
            byte[] junk = new byte[random.Next(0, 160)];
            random.NextBytes(junk);
            if (i % 2 == 0)
            {
                // A plausible start: the real prefix, then noise.
                Array.Copy(real, junk, Math.Min(random.Next(1, 40), Math.Min(real.Length, junk.Length)));
            }

            if (Read(junk, out ProgressMessage? message))
            {
                Assert.All(message!.Batch.Entries, e => Assert.True(ProgressLimits.IsValid(e)));
            }
        }
    }

    [Fact]
    public void Hand_built_out_of_limit_messages_are_refused()
    {
        static byte[] Build(Action<BitWriter> entries, byte format = ProgressBatchMessage.Format, ulong count = 1, string run = "")
        {
            var writer = new BitWriter();
            writer.WriteByte(format);
            writer.WriteUInt64(Session);
            writer.WriteUInt32(1);
            writer.WriteUInt32(1); // sequence
            writer.WriteUInt32(0); // checkpoint
            writer.WriteShortString(run);
            writer.WriteVarUInt(count);
            entries(writer);
            return writer.ToArray();
        }

        static void Stat(BitWriter w, ulong stat, byte op)
        {
            w.WriteByte(1);
            w.WriteVarUInt(stat);
            w.WriteByte(op);
        }

        // The well-formed baseline reads.
        Assert.True(Read(Build(w => Stat(w, 9, (byte)StatOp.Increment)), out _));

        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.Increment), format: 1), out _)); // the format before protocol 6
        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.Increment), format: 3), out _)); // another format
        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.Increment), run: "a run"), out _)); // implausible run id
        Assert.False(Read(Build(w => w.WriteByte(99)), out _)); // unknown kind
        Assert.False(Read(Build(w => Stat(w, ProgressLimits.MaxStat + 1, (byte)StatOp.Increment)), out _));
        Assert.False(Read(Build(w => Stat(w, 9, 0)), out _)); // no op
        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.ResetCampaign)), out _)); // a reset of a stat the game never resets
        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.TrySetLargerPlayNowFromStat + 1)), out _)); // unknown op
        Assert.False(Read(Build(w => Stat(w, 9, 200)), out _));
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.IncrementBy); w.WriteByte(3); w.WriteVarInt(1); }), out _)); // unknown value kind
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.IncrementBy); w.WriteByte(1); w.WriteVarInt((long)int.MaxValue + 1); }), out _));
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.IncrementCampaignBy); w.WriteByte(1); w.WriteVarInt(-ProgressLimits.MaxStatDecrement - 1); }), out _)); // takes too much back
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.TrySetLargerCampaign); w.WriteByte(1); w.WriteVarInt(-1); }), out _)); // a negative record
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.IncrementCampaignBy); w.WriteByte(1); w.WriteVarInt(int.MaxValue); }), out _)); // absurd credit
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.IncrementBy); w.WriteByte(2); w.WriteSingle(-ProgressLimits.MaxStatDecrement - 0.5f); }), out _));
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.TrySetSmaller); w.WriteByte(2); w.WriteSingle(-1f); }), out _)); // a negative record
        Assert.False(Read(Build(w => { Stat(w, 9, (byte)StatOp.TrySetSmaller); w.WriteByte(2); w.WriteSingle(float.NaN); }), out _));
        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.Increment), count: ProgressBatchMessage.MaxEntries + 1), out _));
        Assert.False(Read(Build(w => Stat(w, 9, (byte)StatOp.Increment), count: 1000), out _)); // claims more than the bytes hold
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(2); // unlock check with too many earned
            w.WriteByte(4);
            w.WriteVarInt(1);
            w.WriteBool(true);
            w.WriteByte(ProgressLimits.MaxEarned + 1);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(2); // unlock check with a notification timing past the cap
            w.WriteByte(4);
            w.WriteVarInt(ProgressLimits.MaxNotificationTiming + 1);
            w.WriteBool(true);
            w.WriteByte(0);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(3); // squad whose best scorer's name is past its cap
            w.WriteByte((byte)SquadOutcome.Won);
            w.WriteShortString("gauntlet");
            w.WriteVarUInt(1);
            w.WriteSingle(1f);
            w.WriteShortString(new string('n', ProgressLimits.MaxScorerBytes + 1));
            w.WriteVarUInt(3);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(3); // squad with an implausible id
            w.WriteByte((byte)SquadOutcome.Won);
            w.WriteShortString("bad id");
            w.WriteVarUInt(1);
            w.WriteSingle(1f);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(3); // squad whose best scorer scored more than a run allows
            w.WriteByte((byte)SquadOutcome.Won);
            w.WriteShortString("gauntlet");
            w.WriteVarUInt(1);
            w.WriteSingle(1f);
            w.WriteShortString("Kovalski");
            w.WriteVarUInt(ProgressLimits.MaxRunGoals + 1);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(3); // squad with goals and no scorer
            w.WriteByte((byte)SquadOutcome.Won);
            w.WriteShortString("gauntlet");
            w.WriteVarUInt(1);
            w.WriteSingle(1f);
            w.WriteShortString(string.Empty);
            w.WriteVarUInt(3);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(10); // a mark this build doesn't know
            w.WriteByte(3);
            w.WriteByte(1);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(10); // a Nurse procedure out of range
            w.WriteByte((byte)ProfileMark.NurseOffered);
            w.WriteByte(ProgressLimits.MaxProcedure + 1);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(10); // a seen dialogue that isn't an id
            w.WriteByte((byte)ProfileMark.DialogueSeen);
            w.WriteShortString("two words");
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(9); // two run endings in one batch
            w.WriteByte(9);
        }, count: 2), out _));
        Assert.True(Read(Build(w => w.WriteByte(9)), out ProgressMessage? ending)); // one is fine
        Assert.IsType<RunEndingEntry>(Assert.Single(ending!.Batch.Entries));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(6); // quest step out of range
            w.WriteByte(42);
            w.WriteVarUInt(0);
        }), out _));

        // A deflate bomb: claims a small entry, inflates to far more.
        byte[] bomb = Compression.Deflate(new byte[200_000]);
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(5);
            w.WriteVarUInt(100);
            w.WriteVarUInt((ulong)bomb.Length);
            w.WriteBytes(bomb);
        }), out _));
        Assert.False(Read(Build(w =>
        {
            w.WriteByte(5);
            w.WriteVarUInt(ProgressLimits.MaxRunHistoryBytes + 1);
            w.WriteVarUInt((ulong)bomb.Length);
            w.WriteBytes(bomb);
        }), out _));
    }

    /// <summary>A message whose entries are run history entries, each <paramref name="raw"/> deflated (claiming its length).</summary>
    private static byte[] HistoryMessage(byte[] raw, int entries)
    {
        byte[] deflated = Compression.Deflate(raw);
        var writer = new BitWriter();
        writer.WriteByte(ProgressBatchMessage.Format);
        writer.WriteUInt64(Session);
        writer.WriteUInt32(1);
        writer.WriteUInt32(1); // sequence
        writer.WriteUInt32(0); // checkpoint
        writer.WriteShortString("run-1");
        writer.WriteVarUInt((ulong)entries);
        for (int i = 0; i < entries; i++)
        {
            writer.WriteByte(5);
            writer.WriteVarUInt((ulong)raw.Length);
            writer.WriteVarUInt((ulong)deflated.Length);
            writer.WriteBytes(deflated);
        }

        return writer.ToArray();
    }

    [Fact]
    public void A_run_history_entry_padded_with_zeros_is_refused()
    {
        // Regression case (a compression bomb): 256 KB of zeros deflates to under 300 bytes and used to read as a valid empty entry.
        byte[] entry = RunHistoryDataTests.Encoded(RunHistoryDataTests.Sample());
        Assert.True(Read(HistoryMessage(entry, 1), out ProgressMessage? message)); // the entry itself reads
        Assert.IsType<RunHistoryRecord>(Assert.Single(message!.Batch.Entries));

        byte[] padded = new byte[entry.Length + 16];
        entry.CopyTo(padded, 0);
        Assert.False(Read(HistoryMessage(padded, 1), out _));
        Assert.False(Read(HistoryMessage(new byte[ProgressLimits.MaxRunHistoryBytes], 1), out _));
    }

    [Fact]
    public void A_message_never_inflates_more_than_one_run_history_entrys_worth()
    {
        // About 1,800 of the largest entry fit in one message deflated; together they'd inflate to hundreds of MB.
        byte[] large = RunHistoryDataTests.Encoded(RunHistoryDataTests.Noisy(ProgressLimits.MaxRunHistoryBytes * 3 / 5, 3));
        Assert.True(large.Length * 2 > ProgressLimits.MaxRunHistoryBytes && large.Length <= ProgressLimits.MaxRunHistoryBytes);
        Assert.True(Compression.Deflate(large).Length * 2 < ProgressBatchMessage.MaxMessageBytes); // two fit in one message
        Assert.True(Read(HistoryMessage(large, 1), out _));
        Assert.False(Read(HistoryMessage(large, 2), out _)); // past the message's inflate budget
        Assert.False(Read(HistoryMessage(new byte[1024], ProgressBatchMessage.MaxEntries), out _));

        // Small real entries still read several to a message (the friend saves only one per batch).
        byte[] small = RunHistoryDataTests.Encoded(RunHistoryDataTests.Sample());
        Assert.True(Read(HistoryMessage(small, 3), out ProgressMessage? message));
        Assert.Equal(3, message!.Batch.Entries.Count);
    }

    [Fact]
    public void A_batch_too_big_for_one_message_is_not_encoded()
    {
        // Each entry is within its own limit, and hard to compress: three of them don't fit in one message.
        var entries = Enumerable.Range(0, 3).Select(i => (ProgressEntry)new RunHistoryRecord(RunHistoryDataTests.Noisy(240_000, i))).ToArray();
        Assert.All(entries, e => Assert.True(ProgressLimits.IsValid(e)));
        Assert.Null(ProgressBatchMessage.EncodeBody(new ProgressBatch(1, 0, string.Empty, entries)));
        Assert.NotNull(ProgressBatchMessage.EncodeBody(new ProgressBatch(1, 0, string.Empty, entries[..1])));
    }

    [Fact]
    public void A_full_batch_of_stat_operations_fits_in_one_message()
    {
        var entries = Enumerable.Range(0, ProgressBatchMessage.MaxEntries)
            .Select(i => (ProgressEntry)new StatEntry(i % 190, StatOp.TrySetLargerCampaign, StatValue.Whole(ProgressLimits.MaxStatValue - i)))
            .ToList();
        byte[]? body = ProgressBatchMessage.EncodeBody(new ProgressBatch(1, 0, string.Empty, entries));
        Assert.NotNull(body);
        Assert.True(body!.Length < ProgressBatchMessage.MaxMessageBytes);
    }

    [Fact]
    public void A_batch_never_gives_one_stat_more_than_its_cap()
    {
        // Each entry is within its own limit; together they give stat 9 one more than a batch may.
        ProgressEntry[] tooMuch =
        {
            new StatEntry(9, StatOp.IncrementCampaignBy, StatValue.Whole(ProgressLimits.MaxStatValue)),
            new StatEntry(9, StatOp.Increment, StatValue.None),
        };
        Assert.All(tooMuch, e => Assert.True(ProgressLimits.IsValid(e)));
        Assert.False(ProgressLimits.IsWithinBatchLimits(tooMuch));
        Assert.Throws<ArgumentException>(() => new ProgressBatch(1, 0, string.Empty, tooMuch));

        // Records keep the best value instead of adding, and other stats have their own cap.
        ProgressEntry[] fine =
        {
            new StatEntry(9, StatOp.IncrementCampaignBy, StatValue.Whole(ProgressLimits.MaxStatValue)),
            new StatEntry(9, StatOp.TrySetLargerCampaign, StatValue.Whole(ProgressLimits.MaxStatValue)),
            new StatEntry(10, StatOp.IncrementBy, StatValue.Real(ProgressLimits.MaxStatValue)),
        };
        Assert.True(ProgressLimits.IsWithinBatchLimits(fine));
        Assert.True(Read(Message(new ProgressBatch(1, 0, string.Empty, fine)), out _));

        // A host that sends it anyway: the message reads as false.
        var writer = new BitWriter();
        writer.WriteByte(ProgressBatchMessage.Format);
        writer.WriteUInt64(Session);
        writer.WriteUInt32(1);
        writer.WriteUInt32(1); // sequence
        writer.WriteUInt32(0); // checkpoint
        writer.WriteShortString(string.Empty);
        writer.WriteVarUInt(3);
        for (int i = 0; i < 3; i++)
        {
            writer.WriteByte(1); // stat 9, campaign + the most one operation may carry
            writer.WriteVarUInt(9);
            writer.WriteByte((byte)StatOp.IncrementCampaignBy);
            writer.WriteByte((byte)StatValueKind.Whole);
            writer.WriteVarInt(ProgressLimits.MaxStatValue);
        }

        Assert.False(Read(writer.ToArray(), out _));
    }

    [Fact]
    public void The_description_counts_each_kind()
    {
        string text = new ProgressBatch(1, 0, string.Empty, EveryKind()).Describe();
        Assert.Equal("6 stat operations, 2 unlock checks (2 live-match unlocks earned), squad win gauntlet on difficulty 3, " +
            "squad loss generalmanager on difficulty 0, squad win gauntlet on difficulty 3, squad loss gauntlet on difficulty 3, comics seen, " +
            "the run history entry, 3 bench quest steps, the run's end for the benchwarmers, 3 profile marks", text);
        Assert.Equal("0 stat operations, 0 unlock checks", new ProgressBatch(1, 0, string.Empty, Array.Empty<ProgressEntry>()).Describe());
    }

    [Fact]
    public void Stat_operations_say_what_they_are()
    {
        Assert.All(Enum.GetValues<StatOp>().Where(op => op != StatOp.None), op => Assert.True(StatOps.IsKnown(op)));
        Assert.False(StatOps.IsKnown(StatOp.None));
        Assert.False(StatOps.HasValue(StatOp.IncrementCampaign));
        Assert.True(StatOps.HasValue(StatOp.IncrementCampaignBy));
        Assert.True(StatOps.IsRecord(StatOp.TrySetSmallerPlayNow));
        Assert.False(StatOps.IsRecord(StatOp.IncrementBy));
        Assert.Equal(
            new[]
            {
                StatOp.IncrementCampaign, StatOp.IncrementCampaignBy, StatOp.TrySetLargerCampaign, StatOp.TrySetSmallerCampaign,
                StatOp.ResetCampaign, StatOp.TrySetLargerCampaignFromStat,
            },
            Enum.GetValues<StatOp>().Where(StatOps.IsCampaign));
        Assert.Equal(new[] { StatOp.ResetCampaign, StatOp.ResetPlayNow }, Enum.GetValues<StatOp>().Where(StatOps.IsReset));
        Assert.All(Enum.GetValues<StatOp>().Where(StatOps.IsFromStat), op => Assert.True(StatOps.IsRecord(op) && StatOps.HasValue(op)));
        Assert.False(StatOps.HasValue(StatOp.ResetPlayNow));
        Assert.Equal(StatBucket.PlayNow, StatOps.BucketOf(StatOp.TrySetLargerPlayNowFromStat));
        Assert.Equal(StatBucket.Lifetime, StatOps.BucketOf(StatOp.TrySetLargerFromStat));
    }

    [Fact]
    public void The_win_streak_and_record_holders_round_trip_as_what_they_mean()
    {
        var entries = new List<ProgressEntry>
        {
            new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.Increment, StatValue.None),
            new StatEntry(ProgressGameStats.LargestWinStreak, StatOp.TrySetLargerCampaignFromStat, StatValue.Whole(ProgressGameStats.CurrentWinStreak)),
            new StatEntry(ProgressGameStats.LargestWinStreak, StatOp.TrySetLargerFromStat, StatValue.Whole(ProgressGameStats.CurrentWinStreak)),
            new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.ResetCampaign, StatValue.None),
            new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.ResetPlayNow, StatValue.None),
            new StatEntry(ProgressGameStats.MostGoalsInOneGame, StatOp.TrySetLarger, StatValue.Whole(7)),
            new StatTextEntry(ProgressGameStats.TeamWithMostGoalsInOneGame, ProgressGameStats.MostGoalsInOneGame, "Les Orignaux de Québec", true),
            new StatTextEntry(ProgressGameStats.TeamWithMostHitsInOneGame, ProgressGameStats.MostHitsInOneGame, "Moose", false),
            EntityStatsTests.Skater(),
        };
        var batch = new ProgressBatch(4, 2, "run-1", entries);
        Assert.True(Read(Message(batch), out ProgressMessage? message));
        for (int i = 0; i < entries.Count - 1; i++)
        {
            Assert.Equal(entries[i], message!.Batch.Entries[i]);
        }

        Assert.Equal(EntityStatsTests.Skater().Values, Assert.IsType<EntityStatEntry>(message!.Batch.Entries[^1]).Values);
        Assert.Equal("6 stat operations, 0 unlock checks, 2 record holders, 1 entity stat", batch.Describe());
        Assert.Equal(new double[] { 1, 0, 0, 0, 0, 0 }, entries.OfType<StatEntry>().Select(ProgressLimits.CreditOf)); // resets and records add nothing
    }

    [Fact]
    public void A_reset_or_a_record_from_another_stat_is_only_the_games_own_and_a_holders_name_only_its_records()
    {
        // A host may only do what the game does: reset the current streak, raise the largest from the current one.
        foreach (ProgressEntry bad in new ProgressEntry[]
        {
            new StatEntry(1, StatOp.ResetCampaign, StatValue.None), // GamesWon wiped
            new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.ResetCampaign, StatValue.Whole(0)),
            new StatEntry(9, StatOp.TrySetLargerFromStat, StatValue.Whole(ProgressGameStats.CurrentWinStreak)),
            new StatEntry(ProgressGameStats.LargestWinStreak, StatOp.TrySetLargerFromStat, StatValue.Whole(9)),
            new StatEntry(ProgressGameStats.LargestWinStreak, StatOp.TrySetLargerFromStat, StatValue.Real(19)),
            new StatTextEntry(ProgressGameStats.TeamWithMostHitsInOneGame, ProgressGameStats.MostGoalsInOneGame, "Moose", true),
            new StatTextEntry(9, 8, "Moose", true),
            new StatTextEntry(ProgressGameStats.TeamWithMostGoalsInOneGame, ProgressGameStats.MostGoalsInOneGame, string.Empty, true),
            new StatTextEntry(ProgressGameStats.TeamWithMostGoalsInOneGame, ProgressGameStats.MostGoalsInOneGame, "line\nbreak", true),
            new StatTextEntry(ProgressGameStats.TeamWithMostGoalsInOneGame, ProgressGameStats.MostGoalsInOneGame, new string('m', ProgressLimits.MaxTextBytes + 1), true),
        })
        {
            Assert.False(ProgressLimits.IsValid(bad), bad.ToString());
            Assert.Throws<ArgumentException>(() => new ProgressBatch(1, 0, "run-1", new[] { bad }));
        }

        // Hand-built on the wire: a reset of another stat is refused as the batch is read.
        var writer = new BitWriter();
        writer.WriteUInt32(1);
        writer.WriteUInt32(0);
        writer.WriteShortString("run-1", ProgressLimits.MaxIdBytes);
        writer.WriteVarUInt(1);
        writer.WriteByte(1); // a stat operation
        writer.WriteVarUInt(9);
        writer.WriteByte((byte)StatOp.ResetCampaign);
        var reader = new BitReader(writer.AsSpan());
        Assert.False(ProgressBatchMessage.TryReadBody(ref reader, out _));
    }
}
