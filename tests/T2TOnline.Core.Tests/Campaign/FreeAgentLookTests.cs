using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A free agent's look (<see cref="FreeAgentData.Version2"/>): what a General Manager's card and preview show besides the model.</summary>
public class FreeAgentLookTests
{
    public static FreeAgentData WithLook() => EventChoiceSetTests.Agent().WithLook(RunPreviewSetTests.Look(), new DateDmy(29, 2, 1996), 2);

    private static byte[] Encoded(FreeAgentData agent, int version)
    {
        var writer = new BitWriter();
        agent.Write(writer, version);
        return writer.ToArray();
    }

    private static FreeAgentData? Read(byte[] bytes, int version)
    {
        var reader = new BitReader(bytes);
        bool ok = FreeAgentData.TryRead(ref reader, version, out FreeAgentData? agent);
        Assert.Equal(ok, agent != null);
        return ok && reader.RemainingBits < 8 ? agent : null;
    }

    [Fact]
    public void Version_2_carries_the_look_birth_date_and_number_format()
    {
        FreeAgentData sent = WithLook();
        FreeAgentData read = Read(Encoded(sent, FreeAgentData.Version2), FreeAgentData.Version2)!;
        Assert.NotNull(read);
        Assert.Null(SkaterModelDiff.FirstDifference(sent, read));
        Assert.Null(SkaterModelDiff.FirstDifference(sent.Look!, read.Look!));
        Assert.Equal((new DateDmy(29, 2, 1996), 2), (read.BirthDate, read.NumberFormat));
        Assert.Equal((true, true, "glasses_round", string.Empty, 74, 92), (read.Look!.IsFemale, read.Look.HasGoldenHelmet, read.Look.GlassesSkin, read.Look.HelmetAwaySkin, read.Look.HeightInches, read.Look.WeightKg));
        Assert.Equal(Encoded(sent, FreeAgentData.Version2), Encoded(read, FreeAgentData.Version2));
        Assert.Equal("29.2.1996", read.BirthDate.ToString());

        // Every model field stays as it was (WithLook copies it).
        Assert.Null(SkaterModelDiff.FirstDifference(EventChoiceSetTests.Agent(), StripLook(read)));
    }

    [Fact]
    public void Version_1_is_the_run_start_carriers_layout_and_leaves_the_look_out()
    {
        FreeAgentData agent = EventChoiceSetTests.Agent();
        byte[] v1 = Encoded(agent, FreeAgentData.Version1);
        var writer = new BitWriter();
        agent.Write(writer);
        Assert.Equal(v1, writer.ToArray());

        // An agent without a look in version 2 is version 1's bytes and one flag.
        byte[] v2 = Encoded(agent, FreeAgentData.Version2);
        Assert.InRange(v2.Length, v1.Length, v1.Length + 1);
        Assert.Null(Read(v2, FreeAgentData.Version2)!.Look);

        // Written in version 1, a look isn't there to read.
        FreeAgentData read = Read(Encoded(WithLook(), FreeAgentData.Version1), FreeAgentData.Version1)!;
        Assert.Null(read.Look);
        Assert.Equal((default(DateDmy), 0), (read.BirthDate, read.NumberFormat));
    }

    [Fact]
    public void A_looks_difference_is_named()
    {
        FreeAgentData host = WithLook();
        Assert.Contains("look (none here", SkaterModelDiff.FirstDifference(host, StripLook(host)), StringComparison.Ordinal);
        Assert.Contains("birth date (1.1.2000 here, 29.2.1996 on the host's)",
            SkaterModelDiff.FirstDifference(host, host.WithLook(host.Look!, new DateDmy(1, 1, 2000), host.NumberFormat)), StringComparison.Ordinal);
        Assert.Contains("number format (0 here, 2 on the host's)", SkaterModelDiff.FirstDifference(host, host.WithLook(host.Look!, host.BirthDate, 0)), StringComparison.Ordinal);
        Assert.Contains("hidden shot power (0 here, 3 on the host's)", SkaterModelDiff.FirstDifference(host, host.WithLook(SkaterLook.Default, host.BirthDate, host.NumberFormat)), StringComparison.Ordinal);
        Assert.Null(SkaterModelDiff.FirstDifference(EventChoiceSetTests.Agent(), EventChoiceSetTests.Agent()));
        Assert.Throws<ArgumentNullException>(() => host.WithLook(null!, default, 0));
    }

    public static IEnumerable<object[]> OutOfRange()
    {
        FreeAgentData agent = EventChoiceSetTests.Agent();
        SkaterLook look = RunPreviewSetTests.Look();
        yield return new object[] { "day 32", agent.WithLook(look, new DateDmy(32, 1, 2000), 0) };
        yield return new object[] { "month 13", agent.WithLook(look, new DateDmy(1, 13, 2000), 0) };
        yield return new object[] { "year 10000", agent.WithLook(look, new DateDmy(1, 1, 10000), 0) };
        yield return new object[] { "a negative day", agent.WithLook(look, new DateDmy(-1, 1, 2000), 0) };
        yield return new object[] { "number format 16", agent.WithLook(look, default, FreeAgentData.MaxNumberFormat + 1) };
        yield return new object[] { "a negative number format", agent.WithLook(look, default, -1) };
        yield return new object[] { "a hidden stat over 999", agent.WithLook(new SkaterLook { HiddenSpeed = 1000 }, default, 0) };
        yield return new object[] { "glasses with a line break", agent.WithLook(new SkaterLook { GlassesSkin = "a\nb" }, default, 0) };
        yield return new object[] { "a birth date without a look", new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", BirthDate = new DateDmy(1, 1, 1) } };
        yield return new object[] { "a number format without a look", new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", NumberFormat = 1 } };
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Looks_out_of_range_are_refused_in_a_set(string what, FreeAgentData agent)
    {
        var set = new EventChoiceSet { EventClass = "GeneralManagerEvent", EventName = "GM", NodeType = 8, Choices = new[] { new EventChoiceData { NodeType = 8, ChoiceType = 1, FreeAgent = agent } } };
        Assert.NotNull(set.Problem());
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.EventChoices(1, set));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Looks_at_their_limits_are_sent_and_read()
    {
        string name = new('n', EventChoiceSet.MaxNameBytes);
        var look = new SkaterLook
        {
            HiddenShotPower = -999,
            HiddenSpeed = 999,
            HiddenChecking = -999,
            HiddenShotAccuracy = 999,
            IsFemale = true,
            HasGoldenHelmet = true,
            GlassesSkin = name,
            HelmetAwaySkin = name,
            SizeOffset = 1000,
            NumberColor = new ColorValue(-1000, 1000, 0, 1),
            StickColor = new ColorSchemeData(new ColorValue(1000, 0, 0, 1), default, default),
            SkatesColor = new ColorSchemeData(default, default, new ColorValue(0, 0, -1000, 0)),
            HeightInches = 999,
            WeightKg = 9999,
        };
        FreeAgentData agent = EventChoiceSetTests.Agent().WithLook(look, new DateDmy(31, 12, 9999), FreeAgentData.MaxNumberFormat);
        FreeAgentData read = Read(Encoded(agent, FreeAgentData.Version2), FreeAgentData.Version2)!;
        Assert.Null(SkaterModelDiff.FirstDifference(agent, read));
    }

    [Fact]
    public void A_version_2_agent_cut_short_is_refused_at_every_length()
    {
        byte[] bytes = Encoded(WithLook(), FreeAgentData.Version2);
        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.Null(Read(bytes[..length], FreeAgentData.Version2));
        }

        Assert.NotNull(Read(bytes, FreeAgentData.Version2));
    }

    private static FreeAgentData StripLook(FreeAgentData agent)
    {
        var writer = new BitWriter();
        agent.Write(writer, FreeAgentData.Version1);
        var reader = new BitReader(writer.ToArray());
        Assert.True(FreeAgentData.TryRead(ref reader, out FreeAgentData? read));
        return read!;
    }
}

/// <summary>Where an event choice's image came from (<see cref="ImageSource"/>), alone and in a choice.</summary>
public class ImageSourceTests
{
    public static IEnumerable<ImageSource> EveryKind() => new[]
    {
        ImageSource.Named("ui_atlas", 0, 64.5f, 32, 32),
        ImageSource.Named(null, -16384, 16384, 0, 0),
        ImageSource.Event("stat_icons", 128, 0, 64, 64),
        ImageSource.Icon(ChoiceObjectRef.Object1, "talents", 1, 2, 3, 4),
        ImageSource.Icon(ChoiceObjectRef.AgentTalent(15), string.Empty, 1, 2, 3, 4),
        ImageSource.Icon(ChoiceObjectRef.AgentAbility, "abilities", 1, 2, 3, 4),
    };

    private static byte[] Encoded(ImageSource source)
    {
        var writer = new BitWriter();
        source.Write(writer);
        return writer.ToArray();
    }

    private static ImageSource? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return ImageSource.TryRead(ref reader, out ImageSource? source) && reader.RemainingBits < 8 ? source : null;
    }

    [Fact]
    public void Every_kind_round_trips()
    {
        foreach (ImageSource sent in EveryKind())
        {
            byte[] bytes = Encoded(sent);
            Assert.Equal(sent, Read(bytes));
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.Null(Read(bytes[..length]));
            }

            Assert.False(string.IsNullOrEmpty(sent.ToString()));
        }
    }

    [Fact]
    public void Its_identity_is_its_texture_and_rect_to_a_hundredth_of_a_pixel()
    {
        ImageSource source = ImageSource.Named("ui_atlas", 0, 64.5f, 32, 32);
        Assert.True(source.SameIdentity("ui_atlas", 0.004f, 64.5f, 32, 32));
        Assert.False(source.SameIdentity("ui_atlas", 0.02f, 64.5f, 32, 32));
        Assert.False(source.SameIdentity("ui_atlas2", 0, 64.5f, 32, 32));
        Assert.False(source.SameIdentity(null, 0, 64.5f, 32, 32));
        Assert.True(ImageSource.Named(null, 1, 1, 1, 1).SameIdentity(null, 1, 1, 1, 1));
    }

    public static IEnumerable<object[]> Refused()
    {
        yield return new object[] { "an unknown kind", new ImageSource((ImageSourceKind)3, ChoiceObjectRef.None, null, 0, 0, 1, 1) };
        yield return new object[] { "an icon of no object", new ImageSource(ImageSourceKind.ObjectIcon, ChoiceObjectRef.None, null, 0, 0, 1, 1) };
        yield return new object[] { "an object on a named sprite", new ImageSource(ImageSourceKind.Named, ChoiceObjectRef.Object1, null, 0, 0, 1, 1) };
        yield return new object[] { "an object on the event's own", new ImageSource(ImageSourceKind.Event, ChoiceObjectRef.Object2, null, 0, 0, 1, 1) };
        yield return new object[] { "a rect past 16384", ImageSource.Named(null, 0, 0, 16385, 1) };
        yield return new object[] { "a rect that isn't a number", ImageSource.Named(null, float.NaN, 0, 1, 1) };
        yield return new object[] { "an endless rect", ImageSource.Named(null, 0, float.NegativeInfinity, 1, 1) };
        yield return new object[] { "a texture with a line break", ImageSource.Named("a\nb", 0, 0, 1, 1) };
        yield return new object[] { "a texture over 128 bytes", ImageSource.Named(new string('t', EventChoiceSet.MaxNameBytes + 1), 0, 0, 1, 1) };
        yield return new object[] { "an agent's talent 17", ImageSource.Icon(ChoiceObjectRef.AgentTalent(16), null, 0, 0, 1, 1) };
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Sources_out_of_range_are_refused(string what, ImageSource source)
    {
        Assert.ThrowsAny<ArgumentException>(() => Encoded(source));
        Assert.False(string.IsNullOrEmpty(what));
    }

    public static IEnumerable<object[]> ChoicesThatDontFit()
    {
        var talent = new EventObject { Kind = EventObjectKind.Talent, Id = "t", ClassName = "T", Level = 1 };
        EventChoiceData Choice(Func<EventChoiceData, EventChoiceData> change) => change(new EventChoiceData { NodeType = 7, ChoiceType = 2, Object1 = talent, Images = new[] { "a", null }, Texts = new[] { "x", null } });
        yield return new object[] { "an icon of an object the choice hasn't", Choice(c => c.WithImageSources(new[] { ImageSource.Icon(ChoiceObjectRef.Object2, null, 0, 0, 1, 1), null })) };
        yield return new object[] { "an icon of a free agent it hasn't", Choice(c => c.WithImageSources(new[] { ImageSource.Icon(ChoiceObjectRef.AgentAbility, null, 0, 0, 1, 1), null })) };
        yield return new object[] { "a source for no image", Choice(c => c.WithImageSources(new ImageSource?[] { null, ImageSource.Named(null, 0, 0, 1, 1) })) };
        yield return new object[] { "a source too few", Choice(c => c.WithImageSources(new[] { ImageSource.Named(null, 0, 0, 1, 1) })) };
        yield return new object[] { "sources without images", new EventChoiceData { ImageSources = new[] { ImageSource.Named(null, 0, 0, 1, 1) } } };
        yield return new object[] { "a recipe for no text", Choice(c => c.WithRecipes(null, new[] { null, TextRecipe.Literal("x") })) };
        yield return new object[] { "a recipe too many", Choice(c => c.WithRecipes(null, new[] { null, null, TextRecipe.Literal("x") })) };
        yield return new object[] { "recipes without texts", new EventChoiceData { TextRecipes = new[] { TextRecipe.Literal("x") } } };
        yield return new object[] { "a button recipe without a button", Choice(c => c.WithRecipes(TextRecipe.Literal("x"), null)) };
        yield return new object[] { "a recipe naming an object the choice hasn't", Choice(c => c.WithRecipes(null, new[] { TextRecipe.ObjectName(ChoiceObjectRef.Object2), null })) };
        yield return new object[] { "a recipe naming a free agent's talent past its talents", Choice(c => c.WithRecipes(null, new[] { TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(0)), null })) };
        yield return new object[] { "a recipe out of its limits", Choice(c => c.WithRecipes(null, new[] { TextRecipe.StatName(64, false), null })) };
        EventObject Preview(ImageSource? source, string? icon) => new() { Kind = EventObjectKind.Preview, Id = "p", ClassName = "P", Level = 1, PreviewIcon = icon, PreviewIconSource = source };
        yield return new object[] { "a preview's icon source without its icon", new EventChoiceData { Object1 = Preview(ImageSource.Event(null, 0, 0, 1, 1), null) } };
        yield return new object[] { "a preview's icon that's an object's icon", new EventChoiceData { Object1 = Preview(ImageSource.Icon(ChoiceObjectRef.Object1, null, 0, 0, 1, 1), "i") } };
        yield return new object[] { "a preview's icon source out of range", new EventChoiceData { Object1 = Preview(ImageSource.Event(null, float.NaN, 0, 1, 1), "i") } };
        yield return new object[] { "an icon source on a talent", new EventChoiceData { Object1 = new EventObject { Kind = EventObjectKind.Talent, Id = "t", ClassName = "T", PreviewIconSource = ImageSource.Event(null, 0, 0, 1, 1) } } };
    }

    [Theory]
    [MemberData(nameof(ChoicesThatDontFit))]
    public void Recipes_and_sources_that_dont_fit_their_choice_are_refused(string what, EventChoiceData choice)
    {
        var set = new EventChoiceSet { EventClass = "FanNumber1Event", EventName = "Fan", NodeType = 7, Choices = new[] { choice } };
        Assert.NotNull(set.Problem());
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.EventChoices(1, set));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void A_choice_with_every_image_and_text_carrying_a_large_recipe_or_source_is_sent_and_read()
    {
        // A recipe at its node and string caps: a format of seven literals and six more literals, every string 256 bytes.
        TextRecipe literal = TextRecipe.Literal(new string('l', TextRecipe.MaxTextBytes));
        TextRecipe big = TextRecipe.Concat(new[] { TextRecipe.Format(TextRecipe.Term(new string('t', TextRecipe.MaxTextBytes)), Enumerable.Repeat(literal, 7).ToArray()) }
            .Concat(Enumerable.Repeat(literal, 6)).ToArray());
        Assert.Null(big.Check());
        string text = new('w', 64);
        var choice = new EventChoiceData
        {
            NodeType = 8,
            ChoiceType = 1,
            FreeAgent = FreeAgentLookTests.WithLook(),
            TranslatedButtonName = text,
            ButtonRecipe = big,
            Texts = Enumerable.Repeat<string?>(text, EventChoiceSet.MaxCustom).ToList(),
            TextRecipes = Enumerable.Repeat<TextRecipe?>(big, EventChoiceSet.MaxCustom).ToList(),
            Images = Enumerable.Repeat<string?>("i", EventChoiceSet.MaxCustom).ToList(),
            ImageSources = Enumerable.Range(0, EventChoiceSet.MaxCustom).Select(i => (ImageSource?)ImageSource.Icon(ChoiceObjectRef.AgentTalent(i % 3), new string('t', EventChoiceSet.MaxNameBytes), i, i, i, i)).ToList(),
        };
        var set = new EventChoiceSet { EventClass = "GeneralManagerEvent", EventName = "GM", NodeType = 8, Choices = Enumerable.Repeat(choice, 4).ToList() };
        Assert.Null(set.Problem());
        Assert.True(MenuAttachment.EventChoices(2, set).TryGetEventChoices(out EventChoiceSet? read));
        Assert.Equal(EventChoiceSetTests.Encoded(set), EventChoiceSetTests.Encoded(read!));
        Assert.Equal(choice.ImageSources, read!.Choices[3].ImageSources);
        Assert.Equal(RunPreviewSetTests.Look().GlassesSkin, read.Choices[0].FreeAgent!.Look!.GlassesSkin);

        // Over the attachment's cap with them (8 such choices), it fits without its recipes.
        var over = new EventChoiceSet { EventClass = "GeneralManagerEvent", EventName = "GM", NodeType = 8, Choices = Enumerable.Repeat(choice, EventChoiceSet.MaxChoices).ToList() };
        Assert.Contains($"at most {EventChoiceSet.MaxBytes}", over.Problem(), StringComparison.Ordinal);
        Assert.Null(over.WithoutRecipes().Problem());
    }
}
