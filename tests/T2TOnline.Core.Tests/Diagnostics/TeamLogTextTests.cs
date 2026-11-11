using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class TeamLogTextTests
{
    // The game's own teams have GUID ids, as custom teams do (two of the game's, as its data holds them).
    private const string CalaverasId = "1ca285e4-4945-4406-8c23-c3f2ea4838ee";
    private const string LettuceId = "23b70b6c-b757-4445-a75b-b5c1fc3bc103";

    private static readonly PlayerTextShape Shapes = new(new byte[PlayerTextShape.KeyBytes]);

    private static string Shape(string? text) => Shapes.Describe(text);

    private static GameTeamNames GameTeams()
    {
        var teams = new GameTeamNames();
        teams.Note(new[]
        {
            new KeyValuePair<string?, string?>(CalaverasId, "Calaveras"),
            new KeyValuePair<string?, string?>(LettuceId, "Greasy Lettuce"),
        });
        return teams;
    }

    [Fact]
    public void A_team_of_the_games_goes_by_its_own_name_though_its_id_is_a_guid()
    {
        GameTeamNames teams = GameTeams();
        Assert.Equal("Calaveras", TeamLogText.Of(CalaverasId, "Calaveras", Shape, teams));
        Assert.Equal("Greasy Lettuce", TeamLogText.Of(LettuceId, " Greasy Lettuce ", Shape, teams));
        Assert.Equal("Greasy Lettuce", TeamLogText.Of(LettuceId, string.Empty, Shape, teams)); // no name: the game's
        Assert.Equal(string.Empty, TeamLogText.Of(null, null, Shape, teams));
    }

    [Fact]
    public void A_custom_teams_name_is_written_only_as_its_shape()
    {
        string id = "3702d6cd-2052-4a94-8f6b-b7e765c8b82d";
        string line = TeamLogText.Of(id, "Riley's Rockets", Shape, GameTeams());
        Assert.Equal($"a custom team ({Shape("Riley's Rockets")})", line);
        Assert.DoesNotContain("Riley", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_team_made_with_a_game_teams_id_but_another_name_is_a_shape()
    {
        string line = TeamLogText.Of(CalaverasId, "Riley's Rockets", Shape, GameTeams());
        Assert.Equal($"a custom team ({Shape("Riley's Rockets")})", line);
    }

    [Fact]
    public void Before_the_games_teams_are_read_every_team_is_a_shape()
    {
        var none = new GameTeamNames();
        Assert.Equal($"a custom team ({Shape("Calaveras")})", TeamLogText.Of(CalaverasId, "Calaveras", Shape, none));
        Assert.Equal($"a custom team ({Shape("Calaveras")})", TeamLogText.Of("Calaveras", "Calaveras", Shape, none));
    }

    [Fact]
    public void A_shape_and_the_games_teams_are_needed()
    {
        Assert.Throws<ArgumentNullException>(() => TeamLogText.Of(CalaverasId, "Calaveras", null!, GameTeams()));
        Assert.Throws<ArgumentNullException>(() => TeamLogText.Of(CalaverasId, "Calaveras", Shape, null!));
    }

    [Fact]
    public void The_games_teams_keep_the_first_name_of_an_id_and_skip_blanks()
    {
        var teams = new GameTeamNames();
        Assert.Equal(2, teams.Note(new[]
        {
            new KeyValuePair<string?, string?>(CalaverasId, "Calaveras"),
            new KeyValuePair<string?, string?>(CalaverasId, "Riley's Rockets"),
            new KeyValuePair<string?, string?>(null, "Top Cheese"),
            new KeyValuePair<string?, string?>("26bcb9b9-7e32-461b-9090-3de829377995", null),
            new KeyValuePair<string?, string?>(LettuceId, "Greasy Lettuce"),
        }));
        Assert.Equal(2, teams.Count);
        Assert.True(teams.TryGetName(CalaverasId, "Calaveras", out string name));
        Assert.Equal("Calaveras", name);
        Assert.False(teams.TryGetName(CalaverasId, "Riley's Rockets", out _));
        Assert.Equal(0, teams.Note(new[] { new KeyValuePair<string?, string?>(CalaverasId, "Calaveras") }));
    }

    [Fact]
    public void The_games_teams_are_capped()
    {
        var teams = new GameTeamNames();
        int added = teams.Note(Enumerable.Range(0, GameTeamNames.MaxTeams + 50)
            .Select(i => new KeyValuePair<string?, string?>(Guid.NewGuid().ToString(), $"Team {i}")));
        Assert.Equal(GameTeamNames.MaxTeams, added);
        Assert.Equal(GameTeamNames.MaxTeams, teams.Count);
    }
}
