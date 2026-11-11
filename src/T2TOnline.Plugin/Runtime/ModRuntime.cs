#if HAS_INTEROP
using System;
using System.Globalization;
using System.Linq;
using Il2CppFighting;
using Il2CppInterop.Runtime;
using Il2CppRewired;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Online.Panel;
using T2TOnline.Plugin.Replay;
using T2TOnline.Plugin.Spikes;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Runtime;

/// <summary>
/// Runs the mod each frame: online sessions over Steam, the match setup listener, and the Rewired report and virtual-pad definition once
/// Rewired is up. A player reaches every function on the game's own screens (the title's Online menu, Settings > Online); no key but
/// Insert (BugReports) does anything in the players' build (D142). With the DevTools setting, the developer's keys: F1 the mod's panel
/// (<see cref="ModPanel"/>), F7 joins a friend or hosts, Shift+F7 hosts, Ctrl+F6 the one-PC test while hosting, Ctrl+F5 a simulated drop
/// as a friend, F9 the net overlay (with the status box), F8 for the controller loopback (M0.2), Ctrl+F9 for a snapshot capture and F11 to play it back onto the frozen match (M0.3), F10 to
/// ask the game for a brawl challenge, Ctrl+F10 to open the next kind of event on the world map (events come up at random;
/// <see cref="Campaign.Menus.EventDevKey"/>), Ctrl+F11 for the run-start carrier's self-test
/// (<see cref="Campaign.Menus.RunStartSelfTest"/>), Ctrl+F12 for the native lobby's check tour (<see cref="NativeLobby.LobbyUiCheck"/>), F3/F4 to record a match and play it back in client mode (M2), F2 to load a
/// recording's match fresh and play it onto it (M3). No key acts while the game window isn't focused.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives for the whole game session; Shutdown closes the recorder.")]
internal sealed partial class ModRuntime
{
    /// <summary>Failures in a row after which a tool's step is off for the session (a step failing every frame would only repeat itself).</summary>
    private const int MaxToolFailures = 10;

    private static readonly System.Collections.Generic.Dictionary<string, int> ToolFailures = new(StringComparer.Ordinal);
    private static Il2CppSystem.Action? _brawlOpened; // F10's offer callback, kept alive (OfferBrawl)

    private readonly LoopbackSpike _loopback;
    private readonly FrozenReplay _frozenReplay = new();
    private readonly OnlineSession _online;
    private readonly ModPanel? _panel; // the developer's only: none at all in the players' build (nor its input patches)
    private readonly MatchStreaming _streaming;
    private readonly string _modVersion;
    private readonly ulong _gameBuild;
    private readonly string _streamDirectory;
    private readonly bool _devTools;
    private readonly string? _startupNotice;
    private readonly System.Collections.Generic.HashSet<string> _testKeysHinted = new(StringComparer.Ordinal);

    // Made once: Update and LateUpdate hand them to Safely every frame, and an instance's method group is a new delegate each time.
    private readonly Action _updateRehearsal;
    private readonly Action _updateLoopback;
    private readonly Action _lateUpdateFrozenReplay;
    private readonly Action _lateUpdateStreaming;
    private bool _rewiredReady;
    private bool _devKeyHinted;
    private bool _stopped;
    private Rehearsal? _rehearsal;

    /// <summary>Creates the runtime.</summary>
    /// <param name="loopbackDelayMs">The loopback's simulated link delay.</param>
    /// <param name="devTools">True to make the developer's test keys live (the DevTools setting).</param>
    /// <param name="modVersion">The mod's release ("0.9.2-beta"), for the Steam lobby, the handshake and the screens.</param>
    /// <param name="gameBuild">The first 8 bytes of GameAssembly.dll's SHA-256, which friends must match.</param>
    /// <param name="gameRoot">The game folder.</param>
    /// <param name="harmony">The mod's Harmony instance, for the stream recorder's direct-sound patch.</param>
    /// <param name="startupNotice">A notice for the status box once the game is up (a MelonLoader that isn't the tested one), or null.</param>
    public ModRuntime(int loopbackDelayMs, bool devTools, string modVersion, ulong gameBuild, string gameRoot, HarmonyLib.Harmony harmony, string? startupNotice = null)
    {
        _streaming = new MatchStreaming(harmony);
        _loopback = new LoopbackSpike(loopbackDelayMs);
        _updateRehearsal = UpdateRehearsal;
        _updateLoopback = _loopback.Update;
        _lateUpdateFrozenReplay = _frozenReplay.LateUpdate;
        _lateUpdateStreaming = _streaming.LateUpdate;
        _devTools = devTools;
        _modVersion = modVersion;
        _gameBuild = gameBuild;
        _startupNotice = startupNotice;
        _streamDirectory = DevOutput.StreamsFolder;
        var matches = new MatchRelay(_streaming, _streamDirectory, modVersion, gameBuild);
        OnlineSession? online = null;
        var context = new Campaign.CampaignContext(matches, System.IO.Path.Combine(gameRoot, "UserData", "T2TOnline"), (id, args) => online?.Notify(id, args),
            (who, id, name) => online?.NotifyAbout(who, id, name));
        online = new OnlineSession(modVersion, gameBuild, gameRoot, matches, Campaign.CampaignModules.Create(context));
        _online = online;

        // Every way into a session (F7, the panel, the title's Online row, an invite, Join Game) gives the F8 loopback's pad up first.
        online.BeforeSessionStarts = () => Safely("Loopback (F8)", () => _loopback.Stop("an online session is starting"));

        // The developer's panel (F1): its Online tab finds friends' games to join one by one and hosts apart, as F7 and Shift+F7 do (the
        // F8 loopback gives the pad up first). The players' build has none: every player function is on the game's own screens (D142).
        if (devTools)
        {
            _panel = new ModPanel(new OnlinePanelSession(online, ToggleSession, HostSession, JoinSession), modVersion, devTools, harmony);
        }

        // The native lobby: its one patch (a failure turns off only the lobby), and its check tour.
        NativeLobby.LobbyKit.Install(harmony);
        NativeLobby.LobbyUiCheck.Session = online;
        NativeUi.NativeUiLayer.Session = online; // the Settings tab's rows read it there while online is on
        NativeLobby.LobbyUiCheck.StreamKeys = this;

        // The native screens over the session's actions. A session behaves the same with them off, with no screen up.
        online.Screens = NativeLobby.LobbyScreens.Instance;
        NativeLobby.LobbyScreens.Instance.Attach(online.Actions);
        NativeLobby.LobbyScreens.IdentitySource = online.BrowserIdentity;
        NativeLobby.LobbyUiCheck.Configure(devTools, () => _online.UsesLocalPad || _online.HostStreaming || _online.FriendBusy ||
            _online.Summarize().Phase is not (SessionPhase.Idle or SessionPhase.Unavailable or SessionPhase.WaitingForSteam or SessionPhase.NoSteam));
    }

    /// <summary>Call once per frame.</summary>
    public void Update()
    {
        FrameClock.Latch();
        if (!_rewiredReady && ReInput.isReady)
        {
            // Before any online session can take this PC's keyboard: one that a closed or crashed game never gave back.
            Safely("Keyboard check", LocalKeyboard.GiveBackAfterUnfinishedSession, always: true);
        }

        _online.Update();
        if (!_rewiredReady)
        {
            if (!ReInput.isReady)
            {
                return;
            }

            _rewiredReady = true;
            try
            {
                RewiredReport.Log();
            }
            catch (Exception ex)
            {
                // The report only explains things; it must not stop the tests.
                ModLog.Error("Rewired report failed", ex);
            }

            if (!VirtualPadDefinition.Ensure(out string problem))
            {
                ModLog.Warn($"Virtual pad: not defined ({problem}); friends' pads on this PC (and F8) won't work.");
            }

            if (!VirtualKeyboardDefinition.Ensure(out string keyboardProblem))
            {
                ModLog.Warn($"Virtual keyboard: not defined ({keyboardProblem}); friends playing with the keyboard can't join this PC's sessions.");
            }

            ModLog.Info(_devTools
                ? $"Keys ready: F1 opens the mod's panel, F7 joins a friend's session or hosts one, Shift+F7 hosts even when friends have games open, " +
                  $"{OnlineSession.SoloKey} (while hosting) runs the one-PC online test, {OnlineSession.DropKey} (as a friend) simulates a 15 s network drop, F9 shows the net overlay (a developer's tool, with the status box: {(Startup.ModSettings.ShowStatusBox ? "on" : "off, ShowStatusBox = true turns it on")})."
                : $"Keys ready: Insert marks a moment (a MARK line); everything else is on the game's own screens ({Core.OnlineSettings.PlayersBuild.SettingsOnline}, the title's Online menu).");
            if (_startupNotice != null)
            {
                _online.Notify(NoticeId.LoaderNotice, _startupNotice);
            }

            if (_devTools)
            {
                // The players' build says nothing more here: its build line (PlayersBuild.ModeLine) said it at start-up, naming no key.
                ModLog.Info("Developer keys on (DevTools = true): F8 toggles the controller loopback, Ctrl+F9 saves 10 s of replay snapshots, F10 asks for a brawl challenge, " +
                    "Ctrl+F10 (on the world map, not as a friend) opens the next kind of event, " +
                    "Ctrl+F11 (at the title after picking a squad, or on the world map; not as a friend) runs the run-start carrier's self-test, " +
                    "Ctrl+F12 (at the title, out of a session) runs the native lobby's check tour, " +
                    "F11 (during play) freezes the match and plays the last Ctrl+F9 capture back onto it, F3 records the match to a stream file, F4 plays it back here in client mode, " +
                    "F2 loads the newest recording's match fresh (as a friend's game would) and plays the recording onto it.");
            }
        }

        // The game is up: listen for loaded matches (online play and F2 need it).
        Safely("Match setup", MatchSetup.Update);
        Safely("Fight grace", FightGrace.Update);
        Safely("Native lobby", NativeLobby.LobbyKit.Update);

        // Each tool is guarded on its own, so one tool's failure (F10 outside a match, say) never stops the others, and
        // above all never leaves an online session running unmanaged. The developer's keys work only with DevTools on.
        // No key acts while the game window isn't focused: they're meant for this game, not typed into another window
        // (online, Rewired's keyboard is off then anyway, Online/FocusGuard).
        Keyboard keyboard = ReInput.controllers.Keyboard;
        bool focused = Application.isFocused;

        // The modifiers only for the developer's keys: the players' build reads none of them on a frame with no key down.
        bool control = _devTools && focused && Control(keyboard);
        bool shift = _devTools && focused && (keyboard.GetKey(KeyCode.LeftShift) || keyboard.GetKey(KeyCode.RightShift));

        // Static, its reads passed in: a local function that captured them would make this whole frame's scope a heap closure.
        static bool Down(Keyboard keyboard, bool focused, KeyCode key) => focused && keyboard.GetKeyDown(key);

        // F1: the developer's panel (it guards itself). While it's open Rewired's keyboard is off, so the keys below wait too.
        _panel?.Update(Down(keyboard, focused, KeyCode.F1));
        if (!_devTools && focused)
        {
            HintDevKeys(keyboard);
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F8))
        {
            Safely("Loopback (F8)", () =>
            {
                if (_online.UsesLocalPad)
                {
                    ModLog.Warn($"Loopback (F8): your pad is in an online session; leave it first ({OnlineSession.SoloKey} or F7).");
                }
                else
                {
                    _loopback.Toggle();
                }
            });
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F9))
        {
            if (control)
            {
                if (_devTools)
                {
                    Safely("Snapshot capture (Ctrl+F9)", () => SnapshotProbe.RequestCapture("Ctrl+F9 pressed"));
                }
            }
            else
            {
                _online.ToggleNetOverlay();
            }
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F10))
        {
            if (control)
            {
                Safely("Event test (Ctrl+F10)", Campaign.Menus.EventDevKey.Open);
            }
            else
            {
                Safely("Brawl test (F10)", () => OfferBrawl());
            }
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F3))
        {
            PressF3();
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F2))
        {
            PressF2();
        }

        Safely("Fresh-match rehearsal (F2)", _updateRehearsal);

        // F4 and F11 both take over the match (physics mode, time scale): never both at once (PressF4).
        if (_devTools && Down(keyboard, focused, KeyCode.F4))
        {
            PressF4();
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F11) && control)
        {
            Safely("Run-start self-test (Ctrl+F11)", Campaign.Menus.RunStartSelfTest.Start);
        }
        else if (_devTools && Down(keyboard, focused, KeyCode.F11))
        {
            if (_online.HostStreaming || _online.FriendBusy)
            {
                ModLog.Warn("Frozen replay (F11): not during an online match.");
            }
            else if (_streaming.Playing)
            {
                ModLog.Warn("Frozen replay (F11): a stream is playing; stop it first (F4).");
            }
            else
            {
                Safely("Frozen replay (F11)", () => _frozenReplay.Toggle(SnapshotProbe.LastCapture, SnapshotProbe.LastCaptureScene, SnapshotProbe.LastScene));
            }
        }

        if (_devTools && control && Down(keyboard, focused, KeyCode.F12))
        {
            Safely("Lobby check (Ctrl+F12)", NativeLobby.LobbyUiCheck.StartByKey);
        }

        Safely("Lobby check (Ctrl+F12)", NativeLobby.LobbyUiCheck.Update);

        // F7, not F12: Steam takes F12 for screenshots. Shift+F7 hosts whatever friends have open (F7 joins one if any). The developer's
        // only: a player hosts and joins from the title's Online menu, and leaves from Settings > Online.
        if (_devTools && Down(keyboard, focused, KeyCode.F7))
        {
            if (shift)
            {
                HostSession();
            }
            else
            {
                ToggleSession();
            }
        }

        // The test keys need Ctrl: a stray F5 (the browser's refresh) would hand a friend's skater to the AI for 15 s, and a stray F6
        // would start the one-PC test. Alone they do nothing, said once in the log. The developer's only.
        if (_devTools && Down(keyboard, focused, KeyCode.F6))
        {
            if (!control)
            {
                HintTestKey("F6", OnlineSession.SoloKey, "the one-PC test");
            }
            else if (_loopback.Active)
            {
                ModLog.Warn($"Online one-PC test ({OnlineSession.SoloKey}): the F8 loopback has your pad; turn it off first (F8).");
            }
            else
            {
                _online.ToggleSolo();
            }
        }

        if (_devTools && Down(keyboard, focused, KeyCode.F5))
        {
            if (control)
            {
                _online.SimulateDrop();
            }
            else
            {
                HintTestKey("F5", OnlineSession.DropKey, "the simulated network drop");
            }
        }

        Safely("Loopback (F8)", _updateLoopback);
        Safely("Snapshot probe", SnapshotProbe.Update);
        Safely("Run-start self-test (Ctrl+F11)", Campaign.Menus.RunStartSelfTest.Update);
    }

    /// <summary>F7, and the panel's Host or join and Leave: join or host, or leave. The F8 loopback gives the pad up first.</summary>
    private void ToggleSession()
    {
        Safely("Loopback (F8)", () => _loopback.Stop("an online session is starting"));
        _online.Toggle();
    }

    /// <summary>Shift+F7: host now, whatever friends have open. The F8 loopback gives the pad up first.</summary>
    private void HostSession()
    {
        Safely("Loopback (F8)", () => _loopback.Stop("an online session is starting"));
        _online.HostGame();
    }

    /// <summary>The panel's Join: join the friend's game in <paramref name="lobbyId"/>. The F8 loopback gives the pad up first.</summary>
    private void JoinSession(ulong lobbyId)
    {
        Safely("Loopback (F8)", () => _loopback.Stop("an online session is starting"));
        _online.JoinGame(lobbyId);
    }

    /// <summary>F5 or F6 pressed without Ctrl: nothing is done, and the log says once (per key) what the test key is now.</summary>
    private void HintTestKey(string pressed, string testKey, string what)
    {
        if (_testKeysHinted.Add(pressed))
        {
            ModLog.Info($"{pressed} alone does nothing: {what} is {testKey} (so a stray {pressed} can't start it).");
        }
    }

    /// <summary>
    /// With DevTools off (the players' build): the first developer key pressed says once, in the log, that it's off. One read a frame
    /// (any key down) until a key is down; the keys themselves, and Ctrl, only then.
    /// </summary>
    private void HintDevKeys(Keyboard keyboard)
    {
        if (_devKeyHinted || !keyboard.GetAnyButtonDown())
        {
            return;
        }

        bool pressed = keyboard.GetKeyDown(KeyCode.F1) || keyboard.GetKeyDown(KeyCode.F7) || keyboard.GetKeyDown(KeyCode.F9) || keyboard.GetKeyDown(KeyCode.F2)
            || keyboard.GetKeyDown(KeyCode.F3) || keyboard.GetKeyDown(KeyCode.F4) || keyboard.GetKeyDown(KeyCode.F8) || keyboard.GetKeyDown(KeyCode.F10)
            || keyboard.GetKeyDown(KeyCode.F11) || (Control(keyboard) && (keyboard.GetKeyDown(KeyCode.F5) || keyboard.GetKeyDown(KeyCode.F6) || keyboard.GetKeyDown(KeyCode.F12)));
        if (pressed)
        {
            _devKeyHinted = true;
            ModLog.Info("A developer key was pressed; this is the players' build, where they're off (DevTools = true in the [T2TOnline] settings turns them on). Nothing was done.");
        }
    }

    /// <summary>Either Ctrl key held.</summary>
    private static bool Control(Keyboard keyboard) => keyboard.GetKey(KeyCode.LeftControl) || keyboard.GetKey(KeyCode.RightControl);

    /// <summary>
    /// Runs one tool's step; a failure is logged and stops only that step. After <see cref="MaxToolFailures"/> failures in a
    /// row the tool's steps are off for the session (said once), so a step failing every frame doesn't keep repeating;
    /// <paramref name="always"/> runs it anyway (shutting down). A step that fails only now and then, or only at a look it makes
    /// every so many frames, stays on, its log bounded by <see cref="ModLog"/>'s repeat limit: switching a tool off takes all its
    /// work with it, and a tool can hold work that must go on (under "Native lobby" the lobby's screens tick after the title's
    /// Online row, which looks twice a second; under "Stream (F3/F4)", the online match's stream).
    /// </summary>
    private static void Safely(string tool, Action step, bool always = false)
    {
        bool known = ToolFailures.TryGetValue(tool, out int failures);
        if (failures >= MaxToolFailures && !always)
        {
            return;
        }

        try
        {
            step();
            if (known && failures > 0 && failures < MaxToolFailures)
            {
                ToolFailures[tool] = 0;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"{tool} failed", ex);
            if (failures < MaxToolFailures)
            {
                ToolFailures[tool] = ++failures;
                if (failures == MaxToolFailures)
                {
                    ModLog.Warn($"{tool}: off for the rest of this session after {MaxToolFailures} failures in a row (see above).");
                }
            }
        }
    }

    /// <summary>
    /// Brawl offers come only from campaign events (elite and boss matches, certain teams, knockouts, injuries), so a
    /// Play Now match never gets one. This asks the game's own offer code for one; its own gate still applies
    /// (live play only, not near the end of a period, one brawl per game). F10 calls it, and so does the campaign check tour's
    /// <c>brawl=on</c> (a human's key press would stop a tour). True when the game is opening an offer. Its callback is made
    /// once and kept for the game's life: the game holds it until the offer opens.
    /// </summary>
    internal static bool OfferBrawl()
    {
        _brawlOpened ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            new Action(() => ModLog.Info("Brawl test: the brawl challenge offer is on screen.")));
        bool offered = BrawlChallengeSystem.TryOfferChallenge(0f, _brawlOpened);
        ModLog.Info(offered
            ? "Brawl test (F10): the game is opening a brawl challenge offer. Accept it in game."
            : "Brawl test (F10): the game declined (an offer is already open, a brawl already happened this game, the match isn't in live play, or the period is nearly over).");
        return offered;
    }

    /// <summary>Call once per frame after the game's updates.</summary>
    public void LateUpdate()
    {
        Safely("Frozen replay (F11)", _lateUpdateFrozenReplay);
        Safely("Stream (F3/F4)", _lateUpdateStreaming);
    }

    /// <summary>F3: starts recording the live match to a stream file, or stops.</summary>
    private void ToggleRecording()
    {
        // During an online match the recorder is the friends' stream (and records it anyway): hands off.
        if (_online.HostStreaming || _online.FriendBusy || _streaming.Streaming)
        {
            ModLog.Warn("Stream recorder (F3): not during an online match (the host's match is recorded with its stream already).");
            return;
        }

        if (_streaming.Recording)
        {
            _streaming.StopRecording("F3 pressed");
            return;
        }

        if (_streaming.Playing)
        {
            ModLog.Warn("Stream recorder (F3): a stream is playing; stop it first (F4).");
            return;
        }

        if (SnapshotProbe.LastScene == null)
        {
            ModLog.Warn("Stream recorder (F3): no match is being played yet. Start a match, then press F3.");
            return;
        }

        var header = new T2TOnline.Core.Stream.MatchStreamHeader(T2TOnline.Core.Protocol.ProtocolInfo.Version, _modVersion, _gameBuild, DateTime.UtcNow.Ticks, "F3 recording");
        // With the match setup, so F2 can load this match fresh later.
        if (!MatchSetup.TryCapture(out System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, T2TOnline.Core.Serialization.NetValue>> setup, out string setupProblem))
        {
            ModLog.Warn($"Stream recorder (F3): the match setup couldn't be read ({setupProblem}); F2 won't be able to load this recording's match.");
            setup = new();
        }

        if (_streaming.StartRecording(() => SnapshotProbe.LastScene, StreamDirectory, header, network: null, setup, out string problem))
        {
            ModLog.Info($"Stream recorder (F3): recording this match to {DevOutput.Label(_streaming.FilePath)}. F3 stops; F4 then plays it back here in client mode.");
        }
        else
        {
            ModLog.Warn($"Stream recorder (F3): could not start: {problem}.");
        }
    }

    /// <summary>
    /// F4: plays the last recording back in client mode, or stops: onto this same match when it was recorded here (by
    /// replay id), else onto this freshly loaded copy of it (by key, components taking the host's ids).
    /// </summary>
    private void TogglePlayback()
    {
        if (_online.HostStreaming || _online.FriendBusy || _streaming.Streaming)
        {
            ModLog.Warn("Stream player (F4): not during an online match.");
            return;
        }

        if (_streaming.Playing)
        {
            _streaming.StopPlaying("F4 pressed");
            return;
        }

        if (_streaming.Recording)
        {
            _streaming.StopRecording("F4 pressed: playing it back");
        }

        string? path = _streaming.FilePath;
        Il2CppUltimateReplay.ReplayScene? scene = SnapshotProbe.LastScene;
        if (path == null || scene == null)
        {
            ModLog.Warn("Stream player (F4): nothing recorded in this match yet. Press F3 during play, play a while, then F4.");
            return;
        }

        if (!MatchRecording.TryLoad(path, out MatchRecording? stream, out string problem))
        {
            ModLog.Warn($"Stream player (F4): could not read {DevOutput.Label(path)}: {problem}.");
            return;
        }

        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Stream player (F4): loaded {stream!.Seconds:0.0} s: {stream.Snapshots.Count} snapshots, {stream.Objects.Count} objects, {stream.Events.Count} events in the table, ") +
            $"{stream.Messages.Count} other messages{(stream.Malformed + stream.MissingBase > 0 ? $"; {stream.Malformed} malformed and {stream.MissingBase} without their base skipped" : string.Empty)}" +
            $"{(stream.Truncated ? "; the file was cut off" : string.Empty)}.");
        var link = new SimulatedLink(
            Math.Clamp(Startup.ModSettings.PlaybackLinkDelayMs, 0, 500),
            Math.Clamp(Startup.ModSettings.PlaybackLinkJitterMs, 0, 200),
            Math.Clamp(Startup.ModSettings.PlaybackLinkLossPercent, 0, 50));
        if (!_streaming.Play(new FileStreamSource(stream, link), scene, PlayMatch.Auto, out problem))
        {
            ModLog.Warn($"Stream player (F4): could not play: {problem}.");
        }
    }

    /// <summary>
    /// F2, the fresh-match rehearsal: what a friend's game does with the host's stream, on one PC. The newest recording
    /// that has a match setup and component ids is loaded as a new match (<see cref="MatchSetup.TryLoad"/>), and once it's
    /// in play the recording plays onto it by stable key, its components taking the recorded ids. F2 again stops.
    /// </summary>
    private void ToggleRehearsal()
    {
        if (_rehearsal != null || (_streaming.Playing && _streaming.Player.Source is FileStreamSource))
        {
            if (_rehearsal != null)
            {
                MatchSetup.CancelLoad();
            }

            _rehearsal = null;
            _streaming.StopPlaying("F2 pressed");
            ModLog.Info("Fresh-match rehearsal (F2): stopped.");
            return;
        }

        if (_streaming.Playing || _streaming.Recording || _online.HostStreaming || _online.FriendBusy)
        {
            ModLog.Warn("Fresh-match rehearsal (F2): something else has the match (F3/F4, or an online match); stop it first.");
            return;
        }

        // The newest recording, or the one the replay tour names (PressF2On).
        MatchRecording? stream;
        string problem;
        string? path = _rehearseOnly != null ? OneRecording(_rehearseOnly, out stream, out problem) : NewestRecording(out stream, out problem);
        if (path == null || stream == null)
        {
            _rehearsalOutcome = problem;
            ModLog.Warn($"Fresh-match rehearsal (F2): {problem}");
            return;
        }

        MatchToken before = MatchSetup.CurrentMatch(out _);
        if (!MatchSetup.TryLoad(stream.Setup, out problem))
        {
            _rehearsalOutcome = $"couldn't load the recording's match: {problem}";
            ModLog.Warn($"Fresh-match rehearsal (F2): couldn't load the recording's match: {problem}.");
            return;
        }

        _rehearsalOutcome = null;
        _rehearsal = new Rehearsal(path, stream, before, DateTime.UtcNow);
        ModLog.Info($"Fresh-match rehearsal (F2): loading the match of {System.IO.Path.GetFileName(path)} ({MatchSetup.Describe(stream.Setup)}); " +
            "it plays as soon as the match is in play. F2 again cancels.");
    }

    private void UpdateRehearsal()
    {
        if (_rehearsal == null)
        {
            return;
        }

        MatchToken current = MatchSetup.CurrentMatch(out Il2CppUltimateReplay.ReplayScene? scene);
        if (current.IsNone || current == _rehearsal.Before)
        {
            if (DateTime.UtcNow - _rehearsal.Started > TimeSpan.FromSeconds(90))
            {
                _rehearsalOutcome = "the match didn't load within 90 s";
                ModLog.Warn($"Fresh-match rehearsal (F2): the match didn't load within 90 s; giving up (matches loaded so far: {MatchSetup.LoadedSerial}).");
                MatchSetup.CancelLoad();
                _rehearsal = null;
            }

            return;
        }

        Rehearsal rehearsal = _rehearsal;
        _rehearsal = null;
        if (MatchSetup.TryCapture(out System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, T2TOnline.Core.Serialization.NetValue>> local, out string captureProblem))
        {
            string differences = MatchSetupValues.Differences(rehearsal.Stream.Setup, local, LogNames.TextShape);
            _rehearsalOutcome = differences.Length == 0 ? RehearsalExact : $"the loaded match differs from the recording's: {differences}";
            ModLog.Info(differences.Length == 0
                ? "Fresh-match rehearsal (F2): the loaded match has exactly the recording's setup."
                : $"Fresh-match rehearsal (F2): the loaded match differs from the recording's: {differences}.");
        }
        else
        {
            _rehearsalOutcome = $"couldn't read back the loaded match's setup ({captureProblem})";
            ModLog.Warn($"Fresh-match rehearsal (F2): couldn't read back the loaded match's setup ({captureProblem}).");
        }

        var link = new SimulatedLink(
            Math.Clamp(Startup.ModSettings.PlaybackLinkDelayMs, 0, 500),
            Math.Clamp(Startup.ModSettings.PlaybackLinkJitterMs, 0, 200),
            Math.Clamp(Startup.ModSettings.PlaybackLinkLossPercent, 0, 50));
        if (!_streaming.Play(new FileStreamSource(rehearsal.Stream, link), scene, PlayMatch.Fresh, out string problem))
        {
            ModLog.Warn($"Fresh-match rehearsal (F2): could not play: {problem}.");
        }
    }

    /// <summary>The newest stream file with a match setup and component ids (made by v0.4 or later), loaded.</summary>
    private string? NewestRecording(out MatchRecording? stream, out string problem)
    {
        stream = null;
        if (!System.IO.Directory.Exists(StreamDirectory))
        {
            problem = "no recordings yet: record a match first (F3, or host an online match).";
            return null;
        }

        int looked = 0;
        foreach (string path in System.IO.Directory.GetFiles(StreamDirectory, "*.t2tstream").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(5))
        {
            looked++;
            if (MatchRecording.TryLoad(path, out MatchRecording? loaded, out _) && loaded!.Setup.Count > 0 && loaded.HasComponentIds)
            {
                stream = loaded;
                problem = string.Empty;
                return path;
            }
        }

        problem = looked == 0
            ? "no recordings yet: record a match first (F3, or host an online match)."
            : $"none of the {looked} newest recordings has a match setup and component ids (they're from before v0.4): record a match with F3 first.";
        return null;
    }

    /// <summary>
    /// Call from OnGUI: draws the host's menu as text when one is shown that way, the online status box and, when on, the
    /// net overlay (F9), and the developer's panel (F1) over them.
    /// </summary>
    public void OnGUI()
    {
        if (_stopped)
        {
            // Stopped after an error: only the status box, saying so (the panel is closed and nothing follows the host).
            StatusOverlay.Draw(_online.Status);
            return;
        }

        Campaign.Menus.MenuPanel.Draw();
        StatusOverlay.Draw(_online.Status);
        StatusOverlay.Draw(_online.NetLines, right: true);
        _panel?.OnGUI();
    }

    /// <summary>
    /// Call once per frame after <see cref="Shutdown"/> with afterError: what the online session left (a lobby call still
    /// running, a skater the AI played, a match loading) still finishes, and the status box says the mod stopped.
    /// </summary>
    public void UpdateAfterError() => _online.UpdateAfterError();

    /// <summary>Ends every tool and writes final reports. Each is guarded, so one failure doesn't skip the rest.</summary>
    public void Shutdown(bool afterError = false)
    {
        _stopped = afterError;

        // First: the game gets its keyboard and mouse back whatever happens below.
        _panel?.Shutdown();
        _rehearsal = null;
        Safely("Frozen replay (F11)", () => _frozenReplay.Stop("game closing"), always: true);
        Safely("Stream player (F4)", () => _streaming.StopPlaying("game closing"), always: true);
        Safely("Stream recorder (F3)", () => _streaming.StopRecording("game closing"), always: true);
        _online.Shutdown(removeGamePlayers: afterError);
        if (afterError)
        {
            // The lobby and the Join list are the runtime's screens, and their handlers stop with it: they close. The Backups screen is the
            // native screens' layer's, which outlives the runtime and keeps ticking it, as it does the game's prompts.
            Safely("Native lobby", () =>
            {
                if (NativeLobby.LobbyShell.IsOpen && NativeLobby.LobbyShell.Kind != NativeLobby.ShellKind.Backups)
                {
                    NativeLobby.LobbyShell.Close("the mod stopped");
                }
            }, always: true);
        }

        Safely("Loopback (F8)", () => _loopback.Stop("game closing"), always: true);
        Safely("Snapshot probe", SnapshotProbe.Flush, always: true);
    }

    /// <summary>An F2 rehearsal waiting for its match to load.</summary>
    private sealed record Rehearsal(string Path, MatchRecording Stream, MatchToken Before, DateTime Started);
}
#endif
