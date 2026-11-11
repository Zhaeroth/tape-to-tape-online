using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class ComponentAlignmentTests
{
    private static readonly uint Transform = ComponentAlignment.TypeHash("UltimateReplay.ReplayTransform");
    private static readonly uint Forward = ComponentAlignment.TypeHash("Skaters.ReplayForward");
    private static readonly uint Spine = ComponentAlignment.TypeHash("Skaters.SpineAnimationReplay");
    private static readonly uint Vfx = ComponentAlignment.TypeHash("Skaters.ForwardVFXReplay");
    private static readonly uint Stick = ComponentAlignment.TypeHash("RecordableStick");
    private static readonly uint Talent = ComponentAlignment.TypeHash("UltimateReplay.ReplayParticleSystem");

    [Fact]
    public void A_skater_with_a_component_the_host_added_at_run_time_still_takes_the_ids_of_the_rest()
    {
        // The host's game gave its skater one more component (a talent's effect, granted by a relic whose pick is random
        // on each PC); this PC's copy has the five both games make. By position nothing could be driven; by type, all five.
        ushort[] hostIds = { 10, 11, 12, 13, 14, 15 };
        uint[] hostTypes = { Transform, Forward, Spine, Talent, Vfx, Stick };
        uint[] localTypes = { Transform, Forward, Spine, Vfx, Stick };

        ComponentMatch match = ComponentAlignment.Align(hostIds, hostTypes, localTypes.Length, localTypes);

        Assert.True(match.ByType);
        Assert.Equal(new ushort?[] { 10, 11, 12, 14, 15 }, match.HostIdFor);
        Assert.Equal(5, match.Matched);
        Assert.Equal(1, match.HostOnly);
        Assert.Equal(0, match.LocalOnly);
        Assert.False(match.Whole);
    }

    [Fact]
    public void A_component_only_this_pc_has_keeps_no_host_id()
    {
        ushort[] hostIds = { 10, 11, 12 };
        uint[] hostTypes = { Transform, Forward, Spine };
        uint[] localTypes = { Transform, Talent, Forward, Spine };

        ComponentMatch match = ComponentAlignment.Align(hostIds, hostTypes, localTypes.Length, localTypes);

        Assert.Equal(new ushort?[] { 10, null, 11, 12 }, match.HostIdFor);
        Assert.Equal(1, match.LocalOnly);
        Assert.Equal(0, match.HostOnly);
    }

    [Fact]
    public void Several_components_of_one_type_keep_their_order()
    {
        // Each child's transform: the first host one goes to the first local one, and so on.
        ushort[] hostIds = { 1, 2, 3, 4 };
        uint[] hostTypes = { Transform, Transform, Forward, Transform };
        uint[] localTypes = { Transform, Forward, Transform, Transform };

        ComponentMatch match = ComponentAlignment.Align(hostIds, hostTypes, localTypes.Length, localTypes);

        Assert.Equal(new ushort?[] { 1, 3, 2, 4 }, match.HostIdFor);
        Assert.True(match.Whole);
    }

    [Fact]
    public void The_same_count_with_another_type_in_one_place_leaves_that_one_alone()
    {
        // Both games added one component, of different types: by position the last one would take the other type's state.
        ushort[] hostIds = { 1, 2, 3 };
        uint[] hostTypes = { Transform, Forward, Talent };
        uint[] localTypes = { Transform, Forward, Stick };

        ComponentMatch match = ComponentAlignment.Align(hostIds, hostTypes, localTypes.Length, localTypes);

        Assert.Equal(new ushort?[] { 1, 2, null }, match.HostIdFor);
        Assert.Equal(1, match.HostOnly);
        Assert.Equal(1, match.LocalOnly);
    }

    [Fact]
    public void Without_types_the_counts_must_agree_as_before()
    {
        ushort[] hostIds = { 7, 8, 9 };

        ComponentMatch same = ComponentAlignment.Align(hostIds, null, 3, null);
        ComponentMatch fewer = ComponentAlignment.Align(hostIds, null, 2, null);
        ComponentMatch typesHereOnly = ComponentAlignment.Align(hostIds, null, 3, new[] { Transform, Forward, Spine });

        Assert.False(same.ByType);
        Assert.Equal(new ushort?[] { 7, 8, 9 }, same.HostIdFor);
        Assert.True(same.Whole);
        Assert.Equal(0, fewer.Matched);
        Assert.All(fewer.HostIdFor, id => Assert.Null(id));
        Assert.Equal(new ushort?[] { 7, 8, 9 }, typesHereOnly.HostIdFor);
    }

    [Fact]
    public void Types_that_dont_fit_their_ids_are_treated_as_absent()
    {
        ComponentMatch match = ComponentAlignment.Align(new ushort[] { 1, 2 }, new[] { Transform }, 2, new[] { Transform, Forward });

        Assert.False(match.ByType);
        Assert.Equal(new ushort?[] { 1, 2 }, match.HostIdFor);
    }

    [Fact]
    public void A_free_id_is_never_one_of_the_hosts()
    {
        ushort[] hostIds = { 5, 65535, 65534 };

        Assert.Equal(9, ComponentAlignment.FreeId(9, hostIds));
        ushort free = ComponentAlignment.FreeId(5, hostIds);
        Assert.DoesNotContain(free, hostIds);
        Assert.Equal(65533, free);
    }

    [Fact]
    public void Type_hashes_are_stable_and_tell_classes_apart()
    {
        Assert.Equal(ComponentAlignment.TypeHash("Skaters.ReplayForward"), Forward);
        Assert.NotEqual(Forward, Spine);
        Assert.NotEqual(ComponentAlignment.TypeHash("A.B"), ComponentAlignment.TypeHash("AB"));
        Assert.Throws<ArgumentNullException>(() => ComponentAlignment.TypeHash(null!));
    }

    [Fact]
    public void An_object_tables_component_types_round_trip_after_its_entries()
    {
        var entries = new[]
        {
            new ObjectEntry(1, -5, "F:H:x", "Skater", new ushort[] { 1, 2, 300 }, new[] { Transform, Forward, Talent }),
            new ObjectEntry(2, 7397, string.Empty, "Puck"),
        };
        var writer = new BitWriter();
        ObjectTableMessage.Write(writer, entries);
        var reader = new BitReader(writer.AsSpan());
        var read = new List<ObjectEntry>();

        Assert.True(ObjectTableMessage.TryRead(ref reader, read));
        Assert.Equal(2, read.Count);
        Assert.Equal(new[] { Transform, Forward, Talent }, read[0].ComponentTypes);
        Assert.Equal(new ushort[] { 1, 2, 300 }, read[0].Components);
        Assert.Null(read[1].ComponentTypes);
    }

    [Fact]
    public void A_table_without_types_is_written_as_before()
    {
        // Entries with no types write no section: the bytes an older build reads, unchanged.
        var plain = new BitWriter();
        ObjectTableMessage.Write(plain, new[] { new ObjectEntry(1, 2, "puck", "PuckBody", new ushort[] { 4 }) });
        var typed = new BitWriter();
        ObjectTableMessage.Write(typed, new[] { new ObjectEntry(1, 2, "puck", "PuckBody", new ushort[] { 4 }, new[] { Transform }) });

        byte[] plainBytes = plain.ToArray();
        byte[] typedBytes = typed.ToArray();
        Assert.Equal(plainBytes, typedBytes.Take(plainBytes.Length).ToArray());
        Assert.Equal(plainBytes.Length + 5, typedBytes.Length); // the count and one 32-bit type
    }

    [Fact]
    public void A_cut_or_wrong_types_section_is_refused()
    {
        var writer = new BitWriter();
        ObjectTableMessage.Write(writer, new[] { new ObjectEntry(1, 2, "puck", "PuckBody", new ushort[] { 4, 5 }, new[] { Transform, Forward }) });
        byte[] full = writer.ToArray();
        var plainWriter = new BitWriter();
        ObjectTableMessage.Write(plainWriter, new[] { new ObjectEntry(1, 2, "puck", "PuckBody", new ushort[] { 4, 5 }) });
        int entriesEnd = plainWriter.ByteLength;

        for (int length = entriesEnd + 1; length < full.Length; length++)
        {
            var reader = new BitReader(full.AsSpan(0, length));
            Assert.False(ObjectTableMessage.TryRead(ref reader, new List<ObjectEntry>()), $"{length} bytes");
        }

        // A type count that is neither 0 nor the entry's component count.
        byte[] wrong = full.ToArray();
        wrong[entriesEnd] = 1;
        var wrongReader = new BitReader(wrong.AsSpan(0, entriesEnd + 5));
        Assert.False(ObjectTableMessage.TryRead(ref wrongReader, new List<ObjectEntry>()));
    }

    [Fact]
    public void Two_entries_name_the_same_components_only_with_the_same_ids_and_types_in_order()
    {
        var first = new ObjectEntry(4, 9, "F:H:x", "Skater", new ushort[] { 1, 2 }, new[] { Transform, Forward });

        Assert.True(ComponentAlignment.SameComponents(first, first with { Name = "Another name" }));
        Assert.False(ComponentAlignment.SameComponents(first, first with { Components = new ushort[] { 1, 2, 3 }, ComponentTypes = new[] { Transform, Forward, Talent } }));
        Assert.False(ComponentAlignment.SameComponents(first, first with { Components = new ushort[] { 2, 1 } }));
        Assert.False(ComponentAlignment.SameComponents(first, first with { ComponentTypes = new[] { Forward, Transform } }));
        Assert.False(ComponentAlignment.SameComponents(first, first with { ComponentTypes = null }));
        Assert.True(ComponentAlignment.SameComponents(new ObjectEntry(1, 2, "k", "n"), new ObjectEntry(1, 2, "k", "n", Array.Empty<ushort>(), Array.Empty<uint>())));
    }

    [Fact]
    public void A_lists_difference_says_what_it_gained_and_lost_as_often_as_it_did()
    {
        // The frog's talent added a particle component; a second transform came with it; the old talent's effect left.
        (List<uint> added, List<uint> removed) = ComponentAlignment.Difference(
            new[] { Transform, Forward, Vfx },
            new[] { Transform, Forward, Talent, Transform });

        Assert.Equal(new[] { Talent, Transform }.OrderBy(t => t), added.OrderBy(t => t));
        Assert.Equal(new[] { Vfx }, removed);

        (List<uint> none, List<uint> alsoNone) = ComponentAlignment.Difference(new[] { Transform, Forward }, new[] { Forward, Transform });
        Assert.Empty(none);
        Assert.Empty(alsoNone);
        Assert.Equal(new[] { Spine }, ComponentAlignment.Difference(null, new[] { Spine }).Added);
    }

    [Fact]
    public void Types_that_dont_fit_their_ids_arent_written()
    {
        var writer = new BitWriter();
        Assert.Throws<ArgumentException>(() => ObjectTableMessage.Write(writer, new[] { new ObjectEntry(1, 2, "k", "n", new ushort[] { 1, 2 }, new[] { Transform }) }));
    }
}
