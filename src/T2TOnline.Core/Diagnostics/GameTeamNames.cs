using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The game's own teams as this PC's game holds them, by id with the name the game gives each, for
/// <see cref="TeamLogText"/>. Every team has a GUID for its id, the game's and a custom one alike, so an id alone can't
/// tell a custom team: a team is one of the game's only when this PC read its id from the game's own lists, and only under
/// the game's name for it. At most <see cref="MaxTeams"/> teams. Thread-safe: the map is replaced whole on a change.
/// </summary>
public sealed class GameTeamNames
{
    /// <summary>The most teams kept (the game has a few dozen).</summary>
    public const int MaxTeams = 512;

    private readonly object _gate = new();
    private volatile Dictionary<string, string> _names = new(StringComparer.Ordinal);

    /// <summary>How many teams are known.</summary>
    public int Count => _names.Count;

    /// <summary>
    /// Adds <paramref name="teams"/>, each the game's own team's id and name; one with no id or no name is left out, an id
    /// known already keeps its first name, and past <see cref="MaxTeams"/> the rest are left out. How many were new.
    /// </summary>
    public int Note(IEnumerable<KeyValuePair<string?, string?>> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        lock (_gate)
        {
            Dictionary<string, string>? next = null;
            foreach (KeyValuePair<string?, string?> team in teams)
            {
                string? id = team.Key?.Trim();
                string? name = team.Value?.Trim();
                Dictionary<string, string> current = next ?? _names;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || current.ContainsKey(id) || current.Count >= MaxTeams)
                {
                    continue;
                }

                next ??= new Dictionary<string, string>(_names, StringComparer.Ordinal);
                next[id] = name;
            }

            if (next == null)
            {
                return 0;
            }

            int added = next.Count - _names.Count;
            _names = next;
            return added;
        }
    }

    /// <summary>
    /// The game's name for the team with <paramref name="id"/>, when it is one of the game's and <paramref name="name"/> is
    /// that name (or is empty). A team made with a game team's id but another name is not the game's.
    /// </summary>
    public bool TryGetName(string? id, string? name, out string gameName)
    {
        gameName = string.Empty;
        if (string.IsNullOrEmpty(id) || !_names.TryGetValue(id.Trim(), out string? known))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(name) && !string.Equals(name.Trim(), known, StringComparison.Ordinal))
        {
            return false;
        }

        gameName = known;
        return true;
    }
}
