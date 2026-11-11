using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class PromptTermsTests
{
    /// <summary>The English texts of the in-match concede prompt's terms, and the five other terms that read "Concede" too (the game's order).</summary>
    public static PromptTerms English()
    {
        var terms = new PromptTerms("English");
        terms.Add("ingame/menu/buttons/concede", "Concede");
        terms.Add("v2/ui/prompt/concede/title", "Concede");
        terms.Add("v2/ui/prompt/concede/desc", "Are you sure you want to concede the game?");
        terms.Add("v2ui/prompt/concede/title", "Concede");
        terms.Add("v2ui/prompt/concede/desc", "Are you sure you want to concede the game?");
        terms.Add("v2ui/run_end/concede", "Concede");
        terms.Add("v2ui/world_map_menu/button/concede", "Concede");
        terms.Add("v2ui/world_map_menu/concede/title", "Concede");
        terms.Add("v2ui/world_map_menu/concede/desc", "Are you sure you want to concede the run?");
        terms.Add("general/confirm", "Confirm");
        terms.Add("general/cancel", "Cancel");
        terms.Add("general/yes", "Yes");
        terms.Add("v2ui/rematch_prompt/yes", "Yes");
        return terms;
    }

    [Fact]
    public void A_text_finds_its_first_term_and_all_of_them()
    {
        PromptTerms terms = English();
        Assert.Equal("English", terms.Language);
        Assert.Equal(13, terms.Count);
        Assert.Equal(7, terms.Shared);
        Assert.True(terms.TryFind("Confirm", out string term));
        Assert.Equal("general/confirm", term);
        Assert.True(terms.TryFind("Concede", out term));
        Assert.Equal("ingame/menu/buttons/concede", term);
        Assert.Equal(new[] { "ingame/menu/buttons/concede", "v2/ui/prompt/concede/title", "v2ui/prompt/concede/title", "v2ui/run_end/concede",
            "v2ui/world_map_menu/button/concede", "v2ui/world_map_menu/concede/title" }, terms.Candidates("Concede"));
        Assert.False(terms.TryFind("confirm", out _)); // exact text only
        Assert.False(terms.TryFind(string.Empty, out _));
        Assert.False(terms.TryFind(null, out _));
        Assert.Empty(terms.Candidates("nothing"));
        Assert.Empty(terms.Candidates(null));
    }

    [Fact]
    public void A_term_given_twice_for_its_text_is_kept_once()
    {
        PromptTerms terms = English();
        Assert.False(terms.Add("general/confirm", "Confirm"));
        Assert.Single(terms.Candidates("Confirm"));
        Assert.Equal(13, terms.Count);
    }

    [Fact]
    public void A_text_keeps_at_most_its_cap_of_terms()
    {
        var terms = new PromptTerms("English");
        for (int i = 0; i < PromptTerms.MaxCandidates; i++)
        {
            Assert.True(terms.Add($"t/{i}", "Same"));
        }

        Assert.False(terms.Add("t/over", "Same"));
        Assert.Equal(PromptTerms.MaxCandidates, terms.Candidates("Same").Count);
        Assert.Equal(PromptTerms.MaxCandidates, terms.Shared); // every later term counted, kept or not
    }

    [Theory]
    [InlineData(null, "Text")]
    [InlineData("", "Text")]
    [InlineData("term", null)]
    [InlineData("term", "")]
    public void Empty_terms_and_texts_arent_kept(string? term, string? text)
    {
        var terms = new PromptTerms("English");
        Assert.False(terms.Add(term, text));
        Assert.Equal(0, terms.Count);
    }

    [Fact]
    public void Over_long_terms_and_texts_arent_kept()
    {
        var terms = new PromptTerms(null!);
        Assert.Equal(string.Empty, terms.Language);
        Assert.False(terms.Add(new string('t', PromptTerms.MaxTermChars + 1), "Text"));
        Assert.False(terms.Add("term", new string('x', PromptTerms.MaxTextChars + 1)));
        Assert.True(terms.Add(new string('t', PromptTerms.MaxTermChars), new string('x', PromptTerms.MaxTextChars)));
    }

    [Fact]
    public void A_full_index_keeps_no_more()
    {
        var terms = new PromptTerms("English");
        foreach (int i in Enumerable.Range(0, PromptTerms.MaxTerms))
        {
            Assert.True(terms.Add($"t{i}", $"x{i}"));
        }

        Assert.True(terms.Full);
        Assert.False(terms.Add("one/more", "One more"));
        Assert.False(terms.Add("one/more", "x1"));
        Assert.Equal(PromptTerms.MaxTerms, terms.Count);
    }
}
