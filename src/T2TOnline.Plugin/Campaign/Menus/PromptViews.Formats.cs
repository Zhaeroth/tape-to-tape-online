#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using I2LocalizationManager = Il2CppI2.Loc.LocalizationManager;

namespace T2TOnline.Plugin.Campaign.Menus;

// A text the game formats from a term and arguments, in each friend's language (v1): the game's templates (terms with
// holes) are indexed with its terms, a prompt's or toast's text no term has is matched to one (Core FormatRecipes), and a friend
// makes it again from its own copy of the term. Apart from the prompts' own views (PromptViews.cs).
internal static partial class PromptViews
{
    private const int MaxRecipeFailureNotes = 32;

    private static readonly HashSet<string> SaidRecipeFailed = new(StringComparer.Ordinal);
    private static TextTemplates? _templates;

    /// <summary>
    /// The game's text templates in its current language, made with <see cref="Terms"/> (one pass over the translations), or null
    /// when the terms can't be read.
    /// </summary>
    internal static TextTemplates? Templates()
    {
        _ = Terms();
        return _templates;
    }

    /// <summary>The game's one translation term whose text in this PC's language is <paramref name="text"/>, or false: none, several, or the terms can't be read.</summary>
    internal static bool TryUniqueTerm(string? text, out string term)
    {
        term = string.Empty;
        if (string.IsNullOrEmpty(text) || Terms() is not { } terms)
        {
            return false;
        }

        IReadOnlyList<string> found = terms.Candidates(text);
        if (found.Count != 1)
        {
            return false;
        }

        term = found[0];
        return true;
    }

    /// <summary>
    /// The game's language changed (a friend's own Settings menu): the term and template indexes of the old language are dropped,
    /// and made again for the new one at the next need. Words the mod carries are terms and recipes, never a translated text, so
    /// nothing else here holds the old language.
    /// </summary>
    public static void LanguageChanged()
    {
        _terms = null;
        _templates = null;
        _termsFailed = null;
        SaidRecipeFailed.Clear();
    }

    /// <summary>
    /// Host: each text of the prompt left as the host's own (no term has it) is offered to the recipe finder; a text it makes a
    /// recipe of becomes one in <paramref name="made"/> (and in <paramref name="origins"/>). Returns the words for the log
    /// ("; description is a recipe (...)", or why one wasn't made), "" when no text was offered.
    /// </summary>
    private static string AsRecipes(PromptKind kind, string?[] texts, PromptText[] made, PromptTextOrigin[] origins)
    {
        var notes = new List<string>();
        TextTemplates? templates = null;
        for (int i = 0; i < made.Length; i++)
        {
            if (origins[i] != PromptTextOrigin.Literal || string.IsNullOrEmpty(texts[i]))
            {
                continue;
            }

            string name = PromptProvenance.TextName(kind, i);
            try
            {
                templates ??= Templates();
                if (templates == null)
                {
                    notes.Add($"{name} has no recipe (the game's terms couldn't be read)");
                    continue;
                }

                FormatRecipe? built = FormatRecipes.TryBuild(texts[i]!, templates, new TermNames(), new GameTextFunctions(null, null, null), out string why);
                PromptText? text = built == null ? null : PromptText.FromRecipe(built.Recipe, texts[i]!);
                string? problem = text?.Problem(name);
                if (built == null || text == null || problem != null)
                {
                    notes.Add($"{name} has no recipe ({(built == null ? ShownTextLog.Reason(why, LogNames.TextShape) : problem)})");
                    continue;
                }

                made[i] = text;
                origins[i] = PromptTextOrigin.Recipe;
                notes.Add($"{name}'s recipe is {built}");
            }
            catch (Exception ex)
            {
                notes.Add($"{name} has no recipe ({ModLog.SafeErrorText(ex)})");
            }
        }

        return notes.Count == 0 ? string.Empty : "; " + string.Join("; ", notes);
    }

    /// <summary>
    /// Friend: a recipe text made in this game's language, or the host's text when the recipe makes nothing here (a term or name
    /// this game lacks), said once per recipe.
    /// </summary>
    private static string MakeRecipe(PromptText text)
    {
        string? made = text.Recipe?.Evaluate(new GameTextFunctions(null, null, null));
        if (!string.IsNullOrEmpty(made))
        {
            return made;
        }

        string key = text.Recipe?.ToString() ?? text.Value;
        if (SaidRecipeFailed.Count < MaxRecipeFailureNotes && SaidRecipeFailed.Add(key))
        {
            ModLog.Warn($"Menus: the host's prompt text recipe {ShownTextLog.Recipe(text.Recipe, LogNames.TextShape)} made nothing in this PC's language ({SafeText.Clean(I2LocalizationManager.CurrentLanguage, 32)}); " +
                "the host's text is shown (said once per recipe).");
        }

        return text.Value;
    }

    /// <summary>What the host's game knows by name when it looks for a prompt's recipe: a text that is one term's text (a hole holding "Concede") is that term.</summary>
    internal sealed class TermNames : ITextNames
    {
        /// <inheritdoc />
        public TextRecipe? Translated(string text) =>
            TryUniqueTerm(text, out string term) && term.Length <= TextRecipe.MaxTextBytes ? TextRecipe.Term(term) : null;

        /// <inheritdoc />
        public bool IsName(string text) => false;
    }
}
#endif
