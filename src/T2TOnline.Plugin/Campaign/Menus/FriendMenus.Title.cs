#if HAS_INTEROP
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the title's menus: the host's unlockables (<see cref="UnlockablesView"/>) and its
/// run history (<see cref="RunHistoryPages"/>) as real views. Each opens once the host's attachment that goes with it is here
/// (a few seconds at most, as text meanwhile after a quiet second); one that can't be shown is a note saying why (the host
/// is looking at their own unlockables or history).
/// </summary>
internal sealed partial class FriendMenus
{
    private const string UnlocksNotYet = "the host's unlock states are still on their way";
    private const string HistoryNotYet = "the host's run history page is still on its way";

    // After a tab change, the unlockables menu selects its first row a frame later; the host's row is put back then (the
    // re-select two frames on), and again this many frames on (a list that moved to the row builds its cells meanwhile).
    private const int UnlockRowAgainFrames = 20;

    /// <summary>Why one of the title's real views can't open now ("" when it can, or when it's another menu).</summary>
    private string TitleBlocker(MenuState state)
    {
        if (state.Type == UnlockablesView.Type)
        {
            if (!MenuPatches.UnlockablesReady)
            {
                return "this game's unlockables couldn't be patched to show them";
            }

            if (!_attachments.Has(state.Serial, MenuAttachmentKind.UnlockStates))
            {
                return UnlocksNotYet;
            }
        }

        if (IsHistory(state) && !_attachments.Has(state.Serial, MenuAttachmentKind.HistoryPage))
        {
            return HistoryNotYet;
        }

        return string.Empty;
    }

    /// <summary>
    /// True (and the view waits, as text meanwhile after a quiet second, FriendMenus.Overlays.cs) when
    /// <paramref name="blocker"/> says the host's attachment for one of the title's views is still on its way: it opens once
    /// that's in (<see cref="TitleAttached"/>), for a few seconds at most.
    /// </summary>
    private bool TitleWait(View view, string blocker)
    {
        if (blocker != UnlocksNotYet && blocker != HistoryNotYet)
        {
            view.WaitTitleUntil = 0;
            return false;
        }

        // Like every wait for data that follows a menu's open: nothing for a second (the attachment is usually in by
        // then), then the host's screen as text.
        Wait(view, blocker);
        if (view.WaitTitleUntil == 0)
        {
            view.WaitTitleUntil = FrameClock.Seconds + SummaryWaitSeconds;
            Log($"Menus: {view.LogId} waits for {LogNames.HostTag()}'s {(blocker == UnlocksNotYet ? "unlock states" : "run history page")}; as text if that takes over a second.");
        }

        return true;
    }

    /// <summary>The wait for a title view's attachment is over without it: a note, saying so (it still opens if it comes).</summary>
    private void TitleWaitOver(View view)
    {
        view.WaitTitleUntil = 0;
        view.TitleLate = true;
        string what = view.Type == UnlockablesView.Type ? "unlock states" : "run history page";
        ModLog.Warn($"Menus: {view.LogId} waited {SummaryWaitSeconds:0} s for {LogNames.HostTag()}'s {what}, which didn't come; a note instead.");
        NoteTitle(view, $"the host's {what} didn't come");
    }

    /// <summary>The host's attachment for one of the title's views came: a view waiting for it (or that gave up waiting) opens now.</summary>
    private void TitleAttached(View view, MirroredMenu menu, MenuAttachment attachment)
    {
        bool mine = (attachment.Kind == MenuAttachmentKind.UnlockStates && view.Type == UnlockablesView.Type) ||
            (attachment.Kind == MenuAttachmentKind.HistoryPage && IsHistory(menu.State));
        if (mine && (view.WaitTitleUntil > 0 || view.TitleLate) && view.Mode is Mode.Panel or Mode.Note)
        {
            view.WaitTitleUntil = 0;
            view.TitleLate = false;
            TryReal(view, menu);
        }

        // A newer page for an open history view goes in at its next frame (TitleTick).
    }

    /// <summary>
    /// The unlockables' request: a plan with the host's states (armed just before the menu opens, <see cref="ArmTitle"/>) and
    /// a fresh object for its <c>InitData</c> (which takes no data), so its copy binds to this view. Null with why.
    /// </summary>
    private Il2CppSystem.Object? PrepareUnlockables(View view, out string problem)
    {
        UnlockablesView.Disarm(view.Unlocks);
        view.Unlocks = UnlockablesView.Plan(_attachments.Get(view.Serial, MenuAttachmentKind.UnlockStates), out problem);
        view.UnlocksFollowed = false;
        if (view.Unlocks == null)
        {
            return null;
        }

        Log($"Menus: {view.LogId} opens here on this PC's own rows with {LogNames.HostTag()}'s states ({view.Unlocks.States}).");
        return new Il2CppSystem.Object();
    }

    /// <summary>The attachment a run-end menu is rebuilt from: the host's newest history page in its history mode, else its run summary.</summary>
    private MenuAttachment? RunEndAttachment(View view, MenuState state)
    {
        if (!IsHistory(state))
        {
            return _attachments.Get(view.Serial, MenuAttachmentKind.RunSummary);
        }

        MenuAttachment? page = _attachments.Get(view.Serial, MenuAttachmentKind.HistoryPage);
        view.HistoryShown = page?.ContentHash ?? 0;
        if (page != null && page.TryGetHistoryPage(out HistoryPageData? read))
        {
            Log($"Menus: {view.LogId} opens here on {LogNames.HostTag()}'s run history {read}.");
            WaitForPageLogo(view, read);
        }

        return page;
    }

    /// <summary>Arms what the view's menu takes as it opens (the host's unlock states), just before it's asked for.</summary>
    private static void ArmTitle(View view)
    {
        if (view.Unlocks != null)
        {
            UnlockablesView.Arm(view.Unlocks);
        }
    }

    /// <summary>The view gave up opening, or closed: its plan is disarmed and forgotten.</summary>
    private void DisarmTitle(View view)
    {
        if (view.Unlocks is { NotInTab: > 0 } plan)
        {
            Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s highlighted row wasn't in the tab open here {plan.NotInTab} time(s) (while the tabs changed, " +
                "or a tab this PC lists differently).");
        }

        UnlockablesView.Disarm(view.Unlocks);
        view.Unlocks = null;
    }

    /// <summary>
    /// A title view's frames: the unlockables follow the host's tab and row once their rows are in (and the row again a while
    /// after a tab change); the run history takes the host's newer page.
    /// </summary>
    private void TitleTick(View view, MirroredMenu menu)
    {
        if (view.Type == UnlockablesView.Type && view.Unlocks is { Done: true })
        {
            if (!view.UnlocksFollowed)
            {
                // Its rows came in (its quests tab may finish after it showed): the host's tab and row now.
                view.UnlocksFollowed = true;
                ApplyAll(view, menu);
            }
            else if (view.Frame == view.UnlockRowFrame)
            {
                ApplySelection(view, menu);
            }
        }

        if (view.Frame >= ReselectFrames[0] && IsHistory(menu.State) && view.MenuAs<RunEndMenu>() is { } runEnd &&
            _attachments.Get(view.Serial, MenuAttachmentKind.HistoryPage) is { } newest && newest.ContentHash != view.HistoryShown)
        {
            view.HistoryShown = newest.ContentHash;
            ShowHistoryPage(view, runEnd, newest);
        }

        if (view.LogoWait.Name.Length > 0 && IsHistory(menu.State) && view.MenuAs<RunEndMenu>() is { } open)
        {
            FollowPageLogo(view, open);
        }
    }

    /// <summary>
    /// The host's history page wears a custom logo this PC may not have (v1): the page shows at once with whatever
    /// the game finds (its default logo for a miss); the host sends the logo of the page it rests on, which is put in this
    /// game's logo list as it arrives (<see cref="CustomContent"/>, as a match's logos are), and the page is shown again with it.
    /// </summary>
    private void WaitForPageLogo(View view, HistoryPageData? page)
    {
        string? name = page?.Entry?.Team?.LogoName;
        bool wasWaiting = view.LogoWait.Name.Length > 0;
        view.LogoWait.PageShown(name, name != null && CustomContent.HasLogo(name), FrameClock.Seconds);
        if (view.LogoWait.Name.Length > 0 && !wasWaiting)
        {
            Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s run history page wears a custom logo ({LogNames.TextShape(name)}), which isn't here yet; the page shows the game's default logo until it arrives " +
                $"(the host sends it once it rests on the page, within {HistoryLogoWait.GiveUpSeconds:0} s here).");
        }
    }

    /// <summary>A page waiting for its logo: it is shown again once the logo is in this game's list, or left with the default logo after two minutes (said once).</summary>
    private void FollowPageLogo(View view, RunEndMenu open)
    {
        string name = view.LogoWait.Name;
        switch (view.LogoWait.Poll(FrameClock.Seconds, CustomContent.HasLogo(name)))
        {
            case HistoryLogoWaitState.Arrived:
                if (_attachments.Get(view.Serial, MenuAttachmentKind.HistoryPage) is { } newest)
                {
                    view.HistoryShown = newest.ContentHash;
                    Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s logo ({LogNames.TextShape(name)}) arrived; the run history page shows it now.");
                    ShowHistoryPage(view, open, newest);
                }

                break;
            case HistoryLogoWaitState.GaveUp:
                ModLog.Warn($"Menus: {view.LogId}: {LogNames.HostTag()}'s logo ({LogNames.TextShape(name)}) for the run history page didn't arrive within {HistoryLogoWait.GiveUpSeconds:0} s; " +
                    "the page keeps the game's default logo (the host's log says if it couldn't send it).");
                break;
        }
    }

    /// <summary>The host moved to another run history page: the open menu shows it with the game's own page step.</summary>
    private void ShowHistoryPage(View view, RunEndMenu runEnd, MenuAttachment attachment)
    {
        HistoryPageData? page = RunHistoryPages.Page(attachment, out string problem);
        bool shown = false;
        if (page != null && Try(view, "showing the host's run history page", () => shown = RunHistoryPages.Show(runEnd, page, out problem)) && shown)
        {
            view.ReselectFrame = view.Frame + 2; // its rows were made again: the host's selection again
            RunHistoryPages.Log($"Menus: RunEnd: {LogNames.HostTag()}'s run history {page} shows here.");
            WaitForPageLogo(view, page);
            return;
        }

        if (problem.Length > 0)
        {
            ModLog.Warn($"Menus: {view.LogId} can't show {LogNames.HostTag()}'s next run history page ({problem}); it stays on the last one.");
        }
    }

    /// <summary>The host's unlockables tab or row (a menu step): followed by what it is. True when the tab changed (its rows were made again).</summary>
    private bool ApplyUnlockables(View view, MirroredMenu menu, UnlockableMenu open, MenuTarget target, int index)
    {
        UnlockPlan? plan = view.Unlocks;
        if (plan is not { Done: true })
        {
            return false; // its rows aren't in yet: applied once they are (TitleTick)
        }

        if (target.Group == UnlockablesView.TabGroup)
        {
            bool changed = false;
            string problem = string.Empty;
            if (!Try(view, "opening the host's unlockables tab", () => changed = UnlockablesView.FollowTab(open, plan, index, out problem)))
            {
                return false;
            }

            if (problem.Length > 0)
            {
                Missed(view, problem);
            }

            if (changed)
            {
                view.UnlockRowFrame = view.Frame + UnlockRowAgainFrames;
            }

            return changed;
        }

        if (target.Group == UnlockablesView.RowGroup)
        {
            FollowUnlockRow(view, menu, open);
        }

        return false;
    }

    /// <summary>The host's highlighted unlockables row, highlighted here (the game's own highlight then shows its details).</summary>
    private void FollowUnlockRow(View view, MirroredMenu menu, UnlockableMenu? open)
    {
        if (open == null || view.Mode != Mode.Real || view.Unlocks is not { Done: true } plan ||
            !menu.Values.TryGetValue(MenuTarget.AtItem(UnlockablesView.RowGroup, 0), out int key))
        {
            return;
        }

        AbstractNavigable? row = null;
        string problem = string.Empty;
        if (!Try(view, "finding the host's unlockables row", () => row = UnlockablesView.RowNavigable(open, plan, key, out problem)))
        {
            return;
        }

        NavigationController? navigation = MenuGame.Module()?.NavigationController;
        if (row == null || navigation == null)
        {
            if (problem.Length > 0)
            {
                Missed(view, problem);
            }

            return;
        }

        if (navigation.CurrentNavigable?.Pointer != row.Pointer)
        {
            Try(view, "highlighting the host's unlockables row", () => navigation.SetCurrentNavigable(row, true, true));
        }
    }

    /// <summary>
    /// True (and the view is a note saying why) when <paramref name="view"/> is one of the title's views and
    /// <paramref name="why"/> is a failure, not a wait: its fallback is the note, not the host's screen as text.
    /// </summary>
    private bool NoteInstead(View view, string why)
    {
        bool title = view.Type == UnlockablesView.Type || (view.Type == MenuPayloads.RunEnd && _mirror.TryGet(view.Serial, out MirroredMenu menu) && IsHistory(menu.State));
        if (!title || why is UnlocksNotYet or HistoryNotYet or OtherScene || why == "too many menus are opening here at once")
        {
            return false;
        }

        NoteTitle(view, why);
        return true;
    }

    /// <summary>One of the title's views as a note: the host is looking at their own unlockables or run history, and why it isn't shown here.</summary>
    private void NoteTitle(View view, string why)
    {
        bool changed = view.Mode != Mode.Note || view.Why != why;
        view.Mode = Mode.Note;
        view.Why = why;
        view.Note = $"is looking at their {(view.Type == UnlockablesView.Type ? "unlockables" : "run history")} (a note here: {SafeText.Clean(why, 120)}).";
        view.NoteWord = null;
        _tally.Note(view.Serial);
        _panelDirty = true;
        if (changed)
        {
            Log($"Menus: {view.LogId} shows here as a note: {why}.");
        }
    }

    /// <summary>True for the host's run-end menu in its history mode (the title's run history).</summary>
    private static bool IsHistory(MenuState state) => state.Type == MenuPayloads.RunEnd && RunEndView.Of(state.Data) == RunEndView.Variant.History;

    /// <summary>The title views' part of a view.</summary>
    private sealed partial class View
    {
        /// <summary>Waiting (until then) for the host's unlock states or history page that go with the menu; 0: not waiting.</summary>
        public double WaitTitleUntil { get; set; }

        /// <summary>The wait for the attachment ran out (a note shows): it still opens if the attachment comes.</summary>
        public bool TitleLate { get; set; }

        /// <summary>The unlockables' plan (the host's states, and what this PC's menu made from them), while it's asked for or up.</summary>
        public UnlockPlan? Unlocks { get; set; }

        /// <summary>The host's tab and row were applied once the unlockables' rows were in.</summary>
        public bool UnlocksFollowed { get; set; }

        /// <summary>The frame the host's unlockables row is put back again after a tab change (-1: none).</summary>
        public int UnlockRowFrame { get; set; } = -1;

        /// <summary>The content hash of the run history page shown here (0: none).</summary>
        public ulong HistoryShown { get; set; }

        /// <summary>The custom logo the host's run history page wears, waited for while it isn't here (the host sends the logo of the page it rests on).</summary>
        public HistoryLogoWait LogoWait { get; } = new();
    }
}
#endif
