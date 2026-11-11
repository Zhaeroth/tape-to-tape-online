#if HAS_INTEROP
using System;
using System.IO;
using Il2CppTape2Tape.Hockey.UI;
using MelonLoader.Utils;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// The offer of a problem report at the title after something went wrong (D140; the rule is Core's <see cref="ReportOffer"/>): watches the
/// online session (its start, its end, broken or with an ERROR logged in it) and the runtime's stop, keeps the marker that carries an offer
/// across launches (<c>UserData/T2TOnline/report-offer.txt</c>, a kind, a reason and a UTC time), and at the next idle title asks on the game's
/// yes/no: <b>Not Now</b> (focused, B) or <b>Make Report</b>. Once a launch; never in a session, never over another prompt, never while a check
/// tour holds the title's prompts (the walker answers any prompt), unless the tour asks for it itself (<see cref="OfferForTour"/>). The marker
/// stays in memory while a tour holds the player's data. A part of the native screens' layer: online on, online off and after an error.
/// Cheap when nothing is due: a few field reads a frame. Never throws (the layer guards it too).
/// </summary>
internal static class ReportOfferWatch
{
    private const string Label = "UserData/T2TOnline/" + ReportOffer.MarkerFileName;
    private const long LookEveryMs = 250;

    private static readonly ReportOffer Offer = new();
    private static readonly Action<bool> Answered = OnAnswered;

    private static bool _installed;
    private static bool _sessionOn;
    private static bool _stopNoted;
    private static bool _asking;
    private static bool _forTour;
    private static long _nextLook;
    private static ReportOfferReason _askedFor;

    // What the file holds (null: something the mod didn't write, or not known), and what was last held back for a tour.
    private static ReportOfferMarker? _written;
    private static ReportOfferReason _writtenReason;
    private static ReportOfferMarker? _heldFor;
    private static bool _writeFailed;

    /// <summary>What an offer is due for now (<see cref="ReportOfferReason.None"/> for nothing; the check tours read it).</summary>
    public static ReportOfferReason Pending => Offer.Pending;

    /// <summary>True once the offer was made this launch.</summary>
    public static bool OfferedThisLaunch => Offer.OfferedThisLaunch;

    /// <summary>At start-up, from the report flow's install: the last launch's marker read (it stays until it's answered). Never throws.</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        string? text = null;
        try
        {
            string path = MarkerPath();
            if (File.Exists(path))
            {
                text = new FileInfo(path).Length <= ReportOffer.MaxMarkerLength ? File.ReadAllText(path) : string.Empty;
                _written = ReportOffer.TryParseMarker(text, out ReportOfferMarker kind, out ReportOfferReason reason) ? kind : null;
                _writtenReason = reason;
            }
            else
            {
                _written = ReportOfferMarker.None;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Bug report: {Label} couldn't be read ({ex.GetType().Name}); no offer from the last launch.");
            _written = null;
        }

        ReportOfferReason due = Offer.ReadMarker(text);
        if (due == ReportOfferReason.LastLaunchInSession && _written == ReportOfferMarker.InSession)
        {
            ModLog.Info($"Bug report: the last launch ended during an online game ({Label} was left); a report is offered at the title.");
        }
        else if (due != ReportOfferReason.None)
        {
            ModLog.Info($"Bug report: the last launch left an offer due ({ReportOffer.Describe(due)}; {Label}); a report is offered at the title.");
        }
        else if (_written == null)
        {
            ModLog.Info($"Bug report: {Label} isn't one the mod wrote; it goes.");
        }
    }

    /// <summary>
    /// A check tour's own look at the offer (developer tools only): the next idle title offers what is due even while the tour holds the title's
    /// prompts; the tour answers it with its own press. False when nothing is due or it was offered already.
    /// </summary>
    public static bool OfferForTour()
    {
        if (!Offer.Waiting)
        {
            return false;
        }

        _forTour = true;
        _nextLook = 0;
        return true;
    }

    /// <summary>Every frame, in the native screens' layer: the session and the runtime watched, the marker kept, the offer made when it's due.</summary>
    public static void Tick()
    {
        if (!_installed)
        {
            return;
        }

        Watch();
        SyncMarker();
        if (!Offer.Waiting || _asking)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextLook)
        {
            return;
        }

        _nextLook = now + LookEveryMs;
        bool tour = TourHolds.HoldsTitlePrompts && !_forTour;
        bool promptOpen = LobbyPrompts.IsOpen;
        bool inSession = InSession();
        if (tour || promptOpen || inSession)
        {
            return; // the cheap reasons first: the title isn't looked at
        }

        ReportOfferReason reason = Offer.Due(IdleTitle(), inSession, tour, promptOpen);
        if (reason != ReportOfferReason.None)
        {
            Ask(reason);
        }
    }

    /// <summary>The game is closing cleanly (the mod's own closing line follows): the marker says an offer is still due, or goes. Never throws.</summary>
    public static void AtQuit()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            // A session still on ends with the game: one that broke or logged an ERROR is offered at the next launch (the line below says so).
            ReportOfferMarker wanted = Offer.Closing(NativeUiLayer.Session?.Broken == true, ModLog.ErrorCount.Total);
            if (wanted == _written && (wanted != ReportOfferMarker.Pending || Offer.Pending == _writtenReason))
            {
                return;
            }

            if (TourHolds.HoldsFile("Bug report", Label))
            {
                return;
            }

            Write(wanted, Offer.Pending);
            if (wanted == ReportOfferMarker.Pending)
            {
                ModLog.Info($"Bug report: the game closes with an offer still due ({ReportOffer.Describe(Offer.Pending)}); the next launch makes it.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the offer's marker at the close failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>The session's start and end, and the runtime's stop.</summary>
    private static void Watch()
    {
        if (NativeUiLayer.State == SettingsState.AfterError)
        {
            if (!_stopNoted)
            {
                _stopNoted = true;
                _sessionOn = false;
                Offer.RuntimeStopped();
                ModLog.Info($"Bug report: {ReportOffer.Describe(ReportOfferReason.RuntimeStopped)}; a report is offered at the next idle title.");
            }

            return;
        }

        OnlineSession? session = NativeUiLayer.Session;
        bool on = session != null && session.InOnlineGame;
        if (on == _sessionOn)
        {
            return;
        }

        _sessionOn = on;
        if (on)
        {
            Offer.SessionStarted(ModLog.ErrorCount.Total);
            return;
        }

        ReportOfferReason reason = Offer.SessionEnded(session?.Broken == true, ModLog.ErrorCount.Total);
        if (reason != ReportOfferReason.None)
        {
            ModLog.Info($"Bug report: {ReportOffer.Describe(reason)}; a report is offered at the next idle title{(Offer.OfferedThisLaunch ? " of the next launch (one was offered this launch)" : string.Empty)}.");
        }
    }

    /// <summary>The marker file to what the rule says now, on a change only; held in memory while a tour holds the player's data.</summary>
    private static void SyncMarker()
    {
        ReportOfferMarker wanted = Offer.Marker;
        ReportOfferReason reason = Offer.Pending;
        if (wanted == _written && (wanted != ReportOfferMarker.Pending || reason == _writtenReason))
        {
            return;
        }

        if (_writeFailed)
        {
            return;
        }

        if (TourHolds.On)
        {
            if (_heldFor != wanted)
            {
                _heldFor = wanted;
                _ = TourHolds.HoldsFile("Bug report", Label);
            }

            return;
        }

        _heldFor = null;
        Write(wanted, reason);
    }

    private static void Write(ReportOfferMarker wanted, ReportOfferReason reason)
    {
        try
        {
            string path = MarkerPath();
            if (wanted == ReportOfferMarker.None)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ReportOffer.MarkerText(wanted, reason, DateTime.UtcNow));
            }

            _written = wanted;
            _writtenReason = reason;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writeFailed = true;
            ModLog.Warn($"Bug report: {Label} couldn't be {(wanted == ReportOfferMarker.None ? "removed" : "written")} ({ex.GetType().Name}); an offer isn't carried to the next launch this time.");
        }
    }

    /// <summary>Any part of an online game: this PC's session, following a host's run, the lobby up, or the way home after one.</summary>
    private static bool InSession() =>
        _sessionOn || OnlineSession.LobbyUp || SessionExit.Leaving || CampaignRelay.Current?.Following == true;

    /// <summary>
    /// The title idle and showing its buttons: the game's UI up, no loading screen, no shell, no report being made, the title's own idle state (no
    /// menu over it, no start, not leaving: <see cref="LobbyShell.TitleAllowingOpen"/>), past press-to-start and not between its groups.
    /// </summary>
    private static bool IdleTitle()
    {
        if (MenuGame.Module() == null || Il2CppMichsky.LSS.LoadingScreen.IsActive || LobbyShell.IsOpen || BugReports.Busy)
        {
            return false;
        }

        TitleScreen? title = LobbyShell.TitleAllowingOpen();
        return title != null && title.m_CurrentButtonGroup != TitleScreen.EButtonGroup.PressToStart && !title._isTransitioningGroup;
    }

    private static void Ask(ReportOfferReason reason)
    {
        // The sentence that is true for the reason: a launch that closed during a game says it closed, not that an error ended it (Core's WordFor).
        LobbyWord word = ReportOffer.WordFor(reason) ?? LobbyWords.ReportOffer;
        bool asked = LobbyPrompts.Ask(LobbyLocalization.Say(LobbyWords.ProblemReport.Key), LobbyLocalization.Say(word.Key),
            LobbyLocalization.Say(LobbyTerms.NotNow), LobbyLocalization.Say(LobbyWords.MakeReport.Key), Answered);
        if (!asked)
        {
            return; // looked at again in a moment
        }

        _asking = true;
        _askedFor = reason;
        ModLog.Info($"Bug report: the offer is on the game's prompt ({ReportOffer.Describe(reason)}{(Offer.PendingFromLastLaunch ? ", the last launch" : string.Empty)}{(_forTour ? "; asked by the check tour" : string.Empty)}).");
    }

    private static void OnAnswered(bool make)
    {
        _asking = false;
        _forTour = false;
        Offer.Offered();
        try
        {
            ModLog.Info($"Bug report: offered at the title after {ReportOffer.Describe(_askedFor)}; answered {(make ? "Make" : "Not now")}.");
            if (make)
            {
                _ = ReportFlow.Ask(ReportSources.Offer);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Bug report: the offer's answer failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static string MarkerPath() => Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline", ReportOffer.MarkerFileName);
}
#endif
