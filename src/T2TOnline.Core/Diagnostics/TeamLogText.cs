using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// How a log line names a team (D127). A team of the game's goes by its own name (<c>Calaveras</c>). A custom team's
/// name is player-written, and its id can't tell it apart: the game gives each team a GUID for its id, its own teams and
/// a custom one alike (<c>TeamRepository.CreateTeam</c> makes a new one for each custom team). So a team goes by its name
/// only when this PC's game lists it as one of its own, under that name (<see cref="GameTeams"/>); any other team goes by
/// the name's shape only (<c>a custom team (11 characters, #4be1)</c>), never the text. Before the game's lists are read,
/// every team is a shape.
/// </summary>
public static class TeamLogText
{
    /// <summary>The game's own teams as this PC's game holds them (the plugin reads them once its game is up).</summary>
    public static GameTeamNames GameTeams { get; } = new();

    /// <summary>
    /// The team with <paramref name="id"/> and <paramref name="name"/> for a log line, judged by <see cref="GameTeams"/>
    /// (<see cref="Of(string?, string?, Func{string?, string}, GameTeamNames)"/>).
    /// </summary>
    /// <param name="id">The team's id (<c>TeamData.id</c>).</param>
    /// <param name="name">The team's name (<c>TeamData.teamName</c>).</param>
    /// <param name="shape">Player-written text as its shape (<see cref="PlayerTextShape.Describe"/>, which never throws).</param>
    public static string Of(string? id, string? name, Func<string?, string> shape) => Of(id, name, shape, GameTeams);

    /// <summary>
    /// The team with <paramref name="id"/> and <paramref name="name"/> for a log line: the game's name for one of
    /// <paramref name="gameTeams"/> under that name, "" for no team at all, <c>a custom team (&lt;shape&gt;)</c> for any
    /// other.
    /// </summary>
    /// <param name="id">The team's id (<c>TeamData.id</c>).</param>
    /// <param name="name">The team's name (<c>TeamData.teamName</c>).</param>
    /// <param name="shape">Player-written text as its shape (<see cref="PlayerTextShape.Describe"/>, which never throws).</param>
    /// <param name="gameTeams">The game's own teams.</param>
    public static string Of(string? id, string? name, Func<string?, string> shape, GameTeamNames gameTeams)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(gameTeams);
        if (gameTeams.TryGetName(id, name, out string gameName))
        {
            return gameName;
        }

        return string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name) ? string.Empty : $"a custom team ({shape(name)})";
    }
}
