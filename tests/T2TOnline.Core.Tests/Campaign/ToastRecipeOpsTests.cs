using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The four text ops the toasts' and formatted prompts' recipes add (v1): a team's display name, a relic's,
/// talent's or ability's name by id and level, an injury's name and a stat's keyword-tagged name. Made by
/// <see cref="IToastTextFunctions"/>; a PC whose functions lack it makes none of them.
/// </summary>
public class ToastRecipeOpsTests
{
    private static TextRecipe? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = TextRecipe.TryRead(ref reader, out TextRecipe? recipe);
        Assert.Equal(ok, recipe != null);
        return ok && reader.RemainingBits < 8 ? recipe : null;
    }

    private static byte[] Bytes(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    [Fact]
    public void Every_toast_op_round_trips_field_by_field()
    {
        foreach (TextRecipe sent in TextRecipeTests.EveryToastOp())
        {
            Assert.Null(sent.Check());
            TextRecipe read = Read(TextRecipeTests.Encoded(sent))!;
            Assert.NotNull(read);
            Assert.Equal(TextRecipeTests.Encoded(sent), TextRecipeTests.Encoded(read));
            Assert.Equal((sent.Op, sent.Text, sent.AssetKind, sent.Number, sent.Args.Count), (read.Op, read.Text, read.AssetKind, read.Number, read.Args.Count));
            Assert.Equal(sent.ToString(), read.ToString());
            Assert.Equal(sent.Evaluate(new FakeTexts("de")), read.Evaluate(new FakeTexts("de")));
            Assert.Empty(read.Targets());
        }
    }

    [Fact]
    public void Each_op_calls_the_games_function_it_names_with_its_values()
    {
        var de = new FakeTexts("de");
        Assert.Equal("de:team:Run", TextRecipe.TeamName(ToastTeam.Run).Evaluate(de));
        Assert.Equal("de:team:Home", TextRecipe.TeamName(ToastTeam.Home).Evaluate(de));
        Assert.Equal("de:team:Visitor", TextRecipe.TeamName(ToastTeam.Visitor).Evaluate(de));
        Assert.Equal("de:asset:Relic:hot_cake:1", TextRecipe.AssetName(ToastObjectKind.Relic, "hot_cake", 1).Evaluate(de));
        Assert.Equal("de:asset:Talent:speedy:99", TextRecipe.AssetName(ToastObjectKind.Talent, "speedy", 99).Evaluate(de));
        Assert.Equal("de:asset:Ability:slapshot:0", TextRecipe.AssetName(ToastObjectKind.Ability, "slapshot", 0).Evaluate(de));
        Assert.Equal("de:injury:2", TextRecipe.InjuryName(2).Evaluate(de));
        Assert.Equal("de:keystat:4", TextRecipe.LocalizedStatName(4).Evaluate(de));

        // Inside the formats the toasts use: the stat line, the injury toast's description, markup around a relic.
        Assert.Equal("<	+1 de:keystat:3|1|de:keystat:3>",
            TextRecipe.Format(TextRecipe.Literal("\t+{0} {1}"), TextRecipe.Literal("1"), TextRecipe.LocalizedStatName(3)).Evaluate(de));
        Assert.Equal("<de:term:v2ui/injury_toast/desc|de:injury:2|2>",
            TextRecipe.Format(TextRecipe.Term("v2ui/injury_toast/desc"), TextRecipe.InjuryName(2), TextRecipe.Literal("2")).Evaluate(de));
        Assert.Equal("<color=green>de:asset:Relic:x:2</color>",
            TextRecipe.Concat(TextRecipe.Literal("<color=green>"), TextRecipe.AssetName(ToastObjectKind.Relic, "x", 2), TextRecipe.Literal("</color>")).Evaluate(de));
    }

    [Fact]
    public void A_pc_whose_functions_lack_the_toast_ops_makes_none_of_them_and_still_makes_the_rest()
    {
        ITextFunctions plain = new PlainTexts(new FakeTexts("de"));
        Assert.Null(TextRecipe.TeamName(ToastTeam.Run).Evaluate(plain));
        Assert.Null(TextRecipe.AssetName(ToastObjectKind.Relic, "x", 1).Evaluate(plain));
        Assert.Null(TextRecipe.InjuryName(1).Evaluate(plain));
        Assert.Null(TextRecipe.LocalizedStatName(1).Evaluate(plain));
        Assert.Null(TextRecipe.Format(TextRecipe.Term("f"), TextRecipe.InjuryName(1)).Evaluate(plain));
        Assert.Equal("de:term:f", TextRecipe.Term("f").Evaluate(plain));
        Assert.Equal("de:stat:1:False", TextRecipe.StatName(1, false).Evaluate(plain));
    }

    [Fact]
    public void A_function_that_gives_nothing_or_throws_makes_no_text()
    {
        var de = new FakeTexts("de");
        de.MissingTeams.Add(ToastTeam.Visitor);
        de.MissingAssets.Add("gone");
        Assert.Null(TextRecipe.TeamName(ToastTeam.Visitor).Evaluate(de));
        Assert.Null(TextRecipe.AssetName(ToastObjectKind.Relic, "gone", 1).Evaluate(de));
        Assert.Null(TextRecipe.LocalizedStatName(EventTextRecipes.ForwardStats).Evaluate(de));

        // Never half a text: one missing name makes the whole format nothing.
        Assert.Null(TextRecipe.Format(TextRecipe.Term("f"), TextRecipe.Literal("a"), TextRecipe.AssetName(ToastObjectKind.Relic, "gone", 1)).Evaluate(de));
        Assert.Null(TextRecipe.Concat(TextRecipe.Literal("<b>"), TextRecipe.TeamName(ToastTeam.Visitor)).Evaluate(de));

        var throwing = new FakeTexts("de") { Throws = true };
        foreach (TextRecipe recipe in TextRecipeTests.EveryToastOp())
        {
            Assert.Null(recipe.Evaluate(throwing));
        }
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        yield return new object[] { "no team", TextRecipe.TeamName(ToastTeam.None) };
        yield return new object[] { "team 4", TextRecipe.TeamName((ToastTeam)4) };
        yield return new object[] { "an asset of no kind", TextRecipe.AssetName(ToastObjectKind.None, "x", 1) };
        yield return new object[] { "an asset of kind 4", TextRecipe.AssetName((ToastObjectKind)4, "x", 1) };
        yield return new object[] { "an asset with no id", TextRecipe.AssetName(ToastObjectKind.Relic, string.Empty, 1) };
        yield return new object[] { "an asset id of 129 bytes", TextRecipe.AssetName(ToastObjectKind.Relic, new string('i', EventChoiceSet.MaxNameBytes + 1), 1) };
        yield return new object[] { "an asset id with a line break", TextRecipe.AssetName(ToastObjectKind.Relic, "a\nb", 1) };
        yield return new object[] { "a negative asset level", TextRecipe.AssetName(ToastObjectKind.Talent, "x", -1) };
        yield return new object[] { "an asset level of 100", TextRecipe.AssetName(ToastObjectKind.Talent, "x", TextRecipe.MaxAssetLevel + 1) };
        yield return new object[] { "a negative injury", TextRecipe.InjuryName(-1) };
        yield return new object[] { "injury 64", TextRecipe.InjuryName(TextRecipe.MaxInjury + 1) };
        yield return new object[] { "a negative keyword stat", TextRecipe.LocalizedStatName(-1) };
        yield return new object[] { "keyword stat 64", TextRecipe.LocalizedStatName(TextRecipe.MaxStat + 1) };
        yield return new object[] { "a format around a bad name", TextRecipe.Format(TextRecipe.Literal("{0}"), TextRecipe.TeamName(ToastTeam.None)) };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Recipes_out_of_their_limits_are_refused_and_make_nothing(string what, TextRecipe recipe)
    {
        Assert.NotNull(recipe.Check());
        Assert.ThrowsAny<ArgumentException>(() => TextRecipeTests.Encoded(recipe));
        Assert.Null(recipe.Evaluate(new FakeTexts("de")));
        Assert.False(string.IsNullOrEmpty(recipe.ToString()), what);
    }

    [Fact]
    public void Crafted_toast_ops_that_break_a_rule_are_refused()
    {
        // A team: 1 to 3 only.
        Assert.NotNull(Read(Bytes(w => { w.WriteByte((byte)TextOp.TeamName); w.WriteByte(3); })));
        Assert.Null(Read(Bytes(w => { w.WriteByte((byte)TextOp.TeamName); w.WriteByte(0); })));
        Assert.Null(Read(Bytes(w => { w.WriteByte((byte)TextOp.TeamName); w.WriteByte(4); })));
        Assert.Null(Read(Bytes(w => w.WriteByte((byte)TextOp.TeamName))));

        // An asset: a relic, talent or ability, an id, a level to 99.
        static byte[] Asset(byte kind, string id, long level) => Bytes(w =>
        {
            w.WriteByte((byte)TextOp.AssetName);
            w.WriteByte(kind);
            w.WriteVarUInt((ulong)id.Length);
            w.WriteBytes(System.Text.Encoding.UTF8.GetBytes(id));
            w.WriteVarInt(level);
        });
        Assert.NotNull(Read(Asset(1, "x", 0)));
        Assert.NotNull(Read(Asset(3, "x", TextRecipe.MaxAssetLevel)));
        Assert.Null(Read(Asset(0, "x", 1)));
        Assert.Null(Read(Asset(4, "x", 1)));
        Assert.Null(Read(Asset(1, string.Empty, 1)));
        Assert.Null(Read(Asset(1, new string('i', EventChoiceSet.MaxNameBytes + 1), 1)));
        Assert.Null(Read(Asset(1, "x", -1)));
        Assert.Null(Read(Asset(1, "x", TextRecipe.MaxAssetLevel + 1)));
        Assert.Null(Read(Asset(1, "x", long.MaxValue)));

        // An injury and a keyword stat: 0 to 63.
        foreach (TextOp op in new[] { TextOp.InjuryName, TextOp.LocalizedStatName })
        {
            Assert.NotNull(Read(Bytes(w => { w.WriteByte((byte)op); w.WriteVarInt(63); })));
            Assert.Null(Read(Bytes(w => { w.WriteByte((byte)op); w.WriteVarInt(64); })));
            Assert.Null(Read(Bytes(w => { w.WriteByte((byte)op); w.WriteVarInt(-1); })));
            Assert.Null(Read(Bytes(w => { w.WriteByte((byte)op); w.WriteVarInt(long.MinValue); })));
        }
    }

    [Fact]
    public void A_toast_op_cut_short_is_refused_at_every_length()
    {
        foreach (TextRecipe recipe in TextRecipeTests.EveryToastOp())
        {
            byte[] bytes = TextRecipeTests.Encoded(recipe);
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.Null(Read(bytes[..length]));
            }
        }
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = TextRecipeTests.EveryToastOp().Select(TextRecipeTests.Encoded).ToArray();
        var random = new Random(8181);
        int read = 0;
        for (int round = 0; round < 20000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            switch (random.Next(4))
            {
                case 0:
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes[random.Next(bytes.Length)] = new byte[] { 0x00, 0x01, 0x03, 0x04, 0x0A, 0x0D, 0x3F, 0x7F, 0xFF }[random.Next(9)];
                    break;
                case 2:
                    bytes = bytes[..random.Next(bytes.Length)];
                    break;
                default:
                    bytes = new byte[random.Next(0, 48)];
                    random.NextBytes(bytes);
                    break;
            }

            var reader = new BitReader(bytes);
            if (!TextRecipe.TryRead(ref reader, out TextRecipe? recipe))
            {
                Assert.Null(recipe);
                continue;
            }

            read++;
            Assert.Null(recipe!.Check());
            byte[] again = TextRecipeTests.Encoded(recipe);
            Assert.Equal(again, TextRecipeTests.Encoded(Read(again)!));
            _ = recipe.Evaluate(new FakeTexts("de"));
            _ = recipe.ToString();
        }

        Assert.True(read > 0, "some changed recipes should still read");
    }

    /// <summary>A PC's functions without the toast ops: the interface a build before them would have.</summary>
    private sealed class PlainTexts : ITextFunctions
    {
        private readonly ITextFunctions _inner;

        public PlainTexts(ITextFunctions inner) => _inner = inner;

        public string? Term(string key) => _inner.Term(key);

        public string? Format(string format, IReadOnlyList<string> args) => _inner.Format(format, args);

        public string? ObjectName(ChoiceObjectRef target) => _inner.ObjectName(target);

        public string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex) =>
            _inner.ObjectDescription(target, includeUpgrade, includeStatBonus, colorHex);

        public string? StatName(int stat, bool description) => _inner.StatName(stat, description);

        public string? NurseProcedureName(int procedure) => _inner.NurseProcedureName(procedure);

        public string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames) =>
            _inner.NurseCardDescription(procedure, talent, injury, injuryGames);
    }
}
