#if HAS_INTEROP
using System;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;

namespace T2TOnline.Plugin.Online;

// A friend whose game couldn't load the host's match is asked, on the game's own two-button prompt, what to do: Try again (their
// session ends, their game goes home, and the Join list joins the same lobby again: the mid-run join, which brings the host's current
// run and match) or Leave; B, or the game hiding the prompt (a scene load as their game follows the host on), keeps them following, F7
// and F1 as ever. Their UI is locked while they follow, so for the question alone it's lifted in part (only the prompt answers) and
// their device is lent back to their own game, as for their own Settings menu (OnlineSession.Remap.cs); the host is told their device
// is away, and its AI already plays their skater (their game couldn't load the match). With no screens to ask on, or no moment to ask
// in time, the notice's toast says it as before. Apart from what the session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    private FollowQuestion _question = new();
    private int _questionGeneration;
    private bool _questionLent;

    /// <summary>Friend: the host's match couldn't load or play here (<see cref="MatchRelay.PlayFailed"/>): asked, or told by the toast.</summary>
    private void FriendMatchFailedHere()
    {
        if (!CanEverAsk())
        {
            NotifyLimited(NoticeId.FollowFailed, _hostSteamId, _hostName);
            return;
        }

        if (_question.Failed(Now))
        {
            ModLog.Info($"Lobby: {HostTag()}'s match couldn't load here: this player is asked whether to try again or leave, as soon as their screen can carry the question.");
        }
    }

    /// <summary>True once the host's game has shown its run's end (Campaign/Menus/FriendMenus.cs): no question about its match has an answer then.</summary>
    private static bool HostRunOver => Campaign.Menus.MenuViews.Instance?.Friend.HostRunOver == true;

    /// <summary>Whether the question can be asked at all here: a friend following, with the native screens on.</summary>
    private bool CanEverAsk() => !_solo && ScreensReady && _friend is { State: FriendSessionState.Joined } && _flow.Friend == LobbyFriendState.Playing;

    /// <summary>Whether it can be asked now: nothing else on the screen's one prompt, the friend's own menus closed, no scene on its way, a device to answer with.</summary>
    private bool CanAskNow() =>
        CanEverAsk() && DeviceWithHost && !DeviceLent && (_localPad != null || _localKeyboard != null) && _localPad is not { IsConnected: false } &&
        FriendUi.Locked && !FriendUi.Suspended && !LobbyPrompts.IsOpen && NativeLobby.LobbyQuestion.Current == null && MenuGame.Module() != null &&
        !Il2CppMichsky.LSS.LoadingScreen.IsActive && !WorldMap.Loading && !_relay.LeavingHostMatch && !SessionExit.Leaving && !FriendTransitions.Busy;

    /// <summary>Friend, every frame: the question asked when it can be, and what was lent for it given back if it went unanswered. Never throws.</summary>
    private void UpdateFollowQuestion(long now)
    {
        try
        {
            UpdateFollowQuestionCore(now);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the question about {HostTag()}'s match failed ({ModLog.SafeErrorText(ex)}); your device is back in the session.");
            _questionGeneration++;
            Step("closing the question", LobbyPrompts.CloseNow);
            Step("giving back what the question had", () => GiveBackFromQuestion("it failed"));
            _question.Reset();
        }
    }

    private void UpdateFollowQuestionCore(long now)
    {
        if (_question.Asking)
        {
            bool runOver = HostRunOver;
            switch (_question.WhileAsking(reasonGone: runOver || !_relay.FriendMatchFailed, promptUp: LobbyPrompts.IsOpen))
            {
                case FollowQuestionStep.Close:
                    // The host's next match is loading here, or its run is over (Try again would join a game at its run's end): the
                    // question's reason has gone. Its close answers nothing (the prompt forgets who asked), so what was lent goes back
                    // here, once, before that match or the run's end screens want the device.
                    ModLog.Info($"Lobby: the question about {HostTag()}'s match closes: {(runOver ? "their run is over" : "their next match is loading here")}.");
                    _questionGeneration++;
                    LobbyPrompts.CloseNow();
                    ActOnAnswer(FollowAnswer.None);
                    break;
                case FollowQuestionStep.Gone:
                    // Gone with no answer reaching here (the game hid it, or it failed): nothing answered.
                    ActOnAnswer(FollowAnswer.None);
                    break;
                default:
                    // Kept told (a session that restarted meanwhile starts out with the device back).
                    _friend?.SetPadConnected(false, PadAwayReason.InSettings);
                    break;
            }

            return;
        }

        if (!_question.Waiting)
        {
            return;
        }

        if (!_relay.FriendMatchFailed || _flow.Friend != LobbyFriendState.Playing || HostRunOver)
        {
            _question.Cleared();
            ModLog.Info($"Lobby: the question about {HostTag()}'s match isn't asked: " +
                $"{(_flow.Friend != LobbyFriendState.Playing ? "this game no longer follows it" : HostRunOver ? "their run is over" : "their next match is loading here")}.");
            return;
        }

        switch (_question.Step(CanAskNow(), now))
        {
            case FollowQuestionStep.Ask:
                AskFollowQuestion();
                break;
            case FollowQuestionStep.Tell:
                ModLog.Info($"Lobby: no moment came within {FollowQuestion.WaitMicros / 1_000_000} s to ask about {HostTag()}'s match; the toast says it instead.");
                NotifyLimited(NoticeId.FollowFailed, _hostSteamId, _hostName);
                break;
        }
    }

    /// <summary>The device lent, the lock lifted for the prompt alone, the host told, and the game's prompt asked. Anything that can't be: told by the toast.</summary>
    private void AskFollowQuestion()
    {
        // First: should lending fail or throw part-way (a keyboard given back to some of its players), every give-back takes back
        // what went, as an own menu's failed lend does (TakeDeviceBack).
        _questionLent = true;
        if (!LendDevice(out string device))
        {
            ModLog.Warn($"Lobby: can't ask about {HostTag()}'s match: {device}; the toast says it instead.");
            GiveBackFromQuestion("it couldn't be lent");
            _question.NotAsked();
            NotifyLimited(NoticeId.FollowFailed, _hostSteamId, _hostName);
            return;
        }

        _friend?.SetPadConnected(false, PadAwayReason.InSettings);
        string what = $"the question about {HostTag()}'s match"; // for the log's lines about it
        // A Back, and the cursor held on the prompt, only while the prompt is what takes input (as the own Settings menu's): a menu
        // over it (a host's, mirrored) would otherwise get this player's Back.
        FriendUi.SuspendForOwnMenu(what, navigation: true, owns: LobbyPrompts.Owns, takesBack: LobbyPrompts.TakesInput,
            keepsCursor: navigable => LobbyPrompts.TakesInput() && !LobbyPrompts.Owns(navigable));

        int language = LanguageIndex();
        string host = DisplayName.ForRichText(_hostName); // the game's prompt has rich text on, and this is a Steam name
        int generation = ++_questionGeneration;
        bool asked = LobbyPrompts.AskChoice(LobbyWords.Online.Text(language), LobbyWords.AskMatchLoadFailed.Text(language, host),
            LobbyWords.TryAgain.Text(language), LobbyWords.Leave.Text(language), (first, second) =>
            {
                // Inside the prompt's tick, once it's over (as a failure's Try again is): what was chosen, as the lobby's Leave would be.
                if (generation == _questionGeneration)
                {
                    Guarded("the question about the host's match", () => ActOnAnswer(_question.Answer(first, second)));
                }
            });
        if (!asked)
        {
            ModLog.Warn($"Lobby: couldn't ask about {HostTag()}'s match (another prompt was up); the toast says it instead.");
            GiveBackFromQuestion("it couldn't be asked");
            _question.NotAsked();
            NotifyLimited(NoticeId.FollowFailed, _hostSteamId, _hostName);
            return;
        }

        ModLog.Info($"Lobby: asking: your game couldn't load {HostTag()}'s match: Try again (leave, then join the same lobby again) or Leave; B keeps following " +
            $"({device}; {HostTag()}'s game is told your device is away, and its AI plays your skater).");
    }

    /// <summary>The answer: Try again and Leave end the session (its end gives back the lent device); no answer gives it back and following goes on.</summary>
    private void ActOnAnswer(FollowAnswer answer)
    {
        switch (answer)
        {
            case FollowAnswer.TryAgain:
                ulong lobby = _link.Id;
                string host = _hostName;
                ModLog.Info($"Lobby: the question about {HostTag()}'s match: Try again: leaving, then joining lobby {Tags.Lobby(lobby)} again from the Join list once this game is home.");
                LeaveAll("leaving to join again: the host's match couldn't load here"); // its end gives the lent device back (EndFollowQuestion)
                if (lobby != 0)
                {
                    Screen("joining the same lobby again", s => s.JoinLobbyAgain(lobby, host));
                }

                break;
            case FollowAnswer.Leave:
                ModLog.Info($"Lobby: the question about {HostTag()}'s match: Leave.");
                LeaveAll("left, as asked");
                break;
            default:
                GiveBackFromQuestion("it closed with no answer");
                ModLog.Info($"Lobby: the question about {HostTag()}'s match closed with no answer (B, or this game moved on); following goes on, {LeaveWay} leaves.");
                break;
        }
    }

    /// <summary>What the question lent goes back to the session: the device, the lock whole, the host told. Nothing when nothing was lent.</summary>
    private void GiveBackFromQuestion(string why)
    {
        if (!_questionLent)
        {
            return;
        }

        _questionLent = false;
        Step("taking your device back", TakeDeviceBack);
        Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu(why));
        _friend?.SetPadConnected(_localPad?.IsConnected ?? true);
    }

    /// <summary>
    /// The play period or the session is ending (<paramref name="reason"/>) with the question waiting or up: it goes (its close
    /// answers nothing), the device stays where it was lent (as a session's end sets it), and the lock's lift ends. Each step on its own.
    /// </summary>
    private void EndFollowQuestion(string reason)
    {
        _questionGeneration++;
        if (_question.Asking)
        {
            Step("closing the question", LobbyPrompts.CloseNow);
        }

        if (_questionLent)
        {
            _questionLent = false;
            if (_localPad is { LentTo: >= 0 } pad)
            {
                Step("leaving your pad where it was lent", () => _padBack = pad.EndLend());
            }

            Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu($"the session ended: {reason}"));
        }

        if (_question.Asked + _question.Told > 0)
        {
            ModLog.Info($"Lobby: questions about the host's match this session: {_question.Summary()}.");
        }

        _question = new FollowQuestion(); // the next session counts afresh
    }
}
#endif
