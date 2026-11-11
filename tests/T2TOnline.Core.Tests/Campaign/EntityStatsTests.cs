using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class EntityStatsTests
{
    private const ulong Session = 0x1234_5678_9abc_def0;

    /// <summary>A skater's numbers from one match, every field set.</summary>
    internal static EntityStatEntry Skater(string id = "fwd-kovalski", int goals = 2) => new(true, false, EntityStatGroup.Skater, id,
        EntityStatSchema.Fields(EntityStatGroup.Skater).Select((field, i) => field.Kind switch
        {
            EntityFieldKind.Text => EntityStatValue.OfText(field.Name == "faceSkin" ? null : $"{field.Name}-x"),
            EntityFieldKind.WholeList => EntityStatValue.OfList(new[] { 0, 1, 0, 0, 0 }),
            EntityFieldKind.Real => EntityStatValue.OfReal(1.5f),
            _ => EntityStatValue.OfWhole(field.Name == "goals" ? goals : i % 3),
        }).ToArray());

    private static EntityStatEntry Team(float timeOnAttack) => new(true, true, EntityStatGroup.Team, "officials",
        EntityStatSchema.Fields(EntityStatGroup.Team).Select(field => field.Kind switch
        {
            EntityFieldKind.Text => EntityStatValue.OfText("x"),
            EntityFieldKind.Real => EntityStatValue.OfReal(timeOnAttack),
            _ => EntityStatValue.OfWhole(1),
        }).ToArray());

    private static EntityStatEntry RoundTrip(EntityStatEntry entry)
    {
        var writer = new BitWriter();
        entry.Write(writer);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(EntityStatEntry.TryRead(ref reader, out EntityStatEntry? read));
        Assert.Equal(0, reader.RemainingBits / 8);
        return read!;
    }

    [Fact]
    public void Every_group_has_the_games_fields_once_each()
    {
        foreach (EntityStatGroup group in Enum.GetValues<EntityStatGroup>())
        {
            IReadOnlyList<EntityField> fields = EntityStatSchema.Fields(group);
            Assert.NotEmpty(fields);
            Assert.Equal(fields.Count, fields.Select(f => f.Name).Distinct().Count());
            Assert.Equal(EntityFieldKind.Text, fields[0].Kind); // the id comes first
            Assert.False(fields[0].Credit);

            // The ledger keeps a group's budget per field under 64 keys a group: a field past them would never be counted.
            Assert.True(fields.Count <= 64, $"{group} has {fields.Count} fields");
        }

        // The fields the game's Combine doesn't add: names, the shirt number, the streak it works out from its own.
        Assert.Equal(new[] { "skaterId", "skaterName", "number", "faceSkin", "consecutiveGamesWithoutReceivingInjury" },
            EntityStatSchema.Fields(EntityStatGroup.Skater).Where(f => !f.Credit).Select(f => f.Name));
        Assert.Equal(43, EntityStatSchema.Fields(EntityStatGroup.Skater).Count);
        Assert.Equal(15, EntityStatSchema.Fields(EntityStatGroup.Goalie).Count);
        Assert.Equal(35, EntityStatSchema.Fields(EntityStatGroup.Team).Count);
        Assert.Empty(EntityStatSchema.Fields((EntityStatGroup)6));
    }

    [Fact]
    public void An_entity_stat_round_trips_field_by_field()
    {
        foreach (EntityStatEntry entry in new[] { Skater(), Team(12.25f) })
        {
            EntityStatEntry read = RoundTrip(entry);
            Assert.Equal((entry.Campaign, entry.Cpu, entry.Group, entry.EntityId), (read.Campaign, read.Cpu, read.Group, read.EntityId));
            Assert.Equal(entry.Values, read.Values);
        }

        Assert.Null(RoundTrip(Skater()).Field("faceSkin").Text); // unset stays unset
        Assert.Equal(2, RoundTrip(Skater()).Field("goals").Whole);
    }

    [Fact]
    public void Entries_out_of_their_schema_or_limits_are_refused()
    {
        EntityStatEntry good = Skater();
        Assert.True(good.IsWithinLimits());
        EntityStatValue[] values = good.Values.ToArray();

        Assert.False((good with { Values = values.Take(values.Length - 1).ToArray() }).IsWithinLimits()); // a field short
        Assert.False((good with { Group = (EntityStatGroup)9 }).IsWithinLimits());
        Assert.False((good with { EntityId = string.Empty }).IsWithinLimits());
        Assert.False((good with { EntityId = "a\nb" }).IsWithinLimits());
        Assert.True((good with { EntityId = "Kovalski Jr." }).IsWithinLimits()); // ids may hold spaces

        foreach ((int field, EntityStatValue bad) in new[]
        {
            (8, EntityStatValue.OfWhole(-1)), // a count below zero
            (8, EntityStatValue.OfWhole(ProgressLimits.MaxStatValue + 1)),
            (8, EntityStatValue.OfText("2")), // the wrong kind
            (5, EntityStatValue.OfList(Enumerable.Repeat(1, EntityStatSchema.MaxListLength + 1).ToArray())),
            (5, EntityStatValue.OfList(new[] { 1, -1 })),
            (1, EntityStatValue.OfText(new string('n', ProgressLimits.MaxTextBytes + 1))),
            (1, EntityStatValue.OfText("tab\there")),
        })
        {
            EntityStatValue[] changed = values.ToArray();
            changed[field] = bad;
            Assert.False((good with { Values = changed }).IsWithinLimits());
        }

        Assert.False(Team(float.NaN).IsWithinLimits());
        Assert.False(Team(-1f).IsWithinLimits());
    }

    [Fact]
    public void A_damaged_entry_reads_false_and_never_throws()
    {
        var writer = new BitWriter();
        Skater().Write(writer);
        byte[] bytes = writer.ToArray();
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            byte[] copy = bytes.ToArray();
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            var reader = new BitReader(copy.AsSpan(0, random.Next(copy.Length + 1)));
            if (EntityStatEntry.TryRead(ref reader, out EntityStatEntry? read))
            {
                Assert.True(read!.IsWithinLimits());
            }
        }
    }

    [Fact]
    public void A_hosting_session_can_add_only_so_much_to_an_entity()
    {
        var credit = new EntityCredit();
        EntityStatEntry big = Skater(goals: ProgressLimits.MaxStatValue);
        Assert.True(credit.TryGive(Session, big));
        Assert.False(credit.TryGive(Session, Skater(goals: 1))); // goals would pass the session's cap
        Assert.True(credit.TryGive(Session, Skater("fwd-other", goals: 1))); // another skater has its own
        Assert.True(credit.TryGive(Session, Skater(goals: 0) with { Cpu = true })); // and the computer's set its own
        Assert.Equal(1, credit.Refused);
        Assert.True(credit.TryGive(Session + 1, Skater(goals: 1))); // a new hosting session starts from nothing
    }

    [Fact]
    public void A_hosting_session_can_touch_only_so_many_entities()
    {
        var credit = new EntityCredit();
        for (int i = 0; i < EntityCredit.MaxEntitiesPerSession; i++)
        {
            Assert.True(credit.TryGive(Session, Skater($"fwd-{i}", 0)));
        }

        Assert.False(credit.TryGive(Session, Skater("fwd-one-more", 0)));
        Assert.True(credit.TryGive(Session, Skater("fwd-3", 1))); // one already touched still counts
    }
}
