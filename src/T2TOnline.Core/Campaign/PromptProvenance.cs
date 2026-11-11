using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// One way the host's game makes a prompt's texts: the menu or manager that opens it (<paramref name="Opener"/>, its class name),
/// a few words for the log, the prompt's kind and each text as the game made it (a literal key the caller passes with
/// <c>translateFromKeys</c>, or one of the opener's <c>LocalizedString</c> fields).
/// </summary>
/// <param name="Opener">The opener's class name ("GameMenu").</param>
/// <param name="Name">What the prompt is ("concede").</param>
/// <param name="Kind">The prompt's kind.</param>
/// <param name="Texts">Each text, in the data constructor's order.</param>
public sealed record PromptSource(string Opener, string Name, PromptKind Kind, IReadOnlyList<PromptText> Texts)
{
    /// <summary>For logs: "GameMenu concede".</summary>
    public override string ToString() => $"{Opener} {Name}";
}

/// <summary>How one text of the host's prompt was told apart (for the log).</summary>
public enum PromptTextOrigin
{
    /// <summary>No text.</summary>
    Empty,

    /// <summary>The term the game made it from (an exact <see cref="PromptSource"/>).</summary>
    Exact,

    /// <summary>The one term with this text in the host's language.</summary>
    OnlyTerm,

    /// <summary>The likeliest of several terms with this text (sharing the most of its key with the other texts' terms).</summary>
    BestTerm,

    /// <summary>No term has this text: the host's text itself.</summary>
    Literal,

    /// <summary>The game formatted it from a term and arguments: a recipe (<see cref="FormatRecipes"/>), made in each friend's language.</summary>
    Recipe,
}

/// <summary>
/// Which terms a prompt on the host's screen came from. All 15 callers of the two prompt
/// data constructors a co-op campaign reaches pass terms (literal keys with <c>translateFromKeys</c>, or texts from the opener's
/// <c>LocalizedString</c> fields) and format none of them, and no prompt term has I2 parameters (verified in the game's code); but
/// 13 of the 30 prompt terms share their English text with other terms whose translations differ in some languages, so a text
/// alone can name the wrong term. The host first tries every way the game makes a prompt (<see cref="Match"/>: the literal key
/// sets of <see cref="LiteralSources"/>, and the open menus' <c>LocalizedString</c> fields the game glue reads), each translated
/// as the game made it, against the prompt's texts; only when none gives all of them does it pick per text (<see cref="Guess"/>).
/// Pure logic: the translation is the caller's.
/// </summary>
public static class PromptProvenance
{
    /// <summary>
    /// The prompts the game makes from literal keys (the callers' strings, translated as the data constructor does), by opener
    /// (verified in the game's code: GameMenu; BenchwarmerMenu;
    /// RematchManager, which translates with the constructor's own arguments).
    /// </summary>
    public static IReadOnlyList<PromptSource> LiteralSources { get; } = new[]
    {
        Keys("GameMenu", "concede", PromptKind.YesNo, "v2/ui/prompt/concede/title", "v2/ui/prompt/concede/desc", "general/confirm", "general/cancel"),
        Keys("GameMenu", "quit", PromptKind.YesNo, "general/quit", "v2/ui/prompt/quit/desc", "general/confirm", "general/cancel"),
        Keys("BenchwarmerMenu", "back", PromptKind.YesNo, "general/quit", "v2ui/prompt/quit_generic/desc", "general/yes", "general/no"),
        Keys("BenchwarmerMenu", "open spots", PromptKind.YesNo, "benchwarmers/open_spots_prompt/title", "benchwarmers/open_spots_prompt/message", "general/yes", "general/no"),
        Keys("BenchwarmerMenu", "trio lineup", PromptKind.Dismissable, "benchwarmers/trio_roster_invalid/title", "benchwarmers/trio_roster_invalid/message",
            "benchwarmers/trio_roster_invalid/continue_editing"),
        Keys("RematchManager", "rematch", PromptKind.YesNo, "v2ui/rematch_prompt/title", "v2ui/rematch_prompt/desc", "v2ui/rematch_prompt/yes", "general/quit"),
    };

    /// <summary>
    /// The first of <paramref name="sources"/> (in their order) of the prompt's <paramref name="kind"/> whose every text,
    /// made by <paramref name="translate"/> (the game's own call for its kind, in the host's language), is the prompt's text
    /// (null and "" alike); null when none is.
    /// </summary>
    public static PromptSource? Match(PromptKind kind, IReadOnlyList<string?> texts, IEnumerable<PromptSource> sources, Func<PromptText, string?> translate)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(translate);
        foreach (PromptSource source in sources)
        {
            if (source.Kind != kind || source.Texts.Count != texts.Count)
            {
                continue;
            }

            bool all = true;
            for (int i = 0; i < texts.Count && all; i++)
            {
                all = string.Equals(translate(source.Texts[i]) ?? string.Empty, texts[i] ?? string.Empty, StringComparison.Ordinal);
            }

            if (all)
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// The sources to try, in order: those of <paramref name="openers"/> first (the open menus, newest first, each with its
    /// own sources), then the literal ones of openers that aren't open (a manager, not a menu).
    /// </summary>
    public static List<PromptSource> Order(IEnumerable<(string Opener, IReadOnlyList<PromptSource> Own)> openers)
    {
        ArgumentNullException.ThrowIfNull(openers);
        var ordered = new List<PromptSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string opener, IReadOnlyList<PromptSource> own) in openers)
        {
            ordered.AddRange(own);
            if (seen.Add(opener))
            {
                ordered.AddRange(LiteralSources.Where(s => s.Opener == opener));
            }
        }

        ordered.AddRange(LiteralSources.Where(s => !seen.Contains(s.Opener)));
        return ordered;
    }

    /// <summary>
    /// Each text as a term when the host's language has one for it, else the text itself: the one term with that text, or, of
    /// several, the one sharing the most leading key segments with the other texts' terms (a prompt's terms sit side by side:
    /// "v2/ui/prompt/concede/title" with ".../desc"); for a button (yes, no, dismiss), a "general/" term first. Ties go to the
    /// game's order. <paramref name="terms"/> null: every text literal.
    /// </summary>
    public static PromptText[] Guess(PromptKind kind, IReadOnlyList<string?> texts, PromptTerms? terms, out PromptTextOrigin[] origins)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var made = new PromptText[texts.Count];
        origins = new PromptTextOrigin[texts.Count];
        var candidates = new IReadOnlyList<string>[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            candidates[i] = terms?.Candidates(texts[i]) ?? Array.Empty<string>();
        }

        for (int i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrEmpty(texts[i]))
            {
                (made[i], origins[i]) = (PromptText.Empty, PromptTextOrigin.Empty);
                continue;
            }

            IReadOnlyList<string> own = candidates[i];
            if (own.Count == 0)
            {
                (made[i], origins[i]) = (PromptText.Literal(texts[i]), PromptTextOrigin.Literal);
                continue;
            }

            if (own.Count == 1)
            {
                (made[i], origins[i]) = (PromptText.Term(own[0]), PromptTextOrigin.OnlyTerm);
                continue;
            }

            string best = own.FirstOrDefault(t => IsButton(kind, i) && t.StartsWith("general/", StringComparison.Ordinal)) ?? Likeliest(own, candidates, i);
            (made[i], origins[i]) = (PromptText.Term(best), PromptTextOrigin.BestTerm);
        }

        return made;
    }

    /// <summary>For the log: "best guess for title (of 6), literal for yes" ("" when every text was the only term or empty).</summary>
    public static string Describe(IReadOnlyList<PromptTextOrigin> origins, PromptKind kind)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var parts = new List<string>();
        foreach ((PromptTextOrigin origin, string word) in new[] { (PromptTextOrigin.BestTerm, "best guess for"), (PromptTextOrigin.Recipe, "a recipe for"), (PromptTextOrigin.Literal, "the host's text for") })
        {
            string[] which = Enumerable.Range(0, origins.Count).Where(i => origins[i] == origin).Select(i => TextName(kind, i)).ToArray();
            if (which.Length > 0)
            {
                parts.Add($"{word} {string.Join(", ", which)}");
            }
        }

        return string.Join("; ", parts);
    }

    /// <summary>A text's name by its place: "title", "description", "yes", "no" or "dismiss".</summary>
    public static string TextName(PromptKind kind, int index) => index switch
    {
        0 => "title",
        1 => "description",
        2 => kind == PromptKind.YesNo ? "yes" : "dismiss",
        3 => "no",
        _ => $"text {index + 1}",
    };

    /// <summary>How many leading '/'-separated segments two keys share ("v2/ui/prompt/concede/title", ".../desc": 4).</summary>
    public static int SharedSegments(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        string[] x = a.Split('/');
        string[] y = b.Split('/');
        int shared = 0;
        while (shared < x.Length && shared < y.Length && string.Equals(x[shared], y[shared], StringComparison.Ordinal))
        {
            shared++;
        }

        return shared;
    }

    private static bool IsButton(PromptKind kind, int index) => index >= 2 && (kind == PromptKind.YesNo || index == 2);

    /// <summary>The term of <paramref name="own"/> sharing the most key with any other text's term (the first on a tie).</summary>
    private static string Likeliest(IReadOnlyList<string> own, IReadOnlyList<string>[] candidates, int index)
    {
        string best = own[0];
        int bestScore = -1;
        foreach (string term in own)
        {
            int score = 0;
            for (int j = 0; j < candidates.Length; j++)
            {
                if (j == index)
                {
                    continue;
                }

                foreach (string other in candidates[j])
                {
                    score = Math.Max(score, SharedSegments(term, other));
                }
            }

            if (score > bestScore)
            {
                (best, bestScore) = (term, score);
            }
        }

        return best;
    }

    private static PromptSource Keys(string opener, string name, PromptKind kind, params string[] terms) =>
        new(opener, name, kind, terms.Select(PromptText.Term).ToArray());
}
