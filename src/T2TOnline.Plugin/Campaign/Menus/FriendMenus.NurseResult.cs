#if HAS_INTEROP
using System;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// What a friend's real Nurse screen leaves out at its result, its commit being skipped (v1): the host's commit
/// went through (its outcome is in here), so this PC's screen runs what the game's popups gate on theirs: the hit, the heal's sound
/// and punch, the surgery's tooltips, the frozen peas' title, the clone's cost, the screen's own timed close
/// (<see cref="NurseResultEffects"/>, <see cref="NurseResultPlan"/>) and the added talent's flash in place of the pending suture.
/// No commit, no save, nothing on the run, and <c>_committed</c> stays false. A host whose commit failed sends no outcome: nothing
/// plays, as on its screen. A failure here leaves the result as it was without them (logged once).
/// </summary>
internal sealed partial class FriendMenus
{
    /// <summary>
    /// Friend, after a build of its procedure screen <paramref name="screen"/> at its result (the build's postfix): what each build puts
    /// back, and the first time what plays once. Never throws.
    /// </summary>
    public void NurseBuilt(NurseProcedureScreen screen)
    {
        if (NurseResultShown(screen) is not { } view || view.NurseReal is not { ResultFailed: false } nurse || screen._closing)
        {
            return;
        }

        try
        {
            NurseResultPlan plan = NurseResultPlan.For(NurseScreen.CurrentProcedure(screen._state));
            if (!plan.Any)
            {
                return;
            }

            NurseResultEffects.EachBuild(screen, plan);
            if (!nurse.ResultPlayed)
            {
                nurse.ResultPlayed = true;
                NurseResultEffects.Once(screen, plan);
                ModLog.Info($"Menus: {view.LogId}: the Nurse's result plays here as on {LogNames.HostTag()}'s: {plan}.");
            }
        }
        catch (Exception ex)
        {
            nurse.ResultFailed = true;
            ModLog.Warn($"Menus: {view.LogId}: the Nurse's result effects failed here ({ModLog.SafeErrorText(ex)}); its result shows without them.");
        }
    }

    /// <summary>
    /// Friend, as the screen's panel places its pending talent's suture (<see cref="NursePanelView"/> <paramref name="panel"/>): at a result
    /// the host's commit made, the game's committed branch instead, the added talent's flash. False when the suture stays. Never throws.
    /// </summary>
    public bool NurseFlashesAddedTalent(NurseProcedureScreen screen, NursePanelView panel)
    {
        if (NurseResultShown(screen) is not { } view || view.NurseReal is not { ResultFailed: false } nurse)
        {
            return false;
        }

        try
        {
            screen.FlashAddedTalent(panel);
            return true;
        }
        catch (Exception ex)
        {
            nurse.ResultFailed = true;
            ModLog.Warn($"Menus: {view.LogId}: the Nurse's added talent couldn't flash here ({ModLog.SafeErrorText(ex)}); its result shows without its effects.");
            return false;
        }
    }

    /// <summary>The view whose real procedure screen is <paramref name="screen"/>, once the host's outcome went in there; else null. Nothing allocated.</summary>
    private View? NurseResultShown(NurseProcedureScreen screen)
    {
        foreach (View view in _views.Values)
        {
            if (view.NurseReal is { OutcomeApplied: true, Fallen: false, Screen: { } open } && open.Pointer == screen.Pointer)
            {
                return view;
            }
        }

        return null;
    }
}
#endif
