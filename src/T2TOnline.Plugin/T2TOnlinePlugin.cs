using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Hooks;
#if HAS_INTEROP
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Replay;
using T2TOnline.Plugin.Runtime;
#endif
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin;

/// <summary>
/// Entry point. A MelonPlugin rather than a MelonMod, because only plugins get <see cref="OnPreInitialization"/>:
/// the one moment when IL2CPP is up but Unity hasn't run any game code, so crash reporting and analytics can be
/// stopped before they start.
/// </summary>
public sealed class T2TOnlinePlugin : MelonPlugin
{
    /// <summary>How often Sentry is checked again during the session.</summary>
    private const long SentryRecheckMs = 30_000;

    /// <summary>The MelonLoader version T2T Online is tested with (the with-MelonLoader package ships it).</summary>
    private const string TestedMelonLoader = "0.7.3";

    /// <summary>
    /// What to do when online is off for a reason the logs should show: a problem report, made where this game build makes one (Settings >
    /// Online where the native screens are used, else a held Insert: D140, D142).
    /// </summary>
    private string SendLogs => $"The game plays normally. {Sentence(PlayersBuild.ReportAdvice(NativeScreens))}.";

    private BuildFingerprint? _build;
    private IReadOnlyList<string> _earlyHousekeepingFailures = Array.Empty<string>();
    private bool _crashHandlerChecked;
    private long _nextSentryCheck = long.MaxValue;
    private HookManifest? _manifest;
#if HAS_INTEROP
    private readonly HashSet<string> _unlistedPatches = new(StringComparer.Ordinal);
    private string? _loaderNotice;
    private bool _gamePatchable;
    private ModRuntime? _runtime;
    private OfflineShell? _shell;

    // The runtime after it stopped on an error: still pumped for what its session left, and drawing one status line.
    private ModRuntime? _stopped;
    private int _stoppedFailures;
#endif

#if HAS_INTEROP
    /// <summary>
    /// True when the game's own screens carry the mod's (a tested build whose hooks all resolved, its Settings menu's Online tab not off): where a
    /// problem report is made. Otherwise a held Insert makes one (D140).
    /// </summary>
    private bool NativeScreens => _gamePatchable && SettingsTab.OffReason == null;
#else
    /// <summary>No native screens without the game's interop: a problem report is made by a held Insert.</summary>
    private static bool NativeScreens => false;
#endif

    /// <summary>True when the build, hooks and housekeeping all check out. Online features stay off otherwise.</summary>
    public static bool OnlineAvailable { get; private set; }

    /// <summary>True on a game build the mod wasn't made for: the one case where "this game update" is why online is off.</summary>
    public static bool GameNotTested { get; private set; }

    /// <summary>Why online mode is off, in plain words for the screen ("the game was updated, ..."); empty while it's on.</summary>
    public static string OfflineReason { get; private set; } = string.Empty;

    /// <summary>What the player can do about <see cref="OfflineReason"/>, for the screen; empty while online is on.</summary>
    public static string OfflineAdvice { get; private set; } = string.Empty;

    /// <summary>
    /// The earliest code of ours: MelonLoader makes this object before anything of its own could scan this assembly's types (D108).
    /// First the install check (D125), before anything touches T2TOnline.Core, which may be another build: this method and
    /// <see cref="OnPreInitialization"/> name nothing of it, and the work that does is in methods compiled only when called.
    /// </summary>
    public T2TOnlinePlugin()
    {
        InstallCheck.Run();
        if (InstallCheck.Inert)
        {
            InertPlugin.ClearStaleInterop();
            return;
        }

        StartEarly();
    }

    /// <inheritdoc />
    public override void OnPreInitialization()
    {
        if (InstallCheck.Inert)
        {
            InertPlugin.Stop(this);
            return;
        }

        PreInitialize();
    }

    // D108's work, out of the constructor's own body: it uses T2TOnline.Core.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartEarly()
    {
        EarlyInteropLoads.Start();
        StaleInterop.ClearIfGameChanged();
    }

    // The start once the install is one build: the log opens, and the early blocks of crash reporting and analytics go in.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PreInitialize()
    {
        ModLog.Open(LoggerInstance, Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline", "logs"), MelonEnvironment.GameRootDirectory);
        ModLog.Info($"T2T Online {ModVersion.ForLog} starting (build {ModVersion.Full}). Session log: UserData\\T2TOnline\\logs\\{ModLog.FileName}");
        if (InstallCheck.LogLine != null)
        {
            if (InstallCheck.Leftovers || InstallCheck.Failure != null)
            {
                ModLog.Warn(InstallCheck.LogLine);
            }
            else
            {
                ModLog.Info(InstallCheck.LogLine);
            }
        }

        if (StaleInterop.Report != null)
        {
            ModLog.Info(StaleInterop.Report);
        }

#if HAS_INTEROP
        _loaderNotice = CheckMelonLoader();
#else
        CheckMelonLoader();
#endif
        try
        {
            _build = BuildFingerprint.Compute(MelonEnvironment.GameRootDirectory);
            ModLog.Info($"Game build: {_build.Label} (GameAssembly {_build.GameAssemblySha256}, metadata {_build.MetadataSha256})");
        }
        catch (Exception ex)
        {
            ModLog.Error("Could not fingerprint the game build", ex);
        }

        try
        {
            _earlyHousekeepingFailures = EarlyHousekeeping.Apply();
        }
        catch (Exception ex)
        {
            _earlyHousekeepingFailures = new[] { "early housekeeping" };
            ModLog.Error("Early housekeeping failed", ex);
        }
    }

    /// <inheritdoc />
    public override void OnApplicationEarlyStart()
    {
        ModLog.Info("Unity's first scene is loading. MelonLoader now prepares the game's interop assemblies; the first launch takes several minutes.");
        if (EarlyInteropLoads.Summary(StaleInterop.GeneratorExpected) is { } early)
        {
            if (StaleInterop.GeneratorExpected)
            {
                ModLog.Warn(early);
            }
            else
            {
                ModLog.Info(early);
            }
        }
    }

    /// <inheritdoc />
    public override void OnInitializeMelon()
    {
        // The generator has run (D108): the watch ends.
        EarlyInteropLoads.Stop();

#if HAS_INTEROP
        // First thing: Unity's native telemetry has been running since the player started.
        try
        {
            EngineTelemetry.SwitchOff();
        }
        catch (Exception ex)
        {
            ModLog.Error("Housekeeping: switching Unity's engine telemetry off failed", ex);
        }

        // What says whether the engine's analytics ever ran: its own files in the save folder (never throws).
        AnalyticsFiles.Note();
#endif

        try
        {
            ModSettings.Load();
        }
        catch (Exception ex)
        {
            ModLog.Error("Settings: T2T Online's settings couldn't be loaded; the defaults apply", ex);
        }

#if HAS_INTEROP
        // The mod's folders, before anything of the mod writes to them (never throws).
        DataHousekeeping.Run();
#endif

        // The log header's settings line: the summary of the settings not at their defaults (names and values only).
        ModLog.WriteSettings(ModSettings.NonDefaultSummary);

        // Beside it, once: which build runs (the players' build or the developer's: D142), and a native lobby the file can't turn off without it.
        ModSettings.LogBuildMode();

        try
        {
            Il2CppTypeLocator.Invalidate();
            var failedHousekeeping = new List<string>(_earlyHousekeepingFailures);
            failedHousekeeping.AddRange(Housekeeping.Apply(HarmonyInstance));
            if (Housekeeping.VerifySentryOff() is string sentryProblem)
            {
                failedHousekeeping.Add(sentryProblem);
            }

            _manifest = HookManifest.LoadEmbedded();
            HookCheckResult hooks = _manifest.Check();
            if (hooks.AllPresent)
            {
                ModLog.Info($"Hook manifest: {hooks.Summary}.");
            }
            else
            {
                ModLog.Warn($"Hook manifest: {hooks.Summary}.");
            }

            // Version lock: online only on a game build the mod was tested on (names alone can survive a patch that changed
            // what they do; D12). On any other build the developer setting AllowUntestedBuild runs the offline test tools.
#if HAS_INTEROP
            _gamePatchable = _build is { IsTested: true } && hooks.AllPresent;

            // A check tour's run.txt may ask to start as if online were off (developer tools only; the reason chain's last branch).
            DevOfflineStart.Read(_gamePatchable);
#endif
            GameNotTested = _build is { IsTested: false };
            if (_build == null)
            {
                SwitchOnlineOff("the game's version couldn't be checked", SendLogs);
            }
            else if (!_build.IsTested)
            {
                // Which way the game differs (newer: a new release; older: a Steam update), from Steam's own build number when it can be read.
                GameVersionAge age = BuildFingerprint.SteamAge(MelonEnvironment.GameRootDirectory, out string ageForLog);
                ModLog.Warn($"Online mode is off: {_build.Label} isn't a build T2T Online was tested on; {ageForLog}. Run the after-every-patch checklist (docs/DEVELOPMENT.md) to add it." +
                    (ModSettings.AllowUntestedBuild
                        ? " AllowUntestedBuild is on, so the offline test tools run anyway."
                        : " To use the offline test tools on it meanwhile, set AllowUntestedBuild = true (developers only)."));
                SwitchOnlineOff(UntestedBuildNotice.Reason(age), UntestedBuildNotice.Advice(age, Core.ProjectLinks.ReleasesShort));
            }
            else if (failedHousekeeping.Count > 0)
            {
                SwitchOnlineOff("crash reporting or analytics couldn't be switched off", SendLogs);
            }
            else if (!hooks.AllPresent)
            {
                SwitchOnlineOff("part of the game T2T Online needs wasn't found", SendLogs);
            }
            else if (InstallCheck.Leftovers)
            {
                // D125: the build that runs is whole, but old files of T2T Online are in the game folder (the title's prompt names them).
                SwitchOnlineOff("old T2T Online files are in the game folder", InstallCheck.Advice);
            }
#if HAS_INTEROP
            else if (DevOfflineStart.StartsShell)
            {
                SwitchOnlineOff(DevOfflineStart.OffReason, DevOfflineStart.Advice);
            }
#endif
            else
            {
                OnlineAvailable = true;
            }

#if HAS_INTEROP
            DevOfflineStart.NoteStart(OnlineAvailable, OfflineReason);
#endif
            ModLog.Info(OnlineAvailable
                ? $"Online mode available on {_build!.Label}."
                : "Online mode is off for this session; the game runs normally. See the lines above for why.");
        }
        catch (Exception ex)
        {
#if HAS_INTEROP
            _gamePatchable = false;
#endif
            SwitchOnlineOff("a start-up check failed", SendLogs);
            ModLog.Error("Startup checks failed; online mode is off", ex);
        }

        _nextSentryCheck = Environment.TickCount64 + SentryRecheckMs;
#if HAS_INTEROP
        // A restore the game stopped in the middle of is put right whatever online's state, before the game reads a save (never throws).
        Campaign.SavesAtLaunch.Run(HarmonyInstance, _gamePatchable);
        BugReports.Configure(_build?.Label, _gamePatchable);
        StartRuntime();
        AuditPatches("start-up");
#endif
    }

    /// <inheritdoc />
    public override void OnLateInitializeMelon()
    {
        if (!_crashHandlerChecked)
        {
            _crashHandlerChecked = true;
            LogOtherMelons();
            if (Housekeeping.ReportCrashHandlerState(MelonEnvironment.GameRootDirectory) && OnlineAvailable)
            {
                SwitchOnlineOff("the game's crash reporter may be running", $"Restart the game. If this stays, {PlayersBuild.ReportAdvice(NativeScreens)}.");
                ModLog.Warn("Online mode is off for this session: crash reporting may be live.");
            }
        }

#if HAS_INTEROP
        EngineChecks.CheckPhysicsSimulationMode();
#endif
    }

    /// <inheritdoc />
    public override void OnUpdate()
    {
        // Once a second, one engine read: Unity's crash reporter stays off (never throws).
        CrashReporting.Watch();
        RecheckSentry();
#if HAS_INTEROP
        BugReports.Update();
        if (_runtime == null)
        {
            UpdateStopped();
            _shell?.Update();
            NativeUiLayer.Update();
            return;
        }

        try
        {
            DevOfflineStart.ThrowIfStopAsked();
            _runtime.Update();
        }
        catch (Exception ex)
        {
            ModLog.Error("T2T Online failed and is off for this session (the game carries on normally)", ex);
            StopRuntime();
        }

        // After the runtime: its kit has ticked the layer at its own point in the frame (then this does nothing), or it couldn't.
        NativeUiLayer.Update();
#endif
    }

#if HAS_INTEROP
    /// <inheritdoc />
    public override void OnLateUpdate()
    {
        if (_runtime == null)
        {
            return;
        }

        try
        {
            _runtime.LateUpdate();
        }
        catch (Exception ex)
        {
            ModLog.Error("T2T Online failed and is off for this session (the game carries on normally)", ex);
            StopRuntime();
        }
    }

    /// <inheritdoc />
    public override void OnGUI()
    {
        // The overlays and the developer's panel (F1) guard themselves; this only keeps a failure from reaching MelonLoader.
        try
        {
            if (_runtime != null)
            {
                _runtime.OnGUI();
            }
            else
            {
                // After an error the stopped runtime's status box says so; the shell adds the developer's F1 panel, and its own
                // notice only once nothing else draws in that corner.
                _stopped?.OnGUI();
                _shell?.OnGUI(notice: _stopped == null);
            }

            BugReports.OnGUI();
        }
        catch (Exception ex)
        {
            ModLog.Error("T2T Online's overlays failed", ex);
        }
    }
#endif

    /// <inheritdoc />
    public override void OnApplicationQuit()
    {
        // First, before anything shuts down: the game's own shutdown logs an exception (never throws).
        CrashReporting.SwitchOffAtQuit();
#if HAS_INTEROP
        try
        {
            _runtime?.Shutdown();
            _shell?.Shutdown();
        }
        catch (Exception ex)
        {
            ModLog.Error("T2T Online did not shut down cleanly", ex);
        }

        AuditPatches("the end of the session");
        AnalyticsFiles.Check();

        // A clean close: the problem report's marker says only an offer still due (D140; never throws).
        ReportOfferWatch.AtQuit();
#endif

        ModLog.Info("Game closing.");
        ModLog.Close();
    }

    /// <summary>A text as a sentence's start: its first letter in upper case.</summary>
    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Online mode goes off (for the rest of the session), and the screen says why and what to do: the first reason is
    /// kept, unless <paramref name="replace"/> (the runtime stopping changes what the player must do).
    /// </summary>
    private static void SwitchOnlineOff(string reason, string advice, bool replace = false)
    {
        OnlineAvailable = false;
        if (OfflineReason.Length == 0 || replace)
        {
            OfflineReason = reason;
            OfflineAdvice = advice;
        }
    }

    /// <summary>
    /// Logs MelonLoader's version, and when it isn't the tested one says so in the log and returns a notice for the
    /// screen. Online stays available: the start-up checks that matter (Sentry, the crash handler, the hooks) verify their
    /// own outcome whatever the loader, and keep online off when they fail.
    /// </summary>
    private static string? CheckMelonLoader()
    {
        try
        {
            Version? version = typeof(MelonBase).Assembly.GetName().Version;
            string loaded = version == null ? "of an unknown version" : $"{version.Major}.{version.Minor}.{version.Build}";
            if (loaded == TestedMelonLoader)
            {
                ModLog.Info($"MelonLoader {loaded}: the version T2T Online is tested with.");
                return null;
            }

            ModLog.Warn($"MelonLoader {loaded} is installed, but T2T Online is tested with MelonLoader {TestedMelonLoader} only: its early blocking of crash " +
                "reporting and analytics relies on that version's start-up order. The checks below still confirm the result. To be safe, install " +
                $"MelonLoader {TestedMelonLoader} from its official release, then unzip the T2T Online zip into the game folder again.");
            return $"MelonLoader {loaded} is installed; T2T Online is tested with {TestedMelonLoader}: install MelonLoader {TestedMelonLoader}, then unzip the T2T Online zip again";
        }
        catch (Exception ex)
        {
            ModLog.Warn($"MelonLoader's version couldn't be read ({ModLog.SafeErrorText(ex)}).");
            return null;
        }
    }

    /// <summary>Lists the other MelonLoader mods and plugins loaded with T2T Online (it's tested alone).</summary>
    private static void LogOtherMelons()
    {
        try
        {
            List<string> others = MelonBase.RegisteredMelons
                .Where(melon => melon is not T2TOnlinePlugin)
                .Select(melon => $"{SafeText.Clean(melon.Info.Name, 48)} {SafeText.Clean(melon.Info.Version, 24)}")
                .ToList();
            if (others.Count == 0)
            {
                ModLog.Info("Other MelonLoader mods and plugins: none.");
            }
            else
            {
                ModLog.Warn($"Other MelonLoader mods and plugins loaded ({others.Count}): {string.Join(", ", others)}. T2T Online is tested without any; if something misbehaves, try without them.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"The other MelonLoader mods couldn't be listed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// Every 30 s, checks that Sentry is still off (<see cref="Housekeeping.RecheckSentry"/>): even when a start-up block
    /// failed and online mode is off, the mod stays loaded, so it keeps making sure crash reports can't go out. If Sentry
    /// was running, it's closed and online mode goes off for the rest of the session.
    /// </summary>
    private void RecheckSentry()
    {
        long now = Environment.TickCount64;
        if (now < _nextSentryCheck)
        {
            return;
        }

        _nextSentryCheck = now + SentryRecheckMs;
#if HAS_INTEROP
        EngineTelemetry.Recheck();
        AnalyticsFiles.Recheck();
#endif
        if (Housekeeping.RecheckSentry() is string problem && OnlineAvailable)
        {
            SwitchOnlineOff("crash reporting started during the session",
                $"A session already running carries on; restart the game before the next one, and {PlayersBuild.ReportAdvice(NativeScreens)}.");
            ModLog.Warn($"Online mode is off from now on: {problem}. A session already running carries on; no new one starts.");
        }
    }

#if HAS_INTEROP
    /// <summary>
    /// Ends the runtime after a failure, so nothing it started (an online session above all) keeps running unmanaged.
    /// Online mode is off from then on: the stopped runtime's status box says so until the game restarts (the game no
    /// longer follows the host; the offline notice takes over should it be dropped too); Settings > Online stays (the native screens'
    /// layer outlives the runtime), its cursor on Problem Report, and with the developer tools F1's panel (<see cref="OfflineShell"/>).
    /// </summary>
    private void StopRuntime()
    {
        ModRuntime? runtime = _runtime;
        _runtime = null;
        _stopped = runtime;
        SwitchOnlineOff("T2T Online stopped after an error",
            $"Your game no longer follows the host's. {Sentence(PlayersBuild.ReportAdvice(NativeScreens))}, then quit and start the game again.", replace: true);
        try
        {
            runtime?.Shutdown(afterError: true);
        }
        catch (Exception ex)
        {
            ModLog.Error("T2T Online did not shut down cleanly", ex);
        }

        StartShell(afterError: true);
    }

    /// <summary>
    /// After the runtime stopped on an error: what its session left still finishes (a lobby call still running is left
    /// when it completes, a skater the AI played gets its flags back), as after the session's own error. Three failures
    /// and it's dropped too.
    /// </summary>
    private void UpdateStopped()
    {
        if (_stopped == null)
        {
            return;
        }

        try
        {
            _stopped.UpdateAfterError();
            _stoppedFailures = 0;
        }
        catch (Exception ex)
        {
            ModLog.Error("T2T Online: finishing what the stopped session left failed", ex);
            if (++_stoppedFailures >= 3)
            {
                _stopped = null;
            }
        }
    }

    /// <summary>
    /// Starts the mod's runtime: the game's log tap, the game patches (each installed on its own), and the per-frame
    /// <see cref="ModRuntime"/> (online sessions, match setup and streaming, the developer's test keys). With online mode
    /// off it starts only on an untested build with AllowUntestedBuild on, for the offline test tools; otherwise the
    /// <see cref="OfflineShell"/> says on screen why online is off (and keeps F1's panel for a developer).
    /// </summary>
    private void StartRuntime()
    {
        try
        {
            UnityLogTap.Install();
        }
        catch (Exception ex)
        {
            ModLog.Error("Unity log tap: couldn't be installed; the game's own warnings won't be in this log", ex);
        }

        // The per-run counters follow the game's run file whatever online's state, on a build the mod may patch (never throws).
        Campaign.Progress.ProgressRunScopes.Start(_gamePatchable);

        // The native screens (D139-D141): one layer, before the runtime or the offline shell, whichever starts; never on a build the mod
        // doesn't patch (D12), and never in a mixed install (D125: the plugin is unregistered before this; checked again all the same).
        if (_gamePatchable && !InstallCheck.Inert)
        {
            NativeUiLayer.Start(HarmonyInstance, OnlineAvailable ? Core.OnlineSettings.SettingsState.OnlineOn : Core.OnlineSettings.SettingsState.OnlineOff);
        }
        else
        {
            NativeUiLayer.Skip("T2T Online doesn't patch this game build (untested, or its hooks didn't all resolve)");
        }

        bool offlineOnly = !OnlineAvailable;
        if (offlineOnly)
        {
            if (_build is not { IsTested: false } || !ModSettings.AllowUntestedBuild)
            {
                ModLog.Info(ModSettings.DevTools
                    ? "T2T Online's runtime is off this session (online mode is off): F1 opens the mod's panel; F7, F9, F5 and F6 say online is off."
                    : $"T2T Online's runtime is off this session (online mode is off): its notice says why; {(_gamePatchable ? "Settings > Online still works" : "a held Insert makes a problem report")}.");
                if (_gamePatchable && ModSettings.DevTools)
                {
                    // A check tour can walk the offline screens: its achievements hold goes in first (no tour runs without it).
                    AchievementMirror.InstallForTours(HarmonyInstance);
                }

                StartShell(afterError: false);
                return;
            }

            ModLog.Warn("AllowUntestedBuild: starting the offline test tools on an untested build (F2, F3, F4 and the snapshot probe; their keys need DevTools = true). Online stays off.");
        }

        // Each on its own: one that fails doesn't stop the others. Online play needs them all, fully in: a game only partly
        // patched would run against what the session expects of it.
        var failed = new List<string>();
        TryInstall("Snapshot probe", () => SnapshotProbe.Install(HarmonyInstance, DevOutput.CapturesFolder, measure: ModSettings.SnapshotMeasure), null, failed);
        TryInstall("Match setup", () => MatchSetup.Install(HarmonyInstance), null, failed);
        if (!offlineOnly)
        {
            TryInstall("Virtual pad compat", () => VirtualPadCompat.Install(HarmonyInstance),
                "Virtual pad compat: on (the game's gamepad checks count virtual pads as the pads they stand in for).", failed);
            TryInstall("Held pad guard", () => HeldPadGuard.Install(HarmonyInstance),
                "Held pad guard: on (while the mod holds your pad, the game can't hand it out again when it reconnects).", failed);
            // Cosmetic: if it can't go in, friends see the game's own prompts and online stays on (it says so itself).
            PromptPin.Install(HarmonyInstance);
            TryInstall("Achievements", () => AchievementMirror.Install(HarmonyInstance), null, failed);
            TryInstall("Fight grace", () => FightGrace.Install(HarmonyInstance), null, failed);
            // No patch: the match scene's unload ends the game's fight state a match left on (it says so itself; online stays on without it).
            BrawlLeftoverGuard.Install();
            TryInstall("Focus guard", () => FocusGuard.Install(HarmonyInstance),
                "Focus guard: ready (while online, losing window focus won't pause the match or stop pads being read).", failed);
            // A second net under keyboard-taken.txt: if it can't go in, online stays on and that file's repair covers a crash.
            Input.LocalKeyboard.InstallAssignmentGuard(HarmonyInstance);
            var campaignFailed = new List<string>();
            TryInstall("Campaign", () => Campaign.CampaignModules.Install(HarmonyInstance, campaignFailed), null, failed);
            foreach (string problem in campaignFailed)
            {
                ModLog.Warn($"Campaign: {problem}");
            }
        }
        else if (ModSettings.DevTools)
        {
            // The offline tools' runtime can run a check tour: its achievements hold goes in all the same (no tour runs without it).
            AchievementMirror.InstallForTours(HarmonyInstance);
        }

        if (failed.Count > 0 && OnlineAvailable)
        {
            SwitchOnlineOff("part of T2T Online couldn't be installed", SendLogs);
            ModLog.Warn($"Online mode is off for this session: {string.Join(", ", failed)} couldn't be installed (see above), so the game is only partly patched.");
        }

        try
        {
            ulong gameBuild = ulong.Parse(_build!.GameAssemblySha256[..16], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            _runtime = new ModRuntime(ModSettings.LoopbackDelayMs, ModSettings.DevTools, ModVersion.Release, gameBuild, MelonEnvironment.GameRootDirectory, HarmonyInstance, _loaderNotice);
        }
        catch (Exception ex)
        {
            _runtime = null;
            SwitchOnlineOff("T2T Online couldn't start", SendLogs);
            ModLog.Error("T2T Online's runtime could not start; online mode is off for this session", ex);
            StartShell(afterError: false);
        }
    }

    /// <summary>
    /// Starts the <see cref="OfflineShell"/> (once): the notice saying why online is off, and with the developer tools F1's panel. The panel
    /// patches the game (to hold its UI events while it's open) and offers restores only on a tested build whose hooks all resolved.
    /// </summary>
    private void StartShell(bool afterError)
    {
        // The native screens' layer stays on through this: only the rows' state changes.
        NativeUiLayer.SetState(afterError ? Core.OnlineSettings.SettingsState.AfterError : Core.OnlineSettings.SettingsState.OnlineOff);
        if (_shell != null)
        {
            return;
        }

        try
        {
            _shell = new OfflineShell(ModVersion.Release, ModSettings.DevTools, _gamePatchable ? HarmonyInstance : null,
                _gamePatchable ? null : "Restoring is off while T2T Online is off for this game version: it needs the mod's save guard, which runs only on a tested version. Backing up still works.",
                afterError, _loaderNotice, screensUsable: _gamePatchable, tours: _gamePatchable && ModSettings.DevTools);
        }
        catch (Exception ex)
        {
            _shell = null;
            ModLog.Error("T2T Online's offline notice could not start; it doesn't show this session", ex);
        }
    }

    /// <summary>
    /// Checks that every method the mod patched is a patch target in the hook lists (<see cref="HookManifest.UnlistedPatches"/>),
    /// so none escaped hookcheck.py's folding check; at start-up, and at the end of the session for patches made since
    /// (client mode, the save guard). Each one missing is logged once. Never throws.
    /// </summary>
    private void AuditPatches(string when)
    {
        if (_manifest == null)
        {
            return;
        }

        try
        {
            List<System.Reflection.MethodBase> patched = HarmonyInstance.GetPatchedMethods().ToList();
            List<string> missing = _manifest.UnlistedPatches(patched).Where(_unlistedPatches.Add).ToList();
            if (missing.Count > 0)
            {
                ModLog.Warn($"Patch audit ({when}): {missing.Count} patched method(s) aren't patch targets in the hook lists, so hookcheck.py never checked them " +
                    $"for code folding: {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? ", ..." : string.Empty)}.");
            }
            else if (_unlistedPatches.Count == 0)
            {
                ModLog.Info($"Patch audit ({when}): all {patched.Count} patched methods are patch targets in the hook lists.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Patch audit ({when}) failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Installs one set of patches. A failure (false, or a throw) is logged and added to <paramref name="failed"/>.</summary>
    private static void TryInstall(string what, Func<bool> install, string? onLine, List<string> failed)
    {
        try
        {
            if (install())
            {
                if (onLine != null)
                {
                    ModLog.Info(onLine);
                }

                return;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"{what}: couldn't be installed", ex);
        }

        failed.Add(what);
    }
#endif
}
