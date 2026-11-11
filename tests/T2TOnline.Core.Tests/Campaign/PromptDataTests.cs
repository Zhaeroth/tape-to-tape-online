using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class PromptDataTests
{
    /// <summary>
    /// A yes/no prompt made from the in-match menu's literal keys, with a close button and a default selection, and a no button
    /// from a <c>LocalizedString</c> with its own settings (as an event's exit prompt makes one).
    /// </summary>
    public static PromptData YesNo() => new()
    {
        Kind = PromptKind.YesNo,
        Title = PromptText.Term("v2/ui/prompt/concede/title"),
        Description = PromptText.Term("v2/ui/prompt/concede/desc"),
        Yes = PromptText.Term("general/confirm"),
        No = PromptText.Localized("v2ui/event/exit/no", ignoreArabicFix: true, maxLineLength: 40, convertNumbers: true, dontLocalizeParameters: true),
        ShowCloseButton = true,
        DefaultSelection = 1,
    };

    /// <summary>A notice with the host's texts (none is a term), and no title.</summary>
    public static PromptData Notice() => new()
    {
        Kind = PromptKind.Dismissable,
        Title = PromptText.Empty,
        Description = PromptText.Literal("<color=#ff0000>Your roster isn't valid.</color>\nPick five skaters."),
        Dismiss = PromptText.Literal("OK"),
    };

    /// <summary>
    /// The customization menus' "can't delete this skater" notice (v1): a title and a button that are terms, and a
    /// description the game formats from a term and the skater's name, which a friend's game makes again in its own language.
    /// </summary>
    public static PromptData WithRecipe() => new()
    {
        Kind = PromptKind.Dismissable,
        Title = PromptText.Term("v2/player-customization/cant_delete_skater/title"),
        Description = PromptText.FromRecipe(
            TextRecipe.Format(TextRecipe.Term("v2/player-customization/cant_delete_skater/desc"), TextRecipe.Literal("Ole Bergström")),
            "Skater Ole Bergström can't be deleted because they are in an active tournament."),
        Dismiss = PromptText.Term("general/ok"),
    };

    public static byte[] Encoded(PromptData prompt)
    {
        var writer = new BitWriter();
        prompt.Write(writer);
        return writer.ToArray();
    }

    private static PromptData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = PromptData.TryRead(ref reader, out PromptData? prompt);
        Assert.Equal(ok, prompt != null);
        return ok && reader.RemainingBits < 8 ? prompt : null;
    }

    [Fact]
    public void Prompts_round_trip_text_by_text()
    {
        PromptData yesNo = Read(Encoded(YesNo()))!;
        Assert.NotNull(yesNo);
        Assert.Equal(PromptKind.YesNo, yesNo.Kind);
        Assert.Equal(YesNo().Texts, yesNo.Texts);
        Assert.Equal((true, (int?)1, (PromptText?)null), (yesNo.ShowCloseButton, yesNo.DefaultSelection, yesNo.Dismiss));
        Assert.Equal((PromptTextKind.LocalizedTerm, true, 40, true, true), (yesNo.No!.Kind, yesNo.No.IgnoreArabicFix, yesNo.No.MaxLineLength, yesNo.No.ConvertNumbers, yesNo.No.DontLocalizeParameters));
        Assert.Contains("yes/no prompt (4 term(s), 0 literal) title term v2/ui/prompt/concede/title", yesNo.ToString(), StringComparison.Ordinal);

        PromptData notice = Read(Encoded(Notice()))!;
        Assert.Equal(PromptKind.Dismissable, notice.Kind);
        Assert.Equal(Notice().Texts, notice.Texts);
        Assert.Equal(((PromptText?)null, (PromptText?)null, false, (int?)null), (notice.Yes, notice.No, notice.ShowCloseButton, notice.DefaultSelection));
        Assert.Contains("notice (0 term(s), 2 literal) title empty", notice.ToString(), StringComparison.Ordinal);

        // A kind's missing texts go (and read back) as empty; every text at its cap; no default selection.
        var bare = new PromptData { Kind = PromptKind.YesNo };
        PromptData bareRead = Read(Encoded(bare))!;
        Assert.All(bareRead.Texts, t => Assert.Same(PromptText.Empty, t));
        Assert.Equal(Encoded(bare), Encoded(bareRead));
        string text = new('é', EventChoiceSet.MaxTextBytes / 2);
        var full = new PromptData
        {
            Kind = PromptKind.YesNo,
            Title = PromptText.Literal(text),
            Description = PromptText.Term(text),
            Yes = PromptText.Localized(text, maxLineLength: PromptText.MaxLineLengthLimit),
            No = PromptText.Literal(text),
            DefaultSelection = PromptData.MaxSelection,
        };
        Assert.Null(full.Problem());
        Assert.Equal(Encoded(full), Encoded(Read(Encoded(full))!));
    }

    [Fact]
    public void A_recipe_text_round_trips_with_its_recipe_and_the_hosts_text()
    {
        PromptData sent = WithRecipe();
        Assert.Null(sent.Problem());
        PromptData read = Read(Encoded(sent))!;
        Assert.NotNull(read);
        Assert.Equal(Encoded(sent), Encoded(read));
        Assert.Equal((PromptTextKind.Recipe, sent.Description!.Value), (read.Description!.Kind, read.Description.Value));
        Assert.Equal(sent.Description.Recipe!.ToString(), read.Description.Recipe!.ToString());
        Assert.False(read.Description.IsTerm);
        Assert.Equal(PromptTextKind.Term, read.Title!.Kind);
        Assert.Contains("notice (2 term(s), 0 literal, 1 recipe(s))", read.ToString(), StringComparison.Ordinal);
        Assert.Equal("recipe format(term \"v2/player-customization/cant_delete_skater/desc\", a text (13 characters)) (the host's text: " +
            $"{HostText.TextShape(sent.Description.Value)})", read.Description.ToString());
        Assert.DoesNotContain("Ole", read.ToString() + read.Description, StringComparison.Ordinal);

        // The friend's game makes it in its own language from the same recipe; one that makes nothing leaves the host's text.
        Assert.Equal("<de:term:v2/player-customization/cant_delete_skater/desc|Ole Bergström>", read.Description.Recipe!.Evaluate(new FakeTexts("de")));
        var missing = new FakeTexts("de");
        missing.MissingTerms.Add("v2/player-customization/cant_delete_skater/desc");
        Assert.Null(read.Description.Recipe.Evaluate(missing));

        // As an attachment under the same format (a kind added to format 2).
        MenuAttachment attachment = MenuAttachment.Prompt(8, sent);
        Assert.Equal((MenuAttachmentKind.Prompt, (byte)2), (attachment.Kind, attachment.Format));
        Assert.True(attachment.TryGetPrompt(out PromptData? back));
        Assert.Equal(Encoded(sent), Encoded(back!));
        Assert.Throws<ArgumentNullException>(() => PromptText.FromRecipe(null!, "x"));
    }

    [Fact]
    public void Texts_are_made_by_kind()
    {
        Assert.Same(PromptText.Empty, PromptText.Literal(null));
        Assert.Same(PromptText.Empty, PromptText.Literal(string.Empty));
        Assert.Equal((PromptTextKind.Literal, "Skip?"), (PromptText.Literal("Skip?").Kind, PromptText.Literal("Skip?").Value));
        Assert.True(PromptText.Term("general/yes").IsTerm);
        Assert.True(PromptText.Localized("a/b").IsTerm);
        Assert.False(PromptText.Literal("Yes").IsTerm);
        Assert.False(PromptText.Empty.IsTerm);
        Assert.Equal("term general/yes", PromptText.Term("general/yes").ToString());
        Assert.Equal("localized term a/b", PromptText.Localized("a/b").ToString());
        Assert.Equal("literal (3 characters)", PromptText.Literal("Yes").ToString());
        Assert.Equal("empty", PromptText.Empty.ToString());
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        string over = new('x', EventChoiceSet.MaxTextBytes + 1);
        yield return new object[] { "no kind", new PromptData() };
        yield return new object[] { "an unknown kind", new PromptData { Kind = (PromptKind)3 } };
        yield return new object[] { "a dismiss button on a yes/no prompt", new PromptData { Kind = PromptKind.YesNo, Dismiss = PromptText.Literal("OK") } };
        yield return new object[] { "a yes button on a notice", new PromptData { Kind = PromptKind.Dismissable, Yes = PromptText.Literal("Yes") } };
        yield return new object[] { "a no button on a notice", new PromptData { Kind = PromptKind.Dismissable, No = PromptText.Empty } };
        yield return new object[] { "a close button on a notice", new PromptData { Kind = PromptKind.Dismissable, ShowCloseButton = true } };
        yield return new object[] { "a default selection on a notice", new PromptData { Kind = PromptKind.Dismissable, DefaultSelection = 0 } };
        yield return new object[] { "a default selection past the cap", new PromptData { Kind = PromptKind.YesNo, DefaultSelection = PromptData.MaxSelection + 1 } };
        yield return new object[] { "a negative default selection", new PromptData { Kind = PromptKind.YesNo, DefaultSelection = -1 } };
        yield return new object[] { "a title over 2048 bytes", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Literal(over) } };
        yield return new object[] { "a term over 2048 bytes", new PromptData { Kind = PromptKind.Dismissable, Description = PromptText.Term(over) } };
        yield return new object[] { "a dismiss button with a NUL", new PromptData { Kind = PromptKind.Dismissable, Dismiss = PromptText.Literal("O\0K") } };
        yield return new object[] { "a yes button that isn't text", new PromptData { Kind = PromptKind.YesNo, Yes = PromptText.Literal("\uDC00") } };
        yield return new object[] { "a term with no text", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Term(string.Empty) } };
        yield return new object[] { "an empty text with a text", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Empty with { Value = "x" } } };
        yield return new object[] { "a text of an unknown kind", new PromptData { Kind = PromptKind.YesNo, Title = new PromptText { Kind = (PromptTextKind)5, Value = "x" } } };
        yield return new object[] { "a recipe text with no recipe", new PromptData { Kind = PromptKind.YesNo, Title = new PromptText { Kind = PromptTextKind.Recipe, Value = "x" } } };
        yield return new object[] { "a literal with a recipe", new PromptData { Kind = PromptKind.YesNo, Title = new PromptText { Kind = PromptTextKind.Literal, Value = "x", Recipe = TextRecipe.Literal("x") } } };
        yield return new object[] { "a term with a recipe", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Term("a/b") with { Recipe = TextRecipe.Literal("x") } } };
        yield return new object[] { "a recipe text with no host text", new PromptData { Kind = PromptKind.YesNo, Title = new PromptText { Kind = PromptTextKind.Recipe, Recipe = TextRecipe.Literal("x") } } };
        yield return new object[] { "a recipe out of its limits", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.FromRecipe(TextRecipe.Concat(), "x") } };
        yield return new object[] { "a recipe with RTL settings", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.FromRecipe(TextRecipe.Literal("x"), "x") with { ConvertNumbers = true } } };
        yield return new object[] { "a null text", new PromptData { Kind = PromptKind.YesNo, Title = new PromptText { Kind = PromptTextKind.Literal, Value = null! } } };
        yield return new object[] { "RTL settings on a plain term", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Term("a/b") with { ConvertNumbers = true } } };
        yield return new object[] { "a line length on a literal", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Literal("A") with { MaxLineLength = 3 } } };
        yield return new object[] { "a line length past its cap", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Localized("a/b", maxLineLength: PromptText.MaxLineLengthLimit + 1) } };
        yield return new object[] { "a negative line length", new PromptData { Kind = PromptKind.YesNo, Title = PromptText.Localized("a/b", maxLineLength: -1) } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Prompts_out_of_their_limits_are_refused_when_written_and_never_sent(string what, PromptData prompt)
    {
        Assert.NotNull(prompt.Problem());
        Assert.ThrowsAny<ArgumentException>(() => prompt.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.Prompt(3, prompt));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Crafted_prompts_that_break_a_rule_are_refused()
    {
        foreach (PromptData prompt in new[] { YesNo(), Notice(), WithRecipe() })
        {
            byte[] good = Encoded(prompt);
            Assert.NotNull(Read(good));
            for (int length = 0; length < good.Length; length++)
            {
                Assert.Null(Read(good[..length]));
            }

            Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));
            byte[] kind = (byte[])good.Clone();
            kind[0] = 0;
            Assert.Null(Read(kind));
            kind[0] = 7;
            Assert.Null(Read(kind));
        }

        // A default selection at the cap reads; one past it doesn't.
        Assert.NotNull(Read(Bare(PromptData.MaxSelection + 1)));
        Assert.Null(Read(Bare(PromptData.MaxSelection + 2)));
        Assert.Null(Read(Bare(255)));

        // A text's kind past the known ones, a term that says no text follows, a localized term's unknown flag or line
        // length past its cap: refused.
        Assert.Null(Read(Title(w => w.WriteByte(5))));
        Assert.Null(Read(Title(w =>
        {
            w.WriteByte((byte)PromptTextKind.Term);
            w.WriteBool(false); // no text
        })));
        // A recipe text whose recipe is cut short, is an op this version doesn't have, or is out of its limits: refused; a good one reads.
        Assert.Null(Read(Title(w => Recipe(w, ww => { }))));
        Assert.Null(Read(Title(w => Recipe(w, ww => ww.WriteByte(14)))));
        Assert.Null(Read(Title(w => Recipe(w, ww => ww.WriteByte((byte)TextOp.Concat)))));
        Assert.NotNull(Read(Title(w => Recipe(w, ww => TextRecipe.Literal("x").Write(ww)))));
        Assert.NotNull(Read(Title(w => Localized(w, 0b111, PromptText.MaxLineLengthLimit))));
        Assert.Null(Read(Title(w => Localized(w, 0b1000, 0))));
        Assert.Null(Read(Title(w => Localized(w, 0, PromptText.MaxLineLengthLimit + 1))));

        // The old format's body (a "from keys" flag, then optional texts) doesn't read as this one.
        var old = new BitWriter();
        old.WriteByte((byte)PromptKind.Dismissable);
        old.WriteBool(true); // from keys
        foreach (string term in new[] { "a/title", "a/desc", "general/ok" })
        {
            old.WriteBool(true);
            old.WriteVarUInt((ulong)term.Length);
            old.WriteBytes(System.Text.Encoding.UTF8.GetBytes(term));
        }

        Assert.Null(Read(old.ToArray()));

        static byte[] Bare(int selectionByte)
        {
            var writer = new BitWriter();
            writer.WriteByte((byte)PromptKind.YesNo);
            for (int i = 0; i < 4; i++)
            {
                writer.WriteByte((byte)PromptTextKind.Empty);
            }

            writer.WriteBool(false); // no close button
            writer.WriteByte((byte)selectionByte);
            return writer.ToArray();
        }

        static byte[] Title(Action<BitWriter> title)
        {
            var writer = new BitWriter();
            writer.WriteByte((byte)PromptKind.Dismissable);
            title(writer);
            writer.WriteByte((byte)PromptTextKind.Empty);
            writer.WriteByte((byte)PromptTextKind.Empty);
            return writer.ToArray();
        }

        static void Recipe(BitWriter writer, Action<BitWriter> recipe)
        {
            writer.WriteByte((byte)PromptTextKind.Recipe);
            writer.WriteBool(true);
            writer.WriteVarUInt(1);
            writer.WriteBytes("x"u8);
            recipe(writer);
        }

        static void Localized(BitWriter writer, int flags, int length)
        {
            writer.WriteByte((byte)PromptTextKind.LocalizedTerm);
            writer.WriteBool(true);
            writer.WriteVarUInt(3);
            writer.WriteBytes("a/b"u8);
            writer.WriteByte((byte)flags);
            writer.WriteVarUInt((ulong)length);
        }
    }

    [Fact]
    public void An_attachment_of_a_prompt_round_trips_and_is_only_a_prompt()
    {
        MenuAttachment sent = MenuAttachment.Prompt(21, Notice());
        Assert.Equal((MenuAttachmentKind.Prompt, (byte)2), (sent.Kind, sent.Format));
        Assert.Contains("prompt for #21", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetNpcLine(out _));
        Assert.False(sent.TryGetEventChoices(out _));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetPrompt(out PromptData? prompt));
        Assert.Equal(Encoded(Notice()), Encoded(prompt!));

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.Prompt(0, Notice()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.Prompt(1, null!));
        Assert.False(MenuAttachment.NpcLine(21, NpcLineDataTests.Sample()).TryGetPrompt(out _));

        // The same body under format 1 (an older host's) is refused whole.
        var older = new BitWriter();
        older.WriteUInt32(21);
        older.WriteByte((byte)MenuAttachmentKind.Prompt);
        older.WriteByte(1);
        byte[] body = Encoded(Notice());
        byte[] deflated = Compression.Deflate(body);
        older.WriteVarUInt((ulong)body.Length);
        older.WriteVarUInt((ulong)deflated.Length);
        older.WriteBytes(deflated);
        var olderReader = new BitReader(older.AsSpan());
        Assert.False(MenuAttachmentMessage.TryRead(ref olderReader, out _));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = { Encoded(YesNo()), Encoded(Notice()), Encoded(WithRecipe()) };
        var random = new Random(4);
        int read = 0;
        for (int round = 0; round < 6000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            if (round % 4 == 3)
            {
                bytes = new byte[random.Next(0, 96)];
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
            if (!PromptData.TryRead(ref reader, out PromptData? prompt))
            {
                Assert.Null(prompt);
                continue;
            }

            read++;
            Assert.Null(prompt!.Problem());
            Assert.All(prompt.Texts, t => Assert.Null(t.Problem("a text")));
            Assert.Equal(Encoded(prompt), Encoded(Read(Encoded(prompt))!));
            _ = prompt.ToString();
        }

        Assert.True(read > 0, "some changed prompts should still read");
    }
}
