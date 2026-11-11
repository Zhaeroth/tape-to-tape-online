using System;
using System.IO;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class UiPartsListTests
{
    [Fact]
    public void Sources_and_parts_are_read_with_their_cells()
    {
        UiPartsList? list = UiPartsList.Parse("# a comment\n@Glossary | ui-menu_glossary | GlossaryMenu\nGlossary | | GlossaryMenu, Animator # the root\nGlossary | ScaledContainer/Header |\n", out string problem);
        Assert.NotNull(list);
        Assert.Empty(problem);
        Assert.Equal(new UiPartSource("Glossary", "ui-menu_glossary", "GlossaryMenu"), Assert.Single(list!.Sources));
        Assert.Equal(2, list.Parts.Count);
        Assert.Empty(list.Parts[0].Path);
        Assert.Equal(new[] { "GlossaryMenu", "Animator" }, list.Parts[0].Components);
        Assert.Equal("ScaledContainer/Header", list.Parts[1].Path);
        Assert.Empty(list.Parts[1].Components);
    }

    [Fact]
    public void A_path_with_spaces_stays_whole()
    {
        UiPartsList? list = UiPartsList.Parse("@G | b | r\nG | ScaledContainer/Content/Scroll View | ScrollRect\n", out _);
        Assert.Equal("ScaledContainer/Content/Scroll View", list!.Parts[0].Path);
    }

    [Theory]
    [InlineData("Glossary | a | b\n", "line 1")]
    [InlineData("@G | b | r\nG | a\n", "line 2")]
    [InlineData("@ | b | r\n", "line 1")]
    [InlineData("@G | | r\n", "line 1")]
    [InlineData("@G | b | r\n@G | b | r\n", "line 2")]
    public void A_bad_line_is_named(string text, string where)
    {
        Assert.Null(UiPartsList.Parse(text, out string problem));
        Assert.Contains(where, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Parts_are_found_by_source()
    {
        UiPartsList? list = UiPartsList.Parse("@A | a | ra\n@B | b | rb\nA | x |\nB | y |\nA | z |\n", out _);
        Assert.Equal(new[] { "x", "z" }, list!.PartsOf("A").Select(p => p.Path));
    }

    [Fact]
    public void The_repos_own_list_parses_and_names_the_shells_animated_parts()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "ui_parts.txt");
        UiPartsList? list = UiPartsList.Parse(File.ReadAllText(file), out string problem);
        Assert.True(list != null, problem);
        string[] glossary = list!.PartsOf("Glossary").Select(p => p.Path).ToArray();
        foreach (string animated in new[] { "ScaledContainer/Header", "ScaledContainer/Tabs", "ScaledContainer/Content", "ScaledContainer/Content/Scroll View", "ScaledContainer/Content/Details", "ScaledContainer/BottomBar" })
        {
            Assert.Contains(animated, glossary);
        }
    }
}
