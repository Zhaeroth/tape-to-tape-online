#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Online.Panel;
using UnityEngine;

namespace T2TOnline.Plugin.Runtime;

/// <summary>
/// What runs instead of <see cref="ModRuntime"/> while T2T Online is off: when online mode was off from start-up (an
/// untested game build after a Steam update, a failed start-up check, part of the mod not installed) or the runtime
/// stopped after an error. Without it nothing would say why the title has no Online row, which reads as a broken install. It
/// shows, in the corner where the status box goes, why online is off and what to do (for 30 s once the game is up, again for
/// 30 s when the title screen comes up, again whenever a developer's online key is pressed, and for good after an error, since
/// the game no longer follows the host: then the stopped runtime's status box says so, and the notice only stands in if that's
/// gone too), and where the mod's functions are meanwhile (Settings > Online on a tested build; elsewhere a held Insert makes a
/// problem report). With the developer tools, F1 still opens the mod's panel (co-op slots, backups, settings, help and the logs
/// folder; the players' build has none, D142); on a tested build it also says what the
/// saves' start-up steps did, and sends restored files to Steam Cloud at the title (<see cref="ShellSaves"/>; on any other
/// build the corner notice says them, D12). Keys act only while the game window
/// is focused. It touches the game only through Rewired's keyboard and Unity's GUI, and patches nothing unless the build
/// is a tested one. Every step is guarded: a failure turns it off for the session, logged.
/// </summary>
internal sealed class OfflineShell
{
    /// <summary>How long the notice stays up when it isn't for good.</summary>
    private const long NoticeMs = 30_000;

    /// <summary>The developer's keys that would start or show something online.</summary>
    private static readonly KeyCode[] OnlineKeys = { KeyCode.F7, KeyCode.F9, KeyCode.F5, KeyCode.F6 };

    private readonly ModPanel? _panel; // the developer's only (D142)
    private readonly bool _devTools;
    private readonly bool _nativeScreens;
    private readonly string _version;
    private readonly bool _afterError;
    private readonly string? _extraNotice;
    private readonly InstallPrompt? _installPrompt;
    private readonly ShellSaves? _saves;
    private readonly bool _tours;
    private bool _toursFailed;
    private IReadOnlyList<string> _lines = Array.Empty<string>();
    private long _shownUntil = long.MinValue;
    private long _nextTitleLook;
    private bool _ready;
    private bool _titleSeen;
    private bool _failed;

    // The backups' notice (English) is in the corner notice: a build whose screens aren't used, or a notice they couldn't carry.
    private bool _savesInCorner;

    /// <summary>Creates the shell, and with the developer tools its panel.</summary>
    /// <param name="version">The mod's release, for the notice and the panel.</param>
    /// <param name="devTools">True when the developer keys are on: the panel (F1) and the online keys exist only then.</param>
    /// <param name="harmony">The mod's Harmony instance, or null to patch nothing (the game build isn't a tested one).</param>
    /// <param name="restoreOff">Why backups can't be restored this session, or null when they can (<see cref="ModPanel"/>).</param>
    /// <param name="afterError">True when the runtime stopped after an error (the notice then stays up).</param>
    /// <param name="extraNotice">A line more for the notice (a MelonLoader that isn't the tested one), or null.</param>
    /// <param name="screensUsable">
    /// True on a game build the mod was tested on and can patch (then <paramref name="harmony"/> is given too): the game's
    /// own prompt and toast say the saves' start-up notices, and the restored files go to Steam Cloud at the title. On any
    /// other build the mod stays off the game's screens (D12) and the notices go to the corner.
    /// </param>
    /// <param name="tours">
    /// True with the developer tools on, on a game build the mod patches (its achievements hold in): the check tours run here too, so a
    /// tour walks the native screens with online off, or carries on after the runtime it began under stopped on an error.
    /// </param>
    public OfflineShell(string version, bool devTools, HarmonyLib.Harmony? harmony, string? restoreOff, bool afterError, string? extraNotice, bool screensUsable = false, bool tours = false)
    {
        _version = version;
        _afterError = afterError;
        _extraNotice = extraNotice;
        _tours = tours;
        _devTools = devTools;
        _nativeScreens = screensUsable;
        if (devTools)
        {
            _panel = new ModPanel(new OfflinePanelSession(afterError), version, devTools, harmony, restoreOff);
        }

        // After an error the runtime configured the tours (a tour it began carries on from here); from start-up nothing has, and there is no session.
        if (tours && !afterError)
        {
            NativeLobby.LobbyUiCheck.Configure(devTools, () => false);
        }

        // Old files of T2T Online keep online off (D125): the game's own prompt names them, on a tested game build only (its
        // members are checked there; on another build the corner notice and the panel say it).
        if (harmony != null && Startup.InstallCheck.Verdict is { Outcome: Startup.InstallOutcome.Leftovers } verdict)
        {
            _installPrompt = new InstallPrompt(verdict);
        }

        // What the saves' start-up steps found (a restore put right, its notices, restored files for Steam Cloud): the game's own
        // prompt or toast on a tested game build, where the restored files also go to Steam Cloud at the title. On any other
        // build the mod stays off the game's screens (D12), so the corner notice says it (Steam Cloud's copies that may come
        // back over a restore above all).
        if (screensUsable)
        {
            _saves = new ShellSaves(pushes: true);
        }
        else if (Campaign.SaveBackups.Notice.Length > 0)
        {
            _savesInCorner = true;
            ModLog.Info("Backups: what the start-up found is in the corner notice (T2T Online stays off the game's screens on a game version it wasn't tested on).");
        }

        if (afterError)
        {
            Show();
        }
    }

    /// <summary>
    /// Call once per frame: with the developer tools, F1 opens the panel and an online key (F7, F9, Ctrl+F5, Ctrl+F6; F5 or F6 alone too) shows
    /// the notice again, saying online is off. The players' build reads no key here.
    /// </summary>
    public void Update()
    {
        if (_failed)
        {
            return;
        }

        try
        {
            if (!_ready)
            {
                if (!ReInput.isReady)
                {
                    return;
                }

                _ready = true;
                Show();
                _titleSeen = OnTitle();
                ModLog.Info($"T2T Online is off this session ({Reason()}); its notice is on screen" + (_devTools ? ", and F1 still opens the mod's panel (backups, settings, help, logs)."
                    : _nativeScreens ? $"; {PlayersBuild.SettingsOnline} still works (Problem Report, Save Backups)." : "; a held Insert makes a problem report."));
            }
            else if (!_titleSeen && !_afterError && Environment.TickCount64 >= _nextTitleLook)
            {
                // The missing Online row is what a player notices, at the title, and on a slow PC the title can come after the
                // first 30 s: the notice goes up again when it does (once; an online key puts it back later).
                _nextTitleLook = Environment.TickCount64 + 500;
                if (OnTitle())
                {
                    _titleSeen = true;
                    Show();
                    ModLog.Info("T2T Online is off this session: its notice is on screen again, now the title screen is up.");
                }
            }

            _installPrompt?.Update();
            TickTours();
            if (_saves != null)
            {
                _saves.Update();
                if (_saves.Fallback && !_savesInCorner)
                {
                    _savesInCorner = true;
                    Show();
                }
            }

            // The developer's keys only (the players' build reads none here). As in the runtime, no key acts while the game window isn't focused.
            if (_panel == null)
            {
                return;
            }

            Keyboard keyboard = ReInput.controllers.Keyboard;
            bool focused = Application.isFocused;
            _panel.Update(focused && keyboard.GetKeyDown(KeyCode.F1));
            foreach (KeyCode key in OnlineKeys)
            {
                if (focused && keyboard.GetKeyDown(key))
                {
                    ModLog.Info($"Online ({key}): online is off this session ({Reason()}); nothing was done. The notice is back on screen.");
                    Show();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Call from OnGUI: the notice (while it's up, and <paramref name="notice"/>: nothing else draws in its corner, such as
    /// the stopped runtime's status box) and the panel over it.
    /// </summary>
    public void OnGUI(bool notice = true)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            if (notice && Environment.TickCount64 < _shownUntil)
            {
                StatusOverlay.Draw(_lines);
            }

            _panel?.OnGUI();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>The game is closing: the panel gives the keyboard and mouse back. Never throws.</summary>
    public void Shutdown() => _panel?.Shutdown();

    private static string Reason() => T2TOnlinePlugin.OfflineReason.Length > 0 ? T2TOnlinePlugin.OfflineReason : "see the log";

    /// <summary>The notice's lines as last put up (empty before): the check tours read them.</summary>
    public static IReadOnlyList<string> NoticeLines { get; private set; } = Array.Empty<string>();

    /// <summary>True while the title screen is the active scene.</summary>
    private static bool OnTitle() => Campaign.Scenes.WorldMap.SceneName == Campaign.Scenes.WorldMap.TitleScene;

    /// <summary>Makes the notice's lines (a new list, so the status box measures them again) and puts it up.</summary>
    private void Show()
    {
        var lines = new List<string>
        {
            $"T2T Online {ReleaseVersion.ForScreen(_version)} · online is off: {Reason()}",
        };
        if (T2TOnlinePlugin.OfflineAdvice.Length > 0)
        {
            lines.Add($"   {T2TOnlinePlugin.OfflineAdvice}");
        }

        if (_extraNotice != null)
        {
            lines.Add($"   {_extraNotice}");
        }

        if (_savesInCorner && Campaign.SaveBackups.Notice.Length > 0)
        {
            lines.Add($"   {Campaign.SaveBackups.Notice}");
        }

        // Where the mod's functions are meanwhile: the game's own Settings > Online where its screens are used, else (or with its Online tab off for
        // this game) the held Insert (D142).
        lines.Add($"   {PlayersBuild.OfflineNoticeLine(_nativeScreens && NativeUi.SettingsTab.OffReason == null, _devTools)}");
        _lines = lines;
        NoticeLines = lines; // the check tours read it (the offline walk's off_notice)
        _shownUntil = _afterError ? long.MaxValue : Environment.TickCount64 + NoticeMs;
    }

    /// <summary>The check tours' frame (developer tools only), guarded on its own: a tour's failure never stops the shell.</summary>
    private void TickTours()
    {
        if (!_tours || _toursFailed)
        {
            return;
        }

        try
        {
            NativeLobby.LobbyUiCheck.Update();
        }
        catch (Exception ex)
        {
            _toursFailed = true;
            ModLog.Warn($"Lobby check: the tours' frame failed in the offline shell ({ModLog.SafeErrorText(ex)}); no tour runs this session.");
        }
    }

    private void Fail(Exception ex)
    {
        _failed = true;
        _panel?.Shutdown();
        ModLog.Error("T2T Online's offline notice failed; it's off for this session", ex);
    }
}
#endif
