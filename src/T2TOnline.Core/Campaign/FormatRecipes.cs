using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What the host's game knows by name when it looks for the recipe of a formatted text:
/// the words its hole texts may be, as the node that makes each in a friend's language. The plugin implements it over the game's
/// own objects (a toast's teams, skater, injury and relics; the game's stats and terms); a test over fakes.
/// </summary>
public interface ITextNames
{
    /// <summary>
    /// The node that makes <paramref name="text"/> in a friend's language where the game writes it in the player's language
    /// (a relic's, talent's or ability's name, a stat's keyword name, an injury's, a team's, a unique term's text), or null.
    /// </summary>
    TextRecipe? Translated(string text);

    /// <summary>True for a name the game never translates (a skater's), so a hole that holds it needs nothing.</summary>
    bool IsName(string text);
}

/// <summary>A formatted text's recipe, and how it was made (for the log).</summary>
/// <param name="Recipe">The recipe: the template's term (or the code format) formatted with one node per hole.</param>
/// <param name="Template">The template in words ("term worldmap/notifications/lineup_changed").</param>
/// <param name="Holes">How many holes it has.</param>
/// <param name="Translated">Holes a friend's game makes in its own language (a name, a stat, a term).</param>
/// <param name="Kept">Holes that hold words (not numbers, not a skater's name) no node makes: the host's text, in every language.</param>
public sealed record FormatRecipe(TextRecipe Recipe, string Template, int Holes, int Translated, int Kept)
{
    /// <summary>For logs: "term x/y with 2 hole(s): 1 made in the friend's language, 0 kept as the host's words".</summary>
    public override string ToString() => $"{Template} with {Holes} hole(s): {Translated} made in the friend's language, {Kept} kept as the host's words";
}

/// <summary>
/// Finds the recipe of a text the game made from a template and its arguments (a toast's or a prompt's formatted text), from
/// the shown text alone: the host can't see inside the makers, but it can see which of the game's own templates the text fits
/// and what each hole took (<see cref="TextTemplates"/>), and what each hole's text is (<see cref="ITextNames"/>). The recipe
/// is <c>Format(Term(key) | Literal(code format), node per hole)</c>, sent only when the host's own functions make the shown text
/// from it exactly, so a friend in the same language gets the host's text, and a friend in another gets the same words from its
/// own term and names. Pure logic; never throws for what a function does.
/// </summary>
public static class FormatRecipes
{
    private static readonly Regex Markup = new(@"^(?<open>(?:<[^<>]*>)+)(?<inner>.+?)(?<close>(?:</[^<>]*>)+)\z",
        RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(25));

    /// <summary>
    /// The recipe of <paramref name="shown"/>, or null with <paramref name="why"/> (no template fits, two terms fit equally,
    /// the recipe doesn't make the shown text exactly with <paramref name="functions"/>, or its limits). Of a term's plural
    /// forms the first that makes the text is used.
    /// </summary>
    public static FormatRecipe? TryBuild(string shown, TextTemplates templates, ITextNames names, ITextFunctions functions, out string why)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(functions);
        why = string.Empty;
        if (shown.Length == 0)
        {
            why = "it is empty";
            return null;
        }

        IReadOnlyList<TextTemplateMatch> matches = templates.Match(shown);
        if (matches.Count == 0)
        {
            why = "no term of the game has this shape";
            return null;
        }

        TextTemplate best = matches[0].Template;
        TextTemplate? rival = matches.Select(m => m.Template).FirstOrDefault(t => t.Key != best.Key && t.LiteralChars == best.LiteralChars);
        if (rival != null)
        {
            why = $"{best} and {rival} fit it equally";
            return null;
        }

        string? last = null;
        foreach (TextTemplateMatch match in matches.Where(m => m.Template.Key == best.Key))
        {
            FormatRecipe? built = Build(shown, match, names, functions, out last);
            if (built != null)
            {
                return built;
            }
        }

        why = last ?? "no form of it makes the text";
        return null;
    }

    private static FormatRecipe? Build(string shown, TextTemplateMatch match, ITextNames names, ITextFunctions functions, out string why)
    {
        why = string.Empty;
        int translated = 0;
        int kept = 0;
        var nodes = new TextRecipe[match.Holes.Count + 1];
        TextTemplate template = match.Template;
        nodes[0] = template.Term != null ? TextRecipe.Term(template.Term) : TextRecipe.Literal(template.Text);
        for (int i = 0; i < match.Holes.Count; i++)
        {
            nodes[i + 1] = Node(match.Holes[i], names, ref translated, ref kept);
        }

        TextRecipe recipe = TextRecipe.Format(nodes[0], nodes.Skip(1).ToArray());
        string? problem = recipe.Check();
        if (problem != null)
        {
            why = $"{template} gives a recipe out of its limits ({problem})";
            return null;
        }

        string? made = recipe.Evaluate(functions);
        if (made == null)
        {
            why = $"{template} makes nothing here (a term or name this game lacks)";
            return null;
        }

        if (!string.Equals(made, shown, StringComparison.Ordinal))
        {
            // Not the made text itself: it holds the shown text's arguments, which can be a name a player typed.
            why = $"{template} makes another text here ({Diagnostics.HostText.TextShape(made)}), not the shown one";
            return null;
        }

        return new FormatRecipe(recipe, template.ToString(), match.Holes.Count, translated, kept);
    }

    /// <summary>One hole's text as a node: what the game names, that inside markup around it (<c>&lt;color=green&gt;name&lt;/color&gt;</c>), or its own text.</summary>
    private static TextRecipe Node(string hole, ITextNames names, ref int translated, ref int kept)
    {
        if (!hole.Any(char.IsLetter))
        {
            return TextRecipe.Literal(hole); // a number: the same in every language, whatever a term's text may be
        }

        TextRecipe? node = SafeTranslated(names, hole);
        if (node != null)
        {
            translated++;
            return node;
        }

        try
        {
            Match wrapped = Markup.Match(hole);
            if (wrapped.Success && SafeTranslated(names, wrapped.Groups["inner"].Value) is { } inner)
            {
                translated++;
                return TextRecipe.Concat(TextRecipe.Literal(wrapped.Groups["open"].Value), inner, TextRecipe.Literal(wrapped.Groups["close"].Value));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // a text too odd to look inside: kept whole
        }

        kept += SafeIsName(names, hole) ? 0 : 1;
        return TextRecipe.Literal(hole);
    }

    private static bool SafeIsName(ITextNames names, string text)
    {
        try
        {
            return names.IsName(text);
        }
        catch (Exception)
        {
            return false; // counted as words kept in the host's language
        }
    }

    private static TextRecipe? SafeTranslated(ITextNames names, string text)
    {
        try
        {
            return names.Translated(text);
        }
        catch (Exception)
        {
            return null; // the game's lookup threw: the text stays the host's
        }
    }
}
