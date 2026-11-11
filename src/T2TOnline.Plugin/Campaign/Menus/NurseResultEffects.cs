#if HAS_INTEROP
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppRogue;
using Il2CppSkaters.Injuries;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Core.Campaign;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The game calls for what a friend's procedure screen leaves out at its result, the commit being skipped there (v1;
/// <see cref="NurseResultPlan"/> says which by procedure): the same calls its popups make on their committed branch, on the
/// same pieces, none reaching a commit, a save or the run (their callees checked). <c>_committed</c>
/// itself stays false: <c>BindPanel</c> would read this PC's unchanged run with it set. Each call may throw: the caller catches it.
/// </summary>
internal static class NurseResultEffects
{
    /// <summary>
    /// What each build of the result puts back, its panels being bound anew: the title, the healed icons, the surgery's tooltips and
    /// the clone's cost.
    /// </summary>
    public static void EachBuild(NurseProcedureScreen screen, NurseResultPlan plan)
    {
        NursePanelView panel = screen._panelB;
        if (plan.ProcedureTitle)
        {
            screen._injuryPopup.SetTitle(NurseProcedureScreen.GetProcedureName(NurseProcedureState.Procedure.FrozenPeas));
        }

        if (plan.HealedIcons)
        {
            ShowHealed(panel);
        }

        if (plan.SurgeryTooltips)
        {
            ShowSurgeryTooltips(screen, panel);
        }

        if (plan.CloneCost)
        {
            ShowCloneCost(screen, panel);
        }
    }

    /// <summary>What plays once, at the result's first build: the hit, the heal's sound, a punch, and the screen's own timed close.</summary>
    public static void Once(NurseProcedureScreen screen, NurseResultPlan plan)
    {
        if (plan.Hit != NurseResultShake.None)
        {
            NursePopupView popup = plan.Hit == NurseResultShake.InjuryPopup ? screen._injuryPopup : screen._swapPopup;
            screen.PlayHit(popup._shakeRoot);
        }

        if (plan.HealSound)
        {
            NurseProcedureScreen.PlaySfx(screen._healSfx);
        }

        if (plan.HealPunch)
        {
            // From the injuries it had (the last preview noted them) to the panel's, which ShowHealed emptied.
            screen.PunchHealedInjuryIcon(screen._panelB.Details);
        }

        if (plan.CostPunch)
        {
            // The game's callback at the punch's apex puts the run's injuries on the panel: here they'd take the cost off again.
            screen.PunchInjuryIcon(screen._panelB.Details.m_InjuriesPreview, 0f, null!);
        }

        if (plan.CloseAfterSeconds > 0)
        {
            // It closes unless it's closing by then, and goes with the screen (the destroy token).
            screen.CloseAfterResultAsync(plan.CloseAfterSeconds);
        }
    }

    /// <summary>The panel's skater with no injuries: the commit healed them all, and this PC's copy of the run changes only with the host's next checkpoint.</summary>
    private static void ShowHealed(NursePanelView panel)
    {
        SkaterDetailsUI details = panel.Details;
        var none = new Il2CppList.List<Injury>();
        details.RefreshInjuries(new Il2CppList.IReadOnlyList<Injury>(none.Pointer));

        Il2CppReferenceArray<SkaterInjuriesUI>? views = panel._injuryViews;
        for (int i = 0; views != null && i < views.Length; i++)
        {
            SkaterInjuriesUI? view = views[i];
            if (view != null)
            {
                view.Refresh(details.CurrentSkater);
            }
        }
    }

    /// <summary>
    /// The game's surgery tooltips read the talents its commit granted (the roster's <c>_grantedTalents</c>, every replacement drawn for
    /// the skater): they're on the list only for that call, because the roster's clean-up spares what's on it and these
    /// are this PC's copies, granted to no one.
    /// </summary>
    private static void ShowSurgeryTooltips(NurseProcedureScreen screen, NursePanelView panel)
    {
        NurseProcedureRoster roster = screen._roster;
        Il2CppList.List<Talent> granted = roster._grantedTalents;
        Il2CppList.List<Talent>? replacements = roster.GetReplacements(screen._state._PrimaryPlayerIndex_k__BackingField);
        if (replacements == null || replacements.Count == 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < replacements.Count; i++)
            {
                granted.Add(replacements[i]);
            }

            screen.ShowSurgeryTooltips(panel);
        }
        finally
        {
            granted.Clear();
        }
    }

    /// <summary>The clone's cost on its source's panel, as the clone's preview showed it: the panel was bound anew from this PC's unchanged run.</summary>
    private static void ShowCloneCost(NurseProcedureScreen screen, NursePanelView panel)
    {
        int primary = screen._state._PrimaryPlayerIndex_k__BackingField;
        Il2CppList.List<Il2CppData.ForwardData>? players = screen._squadPlayers;
        if (players != null && primary >= 0 && primary < players.Count)
        {
            screen.ShowCloneCost(panel, panel.Details, players[primary], screen._roster.Team);
        }
    }
}
#endif
