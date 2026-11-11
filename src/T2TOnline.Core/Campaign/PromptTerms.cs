using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The game's translation terms by their text in one language (the host's), to tell which terms a prompt's shown texts may
/// have come from: a friend's game then makes the prompt from those terms,
/// in its own language. A prompt's data on the host holds texts only (its keys were translated, or came from a
/// <c>LocalizedString</c>), and the yes/no prompt's data constructor can't be patched (its <c>Nullable</c> argument is passed
/// by value, which Il2CppInterop's detour reads as an address). Several terms can give the same text in one language while
/// their translations differ in another ("Concede" is six terms in English), so every term of a text is kept, in the game's
/// order (up to <see cref="MaxCandidates"/>): <see cref="PromptProvenance"/> picks among them. Empty texts and texts longer
/// than <see cref="MaxTextChars"/> aren't kept. Not thread-safe; built once per language.
/// </summary>
public sealed class PromptTerms
{
    /// <summary>Most terms kept (the game has a few thousand).</summary>
    public const int MaxTerms = 100_000;

    /// <summary>Longest text kept, in characters (a prompt's description is a few lines).</summary>
    public const int MaxTextChars = 2048;

    /// <summary>Longest term kept, in characters.</summary>
    public const int MaxTermChars = 256;

    /// <summary>Most terms kept for one text (later ones are counted in <see cref="Shared"/> only).</summary>
    public const int MaxCandidates = 8;

    private readonly Dictionary<string, List<string>> _byText = new(StringComparer.Ordinal);
    private int _count;

    /// <summary>Terms of <paramref name="language"/> (the language the texts are in, for the log and to tell when it changed).</summary>
    public PromptTerms(string language)
    {
        Language = language ?? string.Empty;
    }

    /// <summary>The language the texts are in.</summary>
    public string Language { get; }

    /// <summary>How many terms are kept.</summary>
    public int Count => _count;

    /// <summary>How many terms gave a text another term had given first.</summary>
    public int Shared { get; private set; }

    /// <summary>True once <see cref="MaxTerms"/> terms are kept (later ones are dropped).</summary>
    public bool Full => _count >= MaxTerms;

    /// <summary>
    /// Keeps <paramref name="term"/> as a term of <paramref name="text"/>. False when it isn't kept: an empty or over-long term
    /// or text, a term already kept for the text, a text with <see cref="MaxCandidates"/> terms already (counted as shared), or
    /// the index full.
    /// </summary>
    public bool Add(string? term, string? text)
    {
        if (string.IsNullOrEmpty(term) || term.Length > MaxTermChars || string.IsNullOrEmpty(text) || text.Length > MaxTextChars || Full)
        {
            return false;
        }

        if (!_byText.TryGetValue(text, out List<string>? terms))
        {
            _byText[text] = new List<string>(1) { term };
            _count++;
            return true;
        }

        if (terms.Contains(term))
        {
            return false;
        }

        Shared++;
        if (terms.Count >= MaxCandidates)
        {
            return false;
        }

        terms.Add(term);
        _count++;
        return true;
    }

    /// <summary>The first term (in the game's order) whose text is <paramref name="text"/> in this language, or false.</summary>
    public bool TryFind(string? text, out string term)
    {
        term = string.Empty;
        if (string.IsNullOrEmpty(text) || !_byText.TryGetValue(text, out List<string>? found))
        {
            return false;
        }

        term = found[0];
        return true;
    }

    /// <summary>Every term kept whose text is <paramref name="text"/> in this language, in the game's order (none for none).</summary>
    public IReadOnlyList<string> Candidates(string? text) =>
        !string.IsNullOrEmpty(text) && _byText.TryGetValue(text, out List<string>? found) ? found : Array.Empty<string>();
}
