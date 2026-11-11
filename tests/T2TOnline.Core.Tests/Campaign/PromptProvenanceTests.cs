using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class PromptProvenanceTests
{
    /// <summary>English texts by term, as the host's game would make them (the literal sources' terms and a few others).</summary>
    private static readonly Dictionary<string, string> EnglishText = new(StringComparer.Ordinal)
    {
        ["v2/ui/prompt/concede/title"] = "Concede",
        ["v2/ui/prompt/concede/desc"] = "Are you sure you want to concede the game?",
        ["v2ui/prompt/concede/title"] = "Concede",
        ["v2ui/prompt/concede/desc"] = "Are you sure you want to concede the game?",
        ["v2ui/world_map_menu/concede/title"] = "Concede",
        ["v2ui/world_map_menu/concede/desc"] = "Are you sure you want to concede the run?",
        ["general/confirm"] = "Confirm",
        ["general/cancel"] = "Cancel",
        ["general/quit"] = "Quit",
        ["v2/ui/prompt/quit/desc"] = "Are you sure you want to quit the game?",
        ["v2ui/prompt/quit_generic/desc"] = "Are you sure you want to quit?",
        ["general/yes"] = "Yes",
        ["general/no"] = "No",
        ["benchwarmers/open_spots_prompt/title"] = "Open Spots",
        ["benchwarmers/open_spots_prompt/message"] = "Your team still has open spots. Start the run anyway?",
        ["benchwarmers/trio_roster_invalid/title"] = "Trio Lineup Incomplete",
        ["benchwarmers/trio_roster_invalid/message"] = "Trio Squad requires 3 skaters in the lineup before continuing.",
        ["benchwarmers/trio_roster_invalid/continue_editing"] = "Continue Editing",
        ["v2ui/rematch_prompt/title"] = "Game Complete",
        ["v2ui/rematch_prompt/desc"] = "What would you like to do?",
        ["v2ui/rematch_prompt/yes"] = "Rematch",
    };

    /// <summary>The host's translation: a term's English text (a localized term the same, here), a literal itself.</summary>
    private static string? Translate(PromptText text) => text.Kind switch
    {
        PromptTextKind.Empty => string.Empty,
        PromptTextKind.Literal => text.Value,
        _ => EnglishText.TryGetValue(text.Value, out string? made) ? made : null,
    };

    private static string?[] TextsOf(PromptSource source) => source.Texts.Select(Translate).ToArray();

    /// <summary>The map menu's concede, made from its own LocalizedString fields.</summary>
    private static PromptSource MapConcede() => new("WorldMapMenu", "concede", PromptKind.YesNo, new[]
    {
        PromptText.Localized("v2ui/world_map_menu/concede/title"), PromptText.Localized("v2ui/world_map_menu/concede/desc"),
        PromptText.Localized("general/confirm"), PromptText.Localized("general/cancel"),
    });

    [Fact]
    public void Every_literal_source_matches_its_own_texts()
    {
        Assert.Equal(6, PromptProvenance.LiteralSources.Count);
        foreach (PromptSource source in PromptProvenance.LiteralSources)
        {
            Assert.All(source.Texts, t => Assert.Equal(PromptTextKind.Term, t.Kind));
            Assert.All(source.Texts, t => Assert.True(EnglishText.ContainsKey(t.Value), t.Value));
            Assert.Equal(source.Kind == PromptKind.YesNo ? 4 : 3, source.Texts.Count);
            PromptSource? found = PromptProvenance.Match(source.Kind, TextsOf(source), PromptProvenance.Order(Array.Empty<(string, IReadOnlyList<PromptSource>)>()), Translate);
            Assert.Same(source, found);
        }
    }

    [Fact]
    public void A_shared_text_resolves_by_the_prompt_it_came_from()
    {
        // "Concede" is several terms; the in-match prompt's own keys and the map menu's own fields each match only their texts.
        string?[] inMatch = { "Concede", "Are you sure you want to concede the game?", "Confirm", "Cancel" };
        string?[] onMap = { "Concede", "Are you sure you want to concede the run?", "Confirm", "Cancel" };
        List<PromptSource> sources = PromptProvenance.Order(new[] { ("WorldMapMenu", (IReadOnlyList<PromptSource>)new[] { MapConcede() }), ("GameMenu", Array.Empty<PromptSource>()) });

        PromptSource? game = PromptProvenance.Match(PromptKind.YesNo, inMatch, sources, Translate);
        Assert.Equal("GameMenu concede", game?.ToString());
        Assert.Equal("v2/ui/prompt/concede/title", game!.Texts[0].Value);

        PromptSource? map = PromptProvenance.Match(PromptKind.YesNo, onMap, sources, Translate);
        Assert.Equal("WorldMapMenu concede", map?.ToString());
        Assert.Equal(PromptTextKind.LocalizedTerm, map!.Texts[0].Kind);
    }

    [Fact]
    public void A_source_that_differs_in_one_text_or_its_kind_doesnt_match()
    {
        List<PromptSource> sources = PromptProvenance.Order(Array.Empty<(string, IReadOnlyList<PromptSource>)>());
        Assert.Null(PromptProvenance.Match(PromptKind.YesNo, new[] { "Concede", "Are you sure you want to concede the game?", "Confirm", "Nope" }, sources, Translate));
        Assert.Null(PromptProvenance.Match(PromptKind.Dismissable, new[] { "Concede", "Are you sure you want to concede the game?", "Confirm" }, sources, Translate));
        Assert.Null(PromptProvenance.Match(PromptKind.YesNo, new[] { "Concede", "Are you sure you want to concede the game?", "Confirm" }, sources, Translate));

        // Null and "" are alike: a source with an empty text matches a prompt with none there.
        var bare = new PromptSource("Menu", "bare", PromptKind.Dismissable, new[] { PromptText.Empty, PromptText.Term("general/yes"), PromptText.Empty });
        Assert.Same(bare, PromptProvenance.Match(PromptKind.Dismissable, new string?[] { null, "Yes", string.Empty }, new[] { bare }, Translate));
    }

    [Fact]
    public void Open_openers_go_first_with_their_literal_sources_and_the_rest_after()
    {
        var own = new PromptSource("EventChoiceMenu", "exit", PromptKind.YesNo, new[] { PromptText.Empty, PromptText.Empty, PromptText.Empty, PromptText.Empty });
        List<PromptSource> ordered = PromptProvenance.Order(new[]
        {
            ("BenchwarmerMenu", (IReadOnlyList<PromptSource>)Array.Empty<PromptSource>()),
            ("EventChoiceMenu", (IReadOnlyList<PromptSource>)new[] { own }),
            ("BenchwarmerMenu", (IReadOnlyList<PromptSource>)Array.Empty<PromptSource>()),
        });

        Assert.Equal(new[] { "BenchwarmerMenu back", "BenchwarmerMenu open spots", "BenchwarmerMenu trio lineup", "EventChoiceMenu exit", "GameMenu concede", "GameMenu quit", "RematchManager rematch" },
            ordered.Select(s => s.ToString()));
    }

    [Fact]
    public void A_guess_picks_the_terms_that_sit_together()
    {
        PromptTerms terms = PromptTermsTests.English();
        PromptText[] inMatch = PromptProvenance.Guess(PromptKind.YesNo, new[] { "Concede", "Are you sure you want to concede the game?", "Confirm", "Cancel" }, terms, out PromptTextOrigin[] how);
        Assert.Equal(new[] { "v2/ui/prompt/concede/title", "v2/ui/prompt/concede/desc", "general/confirm", "general/cancel" }, inMatch.Select(t => t.Value));
        Assert.All(inMatch, t => Assert.Equal(PromptTextKind.Term, t.Kind));
        Assert.Equal(new[] { PromptTextOrigin.BestTerm, PromptTextOrigin.BestTerm, PromptTextOrigin.OnlyTerm, PromptTextOrigin.OnlyTerm }, how);

        PromptText[] onMap = PromptProvenance.Guess(PromptKind.YesNo, new[] { "Concede", "Are you sure you want to concede the run?", "Confirm", "Cancel" }, terms, out _);
        Assert.Equal("v2ui/world_map_menu/concede/title", onMap[0].Value);

        // A button with a "general/" term takes it; a text with no term goes as the host's; an empty one as nothing.
        PromptText[] rematch = PromptProvenance.Guess(PromptKind.YesNo, new[] { "Game Complete", null, "Yes", string.Empty }, terms, out how);
        Assert.Equal(PromptText.Literal("Game Complete"), rematch[0]);
        Assert.Same(PromptText.Empty, rematch[1]);
        Assert.Equal(PromptText.Term("general/yes"), rematch[2]);
        Assert.Same(PromptText.Empty, rematch[3]);
        Assert.Equal(new[] { PromptTextOrigin.Literal, PromptTextOrigin.Empty, PromptTextOrigin.BestTerm, PromptTextOrigin.Empty }, how);
        Assert.Equal("best guess for yes; the host's text for title", PromptProvenance.Describe(how, PromptKind.YesNo));

        // A title is no button: of several terms with no other text to sit with, the game's first.
        PromptText[] alone = PromptProvenance.Guess(PromptKind.Dismissable, new[] { "Yes", null, null }, terms, out _);
        Assert.Equal("general/yes", alone[0].Value);

        // Without the game's terms, every text is the host's.
        PromptText[] none = PromptProvenance.Guess(PromptKind.Dismissable, new[] { "Lineup", "Pick five.", "OK" }, null, out how);
        Assert.All(none, t => Assert.Equal(PromptTextKind.Literal, t.Kind));
        Assert.Equal("the host's text for title, description, dismiss", PromptProvenance.Describe(how, PromptKind.Dismissable));
        Assert.Equal(string.Empty, PromptProvenance.Describe(new[] { PromptTextOrigin.OnlyTerm, PromptTextOrigin.Exact, PromptTextOrigin.Empty }, PromptKind.Dismissable));
    }

    [Theory]
    [InlineData("v2/ui/prompt/concede/title", "v2/ui/prompt/concede/desc", 4)]
    [InlineData("v2ui/prompt/concede/title", "v2/ui/prompt/concede/desc", 0)]
    [InlineData("general/yes", "general/no", 1)]
    [InlineData("a/b", "a/b", 2)]
    [InlineData("a/bc", "a/b", 1)]
    [InlineData("", "a", 0)]
    public void Shared_segments_count_whole_segments(string a, string b, int shared) => Assert.Equal(shared, PromptProvenance.SharedSegments(a, b));

    [Fact]
    public void Text_names_follow_the_constructor_order()
    {
        Assert.Equal(new[] { "title", "description", "yes", "no" }, Enumerable.Range(0, 4).Select(i => PromptProvenance.TextName(PromptKind.YesNo, i)));
        Assert.Equal(new[] { "title", "description", "dismiss" }, Enumerable.Range(0, 3).Select(i => PromptProvenance.TextName(PromptKind.Dismissable, i)));
        Assert.Equal("text 5", PromptProvenance.TextName(PromptKind.YesNo, 4));
    }
}
