using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>Which of the game's customizable prompts a <see cref="PromptData"/> is. Values are part of the protocol: only add.</summary>
public enum PromptKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>A yes/no question (the game's <c>CustomizableYesNoPrompt</c>).</summary>
    YesNo = 1,

    /// <summary>A notice with one button (the game's <c>CustomizableDismissablePrompt</c>).</summary>
    Dismissable = 2,
}

/// <summary>How one of a prompt's texts is made on a friend's PC (<see cref="PromptText"/>). Values are part of the protocol: only add.</summary>
public enum PromptTextKind : byte
{
    /// <summary>No text (the game's own empty text).</summary>
    Empty = 0,

    /// <summary>
    /// A translation term, made as the prompt data's constructor makes one from its keys (I2's <c>GetTranslation</c> with its
    /// defaults: RTL fixed, no line limit, RTL numbers ignored, no parameters applied).
    /// </summary>
    Term = 1,

    /// <summary>
    /// A translation term from one of the opener's <c>LocalizedString</c> fields, made as its <c>ToString</c> makes it (its own
    /// RTL settings, parameters applied, then the global parameters).
    /// </summary>
    LocalizedTerm = 2,

    /// <summary>The host's text as it shows it (it isn't one of the game's terms).</summary>
    Literal = 3,

    /// <summary>
    /// A text the game formats from a term and arguments (a skater's or a team's name in a term): a <see cref="TextRecipe"/> the
    /// friend's game makes in its own language (<see cref="PromptText.Recipe"/>); the host's text is the fallback.
    /// </summary>
    Recipe = 4,
}

/// <summary>
/// One text of a prompt, as a friend's game makes it: the term the host's game made it from, with how it made it, so a friend's game makes it again
/// in its own language; or the host's text when it isn't a term. Checked when it's written and when it's read; immutable.
/// <code>
/// u8 kind · kinds 1 to 4: optional text (always there) · kind 2: u8 flags (bit 0 Arabic fix off, bit 1 RTL numbers
/// converted, bit 2 parameters not localized) · varuint maximum line length · kind 4: text recipe
/// </code>
/// </summary>
public sealed record PromptText
{
    /// <summary>Largest maximum line length a <see cref="PromptTextKind.LocalizedTerm"/> carries (the game's are 0: no limit).</summary>
    public const int MaxLineLengthLimit = 1000;

    private const int FlagIgnoreArabicFix = 1;
    private const int FlagConvertNumbers = 2;
    private const int FlagDontLocalizeParameters = 4;
    private const int AllFlags = FlagIgnoreArabicFix | FlagConvertNumbers | FlagDontLocalizeParameters;

    /// <summary>No text.</summary>
    public static PromptText Empty { get; } = new();

    /// <summary>How the text is made.</summary>
    public PromptTextKind Kind { get; init; }

    /// <summary>The term (<see cref="PromptTextKind.Term"/>, <see cref="PromptTextKind.LocalizedTerm"/>) or the text (<see cref="PromptTextKind.Literal"/>); "" for none.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>A localized term: its <c>LocalizedString</c>'s <c>mRTL_IgnoreArabicFix</c>.</summary>
    public bool IgnoreArabicFix { get; init; }

    /// <summary>A localized term: its <c>LocalizedString</c>'s <c>mRTL_MaxLineLength</c> (0 to <see cref="MaxLineLengthLimit"/>).</summary>
    public int MaxLineLength { get; init; }

    /// <summary>A localized term: its <c>LocalizedString</c>'s <c>mRTL_ConvertNumbers</c>.</summary>
    public bool ConvertNumbers { get; init; }

    /// <summary>A localized term: its <c>LocalizedString</c>'s <c>m_DontLocalizeParameters</c>.</summary>
    public bool DontLocalizeParameters { get; init; }

    /// <summary>A recipe text: how a friend's game makes it (<see cref="Value"/> is the host's text, shown when it makes nothing); else null.</summary>
    public TextRecipe? Recipe { get; init; }

    /// <summary>A term made as the prompt data's constructor makes one from its keys.</summary>
    public static PromptText Term(string term) => new() { Kind = PromptTextKind.Term, Value = term ?? string.Empty };

    /// <summary>A term from a <c>LocalizedString</c>, with its RTL and parameter settings (made as its <c>ToString</c> makes it).</summary>
    public static PromptText Localized(string term, bool ignoreArabicFix = false, int maxLineLength = 0, bool convertNumbers = false, bool dontLocalizeParameters = false) => new()
    {
        Kind = PromptTextKind.LocalizedTerm,
        Value = term ?? string.Empty,
        IgnoreArabicFix = ignoreArabicFix,
        MaxLineLength = maxLineLength,
        ConvertNumbers = convertNumbers,
        DontLocalizeParameters = dontLocalizeParameters,
    };

    /// <summary>The host's text itself; <see cref="Empty"/> for no text.</summary>
    public static PromptText Literal(string? text) => string.IsNullOrEmpty(text) ? Empty : new() { Kind = PromptTextKind.Literal, Value = text };

    /// <summary>A formatted text a friend's game makes from <paramref name="recipe"/>; <paramref name="shown"/> is the host's text (the fallback).</summary>
    public static PromptText FromRecipe(TextRecipe recipe, string shown) =>
        new() { Kind = PromptTextKind.Recipe, Value = shown ?? string.Empty, Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe)) };

    /// <summary>True for a term (<see cref="PromptTextKind.Term"/> or <see cref="PromptTextKind.LocalizedTerm"/>).</summary>
    public bool IsTerm => Kind is PromptTextKind.Term or PromptTextKind.LocalizedTerm;

    /// <summary>
    /// For logs, as <see cref="ShownTextLog.Prompt(PromptText?, Func{string?, string})"/> writes it with a text's length for
    /// its shape: "term general/yes", "localized term v2ui/...", "literal (5 characters)", "empty". A prompt may come from a
    /// host, and a literal can be a name a player typed: no text of it is written as it is.
    /// </summary>
    public override string ToString() => ShownTextLog.Prompt(this, Diagnostics.HostText.TextShape);

    /// <summary>Why the text can't be sent (<paramref name="what"/> names it), or null when it can.</summary>
    public string? Problem(string what)
    {
        if (Kind is not (PromptTextKind.Empty or PromptTextKind.Term or PromptTextKind.LocalizedTerm or PromptTextKind.Literal or PromptTextKind.Recipe))
        {
            return $"{what} is of an unknown kind {(byte)Kind}";
        }

        if (Value == null || (Kind == PromptTextKind.Empty) != (Value.Length == 0))
        {
            return Kind == PromptTextKind.Empty ? $"{what} is empty but has a text" : $"{what} has no text";
        }

        bool rtl = IgnoreArabicFix || ConvertNumbers || DontLocalizeParameters || MaxLineLength != 0;
        if (Kind != PromptTextKind.LocalizedTerm && rtl)
        {
            return $"{what} isn't a localized term but has its settings";
        }

        if (MaxLineLength is < 0 or > MaxLineLengthLimit)
        {
            return $"{what}'s maximum line length {MaxLineLength} is out of range";
        }

        if ((Kind == PromptTextKind.Recipe) != (Recipe != null))
        {
            return $"{what} has a recipe only a recipe's text has";
        }

        string? recipe = Recipe?.Check();
        return EventWire.CheckText(Value, what) ?? (recipe == null ? null : $"{what}'s recipe: {recipe}");
    }

    /// <summary>Writes the text (<see cref="Empty"/> for null). Throws <see cref="ArgumentException"/> (saying what) when it can't be sent.</summary>
    internal static void Write(BitWriter writer, PromptText? text, string what)
    {
        text ??= Empty;
        string? problem = text.Problem(what);
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(text));
        }

        writer.WriteByte((byte)text.Kind);
        if (text.Kind == PromptTextKind.Empty)
        {
            return;
        }

        EventWire.OptionalText(writer, text.Value);
        if (text.Kind == PromptTextKind.LocalizedTerm)
        {
            writer.WriteByte((byte)((text.IgnoreArabicFix ? FlagIgnoreArabicFix : 0) | (text.ConvertNumbers ? FlagConvertNumbers : 0) | (text.DontLocalizeParameters ? FlagDontLocalizeParameters : 0)));
            writer.WriteVarUInt((ulong)text.MaxLineLength);
        }
        else if (text.Kind == PromptTextKind.Recipe)
        {
            text.Recipe!.Write(writer);
        }
    }

    /// <summary>Reads a text. False for an unknown kind, a missing text, unknown flags or anything out of range. Never throws.</summary>
    internal static bool TryRead(ref BitReader reader, out PromptText? text)
    {
        text = null;
        var kind = (PromptTextKind)reader.ReadByte();
        if (reader.IsOverrun || kind is not (PromptTextKind.Empty or PromptTextKind.Term or PromptTextKind.LocalizedTerm or PromptTextKind.Literal or PromptTextKind.Recipe))
        {
            return false;
        }

        if (kind == PromptTextKind.Empty)
        {
            text = Empty;
            return true;
        }

        if (!EventWire.TryReadOptionalText(ref reader, out string? value) || string.IsNullOrEmpty(value))
        {
            return false;
        }

        var read = new PromptText { Kind = kind, Value = value };
        if (kind == PromptTextKind.LocalizedTerm)
        {
            int flags = reader.ReadByte();
            ulong length = reader.ReadVarUInt();
            if (reader.IsOverrun || (flags & ~AllFlags) != 0 || length > MaxLineLengthLimit)
            {
                return false;
            }

            read = read with
            {
                IgnoreArabicFix = (flags & FlagIgnoreArabicFix) != 0,
                ConvertNumbers = (flags & FlagConvertNumbers) != 0,
                DontLocalizeParameters = (flags & FlagDontLocalizeParameters) != 0,
                MaxLineLength = (int)length,
            };
        }

        if (kind == PromptTextKind.Recipe)
        {
            if (!TextRecipe.TryRead(ref reader, out TextRecipe? recipe))
            {
                return false;
            }

            read = read with { Recipe = recipe };
        }

        if (reader.IsOverrun || read.Problem("a text") != null)
        {
            return false;
        }

        text = read;
        return true;
    }
}

/// <summary>
/// A prompt on the host's screen as its data was made (the arguments of the game's <c>CustomizableYesNoPrompt.Data</c> or
/// <c>CustomizableDismissablePrompt.Data</c>), sent as a <see cref="MenuAttachmentKind.Prompt"/> attachment of the prompt's
/// menu, format <see cref="MenuAttachment.PromptFormat"/> (2). Each text says how a friend's game makes it
/// (<see cref="PromptText"/>): from the exact term the host's game made it from (the caller's literal key, or the opener
/// menu's <c>LocalizedString</c> field), in the friend's own language; or as the host's text. A kind's own texts are never
/// null once read (<see cref="PromptText.Empty"/> for none); the fields a kind doesn't have are null (false). Checked when it's
/// written and when it's read; immutable.
/// <code>
/// u8 kind · text title · text description · yes/no: text yes · text no · bool showClose · u8 default selection + 1
/// (0: none) | dismissable: text dismiss
/// </code>
/// A format 1 body (one "from keys" flag for all four texts) is refused: such a prompt shows as text.
/// </summary>
public sealed class PromptData
{
    /// <summary>Highest default selection (the game's <c>PromptTemplateResult</c> number: yes, no, cancel, and room for more).</summary>
    public const int MaxSelection = 14;

    /// <summary>Which prompt.</summary>
    public PromptKind Kind { get; init; }

    /// <summary>The title (null: none).</summary>
    public PromptText? Title { get; init; }

    /// <summary>The description (null: none).</summary>
    public PromptText? Description { get; init; }

    /// <summary>A yes/no prompt's yes button (null: none, or a notice).</summary>
    public PromptText? Yes { get; init; }

    /// <summary>A yes/no prompt's no button (null: none, or a notice).</summary>
    public PromptText? No { get; init; }

    /// <summary>A notice's button (null: none, or a yes/no prompt).</summary>
    public PromptText? Dismiss { get; init; }

    /// <summary>A yes/no prompt shows its close button.</summary>
    public bool ShowCloseButton { get; init; }

    /// <summary>A yes/no prompt's selected button at first (the game's <c>PromptTemplateResult</c> number), or null for the prompt's own.</summary>
    public int? DefaultSelection { get; init; }

    /// <summary>The kind's texts in the data constructor's order (title, description, then yes and no, or dismiss); a missing one is <see cref="PromptText.Empty"/>.</summary>
    public IReadOnlyList<PromptText> Texts => Kind == PromptKind.YesNo
        ? new[] { Title ?? PromptText.Empty, Description ?? PromptText.Empty, Yes ?? PromptText.Empty, No ?? PromptText.Empty }
        : new[] { Title ?? PromptText.Empty, Description ?? PromptText.Empty, Dismiss ?? PromptText.Empty };

    /// <summary>
    /// For logs: "yes/no prompt (3 term(s), 1 literal) title term v2/ui/prompt/concede/title", as
    /// <see cref="ShownTextLog.Prompt(PromptData, Func{string?, string})"/> writes it with a text's length for its shape.
    /// </summary>
    public override string ToString() => ShownTextLog.Prompt(this, Diagnostics.HostText.TextShape);

    /// <summary>Why the prompt can't be sent, or null when it can.</summary>
    public string? Problem() => Check();

    /// <summary>Writes the prompt. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteByte((byte)Kind);
        PromptText.Write(writer, Title, "the title");
        PromptText.Write(writer, Description, "the description");
        if (Kind == PromptKind.YesNo)
        {
            PromptText.Write(writer, Yes, "the yes button");
            PromptText.Write(writer, No, "the no button");
            writer.WriteBool(ShowCloseButton);
            writer.WriteByte((byte)(DefaultSelection is int selection ? selection + 1 : 0));
        }
        else
        {
            PromptText.Write(writer, Dismiss, "the dismiss button");
        }
    }

    /// <summary>Reads a prompt. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out PromptData? prompt)
    {
        prompt = null;
        var kind = (PromptKind)reader.ReadByte();
        if (kind is not (PromptKind.YesNo or PromptKind.Dismissable) ||
            !PromptText.TryRead(ref reader, out PromptText? title) || !PromptText.TryRead(ref reader, out PromptText? description))
        {
            return false;
        }

        PromptText? yes = null, no = null, dismiss = null;
        bool close = false;
        int? selection = null;
        if (kind == PromptKind.YesNo)
        {
            if (!PromptText.TryRead(ref reader, out yes) || !PromptText.TryRead(ref reader, out no))
            {
                return false;
            }

            close = reader.ReadBool();
            int read = reader.ReadByte();
            selection = read == 0 ? null : read - 1;
        }
        else if (!PromptText.TryRead(ref reader, out dismiss))
        {
            return false;
        }

        var data = new PromptData
        {
            Kind = kind,
            Title = title,
            Description = description,
            Yes = yes,
            No = no,
            Dismiss = dismiss,
            ShowCloseButton = close,
            DefaultSelection = selection,
        };
        if (reader.IsOverrun || data.Check() != null)
        {
            return false;
        }

        prompt = data;
        return true;
    }

    private string? Check()
    {
        if (Kind is not (PromptKind.YesNo or PromptKind.Dismissable))
        {
            return $"prompt kind {Kind} is no prompt";
        }

        if (Kind == PromptKind.YesNo ? Dismiss != null : Yes != null || No != null || ShowCloseButton || DefaultSelection != null)
        {
            return $"a {Kind} prompt with another kind's fields";
        }

        if (DefaultSelection is < 0 or > MaxSelection)
        {
            return $"default selection {DefaultSelection} is out of range";
        }

        return Title?.Problem("the title") ?? Description?.Problem("the description") ?? Yes?.Problem("the yes button") ??
            No?.Problem("the no button") ?? Dismiss?.Problem("the dismiss button");
    }
}
