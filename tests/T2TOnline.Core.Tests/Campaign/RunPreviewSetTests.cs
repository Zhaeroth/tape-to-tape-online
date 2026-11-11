using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>The run-start carrier: the run preview set (attachment kind 5), its skaters' looks and rewards, and the skater comparer.</summary>
public class RunPreviewSetTests
{
    /// <summary>A small run file, as the game's run repository writes one (Easy Save JSON).</summary>
    public static byte[] RunFile(string squad = "vanilla") =>
        Encoding.UTF8.GetBytes("{\n\t\"runData\" : {\n\t\t\"__type\" : \"State.RunDataModelV2,MainScriptsAssembly\",\n\t\t\"value\" : {\"squadId\" : \"" + squad +
            "\", \"currentAct\" : 1, \"currentFloor\" : 0, \"runID\" : \"0a1b2c3d\"}\n\t},\n\t\"runTime\" : {\"__type\" : \"float\", \"value\" : 0}\n}");

    /// <summary>A superstar's look: hidden offsets, glasses, colours, height and weight.</summary>
    public static SkaterLook Look() => new()
    {
        HiddenShotPower = 3,
        HiddenSpeed = -2,
        HiddenChecking = 0,
        HiddenShotAccuracy = 999,
        IsFemale = true,
        HasGoldenHelmet = true,
        GlassesSkin = "glasses_round",
        HelmetAwaySkin = string.Empty,
        SizeOffset = 0.15f,
        NumberColor = new ColorValue(1, 1, 1, 1),
        StickColor = new ColorSchemeData(new ColorValue(0.5f, 0.25f, 0, 1), new ColorValue(0, 0, 0, 1), new ColorValue(2, 2, 2, 1)),
        SkatesColor = null,
        HeightInches = 74,
        WeightKg = 92,
    };

    /// <summary>A superstar pick: the run and three superstars.</summary>
    public static RunPreviewSet SuperStarPick() => new()
    {
        Menu = RunStartMenu.OldSquad,
        Mode = RunPreviewSet.SuperStarOnly,
        IsTeamHome = true,
        RunFile = RunFile(),
        SuperStars = new[]
        {
            new RunStartSkater(EventChoiceSetTests.Agent("ss-1"), Look()),
            new RunStartSkater(EventChoiceSetTests.Agent("ss-2"), SkaterLook.Default, 1),
            new RunStartSkater(EventChoiceSetTests.Agent("ss-3"), Look(), FreeAgentData.MaxNumberFormat),
        },
    };

    /// <summary>The old school draft: superstars, power-ups (abilities and a talent with stickers), relics and random skaters.</summary>
    public static RunPreviewSet OldSchool() => new()
    {
        Menu = RunStartMenu.OldSquad,
        Mode = RunPreviewSet.FullSequence,
        IsTeamHome = false,
        RunFile = RunFile("oldschool"),
        SuperStars = new[] { new RunStartSkater(EventChoiceSetTests.Agent("ss-1"), Look()) },
        RandomSkaters = new[] { new RunStartSkater(EventChoiceSetTests.Agent("rs-1"), SkaterLook.Default), new RunStartSkater(EventChoiceSetTests.Agent("rs-2"), Look(), 2) },
        PowerUps = new[]
        {
            new RunStartReward { Kind = RunStartRewardKind.Ability, Id = "slapshot", Level = 1 },
            new RunStartReward { Kind = RunStartRewardKind.Talent, Id = "speedy", Level = 2, Stickers = new[] { new RunStartSticker(1, 5), new RunStartSticker(3, -2) } },
        },
        Relics = new[] { new RunStartReward { Kind = RunStartRewardKind.Relic, Id = "lucky_puck", Level = 0, Stickers = new[] { new RunStartSticker(0, 1) } } },
    };

    /// <summary>The lineup with benchwarmers: the run (with the superstar picked) and three benchwarmers (hot, locked, cold).</summary>
    public static RunPreviewSet Lineup() => new()
    {
        Menu = RunStartMenu.Benchwarmer,
        IsTeamHome = true,
        RunFile = RunFile("tchel"),
        Pool = new[]
        {
            new RunStartBenchwarmer(new RunStartSkater(EventChoiceSetTests.Agent("kovalski"), Look(), 1), Unlocked: true, Hot: true, Cold: false, BenchPowerId: "overshot"),
            new RunStartBenchwarmer(new RunStartSkater(EventChoiceSetTests.Agent("sally"), SkaterLook.Default), Unlocked: false, Hot: false, Cold: false),
            new RunStartBenchwarmer(new RunStartSkater(EventChoiceSetTests.Agent("kirby"), Look(), 2), Unlocked: true, Hot: false, Cold: true),
        },
    };

    public static byte[] Encoded(RunPreviewSet set)
    {
        var writer = new BitWriter(4096);
        set.Write(writer);
        return writer.ToArray();
    }

    private static RunPreviewSet? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = RunPreviewSet.TryRead(ref reader, out RunPreviewSet? set);
        Assert.Equal(ok, set != null);
        return ok && reader.RemainingBits < 8 ? set : null;
    }

    [Fact]
    public void A_superstar_pick_round_trips_field_by_field()
    {
        RunPreviewSet sent = SuperStarPick();
        RunPreviewSet read = Read(Encoded(sent))!;
        Assert.NotNull(read);
        Assert.Equal((RunStartMenu.OldSquad, RunPreviewSet.SuperStarOnly, true), (read.Menu, read.Mode, read.IsTeamHome));
        Assert.Equal(sent.RunFile, read.RunFile);
        Assert.Equal(sent.RunHash, read.RunHash);
        Assert.Equal(3, read.SuperStars.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(SkaterModelDiff.FirstDifference(sent.SuperStars[i].Model, read.SuperStars[i].Model));
            Assert.Null(SkaterModelDiff.FirstDifference(sent.SuperStars[i].Look, read.SuperStars[i].Look));
        }

        Assert.Equal(new[] { 0, 1, FreeAgentData.MaxNumberFormat }, read.SuperStars.Select(s => s.NumberFormat));
        Assert.Equal(string.Empty, read.SuperStars[0].Look.HelmetAwaySkin); // empty stays empty, null stays null
        Assert.Null(read.SuperStars[0].Look.SkatesColor);
        Assert.Empty(read.PowerUps);
        Assert.Contains("the superstar pick", read.ToString(), StringComparison.Ordinal);
        Assert.Contains("3 superstar(s)", read.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 with a Roman-numeral or Prison back number", read.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("back number", new RunPreviewSet { RunFile = RunFile(), SuperStars = new[] { new RunStartSkater(EventChoiceSetTests.Agent("a"), Look()) } }.ToString(), StringComparison.Ordinal);
        Assert.Equal(Encoded(sent), Encoded(read));
    }

    [Fact]
    public void The_old_school_draft_round_trips_with_every_offer()
    {
        RunPreviewSet sent = OldSchool();
        RunPreviewSet read = Read(Encoded(sent))!;
        Assert.Equal((RunPreviewSet.FullSequence, false), (read.Mode, read.IsTeamHome));
        Assert.Equal(new[] { "rs-1", "rs-2" }, read.RandomSkaters.Select(s => s.Model.Id));
        Assert.Equal(new[] { 0, 2 }, read.RandomSkaters.Select(s => s.NumberFormat));
        Assert.Equal(0, read.SuperStars[0].NumberFormat);
        Assert.Equal(new[] { (RunStartRewardKind.Ability, "slapshot", 1), (RunStartRewardKind.Talent, "speedy", 2) }, read.PowerUps.Select(p => (p.Kind, p.Id, p.Level)));
        Assert.Equal(new[] { new RunStartSticker(1, 5), new RunStartSticker(3, -2) }, read.PowerUps[1].Stickers);
        Assert.Equal("lucky_puck", read.Relics.Single().Id);
        Assert.Contains("the old school draft", read.ToString(), StringComparison.Ordinal);
        Assert.Equal(Encoded(sent), Encoded(read));
    }

    [Fact]
    public void A_lineup_round_trips_with_its_benchwarmers_in_format_4()
    {
        RunPreviewSet sent = Lineup();
        Assert.Null(sent.Problem());
        Assert.Equal(MenuAttachment.RunPreviewLineupFormat, sent.Format);
        RunPreviewSet read = Read(Encoded(sent))!;
        Assert.NotNull(read);
        Assert.Equal((RunStartMenu.Benchwarmer, 0, true), (read.Menu, read.Mode, read.IsTeamHome));
        Assert.Equal(new[] { "kovalski", "sally", "kirby" }, read.Pool.Select(b => b.Skater.Model.Id));
        Assert.Equal(new[] { (true, true, false), (false, false, false), (true, false, true) }, read.Pool.Select(b => (b.Unlocked, b.Hot, b.Cold)));
        Assert.Equal(new[] { "overshot", null, null }, read.Pool.Select(b => b.BenchPowerId));
        Assert.Equal(new[] { 1, 0, 2 }, read.Pool.Select(b => b.Skater.NumberFormat));
        Assert.Contains("number format 2", read.Pool[2].ToString(), StringComparison.Ordinal);
        Assert.Contains("bench power \"overshot\"", read.Pool[0].ToString(), StringComparison.Ordinal);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(SkaterModelDiff.FirstDifference(sent.Pool[i].Skater.Model, read.Pool[i].Skater.Model));
            Assert.Null(SkaterModelDiff.FirstDifference(sent.Pool[i].Skater.Look, read.Pool[i].Skater.Look));
        }

        Assert.Empty(read.SuperStars);
        Assert.Contains("the lineup", read.ToString(), StringComparison.Ordinal);
        Assert.Contains("3 benchwarmer(s) (2 unlocked, 1 hot, 1 cold)", read.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 with a Roman-numeral or Prison back number", read.ToString(), StringComparison.Ordinal);
        Assert.Contains(", locked", read.Pool[1].ToString(), StringComparison.Ordinal);
        Assert.Equal(Encoded(sent), Encoded(read));

        // As an attachment: format 4, and it reads back as the same set.
        MenuAttachment attachment = MenuAttachment.RunPreview(11, sent);
        Assert.Equal((MenuAttachmentKind.RunPreview, MenuAttachment.RunPreviewLineupFormat), (attachment.Kind, attachment.Format));
        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, attachment));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? received));
        Assert.True(received!.TryGetRunPreview(out RunPreviewSet? set));
        Assert.Equal(Encoded(sent), Encoded(set!));

        // An empty pool is a lineup too (a squad whose own swap pool is empty).
        Assert.Null(new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile() }.Problem());
    }

    [Fact]
    public void An_old_squad_set_is_format_3_and_only_a_lineup_is_format_4_and_the_formats_before_are_refused()
    {
        Assert.Equal((3, 4), (MenuAttachment.RunPreviewFormat, MenuAttachment.RunPreviewLineupFormat));
        Assert.Equal(MenuAttachment.RunPreviewFormat, SuperStarPick().Format);
        Assert.Equal(MenuAttachment.RunPreviewFormat, MenuAttachment.RunPreview(3, OldSchool()).Format);

        // A body whose format isn't its menu's is refused on the wire: a lineup sent as the old squad menu's format, an old
        // squad set sent as the lineup's.
        Assert.Null(MenuAttachment.FromWire(4, MenuAttachmentKind.RunPreview, MenuAttachment.RunPreviewFormat, Encoded(Lineup())));
        Assert.Null(MenuAttachment.FromWire(4, MenuAttachmentKind.RunPreview, MenuAttachment.RunPreviewLineupFormat, Encoded(SuperStarPick())));
        Assert.NotNull(MenuAttachment.FromWire(4, MenuAttachmentKind.RunPreview, MenuAttachment.RunPreviewLineupFormat, Encoded(Lineup())));
        Assert.NotNull(MenuAttachment.FromWire(4, MenuAttachmentKind.RunPreview, MenuAttachment.RunPreviewFormat, Encoded(SuperStarPick())));

        // The formats before each skater carried its number format (1 and 2) are refused cleanly, whatever the body is, and so is
        // a format nobody has.
        foreach (byte old in new byte[] { 0, 1, 2, 5, 255 })
        {
            Assert.Null(MenuAttachment.FromWire(4, MenuAttachmentKind.RunPreview, old, Encoded(SuperStarPick())));
            Assert.Null(MenuAttachment.FromWire(4, MenuAttachmentKind.RunPreview, old, Encoded(Lineup())));
        }

        // A format 1 body as it was written (no number format after each look) doesn't read as a set.
        Assert.Null(Read(WithoutNumberFormats(SuperStarPick())));
        Assert.Null(Read(WithoutNumberFormats(Lineup())));

        // A lineup's body cut before its pool (what a format 1 lineup would have been) doesn't read.
        byte[] withoutPool = Encoded(new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile() })[..^1];
        Assert.Null(Read(withoutPool));
    }

    [Fact]
    public void A_benchwarmer_with_an_unknown_flag_is_refused()
    {
        // Written field by field as the set writes a one-benchwarmer lineup, with the flags given.
        Assert.Equal(Encoded(new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = new[] { Lineup().Pool[2] } }), Crafted(1 | 4));
        Assert.True(Read(Crafted(1 | 2))!.Pool[0].Hot);
        Assert.False(Read(Crafted(0))!.Pool[0].Unlocked);
        Assert.Null(Read(Crafted(1 | 8)));
        Assert.Null(Read(Crafted(0x80)));

        static byte[] Crafted(byte flags)
        {
            byte[] run = RunFile();
            var writer = new BitWriter();
            writer.WriteByte((byte)RunStartMenu.Benchwarmer);
            writer.WriteByte(0);
            writer.WriteBool(true);
            writer.WriteVarUInt((ulong)run.Length);
            writer.WriteBytes(run);
            writer.WriteUInt64(RunPreviewSet.HashOf(run));
            for (int list = 0; list < 4; list++)
            {
                writer.WriteVarUInt(0);
            }

            writer.WriteVarUInt(1);
            Lineup().Pool[2].Skater.Write(writer);
            writer.WriteByte(flags);
            EventWire.OptionalName(writer, null);
            return writer.ToArray();
        }
    }

    /// <summary>A set as the formats before the number format wrote it (formats 1 and 2): each skater's model and look, nothing after.</summary>
    private static byte[] WithoutNumberFormats(RunPreviewSet set)
    {
        var writer = new BitWriter(4096);
        writer.WriteByte((byte)set.Menu);
        writer.WriteByte((byte)set.Mode);
        writer.WriteBool(set.IsTeamHome);
        writer.WriteVarUInt((ulong)set.RunFile.Length);
        writer.WriteBytes(set.RunFile);
        writer.WriteUInt64(set.RunHash);
        foreach (IReadOnlyList<RunStartSkater> list in new[] { set.SuperStars, set.RandomSkaters })
        {
            writer.WriteVarUInt((ulong)list.Count);
            foreach (RunStartSkater skater in list)
            {
                skater.Model.Write(writer);
                skater.Look.Write(writer);
            }
        }

        writer.WriteVarUInt(0);
        writer.WriteVarUInt(0);
        if (set.Menu == RunStartMenu.Benchwarmer)
        {
            writer.WriteVarUInt((ulong)set.Pool.Count);
            foreach (RunStartBenchwarmer benchwarmer in set.Pool)
            {
                benchwarmer.Skater.Model.Write(writer);
                benchwarmer.Skater.Look.Write(writer);
                writer.WriteByte((byte)((benchwarmer.Unlocked ? 1 : 0) | (benchwarmer.Hot ? 2 : 0) | (benchwarmer.Cold ? 4 : 0)));
                EventWire.OptionalName(writer, benchwarmer.BenchPowerId);
            }
        }

        return writer.ToArray();
    }

    [Fact]
    public void A_number_format_is_carried_for_every_kind_of_skater_and_a_regular_one_costs_one_byte()
    {
        // Roman numerals (1) and Prison (2) on a superstar, a random skater and a benchwarmer, regular (0) beside them.
        var set = new RunPreviewSet
        {
            Mode = RunPreviewSet.FullSequence,
            RunFile = RunFile(),
            SuperStars = new[] { new RunStartSkater(EventChoiceSetTests.Agent("a"), Look(), 1), new RunStartSkater(EventChoiceSetTests.Agent("b"), Look(), 0) },
            RandomSkaters = new[] { new RunStartSkater(EventChoiceSetTests.Agent("c"), Look(), 2) },
        };
        RunPreviewSet read = Read(Encoded(set))!;
        Assert.Equal(new[] { 1, 0 }, read.SuperStars.Select(s => s.NumberFormat));
        Assert.Equal(new[] { 2 }, read.RandomSkaters.Select(s => s.NumberFormat));

        // One byte a skater over the body the formats before wrote.
        Assert.Equal(WithoutNumberFormats(set).Length + 3, Encoded(set).Length);

        RunPreviewSet lineup = Lineup();
        Assert.Equal(lineup.Pool.Count, Encoded(lineup).Length - WithoutNumberFormats(lineup).Length);
        Assert.Equal(new[] { 1, 0, 2 }, Read(Encoded(lineup))!.Pool.Select(b => b.Skater.NumberFormat));
    }

    [Fact]
    public void A_number_format_out_of_range_is_refused_when_written_and_when_read()
    {
        foreach (int bad in new[] { -1, FreeAgentData.MaxNumberFormat + 1, int.MaxValue })
        {
            var skater = new RunStartSkater(EventChoiceSetTests.Agent("a"), Look(), bad);
            Assert.NotNull(new RunPreviewSet { RunFile = RunFile(), SuperStars = new[] { skater } }.Problem());
            Assert.NotNull(new RunPreviewSet { RunFile = RunFile(), RandomSkaters = new[] { skater } }.Problem());
            Assert.NotNull(new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = new[] { new RunStartBenchwarmer(skater, true, false, false) } }.Problem());
            Assert.Contains("number format", new RunPreviewSet { RunFile = RunFile(), SuperStars = new[] { skater } }.Problem(), StringComparison.Ordinal);
        }

        // On the wire: the varint after the look, crafted (the writer refuses these).
        Assert.NotNull(Read(Crafted(0)));
        Assert.NotNull(Read(Crafted(FreeAgentData.MaxNumberFormat)));
        Assert.Null(Read(Crafted(FreeAgentData.MaxNumberFormat + 1)));
        Assert.Null(Read(Crafted(-1)));
        Assert.Null(Read(Crafted(long.MaxValue)));
        Assert.Null(Read(Crafted(long.MinValue)));

        static byte[] Crafted(long format)
        {
            byte[] run = RunFile();
            var writer = new BitWriter();
            writer.WriteByte((byte)RunStartMenu.OldSquad);
            writer.WriteByte(RunPreviewSet.FullSequence);
            writer.WriteBool(true);
            writer.WriteVarUInt((ulong)run.Length);
            writer.WriteBytes(run);
            writer.WriteUInt64(RunPreviewSet.HashOf(run));
            writer.WriteVarUInt(1);
            RunStartSkater skater = new(EventChoiceSetTests.Agent("a"), Look());
            skater.Model.Write(writer);
            skater.Look.Write(writer);
            writer.WriteVarInt(format);
            for (int list = 0; list < 3; list++)
            {
                writer.WriteVarUInt(0);
            }

            return writer.ToArray();
        }
    }

    [Fact]
    public void A_friends_built_skater_is_compared_with_the_hosts_number_format_after_its_model_and_look()
    {
        var host = new RunStartSkater(EventChoiceSetTests.Agent("a"), Look(), 1);
        Assert.Null(SkaterModelDiff.FirstDifference(host, host with { }));

        // The run file's model has no number format: a skater built from it is regular until it's given the host's.
        Assert.Equal("number format (0 here, 1 on the host's)", SkaterModelDiff.FirstDifference(host, host with { NumberFormat = 0 }));
        Assert.Equal("number format (2 here, 1 on the host's)", SkaterModelDiff.FirstDifference(host, host with { NumberFormat = 2 }));

        // What the model and the look say comes first.
        Assert.Contains("hidden speed", SkaterModelDiff.FirstDifference(host, host with { Look = Look().With(hiddenSpeed: 9), NumberFormat = 0 }), StringComparison.Ordinal);
        Assert.Contains("id (", SkaterModelDiff.FirstDifference(host, new RunStartSkater(EventChoiceSetTests.Agent("b"), Look(), 0)), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => SkaterModelDiff.FirstDifference(host, (RunStartSkater)null!));
        Assert.Throws<ArgumentNullException>(() => SkaterModelDiff.FirstDifference((RunStartSkater)null!, host));
    }

    [Fact]
    public void The_log_text_reads_the_same_on_a_pc_that_writes_decimals_with_a_comma()
    {
        // On a PC whose culture writes decimals with a comma, the run's size is written as every other PC's log writes it.
        CultureInfo before = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = comma;
            Assert.Contains("run 1.5 KB (hash ", SuperStarPick().With(run: new byte[1536]).ToString(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_skater_that_differs_only_in_its_number_format_is_another_skater()
    {
        var regular = new RunStartSkater(EventChoiceSetTests.Agent("a"), Look());
        var roman = regular with { NumberFormat = 1 };
        Assert.NotEqual(regular, roman);
        Assert.Contains("number format 1", roman.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("number format", regular.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_largest_lineup_fits_an_attachment()
    {
        RunStartSkater skater = new(EventChoiceSetTests.Agent(new string('i', EventChoiceSet.MaxNameBytes)), Look());
        var largest = new RunPreviewSet
        {
            Menu = RunStartMenu.Benchwarmer,
            RunFile = new byte[RunPreviewSet.MaxRunBytes],
            Pool = Enumerable.Repeat(new RunStartBenchwarmer(skater, true, true, false), RunPreviewSet.MaxPool).ToList(),
        };
        Assert.Null(largest.Problem());
        MenuAttachment attachment = MenuAttachment.RunPreview(5, largest);
        Assert.InRange(attachment.BodyLength, RunPreviewSet.MaxRunBytes, MenuAttachment.MaxBodyBytes);
        Assert.True(attachment.TryGetRunPreview(out RunPreviewSet? read));
        Assert.Equal(RunPreviewSet.MaxPool, read!.Pool.Count);
    }

    [Fact]
    public void The_run_hash_is_a_checkpoint_hash_of_the_run_file()
    {
        RunPreviewSet set = SuperStarPick();
        var checkpoint = new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = set.RunFile });
        Assert.Equal(checkpoint.Hash, set.RunHash);
        Assert.NotEqual(set.RunHash, RunPreviewSet.HashOf(RunFile("defense")));
    }

    [Fact]
    public void The_largest_set_fits_an_attachment()
    {
        RunStartSkater skater = new(EventChoiceSetTests.Agent(new string('i', EventChoiceSet.MaxNameBytes)), Look());
        RunStartReward reward = new() { Kind = RunStartRewardKind.Talent, Id = new string('t', EventChoiceSet.MaxNameBytes), Level = 99, Stickers = Enumerable.Repeat(new RunStartSticker(255, -999), RunStartReward.MaxStickers).ToList() };
        var largest = new RunPreviewSet
        {
            Mode = RunPreviewSet.FullSequence,
            RunFile = new byte[RunPreviewSet.MaxRunBytes],
            SuperStars = Enumerable.Repeat(skater, RunPreviewSet.MaxSkaters).ToList(),
            RandomSkaters = Enumerable.Repeat(skater, RunPreviewSet.MaxSkaters).ToList(),
            PowerUps = Enumerable.Repeat(reward, RunPreviewSet.MaxRewards).ToList(),
            Relics = Enumerable.Repeat(new RunStartReward { Kind = RunStartRewardKind.Relic, Id = "r" }, RunPreviewSet.MaxRewards).ToList(),
        };
        Assert.Null(largest.Problem());
        MenuAttachment attachment = MenuAttachment.RunPreview(5, largest);
        Assert.InRange(attachment.BodyLength, RunPreviewSet.MaxRunBytes, MenuAttachment.MaxBodyBytes);
        Assert.True(attachment.TryGetRunPreview(out RunPreviewSet? read));
        Assert.Equal(Encoded(largest), Encoded(read!));
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        RunStartSkater good = new(EventChoiceSetTests.Agent(), Look());
        yield return new object[] { "no run", SuperStarPick().With(run: Array.Empty<byte>()) };
        yield return new object[] { "a run over the cap", SuperStarPick().With(run: new byte[RunPreviewSet.MaxRunBytes + 1]) };
        yield return new object[] { "no menu", new RunPreviewSet { Menu = RunStartMenu.None, RunFile = RunFile() } };
        yield return new object[] { "an unknown menu", new RunPreviewSet { Menu = (RunStartMenu)3, RunFile = RunFile() } };
        yield return new object[] { "an unknown mode", new RunPreviewSet { Mode = 2, RunFile = RunFile() } };
        yield return new object[] { "a superstar pick with power-ups", new RunPreviewSet { Mode = RunPreviewSet.SuperStarOnly, RunFile = RunFile(), PowerUps = OldSchool().PowerUps } };
        yield return new object[] { "a superstar pick with random skaters", new RunPreviewSet { Mode = RunPreviewSet.SuperStarOnly, RunFile = RunFile(), RandomSkaters = new[] { good } } };
        yield return new object[] { "a lineup with offers", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), SuperStars = new[] { good } } };
        yield return new object[] { "a lineup with a mode", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, Mode = 1, RunFile = RunFile() } };
        yield return new object[] { "an old squad menu with benchwarmers", new RunPreviewSet { RunFile = RunFile(), Pool = Lineup().Pool } };
        yield return new object[] { "too many benchwarmers", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = Enumerable.Repeat(Lineup().Pool[0], RunPreviewSet.MaxPool + 1).ToList() } };
        yield return new object[] { "a missing benchwarmer", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = new RunStartBenchwarmer[] { null! } } };
        yield return new object[] { "a bench power with a line break", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = new[] { new RunStartBenchwarmer(new RunStartSkater(EventChoiceSetTests.Agent(), Look()), true, false, false, "a\nb") } } };
        yield return new object[] { "a benchwarmer with no id", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = new[] { new RunStartBenchwarmer(new RunStartSkater(EventChoiceSetTests.Agent(string.Empty), Look()), true, false, false) } } };
        yield return new object[] { "a benchwarmer's hidden offset out of range", new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = RunFile(), Pool = new[] { new RunStartBenchwarmer(new RunStartSkater(EventChoiceSetTests.Agent(), Look().With(hidden: -1000)), true, false, false) } } };
        yield return new object[] { "too many superstars", new RunPreviewSet { RunFile = RunFile(), SuperStars = Enumerable.Repeat(good, RunPreviewSet.MaxSkaters + 1).ToList() } };
        yield return new object[] { "too many relics", new RunPreviewSet { RunFile = RunFile(), Relics = Enumerable.Repeat(OldSchool().Relics[0], RunPreviewSet.MaxRewards + 1).ToList() } };
        yield return new object[] { "a missing skater", new RunPreviewSet { RunFile = RunFile(), SuperStars = new RunStartSkater[] { null! } } };
        yield return new object[] { "a skater with no id", new RunPreviewSet { RunFile = RunFile(), SuperStars = new[] { new RunStartSkater(EventChoiceSetTests.Agent(string.Empty), Look()) } } };
        yield return new object[] { "a hidden offset out of range", WithLook(Look().With(hidden: 1000)) };
        yield return new object[] { "a size offset that isn't a number", WithLook(Look().With(size: float.NaN)) };
        yield return new object[] { "an infinite number colour", WithLook(Look().With(number: new ColorValue(float.PositiveInfinity, 0, 0, 1))) };
        yield return new object[] { "glasses with a line break", WithLook(Look().With(glasses: "a\nb")) };
        yield return new object[] { "a negative height", WithLook(Look().With(height: -1)) };
        yield return new object[] { "a relic among the power-ups", new RunPreviewSet { RunFile = RunFile(), PowerUps = OldSchool().Relics } };
        yield return new object[] { "a talent among the relics", new RunPreviewSet { RunFile = RunFile(), Relics = OldSchool().PowerUps } };
        yield return new object[] { "a reward with no id", new RunPreviewSet { RunFile = RunFile(), Relics = new[] { new RunStartReward { Kind = RunStartRewardKind.Relic, Id = string.Empty } } } };
        yield return new object[] { "a reward level over 99", new RunPreviewSet { RunFile = RunFile(), Relics = new[] { new RunStartReward { Kind = RunStartRewardKind.Relic, Id = "r", Level = 100 } } } };
        yield return new object[] { "too many stickers", new RunPreviewSet { RunFile = RunFile(), Relics = new[] { new RunStartReward { Kind = RunStartRewardKind.Relic, Id = "r", Stickers = Enumerable.Repeat(new RunStartSticker(0, 1), RunStartReward.MaxStickers + 1).ToList() } } } };
        yield return new object[] { "a sticker value out of range", new RunPreviewSet { RunFile = RunFile(), Relics = new[] { new RunStartReward { Kind = RunStartRewardKind.Relic, Id = "r", Stickers = new[] { new RunStartSticker(0, 1000) } } } } };
        yield return new object[] { "a sticker stat out of range", new RunPreviewSet { RunFile = RunFile(), Relics = new[] { new RunStartReward { Kind = RunStartRewardKind.Relic, Id = "r", Stickers = new[] { new RunStartSticker(256, 1) } } } } };

        static RunPreviewSet WithLook(SkaterLook look) => new() { RunFile = RunFile(), SuperStars = new[] { new RunStartSkater(EventChoiceSetTests.Agent(), look) } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Sets_out_of_their_limits_are_refused_when_written_and_never_sent(string what, RunPreviewSet set)
    {
        Assert.NotNull(set.Problem());
        Assert.ThrowsAny<ArgumentException>(() => set.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.RunPreview(5, set));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Crafted_sets_that_break_a_rule_are_refused()
    {
        byte[] good = Encoded(SuperStarPick());
        Assert.NotNull(Read(good));

        // An unknown menu, an unknown mode, a run hash that isn't the run's.
        Assert.Null(Read(Changed(good, 0, 9)));
        Assert.Null(Read(Changed(good, 1, 7)));
        int hashAt = 3 + Varint(RunFile().Length) + RunFile().Length;
        Assert.Null(Read(Changed(good, hashAt, (byte)(good[hashAt] ^ 1))));

        // A run length past the cap, or past the message (the reader allocates nothing for it).
        var writer = new BitWriter();
        writer.WriteByte(1);
        writer.WriteByte(1);
        writer.WriteBool(true);
        writer.WriteVarUInt(RunPreviewSet.MaxRunBytes + 1);
        Assert.Null(Read(writer.ToArray()));
        writer = new BitWriter();
        writer.WriteByte(1);
        writer.WriteByte(1);
        writer.WriteBool(true);
        writer.WriteVarUInt(1000);
        writer.WriteBytes(new byte[10]);
        Assert.Null(Read(writer.ToArray()));

        // Cut short at every length; something after it.
        for (int length = 0; length < good.Length; length++)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));

        static byte[] Changed(byte[] bytes, int at, byte value)
        {
            byte[] copy = (byte[])bytes.Clone();
            copy[at] = value;
            return copy;
        }

        static int Varint(int value)
        {
            int length = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                length++;
            }

            return length;
        }
    }

    [Fact]
    public void An_attachment_of_a_set_round_trips_and_is_only_a_run_preview()
    {
        MenuAttachment sent = MenuAttachment.RunPreview(2, OldSchool());
        Assert.Equal((MenuAttachmentKind.RunPreview, MenuAttachment.RunPreviewFormat), (sent.Kind, sent.Format));
        Assert.Contains("run preview for #2", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetEventChoices(out _));
        Assert.False(sent.TryGetNpcLine(out _));
        Assert.False(sent.TryGetPrompt(out _));
        Assert.False(sent.TryGetRunSummary(out _));
        Assert.True(MenuAttachment.IsKnown(MenuAttachmentKind.RunPreview));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetRunPreview(out RunPreviewSet? set));
        Assert.Equal(Encoded(OldSchool()), Encoded(set!));
        Assert.True(sent.DeflatedLength < sent.BodyLength);

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.RunPreview(0, OldSchool()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.RunPreview(1, null!));
        Assert.False(MenuAttachment.NpcLine(13, NpcLineDataTests.Sample()).TryGetRunPreview(out _));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = { Encoded(SuperStarPick()), Encoded(OldSchool()), Encoded(new RunPreviewSet { Menu = RunStartMenu.Benchwarmer, RunFile = new byte[] { 1 } }), Encoded(Lineup()) };
        var random = new Random(55);
        int read = 0;
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            if (round % 5 == 4)
            {
                bytes = new byte[random.Next(0, 300)];
                random.NextBytes(bytes);
            }
            else
            {
                // Flips outside the run's bytes (a flip inside them fails the hash, which is tested above).
                for (int flips = random.Next(1, 3); flips > 0; flips--)
                {
                    int at = random.Next(bytes.Length);
                    bytes[at] ^= (byte)(1 << random.Next(8));
                }
            }

            var reader = new BitReader(bytes);
            if (!RunPreviewSet.TryRead(ref reader, out RunPreviewSet? set))
            {
                Assert.Null(set);
                continue;
            }

            read++;
            Assert.Null(set!.Problem());
            Assert.Equal(Encoded(set), Encoded(Read(Encoded(set))!));
            _ = set.ToString();
        }

        Assert.True(read > 0, "some changed sets should still read");
    }

    [Fact]
    public void The_skater_comparer_names_the_first_field_that_differs()
    {
        FreeAgentData host = EventChoiceSetTests.Agent();
        Assert.Null(SkaterModelDiff.FirstDifference(host, EventChoiceSetTests.Agent()));
        Assert.Equal("speed (70 here, -2 on the host's)", SkaterModelDiff.FirstDifference(host, Copy(host, speed: 70)));
        Assert.Contains("talent 2's contract games (4 here, 3 on the host's)", SkaterModelDiff.FirstDifference(host, Copy(host, contract: 4)), StringComparison.Ordinal);
        Assert.StartsWith("headSkin", SkaterModelDiff.FirstDifference(host, Copy(host, headSkin: "other")), StringComparison.Ordinal);
        Assert.StartsWith("colour schemes' jerseyScheme (primary)", SkaterModelDiff.FirstDifference(host, Copy(host, red: 0.5f)), StringComparison.Ordinal);
        Assert.StartsWith("perk text key", SkaterModelDiff.FirstDifference(host, Copy(host, perk: null)), StringComparison.Ordinal);
        Assert.Null(SkaterModelDiff.FirstDifference(host, Copy(host, perk: null), ignoreFlavor: true));

        // An unset skin is the same whether the game left it empty or null.
        Assert.Null(SkaterModelDiff.FirstDifference(Copy(host, headSkin: string.Empty), Copy(host, headSkin: null)));

        SkaterLook look = Look();
        Assert.Null(SkaterModelDiff.FirstDifference(look, Look()));
        Assert.Equal("hidden speed (5 here, -2 on the host's)", SkaterModelDiff.FirstDifference(look, Look().With(hiddenSpeed: 5)));
        Assert.StartsWith("stick colour", SkaterModelDiff.FirstDifference(look, Look().With(noStick: true)), StringComparison.Ordinal);
        Assert.StartsWith("height", SkaterModelDiff.FirstDifference(look, Look().With(height: 70)), StringComparison.Ordinal);

        static FreeAgentData Copy(FreeAgentData from, int? speed = null, int? contract = null, string? headSkin = "keep", float? red = null, string? perk = "keep")
        {
            var skins = from.Skins.ToList();
            if (headSkin != "keep")
            {
                skins[1] = headSkin;
            }

            var colors = from.Colors!.ToList();
            if (red is float r)
            {
                ColorSchemeData first = colors[0]!;
                colors[0] = first with { Primary = first.Primary with { R = r } };
            }

            var talents = from.Talents.ToList();
            if (contract is int games)
            {
                talents[1] = talents[1] with { ContractGames = games };
            }

            return new FreeAgentData
            {
                Id = from.Id, OriginalId = from.OriginalId, Name = from.Name, FirstName = from.FirstName, LastName = from.LastName, Number = from.Number,
                Checking = from.Checking, Speed = speed ?? from.Speed, ShotAccuracy = from.ShotAccuracy, ShotPower = from.ShotPower, InjuryCounter = from.InjuryCounter,
                IsLefty = from.IsLefty, IsBlack = from.IsBlack, HasOversizedHelmet = from.HasOversizedHelmet, ForwardRarity = from.ForwardRarity, SkaterSize = from.SkaterSize,
                DefaultSkaterType = from.DefaultSkaterType, SkaterCategory = from.SkaterCategory, Skins = skins, Colors = colors, AbilityId = from.AbilityId,
                AbilityLevel = from.AbilityLevel, Talents = talents, FlavorKey = from.FlavorKey, PerkFlavorKey = perk == "keep" ? from.PerkFlavorKey : perk, PowerUpTextKey = from.PowerUpTextKey,
            };
        }
    }
}

/// <summary>Copies of test data with one field changed.</summary>
internal static class RunPreviewTestCopies
{
    public static RunPreviewSet With(this RunPreviewSet set, byte[] run) => new()
    {
        Menu = set.Menu,
        Mode = set.Mode,
        IsTeamHome = set.IsTeamHome,
        RunFile = run,
        SuperStars = set.SuperStars,
        RandomSkaters = set.RandomSkaters,
        PowerUps = set.PowerUps,
        Relics = set.Relics,
        Pool = set.Pool,
    };

    public static SkaterLook With(this SkaterLook look, int? hidden = null, int? hiddenSpeed = null, float? size = null, ColorValue? number = null, string? glasses = "keep",
        int? height = null, bool noStick = false) => new()
    {
        HiddenShotPower = hidden ?? look.HiddenShotPower,
        HiddenSpeed = hiddenSpeed ?? look.HiddenSpeed,
        HiddenChecking = look.HiddenChecking,
        HiddenShotAccuracy = look.HiddenShotAccuracy,
        IsFemale = look.IsFemale,
        HasGoldenHelmet = look.HasGoldenHelmet,
        GlassesSkin = glasses == "keep" ? look.GlassesSkin : glasses,
        HelmetAwaySkin = look.HelmetAwaySkin,
        SizeOffset = size ?? look.SizeOffset,
        NumberColor = number ?? look.NumberColor,
        StickColor = noStick ? null : look.StickColor,
        SkatesColor = look.SkatesColor,
        HeightInches = height ?? look.HeightInches,
        WeightKg = look.WeightKg,
    };
}
