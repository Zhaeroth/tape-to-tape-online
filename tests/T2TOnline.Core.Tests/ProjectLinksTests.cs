using System;
using System.Linq;

namespace T2TOnline.Core.Tests;

public class ProjectLinksTests
{
    [Fact]
    public void The_pages_are_the_one_repository_and_its_tails()
    {
        Assert.StartsWith("https://", ProjectLinks.Repository, StringComparison.Ordinal);
        Assert.False(ProjectLinks.Repository.EndsWith('/'));
        Assert.Equal(ProjectLinks.Repository + "/issues", ProjectLinks.Issues);
        Assert.Equal(ProjectLinks.Repository + "/releases", ProjectLinks.Releases);
        Assert.Equal(ProjectLinks.Releases + "/latest", ProjectLinks.LatestRelease);
    }

    [Fact]
    public void The_short_forms_are_the_pages_without_the_scheme()
    {
        Assert.Equal(ProjectLinks.IssuesShort, ProjectLinks.Issues["https://".Length..]);
        Assert.Equal(ProjectLinks.ReleasesShort, ProjectLinks.Releases["https://".Length..]);
    }

    [Fact]
    public void The_short_forms_use_only_characters_every_language_draws()
    {
        // Letters, digits and . / - are in every font of every language (the checker proves it against the game's own fonts: lobbywords_check.py).
        foreach (string text in new[] { ProjectLinks.IssuesShort, ProjectLinks.ReleasesShort })
        {
            Assert.All(text, c => Assert.True(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '/' or '-', $"'{c}' in {text}"));
            Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_page_is_one_line_without_spaces()
    {
        foreach (string text in new[] { ProjectLinks.Repository, ProjectLinks.Issues, ProjectLinks.Releases, ProjectLinks.LatestRelease, ProjectLinks.IssuesShort, ProjectLinks.ReleasesShort })
        {
            Assert.False(text.Any(char.IsWhiteSpace), text);
        }
    }
}
