using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A game's text functions in one language, for tests: every text says its language ("de:term:x", "de:name:object 1"), a
/// format replaces {0}, {1}... as the game's safe format does, and each call is counted. Objects named in
/// <see cref="Missing"/> aren't there (null); <see cref="Throws"/> makes every function throw. It also has the toast ops'
/// functions ("de:team:Run", "de:asset:Relic:hot_cake:1", "de:injury:2", "de:keystat:3").
/// </summary>
internal sealed class FakeTexts : IToastTextFunctions
{
    public FakeTexts(string language) => Language = language;

    public string Language { get; }

    public HashSet<ChoiceObjectRef> Missing { get; } = new();

    public HashSet<string> MissingTerms { get; } = new(StringComparer.Ordinal);

    public HashSet<ToastTeam> MissingTeams { get; } = new();

    public HashSet<string> MissingAssets { get; } = new(StringComparer.Ordinal);

    public bool Throws { get; set; }

    public int Calls { get; private set; }

    public string? Term(string key) => Call() ?? (MissingTerms.Contains(key) ? null : $"{Language}:term:{key}");

    public string? Format(string format, IReadOnlyList<string> args)
    {
        Call();
        string made = format;
        for (int i = 0; i < args.Count; i++)
        {
            made = made.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}", args[i], StringComparison.Ordinal);
        }

        return $"<{made}|{string.Join("|", args)}>";
    }

    public string? ObjectName(ChoiceObjectRef target) => Call() ?? (Missing.Contains(target) ? null : $"{Language}:name:{target}");

    public string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex) =>
        Call() ?? (Missing.Contains(target) ? null : $"{Language}:desc:{target}:{includeUpgrade}:{includeStatBonus}:{colorHex}");

    public string? StatName(int stat, bool description) => Call() ?? (stat >= EventTextRecipes.ForwardStats ? null : $"{Language}:stat:{stat}:{description}");

    public string? NurseProcedureName(int procedure) => Call() ?? (procedure >= EventTextRecipes.NurseProcedures ? null : $"{Language}:procedure:{procedure}");

    public string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames) =>
        Call() ?? (Missing.Contains(talent) ? null : $"{Language}:card:{procedure?.ToString(CultureInfo.InvariantCulture) ?? "none"}:{talent}:{injury?.ToString(CultureInfo.InvariantCulture) ?? "none"}:{injuryGames}");

    public string? TeamName(ToastTeam team) => Call() ?? (MissingTeams.Contains(team) ? null : $"{Language}:team:{team}");

    public string? AssetName(ToastObjectKind kind, string id, int level) =>
        Call() ?? (MissingAssets.Contains(id) ? null : $"{Language}:asset:{kind}:{id}:{level.ToString(CultureInfo.InvariantCulture)}");

    public string? InjuryName(int injury) => Call() ?? $"{Language}:injury:{injury.ToString(CultureInfo.InvariantCulture)}";

    public string? LocalizedStatName(int stat) => Call() ?? (stat >= EventTextRecipes.ForwardStats ? null : $"{Language}:keystat:{stat.ToString(CultureInfo.InvariantCulture)}");

    private string? Call()
    {
        Calls++;
        return Throws ? throw new InvalidOperationException("the game threw") : (string?)null;
    }
}

/// <summary>A game's format as the game's is: <see cref="string.Format(string, object[])"/>, widths and all (its <c>SafeFormat</c> ends in <c>String.FormatHelper</c>).</summary>
internal sealed class PaddingTexts : ITextFunctions
{
    /// <summary>How many times the format ran.</summary>
    public int Formats { get; private set; }

    /// <summary>When set, what the format makes (else the real format's text).</summary>
    public string? Made { get; init; }

    public string? Term(string key) => key;

    public string? Format(string format, IReadOnlyList<string> args)
    {
        Formats++;
        return Made ?? string.Format(CultureInfo.InvariantCulture, format, args.Cast<object>().ToArray());
    }

    public string? ObjectName(ChoiceObjectRef target) => null;

    public string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex) => null;

    public string? StatName(int stat, bool description) => null;

    public string? NurseProcedureName(int procedure) => null;

    public string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames) => null;
}

public class TextRecipeTests
{
    /// <summary>One recipe of every op, nested as the game's producers nest them.</summary>
    public static IEnumerable<TextRecipe> EveryOp() => new[]
    {
        TextRecipe.Literal("Ölof Bergström\n+2"),
        TextRecipe.Term(EventTextRecipes.FanTraining),
        TextRecipe.Format(TextRecipe.Term(EventTextRecipes.FanNameDescription), TextRecipe.ObjectName(ChoiceObjectRef.Object1),
            TextRecipe.ObjectDescription(ChoiceObjectRef.Object1, false, true, EventTextRecipes.DescriptionColor)),
        TextRecipe.Concat(TextRecipe.Literal("a"), TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(15)), TextRecipe.ObjectName(ChoiceObjectRef.AgentAbility)),
        TextRecipe.ObjectName(ChoiceObjectRef.Object2),
        TextRecipe.ObjectDescription(ChoiceObjectRef.AgentTalent(3), true, false, "FFFFFF"),
        TextRecipe.StatName(4, true),
        TextRecipe.NurseProcedureName(9),
        TextRecipe.NurseCardDescription(5, ChoiceObjectRef.Object1, 3, 2),
        TextRecipe.NurseCardDescription(null, ChoiceObjectRef.None, null, -1),
        TextRecipe.Format(TextRecipe.Literal("{0}")),
    };

    /// <summary>One recipe of each of the toast and prompt ops, alone and in the formats the game's makers use
    /// around them.</summary>
    public static IEnumerable<TextRecipe> EveryToastOp() => new[]
    {
        TextRecipe.TeamName(ToastTeam.Run),
        TextRecipe.TeamName(ToastTeam.Home),
        TextRecipe.TeamName(ToastTeam.Visitor),
        TextRecipe.AssetName(ToastObjectKind.Relic, "hot_cake", 1),
        TextRecipe.AssetName(ToastObjectKind.Talent, "ünïcode_talent", TextRecipe.MaxAssetLevel),
        TextRecipe.AssetName(ToastObjectKind.Ability, "a", 0),
        TextRecipe.InjuryName(0),
        TextRecipe.InjuryName(TextRecipe.MaxInjury),
        TextRecipe.LocalizedStatName(0),
        TextRecipe.LocalizedStatName(TextRecipe.MaxStat),
        TextRecipe.Format(TextRecipe.Term("worldmap/notifications/lineup_changed"), TextRecipe.Literal("2")),
        TextRecipe.Format(TextRecipe.Literal("	+{0} {1}"), TextRecipe.Literal("1"), TextRecipe.LocalizedStatName(3)),
        TextRecipe.Format(TextRecipe.Term("v2ui/injury_toast/desc"), TextRecipe.InjuryName(2), TextRecipe.Literal("2")),
        TextRecipe.Concat(TextRecipe.Literal("<color=green>"), TextRecipe.AssetName(ToastObjectKind.Relic, "x", 2), TextRecipe.Literal("</color>")),
    };

    internal static byte[] Encoded(TextRecipe recipe)
    {
        var writer = new BitWriter();
        recipe.Write(writer);
        return writer.ToArray();
    }

    private static TextRecipe? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = TextRecipe.TryRead(ref reader, out TextRecipe? recipe);
        Assert.Equal(ok, recipe != null);
        return ok && reader.RemainingBits < 8 ? recipe : null;
    }

    [Fact]
    public void Every_op_round_trips_field_by_field()
    {
        foreach (TextRecipe sent in EveryOp())
        {
            TextRecipe read = Read(Encoded(sent))!;
            Assert.NotNull(read);
            Assert.Equal(Encoded(sent), Encoded(read));
            Assert.Equal((sent.Op, sent.Text, sent.Target, sent.Number, sent.Injury, sent.Games, sent.Flag1, sent.Flag2, sent.Args.Count),
                (read.Op, read.Text, read.Target, read.Number, read.Injury, read.Games, read.Flag1, read.Flag2, read.Args.Count));
            Assert.Equal(sent.ToString(), read.ToString());
            Assert.Equal(sent.Evaluate(new FakeTexts("de")), read.Evaluate(new FakeTexts("de")));
        }
    }

    [Fact]
    public void Each_op_calls_the_games_function_it_names_with_its_values()
    {
        var de = new FakeTexts("de");
        Assert.Equal("Ölof Bergström\n+2", TextRecipe.Literal("Ölof Bergström\n+2").Evaluate(de));
        Assert.Equal("de:term:x/y", TextRecipe.Term("x/y").Evaluate(de));
        Assert.Equal("<de:term:f|de:name:object 1|de:desc:object 1:False:True:1FFF75>",
            TextRecipe.Format(TextRecipe.Term("f"), TextRecipe.ObjectName(ChoiceObjectRef.Object1), TextRecipe.ObjectDescription(ChoiceObjectRef.Object1, false, true, "1FFF75")).Evaluate(de));
        Assert.Equal("<Hi Ole|Ole>", TextRecipe.Format(TextRecipe.Literal("Hi {0}"), TextRecipe.Literal("Ole")).Evaluate(de));
        Assert.Equal("ade:name:the free agent's talent 16", TextRecipe.Concat(TextRecipe.Literal("a"), TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(15))).Evaluate(de));
        Assert.Equal("de:stat:4:True", TextRecipe.StatName(4, true).Evaluate(de));
        Assert.Equal("de:procedure:9", TextRecipe.NurseProcedureName(9).Evaluate(de));
        Assert.Equal("de:card:5:object 1:3:2", TextRecipe.NurseCardDescription(5, ChoiceObjectRef.Object1, 3, 2).Evaluate(de));
        Assert.Equal("de:card:none:none:none:0", TextRecipe.NurseCardDescription(null, ChoiceObjectRef.None, null, 0).Evaluate(de));
        Assert.Equal("de:name:the free agent's ability", TextRecipe.ObjectName(ChoiceObjectRef.AgentAbility).Evaluate(de));
    }

    [Fact]
    public void A_function_that_gives_nothing_or_throws_makes_no_text()
    {
        var de = new FakeTexts("de");
        de.Missing.Add(ChoiceObjectRef.Object2);
        de.MissingTerms.Add("gone");
        Assert.Null(TextRecipe.ObjectName(ChoiceObjectRef.Object2).Evaluate(de));
        Assert.Null(TextRecipe.Term("gone").Evaluate(de));

        // Any argument missing: the whole format or concat is none (never half a text).
        Assert.Null(TextRecipe.Format(TextRecipe.Term("f"), TextRecipe.Literal("a"), TextRecipe.ObjectName(ChoiceObjectRef.Object2)).Evaluate(de));
        Assert.Null(TextRecipe.Format(TextRecipe.Term("gone"), TextRecipe.Literal("a")).Evaluate(de));
        Assert.Null(TextRecipe.Concat(TextRecipe.Literal("a"), TextRecipe.Term("gone")).Evaluate(de));

        var throwing = new FakeTexts("de") { Throws = true };
        Assert.Null(TextRecipe.Term("x").Evaluate(throwing));
        Assert.Null(TextRecipe.Format(TextRecipe.Term("f"), TextRecipe.StatName(1, false)).Evaluate(throwing));
        Assert.Throws<ArgumentNullException>(() => TextRecipe.Term("x").Evaluate(null!));
    }

    [Fact]
    public void Targets_lists_every_object_a_recipe_names()
    {
        TextRecipe recipe = TextRecipe.Format(TextRecipe.Term("f"), TextRecipe.ObjectName(ChoiceObjectRef.Object1),
            TextRecipe.Concat(TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(2)), TextRecipe.NurseCardDescription(1, ChoiceObjectRef.None, null, 0)));
        Assert.Equal(new[] { ChoiceObjectRef.Object1, ChoiceObjectRef.AgentTalent(2) }, recipe.Targets());
        Assert.Empty(TextRecipe.Literal("x").Targets());
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        TextRecipe leaf = TextRecipe.Literal("x");
        TextRecipe Deep(int depth) => depth == 1 ? leaf : TextRecipe.Concat(Deep(depth - 1));
        yield return new object[] { "five deep", Deep(TextRecipe.MaxDepth + 1) };
        yield return new object[] { "21 nodes", TextRecipe.Concat(Enumerable.Range(0, 4).Select(_ => TextRecipe.Concat(leaf, leaf, leaf, leaf)).ToArray()) };
        yield return new object[] { "a format of 9 arguments", TextRecipe.Format(leaf, Enumerable.Repeat(leaf, TextRecipe.MaxArgs + 1).ToArray()) };
        yield return new object[] { "a concat of 9", TextRecipe.Concat(Enumerable.Repeat(leaf, TextRecipe.MaxArgs + 1).ToArray()) };
        yield return new object[] { "an empty concat", TextRecipe.Concat() };
        yield return new object[] { "a literal of 257 bytes", TextRecipe.Literal(new string('l', TextRecipe.MaxTextBytes + 1)) };
        yield return new object[] { "a literal with a NUL", TextRecipe.Literal("a\0b") };
        yield return new object[] { "a term with a line break", TextRecipe.Term("a\nb") };
        yield return new object[] { "an empty term", TextRecipe.Term(string.Empty) };
        yield return new object[] { "a colour of 17 bytes", TextRecipe.ObjectDescription(ChoiceObjectRef.Object1, false, false, new string('F', TextRecipe.MaxColorBytes + 1)) };
        yield return new object[] { "a name of no object", TextRecipe.ObjectName(ChoiceObjectRef.None) };
        yield return new object[] { "a description of no object", TextRecipe.ObjectDescription(ChoiceObjectRef.None, false, false, "F") };
        yield return new object[] { "an agent's talent 17", TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(FreeAgentData.MaxTalents)) };
        yield return new object[] { "a negative talent", TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(-1)) };
        yield return new object[] { "an index on object 1", TextRecipe.ObjectName(new ChoiceObjectRef(ChoiceObjectSlot.Object1, 1)) };
        yield return new object[] { "an unknown slot", TextRecipe.ObjectName(new ChoiceObjectRef((ChoiceObjectSlot)5)) };
        yield return new object[] { "stat 64", TextRecipe.StatName(TextRecipe.MaxStat + 1, false) };
        yield return new object[] { "a negative stat", TextRecipe.StatName(-1, false) };
        yield return new object[] { "procedure 32", TextRecipe.NurseProcedureName(TextRecipe.MaxProcedure + 1) };
        yield return new object[] { "a card's procedure 32", TextRecipe.NurseCardDescription(TextRecipe.MaxProcedure + 1, ChoiceObjectRef.None, null, 0) };
        yield return new object[] { "a card's injury 64", TextRecipe.NurseCardDescription(1, ChoiceObjectRef.None, TextRecipe.MaxInjury + 1, 0) };
        yield return new object[] { "a card's 10000 games", TextRecipe.NurseCardDescription(1, ChoiceObjectRef.None, 1, TextRecipe.MaxGames + 1) };
        yield return new object[] { "a card's -2 games", TextRecipe.NurseCardDescription(1, ChoiceObjectRef.None, 1, -2) };
        yield return new object[] { "a missing argument", TextRecipe.Concat(leaf, null!) };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Recipes_out_of_their_limits_are_refused_and_make_nothing(string what, TextRecipe recipe)
    {
        Assert.NotNull(recipe.Check());
        Assert.ThrowsAny<ArgumentException>(() => Encoded(recipe));
        Assert.Null(recipe.Evaluate(new FakeTexts("de")));
        Assert.False(string.IsNullOrEmpty(recipe.ToString()), what);
    }

    [Fact]
    public void A_crafted_recipe_within_its_limits_cant_make_a_huge_text()
    {
        // 85 placeholders a format, three formats deep, around 256 bytes: 157 MB of text from 7 nodes, were it made.
        string holes = string.Concat(Enumerable.Repeat("{0}", 85));
        TextRecipe bomb = TextRecipe.Format(TextRecipe.Literal(holes), TextRecipe.Format(TextRecipe.Literal(holes), TextRecipe.Format(TextRecipe.Literal(holes), TextRecipe.Literal(new string('x', 256)))));
        Assert.Null(bomb.Check());
        Assert.Null(bomb.Evaluate(new FakeTexts("de")));

        TextRecipe wide = TextRecipe.Concat(Enumerable.Repeat(TextRecipe.Format(TextRecipe.Literal(holes), TextRecipe.Literal(new string('x', 200))), 4).ToArray());
        Assert.Null(wide.Check());
        Assert.Null(wide.Evaluate(new FakeTexts("de")));

        // The real formats go through: a few placeholders around a long description.
        TextRecipe real = TextRecipe.Format(TextRecipe.Literal("<b>{0}</b>\n{1}"), TextRecipe.Literal("Name"), TextRecipe.Literal(new string('d', 256)));
        Assert.NotNull(real.Evaluate(new FakeTexts("de")));
    }

    [Theory]
    [InlineData("{0,1000000}")] // the game's format pads a value to this width whatever its length: 1 MB from 11 bytes
    [InlineData("{0,-1000000}")]
    [InlineData("{0 , 16385}")]
    [InlineData("{0,16385:x}")]
    public void A_crafted_format_cant_ask_for_a_huge_text_by_its_width(string format)
    {
        var padding = new PaddingTexts();
        TextRecipe bomb = TextRecipe.Format(TextRecipe.Literal(format), TextRecipe.Literal("x"));
        Assert.Null(bomb.Check());
        Assert.Null(bomb.Evaluate(padding));
        Assert.Equal(0, padding.Formats); // refused before the game's format runs: it would have allocated the text

        // 23 of them fit a literal's 256 bytes: 23 million characters on a toast, were the format run.
        string many = string.Concat(Enumerable.Repeat("{0,999999}", 25));
        TextRecipe wide = TextRecipe.Format(TextRecipe.Literal(many), TextRecipe.Literal("x"));
        Assert.Null(wide.Check());
        Assert.Null(wide.Evaluate(padding));
        Assert.Equal(0, padding.Formats);
    }

    [Fact]
    public void A_small_width_and_the_games_own_placeholders_still_make_their_text()
    {
        var padding = new PaddingTexts();
        Assert.Equal("  x", TextRecipe.Format(TextRecipe.Literal("{0,3}"), TextRecipe.Literal("x")).Evaluate(padding));
        Assert.Equal("x |y", TextRecipe.Format(TextRecipe.Literal("{0,-2}|{1}"), TextRecipe.Literal("x"), TextRecipe.Literal("y")).Evaluate(padding));
        Assert.Equal("{x}", TextRecipe.Format(TextRecipe.Literal("{{{0}}}"), TextRecipe.Literal("x")).Evaluate(padding));
        Assert.Equal(new string(' ', TextRecipe.MaxMadeChars / 2), TextRecipe.Format(TextRecipe.Literal("{0," + (TextRecipe.MaxMadeChars / 2).ToString(CultureInfo.InvariantCulture) + "}"), TextRecipe.Literal(" ")).Evaluate(padding));
    }

    [Fact]
    public void What_the_games_format_makes_over_the_limit_is_no_text()
    {
        // A function that makes more than the format could have asked for (a plural form the check can't see).
        var padding = new PaddingTexts { Made = new string('x', TextRecipe.MaxMadeChars + 1) };
        Assert.Null(TextRecipe.Format(TextRecipe.Literal("{0}"), TextRecipe.Literal("x")).Evaluate(padding));
        Assert.Equal(1, padding.Formats);
    }

    [Fact]
    public void Recipes_at_every_limit_are_sent_and_read()
    {
        TextRecipe longest = TextRecipe.Literal(new string('é', TextRecipe.MaxTextBytes / 2));
        TextRecipe widest = TextRecipe.Format(longest, Enumerable.Repeat(longest, TextRecipe.MaxArgs).ToArray()); // 10 nodes
        TextRecipe deepest = TextRecipe.Concat(TextRecipe.Concat(TextRecipe.Concat(TextRecipe.Term(new string('t', TextRecipe.MaxTextBytes)))));
        TextRecipe seven = TextRecipe.Format(longest, Enumerable.Repeat(longest, TextRecipe.MaxArgs - 1).ToArray()); // 9 nodes
        TextRecipe fullest = TextRecipe.Concat(seven, TextRecipe.Concat(TextRecipe.Concat(TextRecipe.ObjectDescription(ChoiceObjectRef.AgentTalent(15), true, true, new string('F', TextRecipe.MaxColorBytes)),
            TextRecipe.NurseCardDescription(TextRecipe.MaxProcedure, ChoiceObjectRef.AgentAbility, TextRecipe.MaxInjury, TextRecipe.MaxGames)), TextRecipe.StatName(TextRecipe.MaxStat, true),
            TextRecipe.NurseProcedureName(TextRecipe.MaxProcedure))); // 16 nodes, 4 deep
        foreach (TextRecipe recipe in new[] { widest, deepest, fullest })
        {
            Assert.Null(recipe.Check());
            Assert.Equal(Encoded(recipe), Encoded(Read(Encoded(recipe))!));
        }
    }

    [Fact]
    public void Crafted_recipes_that_break_a_rule_are_refused()
    {
        static byte[] Bytes(Action<BitWriter> write)
        {
            var writer = new BitWriter();
            write(writer);
            return writer.ToArray();
        }

        // An op this version doesn't have, and none.
        Assert.Null(Read(Bytes(w => w.WriteByte(14))));
        Assert.Null(Read(Bytes(w => w.WriteByte(0))));

        // A concat that says it has 200 parts (only one follows), and one of none.
        Assert.Null(Read(Bytes(w =>
        {
            w.WriteByte((byte)TextOp.Concat);
            w.WriteByte(200);
            w.WriteByte((byte)TextOp.Literal);
            w.WriteVarUInt(1);
            w.WriteByte((byte)'a');
        })));
        Assert.Null(Read(Bytes(w =>
        {
            w.WriteByte((byte)TextOp.Concat);
            w.WriteByte(0);
        })));

        // A tree nested past the depth limit (a chain of one-part concats, then a literal).
        Assert.Null(Read(Bytes(w =>
        {
            for (int i = 0; i < TextRecipe.MaxDepth; i++)
            {
                w.WriteByte((byte)TextOp.Concat);
                w.WriteByte(1);
            }

            w.WriteByte((byte)TextOp.Literal);
            w.WriteVarUInt(1);
            w.WriteByte((byte)'a');
        })));

        // A literal whose length is more than what follows; a slot this version doesn't have; a talent index past 15.
        Assert.Null(Read(Bytes(w =>
        {
            w.WriteByte((byte)TextOp.Literal);
            w.WriteVarUInt(5);
            w.WriteByte((byte)'a');
        })));
        Assert.Null(Read(Bytes(w =>
        {
            w.WriteByte((byte)TextOp.ObjectName);
            w.WriteByte(9);
        })));
        Assert.Null(Read(Bytes(w =>
        {
            w.WriteByte((byte)TextOp.ObjectName);
            w.WriteByte((byte)ChoiceObjectSlot.AgentTalent);
            w.WriteVarUInt(16);
        })));

        // A stat number that doesn't fit an int.
        Assert.Null(Read(Bytes(w =>
        {
            w.WriteByte((byte)TextOp.StatName);
            w.WriteVarInt(long.MaxValue);
            w.WriteBool(false);
        })));
    }

    [Fact]
    public void A_recipe_cut_short_is_refused_at_every_length()
    {
        foreach (TextRecipe recipe in EveryOp())
        {
            byte[] bytes = Encoded(recipe);
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.Null(Read(bytes[..length]));
            }
        }
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = EveryOp().Select(Encoded).ToArray();
        var random = new Random(2031);
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
                    bytes[random.Next(bytes.Length)] = new byte[] { 0x00, 0x01, 0x03, 0x04, 0x09, 0x0A, 0x7F, 0xFF }[random.Next(8)];
                    break;
                case 2:
                    bytes = bytes[..random.Next(bytes.Length)];
                    break;
                default:
                    bytes = new byte[random.Next(0, 64)];
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
            byte[] again = Encoded(recipe);
            Assert.Equal(again, Encoded(Read(again)!));
            _ = recipe.Evaluate(new FakeTexts("de"));
            _ = recipe.ToString();
        }

        Assert.True(read > 0, "some changed recipes should still read");
    }
}
