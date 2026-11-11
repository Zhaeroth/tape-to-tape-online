using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// How a log line writes what a host's toast or prompt shows (D127: no player-written text in a log). The game writes a
/// team's or a skater's name into some of its toasts and prompts (<c>{0} wins by mercy</c>, <c>{0} has been stripped</c>,
/// <c>Skater {0} can't be deleted ...</c>), and a custom team's and a custom skater's names are typed by their player; and a
/// modified host can send any text as a term's or an object's. So a text is never written as the host sent it: a term's by
/// its key, an object's name or description, the host's own text and a text made by a recipe (whose arguments carry those
/// names) as its shape only (the caller's <c>shape</c>: a length and a hash keyed per launch); a recipe as what it is made
/// of, each of its literal texts as its shape, except a format a recipe starts from when it has no letter (holes and
/// punctuation: the code's own); ids and keys by <see cref="HostText.KeyForLog(string?)"/>. Game-independent; never throws
/// but for a null argument.
/// </summary>
public static class ShownTextLog
{
    // The most characters of one text, and of one recipe, a line writes.
    private const int TextChars = 48;
    private const int RecipeChars = 160;

    /// <summary>
    /// A toast's text for a log line: <c>(a term) v2ui/mercy/notification/title</c> for a term (its key), <c>(a recipe)
    /// format(term "...", name of the run team)</c> for a recipe, <c>(its object's name: 9 characters, #0c2d)</c> or <c>(the
    /// host's text: 11 characters, #0c2d)</c> for any other; <c>none</c> for null.
    /// </summary>
    public static string Toast(ToastText? text, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return text?.Kind switch
        {
            null => "none",
            ToastTextKind.Term when !string.IsNullOrEmpty(text.Term) => $"({text.How}) {HostText.KeyForLog(text.Term)}",
            ToastTextKind.Recipe => $"({text.How}) {Recipe(text.Recipe, shape)}",
            _ => $"({text.How}: {Shape(text.Value, shape)})",
        };
    }

    /// <summary>
    /// A toast copy for a log line, each text by <see cref="Toast(ToastText?, Func{string?, string})"/>, ids by
    /// <see cref="HostText.KeyForLog(string?)"/>: "type 100, timing 32, relic 727e0295-..., title (a term)
    /// v2ui/mercy/notification/title, description (a recipe) format(term "v2ui/mercy/notification/desc", name of the run team)".
    /// </summary>
    public static string Toast(NotificationCopy copy, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(shape);
        var parts = new List<string> { $"type {copy.Type}", $"timing {copy.Timing}" };
        if (copy.ObjectKind != ToastObjectKind.None)
        {
            parts.Add($"{copy.ObjectKind.ToString().ToLowerInvariant()} {HostText.KeyForLog(copy.ObjectId)}");
        }

        if (copy.SkaterId != null)
        {
            parts.Add($"skater {HostText.KeyForLog(copy.SkaterId)}");
        }

        if (copy.InjuryType is int injury)
        {
            parts.Add($"injury {injury} for {copy.InjuryGames} game(s)");
        }

        if (copy.Title != null)
        {
            parts.Add($"title {Toast(copy.Title, shape)}");
        }

        if (copy.Description != null)
        {
            parts.Add($"description {Toast(copy.Description, shape)}");
        }

        if (copy.CustomTexts.Count > 0)
        {
            int recipes = copy.CustomTexts.Count(t => t?.Kind == ToastTextKind.Recipe);
            parts.Add($"{copy.CustomTexts.Count} text(s){(recipes > 0 ? $" ({recipes} as recipes)" : string.Empty)}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A prompt's text for a log line, as <c>PromptText.ToString</c> writes it with nothing the host's player may have typed:
    /// <c>term v2/ui/prompt/concede/title</c>, <c>literal (11 characters, #0c2d)</c>, <c>recipe format(...) (the host's text:
    /// 28 characters, #4be1)</c>; <c>empty</c> for none.
    /// </summary>
    public static string Prompt(PromptText? text, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return text?.Kind switch
        {
            null or PromptTextKind.Empty => "empty",
            PromptTextKind.Term => $"term {HostText.KeyForLog(text.Value)}",
            PromptTextKind.LocalizedTerm => $"localized term {HostText.KeyForLog(text.Value)}",
            PromptTextKind.Literal => $"literal ({Shape(text.Value, shape)})",
            PromptTextKind.Recipe => $"recipe {Recipe(text.Recipe, shape)} (the host's text: {Shape(text.Value, shape)})",
            _ => text.Kind.ToString(),
        };
    }

    /// <summary>
    /// A prompt for a log line (what <see cref="PromptData.ToString"/> writes, with a text's length for its shape), its title by
    /// <see cref="Prompt(PromptText?, Func{string?, string})"/>: "yes/no prompt (4 term(s), 0 literal) title term v2/ui/prompt/concede/title".
    /// </summary>
    public static string Prompt(PromptData prompt, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(shape);
        string what = prompt.Kind == PromptKind.YesNo ? "yes/no prompt" : prompt.Kind == PromptKind.Dismissable ? "notice" : prompt.Kind.ToString();
        IReadOnlyList<PromptText> texts = prompt.Kind is PromptKind.YesNo or PromptKind.Dismissable ? prompt.Texts : Array.Empty<PromptText>();
        int recipes = texts.Count(t => t.Kind == PromptTextKind.Recipe);
        return $"{what} ({texts.Count(t => t.IsTerm)} term(s), {texts.Count(t => t.Kind == PromptTextKind.Literal)} literal" +
            $"{(recipes > 0 ? $", {recipes} recipe(s)" : string.Empty)}) title {Prompt(prompt.Title, shape)}";
    }

    /// <summary>
    /// A prompt's text as a PC shows it (<paramref name="shown"/>), for a log line: in quotes when <paramref name="text"/> is one
    /// of the game's terms (<c>"Concede?"</c>), else as its shape (<c>a text of 11 characters, #0c2d</c>).
    /// </summary>
    public static string Shown(PromptText? text, string? shown, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return text is { IsTerm: true } ? $"\"{SafeText.Clean(shown, TextChars)}\"" : $"a text of {Shape(shown, shape)}";
    }

    /// <summary>
    /// A recipe for a log line, as <see cref="TextRecipe.ToString"/> writes it but each literal text as the caller's shape (a
    /// format it starts from as it is when it has no letter): <c>format(term "v2ui/mercy/notification/desc", name of the run team)</c>,
    /// <c>format(term "...", a text (11 characters, #0c2d))</c>; <c>none</c> for null.
    /// </summary>
    public static string Recipe(TextRecipe? recipe, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return recipe == null ? "none" : SafeText.Clean(Node(recipe, shape, depth: 0, isFormat: false), RecipeChars);
    }

    /// <summary>
    /// A reason a recipe wasn't made, for a log line: each quoted part (a text the recipe made, which can hold a name; a format's
    /// text) as its shape, the rest as it is. A quote that is never closed (a quoted text holding a quote of its own) quotes
    /// everything after it, so that rest is written as its shape too.
    /// </summary>
    public static string Reason(string? why, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (string.IsNullOrEmpty(why))
        {
            return string.Empty;
        }

        var written = new StringBuilder(why.Length);
        int at = 0;
        while (at < why.Length)
        {
            int open = why.IndexOf('"', at);
            if (open < 0)
            {
                written.Append(why, at, why.Length - at);
                break;
            }

            int close = why.IndexOf('"', open + 1);
            int end = close < 0 ? why.Length : close;
            written.Append(why, at, open - at).Append('(').Append(Shape(why[(open + 1)..end], shape)).Append(')');
            at = end + 1;
        }

        return written.ToString();
    }

    private static string Node(TextRecipe node, Func<string?, string> shape, int depth, bool isFormat)
    {
        switch (node.Op)
        {
            case TextOp.Literal:
                // A format's own text is the code's (holes and punctuation): as it is unless it has a letter, which a host chose.
                return isFormat && !TextRecipe.HasLetter(node.Text) ? $"\"{SafeText.Clean(node.Text, 40)}\"" : $"a text ({Shape(node.Text, shape)})";
            case TextOp.Format or TextOp.Concat:
                if (depth > TextRecipe.MaxDepth)
                {
                    return "...";
                }

                IEnumerable<string> args = node.Args.Select((arg, i) =>
                    arg == null ? "?" : Node(arg, shape, depth + 1, isFormat: node.Op == TextOp.Format && i == 0));
                return $"{(node.Op == TextOp.Format ? "format" : "concat")}({string.Join(", ", args)})";
            default:
                // Every other node names something of the game's (a term, an object, a stat, a team by which one it is).
                return node.ToString();
        }
    }

    private static string Shape(string? text, Func<string?, string> shape)
    {
        try
        {
            return shape(text);
        }
        catch (Exception)
        {
            return "a text";
        }
    }
}
