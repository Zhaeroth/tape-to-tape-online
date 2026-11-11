#if HAS_INTEROP
using System;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Runtime;

/// <summary>
/// The install check's leftovers on the title (D125): the game's own one-button prompt, in the player's language, naming each
/// old file to delete (<see cref="NoticeId.InstallLeftovers"/>), once per session, when the title is idle (its warm-ups over, no
/// menu open, no loading screen). Run by the <see cref="OfflineShell"/>, which is up because online is off; its corner notice and
/// the developer's panel carry the same list in English, and are all a player has if the prompt can't be shown (logged). Never throws.
/// </summary>
internal sealed class InstallPrompt
{
    private const long LookEveryMs = 250;
    private const long GiveUpMs = 120_000;

    private readonly string _files;
    private readonly int _count;
    private long _since = -1;
    private long _nextLook;
    private State _state;

    /// <summary>Makes the prompt for <paramref name="verdict"/>'s files.</summary>
    public InstallPrompt(InstallVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        _files = InstallLayout.ForScreen(verdict.Entries);
        _count = verdict.Entries.Count;
    }

    private enum State
    {
        Waiting,
        Open,
        Done,
    }

    /// <summary>Every frame from the shell: waits for an idle title, shows the prompt, and keeps it answered.</summary>
    public void Update()
    {
        if (_state == State.Done)
        {
            return;
        }

        try
        {
            if (_state == State.Open)
            {
                return; // the native screens' layer ticks the prompt (NativeUi.NativeUiLayer)
            }

            long now = Environment.TickCount64;
            if (_since < 0)
            {
                _since = now;
            }

            if (now < _nextLook)
            {
                return;
            }

            _nextLook = now + LookEveryMs;
            if (now - _since > GiveUpMs)
            {
                _state = State.Done;
                ModLog.Warn($"Install check: no idle title screen within {GiveUpMs / 1000} s, so the game's prompt naming the old files wasn't shown; the corner notice lists them.");
                return;
            }

            if (TourHolds.HoldsTitlePrompts || LobbyPrompts.IsOpen || MenuGame.Module() == null || Il2CppMichsky.LSS.LoadingScreen.IsActive || LobbyShell.TitleAllowingOpen() == null)
            {
                return;
            }

            NoticeSpec spec = Notices.Of(NoticeId.InstallLeftovers);
            string text = LobbyLocalization.Say(Notices.LineOf(spec, _files).ForRichText());
            if (LobbyPrompts.Tell(LobbyLocalization.Say(LobbyWords.Online.Key), text, LobbyLocalization.Say(LobbyTerms.Ok), Answered))
            {
                _state = State.Open;
                ModLog.Info($"Install check: the game's prompt names the {_count} old file(s) to delete (listed in the install check's line above).");
            }
        }
        catch (Exception ex)
        {
            _state = State.Done;
            ModLog.Warn($"Install check: the game's prompt naming the old files failed ({ex.GetType().Name}); the corner notice lists them.");
        }
    }

    private void Answered()
    {
        _state = State.Done;
        ModLog.Info("Install check: the prompt naming the old files is gone (closed, or it never came: see above). Online stays off until they're deleted and the game restarts.");
    }
}
#endif
