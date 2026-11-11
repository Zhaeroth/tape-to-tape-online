using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// One shape the game makes a text from: a translation term's text with holes
/// (<c>Line {0} on the ice</c>), or a format the game's code carries (<c>\t+{0} {1}</c>). A term whose text has plural markup
/// (<c>[i2p_One]...[i2p_Plural]...</c>) is one template per form, all with the term's key. A template has at least one hole, the
/// holes are <c>{0}</c> to <c>{n}</c> with nothing but literal text around them (two holes side by side can't be told apart), and
/// a term's literal text has at least <see cref="MinLetters"/> letters ("{0} - {1}" and "{0}/{1}" fit nearly any text). Immutable.
/// </summary>
public sealed class TextTemplate
{
    /// <summary>Fewest letters a term's literal text must have.</summary>
    public const int MinLetters = 3;

    /// <summary>Most holes (the format of a recipe takes at most <see cref="TextRecipe.MaxArgs"/> arguments).</summary>
    public const int MaxHoles = TextRecipe.MaxArgs;

    /// <summary>Longest template text kept, in characters.</summary>
    public const int MaxChars = 512;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);

    // The template's parts in order: a literal (Hole -1) or a hole.
    private readonly (string Literal, int Hole)[] _parts;
    private Regex? _regex;

    private TextTemplate(string? term, string text, (string, int)[] parts, int holes, int literalChars)
    {
        Term = term;
        Text = text;
        _parts = parts;
        Holes = holes;
        LiteralChars = literalChars;
    }

    /// <summary>The term's key, or null for a format the game's code carries.</summary>
    public string? Term { get; }

    /// <summary>The template as the game holds it (a plural term's whole text, with its tags; the form is matched).</summary>
    public string Text { get; }

    /// <summary>How many different holes it has (<c>{0}</c> to <c>{Holes - 1}</c>).</summary>
    public int Holes { get; }

    /// <summary>The characters between its holes: how specific it is (the longest template that fits a text wins).</summary>
    public int LiteralChars { get; }

    /// <summary>What two templates of one term share: the term's key, or the code format's text.</summary>
    public string Key => Term ?? "code:" + Text;

    /// <summary>For logs: "term worldmap/notifications/lineup_changed" or "the format \"\t+{0} {1}\"".</summary>
    public override string ToString() => Term != null ? $"term {Diagnostics.SafeText.Clean(Term, 64)}" : $"the format \"{Diagnostics.SafeText.Clean(Text, 40)}\"";

    /// <summary>
    /// The template of one form of a text, or null when it isn't one (no hole, a brace that isn't <c>{n}</c>, holes not 0 to n
    /// each used, two holes side by side, too few letters, too long, too many holes). <paramref name="term"/> null: a format the
    /// code carries (it may have no letters).
    /// </summary>
    public static TextTemplate? Parse(string? term, string? form, string? whole = null)
    {
        if (string.IsNullOrEmpty(form) || form.Length > MaxChars)
        {
            return null;
        }

        var parts = new List<(string, int)>();
        var literal = new StringBuilder();
        int highest = -1;
        var seen = new HashSet<int>();
        bool lastWasHole = false;
        int letters = 0;
        int literalChars = 0;
        for (int i = 0; i < form.Length; i++)
        {
            char c = form[i];
            if (c == '{')
            {
                int j = i + 1;
                int number = 0;
                while (j < form.Length && form[j] is >= '0' and <= '9' && j - i <= 2)
                {
                    number = (number * 10) + (form[j] - '0');
                    j++;
                }

                if (j == i + 1 || j >= form.Length || form[j] != '}' || number >= MaxHoles)
                {
                    return null;
                }

                if (literal.Length > 0)
                {
                    parts.Add((literal.ToString(), -1));
                    literal.Clear();
                }
                else if (lastWasHole)
                {
                    return null;
                }

                parts.Add((string.Empty, number));
                seen.Add(number);
                highest = Math.Max(highest, number);
                lastWasHole = true;
                i = j;
                continue;
            }

            if (c == '}')
            {
                return null;
            }

            literal.Append(c);
            literalChars++;
            letters += char.IsLetter(c) ? 1 : 0;
            lastWasHole = false;
        }

        if (literal.Length > 0)
        {
            parts.Add((literal.ToString(), -1));
        }

        if (seen.Count == 0 || seen.Count != highest + 1 || (term != null && letters < MinLetters))
        {
            return null;
        }

        return new TextTemplate(term, whole ?? form, parts.ToArray(), seen.Count, literalChars);
    }

    /// <summary>
    /// The text each hole took (<c>Holes</c> of them, in hole order) when <paramref name="shown"/> is this template's text, or
    /// false. A hole takes at least one character and, with a literal after it, as few as will do. Never throws.
    /// </summary>
    public bool TryMatch(string shown, out string[] holes)
    {
        holes = Array.Empty<string>();
        if (shown.Length < LiteralChars + Holes)
        {
            return false;
        }

        foreach ((string literal, int hole) in _parts)
        {
            if (hole < 0 && !shown.Contains(literal, StringComparison.Ordinal))
            {
                return false;
            }
        }

        try
        {
            Match match = (_regex ??= Build()).Match(shown);
            if (!match.Success)
            {
                return false;
            }

            var taken = new string[Holes];
            for (int i = 0; i < taken.Length; i++)
            {
                taken[i] = match.Groups["h" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)].Value;
            }

            holes = taken;
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private Regex Build()
    {
        var pattern = new StringBuilder("^");
        var opened = new HashSet<int>();
        foreach ((string literal, int hole) in _parts)
        {
            if (hole < 0)
            {
                pattern.Append(Regex.Escape(literal));
                continue;
            }

            string name = "h" + hole.ToString(System.Globalization.CultureInfo.InvariantCulture);
            pattern.Append(opened.Add(hole) ? $"(?<{name}>.+?)" : $"\\k<{name}>");
        }

        pattern.Append("\\z");
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout);
    }
}

/// <summary>A template a text fits, and what its holes took.</summary>
/// <param name="Template">The template.</param>
/// <param name="Holes">The text each hole took, in hole order.</param>
public sealed record TextTemplateMatch(TextTemplate Template, IReadOnlyList<string> Holes);

/// <summary>
/// The game's text templates in one language (the host's): its translation terms whose text has holes, and the two formats its
/// code carries for the toasts' stat lines (<see cref="CodeFormats"/>). A text the game made from one of them is matched back
/// to it (<see cref="Match"/>), so a friend's game can make the text again from its own copy of the term. Built once per
/// language (a few hundred templates among the few thousand terms). Not thread-safe.
/// </summary>
public sealed class TextTemplates
{
    /// <summary>Most templates kept (the game has a few hundred).</summary>
    public const int MaxTemplates = 4096;

    /// <summary>Most matches <see cref="Match"/> returns.</summary>
    public const int MaxMatches = 16;

    private static readonly Regex PluralTag = new(@"\[i2p_[A-Za-z0-9]+\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));

    private readonly List<TextTemplate> _all = new();

    /// <summary>Templates of <paramref name="language"/> (the language the texts are in, for the log and to tell when it changed).</summary>
    public TextTemplates(string language)
    {
        Language = language ?? string.Empty;
        _all.AddRange(CodeFormats);
    }

    /// <summary>
    /// The formats the game's code carries (not terms; the same in every language): the team bonus toast's <c>"\t+{0} {1}"</c> and the
    /// Missing Toe relic's <c>"\t{0} {1}"</c>, each a number and a stat's keyword name (verified in the game's code:
    /// NotificationQueue.QueueTeamBonusNotificationFromRelic, VoodooDollController.PushNotification).
    /// </summary>
    public static IReadOnlyList<TextTemplate> CodeFormats { get; } = new[]
    {
        TextTemplate.Parse(null, "\t+{0} {1}")!,
        TextTemplate.Parse(null, "\t{0} {1}")!,
    };

    /// <summary>The language the texts are in.</summary>
    public string Language { get; }

    /// <summary>How many templates (forms) are kept, the code formats included.</summary>
    public int Count => _all.Count;

    /// <summary>
    /// Keeps <paramref name="text"/> as the template of term <paramref name="term"/>, one per plural form. False when it isn't
    /// one (no hole, markup this can't match, too few letters) or the index is full.
    /// </summary>
    public bool Add(string? term, string? text)
    {
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text) || text.IndexOf('{') < 0 || text.Length > TextTemplate.MaxChars * 4 || _all.Count >= MaxTemplates)
        {
            return false;
        }

        bool added = false;
        try
        {
            if (text.Contains("[i2p_", StringComparison.Ordinal))
            {
                string[] pieces = PluralTag.Split(text);
                for (int i = 1; i < pieces.Length; i++)
                {
                    added |= AddForm(term, pieces[0] + pieces[i], text);
                }

                return added;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return AddForm(term, text, text);
    }

    /// <summary>
    /// Every template <paramref name="shown"/> fits, the most specific first (the most literal characters; the game's order among
    /// equals): at most <see cref="MaxMatches"/>. Never throws.
    /// </summary>
    public IReadOnlyList<TextTemplateMatch> Match(string? shown)
    {
        if (string.IsNullOrEmpty(shown))
        {
            return Array.Empty<TextTemplateMatch>();
        }

        var found = new List<TextTemplateMatch>();
        foreach (TextTemplate template in _all)
        {
            if (template.TryMatch(shown, out string[] holes))
            {
                found.Add(new TextTemplateMatch(template, holes));
            }
        }

        return found.OrderByDescending(m => m.Template.LiteralChars).Take(MaxMatches).ToList();
    }

    private bool AddForm(string term, string form, string whole)
    {
        TextTemplate? template = TextTemplate.Parse(term, form, whole);
        if (template == null || _all.Count >= MaxTemplates)
        {
            return false;
        }

        _all.Add(template);
        return true;
    }
}
