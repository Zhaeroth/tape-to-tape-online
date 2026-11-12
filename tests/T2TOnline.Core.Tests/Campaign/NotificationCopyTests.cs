using System;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class NotificationCopyTests
{
    /// <summary>A relic trigger's toast (the game's GameStats.PushRelicNotification): the relic's name, icon and description.</summary>
    public static NotificationCopy Relic() => new()
    {
        Type = 100,
        Timing = 4,
        Team = ToastTeam.Home,
        IsTeamHome = true,
        ObjectKind = ToastObjectKind.Relic,
        ObjectId = "BloodRelic",
        ObjectLevel = 2,
        ObjectFromData = false,
        Title = new ToastText(ToastTextKind.ObjectName, "Blood"),
        TitleIcon = new ToastImage(ToastImageKind.ObjectIcon, "relic_blood"),
        Description = new ToastText(ToastTextKind.ObjectDescription, "Your skaters get +1 <b>Power</b> after a fight."),
    };

    /// <summary>An injury toast: a skater, the injury and its games, the title a term, texts and images of each kind.</summary>
    public static NotificationCopy Injury() => new()
    {
        Type = 0,
        Timing = 1,
        Team = ToastTeam.Run,
        SkaterId = "fwd-kovalski",
        InjuryType = 3,
        InjuryGames = -1,
        Title = new ToastText(ToastTextKind.Term, "Blessure !", "v2ui/notifications/injury_title"),
        Description = new ToastText(ToastTextKind.Literal, "Kovalski est blessé (2 matchs)\nÇa fait mal."),
        CustomTexts = new[] { new ToastText(ToastTextKind.Literal, "+2"), new ToastText(ToastTextKind.Term, "Puissance", "stats/power") },
        CustomImages = new[] { new ToastImage(ToastImageKind.Named, "icon_power"), new ToastImage(ToastImageKind.Named, "icon_speed") },
    };

    /// <summary>
    /// An injury toast as the game makes it (NotificationPusher.OnNewInjury): the title a term, the description a term formatted
    /// with the injury's name and the other games, and custom texts of each recipe kind.
    /// </summary>
    public static NotificationCopy WithRecipes() => new()
    {
        Type = 0,
        Timing = 1,
        Team = ToastTeam.Run,
        SkaterId = "fwd-kovalski",
        InjuryType = 2,
        InjuryGames = 3,
        Title = new ToastText(ToastTextKind.Term, "New Injury", "v2ui/injury_toast/title"),
        Description = new ToastText(ToastTextKind.Recipe, "Injured Legs for this game and 2 other games",
            Recipe: TextRecipe.Format(TextRecipe.Term("v2ui/injury_toast/desc"), TextRecipe.InjuryName(2), TextRecipe.Literal("2"))),
        CustomTexts = new[]
        {
            new ToastText(ToastTextKind.Recipe, "\t+2 <keyword=\"speed\">Speed</keyword>", Recipe: TextRecipe.Format(TextRecipe.Literal("\t+{0} {1}"), TextRecipe.Literal("2"), TextRecipe.LocalizedStatName(1))),
            new ToastText(ToastTextKind.Recipe, "Bean Diet", Recipe: TextRecipe.AssetName(ToastObjectKind.Relic, "bean_diet", 2)),
            new ToastText(ToastTextKind.Recipe, "Ice Bears wins by mercy", Recipe: TextRecipe.Format(TextRecipe.Term("v2ui/mercy/notification/desc"), TextRecipe.TeamName(ToastTeam.Run))),
            new ToastText(ToastTextKind.Literal, "+2"),
        },
    };

    /// <summary>A copy at every limit: the longest texts of three-byte characters, the most texts and images.</summary>
    public static NotificationCopy Largest()
    {
        string longest = new('€', NotificationCopy.MaxTextChars);
        string name = new('n', 42); // 42 three-byte characters are over a name's 128 bytes; 128 one-byte ones fit
        return new NotificationCopy
        {
            Type = NotificationCopy.MaxType,
            Timing = -1,
            Team = ToastTeam.Visitor,
            SkaterId = new string('s', 128),
            InjuryType = NotificationCopy.MaxInjuryType,
            InjuryGames = NotificationCopy.MaxInjuryGames,
            ObjectKind = ToastObjectKind.Ability,
            ObjectId = new string('a', 128),
            ObjectLevel = NotificationCopy.MaxLevel,
            ObjectFromData = true,
            Title = new ToastText(ToastTextKind.Literal, longest),
            TitleIcon = new ToastImage(ToastImageKind.Named, name),
            Description = new ToastText(ToastTextKind.Literal, longest),
            CustomTexts = Enumerable.Range(0, NotificationCopy.MaxTexts).Select(_ => new ToastText(ToastTextKind.Literal, longest)).ToList(),
            CustomImages = Enumerable.Range(0, NotificationCopy.MaxImages).Select(_ => new ToastImage(ToastImageKind.Named, new string('i', 128))).ToList(),
        };
    }

    public static byte[] Encoded(NotificationEvent notification)
    {
        var writer = new BitWriter();
        Assert.True(NotificationMessage.Write(writer, notification));
        return writer.ToArray();
    }

    private static NotificationEvent? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = NotificationMessage.TryRead(ref reader, out NotificationEvent? read);
        Assert.Equal(ok, read != null);
        return ok && reader.RemainingBits < 8 ? read : null;
    }

    /// <summary>A text as plain values (a recipe by its words: two recipes are two objects).</summary>
    private static (ToastTextKind, string, string?, string?)? Shape(ToastText? text) => text == null ? null : (text.Kind, text.Value, text.Term, text.Recipe?.ToString());

    private static void SameCopy(NotificationCopy expected, NotificationCopy actual)
    {
        Assert.Equal((expected.Type, expected.Timing, expected.Team, expected.IsTeamHome, expected.SkaterId), (actual.Type, actual.Timing, actual.Team, actual.IsTeamHome, actual.SkaterId));
        Assert.Equal((expected.InjuryType, expected.InjuryGames), (actual.InjuryType, actual.InjuryGames));
        Assert.Equal((expected.ObjectKind, expected.ObjectId, expected.ObjectLevel, expected.ObjectFromData), (actual.ObjectKind, actual.ObjectId, actual.ObjectLevel, actual.ObjectFromData));
        Assert.Equal((Shape(expected.Title), expected.TitleIcon, Shape(expected.Description)), (Shape(actual.Title), actual.TitleIcon, Shape(actual.Description)));
        Assert.Equal(expected.CustomTexts.Select(Shape), actual.CustomTexts.Select(Shape));
        Assert.Equal(expected.CustomImages, actual.CustomImages);
    }

    [Fact]
    public void Copies_round_trip_field_by_field()
    {
        foreach (NotificationCopy copy in new[] { Relic(), Injury(), Largest(), WithRecipes(), new NotificationCopy { Type = 4 } })
        {
            NotificationEvent read = Read(Encoded(NotificationEvent.Queued(copy)))!;
            Assert.NotNull(read);
            Assert.Equal(NotificationOp.Queue, read.Op);
            SameCopy(copy, read.Copy!);
        }
    }

    [Fact]
    public void Recipe_texts_round_trip_with_their_recipes_and_the_hosts_text()
    {
        NotificationCopy sent = WithRecipes();
        Assert.Null(sent.Problem());
        NotificationEvent read = Read(Encoded(NotificationEvent.Queued(sent)))!;
        Assert.NotNull(read);
        Assert.Equal(Encoded(NotificationEvent.Queued(sent)), Encoded(read));
        NotificationCopy copy = read.Copy!;
        Assert.Equal(ToastTextKind.Recipe, copy.Description!.Kind);
        Assert.Equal(sent.Description!.Value, copy.Description.Value);
        Assert.Equal(sent.Description.Recipe!.ToString(), copy.Description.Recipe!.ToString());
        Assert.Equal(sent.CustomTexts.Select(t => t.Recipe?.ToString()), copy.CustomTexts.Select(t => t.Recipe?.ToString()));
        Assert.Equal(new[] { ToastTextKind.Recipe, ToastTextKind.Recipe, ToastTextKind.Recipe, ToastTextKind.Literal }, copy.CustomTexts.Select(t => t.Kind));
        Assert.Equal("a recipe", copy.Description.How);
        Assert.Contains("description (a recipe) format(term \"v2ui/injury_toast/desc\"", sent.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Injured Legs", sent.ToString(), StringComparison.Ordinal);
        Assert.Equal(ToastTextKind.Term, copy.Title!.Kind);

        // The friend's game makes it in its own language from the recipe, with the term and the names of its own game.
        Assert.Equal("<de:term:v2ui/injury_toast/desc|de:injury:2|2>", copy.Description.Recipe.Evaluate(new FakeTexts("de")));
        Assert.Equal("de:asset:Relic:bean_diet:2", copy.CustomTexts[1].Recipe!.Evaluate(new FakeTexts("de")));
    }

    [Fact]
    public void Pops_and_clears_round_trip()
    {
        Assert.Equal(NotificationEvent.Popped(0x40, false), Read(Encoded(NotificationEvent.Popped(0x40, false))));
        Assert.Equal(NotificationEvent.Popped(-1, true), Read(Encoded(NotificationEvent.Popped(-1, true))));
        Assert.Equal(NotificationEvent.Cleared(), Read(Encoded(NotificationEvent.Cleared())));
    }

    [Fact]
    public void The_largest_copy_fits_a_message()
    {
        byte[] bytes = Encoded(NotificationEvent.Queued(Largest()));
        Assert.True(bytes.Length <= NotificationMessage.MaxBytes, $"{bytes.Length} bytes");
    }

    [Fact]
    public void Copies_out_of_range_are_refused_when_written()
    {
        string tooLong = new('x', NotificationCopy.MaxTextChars + 1);
        NotificationCopy[] bad =
        {
            new() { Type = -1 },
            new() { Type = NotificationCopy.MaxType + 1 },
            new() { Team = (ToastTeam)9 },
            new() { InjuryType = -1 },
            new() { InjuryType = 1, InjuryGames = -2 },
            new() { InjuryType = 1, InjuryGames = NotificationCopy.MaxInjuryGames + 1 },
            new() { ObjectKind = (ToastObjectKind)7, ObjectId = "x" },
            new() { ObjectKind = ToastObjectKind.Relic, ObjectId = string.Empty },
            new() { ObjectKind = ToastObjectKind.Relic, ObjectId = "x", ObjectLevel = NotificationCopy.MaxLevel + 1 },
            new() { ObjectId = "x" },
            new() { ObjectFromData = true },
            new() { SkaterId = "a\u0001b" },
            new() { SkaterId = new string('s', 129) },
            new() { Title = new ToastText(ToastTextKind.Literal, tooLong) },
            new() { Title = new ToastText(ToastTextKind.None, "x") },
            new() { Description = new ToastText((ToastTextKind)9, "x") },
            new() { Title = new ToastText(ToastTextKind.Term, "Two lines", "two\nlines") },
            new() { Title = new ToastText(ToastTextKind.Term, "No term") },
            new() { Title = new ToastText(ToastTextKind.Literal, "A literal", "with/a/term") },
            new() { TitleIcon = new ToastImage(ToastImageKind.None, "x") },
            new() { TitleIcon = new ToastImage(ToastImageKind.Named, string.Empty) },
            new() { CustomTexts = Enumerable.Range(0, NotificationCopy.MaxTexts + 1).Select(_ => new ToastText(ToastTextKind.Literal, "t")).ToList() },
            new() { CustomImages = Enumerable.Range(0, NotificationCopy.MaxImages + 1).Select(_ => new ToastImage(ToastImageKind.Named, "i")).ToList() },
            new() { CustomTexts = new[] { new ToastText(ToastTextKind.Literal, "bad\u0007") } },
            new() { Title = new ToastText(ToastTextKind.Recipe, "No recipe") },
            new() { Title = new ToastText(ToastTextKind.Literal, "A literal", Recipe: TextRecipe.Literal("x")) },
            new() { Title = new ToastText(ToastTextKind.Term, "A term", "a/term", TextRecipe.Literal("x")) },
            new() { Title = new ToastText(ToastTextKind.Recipe, "Out of limits", Recipe: TextRecipe.Concat()) },
            new() { Description = new ToastText(ToastTextKind.Recipe, "A team of none", Recipe: TextRecipe.TeamName(ToastTeam.None)) },
            new() { CustomTexts = new[] { new ToastText(ToastTextKind.Recipe, "An asset of no kind", Recipe: TextRecipe.AssetName(ToastObjectKind.None, "x", 1)) } },
            new() { CustomTexts = new[] { new ToastText(ToastTextKind.Recipe, "x".PadRight(NotificationCopy.MaxTextChars + 1, 'x'), Recipe: TextRecipe.Literal("x")) } },
        };
        foreach (NotificationCopy copy in bad)
        {
            Assert.NotNull(copy.Problem());
            Assert.Throws<ArgumentException>(() => copy.Write(new BitWriter()));
            Assert.False(NotificationMessage.Write(new BitWriter(), NotificationEvent.Queued(copy)));
        }

        Assert.False(NotificationMessage.Write(new BitWriter(), new NotificationEvent(NotificationOp.None, null, 0, false)));
        Assert.False(NotificationMessage.Write(new BitWriter(), new NotificationEvent(NotificationOp.Queue, null, 0, false)));
    }

    [Fact]
    public void Messages_over_their_limits_are_refused_when_read()
    {
        // Each limit broken on the wire by hand, after a good start: too many texts, too many images, a type out of range,
        // an unknown op, an unknown team, a body over the message limit.
        NotificationEvent minimal = Read(Minimal(NotificationCopy.MaxTexts, NotificationCopy.MaxImages))!;
        Assert.Equal((NotificationCopy.MaxTexts, NotificationCopy.MaxImages), (minimal.Copy!.CustomTexts.Count, minimal.Copy.CustomImages.Count));
        Assert.Null(Read(Minimal(NotificationCopy.MaxTexts + 1, 0)));
        Assert.Null(Read(Minimal(0, NotificationCopy.MaxImages + 1)));
        Assert.Null(Read(Write(w =>
        {
            w.WriteByte((byte)NotificationOp.Queue);
            w.WriteVarInt(NotificationCopy.MaxType + 1);
        })));
        Assert.Null(Read(new byte[] { 9 }));
        Assert.Null(Read(new byte[] { 0 }));
        byte[] team = Encoded(NotificationEvent.Queued(new NotificationCopy { Type = 4, Team = ToastTeam.Run }));
        team[1 + 1 + 4] = 9; // op, varint type 4, u32 timing, then the team
        Assert.Null(Read(team));
        byte[] huge = new byte[NotificationMessage.MaxBytes + 2];
        Array.Copy(Encoded(NotificationEvent.Cleared()), huge, 1);
        Assert.Null(Read(huge));
    }

    [Fact]
    public void A_copy_whose_recipes_overflow_a_message_is_refused_when_written_not_by_the_friend()
    {
        // Each recipe within its limits (a format with 8 arguments of 256 bytes is 2 KB), and ten of them over the message's 16 KB.
        TextRecipe wide = TextRecipe.Format(TextRecipe.Literal(new string('f', TextRecipe.MaxTextBytes)),
            Enumerable.Repeat(TextRecipe.Literal(new string('a', TextRecipe.MaxTextBytes)), TextRecipe.MaxArgs).ToArray());
        NotificationCopy copy = new()
        {
            Type = 100,
            Description = new ToastText(ToastTextKind.Recipe, "x", Recipe: wide),
            CustomTexts = Enumerable.Range(0, NotificationCopy.MaxTexts).Select(_ => new ToastText(ToastTextKind.Recipe, "x", Recipe: wide)).ToList(),
        };
        Assert.Null(copy.Problem());
        Assert.False(NotificationMessage.Write(new BitWriter(), NotificationEvent.Queued(copy)));

        // Fewer of them fit, and are read.
        NotificationCopy fits = new()
        {
            Type = 100,
            CustomTexts = Enumerable.Range(0, 3).Select(_ => new ToastText(ToastTextKind.Recipe, "x", Recipe: wide)).ToList(),
        };
        Assert.True(Read(Encoded(NotificationEvent.Queued(fits))) != null);
    }

    [Fact]
    public void Crafted_recipe_texts_that_break_a_rule_are_refused()
    {
        static byte[] Description(Action<BitWriter> recipe) => Write(w =>
        {
            w.WriteByte((byte)NotificationOp.Queue);
            w.WriteVarInt(4);
            w.WriteUInt32(0);
            w.WriteByte((byte)ToastTeam.None);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteByte((byte)ToastObjectKind.None);
            w.WriteByte((byte)ToastTextKind.None);
            w.WriteByte((byte)ToastImageKind.None);
            w.WriteByte((byte)ToastTextKind.Recipe);
            w.WriteBool(true);
            w.WriteVarUInt(1);
            w.WriteBytes("x"u8);
            recipe(w);
            w.WriteVarUInt(0);
            w.WriteVarUInt(0);
        });

        Assert.NotNull(Read(Description(w => TextRecipe.Literal("x").Write(w))));
        Assert.Null(Read(Description(w => w.WriteByte(14)))); // an op this version doesn't have
        Assert.Null(Read(Description(w => w.WriteByte(0)))); // no op
        Assert.Null(Read(Description(w => w.WriteByte((byte)TextOp.TeamName)))); // cut short
        Assert.Null(Read(Description(w =>
        {
            w.WriteByte((byte)TextOp.TeamName);
            w.WriteByte(0); // no team
        })));
        Assert.Null(Read(Description(w =>
        {
            w.WriteByte((byte)TextOp.AssetName);
            w.WriteByte((byte)ToastObjectKind.None);
            w.WriteVarUInt(1);
            w.WriteBytes("x"u8);
            w.WriteVarInt(1);
        })));
    }

    [Fact]
    public void Truncated_messages_are_refused()
    {
        foreach (NotificationEvent sample in new[] { NotificationEvent.Queued(Relic()), NotificationEvent.Queued(Injury()), NotificationEvent.Queued(WithRecipes()), NotificationEvent.Popped(4, true) })
        {
            byte[] bytes = Encoded(sample);
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.Null(Read(bytes.AsSpan(0, length).ToArray()));
            }
        }
    }

    [Fact]
    public void Fuzzed_messages_never_throw()
    {
        var random = new Random(88);
        byte[][] seeds =
        {
            Encoded(NotificationEvent.Queued(Relic())), Encoded(NotificationEvent.Queued(Injury())), Encoded(NotificationEvent.Queued(Largest())),
            Encoded(NotificationEvent.Queued(WithRecipes())), Encoded(NotificationEvent.Popped(2, true)), Encoded(NotificationEvent.Cleared()),
        };
        for (int i = 0; i < 20_000; i++)
        {
            byte[] bytes = (byte[])seeds[random.Next(seeds.Length)].Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips && bytes.Length > 0; f++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            if (random.Next(4) == 0)
            {
                Array.Resize(ref bytes, random.Next(bytes.Length + 1));
            }

            var reader = new BitReader(bytes);
            if (NotificationMessage.TryRead(ref reader, out NotificationEvent? read) && read!.Copy != null)
            {
                // Whatever reads is in range, and writes again.
                Assert.Null(read.Copy.Problem());
                _ = Encoded(read);
            }
        }
    }

    [Fact]
    public void Each_players_own_toasts_are_unlocks_bench_quests_and_the_benchwarmers_at_a_runs_end()
    {
        const int Generic = 100;
        Assert.True(ToastOwner.IsEachPlayersOwn(ToastOwner.UnlockType, 8));
        Assert.True(ToastOwner.IsEachPlayersOwn(Generic, ToastOwner.BenchQuestTiming));

        // The bench snapshot service's two toast makers: Generic, for the title screen.
        Assert.True(ToastOwner.IsEachPlayersOwn(Generic, ToastOwner.TitleScreenTiming));

        // The run's toasts that friends see as the host's: a relic's after a goal, an upgrade's on the map, mercy's at the end.
        Assert.False(ToastOwner.IsEachPlayersOwn(Generic, 4));
        Assert.False(ToastOwner.IsEachPlayersOwn(Generic, 2));
        Assert.False(ToastOwner.IsEachPlayersOwn(Generic, 0x20));
        Assert.False(ToastOwner.IsEachPlayersOwn(0, 1));
    }

    [Fact]
    public void A_friend_s_cap_takes_a_burst_from_a_link_catching_up_but_not_a_storm()
    {
        var throttle = new ToastThrottle(16, 64);
        long now = 5_000_000;
        Assert.Equal(64, Enumerable.Range(0, 200).Count(_ => throttle.TryTake(now)));
        Assert.Equal(136, throttle.Dropped);

        // 16 a second after that, and never more than the burst saved up.
        Assert.True(throttle.TryTake(now + 62_500));
        Assert.False(throttle.TryTake(now + 62_500));
        Assert.Equal(64, Enumerable.Range(0, 200).Count(_ => throttle.TryTake(now + 600_000_000)));
    }

    [Fact]
    public void The_throttle_lets_eight_a_second_through()
    {
        var throttle = new ToastThrottle();
        long now = 5_000_000;
        int sent = Enumerable.Range(0, 20).Count(_ => throttle.TryTake(now));
        Assert.Equal(ToastThrottle.PerSecond, sent);
        Assert.Equal(12, throttle.Dropped);

        // One more every eighth of a second; never more than a second's worth saved up.
        Assert.False(throttle.TryTake(now + 100_000));
        Assert.True(throttle.TryTake(now + 125_000));
        Assert.Equal(ToastThrottle.PerSecond, Enumerable.Range(0, 20).Count(_ => throttle.TryTake(now + 60_000_000)));

        // A clock that goes back doesn't refill it.
        Assert.False(throttle.TryTake(now));
        throttle.Reset();
        Assert.Equal(0, throttle.Dropped);
        Assert.True(throttle.TryTake(0));
    }

    [Fact]
    public void The_tally_counts_by_type()
    {
        var tally = new ToastTally();
        Assert.Equal("none", tally.ToString());
        Assert.True(tally.Add("Generic"));
        Assert.False(tally.Add("Generic"));
        Assert.True(tally.Add("Injury"));
        Assert.True(tally.Add(string.Empty));
        Assert.Equal(4, tally.Total);
        Assert.Equal("? 1, Generic 2, Injury 1", tally.ToString());
        tally.Clear();
        Assert.Equal((0, "none"), (tally.Total, tally.ToString()));
    }

    [Fact]
    public void Text_kinds_say_where_they_come_from()
    {
        Assert.Equal("a term", new ToastText(ToastTextKind.Term, "x", "t").How);
        Assert.Equal("its object's name", new ToastText(ToastTextKind.ObjectName, "x").How);
        Assert.Equal("its object's description", new ToastText(ToastTextKind.ObjectDescription, "x").How);
        Assert.Equal("the host's text", new ToastText(ToastTextKind.Literal, "x").How);
        Assert.Equal("a recipe", new ToastText(ToastTextKind.Recipe, "x", Recipe: TextRecipe.Literal("x")).How);
        Assert.Contains("relic BloodRelic", Relic().ToString(), StringComparison.Ordinal);
        Assert.Contains("injury 3 for -1 game(s)", Injury().ToString(), StringComparison.Ordinal);
    }

    private static byte[] Write(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    /// <summary>A minimal copy's message written by hand, with <paramref name="texts"/> custom texts and <paramref name="images"/> images.</summary>
    private static byte[] Minimal(int texts, int images) => Write(w =>
    {
        w.WriteByte((byte)NotificationOp.Queue);
        w.WriteVarInt(4); // type
        w.WriteUInt32(0); // timing
        w.WriteByte((byte)ToastTeam.None);
        w.WriteBool(false); // home
        w.WriteBool(false); // no skater
        w.WriteBool(false); // no injury
        w.WriteByte((byte)ToastObjectKind.None);
        w.WriteByte((byte)ToastTextKind.None); // title
        w.WriteByte((byte)ToastImageKind.None); // icon
        w.WriteByte((byte)ToastTextKind.None); // description
        w.WriteVarUInt((ulong)texts);
        for (int i = 0; i < texts; i++)
        {
            w.WriteByte((byte)ToastTextKind.Literal);
            w.WriteBool(true);
            w.WriteVarUInt(1);
            w.WriteByte((byte)'t');
        }

        w.WriteVarUInt((ulong)images);
        for (int i = 0; i < images; i++)
        {
            w.WriteByte((byte)ToastImageKind.Named);
            w.WriteVarUInt(1);
            w.WriteByte((byte)'i');
        }
    });
}
