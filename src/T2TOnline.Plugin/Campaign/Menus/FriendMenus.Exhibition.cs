#if HAS_INTEROP
using Il2CppData;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of Play Now's controller screen beyond its icons (v1): the host's two
/// teams and its rules page on this PC's real screen, followed with the game's own calls (<see cref="ExhibitionScreen"/>). Each
/// is compared with what the screen shows now, so a late change (its own preparing for a kept copy, the friend's own saved
/// rules loading in the background) comes back to the host's.
/// </summary>
internal sealed partial class FriendMenus
{
    private const string HomeSide = "home";
    private const string VisitorSide = "visitor";

    /// <summary>How often the teams and rules are compared while nothing changed (a kept copy's own preparing can end late).</summary>
    private const int ExhibitionEveryFrames = 12;

    /// <summary>For this long after the rules page opens they're compared every frame: this PC's own saved rules load in the background.</summary>
    private const int RulesSettleFrames = 30;

    /// <summary>How long a team that isn't here is left before it's looked for again.</summary>
    private const double TeamRetrySeconds = 0.5;

    /// <summary>A real exhibition screen's frames, once its icons have the host's state: its teams, its rules page.</summary>
    private void ExhibitionTick(View view, ControllerAssignmentMenu open, ControllerView controllers, bool applied)
    {
        ControllerScreenState? screen = controllers.State;
        if (screen is not { Kind: ControllerScreenKind.PlayNow } || open.IsHidden ||
            !(applied || view.Frame % ExhibitionEveryFrames == 0 || view.Frame < controllers.RulesSettleUntil))
        {
            return;
        }

        UseHostContent(view, controllers);
        FollowTeam(view, open, open.m_HomeTeam, screen.HomeTeam, HomeSide, controllers.Home);
        if (view.Mode == Mode.Real)
        {
            FollowTeam(view, open, open.m_VisitorTeam, screen.VisitorTeam, VisitorSide, controllers.Visitor);
        }

        if (view.Mode == Mode.Real && screen.RulesPage)
        {
            FollowRules(view, open, screen, controllers);
        }
    }

    /// <summary>The host's newest list of what its teams need: from now on the game's team build finds those pieces here (as a match's setup makes it).</summary>
    private void UseHostContent(View view, ControllerView controllers)
    {
        MenuAttachment? attachment = _attachments.Get(view.Serial, MenuAttachmentKind.ControllerContent);
        if (attachment == null || ReferenceEquals(attachment, controllers.ContentOf))
        {
            return;
        }

        controllers.ContentOf = attachment;
        if (attachment.TryGetControllerContent(out ControllerContentData? content) && content != null)
        {
            CustomContent.BeginLoad(content.Refs);
        }
    }

    /// <summary>One side's team as the host's: the tab's name, and the team itself once it's here (meanwhile the host's name for it, nothing of another team's).</summary>
    private void FollowTeam(View view, ControllerAssignmentMenu open, ControllerAssignmentTeam? panel, ControllerTeam want, string side, TeamSideView seen)
    {
        if (panel == null)
        {
            return;
        }

        ControllerTeamChange change = ControllerExhibitionPlan.ForTeam(ExhibitionScreen.Shown(panel), want);
        if ((change & ControllerTeamChange.Tab) != 0)
        {
            Try(view, $"showing the host's {side} tab", () => ExhibitionScreen.ShowTab(open, panel, want.Tab));
            if (view.Mode != Mode.Real)
            {
                return;
            }
        }

        // (Only a name shows while it waits: back to a team the panel still holds, it's drawn again all the same.)
        if ((change & ControllerTeamChange.Team) == 0 && seen.WaitingFor.Length == 0)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (seen.WaitingFor == want.Id && now < seen.RetryAt)
        {
            return;
        }

        TeamData? team = ExhibitionScreen.FindTeam(want.Id, out string problem);
        if (team == null)
        {
            seen.RetryAt = now + TeamRetrySeconds;
            if (seen.WaitingFor != want.Id)
            {
                seen.WaitingFor = want.Id;
                string why = problem.Length > 0 ? problem : "this PC's game doesn't have it";
                Try(view, $"showing the host's {side} team's name", () => ExhibitionScreen.WaitingLook(panel, want.Name, waiting: true));
                ModLog.Warn($"Menus: {view.LogId}: {LogNames.HostTag()}'s {side} team {Core.Diagnostics.HostText.KeyForLog(want.Id)} ({LogNames.TextShape(want.Name)}) isn't here ({why}); its name shows on the screen until it is.");
            }

            return;
        }

        if (Try(view, $"showing the host's {side} team", () =>
            {
                ExhibitionScreen.WaitingLook(panel, want.Name, waiting: false);
                ExhibitionScreen.ShowTeam(panel, team, want.ColorHome);
            }))
        {
            seen.WaitingFor = string.Empty;
            if (_controllerLines.TrySpend())
            {
                ModLog.Info($"Menus: {view.LogId}: the {side} team here is {Core.Diagnostics.HostText.KeyForLog(want.Id)}{(want.ColorHome == (side == HomeSide) ? string.Empty : ", colours swapped")}, as on {LogNames.HostTag()}'s screen.");
            }
        }
    }

    /// <summary>The rules page up or down as on the host's screen, and while it's up each rule at the host's choice (its cursor and presses come through the views' own steps).</summary>
    private void FollowRules(View view, ControllerAssignmentMenu open, ControllerScreenState screen, ControllerView controllers)
    {
        switch (ControllerExhibitionPlan.ForRules(screen.InRules, open.m_IsInGameSettings, open.m_GameSettings != null))
        {
            case ControllerRulesStep.Show:
                if (Try(view, "opening the rules page as the host did", () => ExhibitionScreen.ShowRules(open, screen)))
                {
                    controllers.RulesUp = true;
                    controllers.RulesSettleUntil = view.Frame + RulesSettleFrames;

                    // Its own show selects its first row after a delay: the host's cursor again after that.
                    view.ReselectFrame = view.Frame + RulesSettleFrames;
                    Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s rules page is up here too ({ControllerRules.Describe(screen.Rules)}); this PC's own saved rules aren't changed.");
                }

                break;
            case ControllerRulesStep.Hide:
                if (Try(view, "closing the rules page as the host did", () => ExhibitionScreen.HideRules(open)))
                {
                    controllers.RulesUp = false;
                    Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s rules page closed here too.");
                }

                break;
            case ControllerRulesStep.Wait:
                if (!controllers.RulesWaitLogged)
                {
                    controllers.RulesWaitLogged = true;
                    Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s rules page is up; this PC's is still loading.");
                }

                return;
        }

        if (view.Mode == Mode.Real && open.m_IsInGameSettings && screen.Rules.Count > 0)
        {
            Try(view, "setting the rules as the host's", () => ExhibitionScreen.SetRules(open.m_GameSettings, screen.Rules));
        }
    }

    /// <summary>The host left its screen for the match (its exit showed the loading transition): the game's own start sound plays here too, as its exit plays it.</summary>
    private void ExhibitionStarts(View view, MenuEvent? closed)
    {
        if (view.Type == ControllerViews.Type && view.Mode == Mode.Real && closed?.Transition == MenuTransition.Loading)
        {
            Try(view, "playing the match's start sound", GameStartMenusHandler.PlayMatchStartingSound);
            Log($"Menus: {LogNames.HostTag()} started the match from {view.LogId}; the start sound and loading transition play here, and the match loads with the host's setup.");
        }
    }

    /// <summary>A side's panel here, while its team is looked for.</summary>
    private sealed class TeamSideView
    {
        /// <summary>The host's team this panel shows only the name of ("": none).</summary>
        public string WaitingFor { get; set; } = string.Empty;

        /// <summary>When it's looked for again.</summary>
        public double RetryAt { get; set; }
    }
}
#endif
