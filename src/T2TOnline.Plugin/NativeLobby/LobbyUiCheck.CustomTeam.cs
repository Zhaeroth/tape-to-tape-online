#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppData;
using Il2CppScriptableStates;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// selfdrive's team=custom (Core's UiCheckPlan.SelfDriveWithCustomTeam): the Quick Game played on this PC's first custom team, then this
// launch's session log read for any of its typed names (D127: a custom team's, skaters', goalie's and logo's names are written by their
// shape only). The team is the game's own list of custom teams (ITeamRepository.GetAllCustomTeams, which reads the game's files and
// writes none); its names are held in memory only, never in a line, the summary or a picture's name, and let go when the check is done
// or the tour restores. The pick is the game's own call for a Quick Game's team (TeamSelection.SetQuickMatchTeam, which the controller
// screen's own apply makes; it clones the team in memory), made on the team list the title's Play Now hands the game
// (TitleScreen.m_TeamSelection, the GameStartConfig's), after the controller screen is up: the tour leaves that screen by SetResult and
// FinishExitMenu, which apply nothing of the screen's (verified in the game's code), so the match is played on what the call set. The
// next controller screen puts the player's saved choice back as it opens (ApplySavedTeamSelection). With no custom team on the PC both
// steps say "skipped": nothing is made, since a new custom team would be a write.
internal static partial class LobbyUiCheck
{
    // How long the check waits for the cleaner's count line: the cleaner says it at most once every 10 s (LogNames.Update).
    private const double CleanerLineWaitSeconds = 12;

    private static TeamData? _customTeam;
    private static List<string>? _customNames;

    private static void AddCustomTeamSteps()
    {
        Steps[UiCheckPlan.CustomTeamStep] = StepCustomTeam;
        Steps[UiCheckPlan.CustomNamesStep] = StepCustomNames;
    }

    /// <summary>Everything the custom team steps hold, let go (the names first). From any state.</summary>
    private static void EndCustomTeam()
    {
        _customNames = null;
        _customTeam = null;
    }

    /// <summary>This PC's custom teams counted; the first one's typed names held in memory only; the pick armed for <c>sd_start</c>.</summary>
    private static IEnumerable<Wait> StepCustomTeam()
    {
        EndCustomTeam();
        yield return Frames(1);
        Il2CppSystem.Collections.Generic.List<TeamData>? teams;
        try
        {
            teams = CustomContent.TeamRepository()?.GetAllCustomTeams();
        }
        catch (Exception ex)
        {
            // The game's list has a throwing path of its own (a custom team's file it can't find).
            Problem($"this PC's custom teams couldn't be listed ({ModLog.SafeErrorText(ex)})");
            yield break;
        }

        if (teams == null)
        {
            Problem("the game's team repository isn't there");
            yield break;
        }

        int count = teams.Count;
        TeamData? first = count > 0 ? teams[0] : null;
        if (first == null)
        {
            StepNote("skipped, no custom team on this PC: the Quick Game is played on the game's teams, as without the key, and no log is checked; none is made (that would write a save)");
            yield break;
        }

        (List<string> names, int skaters, bool goalie, bool logo) = TypedNames(first);
        _customTeam = first;
        _customNames = names;
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"custom teams on this PC: {count}; the first one is played: {names.Count} typed name(s) held in memory only ({skaters} custom skater(s), a custom goalie {goalie}, a custom logo {logo}); its pick is armed for the Quick Game's start"));
    }

    /// <summary>In <c>sd_start</c>, the controller screen up: the Quick Game's home team set to the armed custom team by the game's own call. Nothing when none is armed.</summary>
    private static void PlayCustomTeam()
    {
        TeamData? team = _customTeam;
        if (_customNames == null)
        {
            return;
        }

        if (team == null || team.WasCollected)
        {
            Problem("the custom team read before the start has gone: the Quick Game is played on the game's teams");
            return;
        }

        TeamSelection? selection = Title().m_TeamSelection;
        if (selection == null)
        {
            Problem("the title's team list isn't there: the Quick Game is played on the game's teams");
            return;
        }

        selection.SetQuickMatchTeam(team, true);
        // The call puts a copy in (TeamData.Clone): the same id, or at least the same typed name (compared here, never written).
        TeamData? set = selection.homeTeam;
        bool home = set != null && (string.Equals(set.id, team.id, StringComparison.Ordinal) || string.Equals(set.teamName, team.teamName, StringComparison.Ordinal));
        StepNote($"the Quick Game's home team set to the first custom team by the game's own SetQuickMatchTeam: {(home ? "the title's team list holds it" : "the title's team list doesn't hold it")}");
        if (!home)
        {
            Problem("the custom team isn't the Quick Game's home team after the game's own call");
        }
    }

    /// <summary>This launch's session log read for the custom team's typed names, the cleaner's line and the setup's custom team; counts only.</summary>
    private static IEnumerable<Wait> StepCustomNames()
    {
        if (_customNames is not { } names)
        {
            StepNote("skipped, no custom team: nothing to check");
            yield break;
        }

        try
        {
            CustomNameCheck? check = null;
            string problem = string.Empty;
            double until = FrameClock.Seconds + CleanerLineWaitSeconds;
            while (true)
            {
                List<string>? lines = SessionLogLines(0, out problem);
                if (lines != null)
                {
                    check = new CustomNameCheck(names);
                    foreach (string line in lines)
                    {
                        check.Read(line);
                    }
                }

                if (lines == null || (check!.CleanerCount > 0 && check.CustomTeamSetupSeen) || FrameClock.Seconds >= until)
                {
                    break;
                }

                yield return Seconds(1);
            }

            if (check == null)
            {
                Problem(problem);
                yield break;
            }

            StepNote($"the session log ({ModLog.FileName}): {check.Summary()}");
            if (check.Problem() is { } wrong)
            {
                Problem(wrong);
            }
        }
        finally
        {
            EndCustomTeam();
        }
    }

    /// <summary>
    /// A custom team's typed names as the game keeps them, the way the log's cleaner registers them (LogNames.NoteCustomTeam, NoteLogo): its
    /// name, city and short name; each custom skater's and goalie's first and last name and both as the game writes them; its custom logo's
    /// key, file name and file name without <c>.png</c>. In memory only.
    /// </summary>
    private static (List<string> Names, int Skaters, bool Goalie, bool Logo) TypedNames(TeamData team)
    {
        var names = new List<string>();
        void Add(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text) && !names.Contains(text))
            {
                names.Add(text);
            }
        }

        void AddPerson(string? first, string? last)
        {
            Add(first);
            Add(last);
            Add(string.Concat(first, " ", last).Trim());
        }

        Add(team.teamName);
        Add(team.city);
        Add(team.nickname);
        int skaters = 0;
        Il2CppSystem.Collections.Generic.List<ForwardData>? forwards = team.GetForwards(false);
        for (int i = 0; forwards != null && i < forwards.Count; i++)
        {
            if (forwards[i] is { isCustomSkater: true } forward)
            {
                skaters++;
                AddPerson(forward.firstName, forward.lastName);
            }
        }

        bool goalie = team.goalie is { isCustomSkater: true };
        if (goalie)
        {
            AddPerson(team.goalie!.firstName, team.goalie.lastName);
        }

        string? key = team.logo != null ? team.logo.name : null;
        bool logo = key != null && key.StartsWith(Core.Stream.CustomItem.LogoPrefix, StringComparison.Ordinal);
        if (logo)
        {
            string file = Core.Stream.CustomItem.LogoFileName(key!);
            Add(key);
            Add(file);
            Add(file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file);
        }

        return (names, skaters, goalie, logo);
    }
}
#endif
