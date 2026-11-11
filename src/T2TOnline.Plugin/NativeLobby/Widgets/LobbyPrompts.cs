#if HAS_INTEROP
using System;
using Il2CppBishopGames.Catalogs;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using PromptResult = Il2CppTape2Tape.Customization.UI.PromptTemplateMenu.PromptTemplateResult;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// The game's own yes/no prompt and notice, as the game opens them (<c>ShowMenuAsync</c>, as <see cref="PromptViews"/> does): over the shell, its Back the safe answer, its first button focused.
/// One at a time; the answer is read from the prompt's own <c>m_Result</c> when its close is over (the game sends it last; verified in the game's code: <c>CloseWithResult</c>: the hide,
/// then <c>SendResultOnce</c>), with no delegate on a by-value enum. Nothing here blocks the session. Their texts are this PC's.
/// <para>
/// Whoever asked is always told, once, however the prompt goes (<see cref="PromptClose"/>), but for <see cref="CloseNow"/>: what it asked about has gone, and nobody is
/// told (so nothing lands where the session has just moved on from). A prompt the game closes by itself (its plain hide, another menu's start, a scene load) is
/// destroyed as its close ends, and only then has its result (verified in the game's code: <c>UIModule.DisposeAfterHiding</c>, then <c>PromptTemplateMenu.BehaviourDestroy</c> sends
/// Cancel once). So a prompt is open until its answer is taken, whatever Unity says of its menu: the menu's object is held here and read after its Unity half has gone, and
/// <see cref="IsOpen"/> never asks Unity (to which a destroyed menu equals null).
/// </para>
/// </summary>
internal static class LobbyPrompts
{
    private const double FindSeconds = 8;

    private static readonly PromptClose Close = new();

    private static AbstractMenu? _open;
    private static PromptTemplateMenu? _openYesNo;
    private static double _since;
    private static Action<bool>? _answered;
    private static Action<bool>? _answeredRetry;
    private static Action<bool, bool>? _answeredChoice;
    private static bool _waiting;
    private static bool _closeWhenFound;
    private static IntPtr _topBefore;
    private static bool _takesInputFailed;

    /// <summary>
    /// A prompt asked for is on its way, up, or gone with its answer not yet taken (the next tick takes it). The held object itself is asked, never Unity: a menu
    /// the game has destroyed still owes its answer.
    /// </summary>
    public static bool IsOpen => _waiting || _open is not null;

    /// <summary>A prompt asked for here is on its way: what it gets as its data is this PC's own, not a host's for friends.</summary>
    public static bool IsAsking => _waiting;

    /// <summary>
    /// True when the prompt up, or on its way, was asked while <paramref name="menu"/> was the top menu: the lobby's own questions, and what the session said over
    /// it. Not one the player asked for over another menu (a problem report's from their own Settings). Never asks Unity of the prompt.
    /// </summary>
    public static bool AskedOver(AbstractMenu? menu) => IsOpen && menu is not null && !menu.WasCollected && _topBefore == menu.Pointer;

    /// <summary>A menu of this class showing now is one asked for here: each PC's own, never a host's menu for friends to follow.</summary>
    public static bool IsAskedFor(string type) => _waiting && type is nameof(CustomizableYesNoPrompt) or nameof(CustomizableDismissablePrompt);

    /// <summary>A two-button prompt. Its first button (the game's Yes) is the safe answer, and has focus; <paramref name="answered"/> gets true for the other one. B is the safe answer.</summary>
    public static bool Ask(string title, string text, string safeWord, string otherWord, Action<bool> answered)
    {
        if (IsOpen)
        {
            return false;
        }

        UIModule? ui = MenuGame.Module();
        if (ui == null)
        {
            return false;
        }

        var data = new CustomizableYesNoPrompt.Data(title, text, safeWord, otherWord, false, new Il2CppSystem.Nullable<PromptResult>(), false);
        _topBefore = TopNow();
        Begin(answered); // before the call: a prompt whose prefab is held shows inside it
        _ = ui.ShowMenuAsync<CustomizableYesNoPrompt>(MenuDefinitionCatalogReference.CustomizableYesNoPrompt.Name, data);
        return true;
    }

    /// <summary>A failure's two buttons: its first (Try again) focused; <paramref name="tryAgain"/> gets true only for a press of it. B, a close or a session's end are the other answer.</summary>
    public static bool AskRetry(string title, string text, string retryWord, string backWord, Action<bool> tryAgain)
    {
        if (!Ask(title, text, retryWord, backWord, _ => { }))
        {
            return false;
        }

        _answeredRetry = tryAgain;
        return true;
    }

    /// <summary>
    /// Two buttons and no answer: <paramref name="answered"/> gets (true, false) for the first (focused), (false, true) for the
    /// second, and (false, false) for B (the game's prompt closes with Cancel on Back: <c>PromptTemplateMenu.OnBackInput</c>),
    /// a close by the game (a scene load hides every menu) or a session's end.
    /// </summary>
    public static bool AskChoice(string title, string text, string firstWord, string secondWord, Action<bool, bool> answered)
    {
        ArgumentNullException.ThrowIfNull(answered);
        if (!Ask(title, text, firstWord, secondWord, _ => { }))
        {
            return false;
        }

        _answeredChoice = answered;
        return true;
    }

    /// <summary>
    /// True when <paramref name="component"/> is on the prompt asked for here: the one up, or while it's on its way, a game prompt
    /// (its first selection comes before it's found). For a lock lifted for the prompt alone (<c>FriendUi.SuspendForOwnMenu</c>).
    /// </summary>
    public static bool Owns(Component? component)
    {
        if (component == null || component.WasCollected)
        {
            return false;
        }

        if (_open != null && !_open.WasCollected)
        {
            return component.transform.IsChildOf(_open.transform);
        }

        return _waiting && component.GetComponentInParent<PromptTemplateMenu>() != null;
    }

    /// <summary>
    /// True while the prompt asked for here is up and the game's UI lets it take input: no menu over it
    /// (<c>UIModule.CanProcessInput</c>, the gate the game's own input handlers ask). A Back is its own then, and nothing else's.
    /// Never throws.
    /// </summary>
    public static bool TakesInput()
    {
        try
        {
            AbstractMenu? open = _open;
            if (open == null || open.WasCollected || open.IsHiding)
            {
                return false;
            }

            UIModule? ui = MenuGame.Module();
            return ui != null && ui.CanProcessInput(open);
        }
        catch (Exception ex)
        {
            if (!_takesInputFailed)
            {
                _takesInputFailed = true;
                ModLog.Warn($"Lobby: couldn't tell whether the prompt takes input ({ex.GetType().Name}); taken as not (said once).");
            }

            return false;
        }
    }

    /// <summary>A one-button notice: <paramref name="answered"/> is told when it's closed.</summary>
    public static bool Tell(string title, string text, string button, Action answered)
    {
        if (IsOpen)
        {
            return false;
        }

        UIModule? ui = MenuGame.Module();
        if (ui == null)
        {
            return false;
        }

        var data = new CustomizableDismissablePrompt.Data(title, text, button, false);
        _topBefore = TopNow();
        Begin(_ => answered()); // before the call: a prompt whose prefab is held shows inside it
        _ = ui.ShowMenuAsync<CustomizableDismissablePrompt>(MenuDefinitionCatalogReference.CustomizableDismissablePrompt.Name, data);
        return true;
    }

    /// <summary>The prompt that's up goes, with its safe answer: what it asked about has gone (a session ending under it).</summary>
    public static void CloseNow()
    {
        if (_waiting)
        {
            // Asked for, not up yet: it goes as it comes, or a question about what's gone stays on the screen.
            _answered = null;
            _answeredRetry = null;
            _answeredChoice = null;
            _closeWhenFound = true;
        }
        else if (_open != null && !_open.WasCollected && !_open.IsHiding)
        {
            _answered = null;
            _answeredRetry = null;
            _answeredChoice = null;
            Close.ClosedByAsker();
            _ = _open.Hide();
        }
    }

    /// <summary>Every frame: finds what was opened, and sees it go.</summary>
    public static void Tick()
    {
        if (!IsOpen)
        {
            return;
        }

        try
        {
            if (_waiting)
            {
                AbstractMenu? top = LobbyShell.TopShown();

                // Not one that was up already (the session's own question, the game's onboarding): ours comes on top of it.
                if (top != null && top.Pointer != _topBefore && (top.TryCast<PromptTemplateMenu>() != null || top.TryCast<CustomizableDismissablePrompt>() != null))
                {
                    _open = top;
                    _openYesNo = top.TryCast<PromptTemplateMenu>();
                    _waiting = false;
                    if (_closeWhenFound || (Campaign.Scenes.FriendUi.Locked && !Campaign.Scenes.FriendUi.Suspended))
                    {
                        // What it asked about has gone; or a Start's lock came first, and nothing could dismiss it under the lock (the log has what it said). A lock
                        // lifted in part for a question of the session's own (FriendUi.SuspendForOwnMenu) lets it be answered.
                        _closeWhenFound = false;
                        Close.ClosedByAsker();
                        _ = top.Hide();
                    }
                }
                else if (FrameClock.Seconds - _since > FindSeconds)
                {
                    ModLog.Warn($"Lobby: a prompt didn't come within {FindSeconds:0} s; taking its safe answer.");
                    Action<bool>? retry = _answeredRetry;
                    Action<bool, bool>? choice = _answeredChoice;
                    _answeredRetry = null;
                    _answeredChoice = null;
                    Finish(false);
                    retry?.Invoke(false);
                    choice?.Invoke(false, false);
                }

                return;
            }

            // The result is read from the held object whatever Unity says of the menu by now: it comes as the close ends, or as the menu is destroyed (the game's own
            // Cancel), and the menu may be gone in that very frame. Unity is asked only whether the menu has gone (a destroyed one equals null), and never called on it then.
            AbstractMenu open = _open!;
            PromptTemplateMenu? yesNo = _openYesNo;
            bool sent = yesNo is { WasCollected: false } && yesNo.m_HasSentResult;
            PromptResult result = sent ? yesNo!.m_Result : PromptResult.Cancel;
            bool gone = open.WasCollected || Destroyed(open);
            var look = new PromptLook(yesNo is not null, sent, sent && result == PromptResult.Yes, sent && result == PromptResult.No, gone, !gone && open.IsHiding, !gone && open.m_IsShown);

            // A notice has no answer, nor a yes/no closed by whoever asked. One closed from outside (a Start closing what's open, a scene load) has the game's Cancel as it's
            // destroyed, or none: the safe answer either way, at the latest a moment after its close is over. It can't take for ever.
            switch (Close.Look(look, FrameClock.Seconds))
            {
                case PromptAnswer.First:
                    Answer(other: false, first: true);
                    break;
                case PromptAnswer.Second:
                    Answer(other: true, first: false);
                    break;
                case PromptAnswer.Neither:
                    Answer(other: false, first: false);
                    break;
                case PromptAnswer.NeitherLate:
                    ModLog.Warn($"Lobby: a prompt took more than {PromptClose.CloseSeconds:0} s to close; taking its safe answer.");
                    Answer(other: false, first: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: a prompt's tick failed ({ModLog.SafeErrorText(ex)}).");
            Action<bool>? retry = _answeredRetry;
            Action<bool, bool>? choice = _answeredChoice;
            _answeredRetry = null;
            _answeredChoice = null;
            Finish(false);
            retry?.Invoke(false);
            choice?.Invoke(false, false); // no answer: whoever asked gives back what it lent for the question
        }
    }

    private static void Answer(bool other, bool first)
    {
        Action<bool>? retry = _answeredRetry;
        Action<bool, bool>? choice = _answeredChoice;
        _answeredRetry = null;
        _answeredChoice = null;
        Finish(other);
        retry?.Invoke(first);
        choice?.Invoke(first, other);
    }

    /// <summary>Unity's own word on <paramref name="menu"/>: destroyed (its equality takes a destroyed object for null).</summary>
    private static bool Destroyed(UnityEngine.Object menu) => menu == null;

    private static IntPtr TopNow() => LobbyShell.TopShown() is { } top ? top.Pointer : IntPtr.Zero;

    private static void Begin(Action<bool> answered)
    {
        _closeWhenFound = false;
        Close.Begin();
        _answered = answered;
        _answeredRetry = null;
        _answeredChoice = null;
        _since = FrameClock.Seconds;
        _waiting = true;
        _open = null;
    }

    private static void Finish(bool other)
    {
        Action<bool>? answered = _answered;
        _answered = null;
        _open = null;
        _openYesNo = null;
        _waiting = false;
        answered?.Invoke(other);
    }
}

/// <summary>
/// A short toast through the game's own <c>NotificationQueue</c>: its panel, its sound. Marked while it's queued, so <see cref="Toasts"/> doesn't send it to friends: each PC queues its own.
/// </summary>
internal static class LobbyToast
{
    /// <summary>True while one of these is being queued (<see cref="Toasts"/> leaves it alone).</summary>
    public static bool Queuing { get; private set; }

    /// <summary>
    /// Queues a line, its names kept from rich text, for when the game may show it. False without the game's queue. Where the queue shows a timing by itself (the title's
    /// 0x10, the map's 2, <c>SetAutoNotify</c>) a toast for all timings goes at once (verified in the game's code: <c>Queue</c>). A match's queue shows none by itself, so "now" there
    /// pops the title's timing, which only this toast can hold in a match (<c>NotificationPusher</c> clears the queue at a game's start and end), and the game's own toasts keep their moments.
    /// <paramref name="title"/> is the toast's title, the Online word when null.
    /// </summary>
    public static bool Show(LobbyLine line, ToastTiming timing = ToastTiming.Now, string? title = null)
    {
        NotificationQueue? queue = NotificationQueue.Instance;
        if (queue == null)
        {
            return false;
        }

        var safe = new string[line.Args.Count];
        for (int i = 0; i < safe.Length; i++)
        {
            safe[i] = DisplayName.ForRichText(line.Args[i]);
        }

        string text = LobbyLocalization.Say(new LobbyLine(line.Term, safe));
        Queuing = true;
        try
        {
            ENotificationTiming mask = timing == ToastTiming.NextFaceoff ? ENotificationTiming.NextFaceoff : ENotificationTiming.All;
            queue.QueueGenericNotification(mask, title ?? LobbyLocalization.Say(LobbyWords.Online.Key), text, new Il2CppStringArray(0), new Il2CppReferenceArray<Sprite>(0), null!, true);
            if (timing == ToastTiming.Now && queue.m_AutoNotifyTimings == ENotificationTiming.None)
            {
                queue.DisplayNotificationsNow(ENotificationTiming.TitleScreen);
            }
        }
        finally
        {
            Queuing = false;
        }

        return true;
    }
}
#endif
