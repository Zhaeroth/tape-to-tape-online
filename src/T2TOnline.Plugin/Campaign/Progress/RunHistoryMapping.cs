#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppData;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppState.RunHistory;
using Il2CppTape2Tape.Customization;
using Il2CppTape2Tape.Hockey;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// The game's run history entry (<c>RunHistoryEntryModel</c> and the models it holds) to and from
/// <see cref="RunHistoryData"/>, field by field. The host reads its game's entry as it's saved; a friend's game gets a new
/// entry of the game's own types with the host's values, and saves it through its own run history repository. Text and
/// lists longer than the wire allows are cut (the game's are short).
/// </summary>
internal static class RunHistoryMapping
{
    /// <summary>Why a match is left out of an entry made for this game, worded to follow "N match(es) ... left out: ".</summary>
    public const string MatchesLeftOutWhy =
        "they name a team this game doesn't have (or its teams couldn't be read), and its run history page can't show a match without its team";

    /// <summary>Lines about matches left out said per launch by <see cref="ToGame(RunHistoryData)"/> (a host's page can come at every step).</summary>
    private const int LeftOutLinesSaid = 3;

    private static int _leftOutLines;

    /// <summary>The host's entry as plain data.</summary>
    public static RunHistoryData ToData(RunHistoryEntryModel entry) => new()
    {
        RunId = Cut(entry.runID),
        SquadId = Cut(entry.squadId),
        DifficultyLevel = entry.difficultyLevel,
        GameRule = (int)entry.gameRule,
        IsWin = entry.isWin,
        IsConceded = entry.isConceded,
        TimestampUtc = entry.timestampUtc,
        RunTime = entry.runTime,
        SquadName = Cut(entry.squadName),
        Team = entry.teamSnapshot == null ? null : Team(entry.teamSnapshot),
        Scorers = Items(entry.scorersStats).Select(Scorer).ToList(),
        Matches = Items(entry.matchScores).Select(Match).ToList(),
        Goalie = entry.goalieStats == null ? null : Goalie(entry.goalieStats),
        SticksBroken = entry.sticksBroken,
        InjuriesInflicted = entry.injuriesInflicted,
        InjuriesReceived = entry.injuriesReceived,
        TotalCritShots = entry.totalCritShots,
    };

    /// <summary>
    /// <see cref="ToGame(RunHistoryData, out int)"/>, for a caller with no line of its own for the matches left out: the
    /// first few times any are, the log says how many.
    /// </summary>
    public static RunHistoryEntryModel ToGame(RunHistoryData data)
    {
        RunHistoryEntryModel entry = ToGame(data, out int leftOut);
        if (leftOut > 0 && _leftOutLines < LeftOutLinesSaid)
        {
            _leftOutLines++;
            ModLog.Warn($"Run history: {leftOut} of the {data.Matches.Count} match(es) of a host's run are left out of the entry made for this game: " +
                $"{MatchesLeftOutWhy}{(_leftOutLines == LeftOutLinesSaid ? " (not said again this launch)" : string.Empty)}.");
        }

        return entry;
    }

    /// <summary>
    /// A new entry of the game's own types, with <paramref name="data"/>'s values. Its time is always a date: the game's
    /// run-end menu makes a <c>DateTime</c> of it when it shows the entry, and throws for a number that isn't one. Its
    /// matches are the ones that menu can show (<see cref="RunHistorySafety.ShownMatches"/>): it finds each match's
    /// opponent among this game's teams and throws for one it doesn't find, so a match against a team this game doesn't
    /// have is left out (<paramref name="matchesLeftOut"/>: how many; none for an honest host, whose opponents are the
    /// game's own teams).
    /// </summary>
    public static RunHistoryEntryModel ToGame(RunHistoryData data, out int matchesLeftOut)
    {
        IReadOnlyList<MatchLine> matches = RunHistorySafety.ShownMatches(data.Matches, GameTeams());
        matchesLeftOut = data.Matches.Count - matches.Count;
        var entry = new RunHistoryEntryModel
        {
            runID = data.RunId,
            squadId = data.SquadId,
            difficultyLevel = data.DifficultyLevel,
            gameRule = (GameRule)data.GameRule,
            isWin = data.IsWin,
            isConceded = data.IsConceded,
            timestampUtc = RunHistorySafety.AsDate(data.TimestampUtc),
            runTime = data.RunTime,
            squadName = data.SquadName,
            teamSnapshot = data.Team == null ? null : Team(data.Team),
            scorersStats = ToGameList(data.Scorers.Select(Scorer)),
            matchScores = ToGameList(matches.Select(Match)),
            goalieStats = data.Goalie == null ? null : Goalie(data.Goalie),
            sticksBroken = data.SticksBroken,
            injuriesInflicted = data.InjuriesInflicted,
            injuriesReceived = data.InjuriesReceived,
            totalCritShots = data.TotalCritShots,
        };
        return entry;
    }

    /// <summary>
    /// Whether this game has a team, by id, as its run-end menu looks one up (<c>RunEndMenu.FindTeam</c>: the campaign's
    /// teams, then every team of the repository with the campaign-exclusive ones, which are the campaign's, the league's,
    /// the campaign-exclusive and this PC's custom teams). The game's own teams are read once per entry
    /// (<c>GetBuiltInTeams</c>); only an id that isn't one of them is asked of the repository (<c>GetTeam</c>: the
    /// campaign's, the league's and the custom teams). False for every id when the teams can't be read. Never throws.
    /// </summary>
    private static Func<string, bool> GameTeams()
    {
        ITeamRepository? repository = null;
        HashSet<string>? builtIn = null;
        bool read = false;
        return id =>
        {
            try
            {
                if (!read)
                {
                    read = true;
                    Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<ITeamRepository>());
                    repository = found == null ? null : new ITeamRepository(found.Pointer);
                    if (repository != null)
                    {
                        var ids = new HashSet<string>(StringComparer.Ordinal);
                        Il2CppSystem.Collections.Generic.List<TeamData>? teams = repository.GetBuiltInTeams(true, true, true);
                        for (int i = 0; teams != null && i < teams.Count; i++)
                        {
                            TeamData? team = teams[i];
                            if (team != null && team.id is { Length: > 0 } teamId)
                            {
                                ids.Add(teamId);
                            }
                        }

                        builtIn = ids;
                    }
                }

                return repository != null && builtIn != null && (builtIn.Contains(id) || repository.GetTeam(id) != null);
            }
            catch (Exception)
            {
                return false; // the match is left out, and its count says so
            }
        };
    }

    private static TeamSnapshot Team(RunHistoryTeamModel team) => new()
    {
        Id = Cut(team.id),
        TeamName = Cut(team.teamName),
        City = Cut(team.city),
        Nickname = Cut(team.nickname),
        LogoName = Cut(team.logoName),
        PrimaryColor = Rgba(team.primaryColorPlayer),
        SecondaryColor = Rgba(team.secondaryColorPlayer),
        Forwards = Items(team.forwards).Select(Forward).ToList(),
        Goalie = team.goalie == null ? null : new GoalieSnapshot
        {
            Id = Cut(team.goalie.id),
            FirstName = Cut(team.goalie.firstName),
            LastName = Cut(team.goalie.lastName),
            HeadSkin = Cut(team.goalie.headSkin),
            AbilityId = Cut(team.goalie.abilityId),
            TalentIds = Texts(team.goalie.talentIds),
        },
        RelicIds = Texts(team.relicIds),
    };

    private static RunHistoryTeamModel Team(TeamSnapshot team) => new()
    {
        id = team.Id,
        teamName = team.TeamName,
        city = team.City,
        nickname = team.Nickname,
        logoName = team.LogoName,
        primaryColorPlayer = ToColor(team.PrimaryColor),
        secondaryColorPlayer = ToColor(team.SecondaryColor),
        forwards = ToGameList(team.Forwards.Select(Forward)),
        goalie = team.Goalie == null ? null : new RunHistoryGoalieModel
        {
            id = team.Goalie.Id,
            firstName = team.Goalie.FirstName,
            lastName = team.Goalie.LastName,
            headSkin = team.Goalie.HeadSkin,
            abilityId = team.Goalie.AbilityId,
            talentIds = Texts(team.Goalie.TalentIds),
        },
        relicIds = Texts(team.RelicIds),
    };

    private static ForwardSnapshot Forward(RunHistoryForwardModel forward) => new()
    {
        Id = Cut(forward.id),
        FirstName = Cut(forward.firstName),
        LastName = Cut(forward.lastName),
        HeadSkin = Cut(forward.headSkin),
        HelmetSkin = Cut(forward.helmetSkin),
        BodySkin = Cut(forward.bodySkin),
        DisplayOverall = forward.displayOverall,
        DisplaySpeed = forward.displaySpeed,
        DisplayAccuracy = forward.displayAccuracy,
        DisplayShotPower = forward.displayShotPower,
        DisplayChecking = forward.displayChecking,
        AbilityId = Cut(forward.abilityId),
        TalentIds = Texts(forward.talentIds),
        IsDeparted = forward.isDeparted,
        SkaterCategory = (int)forward.skaterCategory,
    };

    private static RunHistoryForwardModel Forward(ForwardSnapshot forward) => new()
    {
        id = forward.Id,
        firstName = forward.FirstName,
        lastName = forward.LastName,
        headSkin = forward.HeadSkin,
        helmetSkin = forward.HelmetSkin,
        bodySkin = forward.BodySkin,
        displayOverall = forward.DisplayOverall,
        displaySpeed = forward.DisplaySpeed,
        displayAccuracy = forward.DisplayAccuracy,
        displayShotPower = forward.DisplayShotPower,
        displayChecking = forward.DisplayChecking,
        abilityId = forward.AbilityId,
        talentIds = Texts(forward.TalentIds),
        isDeparted = forward.IsDeparted,
        skaterCategory = (SkaterCategory)forward.SkaterCategory,
    };

    private static ScorerLine Scorer(ScorersStats line) => new()
    {
        SkaterId = Cut(line.skaterId),
        DisplayName = Cut(line.displayName),
        Goals = line.goals,
        LastGoalOrder = line.lastGoalOrder,
        Assists = line.assists,
        Shots = line.shots,
        Hits = line.hits,
        InjuriesReceived = line.injuriesReceived,
    };

    private static ScorersStats Scorer(ScorerLine line) => new()
    {
        skaterId = line.SkaterId,
        displayName = line.DisplayName,
        goals = line.Goals,
        lastGoalOrder = line.LastGoalOrder,
        assists = line.Assists,
        shots = line.Shots,
        hits = line.Hits,
        injuriesReceived = line.InjuriesReceived,
    };

    private static MatchLine Match(MatchScore match)
    {
        var scorers = new List<KeyValuePair<string, ScorerLine>>();
        Il2CppSystem.Collections.Generic.Dictionary<string, ScorersStats>? byKey = match.scorersStats;
        if (byKey != null)
        {
            Il2CppSystem.Collections.Generic.Dictionary<string, ScorersStats>.Enumerator items = byKey.GetEnumerator();
            while (items.MoveNext() && scorers.Count < RunHistoryData.MaxItems)
            {
                Il2CppSystem.Collections.Generic.KeyValuePair<string, ScorersStats> item = items.Current;
                if (item.Value != null)
                {
                    scorers.Add(new KeyValuePair<string, ScorerLine>(Cut(item.Key) ?? string.Empty, Scorer(item.Value)));
                }
            }
        }

        return new MatchLine
        {
            Act = match.act,
            TeamId = Cut(match.teamId),
            Score = Cut(match.score),
            Win = match.Win,
            Shootout = match.Shootout,
            IsBoss = match.IsBoss,
            SelfScore = match.SelfScore,
            OtherScore = match.OtherScore,
            MapPointX = match.MapPoint.x,
            MapPointY = match.MapPoint.y,
            Scorers = scorers,
            GoalieStats = match.goalieStats == null ? null : Goalie(match.goalieStats),
        };
    }

    private static MatchScore Match(MatchLine match)
    {
        var scorers = new Il2CppSystem.Collections.Generic.Dictionary<string, ScorersStats>();
        foreach (KeyValuePair<string, ScorerLine> item in match.Scorers)
        {
            scorers[item.Key] = Scorer(item.Value); // a repeated key keeps the last, as the game's own dictionary would
        }

        return new MatchScore
        {
            act = match.Act,
            teamId = match.TeamId,
            score = match.Score,
            Win = match.Win,
            Shootout = match.Shootout,
            IsBoss = match.IsBoss,
            SelfScore = match.SelfScore,
            OtherScore = match.OtherScore,
            MapPoint = new Vector2(match.MapPointX, match.MapPointY),
            scorersStats = scorers,
            goalieStats = match.GoalieStats == null ? null : Goalie(match.GoalieStats),
        };
    }

    private static GoalieLine Goalie(GoalieStats line) => new()
    {
        SkaterId = Cut(line.skaterId),
        DisplayName = Cut(line.displayName),
        GamesPlayed = line.gamesPlayed,
        GoalsAgainst = line.goalsAgainst,
        ShotsAgainst = line.shotsAgainst,
        Hits = line.hits,
    };

    private static GoalieStats Goalie(GoalieLine line) => new()
    {
        skaterId = line.SkaterId,
        displayName = line.DisplayName,
        gamesPlayed = line.GamesPlayed,
        goalsAgainst = line.GoalsAgainst,
        shotsAgainst = line.ShotsAgainst,
        hits = line.Hits,
    };

    private static float[] Rgba(Color color) => new[] { color.r, color.g, color.b, color.a };

    private static Color ToColor(IReadOnlyList<float> rgba) => rgba.Count == 4 ? new Color(rgba[0], rgba[1], rgba[2], rgba[3]) : Color.white;

    private static IEnumerable<T> Items<T>(Il2CppSystem.Collections.Generic.List<T>? list)
        where T : Il2CppSystem.Object
    {
        if (list == null)
        {
            yield break;
        }

        for (int i = 0; i < list.Count && i < RunHistoryData.MaxItems; i++)
        {
            if (list[i] != null)
            {
                yield return list[i];
            }
        }
    }

    private static List<string> Texts(Il2CppSystem.Collections.Generic.List<string>? list)
    {
        var texts = new List<string>();
        for (int i = 0; list != null && i < list.Count && texts.Count < RunHistoryData.MaxItems; i++)
        {
            texts.Add(Cut(list[i]) ?? string.Empty);
        }

        return texts;
    }

    private static Il2CppSystem.Collections.Generic.List<string> Texts(IReadOnlyList<string> texts)
    {
        var list = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (string text in texts)
        {
            list.Add(text);
        }

        return list;
    }

    private static Il2CppSystem.Collections.Generic.List<T> ToGameList<T>(IEnumerable<T> items)
        where T : Il2CppSystem.Object
    {
        var list = new Il2CppSystem.Collections.Generic.List<T>();
        foreach (T item in items)
        {
            list.Add(item);
        }

        return list;
    }

    /// <summary>Text cut to what the wire carries (at a character boundary).</summary>
    private static string? Cut(string? text)
    {
        if (text == null || System.Text.Encoding.UTF8.GetByteCount(text) <= RunHistoryData.MaxTextBytes)
        {
            return text;
        }

        int length = text.Length;
        while (length > 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > RunHistoryData.MaxTextBytes)
        {
            length--;
        }

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}
#endif
