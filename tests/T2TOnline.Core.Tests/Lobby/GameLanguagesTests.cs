using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The game's language name to the words' column: one rule for the mod's lines and its words in the game's source.</summary>
public class GameLanguagesTests
{
    [Fact]
    public void Each_of_the_games_names_is_its_own_column()
    {
        for (int i = 0; i < LobbyWords.Languages.Count; i++)
        {
            Assert.Equal(i, GameLanguages.IndexOf(LobbyWords.Languages[i]));
        }

        Assert.Equal(LobbyWords.LanguageCount, LobbyWords.Languages.Count);
    }

    [Theory]
    [InlineData("english", 0)]
    [InlineData("PORTUGUESE (BRAZIL)", 5)]
    [InlineData("Spanish (Mexico)", 2)]
    [InlineData("Chinese (Simplified)", 9)]
    [InlineData("Chinese (Traditional)", -1)]
    [InlineData("Portuguese", -1)]
    [InlineData("Klingon", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void Other_names_read_as_their_language_or_none(string? name, int column)
    {
        Assert.Equal(column, GameLanguages.IndexOf(name));
    }

    [Fact]
    public void No_name_starts_another_so_the_order_never_decides()
    {
        foreach (string a in LobbyWords.Languages)
        {
            foreach (string b in LobbyWords.Languages)
            {
                Assert.True(ReferenceEquals(a, b) || !b.StartsWith(a, System.StringComparison.OrdinalIgnoreCase), $"{a} starts {b}");
            }
        }
    }
}
