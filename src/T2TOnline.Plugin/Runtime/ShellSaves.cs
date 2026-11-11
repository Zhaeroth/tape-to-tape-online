#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Runtime;

/// <summary>
/// What the saves' start-up steps (<see cref="SavesAtLaunch"/>) leave for the title while T2T Online is off, done by the
/// <see cref="OfflineShell"/> as the session and run sync do it with online on: the notices of a restore put right at
/// start-up (<see cref="SaveBackups.TakeStartUpNotices"/>), each said once the title is idle (its warm-ups over, no menu open,
/// no loading screen), a blocking one in the game's own one-button prompt and the rest in its toast, in the player's
/// language; and the restored files Steam Cloud must still get that the game didn't load on its way to the title, sent there
/// (<see cref="SaveBackups.PushRestoredAtTitle"/>). On a game build the mod may patch; and on another whose members all
/// resolved, for the notices only (Steam Cloud's copies that may come back over a restore above all: <c>pushes</c> false, as
/// sending needs the guard on the game's loads). A notice that can't be said goes to the shell's corner notice
/// (<see cref="Fallback"/>), which carries <see cref="SaveBackups.Notice"/>. Nothing at all to do costs a field read a frame.
/// Never throws.
/// </summary>
internal sealed class ShellSaves
{
    private const long LookEveryMs = 250;
    private const long GiveUpMs = 120_000;

    private readonly Queue<NoticeId> _waiting = new();
    private readonly bool _pushes;
    private long _since = -1;
    private long _nextLook;
    private bool _taken;
    private bool _promptOpen;
    private bool _noticesDone;
    private bool _stopped;

    /// <summary>Creates it; <paramref name="pushes"/>: the restored files Steam Cloud must still get are sent at the title (a game build the mod patches).</summary>
    public ShellSaves(bool pushes) => _pushes = pushes;

    /// <summary>True once a notice couldn't be said on the game's screens: the corner notice says it instead.</summary>
    public bool Fallback { get; private set; }

    /// <summary>Every frame from the shell: waits for an idle title, sends the restored files Steam Cloud must still get, and says each notice in turn.</summary>
    public void Update()
    {
        if (_stopped || (_noticesDone && (!_pushes || !SaveBackups.PushDue)))
        {
            return;
        }

        try
        {
            long now = Environment.TickCount64;
            if (_promptOpen)
            {
                // The give-up clock waits for a title free for the next notice, not for the player to read this one: it starts
                // again when this prompt closes.
                _since = now;
                return; // the native screens' layer ticks the prompt (NativeUi.NativeUiLayer)
            }

            if (_since < 0)
            {
                _since = now;
            }

            if (now < _nextLook)
            {
                return;
            }

            _nextLook = now + LookEveryMs;
            if (!_noticesDone && now - _since > GiveUpMs)
            {
                GiveUp($"the title wasn't free for it within {GiveUpMs / 1000} s: not up, a menu open, or another prompt up");
            }

            if (_pushes && SaveBackups.PushDue && CampaignGame.TitleLoaded())
            {
                // As run sync's update does with online on: the restored files the game didn't load go to Steam Cloud at the title.
                SaveBackups.PushRestoredAtTitle();
            }

            if (_noticesDone || LobbyPrompts.IsOpen || MenuGame.Module() == null || Il2CppMichsky.LSS.LoadingScreen.IsActive || LobbyShell.TitleAllowingOpen() == null)
            {
                return;
            }

            SayNext();
        }
        catch (Exception ex)
        {
            // Whichever part failed, both stop: the notices go to the corner notice, and the restored files stay listed on disk
            // (the game's load of each, or a later start, sends them).
            _promptOpen = false;
            _stopped = true;
            if (!_noticesDone)
            {
                GiveUp($"the game's screens failed: {ex.GetType().Name}");
            }

            ModLog.Warn($"Backups: the title's look at the saves failed ({ModLog.SafeErrorText(ex)}); it's off for this session.");
        }
    }

    /// <summary>The next notice, at an idle title: a blocking one as the game's prompt (the next waits for its close), a toast at once.</summary>
    private void SayNext()
    {
        if (!_taken)
        {
            _taken = true;
            foreach (NoticeId id in SaveBackups.TakeStartUpNotices())
            {
                _waiting.Enqueue(id);
            }
        }

        if (_waiting.Count == 0)
        {
            _noticesDone = true;
            return;
        }

        NoticeId next = _waiting.Peek();
        NoticeSpec spec = Notices.Of(next);
        LobbyLine line = Notices.LineOf(spec);
        if (spec.Kind == NoticeKind.Blocking)
        {
            string text = LobbyLocalization.Say(line.ForRichText());
            if (LobbyPrompts.Tell(LobbyLocalization.Say(LobbyWords.Online.Key), text, LobbyLocalization.Say(LobbyTerms.Ok), Answered))
            {
                _waiting.Dequeue();
                _promptOpen = true;
                ModLog.Info($"Backups: {next} is on screen (the game's prompt), at the title.");
            }

            return;
        }

        _waiting.Dequeue();
        bool shown;
        using (OwnToasts.Begin())
        {
            shown = spec.Kind == NoticeKind.Toast && LobbyToast.Show(line, ToastTiming.Now);
        }

        if (shown)
        {
            ModLog.Info($"Backups: {next} is on screen (the game's toast), at the title.");
        }
        else if (spec.IsShown)
        {
            GiveUp($"{next}: the game has no toast queue here");
        }
    }

    private void Answered()
    {
        _promptOpen = false;
        ModLog.Info("Backups: the prompt about what the start-up found is closed.");
    }

    /// <summary>The notices left go to the shell's corner notice (<see cref="SaveBackups.Notice"/>, English; the developer's panel shows them too).</summary>
    private void GiveUp(string why)
    {
        _noticesDone = true;
        _waiting.Clear();
        if (!_taken)
        {
            _taken = true;
            if (SaveBackups.TakeStartUpNotices().Count == 0)
            {
                return; // nothing was waiting to be said
            }
        }

        Fallback = true;
        ModLog.Warn($"Backups: what the start-up found couldn't be said on the game's screens ({why}); the corner notice says it.");
    }
}
#endif
