#if HAS_INTEROP
using System;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// Making a problem report from the mod's native rows (D140): Settings > Online's Problem Report, the Online menu's Report a Problem and the offer
/// at the title. The zip is <see cref="BugReports"/>'s, unchanged (its manifest names where it was asked from, <see cref="ReportSources"/>); no
/// Explorer by itself. Once it's made, the game's prompt says where it is and where to post it: <b>Close</b> (focused, B) and <b>Show File</b>
/// (Explorer with the zip selected) while the title scene is the active one, the title or a menu over it; a one-button notice anywhere else
/// (Explorer would take the game's focus). A failure says why on the one-button notice.
/// <para>
/// A part of the native screens' layer (<see cref="NativeUiLayer"/>), so it works with online on, online off and after an error. The prompt
/// waits for a place it can be answered: no other prompt, no loading screen, the game's UI up, a following friend's screen not locked to the
/// host's (the lock would hide it), and in a match the pause menu or Settings on top (never over live play); after
/// <see cref="PlaceWaitMs"/> without one, the game's own toast says it instead. A prompt that is closed for a screen that went, before the
/// player answered it, is shown again the same way. Cheap when nothing waits: a few field reads a frame.
/// </para>
/// </summary>
internal static class ReportFlow
{
    private const long LookEveryMs = 250;
    private const long PlaceWaitMs = 120_000;

    private static readonly Action<bool> PromptAnswered = OnPromptAnswered;
    private static readonly Action NoticeClosed = OnNoticeClosed;

    private static Stage _stage;
    private static int _finishedAtAsk;
    private static long _readyAt;
    private static long _nextLook;
    private static string? _waitSaid;
    private static bool _busyShown;
    private static bool _installed;
    private static bool _fromOnlineMenu;

    private enum Stage
    {
        /// <summary>Nothing asked, or its prompt is done.</summary>
        None,

        /// <summary>The report asked for here is being made.</summary>
        Making,

        /// <summary>Its outcome is in (or it couldn't start): the prompt waits for a place.</summary>
        Ready,

        /// <summary>The prompt is up (the layer ticks the game's prompts).</summary>
        Shown,
    }

    /// <summary>True while a report is being made (the rows say so).</summary>
    public static bool Busy => BugReports.Busy;

    /// <summary>The last prompt's Show File: offered (the title), or why not; empty before any (the check tours read it).</summary>
    public static string LastShowFile { get; private set; } = string.Empty;

    /// <summary>
    /// True from the outcome's prompt going up until its answer is in, however it closes (the check tours read it). A prompt closed for a screen
    /// that went tells no one (<c>LobbyPrompts.CloseNow</c>): the next frame finds it gone and the outcome waits to be shown again.
    /// </summary>
    public static bool PromptShown => _stage == Stage.Shown;

    /// <summary>
    /// Once, from the native screens' layer as it starts: the flow's and the offer's ticks in the layer's frame, and the last launch's marker read.
    /// Never throws.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        try
        {
            ReportOfferWatch.Install();
            NativeUiLayer.Add("the problem report", Tick);
            NativeUiLayer.Add("the problem report's offer", ReportOfferWatch.Tick);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the native screens' report flow couldn't start ({ModLog.SafeErrorText(ex)}); Settings > Online's row still makes one.");
        }
    }

    /// <summary>
    /// Starts a report asked for from <paramref name="source"/> (one of <see cref="ReportSources"/>); its prompt follows when it's made. False when
    /// one is being made already (the row refuses, as the game's locked rows do). A report that couldn't start says why on the game's prompt. Never throws.
    /// </summary>
    public static bool Ask(string source)
    {
        try
        {
            ModLog.Info($"Bug report: asked from {source}.");
            if (BugReports.Busy)
            {
                ModLog.Info("Bug report: one is being made already; this press waits for it.");
                return false;
            }

            _finishedAtAsk = BugReports.Finished;
            _waitSaid = null;
            _nextLook = 0;
            if (BugReports.Make(source, ReportExplorer.Never))
            {
                _stage = Stage.Making;
                _fromOnlineMenu = ReportReturn.Applies(source);
                return true;
            }

            if (BugReports.Busy)
            {
                ModLog.Info("Bug report: one is being made already; this press waits for it.");
                return false;
            }

            ModLog.Warn($"Bug report: couldn't start ({Problem()}); the game's prompt says why.");
            _stage = Stage.Ready;
            _readyAt = Environment.TickCount64;
            _fromOnlineMenu = ReportReturn.Applies(source);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the ask from {source} failed ({ModLog.SafeErrorText(ex)}).");
            return false;
        }
    }

    /// <summary>Every frame, in the native screens' layer: the Online menu row's label while one is made; the outcome's prompt once it's in.</summary>
    public static void Tick()
    {
        bool busy = BugReports.Busy;
        if (busy != _busyShown)
        {
            _busyShown = busy;
            OnlineGroup.ShowReportBusy(busy);
        }

        if (_stage == Stage.None || (_stage == Stage.Shown && LobbyPrompts.IsOpen))
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_stage == Stage.Shown)
        {
            // Up, and now no prompt is open, yet its answer never came here: whoever closes the prompts for a screen that goes (the lobby's end over
            // a prompt asked on it, a question that failed) closed it, and such a close tells no one. The player hasn't read where the report is:
            // it waits for a place again, as when it was first ready.
            _stage = Stage.Ready;
            _readyAt = now;
            _nextLook = 0;
            _waitSaid = null;
            ModLog.Info("Bug report: its prompt was closed with a screen that went, before it was answered; it is shown again where it can be answered.");
        }

        if (_stage == Stage.Making)
        {
            if (BugReports.Finished <= _finishedAtAsk)
            {
                return; // its outcome comes in BugReports.Update, at the frame's start
            }

            _stage = Stage.Ready;
            _readyAt = now;
            _nextLook = 0;
        }

        if (now < _nextLook)
        {
            return;
        }

        _nextLook = now + LookEveryMs;
        bool made = BugReports.State == ReportState.Ready && BugReports.Finished > _finishedAtAsk && BugReports.Last.Name.Length > 0;
        if (Place(out bool atTitle, out bool inMatch) is { } why)
        {
            if (now - _readyAt > PlaceWaitMs)
            {
                _stage = Stage.None;
                _fromOnlineMenu = false; // no prompt closes: the cursor is wherever the player has it by now
                Toast(made);
                ModLog.Info($"Bug report: no place for the prompt within {PlaceWaitMs / 1000} s ({why}); the game's toast says it.");
            }
            else if (_waitSaid != why)
            {
                _waitSaid = why;
                ModLog.Info($"Bug report: the prompt waits ({why}).");
            }

            return;
        }

        if (Show(made, atTitle, inMatch))
        {
            _stage = Stage.Shown;
        }
    }

    /// <summary>Why the prompt can't be shown now, or null; and where the player is.</summary>
    private static string? Place(out bool atTitle, out bool inMatch)
    {
        string scene = WorldMap.SceneName;
        atTitle = scene == WorldMap.TitleScene;
        inMatch = scene == WorldMap.MatchScene;
        if (LobbyPrompts.IsOpen)
        {
            return "another prompt is open";
        }

        if (MenuGame.Module() == null || Il2CppMichsky.LSS.LoadingScreen.IsActive)
        {
            return "a loading screen";
        }

        if (FriendUi.Locked && !FriendUi.Suspended)
        {
            return "this screen follows the host's";
        }

        if (inMatch && !IsPauseOrSettings(LobbyShell.TopShown()))
        {
            return "in a match with no menu up";
        }

        return null;
    }

    private static bool IsPauseOrSettings(Il2CppBishopGames.UI.AbstractMenu? menu) =>
        menu != null && !menu.WasCollected && (menu.TryCast<GameMenu>() != null || menu.TryCast<SettingsMenu>() != null);

    /// <summary>The game's prompt for the outcome; false when it couldn't open now (tried again).</summary>
    private static bool Show(bool made, bool atTitle, bool inMatch)
    {
        string title = LobbyLocalization.Say(LobbyWords.ProblemReport.Key);
        string close = LobbyLocalization.Say(LobbyTerms.Close);
        if (!made)
        {
            string text = LobbyLocalization.Say(new LobbyLine(LobbyWords.ReportFailed, Problem()).ForRichText());
            if (!LobbyPrompts.Tell(title, text, close, NoticeClosed))
            {
                return false;
            }

            LastShowFile = "not offered: no report";
            ModLog.Info("Bug report: couldn't be made; the game's prompt says why.");
            return true;
        }

        string ready = LobbyLocalization.Say(new LobbyLine(LobbyWords.ReportReady, BugReports.Last.Name, ProjectLinks.IssuesShort).ForRichText());
        bool asked = atTitle
            ? LobbyPrompts.Ask(title, ready, close, LobbyLocalization.Say(LobbyWords.ShowFile.Key), PromptAnswered)
            : LobbyPrompts.Tell(title, ready, close, NoticeClosed);
        if (!asked)
        {
            return false;
        }

        LastShowFile = atTitle ? "offered" : inMatch ? "not offered: in a match" : "not offered: away from the title";
        ModLog.Info($"Bug report: ready, shown on the game's prompt (Show File {LastShowFile}).");
        return true;
    }

    /// <summary>The prompt's last resort: the game's own toast with the same words (the mod's own toast: never sent to friends).</summary>
    private static void Toast(bool made)
    {
        try
        {
            LobbyLine line = made
                ? new LobbyLine(LobbyWords.ReportReady, BugReports.Last.Name, ProjectLinks.IssuesShort)
                : new LobbyLine(LobbyWords.ReportFailed, Problem());
            using (OwnToasts.Begin())
            {
                if (!LobbyToast.Show(line, ToastTiming.Now, LobbyLocalization.Say(LobbyWords.ProblemReport.Key)))
                {
                    ModLog.Info("Bug report: the game has no toast queue here either; the log line above names the zip.");
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the game's toast failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void OnPromptAnswered(bool showFile)
    {
        _stage = Stage.None;
        try
        {
            ModLog.Info($"Bug report: the prompt closed ({(showFile ? "Show File" : "Close")}).");
            if (showFile)
            {
                _ = BugReports.ShowLastInExplorer("the report's prompt");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the prompt's answer failed ({ModLog.SafeErrorText(ex)}).");
        }

        ReturnCursor();
    }

    private static void OnNoticeClosed()
    {
        _stage = Stage.None;
        ModLog.Info("Bug report: the prompt closed (Close).");
        ReturnCursor();
    }

    /// <summary>
    /// The prompt of a report the Online menu's Report a Problem asked for is answered (Close, Show File, or the game closing it): that row takes the
    /// cursor on the next frame, while the Online menu is still what the title shows (<see cref="ReportReturn"/>). Left to itself the game gives the
    /// cursor to the group's default row, Host Lobby (verified in the game's code: the group is a <c>NavigableParentSelectDefault</c>, as the game's own
    /// Play group); the game's select and this one both end on the row, whichever comes first. Never for a report Settings > Online asked for: there
    /// the game puts the cursor back on the row by itself. Never throws.
    /// </summary>
    private static void ReturnCursor()
    {
        if (!_fromOnlineMenu)
        {
            return;
        }

        _fromOnlineMenu = false;
        try
        {
            OnlineSession? session = NativeUiLayer.Session;
            bool inSession = session is { InOnlineGame: true } || OnlineSession.LobbyUp || SessionExit.Leaving || CampaignRelay.Current?.Following == true;
            var look = new ReportReturnLook(
                AtTitle: WorldMap.SceneName == WorldMap.TitleScene,
                InSession: inSession,
                ScreenOpen: LobbyShell.IsOpen,
                PromptOpen: LobbyPrompts.IsOpen,
                MenuOnTop: LobbyShell.TopShown() != null,
                GroupShown: OnlineGroup.IsShown,
                RowThere: OnlineGroup.ReportRowThere,
                ReportBusy: BugReports.Busy);
            string? why = ReportReturn.WhyNot(look);
            if (why == null && OnlineGroup.SelectReportRow())
            {
                ModLog.Info("Bug report: the cursor is back on Report a Problem (the Online menu).");
            }
            else
            {
                ModLog.Info($"Bug report: the cursor stays where the game puts it ({why ?? "the row has gone"}); Report a Problem doesn't take it back.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the cursor's way back to Report a Problem failed ({ModLog.SafeErrorText(ex)}); it stays where the game puts it.");
        }
    }

    private static string Problem() => BugReports.Problem.Length > 0 ? BugReports.Problem : "it couldn't start";
}
#endif
