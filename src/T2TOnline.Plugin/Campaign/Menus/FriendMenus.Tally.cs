#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppData;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// What a friend's log says about what this player saw of the host's menus:
/// the session's tally (<see cref="FriendMenuTally"/>), said when following stops, and the lines that name what a menu showed: the
/// run a run-end shows, a prompt's text and language, the procedure on the Nurse's text panel, the host's curse pick, the lineup
/// the host's next run should have. One line a menu or a pick; each is only read from what's at hand, and a line that fails is
/// left out with a trace: it never stops a view.
/// </summary>
internal sealed partial class FriendMenus
{
    private const string NurseScreenKind = "NurseProcedureScreen";

    private readonly FriendMenuTally _tally = new();

    // The lineup a real run-team lineup showed last, for the host's next checkpoint.
    private ShownLineup? _shownLineup;

    /// <summary>Following started: nothing is counted yet.</summary>
    private void TallyStarted()
    {
        _tally.Clear();
        _ = _lines.TakeLeftOut();
        _shownLineup = null;
    }

    /// <summary>Following stopped: what this session showed is said, before the views go. Unlocks noted for a screen that won't come are said too.</summary>
    private void TallyStopped(string reason)
    {
        UnlockViews.SayForgotten(reason);
        _tally.CloseAll(serial => _views.TryGetValue(serial, out View? view) && Quiet(view));
        if (_tally.Any)
        {
            ModLog.Info(_tally.Line(LogNames.HostTag(), _lines.TakeLeftOut()));
        }

        _tally.Clear();
        _shownLineup = null;
    }

    /// <summary>A real view came up: counted, and what it shows named once. Never throws.</summary>
    private void TallyBound(View view)
    {
        _tally.Real(view.Serial);
        try
        {
            if (view.Type == MenuPayloads.RunEnd && _attachments.Get(view.Serial, MenuAttachmentKind.RunSummary) is { } summary &&
                summary.TryGetRunSummary(out RunHistoryData? run) && run != null)
            {
                // Straight to the log, not through the menus' line budget: once a run, in the busiest minute of it.
                ModLog.Info($"Menus: {view.LogId} shows {LogNames.HostTag()}'s {run.Words()}.");
            }
            else if (PromptViews.IsPrompt(view.Type))
            {
                Log($"Menus: {view.LogId} (#{view.Serial}) shows {LogNames.HostTag()}'s prompt here {PromptViews.ShownHere(view.Type, _attachments.Get(view.Serial, MenuAttachmentKind.Prompt), LogNames.HostTag())}.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: {view.LogId}: what it shows wasn't said ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// A view closes: counted (a wait that hadn't shown anything yet showed nothing). A Canada curse menu asked for that never
    /// opened is said: its curses stay with the game's request, as the host's game keeps its own. A real
    /// run-team lineup leaves what it showed for the host's next checkpoint.
    /// </summary>
    private void TallyClosing(View view)
    {
        _tally.Closed(view.Serial, Quiet(view));
        if (view.Type == CanadaViews.Type && view.Request != null)
        {
            Log($"Menus: {view.LogId} (#{view.Serial}) closed before it opened here; the curses made here stay with the game's request (as the host's game keeps its own).");
        }

        if (view.Type == LineupViews.Type && view.Mode == Mode.Real && view.LineupData is { } data && LineupViews.OnRunTeam(data))
        {
            _shownLineup = new ShownLineup(view.LogId, LineupViews.HostCards(data), LineupViews.HostStarters(data), view.LineupChanges);
        }
    }

    /// <summary>
    /// A run of the host's loaded here: the first real one after a real lineup closed should have the lineup it showed last. Said as
    /// a plain line whatever it finds: what the host does between the close and its next save (an event's skater, another lineup) can
    /// change the team. Never throws.
    /// </summary>
    private void CheckShownLineup(uint checkpoint)
    {
        if (checkpoint == 0 || _shownLineup is not { } shown)
        {
            return;
        }

        _shownLineup = null;
        try
        {
            TeamData? team = CampaignGame.State()?.runData?.teamData;
            if (team != null)
            {
                ModLog.Info($"Menus: {shown.Id} closed here showing {LogNames.HostTag()}'s order ({shown.Cards.Count} card(s), {shown.Changes} change(s) followed); " +
                    $"the host's run {checkpoint} {LineupOrder.AgainstRun(shown.Cards, shown.Starters, LineupViews.RosterIds(team), team.goalie?.id)}.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: {shown.Id}: its lineup wasn't checked against the host's run {checkpoint} ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The host's Nurse procedure screen on the text panel, once a procedure: the title this PC shows, and why
    /// it's only "Nurse" when it is. Counted once a takeover (<see cref="FriendMenuTally"/>).
    /// </summary>
    private void SayNurseText(View view)
    {
        NurseScreenText? nurse = view.Nurse;
        int procedure = nurse?.Procedure ?? -1;
        if (nurse == null || view.NurseTextSaid == procedure)
        {
            return;
        }

        view.NurseTextSaid = procedure;
        string plain = view.NurseTitle != "Nurse" ? string.Empty
            : procedure < 0 ? "; the title is \"Nurse\": the host's screen named no procedure"
            : "; the title is \"Nurse\": this game has no name for that procedure";
        Log($"Menus: {LogNames.HostTag()}'s Nurse procedure screen shows here as text, titled \"{view.NurseTitle}\" (procedure {procedure}{plain}), {nurse.Rows.Count} row(s), {nurse.Lines.Count} line(s).");
        if (!view.NurseTextCounted)
        {
            view.NurseTextCounted = true;
            if (view.NurseReal is not { Fallen: true })
            {
                _tally.ScreenText(NurseScreenKind, NurseTextWhy(view), wasReal: false);
            }
        }
    }

    /// <summary>
    /// The host's click in a Canada curse menu shown here: when it's on a card, the curse it picked and where this PC's highlight
    /// was. Once a view. Never throws.
    /// </summary>
    private void SayCursePick(View view, MirroredMenu menu, MenuTarget target)
    {
        if (view.Type != CanadaViews.Type || view.CursePickSaid || view.Mode != Mode.Real || view.Menu?.TryCast<CanadaCurseMenu>() is not { } curses)
        {
            return;
        }

        try
        {
            int picked = CanadaViews.CardOf(curses, MenuTargets.Find(curses.transform, target));
            if (picked < 0)
            {
                return;
            }

            view.CursePickSaid = true;
            int here = CanadaViews.CardOf(curses, MenuGame.Module()?.NavigationController?.CurrentNavigable);
            MenuData data = menu.State.Data;
            Log($"Menus: {LogNames.HostTag()} picked {CanadaViews.CurseWords(data, picked)} in {view.LogId}; this PC's highlight was on {(here >= 0 ? CanadaViews.CurseWords(data, here) : "no curse")}.");
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: {view.LogId}: the host's pick wasn't named ({ex.GetType().Name}).");
        }
    }

    /// <summary>The event card the host picked in words, from the set shown here (a card the set doesn't have says so). Never throws.</summary>
    private static string PickWords(View view, EventChoiceMenu open, EventChoiceItem item)
    {
        try
        {
            return view.Event?.Set.PickWords(EventCapture.ChoiceOf(open, item), own: false, LogNames.TextShape) ?? "a choice";
        }
        catch (Exception)
        {
            return "a choice";
        }
    }

    /// <summary>What a real run-team lineup showed last, for the host's next checkpoint.</summary>
    private sealed record ShownLineup(string Id, IReadOnlyList<string> Cards, int Starters, int Changes);

    /// <summary>The log's part of a view.</summary>
    private sealed partial class View
    {
        /// <summary>The procedure last said on the Nurse's text panel (-2: none yet).</summary>
        public int NurseTextSaid { get; set; } = -2;

        /// <summary>This view's text panel of the Nurse's procedure screen was counted.</summary>
        public bool NurseTextCounted { get; set; }

        /// <summary>A Canada curse menu's pick was said here.</summary>
        public bool CursePickSaid { get; set; }

        /// <summary>A lineup's newest host data followed here.</summary>
        public MenuData? LineupData { get; set; }

        /// <summary>How many times a lineup here moved to follow the host's.</summary>
        public int LineupChanges { get; set; }
    }
}
#endif
