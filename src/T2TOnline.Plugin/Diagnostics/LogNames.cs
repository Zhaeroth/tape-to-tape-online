#if HAS_INTEROP
using System.Collections.Generic;
using Il2CppData;
using Il2CppState;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>
/// How the campaign, match, stream and input parts name players and player-written text in their log lines (D127): a
/// player as their tag under the lobby's key (<see cref="LogTags"/>), the host a friend follows as the host's tag, a
/// custom team's, skater's or logo's name as its length and a hash keyed per launch. Names stay on screen only, in the
/// game's own UI; the parts that keep a host's name for their screens use <see cref="HostTag"/> for their logs. Behind
/// those sources, the names custom content brings are registered with the log's backstop (<see cref="NotePlayerText"/>),
/// and the game's own teams are read for <see cref="TeamLogText"/> (<see cref="Update"/>).
/// </summary>
internal static class LogNames
{
    private static readonly PlayerTextShape Texts = PlayerTextShape.ForLaunch();
    private static readonly PlayerWrittenNames Written = new();
    private static bool _playableRead;
    private static bool _campaignRead;
    private static double _nextCheck;
    private static double _nextTextsLine;
    private static int _textsLogged;

    /// <summary><paramref name="steamId"/>'s tag (<c>P#3fa9c1</c>), or <c>nobody</c> for 0.</summary>
    public static string Tag(ulong steamId) => steamId == 0 ? "nobody" : ModLog.Scrub.Tags.Player(steamId);

    /// <summary>
    /// The tag of the host this PC follows (<c>P#3fa9c1</c>), or <c>the host</c> when it follows nobody now (on the host's
    /// own PC, before the link is up, after the session). Read at each line: tags change with the lobby.
    /// </summary>
    public static string HostTag()
    {
        ulong host = CampaignRelay.Current?.FollowedHost ?? 0;
        return host != 0 ? Tag(host) : "the host";
    }

    /// <summary>Player-written <paramref name="text"/> as its shape: <c>12 characters, #4be1</c> (<see cref="PlayerTextShape"/>).</summary>
    public static string TextShape(string? text) => Texts.Describe(text);

    /// <summary>
    /// A game object for a log line. The game names a skater's object after the skater (first and last name:
    /// <c>GameControllerBase.InitializeForward</c>), and a custom skater's name is typed by its player, so a skater's
    /// object never goes by its name: it goes as <see cref="Match.SkaterLogText.Of"/> writes every skater (<c>skater
    /// 0f2b7633-...</c>, the same on the host and on a friend), and its name is kept for <see cref="ReferenceText"/>. Any
    /// other object goes by its own name (its prefab's, or one the game's code gave it). <c>?</c> for no object. Never throws.
    /// </summary>
    public static string ObjectName(UnityEngine.GameObject? gameObject)
    {
        string name;
        Il2CppSkaters.Forward? forward;
        try
        {
            if (gameObject == null)
            {
                return "?";
            }

            name = gameObject.name ?? "?";
            forward = gameObject.GetComponent<Il2CppSkaters.Forward>();
        }
        catch (System.Exception)
        {
            return "?";
        }

        return forward == null ? name : Match.SkaterLogText.Of(forward);
    }

    /// <summary>The name of <paramref name="component"/>'s object for a log line (<see cref="ObjectName(UnityEngine.GameObject?)"/>). Never throws.</summary>
    public static string ObjectName(UnityEngine.Component? component)
    {
        try
        {
            return component != null ? ObjectName(component.gameObject) : "?";
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>
    /// A text that names objects (a scene path, an asset's name, a stable key) for a log line: every skater's name this
    /// PC's lines have met (<see cref="NoteSkaterNames"/>) is written as its shape.
    /// </summary>
    public static string ReferenceText(string? text) => Written.Hide(text, TextShape);

    /// <summary>Keeps a skater's object name and its data's name for <see cref="ReferenceText"/> (a line about the skater met them).</summary>
    public static void NoteSkaterNames(string? objectName, string? dataName)
    {
        Written.Note(objectName);
        Written.Note(dataName);
    }

    /// <summary>
    /// Registers <paramref name="text"/>, a name a player typed (a custom team's, skater's, goalie's or logo's), with the
    /// log's backstop: from now on any line that slips it writes <c>a custom name (12 characters, #4be1)</c>
    /// (<see cref="LogScrub.RegisterPlayerText"/>). A known text costs a look. Never throws.
    /// </summary>
    public static void NotePlayerText(string? text)
    {
        try
        {
            ModLog.Scrub.RegisterPlayerText(text, TextShape);
        }
        catch (System.Exception)
        {
            // A name that can't be registered is only a weaker backstop: the sources still write its shape.
        }
    }

    /// <summary>A custom skater's or goalie's names as the game writes them (<c>first + " " + last</c>, its object's name) for the backstop. Never throws.</summary>
    public static void NotePlayerName(string? firstName, string? lastName)
    {
        if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName))
        {
            NotePlayerText(string.Concat(firstName, " ", lastName));
        }
    }

    /// <summary>A custom logo's key (<c>custom_&lt;file&gt;.png</c>), its file's name and that name without <c>.png</c>, for the backstop. Never throws.</summary>
    public static void NoteLogo(string? key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Core.Stream.CustomItem.LogoPrefix, System.StringComparison.Ordinal))
        {
            return;
        }

        string file = Core.Stream.CustomItem.LogoFileName(key);
        NotePlayerText(key);
        NotePlayerText(file);
        NotePlayerText(file.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ? file[..^4] : file);
    }

    /// <summary>A custom team's typed names as the game keeps them (its name, city and short name), for the backstop. Never throws.</summary>
    public static void NoteCustom(Il2CppTape2Tape.Customization.TeamDataModel? model)
    {
        try
        {
            if (model != null)
            {
                NotePlayerText(model.TeamName);
                NotePlayerText(model.City);
                NotePlayerText(model.nickname);
            }
        }
        catch (System.Exception)
        {
            // Only a weaker backstop.
        }
    }

    /// <summary>
    /// A custom skater's typed names as the game keeps them (its first and last name), for the backstop. Not its data's
    /// <c>name</c>: nothing proves it isn't the skater's id, which every skater line writes on purpose. Never throws.
    /// </summary>
    public static void NoteCustom(Il2CppTape2Tape.Customization.ForwardDataModel? model)
    {
        try
        {
            if (model != null)
            {
                NotePlayerName(model.firstName, model.lastName);
            }
        }
        catch (System.Exception)
        {
            // Only a weaker backstop.
        }
    }

    /// <summary>A custom goalie's typed names as the game keeps them (its first and last name), for the backstop. Never throws.</summary>
    public static void NoteCustom(Il2CppTape2Tape.Customization.GoalieDataModel? model)
    {
        try
        {
            if (model != null)
            {
                NotePlayerName(model.firstName, model.lastName);
            }
        }
        catch (System.Exception)
        {
            // Only a weaker backstop.
        }
    }

    /// <summary>
    /// A custom team's typed names (its name, city and short name), and its custom skaters' and goalie's (the game's own
    /// mark, <c>isCustomSkater</c>, tells them from the game's skaters a custom team may hold), for the backstop. Never throws.
    /// </summary>
    public static void NoteCustomTeam(TeamData? team)
    {
        try
        {
            if (team == null)
            {
                return;
            }

            NotePlayerText(team.teamName);
            NotePlayerText(team.city);
            NotePlayerText(team.nickname);
            Il2CppSystem.Collections.Generic.List<ForwardData>? forwards = team.GetForwards(false);
            for (int i = 0; forwards != null && i < forwards.Count; i++)
            {
                ForwardData? forward = forwards[i];
                if (forward != null && forward.isCustomSkater)
                {
                    NotePlayerName(forward.firstName, forward.lastName);
                }
            }

            GoaltenderData? goalie = team.goalie;
            if (goalie != null && goalie.isCustomSkater)
            {
                NotePlayerName(goalie.firstName, goalie.lastName);
            }
        }
        catch (System.Exception)
        {
            // Only a weaker backstop.
        }
    }

    /// <summary>
    /// A campaign's player-written names for the backstop: this PC's custom campaign team (the game's own field for it),
    /// and the run's team when it wears a custom logo (only a custom team can: so a host's custom campaign team on a
    /// friend's copy of the run). Never throws.
    /// </summary>
    public static void NoteRunNames(CampaignState? campaign)
    {
        try
        {
            NoteCustomTeam(campaign?.customCampaignTeam);
            TeamData? run = campaign?.runData?.teamData;
            string? logo = run != null && run.logo != null ? run.logo.name : null;
            if (logo != null && logo.StartsWith(Core.Stream.CustomItem.LogoPrefix, System.StringComparison.Ordinal))
            {
                NoteLogo(logo);
                NoteCustomTeam(run);
            }
        }
        catch (System.Exception)
        {
            // Only a weaker backstop: the sources write these names by their shape.
        }
    }

    /// <summary>
    /// Call once per frame (from launch): reads the game's own teams for <see cref="TeamLogText"/> once its lists are up
    /// (<see cref="NoteGameTeams"/>), and says how many custom names the log's backstop knows when that changed (at most
    /// one line every 10 s). Two field reads once the teams are read and no name is new. Never throws.
    /// </summary>
    public static void Update()
    {
        int texts = ModLog.Scrub.PlayerTextCount;
        if (_playableRead && _campaignRead && texts == _textsLogged)
        {
            return;
        }

        double now = Streaming.FrameClock.Seconds;
        if (now < _nextCheck)
        {
            return;
        }

        _nextCheck = now + 1;
        if (texts != _textsLogged && now >= _nextTextsLine)
        {
            _textsLogged = texts;
            _nextTextsLine = now + 10;
            int refused = ModLog.Scrub.RefusedPlayerTexts;
            ModLog.Info($"Log: the log's cleaner knows {texts} custom name(s) of teams, skaters, goalies and logos; a line that slips one writes \"{LogScrub.PlayerTextLabel} (<length>, #<hash>)\"" +
                $"{(refused > 0 ? $" ({refused} more refused over its limit of {LogScrub.MaxPlayerTexts})" : string.Empty)}.");
        }

        if (!_playableRead || !_campaignRead)
        {
            NoteGameTeams();
        }
    }

    /// <summary>
    /// Reads the game's own teams for <see cref="TeamLogText"/>: the playable teams (the exhibition's, the campaign's and the
    /// league's) and each squad's starting team and campaign elites and bosses, each part once it is up. A failure is logged
    /// once and stops the tries (a team not read is then a shape, as before). Never throws.
    /// </summary>
    private static void NoteGameTeams()
    {
        try
        {
            if (!_playableRead && Match.CustomContent.TeamRepository()?.TryCast<Il2CppTape2Tape.Customization.TeamRepository>()?.m_playableTeams is { } playable)
            {
                var teams = new List<TeamData?>();
                AddAll(teams, playable.campaignTeams);
                AddAll(teams, playable.leagueTeams);
                AddAll(teams, playable.campaignExclusiveTeams);
                teams.Add(playable.onboardingTeam);
                int added = TeamLogText.GameTeams.Note(Pairs(teams));
                _playableRead = TeamLogText.GameTeams.Count > 0;
                if (_playableRead)
                {
                    ModLog.Info($"Log: the game's own teams are known for the log's team names ({added} playable teams).");
                }
            }

            if (!_campaignRead && Campaign.CampaignGame.State() is { squads: { } squads })
            {
                var teams = new List<TeamData?>();
                for (int i = 0; i < squads.Count; i++)
                {
                    RunSquadScriptableObject? squad = squads[i];
                    if (squad == null)
                    {
                        continue;
                    }

                    teams.Add(squad.startingTeam);
                    if (squad.campaignOpponents is { } opponents)
                    {
                        AddElites(teams, opponents.act1Elites);
                        AddElites(teams, opponents.act2Elites);
                        AddElites(teams, opponents.act3Elites);
                        AddBosses(teams, opponents.act1Bosses);
                        AddBosses(teams, opponents.act2Bosses);
                        AddBosses(teams, opponents.act3Bosses);
                    }
                }

                int added = TeamLogText.GameTeams.Note(Pairs(teams));
                _campaignRead = squads.Count > 0;
                if (_campaignRead)
                {
                    ModLog.Info($"Log: the campaign's own teams are known for the log's team names ({added} more from {squads.Count} squads' starting teams, elites and bosses).");
                }
            }
        }
        catch (System.Exception ex)
        {
            _playableRead = _campaignRead = true;
            ModLog.Warn($"Log: the game's own teams couldn't be read for the log's team names ({ModLog.SafeErrorText(ex)}); a team the log hasn't read is written by its shape.");
        }
    }

    private static void AddAll(List<TeamData?> into, Il2CppSystem.Collections.Generic.List<TeamData>? teams)
    {
        for (int i = 0; teams != null && i < teams.Count; i++)
        {
            into.Add(teams[i]);
        }
    }

    private static void AddElites(List<TeamData?> into, Il2CppSystem.Collections.Generic.List<ActElite>? elites)
    {
        for (int i = 0; elites != null && i < elites.Count; i++)
        {
            into.Add(elites[i]?.team);
        }
    }

    private static void AddBosses(List<TeamData?> into, Il2CppSystem.Collections.Generic.List<ActBoss>? bosses)
    {
        for (int i = 0; bosses != null && i < bosses.Count; i++)
        {
            into.Add(bosses[i]?.team);
        }
    }

    private static List<KeyValuePair<string?, string?>> Pairs(List<TeamData?> teams)
    {
        var pairs = new List<KeyValuePair<string?, string?>>(teams.Count);
        foreach (TeamData? team in teams)
        {
            if (team != null)
            {
                pairs.Add(new KeyValuePair<string?, string?>(team.id, team.teamName));
            }
        }

        return pairs;
    }
}
#endif
