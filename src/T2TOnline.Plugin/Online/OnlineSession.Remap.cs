#if HAS_INTEROP
using System;
using System.Globalization;
using System.Linq;
using Il2CppRewired;
using Il2CppRewired.UI.ControlMapper;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Online.Panel;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Online;

// A friend's own game menus while joined: "Change my controls" (F1 > Controller) opens this game's own remapper as its Settings > Controls >
// Remap does, and "Your game's settings" (F1 > Controller and F1 > Settings) its whole Settings menu, as a couch player reaches both from the
// pause menu. Meanwhile their device is lent back to their own game (the host is told why, and its AI plays their skater after 3 s), the friend
// UI lock is lifted for that menu only, and only the Settings menu's own files may be saved. When it closes, their controls are read again
// (with the maps' snapshots, so the session's end gives back the new ones) and sent to the host if they changed (a new generation: the host
// rebuilds their virtual controller's maps and the maps guard takes the new fingerprint), their device is taken back and their prompts pinned
// again. A menu that can't open falls back to leaving, remapping at the title and rejoining, said on screen. A pad that's another one after a
// swap has its own maps read and sent the same way. Apart from what the session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    /// <summary>How long a Settings menu that showed after its flow gave up is looked for, to close it (no flow is left to drive it).</summary>
    private const long LateMenuMicros = 10_000_000;

    /// <summary>A joystick id no pad has: the next pad picked up is read whatever its own (<see cref="WatchPadSwap"/>).</summary>
    private const int NoPadRead = -1;

    private OwnMenuFlow _ownMenu = new();
    private OwnSettingsMenu? _ownMenuView;
    private OwnSettingsMenu? _lateMenu;
    private long _lateMenuUntil;
    private SaveAllowance? _settingsSaves;
    private bool _remapperAsked;
    private bool _remapperOpen;
    private long _remapperOpenedAt;
    private int _remapperPlayer = -1;
    private int _lendPlayer = -1;
    private bool _keyboardLent;
    private bool _ownMenuOff;
    private int _uiCategory = -1;
    private int _padSwapJoystick = int.MinValue;
    private bool _padSwapPending;
    private readonly RepeatLimiter _padAwayLines = new(24, 60_000, 16);

    /// <summary>A friend's own menus for the panel: whether they can be opened now, whether one is, and a line about it.</summary>
    public OwnMenuView OwnMenu
    {
        get
        {
            // In the lobby phase there's nothing to lend: their game and device are theirs, and the title's own Settings are one press away.
            if (_friend is not { State: FriendSessionState.Joined } || (_localPad == null && _localKeyboard == null) || _broken || !DeviceWithHost)
            {
                return _ownMenu.HoldsDevice ? new OwnMenuView(false, true, "Your own menu is open; your session is reconnecting.") : OwnMenuView.None;
            }

            if (OwnUnlocks.Holding)
            {
                // Its own presses close it; Close it here is the way out should they not reach it.
                return new OwnMenuView(false, true, "Your new-unlocks screen is open: the press past its last unlock closes it (or Close it here); your way home waits for it.");
            }

            if (_ownMenuOff)
            {
                return new OwnMenuView(false, false, $"Your own menus are off until the game restarts, after an error (see the log): {LeaveWay} to leave, Settings at the title, then {JoinWay} again.");
            }

            string host = _solo ? "the one-PC test's host" : _hostName;
            return _ownMenu.State switch
            {
                OwnMenuState.Idle => new OwnMenuView(true, false,
                    $"Change your controls, or your game's own settings (volume, video, controls; language at the title screen only), without leaving; {host}'s game is told, and its AI plays your skater meanwhile if a match is on."),
                OwnMenuState.Opening => new OwnMenuView(true, true, $"Opening your game's {(_ownMenu.Kind == OwnMenuKind.Remapper ? "remapper" : "settings")}..."),
                OwnMenuState.Open when _ownMenu.Kind == OwnMenuKind.Remapper => new OwnMenuView(true, true,
                    "Your game's remapper is open: change your controls with your pad or the mouse, then close it (its Close, or Esc / B). Pad not answering it? Use the mouse, or Close it here."),
                OwnMenuState.Open => new OwnMenuView(true, true, "Your game's settings are open: Back (Esc / B) closes them; your controls are read again then."),
                _ => new OwnMenuView(true, true, "Reading your controls again..."),
            };
        }
    }

    /// <summary>
    /// The panel's "Change my controls" (<paramref name="remapper"/>) or "Your game's settings", or My Settings: opens this game's own remapper
    /// or Settings menu while joined; the Settings menu on its Online tab with <paramref name="onlineTab"/> (My Settings: Leave Online Game is
    /// there, the game's own tabs a bumper away). Returns a line for the panel saying what happened.
    /// </summary>
    public string OpenOwnMenu(bool remapper, bool onlineTab = false)
    {
        try
        {
            return OpenOwnMenuCore(remapper ? OwnMenuKind.Remapper : OwnMenuKind.Settings, onlineTab);
        }
        catch (Exception ex)
        {
            OwnMenuFailed("opening your own menu", ex);
            return $"Your own menus failed (see the log); they're off until the game restarts. {LeaveWay}, Settings at the title, {JoinWay} again still works.";
        }
    }

    /// <summary>The panel's "Close it": closes the friend's own menu the way its Back would (the remapper with its own close, saving). A line for the panel.</summary>
    public string CloseOwnMenu()
    {
        try
        {
            return CloseOwnMenuCore();
        }
        catch (Exception ex)
        {
            OwnMenuFailed("closing your own menu", ex);
            return "Your own menus failed (see the log); your device is back in the session.";
        }
    }

    private string OpenOwnMenuCore(OwnMenuKind kind, bool onlineTab)
    {
        string what = kind == OwnMenuKind.Remapper ? "your game's remapper" : "your game's settings";
        if (_friend is not { State: FriendSessionState.Joined } friend || (_localPad == null && _localKeyboard == null) || !DeviceWithHost)
        {
            return $"Only in a friend's game: {what} is in Settings at your title screen otherwise.";
        }

        if (_ownMenuOff)
        {
            return $"Your own menus are off until the game restarts, after an error (see the log): {LeaveWay} to leave, Settings at the title, then {JoinWay} again.";
        }

        if (_ownMenu.HoldsDevice)
        {
            return "Your own menu is open already; close it first.";
        }

        if (_unlocksLent)
        {
            return "Your new-unlocks screen is open; it closes past its last unlock.";
        }

        if (_questionLent)
        {
            return "A question is up on your screen; answer it first (B keeps following).";
        }

        if (_localPad is { IsConnected: false })
        {
            return "Your pad is unplugged: plug it back in first.";
        }

        long now = Now;
        _ownMenu.TryStart(kind, now);
        _lateMenu = null; // a late one from before is this one's now
        _remapperAsked = false;
        _remapperOpen = false;
        _remapperPlayer = -1;
        if (!LendDevice(out string device))
        {
            _ownMenu.Abort(now, fellBack: false);
            ModLog.Warn($"Your controls: can't open {what}: {device}.");
            return $"Can't open {what}: {device}.";
        }

        friend.SetPadConnected(false, _ownMenu.AwayReason);
        OwnSettingsMenu view = _ownMenuView = new OwnSettingsMenu();
        bool settings = kind == OwnMenuKind.Settings;
        FriendUi.SuspendForOwnMenu(what, navigation: settings, owns: settings ? view.Owns : static _ => false, takesBack: settings ? view.TakesBack : static () => false, keepsCursor: view.KeepsCursorFrom);
        if (settings)
        {
            _settingsSaves ??= SaveGuard.Allow("your own settings, while you follow", OwnSettingsFiles.Names.ToArray());
        }

        string host = _solo ? "the one-PC test's host side" : $"{HostTag()}'s game";
        string ai = $"{host} is told why, and its AI plays your skater after 3 s if a match is on (its AI stand-in setting)";
        bool online = settings && onlineTab;
        ModLog.Info(settings
            ? $"Your settings: opening this game's Settings menu{(online ? " on its Online tab" : string.Empty)} (volume, video, controls, language) while you follow ({device}; {ai}). " +
              "What you change stays on this PC; its gameplay options are for your own games (the host's game uses the host's). " +
              $"{LanguageHint()}"
            : $"Your controls: opening this game's remapper ({device}; {ai}).");

        // On the Online tab: asked just before the game's open, which the tab's patch takes as its data step runs (NativeUi/SettingsTab.cs).
        if (online)
        {
            NativeUi.SettingsTab.RequestOnlineTab("My Settings");
        }

        if (!OwnSettingsMenu.Show(controlsTab: !settings, out string problem))
        {
            if (online)
            {
                NativeUi.SettingsTab.ForgetOnlineTab(); // no open came: a later one is the game's own
            }

            FallBack(now, problem);
            return $"Couldn't open {what} here ({problem}).";
        }

        Notify(NoticeId.OwnMenu, settings
            ? "Your game's settings: Back closes them. Volume and video are yours; gameplay options apply in your own games. Your skater plays itself meanwhile."
            : "Your game's remapper: change your controls with your pad or the mouse, then close it. Your skater plays itself meanwhile.");
        return settings ? "Opening your game's settings." : "Opening your game's remapper.";
    }

    private string CloseOwnMenuCore()
    {
        if (OwnUnlocks.Holding)
        {
            OwnUnlocks.Abort("closed from the panel");
            return "Closing your new-unlocks screen.";
        }

        if (!_ownMenu.HoldsDevice || _ownMenuView == null)
        {
            return "No menu of yours is open.";
        }

        if (_ownMenuView.OpenMapper is { } mapper)
        {
            ModLog.Info("Your controls: closing the remapper from the panel (its own close, which saves what you changed).");
            mapper.Close(true);
            return "Closing the remapper.";
        }

        ModLog.Info("Your settings: closing the Settings menu from the panel (its own back).");
        _ownMenuView.Close();
        if (_ownMenu.State == OwnMenuState.Opening)
        {
            FallBack(Now, "closed from the panel before it opened");
        }

        return "Closing your game's settings.";
    }

    /// <summary>
    /// Friend, every frame: the friend's own menu, step by step (shown, the remapper opened over it and its player chosen,
    /// closed, the device taken back), the host kept told the device is away (a session that restarted meanwhile starts out
    /// with it back), time limits, the settings files' grace, and a pad swapped for another one.
    /// </summary>
    private void UpdateOwnMenu(long now)
    {
        try
        {
            UpdateOwnMenuCore(now);
        }
        catch (Exception ex)
        {
            // The session carries on: only this feature stops, with the device back in the session.
            OwnMenuFailed("following your own menu", ex);
        }
    }

    /// <summary>
    /// The friend's own menu failed (<paramref name="what"/>): the menu closed if it's up (the lock would leave it on their
    /// screen with nothing answering it), their device back in the session, the lock whole again, the host told they're back,
    /// and own menus off for the rest of the session (the panel says so). Said once. Never throws.
    /// </summary>
    private void OwnMenuFailed(string what, Exception ex)
    {
        bool first = !_ownMenuOff;
        _ownMenuOff = true;
        if (first)
        {
            ModLog.Error($"Your controls: {what} failed; your own menus are off until the game restarts ({LeaveWay}, Settings at the title, {JoinWay} again still works)", ex);
        }

        OwnSettingsMenu? view = _ownMenuView;
        Step("closing your own remapper", () => view?.OpenMapper?.Close(true));
        Step("closing your own menu", () => view?.Close());
        Step("taking your device back", TakeDeviceBack);
        Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu("an error"));
        Step("telling the host your device is back", () => _friend?.SetPadConnected(_localPad?.IsConnected ?? true));
        _ownMenu.Abort(Now, fellBack: false);
        _ownMenuView = null;
        _lateMenu = view is { Menu: null } ? view : null; // asked for and not up yet: closed if it shows after all
        _lateMenuUntil = Now + LateMenuMicros;
        if (first)
        {
            Notify(NoticeId.OwnMenuFailed);
        }
    }

    private void UpdateOwnMenuCore(long now)
    {
        switch (_ownMenu.Update(now))
        {
            case OwnMenuTick.OpenTimedOut:
                FallBack(now, $"it didn't open within {OwnMenuFlow.OpenTimeoutMicros / 1_000_000} s");
                return;
            case OwnMenuTick.CloseTimedOut:
                ModLog.Warn($"Your controls: your own menu didn't finish closing within {OwnMenuFlow.CloseTimeoutMicros / 1_000_000} s; taking your device back anyway.");
                FinishOwnMenu(now, "it took too long to close");
                return;
            case OwnMenuTick.AllowanceOver:
                EndSettingsSaves();
                break;
        }

        if (!_ownMenu.HoldsDevice || _ownMenuView is not { } view)
        {
            CloseLateMenu(now);
            if (!_ownMenuOff)
            {
                WatchPadSwap();
            }

            return;
        }

        // Kept told (a session that restarted meanwhile starts out with the device back); the remapper opened from the
        // Settings menu is "changing controls" for as long as it's open.
        _friend?.SetPadConnected(false, _remapperOpen ? PadAwayReason.ChangingControls : _ownMenu.AwayReason);
        switch (_ownMenu.State)
        {
            case OwnMenuState.Opening:
                StepOpening(now, view);
                break;
            case OwnMenuState.Open:
                StepOpen(now, view);
                break;
            case OwnMenuState.Closing when view.Menu == null || view.Gone:
                FinishOwnMenu(now, "its menu closed");
                break;
        }
    }

    /// <summary>Opening: the Settings menu found once it's up; for the remapper, then asked for and waited for.</summary>
    private void StepOpening(long now, OwnSettingsMenu view)
    {
        if (view.Find() == null)
        {
            return;
        }

        string after = Seconds(now - _ownMenu.StartedAtMicros);
        if (_ownMenu.Kind == OwnMenuKind.Settings)
        {
            _ownMenu.Shown(now);
            ModLog.Info($"Your settings: this game's Settings menu is open after {after}" + (!FriendUi.Suspended ? "." // the one-PC test: not locked, it's the host's own game
                : view.HasSystemTab ? "; its System tab (delete saves, reset unlocks and stats) does nothing while you follow" +
                    (view.HasLanguageRow ? ", but its Language row is yours (the game lets it work at the title screen only)." : "; its Language row wasn't found, so the language can't be changed here.")
                : $"; its System tab wasn't found, so none of its buttons answer while you follow (only its back{(ModSettings.DevTools ? ", or Close it in F1" : string.Empty)})."));
            return;
        }

        if (!_remapperAsked)
        {
            _remapperAsked = true;
            if (!view.OpenRemapper(out string problem))
            {
                FallBack(now, problem);
            }

            return;
        }

        if (view.OpenMapper is { } mapper)
        {
            _ownMenu.Shown(now);
            RemapperOpened(now, mapper, "path: the game's remapper while joined");
        }
        else if (view.Gone)
        {
            FallBack(now, "its Settings menu closed before the remapper opened");
        }
    }

    /// <summary>Open: the remapper watched (its player chosen as it opens); its close, or the menu's, starts the closing.</summary>
    private void StepOpen(long now, OwnSettingsMenu view)
    {
        ControlMapper? mapper = view.OpenMapper;
        if (mapper != null && !_remapperOpen)
        {
            RemapperOpened(now, mapper, "from your Settings menu's Remap");
        }
        else if (mapper != null)
        {
            _remapperPlayer = mapper.currentPlayerId;
        }
        else if (_remapperOpen)
        {
            _remapperOpen = false;
            ModLog.Info($"Your controls: the remapper closed after {Seconds(now - _remapperOpenedAt)}" + RemapperPlayerNote() + ".");
            if (_ownMenu.Kind == OwnMenuKind.Remapper)
            {
                // As the couch player who opened it for the remapper only: the Settings menu under it goes too.
                _ownMenu.Closed(now);
                view.Close();
                return;
            }
        }

        if (view.Menu == null || view.Gone)
        {
            _ownMenu.Closed(now);
        }
    }

    /// <summary>The remapper is open: it's shown on the player the friend's device is lent to (it opens on the last active controller's).</summary>
    private void RemapperOpened(long now, ControlMapper mapper, string how)
    {
        _remapperOpen = true;
        _remapperOpenedAt = now;
        int player = _lendPlayer;
        string chosen = string.Empty;
        if (player >= 0 && mapper.currentPlayerId != player)
        {
            try
            {
                int was = mapper.currentPlayerId;
                mapper.OnPlayerSelected(player, true);
                chosen = string.Create(CultureInfo.InvariantCulture, $" (it opened on Rewired player {was}; Rewired player {player} chosen for you)");
            }
            catch (Exception ex)
            {
                chosen = $" (choosing Rewired player {player} failed: {ex.GetType().Name}; pick it at the remapper's top)";
            }
        }

        _remapperPlayer = mapper.currentPlayerId;
        ModLog.Info($"Your controls: the remapper is open after {Seconds(now - _ownMenu.StartedAtMicros)}, on Rewired player {_remapperPlayer}{chosen}, " +
            $"where your {(_localKeyboard != null ? "keyboard and mouse are" : "pad is")}; {how}.");
    }

    /// <summary>A note for the remapper's close line when it was left on another player than the friend's device's (what was changed there isn't theirs).</summary>
    private string RemapperPlayerNote() =>
        _lendPlayer >= 0 && _remapperPlayer >= 0 && _remapperPlayer != _lendPlayer
            ? string.Create(CultureInfo.InvariantCulture, $"; it was left on Rewired player {_remapperPlayer}, and yours is {_lendPlayer}: changes made there aren't yours")
            : string.Empty;

    /// <summary>
    /// Lends the friend's device back to their own game: a pad to the player that had it (the UI's map category on only), a
    /// keyboard and mouse to their players (not in the one-PC test, which never takes them). False, with why, when it can't.
    /// </summary>
    private bool LendDevice(out string device)
    {
        _lendPlayer = -1;
        _keyboardLent = false;
        if (_localPad != null)
        {
            if (_uiCategory < 0)
            {
                _uiCategory = ReInput.mapping.GetMapCategoryId("UI");
            }

            int player = _uiCategory >= 0 ? _localPad.Lend(new[] { _uiCategory }) : -1;
            if (player < 0)
            {
                device = _uiCategory < 0 ? "Rewired has no UI map category here" : "your pad couldn't be lent to a player of this game (unplugged, or none is free)";
                return false;
            }

            _lendPlayer = player;
            device = $"your pad goes back on Rewired player {player} meanwhile";
            return true;
        }

        LocalKeyboard keyboard = _localKeyboard!;
        _lendPlayer = keyboard.SourcePlayer;
        if (_solo || !keyboard.IsDetached)
        {
            device = "your keyboard stays on this game's players";
            return true;
        }

        _keyboardLent = true; // first: should lending fail part-way, the failure's TakeDeviceBack takes it again
        keyboard.Lend();
        device = $"your keyboard and mouse go back on this game's players {keyboard.Owners} meanwhile";
        return true;
    }

    /// <summary>
    /// The friend's own menu closed: their controls read again, their device taken back, the lock whole again, the host told
    /// they're back, and the new controls sent (with their prompts pinned again) when they changed.
    /// </summary>
    private void FinishOwnMenu(long now, string why)
    {
        OwnMenuKind kind = _ownMenu.Kind;
        InputBindingSet? before = _controls;
        InputBindingSet? after = null;
        Step("reading your controls again", () => after = ReadControlsAgain());
        Step("taking your device back", TakeDeviceBack);
        Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu(why));
        _friend?.SetPadConnected(_localPad?.IsConnected ?? true);
        _ownMenu.Finish(now);
        _ownMenuView = null;

        string label = kind == OwnMenuKind.Remapper ? "Your controls" : "Your settings";
        string held = Seconds(_ownMenu.LastHeldMicros);
        if (after == null || after.SameControlsAs(before))
        {
            ModLog.Info($"{label}: your own menu closed after {held} ({why}); your controls are unchanged, so nothing was sent. " +
                $"Your {(_localPad != null ? "pad" : "keyboard")} is taken again: only the online session reads it.");
            Notify(NoticeId.OwnMenu, "Back in the game: your controls are as they were.");
            return;
        }

        _controls = after;
        _friend?.SetBindings(after);
        NoteControlsChanged("your own menu"); // the pin reads the maps again (what it follows of the game's own choice kept), the host gets new prompt names

        string hostTag = _solo ? "the one-PC test's host side" : HostTag();
        ModLog.Info($"{label}: your own menu closed after {held} ({why}); your controls read again (generation {after.Generation}) are new (changed mid-session): " +
            $"{(after.Source == BindingSource.OwnMaps ? $"{after.Differences} differ from the game's defaults" : "the game's defaults")}; sent to {hostTag}. " +
            $"Your {(_localPad != null ? "pad" : "keyboard")} is taken again: only the online session reads it.");
        NotifyText(NoticeId.OwnMenu, $"Back in the game with your new controls; {(_solo ? "the host side" : _hostName)} uses them now.",
            $"Back in the game with your new controls; {(_solo ? "the host side" : HostTag())} uses them now.");
    }

    /// <summary>The friend's controls read again (a new generation), while the device is still lent: a pad's from the player it's on, a keyboard's afresh (with new snapshots of its maps).</summary>
    private InputBindingSet? ReadControlsAgain()
    {
        if (_localPad != null)
        {
            if (!_localPad.IsConnected)
            {
                // Its maps can't be read from a joystick that's gone: the controls stay, and the pad picked up next is read
                // whatever its id (WatchPadSwap), with whatever was changed and saved here.
                _padSwapJoystick = NoPadRead;
                ModLog.Info("Your controls: your pad is unplugged, so its maps aren't read now; they're read when it's back.");
                return null;
            }

            return PadRemapReader.Read(_localPad, NextControlsGeneration());
        }

        if (_localKeyboard == null)
        {
            return null;
        }

        LocalKeyboard? found = LocalKeyboard.Find(NextControlsGeneration(), out string problem);
        if (found == null)
        {
            ModLog.Warn($"Your controls: your keyboard couldn't be read again ({problem}); your earlier controls stay.");
            return null;
        }

        _localKeyboard = found;
        return found.Controls;
    }

    /// <summary>
    /// The friend's device back to the session: the pad detached (its new maps the session's end gives back), the keyboard and
    /// mouse taken again. A pad not detached for any reason is taken, a lend that failed half-way too (it isn't read otherwise:
    /// the friend would play on with no input until they rejoin). One unplugged meanwhile is looked for as usual
    /// (WatchLocalPad), which says so when it's back.
    /// </summary>
    private void TakeDeviceBack()
    {
        if (_localPad is { IsDetached: false } pad)
        {
            if (!pad.TakeBack())
            {
                ModLog.Warn("Your controls: your pad's maps couldn't be read again for the session's end; it gives back the ones from before.");
            }

            _localPadWasConnected = pad.IsConnected;
            if (!_localPadWasConnected)
            {
                ModLog.Warn("Your controls: your pad was unplugged while your own menu had it. Your player stands still until it's back; reconnect it (or plug in another) to carry on.");
            }
        }

        if (_localKeyboard != null && _keyboardLent && !_localKeyboard.IsDetached)
        {
            _localKeyboard.Detach();
        }

        _keyboardLent = false;
        _lendPlayer = -1;
    }

    /// <summary>
    /// The menu couldn't open (<paramref name="problem"/>): it's closed if it's up, the device taken back, and the friend told
    /// the way that always works: leave, change the controls at the title, and join again while their player is kept.
    /// </summary>
    private void FallBack(long now, string problem)
    {
        OwnMenuKind kind = _ownMenu.Kind;
        Step("closing your own menu", () => _ownMenuView?.Close());
        Step("taking your device back", TakeDeviceBack);
        Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu("it couldn't open"));
        _friend?.SetPadConnected(_localPad?.IsConnected ?? true);
        _ownMenu.Abort(now, fellBack: true);
        if (_ownMenuView is { Menu: null })
        {
            // Asked for and not up yet: should it show after all, it's closed then (nothing would answer it).
            _lateMenu = _ownMenuView;
            _lateMenuUntil = now + LateMenuMicros;
        }

        _ownMenuView = null;
        string key = _solo ? SoloKey : LeaveWay;
        string again = _solo ? SoloKey : JoinWay;
        if (kind == OwnMenuKind.Remapper)
        {
            ModLog.Warn($"Your controls: couldn't open this game's remapper here ({problem}); path: leave, remap at the title, join again ({key}, Settings > Controls > Remap, then {again} " +
                $"within {ParkFor.TotalMinutes:0} minutes: your player is kept).");
            Notify(NoticeId.OwnMenuFailed);
        }
        else
        {
            ModLog.Warn($"Your settings: couldn't open this game's Settings menu here ({problem}); they're at your title screen once you leave ({key}).");
            Notify(NoticeId.OwnMenuFailed);
        }
    }

    /// <summary>A Settings menu that showed after its flow gave up is closed (nothing would answer it); looked for a few seconds.</summary>
    private void CloseLateMenu(long now)
    {
        if (_lateMenu == null)
        {
            return;
        }

        if (now > _lateMenuUntil)
        {
            _lateMenu = null;
        }
        else if (_lateMenu.Find() != null)
        {
            ModLog.Info("Your controls: the Settings menu showed after all, after its remapper had been given up; closed (its own back).");
            _lateMenu.Close();
            _lateMenu = null;
        }
    }

    /// <summary>
    /// The friend's side is ending (<paramref name="reason"/>) with their own menu open: it stays open on their own game, their
    /// device stays where it was lent (set as a give-back sets it), and nothing more is sent. Each step on its own: a flow left
    /// under way would keep the next session's device from being read.
    /// </summary>
    private void EndOwnMenu(string reason)
    {
        Step("ending My Settings", EndMySettings); // the shortcut on the host's pause menu (OnlineSession.MySettings.cs)
        EndOwnUnlocksLend(reason); // the new-unlocks screen's lend ends the same way (OnlineSession.OwnUnlocks.cs)
        if (_ownMenu.HoldsDevice)
        {
            if (_localPad is { LentTo: >= 0 } pad)
            {
                Step("leaving your pad where it was lent", () => _padBack = pad.EndLend());
            }

            Step("locking your own UI again", () => FriendUi.ResumeAfterOwnMenu($"the session ended: {reason}"));
            ModLog.Info($"Your controls: the session ended ({reason}) while your own menu was open; it stays open on your game, and your device is yours again.");
            _ownMenu.Abort(Now, fellBack: false);
        }

        if (_ownMenu.Opened + _ownMenu.FellBack > 0)
        {
            ModLog.Info($"Your controls: {_ownMenu.Opened} own menu(s) opened this session, {_ownMenu.FellBack} of them couldn't open.");
        }

        _ownMenu = new OwnMenuFlow();
        _ownMenuView = null;
        _lateMenu = null;
        _keyboardLent = false;
        _lendPlayer = -1;
        _padSwapJoystick = int.MinValue;
        _padSwapPending = false;
        EndSettingsSaves();
    }

    /// <summary>Where the friend's language can be changed: the game's own Language control works at the title screen only (<c>MainMenuOnlyDropdown</c>).</summary>
    private static string LanguageHint() => Campaign.Scenes.WorldMap.SceneName == "start_screen"
        ? "This is the title screen: the Language row on the System tab works."
        : "The game's Language row (System tab) works at the title screen only; this game isn't on it now.";

    /// <summary>The settings files go back under the save guard (the menu closed a moment ago, or the session ended).</summary>
    private void EndSettingsSaves()
    {
        SaveAllowance? saves = _settingsSaves;
        _settingsSaves = null;
        if (saves == null)
        {
            return;
        }

        saves.Dispose();
        if (SaveGuard.Refusing)
        {
            ModLog.Info("Save guard: your own settings files (config.es3, GameplaySettings.es3) are refused again while you follow.");
        }
    }

    /// <summary>
    /// Friend, every frame while no menu of theirs is open: a pad picked up after a swap that's another joystick than the one
    /// its controls were read from has its own maps (Rewired keeps them per pad), so they're read and sent when they differ.
    /// </summary>
    private void WatchPadSwap()
    {
        if (_localPad == null)
        {
            return;
        }

        if (_padSwapJoystick == int.MinValue)
        {
            _padSwapJoystick = _localPad.Joystick.id; // the one its controls were read from at the start (asked once)
            return;
        }

        // Looked at only after the pad came back (WatchLocalPad): no Rewired question every frame.
        if (!_padSwapPending)
        {
            return;
        }

        // Kept for when it's back in the session (a pad picked up during a rejoin): the rejoin's hello sends the controls it has.
        if (_friend is not { State: FriendSessionState.Joined })
        {
            return;
        }

        _padSwapPending = false;
        if (_localPad is not { IsDetached: true, IsConnected: true } pad || pad.Joystick.id == _padSwapJoystick)
        {
            return;
        }

        _padSwapJoystick = pad.Joystick.id;
        InputBindingSet read = PadRemapReader.Read(pad, NextControlsGeneration());
        if (read.SameControlsAs(_controls))
        {
            ModLog.Info($"Your controls: your pad is now \"{SafeText.Clean(pad.Joystick.name, 48)}\"; its maps give the same controls, so nothing was sent.");
            return;
        }

        _controls = read;
        _friend.SetBindings(read);
        NoteControlsChanged("another pad, with its own maps");
        ModLog.Info($"Your controls: your pad is now \"{SafeText.Clean(pad.Joystick.name, 48)}\", with its own maps (generation {read.Generation}); " +
            $"sent to {(_solo ? "the one-PC test's host side" : $"{HostTag()}")}.");
    }

    /// <summary>
    /// Host: a friend's pad (or keyboard) is away (<see cref="HostEventKind.PadDisconnected"/>), and why: unplugged, or their
    /// own remapper or settings open (their device lent back to their own game). Logged, and the host's screen says a menu.
    /// </summary>
    private void PadAway(ulong steamId, string tag, PadAwayReason why)
    {
        if (!PadAwayLine(steamId, tag))
        {
            return;
        }

        string display = _pads.TryGetValue(steamId, out RemotePad? remote) ? remote.DisplayName : DisplayNameOf(steamId);
        string kind = remote?.Pad.IsKeyboard == true ? "virtual keyboard" : "virtual pad";
        string meanwhile = ModSettings.AiStandIn
            ? "the AI plays their skater after 3 s until they're back"
            : $"their {kind} is neutral until they're back (the AI stand-in is off)";
        switch (why)
        {
            case PadAwayReason.ChangingControls:
                ModLog.Info($"Online: {tag} is changing their controls (their own game's remapper is open); {meanwhile}.");
                string changing = $"is changing their controls; {(ModSettings.AiStandIn ? "the AI plays their skater meanwhile" : "their player stands still meanwhile")}.";
                NotifyText(NoticeId.FriendInOwnMenu, $"{display} {changing}", $"{tag} {changing}");
                break;
            case PadAwayReason.InSettings when _relay.StreamTo(steamId) is { LoadFailure.Length: > 0 }:
                // The same reason on the wire: a friend whose game couldn't load the match is asked what to do on their own game's prompt (OnlineSession.FollowQuestion.cs).
                ModLog.Info($"Online: {tag}'s device is lent back to their own game (their game couldn't load this match: it asks them whether to try again or leave, " +
                    $"unless they opened their own settings); {(ModSettings.AiStandIn ? "the AI plays their skater meanwhile" : $"their {kind} is neutral meanwhile (the AI stand-in is off)")}.");
                break;
            case PadAwayReason.InSettings:
                ModLog.Info($"Online: {tag} has their own game's settings open; {meanwhile}.");
                string inSettings = $"is in their own settings; {(ModSettings.AiStandIn ? "the AI plays their skater meanwhile" : "their player stands still meanwhile")}.";
                NotifyText(NoticeId.FriendInOwnMenu, $"{display} {inSettings}", $"{tag} {inSettings}");
                break;
            default:
                ModLog.Warn($"Online: {tag} unplugged their pad; their {kind} is neutral until it's back.");
                break;
        }
    }

    /// <summary>
    /// Host: whether a friend's device going away or coming back gets its line and notice. Bounded per friend, then once a
    /// minute with the count: messages 17 and 18 cost a friend nothing to repeat (and a reason change raises one too), and each
    /// line is a flushed write on this PC's main thread. What they play with follows every message whatever this says.
    /// </summary>
    private bool PadAwayLine(ulong steamId, string tag)
    {
        switch (_padAwayLines.Next(steamId.ToString(CultureInfo.InvariantCulture), Environment.TickCount64, out int counted))
        {
            case RepeatDecision.Write:
                return true;
            case RepeatDecision.Summarize:
                ModLog.Info($"Online: {tag}'s device went away or came back {counted} more time(s); only counted now.");
                return false;
            default:
                return false;
        }
    }

    /// <summary>The friend's status box line while their own menu has their device ("" otherwise).</summary>
    private string OwnMenuStatusLine()
    {
        if (!_ownMenu.HoldsDevice)
        {
            return string.Empty;
        }

        string menu = _remapperOpen || _ownMenu.Kind == OwnMenuKind.Remapper ? "remapper" : "settings";
        string host = _solo ? "the host side" : $"{_hostName}'s game";
        return $"   your own {menu} {(_ownMenu.State == OwnMenuState.Closing ? "is closing" : "is open")}: {host} knows you're away, and its AI plays your skater if a match is on{(ModSettings.DevTools ? " (F1 > Controller > Close it)" : string.Empty)}";
    }

    /// <summary>How a friend's away pad is named in the host's status box and panel ("pad unplugged", "changing controls").</summary>
    private static string AwayLabel(PadAwayReason why) => why switch
    {
        PadAwayReason.ChangingControls => "changing controls",
        PadAwayReason.InSettings => "in their settings",
        _ => "pad unplugged",
    };

    /// <summary>Microseconds as seconds for the log ("0.6 s", "42 s").</summary>
    private static string Seconds(long micros) =>
        micros < 10_000_000 ? string.Create(CultureInfo.InvariantCulture, $"{micros / 1_000_000.0:0.0} s") : string.Create(CultureInfo.InvariantCulture, $"{micros / 1_000_000} s");
}
#endif
